"""Internal single-namespace development worker; never a Public admission API."""
from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path
import select
import selectors
import signal
import socket
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src/public"))
sys.path.insert(0, str(ROOT / "scripts"))
from fifth_freedom_control.easytier_profile import (ProfileError, core_arguments,
    parse_peer_observation, parse_profile, peer_query_arguments)
from fifth_freedom_control.owned_process import LifecycleError, OwnedChild, VerifiedExecutable, _BOOTSTRAP
from fifth_freedom_control.overlay_supervisor import KernelTunMonitor
from fifth_freedom_control.secure_config import read_private
from ci_socket_isolation import observe_echo, parse_ready, PORT_FIELDS

CLEAN_ENV = {"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8", "TZ": "UTC"}


class ProbeError(RuntimeError):
    pass


def fail(code):
    raise ProbeError(code) from None


class VerifiedPipeChild:
    """Execute the retained verified ELF fd; capture bounded synthetic output.

    This development helper does not change OwnedChild's DEVNULL contract. No
    executable pathname is reopened, and a child is pinned before poll/wait.
    """
    def __init__(self, program, arguments, *, writable=False):
        self.process = None
        self.pidfd = -1
        self.program = program
        if (not isinstance(program, VerifiedExecutable) or not isinstance(arguments, tuple)
                or len(arguments) > 32 or any(type(arg) is not str or "\x00" in arg or len(arg) > 4096 for arg in arguments)):
            fail("PIPE_CHILD_SPEC_INVALID")
        program.check_unchanged()
        ready_read = ready_write = release_read = release_write = -1
        try:
            ready_read, ready_write = os.pipe2(os.O_CLOEXEC)
            release_read, release_write = os.pipe2(os.O_CLOEXEC)
            # Reuse the production verified-fd hash/exec + parent-death barrier;
            # only this development wrapper gives the target bounded pipes.
            self.process = subprocess.Popen([sys.executable, "-I", "-S", "-c", _BOOTSTRAP,
                str(os.getpid()), str(program.fd), str(ready_write), str(release_read),
                str(program.identity.size), program.identity.sha256, str(program.path), *arguments],
                pass_fds=(program.fd, ready_write, release_read), close_fds=True,
                stdin=subprocess.PIPE if writable else subprocess.DEVNULL,
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=CLEAN_ENV, bufsize=0)
            self.pidfd = os.pidfd_open(self.process.pid)
            os.close(ready_write); ready_write = -1
            os.close(release_read); release_read = -1
            if not select.select([ready_read], [], [], 3)[0] or os.read(ready_read, 1) != b"A":
                fail("PIPE_BOOTSTRAP_FAILED")
            os.write(release_write, b"X")
            if not select.select([ready_read], [], [], 3)[0] or os.read(ready_read, 1) != b"":
                fail("PIPE_EXEC_FAILED")
            program.check_unchanged()
        except BaseException:
            if release_write >= 0:
                os.close(release_write); release_write = -1
            self.close()
            fail("PIPE_CHILD_START_FAILED")
        finally:
            for descriptor in (ready_read, ready_write, release_read, release_write):
                if descriptor >= 0:
                    os.close(descriptor)

    @property
    def alive(self):
        return self.pidfd >= 0 and not select.select([self.pidfd], [], [], 0)[0]

    def read(self, *, limit, timeout, line=False):
        if not 1 <= limit <= 65536 or not 0 < timeout <= 10:
            fail("PIPE_READ_LIMIT_INVALID")
        deadline, data = time.monotonic() + timeout, bytearray()
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(self.process.stdout, selectors.EVENT_READ)
                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0 or not selector.select(remaining):
                        fail("PIPE_READ_TIMEOUT")
                    block = os.read(self.process.stdout.fileno(), min(8192, limit + 1 - len(data)))
                    if not block:
                        if line:
                            fail("PIPE_READY_EOF")
                        self.process.wait(timeout=max(0.001, deadline - time.monotonic()))
                        if self.process.returncode:
                            fail("PIPE_CHILD_FAILED")
                        self.program.check_unchanged()
                        return bytes(data)
                    data.extend(block)
                    if len(data) > limit:
                        fail("PIPE_OUTPUT_LIMIT")
                    if line and b"\n" in data:
                        if data.count(b"\n") != 1 or not data.endswith(b"\n"):
                            fail("PIPE_READY_INVALID")
                        if not self.alive:
                            fail("PIPE_CHILD_EXITED")
                        self.program.check_unchanged()
                        return bytes(data)
        except (OSError, subprocess.SubprocessError):
            fail("PIPE_READ_FAILED")

    def close(self, graceful=False):
        if self.process is None:
            return
        try:
            if graceful and self.alive and self.process.stdin is not None:
                try:
                    self.process.stdin.write(b"STOP\n")
                    self.process.stdin.flush()
                    self.process.wait(timeout=2)
                except (OSError, subprocess.SubprocessError):
                    pass
            if self.pidfd >= 0:
                for sig in (signal.SIGTERM, signal.SIGKILL):
                    if self.alive:
                        signal.pidfd_send_signal(self.pidfd, sig)
                    try:
                        self.process.wait(timeout=2)
                        break
                    except subprocess.TimeoutExpired:
                        continue
                else:
                    fail("PIPE_CHILD_CLEANUP_TIMEOUT")
            else:
                # The unreleased bootstrap receives EOF and exits, without
                # signalling a PID for which no ownership handle was obtained.
                self.process.wait(timeout=3)
        except ProcessLookupError:
            self.process.wait(timeout=2)
        finally:
            for stream in (self.process.stdin, self.process.stdout):
                if stream is not None:
                    stream.close()
            if self.pidfd >= 0:
                os.close(self.pidfd)
                self.pidfd = -1
            self.process = None


def bounded_query(program, arguments):
    child = VerifiedPipeChild(program, arguments)
    try:
        return child.read(limit=65536, timeout=2)
    finally:
        child.close()


def _reply(value):
    data = json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n"
    if len(data) > 4096:
        fail("WORKER_REPLY_LIMIT")
    sys.stdout.write(data)
    sys.stdout.flush()


async def cleanup_children(core, probe, handles):
    failed = False
    if probe is not None:
        try:
            probe.close(graceful=True)
        except BaseException:
            failed = True
    if core is not None:
        try:
            await core.stop()
        except BaseException:
            failed = True
    for handle in reversed(handles):
        try:
            handle.close()
        except BaseException:
            failed = True
    if failed:
        fail("WORKER_CHILD_CLEANUP_FAILED")


async def serve(manifest):
    profile = parse_profile(json.dumps(manifest["profile"]).encode())
    handles, core, probe = [], None, None
    try:
        for role in ("core", "cli", "probe"):
            image = manifest[role]
            handles.append(VerifiedExecutable(Path(image["path"]), image["sha256"], image["bytes"]))
        core_image, cli_image, probe_image = handles
        if (core_image.identity.sha256 != profile.candidate.executable_sha256
                or core_image.identity.size != profile.candidate.executable_size):
            fail("WORKER_CORE_PROFILE_MISMATCH")
        monitor = KernelTunMonitor(profile.interface_name, str(profile.local.overlay.ip))
        await monitor.prepare()
        # No inherited ET_* options; core output includes cfg.dump and stays null.
        core = await OwnedChild.spawn(core_image, core_arguments(profile, manifest["config"]))
        _reply({"event": "started", "profile_sha256": profile.sha256})
        while True:
            line = await asyncio.to_thread(sys.stdin.buffer.readline, 4097)
            if not line:
                fail("WORKER_PARENT_EOF")
            if len(line) > 4096 or not line.endswith(b"\n"):
                fail("WORKER_COMMAND_LIMIT")
            command = json.loads(line)
            action = command.get("action")
            if action == "stop":
                break
            if action == "stop-core":
                await core.stop()
                absent = False
                try:
                    socket.if_nametoindex(profile.interface_name)
                except OSError:
                    absent = True
                _reply({"event": "core-stopped", "core_reaped": core.closed, "tun_absent": absent})
                continue
            if not core.image_matches():
                fail("WORKER_CORE_EXITED")
            observation = await monitor.observe(core)
            if action == "observe":
                authenticated = False
                if observation is not None:
                    try:
                        raw = await asyncio.to_thread(bounded_query, cli_image, peer_query_arguments(profile))
                        parse_peer_observation(profile, raw)
                        authenticated = True
                    except (ProfileError, ProbeError):
                        pass
                if not core.image_matches():
                    fail("WORKER_CORE_EXITED")
                _reply({"event": "observation", "tun_owned": observation is not None,
                        "peer_authenticated": authenticated,
                        "interface_index": observation.interface_index if observation is not None else None,
                        "probe_alive": probe.alive if probe is not None else None})
            elif action == "start-probe":
                if observation is None or probe is not None or profile.role != "responder":
                    fail("WORKER_PROBE_STATE_INVALID")
                probe = VerifiedPipeChild(probe_image, ("serve", "interface-bound",
                    str(profile.local.overlay.ip), profile.interface_name), writable=True)
                ready = parse_ready(probe.read(limit=4096, timeout=4, line=True), "interface-bound")
                _reply({"event": "probe-ready", **{key: ready[key] for key in PORT_FIELDS}})
            elif action == "echo":
                if observation is None:
                    fail("WORKER_TUN_NOT_READY")
                ports = command.get("ports")
                if (type(ports) is not dict or set(ports) != set(PORT_FIELDS)
                        or any(type(value) is not int or not 1 <= value <= 65535 for value in ports.values())):
                    fail("WORKER_PORTS_INVALID")
                outcomes = {}
                for key in PORT_FIELDS:
                    outcomes[key] = await asyncio.to_thread(observe_echo,
                        "tcp" if key == "tcp_port" else "udp", str(profile.remote.overlay.ip),
                        ports[key], (str(profile.remote.overlay.ip),))
                if not core.image_matches() or await monitor.observe(core) is None:
                    fail("WORKER_OWNERSHIP_LOST")
                _reply({"event": "echo", "outcomes": outcomes})
            else:
                fail("WORKER_COMMAND_INVALID")
        # Stop the synthetic native probe before the owned overlay. This is
        # development cleanup, not a claim about production native lease expiry.
        await cleanup_children(core, probe, handles)
        core = probe = None
        handles = []
        _reply({"event": "stopped", "children_reaped": True})
    finally:
        await cleanup_children(core, probe, handles)


def main(namespace_fd, manifest_path):
    # This executes before asyncio.to_thread or any spawned child. All threads
    # and exact core/CLI children inherit this one owned namespace afterwards.
    # The owner must acquire its worker pidfd before releasing any namespace
    # work or core startup. An acquisition failure closes stdin and starts none.
    if sys.stdin.buffer.readline(7) != b"START\n":
        fail("WORKER_NOT_RELEASED")
    manifest = json.loads(read_private(manifest_path, 32768))
    expected = manifest["namespace"]
    info = os.fstat(namespace_fd)
    if [info.st_dev, info.st_ino] != expected:
        fail("WORKER_NAMESPACE_IDENTITY_MISMATCH")
    os.setns(namespace_fd, 0)
    actual = os.stat("/proc/self/ns/net")
    if [actual.st_dev, actual.st_ino] != expected:
        fail("WORKER_NAMESPACE_ENTRY_FAILED")
    os.close(namespace_fd)
    os.chdir(Path(manifest_path).parent)
    asyncio.run(serve(manifest))


if __name__ == "__main__":
    try:
        if len(sys.argv) != 3:
            fail("WORKER_ARGUMENTS_INVALID")
        main(int(sys.argv[1]), sys.argv[2])
    except BaseException:
        # Exception messages and runtime/config output never enter public logs.
        sys.stderr.write("EASYTIER_DEVELOPMENT_WORKER_FAILED\n")
        raise SystemExit(1)
