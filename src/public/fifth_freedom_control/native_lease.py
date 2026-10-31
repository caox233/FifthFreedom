"""Private parent capability for SERVER10, not an overlay admission provider.

The trusted deployment owner supplies independently obtained binding values.
Neither a HELLO nor a receipt/hash authenticates an encrypted overlay. Renewal
is deliberately driven by the supervisor's fresh observation cycle; there is
no independent timer that could keep service alive after its monitor stalls.
"""
import array
from dataclasses import asdict, dataclass, field
import ipaddress
import json
import os
import re
import socket
import struct
import time

from .owned_process import LifecycleError, OwnedChild

VERSION = "ff-public-owner-lease-v1"
MAX_FRAME = 4096
_HEX = re.compile(r"[0-9a-f]{64}")


def _invalid():
    raise LifecycleError("PUBLIC_OWNER_LEASE_INVALID") from None


@dataclass(frozen=True)
class LeaseBinding:
    config_sha256: str = field(repr=False)
    service_manifest_sha256: str = field(repr=False)
    admitted_client_storage_key: str = field(repr=False)
    runtime_profile_sha256: str = field(repr=False)
    runtime_executable_sha256: str = field(repr=False)
    netns_device: int
    netns_inode: int
    interface_index: int
    overlay_ipv4: str
    overlay_prefix: int

    def __post_init__(self):
        digests = (self.config_sha256, self.service_manifest_sha256,
                   self.runtime_profile_sha256, self.runtime_executable_sha256)
        if (any(type(value) is not str or not _HEX.fullmatch(value) for value in digests)
                or type(self.admitted_client_storage_key) is not str
                or not 1 <= len(self.admitted_client_storage_key.encode("utf-8")) <= 512
                or any(ord(char) < 32 for char in self.admitted_client_storage_key)
                or type(self.netns_device) is not int or not 0 <= self.netns_device < 2 ** 64
                or type(self.netns_inode) is not int or not 1 <= self.netns_inode < 2 ** 64
                or type(self.interface_index) is not int or not 1 <= self.interface_index < 2 ** 32
                or type(self.overlay_prefix) is not int or not 8 <= self.overlay_prefix <= 30):
            _invalid()
        try:
            address = ipaddress.IPv4Address(self.overlay_ipv4)
            network = ipaddress.IPv4Network((address, self.overlay_prefix), strict=False)
            private = ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10")
            if (str(address) != self.overlay_ipv4 or address in (network.network_address, network.broadcast_address)
                    or not any(network.subnet_of(ipaddress.IPv4Network(value)) for value in private)):
                _invalid()
        except (TypeError, ValueError):
            _invalid()


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            _invalid()
        result[key] = value
    return result


def _frame(data):
    try:
        value = json.loads(data.decode("utf-8"), object_pairs_hook=_unique,
                           parse_constant=lambda _: _invalid())
        if (type(value) is not dict or set(value) != {"version", "kind", "nonce", "sequence", "binding"}
                or value["version"] != VERSION or value["kind"] not in ("hello", "ready")
                or type(value["nonce"]) is not str or not _HEX.fullmatch(value["nonce"])
                or type(value["sequence"]) is not int or not 0 <= value["sequence"] < 2 ** 64
                or type(value["binding"]) is not dict):
            _invalid()
        binding = LeaseBinding(**value["binding"])
        return value, binding
    except (ValueError, TypeError, UnicodeError, RecursionError):
        _invalid()


class NativeOwnerLease:
    """One socketpair, one exact spawned native process, one irreversible lease.

    ``binding`` is a zero-argument callable returning fresh trusted-owner values,
    so interface/index can be discovered after the owned overlay has started.
    It must not derive expectations from incoming native frames. Its results are
    pinned on HELLO and must remain identical on each renewal. The production
    Public factory remains closed until it can supply real admission evidence.
    """
    def __init__(self, binding, *, lease_ms=2000, renew_seconds=0.5, hello_timeout=30.0):
        if (not callable(binding) or type(lease_ms) is not int or not 100 <= lease_ms <= 5000
                or type(renew_seconds) not in (int, float)
                or not 0.01 <= renew_seconds <= min(1.0, lease_ms / 2000)
                or type(hello_timeout) not in (int, float) or not 0.1 <= hello_timeout <= 60):
            _invalid()
        self._binding_provider = binding
        self._lease_ms, self._renew_seconds = lease_ms, renew_seconds
        # Native verifies real package bytes before HELLO. Its own three-second
        # ADMIT deadline begins at HELLO, not at process creation.
        self._hello_timeout = hello_timeout
        self._socket = self.child_socket = None
        self._child = self._binding = self._nonce = None
        self._started = self._admitted = self._expires = self._renewed = None
        self._sequence = 0
        self._ready = self._revoked = False
        try:
            self._socket, self.child_socket = socket.socketpair(socket.AF_UNIX, socket.SOCK_SEQPACKET)
            for endpoint in (self._socket, self.child_socket):
                endpoint.setsockopt(socket.SOL_SOCKET, socket.SO_PASSCRED, 1)
                endpoint.set_inheritable(False)
            self._socket.setblocking(False)
        except (OSError, AttributeError):
            self.revoke()
            _invalid()

    def arguments(self, config_path):
        """Nonsecret CLI arguments; configuration content/keys are never argv."""
        if (self._revoked or self.child_socket is None or self.child_socket.fileno() < 3
                or type(config_path) is not str or not config_path.startswith("/")
                or "\x00" in config_path or len(config_path) > 4096):
            _invalid()
        return ("--deployment-profile", "public", "--public-deployment-config", config_path,
                "--owned-public-lease-fd", str(self.child_socket.fileno()))

    def revoke(self):
        self._revoked, self._ready = True, False
        for endpoint in (self._socket, self.child_socket):
            if endpoint is not None:
                endpoint.close()

    def _receive(self, child):
        try:
            data, ancillary, flags, _ = self._socket.recvmsg(
                MAX_FRAME, socket.CMSG_SPACE(12) + socket.CMSG_SPACE(256), socket.MSG_CMSG_CLOEXEC)
        except BlockingIOError:
            return None
        except OSError:
            _invalid()
        credentials = []
        invalid = not data or bool(flags & (socket.MSG_TRUNC | socket.MSG_CTRUNC))
        for level, kind, payload in ancillary:
            if level == socket.SOL_SOCKET and kind == socket.SCM_RIGHTS:
                # The kernel installed these descriptors even for a rejected
                # frame. Close every delivered FD before raising the fixed error.
                rights = array.array("i")
                rights.frombytes(payload[:len(payload) - len(payload) % rights.itemsize])
                for descriptor in rights:
                    os.close(descriptor)
                invalid = True
            elif level == socket.SOL_SOCKET and kind == socket.SCM_CREDENTIALS and len(payload) == 12:
                credentials.append(struct.unpack("3i", payload))
            else:
                invalid = True
        if invalid or credentials != [(child.pid, os.geteuid(), os.getegid())]:
            _invalid()
        return _frame(data)

    def _send(self, kind, sequence):
        frame = {"version": VERSION, "kind": kind, "nonce": self._nonce, "sequence": sequence,
                 "binding": asdict(self._binding), "lease_ms": self._lease_ms}
        data = json.dumps(frame, separators=(",", ":"), ensure_ascii=True).encode("ascii")
        if len(data) > MAX_FRAME:
            _invalid()
        try:
            if self._socket.send(data, socket.MSG_DONTWAIT | socket.MSG_NOSIGNAL) != len(data):
                _invalid()
        except OSError:
            _invalid()
        self._sequence, self._renewed = sequence, time.monotonic()
        self._expires = self._renewed + self._lease_ms / 1000

    async def observe(self, child):
        # No await: the freshly observed owner binding, child identity and send
        # form one event-loop step. No background heartbeat outlives this call.
        try:
            return self._observe(child)
        except BaseException:
            self.revoke()
            raise LifecycleError("PUBLIC_OWNER_LEASE_INVALID") from None

    def _observe(self, child):
        if self._revoked or not isinstance(child, OwnedChild) or not child.image_matches():
            _invalid()
        now = time.monotonic()
        if self._started is None:
            self._started, self._child = now, child
            # Only the spawned process retains its endpoint after this point.
            self.child_socket.close()
        if (self._child is not child or self._expires is not None and now >= self._expires
                or self._nonce is None and now - self._started >= self._hello_timeout
                or self._admitted is not None and not self._ready and now - self._admitted >= 10):
            _invalid()
        binding = self._binding_provider()
        if not isinstance(binding, LeaseBinding) or self._binding is not None and binding != self._binding:
            _invalid()
        # Revalidate even a dataclass forged/mutated by a caller; formats alone
        # remain only consistency checks, never independent runtime admission.
        LeaseBinding(**asdict(binding))
        if (not child.image_matches() or self._expires is not None and time.monotonic() >= self._expires
                or self._nonce is None and time.monotonic() - self._started >= self._hello_timeout):
            _invalid()
        for _ in range(3):
            received = self._receive(child)
            if received is None:
                break
            frame, actual = received
            if actual != binding:
                _invalid()
            if self._nonce is None:
                if frame["kind"] != "hello" or frame["sequence"] != 0:
                    _invalid()
                self._binding, self._nonce = binding, frame["nonce"]
                self._admitted = time.monotonic()
                self._send("admit", 1)
            else:
                if (self._ready or frame["kind"] != "ready" or frame["sequence"] != 1
                        or frame["nonce"] != self._nonce):
                    _invalid()
                self._ready = True
        else:
            _invalid()  # finite receive work; no peer-driven event-loop starvation
        now = time.monotonic()
        if self._expires is not None:
            # Reject stale queued results before issuing a renewal.
            if now >= self._expires or not child.image_matches():
                _invalid()
            if now - self._renewed >= self._renew_seconds:
                if self._sequence == 2 ** 64 - 1:
                    _invalid()
                self._send("renew", self._sequence + 1)
        return self._ready
