"""Ownership/failure regressions; these are not a live namespace acceptance run."""
from pathlib import Path
import io
import json
import os
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_socket_isolation as isolation


@unittest.skipUnless(sys.platform == "linux", "Linux namespace handle identity/cleanup semantics")
class NamespaceFixture(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="ff-isolation-unit-")
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.topology = isolation.Topology("0123456789abcdef", 37)
        self.commands = []
        self.fail_at = None
        self.ns = isolation.OwnedNamespaces("ip", self.topology, self.run_command, self.directory)
        self.addCleanup(self.ns.cleanup)

    def run_command(self, command):
        self.commands.append(command)
        if self.fail_at == len(self.commands):
            raise isolation.IsolationError("SYNTHETIC_COMMAND_FAILURE")
        if command[1:3] == ["netns", "add"]:
            (self.directory / command[3]).touch(exist_ok=False)
        elif command[1:3] == ["netns", "delete"]:
            (self.directory / command[3]).unlink()
        return b""

    def test_topology_never_changes_host_links_routes_or_sysctls(self):
        self.ns.setup()
        server, protected, wrong = self.topology.names
        mutations = [command for command in self.commands if command[1:3] != ["netns", "add"]]
        self.assertTrue(all(command[:2] == ["ip", "-n"] for command in mutations))
        self.assertTrue(all(command[2] in self.topology.names for command in mutations))
        veths = [command for command in mutations if command[3:5] == ["link", "add"]]
        self.assertEqual(len(veths), 2)
        self.assertTrue(all(command[2] == server for command in veths))
        self.assertEqual({command[-1] for command in veths}, {protected, wrong})
        routes = [command for command in mutations if command[3] == "route"]
        self.assertEqual(routes, [["ip", "-n", wrong, "route", "add", "10.203.37.1/32",
                                  "via", "10.204.37.1", "dev", "ff-client"]])
        addresses = [command for command in mutations if command[3] == "address"]
        self.assertEqual({command[5] for command in addresses},
                         {"10.203.37.1/30", "10.203.37.2/30", "10.204.37.1/30", "10.204.37.2/30"})
        self.assertTrue(self.ns.cleanup())
        self.assertEqual(self.ns.deleted, list(reversed(self.topology.names)))
        self.assertFalse(list(self.directory.iterdir()))

    def test_preexisting_namespace_is_never_owned_or_deleted(self):
        name = self.topology.names[0]
        sentinel = self.directory / name
        sentinel.write_text("existing owner")
        with self.assertRaisesRegex(isolation.IsolationError, "COLLISION"):
            self.ns.setup()
        self.assertTrue(self.ns.cleanup())
        self.assertEqual(sentinel.read_text(), "existing owner")
        self.assertEqual(self.commands, [])

    def test_add_failure_cleans_only_prior_successes(self):
        self.fail_at = 2
        with self.assertRaisesRegex(isolation.IsolationError, "SYNTHETIC"):
            self.ns.setup()
        self.assertEqual(list(self.ns.owned), [self.topology.names[0]])
        self.assertTrue(self.ns.cleanup())
        deletes = [command[-1] for command in self.commands if command[1:3] == ["netns", "delete"]]
        self.assertEqual(deletes, [self.topology.names[0]])

    def test_setup_link_failure_still_removes_all_three_owned_namespaces(self):
        self.fail_at = 4
        with self.assertRaises(isolation.IsolationError):
            self.ns.setup()
        self.assertTrue(self.ns.cleanup())
        self.assertEqual(set(self.ns.deleted), set(self.topology.names))

    def test_replaced_namespace_handle_is_never_used_or_removed(self):
        name = self.topology.names[0]
        self.ns.create(name)
        original = self.directory / name
        original.rename(self.directory / "old-owner-handle")
        original.write_text("replacement owner")
        with self.assertRaisesRegex(isolation.IsolationError, "IDENTITY_CHANGED"):
            self.ns.execute(name, ["synthetic-probe"])
        self.assertFalse(self.ns.cleanup())
        self.assertEqual(original.read_text(), "replacement owner")
        self.assertFalse(any(command[1:3] == ["netns", "delete"] for command in self.commands))

    def test_unowned_name_cannot_execute_commands(self):
        with self.assertRaisesRegex(isolation.IsolationError, "IDENTITY_UNVERIFIED"):
            self.ns.command("unrelated", "link", "set", "lo", "up")
        self.assertEqual(self.commands, [])

    def test_cleanup_error_is_preserved_and_other_owned_names_are_cleaned(self):
        self.ns.setup()
        self.fail_at = len(self.commands) + 1
        self.assertFalse(self.ns.cleanup())
        self.assertEqual(len(self.ns.deleted), 2)
        self.assertEqual(self.ns.cleanup_errors, ["NAMESPACE_CLEANUP_FAILED"])


class ProbeFlowTests(unittest.TestCase):
    def setUp(self):
        self.namespaces = type("Namespaces", (), {"topology": isolation.Topology("0123456789abcdef", 37)})()
        self.probes = []
        self.observations = []
        self.cleanup_errors = []
        owner = self

        class Probe:
            def __init__(self, namespaces, executable, mode):
                self.mode = mode
                self.ready = dict(zip(isolation.PORT_FIELDS, (10001, 10002, 10003)))
                self.alive = True
                self.stopped = False
                owner.probes.append(self)
            def start(self):
                return self.ready
            def assert_alive(self):
                if not self.alive:
                    raise isolation.IsolationError("PROBE_NOT_ALIVE")
            def stop(self):
                self.stopped = True
                self.alive = False
                return True
        self.factory = Probe

    def observer(self, namespaces, client, protocol, port):
        mode = self.probes[-1].mode
        return "timeout" if mode == "interface-bound" and client == namespaces.topology.names[2] else "echo"

    def run_flow(self, observer=None):
        isolation.verify_paths(self.namespaces, Path("synthetic"), self.observations,
                               self.cleanup_errors, self.factory, observer or self.observer)

    def test_success_requires_both_baseline_paths_and_protected_after_each_negative(self):
        self.run_flow()
        self.assertEqual(len(self.observations), 18)
        baseline = [row for row in self.observations if row["mode"] == "ip-only"]
        self.assertTrue(all(row["observation"] == "echo" for row in baseline))
        self.assertEqual({row["socket"] for row in baseline if row["path"] == "wrong"}, set(isolation.PORT_FIELDS))
        self.assertEqual([(row["phase"], row["path"]) for row in self.observations[-3:]],
                         [("after", "protected")] * 3)
        self.assertTrue(all(probe.stopped for probe in self.probes))

    def test_failed_wrong_path_baseline_cannot_be_reported_as_isolation(self):
        def observe(ns, client, protocol, port):
            return "timeout" if client == ns.topology.names[2] else "echo"
        with self.assertRaisesRegex(isolation.IsolationError, "REQUIRED_PATH"):
            self.run_flow(observe)
        self.assertEqual([probe.mode for probe in self.probes], ["ip-only"])
        self.assertTrue(self.probes[0].stopped)

    def test_wrong_interface_echo_is_failure(self):
        with self.assertRaisesRegex(isolation.IsolationError, "WRONG_INTERFACE"):
            self.run_flow(lambda *args: "echo")
        self.assertTrue(all(probe.stopped for probe in self.probes))

    def test_lost_listener_cannot_pass_as_negative(self):
        def observe(ns, client, protocol, port):
            value = self.observer(ns, client, protocol, port)
            if value == "timeout":
                self.probes[-1].alive = False
            return value
        with self.assertRaisesRegex(isolation.IsolationError, "NOT_ALIVE"):
            self.run_flow(observe)

    def test_route_error_cannot_pass_as_negative(self):
        def observe(*args):
            value = self.observer(*args)
            return "network_error" if value == "timeout" else value
        with self.assertRaisesRegex(isolation.IsolationError, "WRONG_INTERFACE"):
            self.run_flow(observe)

    def test_protected_path_must_work_after_negative(self):
        def observe(*args):
            if len(self.observations) >= 15:
                return "timeout"
            return self.observer(*args)
        with self.assertRaisesRegex(isolation.IsolationError, "REQUIRED_PATH"):
            self.run_flow(observe)

    def test_probe_cleanup_failure_blocks_next_mode(self):
        old = self.factory.stop
        def stop(probe):
            old(probe)
            return False
        with patch.object(self.factory, "stop", stop):
            with self.assertRaisesRegex(isolation.IsolationError, "CLEANUP_FAILED"):
                self.run_flow()
        self.assertEqual(len(self.probes), 1)
        self.assertEqual(self.cleanup_errors, ["PROBE_CLEANUP_FAILED"])

    def test_ready_rejects_invalid_ports_mode_and_schema(self):
        row = {"schema": 1, "event": "READY", "mode": "ip-only",
               "tcp_port": 10001, "udp_port": 10002, "udp_ephemeral_port": 10003}
        self.assertEqual(isolation.parse_ready(json.dumps(row), "ip-only"), row)
        for update in ({"tcp_port": True}, {"udp_port": 0}, {"udp_ephemeral_port": 65536},
                       {"udp_ephemeral_port": 10002}, {"mode": "interface-bound"}, {"schema": True}):
            with self.subTest(update=update), self.assertRaisesRegex(isolation.IsolationError, "READY_INVALID"):
                isolation.parse_ready(json.dumps({**row, **update}), "ip-only")


class ReadyBoundaryTests(unittest.TestCase):
    class Selector:
        def __enter__(self):
            return self
        def __exit__(self, *args):
            pass
        def register(self, *args):
            pass
        def select(self, timeout):
            return [object()]

    def test_oversized_ready_is_rejected_before_json_parsing(self):
        process = type("Process", (), {"stdout": type("Pipe", (), {"fileno": lambda self: 9})()})()
        with patch.object(isolation.selectors, "DefaultSelector", self.Selector), \
             patch.object(isolation.os, "read", return_value=b"x" * (isolation.OUTPUT_LIMIT + 1)):
            with self.assertRaisesRegex(isolation.IsolationError, "READY_TOO_LARGE"):
                isolation.read_ready(process, "ip-only")

    def test_silent_ready_has_start_deadline(self):
        process = type("Process", (), {"stdout": object()})()
        with patch.object(isolation.selectors, "DefaultSelector", self.Selector), \
             patch.object(isolation.time, "monotonic", side_effect=[0, isolation.READY_TIMEOUT + 1]):
            with self.assertRaisesRegex(isolation.IsolationError, "READY_TIMEOUT"):
                isolation.read_ready(process, "ip-only")

    def test_eof_during_ready_is_failure(self):
        process = type("Process", (), {"stdout": type("Pipe", (), {"fileno": lambda self: 9})()})()
        with patch.object(isolation.selectors, "DefaultSelector", self.Selector), \
             patch.object(isolation.os, "read", return_value=b""):
            with self.assertRaisesRegex(isolation.IsolationError, "READY_EOF"):
                isolation.read_ready(process, "ip-only")

    def test_duplicate_ready_keys_are_rejected(self):
        data = '{"schema":1,"event":"READY","mode":"interface-bound","mode":"ip-only",' \
               '"tcp_port":10001,"udp_port":10002,"udp_ephemeral_port":10003}'
        with self.assertRaisesRegex(isolation.IsolationError, "DUPLICATE_JSON_KEY"):
            isolation.parse_ready(data, "ip-only")


class OwnedProcessTests(unittest.TestCase):
    def test_stop_escalates_only_the_owned_handle_with_bounded_waits(self):
        class HungChild:
            def __init__(self):
                self.stdin = io.BytesIO()
                self.stdout = io.BytesIO()
                self.events = []
                self.returncode = None
            def poll(self):
                return self.returncode
            def wait(self, timeout):
                self.events.append(("wait", timeout))
                if self.returncode is None:
                    raise subprocess.TimeoutExpired("owned", timeout)
                return self.returncode
            def terminate(self):
                self.events.append(("terminate",))
            def kill(self):
                self.events.append(("kill",))
                self.returncode = -9
        child = HungChild()
        probe = isolation.OwnedProbe(None, Path("synthetic"), "ip-only")
        probe.process = child
        self.assertFalse(probe.stop())
        self.assertTrue(probe.stopped)
        self.assertEqual(child.events, [("wait", 4), ("terminate",), ("wait", 2), ("kill",), ("wait", 2)])

    def test_command_timeout_is_fixed_diagnostic_and_not_a_success(self):
        with patch.object(subprocess, "run", side_effect=subprocess.TimeoutExpired("private detail", 8)):
            with self.assertRaisesRegex(isolation.IsolationError, "^COMMAND_TIMEOUT$"):
                isolation.checked_run(["ip", "netns", "add", "owned"])

    def test_privilege_denial_is_failed_receipt_without_namespace_creation(self):
        with patch.object(isolation, "preflight", side_effect=isolation.IsolationError("NET_ADMIN_AND_SYS_ADMIN_REQUIRED")), \
             patch.object(isolation, "OwnedNamespaces") as namespaces:
            receipt = isolation.run_verification(Path("unused"))
        namespaces.assert_not_called()
        self.assertFalse(receipt["interface_isolation_verified"])
        self.assertEqual(receipt["error"], "NET_ADMIN_AND_SYS_ADMIN_REQUIRED")
        self.assertTrue(all(receipt[key] is False for key in isolation.SCOPE))

    def test_namespace_cleanup_failure_cannot_produce_success_receipt(self):
        class Namespace:
            def __init__(self, *args):
                self.owned = {"synthetic": object()}
                self.deleted = []
                self.cleanup_errors = []
            def setup(self):
                pass
            def cleanup(self):
                self.cleanup_errors.append("NAMESPACE_CLEANUP_FAILED")
                return False
        with patch.object(isolation, "preflight", return_value="ip"), \
             patch.object(isolation, "probe_digest", return_value=(Path("synthetic"), "0" * 64)), \
             patch.object(isolation, "OwnedNamespaces", Namespace), \
             patch.object(isolation, "verify_paths"):
            receipt = isolation.run_verification(Path("synthetic"))
        self.assertFalse(receipt["interface_isolation_verified"])
        self.assertEqual(receipt["status"], "failed")
        self.assertEqual(receipt["cleanup_errors"], ["NAMESPACE_CLEANUP_FAILED"])


class RealClientTests(unittest.TestCase):
    def test_real_tcp_and_udp_echo_clients_validate_payload(self):
        # This checks the actual client with OS sockets on loopback. It does not
        # claim interface isolation: that belongs to the privileged CLI run.
        for protocol, kind in (("tcp", socket.SOCK_STREAM), ("udp", socket.SOCK_DGRAM)):
            with self.subTest(protocol=protocol), socket.socket(socket.AF_INET, kind) as listener:
                listener.bind(("127.0.0.1", 0))
                listener.settimeout(3)
                port = listener.getsockname()[1]
                if protocol == "tcp":
                    listener.listen(1)
                errors = []
                def respond():
                    try:
                        if protocol == "tcp":
                            peer, _ = listener.accept()
                            with peer:
                                peer.settimeout(3)
                                peer.sendall(peer.recv(128))
                        else:
                            data, peer = listener.recvfrom(128)
                            listener.sendto(data, peer)
                    except OSError as error:
                        errors.append(type(error).__name__)
                worker = threading.Thread(target=respond)
                worker.start()
                try:
                    self.assertEqual(isolation.observe_echo(protocol, "127.0.0.1", port, {"127.0.0.1"}), "echo")
                finally:
                    worker.join(timeout=4)
                self.assertFalse(worker.is_alive())
                self.assertEqual(errors, [])

    def test_real_udp_reply_from_wrong_port_is_not_echo(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener, \
             socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as unrelated:
            listener.bind(("127.0.0.1", 0))
            unrelated.bind(("127.0.0.1", 0))
            listener.settimeout(3)
            errors = []
            def respond():
                try:
                    data, peer = listener.recvfrom(128)
                    unrelated.sendto(data, peer)
                except OSError as error:
                    errors.append(type(error).__name__)
            worker = threading.Thread(target=respond)
            worker.start()
            try:
                self.assertEqual(isolation.observe_echo("udp", "127.0.0.1",
                    listener.getsockname()[1], {"127.0.0.1"}), "invalid_reply")
            finally:
                worker.join(timeout=4)
            self.assertFalse(worker.is_alive())
            self.assertEqual(errors, [])

    def test_internal_cli_rejects_outside_topology_addresses(self):
        with patch.object(isolation, "observe_echo") as observe:
            self.assertEqual(isolation.client_main(["--protocol", "udp", "--address", "192.0.2.1",
                "--port", "9999", "--allow-source", "192.0.2.1"]), 1)
        observe.assert_not_called()


if __name__ == "__main__":
    unittest.main()
