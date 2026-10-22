from __future__ import annotations

import hashlib
import sqlite3
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scblctl.provision import (
    ManagedFileSnapshot,
    ProvisionError,
    ProvisionLayout,
    Provisioner,
    RUNTIME_UNIT_NAMES,
    RuntimeTransactionSnapshot,
)
from scblctl.release import UNIT_PROGRAM_ARGUMENTS, RuntimeManifest
from scblctl.system import CommandResult


class RecordingRunner:
    def __init__(self) -> None:
        self.commands: list[tuple[str, ...]] = []

    def available(self, _command: str) -> bool:
        return True

    def run(self, args, *, timeout: int = 15) -> CommandResult:
        command = tuple(args)
        self.commands.append(command)
        if command[:2] == ("systemctl", "show"):
            stdout = (
                "LoadState=loaded\nActiveState=active\nSubState=running\n"
                "UnitFileState=enabled\nExecMainStatus=0\n"
                "Result=success\nNRestarts=0"
            )
        else:
            stdout = ""
        return CommandResult(command, 0, stdout, "")


class GrowingRestartRunner(RecordingRunner):
    def run(self, args, *, timeout: int = 15) -> CommandResult:
        command = tuple(args)
        if command[:2] != ("systemctl", "show"):
            return super().run(command, timeout=timeout)
        observation = sum(
            1
            for previous in self.commands
            if previous[:2] == ("systemctl", "show")
        ) // len(RUNTIME_UNIT_NAMES)
        self.commands.append(command)
        stdout = (
            "LoadState=loaded\nActiveState=active\nSubState=running\n"
            "UnitFileState=enabled\nExecMainStatus=0\nResult=success\n"
            f"NRestarts={observation}"
        )
        return CommandResult(command, 0, stdout, "")


class RuntimeTransactionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="scbl-runtime-transaction-")
        self.root = Path(self.temporary.name)
        self.layout = ProvisionLayout(
            current=self.root / "opt/scbl/current",
            releases=self.root / "opt/scbl/releases",
            cache=self.root / "var/cache/scbl",
            data=self.root / "var/lib/scbl",
            config_root=self.root / "etc/scbl",
            unit_root=self.root / "etc/systemd/system",
            wait_script=self.root / "usr/local/lib/scbl/wait-scbl0",
        )
        self.layout.current.parent.mkdir(parents=True)
        self.layout.unit_root.mkdir(parents=True)
        (self.layout.data / "dedicated").mkdir(parents=True)
        self.previous = self.layout.releases / "2.0.21-old"
        self.target = self.layout.releases / "2.0.23-new"
        self.previous.mkdir(parents=True)
        self.target.mkdir(parents=True)
        (self.previous / "dedicated_server").write_bytes(b"old")
        (self.target / "dedicated_server").write_bytes(b"new")
        self.runner = RecordingRunner()
        self.provisioner = Provisioner(
            self.runner, layout=self.layout, sleep=lambda _seconds: None
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    @staticmethod
    def digest(payload: bytes) -> str:
        return hashlib.sha384(payload).hexdigest()

    def manifest(self, migrations: dict[int, str]) -> RuntimeManifest:
        return RuntimeManifest(
            2,
            "2.0.23",
            {program: "0" * 64 for program in UNIT_PROGRAM_ARGUMENTS},
            dict(UNIT_PROGRAM_ARGUMENTS),
            migrations,
        )

    def snapshots(self) -> dict[str, ManagedFileSnapshot]:
        result: dict[str, ManagedFileSnapshot] = {}
        for name in RUNTIME_UNIT_NAMES:
            if name == "scbl-dedicated.service":
                content = (
                    b"[Service]\n"
                    b"ExecStart=/opt/scbl/current/dedicated_server "
                    b"--config /etc/scbl/dedicated.toml\n"
                )
            else:
                content = f"[Service]\nDescription={name}\n".encode()
            result[name] = ManagedFileSnapshot(content, 0o644)
            (self.layout.unit_root / name).write_bytes(b"new unit")
        return result

    def write_checkpoint(self, migrations: dict[int, str]) -> None:
        database = self.layout.data / "dedicated/5th-echelon.db"
        connection = sqlite3.connect(database)
        try:
            connection.execute(
                "CREATE TABLE _sqlx_migrations ("
                "version BIGINT PRIMARY KEY, checksum BLOB NOT NULL, success BOOLEAN NOT NULL)"
            )
            for version, digest in migrations.items():
                connection.execute(
                    "INSERT INTO _sqlx_migrations(version, checksum, success) "
                    "VALUES (?, ?, 1)",
                    (version, bytes.fromhex(digest)),
                )
            connection.commit()
        finally:
            connection.close()

    def test_new_migration_recovers_old_stack_with_new_compatible_dedicated(self) -> None:
        first = self.digest(b"first")
        second = self.digest(b"second")
        self.write_checkpoint({1: first, 2: second})
        snapshot = RuntimeTransactionSnapshot(
            self.previous,
            self.snapshots(),
            {1: first},
        )

        with patch("scblctl.provision.activate_release") as activate:
            result = self.provisioner._recover_failed_install(
                snapshot,
                self.target,
                self.manifest({1: first, 2: second}),
            )

        self.assertIn("兼容拆分态", result)
        activate.assert_called_once_with(self.previous, self.layout.current)
        dedicated = (self.layout.unit_root / "scbl-dedicated.service").read_text()
        self.assertIn(
            f"ExecStart={self.target / 'dedicated_server'} --config", dedicated
        )
        self.assertNotIn("ExecStart=/opt/scbl/current/dedicated_server", dedicated)
        self.assertIn(("systemctl", "daemon-reload"), self.runner.commands)
        shown = [
            command[2]
            for command in self.runner.commands
            if command[:2] == ("systemctl", "show")
        ]
        self.assertEqual(list(RUNTIME_UNIT_NAMES) * 2, shown)

    def test_health_failure_without_new_migration_restores_exact_units(self) -> None:
        first = self.digest(b"first")
        self.write_checkpoint({1: first})
        units = self.snapshots()
        snapshot = RuntimeTransactionSnapshot(self.previous, units, {1: first})

        with patch("scblctl.provision.activate_release") as activate:
            result = self.provisioner._recover_failed_install(
                snapshot,
                self.target,
                self.manifest({1: first}),
            )

        self.assertIn("完整态", result)
        activate.assert_called_once_with(self.previous, self.layout.current)
        for name, expected in units.items():
            self.assertEqual(expected.content, (self.layout.unit_root / name).read_bytes())

    def test_recovery_fails_closed_when_migration_history_changes(self) -> None:
        before = self.digest(b"before")
        after = self.digest(b"after")
        self.write_checkpoint({1: after})
        snapshot = RuntimeTransactionSnapshot(
            self.previous,
            self.snapshots(),
            {1: before},
        )

        with self.assertRaisesRegex(ProvisionError, "被删除或改写"):
            self.provisioner._recover_failed_install(
                snapshot,
                self.target,
                self.manifest({1: after}),
            )

    def test_four_service_health_rejects_growing_restart_counters(self) -> None:
        runner = GrowingRestartRunner()
        provisioner = Provisioner(
            runner,
            layout=self.layout,
            sleep=lambda _seconds: None,
        )
        with self.assertRaisesRegex(ProvisionError, "NRestarts"):
            provisioner._assert_required_services_active()


if __name__ == "__main__":
    unittest.main()
