"""Execute production AuthService branches with an in-memory transport fixture."""
from pathlib import Path
import os
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]


class PackageAdmissionMessageTests(unittest.TestCase):
    def test_login_register_reasons_and_transport_controls(self):
        dotnet = shutil.which("dotnet")
        self.assertIsNotNone(dotnet, "The pinned .NET SDK must be installed before source checks")
        result = subprocess.run(
            [dotnet, "run", "--project", str(ROOT / "tests/PackageAdmissionMessages/PackageAdmissionMessages.csproj"), "-c", "Release"],
            cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="strict", timeout=180,
            env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(sum(line.startswith("PASS ") for line in result.stdout.splitlines()), 43, result.stdout)
        self.assertIn("RESULT package-admission-messages passed=43 failed=0;", result.stdout)
        self.assertNotIn("FAIL ", result.stdout)


if __name__ == "__main__":
    unittest.main()
