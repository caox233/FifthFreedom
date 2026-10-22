"""Portable schema guards; wire decoding and admission run in the Rust suites."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
LAN = ROOT / "src/native/shared/api/proto/users.proto"
LAUNCHER = ROOT / "src/launcher/src/users.proto"
DONOR = ROOT / "integration/scbl3/shared/api/proto/users.proto"


def declarations(path):
    text = re.sub(r"//[^\n]*", "", path.read_text(encoding="utf-8"))
    messages = {}
    for name, body in re.findall(r"message\s+(\w+)\s*\{([^{}]*)\}", text):
        fields = {}
        numbers = set()
        for repeated, kind, field, tag in re.findall(
            r"\b(repeated\s+)?(\w+)\s+(\w+)\s*=\s*(\d+)\s*;", body
        ):
            if field in fields or int(tag) in numbers:
                raise ValueError(f"duplicate field in {name}")
            numbers.add(int(tag))
            fields[field] = (kind, int(tag), bool(repeated))
        messages[name] = fields
    methods = {}
    for name, body in re.findall(r"service\s+(\w+)\s*\{([^{}]*)\}", text):
        methods[name] = {
            method: (request, response)
            for method, request, response in re.findall(
                r"rpc\s+(\w+)\s*\(\s*(\w+)\s*\)\s*returns\s*\(\s*(\w+)\s*\)", body
            )
        }
    return messages, methods


class ProtocolCompatibilityTests(unittest.TestCase):
    def test_launcher_and_server_share_the_complete_users_contract(self):
        self.assertEqual(declarations(LAN), declarations(LAUNCHER))

    def test_every_donor_field_retains_its_number_type_and_cardinality(self):
        baseline, _ = declarations(LAN)
        donor, _ = declarations(DONOR)
        for message, fields in donor.items():
            with self.subTest(message=message):
                self.assertIn(message, baseline)
                for name, declaration in fields.items():
                    self.assertEqual(baseline[message].get(name), declaration, name)

    def test_every_donor_rpc_is_retained_but_donor_is_not_lan_compatible(self):
        _, baseline = declarations(LAN)
        _, donor = declarations(DONOR)
        for service, methods in donor.items():
            for name, signature in methods.items():
                self.assertEqual(baseline[service].get(name), signature)
        self.assertEqual(
            baseline["Users"].get("GetCapabilities"),
            ("CapabilitiesRequest", "CapabilitiesResponse"),
        )
        self.assertNotIn("GetCapabilities", donor["Users"])

    def test_login_and_registration_keep_product_and_package_tags(self):
        messages, _ = declarations(LAN)
        for name in ("LoginRequest", "RegisterRequest"):
            with self.subTest(message=name):
                self.assertEqual(messages[name]["username"], ("string", 1, False))
                self.assertEqual(messages[name]["password"], ("string", 2, False))
                self.assertEqual(messages[name]["client_product"], ("string", 10, False))
                self.assertEqual(messages[name]["package_identity"], ("PackageIdentity", 11, False))

    def test_package_identity_requires_all_four_fixed_role_fields(self):
        messages, _ = declarations(LAN)
        expected = {
            "schema": ("uint32", 1, False),
            "release_id": ("string", 2, False),
            "compat_id": ("string", 3, False),
            "manifest_sha256": ("string", 4, False),
            **{name: ("ReleaseComponent", tag, False) for tag, name in enumerate(
                ("launcher", "main_dll", "gameplay_dll", "server"), 5
            )},
        }
        self.assertEqual(messages["PackageIdentity"], expected)

    def test_capabilities_preserve_ready_ports_and_observer_fields(self):
        messages, _ = declarations(LAN)
        fields = [
            ("product", "string"), ("protocol_version", "uint32"),
            ("version", "string"), ("ready", "bool"), ("official_only", "bool"),
            ("config_port", "uint32"), ("content_port", "uint32"),
            ("auth_port", "uint32"), ("secure_port", "uint32"),
            ("observer_allowed", "bool"),
        ]
        self.assertEqual(messages["CapabilitiesResponse"], {
            name: (kind, tag, False) for tag, (name, kind) in enumerate(fields, 1)
        })


class StartupWiringTests(unittest.TestCase):
    def test_explicit_profile_is_checked_before_persistent_or_network_effects(self):
        # This is only a wiring guard; deployment_profile.rs also has executable
        # Rust tests. It protects moving the gate below LAN-only startup effects.
        source = (ROOT / "src/native/server/dedicated-server/src/main.rs").read_text()
        self.assertIn('default = "DeploymentProfile::Lan"', source)
        startup = source[source.index("fn main()") : source.index("#[cfg(test)]\nmod user_packet_tests")]
        gate = startup.index("args.deployment_profile.startup_plan()?")
        for effect in ("verify_server_package", "create_dir_all", "set_current_dir", "Storage::init", "lan_runtime::initialize", "spawn_required_service"):
            self.assertLess(gate, startup.index(effect), effect)


if __name__ == "__main__":
    unittest.main()
