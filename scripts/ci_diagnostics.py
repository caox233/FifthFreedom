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
               "OperationalError", "IntegrityError", "DatabaseError", "ProgrammingError", "LifecycleError"}
_KNOWN_SIGNATURES = {
    "dependency-download-failed": ("failed to download", "unable to load the service index"),
    "custom-build-command-failed": ("failed to run custom build command",),
    "linker-failed": ("linking with", "linker command failed"),
    "missing-native-toolchain": ("Visual Studio C++ Build Tools", "Visual Studio C++ toolchain not found"),
    "missing-dotnet-sdk": ("Install .NET SDK 10.0.302", "A compatible .NET SDK was not found"),
    "probe-result-incomplete": ("no complete, matching nonzero RESULT",),
    "probe-timeout": ("exceeded 180 seconds",),
}
_PREFLIGHT_CODES = {
    "PREFLIGHT_CANDIDATE_CHANGED", "PREFLIGHT_EXECUTABLE_CHANGED", "PREFLIGHT_FIXTURE_FAILED",
    "PREFLIGHT_INPUT_LINK_REJECTED", "PREFLIGHT_LINUX_REQUIRED", "PREFLIGHT_NATIVE_REJECTED",
    "PREFLIGHT_OUTPUT_LIMIT", "PREFLIGHT_PRIVATE_STATE_CHANGED", "PREFLIGHT_PROCESS_FAILED",
    "PREFLIGHT_PUBLIC_GATE_NOT_CLOSED", "PREFLIGHT_REPORT_INVALID", "PREFLIGHT_REPORT_MISMATCH",
    "PREFLIGHT_TEMP_INSIDE_CANDIDATE", "PREFLIGHT_TEMP_INVALID", "PREFLIGHT_UNVERIFIED_CANDIDATE",
}
_OWNED_PROCESS_CODES = {
    "OWNED_PROCESS_UNSUPPORTED", "EXECUTABLE_PATH_INVALID", "EXECUTABLE_FILE_INVALID",
    "EXECUTABLE_IDENTITY_INVALID", "EXECUTABLE_CAPABILITIES_UNVERIFIED", "EXECUTABLE_CAPABILITIES_FORBIDDEN",
    "EXECUTABLE_ELF_INVALID", "EXECUTABLE_HASH_MISMATCH", "EXECUTABLE_CHANGED", "EXECUTABLE_UNAVAILABLE",
}
_INTEROP_CODES = {"NATIVE_INTEROP_CARGO_ARTIFACT_INVALID", "NATIVE_INTEROP_RESULT_INVALID",
                  "NATIVE_INTEROP_TEST_EXECUTABLE_CHANGED", "NATIVE_MANAGED_INTEROP_FAILED"}
_PREPARATION_CODES = {"FINAL_PREPARATION_LOOPBACK_ADDRESS_IN_USE", "FINAL_PREPARATION_LOOPBACK_PERMISSION_DENIED",
                      "FINAL_PREPARATION_LOOPBACK_START_FAILED"}
# Case identities are public test source constants. Each suite has its own
# bound so a valid ordinal from another suite cannot masquerade as evidence.
_MANAGED_CASE_COUNTS = {"PublicSessionControl": 15, "PublicNativeReturn": 28,
                        "FinalLaunchPreparation": 9, "NativeManagedInterop": 3, "OwnedCoreProcess": 17,
                        "OwnedCoreRead": 22, "OwnedCoreReadInterop": 4, "OwnedCoreDependencies": 13}
_MANAGED_SETUP_CASES = {
    "PublicSessionControl": {"SETUP_PACKAGE", "SETUP_CERTIFICATES", "CLEANUP", "UNKNOWN_PHASE"},
    "PublicNativeReturn": {"SETUP_PACKAGE", "CLEANUP", "UNKNOWN_PHASE"},
    "FinalLaunchPreparation": {"SETUP_PACKAGE", "SETUP_LISTENER", "CLEANUP", "UNKNOWN_PHASE"},
    "NativeManagedInterop": {"SETUP_PACKAGE", "CLEANUP", "UNKNOWN_PHASE"},
    "OwnedCoreProcess": set(),
    "OwnedCoreRead": set(),
    "OwnedCoreReadInterop": set(),
    "OwnedCoreDependencies": {"SETUP_OR_CLEANUP"},
}
_DEPENDENCY_EXCEPTIONS = frozenset(("UnauthorizedAccessException", "IOException", "OperationCanceledException",
    "TimeoutException", "InvalidOperationException", "OtherException"))
_MANAGED_EXCEPTION_PATTERN = (
    r"AuthenticationException|CryptographicException|IOException|SocketException|"
    r"HttpRequestException|OperationCanceledException|TaskCanceledException|TimeoutException|"
    r"ArgumentException|InvalidOperationException|PlatformNotSupportedException|PublicControlException|"
    r"EndOfStreamException|ObjectDisposedException|Win32Exception|FileNotFoundException|"
    r"DirectoryNotFoundException|UnauthorizedAccessException|InvalidDataException|MissingMethodException|"
    r"JsonException|Exception|OtherException"
)
_MANAGED_FAILURE = re.compile(
    r"CI_MANAGED_FAILURE suite=(PublicSessionControl|PublicNativeReturn|FinalLaunchPreparation|NativeManagedInterop|OwnedCoreProcess|OwnedCoreRead|OwnedCoreReadInterop|OwnedCoreDependencies) "
    r"case=([0-9]{2}|SETUP_PACKAGE|SETUP_CERTIFICATES|SETUP_LISTENER|CLEANUP|UNKNOWN_PHASE|SETUP_OR_CLEANUP) "
    r"exception=(" + _MANAGED_EXCEPTION_PATTERN + r")\Z"
)
_RETURN_PHASE_PATTERN = (
    r"RUN_CASE|CHILD_START|WAIT_IDLE|WAIT_FRAME|WAIT_CONNECT|WAIT_AUTH|WAIT_DISCONNECT|"
    r"WAIT_REJECT|CONFIG_DELIVERY|CONFIG_ACK|OWNER_CAPTURE|STATE_WAIT|"
    r"PIPE_CONNECT|PIPE_READ|CHILD_EXIT|CLEANUP|CONFIG_DELIVERY_WAIT|OWNED_DIRECTORY_CLEANUP"
)
_RETURN_PHASE = re.compile(
    r"CI_RETURN_PHASE suite=(PublicNativeReturn|NativeManagedInterop) case=([0-9]{2}) "
    r"phase=(" + _RETURN_PHASE_PATTERN + r")\Z"
)
_RETURN_CHILD_FAILURE = re.compile(
    r"CI_RETURN_CHILD_FAILURE phase=(" + _RETURN_PHASE_PATTERN + r") "
    r"exception=(" + _MANAGED_EXCEPTION_PATTERN + r")\Z"
)
_PIPE_BOUNDARY_QUERY = re.compile(
    r"PIPE_BOUNDARY_QUERY role=(LEGACY|FACTORY|PRODUCTION) status=(SUCCESS|FAILURE) "
    r"type=(BYTE|MESSAGE|LOCAL_BYTE|LOCAL_MESSAGE|OTHER) end=(SERVER|CLIENT|OTHER)\Z"
)
_RETURN_DIRECTORY_FAILURE = re.compile(
    r"CI_RETURN_DIRECTORY_FAILURE suite=PublicNativeReturn case=(15|16|17) "
    r"exception=(" + _MANAGED_EXCEPTION_PATTERN + r") child_exited=(YES|NO|UNKNOWN) "
    r"process_disposed=(YES|NO) config_disposed=(YES|NO) readonly_files=(YES|NO|UNKNOWN)\Z"
)
_OWNED_CORE_FAILURE = re.compile(
    r"CI_OWNED_CORE_FAILURE case=([0-9]{2}) phase=(INPUT|RESUME|STOP|CHILD_READY|PARENT_DEATH) "
    r"code=(OWNED_CORE_INPUTS_INVALID|OWNED_CORE_WINDOWS_REQUIRED|OWNED_CORE_JOB_FAILED|"
    r"OWNED_CORE_ATTRIBUTES_FAILED|OWNED_CORE_ENVIRONMENT_FAILED|OWNED_CORE_CREATE_FAILED|"
    r"OWNED_CORE_MEMBERSHIP_FAILED|OWNED_CORE_CLEANUP_FAILED|OWNED_CORE_STATE_INVALID|"
    r"OWNED_CORE_RESUME_FAILED|PUBLIC_RETURN_REJECTED|OTHER)\Z"
)


def managed_case_allowed(suite: str, case: str) -> bool:
    return (case in _MANAGED_SETUP_CASES[suite]
            or (case.isascii() and case.isdigit() and 1 <= int(case) <= _MANAGED_CASE_COUNTS[suite]))


_TLS_FAILURE = re.compile(
    r"CI_TLS_FAILURE suite=PublicSessionControl case=05 "
    r"client=(REQUEST_REJECTED|RESPONSE_INVALID|TRANSPORT_FAILED|TIMEOUT|OTHER) "
    r"server_stage=(ACCEPT|TLS|REQUEST|RESPONSE|COMPLETE) "
    r"server_exception=(NONE|OperationCanceledException|IOException|AuthenticationException|"
    r"SocketException|InvalidOperationException|OtherException) "
    r"native=(NONE|NO_CREDENTIALS|UNKNOWN_CREDENTIALS|ALGORITHM_MISMATCH|ILLEGAL_MESSAGE|"
    r"UNTRUSTED_ROOT|WRONG_PRINCIPAL|CERT_EXPIRED|INTERNAL_ERROR|BAD_KEYSET|OTHER)\Z"
)


def owned_process_codes(text: str) -> list[str]:
    return sorted({code for code in _OWNED_PROCESS_CODES
                   if re.search(r"(?<![A-Za-z0-9_])" + code + r"(?![A-Za-z0-9_])", text)})[:MAX_ITEMS]


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
    owned = sorted({code for _, trace in [*result.failures, *result.errors]
                    for code in owned_process_codes(trace)})[:MAX_ITEMS]
    # Put fixed guard codes before long method lists so the bounded annotation
    # still distinguishes an unsupported platform from rejected fixture metadata.
    owned_field = f"owned_process_codes={','.join(owned)}; " if owned else ""
    return (f"suite={safe_identifier(suite)}; executed_methods={result.testsRun}; "
            f"{owned_field}"
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


def known_native_test_ids() -> set[str]:
    """Only public, statically declared tests in the two owned Windows suites."""
    folder = Path(__file__).resolve().parents[1] / "src/native/client/hooks/src/api"
    names = set()
    for module in ("public_return", "source_transport"):
        try:
            source = (folder / (module + "_tests.rs")).read_text(encoding="utf-8")
        except OSError:
            continue
        for name in re.findall(r"(?m)^#\[test\]\s*fn ([a-z][a-z0-9_]{0,159})\(\)", source):
            names.add("api::" + module + "::tests::" + name)
    return names


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
    # The static native preflight intentionally retains no raw output. Preserve
    # only its source-defined adapter codes; never echo suffixes, paths or values.
    preflight = sorted({code for code in _PREFLIGHT_CODES
                        if re.search(r"(?<![A-Za-z0-9_])" + code + r"(?![A-Za-z0-9_])", text)})[:MAX_ITEMS]
    # Only the exact reviewed suite, fixed case ordinals/phases and class names
    # can leave a managed probe log. Never publish its exception text or stack.
    managed = sorted({":".join(match.groups())
                      for line in text.splitlines()
                      if (match := _MANAGED_FAILURE.fullmatch(line))
                      and managed_case_allowed(match[1], match[2])
                      and (match[1] != "OwnedCoreDependencies" or match[3] in _DEPENDENCY_EXCEPTIONS)})[:MAX_ITEMS]
    return_phases = sorted({":".join(match.groups())
                            for line in text.splitlines()
                            if (match := _RETURN_PHASE.fullmatch(line))
                            and managed_case_allowed(match[1], match[2])})[:MAX_ITEMS]
    child_failures = sorted({":".join(match.groups())
                             for line in text.splitlines()
                             if (match := _RETURN_CHILD_FAILURE.fullmatch(line))})[:MAX_ITEMS]
    pipe_boundary = sorted({":".join(match.groups()) for line in text.splitlines()
                            if (match := _PIPE_BOUNDARY_QUERY.fullmatch(line))})[:MAX_ITEMS]
    directory_failures = sorted({"PublicNativeReturn:" + ":".join(match.groups())
                                for line in text.splitlines()
                                if (match := _RETURN_DIRECTORY_FAILURE.fullmatch(line))})[:MAX_ITEMS]
    core_failures = sorted({":".join(match.groups()) for line in text.splitlines()
                           if (match := _OWNED_CORE_FAILURE.fullmatch(line))
                           and managed_case_allowed("OwnedCoreProcess", match[1])})[:MAX_ITEMS]
    tls = sorted({"PublicSessionControl:05:" + ":".join(match.groups())
                  for line in text.splitlines()
                  if (match := _TLS_FAILURE.fullmatch(line))})[:MAX_ITEMS]
    native_allowed = known_native_test_ids()
    native_tests = sorted({match[1] for line in text.splitlines()
        if (match := re.fullmatch(r"test ([a-z0-9_:]+) \.\.\. FAILED", line))
        and match[1] in native_allowed})[:MAX_ITEMS]
    def fixed_codes(allowed):
        return sorted(code for code in allowed
                      if re.search(r"(?<![A-Za-z0-9_])" + code + r"(?![A-Za-z0-9_])", text))[:MAX_ITEMS]
    return {"compiler_codes": codes, "ui_checks": labels[:MAX_ITEMS],
            "categories": signatures[:MAX_ITEMS], "exception_types": exceptions,
            "preflight_codes": preflight, "owned_process_codes": owned_process_codes(text),
            "managed_cases": managed, "tls_cases": tls,
            "return_phases": return_phases, "return_child_failures": child_failures,
            "pipe_boundary_queries": pipe_boundary,
            "return_directory_failures": directory_failures,
            "owned_core_failures": core_failures,
            "native_interop_codes": fixed_codes(_INTEROP_CODES),
            "native_failed_tests": native_tests,
            "final_preparation_codes": fixed_codes(_PREPARATION_CODES)}


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
