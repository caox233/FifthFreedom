from __future__ import annotations

import os
import ipaddress
import re
import socket
import sqlite3
import subprocess
import time
from contextlib import AbstractContextManager
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Mapping

from control_plane_domain import (
    ClientState,
    clean_int,
    clean_text,
    is_client_overlay_ip,
    utc_ms,
)


DatabaseFactory = Callable[[], AbstractContextManager[Any]]


@dataclass
class AuthoritativeGameSession:
    session_id: int
    session_type: int
    host_user_id: int
    host_username: str
    host_virtual_ip: str = ""
    participant_count: int = 0
    participant_ips: set[str] = field(default_factory=set)
    participant_user_ids: set[int] = field(default_factory=set)

    def score(self) -> tuple[int, int, int]:
        return (len(self.participant_ips), self.participant_count, self.session_id)


_OVERLAY_NETWORK = ipaddress.ip_network(
    os.environ.get("SCBL_VIRTUAL_NET", "10.66.0.0/24"), strict=False
)
if _OVERLAY_NETWORK.version != 4:
    raise ValueError("SCBL_VIRTUAL_NET must be an IPv4 network")
_OVERLAY_IP_REGEX = re.compile(r"(?<![0-9])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9])")


def extract_overlay_ips(value: str) -> list[str]:
    return [
        ip
        for ip in dict.fromkeys(_OVERLAY_IP_REGEX.findall(value or ""))
        if is_client_overlay_ip(ip, _OVERLAY_NETWORK)
    ]


def is_transient_sqlite_contention(error: sqlite3.OperationalError) -> bool:
    error_code = getattr(error, "sqlite_errorcode", None)
    if error_code in {
        getattr(sqlite3, "SQLITE_BUSY", None),
        getattr(sqlite3, "SQLITE_LOCKED", None),
    }:
        return True
    message = str(error).lower()
    return "database is locked" in message or "database is busy" in message


def read_authenticated_identity_row(
    user_id: int,
    database: DatabaseFactory,
    *,
    attempts: int,
    retry_seconds: float,
) -> tuple[Any, ...] | None:
    """Read one token identity, retrying only short SQLite contention."""
    for attempt in range(attempts):
        try:
            with database() as connection:
                return connection.execute(
                    "SELECT username, auth_version FROM users WHERE id = ? LIMIT 1",
                    (user_id,),
                ).fetchone()
        except sqlite3.OperationalError as error:
            if not is_transient_sqlite_contention(error) or attempt + 1 >= attempts:
                raise
            time.sleep(retry_seconds * (2**attempt))
    return None


def load_authoritative_sessions(
    database_exists: bool,
    database: DatabaseFactory,
) -> dict[str, AuthoritativeGameSession]:
    if not database_exists:
        return {}
    with database() as connection:
        rows = connection.execute(
            """
            SELECT g.id, g.type_id, g.creator_id, host.username,
                   p.user_id, COALESCE(s.url, '')
            FROM game_sessions AS g
            JOIN users AS host ON host.id = g.creator_id
            JOIN participants AS p ON p.game_id = g.id
            LEFT JOIN station_urls AS s ON s.user_id = p.user_id
            WHERE g.destroyed_at IS NULL
            ORDER BY g.id DESC
            """
        ).fetchall()

    sessions: dict[int, AuthoritativeGameSession] = {}
    user_ips: dict[tuple[int, int], set[str]] = {}
    for session_id, session_type, creator_id, host_username, user_id, url in rows:
        sid = int(session_id)
        uid = int(user_id)
        session = sessions.setdefault(
            sid,
            AuthoritativeGameSession(
                session_id=sid,
                session_type=int(session_type),
                host_user_id=int(creator_id),
                host_username=clean_text(host_username, 64),
            ),
        )
        session.participant_user_ids.add(uid)
        user_ips.setdefault((sid, uid), set()).update(extract_overlay_ips(str(url or "")))

    result: dict[str, AuthoritativeGameSession] = {}
    for session in sessions.values():
        session.participant_count = len(session.participant_user_ids)
        for user_id in session.participant_user_ids:
            ips = user_ips.get((session.session_id, user_id), set())
            session.participant_ips.update(ips)
            if user_id == session.host_user_id and ips and not session.host_virtual_ip:
                session.host_virtual_ip = sorted(
                    ips,
                    key=lambda value: int(value.rsplit(".", 1)[-1]),
                )[0]
        if (
            not session.host_virtual_ip
            or session.participant_count < 2
            or len(session.participant_ips) < 2
        ):
            continue
        for ip in session.participant_ips:
            current = result.get(ip)
            if current is None or session.score() > current.score():
                result[ip] = session
    return result


def game_session_payload(
    source_ip: str,
    sessions: dict[str, AuthoritativeGameSession],
    snapshot_age_ms: int | None,
    snapshot_error: str,
) -> dict[str, Any]:
    session = sessions.get(source_ip)
    if session is None:
        return {
            "active": False,
            "authoritative": True,
            "sessionId": None,
            "sessionType": None,
            "hostUserId": None,
            "hostUsername": "",
            "hostVirtualIp": "",
            "requesterIsHost": False,
            "participantCount": 0,
            "source": "game-server",
            "snapshotAgeMs": snapshot_age_ms,
            "snapshotError": snapshot_error,
            "observedAtUnixMs": utc_ms(),
        }
    return {
        "active": True,
        "authoritative": True,
        "sessionId": session.session_id,
        "sessionType": session.session_type,
        "hostUserId": session.host_user_id,
        "hostUsername": session.host_username,
        "hostVirtualIp": session.host_virtual_ip,
        "requesterIsHost": source_ip == session.host_virtual_ip,
        "participantCount": session.participant_count,
        "source": "game-server",
        "snapshotAgeMs": snapshot_age_ms,
        "snapshotError": snapshot_error,
        "observedAtUnixMs": utc_ms(),
    }


def decorate_clients(
    clients: list[ClientState],
    sessions: dict[str, AuthoritativeGameSession],
) -> list[dict[str, Any]]:
    output: list[dict[str, Any]] = []
    for client in clients:
        item = client.public_dict()
        session = sessions.get(client.virtual_ip)
        if session is not None:
            is_host = client.virtual_ip == session.host_virtual_ip
            item["gameRole"] = "host" if is_host else "client"
            item["gamePeerIp"] = "" if is_host else session.host_virtual_ip
            item["gameRoleSource"] = "game-server"
            item["gameSessionId"] = session.session_id
            item["authoritativeHostVirtualIp"] = session.host_virtual_ip
        output.append(item)
    return output


def tcp_open(host: str, port: int, timeout: float = 0.18) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def udp_bound(port: int) -> bool:
    try:
        result = subprocess.run(
            ["ss", "-lunH"],
            capture_output=True,
            text=True,
            timeout=1.0,
            check=False,
        )
        if result.returncode != 0:
            return False
        suffix = f":{port}"
        return any(
            any(field.endswith(suffix) for field in line.split())
            for line in result.stdout.splitlines()
        )
    except Exception:
        return False


def service_active(name: str) -> bool:
    try:
        result = subprocess.run(
            ["systemctl", "is-active", "--quiet", name],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=1.0,
            check=False,
        )
        return result.returncode == 0
    except Exception:
        return False


def interface_ipv4_ready(server_ip: str) -> bool:
    try:
        result = subprocess.run(
            ["ip", "-4", "-o", "addr", "show", "dev", "scbl0"],
            capture_output=True,
            text=True,
            timeout=1.0,
            check=False,
        )
        return result.returncode == 0 and server_ip in result.stdout
    except Exception:
        return False


def database_health(
    database_exists: bool,
    database: DatabaseFactory,
) -> tuple[bool, int | None]:
    if not database_exists:
        return False, None
    try:
        with database() as connection:
            row = connection.execute("SELECT COUNT(*) FROM users").fetchone()
        return True, int(row[0]) if row else 0
    except Exception:
        return False, None


def account_exists(
    username: str,
    database_exists: bool,
    database: DatabaseFactory,
) -> bool | None:
    username = clean_text(username, 64)
    if not username or not database_exists:
        return None
    try:
        with database() as connection:
            row = connection.execute(
                "SELECT 1 FROM users WHERE username = ? LIMIT 1",
                (username,),
            ).fetchone()
        return bool(row)
    except Exception:
        return None


def compute_health(
    server_ip: str,
    database_status: tuple[bool, int | None],
) -> dict[str, Any]:
    database_ok, user_count = database_status
    tunnel = service_active("scbl-tunnel.service") and interface_ipv4_ready(server_ip)
    services = {
        "tunnel": tunnel,
        "dedicated": service_active("scbl-dedicated.service"),
        "grpc": tcp_open(server_ip, 50051),
        "config": tcp_open(server_ip, 80),
        "content": tcp_open(server_ip, 8000),
        "auth": udp_bound(21126),
        "secure": udp_bound(21127),
        "database": database_ok,
    }
    critical = services["tunnel"] and services["grpc"] and services["database"]
    all_ok = all(services.values())
    return {
        "overall": "healthy" if all_ok else "degraded" if critical else "down",
        "services": services,
        "userCount": user_count,
        "checkedAtUnixMs": utc_ms(),
    }


def topology_summary(
    peers: list[dict[str, Any]],
    overlay_network: ipaddress.IPv4Network = _OVERLAY_NETWORK,
) -> dict[str, Any]:
    summary: dict[str, Any] = {
        "onlineClients": len(peers),
        "serverUnderlay": {"ipv4": 0, "ipv6": 0, "unknown": 0},
        "serverTransport": {"udp": 0, "tcp": 0, "wss": 0, "other": 0},
        "gameRoles": {"host": 0, "client": 0, "running": 0, "idle": 0, "other": 0},
        "gamePaths": {"direct": 0, "relayed": 0, "pending": 0},
    }
    for client in peers:
        family = clean_text(client.get("serverAddressFamily"), 16).lower()
        family_key = "ipv6" if family == "ipv6" else "ipv4" if family == "ipv4" else "unknown"
        summary["serverUnderlay"][family_key] += 1
        transport = clean_text(client.get("serverTransport"), 32).lower()
        transport_key = (
            "wss"
            if "wss" in transport
            else "tcp"
            if "tcp" in transport
            else "udp"
            if "udp" in transport
            else "other"
        )
        summary["serverTransport"][transport_key] += 1
        role = clean_text(client.get("gameRole"), 16).lower()
        summary["gameRoles"][role if role in {"host", "client", "running", "idle"} else "other"] += 1
        peer_ip = clean_text(client.get("gamePeerIp"), 64)
        if role != "client" or not is_client_overlay_ip(peer_ip, overlay_network):
            continue
        hop_count = clean_int(client.get("hopCount"), 0, 255) or 0
        game_transport = clean_text(client.get("gameTransport"), 32)
        relayed = hop_count > 1 or "relay" in game_transport.lower() or "多跳" in game_transport
        if relayed:
            summary["gamePaths"]["relayed"] += 1
        elif game_transport or clean_text(client.get("gameAddressFamily"), 16):
            summary["gamePaths"]["direct"] += 1
        else:
            summary["gamePaths"]["pending"] += 1
    return summary


def capabilities(
    server_ip: str,
    control_port: int,
    environ: Mapping[str, str] = os.environ,
) -> dict[str, Any]:
    return {
        "networkName": environ.get("EASYTIER_NETWORK_NAME", "scbl-public"),
        "virtualSubnet": environ.get("SCBL_VIRTUAL_NET", "10.66.0.0/24"),
        "serverVirtualIp": server_ip,
        "mtu": int(environ.get("SCBL_MTU", "1380")),
        "tcpPort": None,
        "tcpEnabled": False,
        "wssPort": int(environ.get("SCBL_WSS_PORT", "11010")),
        "quicPort": int(environ.get("SCBL_QUIC_PORT", "11010")),
        "ipv4Enabled": True,
        "ipv6Enabled": environ.get("SCBL_ENABLE_IPV6", "y").lower()
        in {"1", "y", "yes", "true", "on"},
        "wssEnabled": True,
        "quicEnabled": True,
        "p2pEnabled": True,
        "relayEnabled": True,
        "distributedMesh": True,
        "controlPlanePort": control_port,
    }
