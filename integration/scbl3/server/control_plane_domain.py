from __future__ import annotations

import ipaddress
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable


def utc_ms() -> int:
    return int(time.time() * 1000)


def clean_text(value: Any, limit: int = 128) -> str:
    return str(value or "").replace("\x00", "").strip()[:limit]


def clean_int(
    value: Any,
    minimum: int = 0,
    maximum: int = 10_000_000,
) -> int | None:
    if value is None or value == "":
        return None
    try:
        number = int(value)
    except (TypeError, ValueError):
        return None
    return max(minimum, min(maximum, number))


def clean_float(
    value: Any,
    minimum: float = 0.0,
    maximum: float = 100.0,
) -> float | None:
    if value is None or value == "":
        return None
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return max(minimum, min(maximum, number))


def is_client_overlay_ip(
    value: str,
    network: ipaddress.IPv4Network = ipaddress.ip_network("10.66.0.0/24"),
) -> bool:
    try:
        ip = ipaddress.ip_address(value)
    except ValueError:
        return False
    return (
        ip.version == 4
        and ip in network
        and ip not in {network.network_address, network.broadcast_address}
    )


def normalize_gameplay_mode(value: Any) -> str:
    mode = clean_text(value, 16).lower()
    if mode not in {"official", "mod"}:
        raise ValueError("gameplayMode must be official or mod")
    return mode


@dataclass
class ClientState:
    username: str
    virtual_ip: str
    instance_id: str
    client_version: str
    easytier_version: str
    gameplay_mode: str
    gameplay_integrity_valid: bool
    game_running: bool
    game_role: str
    game_peer_ip: str
    server_latency_ms: int | None
    server_transport: str
    server_address_family: str
    game_latency_ms: int | None
    game_transport: str
    game_address_family: str
    next_hop: str
    hop_count: int | None
    game_latency_p50_ms: int | None
    game_latency_p95_ms: int | None
    game_jitter_ms: int | None
    game_loss_percent: float | None
    user_id: int | None = None
    identity_trusted: bool = False
    last_seen_ms: int = field(default_factory=utc_ms)

    def public_dict(self) -> dict[str, Any]:
        return {
            "username": self.username,
            "userId": self.user_id,
            "identityTrusted": self.identity_trusted,
            "virtualIp": self.virtual_ip,
            "instanceId": self.instance_id,
            "clientVersion": self.client_version,
            "easyTierVersion": self.easytier_version,
            "gameplayMode": self.gameplay_mode,
            "gameplayIntegrityValid": self.gameplay_integrity_valid,
            "gameRunning": self.game_running,
            "gameRole": self.game_role,
            "gamePeerIp": self.game_peer_ip,
            "serverLatencyMs": self.server_latency_ms,
            "serverTransport": self.server_transport,
            "serverAddressFamily": self.server_address_family,
            "gameLatencyMs": self.game_latency_ms,
            "gameTransport": self.game_transport,
            "gameAddressFamily": self.game_address_family,
            "nextHop": self.next_hop,
            "hopCount": self.hop_count,
            "gameLatencyP50Ms": self.game_latency_p50_ms,
            "gameLatencyP95Ms": self.game_latency_p95_ms,
            "gameJitterMs": self.game_jitter_ms,
            "gameLossPercent": self.game_loss_percent,
            "gameRoleSource": "client",
            "gameSessionId": None,
            "authoritativeHostVirtualIp": "",
            "lastSeenUnixMs": self.last_seen_ms,
        }


@dataclass(frozen=True)
class GameplayModeAssertion:
    username: str
    virtual_ip: str
    instance_id: str
    gameplay_mode: str
    gameplay_integrity_valid: bool
    user_id: int | None
    identity_trusted: bool
    last_seen_ms: int

    @classmethod
    def from_client(cls, state: ClientState) -> GameplayModeAssertion:
        return cls(
            username=state.username,
            virtual_ip=state.virtual_ip,
            instance_id=state.instance_id,
            gameplay_mode=state.gameplay_mode,
            gameplay_integrity_valid=state.gameplay_integrity_valid,
            user_id=state.user_id,
            identity_trusted=state.identity_trusted,
            last_seen_ms=state.last_seen_ms,
        )


class RuntimeState:
    def __init__(
        self,
        heartbeat_ttl_seconds: int = 20,
        *,
        mode_assertion_ttl_seconds: int = 90,
        clock: Callable[[], int] = utc_ms,
    ) -> None:
        self._heartbeat_ttl_ms = max(10, heartbeat_ttl_seconds) * 1000
        self._mode_assertion_ttl_ms = max(
            heartbeat_ttl_seconds,
            mode_assertion_ttl_seconds,
        ) * 1000
        self._clock = clock
        self.lock = threading.RLock()
        self.clients: dict[str, ClientState] = {}
        self.mode_assertions: dict[str, GameplayModeAssertion] = {}

    def cleanup(self) -> None:
        now = self._clock()
        online_cutoff = now - self._heartbeat_ttl_ms
        assertion_cutoff = now - self._mode_assertion_ttl_ms
        with self.lock:
            stale_clients = [
                key
                for key, state in self.clients.items()
                if state.last_seen_ms < online_cutoff
            ]
            for key in stale_clients:
                self.clients.pop(key, None)
            stale_assertions = [
                key
                for key, assertion in self.mode_assertions.items()
                if assertion.last_seen_ms < assertion_cutoff
            ]
            for key in stale_assertions:
                self.mode_assertions.pop(key, None)

    def upsert(self, state: ClientState) -> None:
        self.cleanup()
        with self.lock:
            for key, existing in list(self.clients.items()):
                if (
                    key != state.virtual_ip
                    and state.instance_id
                    and existing.instance_id == state.instance_id
                ):
                    self.clients.pop(key, None)
            self.clients[state.virtual_ip] = state
            assertion_key = state.username.casefold()
            for key, assertion in list(self.mode_assertions.items()):
                if key != assertion_key and (
                    assertion.virtual_ip == state.virtual_ip
                    or (
                        state.instance_id
                        and assertion.instance_id == state.instance_id
                    )
                ):
                    self.mode_assertions.pop(key, None)
            if assertion_key and state.identity_trusted:
                self.mode_assertions[assertion_key] = GameplayModeAssertion.from_client(state)

    def active_clients(self) -> list[ClientState]:
        self.cleanup()
        with self.lock:
            return sorted(
                self.clients.values(),
                key=lambda item: int(item.virtual_ip.rsplit(".", 1)[-1]),
            )

    def client_by_virtual_ip(self, virtual_ip: str) -> ClientState | None:
        self.cleanup()
        with self.lock:
            return self.clients.get(virtual_ip)

    def active_mode_assertions(self) -> list[GameplayModeAssertion]:
        self.cleanup()
        with self.lock:
            return sorted(
                self.mode_assertions.values(),
                key=lambda assertion: assertion.username.casefold(),
            )

    def mode_assertion_by_virtual_ip(
        self, virtual_ip: str
    ) -> GameplayModeAssertion | None:
        self.cleanup()
        with self.lock:
            return next(
                (
                    assertion
                    for assertion in self.mode_assertions.values()
                    if assertion.virtual_ip == virtual_ip
                ),
                None,
            )

    def invalidate_mode_assertion(
        self,
        virtual_ip: str,
        username: str = "",
    ) -> None:
        username_key = username.casefold()
        with self.lock:
            for key, assertion in list(self.mode_assertions.items()):
                if assertion.virtual_ip == virtual_ip or (
                    username_key and key == username_key
                ):
                    self.mode_assertions.pop(key, None)


def gameplay_modes_compatible(
    source: ClientState | GameplayModeAssertion,
    target: ClientState | GameplayModeAssertion,
    _context: str,
) -> tuple[bool, str]:
    if source.gameplay_mode != target.gameplay_mode:
        return (False, "gameplay mode mismatch")
    if source.gameplay_mode == "mod" and (
        not source.gameplay_integrity_valid or not target.gameplay_integrity_valid
    ):
        return (False, "mod files verification failed")
    return (True, "compatible")
