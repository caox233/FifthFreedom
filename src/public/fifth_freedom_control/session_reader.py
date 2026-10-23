"""Read-only session projection for the retained LAN schema.

This is not an authentication endpoint. The future protected control-plane
adapter must verify the token signature, expiry and source before creating
AuthenticatedGameIdentity. An instance of that dataclass is not itself proof of
authentication. No request JSON should be unpacked directly into it.

Server policy comes from verified package admission and server-owned observer
policy, never client claims. There are no inherited addresses or credentials.
"""
from contextlib import closing
from dataclasses import dataclass
from datetime import datetime, timezone
import ipaddress
from pathlib import Path
import re
import sqlite3
import time
from typing import Mapping

GAME_PRODUCTS = {"scbl-lan/2/game/official": 1, "scbl-lan/2/game/mod": 2}
ROLE_NAMES = ("launcher", "main_dll", "gameplay_dll", "server")


class AdmissionError(ValueError):
    """A bounded reason code; messages never contain request/database values."""


@dataclass(frozen=True)
class ReleaseIdentity:
    release_id: str
    compat_id: str
    manifest_sha256: str

    def __post_init__(self):
        if (not isinstance(self.release_id, str) or not isinstance(self.compat_id, str)
                or not isinstance(self.manifest_sha256, str)
                or re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", self.release_id) is None
                or re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", self.compat_id) is None
                or re.fullmatch(r"[0-9a-f]{64}", self.manifest_sha256) is None):
            raise AdmissionError("PACKAGE_IDENTITY_INVALID")

    @property
    def storage_key(self):
        return f"1|{self.release_id}|{self.compat_id}|{self.manifest_sha256}"

    def admit(self, declared: Mapping | None):
        if declared is None:
            raise AdmissionError("PACKAGE_IDENTITY_REQUIRED")
        if not isinstance(declared, Mapping) or type(declared.get("schema")) is not int or declared["schema"] != 1:
            raise AdmissionError("PACKAGE_IDENTITY_INVALID")
        candidate = ReleaseIdentity(declared.get("release_id"), declared.get("compat_id"), declared.get("manifest_sha256"))
        for name in ROLE_NAMES:
            role = declared.get(name)
            if not isinstance(role, Mapping) or (role.get("release_id"), role.get("compat_id")) != (candidate.release_id, candidate.compat_id):
                raise AdmissionError("PACKAGE_MIXED")
        for field, reason in [("release_id", "RELEASE_MISMATCH"), ("compat_id", "COMPAT_MISMATCH"), ("manifest_sha256", "PACKAGE_CATALOG_MISMATCH")]:
            if getattr(candidate, field) != getattr(self, field):
                raise AdmissionError(reason)


@dataclass(frozen=True)
class ServerPolicy:
    approved_release: ReleaseIdentity
    client_network: ipaddress.IPv4Network
    observer_allowed: bool = False
    official_only: bool = False

    def __post_init__(self):
        if not isinstance(self.approved_release, ReleaseIdentity) or not isinstance(self.client_network, ipaddress.IPv4Network):
            raise AdmissionError("SERVER_POLICY_INVALID")
        if type(self.observer_allowed) is not bool or type(self.official_only) is not bool:
            raise AdmissionError("SERVER_POLICY_INVALID")


@dataclass(frozen=True)
class AuthenticatedGameIdentity:
    # Server-internal output of a future verified token/admission adapter.
    user_id: int
    username: str
    auth_version: int
    source_ip: str
    product: str
    game_instance: str
    release_identity: str
    observer_admitted_run: str | None = None


@dataclass(frozen=True)
class Member:
    user_id: int
    source_ip: str


@dataclass(frozen=True)
class Session:
    session_id: int
    session_type: int
    host_user_id: int
    host_username: str
    gameplay_mode: int
    members: tuple[Member, ...]


def _client_ip(value, network):
    try:
        address = ipaddress.IPv4Address(value)
    except (ipaddress.AddressValueError, TypeError):
        raise AdmissionError("AUTH_SOURCE_INVALID") from None
    if (str(address) != value or address.packed[0] == 0 or address.packed[0] >= 224
            or address not in network or address in (network.network_address, network.broadcast_address)
            or address.is_unspecified or address.is_multicast):
        raise AdmissionError("AUTH_SOURCE_INVALID")
    return str(address)


def _validate(policy, identity, package, protocol_version, observer_requested):
    if type(protocol_version) is not int or protocol_version != 1:
        raise AdmissionError("PROTOCOL_VERSION_UNSUPPORTED")
    if (not isinstance(identity, AuthenticatedGameIdentity)
            or type(identity.user_id) is not int or not 0 < identity.user_id <= 0xFFFFFFFF
            or type(identity.auth_version) is not int or not 0 < identity.auth_version <= 0x7FFFFFFFFFFFFFFF
            or not isinstance(identity.username, str) or not identity.username or "\x00" in identity.username
            or len(identity.username.encode("utf-8")) > 64):
        raise AdmissionError("AUTH_IDENTITY_INVALID")
    if not isinstance(identity.product, str) or identity.product not in GAME_PRODUCTS:
        raise AdmissionError("GAME_PRODUCT_REQUIRED")
    mode = GAME_PRODUCTS[identity.product]
    if policy.official_only and mode != 1:
        raise AdmissionError("GAMEPLAY_MODE_UNSUPPORTED")
    if (not isinstance(identity.game_instance, str)
            or re.fullmatch(r"[0-9a-f]{32}", identity.game_instance) is None
            or identity.game_instance == "0" * 32):
        raise AdmissionError("GAME_INSTANCE_INVALID")
    policy.approved_release.admit(package)
    if identity.release_identity != policy.approved_release.storage_key:
        raise AdmissionError("AUTH_RELEASE_MISMATCH")
    _client_ip(identity.source_ip, policy.client_network)
    if type(observer_requested) is not bool:
        raise AdmissionError("OB_REQUEST_INVALID")
    if observer_requested and (not policy.observer_allowed or identity.observer_admitted_run != identity.game_instance):
        raise AdmissionError("OB_NOT_ALLOWED")
    return mode


class SessionReader:
    """Read a consistent snapshot of only the authenticated requester's rooms."""

    def __init__(self, database: Path, policy: ServerPolicy):
        if not isinstance(policy, ServerPolicy):
            raise AdmissionError("SERVER_POLICY_INVALID")
        self.database = Path(database)
        self.policy = policy

    def read(self, identity: AuthenticatedGameIdentity, package: Mapping | None, *,
             protocol_version: int, observer_requested: bool = False,
             now: datetime | None = None) -> tuple[Session, ...]:
        mode = _validate(self.policy, identity, package, protocol_version, observer_requested)
        instant = datetime.now(timezone.utc) if now is None else now
        if instant.tzinfo is None:
            raise AdmissionError("SERVER_CLOCK_INVALID")
        timestamp = instant.astimezone(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
        try:
            # mode=ro forbids missing-file creation and writes. The transaction
            # makes revocation/run checks and session selection one DB snapshot.
            with closing(sqlite3.connect(self.database.resolve().as_uri() + "?mode=ro", uri=True, timeout=1.0)) as connection:
                # A canceled HTTPS request must not leave an unbounded database
                # worker behind. Bound both lock wait and SQLite VM work; the
                # transaction remains read-only and consistent across queries.
                deadline = time.monotonic() + 2.0
                connection.set_progress_handler(lambda: int(time.monotonic() >= deadline), 1000)
                connection.execute("BEGIN")
                principal = connection.execute(
                    """SELECT u.username,u.auth_version,r.source_ip FROM users u JOIN game_runs r ON r.user_id=u.id
                       WHERE u.id=? AND r.instance=? AND r.retired_at IS NULL AND r.mode=? AND r.release_identity=?
                       AND datetime(r.seen_at)>=datetime(?,'-90 seconds') AND datetime(r.seen_at)<=datetime(?)""",
                    (identity.user_id, identity.game_instance, mode, self.policy.approved_release.storage_key, timestamp, timestamp),
                ).fetchone()
                if principal != (identity.username, identity.auth_version, identity.source_ip):
                    raise AdmissionError("AUTH_RUN_REVOKED_OR_STALE")
                rows = connection.execute(
                    """SELECT g.id,g.type_id,g.creator_id,host.username,g.gameplay_mode,p.user_id,r.source_ip
                       FROM game_sessions g
                       JOIN users host ON host.id=g.creator_id
                       JOIN game_runs hr ON hr.user_id=g.creator_id AND hr.retired_at IS NULL
                       JOIN participants p ON p.game_id=g.id
                       JOIN game_runs r ON r.user_id=p.user_id AND r.retired_at IS NULL
                       WHERE g.destroyed_at IS NULL AND g.gameplay_mode=? AND g.release_identity=?
                       AND hr.mode=g.gameplay_mode AND hr.release_identity=g.release_identity
                       AND datetime(hr.seen_at)>=datetime(?,'-90 seconds') AND datetime(hr.seen_at)<=datetime(?)
                       AND r.mode=g.gameplay_mode AND r.release_identity=g.release_identity
                       AND datetime(r.seen_at)>=datetime(?,'-90 seconds') AND datetime(r.seen_at)<=datetime(?)
                       AND EXISTS(SELECT 1 FROM participants requester WHERE requester.game_id=g.id AND requester.user_id=?)
                       AND EXISTS(SELECT 1 FROM participants host_member WHERE host_member.game_id=g.id AND host_member.user_id=g.creator_id)
                       ORDER BY g.id DESC,p.user_id""",
                    (mode, self.policy.approved_release.storage_key, timestamp, timestamp, timestamp, timestamp, identity.user_id),
                ).fetchmany(4097)
                if len(rows) > 4096:
                    raise AdmissionError("SESSION_SNAPSHOT_TOO_LARGE")
        except sqlite3.Error:
            raise AdmissionError("PUBLIC_SCHEMA_UNAVAILABLE") from None
        grouped = {}
        for sid, kind, host, username, gameplay, user_id, source_ip in rows:
            address = _client_ip(source_ip, self.policy.client_network)
            entry = grouped.setdefault(sid, ((sid, kind, host, username, gameplay), []))
            entry[1].append(Member(user_id, address))
        result = []
        for fields, members in grouped.values():
            # Reject ambiguous ownership/IP collisions; never infer membership
            # from station_urls or a client-supplied username/address alone.
            ids = {member.user_id for member in members}
            ips = {member.source_ip for member in members}
            if len(ids) != len(members) or len(ips) != len(members):
                raise AdmissionError("SESSION_MEMBERSHIP_AMBIGUOUS")
            if len(members) >= 2 and identity.user_id in ids and fields[2] in ids:
                result.append(Session(*fields, tuple(members)))
        return tuple(result)
