from __future__ import annotations

import os
import stat
import tempfile
import unittest
from pathlib import Path

from scblctl.provision import ProvisionError, ProvisionLayout, Provisioner
from scblctl.system import CommandResult


class RecordingRunner:
    def __init__(self, observed: str = "1") -> None:
        self.observed = observed
        self.commands: list[tuple[str, ...]] = []

    def available(self, _command: str) -> bool:
        return True

    def run(self, args, *, timeout: int = 15) -> CommandResult:
        command = tuple(args)
        self.commands.append(command)
        stdout = self.observed if command == ("sysctl", "-n", "net.ipv4.ip_forward") else ""
        return CommandResult(command, 0, stdout, "")


class KernelNetworkingTests(unittest.TestCase):
    def test_ipv4_forwarding_is_persisted_loaded_and_verified(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / "90-scbl-ipv4-forwarding.conf"
            runner = RecordingRunner()
            provisioner = Provisioner(
                runner=runner,
                layout=ProvisionLayout(ipv4_forwarding_file=target),
            )

            provisioner._configure_ipv4_forwarding()

            self.assertEqual(
                "# Managed by SCBL. Required for routing overlay client traffic.\n"
                "net.ipv4.ip_forward = 1\n",
                target.read_text(encoding="utf-8"),
            )
            if os.name == "posix":
                self.assertEqual(0o644, stat.S_IMODE(target.stat().st_mode))
            self.assertEqual(
                [
                    ("sysctl", "--load", str(target)),
                    ("sysctl", "-n", "net.ipv4.ip_forward"),
                ],
                runner.commands,
            )

    def test_ipv4_forwarding_fails_closed_when_runtime_value_did_not_change(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            provisioner = Provisioner(
                runner=RecordingRunner(observed="0"),
                layout=ProvisionLayout(
                    ipv4_forwarding_file=Path(temporary)
                    / "90-scbl-ipv4-forwarding.conf"
                ),
            )
            with self.assertRaisesRegex(ProvisionError, "IPv4 转发未能收敛"):
                provisioner._configure_ipv4_forwarding()


if __name__ == "__main__":
    unittest.main()
