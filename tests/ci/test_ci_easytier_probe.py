"""Probe control contracts; no EasyTier, TUN, namespace or network is started."""
import copy
import asyncio
from dataclasses import asdict
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_easytier_probe as probe
import easytier_probe_worker as worker
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey


def candidate():
    return probe.RuntimeCandidateIdentity("8428a89d2dabc94c97d370ec607c6ca142473626",
        "1" * 64, "2" * 64, "3" * 64, "4" * 64, "5" * 64, 10000)


def profiles(transport="quic"):
    return probe.profile_pair(probe.ProbeTopology("0123456789abcdef", 7), transport, candidate(),
        [X25519PrivateKey.generate(), X25519PrivateKey.generate()])


class ContainmentTests(unittest.TestCase):
    def test_rules_bind_three_drop_chains_and_only_exact_pair_interfaces_and_ports(self):
        for transport in probe.TRANSPORTS:
            for profile in profiles(transport):
                rules = probe.containment_rules(profile)
                chains = [item["chain"] for item in rules if "chain" in item]
                self.assertEqual({item["name"] for item in chains}, {"input", "output", "forward"})
                self.assertTrue(all(item["policy"] == "drop" for item in chains))
                self.assertEqual(len([item for item in rules if "rule" in item]), 13)
                serialized = json.dumps(rules)
                self.assertIn(profile.remote.underlay_ipv4, serialized)
                self.assertIn(str(profile.remote.overlay.ip), serialized)
                self.assertNotIn("0.0.0.0/0", serialized)
                self.assertNotIn("masquerade", serialized)
                self.assertNotIn('"forward", "expr": [{"accept"', serialized)

    def test_full_readback_rejects_extra_accept_rule_policy_or_expression_changes(self):
        profile = profiles()[0]
        expected = probe.containment_rules(profile)
        normalized = probe.normalize_nft(expected)[0]
        accepted = copy.deepcopy(expected)
        for index, wrapper in enumerate(accepted):
            value = next(iter(wrapper.values()))
            value["handle"] = index + 1
            if "rule" in wrapper and value["expr"][-1] == {"drop": None}:
                value["expr"][0] = {"counter": {"packets": 3, "bytes": 120}}
        accepted.insert(0, {"metainfo": {"version": "test"}})
        actual, counters = probe.normalize_nft(accepted)
        self.assertEqual(actual, normalized)
        self.assertEqual(counters, dict(input=3, output=3, forward=3))
        mutations = []
        extra = copy.deepcopy(expected)
        extra.append({"rule": {"family": "inet", "table": "ffprobe", "chain": "output", "expr": [{"accept": None}]}})
        mutations.append(extra)
        policy = copy.deepcopy(expected); policy[1]["chain"]["policy"] = "accept"; mutations.append(policy)
        endpoint = copy.deepcopy(expected)
        endpoint[4]["rule"]["expr"][1]["match"]["right"] = "0.0.0.0/0"; mutations.append(endpoint)
        reordered = copy.deepcopy(expected)
        first = next(i for i, value in enumerate(reordered) if "rule" in value and value["rule"]["chain"] == "input")
        last = next(i for i, value in enumerate(reordered) if "rule" in value and value["rule"]["chain"] == "input" and value["rule"]["expr"][-1] == {"drop": None})
        reordered[first], reordered[last] = reordered[last], reordered[first]; mutations.append(reordered)
        for changed in mutations:
            with self.subTest(changed=changed):
                self.assertNotEqual(probe.normalize_nft(changed)[0], normalized)
        for malformed in ([{"set": {}}], [{"rule": {"expr": [None]}}],
                          [{"rule": {"chain": "input", "expr": [{"counter": {"packets": True, "bytes": 0}}]}}]):
            with self.assertRaises(probe.ProbeError):
                probe.normalize_nft(malformed)

    def test_default_routes_are_rejected_in_either_family(self):
        namespaces = probe.ProbeNamespaces("ip", probe.ProbeTopology("0123456789abcdef", 1))
        for route in ("default", "0.0.0.0/0", "::/0"):
            with patch.object(namespaces, "command", return_value=probe.encoded([{"dst": route}])), self.assertRaisesRegex(probe.ProbeError, "DEFAULT_ROUTE_FORBIDDEN"):
                namespaces.verify_routes("unused")
        with patch.object(namespaces, "command", return_value=b"[]") as command:
            namespaces.verify_routes("owned")
            self.assertEqual(command.call_count, 2)

    def test_topology_rejects_unowned_or_shell_shaped_names(self):
        for token, subnet in (("../x", 1), ("a" * 16, True), ("a" * 16, 256)):
            with self.assertRaises(probe.ProbeError):
                probe.ProbeTopology(token, subnet)

    def test_mismatch_codes_explain_shapes_without_accepting_or_echoing_unknown_values(self):
        expected = probe.normalize_nft(probe.containment_rules(profiles()[0]))[0]
        changed = copy.deepcopy(expected)
        state_match = next(expression["match"] for item in changed if "rule" in item
            for expression in item["rule"]["expr"] if "match" in expression and "ct" in expression["match"]["left"])
        state_match["op"] = "in"
        codes = probe.nft_difference_codes(expected, changed)
        self.assertTrue(any(code.endswith("MATCH_OP_EQ_TO_IN") for code in codes))
        self.assertNotEqual(expected, changed)  # Diagnostics do not relax equality.
        changed[0]["table"]["secret-unknown-key"] = "private-secret-value"
        changed[1]["chain"]["name"] = "private-namespace-name"
        codes = probe.nft_difference_codes(expected, changed)
        self.assertNotIn("secret", json.dumps(codes))
        self.assertNotIn("private", json.dumps(codes))
        self.assertLessEqual(len(codes), 16)
        for code in codes: self.assertRegex(code, r"^NFT_[A-Z0-9_]{1,156}$")
        codes = probe.nft_difference_codes([0] * 1000, [1] * 1000)
        self.assertEqual(len(codes), 16)

    def test_containment_only_never_loads_or_starts_runtime_and_cleans_failures(self):
        created = []
        class Namespaces:
            def __init__(self, _ip, topology, **_):
                self.topology, self.owned, self.deleted, self.cleanup_errors = topology, {}, [], []
                created.append(self)
            def setup_pair(self): self.owned = dict.fromkeys(self.topology.names)
            def containment(self, *_args, **_kwargs):
                raise probe.ContainmentMismatch(["NFT_I6_RULE_EXPR_COUNT_6_TO_5"])
            def cleanup(self): self.deleted = list(self.owned); self.owned.clear()
        with patch.object(probe, "preconditions", return_value=[]), patch.object(probe, "checked_checkout", return_value="1" * 40), patch.object(probe, "ProbeNamespaces", Namespaces), patch.object(probe, "NamespaceWorker") as worker, patch.object(probe, "load_build") as load:
            receipt = probe.run_containment()
        self.assertEqual(receipt["schema"], probe.CONTAINMENT_SCHEMA)
        self.assertEqual(receipt["status"], "failed")
        self.assertFalse(receipt["runtime_executed"])
        self.assertFalse(receipt["production_admission"])
        self.assertEqual(receipt["transports"][0]["namespaces_created"], 2)
        self.assertEqual(receipt["transports"][0]["namespaces_removed"], 2)
        self.assertEqual(receipt["transports"][0]["fixed_diagnostics"], ["NFT_I6_RULE_EXPR_COUNT_6_TO_5"])
        worker.assert_not_called(); load.assert_not_called()

    def test_typed_ports_keep_protocol_and_exact_fields_while_portless_rules_keep_meta(self):
        for transport in probe.TRANSPORTS:
            for profile in profiles(transport):
                rules = probe.containment_rules(profile)
                port_rules, portless_rules = [], []
                for wrapper in rules:
                    if "rule" not in wrapper or wrapper["rule"]["expr"][-1] != {"accept": None}:
                        continue
                    expressions = wrapper["rule"]["expr"]
                    port_matches = [item["match"] for item in expressions if "match" in item
                        and item["match"]["left"].get("payload", {}).get("protocol") in ("tcp", "udp")]
                    meta = [item["match"] for item in expressions if "match" in item
                        and item["match"]["left"].get("meta", {}).get("key") == "l4proto"]
                    if port_matches:
                        self.assertEqual(len(port_matches), 1)
                        self.assertEqual(meta, [])
                        selector = port_matches[0]
                        self.assertIn(selector["left"]["payload"]["field"], ("sport", "dport"))
                        self.assertIs(type(selector["right"]), int)
                        port_rules.append(wrapper)
                    else:
                        self.assertEqual(len(meta), 1)
                        self.assertIn(meta[0]["right"], ("tcp", "udp"))
                        portless_rules.append(wrapper)
                self.assertEqual((len(port_rules), len(portless_rules)), (6, 4))
                normalized = probe.normalize_nft(rules)[0]
                # A changed protocol, field or exact port is never accepted as
                # presentation normalization; no generic predicate is ignored.
                for mutation in ("protocol", "field", "port"):
                    changed = copy.deepcopy(rules)
                    selector = next(item["match"] for row in changed if "rule" in row
                        for item in row["rule"]["expr"] if "match" in item
                        and item["match"]["left"].get("payload", {}).get("protocol") == "tcp")
                    if mutation == "protocol": selector["left"]["payload"]["protocol"] = "udp"
                    elif mutation == "field": selector["left"]["payload"]["field"] = "dport" if selector["left"]["payload"]["field"] == "sport" else "sport"
                    else: selector["right"] += 1
                    self.assertNotEqual(probe.normalize_nft(changed)[0], normalized)


class ReceiptAndControlTests(unittest.TestCase):
    def test_real_guard3_receipt_and_older_or_incomplete_identity(self):
        lock = json.loads((ROOT / "scripts/easytier-source.lock.json").read_bytes())
        receipt = json.loads((ROOT / "docs/evidence/easytier-clean-linux-build-guard3.json").read_bytes())
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "build.json"
            path.write_bytes(probe.encoded(receipt))
            result, identity = probe.load_build(path, lock)
            self.assertEqual(identity.executable_sha256, receipt["built_executables"]["easytier-core"]["sha256"])
            self.assertEqual(sum(item.get("passed", 0) for item in result["runs"]), 35)
            for change in ("patch", "missing", "skipped", "duplicate", "runtime"):
                wrong = copy.deepcopy(receipt)
                if change == "patch": wrong["patch_manifest_sha256"] = "0" * 64
                elif change == "missing": wrong["runs"].pop(0)
                elif change == "skipped": wrong["runs"][0]["ignored"] = 1
                elif change == "duplicate": wrong["runs"][1] = wrong["runs"][0]
                else: wrong["runtime_executed"] = True
                path.write_bytes(probe.encoded(wrong))
                with self.subTest(change=change), self.assertRaises(probe.ProbeError):
                    probe.load_build(path, lock)
            old = json.loads((ROOT / "docs/evidence/easytier-clean-linux-build-guard2.json").read_bytes())
            path.write_bytes(probe.encoded(old))
            with self.assertRaises(probe.ProbeError): probe.load_build(path, lock)

    def test_precondition_failure_never_touches_builds_processes_or_namespaces(self):
        with patch.object(probe, "preconditions", return_value=["EXISTING_TUN_REQUIRED"]), patch.object(probe, "bounded_command") as command, patch.object(probe, "run_transport") as transport:
            receipt = probe.run(None)
        command.assert_not_called(); transport.assert_not_called()
        self.assertEqual(receipt["status"], "failed")
        self.assertEqual(receipt["transports"], [])
        self.assertFalse(receipt["production_admission"])

    def test_malformed_build_shapes_and_boolean_counts_have_fixed_failure_codes(self):
        lock = json.loads((ROOT / "scripts/easytier-source.lock.json").read_bytes())
        receipt = json.loads((ROOT / "docs/evidence/easytier-clean-linux-build-guard3.json").read_bytes())
        bad = [[], None]
        for field, value in (("runs", [None] * 7), ("built_executables", []),
                             ("built_executables", {"easytier-core": None, "easytier-cli": {}})):
            malformed = copy.deepcopy(receipt); malformed[field] = value; bad.append(malformed)
        for field, value in (("exit_code", False), ("passed", True), ("failed", False), ("ignored", False), ("stage", [])):
            malformed = copy.deepcopy(receipt); malformed["runs"][0][field] = value; bad.append(malformed)
        malformed = copy.deepcopy(receipt); malformed["built_executables"]["easytier-cli"]["bytes"] = True; bad.append(malformed)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "build.json"
            for value in bad:
                path.write_bytes(probe.encoded(value))
                with self.subTest(value=value), self.assertRaises(probe.ProbeError):
                    probe.load_build(path, lock)

    def test_negative_traffic_rejects_dead_infrastructure_and_unexpected_success(self):
        from unittest.mock import Mock
        client, server = Mock(), Mock()
        server.command.return_value = dict(tun_owned=True, probe_alive=True)
        client.command.return_value = dict(event="echo", outcomes={key: "timeout" for key in probe.PORT_FIELDS})
        ports = dict(zip(probe.PORT_FIELDS, (12001, 12002, 12003)))
        self.assertEqual(set(probe.echo(client, server, ports, False).values()), {"timeout"})
        for field in ("tun_owned", "probe_alive"):
            server.command.return_value = {"tun_owned": True, "probe_alive": True}
            server.command.return_value[field] = False
            with self.assertRaisesRegex(probe.ProbeError, "SERVER_INFRASTRUCTURE_UNHEALTHY"):
                probe.echo(client, server, ports, False)
        server.command.return_value = dict(tun_owned=True, probe_alive=True)
        client.command.return_value["outcomes"]["tcp_port"] = "echo"
        with self.assertRaisesRegex(probe.ProbeError, "OVERLAY_TRANSFER_CONTRACT_FAILED"):
            probe.echo(client, server, ports, False)

    def test_json_and_diagnostics_do_not_echo_arbitrary_secret_values(self):
        for raw in (b'{"a":1,"a":2}', b'{"a":NaN}', b'x' * 65537):
            with self.assertRaises(probe.ProbeError): probe.json_bytes(raw, 65536)
        self.assertEqual(probe.diagnostic(OSError("private-secret-value")), "DEVELOPMENT_PROBE_FAILED")
        self.assertEqual(probe.diagnostic(probe.ProbeError("private-secret-value")), "DEVELOPMENT_PROBE_FAILED")

    def test_cleanup_attempts_every_owned_handle_after_an_earlier_failure(self):
        from unittest.mock import Mock, AsyncMock
        core, native, first, last = Mock(), Mock(), Mock(), Mock()
        core.stop = AsyncMock(side_effect=worker.LifecycleError("SYNTHETIC_CORE_FAILURE"))
        native.close.side_effect = worker.ProbeError("SYNTHETIC_PROBE_FAILURE")
        last.close.side_effect = OSError("private-message")
        with self.assertRaisesRegex(probe.ProbeError, "WORKER_CHILD_CLEANUP_FAILED"):
            asyncio.run(worker.cleanup_children(core, native, [first, last]))
        native.close.assert_called_once_with(graceful=True)
        core.stop.assert_awaited_once()
        first.close.assert_called_once(); last.close.assert_called_once()

    def test_matrix_uses_same_server_and_reaps_every_phase_even_when_wrong_pin_transfers(self):
        for wrongly_transfers in (False, True):
            instances, namespace_instances = [], []
            class Namespaces:
                def __init__(self, _ip, topology, **_):
                    self.topology, self.owned, self.deleted, self.cleanup_errors = topology, {}, [], []
                    namespace_instances.append(self)
                def setup_pair(self): self.owned = dict.fromkeys(self.topology.names)
                def containment(self, *_args, **_kwargs): return dict(input=0, output=0, forward=0)
                def cleanup(self): self.deleted = list(self.owned); self.owned.clear()
            class Worker:
                def __init__(self, _namespaces, _name, manifest):
                    self.profile, self.name = manifest
                    self.stopped, self.core_stopped, self.probe_started = False, False, False
                    instances.append(self)
                def stop(self): self.stopped = True; return True
                def command(self, action, **_):
                    if action == "observe":
                        return dict(event="observation", tun_owned=not self.core_stopped,
                            peer_authenticated="wrong" not in self.name, probe_alive=self.probe_started)
                    if action == "start-probe":
                        self.probe_started = True
                        return dict(event="probe-ready", **dict(zip(probe.PORT_FIELDS, (12001, 12002, 12003))))
                    if action == "stop-core":
                        self.core_stopped = True
                        return dict(event="core-stopped", core_reaped=True, tun_absent=True)
                    if action == "echo":
                        server = instances[0]
                        allowed = not server.core_stopped and ("wrong" not in self.name or wrongly_transfers)
                        return dict(event="echo", outcomes={key: "echo" if allowed else "timeout" for key in probe.PORT_FIELDS})
                    raise AssertionError(action)
            def public_only_manifest(_stage, name, _namespaces, profile, *_):
                return profile, name
            ticks = iter(range(1000))
            with patch.object(probe, "ProbeNamespaces", Namespaces), patch.object(probe, "NamespaceWorker", Worker), patch.object(probe, "manifest", side_effect=public_only_manifest), patch.object(probe.time, "monotonic", side_effect=lambda: next(ticks)), patch.object(probe.time, "sleep"):
                result = probe.run_transport("quic", candidate(), {}, Path("unused"), "ip", "nft")
            self.assertTrue(all(item.stopped for item in instances))
            self.assertEqual(result["namespaces_created"], 2)
            self.assertEqual(result["namespaces_removed"], 2)
            self.assertEqual(len([item for item in instances if item.profile.role == "responder"]), 1)
            if wrongly_transfers:
                self.assertEqual(result["status"], "failed")
                self.assertEqual(result["error"], "OVERLAY_TRANSFER_CONTRACT_FAILED")
            else:
                self.assertEqual(result["status"], "owned_overlay_probe_verified")
                self.assertEqual([row["phase"] for row in result["phases"]], ["correct-pin-before", "wrong-pin", "correct-pin-after"])
                self.assertGreaterEqual(len(result["phases"][1]["echo_rounds"]), 2)
                self.assertFalse(result["core_exit"]["production_native_lease_exercised"])


@unittest.skipUnless(sys.platform == "linux", "Linux ELF/pidfd ownership contract")
class VerifiedPipeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="ff-probe-unittest-", dir="/tmp")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def image(self, name):
        source = Path(shutil.which(name)).resolve()
        content = source.read_bytes()
        destination = self.root / name
        value = probe.private_copy(source, destination, dict(bytes=len(content), sha256=probe.sha(content)))
        image = probe.VerifiedExecutable(destination, value["sha256"], value["bytes"])
        self.addCleanup(image.close)
        return image

    def test_bounded_fdexec_returns_exact_stdout_and_reaps(self):
        image = self.image("printf")
        child = worker.VerifiedPipeChild(image, ("synthetic-%s", "response"))
        process = child.process
        try:
            self.assertEqual(child.read(limit=64, timeout=2), b"synthetic-response")
        finally:
            child.close()
        self.assertIsNotNone(process.returncode)
        self.assertEqual(child.pidfd, -1)

    def test_overflow_and_timeout_kill_and_reap_exact_child(self):
        for name, args, expected in (("yes", (), "PIPE_OUTPUT_LIMIT"), ("sleep", ("10",), "PIPE_READ_TIMEOUT")):
            image = self.image(name)
            child = worker.VerifiedPipeChild(image, args)
            process = child.process
            try:
                with self.assertRaisesRegex(probe.ProbeError, expected):
                    child.read(limit=64, timeout=0.1)
            finally:
                child.close()
            self.assertIsNotNone(process.returncode)
            with self.assertRaises(ChildProcessError): os.waitpid(process.pid, os.WNOHANG)

    def test_pidfd_acquisition_failure_does_not_release_or_leak_bootstrap(self):
        image = self.image("sleep")
        original = worker.subprocess.Popen
        created = []
        def remember(*args, **kwargs):
            child = original(*args, **kwargs); created.append(child); return child
        with patch.object(worker.subprocess, "Popen", side_effect=remember), patch.object(worker.os, "pidfd_open", side_effect=OSError("synthetic")):
            with self.assertRaisesRegex(probe.ProbeError, "PIPE_CHILD_START_FAILED"):
                worker.VerifiedPipeChild(image, ("10",))
        self.assertEqual(len(created), 1)
        self.assertIsNotNone(created[0].returncode)
        self.assertNotEqual(created[0].returncode, 0)
        with self.assertRaises(ChildProcessError): os.waitpid(created[0].pid, os.WNOHANG)

    def test_private_copy_refuses_wrong_hash_symlink_and_overwrite(self):
        image = self.image("printf")
        identity = dict(bytes=image.identity.size, sha256=image.identity.sha256)
        with self.assertRaises(FileExistsError):
            probe.private_copy(image.path, image.path, identity)
        wrong = dict(identity, sha256="0" * 64)
        with self.assertRaisesRegex(probe.ProbeError, "IMAGE_HASH_MISMATCH"):
            probe.private_copy(image.path, self.root / "wrong", wrong)
        link = self.root / "symlink"; link.symlink_to(image.path)
        with self.assertRaises(OSError): probe.private_copy(link, self.root / "linked", identity)
        self.assertFalse((self.root / "wrong").exists())
        self.assertFalse((self.root / "linked").exists())

    def test_host_tool_timeout_overflow_and_input_deadline(self):
        for command, payload, code in ((["sleep", "10"], None, "NAMESPACE_TOOL_TIMEOUT"),
                                      (["yes"], None, "NAMESPACE_TOOL_OUTPUT_LIMIT"),
                                      (["sleep", "10"], b"x" * 32768, "NAMESPACE_TOOL_TIMEOUT")):
            with self.assertRaisesRegex(probe.ProbeError, code):
                probe.bounded_command(command, payload=payload, timeout=0.1, limit=64)

    def test_sanitized_receipt_readable_under_private_umask_without_changing_parent(self):
        target = self.root / "receipt.json"
        previous = os.umask(0o077)
        try:
            probe.write_receipt(target, {"status": "failed", "production_admission": False})
        finally:
            os.umask(previous)
        self.assertEqual(target.stat().st_mode & 0o777, 0o644)
        self.assertEqual(self.root.stat().st_mode & 0o777, 0o700)
        with self.assertRaises(FileExistsError): probe.write_receipt(target, {})


if __name__ == "__main__":
    unittest.main()
