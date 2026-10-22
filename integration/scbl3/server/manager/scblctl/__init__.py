"""SCBL 3.0 server management core."""

import json
from pathlib import Path

from .config import CONFIG_SCHEMA_VERSION


def _source_version() -> str:
    """Read the sole editable version source in a source checkout.

    The standalone Manager archive receives a literal version during build, so
    installed servers never depend on repository files.
    """

    for parent in Path(__file__).resolve().parents:
        candidate = parent / "COMPONENT_VERSIONS.json"
        if candidate.is_file():
            raw = json.loads(candidate.read_text(encoding="utf-8"))
            components = raw.get("serverComponents")
            value = components.get("manager") if isinstance(components, dict) else None
            if isinstance(value, str):
                return value
    return "0.0.0+source"


__all__ = ["CONFIG_SCHEMA_VERSION"]
__version__ = _source_version()
