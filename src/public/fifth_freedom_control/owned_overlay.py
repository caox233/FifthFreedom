"""Owned Linux observations for the finite development profile, not admission.

The caller owns the namespace, egress rules and private emitted configuration.
This module verifies local process/TUN/RPC observations. It cannot establish a
production deployment policy or replace the patched runtime's crypto contracts.
The caller must pin core AND CLI to one verified build receipt: a caller-chosen
CLI digest alone cannot prove that its well-shaped JSON came from that runtime.
Peer authentication fields describe handshake history. Recent local polling is
not evidence of recently authenticated peer traffic; this module has no such
receive observation and cannot authorize production lease renewal.
"""
import asyncio
from dataclasses import dataclass
import os
import re
import time

from .easytier_profile import (MAX_PEERS, ProfileError, core_arguments,
                              parse_peer_observation, parse_profile, peer_query_arguments)
from .native_lease import LeaseBinding
from .owned_process import LifecycleError, OwnedChild, OwnedQueryChild, VerifiedExecutable
from .overlay_supervisor import ChildSpec, KernelTunMonitor


@dataclass(frozen=True)
class QueryLimits:
    timeout: float = 1.5
    maximum_bytes: int = MAX_PEERS

    def __post_init__(self):
        if (type(self.timeout) not in (int, float) or not 0.1 <= self.timeout <= 2
                or type(self.maximum_bytes) is not int or not 1 <= self.maximum_bytes <= MAX_PEERS):
            raise LifecycleError("QUERY_BUDGET_INVALID")


async def _read_output(child, maximum):
    """Event-loop pipe reads; no background read thread can outlive a timeout."""
    loop, data = asyncio.get_running_loop(), bytearray()
    fd = child.output_fd
    os.set_blocking(fd, False)
    while True:
        if child.alive and not child.image_matches():
            raise LifecycleError("QUERY_IMAGE_CHANGED")
        future = loop.create_future()

        def readable():
            if future.done():
                return
            try:
                future.set_result(os.read(fd, min(8192, maximum + 1 - len(data))))
            except BlockingIOError:
                pass
            except OSError:
                future.set_exception(LifecycleError("QUERY_OUTPUT_UNAVAILABLE"))

        loop.add_reader(fd, readable)
        try:
            block = await future
        finally:
            loop.remove_reader(fd)
        if not block:
            if await child.wait() != 0:
                raise LifecycleError("QUERY_EXIT_FAILED")
            return bytes(data)
        data.extend(block)
        if len(data) > maximum:
            raise LifecycleError("QUERY_OUTPUT_LIMIT")


class OwnedCliQuery:
    """One bounded, verified-fd CLI execution at a time; raw bytes stay private."""
    def __init__(self, program, limits=QueryLimits()):
        if not isinstance(program, VerifiedExecutable) or type(limits) is not QueryLimits:
            raise LifecycleError("QUERY_SPEC_INVALID")
        self.program, self.limits = program, limits
        self._child = self._task = None
        self._closed = False

    async def run(self, arguments, *, on_failure):
        if self._closed or self._task is not None or self._child is not None or not callable(on_failure):
            raise LifecycleError("QUERY_STATE_INVALID")
        self._task = asyncio.current_task()
        def retain(child):
            # Bootstrap may fail before spawn returns. Retain exact ownership
            # now so even failed bootstrap cleanup has a retryable handle.
            self._child = child
        try:
            try:
                async with asyncio.timeout(self.limits.timeout):
                    self.program.check_unchanged()
                    self._child = await OwnedQueryChild.spawn(self.program, arguments,
                        startup_timeout=self.limits.timeout, on_failure=on_failure, on_owned=retain)
                    data = await _read_output(self._child, self.limits.maximum_bytes)
                    self.program.check_unchanged()
                    if self._closed:
                        raise LifecycleError("QUERY_CLOSED")
                    return data
            except BaseException as error:
                # Lease revocation precedes any awaited process cleanup. The
                # caller supplies NativeOwnerLease.revoke through its monitor.
                try:
                    on_failure()
                except BaseException:
                    pass
                if isinstance(error, asyncio.CancelledError):
                    raise
                raise LifecycleError("OWNED_QUERY_FAILED") from None
        finally:
            try:
                if self._child is not None:
                    cleanup = asyncio.create_task(self._child.stop(.1, 1))
                    try:
                        await asyncio.shield(cleanup)
                    except asyncio.CancelledError:
                        try:
                            on_failure()
                        except BaseException:
                            pass
                        await asyncio.shield(cleanup)
                        raise
                    self._child = None
            except BaseException:
                # A bounded cleanup failure retains its exact handle. A new
                # run must never overwrite it; close() can retry that handle.
                self._closed = True
                try:
                    on_failure()
                except BaseException:
                    pass
                raise
            finally:
                self._task = None

    async def close(self):
        self._closed = True
        task = self._task
        if task is not None and task is not asyncio.current_task():
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
        if self._child is not None:
            await asyncio.shield(self._child.stop(.1, 1))
            self._child = None


def _namespace(fd):
    info = os.fstat(fd)
    return info.st_dev, info.st_ino


def _current_namespace():
    info = os.stat("/proc/self/ns/net")
    return info.st_dev, info.st_ino


def _proc_bytes(child, name, maximum):
    descriptor = os.open(name, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=child.procfd)
    try:
        result = bytearray()
        while len(result) <= maximum:
            block = os.read(descriptor, min(8192, maximum + 1 - len(result)))
            if not block:
                return bytes(result)
            result.extend(block)
        raise LifecycleError("RPC_KERNEL_TABLE_LIMIT")
    finally:
        os.close(descriptor)


def owned_rpc_listener(child, port):
    """Exact pinned core fd owns the sole loopback TCP listener for this port."""
    if (not isinstance(child, OwnedChild) or not child.image_matches()
            or type(port) is not int or not 1024 <= port <= 65535):
        raise LifecycleError("RPC_OWNER_INVALID")
    folder = os.open("fd", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                     dir_fd=child.procfd)
    try:
        entries = os.listdir(folder)
        if len(entries) > 256:
            raise LifecycleError("RPC_OWNER_FD_LIMIT")
        inodes = set()
        for entry in entries:
            try:
                value = os.readlink(entry, dir_fd=folder)
            except FileNotFoundError:
                continue
            match = re.fullmatch(r"socket:\[([0-9]+)\]", value)
            if match:
                inodes.add(match[1])
    finally:
        os.close(folder)
    matched = []
    for table in ("tcp", "tcp6"):
        rows = _proc_bytes(child, "net/" + table, 256 * 1024).splitlines()[1:]
        if len(rows) > 2048:
            raise LifecycleError("RPC_KERNEL_TABLE_LIMIT")
        for row in rows:
            fields = row.split()
            if len(fields) < 10:
                raise LifecycleError("RPC_KERNEL_TABLE_INVALID")
            address, local_port = fields[1].rsplit(b":", 1)
            if fields[3] == b"0A" and int(local_port, 16) == port:
                matched.append((table, address, fields[9].decode("ascii")))
    if not child.image_matches():
        raise LifecycleError("RPC_OWNER_CHANGED")
    if not matched:
        return None
    if len(matched) != 1 or matched[0][0:2] != ("tcp", b"0100007F") or matched[0][2] not in inodes:
        raise LifecycleError("RPC_LISTENER_NOT_OWNED_LOOPBACK")
    return int(matched[0][2])


class OwnedOverlayMonitor:
    """Direct OwnedSupervisor monitor, permanently revoked after observation loss.

    Namespace handles and installed ELF handles are supplied by the trusted
    local owner. Matching namespace inode does not prove isolation/ownership of
    the whole namespace. Same-build CLI/core provenance, private configuration
    and current nft containment are independent caller prerequisites.
    No existing PID is adopted and no boolean can bypass a check.
    The finite recipe still has purpose owned-isolated-development. Production
    create_public_supervisor remains closed and does not instantiate this class.
    Peer auth level/keys and is_closed=false do not establish authenticated-data
    freshness: the retained runtime permits unencrypted Ping/Pong. The local
    observation timestamp measures this query, not a remote cryptographic event.
    """
    def __init__(self, recipe, core, cli, namespace_fd, *, revoke, limits=QueryLimits()):
        self._namespace_fd = -1
        self._prepared = self._revoked = self._observing = False
        self._child = self._accepted = self._peer = self._rpc = None
        self._observed_at = None
        self._generation = 0
        if (not isinstance(core, VerifiedExecutable) or not isinstance(cli, VerifiedExecutable)
                or type(namespace_fd) is not int or namespace_fd < 0 or not callable(revoke)):
            raise LifecycleError("OVERLAY_MONITOR_SPEC_INVALID")
        self.profile = parse_profile(recipe)
        core.check_unchanged()
        cli.check_unchanged()
        if (core.identity.sha256 != self.profile.candidate.executable_sha256
                or core.identity.size != self.profile.candidate.executable_size):
            raise LifecycleError("OVERLAY_CORE_PROFILE_MISMATCH")
        self.core, self.cli, self._revoke_native = core, cli, revoke
        self._query = OwnedCliQuery(cli, limits)
        self._kernel = KernelTunMonitor(self.profile.interface_name, str(self.profile.local.overlay.ip))
        try:
            self._namespace_fd = os.dup(namespace_fd)
            self._namespace = _namespace(self._namespace_fd)
            if self._namespace != _current_namespace():
                raise LifecycleError("OVERLAY_NAMESPACE_CHANGED")
        except BaseException:
            if self._namespace_fd >= 0:
                os.close(self._namespace_fd)
                self._namespace_fd = -1
            raise LifecycleError("OVERLAY_NAMESPACE_CHANGED") from None

    def core_spec(self, private_config_path):
        """Use with OwnedSupervisor after the owner emits the reviewed TOML."""
        if self._revoked:
            raise LifecycleError("OVERLAY_MONITOR_REVOKED")
        return ChildSpec(self.core, core_arguments(self.profile, private_config_path))

    def revoke(self):
        if not self._revoked:
            self._revoked = True
            self._generation += 1
            self._accepted = None
            # Synchronous endpoint closure happens before all awaited cleanup.
            self._revoke_native()

    def _check(self, child=None):
        if self._revoked or self._namespace_fd < 0:
            raise LifecycleError("OVERLAY_MONITOR_REVOKED")
        if _namespace(self._namespace_fd) != self._namespace or _current_namespace() != self._namespace:
            raise LifecycleError("OVERLAY_NAMESPACE_CHANGED")
        self.core.check_unchanged()
        self.cli.check_unchanged()
        if child is not None:
            if (not isinstance(child, OwnedChild) or isinstance(child, OwnedQueryChild)
                    or child.image != self.core.identity or not child.image_matches()
                    or self._child is not None and self._child is not child):
                raise LifecycleError("OVERLAY_CORE_CHANGED")
            info = os.stat("ns/net", dir_fd=child.procfd)
            if (info.st_dev, info.st_ino) != self._namespace:
                raise LifecycleError("OVERLAY_NAMESPACE_CHANGED")

    async def prepare(self):
        try:
            if self._prepared:
                raise LifecycleError("OVERLAY_MONITOR_ALREADY_PREPARED")
            self._check()
            await self._kernel.prepare()
            self._check()
            self._prepared = True
        except BaseException:
            self.revoke()
            raise LifecycleError("OVERLAY_PREPARATION_FAILED") from None

    async def observe(self, child):
        try:
            if not self._prepared or self._observing:
                raise LifecycleError("OVERLAY_MONITOR_STATE_INVALID")
            self._observing = True
            generation = self._generation
            self._check(child)
            self._child = child
            before = await self._kernel.observe(child)
            self._check(child)
            if before is None:
                if self._accepted is not None:
                    raise LifecycleError("OVERLAY_OWNERSHIP_LOST")
                return None
            rpc = owned_rpc_listener(child, self.profile.rpc_port)
            if rpc is None:
                if self._accepted is not None:
                    raise LifecycleError("OVERLAY_RPC_LOST")
                return None
            if self._rpc is not None and self._rpc != rpc:
                raise LifecycleError("OVERLAY_RPC_CHANGED")
            raw = await self._query.run(peer_query_arguments(self.profile), on_failure=self.revoke)
            self._check(child)
            after = await self._kernel.observe(child)
            self._check(child)
            if (generation != self._generation or after != before
                    or owned_rpc_listener(child, self.profile.rpc_port) != rpc):
                raise LifecycleError("OVERLAY_OBSERVATION_CHANGED")
            try:
                peer = parse_peer_observation(self.profile, raw)
            except ProfileError:
                # Before first readiness a missing/unapproved peer is pending,
                # bounded by the supervisor's startup deadline. Once accepted,
                # it is permanent loss; no later good reply restores the lease.
                if self._accepted is None:
                    return None
                raise LifecycleError("OVERLAY_PEER_LOST") from None
            if self._peer is not None and self._peer != peer:
                raise LifecycleError("OVERLAY_PEER_CHANGED")
            if self._accepted is not None and self._accepted != after:
                raise LifecycleError("OVERLAY_OWNERSHIP_LOST")
            self._accepted, self._peer, self._rpc = after, peer, rpc
            # Local snapshot age only; peer fields report handshake history,
            # not a current-session authenticated receive event.
            self._observed_at = time.monotonic()
            return after
        except BaseException as error:
            self.revoke()
            if isinstance(error, asyncio.CancelledError):
                raise
            raise LifecycleError("OWNED_OVERLAY_OBSERVATION_FAILED") from None
        finally:
            self._observing = False

    def lease_binding(self, *, config_sha256, service_manifest_sha256, admitted_client_storage_key):
        """Development binding from a recent local snapshot and native identity.

        Intended as NativeOwnerLease's binding callback, immediately following
        this monitor in OwnedSupervisor. It starts no independent heartbeat.
        The 0.5s bound is local query age, not authenticated-traffic freshness.
        Repeated unchanged peer JSON can satisfy it; production admission must
        remain closed until a separate reviewed current-session proof exists.
        """
        try:
            self._check(self._child)
            if (self._accepted is None or self._observed_at is None
                    or time.monotonic() - self._observed_at > .5
                    or owned_rpc_listener(self._child, self.profile.rpc_port) != self._rpc):
                raise LifecycleError("OVERLAY_OBSERVATION_STALE")
            observation = self._accepted
            return LeaseBinding(config_sha256, service_manifest_sha256, admitted_client_storage_key,
                self.profile.sha256, self.core.identity.sha256, observation.namespace_device,
                observation.namespace_inode, observation.interface_index, observation.address,
                self.profile.local.overlay.network.prefixlen)
        except BaseException:
            self.revoke()
            raise LifecycleError("OVERLAY_LEASE_BINDING_INVALID") from None

    async def close(self):
        try:
            self.revoke()
        finally:
            try:
                await self._query.close()
            finally:
                if self._namespace_fd >= 0:
                    os.close(self._namespace_fd)
                    self._namespace_fd = -1
