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
from fifth_freedom_control.easytier_profile import (ProfileError, PeerObservationError,
    PEER_OBSERVATION_REASONS, core_arguments,
    parse_peer_observation, parse_profile, peer_query_arguments)
from fifth_freedom_control.owned_process import LifecycleError, OwnedChild, VerifiedExecutable, _BOOTSTRAP
from fifth_freedom_control.overlay_supervisor import KernelTunMonitor
from fifth_freedom_control.secure_config import AdmissionError, read_private
from ci_socket_isolation import observe_echo, parse_ready, PORT_FIELDS

CLEAN_ENV = {"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8", "TZ": "UTC"}
READINESS_SCHEMA = "ff-easytier-readiness-observation-v1"
QUERY_FAILURE_CODES = frozenset(("PIPE_READ_LIMIT_INVALID", "PIPE_READ_TIMEOUT", "PIPE_CHILD_FAILED",
    "PIPE_OUTPUT_LIMIT", "PIPE_CHILD_EXITED", "PIPE_READ_FAILED", "WORKER_UNCLASSIFIED_ERROR"))


def valid_readiness_record(value):
    if (type(value) is not dict or set(value) != {"schema", "core_image_matches", "tun_owned",
            "peer_authenticated", "query_state", "query_code", "local_bind_kind"}
            or value["schema"] != READINESS_SCHEMA or value["core_image_matches"] is not True
            or type(value["tun_owned"]) is not bool or type(value["peer_authenticated"]) is not bool
            or type(value["query_state"]) is not str):
        return False
    state, code, bind = value["query_state"], value["query_code"], value["local_bind_kind"]
    if not value["tun_owned"]:
        return not value["peer_authenticated"] and state == "tun-pending" and code is None and bind is None
    if value["peer_authenticated"]:
        return state == "authenticated" and code is None and type(bind) is str and bind in ("exact", "wildcard")
    return (bind is None and type(code) is str
        and (state == "query-failed" and code in QUERY_FAILURE_CODES
             or state == "parse-rejected" and code in PEER_OBSERVATION_REASONS))


class ProbeError(RuntimeError):
    pass


FAILURE_SCHEMA = "ff-easytier-worker-failure-v1"
FAILURE_PHASES = frozenset(("arguments", "release", "manifest", "namespace", "profile", "images",
    "monitor-prepare", "core-start", "parent-command", "observe", "peer-query", "probe-start", "echo", "core-stop", "cleanup"))
# Never derive public strings from arbitrary exception messages or errno text.
# Unknown/new implementation failures remain a fixed unclassified failure.
FAILURE_CODES = frozenset("""
WORKER_UNCLASSIFIED_ERROR WORKER_OS_ERROR WORKER_INPUT_ERROR WORKER_CANCELLED
WORKER_ARGUMENTS_INVALID WORKER_NOT_RELEASED WORKER_NAMESPACE_IDENTITY_MISMATCH
WORKER_NAMESPACE_ENTRY_FAILED WORKER_CORE_PROFILE_MISMATCH WORKER_PARENT_EOF
WORKER_COMMAND_LIMIT WORKER_COMMAND_INVALID WORKER_CORE_EXITED WORKER_PROBE_STATE_INVALID
WORKER_TUN_NOT_READY WORKER_PORTS_INVALID WORKER_OWNERSHIP_LOST WORKER_REPLY_LIMIT
WORKER_CHILD_CLEANUP_FAILED PROFILE_INVALID JSON_INVALID JSON_SIZE_INVALID
CANDIDATE_INVALID PRIVATE_FILE_INVALID PRIVATE_FILE_CHANGED PRIVATE_FILE_UNAVAILABLE
PRIVATE_FILE_TOO_LARGE PRIVATE_FILE_UNSUPPORTED OWNED_PROCESS_UNSUPPORTED
EXECUTABLE_PATH_INVALID EXECUTABLE_IDENTITY_INVALID EXECUTABLE_FILE_INVALID
EXECUTABLE_CAPABILITIES_UNVERIFIED EXECUTABLE_CAPABILITIES_FORBIDDEN EXECUTABLE_ELF_INVALID
EXECUTABLE_CHANGED EXECUTABLE_HASH_MISMATCH EXECUTABLE_UNAVAILABLE
CHILD_SPEC_INVALID CHILD_BOOTSTRAP_FAILED CHILD_EXITED_DURING_START CHILD_EXEC_FAILED
CHILD_BOOTSTRAP_CLEANUP_FAILED CHILD_START_FAILED CHILD_HANDSHAKE_FAILED
CHILD_STOP_BUDGET_INVALID CHILD_SIGNAL_FAILED CHILD_STOP_TIMEOUT
OVERLAY_INTERFACE_INVALID OVERLAY_ADDRESS_INVALID OVERLAY_INTERFACE_ALREADY_EXISTS
OVERLAY_NAMESPACE_UNAVAILABLE OVERLAY_OWNERSHIP_LOST OVERLAY_NAMESPACE_CHANGED
OVERLAY_ADDRESS_OR_LINK_LOST OVERLAY_FD_BOUND_EXCEEDED OVERLAY_FDINFO_BOUND_EXCEEDED
OVERLAY_TUN_NOT_OWNED OVERLAY_OBSERVATION_UNAVAILABLE
OVERLAY_NAMESPACE_OBSERVATION_UNAVAILABLE OVERLAY_LINK_QUERY_UNAVAILABLE
OVERLAY_ADDRESS_QUERY_UNAVAILABLE OVERLAY_FDINFO_DIRECTORY_UNAVAILABLE
OVERLAY_FDINFO_LIST_UNAVAILABLE OVERLAY_FDINFO_ENTRY_UNAVAILABLE
OVERLAY_FDINFO_READ_UNAVAILABLE OVERLAY_TUN_DESCRIPTOR_UNAVAILABLE
OVERLAY_INTERFACE_RECHECK_UNAVAILABLE
PIPE_CHILD_SPEC_INVALID PIPE_BOOTSTRAP_FAILED PIPE_EXEC_FAILED PIPE_CHILD_START_FAILED
PIPE_READ_LIMIT_INVALID PIPE_READ_TIMEOUT PIPE_READY_EOF PIPE_CHILD_FAILED PIPE_OUTPUT_LIMIT
PIPE_READY_INVALID PIPE_CHILD_EXITED PIPE_READ_FAILED PIPE_CHILD_CLEANUP_TIMEOUT
""".split())
CLEANUP_CODES = frozenset(("WORKER_CHILD_CLEANUP_FAILED", "CHILD_START_CLEANUP_UNCONFIRMED", "CLI_CHILD_CLEANUP_UNCONFIRMED"))


class QueryCleanupUnconfirmed(RuntimeError):
    def __init__(self, cause):
        super().__init__("CLI_CHILD_CLEANUP_UNCONFIRMED")
        self.cause = cause


class WorkerFailure(ProbeError):
    def __init__(self, record):
        super().__init__("WORKER_REPORTED_FAILURE")
        self.record = record


def fixed_failure_code(error):
    if isinstance(error, (ProbeError, LifecycleError, ProfileError, AdmissionError)):
        code = str(error)
        return code if code in FAILURE_CODES else "WORKER_UNCLASSIFIED_ERROR"
    if isinstance(error, OSError):
        return "WORKER_OS_ERROR"
    if isinstance(error, (ValueError, TypeError, KeyError)):
        return "WORKER_INPUT_ERROR"
    if isinstance(error, (KeyboardInterrupt, asyncio.CancelledError)):
        return "WORKER_CANCELLED"
    return "WORKER_UNCLASSIFIED_ERROR"


def failure_record(error, state):
    return dict(event="failed", schema=FAILURE_SCHEMA,
        phase=state["phase"] if state["phase"] in FAILURE_PHASES else "arguments",
        code=fixed_failure_code(error), cleanup_reaped=state["cleanup_code"] is None,
        cleanup_code=state["cleanup_code"])


def valid_failure_record(value):
    return (type(value) is dict and set(value) == {"event", "schema", "phase", "code", "cleanup_reaped", "cleanup_code"}
        and value["event"] == "failed" and value["schema"] == FAILURE_SCHEMA
        and type(value["phase"]) is str and value["phase"] in FAILURE_PHASES
        and type(value["code"]) is str and value["code"] in FAILURE_CODES
        and type(value["cleanup_reaped"]) is bool
        and (value["cleanup_code"] is None or type(value["cleanup_code"]) is str and value["cleanup_code"] in CLEANUP_CODES)
        and value["cleanup_reaped"] == (value["cleanup_code"] is None))


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
    child, error, result = None, None, None
    try:
        child = VerifiedPipeChild(program, arguments)
        result = child.read(limit=65536, timeout=2)
    except BaseException as caught:
        error = caught
    finally:
        if child is not None:
            try:
                child.close()
            except BaseException as caught:
                raise QueryCleanupUnconfirmed(error if error is not None else caught) from None
    if child is None:
        # Constructor unwind was internal; no ownership handle was returned.
        raise QueryCleanupUnconfirmed(error) from None
    if error is not None:
        raise error
    return result


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


async def serve(manifest, state):
    state["phase"] = "profile"
    profile = parse_profile(json.dumps(manifest["profile"]).encode())
    handles, core, probe = [], None, None
    failure = None
    try:
        state["phase"] = "images"
        for role in ("core", "cli", "probe"):
            image = manifest[role]
            handles.append(VerifiedExecutable(Path(image["path"]), image["sha256"], image["bytes"]))
        core_image, cli_image, probe_image = handles
        if (core_image.identity.sha256 != profile.candidate.executable_sha256
                or core_image.identity.size != profile.candidate.executable_size):
            fail("WORKER_CORE_PROFILE_MISMATCH")
        state["phase"] = "monitor-prepare"
        monitor = KernelTunMonitor(profile.interface_name, str(profile.local.overlay.ip))
        await monitor.prepare()
        # No inherited ET_* options; core output includes cfg.dump and stays null.
        state["phase"] = "core-start"
        core = await OwnedChild.spawn(core_image, core_arguments(profile, manifest["config"]))
        _reply({"event": "started", "profile_sha256": profile.sha256})
        while True:
            state["phase"] = "parent-command"
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
                state["phase"] = "core-stop"
                await core.stop()
                absent = False
                try:
                    socket.if_nametoindex(profile.interface_name)
                except OSError:
                    absent = True
                _reply({"event": "core-stopped", "core_reaped": core.closed, "tun_absent": absent})
                continue
            state["phase"] = "observe"
            if not core.image_matches():
                fail("WORKER_CORE_EXITED")
            observation = await monitor.observe(core)
            if action == "observe":
                authenticated = False
                query_state, query_code, local_bind_kind = "tun-pending", None, None
                if observation is not None:
                    state["phase"] = "peer-query"
                    try:
                        raw = await asyncio.to_thread(bounded_query, cli_image, peer_query_arguments(profile))
                        peer_observation = parse_peer_observation(profile, raw)
                        authenticated = True
                        query_state, local_bind_kind = "authenticated", peer_observation.local_bind_kind
                    except QueryCleanupUnconfirmed as error:
                        state["cleanup_code"] = "CLI_CHILD_CLEANUP_UNCONFIRMED"
                        raise error.cause from None
                    except asyncio.CancelledError:
                        # Cancelling the await does not establish that the
                        # bounded executor query has completed its own close.
                        state["cleanup_code"] = "CLI_CHILD_CLEANUP_UNCONFIRMED"
                        raise
                    except PeerObservationError as error:
                        query_state, query_code = "parse-rejected", error.reason
                    except ProbeError as error:
                        code = fixed_failure_code(error)
                        query_state = "query-failed"
                        query_code = code if code in QUERY_FAILURE_CODES else "WORKER_UNCLASSIFIED_ERROR"
                    state["phase"] = "observe"
                if not core.image_matches():
                    fail("WORKER_CORE_EXITED")
                _reply({"event": "observation", "tun_owned": observation is not None,
                        "peer_authenticated": authenticated,
                        "interface_index": observation.interface_index if observation is not None else None,
                        "probe_alive": probe.alive if probe is not None else None,
                        "readiness": dict(schema=READINESS_SCHEMA, core_image_matches=True,
                            tun_owned=observation is not None, peer_authenticated=authenticated,
                            query_state=query_state, query_code=query_code, local_bind_kind=local_bind_kind)})
            elif action == "start-probe":
                state["phase"] = "probe-start"
                if observation is None or probe is not None or profile.role != "responder":
                    fail("WORKER_PROBE_STATE_INVALID")
                probe = VerifiedPipeChild(probe_image, ("serve", "interface-bound",
                    str(profile.local.overlay.ip), profile.interface_name), writable=True)
                ready = parse_ready(probe.read(limit=4096, timeout=4, line=True), "interface-bound")
                _reply({"event": "probe-ready", **{key: ready[key] for key in PORT_FIELDS}})
            elif action == "echo":
                state["phase"] = "echo"
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
    except BaseException as error:
        failure = error
        if ((state["phase"] == "core-start" and core is None)
                or (state["phase"] == "probe-start" and probe is None)):
            # A failed spawn does its own unwind. Without a returned ownership
            # handle this worker conservatively makes no independent reap claim.
            state["cleanup_code"] = "CHILD_START_CLEANUP_UNCONFIRMED"
    finally:
        try:
            await cleanup_children(core, probe, handles)
        except BaseException as error:
            state["cleanup_code"] = "WORKER_CHILD_CLEANUP_FAILED"
            if failure is None:
                failure = error
                state["phase"] = "cleanup"
    if failure is not None:
        raise WorkerFailure(failure_record(failure, state)) from None
    _reply({"event": "stopped", "children_reaped": True})


def main(namespace_fd, manifest_path, state):
    # This executes before asyncio.to_thread or any spawned child. All threads
    # and exact core/CLI children inherit this one owned namespace afterwards.
    # The owner must acquire its worker pidfd before releasing any namespace
    # work or core startup. An acquisition failure closes stdin and starts none.
    state["phase"] = "release"
    if sys.stdin.buffer.readline(7) != b"START\n":
        fail("WORKER_NOT_RELEASED")
    state["phase"] = "manifest"
    manifest = json.loads(read_private(manifest_path, 32768))
    state["phase"] = "namespace"
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
    asyncio.run(serve(manifest, state))


if __name__ == "__main__":
    state = dict(phase="arguments", cleanup_code=None)
    try:
        if len(sys.argv) != 3:
            fail("WORKER_ARGUMENTS_INVALID")
        main(int(sys.argv[1]), sys.argv[2], state)
    except BaseException as error:
        # Exception messages and runtime/config output never enter public logs.
        try:
            _reply(error.record if isinstance(error, WorkerFailure) else failure_record(error, state))
        except BaseException:
            pass  # The parent may have closed the pipe; never log raw exceptions.
        sys.stderr.write("EASYTIER_DEVELOPMENT_WORKER_FAILED\n")
        raise SystemExit(1)
