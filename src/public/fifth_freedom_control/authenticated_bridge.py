"""Versioned, in-process authenticated bridge to the read-only session reader.

The v1 token envelope and canonical base64url checks are adapted from preserved
SCBL3 control_plane_auth.py and the retained LAN server api.rs. See NOTICE.md and
the repository GPLv3 LICENSE. HMAC uses Python's standard library; no new token
algorithm, key derivation or token issuer is introduced here.

The caller must supply the actual issuer's key over a trusted process/configuration
boundary and the observed connection source. Request headers are not a key or
source authority. This module opens no listener and does not read environment
variables, log tokens, create keys, or enable the Rust Public startup profile.
"""
import base64
from dataclasses import dataclass, field
from datetime import datetime, timezone
import hashlib
import hmac
import ipaddress
import json
import re
import time
from typing import Callable, Mapping

from .session_reader import (
    AdmissionError, AuthenticatedGameIdentity, ReleaseIdentity, Session, SessionReader,
)

BRIDGE_CONTRACT_VERSION = 1
TOKEN_VERSION = "v1"
MAX_TOKEN_BYTES = 4096
MIN_TTL_SECONDS = 300
MAX_TTL_SECONDS = 604800
MAX_FUTURE_SKEW_SECONDS = 60


@dataclass(frozen=True)
class AuthBridgeConfig:
    key: bytes = field(repr=False)
    contract_version: int = BRIDGE_CONTRACT_VERSION

    def __post_init__(self):
        if self.key is None or self.key == b"":
            raise AdmissionError("AUTH_KEY_UNAVAILABLE")
        if type(self.key) is not bytes or len(self.key) != 32:
            raise AdmissionError("AUTH_CONFIG_INVALID")
        if type(self.contract_version) is not int or self.contract_version != BRIDGE_CONTRACT_VERSION:
            raise AdmissionError("AUTH_BRIDGE_VERSION_UNSUPPORTED")

    @classmethod
    def from_hex(cls, value: str | None):
        if value is None or value == "":
            raise AdmissionError("AUTH_KEY_UNAVAILABLE")
        if not isinstance(value, str) or re.fullmatch(r"[0-9A-Fa-f]{64}", value) is None:
            raise AdmissionError("AUTH_CONFIG_INVALID")
        return cls(bytes.fromhex(value))


def _base64url(value: str) -> bytes:
    # Preserve donor/Rust canonical no-padding semantics; equivalent encodings
    # must not become different token strings with the same decoded signature.
    if not value or len(value) > MAX_TOKEN_BYTES or re.fullmatch(r"[A-Za-z0-9_-]+", value) is None:
        raise AdmissionError("AUTH_TOKEN_INVALID")
    try:
        decoded = base64.b64decode(value + "=" * (-len(value) % 4), altchars=b"-_", validate=True)
    except (ValueError, TypeError):
        raise AdmissionError("AUTH_TOKEN_INVALID") from None
    if base64.urlsafe_b64encode(decoded).rstrip(b"=").decode("ascii") != value:
        raise AdmissionError("AUTH_TOKEN_INVALID")
    return decoded


def _unique_object(pairs):
    # Rust's claims struct rejects repeated known fields. Reject all duplicate
    # keys here rather than applying Python's otherwise silent last-value rule.
    result = {}
    for key, value in pairs:
        if key in result:
            raise AdmissionError("AUTH_TOKEN_INVALID")
        result[key] = value
    return result


def _invalid_constant(_value):
    raise AdmissionError("AUTH_TOKEN_INVALID")


def _integer(value, minimum, maximum):
    return type(value) is int and minimum <= value <= maximum


def _verify_claims(token, source_ip, config, approved_release, now):
    if not isinstance(token, str) or not token or len(token) > MAX_TOKEN_BYTES or not token.isascii():
        raise AdmissionError("AUTH_TOKEN_INVALID")
    parts = token.split(".")
    if len(parts) != 3 or parts[0] != TOKEN_VERSION:
        raise AdmissionError("AUTH_TOKEN_INVALID")
    _, payload_segment, signature_segment = parts
    signature = _base64url(signature_segment)
    if len(signature) != hashlib.sha256().digest_size:
        raise AdmissionError("AUTH_TOKEN_INVALID")
    expected = hmac.new(config.key, f"{TOKEN_VERSION}.{payload_segment}".encode("ascii"), hashlib.sha256).digest()
    if not hmac.compare_digest(expected, signature):
        raise AdmissionError("AUTH_TOKEN_INVALID")
    try:
        payload = json.loads(_base64url(payload_segment).decode("utf-8"),
                             object_pairs_hook=_unique_object, parse_constant=_invalid_constant)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise AdmissionError("AUTH_TOKEN_INVALID") from None
    if not isinstance(payload, dict):
        raise AdmissionError("AUTH_TOKEN_INVALID")
    if (not _integer(payload.get("user_id"), 1, 0xFFFFFFFF)
            or not _integer(payload.get("auth_version"), 1, 0x7FFFFFFFFFFFFFFF)
            or not _integer(payload.get("issued_at"), -0x8000000000000000, 0x7FFFFFFFFFFFFFFF)
            or not _integer(payload.get("expires_at"), -0x8000000000000000, 0x7FFFFFFFFFFFFFFF)):
        raise AdmissionError("AUTH_TOKEN_INVALID")
    username = payload.get("username")
    if not isinstance(username, str) or not username or "\x00" in username:
        raise AdmissionError("AUTH_TOKEN_INVALID")
    try:
        if len(username.encode("utf-8")) > 64:
            raise AdmissionError("AUTH_TOKEN_INVALID")
    except UnicodeEncodeError:
        raise AdmissionError("AUTH_TOKEN_INVALID") from None
    token_source = payload.get("source_ip")
    if not isinstance(token_source, str) or len(token_source) > 64 or not isinstance(source_ip, str) or len(source_ip) > 64:
        raise AdmissionError("AUTH_TOKEN_INVALID")
    try:
        canonical_token_source = str(ipaddress.ip_address(token_source))
        canonical_source = str(ipaddress.ip_address(source_ip))
    except ValueError:
        raise AdmissionError("AUTH_TOKEN_INVALID") from None
    if canonical_token_source != token_source or canonical_token_source != canonical_source:
        raise AdmissionError("AUTH_TOKEN_WRONG_SOURCE")
    issued, expiry = payload["issued_at"], payload["expires_at"]
    if issued > now + MAX_FUTURE_SKEW_SECONDS:
        raise AdmissionError("AUTH_TOKEN_FUTURE_ISSUED")
    if expiry <= now:
        raise AdmissionError("AUTH_TOKEN_EXPIRED")
    if not MIN_TTL_SECONDS <= expiry - issued <= MAX_TTL_SECONDS:
        raise AdmissionError("AUTH_TOKEN_INVALID")

    # The retained LAN producer signs this additional release identity and game
    # run. Legacy donor account tokens omit them and cannot be upgraded here.
    release = payload.get("release")
    if not isinstance(release, str) or len(release) > 200:
        raise AdmissionError("AUTH_RELEASE_REQUIRED")
    fields = release.split("|")
    if len(fields) != 4 or fields[0] != "1":
        raise AdmissionError("AUTH_RELEASE_INVALID")
    try:
        signed_release = ReleaseIdentity(*fields[1:])
    except AdmissionError:
        raise AdmissionError("AUTH_RELEASE_INVALID") from None
    if signed_release != approved_release:
        raise AdmissionError("AUTH_RELEASE_MISMATCH")
    run = payload.get("game_run")
    if not isinstance(run, dict) or not isinstance(run.get("product"), str) or not isinstance(run.get("instance"), str):
        raise AdmissionError("AUTH_GAME_RUN_REQUIRED")
    # Product/nonce canonicality, package admission and current persisted run are
    # checked by SessionReader, using this signed pair without request overrides.
    # Optional native grant is signed only after native observer-policy admission.
    # Absence preserves the old token format and grants no observer authority.
    # Never infer this grant from HTTP metadata or the request boolean.
    observer_grant = payload.get("observer_admitted_run")
    if observer_grant is not None and (not isinstance(observer_grant, str)
            or re.fullmatch(r"[0-9a-f]{32}", observer_grant) is None
            or observer_grant == "0" * 32 or observer_grant != run["instance"]):
        raise AdmissionError("AUTH_TOKEN_INVALID")
    return AuthenticatedGameIdentity(
        payload["user_id"], username, payload["auth_version"], canonical_source,
        run["product"], run["instance"], signed_release.storage_key, observer_grant,
    )


class AuthenticatedSessionBridge:
    """Verify existing signed claims, then query the current read-only DB state."""

    def __init__(self, reader: SessionReader, config: AuthBridgeConfig, *, clock: Callable[[], int] | None = None):
        if not isinstance(reader, SessionReader) or not isinstance(config, AuthBridgeConfig):
            raise AdmissionError("AUTH_CONFIG_INVALID")
        if clock is not None and not callable(clock):
            raise AdmissionError("AUTH_CONFIG_INVALID")
        self.reader = reader
        self.config = config
        self.clock = clock if clock is not None else lambda: int(time.time())

    def read(self, token: str, source_ip: str, package: Mapping | None, *,
             protocol_version: int, observer_requested: bool = False) -> tuple[Session, ...]:
        try:
            now = self.clock()
        except Exception:
            raise AdmissionError("AUTH_CLOCK_INVALID") from None
        if not _integer(now, 0, 253402300799):
            raise AdmissionError("AUTH_CLOCK_INVALID")
        identity = _verify_claims(token, source_ip, self.config, self.reader.policy.approved_release, now)
        # Do not return a verified identity alone: DB revocation and current-run
        # checks must execute as part of every bridge request, not be cached away.
        return self.reader.read(identity, package, protocol_version=protocol_version,
                                observer_requested=observer_requested,
                                now=datetime.fromtimestamp(now, timezone.utc))
