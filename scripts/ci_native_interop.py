"""Bind the explicit managed IPC probe to this Cargo invocation's test artifact."""
from __future__ import annotations

import json
from pathlib import Path
import re

RESULT = ("RESULT 3 native-managed IPC interoperability checks passed; synthetic owned children only; "
          "package/DLL admission and game not exercised.")


def test_executable(text: str, target: Path) -> Path:
    """Do not infer executable identity from directory glob order or timestamps."""
    def unique(rows):
        result = {}
        for key, value in rows:
            if key in result:
                raise ValueError("NATIVE_INTEROP_CARGO_METADATA_INVALID")
            result[key] = value
        return result
    try:
        rows = [json.loads(line, object_pairs_hook=unique) for line in text.splitlines() if line.strip()]
        if any(type(row) is not dict for row in rows):
            raise ValueError()
        completed = [row for row in rows if row.get("reason") == "build-finished"]
        selected = [row for row in rows if row.get("reason") == "compiler-artifact"
                    and row.get("target", {}).get("name") == "hooks"
                    and row.get("profile", {}).get("test") is True
                    and row.get("executable") is not None]
        if len(completed) != 1 or completed[0].get("success") is not True or len(selected) != 1:
            raise ValueError()
        name = selected[0]["executable"]
        if type(name) is not str:
            raise ValueError()
        path = Path(name)
        if (not path.is_absolute() or not re.fullmatch(r"hooks-[0-9a-f]+\.exe", path.name)
                or path.is_symlink() or not path.is_file()
                or not path.resolve().is_relative_to(target.resolve())):
            raise ValueError()
        return path.resolve()
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        raise ValueError("NATIVE_INTEROP_CARGO_ARTIFACT_INVALID") from None


def require_summary(text: str) -> int:
    lines = text.splitlines()
    if (sum(line.startswith("PASS ") for line in lines) != 3
            or [line for line in lines if line.startswith("RESULT ")] != [RESULT]
            or any("FAIL" in line or line.startswith("SKIP ") for line in lines)):
        raise ValueError("NATIVE_INTEROP_RESULT_INVALID")
    return 3
