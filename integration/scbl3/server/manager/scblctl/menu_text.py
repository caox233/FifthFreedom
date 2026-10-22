from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path


_ANNOUNCEMENT_EDITOR_SECTIONS = {
    "[中文标题]": "title",
    "[中文内容]": "body",
    "[英文标题]": "title_en",
    "[英文内容]": "body_en",
    "[级别]": "level",
    "[客户端版本]": "version",
}


def announcement_editor_text(
    status: object, kind: str, current_client_version: str
) -> str:
    def field(name: str, default: str = "") -> str:
        value = getattr(status, name, default)
        return value if isinstance(value, str) else default

    level = field("level", "info") or "info"
    version = field("version") or current_client_version
    return "\n".join(
        (
            "# SCBL 公告编辑器（保存：Ctrl+O、回车；退出：Ctrl+X）",
            "# 可写多行正文；请保留下面的方括号标题。保存后由 SCBL 统一发布为 UTF-8。",
            "[中文标题]",
            field("title"),
            "[中文内容]",
            field("body"),
            "[英文标题]",
            field("title_en"),
            "[英文内容]",
            field("body_en"),
            "[级别]",
            level if kind != "update" else "info",
            "[客户端版本]",
            version if kind == "update" else "",
            "",
        )
    )


def decode_announcement_editor(data: bytes) -> str:
    for encoding in ("utf-8-sig", "gb18030"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    raise ValueError("公告文件编码无法识别；请在 nano 中使用 UTF-8 或 GB18030/GBK")


def parse_announcement_editor(text: str) -> dict[str, str]:
    sections: dict[str, list[str]] = {
        name: [] for name in _ANNOUNCEMENT_EDITOR_SECTIONS.values()
    }
    current: str | None = None
    for raw_line in text.splitlines():
        marker = raw_line.strip()
        if marker in _ANNOUNCEMENT_EDITOR_SECTIONS:
            current = _ANNOUNCEMENT_EDITOR_SECTIONS[marker]
            continue
        if current is None:
            if marker and not marker.startswith("#"):
                raise ValueError("公告模板格式错误；请保留方括号标题")
            continue
        sections[current].append(raw_line)

    values = {name: "\n".join(lines).strip() for name, lines in sections.items()}
    if not values["title"] or not values["body"]:
        raise ValueError("中文标题和中文内容不能为空")
    if values["level"] not in {"info", "warning", "error"}:
        raise ValueError("级别必须是 info、warning 或 error")
    return values


def edit_announcement_with_nano(
    manager: object, kind: str, status: object
) -> dict[str, str]:
    current_version = manager.current_client_version() if kind == "update" else ""
    descriptor, temporary_name = tempfile.mkstemp(
        prefix="scbl-announcement-", suffix=".txt"
    )
    editor_path = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
            output.write(announcement_editor_text(status, kind, current_version))
        environment = os.environ.copy()
        environment["LANG"] = "C.UTF-8"
        environment["LC_ALL"] = "C.UTF-8"
        try:
            result = subprocess.run(
                ["nano", str(editor_path)], env=environment, check=False
            )
        except FileNotFoundError as exc:
            raise OSError("未安装 nano，请先安装 nano 后重试") from exc
        if result.returncode != 0:
            raise OSError(f"nano 未正常退出（代码 {result.returncode}）")
        values = parse_announcement_editor(decode_announcement_editor(editor_path.read_bytes()))
        if kind != "update":
            values["version"] = ""
        elif not values["version"]:
            values["version"] = current_version
        return values
    finally:
        editor_path.unlink(missing_ok=True)


def read_operator_text(prompt: str) -> str:
    """Read UTF-8 or GB18030 operator text without mixing input buffers."""
    value = input(prompt)
    encoding = getattr(sys.stdin, "encoding", None) or "utf-8"
    try:
        raw = value.encode(encoding, errors="surrogateescape")
    except (LookupError, UnicodeEncodeError):
        return value
    for candidate in ("utf-8-sig", "gb18030"):
        try:
            return raw.decode(candidate)
        except UnicodeDecodeError:
            continue
    raise ValueError("终端文字编码无法识别；请使用 UTF-8 或 GB18030/GBK")


def configure_terminal_input() -> None:
    """Preserve non-UTF-8 bytes so free-form menu fields can decode them."""
    reconfigure = getattr(sys.stdin, "reconfigure", None)
    if not callable(reconfigure):
        return
    try:
        reconfigure(errors="surrogateescape")
    except (LookupError, OSError, ValueError):
        pass
