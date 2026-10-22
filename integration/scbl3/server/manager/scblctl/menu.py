"""Thin human-facing SCBL menu entry.

Domain menu flows live in ``menu_server`` and ``menu_client``. Text decoding
and file selection are isolated helpers so this module stays a stable import
surface for the CLI and tests.
"""

from __future__ import annotations

import sys

from .menu_server import (
    _announcement_editor_text,
    _apply_current_config,
    _decode_announcement_editor,
    _parse_announcement_editor,
    _read_operator_text,
    _save_menu_settings,
    _select_server_file,
    run_menu,
)

__all__ = ["run_menu"]
