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
        for kind, value in (("native", native()), ("easytier", overlay())):
            self.assertEqual(self.inspect(kind, value), (0, ""))
            self.assertEqual(self.inspect(kind, value, exit_code=1)[0], 1)

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
