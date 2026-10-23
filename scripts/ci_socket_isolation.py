"""Verify native socket ingress isolation in three owned Linux namespaces.

Run only on an isolated development/CI machine with namespace privileges. This
uses a synthetic echo probe linked to production SocketPolicy, not a game server
or an encrypted overlay. Missing privileges are a failure, never a passing skip.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import errno
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import secrets
import selectors
import shutil
import signal
import socket
import stat
import subprocess
import sys
import time

PAYLOAD = b"ff-socket-probe-v1"
COMMAND_TIMEOUT = 8
CLIENT_TIMEOUT = 1.0
READY_TIMEOUT = 8
OUTPUT_LIMIT = 4096
PORT_FIELDS = ("tcp_port", "udp_port", "udp_ephemeral_port")
SCOPE = {"encrypted_overlay_verified": False, "owned_overlay_liveness_verified": False,
         "game_executed": False, "deployed": False, "public_enabled": False}


class IsolationError(RuntimeError):
    """Fixed diagnostic codes only; never expose subprocess exception content."""


def checked_run(command):
    try:
        result = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=COMMAND_TIMEOUT, check=False)
    except subprocess.TimeoutExpired:
        raise IsolationError("COMMAND_TIMEOUT") from None
    except OSError:
        raise IsolationError("COMMAND_START_FAILED") from None
    if result.returncode != 0:
        raise IsolationError("COMMAND_FAILED")
    if len(result.stdout) + len(result.stderr) > OUTPUT_LIMIT:
        raise IsolationError("COMMAND_OUTPUT_TOO_LARGE")
    return result.stdout


def preflight():
    if sys.platform != "linux":
        raise IsolationError("LINUX_REQUIRED")
    if os.geteuid() != 0:
        raise IsolationError("ROOT_NAMESPACE_PRIVILEGES_REQUIRED")
    try:
        line = next(row for row in Path("/proc/self/status").read_text().splitlines()
                    if row.startswith("CapEff:"))
        capabilities = int(line.split()[1], 16)
    except (OSError, ValueError, StopIteration, IndexError):
        raise IsolationError("NAMESPACE_PRIVILEGES_UNVERIFIED") from None
    # ip netns add mounts a namespace handle; veth and addresses require NET_ADMIN.
    if capabilities & ((1 << 12) | (1 << 21)) != ((1 << 12) | (1 << 21)):
        raise IsolationError("NET_ADMIN_AND_SYS_ADMIN_REQUIRED")
    ip = shutil.which("ip")
    if ip is None:
        raise IsolationError("IPROUTE2_REQUIRED")
    return ip


@dataclass(frozen=True)
class Topology:
    token: str
    subnet: int

    @staticmethod
    def fresh():
        return Topology(secrets.token_hex(8), secrets.randbelow(256))

    def __post_init__(self):
        if (len(self.token) != 16 or any(x not in "0123456789abcdef" for x in self.token)
                or type(self.subnet) is not int or not 0 <= self.subnet <= 255):
            raise IsolationError("TOPOLOGY_INVALID")

    @property
    def names(self):
        return tuple(f"ffsi-{self.token}-{role}" for role in ("s", "p", "w"))

    @property
    def protected_ip(self):
        return f"10.203.{self.subnet}.1"

    @property
    def external_ip(self):
        return f"10.204.{self.subnet}.1"

    @property
    def protected_client_ip(self):
        return f"10.203.{self.subnet}.2"

    @property
    def wrong_client_ip(self):
        return f"10.204.{self.subnet}.2"


class OwnedNamespaces:
    """Keep open handles and compare their identity before using/deleting names.

    Only successful `ip netns add` operations become owned. A collision or changed
    handle is never deleted. No prefix enumeration or host link mutation is used.
    """
    def __init__(self, ip, topology, run=checked_run, namespace_root=Path("/run/netns")):
        self.ip = ip
        self.topology = topology
        self.run = run
        self.namespace_root = Path(namespace_root)
        self.owned = {}
        self.cleanup_errors = []
        self.deleted = []

    def create(self, name):
        if name not in self.topology.names or name in self.owned:
            raise IsolationError("NAMESPACE_NAME_NOT_OWNED")
        path = self.namespace_root / name
        if os.path.lexists(path):
            raise IsolationError("NAMESPACE_COLLISION")
        self.run([self.ip, "netns", "add", name])
        # Remember successful creation even if acquiring the identity fails.
        # An unverifiable handle is deliberately left alone and reported failed.
        self.owned[name] = None
        try:
            fd = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0))
            self.owned[name] = fd
            self.assert_owned(name)
        except OSError:
            raise IsolationError("NAMESPACE_IDENTITY_UNVERIFIED") from None

    def assert_owned(self, name):
        if name not in self.owned or self.owned[name] is None:
            raise IsolationError("NAMESPACE_IDENTITY_UNVERIFIED")
        try:
            expected = os.fstat(self.owned[name])
            current = (self.namespace_root / name).lstat()
        except OSError:
            raise IsolationError("NAMESPACE_IDENTITY_CHANGED") from None
        if (stat.S_ISLNK(current.st_mode)
                or (expected.st_dev, expected.st_ino) != (current.st_dev, current.st_ino)):
            raise IsolationError("NAMESPACE_IDENTITY_CHANGED")

    def command(self, name, *arguments):
        self.assert_owned(name)
        return self.run([self.ip, "-n", name, *arguments])

    def execute(self, name, arguments):
        self.assert_owned(name)
        return [self.ip, "netns", "exec", name, *arguments]

    def setup(self):
        server, protected, wrong = self.topology.names
        for name in (server, protected, wrong):
            self.create(name)
        # Both veth pairs originate inside the owned server namespace. Their
        # peers move directly to owned clients, never into the host namespace.
        self.assert_owned(protected)
        self.command(server, "link", "add", "ff-protected", "type", "veth", "peer", "name",
                     "ff-client", "netns", protected)
        self.assert_owned(wrong)
        self.command(server, "link", "add", "ff-outside", "type", "veth", "peer", "name",
                     "ff-client", "netns", wrong)
        for name in (server, protected, wrong):
            self.command(name, "link", "set", "lo", "up")
        for name, interface, address in (
                (server, "ff-protected", self.topology.protected_ip),
                (server, "ff-outside", self.topology.external_ip),
                (protected, "ff-client", self.topology.protected_client_ip),
                (wrong, "ff-client", self.topology.wrong_client_ip)):
            self.command(name, "address", "add", address + "/30", "dev", interface)
            self.command(name, "link", "set", interface, "up")
        self.command(wrong, "route", "add", self.topology.protected_ip + "/32", "via",
                     self.topology.external_ip, "dev", "ff-client")
        # The destination is local to the server. No IP forwarding, host route,
        # sysctl, firewall, NAT, default route or external connection is needed.

    def cleanup(self):
        for name, fd in reversed(list(self.owned.items())):
            try:
                self.assert_owned(name)
                self.run([self.ip, "netns", "delete", name])
                if os.path.lexists(self.namespace_root / name):
                    raise IsolationError("NAMESPACE_CLEANUP_INCOMPLETE")
                self.deleted.append(name)
            except (IsolationError, OSError):
                self.cleanup_errors.append("NAMESPACE_CLEANUP_FAILED")
            finally:
                if fd is not None:
                    try:
                        os.close(fd)
                    except OSError:
                        self.cleanup_errors.append("NAMESPACE_HANDLE_CLOSE_FAILED")
                del self.owned[name]
        return not self.cleanup_errors


def unique_json(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise IsolationError("DUPLICATE_JSON_KEY")
        value[key] = item
    return value


def parse_ready(data, mode):
    try:
        value = json.loads(data, object_pairs_hook=unique_json)
    except (ValueError, UnicodeError):
        raise IsolationError("PROBE_READY_INVALID") from None
    if (not isinstance(value, dict)
            or set(value) != {"schema", "event", "mode", *PORT_FIELDS}
            or type(value.get("schema")) is not int or value["schema"] != 1
            or value["event"] != "READY" or value["mode"] != mode
            or any(type(value[key]) is not int or not 1 <= value[key] <= 65535
                   for key in PORT_FIELDS)
            or value["udp_port"] == value["udp_ephemeral_port"]):
        raise IsolationError("PROBE_READY_INVALID")
    return value


def read_ready(process, mode):
    deadline = time.monotonic() + READY_TIMEOUT
    data = b""
    with selectors.DefaultSelector() as selector:
        selector.register(process.stdout, selectors.EVENT_READ)
        while b"\n" not in data:
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not selector.select(remaining):
                raise IsolationError("PROBE_READY_TIMEOUT")
            block = os.read(process.stdout.fileno(), OUTPUT_LIMIT + 1 - len(data))
            if not block:
                raise IsolationError("PROBE_READY_EOF")
            data += block
            if len(data) > OUTPUT_LIMIT:
                raise IsolationError("PROBE_READY_TOO_LARGE")
    if data.count(b"\n") != 1 or not data.endswith(b"\n"):
        raise IsolationError("PROBE_READY_INVALID")
    return parse_ready(data, mode)


class OwnedProbe:
    def __init__(self, namespaces, executable, mode):
        self.namespaces = namespaces
        self.mode = mode
        self.process = None
        self.ready = None
        self.executable = executable
        self.stopped = False

    def start(self):
        command = self.namespaces.execute(self.namespaces.topology.names[0],
            [str(self.executable), "serve", self.mode,
             self.namespaces.topology.protected_ip, "ff-protected"])
        try:
            self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                            stderr=subprocess.STDOUT, bufsize=0)
        except OSError:
            raise IsolationError("PROBE_START_FAILED") from None
        self.ready = read_ready(self.process, self.mode)
        self.assert_alive()
        return self.ready

    def assert_alive(self):
        if self.process is None or self.process.poll() is not None:
            raise IsolationError("PROBE_NOT_ALIVE")

    def stop(self):
        if self.process is None:
            return True
        process = self.process
        clean = False
        try:
            if process.poll() is None:
                try:
                    process.stdin.write(b"STOP\n")
                    process.stdin.flush()
                    clean = process.wait(timeout=4) == 0
                except (OSError, subprocess.TimeoutExpired):
                    pass
            # Signal only this Popen child. No PID enumeration, process groups,
            # global name matches or externally supplied process identities.
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=2)
        except (OSError, subprocess.TimeoutExpired):
            clean = False
        finally:
            self.stopped = process.poll() is not None
            for stream in (process.stdin, process.stdout):
                if stream is not None:
                    stream.close()
        return clean and self.stopped


def observe_echo(protocol, address, port, allowed_sources):
    """One bounded synthetic exchange, used inside an owned client namespace."""
    kind = socket.SOCK_STREAM if protocol == "tcp" else socket.SOCK_DGRAM
    try:
        with socket.socket(socket.AF_INET, kind) as channel:
            deadline = time.monotonic() + CLIENT_TIMEOUT
            channel.settimeout(CLIENT_TIMEOUT)
            if protocol == "tcp":
                channel.connect((address, port))
                channel.sendall(PAYLOAD)
                data = b""
                while len(data) < len(PAYLOAD):
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        return "timeout"
                    channel.settimeout(remaining)
                    chunk = channel.recv(len(PAYLOAD) + 1 - len(data))
                    if not chunk:
                        return "closed"
                    data += chunk
                return "echo" if data == PAYLOAD else "invalid_reply"
            # A wildcard LAN ephemeral socket replies using its egress address.
            # Unconnected UDP intentionally permits either server interface IP;
            # still require exact payload and the original destination port.
            channel.sendto(PAYLOAD, (address, port))
            data, peer = channel.recvfrom(len(PAYLOAD) + 1)
            return ("echo" if data == PAYLOAD and peer[1] == port
                    and peer[0] in allowed_sources else "invalid_reply")
    except (TimeoutError, socket.timeout):
        return "timeout"
    except OSError as error:
        if error.errno in (errno.ECONNREFUSED, errno.ECONNRESET):
            return "refused"
        return "network_error"


def client_observation(namespaces, client_name, protocol, port):
    topology = namespaces.topology
    command = namespaces.execute(client_name, [sys.executable, str(Path(__file__).resolve()),
        "client", "--protocol", protocol, "--address", topology.protected_ip,
        "--port", str(port), "--allow-source", topology.protected_ip,
        "--allow-source", topology.external_ip])
    data = namespaces.run(command)
    try:
        value = json.loads(data, object_pairs_hook=unique_json)
    except (ValueError, UnicodeError):
        raise IsolationError("CLIENT_RESULT_INVALID") from None
    if (not isinstance(value, dict) or set(value) != {"schema", "observation"}
            or type(value["schema"]) is not int or value["schema"] != 1
            or value["observation"] not in ("echo", "timeout", "refused", "closed",
                                            "invalid_reply", "network_error")):
        raise IsolationError("CLIENT_RESULT_INVALID")
    return value["observation"]


def check_path(namespaces, probe, phase, path, expect_echo, observations, observe):
    client = namespaces.topology.names[1 if path == "protected" else 2]
    for field in PORT_FIELDS:
        probe.assert_alive()
        protocol = "tcp" if field == "tcp_port" else "udp"
        outcome = observe(namespaces, client, protocol, probe.ready[field])
        observations.append({"mode": probe.mode, "phase": phase, "path": path,
                             "socket": field, "observation": outcome})
        probe.assert_alive()
        if expect_echo and outcome != "echo":
            raise IsolationError("REQUIRED_PATH_DID_NOT_ECHO")
        if not expect_echo and outcome not in ("timeout", "refused"):
            raise IsolationError("WRONG_INTERFACE_NOT_ISOLATED")


def verify_paths(namespaces, executable, observations, cleanup_errors,
                 probe_factory=OwnedProbe, observe=client_observation):
    # No topology changes between controls and the production enforcement test.
    for mode in ("ip-only", "interface-bound"):
        probe = probe_factory(namespaces, executable, mode)
        try:
            probe.start()
            check_path(namespaces, probe, "before", "protected", True, observations, observe)
            check_path(namespaces, probe, "ingress", "wrong", mode == "ip-only", observations, observe)
            check_path(namespaces, probe, "after", "protected", True, observations, observe)
        finally:
            if not probe.stop():
                cleanup_errors.append("PROBE_CLEANUP_FAILED")
        if cleanup_errors:
            raise IsolationError("PROBE_CLEANUP_FAILED")


def probe_digest(path):
    try:
        path = path.resolve(strict=True)
        info = path.stat()
        if not stat.S_ISREG(info.st_mode) or not os.access(path, os.X_OK) or info.st_size > 512 * 1024 * 1024:
            raise IsolationError("PROBE_FILE_INVALID")
        digest = hashlib.sha256()
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
        return path, digest.hexdigest()
    except OSError:
        raise IsolationError("PROBE_FILE_INVALID") from None


def run_verification(executable):
    receipt = {"schema": 1, "interface_isolation_verified": False, **SCOPE,
               "status": "failed", "observations": [], "cleanup_errors": [],
               "namespaces_created": 0, "namespaces_removed": 0}
    namespaces = None
    try:
        ip = preflight()
        executable, digest = probe_digest(executable)
        receipt["probe_sha256"] = digest
        topology = Topology.fresh()
        receipt["topology"] = {"server_ip": topology.protected_ip,
                               "external_ip": topology.external_ip, "namespaces": topology.names}
        namespaces = OwnedNamespaces(ip, topology)
        namespaces.setup()
        verify_paths(namespaces, executable, receipt["observations"], receipt["cleanup_errors"])
    except IsolationError as error:
        receipt["error"] = str(error)
    except (OSError, ValueError, KeyboardInterrupt):
        receipt["error"] = "HARNESS_INTERRUPTED_OR_IO_FAILED"
    finally:
        if namespaces is not None:
            receipt["namespaces_created"] = len(namespaces.owned)
            namespaces.cleanup()
            receipt["namespaces_removed"] = len(namespaces.deleted)
            receipt["cleanup_errors"].extend(namespaces.cleanup_errors)
    if "error" not in receipt and not receipt["cleanup_errors"]:
        receipt["status"] = "interface_ingress_isolation_verified"
        receipt["interface_isolation_verified"] = True
    return receipt


def cancelled(signum, frame):
    # Repeated termination signals must not interrupt the owned finally cleanup.
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    raise KeyboardInterrupt


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    # SIGTERM from a CI cancellation follows the same bounded owned cleanup as
    # Ctrl-C; SIGKILL/machine loss cannot be handled by any userspace finally.
    previous = signal.signal(signal.SIGTERM, cancelled)
    try:
        receipt = run_verification(args.probe)
    finally:
        signal.signal(signal.SIGTERM, previous)
    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open("x", encoding="utf-8") as output:
            json.dump(receipt, output, indent=2)
            output.write("\n")
    except OSError:
        print("ISOLATION_RECEIPT_WRITE_FAILED", file=sys.stderr)
        return 1
    print(json.dumps(receipt, sort_keys=True))
    return 0 if receipt["interface_isolation_verified"] else 1


def client_main(argv):
    parser = argparse.ArgumentParser(description="Internal synthetic client for the owned namespace harness")
    parser.add_argument("--protocol", choices=("tcp", "udp"), required=True)
    parser.add_argument("--address", type=ipaddress.IPv4Address, required=True)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--allow-source", type=ipaddress.IPv4Address, action="append", required=True)
    args = parser.parse_args(argv)
    octets = str(args.address).split(".")
    expected_sources = {str(args.address), f"10.204.{octets[2]}.1"}
    if (not 1 <= args.port <= 65535 or octets[:2] != ["10", "203"] or octets[3] != "1"
            or {str(value) for value in args.allow_source} != expected_sources):
        return 1
    outcome = observe_echo(args.protocol, str(args.address), args.port,
                           {str(value) for value in args.allow_source})
    print(json.dumps({"schema": 1, "observation": outcome}))
    return 0


if __name__ == "__main__":
    raise SystemExit(client_main(sys.argv[2:]) if len(sys.argv) > 1 and sys.argv[1] == "client"
                     else main())
