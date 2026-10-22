from __future__ import annotations

import io
import os
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import patch

from scblctl.menu import (
    _apply_current_config,
    _announcement_editor_text,
    _decode_announcement_editor,
    _parse_announcement_editor,
    _read_operator_text,
    _save_menu_settings,
    _select_server_file,
)
from scblctl.config import ServerConfig, save_config
from scblctl.live_state import LiveState, LiveStateError
from scblctl.menu_server import _confirm_online_override
from scblctl.paths import RuntimePaths


class MenuInputTests(unittest.TestCase):
    def test_online_operation_requires_explicit_force(self) -> None:
        config = ServerConfig.new()
        with (
            patch(
                "scblctl.live_state.read_live_state",
                return_value=LiveState(2, ("one", "two")),
            ),
            patch("builtins.input", return_value="FORCE"),
            redirect_stdout(io.StringIO()) as output,
        ):
            self.assertEqual(
                (True, True),
                _confirm_online_override(config, action="安装此更新"),
            )
        self.assertIn("当前有 2 名玩家在线", output.getvalue())

    def test_online_operation_cancels_without_force(self) -> None:
        config = ServerConfig.new()
        with (
            patch(
                "scblctl.live_state.read_live_state",
                side_effect=LiveStateError("状态不可用"),
            ),
            patch("builtins.input", return_value="yes"),
            redirect_stdout(io.StringIO()) as output,
        ):
            self.assertEqual(
                (False, False),
                _confirm_online_override(config, action="安装此更新"),
            )
        self.assertIn("已取消", output.getvalue())

    def test_menu_settings_save_valid_common_values(self) -> None:
        config = ServerConfig.new(public_host="sc6.example.invalid")
        with tempfile.TemporaryDirectory() as temporary:
            paths = RuntimePaths(Path(temporary) / "server.toml", Path(temporary) / "state")
            with redirect_stdout(io.StringIO()):
                _save_menu_settings(
                    paths,
                    config,
                    {
                        "server.mode": "test",
                        "services.public_update_base_url": "http://test.example.invalid:18081",
                    },
                )
            saved = paths.config.read_text(encoding="utf-8")
        self.assertIn('mode = "test"', saved)
        self.assertIn('public_update_base_url = "http://test.example.invalid:18081"', saved)

    def test_apply_current_config_stops_while_players_are_online(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            paths = RuntimePaths(Path(temporary) / "server.toml", Path(temporary) / "state")
            save_config(ServerConfig.new(), paths.config)
            live = type("Live", (), {"online_count": 2})()
            with (
                patch("scblctl.live_state.read_live_state", return_value=live),
                patch("scblctl.provision.Provisioner") as provisioner,
                redirect_stdout(io.StringIO()),
            ):
                _apply_current_config(paths)
            provisioner.assert_not_called()

    def test_apply_current_config_repairs_verified_release_when_offline(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            paths = RuntimePaths(Path(temporary) / "server.toml", Path(temporary) / "state")
            save_config(ServerConfig.new(), paths.config)
            live = type("Live", (), {"online_count": 0})()
            with (
                patch("scblctl.live_state.read_live_state", return_value=live),
                patch("builtins.input", return_value="APPLY"),
                patch("scblctl.provision.Provisioner") as provisioner,
                redirect_stdout(io.StringIO()),
            ):
                provisioner.return_value.repair.return_value = Path("/opt/scbl/releases/3.0.2")
                _apply_current_config(paths)
            provisioner.return_value.repair.assert_called_once()

    def test_announcement_editor_accepts_multiline_utf8_content(self) -> None:
        source = """# comment
[中文标题]
公告
[中文内容]
欢迎来到5TH，
CN交流群：709112052 等你来战！
[英文标题]
Notice
[英文内容]
Welcome.
[级别]
info
[客户端版本]

"""
        values = _parse_announcement_editor(source)
        self.assertEqual("公告", values["title"])
        self.assertEqual("欢迎来到5TH，\nCN交流群：709112052 等你来战！", values["body"])
        self.assertEqual("Notice", values["title_en"])

    def test_announcement_editor_decodes_gb18030_saved_by_terminal(self) -> None:
        expected = "[中文标题]\n公告\n[中文内容]\n等你来战！\n[英文标题]\n\n[英文内容]\n\n[级别]\ninfo\n[客户端版本]\n"
        self.assertEqual(expected, _decode_announcement_editor(expected.encode("gb18030")))

    def test_announcement_editor_template_keeps_existing_values(self) -> None:
        status = type(
            "Status",
            (),
            {"title": "公告", "body": "正文", "title_en": "", "body_en": "", "level": "warning", "version": "2.0.15"},
        )()
        values = _parse_announcement_editor(_announcement_editor_text(status, "active", ""))
        self.assertEqual("公告", values["title"])
        self.assertEqual("warning", values["level"])

    def test_operator_text_uses_the_existing_text_stream(self) -> None:
        value = "欢迎来到5th Echelon，等待你的加入！"
        with patch("builtins.input", return_value=value) as read:
            self.assertEqual(value, _read_operator_text("中文标题："))
        read.assert_called_once_with("中文标题：")

    def test_operator_text_accepts_gb18030_from_utf8_terminal(self) -> None:
        expected = "欢迎来到5TH，CN交流群：709112052 等你来战！"
        stream = io.TextIOWrapper(
            io.BytesIO(expected.encode("gb18030") + b"\n"),
            encoding="utf-8",
            errors="surrogateescape",
        )
        with (
            patch("scblctl.menu_text.sys.stdin", stream),
            patch("scblctl.menu_text.sys.stdout", io.StringIO()),
        ):
            self.assertEqual(expected, _read_operator_text("中文内容："))

    def test_operator_text_accepts_utf8(self) -> None:
        expected = "欢迎来到5TH，CN交流群：709112052 等你来战！"
        stream = io.TextIOWrapper(
            io.BytesIO(expected.encode("utf-8") + b"\n"),
            encoding="utf-8",
            errors="surrogateescape",
        )
        with (
            patch("scblctl.menu_text.sys.stdin", stream),
            patch("scblctl.menu_text.sys.stdout", io.StringIO()),
        ):
            self.assertEqual(expected, _read_operator_text("中文内容："))

    def test_server_file_selector_lists_newest_matching_patch_first(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            cache = Path(temporary)
            old = cache / "SCBL-Client-Patch-v2.0.6.scblpatch"
            new = cache / "SCBL-Client-Patch-v2.0.7.scblpatch"
            ignored = cache / "SCBL-Server-v2.0.7-update.zip"
            for path in (old, new, ignored):
                path.write_bytes(b"patch")
            os.utime(old, (1, 1))
            os.utime(new, (2, 2))
            with (
                patch("builtins.input", return_value="1"),
                patch("scblctl.menu.sys.stdout", io.StringIO()),
            ):
                selected = _select_server_file(
                    "客户端补丁",
                    suffixes=(".scblpatch",),
                    prefixes=("SCBL-Client-",),
                    cache=cache,
                )
            self.assertEqual(new, selected)


if __name__ == "__main__":
    unittest.main()
