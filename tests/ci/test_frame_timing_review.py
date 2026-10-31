import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("frame_timing_review", ROOT / "scripts/frame_timing_review.py")
review = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(review)


def fixture():
    runs = []
    for phase, emf, shift in (("baseline", "off", 0), ("baseline", "on", 10),
                              ("modified", "off", 2), ("modified", "on", 5)):
        runs.append({"phase": phase, "emf": emf, "sample": 1,
                     "build_sha": ("a" if phase == "baseline" else "b") * 40,
                     "frames": [{"index": i, "frame_ms": i + 1 + shift, "width": 1920, "height": 1080}
                                for i in range(100)]})
    return {"schema": 1, "evidence": "synthetic_fixture", "metric": "present_interval_ms",
            "controls": {key: True for key in review.CONTROLS}, "runs": runs}


class FrameTimingReviewTests(unittest.TestCase):
    def test_hand_calculated_nearest_rank_and_four_way_toggle_comparison(self):
        result = review.analyze(fixture())
        baseline_off = next(row for row in result["runs"] if row["phase"] == "baseline" and row["emf"] == "off")
        self.assertEqual(baseline_off["all_frames"]["p95_ms"], 95)
        self.assertEqual(baseline_off["all_frames"]["p99_ms"], 99)
        self.assertEqual(result["comparisons"][0]["p99_ms"], {
            "baseline_on_minus_off": 10, "modified_on_minus_off": 3,
            "modified_minus_baseline_off": 2, "modified_minus_baseline_on": -5,
            "difference_of_toggle_differences": -7})
        self.assertFalse(result["performance_fix_verified"])
        self.assertFalse(result["input_provenance_independently_verified"])

    def test_zero_dimension_tail_is_retained_and_split_not_silently_dropped(self):
        data = fixture()
        frames = data["runs"][0]["frames"]
        for i in range(95, 100):
            frames[i]["width"] = 0
            frames[i]["frame_ms"] = 1000
        result = review.analyze(data)
        run = result["runs"][0]
        self.assertEqual(run["all_frames"]["p99_ms"], 1000)
        self.assertEqual(run["positive_dimensions"]["p99_ms"], 95)
        self.assertEqual(run["zero_dimension_frames"]["count"], 5)
        self.assertEqual(result["comparisons"][0]["zero_dimension_counts_in_group_order"], [5, 0, 0, 0])
        self.assertFalse(result["comparisons"][1]["at_least_100_frames_each"])

    def test_no_visible_frames_is_unknown_not_zero_milliseconds(self):
        data = fixture()
        for frame in data["runs"][0]["frames"]:
            frame["height"] = 0
        result = review.analyze(data)
        self.assertIsNone(result["runs"][0]["positive_dimensions"]["p99_ms"])
        self.assertIsNone(result["comparisons"][1]["p99_ms"])

    def test_unknown_fields_cannot_echo_private_labels(self):
        for location in ("document", "run", "frame"):
            data = fixture()
            target = data if location == "document" else data["runs"][0]
            if location == "frame":
                target = target["frames"][0]
            target["player"] = "private-fixture-marker"
            with self.subTest(location=location), self.assertRaises(review.InputError) as error:
                review.analyze(data)
            self.assertNotIn("private-fixture-marker", str(error.exception))

    def test_missing_duplicate_unpaired_or_mixed_build_groups_rejected(self):
        changes = [lambda d: d["runs"].pop(),
                   lambda d: d["runs"].append(copy.deepcopy(d["runs"][0])),
                   lambda d: d["runs"][0].update(sample=2),
                   lambda d: d["runs"][0].update(build_sha="c" * 40)]
        for change in changes:
            data = fixture()
            change(data)
            with self.subTest(change=change), self.assertRaises(review.InputError):
                review.analyze(data)

    def test_gaps_duplicates_nonfinite_negative_and_bool_numeric_rejected(self):
        changes = [("index", 2), ("index", True), ("frame_ms", float("nan")),
                   ("frame_ms", float("inf")), ("frame_ms", 0), ("frame_ms", -1),
                   ("frame_ms", True), ("width", -1), ("height", True), ("width", 1.5)]
        for key, value in changes:
            data = fixture()
            data["runs"][0]["frames"][1][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(review.InputError):
                review.analyze(data)

    def test_subview_or_error_count_is_not_frame_timing_and_unmatched_controls_are_visible(self):
        for metric in ("view_us", "draw_error_count", "mixed_cpu_gpu"):
            data = fixture()
            data["metric"] = metric
            with self.subTest(metric=metric), self.assertRaises(review.InputError):
                review.analyze(data)
        data = fixture()
        data["controls"]["same_scene"] = False
        comparison = review.analyze(data)["comparisons"][0]
        self.assertFalse(comparison["controls_match"])
        self.assertEqual(comparison["comparison_status"], "blocked")
        self.assertIsNone(comparison["p99_ms"])

    def test_too_few_frames_remain_marked_insufficient(self):
        data = fixture()
        for run in data["runs"]:
            run["frames"] = run["frames"][:2]
        result = review.analyze(data)
        self.assertFalse(result["comparisons"][0]["at_least_100_frames_each"])
        self.assertEqual(result["runs"][0]["all_frames"]["p99_ms"], 2)
        self.assertIsNone(result["comparisons"][0]["p99_ms"])

    def test_resolution_changes_and_different_sizes_cannot_be_compared(self):
        for change_all in (True, False):
            data = fixture()
            frames = data["runs"][0]["frames"] if change_all else data["runs"][0]["frames"][:1]
            for frame in frames:
                frame["width"] = 1280
                frame["height"] = 720
            comparison = review.analyze(data)["comparisons"][0]
            self.assertIn("RESOLUTION_MISMATCH_OR_UNAVAILABLE", comparison["blocked_reasons"])
            self.assertIsNone(comparison["p99_ms"])

    def test_two_real_sample_quartets_are_not_pooled_or_repaired_by_other_samples(self):
        data = fixture()
        second = copy.deepcopy(data["runs"])
        for run in second:
            run["sample"] = 2
            for frame in run["frames"]:
                frame["frame_ms"] *= 2
        data["runs"].extend(second)
        result = review.analyze(data)
        self.assertEqual(len(result["comparisons"]), 4)
        self.assertEqual(result["comparisons"][0]["p99_ms"]["baseline_on_minus_off"], 10)
        self.assertEqual(result["comparisons"][2]["p99_ms"]["baseline_on_minus_off"], 20)
        data["runs"].pop()
        with self.assertRaisesRegex(review.InputError, "UNPAIRED_GROUPS"):
            review.analyze(data)

    def test_cli_utf8_receipt_no_overwrite_and_no_private_error_echo(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, output = root / "输入.json", root / "report.json"
            source.write_text(json.dumps(fixture()), encoding="utf-8")
            command = [sys.executable, "-B", str(ROOT / "scripts/frame_timing_review.py"), str(source), "--output", str(output)]
            first = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(first.returncode, 0, first.stderr)
            original = output.read_bytes()
            result = json.loads(original)
            self.assertEqual(len(result["input_sha256"]), 64)
            self.assertFalse(result["capture_started"])
            self.assertNotIn(str(source), original.decode("utf-8"))
            second = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(second.returncode, 1)
            self.assertEqual(output.read_bytes(), original)
            source.write_text('{"private-fixture-marker":', encoding="utf-8")
            third = subprocess.run(command, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(third.returncode, 1)
            self.assertNotIn("private-fixture-marker", third.stderr)
            self.assertNotIn(str(source), third.stderr)

    def test_duplicate_json_keys_and_excess_bytes_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "input.json"
            source.write_text('{"schema": 1, "schema": 1}', encoding="utf-8")
            with self.assertRaisesRegex(review.InputError, "DUPLICATE_JSON_KEY"):
                review.read_input(source)
            source.write_bytes(b" " * (review.MAX_BYTES + 1))
            with self.assertRaisesRegex(review.InputError, "INPUT_SIZE"):
                review.read_input(source)


if __name__ == "__main__":
    unittest.main()
