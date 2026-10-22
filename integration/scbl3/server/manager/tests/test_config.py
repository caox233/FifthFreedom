from __future__ import annotations

import os
import stat
import tempfile
import unittest
from pathlib import Path

from scblctl.config import (
    ConfigError,
    ServerConfig,
    dump_toml,
    impact_for,
    load_config,
    save_config,
)


class ConfigTests(unittest.TestCase):
    def test_new_config_round_trip_and_secret(self) -> None:
        config = ServerConfig.new(
            public_host="scbl.example.com",
            mode="test",
            server_id="scbl-test-01",
        )
        self.assertEqual([], config.validate())
        self.assertGreaterEqual(len(config.network.secret), 24)

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "server.toml"
            save_config(config, path)
            loaded = load_config(path)
            self.assertEqual(config, loaded)
            self.assertEqual("test", loaded.server.mode)
            if os.name == "posix":
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))

    def test_dump_redacts_secret(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        rendered = dump_toml(config, redact=True)
        self.assertIn('secret = "********"', rendered)
        self.assertNotIn(config.network.secret, rendered)

    def test_rejects_unknown_keys(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        rendered = dump_toml(config).replace(
            '[server]\n', '[server]\nmisspelled_setting = true\n'
        )
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "server.toml"
            path.write_text(rendered, encoding="utf-8")
            with self.assertRaisesRegex(ConfigError, "未知项"):
                load_config(path)

    def test_obsolete_easytier_version_is_rejected(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        rendered = dump_toml(config).replace(
            "[easytier]\n", '[easytier]\nversion = "2.6.4"\n'
        )
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "server.toml"
            path.write_text(rendered, encoding="utf-8")
            with self.assertRaisesRegex(ConfigError, "未知项"):
                load_config(path)

    def test_validation_rejects_public_default_secret(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        config.network.secret = "CHANGE_ME_SCBL_PUBLIC_SECRET_2026"
        self.assertTrue(any("公开默认值" in item for item in config.validate()))

    def test_network_relationship_validation(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        config.network.virtual_ip = "10.66.0.0"
        config.network.virtual_cidr = "10.66.0.0/24"
        self.assertTrue(any("网络地址或广播地址" in item for item in config.validate()))

        config = ServerConfig.new(public_host="192.0.2.10")
        config.network.virtual_ip = "2001:db8::1"
        config.network.virtual_cidr = "2001:db8::1/64"
        config.network.virtual_network = "2001:db8::/64"
        self.assertTrue(any("必须使用 IPv4" in item for item in config.validate()))

    def test_port_collisions_and_invalid_public_url_are_rejected(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        config.services.update_port = config.network.public_port
        self.assertTrue(any("端口必须互不相同" in item for item in config.validate()))

        config = ServerConfig.new(public_host="192.0.2.10")
        config.services.control_port = 21126
        self.assertTrue(any("Dedicated 固定端口" in item for item in config.validate()))

        for invalid in (
            "http://example.com:99999",
            "http://user@example.com:18080",
            "http://example.com:18080/files",
        ):
            config = ServerConfig.new(public_host="192.0.2.10")
            config.services.public_update_base_url = invalid
            self.assertTrue(any("public_update_base_url" in item for item in config.validate()))

    def test_one_game_port_is_the_only_transport_port_setting(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        self.assertEqual(11010, config.network.public_port)
        self.assertNotIn("wss_port", config.to_mapping()["network"])
        self.assertNotIn("quic_port", config.to_mapping()["network"])

    def test_set_converts_types(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        self.assertEqual(18081, config.set("services.update_port", "18081"))
        self.assertFalse(config.set("network.enable_ipv6", "false"))
        with self.assertRaises(ConfigError):
            config.set("network.enable_ipv6", "perhaps")
        with self.assertRaises(ConfigError):
            config.set("gameplay.extended_loadouts", "true")

    def test_impact_map_restarts_only_affected_components(self) -> None:
        self.assertEqual(
            ["client-metadata", "firewall", "update"],
            impact_for({"services.update_port"}),
        )
        self.assertEqual(["control"], impact_for({"services.heartbeat_ttl"}))
        self.assertEqual(["updater"], impact_for({"updates.repository"}))
        self.assertEqual(
            ["client-metadata", "firewall", "tunnel"],
            impact_for({"network.public_port"}),
        )

    def test_update_source_is_explicitly_official_by_default(self) -> None:
        config = ServerConfig.new(public_host="192.0.2.10")
        self.assertEqual("caox233/SCBL", config.updates.repository)
        rendered = dump_toml(config)
        self.assertNotIn("[trust]", rendered)
        self.assertNotIn("[testing]", rendered)
        self.assertIn("schema_version = 5", rendered)
        self.assertNotIn("[gameplay]", rendered)

    def test_public_host_is_optional_for_router_mapped_servers(self) -> None:
        config = ServerConfig.new(mode="stable", server_id="scbl-prod-01")
        self.assertEqual([], config.validate())
        config.services.public_update_base_url = "http://sc6.example.com:18080"
        self.assertEqual([], config.validate())


if __name__ == "__main__":
    unittest.main()
