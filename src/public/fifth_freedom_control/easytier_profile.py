"""Finite owned-development EasyTier recipes and observations, never admission.

The public recipe has no PSK/private key. Parsing and RPC observation use only
the standard library. Private TOML generation lazily requires cryptography;
there is deliberately no handwritten X25519 implementation or runtime launcher.
"""
import base64
from dataclasses import asdict, dataclass, field
import hashlib
import ipaddress
import json
import os
import re
import stat
import sys
from urllib.parse import urlsplit
import uuid

from .overlay_supervisor import RuntimeCandidateIdentity


class ProfileError(ValueError):
    """Only fixed error codes; never include input, private TOML or RPC output."""


SCHEMA = "ff-easytier-owned-probe-v1"
PURPOSE = "owned-isolated-development"
MAX_RECIPE = 16384
MAX_PEERS = 65536
_RFC1918 = tuple(ipaddress.ip_network(value) for value in
                 ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))


def _fail(code="PROFILE_INVALID"):
    raise ProfileError(code) from None


def _object(value, keys):
    if type(value) is not dict or set(value) != set(keys):
        _fail()
    return value


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            _fail("JSON_DUPLICATE_KEY")
        result[key] = value
    return result


def _json(content, limit):
    if type(content) is not bytes or not 1 <= len(content) <= limit:
        _fail("JSON_SIZE_INVALID")
    try:
        return json.loads(content.decode("utf-8"), object_pairs_hook=_unique,
                          parse_constant=lambda _: _fail("JSON_NUMBER_INVALID"))
    except (UnicodeError, ValueError, RecursionError):
        _fail("JSON_INVALID")


def _canonical(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True) + "\n").encode()


def _key(value):
    try:
        raw = base64.b64decode(value, validate=True) if type(value) is str else b""
    except (ValueError, TypeError):
        _fail("PUBLIC_KEY_INVALID")
    if len(raw) != 32 or not any(raw) or base64.b64encode(raw).decode() != value:
        _fail("PUBLIC_KEY_INVALID")
    return raw


def _integer(value, low=1, high=65535):
    if type(value) is not int or not low <= value <= high:
        _fail()
    return value


def _private_ip(value):
    try:
        address = ipaddress.IPv4Address(value) if type(value) is str else None
        if address is None or str(address) != value or not any(address in net for net in _RFC1918):
            _fail()
        return address
    except ipaddress.AddressValueError:
        _fail()


@dataclass(frozen=True)
class Endpoint:
    underlay_ipv4: str
    overlay_cidr: str
    listen_port: int
    public_key: str = field(repr=False)

    @property
    def overlay(self):
        return ipaddress.IPv4Interface(self.overlay_cidr)


@dataclass(frozen=True)
class ProbeProfile:
    # Construct through parse_profile. Every consumer revalidates to prevent a
    # manually constructed dataclass from bypassing the finite recipe policy.
    transport: str
    role: str
    instance_id: str
    network_name: str
    interface_name: str
    rpc_port: int
    local: Endpoint
    remote: Endpoint
    candidate: RuntimeCandidateIdentity
    schema: str = SCHEMA
    purpose: str = PURPOSE

    @property
    def canonical_bytes(self):
        return _canonical(asdict(self))

    @property
    def sha256(self):
        return hashlib.sha256(self.canonical_bytes).hexdigest()

    def uri(self, endpoint):
        suffix = "/" if self.transport == "wss" else ""
        return f"{self.transport}://{endpoint.underlay_ipv4}:{endpoint.listen_port}{suffix}"


def parse_profile(content):
    value = _object(_json(content, MAX_RECIPE), (
        "schema", "purpose", "transport", "role", "instance_id", "network_name",
        "interface_name", "rpc_port", "local", "remote", "candidate"))
    if (value["schema"] != SCHEMA or value["purpose"] != PURPOSE
            or value["transport"] not in ("tcp", "quic", "wss")
            or value["role"] not in ("initiator", "responder")):
        _fail()
    try:
        ident = uuid.UUID(value["instance_id"])
        if str(ident) != value["instance_id"] or ident.version != 4:
            _fail()
    except (ValueError, TypeError, AttributeError):
        _fail()
    for name, expression in (("network_name", r"ffprobe-[0-9a-f]{32}"),
                             ("interface_name", r"ff[a-z0-9]{1,12}")):
        if type(value[name]) is not str or re.fullmatch(expression, value[name]) is None:
            _fail()
    _integer(value["rpc_port"], 1024)
    endpoints = []
    for name in ("local", "remote"):
        item = _object(value[name], ("underlay_ipv4", "overlay_cidr", "listen_port", "public_key"))
        _private_ip(item["underlay_ipv4"])
        _integer(item["listen_port"], 1024)
        _key(item["public_key"])
        try:
            overlay = ipaddress.IPv4Interface(item["overlay_cidr"])
            if (str(overlay) != item["overlay_cidr"] or overlay.network.prefixlen != 30
                    or overlay.ip in (overlay.network.network_address, overlay.network.broadcast_address)):
                _fail()
            _private_ip(str(overlay.ip))
        except (ValueError, TypeError):
            _fail()
        endpoints.append(Endpoint(**item))
    local, remote = endpoints
    if (local.underlay_ipv4 == remote.underlay_ipv4 or local.public_key == remote.public_key
            or local.overlay.network != remote.overlay.network or local.overlay.ip == remote.overlay.ip
            or any(_private_ip(end.underlay_ipv4) in local.overlay.network for end in endpoints)
            or value["rpc_port"] == local.listen_port):
        _fail()
    candidate = _object(value["candidate"], RuntimeCandidateIdentity.__dataclass_fields__)
    try:
        candidate = RuntimeCandidateIdentity(**candidate)
    except (TypeError, ValueError, RuntimeError):
        _fail("CANDIDATE_INVALID")
    return ProbeProfile(**{key: value[key] for key in value if key not in ("local", "remote", "candidate")},
                        local=local, remote=remote, candidate=candidate)


def _validated(profile):
    if type(profile) is not ProbeProfile:
        _fail()
    try:
        return parse_profile(profile.canonical_bytes)
    except (TypeError, ValueError, RecursionError):
        _fail()


def secure_flags(profile):
    """Exact upstream FlagsInConfig names. No fictional disable-TCP-STUN flag."""
    profile = _validated(profile)
    return dict(default_protocol=profile.transport, dev_name=profile.interface_name,
        enable_encryption=True, encryption_algorithm="aes-256-gcm", enable_ipv6=False,
        mtu=1380, no_tun=False, use_smoltcp=False, bind_device=True,
        data_compress_algo=1, enable_exit_node=False, proxy_forward_by_system=False,
        disable_p2p=True, p2p_only=True, lazy_p2p=False, need_p2p=False,
        relay_all_peer_rpc=False, relay_network_whitelist="", private_mode=True,
        disable_tcp_hole_punching=True, disable_udp_hole_punching=True,
        disable_sym_hole_punching=True, disable_upnp=True, disable_relay_data=True,
        enable_udp_broadcast_relay=False, accept_dns=False, enable_kcp_proxy=False,
        disable_kcp_input=True, disable_relay_kcp=True, enable_relay_foreign_network_kcp=False,
        enable_quic_proxy=False, disable_quic_input=True, disable_relay_quic=True,
        enable_relay_foreign_network_quic=False)


def emit_private_toml(profile, *, network_secret, local_private_key):
    """Caller supplies fresh random 32-byte secrets. Never log the return value.

    This checks key consistency, not whether the caller actually used a CSPRNG.
    The PSK is represented as canonical base64, identically on both owned peers.
    """
    profile = _validated(profile)
    if any(type(secret) is not bytes or len(secret) != 32 or not any(secret)
           for secret in (network_secret, local_private_key)):
        _fail("SECRET_MATERIAL_INVALID")
    try:
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
    except ImportError:
        _fail("CRYPTO_PROVIDER_REQUIRED")
    try:
        private = X25519PrivateKey.from_private_bytes(local_private_key)
        if private.public_key().public_bytes_raw() != _key(profile.local.public_key):
            _fail("KEY_PAIR_MISMATCH")
        # Reject low-order remote public keys using the audited provider.
        private.exchange(X25519PublicKey.from_public_bytes(_key(profile.remote.public_key)))
    except ValueError:
        _fail("KEY_MATERIAL_INVALID")
    def literal(value):
        if type(value) is bool:
            return "true" if value else "false"
        return json.dumps(value, ensure_ascii=True)
    root = dict(instance_name=profile.network_name, instance_id=profile.instance_id,
                hostname=profile.interface_name, ipv4=profile.local.overlay_cidr, dhcp=False,
                ipv6_public_addr_provider=False, ipv6_public_addr_auto=False,
                listeners=[profile.uri(profile.local)], mapped_listeners=[], exit_nodes=[],
                routes=[], proxy_network=[], port_forward=[], stun_servers=[], stun_servers_v6=[])
    if profile.role == "responder":
        root["peer"] = []
    lines = [f"{key} = {literal(value)}" for key, value in root.items()]
    sections = [("network_identity", dict(network_name=profile.network_name,
                    network_secret=base64.b64encode(network_secret).decode())),
                ("secure_mode", dict(enabled=True, local_public_key=profile.local.public_key,
                    local_private_key=base64.b64encode(local_private_key).decode())),
                ("flags", secure_flags(profile))]
    for name, values in sections:
        lines += ["", f"[{name}]"] + [f"{key} = {literal(value)}" for key, value in values.items()]
    if profile.role == "initiator":
        lines += ["", "[[peer]]", f"uri = {literal(profile.uri(profile.remote))}",
                  f"peer_public_key = {literal(profile.remote.public_key)}"]
    return ("\n".join(lines) + "\n").encode()


def write_private_toml(path, content):
    """Create one new 0600 regular file in an owned 0700 directory; no replace."""
    descriptors = []
    try:
        raw = os.fspath(path)
        if (sys.platform != "linux" or type(raw) is not str or not raw.startswith("/")
                or any(part in ("", ".", "..") for part in raw.split("/")[1:])
                or type(content) is not bytes or not 1 <= len(content) <= 16384):
            _fail("PRIVATE_OUTPUT_INVALID")
        directory = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        descriptors.append(directory)
        for part in raw.split("/")[1:-1]:
            directory = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=directory)
            descriptors.append(directory)
            info = os.fstat(directory)
            sticky_root = info.st_uid == 0 and bool(info.st_mode & stat.S_ISVTX)
            if info.st_uid not in (0, os.geteuid()) or info.st_mode & 0o022 and not sticky_root:
                _fail("PRIVATE_OUTPUT_INVALID")
        info = os.fstat(directory)
        if info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) != 0o700:
            _fail("PRIVATE_OUTPUT_INVALID")
        descriptor = os.open(raw.split("/")[-1], os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                             0o600, dir_fd=directory)
        descriptors.append(descriptor)
        os.fchmod(descriptor, 0o600)
        remaining = memoryview(content)
        while remaining:
            count = os.write(descriptor, remaining)
            if count <= 0:
                _fail("PRIVATE_OUTPUT_FAILED")
            remaining = remaining[count:]
        os.fsync(descriptor)
        os.fsync(directory)
    except OSError:
        _fail("PRIVATE_OUTPUT_FAILED")
    finally:
        for descriptor in reversed(descriptors):
            os.close(descriptor)


def core_arguments(profile, config_path):
    profile = _validated(profile)
    if (type(config_path) is not str or not config_path.startswith("/")
            or any(part in ("", ".", "..") for part in config_path.split("/")[1:]) or "\x00" in config_path):
        _fail("CONFIG_PATH_INVALID")
    return ("--config-file", config_path, "--disable-env-parsing", "--rpc-portal",
            f"127.0.0.1:{profile.rpc_port}", "--rpc-portal-whitelist", "127.0.0.1/32")


def peer_query_arguments(profile):
    profile = _validated(profile)
    # -o json alone prints a table projection and loses the authentication data.
    return ("--rpc-portal", f"127.0.0.1:{profile.rpc_port}", "--instance-id", profile.instance_id,
            "--verbose", "peer", "list")


@dataclass(frozen=True)
class PeerObservation:
    profile_sha256: str
    connection_id: str
    local_peer_id: int
    remote_peer_id: int
    # No raw RPC output, keys, secrets or arbitrary upstream error strings.


def _rpc_key(value):
    if type(value) is not list or len(value) != 32 or any(type(x) is not int or not 0 <= x <= 255 for x in value):
        _fail("PEER_OBSERVATION_INVALID")
    return bytes(value)


def _tunnel_endpoint(value, scheme, address, port=None):
    _object(value, ("url",))
    if type(value["url"]) is not str or len(value["url"]) > 128:
        _fail()
    url = urlsplit(value["url"])
    actual_port = url.port
    if (url.scheme != scheme or url.hostname != address or url.username is not None or url.password is not None
            or url.query or url.fragment or url.path not in ("", "/") or actual_port is None
            or not 1 <= actual_port <= 65535 or port is not None and actual_port != port):
        _fail()


def parse_peer_observation(profile, content):
    """Parse the pinned CLI verbose route/peer projection, not a trusted RPC.

    The owner must bind CLI ELF, exact core/namespace/loopback RPC, deadline and
    output limit. This list joins routes to peers: it cannot prove absence of
    unrouted connections or substitute for a handshake key allowlist.
    """
    profile = _validated(profile)
    try:
        pairs = _json(content, MAX_PEERS)
        if type(pairs) is not list or len(pairs) != 1:
            _fail()
        pair = _object(pairs[0], ("peer", "route"))
        peer, route = pair["peer"], pair["route"]
        if type(peer) is not dict or type(route) is not dict:
            _fail()
        if type(peer["conns"]) is not list or len(peer["conns"]) != 1:
            _fail()
        conn = peer["conns"][0]
        remote_id = _integer(peer["peer_id"], 1, 2**32 - 1)
        local_id = _integer(conn["my_peer_id"], 1, 2**32 - 1)
        if (local_id == remote_id or conn["peer_id"] != remote_id or route["peer_id"] != remote_id
                or type(conn["peer_id"]) is not int or type(route["peer_id"]) is not int
                or type(route["cost"]) is not int or route["cost"] != 1
                or type(route["next_hop_peer_id"]) is not int or route["next_hop_peer_id"] != remote_id
                or conn["is_closed"] is not False
                or conn["is_client"] is not (profile.role == "initiator")
                or conn["network_name"] != profile.network_name
                or type(conn["secure_auth_level"]) is not int or conn["secure_auth_level"] != 3
                or _rpc_key(conn["noise_local_static_pubkey"]) != _key(profile.local.public_key)
                or _rpc_key(conn["noise_remote_static_pubkey"]) != _key(profile.remote.public_key)):
            _fail()
        ipv4 = _object(route["ipv4_addr"], ("address", "network_length"))
        address = _object(ipv4["address"], ("addr",))["addr"]
        if (type(address) is not int or address != int(profile.remote.overlay.ip)
                or type(ipv4["network_length"]) is not int or ipv4["network_length"] != 30
                or route["proxy_cidrs"] != []):
            _fail()
        conn_id = conn["conn_id"]
        parsed_id = uuid.UUID(conn_id)
        if str(parsed_id) != conn_id:
            _fail()
        # UUID is protobuf's four big-endian u32 parts, not a string here.
        parts = {f"part{i + 1}": int.from_bytes(parsed_id.bytes[i*4:i*4+4], "big") for i in range(4)}
        if (peer["default_conn_id"] != parts or peer["directly_connected_conns"] != [parts]
                or any(type(value) is not int for value in peer["default_conn_id"].values())):
            _fail()
        tunnel = _object(conn["tunnel"], ("tunnel_type", "local_addr", "remote_addr", "resolved_remote_addr"))
        if tunnel["tunnel_type"] != profile.transport:
            _fail()
        _tunnel_endpoint(tunnel["local_addr"], profile.transport, profile.local.underlay_ipv4,
                         profile.local.listen_port if profile.role == "responder" else None)
        for name in ("remote_addr", "resolved_remote_addr"):
            _tunnel_endpoint(tunnel[name], profile.transport, profile.remote.underlay_ipv4,
                             profile.remote.listen_port if profile.role == "initiator" else None)
        return PeerObservation(profile.sha256, conn_id, local_id, remote_id)
    except (ValueError, TypeError, KeyError, AttributeError, OverflowError):
        _fail("PEER_OBSERVATION_INVALID")
