from __future__ import annotations

import os
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from scblctl.config import ServerConfig
from scblctl.release import UNIT_PROGRAM_ARGUMENTS, RuntimeManifest
from scblctl.templates import (
    WAIT_SCBL0,
    load_control_auth_policy,
    load_or_create_control_auth_key,
    render_dedicated_config,
    render_easytier_config,
    render_runtime_env,
    render_systemd_units,
)


class TemplateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.config = ServerConfig.new(public_host="scbl.example.com")

    @staticmethod
    def runtime_manifest() -> RuntimeManifest:
        arguments = dict(UNIT_PROGRAM_ARGUMENTS)
        return RuntimeManifest(
            2,
            "2.0.0",
            {program: "0" * 64 for program in arguments},
            arguments,
            {20260808120000: "0" * 96},
        )

    def test_tunnel_unit_uses_manager_owned_external_easytier(self) -> None:
        units = render_systemd_units(self.config, self.runtime_manifest())
        tunnel = units["scbl-tunnel.service"]
        self.assertIn("/opt/scbl/external/easytier/current/easytier-core", tunnel)
        self.assertNotIn("/opt/scbl/current/easytier-core", tunnel)

    def test_dedicated_binds_only_to_overlay(self) -> None:
        rendered = render_dedicated_config(self.config, ticket_key=bytes(range(32)))
        self.assertIn('api_server = "10.66.0.1:50051"', rendered)
        self.assertIn('listen = "10.66.0.1:80"', rendered)
        self.assertNotIn('listen = "0.0.0.0:', rendered)
        self.assertEqual(2, rendered.count("0,\n    1,\n    2,"))

    def test_easytier_uses_one_game_port_for_wss_tcp_and_quic_udp(self) -> None:
        self.config.server.server_id = "scbl-prod-01"
        rendered = render_easytier_config(self.config)
        self.assertIn("wss://0.0.0.0:11010", rendered)
        self.assertIn("quic://0.0.0.0:11010", rendered)
        self.assertIn("quic://[::]:11010", rendered)
        self.assertNotIn("udp://", rendered)
        self.assertIn('default_protocol = "quic"', rendered)
        self.assertNotIn("tcp://0.0.0.0", rendered)
        self.assertIn("disable_p2p = true", rendered)
        self.assertIn("need_p2p = false", rendered)
        self.assertIn("p2p_only = false", rendered)
        self.assertIn("disable_relay_data = false", rendered)
        self.assertIn("relay_all_peer_rpc = true", rendered)
        self.assertIn("disable_udp_hole_punching = true", rendered)
        self.assertIn("disable_tcp_hole_punching = true", rendered)
        self.assertIn("disable_sym_hole_punching = true", rendered)
        self.assertIn('hostname = "scbl-prod-01"', rendered)
        self.assertIn("enable_ipv6 = true", rendered)

        self.config.network.enable_ipv6 = False
        ipv4_only = render_easytier_config(self.config)
        self.assertNotIn("[::]", ipv4_only)
        self.assertIn("enable_ipv6 = false", ipv4_only)

    def test_runtime_environment_is_quoted_and_contains_no_shell_source(self) -> None:
        auth_token_key = bytes(range(32))
        rendered = render_runtime_env(
            self.config,
            version="2.0.0",
            auth_token_key=auth_token_key,
        )
        self.assertIn('SCBL_SECRET="', rendered)
        self.assertIn(
            f'SCBL_AUTH_TOKEN_KEY="{auth_token_key.hex()}"',
            rendered,
        )
        self.assertIn('SCBL_CONTROL_AUTH_POLICY="transition"', rendered)
        self.assertNotIn(
            f'SCBL_AUTH_TOKEN_KEY="{self.config.network.secret}"',
            rendered,
        )
        self.assertIn('SCBL_DB_PATH="/var/lib/scbl/dedicated/5th-echelon.db"', rendered)
        self.assertIn('SCBL_SERVER_MODE="stable"', rendered)
        self.assertNotIn("SCBL_ALLOW_NEWER_TEST_CLIENTS", rendered)
        self.assertIn('SCBL_QUIC_PORT="11010"', rendered)
        self.assertNotIn("export ", rendered)

        self.config.server.mode = "test"
        test_rendered = render_runtime_env(
            self.config,
            version="2.0.0",
            auth_token_key=auth_token_key,
        )
        self.assertIn('SCBL_SERVER_MODE="test"', test_rendered)

    def test_control_auth_key_is_created_once_and_reused(self) -> None:
        with tempfile.TemporaryDirectory(prefix="scbl-control-auth-key-") as temporary:
            path = Path(temporary) / "manager" / "control-auth.key"
            first_key = bytes(range(32))
            replacement_key = bytes(reversed(range(32)))
            with patch(
                "scblctl.templates.secrets.token_bytes",
                side_effect=(first_key, replacement_key),
            ) as generate:
                self.assertEqual(first_key, load_or_create_control_auth_key(path))
                self.assertEqual(first_key, load_or_create_control_auth_key(path))
            self.assertEqual(1, generate.call_count)
            self.assertEqual(first_key, path.read_bytes())
            if os.name == "posix":
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))

    def test_runtime_environment_rejects_invalid_auth_inputs(self) -> None:
        with self.assertRaisesRegex(ValueError, "exactly 32 bytes"):
            render_runtime_env(self.config, version="2.0.0", auth_token_key=b"short")
        with self.assertRaisesRegex(ValueError, "transition or required"):
            render_runtime_env(
                self.config,
                version="2.0.0",
                auth_token_key=bytes(32),
                control_auth_policy="optional",
            )

    def test_control_auth_policy_preserves_existing_required_value(self) -> None:
        with tempfile.TemporaryDirectory(prefix="scbl-control-auth-policy-") as temporary:
            runtime_env = Path(temporary) / "runtime.env"
            self.assertEqual("transition", load_control_auth_policy(runtime_env))
            runtime_env.write_text(
                'SCBL_SECRET="not-the-auth-key"\n'
                'SCBL_CONTROL_AUTH_POLICY="required"\n',
                encoding="utf-8",
            )
            self.assertEqual("required", load_control_auth_policy(runtime_env))

    def test_control_auth_policy_fails_closed_for_invalid_or_duplicate_values(self) -> None:
        with tempfile.TemporaryDirectory(prefix="scbl-control-auth-policy-") as temporary:
            runtime_env = Path(temporary) / "runtime.env"
            runtime_env.write_text(
                'SCBL_CONTROL_AUTH_POLICY="optional"\n',
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "transition or required"):
                load_control_auth_policy(runtime_env)

            runtime_env.write_text(
                'SCBL_CONTROL_AUTH_POLICY="transition"\n'
                'SCBL_CONTROL_AUTH_POLICY="required"\n',
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "exactly once"):
                load_control_auth_policy(runtime_env)

    def test_units_use_separate_users_and_hardening(self) -> None:
        units = render_systemd_units(self.config, self.runtime_manifest())
        self.assertEqual(4, len(units))
        self.assertIn("User=scbl-game", units["scbl-dedicated.service"])
        self.assertIn("User=scbl-control", units["scbl-control-plane.service"])
        self.assertIn("User=scbl-update", units["scbl-update.service"])
        for unit in units.values():
            self.assertIn("NoNewPrivileges=true", unit)
            self.assertIn("ProtectSystem=strict", unit)
        self.assertIn("CAP_NET_ADMIN", units["scbl-tunnel.service"])
        self.assertIn(
            "ExecStartPost=/usr/local/lib/scbl/wait-scbl0 10.66.0.1 30",
            units["scbl-tunnel.service"],
        )
        self.assertIn("CAP_NET_BIND_SERVICE", units["scbl-dedicated.service"])
        self.assertIn("AF_NETLINK", units["scbl-dedicated.service"])
        self.assertIn("AF_NETLINK", units["scbl-control-plane.service"])
        for name in (
            "scbl-dedicated.service",
            "scbl-control-plane.service",
            "scbl-update.service",
        ):
            self.assertIn("PrivateDevices=true", units[name])
            self.assertIn("ProtectKernelTunables=true", units[name])
            self.assertIn("RestrictNamespaces=true", units[name])
        self.assertIn("CapabilityBoundingSet=\n", units["scbl-control-plane.service"])
        self.assertIn("CapabilityBoundingSet=\n", units["scbl-update.service"])
        update = units["scbl-update.service"]
        self.assertIn(
            "--root /var/lib/scbl/client-updates --bundle-cache /var/lib/scbl/client-bundle-cache",
            update,
        )
        self.assertIn("ReadOnlyPaths=/opt/scbl/current /var/lib/scbl/client-updates", update)
        self.assertIn("ReadWritePaths=/var/lib/scbl/client-bundle-cache", update)
        self.assertNotIn("ReadWritePaths=/var/lib/scbl/client-updates", update)

    def test_modern_runtime_units_converge_on_current_symlink(self) -> None:
        units = render_systemd_units(self.config, self.runtime_manifest())
        self.assertIn(
            "ExecStart=/opt/scbl/current/dedicated_server --config",
            units["scbl-dedicated.service"],
        )
        self.assertIn(
            "ExecStart=/usr/bin/python3 /opt/scbl/current/scbl_control_plane.pyz",
            units["scbl-control-plane.service"],
        )
        self.assertIn(
            "ExecStart=/usr/bin/python3 /opt/scbl/current/scbl_update_server.py",
            units["scbl-update.service"],
        )
        self.assertNotIn("/opt/scbl/releases/", "\n".join(units.values()))

    def test_wait_scbl0_supports_a_bounded_custom_timeout(self) -> None:
        self.assertIn('timeout="${2:-20}"', WAIT_SCBL0)
        self.assertIn('seq 1 "$timeout"', WAIT_SCBL0)


if __name__ == "__main__":
    unittest.main()
