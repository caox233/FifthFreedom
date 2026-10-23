"""Synthetic receipt fixtures verify CI admission, not privileged runtime success."""
from contextlib import redirect_stdout
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_probe_result as probe
import ci_easytier_probe as overlay_producer
import ci_native_lifecycle as native_producer
import ci_owned_stack as stack_producer
import easytier_probe_worker as worker_producer


def native():
    return {"schema": 1, "status": "native_owned_lifecycle_verified",
        "scope": {"synthetic_runtime_bindings": True, "controlled_native_startup_test": True,
            "encrypted_overlay_verified": False, "production_provider_open": False, "game_executed": False,
            "complete_windows_client": False, "deployed": False, "published": False},
        "cases": [{"case": case, "native_reaped": True, **({"no_state_created": True} if case in ("withheld", "denied")
            else {"actual_listener_count": 5, "protected_http_and_tcp_verified": True, "wrong_interface_rejected": True,
                "established_connection_closed": True, "all_five_ports_released": True, "native_exit": 92})}
            for case in native_producer.CASES],
        "copied_package_native_verified": True, "source_package_unchanged": True, "private_tree_removed": True,
        "all_owned_children_reaped": True, "no_default_routes": True, "owned_namespaces_removed": 3}


def overlay():
    def phase(name):
        return {"phase": name, "profile_sha256": "a" * 64, **({"echo_rounds": [dict.fromkeys(overlay_producer.PORT_FIELDS, "timeout")] * 2,
            "minimum_observation_seconds": 8, "tun_owned": True, "peer_authenticated": False} if name == "wrong-pin"
            else {"echo": dict.fromkeys(overlay_producer.PORT_FIELDS, "echo")})}
    return {"schema": overlay_producer.SCHEMA, "status": "isolated_development_matrix_verified", "precondition_failures": [],
        "private_stage_removed": True, "runtime_binary_uploaded": False, "raw_runtime_output_retained": False,
        "game_executed": False, "production_admission": False, "native_owner_lease_verified": False,
        "transports": [{"transport": transport, "status": "owned_overlay_probe_verified", "cleanup_errors": [],
            "namespaces_created": 2, "namespaces_removed": 2,
            "phases": [phase(name) for name in ("correct-pin-before", "wrong-pin", "correct-pin-after")],
            "core_exit": {"core_reaped": True, "tun_absent": True, "new_traffic_denied": True, "production_native_lease_exercised": False},
            "blocked_packets": {role: dict.fromkeys(("input", "output", "forward"), 0) for role in ("client", "server")}}
            for transport in overlay_producer.TRANSPORTS]}


def owned_stack():
    return {"schema": stack_producer.SCHEMA, "status": "owned_stack_development_verified",
        "scope": dict(stack_producer.SCOPE), "precondition_failures": [],
        "copied_package_native_verified": True, "source_package_unchanged": True, "private_stage_removed": True,
        "cases": [{"case": case, "status": "owned_stack_case_verified", "cleanup_errors": [],
            "namespaces_created": 2, "namespaces_removed": 2, "actual_native_ready": True,
            "owned_listeners": 5, "established_tcp": True, "runtime_profile_sha256": "a" * 64,
            "containment_after": [dict.fromkeys(("input", "output", "forward"), 0) for _ in range(2)],
            **({"native_watchdog_exit": 92, "ports_rebound": 5, "held_tcp_eof_or_reset": True,
                "overlay_alive_during_close": True} if case == "lease-revoke" else
               {"exact_peer_reaped": True, "monitor_permanently_revoked": True, "native_reaped": True,
                "native_listeners_remaining": 0, "remote_tcp_eof_claimed": False})}
            for case in stack_producer.CASES]}


class ProbeResultTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-probe-result-tests-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.receipt = self.root / "receipt.json"
        self.error_log = self.root / "stderr.txt"
        self.error_log.write_text("PRIVATE_RAW_ERROR\n")

    def inspect(self, kind, value, exit_code=0):
        self.receipt.write_text(json.dumps(value))
        output = io.StringIO()
        with redirect_stdout(output):
            result = probe.inspect(kind, exit_code, self.receipt, self.error_log)
        self.assertNotIn("PRIVATE", output.getvalue())
        return result, output.getvalue()

    def test_matching_success_receipts_need_successful_process(self):
        for kind, value in (("native", native()), ("easytier", overlay()), ("owned-stack", owned_stack())):
            self.assertEqual(self.inspect(kind, value), (0, ""))
            self.assertEqual(self.inspect(kind, value, exit_code=1)[0], 1)

    def test_owned_stack_requires_both_controls_actual_positive_and_cleanup(self):
        for index in (0, 1):
            for key, invalid in (("actual_native_ready", False), ("established_tcp", False),
                    ("owned_listeners", 4), ("owned_listeners", True), ("namespaces_removed", 1),
                    ("cleanup_errors", ["STACK_WORKER_CLEANUP_FAILED"]), ("runtime_profile_sha256", "PRIVATE"),
                    ("containment_after", []), ("containment_after", [{"input": -1, "output": 0, "forward": 0}] * 2)):
                value = owned_stack(); value["cases"][index][key] = invalid
                with self.subTest(case=index, key=key):
                    self.assertEqual(self.inspect("owned-stack", value)[0], 1)
        for cases in ([], list(reversed(owned_stack()["cases"])), owned_stack()["cases"][:1]):
            value = owned_stack(); value["cases"] = cases
            self.assertFalse(probe.verified("owned-stack", value))

    def test_owned_stack_requires_real_eof_with_overlay_alive_and_sticky_peer_loss(self):
        for index, key, invalid in ((0, "native_watchdog_exit", 0), (0, "ports_rebound", 4),
                (0, "held_tcp_eof_or_reset", False), (0, "overlay_alive_during_close", False),
                (1, "exact_peer_reaped", False), (1, "monitor_permanently_revoked", False),
                (1, "native_reaped", False), (1, "native_listeners_remaining", False),
                (1, "native_listeners_remaining", 1), (1, "remote_tcp_eof_claimed", True)):
            value = owned_stack(); value["cases"][index][key] = invalid
            with self.subTest(case=index, key=key):
                self.assertFalse(probe.verified("owned-stack", value))

    def test_owned_stack_preserves_privacy_admission_and_distinct_scope(self):
        for key in ("copied_package_native_verified", "source_package_unchanged", "private_stage_removed"):
            value = owned_stack(); value[key] = False
            self.assertFalse(probe.verified("owned-stack", value))
        for key in ("production_admission", "ordinary_public_enabled", "game_executed",
                    "complete_client_package", "runtime_binary_uploaded", "raw_runtime_output_retained"):
            for invalid in (True, 0):
                value = owned_stack(); value["scope"][key] = invalid
                self.assertFalse(probe.verified("owned-stack", value))
        for key, invalid in (("transport", "quic"), ("purpose", "PRIVATE")):
            value = owned_stack(); value["scope"][key] = invalid
            self.assertFalse(probe.verified("owned-stack", value))
        value = owned_stack(); value["precondition_failures"] = ["EXISTING_TUN_REQUIRED"]
        self.assertFalse(probe.verified("owned-stack", value))
        self.assertFalse(probe.verified("easytier", owned_stack()))

    def test_owned_stack_failure_diagnostics_only_export_fixed_codes_and_nft_shapes(self):
        value = {"error": "STACK_CASES_INCOMPLETE", "cases": [{"error": "STACK_STATE_TIMEOUT /PRIVATE_PATH",
            "fixed_diagnostics": ["NFT_I0_RULE_EXPR_COUNT_6_TO_5", "NFT_PRIVATE_VALUE"]}],
            "private": "STACK_STATE_INVALID_PRIVATE_SUFFIX"}
        result, output = self.inspect("owned-stack", value, 1)
        self.assertEqual(result, 1)
        self.assertIn("STACK_CASES_INCOMPLETE", output)
        self.assertIn("STACK_STATE_TIMEOUT", output)
        self.assertIn("NFT_I0_RULE_EXPR_COUNT_6_TO_5", output)
        self.assertNotIn("STACK_STATE_INVALID", output)

    def test_worker_failure_diagnostics_require_exact_source_enums_and_cleanup_consistency(self):
        record = {"event": "failed", "schema": worker_producer.FAILURE_SCHEMA,
            "phase": "core-start", "code": "CHILD_BOOTSTRAP_FAILED", "cleanup_reaped": False,
            "cleanup_code": "CHILD_START_CLEANUP_UNCONFIRMED"}
        value = {"error": "WORKER_REPORTED_FAILURE", "transports": [{"worker_failure": record}]}
        result, output = self.inspect("easytier", value, 1)
        self.assertEqual(result, 1)
        self.assertIn("worker_failures=core-start:CHILD_BOOTSTRAP_FAILED:cleanup_reaped=false:CHILD_START_CLEANUP_UNCONFIRMED", output)
        for key, invalid in (("phase", "PRIVATE"), ("code", "CHILD_BOOTSTRAP_FAILED_PRIVATE"),
                ("cleanup_reaped", 0), ("cleanup_reaped", True), ("cleanup_code", "PRIVATE"),
                ("extra", "PRIVATE"), ("schema", "PRIVATE")):
            bad = {"transports": [{"worker_failure": {**record, key: invalid}}]}
            self.assertEqual(probe.worker_diagnostics(bad), [])
            self.assertNotIn("PRIVATE", self.inspect("easytier", bad, 1)[1])
        for kind, good in (("easytier", overlay()), ("owned-stack", owned_stack())):
            good["transports" if kind == "easytier" else "cases"][0]["worker_failure"] = record
            self.assertEqual(self.inspect(kind, good)[0], 1)

    def test_readiness_timeout_annotation_uses_real_producer_fixed_shape(self):
        last = dict(schema=worker_producer.READINESS_SCHEMA, core_image_matches=True,
            tun_owned=True, peer_authenticated=False, query_state="query-failed",
            query_code="PIPE_READ_TIMEOUT", local_bind_kind=None)
        record = overlay_producer.ReadinessTimeout("initiator", True, 7, last).record
        value = {"error": "OVERLAY_READY_TIMEOUT", "transports": [{"readiness_timeout": record}]}
        result, output = self.inspect("easytier", value, 1)
        self.assertEqual(result, 1)
        self.assertIn("readiness_timeouts=initiator:expected_auth=true:observations=7:core=true:"
            "tun=true:peer=false:query=query-failed:reason=PIPE_READ_TIMEOUT:bind=NONE", output)
        for kind, good in (("easytier", overlay()), ("owned-stack", owned_stack())):
            good["transports" if kind == "easytier" else "cases"][0]["readiness_timeout"] = record
            self.assertFalse(probe.verified(kind, good))

    def test_readiness_timeout_rejects_private_values_unknown_fields_and_contradictions(self):
        last = dict(schema=worker_producer.READINESS_SCHEMA, core_image_matches=True,
            tun_owned=False, peer_authenticated=False, query_state="tun-pending",
            query_code=None, local_bind_kind=None)
        record = overlay_producer.ReadinessTimeout("responder", False, 1, last).record
        self.assertEqual(len(probe.readiness_diagnostics({"transports": [{"readiness_timeout": record}]})), 1)
        changes = [(key, invalid) for key, invalid in (("role", "PRIVATE"), ("extra", "PRIVATE"),
            ("schema", "PRIVATE"), ("expected_authenticated", 0), ("observation_count", True),
            ("observation_count", 0), ("observation_count", 2049))]
        bad_records = [{**record, key: invalid} for key, invalid in changes]
        bad_records.extend({**record, "last_observation": {**last, key: invalid}}
            for key, invalid in (("schema", "PRIVATE"), ("core_image_matches", False),
                ("tun_owned", 0), ("peer_authenticated", True), ("query_state", "PRIVATE"),
                ("query_code", "PIPE_READ_TIMEOUT_PRIVATE"), ("local_bind_kind", "PRIVATE"),
                ("extra", "PRIVATE")))
        for bad in bad_records:
            value = {"transports": [{"readiness_timeout": bad}]}
            self.assertEqual(probe.readiness_diagnostics(value), [])
            self.assertNotIn("readiness_timeouts=", self.inspect("easytier", value, 1)[1])

    def test_native_missing_cases_cleanup_or_scope_cannot_pass(self):
        for changes in ({"cases": []}, {"cases": list(reversed(native()["cases"]))},
                        {"private_tree_removed": False}, {"all_owned_children_reaped": False},
                        {"owned_namespaces_removed": 2}, {"schema": True},
                        {"scope": {**native()["scope"], "production_provider_open": True}}):
            value = native(); value.update(changes)
            self.assertEqual(self.inspect("native", value)[0], 1)

    def test_overlay_requires_complete_matrix_containment_and_no_publication_claim(self):
        for change in ("missing_transport", "cleanup_failed", "preconditions", "publication", "private_cleanup"):
            value = overlay()
            if change == "missing_transport": value["transports"].pop()
            if change == "cleanup_failed": value["transports"][0]["cleanup_errors"].append("OWNED_NAMESPACE_CLEANUP_FAILED")
            if change == "preconditions": value["precondition_failures"].append("EXISTING_TUN_REQUIRED")
            if change == "publication": value["production_admission"] = True
            if change == "private_cleanup": value["private_stage_removed"] = False
            with self.subTest(change=change):
                self.assertEqual(self.inspect("easytier", value)[0], 1)

    def test_real_producer_schema_shapes_do_not_allow_case_or_phase_claims_without_observations(self):
        value = native()
        value["cases"][0]["no_state_created"] = False
        self.assertFalse(probe.verified("native", value))
        value = native(); value["cases"][2]["native_exit"] = 0
        self.assertFalse(probe.verified("native", value))
        value = native(); value["cases"][4]["native_exit"] = -9
        self.assertTrue(probe.verified("native", value))
        value = native(); value["scope"]["production_provider_open"] = 0
        self.assertFalse(probe.verified("native", value))
        for change in ("schema", "namespace_count", "phase", "positive", "negative", "core_exit", "containment"):
            value = overlay(); row = value["transports"][0]
            if change == "schema": value["schema"] = 1
            if change == "namespace_count": row["namespaces_removed"] = 3
            if change == "phase": row["phases"].pop()
            if change == "positive": row["phases"][0]["echo"]["tcp_port"] = "timeout"
            if change == "negative": row["phases"][1]["echo_rounds"][0]["tcp_port"] = "echo"
            if change == "core_exit": row["core_exit"]["core_reaped"] = False
            if change == "containment": row["blocked_packets"].pop("client")
            with self.subTest(change=change):
                self.assertFalse(probe.verified("easytier", value))

    def test_failure_annotation_uses_fixed_codes_and_no_raw_paths_values_or_unknown_suffix(self):
        self.error_log.write_text("NATIVE_READY_TIMEOUT /PRIVATE_PATH key=PRIVATE_VALUE\n"
            "WORKER_EVENT_INVALID_PRIVATE_SUFFIX PRIVATE_UNKNOWN_CODE\n")
        result, output = self.inspect("native", {"error": "PRIVATE_RECEIPT_VALUE", "status": "failed"}, 1)
        self.assertEqual(result, 1)
        self.assertIn("fixed_codes=NATIVE_READY_TIMEOUT", output)
        self.assertNotIn("WORKER_EVENT_INVALID", output)

    def test_containment_preflight_has_distinct_no_runtime_scope_and_complete_cleanup(self):
        value = {"schema": overlay_producer.CONTAINMENT_SCHEMA, "status": "containment_preflight_verified",
            "input_identity": "synthetic_profile_only_no_executable", "precondition_failures": [],
            "runtime_executed": False, "runtime_binary_uploaded": False, "production_admission": False,
            "raw_runtime_output_retained": False,
            "transports": [{"transport": transport, "status": "containment_pair_verified",
                "namespaces_created": 2, "namespaces_removed": 2, "cleanup_errors": []}
                for transport in overlay_producer.TRANSPORTS]}
        self.assertEqual(self.inspect("containment", value), (0, ""))
        self.assertFalse(probe.verified("easytier", value))
        for change in ("scope", "missing_transport", "cleanup", "count", "schema", "input"):
            bad = copy.deepcopy(value)
            if change == "scope": bad["runtime_executed"] = True
            if change == "missing_transport": bad["transports"].pop()
            if change == "cleanup": bad["transports"][0]["cleanup_errors"] = ["OWNED_NAMESPACE_CLEANUP_FAILED"]
            if change == "count": bad["transports"][0]["namespaces_removed"] = True
            if change == "schema": bad["schema"] = overlay_producer.SCHEMA
            if change == "input": bad["input_identity"] = "PRIVATE_INPUT"
            with self.subTest(change=change):
                self.assertFalse(probe.verified("containment", bad))

    def test_nft_diagnostics_accept_exact_producer_shapes_but_no_keys_values_or_suffixes(self):
        changes = [([{"rule": {"expr": [{"match": {"right": "tcp"}}]}}],
                    [{"rule": {"expr": [{"match": {"right": 6}}]}}]),
                   ([{"chain": {"prio": 0}}], [{"chain": {"prio": "filter"}}]),
                   ([{"rule": {"expr": [{"match": {"op": "=="}}]}}],
                    [{"rule": {"expr": [{"match": {"op": "in"}}]}}])]
        for expected, actual in changes:
            codes = overlay_producer.nft_difference_codes(expected, actual)
            value = {"transports": [{"fixed_diagnostics": codes}], "error": "CONTAINMENT_CHANGED"}
            self.assertEqual(probe.nft_diagnostics(value), sorted(codes))
            result, output = self.inspect("containment", value, 1)
            self.assertEqual(result, 1)
            self.assertIn("nft_shape=", output)
        for code in ("NFT_I0_RULE_PRIVATE_KEY_MISSING_KEY", "NFT_I64_RULE_MISSING_KEY",
                     "NFT_I0_RULE_PRIVATE_VALUE_TO_TCP", "NFT_I0_RULE_TYPE_STRING_TO_PRIVATE_TYPE",
                     "NFT_I0_RULE_TYPE_STRING_TO_INTEGER_PRIVATE_SUFFIX", "NFT_I0_RULE_COUNT_1_TO_9000",
                     "NFT_PRIVATE_SECRET", "NFT_I0_RULE_MISSING_KEY /PRIVATE_PATH"):
            value = {"transports": [{"fixed_diagnostics": [code]}]}
            self.assertEqual(probe.nft_diagnostics(value), [])
            self.assertNotIn("PRIVATE", self.inspect("containment", value, 1)[1])

    def test_missing_malformed_and_oversized_receipts_do_not_certify_zero_exit(self):
        for content in (None, b"PRIVATE_NOT_JSON", b"x" * (256 * 1024 + 1),
                        b'{"status":"PRIVATE_FIRST","status":"PRIVATE_SECOND"}'):
            if content is not None:
                self.receipt.write_bytes(content)
            output = io.StringIO()
            with redirect_stdout(output):
                self.assertEqual(probe.inspect("native", 0, self.receipt, self.error_log), 1)
            self.assertIn("PROBE_SUCCESS_RECEIPT_INVALID", output.getvalue())
            self.assertNotIn("PRIVATE", output.getvalue())


if __name__ == "__main__":
    unittest.main()
