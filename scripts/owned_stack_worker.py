"""Internal isolated TCP development composition; never Public admission."""
from __future__ import annotations

import asyncio
import contextlib
import json
import os
from pathlib import Path
import socket
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
sys.path.insert(0, str(ROOT / "src/public"))
import ci_native_lifecycle as native
from ci_easytier_probe import encoded, json_bytes, sha
from easytier_probe_worker import ProbeError, WorkerFailure, failure_record, fail
from fifth_freedom_control.easytier_profile import parse_profile
from fifth_freedom_control.native_lease import NativeOwnerLease
from fifth_freedom_control.owned_overlay import OwnedOverlayMonitor
from fifth_freedom_control.owned_process import LifecycleError, OwnedChild, VerifiedExecutable
from fifth_freedom_control.overlay_supervisor import (ChildSpec, KernelTunMonitor,
    LifecycleLimits, OwnedSupervisor, State)
from fifth_freedom_control.secure_config import read_private


def reply(event, **values):
    print(json.dumps(dict(event=event, **values), separators=(",", ":")), flush=True)


async def input_line(descriptor, limit=4096):
    """No blocking executor thread remains alive after worker cancellation."""
    loop = asyncio.get_running_loop()
    result = loop.create_future()
    data = bytearray()
    def readable():
        try:
            block = os.read(descriptor, 1)
            if not block:
                if not result.done(): result.set_result(None)
            elif block == b"\n":
                if not result.done(): result.set_result(json_bytes(bytes(data), limit))
            else:
                data.extend(block)
                if len(data) > limit: fail("STACK_COMMAND_LIMIT")
        except BaseException as error:
            if not result.done(): result.set_exception(error)
    loop.add_reader(descriptor, readable)
    try:
        return await result
    finally:
        loop.remove_reader(descriptor)


def listeners_absent(address):
    encoded_address = f"{int.from_bytes(socket.inet_aton(address), 'little'):08X}"
    for protocol, ports in native.PORTS.items():
        rows = Path("/proc/net/" + protocol).read_bytes().splitlines()[1:]
        if len(rows) > 256: fail("STACK_SOCKET_TABLE_LIMIT")
        for row in rows:
            fields = row.split()
            if len(fields) < 10: fail("STACK_SOCKET_TABLE_INVALID")
            for port in ports:
                if fields[1] == f"{encoded_address}:{port:04X}".encode() and fields[3] == (b"0A" if protocol == "tcp" else b"07"):
                    fail("STACK_NATIVE_LISTENER_REMAINS")


def rebind_all(address):
    # Reuse the existing actual five-port check, suppress its internal protocol
    # line. No raw runtime data is captured or forwarded.
    import io
    with contextlib.redirect_stdout(io.StringIO()) as output:
        native.closed_ports(address)
    if json.loads(output.getvalue()) != {"event": "PORTS_CLOSED", "count": 5}:
        fail("STACK_NATIVE_PORT_CHECK_FAILED")


async def held_closed(stream, timeout):
    """Only actual EOF/reset is success; timeout and pending data are failures."""
    if not 0 < timeout <= 1.5: fail("STACK_CLOSE_BUDGET_INVALID")
    deadline, received = time.monotonic() + timeout, 0
    loop = asyncio.get_running_loop()
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0: fail("STACK_HELD_CONNECTION_TIMEOUT")
        try:
            block = await asyncio.wait_for(loop.sock_recv(stream, 4096), remaining)
        except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
            return
        except TimeoutError:
            fail("STACK_HELD_CONNECTION_TIMEOUT")
        if not block: return
        received += len(block)
        if received > 16384: fail("STACK_HELD_CONNECTION_LIMIT")


class PauseAfterObservation:
    """Case A fault barrier only: never returns old proof or renews a lease.

    The owner gets an ACK only after a successful real observation and before it
    returns to OwnedSupervisor. The one deadline includes revoke, native exit,
    remote EOF and release. Timeout follows the original supervisor fail-close.
    """
    def __init__(self, monitor, hold_seconds=1.8):
        if not 0 < hold_seconds <= 1.8: fail("STACK_PAUSE_BUDGET_INVALID")
        self.monitor, self.hold_seconds = monitor, hold_seconds
        self.requested = False
        self.entered, self.release = asyncio.Event(), asyncio.Event()
        self.deadline = None
        profile = monitor.profile
        self.kernel = KernelTunMonitor(profile.interface_name, str(profile.local.overlay.ip))
        self.observation = None

    async def prepare(self):
        await self.kernel.prepare()
        await self.monitor.prepare()

    async def observe(self, child):
        started = time.monotonic()
        observed = await self.monitor.observe(child)
        if self.requested and observed is not None:
            self.observation = observed
            self.deadline = started + self.hold_seconds
            self.remaining()
            self.entered.set()
            try:
                await asyncio.wait_for(self.release.wait(), self.remaining())
            except TimeoutError:
                fail("STACK_PAUSE_EXPIRED")
            fail("STACK_FAULT_CONTROL_COMPLETE")
        return observed

    def remaining(self):
        if self.deadline is None or time.monotonic() >= self.deadline:
            fail("STACK_PAUSE_EXPIRED")
        return self.deadline - time.monotonic()

    async def pause(self):
        if self.requested: fail("STACK_PAUSE_ALREADY_REQUESTED")
        self.requested = True
        await asyncio.wait_for(self.entered.wait(), 2)
        self.remaining()

    async def prove_overlay(self, child):
        self.remaining()
        if await self.kernel.observe(child) != self.observation:
            fail("STACK_PAUSED_OVERLAY_CHANGED")
        self.remaining()


async def cleanup_stack(*, held, owner, pause, supervisor, startup, core, monitor, handles):
    """Try each independently owned responsibility; never bypass server order.

    A failed server stop does not authorize stopping its overlay separately.
    Query/ns/image descriptors are independent and must still be closed. Failure
    is sticky, and the caller must not emit children_reaped on this path.
    """
    clean = True
    for close in ((held.close if held is not None else None),
                  (owner.revoke if owner is not None else None),
                  (pause.release.set if pause is not None else None)):
        if close is not None:
            try: close()
            except BaseException: clean = False
    if supervisor is not None:
        try: await supervisor.stop()
        except BaseException: clean = False
    if startup is not None:
        try:
            await asyncio.wait_for(asyncio.shield(startup), 6)
        except LifecycleError:
            # A startup failure is already propagated through status/start; it
            # does not mean a successful exact supervisor stop leaked a child.
            pass
        except BaseException:
            clean = False
            startup.cancel()
    if core is not None:
        try: await core.stop(.5, 2)
        except BaseException: clean = False
    if monitor is not None:
        try: await monitor.close()
        except BaseException: clean = False
    for image in reversed(handles):
        try: image.close()
        except BaseException: clean = False
    return clean


async def serve(document, namespace_fd, state):
    state["phase"] = "profile"
    profile = parse_profile(encoded(document["profile"]))
    if profile.transport != "tcp": fail("STACK_ONLY_TCP_DEVELOPMENT")
    handles, core, monitor, supervisor, startup, owner, held = [], None, None, None, None, None, None
    monitor_revocations = []
    stopped = False
    primary_failure = False
    pause = None
    try:
        state["phase"] = "images"
        images = {}
        for role in ("core", "cli") + (("native",) if profile.role == "responder" else ()):
            row = document[role]
            image = VerifiedExecutable(Path(row["path"]), row["sha256"], row["bytes"])
            handles.append(image); images[role] = image
        def revoke():
            # Evidence is recorded synchronously before any native cleanup await.
            monitor_revocations.append(bool(supervisor is not None and supervisor.native is not None))
            if owner is not None: owner.revoke()
        state["phase"] = "monitor-prepare"
        monitor = OwnedOverlayMonitor(encoded(document["profile"]), images["core"], images["cli"], namespace_fd, revoke=revoke)
        pause = None
        if profile.role == "responder":
            config = read_private(document["native_config"], 16384)
            identity = document["native_identity"]
            owner = NativeOwnerLease(lambda: monitor.lease_binding(config_sha256=sha(config),
                service_manifest_sha256=identity["service"]["manifest_sha256"],
                admitted_client_storage_key=identity["admitted_client"]["storage_key"]), hello_timeout=60)
            native_spec = ChildSpec(images["native"], owner.arguments(document["native_config"]), owner.child_socket)
            pause = PauseAfterObservation(monitor) if document["case"] == "lease-revoke" else None
            supervisor = OwnedSupervisor(monitor.core_spec(document["config"]), native_spec, pause or monitor,
                owner, LifecycleLimits(startup=60, observation=2, interval=.05, terminate=.5, kill=2))
            state["phase"] = "core-start"
            startup = asyncio.create_task(supervisor.start())
            await asyncio.sleep(0)
        else:
            await monitor.prepare()
            spec = monitor.core_spec(document["config"])
            state["phase"] = "core-start"
            core = await OwnedChild.spawn(spec.executable, spec.arguments, startup_timeout=10)
        reply("started", profile_sha256=profile.sha256)
        while True:
            state["phase"] = "parent-command"
            command = await input_line(sys.stdin.fileno())
            if command is None or command == {"action": "stop"}: break
            if type(command) is not dict or set(command) != {"action"}: fail("STACK_COMMAND_INVALID")
            action = command["action"]
            state["phase"] = "observe"
            if action == "observe" and core is not None:
                observed = await monitor.observe(core)
                reply("observation", authenticated=observed is not None)
            elif action == "status" and supervisor is not None:
                if startup.done() and startup.exception() is not None:
                    # The lifecycle engine may have failed before a spawned
                    # handle was returned. Do not infer a reap proof from EOF.
                    if supervisor.overlay is None or supervisor.native is None:
                        state["cleanup_code"] = "CHILD_START_CLEANUP_UNCONFIRMED"
                    raise startup.exception()
                if supervisor.ready:
                    native.listener_ownership(supervisor.native, str(profile.local.overlay.ip))
                    reply("ready", native_listeners=5)
                elif supervisor.state is State.STOPPED:
                    if not supervisor.native or not supervisor.native.closed or not supervisor.overlay.closed or not any(monitor_revocations):
                        fail("STACK_REVOKE_CLEANUP_INVALID")
                    listeners_absent(str(profile.local.overlay.ip))
                    reply("revoked", native_reaped=True, core_reaped=True, monitor_revoked=True, native_listeners=0)
                else:
                    reply("pending")
            elif action == "hold" and core is not None and held is None:
                if await monitor.observe(core) is None: fail("STACK_CLIENT_NOT_AUTHENTICATED")
                held = socket.socket(); held.setblocking(False)
                await asyncio.wait_for(asyncio.get_running_loop().sock_connect(held, (str(profile.remote.overlay.ip), 50051)), 2)
                await asyncio.wait_for(asyncio.get_running_loop().sock_sendall(held,
                    b"PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n" + b"\0\0\0\x04\0\0\0\0\0"), 1)
                if await monitor.observe(core) is None: fail("STACK_CLIENT_NOT_AUTHENTICATED")
                reply("connected", established_tcp=True)
            elif action == "closed" and core is not None and held is not None:
                await held_closed(held, 1.0)
                if not core.image_matches(): fail("STACK_CLIENT_CORE_LOST")
                reply("closed", actual_eof_or_reset=True, client_core_alive=True)
            elif action == "pause" and pause is not None and supervisor.ready:
                await pause.pause()
                reply("paused", fresh_observation=True)
            elif action == "revoke" and pause is not None and pause.entered.is_set():
                pause.remaining()
                owner.revoke()
                result = await asyncio.wait_for(supervisor.native.wait(), pause.remaining())
                if result != 92: fail("STACK_NATIVE_WATCHDOG_EXIT_INVALID")
                await pause.prove_overlay(supervisor.overlay)
                rebind_all(str(profile.local.overlay.ip))
                pause.remaining()
                reply("native-stopped", exit_code=92, rebound_ports=5, server_overlay_alive=True)
            elif action == "release" and pause is not None and pause.entered.is_set():
                await pause.prove_overlay(supervisor.overlay)
                pause.release.set()
                await asyncio.wait_for(supervisor.wait(), 4)
                reply("released", server_overlay_alive_at_release=True)
            else:
                fail("STACK_COMMAND_INVALID")
        stopped = True
    except BaseException:
        primary_failure = True
        if state["phase"] == "core-start" and supervisor is None and core is None:
            state["cleanup_code"] = "CHILD_START_CLEANUP_UNCONFIRMED"
        raise
    finally:
        clean = await cleanup_stack(held=held, owner=owner, pause=pause, supervisor=supervisor,
            startup=startup, core=core, monitor=monitor, handles=handles)
        if not clean:
            state["cleanup_code"] = "WORKER_CHILD_CLEANUP_FAILED"
            if not primary_failure:
                state["phase"] = "cleanup"
                fail("WORKER_CHILD_CLEANUP_FAILED")
    if stopped: reply("stopped", children_reaped=True)


def main(namespace_fd, manifest_path, state):
    state["phase"] = "release"
    if sys.stdin.buffer.readline(7) != b"START\n": fail("WORKER_NOT_RELEASED")
    state["phase"] = "manifest"
    document = json_bytes(read_private(manifest_path, 32768), 32768)
    state["phase"] = "namespace"
    expected = document["namespace"]
    info = os.fstat(namespace_fd)
    if [info.st_dev, info.st_ino] != expected: fail("WORKER_NAMESPACE_IDENTITY_MISMATCH")
    os.setns(namespace_fd, 0)
    actual = os.stat("/proc/self/ns/net")
    if [actual.st_dev, actual.st_ino] != expected: fail("WORKER_NAMESPACE_ENTRY_FAILED")
    os.chdir(Path(manifest_path).parent)
    try:
        asyncio.run(serve(document, namespace_fd, state))
    finally:
        os.close(namespace_fd)


if __name__ == "__main__":
    state = dict(phase="arguments", cleanup_code=None)
    try:
        if len(sys.argv) != 3: fail("WORKER_ARGUMENTS_INVALID")
        main(int(sys.argv[1]), sys.argv[2], state)
    except BaseException as error:
        # Reuse the already-reviewed strict whitelist and schema. Unknown
        # runtime failures stay unclassified; never echo a path or exception.
        try:
            record = error.record if isinstance(error, WorkerFailure) else failure_record(error, state)
            print(json.dumps(record, separators=(",", ":")), flush=True)
        except BaseException:
            pass
        sys.stderr.write("OWNED_STACK_WORKER_FAILED\n")
        raise SystemExit(1)
