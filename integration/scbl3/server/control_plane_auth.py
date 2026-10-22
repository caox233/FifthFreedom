from __future__ import annotations

import base64
import hashlib
import hmac
import ipaddress
import json
import time
from dataclasses import dataclass
from typing import Any, Callable


@dataclass(frozen=True)
class AuthenticatedIdentity:
    user_id: int
    username: str
    source_ip: str
    auth_version: int


class ControlAuthError(ValueError):
    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


def _decode_base64url(value: str) -> bytes:
    if not value or len(value) > 4096:
        raise ControlAuthError("invalid_auth_token")
    if any(character not in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-" for character in value):
        raise ControlAuthError("invalid_auth_token")
    padding = "=" * (-len(value) % 4)
    try:
        decoded = base64.b64decode(value + padding, altchars=b"-_", validate=True)
    except (ValueError, TypeError):
        raise ControlAuthError("invalid_auth_token") from None
    if base64.urlsafe_b64encode(decoded).rstrip(b"=").decode("ascii") != value:
        raise ControlAuthError("invalid_auth_token")
    return decoded


def validate_control_auth_token(
    token: str,
    source_ip: str,
    *,
    key: bytes,
    identity_loader: Callable[[int], tuple[Any, ...] | None],
    token_version: str = "v1",
    min_ttl_seconds: int = 5 * 60,
    max_ttl_seconds: int = 7 * 24 * 60 * 60,
    max_future_skew_seconds: int = 60,
    now: int | None = None,
) -> AuthenticatedIdentity:
    if not key:
        raise ControlAuthError("auth_token_key_unavailable")
    if not token or len(token) > 4096:
        raise ControlAuthError("invalid_auth_token")
    parts = token.split(".")
    if len(parts) != 3 or parts[0] != token_version:
        raise ControlAuthError("invalid_auth_token")
    payload_segment, signature_segment = parts[1], parts[2]
    signature = _decode_base64url(signature_segment)
    if len(signature) != hashlib.sha256().digest_size:
        raise ControlAuthError("invalid_auth_token")
    expected = hmac.new(
        key,
        f"{token_version}.{payload_segment}".encode("ascii"),
        hashlib.sha256,
    ).digest()
    if not hmac.compare_digest(expected, signature):
        raise ControlAuthError("invalid_auth_token")
    try:
        payload = json.loads(_decode_base64url(payload_segment).decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise ControlAuthError("invalid_auth_token") from None
    if not isinstance(payload, dict):
        raise ControlAuthError("invalid_auth_token")

    integer_fields = ("user_id", "issued_at", "expires_at", "auth_version")
    if any(
        not isinstance(payload.get(field), int)
        or isinstance(payload.get(field), bool)
        for field in integer_fields
    ):
        raise ControlAuthError("invalid_auth_token")
    username = payload.get("username")
    token_source_ip = payload.get("source_ip")
    if (
        not isinstance(username, str)
        or not username
        or len(username.encode("utf-8")) > 64
        or not isinstance(token_source_ip, str)
    ):
        raise ControlAuthError("invalid_auth_token")
    user_id = int(payload["user_id"])
    issued_at = int(payload["issued_at"])
    expires_at = int(payload["expires_at"])
    auth_version = int(payload["auth_version"])
    if user_id <= 0 or auth_version < 1:
        raise ControlAuthError("invalid_auth_token")
    try:
        canonical_token_ip = str(ipaddress.ip_address(token_source_ip))
        canonical_source_ip = str(ipaddress.ip_address(source_ip))
    except ValueError:
        raise ControlAuthError("invalid_auth_token") from None
    if canonical_token_ip != token_source_ip or canonical_token_ip != canonical_source_ip:
        raise ControlAuthError("auth_token_wrong_source")

    current_time = int(time.time()) if now is None else now
    if issued_at > current_time + max_future_skew_seconds:
        raise ControlAuthError("auth_token_future_issued")
    if expires_at <= current_time:
        raise ControlAuthError("auth_token_expired")
    lifetime = expires_at - issued_at
    if lifetime < min_ttl_seconds or lifetime > max_ttl_seconds:
        raise ControlAuthError("invalid_auth_token")

    try:
        row = identity_loader(user_id)
    except Exception:
        raise ControlAuthError("auth_identity_database_unavailable") from None
    if row is None:
        raise ControlAuthError("auth_token_unknown_user")
    database_username, database_auth_version = str(row[0]), int(row[1])
    if username != database_username or auth_version != database_auth_version:
        raise ControlAuthError("auth_token_revoked")
    return AuthenticatedIdentity(
        user_id=user_id,
        username=database_username,
        source_ip=canonical_source_ip,
        auth_version=database_auth_version,
    )
