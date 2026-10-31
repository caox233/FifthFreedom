"""Candidate-lane evidence checks; no privileged namespace operations here."""
import copy
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_candidate as ci
import ci_socket_isolation as harness


class SocketLaneReceiptTests(unittest.TestCase):
    def setUp(self):
        self.sha = "a" * 64
        self.report = {"schema": 1, "status": "interface_ingress_isolation_verified",
                       "interface_isolation_verified": True, "probe_sha256": self.sha,
                       "cleanup_errors": [], "namespaces_created": 3, "namespaces_removed": 3,
                       **harness.SCOPE, "observations": []}
        for mode in ("ip-only", "interface-bound"):
            for phase, path in (("before", "protected"), ("ingress", "wrong"), ("after", "protected")):
                for field in harness.PORT_FIELDS:
                    self.report["observations"].append({"mode": mode, "phase": phase, "path": path,
                        "socket": field, "observation": "timeout" if mode == "interface-bound" and path == "wrong" else "echo"})

    def test_exact_positive_and_negative_controls_are_required(self):
        ci.validate_socket_receipt(self.report, self.sha)
        for index in range(18):
            report = copy.deepcopy(self.report)
            report["observations"][index]["observation"] = (
                "echo" if report["observations"][index]["observation"] == "timeout" else "timeout")
            with self.subTest(index=index), self.assertRaisesRegex(RuntimeError, "controls or protected paths"):
                ci.validate_socket_receipt(report, self.sha)

    def test_incomplete_duplicate_or_reordered_observations_cannot_pass(self):
        for observations in (self.report["observations"][:-1], self.report["observations"] * 2,
                             list(reversed(self.report["observations"])),
                             [self.report["observations"][0]] * 18):
            with self.subTest(count=len(observations)), self.assertRaises(RuntimeError):
                ci.validate_socket_receipt({**self.report, "observations": observations}, self.sha)

    def test_cleanup_identity_scope_and_typed_success_flags_cannot_be_weakened(self):
        variants = [("schema", True), ("interface_isolation_verified", 1), ("probe_sha256", "b" * 64),
                    ("cleanup_errors", ["failed"]), ("namespaces_removed", 2), ("namespaces_created", True),
                    ("error", "failed"), *[(name, True) for name in harness.SCOPE]]
        for field, value in variants:
            with self.subTest(field=field), self.assertRaisesRegex(RuntimeError, "scope and cleanup"):
                ci.validate_socket_receipt({**self.report, field: value}, self.sha)

    def test_lane_rejects_non_hosted_dirty_or_different_revision_before_privilege_use(self):
        receipt = {"github_actions": True, "tracked_source_dirty": False, "commit": "a" * 40}
        for changes in ({"github_actions": False}, {"tracked_source_dirty": True}, {"commit": "b" * 40}):
            with self.subTest(field=next(iter(changes))), patch.object(ci, "sys", SimpleNamespace(platform="linux")), \
                    patch.dict(ci.os.environ, {"GITHUB_SHA": "a" * 40}), patch.object(ci, "run_build_step") as run:
                with self.assertRaisesRegex(RuntimeError, "clean hosted Linux"):
                    ci.linux_socket_isolation({**receipt, **changes})
                run.assert_not_called()

    def test_empty_harness_discovery_rejects_before_build_or_privilege_use(self):
        receipt = {"github_actions": True, "tracked_source_dirty": False, "commit": "a" * 40}
        with patch.object(ci, "sys", SimpleNamespace(platform="linux")), \
                patch.dict(ci.os.environ, {"GITHUB_SHA": "a" * 40}), \
                patch.object(unittest.TestLoader, "discover", return_value=unittest.TestSuite()), \
                patch.object(ci, "run_build_step") as run:
            with self.assertRaisesRegex(RuntimeError, "not discovered"):
                ci.linux_socket_isolation(receipt)
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
