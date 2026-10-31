"""Linux owner-controlled configuration. Never obtain keys from HTTP or argv."""
from contextlib import contextmanager
from dataclasses import dataclass, field
import ipaddress
import json
import os
from pathlib import Path
import ssl
import stat
import sys

from .authenticated_bridge import AuthBridgeConfig
from .session_reader import AdmissionError, ReleaseIdentity, ServerPolicy, SessionReader


@contextmanager
def private_file(path):
    """Open a private regular file without following any path-component symlink.

    This is deliberately Linux-only; Windows ACLs need a separate implementation.
    The immediate directory must be owner-only. Ancestors may be owned by root
    or this uid, with a root-owned sticky directory permitted for /tmp fixtures.
    Holding directory/file descriptors avoids check-then-open substitution.
    """
    descriptors = []
    try:
        if sys.platform != "linux" or not isinstance(path, (str, Path)):
            raise AdmissionError("PRIVATE_FILE_UNSUPPORTED")
        raw = str(path)
        parts = raw.split("/")
        if not raw.startswith("/") or any(part in (".", "..", "") for part in parts[1:]):
            raise AdmissionError("PRIVATE_FILE_INVALID")
        uid = os.geteuid()
        directory = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        descriptors.append(directory)
        for component in parts[1:-1]:
            directory = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                dir_fd=directory)
            descriptors.append(directory)
            info = os.fstat(directory)
            mode = stat.S_IMODE(info.st_mode)
            sticky_root = info.st_uid == 0 and bool(info.st_mode & stat.S_ISVTX)
            if info.st_uid not in (0, uid) or (mode & 0o022 and not sticky_root):
                raise AdmissionError("PRIVATE_FILE_INVALID")
        parent = os.fstat(directory)
        if parent.st_uid != uid or stat.S_IMODE(parent.st_mode) not in (0o500, 0o700):
            raise AdmissionError("PRIVATE_FILE_INVALID")
        descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NONBLOCK | os.O_NOFOLLOW | os.O_CLOEXEC,
                             dir_fd=directory)
        descriptors.append(descriptor)
        info = os.fstat(descriptor)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != uid or info.st_nlink != 1
                or stat.S_IMODE(info.st_mode) not in (0o400, 0o600)):
            raise AdmissionError("PRIVATE_FILE_INVALID")
        yield descriptor
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(info, field) != getattr(after, field) for field in fields):
            raise AdmissionError("PRIVATE_FILE_CHANGED")
    except OSError:
        raise AdmissionError("PRIVATE_FILE_UNAVAILABLE") from None
    finally:
        for descriptor in reversed(descriptors):
            os.close(descriptor)


def read_private(path, maximum):
    with private_file(path) as descriptor:
        data = bytearray()
        while len(data) <= maximum:
            block = os.read(descriptor, maximum + 1 - len(data))
            if not block:
                break
            data.extend(block)
        if len(data) > maximum:
            raise AdmissionError("PRIVATE_FILE_TOO_LARGE")
        return bytes(data)


def load_issuer_key(path):
    # The native opt-in loader consumes the same exact 32 raw bytes. A newline,
    # hex-encoded string or short read is not repaired into another signing key.
    data = read_private(path, 32)
    if len(data) != 32:
        raise AdmissionError("AUTH_CONFIG_INVALID")
    return AuthBridgeConfig(data)


def unique_json(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise AdmissionError("JSON_INVALID")
        result[key] = value
    return result


def parse_json(data):
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=unique_json,
                          parse_constant=lambda _: (_ for _ in ()).throw(AdmissionError("JSON_INVALID")))
    except (UnicodeError, ValueError, RecursionError):
        raise AdmissionError("JSON_INVALID") from None


@dataclass(frozen=True)
class RuntimeConfig:
    listen_ip: str
    listen_port: int
    database: Path = field(repr=False)
    issuer_key_file: Path = field(repr=False)
    tls_certificate_file: Path = field(repr=False)
    tls_private_key_file: Path = field(repr=False)
    policy: ServerPolicy


def load_config(path):
    config = parse_json(read_private(path, 16384))
    expected = {"schema", "listen_ip", "listen_port", "database", "issuer_key_file",
                "tls_certificate_file", "tls_private_key_file", "approved_release", "client_network",
                "observer_allowed", "official_only"}
    if not isinstance(config, dict) or set(config) != expected or type(config["schema"]) is not int or config["schema"] != 1:
        raise AdmissionError("PUBLIC_CONFIG_INVALID")
    try:
        address = ipaddress.IPv4Address(config["listen_ip"])
        network = ipaddress.IPv4Network(config["client_network"], strict=True)
        port = config["listen_port"]
        if (str(address) != config["listen_ip"] or address.is_multicast or address.is_reserved
                or type(port) is not int or not 1024 <= port <= 65535):
            raise AdmissionError("PUBLIC_CONFIG_INVALID")
        release = config["approved_release"]
        if not isinstance(release, dict) or set(release) != {"release_id", "compat_id", "manifest_sha256"}:
            raise AdmissionError("PUBLIC_CONFIG_INVALID")
        policy = ServerPolicy(ReleaseIdentity(**release), network, config["observer_allowed"], config["official_only"])
        paths = []
        for key in ("database", "issuer_key_file", "tls_certificate_file", "tls_private_key_file"):
            value = config[key]
            if not isinstance(value, str) or not value.startswith("/") or "\x00" in value or any(p in (".", "..", "") for p in value.split("/")[1:]):
                raise AdmissionError("PUBLIC_CONFIG_INVALID")
            paths.append(Path(value))
        return RuntimeConfig(str(address), port, *paths, policy)
    except (TypeError, ValueError):
        raise AdmissionError("PUBLIC_CONFIG_INVALID") from None


def build_tls_context(config):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.options |= ssl.OP_NO_COMPRESSION
    context.set_alpn_protocols(["http/1.1"])
    try:
        # OpenSSL reads the already-validated Linux descriptors. It never reopens
        # the user path after an ownership/permission check (no symlink race).
        with private_file(config.tls_certificate_file) as certificate, private_file(config.tls_private_key_file) as key:
            if os.fstat(certificate).st_size > 65536 or os.fstat(key).st_size > 16384:
                raise AdmissionError("TLS_CONFIG_INVALID")
            context.load_cert_chain(f"/proc/self/fd/{certificate}", f"/proc/self/fd/{key}", password=lambda: "")
    except (ssl.SSLError, OSError, ValueError):
        raise AdmissionError("TLS_CONFIG_INVALID") from None
    return context


def validate_database(config):
    # Preserve the live SQLite/WAL path: the native writer owns mutation and the
    # reader opens mode=ro. Startup checks schema, never creates or migrates it.
    import sqlite3
    from contextlib import closing
    try:
        with private_file(config.database):
            pass
        with closing(sqlite3.connect(config.database.as_uri() + "?mode=ro", uri=True, timeout=1.0)) as connection:
            connection.execute("SELECT id,username,auth_version FROM users LIMIT 0")
            connection.execute("SELECT user_id,instance,mode,source_ip,release_identity,retired_at,seen_at FROM game_runs LIMIT 0")
            connection.execute("SELECT id,type_id,creator_id,gameplay_mode,release_identity,destroyed_at FROM game_sessions LIMIT 0")
            connection.execute("SELECT game_id,user_id FROM participants LIMIT 0")
    except sqlite3.Error:
        raise AdmissionError("PUBLIC_SCHEMA_UNAVAILABLE") from None
    return SessionReader(config.database, config.policy)
