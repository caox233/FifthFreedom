"""Validate bounded owned-probe receipts and annotate only fixed public codes."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import re

import ci_diagnostics

CODES = set("""ROOT_NAMESPACE_PRIVILEGES_REQUIRED NET_ADMIN_AND_SYS_ADMIN_REQUIRED
CAPABILITIES_UNVERIFIED EXISTING_TUN_REQUIRED GITHUB_HOSTED_REQUIRED OWNED_NAMESPACE_APIS_REQUIRED
IP_REQUIRED NFT_REQUIRED CHECKOUT_IDENTITY_MISMATCH CHECKOUT_REVISION_MISMATCH CHECKOUT_TRACKED_SOURCE_DIRTY
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
CONTAINMENT_CHANGED CONTAINMENT_READBACK_INVALID DEFAULT_ROUTE_FORBIDDEN
OVERLAY_READY_TIMEOUT OVERLAY_TRANSFER_CONTRACT_FAILED OWNED_CORE_EXIT_NOT_CONFIRMED
OWNED_NAMESPACE_CLEANUP_FAILED OWNED_WORKER_CLEANUP_FAILED PRIVATE_STAGE_CLEANUP_FAILED
PRIVATE_STAGE_IDENTITY_CHANGED SOCKET_PROBE_NOT_READY STOPPED_OVERLAY_STILL_TRANSFERS
WRONG_PIN_INFRASTRUCTURE_OR_AUTH_INVALID SERVER_INFRASTRUCTURE_UNHEALTHY TRANSPORT_MATRIX_INCOMPLETE
DEVELOPMENT_PROBE_FAILED DEVELOPMENT_PROBE_CANCELLED IMAGE_HASH_MISMATCH IMAGE_IDENTITY_INVALID""".split())


def booleans(value, expected):
    return type(value) is dict and set(value) == set(expected) and all(value[key] is flag for key, flag in expected.items())


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


def verified(kind, value):
    if type(value) is not dict:
        return False
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
    ci_diagnostics.emit_error("probe=" + kind + "; fixed_codes=" + reason + "; no raw probe output published")
    return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kind", choices=("native", "easytier"), required=True)
    parser.add_argument("--exit-code", type=int, required=True)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--error-log", type=Path, required=True)
    args = parser.parse_args()
    return inspect(args.kind, args.exit_code, args.receipt, args.error_log)


if __name__ == "__main__":
    raise SystemExit(main())
