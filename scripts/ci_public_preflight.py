"""Exercise the admitted Linux binary's static preflight with disposable inputs.

No input key, runtime configuration, private path or raw process output is logged
or returned. A passing receipt still requires Public startup to remain closed.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import secrets
import stat
import subprocess
import sys
import tempfile

import public_server_candidate as candidate


class PreflightError(RuntimeError):
    """Fixed error codes, never subprocess output or temporary input values."""


def expected_report(native_identity):
    return {
        "schema": 1, "status": "static_preflight_verified", "startup_allowed": False,
        "service_manifest_sha256": native_identity["service"]["manifest_sha256"],
        "admitted_client_storage_key": native_identity["admitted_client"]["storage_key"],
        "shared_auth_key_validated": True, "data_directory_private": True,
        "data_lock_acquired": False, "observer_enabled": False,
        "native_listener_plan": {"count": 5, "bind_scope": "declared_overlay_ipv4",
            "config_tcp": 80, "content_tcp": 8000, "auth_udp": 21126,
            "secure_udp": 21127, "api_tcp": 50051},
        "blockers": ["OWNED_ENCRYPTED_OVERLAY_UNVERIFIED", "OVERLAY_LIFETIME_SUPERVISION_UNVERIFIED",
            "PUBLIC_SOCKET_CONFINEMENT_NOT_ACTIVATED", "PUBLIC_SOURCE_POLICY_NOT_ACTIVATED"],
    }


def snapshot(root):
    result = {}
    for path in sorted(root.rglob("*")):
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise PreflightError("PREFLIGHT_INPUT_LINK_REJECTED")
        if path.is_dir():
            row = {"mode": stat.S_IMODE(info.st_mode), "kind": "directory"}
        else:
            row = {"mode": stat.S_IMODE(info.st_mode), "kind": "file", **candidate.sha_file(path)}
        result[path.relative_to(root).as_posix()] = row
    return result


def private_file(path, data):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "wb") as output:
        output.write(data)


def captured(args, working_directory):
    try:
        result = subprocess.run(args, cwd=working_directory, capture_output=True, timeout=120)
    except (OSError, subprocess.SubprocessError):
        raise PreflightError("PREFLIGHT_PROCESS_FAILED") from None
    if len(result.stdout) > 16384 or len(result.stderr) > 16384:
        raise PreflightError("PREFLIGHT_OUTPUT_LIMIT")
    return result


def check(candidate_root, candidate_receipt, runner_temp):
    """Return fixed-scope evidence; leave candidate and runner temp unchanged."""
    if sys.platform != "linux":
        raise PreflightError("PREFLIGHT_LINUX_REQUIRED")
    root = Path(os.path.abspath(candidate_root))
    temporary_root = Path(os.path.abspath(runner_temp))
    if temporary_root.is_relative_to(root):
        raise PreflightError("PREFLIGHT_TEMP_INSIDE_CANDIDATE")
    for path in (*reversed(temporary_root.parents), temporary_root):
        info = path.lstat()
        if not stat.S_ISDIR(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise PreflightError("PREFLIGHT_TEMP_INVALID")
    if (candidate_receipt.get("status") != "native_dual_identity_verified"
            or candidate_receipt.get("scope") != candidate.SCOPE
            or candidate_receipt.get("native_verification") != candidate_receipt.get("expected_native_result")):
        raise PreflightError("PREFLIGHT_UNVERIFIED_CANDIDATE")
    native_identity = candidate_receipt["native_verification"]
    executable = root / "bin/FifthFreedom.Server"
    if candidate.sha_file(executable) != candidate_receipt["files"].get("bin/FifthFreedom.Server"):
        raise PreflightError("PREFLIGHT_EXECUTABLE_CHANGED")
    for name, row in candidate_receipt["files"].items():
        if candidate.sha_file(root / name) != row:
            raise PreflightError("PREFLIGHT_CANDIDATE_CHANGED")
    original = snapshot(root)
    expected = expected_report(native_identity)
    try:
        with tempfile.TemporaryDirectory(prefix="ff-native-preflight-", dir=temporary_root) as directory:
            private = Path(directory)
            private.chmod(0o700)
            data = private / "data"
            data.mkdir(mode=0o700)
            key = private / "issuer.key"
            config = private / "deployment.json"
            private_file(key, secrets.token_bytes(32))
            private_file(config, candidate.json_bytes({"schema": 1, "overlay_ipv4": "10.77.0.1",
                "overlay_prefix": 24, "overlay_interface": "ffpublic0", "auth_key_file": str(key),
                "data_directory": str(data)}))
            private_before = snapshot(private)
            result = captured([str(executable), "--deployment-profile", "public",
                               "--check-public-deployment", str(config)], private)
            if result.returncode or result.stderr:
                raise PreflightError("PREFLIGHT_NATIVE_REJECTED")
            try:
                report = json.loads(result.stdout, object_pairs_hook=candidate.unique)
            except (TypeError, ValueError):
                raise PreflightError("PREFLIGHT_REPORT_INVALID") from None
            # Canonical serialization also rejects integer/boolean substitution.
            if candidate.json_bytes(report) != candidate.json_bytes(expected):
                raise PreflightError("PREFLIGHT_REPORT_MISMATCH")
            rejected = captured([str(executable), "--deployment-profile", "public"], private)
            if not rejected.returncode or rejected.stdout or b"PUBLIC_PROFILE_NOT_READY:" not in rejected.stderr:
                raise PreflightError("PREFLIGHT_PUBLIC_GATE_NOT_CLOSED")
            if private_before != snapshot(private):
                raise PreflightError("PREFLIGHT_PRIVATE_STATE_CHANGED")
            if original != snapshot(root):
                raise PreflightError("PREFLIGHT_CANDIDATE_CHANGED")
    except PreflightError:
        raise
    except (OSError, ValueError):
        raise PreflightError("PREFLIGHT_FIXTURE_FAILED") from None
    return {"schema": 1, "status": "static_preflight_and_closed_gate_verified",
            "static_preflight": expected, "ordinary_public_startup_rejected": True,
            "ephemeral_inputs_removed": True, "candidate_unchanged": True,
            "native_public_profile_enabled": False, "game_executed": False,
            "deployed": False, "published": False}
