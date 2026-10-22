from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from scblctl.maintenance import _retained_release_paths, prune_obsolete_runtime_storage


class MaintenanceTests(unittest.TestCase):
    def test_prune_keeps_current_previous_and_unknown_directories(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            releases = root / "releases"
            store = root / "store"
            releases.mkdir()
            store.mkdir()
            current = releases / "3.0.3-20260822010000"
            previous = releases / "3.0.2-20260821010000"
            old = releases / "3.0.1-20260820010000"
            unknown = releases / "manual-notes"
            for path in (current, previous, old, unknown):
                path.mkdir()
                (path / "payload").write_bytes(b"x")
            current_digest = "a" * 64
            previous_digest = "b" * 64
            stale_digest = "c" * 64
            for path, digest in ((current, current_digest), (previous, previous_digest)):
                (path / "component-versions.json").write_text(
                    json.dumps({"component": {"sha256": digest}}), encoding="utf-8"
                )
            for digest in (current_digest, previous_digest, stale_digest):
                directory = store / digest
                directory.mkdir()
                (directory / "payload").write_bytes(b"data")

            paths = SimpleNamespace(
                releases=str(releases),
                component_store=str(store),
                current=str(root / "current"),
                previous=str(root / "previous"),
            )
            with (
                patch("scblctl.maintenance.DEPLOYMENT_PATHS", paths),
                patch("scblctl.maintenance._retained_release_paths", return_value={current, previous}),
                patch("scblctl.maintenance._require_root"),
            ):
                preview = prune_obsolete_runtime_storage(apply=False)
                self.assertEqual((old,), preview.releases)
                self.assertEqual((store / stale_digest,), preview.components)
                prune_obsolete_runtime_storage(apply=True)

            self.assertTrue(current.exists())
            self.assertTrue(previous.exists())
            self.assertTrue(unknown.exists())
            self.assertFalse(old.exists())
            self.assertTrue((store / current_digest).exists())
            self.assertFalse((store / stale_digest).exists())

    def test_runtime_retention_keeps_three_newest_without_links(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            releases = root / "releases"
            releases.mkdir()
            paths = []
            for version in range(1, 5):
                path = releases / f"3.0.{version}-2026082{version}010000"
                path.mkdir()
                paths.append(path.resolve())
            deployment_paths = SimpleNamespace(
                releases=str(releases),
                current=str(root / "missing-current"),
                previous=str(root / "missing-previous"),
            )
            with patch("scblctl.maintenance.DEPLOYMENT_PATHS", deployment_paths):
                retained = _retained_release_paths()
            self.assertEqual(set(paths[-3:]), retained)


if __name__ == "__main__":
    unittest.main()
