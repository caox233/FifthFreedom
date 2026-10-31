"""Probe control contracts; no EasyTier, TUN, namespace or network is started."""
import copy
import asyncio
from dataclasses import asdict
import json
import io
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


def readiness(tun_owned, authenticated):
    return dict(schema=worker.READINESS_SCHEMA, core_image_matches=True, tun_owned=tun_owned,
        peer_authenticated=authenticated, query_state="authenticated" if authenticated else "parse-rejected" if tun_owned else "tun-pending",
        query_code=None if authenticated or not tun_owned else "PEER_SET_EMPTY",
        local_bind_kind="exact" if authenticated else None)


class IsolatedEntryTests(unittest.TestCase):
    def invoke(self, script, *arguments, directory):
        # The workflow uses isolation. Neither CWD nor PYTHONPATH may supply
        # imports; only explicitly anchored repository paths are available.
        env = dict(os.environ, PYTHONPATH=str(directory), PYTHONHOME=str(directory / "absent-home"))
        env.pop("GITHUB_ACTIONS", None)
        env.pop("RUNNER_ENVIRONMENT", None)
        return subprocess.run([sys.executable, "-I", "-B", str(ROOT / "scripts" / script), *arguments],
            cwd=directory, env=env, capture_output=True, text=True, timeout=10)

    def test_exact_isolated_probe_help_ignores_ambient_import_paths(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            (directory / "ci_socket_isolation.py").write_text("raise RuntimeError('ambient module must not run')\n")
            result = self.invoke("ci_easytier_probe.py", "--help", directory=directory)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--containment-only", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_exact_isolated_probe_refuses_missing_hosted_authorization_before_namespace(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            output = directory / "public-receipt.json"
            result = self.invoke("ci_easytier_probe.py", "--containment-only", "--output", str(output), directory=directory)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertEqual(result.stderr, "")
            receipt = json.loads(output.read_bytes())
        self.assertEqual(result.stdout, "EASYTIER_CONTAINMENT_FAILED\n")
        self.assertEqual(receipt["schema"], probe.CONTAINMENT_SCHEMA)
        self.assertEqual(receipt["transports"], [])
        self.assertFalse(receipt["runtime_executed"])
        self.assertFalse(receipt["production_admission"])
        self.assertIn("GITHUB_HOSTED_REQUIRED" if sys.platform == "linux" else "LINUX_REQUIRED", receipt["precondition_failures"])

    def test_exact_isolated_worker_missing_arguments_is_fixed_failure(self):
        with tempfile.TemporaryDirectory() as name:
            result = self.invoke("easytier_probe_worker.py", directory=Path(name))
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stderr, "EASYTIER_DEVELOPMENT_WORKER_FAILED\n")
        value = json.loads(result.stdout)
        self.assertTrue(worker.valid_failure_record(value))
        self.assertEqual(value["phase"], "arguments")
        self.assertEqual(value["code"], "WORKER_ARGUMENTS_INVALID")
        self.assertTrue(value["cleanup_reaped"])

    def test_windows_refusal_receipt_needs_no_posix_mode_api(self):
        with tempfile.TemporaryDirectory() as name:
            output = Path(name) / "refusal.json"
            receipt = {"status": "failed", "precondition_failures": ["LINUX_REQUIRED"]}
            with patch.object(probe.sys, "platform", "win32"), patch.object(probe.os, "fchmod", create=True) as mode:
                probe.write_receipt(output, receipt)
                mode.assert_not_called()
            self.assertEqual(json.loads(output.read_bytes()), receipt)
            with self.assertRaises(FileExistsError):
                probe.write_receipt(output, {"overwrite": "forbidden"})


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
    def test_readiness_timeout_preserves_last_fixed_observation_without_raw_rpc(self):
        from unittest.mock import Mock
        endpoint = Mock()
        observations = [readiness(False, False), readiness(True, False)]
        endpoint.command.side_effect = [dict(event="observation", tun_owned=value["tun_owned"],
            peer_authenticated=value["peer_authenticated"], readiness=value) for value in observations]
        with patch.object(probe.time, "monotonic", side_effect=[0, 1, 16]), patch.object(probe.time, "sleep"):
            with self.assertRaises(probe.ReadinessTimeout) as caught:
                probe.await_ready(endpoint)
        record = caught.exception.record
        self.assertTrue(probe.valid_readiness_timeout(record))
        self.assertEqual(record["observation_count"], 2)
        self.assertEqual(record["last_observation"], observations[-1])
        self.assertEqual(str(caught.exception), "OVERLAY_READY_TIMEOUT")
        self.assertFalse(probe.valid_readiness_timeout(dict(record, raw_rpc="secret")))
        self.assertFalse(probe.valid_readiness_timeout(dict(record, observation_count=True)))
        self.assertFalse(probe.valid_readiness_timeout(dict(record, last_observation=readiness(True, True))))
        self.assertFalse(probe.valid_readiness_timeout(dict(record, expected_authenticated=False)))
        for changed in (dict(observations[-1], query_code="private-secret"),
                        dict(observations[-1], extra="private-secret"),
                        dict(observations[-1], local_bind_kind="wildcard"),
                        dict(observations[-1], core_image_matches=1)):
            self.assertFalse(worker.valid_readiness_record(changed))
            endpoint.command.side_effect = None
            endpoint.command.return_value = dict(event="observation", tun_owned=True, peer_authenticated=False, readiness=changed)
            with self.assertRaisesRegex(probe.ProbeError, "OBSERVATION_INVALID"):
                probe.await_ready(endpoint)

    def test_authenticated_wildcard_bind_is_observation_not_source_address_proof(self):
        from unittest.mock import Mock
        observed = dict(readiness(True, True), local_bind_kind="wildcard")
        self.assertTrue(worker.valid_readiness_record(observed))
        endpoint = Mock()
        endpoint.command.return_value = dict(event="observation", tun_owned=True, peer_authenticated=True, readiness=observed)
        self.assertEqual(probe.await_ready(endpoint)["readiness"], observed)
        endpoint.command.return_value["tun_owned"] = False
        with self.assertRaisesRegex(probe.ProbeError, "OBSERVATION_INVALID"):
            probe.await_ready(endpoint)

    def test_worker_failure_frame_is_strict_and_never_echoes_unknown_messages(self):
        state = dict(phase="observe", cleanup_code=None)
        value = worker.failure_record(worker.LifecycleError("OVERLAY_TUN_NOT_OWNED"), state)
        self.assertTrue(worker.valid_failure_record(value))
        self.assertEqual(value["code"], "OVERLAY_TUN_NOT_OWNED")
        for error in (RuntimeError("SECRET"), worker.ProbeError("SECRET"), OSError("SECRET")):
            record = worker.failure_record(error, state)
            self.assertNotIn("SECRET", json.dumps(record))
            self.assertTrue(worker.valid_failure_record(record))
        for key, changed in (("schema", "unknown"), ("phase", "secret-phase"),
                             ("code", "SECRET"), ("cleanup_reaped", 1), ("cleanup_code", [])):
            self.assertFalse(worker.valid_failure_record(dict(value, **{key: changed})))
        self.assertFalse(worker.valid_failure_record(dict(value, extra="SECRET")))
        self.assertFalse(worker.valid_failure_record(dict(value, cleanup_reaped=False)))
        # The combined worker must preserve a failed pre-revocation held-TCP
        # control, including an independently failed cleanup, in this protocol.
        for cleanup in (None, "WORKER_CHILD_CLEANUP_FAILED"):
            held = worker.failure_record(worker.ProbeError("STACK_HELD_CONNECTION_NOT_OPEN"),
                dict(phase="parent-command", cleanup_code=cleanup))
            reopened = json.loads(json.dumps(held))
            self.assertTrue(worker.valid_failure_record(reopened))
            self.assertEqual(reopened["code"], "STACK_HELD_CONNECTION_NOT_OPEN")
            self.assertEqual(reopened["cleanup_reaped"], cleanup is None)
            self.assertEqual(reopened["cleanup_code"], cleanup)

    def test_combined_owner_fixed_codes_roundtrip_without_accepting_prefixes(self):
        import easytier_probe_worker as worker
        codes = """LIFECYCLE_LIMITS_INVALID
LIFECYCLE_OBSERVATION_FAILED
NAMESPACE_SOCKET_TABLE_TOO_LARGE
NATIVE_EXITED
NATIVE_IMAGE_CHANGED
NATIVE_LEASE_REVOCATION_FAILED
NATIVE_LISTENER_OWNERSHIP_INVALID
NATIVE_MONITOR_FAILED
NATIVE_OBSERVATION_INVALID
NATIVE_READINESS_LOST
OVERLAY_CORE_CHANGED
OVERLAY_CORE_PROFILE_MISMATCH
OVERLAY_EXITED
OVERLAY_LEASE_BINDING_INVALID
OVERLAY_MONITOR_ALREADY_PREPARED
OVERLAY_MONITOR_FAILED
OVERLAY_MONITOR_REVOKED
OVERLAY_MONITOR_SPEC_INVALID
OVERLAY_MONITOR_STATE_INVALID
OVERLAY_OBSERVATION_CHANGED
OVERLAY_OBSERVATION_INVALID
OVERLAY_OBSERVATION_STALE
OVERLAY_PEER_CHANGED
OVERLAY_PEER_LOST
OVERLAY_PREPARATION_FAILED
OVERLAY_RPC_CHANGED
OVERLAY_RPC_LOST
OWNED_OVERLAY_OBSERVATION_FAILED
OWNED_QUERY_FAILED
PUBLIC_OWNER_LEASE_INVALID
PUBLIC_PROFILE_NOT_READY
QUERY_BUDGET_INVALID
QUERY_CLOSED
QUERY_EXIT_FAILED
QUERY_IMAGE_CHANGED
QUERY_OUTPUT_LIMIT
QUERY_OUTPUT_UNAVAILABLE
QUERY_SPEC_INVALID
QUERY_STATE_INVALID
RPC_KERNEL_TABLE_INVALID
RPC_KERNEL_TABLE_LIMIT
RPC_LISTENER_NOT_OWNED_LOOPBACK
RPC_OWNER_CHANGED
RPC_OWNER_FD_LIMIT
RPC_OWNER_INVALID
RUNTIME_CANDIDATE_INVALID
STACK_CLIENT_CORE_LOST
STACK_CLIENT_NOT_AUTHENTICATED
STACK_CLOSE_BUDGET_INVALID
STACK_COMMAND_INVALID
STACK_COMMAND_LIMIT
STACK_FAULT_CONTROL_COMPLETE
STACK_HELD_CONNECTION_LIMIT
STACK_HELD_CONNECTION_TIMEOUT
STACK_NATIVE_LISTENER_REMAINS
STACK_NATIVE_PORT_CHECK_FAILED
STACK_NATIVE_WATCHDOG_EXIT_INVALID
STACK_ONLY_TCP_DEVELOPMENT
STACK_PAUSED_OVERLAY_CHANGED
STACK_PAUSE_ALREADY_REQUESTED
STACK_PAUSE_BUDGET_INVALID
STACK_PAUSE_EXPIRED
STACK_REVOKE_CLEANUP_INVALID
STACK_SOCKET_TABLE_INVALID
STACK_SOCKET_TABLE_LIMIT
STARTUP_TIMEOUT
STOP_REQUESTED
SUPERVISOR_ALREADY_STARTED
SUPERVISOR_CANCELLED
SUPERVISOR_START_FAILED
SUPERVISOR_STOPPED""".split()
        for code in codes:
            frame = worker.failure_record(worker.LifecycleError(code), dict(phase="observe", cleanup_code=None))
            self.assertTrue(worker.valid_failure_record(json.loads(json.dumps(frame))))
            self.assertEqual(frame["code"], code)
        for code in ("OVERLAY_SECRET_SENTINEL", "STACK_PRIVATE_PATH", "QUERY_UNKNOWN", "STOP_REQUESTED private"):
            frame = worker.failure_record(worker.LifecycleError(code), dict(phase="observe", cleanup_code=None))
            self.assertEqual(frame["code"], "WORKER_UNCLASSIFIED_ERROR")
            self.assertNotIn(code, json.dumps(frame))

    def test_kernel_query_frame_adds_only_current_failure_and_validated_profile_role(self):
        # Projection contracts use an explicitly synthetic extractor result;
        # production syscall classification is tested with the monitor itself.
        details = dict(query_stage="ipv4_address", errno_class="EADDRNOTAVAIL",
            previously_observed=True, link_up=True)
        error = worker.LifecycleError("OVERLAY_ADDRESS_QUERY_UNAVAILABLE")
        for profile in profiles():
            for cleanup in (None, "WORKER_CHILD_CLEANUP_FAILED"):
                state = dict(phase="observe", cleanup_code=cleanup, profile_role=profile.role)
                with patch.object(worker, "kernel_query_failure", return_value=details):
                    value = worker.failure_record(error, state)
                decoded = json.loads(json.dumps(value))
                self.assertTrue(worker.valid_failure_record(decoded))
                self.assertEqual(decoded["kernel_query"], dict(role=profile.role, **details))
                self.assertEqual(decoded["cleanup_reaped"], cleanup is None)
                self.assertEqual(decoded["code"], "OVERLAY_ADDRESS_QUERY_UNAVAILABLE")
        # A previous record in state is not authority for a later exception.
        state = dict(phase="observe", cleanup_code=None, profile_role="initiator", kernel_query=details)
        self.assertNotIn("kernel_query", worker.failure_record(error, state))
        with patch.object(worker, "kernel_query_failure", return_value=details):
            for changed in (dict(profile_role="other"), dict(profile_role=None), dict(phase="arguments")):
                self.assertNotIn("kernel_query", worker.failure_record(error, dict(state, **changed)))
            self.assertNotIn("kernel_query", worker.failure_record(worker.LifecycleError("OVERLAY_LINK_QUERY_UNAVAILABLE"), state))

    def test_kernel_query_frame_rejects_inconsistent_fields_types_or_unknown_values(self):
        value = dict(event="failed", schema=worker.FAILURE_SCHEMA, phase="observe",
            code="OVERLAY_ADDRESS_QUERY_UNAVAILABLE", cleanup_reaped=True, cleanup_code=None,
            kernel_query=dict(role="initiator", query_stage="ipv4_address", errno_class="EPERM",
                previously_observed=False, link_up=True))
        self.assertTrue(worker.valid_failure_record(value))
        for key, changed in (("role", "unknown"), ("role", True), ("query_stage", "link_flags"),
                ("query_stage", "private-stage"), ("errno_class", "EPERM: secret"),
                ("errno_class", 1), ("errno_class", "EADDRNOTAVAIL"),
                ("previously_observed", 1), ("link_up", 1), ("extra", "secret")):
            row = copy.deepcopy(value)
            row["kernel_query"][key] = changed
            self.assertFalse(worker.valid_failure_record(row), key)
        for key, changed in (("event", "observed"), ("phase", "arguments"),
                ("code", "OVERLAY_TUN_NOT_OWNED"), ("kernel_query", None)):
            self.assertFalse(worker.valid_failure_record(dict(value, **{key: changed})))
        for key in value["kernel_query"]:
            row = copy.deepcopy(value)
            del row["kernel_query"][key]
            self.assertFalse(worker.valid_failure_record(row))

    def test_source_failure_survives_successful_or_failed_independent_cleanup(self):
        from unittest.mock import Mock, AsyncMock
        profile = profiles()[1]
        codes = ("OVERLAY_TUN_NOT_OWNED", "OVERLAY_NAMESPACE_OBSERVATION_UNAVAILABLE",
            "OVERLAY_LINK_QUERY_UNAVAILABLE", "OVERLAY_ADDRESS_QUERY_UNAVAILABLE",
            "OVERLAY_FDINFO_DIRECTORY_UNAVAILABLE", "OVERLAY_FDINFO_LIST_UNAVAILABLE",
            "OVERLAY_FDINFO_ENTRY_UNAVAILABLE", "OVERLAY_FDINFO_READ_UNAVAILABLE",
            "OVERLAY_TUN_DESCRIPTOR_UNAVAILABLE", "OVERLAY_INTERFACE_RECHECK_UNAVAILABLE")
        for code, cleanup_fails in [(code, False) for code in codes] + [(codes[0], True), (codes[6], True)]:
            state = dict(phase="arguments", cleanup_code=None)
            handles = [Mock(), Mock(), Mock()]
            handles[0].identity.sha256 = profile.candidate.executable_sha256
            handles[0].identity.size = profile.candidate.executable_size
            core = Mock()
            core.image_matches.return_value = True
            core.stop = AsyncMock(side_effect=worker.LifecycleError("CHILD_STOP_TIMEOUT") if cleanup_fails else None)
            monitor = Mock()
            monitor.prepare = AsyncMock()
            monitor.observe = AsyncMock(side_effect=worker.LifecycleError(code))
            document = dict(profile=asdict(profile), config="/tmp/synthetic-private-config",
                **{role: dict(path="/tmp/synthetic-" + role, sha256="1" * 64, bytes=64) for role in ("core", "cli", "probe")})
            stdin = Mock(buffer=io.BytesIO(b'{"action":"observe"}\n'))
            with patch.object(worker, "VerifiedExecutable", side_effect=handles), patch.object(worker, "KernelTunMonitor", return_value=monitor), patch.object(worker.OwnedChild, "spawn", new=AsyncMock(return_value=core)), patch.object(worker.sys, "stdin", stdin), patch.object(worker, "_reply") as reply:
                with self.assertRaises(worker.WorkerFailure) as raised:
                    asyncio.run(worker.serve(document, state))
            record = raised.exception.record
            self.assertEqual((record["phase"], record["code"]), ("observe", code))
            self.assertEqual(state["profile_role"], profile.role)
            self.assertNotIn("kernel_query", record)  # A code string is not syscall evidence.
            self.assertEqual(record["cleanup_reaped"], not cleanup_fails)
            self.assertEqual(record["cleanup_code"], "WORKER_CHILD_CLEANUP_FAILED" if cleanup_fails else None)
            self.assertTrue(worker.valid_failure_record(record))
            core.stop.assert_awaited_once()
            for handle in handles: handle.close.assert_called_once()
            self.assertEqual(reply.call_count, 1)  # started, never a successful stop ACK

    def test_unconfirmed_query_cleanup_never_claims_all_children_reaped(self):
        from unittest.mock import Mock, AsyncMock
        profile = profiles()[1]
        state = dict(phase="arguments", cleanup_code=None)
        handles = [Mock(), Mock(), Mock()]
        handles[0].identity.sha256 = profile.candidate.executable_sha256
        handles[0].identity.size = profile.candidate.executable_size
        core = Mock(); core.image_matches.return_value = True; core.stop = AsyncMock()
        monitor = Mock(); monitor.prepare = AsyncMock(); monitor.observe = AsyncMock(return_value=Mock())
        query = Mock(); query.read.side_effect = worker.ProbeError("PIPE_READ_TIMEOUT")
        query.close.side_effect = worker.ProbeError("PIPE_CHILD_CLEANUP_TIMEOUT")
        document = dict(profile=asdict(profile), config="/tmp/synthetic-private-config",
            **{role: dict(path="/tmp/synthetic-" + role, sha256="1" * 64, bytes=64) for role in ("core", "cli", "probe")})
        stdin = Mock(buffer=io.BytesIO(b'{"action":"observe"}\n'))
        with patch.object(worker, "VerifiedExecutable", side_effect=handles), patch.object(worker, "KernelTunMonitor", return_value=monitor), patch.object(worker.OwnedChild, "spawn", new=AsyncMock(return_value=core)), patch.object(worker, "VerifiedPipeChild", return_value=query), patch.object(worker.sys, "stdin", stdin), patch.object(worker, "_reply"):
            with self.assertRaises(worker.WorkerFailure) as raised:
                asyncio.run(worker.serve(document, state))
        self.assertEqual((raised.exception.record["phase"], raised.exception.record["code"]), ("peer-query", "PIPE_READ_TIMEOUT"))
        self.assertFalse(raised.exception.record["cleanup_reaped"])
        self.assertEqual(raised.exception.record["cleanup_code"], "CLI_CHILD_CLEANUP_UNCONFIRMED")
        self.assertTrue(worker.valid_failure_record(raised.exception.record))
        query.close.assert_called_once(); core.stop.assert_awaited_once()
        for handle in handles: handle.close.assert_called_once()
        with patch.object(worker, "VerifiedPipeChild", side_effect=worker.ProbeError("PIPE_CHILD_START_FAILED")):
            with self.assertRaises(worker.QueryCleanupUnconfirmed): worker.bounded_query(None, ())

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
                            peer_authenticated="wrong" not in self.name, probe_alive=self.probe_started,
                            readiness=readiness(not self.core_stopped, "wrong" not in self.name))
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

    def test_actual_worker_failure_is_bounded_and_parent_reaps_without_second_ack(self):
        # This real worker is deliberately missing arguments: no namespace,
        # config, core, CLI, TUN or synthetic socket probe can be started.
        child = probe.NamespaceWorker.__new__(probe.NamespaceWorker)
        child.cleaned, child.worker_failure = False, None
        child.process = subprocess.Popen([sys.executable, "-I", "-B", str(ROOT / "scripts/easytier_probe_worker.py")],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
        child.pidfd = os.pidfd_open(child.process.pid)
        process = child.process
        try:
            with self.assertRaises(worker.WorkerFailure) as raised:
                child.read()
            self.assertEqual(raised.exception.record, dict(event="failed", schema=worker.FAILURE_SCHEMA,
                phase="arguments", code="WORKER_ARGUMENTS_INVALID", cleanup_reaped=True, cleanup_code=None))
            self.assertTrue(child.stop())
        finally:
            child.stop()
        self.assertEqual(process.returncode, 1)
        self.assertIsNone(child.process)
        with self.assertRaises(ChildProcessError): os.waitpid(process.pid, os.WNOHANG)

    def test_parent_rejects_extra_fields_in_failure_frame(self):
        child = probe.NamespaceWorker.__new__(probe.NamespaceWorker)
        child.cleaned, child.worker_failure = False, None
        value = worker.failure_record(worker.ProbeError("WORKER_COMMAND_INVALID"), dict(phase="parent-command", cleanup_code=None))
        value["extra"] = "private-input-must-not-be-forwarded"
        child.process = subprocess.Popen([sys.executable, "-I", "-S", "-c",
            "import sys; sys.stdout.write(sys.argv[1]); sys.stdout.flush()", json.dumps(value) + "\n"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
        child.pidfd = os.pidfd_open(child.process.pid)
        process = child.process
        try:
            with self.assertRaisesRegex(worker.ProbeError, "WORKER_FAILURE_FRAME_INVALID"):
                child.read()
            self.assertIsNone(child.worker_failure)
        finally:
            child.stop()
        self.assertIsNotNone(process.returncode)
        with self.assertRaises(ChildProcessError): os.waitpid(process.pid, os.WNOHANG)

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
