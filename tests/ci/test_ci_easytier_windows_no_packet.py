"""Independent selected-graph and static build gates, without running a core/driver."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import struct
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))
import ci_easytier_windows_no_packet as build
from test_pe_imports import fixture


def graph():
    return ("easytier v2.6.4 (C:\\synthetic\\easytier)|" + ",".join(sorted(build.FEATURES)) + "\n"
            "pnet v0.35.0|serde\npnet_base v0.35.0|serde,std\npnet_packet v0.35.0|\n"
            "ipnetwork v0.20.0|default,serde\nnetwork-interface v2.0.5|default\n").encode()


class NoPacketBuildTests(unittest.TestCase):
    def test_selected_graph_preserves_std_packet_types_without_capture_backends(self):
        result = build.selected_graph(graph())
        self.assertEqual(result["capture_dependencies"], [])
        self.assertTrue(result["metadata_workspace_union_is_not_selected_graph"])
        self.assertEqual(result["edges"], ["normal", "build"])
        self.assertFalse(result["default_features"])

    def test_capture_package_or_default_feature_reactivation_is_rejected(self):
        mutations = [graph() + (name + " v0.35.0|default\n").encode() for name in build.FORBIDDEN]
        mutations += [graph().replace(b"pnet v0.35.0|serde", b"pnet v0.35.0|serde,std"),
                      graph().replace(b"|ff-windows", b"|default,ff-windows"),
                      graph().replace(b"|ff-windows", b"|faketcp,ff-windows"),
                      graph().replace(b"pnet_base v0.35.0|serde,std", b"pnet_base v0.35.0|serde")]
        for value in mutations:
            with self.assertRaises(build.paired.BuildError):
                build.selected_graph(value)

    def test_cargo_repeated_nodes_and_build_contexts_do_not_hide_forbidden_nodes(self):
        value = graph() + b"syn v2.0.117|default\nsyn v2.0.117|default (*)\nsyn v2.0.117|default,full\n"
        rows = build.selected_graph(value)["selected_nodes"]
        self.assertEqual(len([row for row in rows if row["name"] == "syn"]), 2)
        with self.assertRaisesRegex(build.paired.BuildError, "CAPTURE_DEPENDENCY"):
            build.selected_graph(value + b"pnet_datalink v0.35.0|std (*)\n")

    def test_graph_shape_version_missing_dependency_and_duplicate_features_fail(self):
        for value in (b"", b"\xff", graph() + b"unrecognized\n", graph().replace(b"0.35.0", b"0.35.1"),
                      graph().replace(b"network-interface v2.0.5|default\n", b""),
                      graph().replace(b"pnet v0.35.0|serde", b"pnet v0.35.0|serde,serde")):
            with self.assertRaises(build.paired.BuildError):
                build.selected_graph(value)

    def test_normal_and_delay_packet_imports_are_both_rejected(self):
        for normal in (False, True):
            data, directory, _ = fixture()
            struct.pack_into("<H", data, 0x84, 0x8664)
            if normal:
                data[0x400:0x40d] = b"Packet.dll\0\0\0"
                data[directory + 13 * 8:directory + 14 * 8] = bytes(8)
            with self.assertRaisesRegex(build.paired.BuildError, "NO_PACKET_IMPORT"):
                build.inspect_without_packet(bytes(data))
        data, directory, _ = fixture()
        struct.pack_into("<H", data, 0x84, 0x8664)
        data[directory + 13 * 8:directory + 14 * 8] = bytes(8)
        result = build.inspect_without_packet(bytes(data))
        self.assertEqual(result["imports"], {"schema": 1, "normal": ["kernel32.dll"], "delay": []})

    def test_recipe_rejects_wrong_target_default_features_or_changed_original_pin(self):
        original = json.loads(build.LOCK.read_bytes())
        build.load_base(original)
        for field, value in (("target", "x86_64-unknown-linux-gnu"), ("no_default_features", False),
                             ("features", [*original["build"]["features"], "faketcp"]), ("execute_binaries", True)):
            changed = copy.deepcopy(original)
            changed["build"][field] = value
            with self.assertRaises(build.paired.BuildError):
                build.load_base(changed)
        changed = copy.deepcopy(original)
        changed["base_recipe"]["sha256"] = "0" * 64
        with self.assertRaises(build.source.PreparationError):
            build.load_base(changed)

    def test_exact_production_module_is_bound_to_contract_and_patch(self):
        lock = json.loads(build.LOCK.read_bytes())
        build.verify_contracts(lock)
        module = lock["linked_module"]
        row = next(item for item in lock["files"] if item["path"] == module["production_path"])
        self.assertIsNone(row["before"])
        self.assertEqual(row["after"], {key: module[key] for key in ("bytes", "sha256")})
        patch_data = build.source.checked_bytes(build.ROOT / lock["patch"]["path"], lock["patch"], 1024 * 1024)
        self.assertIn(("+++ b/" + module["production_path"]).encode(), patch_data)

    def test_changed_source_or_reintroduced_import_library_cannot_reuse_receipt(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            for name in ("easytier-source", "tun-source"):
                (work / name).mkdir()
                (work / name / "source.rs").write_bytes(b"// synthetic source identity fixture\n")
            rows = {"easytier": build.paired.inventory(work / "easytier-source"),
                    "tun": build.paired.inventory(work / "tun-source")}
            receipt = {"source_files": rows, "source_tree_sha256": build.source.sha(build.source.encoded(rows))}
            build.require_prepared(work, receipt)
            (work / "tun-source/source.rs").write_bytes(b"changed")
            with self.assertRaisesRegex(build.paired.BuildError, "SOURCE_CHANGED"):
                build.require_prepared(work, receipt)
            (work / "easytier-source/Packet.lib").write_bytes(b"!<arch>\n")
            with self.assertRaisesRegex(build.paired.BuildError, "BINARY_INPUT"):
                build.require_prepared(work, receipt)


if __name__ == "__main__":
    unittest.main()
