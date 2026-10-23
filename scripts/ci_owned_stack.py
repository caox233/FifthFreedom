"""Same-run, owned namespace EasyTier TCP + native lease development proof.

Only a bounded sanitized receipt survives. This does not admit ordinary Public,
run a game, publish binaries, or replace the QUIC/WSS/wrong-pin matrix.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict
import json
import os
from pathlib import Path
import select
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_easytier_probe as probe
import ci_native_lifecycle as native
from fifth_freedom_control.easytier_profile import emit_private_toml, parse_profile, write_private_toml
from fifth_freedom_control.owned_process import LifecycleError

SCHEMA = "ff-owned-overlay-native-stack-development-v1"
CASES = ("lease-revoke", "peer-loss")
SCOPE = dict(transport="tcp", purpose="owned-isolated-development", production_admission=False,
    ordinary_public_enabled=False, game_executed=False, complete_client_package=False,
    runtime_binary_uploaded=False, raw_runtime_output_retained=False)


def require(value, code):
    if not value: probe.fail(code)


def same_run(build, commit, run_id, attempt):
    require((build.get("checkout_commit"), build.get("run_id"), build.get("run_attempt"))
            == (commit, run_id, attempt), "STACK_RUNTIME_RUN_MISMATCH")


class StackWorker(probe.NamespaceWorker):
    """Reuse reviewed bounded commands/exact worker stop; retain START barrier."""
    def __init__(self, namespaces, name, manifest_path):
        self.process, self.pidfd, self.cleaned = None, -1, False
        expected = parse_profile(probe.encoded(probe.json_bytes(Path(manifest_path).read_bytes())["profile"])).sha256
        namespaces.assert_owned(name)
        descriptor = namespaces.owned[name]
        try:
            self.process = subprocess.Popen([sys.executable, "-I", "-B", str(ROOT / "scripts/owned_stack_worker.py"),
                str(descriptor), str(manifest_path)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, bufsize=0, env=probe.CLEAN_ENV, pass_fds=(descriptor,), close_fds=True)
            self.pidfd = os.pidfd_open(self.process.pid)
            self.process.stdin.write(b"START\n")
            require(self.read(15) == {"event": "started", "profile_sha256": expected}, "STACK_WORKER_START_FAILED")
        except BaseException:
            self.stop()
            raise


def await_state(server, client=None, *, revoked=False, timeout=60):
    deadline = time.monotonic() + timeout
    client_ready = client is None
    while True:
        if client is not None and not client_ready:
            response = client.command("observe")
            require(type(response) is dict and set(response) == {"event", "authenticated"}
                    and response["event"] == "observation" and type(response["authenticated"]) is bool,
                    "STACK_CLIENT_OBSERVATION_INVALID")
            client_ready = response["authenticated"]
        response = server.command("status")
        if not revoked and response == {"event": "ready", "native_listeners": 5} and client_ready:
            return response
        if revoked and response == dict(event="revoked", native_reaped=True, core_reaped=True,
                                        monitor_revoked=True, native_listeners=0):
            return response
        allowed = ({"event": "pending"}, {"event": "ready", "native_listeners": 5})
        require(response in allowed, "STACK_STATE_INVALID")
        require(time.monotonic() < deadline, "STACK_STATE_TIMEOUT")
        time.sleep(.05)


def make_manifest(stage, name, namespaces, profile, key, secret, images, case, native_identity):
    config = stage / (name + ".toml")
    write_private_toml(config, emit_private_toml(profile, network_secret=secret, local_private_key=key.private_bytes_raw()))
    namespace_name = namespaces.topology.names[0 if profile.role == "initiator" else 1]
    info = os.fstat(namespaces.owned[namespace_name])
    document = dict(profile=asdict(profile), namespace=[info.st_dev, info.st_ino], config=str(config),
                    case=case, core=images["core"], cli=images["cli"])
    if profile.role == "responder":
        keyfile, data, configuration = stage / (name + ".key"), stage / (name + "-data"), stage / (name + "-native.json")
        native.private_file(keyfile, secrets.token_bytes(32)); data.mkdir(mode=0o700)
        native.private_file(configuration, probe.encoded(dict(schema=1,
            overlay_ipv4=str(profile.local.overlay.ip), overlay_prefix=profile.local.overlay.network.prefixlen,
            overlay_interface=profile.interface_name, auth_key_file=str(keyfile), data_directory=str(data),
            runtime_profile_sha256=profile.sha256, runtime_executable_sha256=images["core"]["sha256"])))
        document.update(native=images["native"], native_config=str(configuration), native_identity=native_identity)
    path = stage / (name + ".json")
    native.private_file(path, probe.encoded(document))
    return path


def run_case(case, candidate, images, stage, identity, ip, nft):
    from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
    topology = probe.ProbeTopology.fresh()
    namespaces = probe.ProbeNamespaces(ip, topology, run=probe.namespace_command)
    workers = []
    row = dict(case=case, status="failed", cleanup_errors=[], namespaces_created=0, namespaces_removed=0)
    try:
        namespaces.setup_pair()
        keys = [X25519PrivateKey.generate(), X25519PrivateKey.generate()]
        profiles = probe.profile_pair(topology, "tcp", candidate, keys)
        secret = secrets.token_bytes(32)
        for name, profile in zip(topology.names, profiles):
            namespaces.containment(name, nft, profile, install=True)
        manifests = [make_manifest(stage, case + ("-client" if index == 0 else "-server"), namespaces,
            profile, keys[index], secret, images, case, identity) for index, profile in enumerate(profiles)]
        server = StackWorker(namespaces, topology.names[1], manifests[1]); workers.append(server)
        client = StackWorker(namespaces, topology.names[0], manifests[0]); workers.append(client)
        await_state(server, client)
        row.update(actual_native_ready=True, owned_listeners=5, runtime_profile_sha256=profiles[1].sha256)
        require(client.command("hold") == dict(event="connected", established_tcp=True), "STACK_CONNECTION_NOT_ESTABLISHED")
        row["established_tcp"] = True
        # Host namespace owner independently rechecks the complete strict rules
        # and routes immediately before either finite control. It does not
        # accept a worker's boolean about encryption or namespace containment.
        for name, profile in zip(topology.names, profiles): namespaces.containment(name, nft, profile)
        if case == "lease-revoke":
            require(server.command("pause") == dict(event="paused", fresh_observation=True), "STACK_PAUSE_NOT_ENTERED")
            require(server.command("revoke") == dict(event="native-stopped", exit_code=92,
                rebound_ports=5, server_overlay_alive=True), "STACK_NATIVE_REVOKE_FAILED")
            require(client.command("closed") == dict(event="closed", actual_eof_or_reset=True,
                client_core_alive=True), "STACK_HELD_CONNECTION_NOT_CLOSED")
            require(server.command("release") == dict(event="released", server_overlay_alive_at_release=True),
                    "STACK_RELEASE_FAILED")
            row.update(native_watchdog_exit=92, ports_rebound=5, held_tcp_eof_or_reset=True,
                       overlay_alive_during_close=True)
        else:
            require(client.stop(), "STACK_EXACT_PEER_STOP_FAILED")
            await_state(server, revoked=True, timeout=20)
            row.update(exact_peer_reaped=True, monitor_permanently_revoked=True,
                       native_reaped=True, native_listeners_remaining=0, remote_tcp_eof_claimed=False)
        row["containment_after"] = [namespaces.containment(name, nft, profile)
            for name, profile in zip(topology.names, profiles)]
        row["status"] = "owned_stack_case_verified"
    except (probe.ProbeError, probe.IsolationError, native.ProbeError, LifecycleError,
            OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
        row["error"] = probe.diagnostic(error)
        if isinstance(error, probe.ContainmentMismatch): row["fixed_diagnostics"] = error.diagnostics
    except KeyboardInterrupt:
        row["error"] = "OWNED_STACK_CANCELLED"
    finally:
        for worker in reversed(workers):
            try:
                if not worker.stop(): row["cleanup_errors"].append("STACK_WORKER_CLEANUP_FAILED")
            except BaseException:
                row["cleanup_errors"].append("STACK_WORKER_CLEANUP_FAILED")
        row["namespaces_created"] = len(namespaces.owned)
        try: namespaces.cleanup()
        except (OSError, probe.ProbeError, probe.IsolationError):
            row["cleanup_errors"].append("STACK_NAMESPACE_CLEANUP_FAILED")
        row["namespaces_removed"] = len(namespaces.deleted)
        row["cleanup_errors"].extend(namespaces.cleanup_errors)
        if row["cleanup_errors"] or row["namespaces_created"] != row["namespaces_removed"]:
            row["status"] = "failed"
    return row


def run(args):
    result = dict(schema=SCHEMA, status="failed", scope=dict(SCOPE), cases=[],
        precondition_failures=probe.preconditions(), copied_package_native_verified=False,
        source_package_unchanged=False, private_stage_removed=False)
    if result["precondition_failures"]: return result
    stage, stage_identity, rows, receipt = None, None, None, None
    try:
        commit = probe.checked_checkout()
        require(commit == args.commit, "STACK_CHECKOUT_MISMATCH")
        receipt = native.candidate.read_json(args.candidate_receipt)
        require(receipt == native.candidate.read_json(args.package / "CANDIDATE-RECEIPT.json"), "STACK_NATIVE_RECEIPT_MISMATCH")
        rows = native.validate_input(args.package, receipt, args.commit, args.run_id, args.run_attempt)
        lock = probe.json_bytes((ROOT / "scripts/easytier-source.lock.json").read_bytes())
        build, candidate = probe.load_build(args.build_receipt, lock)
        same_run(build, args.commit, args.run_id, args.run_attempt)
        result.update(source_commit=commit, run_id=args.run_id, run_attempt=args.run_attempt,
            runtime_candidate=asdict(candidate), cli_identity=build["built_executables"]["easytier-cli"])
        stage = Path(tempfile.mkdtemp(prefix="ff-owned-stack-", dir="/tmp"))
        info = stage.lstat(); stage_identity = (info.st_dev, info.st_ino)
        copied = stage / "package"
        native.copy_private_package(args.package, copied, rows)
        actual = probe.bounded_command([str(copied / "tools/verify_public_package"), str(copied)], timeout=10, limit=16384)
        require(probe.json_bytes(actual) == receipt["native_verification"], "STACK_COPIED_NATIVE_IDENTITY_MISMATCH")
        result["copied_package_native_verified"] = True
        images = {name: probe.private_copy(source, stage / name, build["built_executables"][role])
            for name, source, role in (("core", args.core, "easytier-core"), ("cli", args.cli, "easytier-cli"))}
        images["native"] = dict(path=str(copied / "bin/FifthFreedom.Server"), **rows["bin/FifthFreedom.Server"])
        for case in CASES:
            row = run_case(case, candidate, images, stage, receipt["native_verification"], shutil.which("ip"), shutil.which("nft"))
            result["cases"].append(row)
            require(row["status"] == "owned_stack_case_verified", "STACK_CASES_INCOMPLETE")
        require(all(native.candidate.sha_file(copied / name) == row for name, row in rows.items()), "STACK_PRIVATE_PACKAGE_CHANGED")
        result["status"] = "owned_stack_development_verified"
    except (probe.ProbeError, probe.IsolationError, native.ProbeError, LifecycleError,
            OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
        result["error"] = probe.diagnostic(error)
    except KeyboardInterrupt:
        result["error"] = "OWNED_STACK_CANCELLED"
    finally:
        if rows is not None:
            try:
                result["source_package_unchanged"] = native.validate_input(args.package, receipt,
                    args.commit, args.run_id, args.run_attempt) == rows
            except (native.ProbeError, OSError, ValueError): pass
            if not result["source_package_unchanged"]: result["status"] = "failed"
        if stage is not None:
            try:
                info = stage.lstat()
                require(stat.S_ISDIR(info.st_mode) and (info.st_dev, info.st_ino) == stage_identity
                    and info.st_uid == os.geteuid() and not info.st_mode & 0o077, "STACK_PRIVATE_STAGE_CHANGED")
                shutil.rmtree(stage)
                result["private_stage_removed"] = True
            except (OSError, probe.ProbeError):
                result["status"], result["error"] = "failed", "STACK_PRIVATE_CLEANUP_FAILED"
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("package", "candidate-receipt", "build-receipt", "core", "cli", "output"):
        parser.add_argument("--" + option, type=Path, required=True)
    for option in ("commit", "run-id", "run-attempt"):
        parser.add_argument("--" + option, required=True)
    args = parser.parse_args(argv)
    if not args.output.is_absolute() or not args.output.parent.is_dir() or args.output.exists():
        sys.stderr.write("PUBLIC_RECEIPT_PARENT_REQUIRED\n"); return 1
    def cancel(_signal, _frame):
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, cancel); signal.signal(signal.SIGINT, cancel)
    result = run(args)
    try: native.write_report(args.output, result)
    except OSError:
        sys.stderr.write("PUBLIC_RECEIPT_WRITE_FAILED\n"); return 1
    success = result["status"] == "owned_stack_development_verified"
    print("OWNED_STACK_DEVELOPMENT_" + ("VERIFIED" if success else "FAILED"))
    return 0 if success else 1


if __name__ == "__main__":
    raise SystemExit(main())
