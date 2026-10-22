from __future__ import annotations

import hashlib
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from scblctl.external_easytier import (
    EasyTierInstallError,
    EasyTierLayout,
    EasyTierManager,
)
from scblctl.system import CommandResult


class _Response(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *_args):
        self.close()


class _Runner:
    def run(self, args, *, timeout=15):
        output = (
            "--config-file --rpc-portal --console-log-level --file-log-level"
            if Path(args[0]).name == "easytier-core"
            else "EasyTier CLI"
        )
        return CommandResult(tuple(args), 0, output, "")


class EasyTierManagerTests(unittest.TestCase):
    def make_archive(self, root: Path, *, unsafe: bool = False) -> bytes:
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            archive.writestr("../easytier-core" if unsafe else "easytier/easytier-core", b"core")
            archive.writestr("easytier/easytier-cli", b"cli")
        return stream.getvalue()

    def manager(self, root: Path, archive: bytes, *, calls: list[str]) -> EasyTierManager:
        asset_name = "easytier-linux-x86_64-v9.8.7.zip"
        release = {
            "tag_name": "v9.8.7",
            "assets": [
                {
                    "name": asset_name,
                    "size": len(archive),
                    "digest": "sha256:" + hashlib.sha256(archive).hexdigest(),
                    "browser_download_url": (
                        "https://github.com/EasyTier/EasyTier/releases/download/v9.8.7/"
                        + asset_name
                    ),
                }
            ],
        }

        def opener(request, *, timeout):
            url = request.full_url
            calls.append(url)
            if url.endswith("/releases/latest"):
                return _Response(json.dumps(release).encode())
            return _Response(archive)

        return EasyTierManager(
            layout=EasyTierLayout(root / "external", root / "cache"),
            runner=_Runner(),
            opener=opener,
            machine=lambda: "x86_64",
        )

    def test_first_install_downloads_once_then_repeated_install_reuses_verified_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            calls: list[str] = []
            manager = self.manager(root, self.make_archive(root), calls=calls)
            with patch.object(manager, "_activate"):
                installed = manager.ensure_installed()
            self.assertFalse(installed.reused)
            self.assertEqual(2, len(calls))
            calls.clear()

            with patch.object(manager, "_current_target", return_value=installed.path):
                reused = manager.ensure_installed()
            self.assertTrue(reused.reused)
            self.assertEqual([], calls)
            self.assertEqual("9.8.7", reused.version)
            self.assertEqual(b"core", (reused.path / "easytier-core").read_bytes())

    def test_update_reuses_verified_cached_archive(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            calls: list[str] = []
            manager = self.manager(root, self.make_archive(root), calls=calls)
            with patch.object(manager, "_activate"):
                manager.install_latest()
            shutil_target = manager.layout.releases / "9.8.7"
            # Removing only the release forces a reinstall while retaining the verified cache.
            import shutil

            shutil.rmtree(shutil_target)
            calls.clear()
            with patch.object(manager, "_activate"):
                manager.install_latest()
            self.assertEqual(1, len(calls))
            self.assertTrue(calls[0].endswith("/releases/latest"))

    def test_unsafe_official_archive_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = self.manager(root, self.make_archive(root, unsafe=True), calls=[])
            with self.assertRaisesRegex(EasyTierInstallError, "不安全"):
                manager.install_latest()

    def test_release_cleanup_keeps_three_generations(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manager = self.manager(root, self.make_archive(root), calls=[])
            manager.layout.releases.mkdir(parents=True)
            generations = []
            for version in ("1.0.0", "2.0.0", "3.0.0", "4.0.0"):
                path = manager.layout.releases / version
                path.mkdir()
                generations.append(path)
            manager._prune_releases({generations[-1], generations[-2]})
            self.assertEqual(
                ["2.0.0", "3.0.0", "4.0.0"],
                sorted(path.name for path in manager.layout.releases.iterdir()),
            )


if __name__ == "__main__":
    unittest.main()
