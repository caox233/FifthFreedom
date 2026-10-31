"""Owned-namespace development test of the actual native Public lease entrypoint.

Requires root NET_ADMIN/SYS_ADMIN on an isolated Linux CI runner. Runtime/profile
bindings are explicitly synthetic; no EasyTier, game, production provider or
external route is started. Only a sanitized receipt survives temporary cleanup.
"""
from __future__ import annotations

import argparse
import asyncio
import ctypes
from dataclasses import replace
import hashlib
import json
import os
from pathlib import Path
import re
import select
import selectors
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
# -I deliberately omits script directories; use this reviewed checkout only.
sys.path.insert(0, str(ROOT / "scripts"))
try:
    import ci_socket_isolation as isolation
    import public_server_candidate as candidate
finally:
    sys.path.pop(0)
sys.path.insert(0, str(ROOT / "src/public"))
try:
    from fifth_freedom_control.native_lease import LeaseBinding, NativeOwnerLease
    from fifth_freedom_control.owned_process import LifecycleError, OwnedChild, VerifiedExecutable
finally:
    sys.path.pop(0)

CASES = ("withheld", "denied", "eof", "expiry", "parent_death")
PORTS = {"tcp": (80, 8000, 50051), "udp": (21126, 21127)}
SCOPE = {"synthetic_runtime_bindings": True, "controlled_native_startup_test": True,
         "encrypted_overlay_verified": False, "production_provider_open": False,
         "game_executed": False, "complete_windows_client": False,
         "deployed": False, "published": False}
MAX_EVENT = 4096


class ProbeError(RuntimeError):
    """Only fixed reason codes, never captured process/configuration content."""


def require(condition, code):
    if not condition:
        raise ProbeError(code)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_relative(name):
    require(type(name) is str and 1 <= len(name) <= 256 and "\\" not in name
            and not name.startswith("/") and all(part not in ("", ".", "..") for part in name.split("/")),
            "CANDIDATE_PATH_INVALID")
    return name


def validate_input(package, receipt, commit, run_id, attempt):
    """Read-only identity/run admission; this never executes downloaded input."""
    try:
        return _validate_input(package, receipt, commit, run_id, attempt)
    except FileNotFoundError:
        raise ProbeError("CANDIDATE_INPUT_MISSING") from None
    except OSError:
        raise ProbeError("CANDIDATE_INPUT_UNREADABLE") from None


def _validate_input(package, receipt, commit, run_id, attempt):
    # Keep file identity and inventory checks intact; the public boundary only
    # translates filesystem failures so paths cannot enter diagnostic output.
    require(re.fullmatch(r"[a-f0-9]{40}", commit) is not None
            and re.fullmatch(r"[1-9][0-9]{0,19}", run_id) is not None
            and re.fullmatch(r"[1-9][0-9]{0,7}", attempt) is not None, "EXPECTED_RUN_INVALID")
    require(type(receipt) is dict and receipt.get("schema") == 1 and type(receipt.get("schema")) is int
            and receipt.get("kind") == "public-server-component-candidate"
            and (receipt.get("source_commit"), receipt.get("run_id"), receipt.get("run_attempt")) == (commit, run_id, attempt)
            and receipt.get("scope") == candidate.SCOPE and receipt.get("status") == "native_dual_identity_verified"
            and receipt.get("native_verification") == receipt.get("expected_native_result"), "CANDIDATE_RECEIPT_INVALID")
    rows = receipt.get("files")
    require(type(rows) is dict and 7 <= len(rows) <= 64, "CANDIDATE_FILES_INVALID")
    required = {"bin/FifthFreedom.Server", "tools/verify_public_package", "package/PUBLIC-SERVER-CATALOG.json",
                "client/package/RELEASE-CATALOG.json", "client/FifthFreedom.exe",
                "client/runtime/uplay_r1_loader.dll", "client/runtime/scbl_gameplay.dll",
                "client/runtime/FifthFreedom.Server.exe"}
    require(required <= set(rows), "CANDIDATE_FILES_INVALID")
    result = {}
    for name, row in rows.items():
        safe_relative(name)
        require(type(row) is dict and set(row) == {"bytes", "sha256"}
                and type(row["bytes"]) is int and 0 < row["bytes"] <= candidate.LIMIT
                and type(row["sha256"]) is str and re.fullmatch(r"[0-9a-f]{64}", row["sha256"]), "CANDIDATE_METADATA_INVALID")
        require(candidate.sha_file(package / name) == row, "CANDIDATE_BYTES_CHANGED")
        result[name] = row
    actual = set()
    for path in package.rglob("*"):
        info = path.lstat()
        require(not stat.S_ISLNK(info.st_mode), "CANDIDATE_LINK_REJECTED")
        if not stat.S_ISDIR(info.st_mode):
            actual.add(path.relative_to(package).as_posix())
    require(actual == set(rows) | {"CANDIDATE-RECEIPT.json", "SHA256SUMS.txt"}, "CANDIDATE_EXTRA_FILES")
    for name in ("CANDIDATE-RECEIPT.json", "SHA256SUMS.txt"):
        result[name] = candidate.sha_file(package / name)
    identity = receipt["native_verification"]
    require(type(identity) is dict and identity.get("result") == "verified"
            and identity.get("platform") == "linux-x86_64"
            and identity.get("service", {}).get("executable_sha256") == rows["bin/FifthFreedom.Server"]["sha256"]
            and identity.get("service", {}).get("manifest_sha256") == rows["package/PUBLIC-SERVER-CATALOG.json"]["sha256"],
            "CANDIDATE_NATIVE_IDENTITY_INVALID")
    return result


def copy_private_package(source, destination, rows):
    destination.mkdir(mode=0o700)
    for name, row in rows.items():
        candidate.checked_copy(source / name, destination / name, row)
        (destination / name).chmod(0o700 if name in ("bin/FifthFreedom.Server", "tools/verify_public_package") else 0o600)
    # Every newly created directory belongs to this root-owned temporary tree.
    for path in destination.rglob("*"):
        if path.is_dir():
            path.chmod(0o700)


def private_file(path, content):
    with os.fdopen(os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_NOFOLLOW", 0), 0o600), "wb") as stream:
        stream.write(content)


def write_report(path, report):
    """Only the final sanitized receipt is readable by the non-root uploader."""
    with os.fdopen(os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_NOFOLLOW", 0), 0o644), "wb") as stream:
        stream.write((json.dumps(report, indent=2) + "\n").encode())
        if os.name == "posix":
            # Keep this independent of sudo's umask; no private inputs use this
            # writer. Change the owned descriptor, never a replacement pathname.
            os.fchmod(stream.fileno(), 0o644)


def emit(event, **fields):
    print(json.dumps({"event": event, **fields}, separators=(",", ":")), flush=True)


def read_event(process, expected, timeout):
    deadline = time.monotonic() + timeout
    value = bytearray(getattr(process, "_ff_pending", b""))
    with selectors.DefaultSelector() as selector:
        selector.register(process.stdout, selectors.EVENT_READ)
        while b"\n" not in value:
            remaining = deadline - time.monotonic()
            require(remaining > 0 and selector.select(remaining), "WORKER_EVENT_TIMEOUT")
            block = os.read(process.stdout.fileno(), MAX_EVENT + 1 - len(value))
            require(block, "WORKER_EVENT_EOF")
            value.extend(block)
            require(len(value) <= MAX_EVENT, "WORKER_EVENT_TOO_LARGE")
    line, remaining = value.split(b"\n", 1)
    process._ff_pending = remaining
    try:
        result = json.loads(line, object_pairs_hook=candidate.unique)
    except (ValueError, TypeError):
        raise ProbeError("WORKER_EVENT_INVALID") from None
    if type(result) is dict and result.get("event") == "FAILED":
        require(set(result) == {"event", "code"} and type(result["code"]) is str
                and re.fullmatch(r"[A-Z][A-Z0-9_]{1,95}", result["code"]), "WORKER_EVENT_INVALID")
        raise ProbeError("WORKER_" + result["code"])
    require(type(result) is dict and result.get("event") == expected, "WORKER_EVENT_INVALID")
    return result


def command(process, value):
    try:
        process.stdin.write(value.encode("ascii") + b"\n")
        process.stdin.flush()
    except (OSError, ValueError):
        raise ProbeError("WORKER_COMMAND_FAILED") from None


def stop_owned(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()
    process.wait(timeout=3)
    for stream in (process.stdin, process.stdout):
        if stream is not None:
            stream.close()


def listener_ownership(child, address):
    """Check all five kernel socket inodes against this exact child's proc FD."""
    require(child.image_matches(), "NATIVE_IMAGE_CHANGED")
    descriptors = os.open("fd", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=child.procfd)
    try:
        owned = set()
        for name in os.listdir(descriptors):
            try:
                link = os.readlink(name, dir_fd=descriptors)
            except FileNotFoundError:
                continue
            match = re.fullmatch(r"socket:\[(\d+)\]", link)
            if match:
                owned.add(match[1])
    finally:
        os.close(descriptors)
    encoded = f"{int.from_bytes(socket.inet_aton(address), 'little'):08X}"
    count = 0
    for protocol, ports in PORTS.items():
        lines = Path(f"/proc/net/{protocol}").read_text().splitlines()[1:]
        require(len(lines) <= 256, "NAMESPACE_SOCKET_TABLE_TOO_LARGE")
        # Accepted connections and TIME_WAIT share the listening local tuple.
        # Uniqueness is a property of TCP LISTEN sockets, not all TCP states.
        rows = [line.split() for line in lines]
        if protocol == "tcp":
            rows = [fields for fields in rows if fields[3] == "0A"]
        for port in ports:
            matches = [fields for fields in rows if fields[1] == f"{encoded}:{port:04X}"]
            require(len(matches) == 1 and matches[0][9] in owned
                    and matches[0][3] == ("0A" if protocol == "tcp" else "07"), "NATIVE_LISTENER_OWNERSHIP_INVALID")
            count += 1
    require(child.image_matches() and count == 5, "NATIVE_IMAGE_CHANGED")


async def worker(case, configuration, package):
    """Runs only inside an already-owned isolated server namespace."""
    document = candidate.read_json(configuration)
    receipt = candidate.read_json(package / "CANDIDATE-RECEIPT.json")
    identity = receipt["native_verification"]
    namespace = os.stat("/proc/self/ns/net")
    binding = LeaseBinding(digest(configuration.read_bytes()), identity["service"]["manifest_sha256"],
        identity["admitted_client"]["storage_key"], document["runtime_profile_sha256"], document["runtime_executable_sha256"],
        namespace.st_dev, namespace.st_ino, socket.if_nametoindex("ff-protected"), document["overlay_ipv4"], 30)
    expected = replace(binding, runtime_profile_sha256="f" * 64) if case == "denied" else binding
    owner = NativeOwnerLease(lambda: expected, lease_ms=1500, renew_seconds=.25, hello_timeout=60)
    binary = package / "bin/FifthFreedom.Server"
    row = receipt["files"]["bin/FifthFreedom.Server"]
    image = VerifiedExecutable(binary, row["sha256"], row["bytes"])
    child = None
    data = Path(document["data_directory"])
    require(not list(data.iterdir()), "STATE_NOT_EMPTY_BEFORE_ADMISSION")
    try:
        child = await OwnedChild.spawn(image, owner.arguments(str(configuration)), startup_timeout=30, lease_socket=owner.child_socket)
        owner.child_socket.close()
        emit("STARTED", pid=child.pid)
        deadline = time.monotonic() + 60
        if case == "withheld":
            code = await asyncio.wait_for(child.wait(), 64)
            require(code == 1 and not list(data.iterdir()), "WITHHELD_ADMISSION_WROTE_STATE")
            emit("DENIED", no_state_created=True)
            return
        try:
            while not await owner.observe(child):
                require(time.monotonic() < deadline, "NATIVE_READY_TIMEOUT")
                await asyncio.sleep(.05)
        except LifecycleError:
            if case != "denied":
                raise
            code = await asyncio.wait_for(child.wait(), 5)
            require(code == 1 and not list(data.iterdir()), "DENIED_ADMISSION_WROTE_STATE")
            emit("DENIED", no_state_created=True)
            return
        require(case in ("eof", "expiry", "parent_death"), "DENIED_ADMISSION_WAS_ACCEPTED")
        listener_ownership(child, document["overlay_ipv4"])
        require((data / "server.lock").is_file() and (data / "5th-echelon.db").is_file()
                and (data / "data/mp_balancing.ini").is_file(), "ADMITTED_STATE_MISSING")
        emit("READY", owned_listener_count=5, state_after_admission=True)
        with selectors.DefaultSelector() as selector:
            selector.register(sys.stdin, selectors.EVENT_READ)
            while True:
                require(await owner.observe(child), "NATIVE_READY_REVOKED")
                if selector.select(0):
                    incoming = os.read(sys.stdin.fileno(), 65)
                    require(incoming in (b"EOF\n", b"EXPIRY\n", b"PARENT_DEATH\n"), "WORKER_COMMAND_INVALID")
                    if incoming == b"PARENT_DEATH\n":
                        # The outer harness is a subreaper and holds this child's
                        # exact pidfd. No orphan is left to a container PID1.
                        os._exit(0)
                    if incoming == b"EOF\n":
                        owner.revoke()
                    # EXPIRY intentionally leaves the endpoint open and stops
                    # observation/renewal, proving the native timer acts alone.
                    code = await asyncio.wait_for(child.wait(), 6)
                    require(code == 92, "NATIVE_REVOCATION_EXIT_INVALID")
                    emit("STOPPED", native_exit=code)
                    return
                await asyncio.sleep(.05)
    finally:
        owner.revoke()
        if child is not None:
            await child.stop(.5, 2)
        image.close()


def held_open(stream):
    """Sample this held connection before the control; never open a new one.

    Drain only bounded protocol bytes. Actual EAGAIN is the open observation;
    EOF/reset are already-closed failures, not evidence about a later revoke.
    """
    deadline, received = time.monotonic() + .1, 0
    previous_timeout = stream.gettimeout()
    stream.setblocking(False)
    try:
        while True:
            require(time.monotonic() < deadline, "CLIENT_OPEN_CHECK_TIMEOUT")
            try:
                block = stream.recv(4096)
            except BlockingIOError:
                require(time.monotonic() < deadline, "CLIENT_OPEN_CHECK_TIMEOUT")
                return
            except OSError:
                raise ProbeError("CLIENT_CONNECTION_NOT_OPEN") from None
            require(block, "CLIENT_CONNECTION_NOT_OPEN")
            received += len(block)
            require(received <= 16384, "CLIENT_OUTPUT_LIMIT")
    finally:
        stream.settimeout(previous_timeout)


def peer_client(address, mode):
    if mode == "verify":
        for port, path in ((80, "/"), (8000, "/mp_balancing.ini")):
            with socket.create_connection((address, port), 2) as stream:
                stream.sendall(f"GET {path} HTTP/1.1\r\n\r\n".encode("ascii"))
                header = bytearray()
                while b"\r\n" not in header:
                    block = stream.recv(128 - len(header))
                    require(block and len(header) + len(block) <= 128, "HTTP_PROBE_FAILED")
                    header.extend(block)
                require(header.startswith(b"HTTP/1.0 200 OK\r\n"), "HTTP_PROBE_FAILED")
        with socket.create_connection((address, 50051), 2):
            pass
        emit("VERIFIED", tcp_listeners=3)
        return
    if mode == "denied":
        for port in PORTS["tcp"]:
            try:
                with socket.create_connection((address, port), .4):
                    raise ProbeError("WRONG_INTERFACE_ACCEPTED")
            except (TimeoutError, ConnectionRefusedError):
                pass
        emit("REJECTED", tcp_listeners=3)
        return
    require(mode == "hold", "CLIENT_MODE_INVALID")
    with socket.create_connection((address, 50051), 2) as stream:
        # Hold a real established TCP connection after sending an HTTP/2
        # preface, without invoking an account RPC or claiming an H2 handshake.
        stream.sendall(b"PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n" + b"\0\0\0\x04\0\0\0\0\0")
        emit("CONNECTED")
        require(sys.stdin.buffer.readline(65) == b"CHECK_OPEN\n", "CLIENT_COMMAND_INVALID")
        held_open(stream)
        emit("OPEN")
        require(sys.stdin.buffer.readline(65) == b"CHECK_CLOSED\n", "CLIENT_COMMAND_INVALID")
        stream.settimeout(3)
        received = 0
        while True:
            try:
                block = stream.recv(4096)
            except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
                break
            if not block:
                break
            received += len(block)
            require(received <= 16384, "CLIENT_OUTPUT_LIMIT")
        emit("CLOSED")


class Subreaper:
    """Adopt/reap only our known worker's native child after parent-death tests."""
    def __enter__(self):
        self.libc = ctypes.CDLL(None, use_errno=True)
        self.previous = ctypes.c_int()
        require(self.libc.prctl(37, ctypes.byref(self.previous), 0, 0, 0) == 0, "SUBREAPER_QUERY_FAILED")
        require(self.libc.prctl(36, 1, 0, 0, 0) == 0, "SUBREAPER_ENABLE_FAILED")
        return self

    def __exit__(self, *_):
        require(self.libc.prctl(36, self.previous.value, 0, 0, 0) == 0, "SUBREAPER_RESTORE_FAILED")


def pin_native(pid, worker_process):
    require(type(pid) is int and 1 <= pid < 2 ** 31, "NATIVE_PID_INVALID")
    fd = os.pidfd_open(pid)
    try:
        # Once the exact worker/child relationship is confirmed, transfer this
        # handle to the caller before any image checks that can fail. The caller
        # must still stop/reap this owned child after an image mismatch.
        status = Path(f"/proc/{pid}/stat").read_text()
        require(len(status) < 4096 and int(status.rsplit(") ", 1)[1].split()[1]) == worker_process.pid,
                "NATIVE_PARENT_CHANGED")
        return fd
    except BaseException:
        os.close(fd)
        raise


def validate_native_image(pid, pidfd, executable):
    actual, expected = os.stat(f"/proc/{pid}/exe"), executable.stat()
    require((actual.st_dev, actual.st_ino, actual.st_size) == (expected.st_dev, expected.st_ino, expected.st_size)
            and not select.select([pidfd], [], [], 0)[0], "NATIVE_IMAGE_CHANGED")


def wait_native_death(pid, pidfd, *, adopted_required=False):
    require(select.select([pidfd], [], [], 6)[0], "NATIVE_DID_NOT_STOP")
    try:
        got, status = os.waitpid(pid, os.WNOHANG)
    except ChildProcessError:
        require(not adopted_required, "NATIVE_NOT_ADOPTED")
        return None
    require(got == pid, "NATIVE_NOT_REAPED")
    code = os.waitstatus_to_exitcode(status)
    if adopted_required:
        require(code in (-signal.SIGKILL, 92), "NATIVE_PARENT_DEATH_EXIT_INVALID")
    return code


def start_process(arguments):
    try:
        return subprocess.Popen(arguments, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, bufsize=0, env={"PATH": "/usr/sbin:/usr/bin:/bin", "LANG": "C.UTF-8", "TZ": "UTC"})
    except OSError:
        raise ProbeError("PROBE_CHILD_START_FAILED") from None


def execute_case(namespaces, package, private, case):
    data = private / "data"
    data.mkdir(mode=0o700)
    private_file(private / "issuer.key", os.urandom(32))
    configuration = private / "deployment.json"
    private_file(configuration, json.dumps({"schema": 1, "overlay_ipv4": namespaces.topology.protected_ip,
        "overlay_prefix": 30, "overlay_interface": "ff-protected", "auth_key_file": str(private / "issuer.key"),
        "data_directory": str(data),
        "runtime_profile_sha256": digest(b"SYNTHETIC NATIVE LEASE DEVELOPMENT PROFILE; NO ENCRYPTED OVERLAY"),
        "runtime_executable_sha256": digest(b"SYNTHETIC NO RUNTIME EXECUTABLE WAS STARTED")}).encode())
    script = str(Path(__file__).resolve())
    executable = package / "bin/FifthFreedom.Server"
    worker_process = hold = None
    native_pid = pidfd = None
    native_reaped = False
    try:
        worker_process = start_process(namespaces.execute(namespaces.topology.names[0],
            [sys.executable, "-I", "-S", script, "--worker", case, str(configuration), str(package)]))
        started = read_event(worker_process, "STARTED", 35)
        require(set(started) == {"event", "pid"}, "WORKER_EVENT_INVALID")
        native_pid = started["pid"]
        pidfd = pin_native(native_pid, worker_process)
        validate_native_image(native_pid, pidfd, executable)
        if case in ("withheld", "denied"):
            event = read_event(worker_process, "DENIED", 66)
            require(event == {"event": "DENIED", "no_state_created": True}
                    and worker_process.wait(timeout=5) == 0 and not list(data.iterdir()), "EARLY_ADMISSION_SIDE_EFFECT")
            wait_native_death(native_pid, pidfd)
            native_reaped = True
            return {"case": case, "no_state_created": True, "native_reaped": True}
        event = read_event(worker_process, "READY", 66)
        require(event == {"event": "READY", "owned_listener_count": 5, "state_after_admission": True}, "WORKER_READY_INVALID")
        for namespace, mode, expected in ((namespaces.topology.names[1], "verify", "VERIFIED"),
                                          (namespaces.topology.names[2], "denied", "REJECTED")):
            client = start_process(namespaces.execute(namespace,
                [sys.executable, "-I", "-S", script, "--client", namespaces.topology.protected_ip, mode]))
            try:
                require(read_event(client, expected, 8) == {"event": expected, "tcp_listeners": 3}
                        and client.wait(timeout=3) == 0, "CLIENT_RESULT_INVALID")
            finally:
                stop_owned(client)
        hold = start_process(namespaces.execute(namespaces.topology.names[1],
            [sys.executable, "-I", "-S", script, "--client", namespaces.topology.protected_ip, "hold"]))
        require(read_event(hold, "CONNECTED", 5) == {"event": "CONNECTED"}, "CONNECTION_NOT_ESTABLISHED")
        command(hold, "CHECK_OPEN")
        require(read_event(hold, "OPEN", 3) == {"event": "OPEN"}
                and hold.poll() is None, "CONNECTION_NOT_OPEN")
        command(worker_process, {"eof": "EOF", "expiry": "EXPIRY", "parent_death": "PARENT_DEATH"}[case])
        if case == "parent_death":
            require(worker_process.wait(timeout=5) == 0, "PARENT_EXIT_FAILED")
            native_exit = wait_native_death(native_pid, pidfd, adopted_required=True)
        else:
            event = read_event(worker_process, "STOPPED", 7)
            require(event == {"event": "STOPPED", "native_exit": 92}
                    and worker_process.wait(timeout=3) == 0, "NATIVE_EXIT_INVALID")
            wait_native_death(native_pid, pidfd)
            native_exit = 92
        native_reaped = True
        command(hold, "CHECK_CLOSED")
        require(read_event(hold, "CLOSED", 5) == {"event": "CLOSED"} and hold.wait(timeout=3) == 0, "CONNECTION_NOT_CLOSED")
        # Check the exact five ports can now be bound in that same owned server
        # namespace. This tests UDP closure as well as the three TCP listeners.
        closed = start_process(namespaces.execute(namespaces.topology.names[0],
            [sys.executable, "-I", "-S", script, "--closed-ports", namespaces.topology.protected_ip]))
        try:
            require(read_event(closed, "PORTS_CLOSED", 5) == {"event": "PORTS_CLOSED", "count": 5}
                    and closed.wait(timeout=3) == 0, "LISTENERS_NOT_CLOSED")
        finally:
            stop_owned(closed)
        return {"case": case, "actual_listener_count": 5, "protected_http_and_tcp_verified": True,
                "wrong_interface_rejected": True, "established_connection_closed": True,
                "all_five_ports_released": True, "native_exit": native_exit, "native_reaped": True}
    finally:
        cleanup_failed = False
        for process in (hold, worker_process):
            if process is not None:
                try:
                    stop_owned(process)
                except (OSError, subprocess.SubprocessError):
                    cleanup_failed = True
        if pidfd is not None:
            try:
                if not native_reaped:
                    # Exact handle only; an exceptional worker stop may adopt
                    # its native child. No process-group/PID search is used.
                    if not select.select([pidfd], [], [], 2)[0]:
                        signal.pidfd_send_signal(pidfd, signal.SIGKILL)
                    wait_native_death(native_pid, pidfd)
            except (OSError, ProbeError):
                cleanup_failed = True
            finally:
                os.close(pidfd)
        require(not cleanup_failed, "OWNED_PROCESS_CLEANUP_FAILED")


def closed_ports(address):
    handles = []
    try:
        for protocol, ports in PORTS.items():
            for port in ports:
                handle = socket.socket(socket.AF_INET, socket.SOCK_STREAM if protocol == "tcp" else socket.SOCK_DGRAM)
                handles.append(handle)
                if protocol == "tcp":
                    handle.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                handle.bind((address, port))
                if protocol == "tcp":
                    handle.listen(1)
        emit("PORTS_CLOSED", count=5)
    finally:
        for handle in handles:
            handle.close()



def verify_checkout(commit):
    # sudo executes as root while checkout remains owned by the ordinary runner.
    # Trust only this read-only command's exact repository, never global Git
    # configuration, a wildcard safe.directory or a chown of shared inputs.
    git = ["git", "-c", "safe.directory=" + str(ROOT), "-C", str(ROOT)]
    try:
        actual = subprocess.check_output([*git, "rev-parse", "HEAD"], text=True,
            stderr=subprocess.DEVNULL, timeout=5).strip()
        dirty = subprocess.check_output([*git, "status", "--porcelain", "--untracked-files=no"],
            stderr=subprocess.DEVNULL, timeout=5)
    except (OSError, subprocess.SubprocessError):
        raise ProbeError("CHECKOUT_VERIFICATION_FAILED") from None
    require(actual == commit, "CHECKOUT_REVISION_MISMATCH")
    require(not dirty.strip(), "CHECKOUT_TRACKED_SOURCE_DIRTY")


def run(package, receipt_path, runner_temp, commit, run_id, attempt):
    ip = isolation.preflight()  # Missing root/caps is a failure before mutation.
    verify_checkout(commit)
    package, runner_temp = package.absolute(), runner_temp.absolute()
    require(not runner_temp.is_relative_to(package), "PRIVATE_ROOT_INSIDE_CANDIDATE")
    receipt = candidate.read_json(receipt_path)
    require(receipt == candidate.read_json(package / "CANDIDATE-RECEIPT.json"), "CANDIDATE_RECEIPT_MISMATCH")
    rows = validate_input(package, receipt, commit, run_id, attempt)
    topology = isolation.Topology.fresh()
    namespaces = isolation.OwnedNamespaces(ip, topology)
    private_path = None
    outcomes = []
    try:
        with tempfile.TemporaryDirectory(prefix="ff-native-lifecycle-", dir=runner_temp) as temporary:
            private_path = Path(temporary)
            private_path.chmod(0o700)
            copied = private_path / "package"
            copy_private_package(package, copied, rows)
            verified = subprocess.run([str(copied / "tools/verify_public_package"), str(copied)],
                stdin=subprocess.DEVNULL, capture_output=True, timeout=120, cwd=copied,
                env={"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8", "TZ": "UTC"})
            require(verified.returncode == 0 and len(verified.stdout) <= 16384 and not verified.stderr,
                    "COPIED_NATIVE_VERIFICATION_FAILED")
            require(json.loads(verified.stdout, object_pairs_hook=candidate.unique) == receipt["native_verification"],
                    "COPIED_NATIVE_IDENTITY_MISMATCH")
            namespaces.setup()
            for name in topology.names:
                require(not json.loads(namespaces.command(name, "-j", "route", "show", "default")), "NAMESPACE_DEFAULT_ROUTE_PRESENT")
            with Subreaper():
                for case in CASES:
                    directory = private_path / case
                    directory.mkdir(mode=0o700)
                    outcomes.append(execute_case(namespaces, copied, directory, case))
            for name, row in rows.items():
                require(candidate.sha_file(copied / name) == row, "PRIVATE_PACKAGE_CHANGED")
    finally:
        cleanup = namespaces.cleanup()
        require(cleanup, "OWNED_NAMESPACE_CLEANUP_FAILED")
    require(cleanup and len(namespaces.deleted) == 3, "OWNED_NAMESPACE_CLEANUP_FAILED")
    require(private_path is not None and not private_path.exists(), "PRIVATE_TREE_NOT_REMOVED")
    require(validate_input(package, receipt, commit, run_id, attempt) == rows, "SOURCE_PACKAGE_CHANGED")
    return {"schema": 1, "status": "native_owned_lifecycle_verified", "source_commit": commit,
            "run_id": run_id, "run_attempt": attempt, "scope": dict(SCOPE), "cases": outcomes,
            "copied_package_native_verified": True, "source_package_unchanged": True,
            "private_tree_removed": True, "owned_namespaces_removed": 3,
            "all_owned_children_reaped": True, "no_default_routes": True}


def dispatch(argv=None):
    arguments = sys.argv[1:] if argv is None else argv
    try:
        if arguments[:1] == ["--worker"]:
            require(len(arguments) == 4 and arguments[1] in CASES, "WORKER_ARGUMENT_INVALID")
            asyncio.run(worker(arguments[1], Path(arguments[2]), Path(arguments[3])))
            return 0
        if arguments[:1] == ["--client"]:
            require(len(arguments) == 3, "CLIENT_ARGUMENT_INVALID")
            peer_client(arguments[1], arguments[2])
            return 0
        if arguments[:1] == ["--closed-ports"]:
            require(len(arguments) == 2, "CLIENT_ARGUMENT_INVALID")
            closed_ports(arguments[1])
            return 0
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument("--package", type=Path, required=True)
        parser.add_argument("--candidate-receipt", type=Path, required=True)
        parser.add_argument("--runner-temp", type=Path, required=True)
        parser.add_argument("--report", type=Path, required=True)
        parser.add_argument("--commit", required=True)
        parser.add_argument("--run-id", required=True)
        parser.add_argument("--run-attempt", required=True)
        args = parser.parse_args(arguments)
        require(not args.report.absolute().is_relative_to(args.package.absolute()), "REPORT_INSIDE_CANDIDATE")
        # The ordinary CI runner creates this directory before invoking sudo;
        # creating it as root with umask077 would prevent artifact traversal.
        require(args.report.parent.is_dir(), "REPORT_DIRECTORY_REQUIRED")
        result = run(args.package, args.candidate_receipt, args.runner_temp, args.commit, args.run_id, args.run_attempt)
        write_report(args.report, result)
        print(json.dumps({"status": result["status"], "private_tree_removed": True,
                          "production_provider_open": False, "encrypted_overlay_verified": False}))
        return 0
    except (ProbeError, LifecycleError, isolation.IsolationError, candidate.CandidateError) as error:
        code = str(error)
    except KeyboardInterrupt:
        code = "NATIVE_LIFECYCLE_INTERRUPTED"
    except (OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError, TimeoutError):
        code = "NATIVE_LIFECYCLE_PROBE_FAILED"
    if arguments[:1] in (["--worker"], ["--client"], ["--closed-ports"]):
        try:
            emit("FAILED", code=code)
        except OSError:
            pass
    else:
        print(code, file=sys.stderr)
    return 1


def main(argv=None):
    # CI SIGTERM follows the same finally cleanup as a failed assertion. The
    # first signal disables repeated SIGTERM so cleanup cannot be interrupted;
    # SIGKILL or loss of the entire runner cannot be handled in userspace.
    previous = signal.signal(signal.SIGTERM, isolation.cancelled)
    try:
        return dispatch(argv)
    finally:
        signal.signal(signal.SIGTERM, previous)


if __name__ == "__main__":
    raise SystemExit(main())
