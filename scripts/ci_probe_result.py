"""Validate bounded owned-probe receipts and annotate only fixed public codes."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import re

import ci_diagnostics
from easytier_probe_worker import valid_failure_record
from ci_easytier_probe import valid_readiness_timeout

CODES = set("""ROOT_NAMESPACE_PRIVILEGES_REQUIRED NET_ADMIN_AND_SYS_ADMIN_REQUIRED
CAPABILITIES_UNVERIFIED EXISTING_TUN_REQUIRED GITHUB_HOSTED_REQUIRED OWNED_NAMESPACE_APIS_REQUIRED
IP_REQUIRED NFT_REQUIRED CHECKOUT_IDENTITY_MISMATCH CHECKOUT_REVISION_MISMATCH CHECKOUT_TRACKED_SOURCE_DIRTY
CHECKOUT_VERIFICATION_FAILED
CANDIDATE_RECEIPT_INVALID CANDIDATE_BYTES_CHANGED CANDIDATE_RECEIPT_MISMATCH CANDIDATE_FILES_INVALID
COPIED_NATIVE_VERIFICATION_FAILED COPIED_NATIVE_IDENTITY_MISMATCH REPORT_DIRECTORY_REQUIRED
NATIVE_READY_TIMEOUT NATIVE_READY_REVOKED NATIVE_LISTENER_OWNERSHIP_INVALID NATIVE_NOT_REAPED
NATIVE_DID_NOT_STOP NATIVE_PARENT_DEATH_EXIT_INVALID NATIVE_REVOCATION_EXIT_INVALID
DENIED_ADMISSION_WAS_ACCEPTED DENIED_ADMISSION_WROTE_STATE WITHHELD_ADMISSION_WROTE_STATE
EARLY_ADMISSION_SIDE_EFFECT STATE_NOT_EMPTY_BEFORE_ADMISSION ADMITTED_STATE_MISSING
CONNECTION_NOT_CLOSED CONNECTION_NOT_ESTABLISHED HTTP_PROBE_FAILED LISTENERS_NOT_CLOSED
WRONG_INTERFACE_ACCEPTED OWNED_PROCESS_CLEANUP_FAILED PRIVATE_TREE_NOT_REMOVED
SOURCE_PACKAGE_CHANGED PRIVATE_PACKAGE_CHANGED NAMESPACE_DEFAULT_ROUTE_PRESENT
NATIVE_LIFECYCLE_PROBE_FAILED NATIVE_LIFECYCLE_INTERRUPTED WORKER_EVENT_TIMEOUT WORKER_EVENT_INVALID
WORKER_EVENT_EOF WORKER_READY_INVALID WORKER_COMMAND_FAILED WORKER_REPLY_EOF WORKER_REPLY_INVALID
WORKER_REPLY_LIMIT WORKER_REPLY_TIMEOUT WORKER_START_FAILED WORKER_EXITED
WORKER_CORE_PROFILE_MISMATCH WORKER_CORE_EXITED WORKER_TUN_NOT_READY WORKER_OWNERSHIP_LOST
NAMESPACE_TOOL_FAILED NAMESPACE_TOOL_TIMEOUT NAMESPACE_TOOL_OUTPUT_LIMIT NAMESPACE_TOOL_START_FAILED
CONTAINMENT_CHANGED CONTAINMENT_READBACK_INVALID CONTAINMENT_PREFLIGHT_INCOMPLETE DEFAULT_ROUTE_FORBIDDEN
OVERLAY_READY_TIMEOUT OVERLAY_TRANSFER_CONTRACT_FAILED OWNED_CORE_EXIT_NOT_CONFIRMED
OWNED_NAMESPACE_CLEANUP_FAILED OWNED_WORKER_CLEANUP_FAILED PRIVATE_STAGE_CLEANUP_FAILED
PRIVATE_STAGE_IDENTITY_CHANGED SOCKET_PROBE_NOT_READY STOPPED_OVERLAY_STILL_TRANSFERS
WRONG_PIN_INFRASTRUCTURE_OR_AUTH_INVALID SERVER_INFRASTRUCTURE_UNHEALTHY TRANSPORT_MATRIX_INCOMPLETE
DEVELOPMENT_PROBE_FAILED DEVELOPMENT_PROBE_CANCELLED IMAGE_HASH_MISMATCH IMAGE_IDENTITY_INVALID""".split())
CODES.update("""PUBLIC_RECEIPT_PARENT_REQUIRED PUBLIC_RECEIPT_WRITE_FAILED OWNED_STACK_CANCELLED
STACK_CASES_INCOMPLETE STACK_CHECKOUT_MISMATCH STACK_CLIENT_OBSERVATION_INVALID
STACK_CONNECTION_NOT_ESTABLISHED STACK_COPIED_NATIVE_IDENTITY_MISMATCH STACK_EXACT_PEER_STOP_FAILED
STACK_HELD_CONNECTION_NOT_CLOSED STACK_NAMESPACE_CLEANUP_FAILED STACK_NATIVE_RECEIPT_MISMATCH
STACK_NATIVE_REVOKE_FAILED STACK_PAUSE_NOT_ENTERED STACK_PRIVATE_CLEANUP_FAILED
STACK_PRIVATE_PACKAGE_CHANGED STACK_PRIVATE_STAGE_CHANGED STACK_RELEASE_FAILED
STACK_RUNTIME_RUN_MISMATCH STACK_STATE_INVALID STACK_STATE_TIMEOUT STACK_WORKER_CLEANUP_FAILED
STACK_WORKER_START_FAILED STACK_CHILD_CLEANUP_FAILED STACK_CLIENT_CORE_LOST
STACK_CLIENT_NOT_AUTHENTICATED STACK_CLOSE_BUDGET_INVALID STACK_COMMAND_INVALID STACK_COMMAND_LIMIT
STACK_HELD_CONNECTION_LIMIT STACK_HELD_CONNECTION_TIMEOUT STACK_NATIVE_LISTENER_REMAINS
STACK_NATIVE_PORT_CHECK_FAILED STACK_NATIVE_WATCHDOG_EXIT_INVALID STACK_ONLY_TCP_DEVELOPMENT
STACK_PAUSED_OVERLAY_CHANGED STACK_PAUSE_ALREADY_REQUESTED STACK_PAUSE_BUDGET_INVALID
STACK_PAUSE_EXPIRED STACK_REVOKE_CLEANUP_INVALID STACK_SOCKET_TABLE_INVALID STACK_SOCKET_TABLE_LIMIT
STACK_START_FAILED""".split())
CODES.update({"WORKER_REPORTED_FAILURE", "WORKER_FAILURE_FRAME_INVALID"})
# Native worker errors are wrapped by read_event with a WORKER_ prefix.
# Keep the public source reasons explicit; arbitrary worker strings stay private.
CODES.update("WORKER_" + code for code in """PUBLIC_OWNER_LEASE_INVALID
STATE_NOT_EMPTY_BEFORE_ADMISSION WITHHELD_ADMISSION_WROTE_STATE DENIED_ADMISSION_WROTE_STATE
DENIED_ADMISSION_WAS_ACCEPTED NATIVE_READY_TIMEOUT NATIVE_READY_REVOKED ADMITTED_STATE_MISSING
NATIVE_IMAGE_CHANGED NATIVE_REVOCATION_EXIT_INVALID HTTP_PROBE_FAILED WRONG_INTERFACE_ACCEPTED
NATIVE_LISTENER_OWNERSHIP_INVALID NAMESPACE_SOCKET_TABLE_TOO_LARGE
CLIENT_MODE_INVALID CLIENT_COMMAND_INVALID CLIENT_ARGUMENT_INVALID CLIENT_OUTPUT_LIMIT
WORKER_ARGUMENT_INVALID WORKER_COMMAND_INVALID NATIVE_LIFECYCLE_PROBE_FAILED""".split())
CODES.update({"COMMAND_TIMEOUT", "COMMAND_START_FAILED", "COMMAND_FAILED", "COMMAND_OUTPUT_TOO_LARGE"})
# Exact native entry/namespace reasons, including pre-worker validation and cleanup.
# These are reviewed literals from the producers, never a generic uppercase match.
CODES.update("""CANDIDATE_EXTRA_FILES CANDIDATE_LINK_REJECTED CANDIDATE_METADATA_INVALID
CANDIDATE_NATIVE_IDENTITY_INVALID CANDIDATE_PATH_INVALID CLIENT_ARGUMENT_INVALID
CLIENT_COMMAND_INVALID CLIENT_MODE_INVALID CLIENT_OUTPUT_LIMIT CLIENT_RESULT_INVALID
EXPECTED_RUN_INVALID NAMESPACE_SOCKET_TABLE_TOO_LARGE NATIVE_EXIT_INVALID NATIVE_IMAGE_CHANGED
NATIVE_NOT_ADOPTED NATIVE_PARENT_CHANGED NATIVE_PID_INVALID PARENT_EXIT_FAILED
PRIVATE_ROOT_INSIDE_CANDIDATE PROBE_CHILD_START_FAILED REPORT_INSIDE_CANDIDATE
SUBREAPER_ENABLE_FAILED SUBREAPER_QUERY_FAILED SUBREAPER_RESTORE_FAILED
WORKER_ARGUMENT_INVALID WORKER_COMMAND_INVALID WORKER_EVENT_TOO_LARGE
DUPLICATE_JSON_KEY IPROUTE2_REQUIRED LINUX_REQUIRED NAMESPACE_CLEANUP_INCOMPLETE
NAMESPACE_COLLISION NAMESPACE_IDENTITY_CHANGED NAMESPACE_IDENTITY_UNVERIFIED
NAMESPACE_NAME_NOT_OWNED NAMESPACE_PRIVILEGES_UNVERIFIED PROBE_CLEANUP_FAILED
PROBE_FILE_INVALID PROBE_NOT_ALIVE PROBE_READY_EOF PROBE_READY_INVALID PROBE_READY_TIMEOUT
PROBE_READY_TOO_LARGE PROBE_START_FAILED REQUIRED_PATH_DID_NOT_ECHO TOPOLOGY_INVALID
WRONG_INTERFACE_NOT_ISOLATED""".split())
# read_json/sha_file/checked_copy can fail before the native process is started.
CODES.update("""INPUT_JSON_DUPLICATE INPUT_LINK_REJECTED INPUT_FILE_INVALID INPUT_CHANGED
INPUT_JSON_INVALID COPY_HASH_MISMATCH""".split())
# The namespace worker also reads its own private config and copied receipt.
CODES.update("WORKER_" + code for code in """INPUT_JSON_DUPLICATE INPUT_LINK_REJECTED
INPUT_FILE_INVALID INPUT_CHANGED INPUT_JSON_INVALID""".split())
# The same owned-process errors may reach dispatch directly or through read_event.
_NATIVE_LIFECYCLE_CODES = frozenset("""PUBLIC_OWNER_LEASE_INVALID EXECUTABLE_PATH_INVALID
OWNED_PROCESS_UNSUPPORTED EXECUTABLE_IDENTITY_INVALID EXECUTABLE_FILE_INVALID
EXECUTABLE_CAPABILITIES_UNVERIFIED EXECUTABLE_CAPABILITIES_FORBIDDEN EXECUTABLE_ELF_INVALID
EXECUTABLE_CHANGED EXECUTABLE_HASH_MISMATCH EXECUTABLE_UNAVAILABLE CHILD_SPEC_INVALID
CHILD_LEASE_SOCKET_INVALID CHILD_BOOTSTRAP_FAILED CHILD_EXITED_DURING_START CHILD_EXEC_FAILED
CHILD_BOOTSTRAP_CLEANUP_FAILED CHILD_START_FAILED CHILD_STOP_BUDGET_INVALID CHILD_SIGNAL_FAILED
CHILD_STOP_TIMEOUT""".split())
CODES.update(_NATIVE_LIFECYCLE_CODES)
CODES.update("WORKER_" + code for code in _NATIVE_LIFECYCLE_CODES)
_NATIVE_OPEN_CODES = frozenset(("CLIENT_CONNECTION_NOT_OPEN", "CLIENT_OPEN_CHECK_TIMEOUT", "CONNECTION_NOT_OPEN"))
CODES.update(_NATIVE_OPEN_CODES)
CODES.update("WORKER_" + code for code in _NATIVE_OPEN_CODES)
CODES.add("STACK_HELD_CONNECTION_NOT_OPEN")
_STARTUP_MODULES = frozenset(("ci_socket_isolation", "ci_easytier_probe", "ci_native_lifecycle",
    "easytier_probe_worker", "public_server_candidate", "fifth_freedom_control", "cryptography"))



_NFT_KEYS = "TABLE CHAIN RULE FAMILY NAME TYPE HOOK PRIO POLICY EXPR MATCH OP LEFT RIGHT META KEY PAYLOAD PROTOCOL FIELD CT ACCEPT DROP COUNTER PACKETS BYTES SET PREFIX ADDR LEN".split()
_NFT_ENUMS = "INET INPUT OUTPUT FORWARD FILTER DROP ACCEPT IP IP6 TCP UDP IPV4 IPV6 NFPROTO IIFNAME OIFNAME L4PROTO SADDR DADDR SPORT DPORT STATE ESTABLISHED RELATED NEW INVALID UNTRACKED EQ NE IN AND OR".split()
_NFT_TYPES = "OBJECT LIST STRING INTEGER BOOL NULL OTHER".split()
_NFT_INDEX = r"(?:[0-9]|[1-5][0-9]|6[0-3])"
_NFT_TOKEN = "(?:" + "|".join(_NFT_KEYS) + r"|I" + _NFT_INDEX + ")"
_NFT_ENUM = "(?:" + "|".join(_NFT_ENUMS) + ")"
_NFT_TYPE = "(?:" + "|".join(_NFT_TYPES) + ")"
_NFT_COUNT = "(?:" + _NFT_INDEX + "|OVER63)"
_NFT_DIAGNOSTIC = re.compile(
    r"NFT_(?:" + _NFT_TOKEN + "(?:_" + _NFT_TOKEN + ")*)?_"
    r"(?:EXTRA_KEY|MISSING_KEY|UNCLASSIFIED_KEY_DIFFERENCE|UNCLASSIFIED_INDEX_OVER63|"
    r"TYPE_" + _NFT_TYPE + "_TO_" + _NFT_TYPE + "|COUNT_" + _NFT_COUNT + "_TO_" + _NFT_COUNT +
    "|" + _NFT_ENUM + "_TO_" + _NFT_ENUM + "|" + _NFT_TYPE + r"_VALUE_DIFFERENT)\Z")


def nft_diagnostics(value):
    rows = value.get("transports", value.get("cases")) if type(value) is dict else None
    if type(rows) is not list:
        return []
    result = set()
    for row in rows[:3]:
        values = row.get("fixed_diagnostics") if type(row) is dict else None
        if type(values) is not list:
            continue
        for code in values[:16]:
            if (type(code) is str and len(code) <= 160 and
                    (code in ("NFT_UNCLASSIFIED_DEEP_DIFFERENCE", "NFT_UNCLASSIFIED_DIFFERENCE")
                     or _NFT_DIAGNOSTIC.fullmatch(code))):
                result.add(code)
    return sorted(result)[:4]


def booleans(value, expected):
    return type(value) is dict and set(value) == set(expected) and all(value[key] is flag for key, flag in expected.items())


def worker_diagnostics(value):
    rows = value.get("transports", value.get("cases")) if type(value) is dict else None
    if type(rows) is not list:
        return []
    summaries = set()
    for row in rows[:3]:
        record = row.get("worker_failure") if type(row) is dict else None
        if valid_failure_record(record):
            summaries.add(":".join((record["phase"], record["code"],
                "cleanup_reaped=" + ("true" if record["cleanup_reaped"] else "false"),
                record["cleanup_code"] or "NONE")))
    return sorted(summaries)[:3]


def readiness_diagnostics(value):
    rows = value.get("transports", value.get("cases")) if type(value) is dict else None
    if type(rows) is not list:
        return []
    summaries = set()
    for row in rows[:3]:
        record = row.get("readiness_timeout") if type(row) is dict else None
        if valid_readiness_timeout(record):
            last = record["last_observation"]
            summaries.add(":".join((record["role"],
                "expected_auth=" + str(record["expected_authenticated"]).lower(),
                "observations=" + str(record["observation_count"]), "core=true",
                "tun=" + str(last["tun_owned"]).lower(),
                "peer=" + str(last["peer_authenticated"]).lower(),
                "query=" + last["query_state"], "reason=" + (last["query_code"] or "NONE"),
                "bind=" + (last["local_bind_kind"] or "NONE"))))
    return sorted(summaries)[:3]


def native_cases(cases):
    names = ["withheld", "denied", "eof", "expiry", "parent_death"]
    if (type(cases) is not list or any(type(row) is not dict for row in cases)
            or [row.get("case") for row in cases] != names):
        return False
    for row in cases:
        if row.get("native_reaped") is not True:
            return False
        if row["case"] in ("withheld", "denied"):
            if row.get("no_state_created") is not True:
                return False
        elif (type(row.get("actual_listener_count")) is not int or row["actual_listener_count"] != 5
                or any(row.get(key) is not True for key in ("protected_http_and_tcp_verified", "wrong_interface_rejected",
                    "established_connection_closed", "all_five_ports_released"))
                or type(row.get("native_exit")) is not int
                or row["native_exit"] not in ((92, -9) if row["case"] == "parent_death" else (92,))):
            return False
    return True


def echo_outcomes(value, allowed):
    return (type(value) is dict and set(value) == {"tcp_port", "udp_port", "udp_ephemeral_port"}
            and all(item in allowed for item in value.values()))


def overlay_transport(row):
    phases = row.get("phases")
    if (row.get("status") != "owned_overlay_probe_verified" or row.get("cleanup_errors") != []
            or "worker_failure" in row or "readiness_timeout" in row or "error" in row
            or any(type(row.get(key)) is not int or row[key] != 2 for key in ("namespaces_created", "namespaces_removed"))
            or type(phases) is not list or any(type(item) is not dict for item in phases)
            or [item.get("phase") for item in phases] != ["correct-pin-before", "wrong-pin", "correct-pin-after"]):
        return False
    for phase in phases:
        if type(phase.get("profile_sha256")) is not str or not re.fullmatch(r"[0-9a-f]{64}", phase["profile_sha256"]):
            return False
    if not echo_outcomes(phases[0].get("echo"), ("echo",)) or not echo_outcomes(phases[2].get("echo"), ("echo",)):
        return False
    wrong = phases[1]
    rounds = wrong.get("echo_rounds")
    if (type(rounds) is not list or len(rounds) < 2 or not all(echo_outcomes(item, ("timeout", "refused")) for item in rounds)
            or type(wrong.get("minimum_observation_seconds")) is not int or wrong["minimum_observation_seconds"] != 8
            or wrong.get("tun_owned") is not True or wrong.get("peer_authenticated") is not False):
        return False
    blocked = row.get("blocked_packets")
    if (type(blocked) is not dict or set(blocked) != {"client", "server"}
            or any(type(value) is not dict or set(value) != {"input", "output", "forward"}
                   or any(type(n) is not int or n < 0 for n in value.values()) for value in blocked.values())):
        return False
    return booleans(row.get("core_exit"), {"core_reaped": True, "tun_absent": True, "new_traffic_denied": True,
                                          "production_native_lease_exercised": False})


def owned_stack_cases(cases):
    if (type(cases) is not list or any(type(row) is not dict for row in cases)
            or [row.get("case") for row in cases] != ["lease-revoke", "peer-loss"]):
        return False
    for row in cases:
        counters = row.get("containment_after")
        if (row.get("status") != "owned_stack_case_verified" or row.get("cleanup_errors") != []
                or "worker_failure" in row or "readiness_timeout" in row or "error" in row
                or any(row.get(key) is not True for key in ("actual_native_ready", "established_tcp"))
                or type(row.get("owned_listeners")) is not int or row["owned_listeners"] != 5
                or any(type(row.get(key)) is not int or row[key] != 2 for key in ("namespaces_created", "namespaces_removed"))
                or type(row.get("runtime_profile_sha256")) is not str
                or not re.fullmatch(r"[0-9a-f]{64}", row["runtime_profile_sha256"])
                or type(counters) is not list or len(counters) != 2
                or any(type(item) is not dict or set(item) != {"input", "output", "forward"}
                    or any(type(n) is not int or n < 0 for n in item.values()) for item in counters)):
            return False
        if row["case"] == "lease-revoke":
            if (type(row.get("native_watchdog_exit")) is not int or row["native_watchdog_exit"] != 92
                    or type(row.get("ports_rebound")) is not int or row["ports_rebound"] != 5
                    or any(row.get(key) is not True for key in ("held_tcp_eof_or_reset", "overlay_alive_during_close"))):
                return False
        elif (any(row.get(key) is not True for key in ("exact_peer_reaped", "monitor_permanently_revoked", "native_reaped"))
                or type(row.get("native_listeners_remaining")) is not int or row["native_listeners_remaining"] != 0
                or row.get("remote_tcp_eof_claimed") is not False):
            return False
    return True


def verified(kind, value):
    if type(value) is not dict:
        return False
    if kind == "owned-stack":
        scope = value.get("scope")
        flags = {"production_admission": False, "ordinary_public_enabled": False, "game_executed": False,
            "complete_client_package": False, "runtime_binary_uploaded": False, "raw_runtime_output_retained": False}
        return (value.get("schema") == "ff-owned-overlay-native-stack-development-v1"
            and value.get("status") == "owned_stack_development_verified" and value.get("precondition_failures") == []
            and type(scope) is dict and set(scope) == set(flags) | {"transport", "purpose"}
            and scope["transport"] == "tcp" and scope["purpose"] == "owned-isolated-development"
            and all(scope[key] is flag for key, flag in flags.items())
            and all(value.get(key) is True for key in ("copied_package_native_verified", "source_package_unchanged", "private_stage_removed"))
            and owned_stack_cases(value.get("cases")))
    if kind == "native":
        expected_scope = {"synthetic_runtime_bindings": True, "controlled_native_startup_test": True,
            "encrypted_overlay_verified": False, "production_provider_open": False, "game_executed": False,
            "complete_windows_client": False, "deployed": False, "published": False}
        return (type(value.get("schema")) is int and value["schema"] == 1
            and value.get("status") == "native_owned_lifecycle_verified" and booleans(value.get("scope"), expected_scope)
            and native_cases(value.get("cases"))
            and all(value.get(key) is True for key in ("copied_package_native_verified", "source_package_unchanged",
                "private_tree_removed", "all_owned_children_reaped", "no_default_routes"))
            and type(value.get("owned_namespaces_removed")) is int and value["owned_namespaces_removed"] == 3)
    if kind == "containment":
        rows = value.get("transports")
        return (value.get("schema") == "ff-easytier-containment-preflight-v1"
            and value.get("status") == "containment_preflight_verified"
            and value.get("input_identity") == "synthetic_profile_only_no_executable"
            and value.get("precondition_failures") == []
            and all(value.get(key) is False for key in ("runtime_executed", "runtime_binary_uploaded",
                "production_admission", "raw_runtime_output_retained"))
            and type(rows) is list and all(type(row) is dict for row in rows)
            and [row.get("transport") for row in rows] == ["quic", "wss", "tcp"]
            and all(row.get("status") == "containment_pair_verified" and row.get("cleanup_errors") == []
                and all(type(row.get(key)) is int and row[key] == 2 for key in ("namespaces_created", "namespaces_removed"))
                for row in rows))
    rows = value.get("transports")
    return (kind == "easytier" and value.get("schema") == "ff-easytier-isolated-development-probe-v1"
        and value.get("status") == "isolated_development_matrix_verified"
        and value.get("precondition_failures") == [] and value.get("private_stage_removed") is True
        and all(value.get(key) is False for key in ("runtime_binary_uploaded", "raw_runtime_output_retained",
            "game_executed", "production_admission", "native_owner_lease_verified"))
        and type(rows) is list and all(type(row) is dict for row in rows)
        and [row.get("transport") for row in rows] == ["quic", "wss", "tcp"]
        and all(overlay_transport(row) for row in rows))


def inspect(kind, exit_code, receipt, error_log):
    value = None
    def unique(rows):
        result = {}
        for key, item in rows:
            if key in result:
                raise ValueError("PROBE_RECEIPT_AMBIGUOUS")
            result[key] = item
        return result
    try:
        with receipt.open("rb") as source:
            content = source.read(256 * 1024 + 1)
        if len(content) <= 256 * 1024:
            value = json.loads(content, object_pairs_hook=unique)
    except (OSError, ValueError, RecursionError):
        pass
    if exit_code == 0 and verified(kind, value):
        return 0
    text = ci_diagnostics.read_bounded_logs([error_log])
    if value is not None:
        text += "\n" + json.dumps(value)
    codes = sorted(code for code in CODES
                   if re.search(r"(?<![A-Za-z0-9_])" + code + r"(?![A-Za-z0-9_])", text))[:8]
    reason = ",".join(codes) or ("PROBE_SUCCESS_RECEIPT_INVALID" if exit_code == 0 else "UNCLASSIFIED_PROBE_FAILURE")
    shape = nft_diagnostics(value)
    fields = "; nft_shape=" + ",".join(shape) if shape else ""
    workers = worker_diagnostics(value)
    if workers:
        fields += "; worker_failures=" + ",".join(workers)
    readiness = readiness_diagnostics(value)
    if readiness:
        fields += "; readiness_timeouts=" + ",".join(readiness)
    exceptions = ci_diagnostics.known_exception_types(text)
    if exceptions:
        fields += "; exception_types=" + ",".join(exceptions)
    missing = sorted({match[1] for line in text.splitlines()
        if (match := re.fullmatch(r"ModuleNotFoundError: No module named '([a-z_]+)'", line))
        and match[1] in _STARTUP_MODULES})
    if missing:
        fields += "; missing_modules=" + ",".join(missing)
    ci_diagnostics.emit_error("probe=" + kind + "; fixed_codes=" + reason + fields + "; no raw probe output published")
    return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kind", choices=("native", "easytier", "containment", "owned-stack"), required=True)
    parser.add_argument("--exit-code", type=int, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--error-log", type=Path, required=True)
    args = parser.parse_args()
    return inspect(args.kind, args.exit_code, args.receipt, args.error_log)


if __name__ == "__main__":
    raise SystemExit(main())
