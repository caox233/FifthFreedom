"""Validate actual-render receipts without accepting fabricated or incomplete evidence."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import struct
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_visual_probe as visual


class VisualReceiptTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-visual-receipt-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        # Synthetic bytes only exercise receipt validation. They are never
        # exported or represented as an actual WPF screenshot.
        self.frames = []
        for index, scenario in enumerate(visual.scenarios()):
            data = (b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\rIHDR" + struct.pack(">II", round(scenario["Width"]*scenario["Dpi"]/96),
                    round(scenario["Height"]*scenario["Dpi"]/96)) + b"synthetic-validator-fixture" * 60)
            name = f"fixture-{index}.png"
            (self.root / name).write_bytes(data)
            self.frames.append({**scenario, "Id": f"fixture-{index}", "ErrorType": None,
                                "ObservedDpi": scenario["Dpi"], "ProductionStateHook": True,
                                "Screenshot": name, "Sha256": hashlib.sha256(data).hexdigest(),
                                "Checks": [{"Category": "geometry", "Target": "fixture", "Passed": True},
                                           {"Category": "keyboard", "Target": "fixture", "Passed": True}]})
        self.report = {"Schema": 1, "FixtureId": visual.FIXTURE_ID, "NetworkStarted": False,
                       "GameStarted": False, "PersistedSettingsRead": False, "Frames": self.frames}

    def validate(self, report=None, **kwargs):
        return visual.validate_frames(self.report if report is None else report, self.root, require_hook=kwargs.get("require_hook", True))

    def test_complete_matrix_covers_settings_public_and_requested_root_dpi(self):
        self.assertEqual(len(visual.scenarios()), 128)
        self.assertEqual({row["Dpi"] for row in visual.scenarios()}, {96, 120, 144, 192})
        self.assertEqual(self.validate()["frames"], 128)

    def test_missing_or_duplicate_scenario_is_rejected(self):
        for frames in (self.frames[:-1], self.frames + [self.frames[0]]):
            with self.subTest(count=len(frames)), self.assertRaises(ValueError):
                self.validate({**self.report, "Frames": frames})

    def test_stale_or_tampered_image_is_rejected(self):
        (self.root / self.frames[0]["Screenshot"]).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "Screenshot bytes"):
            self.validate()

    def test_wrong_dpi_cannot_pass_as_rescaled_screenshot(self):
        self.frames[0]["ObservedDpi"] = 120
        with self.assertRaisesRegex(ValueError, "requested root DPI"):
            self.validate()

    def test_wrong_pixel_dimensions_are_rejected_even_with_matching_hash(self):
        path = self.root / self.frames[0]["Screenshot"]
        data = bytearray(path.read_bytes()); data[16:24] = struct.pack(">II", 1, 1); path.write_bytes(data)
        self.frames[0]["Sha256"] = visual.sha(path)
        with self.assertRaisesRegex(ValueError, "pixel dimensions"):
            self.validate()

    def test_missing_layout_assertions_or_current_state_hook_are_rejected(self):
        for field, value in (("Checks", []), ("ProductionStateHook", False)):
            altered = copy.deepcopy(self.report)
            altered["Frames"][0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.validate(altered)

    def test_old_missing_state_hook_is_explicit_and_layout_failures_are_preserved(self):
        self.frames[0]["ProductionStateHook"] = False
        self.frames[0]["Checks"][0]["Passed"] = False
        result = self.validate(require_hook=False)
        self.assertEqual(result["layout_failure_count"], 1)
        self.assertEqual(result["failures"][0]["Target"], "fixture")

    def test_privacy_boundaries_and_path_identity_are_enforced(self):
        for name in ("NetworkStarted", "GameStarted", "PersistedSettingsRead"):
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "isolation"):
                self.validate({**self.report, name: True})
        self.frames[0]["Screenshot"] = "../outside.png"
        with self.assertRaisesRegex(ValueError, "Unsafe"):
            self.validate()


if __name__ == "__main__":
    unittest.main()
