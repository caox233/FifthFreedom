"""Bounded public CI annotations: identities and codes, never arbitrary log text."""
from __future__ import annotations

from contextlib import contextmanager
import os
from pathlib import Path
import re

MAX_MESSAGE = 1600
MAX_ITEMS = 8
MAX_LOG_BYTES = 64 * 1024
_IDENTIFIER = re.compile(r"[A-Za-z_][A-Za-z0-9_.-]{0,179}\Z")
_TEST_ID = re.compile(r"test_[A-Za-z0-9_]+(?:\.[A-Za-z_][A-Za-z0-9_]*)+\Z")
_COMPILER_CODE = re.compile(r"\berror(?:\[(E[0-9]{4})\]|\s+((?:CS|NETSDK|NU|MSB|MC|LNK|C)[0-9]{4})\b)", re.IGNORECASE)
_EXCEPTIONS = {"RuntimeError", "ValueError", "OSError", "FileNotFoundError", "PermissionError",
               "FileExistsError", "TimeoutExpired", "CalledProcessError", "UnicodeDecodeError",
               "ImportError", "ModuleNotFoundError", "AssertionError", "XamlParseException",
               "IOException", "InvalidOperationException", "TypeInitializationException",
               "OperationalError", "IntegrityError", "DatabaseError", "ProgrammingError"}
_KNOWN_SIGNATURES = {
    "dependency-download-failed": ("failed to download", "unable to load the service index"),
    "custom-build-command-failed": ("failed to run custom build command",),
    "linker-failed": ("linking with", "linker command failed"),
    "missing-native-toolchain": ("Visual Studio C++ Build Tools", "Visual Studio C++ toolchain not found"),
    "missing-dotnet-sdk": ("Install .NET SDK 10.0.302", "A compatible .NET SDK was not found"),
    "probe-result-incomplete": ("no complete, matching nonzero RESULT",),
    "probe-timeout": ("exceeded 180 seconds",),
}


def escape_command_data(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def escape_command_property(text: str) -> str:
    return escape_command_data(text).replace(":", "%3A").replace(",", "%2C")


def safe_identifier(value: str) -> str:
    return value if _IDENTIFIER.fullmatch(value) else "unavailable"


def method_id(test) -> str:
    # subTest.id() can embed passwords, endpoints or arbitrary fixture values.
    value = getattr(test, "test_case", test).id()
    return value if len(value) <= 240 and _TEST_ID.fullmatch(value) else "unavailable-test-method"


def emit_error(message: str) -> None:
    """Only callers' structured, allowlisted summaries may enter this function."""
    bounded = message[:MAX_MESSAGE]
    if len(message) > MAX_MESSAGE:
        bounded += " [truncated]"
    if os.environ.get("GITHUB_ACTIONS") == "true":
        print("::error title=Fifth Freedom CI::" + escape_command_data(bounded), flush=True)
    else:
        print("CI_ERROR_SUMMARY " + escape_command_data(bounded), flush=True)


def test_failure_summary(suite: str, result) -> str:
    names = sorted({method_id(test) for test, _ in [*result.failures, *result.errors]}
                   | {method_id(test) for test in result.unexpectedSuccesses})
    shown = names[:MAX_ITEMS]
    suffix = f"; additional_failed_methods={len(names) - len(shown)}" if len(names) > len(shown) else ""
    exceptions = sorted({kind for _, trace in [*result.failures, *result.errors]
                         for kind in known_exception_types(trace)})[:MAX_ITEMS]
    return (f"suite={safe_identifier(suite)}; executed_methods={result.testsRun}; "
            f"failed_methods={','.join(shown) or 'none'}; assertion_events={len(result.failures)}; "
            f"error_events={len(result.errors)}; unexpected_successes={len(result.unexpectedSuccesses)}"
            f"; exception_types={','.join(exceptions) or 'unclassified'}"
            f"{suffix}; full diagnostics remain in the run artifacts")


def known_exception_types(text: str) -> list[str]:
    """Report only fixed known class names; never the adjacent traceback text."""
    return sorted({name for name in _EXCEPTIONS if re.search(r"\b" + name + r"\b", text)})[:MAX_ITEMS]


def known_ui_labels(source_paths) -> set[str]:
    """Only exact static Check(..., \"label\") source literals are publishable.

    Dynamic concatenations, runtime values and unknown exception text are omitted.
    A narrow parser may miss labels; missing details are preferable to disclosure.
    """
    labels = set()
    for path in source_paths:
        try:
            text = Path(path).read_text(encoding="utf-8-sig")
        except OSError:
            continue
        for label in re.findall(r'\bCheck\([^;]{0,4096},\s*"([^"\\\r\n]{1,160})"\s*\);', text):
            if all(ord(char) >= 32 for char in label):
                labels.add(label)
    return labels


def diagnostic_details(text: str, *, ui_labels=()) -> dict:
    # Only fixed-shape codes and exact source-known labels leave the raw log.
    codes = sorted({(a or b).upper() for a, b in _COMPILER_CODE.findall(text)})[:MAX_ITEMS]
    labels = []
    allowed = set(ui_labels)
    for line in text.splitlines():
        if line.startswith("FAIL "):
            candidate = line[5:]
        elif line.startswith("System.Exception: "):
            candidate = line[len("System.Exception: "):]
        elif line.startswith("System.InvalidOperationException: "):
            candidate = line[len("System.InvalidOperationException: "):]
        else:
            continue
        if candidate in allowed and candidate not in labels:
            labels.append(candidate)
    signatures = [name for name, needles in _KNOWN_SIGNATURES.items() if any(needle in text for needle in needles)]
    exceptions = known_exception_types(text)
    return {"compiler_codes": codes, "ui_checks": labels[:MAX_ITEMS],
            "categories": signatures[:MAX_ITEMS], "exception_types": exceptions}


def read_bounded_logs(paths) -> str:
    chunks = []
    for path in list(paths)[:MAX_ITEMS]:
        try:
            with Path(path).open("rb") as source:
                source.seek(0, 2)
                source.seek(max(0, source.tell() - MAX_LOG_BYTES))
                chunks.append(source.read(MAX_LOG_BYTES).decode("utf-8", errors="replace"))
        except OSError:
            continue
    return "\n".join(chunks)


class AnnotatedFailure(RuntimeError):
    """The structured failure was already emitted; do not duplicate it upstream."""


@contextmanager
def diagnostic_step(label: str, logs=lambda: (), ui_labels=()):
    try:
        yield
    except AnnotatedFailure:
        raise
    except Exception as error:
        details = diagnostic_details(str(error) + "\n" + read_bounded_logs(logs()), ui_labels=ui_labels)
        kind = type(error).__name__ if type(error).__name__ in _EXCEPTIONS else "unclassified-exception"
        fields = [f"step={safe_identifier(label)}", f"exception={kind}"]
        for key, values in details.items():
            if values:
                fields.append(key + "=" + " | ".join(values))
        fields.append("full diagnostics remain in the run artifacts")
        emit_error("; ".join(fields))
        raise AnnotatedFailure(f"CI step {safe_identifier(label)} failed; see its diagnostic artifact") from error
