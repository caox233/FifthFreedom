"""Evidence parser controls; these inert strings do not execute x86 code."""
import json
from itertools import product
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_camera_abi_replay as replay


class CameraAbiReceiptTests(unittest.TestCase):
    def output(self, old=False):
        value = {"schema": 1, "cases": 64, "hooked_calls": 32, "unhooked_calls": 32,
                 "cleanup_calls": 64, "negative_controls": 1, "checks": 515,
                 "status": "failed" if old else "passed", "retail_body": False, "native_return_type_confirmed": False,
                 "game_executed": False, "failures": {"stack_cleanup": 32 if old else 0,
                 "argument_forwarding": 32 if old else 0, "receiver": 0, "nonvolatile": 0,
                 "native_count": 0, "cleanup": 0, "caller_stack": 0, "branch": 0, "restoration": 0}}
        summary = ("FAILED. 0 passed; 1 failed" if old else "ok. 1 passed; 0 failed")
        text = ""
        for stage, repeat, receiver, argument in product(range(4), range(2), range(2), (0, 0xFFFFFFFF, 0x13579BDF, 0x2468ACE0)):
            hooked = stage in (1, 3)
            received = 7 if old and hooked else argument
            row = {"stage": stage, "repeat": repeat, "receiver": receiver, "argument": argument,
                   "received_argument": received, "hooked": hooked, "stack_delta": -4 if old and hooked else 0,
                   "branch": 0 if received == 0 else 2 if received == 0xFFFFFFFF else 1}
            text += "CAMERA_ABI_CASE " + json.dumps(row) + "\n"
        text += "CAMERA_ABI_RESULT " + json.dumps(value) + "\n"
        text += "CAMERA_ABI_CHILD_EXIT code=" + ("101" if old else "0") + "\n"
        text += (("failures:\n    " + replay.TEST + "\n" if old else "") + "test result: " + summary
                 + "; 0 ignored; 0 measured; 100 filtered out; finished in 0.01s\n") * 2
        return text, "CAMERA_ABI_CONTRACT_FAILED" if old else ""

    def test_all_four_roles_require_completed_real_test_results(self):
        for phase in ("baseline", "modified", "rollback", "reapplied"):
            old = phase in ("baseline", "rollback")
            out, err = self.output(old)
            self.assertEqual(replay.inspect_result(phase, 101 if old else 0, out, err)["cases"], 64)

    def test_compile_failure_crash_and_missing_actual_assertion_are_not_baseline(self):
        out, err = self.output(True)
        for code, stdout, stderr in ((1, out, err), (-1073741819, out, err),
                                     (101, "error: compilation failed", err), (101, out, ""), (0, out, err)):
            with self.subTest(code=code), self.assertRaises(ValueError):
                replay.inspect_result("baseline", code, stdout, stderr)

    def test_skipped_short_duplicate_or_extra_failure_results_are_rejected(self):
        out, err = self.output(True)
        changes = [out.replace("0 ignored", "1 ignored"), out.replace('"cases": 64', '"cases": 32'),
                   out + out, out.replace('"nonvolatile": 0', '"nonvolatile": 1'),
                   out.replace('"stack_cleanup": 32', '"stack_cleanup": 31'),
                   out.replace('"argument_forwarding": 32', '"argument_forwarding": 33'),
                   out.replace('"receiver": 0', '"receiver": false'),
                   out.replace('"game_executed": false', '"game_executed": true')]
        for changed in changes:
            with self.subTest(output=changed[:100]), self.assertRaises(ValueError):
                replay.inspect_result("baseline", 101, changed, err)

    def test_modified_requires_no_abi_failure(self):
        out, err = self.output(False)
        for name in ("argument_forwarding", "stack_cleanup", "cleanup", "native_count", "nonvolatile", "receiver"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                replay.inspect_result("modified", 0, out.replace(f'"{name}": 0', f'"{name}": 1'), err)

    def test_case_matrix_requires_every_stage_receiver_repeat_and_argument(self):
        out, err = self.output(True)
        lines = out.splitlines(keepends=True)
        for changed in ("".join(lines[1:]), lines[0] + out,
                        out.replace('"stage": 3', '"stage": 2'),
                        out.replace('"stack_delta": -4', '"stack_delta": -8'),
                        out.replace('"received_argument": 7', '"received_argument": 0'),
                        out.replace('"hooked": true', '"hooked": false')):
            with self.subTest(output=changed[:100]), self.assertRaises(ValueError):
                replay.inspect_result("baseline", 101, changed, err)

    def test_malformed_extra_markers_and_numeric_bool_or_float_are_rejected(self):
        out, err = self.output(True)
        for changed in (out.replace('"schema": 1', '"schema": true'),
                        out.replace('"cases": 64', '"cases": 64.0'),
                        out + "CAMERA_ABI_CASE broken\n", out + "CAMERA_ABI_RESULT broken\n",
                        out + "failures:\n    unrelated::unrelated_test\n",
                        out.replace(replay.TEST, "unrelated::unrelated_test")):
            with self.subTest(output=changed[-100:]), self.assertRaises(ValueError):
                replay.inspect_result("baseline", 101, changed, err)

    def test_only_the_same_test_module_may_be_appended(self):
        raw = b"// Unicode stays exact: \xe6\xb5\x8b\xe8\xaf\x95\nfn untouched() {}\n"
        compiled, record = replay.instrument(raw)
        self.assertEqual(compiled, raw + replay.MODULE.encode("utf-8"))
        self.assertTrue(record["test_module_appended"])
        again, record = replay.instrument(compiled)
        self.assertEqual(again, compiled)
        self.assertFalse(record["test_module_appended"])
        for changed in (raw + b"mod abi_replay;", compiled + replay.MODULE.encode("utf-8")):
            with self.assertRaises(ValueError):
                replay.instrument(changed)

    def observed_fault(self):
        out, _ = self.output(False)
        rows = [json.loads(line[len("CAMERA_ABI_CASE "):]) for line in out.splitlines() if line.startswith("CAMERA_ABI_CASE ")]
        rows = sorted([r for r in rows if r["stage"] == 0], key=lambda r: (r["repeat"], r["receiver"],
            (0x13579BDF, 0, 0xFFFFFFFF, 0x2468ACE0).index(r["argument"])))
        event = {"schema": 1, "completed_cases": 16, "stage": 1, "repeat": 0, "receiver": 0,
                 "argument": 0x13579BDF, "received_argument": 7, "native_count_delta": 1,
                 "prior_failures": 0, "cleanup_calls": 17, "exception_code": 0xC0000005,
                 "access": 8, "child_exit_code": 86, "receiver_correct": True, "cleanup_correct": True,
                 "previous_cases_validated": True, "context_resumed": False, "game_executed": False,
                 "eip": 1, "exception_address": 1, "access_target": 1}
        text = "".join("CAMERA_ABI_CASE " + json.dumps(row) + "\n" for row in rows)
        text += "CAMERA_ABI_ABORT " + json.dumps(event) + "\nCAMERA_ABI_CHILD_EXIT code=86\n"
        text += "failures:\n    " + replay.TEST + "\n"
        text += "test result: FAILED. 0 passed; 1 failed; 0 ignored; 0 measured; 100 filtered out; finished in 0.01s\n"
        return text, "CAMERA_ABI_CONTRACT_FAILED"

    def test_exact_owned_exception_observation_is_partial_negative_evidence_only(self):
        out, err = self.observed_fault()
        for phase in ("baseline", "rollback"):
            result = replay.inspect_result(phase, 101, out, err)
            self.assertEqual(result["completed_cases"], 16)
            self.assertFalse(result["remaining_cases_executed"])
            self.assertFalse(result["complete_matrix_pass_claimed"])

    def test_unrelated_premature_or_unobserved_crash_is_rejected(self):
        out, err = self.observed_fault()
        changes = [out.replace('"prior_failures": 0', '"prior_failures": 1'),
                   out.replace('"access": 8', '"access": 1'), out.replace('"receiver_correct": true', '"receiver_correct": false'),
                   out.replace('"native_count_delta": 1', '"native_count_delta": 2'),
                   out.replace('"received_argument": 7', '"received_argument": 324508639'),
                   out.replace('"exception_address": 1', '"exception_address": 0'),
                   out.replace('"context_resumed": false', '"context_resumed": true'),
                   out.replace("code=86", "code=-1073741819"), "\n".join(out.splitlines()[1:]),
                   out + "CAMERA_ABI_RESULT {}\n", out + "CAMERA_ABI_ABORT broken\n"]
        for changed in changes:
            with self.subTest(output=changed[-100:]), self.assertRaises(ValueError):
                replay.inspect_result("baseline", 101, changed, err)
        for phase, code in (("modified", 101), ("reapplied", 101), ("baseline", -1073741819), ("baseline", 86)):
            with self.subTest(phase=phase, code=code), self.assertRaises(ValueError):
                replay.inspect_result(phase, code, out, err)


if __name__ == "__main__":
    unittest.main()
