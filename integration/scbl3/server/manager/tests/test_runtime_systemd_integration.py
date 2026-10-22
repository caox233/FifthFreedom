from __future__ import annotations

import hashlib
import os
import shutil
import sqlite3
import subprocess
import unittest
from pathlib import Path

from scblctl.provision import (
    ManagedFileSnapshot,
    ProvisionLayout,
    Provisioner,
    RUNTIME_UNIT_NAMES,
    RuntimeTransactionSnapshot,
)
from scblctl.release import UNIT_PROGRAM_ARGUMENTS, RuntimeManifest, activate_release


class RuntimeSystemdIntegrationTests(unittest.TestCase):
    """Exercise rollback against a real, isolated WSL systemd instance."""

    root = Path("/run/scbl-runtime-transaction-qa")
    unit_root = Path("/run/systemd/system")

    def setUp(self) -> None:
        if os.name != "posix" or os.geteuid() != 0:
            self.skipTest("requires Linux root")
        if subprocess.run(
            ("systemctl", "is-system-running"),
            check=False,
            capture_output=True,
            text=True,
        ).returncode not in {0, 1}:
            self.skipTest("requires a running systemd instance")
        for unit in RUNTIME_UNIT_NAMES:
            state = subprocess.run(
                ("systemctl", "show", unit, "--property=LoadState", "--value"),
                check=False,
                capture_output=True,
                text=True,
            ).stdout.strip()
            if state and state != "not-found":
                self.skipTest(f"refusing to replace existing unit: {unit}")
        if self.root.exists():
            self.fail(f"stale integration root exists: {self.root}")

        releases = self.root / "opt/scbl/releases"
        self.old = releases / "2.0.21-old"
        self.new = releases / "2.0.23-new"
        self.old.mkdir(parents=True)
        self.new.mkdir(parents=True)
        for directory in (self.old, self.new):
            binary = directory / "dedicated_server"
            binary.write_text("#!/bin/sh\nexec /bin/sleep 300\n", encoding="utf-8")
            binary.chmod(0o755)

        self.layout = ProvisionLayout(
            current=self.root / "opt/scbl/current",
            releases=releases,
            cache=self.root / "var/cache/scbl",
            data=self.root / "var/lib/scbl",
            config_root=self.root / "etc/scbl",
            unit_root=self.unit_root,
            wait_script=self.root / "usr/local/lib/scbl/wait-scbl0",
        )
        (self.layout.data / "dedicated").mkdir(parents=True)
        activate_release(self.new, self.layout.current)
        self.first = hashlib.sha384(b"first").hexdigest()
        self.second = hashlib.sha384(b"second").hexdigest()

    def tearDown(self) -> None:
        subprocess.run(
            ("systemctl", "stop", *RUNTIME_UNIT_NAMES),
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        subprocess.run(
            ("systemctl", "disable", "--runtime", *RUNTIME_UNIT_NAMES),
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        for unit in RUNTIME_UNIT_NAMES:
            (self.unit_root / unit).unlink(missing_ok=True)
        subprocess.run(
            ("systemctl", "daemon-reload"),
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        shutil.rmtree(self.root, ignore_errors=True)

    def _unit_snapshots(self) -> dict[str, ManagedFileSnapshot]:
        snapshots: dict[str, ManagedFileSnapshot] = {}
        for unit in RUNTIME_UNIT_NAMES:
            if unit == "scbl-dedicated.service":
                command = f"{self.old}/dedicated_server --config /dev/null"
            else:
                command = "/bin/sleep 300"
            content = (
                f"[Unit]\nDescription=SCBL transaction QA {unit}\n"
                f"[Service]\nType=simple\nExecStart={command}\n"
                "Restart=on-failure\nRestartSec=1\n"
                "[Install]\nWantedBy=multi-user.target\n"
            ).encode("utf-8")
            snapshots[unit] = ManagedFileSnapshot(content, 0o644)
            (self.unit_root / unit).write_bytes(content)
        subprocess.run(("systemctl", "daemon-reload"), check=True)
        subprocess.run(
            ("systemctl", "enable", "--runtime", *RUNTIME_UNIT_NAMES), check=True
        )
        for unit in RUNTIME_UNIT_NAMES:
            (self.unit_root / unit).write_text(
                "[Service]\nType=oneshot\nExecStart=/bin/false\n",
                encoding="utf-8",
            )
        return snapshots

    def _write_checkpoint(self, migrations: dict[int, str]) -> None:
        database = self.layout.data / "dedicated/5th-echelon.db"
        database.unlink(missing_ok=True)
        with sqlite3.connect(database) as connection:
            connection.execute(
                "CREATE TABLE _sqlx_migrations ("
                "version BIGINT PRIMARY KEY, checksum BLOB NOT NULL, "
                "success BOOLEAN NOT NULL)"
            )
            for version, digest in migrations.items():
                connection.execute(
                    "INSERT INTO _sqlx_migrations(version, checksum, success) "
                    "VALUES (?, ?, 1)",
                    (version, bytes.fromhex(digest)),
                )

    def _manifest(self) -> RuntimeManifest:
        return RuntimeManifest(
            2,
            "2.0.23",
            {program: "0" * 64 for program in UNIT_PROGRAM_ARGUMENTS},
            dict(UNIT_PROGRAM_ARGUMENTS),
            {1: self.first, 2: self.second},
        )

    def test_real_systemd_recovers_complete_and_forward_migration_stacks(self) -> None:
        snapshots = self._unit_snapshots()
        provisioner = Provisioner(layout=self.layout)

        self._write_checkpoint({1: self.first})
        result = provisioner._recover_failed_install(
            RuntimeTransactionSnapshot(self.old, snapshots, {1: self.first}),
            self.new,
            self._manifest(),
        )
        self.assertIn("完整态", result)
        self.assertEqual(self.old, self.layout.current.resolve(strict=True))
        dedicated_unit = (self.unit_root / "scbl-dedicated.service").read_text(
            encoding="utf-8"
        )
        self.assertIn(f"ExecStart={self.old}/dedicated_server --config", dedicated_unit)

        activate_release(self.new, self.layout.current)
        for unit in RUNTIME_UNIT_NAMES:
            (self.unit_root / unit).write_text(
                "[Service]\nType=oneshot\nExecStart=/bin/false\n",
                encoding="utf-8",
            )
        self._write_checkpoint({1: self.first, 2: self.second})
        result = provisioner._recover_failed_install(
            RuntimeTransactionSnapshot(self.old, snapshots, {1: self.first}),
            self.new,
            self._manifest(),
        )
        self.assertIn("兼容拆分态", result)
        self.assertEqual(self.old, self.layout.current.resolve(strict=True))
        dedicated_unit = (self.unit_root / "scbl-dedicated.service").read_text(
            encoding="utf-8"
        )
        self.assertIn(f"ExecStart={self.new}/dedicated_server --config", dedicated_unit)
        provisioner._assert_required_services_active()


if __name__ == "__main__":
    unittest.main()
