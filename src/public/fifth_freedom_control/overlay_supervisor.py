"""Owned process lifetime, deliberately separate from Public admission.

The lifecycle engine can run synthetic children. Its READY state says only that
the supplied local observations remain valid. Production admission is CLOSED:
neither a candidate hash nor interface/TUN ownership proves encrypted peers,
native package admission, source policy or an egress-isolated namespace.
"""
import asyncio
from dataclasses import dataclass, field
import errno
from enum import Enum
import ipaddress
import os
import re
import socket
import stat
import struct
from typing import Protocol

from .owned_process import LifecycleError, OwnedChild, VerifiedExecutable


@dataclass(frozen=True)
class RuntimeCandidateIdentity:
    """Integrity/provenance inputs, never an authorization or secure profile."""
    upstream_commit: str
    source_manifest_sha256: str
    patch_manifest_sha256: str
    cargo_lock_sha256: str
    build_profile_sha256: str
    executable_sha256: str
    executable_size: int
    role: str = "easytier-core"
    target: str = "x86_64-unknown-linux-gnu"

    def __post_init__(self):
        hashes = (self.source_manifest_sha256, self.patch_manifest_sha256,
                  self.cargo_lock_sha256, self.build_profile_sha256, self.executable_sha256)
        if (not isinstance(self.upstream_commit, str) or re.fullmatch(r"[0-9a-f]{40}", self.upstream_commit) is None
                or any(not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None for value in hashes)
                or type(self.executable_size) is not int or not 64 <= self.executable_size <= 512 * 1024 * 1024
                or self.role != "easytier-core" or self.target != "x86_64-unknown-linux-gnu"):
            raise LifecycleError("RUNTIME_CANDIDATE_INVALID")


class PublicAdmissionProvider(Protocol):
    """Future provider must validate profile AND supply actual readiness proofs.

    This interface intentionally has no boolean 'encrypted' input and is not
    configurable through environment variables, a JSON request or CLI flags.
    """
    def admit(self, candidate: RuntimeCandidateIdentity): ...


class ClosedPublicAdmission:
    def admit(self, candidate):
        raise LifecycleError("PUBLIC_PROFILE_NOT_READY")


def create_public_supervisor(candidate: RuntimeCandidateIdentity):
    # Keep a concrete closed provider until the patched runtime, owned isolated
    # namespace, secure-session profile and native activation contract exist.
    return ClosedPublicAdmission().admit(candidate)


@dataclass(frozen=True)
class ChildSpec:
    executable: VerifiedExecutable = field(repr=False)
    arguments: tuple = field(repr=False)
    lease_socket: socket.socket | None = field(default=None, repr=False)


@dataclass(frozen=True)
class LifecycleLimits:
    startup: float = 10.0
    observation: float = 0.5
    interval: float = 0.1
    terminate: float = 1.0
    kill: float = 2.0

    def __post_init__(self):
        values = (self.startup, self.observation, self.interval, self.terminate, self.kill)
        if (any(type(value) not in (int, float) for value in values)
                or not 0.1 <= self.startup <= 60 or not 0.01 <= self.observation <= 2
                or not 0.01 <= self.interval <= 1 or not 0.01 <= self.terminate <= 10
                or not 0.01 <= self.kill <= 10):
            raise LifecycleError("LIFECYCLE_LIMITS_INVALID")


@dataclass(frozen=True)
class OverlayObservation:
    namespace_device: int
    namespace_inode: int
    interface_index: int
    address: str


class OverlayMonitor(Protocol):
    async def prepare(self): ...
    async def observe(self, child: OwnedChild) -> OverlayObservation | None: ...


class ServiceReadiness(Protocol):
    async def observe(self, child: OwnedChild) -> bool: ...


class State(Enum):
    NEW = "new"
    STARTING_OVERLAY = "starting_overlay"
    STARTING_NATIVE = "starting_native"
    READY = "ready"
    REVOKED = "revoked"
    STOPPED = "stopped"


class OwnedSupervisor:
    """Reusable lifetime engine; construction grants no Public authorization.

    Only exact spawned handles are signalled. A failed monitor or changed
    observation permanently revokes readiness. Cleanup awaits native death
    before stopping overlay; it never removes routes/interfaces by name.
    """
    def __init__(self, overlay: ChildSpec, native: ChildSpec, monitor: OverlayMonitor,
                 service: ServiceReadiness, limits=LifecycleLimits()):
        self.overlay_spec, self.native_spec = overlay, native
        self.monitor, self.service, self.limits = monitor, service, limits
        self.overlay = self.native = None
        self.state, self.reason = State.NEW, None
        self._runner = None
        self._startup_done, self._stop_requested = asyncio.Event(), asyncio.Event()
        self._observation = None
        self._cleanup_lock = asyncio.Lock()

    @property
    def ready(self):
        return self.state is State.READY

    def _revoke(self, reason):
        if self.reason is None:
            self.reason = reason
        self.state = State.REVOKED
        revoke = getattr(self.service, "revoke", None)
        if revoke is not None:
            # Close the private admission endpoint before any awaited cleanup.
            # The native watchdog must not depend on the Python cleanup task.
            try:
                revoke()
            except Exception:
                if self.reason is None:
                    self.reason = "NATIVE_LEASE_REVOCATION_FAILED"

    async def _observe(self):
        if not self.overlay.image_matches():
            raise LifecycleError("OVERLAY_EXITED")
        try:
            observed = await asyncio.wait_for(self.monitor.observe(self.overlay), self.limits.observation)
        except Exception:
            raise LifecycleError("OVERLAY_MONITOR_FAILED") from None
        if self._stop_requested.is_set():
            raise LifecycleError("STOP_REQUESTED")
        if not self.overlay.image_matches():
            raise LifecycleError("OVERLAY_EXITED")
        if observed is not None and not isinstance(observed, OverlayObservation):
            raise LifecycleError("OVERLAY_OBSERVATION_INVALID")
        if self._observation is not None and observed != self._observation:
            raise LifecycleError("OVERLAY_OWNERSHIP_LOST")
        return observed

    async def _pause(self):
        try:
            await asyncio.wait_for(self._stop_requested.wait(), self.limits.interval)
        except TimeoutError:
            pass
        if self._stop_requested.is_set():
            raise LifecycleError("STOP_REQUESTED")

    async def _cleanup(self):
        # A failed native stop deliberately prevents overlay cleanup. Retain the
        # exact handles for diagnosis/retry; never claim a bounded stop succeeded
        # for a kernel task stuck beyond SIGKILL (e.g. uninterruptible I/O).
        async with self._cleanup_lock:
            if self.native is not None:
                await self.native.stop(self.limits.terminate, self.limits.kill)
            if self.overlay is not None:
                await self.overlay.stop(self.limits.terminate, self.limits.kill)

    async def _run(self):
        deadline = asyncio.get_running_loop().time() + self.limits.startup
        try:
            self.state = State.STARTING_OVERLAY
            try:
                await asyncio.wait_for(self.monitor.prepare(), self.limits.observation)
            except Exception:
                raise LifecycleError("OVERLAY_PREPARATION_FAILED") from None
            if self._stop_requested.is_set():
                raise LifecycleError("STOP_REQUESTED")
            self.overlay = await OwnedChild.spawn(self.overlay_spec.executable, self.overlay_spec.arguments,
                                                 startup_timeout=min(30.0, self.limits.startup))
            while self._observation is None:
                self._observation = await self._observe()
                if asyncio.get_running_loop().time() >= deadline:
                    raise LifecycleError("STARTUP_TIMEOUT")
                if self._observation is None:
                    await self._pause()
            if self._stop_requested.is_set():
                raise LifecycleError("STOP_REQUESTED")
            self.state = State.STARTING_NATIVE
            try:
                self.native = await OwnedChild.spawn(self.native_spec.executable, self.native_spec.arguments,
                    startup_timeout=min(30.0, max(0.1, deadline - asyncio.get_running_loop().time())),
                    lease_socket=self.native_spec.lease_socket)
            finally:
                if self.native_spec.lease_socket is not None:
                    self.native_spec.lease_socket.close()
            while True:
                await self._observe()
                if not self.native.image_matches():
                    raise LifecycleError("NATIVE_EXITED")
                try:
                    live = await asyncio.wait_for(self.service.observe(self.native), self.limits.observation)
                except Exception:
                    raise LifecycleError("NATIVE_MONITOR_FAILED") from None
                # Awaited observations can become stale while another task
                # stops us or an owned child exits. Never publish their result
                # without rechecking exact handles after that await boundary.
                if self._stop_requested.is_set():
                    raise LifecycleError("STOP_REQUESTED")
                if not self.overlay.image_matches():
                    raise LifecycleError("OVERLAY_EXITED")
                if not self.native.image_matches():
                    raise LifecycleError("NATIVE_EXITED")
                if type(live) is not bool:
                    raise LifecycleError("NATIVE_OBSERVATION_INVALID")
                if self.state is State.STARTING_NATIVE:
                    if asyncio.get_running_loop().time() >= deadline:
                        raise LifecycleError("STARTUP_TIMEOUT")
                    if live:
                        self.state = State.READY
                        self._startup_done.set()
                elif not live:
                    raise LifecycleError("NATIVE_READINESS_LOST")
                await self._pause()
        except asyncio.CancelledError:
            self._revoke("SUPERVISOR_CANCELLED")
        except LifecycleError as error:
            self._revoke(str(error))
        except BaseException:
            # Monitor exceptions may include paths or secrets; never retain them.
            self._revoke("LIFECYCLE_OBSERVATION_FAILED")
        finally:
            if self.state is not State.REVOKED:
                self._revoke("SUPERVISOR_STOPPED")
            cleanup = asyncio.create_task(self._cleanup())
            try:
                await asyncio.shield(cleanup)
            except asyncio.CancelledError:
                # A second cancellation cannot orphan the already owned handles.
                await asyncio.shield(cleanup)
            finally:
                self._startup_done.set()
            self.state = State.STOPPED

    async def start(self):
        if self.state is not State.NEW or self._runner is not None:
            raise LifecycleError("SUPERVISOR_ALREADY_STARTED")
        # Reserve before the first await/scheduling point. Concurrent callers
        # must never overwrite an existing runner or its owned child handles.
        self.state = State.STARTING_OVERLAY
        self._runner = asyncio.create_task(self._run())
        try:
            await self._startup_done.wait()
        except asyncio.CancelledError:
            self._revoke("SUPERVISOR_CANCELLED")
            self._stop_requested.set()
            await asyncio.shield(self._runner)
            raise
        if not self.ready:
            await self.wait()
            raise LifecycleError(self.reason or "SUPERVISOR_START_FAILED")

    async def stop(self):
        if self.state is State.STOPPED:
            return
        if self._runner is None:
            self._revoke("STOP_REQUESTED")
            self.state = State.STOPPED
            return
        self._revoke("STOP_REQUESTED")
        self._stop_requested.set()
        if self._runner.done() and self._runner.exception() is not None:
            # A prior bounded stop failed. Retry only our retained exact handles,
            # in the same order, with readiness still permanently revoked.
            await asyncio.shield(self._cleanup())
            self.state = State.STOPPED
            return
        await self.wait()

    async def wait(self):
        if self._runner is not None:
            await asyncio.shield(self._runner)


_KERNEL_QUERY_CODES = {
    "namespace": "OVERLAY_NAMESPACE_OBSERVATION_UNAVAILABLE",
    "link_flags": "OVERLAY_LINK_QUERY_UNAVAILABLE",
    "ipv4_address": "OVERLAY_ADDRESS_QUERY_UNAVAILABLE",
    "fdinfo_directory": "OVERLAY_FDINFO_DIRECTORY_UNAVAILABLE",
    "fdinfo_list": "OVERLAY_FDINFO_LIST_UNAVAILABLE",
    "fdinfo_entry": "OVERLAY_FDINFO_ENTRY_UNAVAILABLE",
    "fdinfo_read": "OVERLAY_FDINFO_READ_UNAVAILABLE",
    "tun_descriptor": "OVERLAY_TUN_DESCRIPTOR_UNAVAILABLE",
    "interface_recheck": "OVERLAY_INTERFACE_RECHECK_UNAVAILABLE",
}
_KERNEL_ERRNO_NAMES = ("EADDRNOTAVAIL", "ENODEV", "ENXIO", "EACCES", "EPERM", "EIO",
                       "EINTR", "EINVAL", "EBADF", "ENOENT", "ENOBUFS", "ENOMEM")
_KERNEL_ERRNO_CLASSES = frozenset((*_KERNEL_ERRNO_NAMES, "OTHER"))


@dataclass(frozen=True, slots=True)
class _KernelQueryFailure:
    code: str
    query_stage: str
    errno_class: str
    previously_observed: bool
    link_up: bool | None


def valid_kernel_query_failure(value, code):
    """Strict nonsecret diagnostics, never an observation/admission proof."""
    return (type(value) is dict
        and set(value) == {"query_stage", "errno_class", "previously_observed", "link_up"}
        and type(value["query_stage"]) is str
        and type(code) is str and _KERNEL_QUERY_CODES.get(value["query_stage"]) == code
        and type(value["errno_class"]) is str and value["errno_class"] in _KERNEL_ERRNO_CLASSES
        and type(value["previously_observed"]) is bool
        and (value["link_up"] is None if value["query_stage"] in ("namespace", "link_flags")
             else type(value["link_up"]) is bool if value["query_stage"] == "ipv4_address"
             else value["link_up"] is True)
        and not (value["query_stage"] == "ipv4_address" and value["errno_class"] == "EADDRNOTAVAIL"
                 and value["previously_observed"] is False and value["link_up"] is True))


def kernel_query_failure(error):
    """Extract only metadata attached to this exact failed monitor invocation."""
    if type(error) is not LifecycleError or len(error.args) != 1 or type(error.args[0]) is not str:
        return None
    details = getattr(error, "_kernel_query_failure", None)
    if type(details) is not _KernelQueryFailure or details.code != error.args[0]:
        return None
    value = dict(query_stage=details.query_stage, errno_class=details.errno_class,
                 previously_observed=details.previously_observed, link_up=details.link_up)
    return value if valid_kernel_query_failure(value, details.code) else None


class KernelTunMonitor:
    """Local Linux ownership observations, NOT encryption or peer admission.

    Requires the owned overlay in our exact namespace. The caller must already
    own its isolated namespace and apply egress policy. That production adapter
    is not supplied here. The real TUN path requires a privileged integration
    test and is not claimed by synthetic monitor tests.
    """
    def __init__(self, interface, address):
        if (not isinstance(interface, str) or re.fullmatch(r"[A-Za-z0-9_-]{1,15}", interface) is None
                or interface == "lo"):
            raise LifecycleError("OVERLAY_INTERFACE_INVALID")
        try:
            parsed = ipaddress.IPv4Address(address)
            allowed = any(parsed in ipaddress.IPv4Network(network) for network in
                          ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10"))
            if str(parsed) != address or not allowed:
                raise ValueError()
        except (ValueError, TypeError):
            raise LifecycleError("OVERLAY_ADDRESS_INVALID") from None
        self.interface, self.address = interface, address
        self._namespace = None
        self._index = None

    async def prepare(self):
        if not hasattr(os, "pidfd_open"):
            raise LifecycleError("OWNED_PROCESS_UNSUPPORTED")
        try:
            socket.if_nametoindex(self.interface)
        except OSError:
            pass
        else:
            raise LifecycleError("OVERLAY_INTERFACE_ALREADY_EXISTS")
        try:
            info = os.stat("/proc/self/ns/net")
            self._namespace = (info.st_dev, info.st_ino)
        except OSError:
            raise LifecycleError("OVERLAY_NAMESPACE_UNAVAILABLE") from None

    async def observe(self, child):
        import fcntl  # Linux only; importing the package stays safe on Windows.
        previously_observed, link_up = self._index is not None, None
        if self._namespace is None or not child.image_matches():
            raise LifecycleError("OVERLAY_OWNERSHIP_LOST")
        # Keep syscall failures fail-closed, but identify the fixed observation
        # stage without publishing exception text, paths or kernel output.
        unavailable = "OVERLAY_NAMESPACE_OBSERVATION_UNAVAILABLE"
        try:
            own_ns = os.stat("/proc/self/ns/net")
            child_ns = os.stat("ns/net", dir_fd=child.procfd)
            if ((own_ns.st_dev, own_ns.st_ino) != self._namespace
                    or (child_ns.st_dev, child_ns.st_ino) != self._namespace):
                raise LifecycleError("OVERLAY_NAMESPACE_CHANGED")
            try:
                index = socket.if_nametoindex(self.interface)
            except OSError:
                if self._index is not None:
                    raise LifecycleError("OVERLAY_OWNERSHIP_LOST") from None
                return None
            if self._index is not None and index != self._index:
                raise LifecycleError("OVERLAY_OWNERSHIP_LOST")
            request = struct.pack("256s", self.interface.encode("ascii"))
            unavailable = "OVERLAY_LINK_QUERY_UNAVAILABLE"
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
                flags = struct.unpack_from("H", fcntl.ioctl(probe.fileno(), 0x8913, request), 16)[0]
                link_up = bool(flags & 1)
                unavailable = "OVERLAY_ADDRESS_QUERY_UNAVAILABLE"
                try:
                    address = socket.inet_ntoa(fcntl.ioctl(probe.fileno(), 0x8915, request)[20:24])
                except OSError as error:
                    # TUNSETIFF makes the interface visible before the runtime
                    # raises it and assigns IPv4; the initial missing-address
                    # window can therefore have either DOWN or UP link flags.
                    # Only that initial missing-address state may remain pending
                    # under the owner's startup deadline; it grants no ownership
                    # observation and commits no interface index or native lease.
                    # Other errors and all losses after a committed observation
                    # remain fatal, including the same DOWN/no-address state.
                    if self._index is None and error.errno == errno.EADDRNOTAVAIL:
                        return None
                    raise
            if not flags & 1 or address != self.address:
                raise LifecycleError("OVERLAY_ADDRESS_OR_LINK_LOST")
            unavailable = "OVERLAY_FDINFO_DIRECTORY_UNAVAILABLE"
            folder = os.open("fdinfo", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC, dir_fd=child.procfd)
            try:
                unavailable = "OVERLAY_FDINFO_LIST_UNAVAILABLE"
                descriptors = os.listdir(folder)
                if len(descriptors) > 256:
                    raise LifecycleError("OVERLAY_FD_BOUND_EXCEEDED")
                owned = False
                for entry in descriptors:
                    if not entry.isascii() or not entry.isdecimal():
                        continue
                    unavailable = "OVERLAY_FDINFO_ENTRY_UNAVAILABLE"
                    descriptor = os.open(entry, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=folder)
                    try:
                        unavailable = "OVERLAY_FDINFO_READ_UNAVAILABLE"
                        data = os.read(descriptor, 8193)
                    finally:
                        os.close(descriptor)
                    if len(data) > 8192:
                        raise LifecycleError("OVERLAY_FDINFO_BOUND_EXCEEDED")
                    if b"iff:\t" + self.interface.encode("ascii") in data.splitlines():
                        unavailable = "OVERLAY_TUN_DESCRIPTOR_UNAVAILABLE"
                        info = os.stat("fd/" + entry, dir_fd=child.procfd)
                        if stat.S_ISCHR(info.st_mode) and info.st_rdev == os.makedev(10, 200):
                            owned = True
                if not owned:
                    raise LifecycleError("OVERLAY_TUN_NOT_OWNED")
            finally:
                os.close(folder)
            unavailable = "OVERLAY_INTERFACE_RECHECK_UNAVAILABLE"
            if not child.image_matches() or socket.if_nametoindex(self.interface) != index:
                raise LifecycleError("OVERLAY_OWNERSHIP_LOST")
            self._index = index
            return OverlayObservation(*self._namespace, index, self.address)
        except OSError as error:
            # Preserve the original fixed failure and fail-closed behavior.
            # Never retain exception text, paths, addresses, numeric errno or
            # metadata from an earlier call, including an earlier success.
            errno_class = next((name for name in _KERNEL_ERRNO_NAMES
                if type(error.errno) is int and error.errno == getattr(errno, name, None)), "OTHER")
            stage = next(name for name, code in _KERNEL_QUERY_CODES.items() if code == unavailable)
            failure = LifecycleError(unavailable)
            failure._kernel_query_failure = _KernelQueryFailure(
                unavailable, stage, errno_class, previously_observed, link_up)
            raise failure from None
