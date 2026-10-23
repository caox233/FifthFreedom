"""Owned hosted-Linux overlay development probe; no production admission."""
from __future__ import annotations

import argparse
import base64
from dataclasses import asdict, dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import select
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "src/public"))
from fifth_freedom_control.easytier_profile import parse_profile, emit_private_toml, write_private_toml
from fifth_freedom_control.owned_process import LifecycleError, VerifiedExecutable
from fifth_freedom_control.overlay_supervisor import RuntimeCandidateIdentity
from ci_socket_isolation import OwnedNamespaces, IsolationError, PORT_FIELDS
from easytier_probe_worker import (CLEAN_ENV, ProbeError, WorkerFailure, valid_failure_record,
    valid_readiness_record, fail)

PIN_GUARD = "7eeeda28458f17d8ee393176a767725c19e3315f0bc49e9471ebe138cc123328"
TRANSPORTS = ("quic", "wss", "tcp")
SCHEMA = "ff-easytier-isolated-development-probe-v1"
CONTAINMENT_SCHEMA = "ff-easytier-containment-preflight-v1"


class ContainmentMismatch(ProbeError):
    def __init__(self, diagnostics):
        super().__init__("CONTAINMENT_CHANGED")
        self.diagnostics = diagnostics


class ReadinessTimeout(ProbeError):
    def __init__(self, role, authenticated, samples, last):
        super().__init__("OVERLAY_READY_TIMEOUT")
        self.record = dict(schema="ff-easytier-readiness-timeout-v1", role=role,
            expected_authenticated=authenticated, observation_count=samples, last_observation=last)


def valid_readiness_timeout(value):
    return (type(value) is dict and set(value) == {"schema", "role", "expected_authenticated", "observation_count", "last_observation"}
        and value["schema"] == "ff-easytier-readiness-timeout-v1"
        and type(value["role"]) is str and value["role"] in ("initiator", "responder")
        and type(value["expected_authenticated"]) is bool
        and type(value["observation_count"]) is int and 1 <= value["observation_count"] <= 2048
        and valid_readiness_record(value["last_observation"])
        and not (value["last_observation"]["tun_owned"]
                 and (not value["expected_authenticated"] or value["last_observation"]["peer_authenticated"])))


def encoded(value):
    return (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()


def sha(data):
    return hashlib.sha256(data).hexdigest()


def _unique(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            fail("DUPLICATE_JSON_KEY")
        value[key] = item
    return value


def json_bytes(data, limit=262144):
    if not isinstance(data, bytes) or not 1 <= len(data) <= limit:
        fail("JSON_BOUND_INVALID")
    try:
        return json.loads(data, object_pairs_hook=_unique,
                          parse_constant=lambda _: fail("JSON_NUMBER_INVALID"))
    except (ValueError, UnicodeError, RecursionError):
        fail("JSON_INVALID")


def bounded_command(command, *, payload=None, timeout=8, limit=65536):
    """Host tools only, fixed arguments; never core/CLI or a shell."""
    if (payload is not None and (type(payload) is not bytes or len(payload) > 32768)
            or not 0 < timeout <= 10 or not 1 <= limit <= 65536):
        fail("NAMESPACE_TOOL_BOUNDS_INVALID")
    child, pidfd = None, -1
    try:
        child = subprocess.Popen(command, stdin=subprocess.PIPE if payload is not None else subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=CLEAN_ENV, bufsize=0)
        pidfd = os.pidfd_open(child.pid)
    except OSError:
        if child is not None:
            # This is exactly our unreaped Popen child, never a discovered PID.
            # A zombie's PID cannot be reused before this parent reaps it.
            child.kill()
            child.wait(timeout=2)
            child.stdout.close()
            if child.stdin is not None:
                child.stdin.close()
        fail("NAMESPACE_TOOL_START_FAILED")
    data = bytearray()
    deadline = time.monotonic() + timeout
    try:
        if payload is not None:
            os.set_blocking(child.stdin.fileno(), False)
            offset = 0
            while offset < len(payload):
                remaining = deadline - time.monotonic()
                if remaining <= 0 or not select.select([], [child.stdin], [], remaining)[1]:
                    fail("NAMESPACE_TOOL_TIMEOUT")
                try:
                    offset += os.write(child.stdin.fileno(), payload[offset:offset + 4096])
                except BlockingIOError:
                    continue
            child.stdin.close()
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not select.select([child.stdout], [], [], remaining)[0]:
                fail("NAMESPACE_TOOL_TIMEOUT")
            block = os.read(child.stdout.fileno(), min(8192, limit + 1 - len(data)))
            if not block:
                child.wait(timeout=max(0.001, deadline - time.monotonic()))
                if child.returncode:
                    fail("NAMESPACE_TOOL_FAILED")
                return bytes(data)
            data.extend(block)
            if len(data) > limit:
                fail("NAMESPACE_TOOL_OUTPUT_LIMIT")
    except (OSError, subprocess.SubprocessError):
        fail("NAMESPACE_TOOL_FAILED")
    finally:
        try:
            if not select.select([pidfd], [], [], 0)[0]:
                try:
                    signal.pidfd_send_signal(pidfd, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            child.wait(timeout=2)
        finally:
            child.stdout.close()
            if child.stdin is not None and not child.stdin.closed:
                child.stdin.close()
            os.close(pidfd)


def namespace_command(command):
    # OwnedNamespaces aggregates this exact exception during cleanup.
    try:
        return bounded_command(command)
    except ProbeError as error:
        raise IsolationError(str(error)) from None


def preconditions():
    codes = []
    if sys.platform != "linux":
        return ["LINUX_REQUIRED"]
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        codes.append("GITHUB_HOSTED_REQUIRED")
    if os.geteuid() != 0:
        codes.append("ROOT_NAMESPACE_PRIVILEGES_REQUIRED")
    try:
        cap = next(row for row in Path("/proc/self/status").read_text().splitlines() if row.startswith("CapEff:"))
        value = int(cap.split()[1], 16)
        if value & ((1 << 12) | (1 << 21)) != ((1 << 12) | (1 << 21)):
            codes.append("NET_ADMIN_AND_SYS_ADMIN_REQUIRED")
    except (OSError, ValueError, StopIteration):
        codes.append("CAPABILITIES_UNVERIFIED")
    if not hasattr(os, "setns") or not hasattr(os, "pidfd_open") or not hasattr(signal, "pidfd_send_signal"):
        codes.append("OWNED_NAMESPACE_APIS_REQUIRED")
    try:
        info = Path("/dev/net/tun").lstat()
        if not stat.S_ISCHR(info.st_mode) or info.st_rdev != os.makedev(10, 200) or not os.access("/dev/net/tun", os.R_OK | os.W_OK):
            codes.append("EXISTING_TUN_REQUIRED")
    except OSError:
        codes.append("EXISTING_TUN_REQUIRED")
    for tool in ("ip", "nft"):
        if shutil.which(tool) is None:
            codes.append(tool.upper() + "_REQUIRED")
    return codes


@dataclass(frozen=True)
class ProbeTopology:
    token: str
    subnet: int

    def __post_init__(self):
        if (type(self.token) is not str or re.fullmatch(r"[0-9a-f]{16}", self.token) is None
                or type(self.subnet) is not int or not 0 <= self.subnet <= 255):
            fail("TOPOLOGY_INVALID")

    @classmethod
    def fresh(cls):
        return cls(secrets.token_hex(8), secrets.randbelow(256))

    @property
    def names(self):
        return ("ffet-" + self.token + "-c", "ffet-" + self.token + "-s")

    def underlay(self, index):
        return f"10.241.{self.subnet}.{index + 1}"

    def overlay(self, index):
        return f"10.242.{self.subnet}.{index + 1}"


def _match(left, right):
    return {"match": {"op": "==", "left": left, "right": right}}


def containment_rules(profile):
    """Exact nft JSON objects, including terminal blocked-attempt counters."""
    family, table = "inet", "ffprobe"
    result = [{"table": {"family": family, "name": table}}]
    for chain in ("input", "output", "forward"):
        result.append({"chain": {"family": family, "table": table, "name": chain,
            "type": "filter", "hook": chain, "prio": 0, "policy": "drop"}})
    def rule(chain, expressions):
        result.append({"rule": {"family": family, "table": table, "chain": chain, "expr": expressions}})
    def flow(chain, interface, source, destination, protocol, field=None, port=None, established=False):
        interface_key = "iifname" if chain == "input" else "oifname"
        expr = [_match({"meta": {"key": interface_key}}, interface),
                _match({"payload": {"protocol": "ip", "field": "saddr"}}, source),
                _match({"payload": {"protocol": "ip", "field": "daddr"}}, destination)]
        if field is not None:
            # Typed tcp/udp payloads require that transport protocol. nft
            # establishes the dependency and omits a redundant explicit meta
            # l4proto predicate in its canonical readback (hosted evidence).
            expr.append(_match({"payload": {"protocol": protocol, "field": field}}, port))
        else:
            # Portless overlay rules still need their explicit L4 restriction.
            expr.append(_match({"meta": {"key": "l4proto"}}, protocol))
        if established:
            expr.append(_match({"ct": {"key": "state"}}, "established"))
        rule(chain, expr + [{"accept": None}])
    protocol = "udp" if profile.transport == "quic" else "tcp"
    for chain in ("input", "output"):
        # RPC is limited to the configured TCP port on loopback, including the
        # ephemeral querying client's reply direction.
        for field in ("sport", "dport"):
            flow(chain, "lo", "127.0.0.1", "127.0.0.1", "tcp", field, profile.rpc_port)
        incoming = chain == "input"
        source = profile.remote if incoming else profile.local
        destination = profile.local if incoming else profile.remote
        request = (profile.role == "responder") == incoming
        port = profile.local.listen_port if profile.role == "responder" else profile.remote.listen_port
        flow(chain, "ff-under", source.underlay_ipv4, destination.underlay_ipv4, protocol,
             "dport" if request else "sport", port, established=not request)
        for inner in ("tcp", "udp"):
            flow(chain, profile.interface_name, str(source.overlay.ip), str(destination.overlay.ip), inner)
    for chain in ("input", "output", "forward"):
        rule(chain, [{"counter": {"packets": 0, "bytes": 0}}, {"drop": None}])
    return result


def normalize_nft(objects):
    result, dropped = [], {"input": 0, "output": 0, "forward": 0}
    if not isinstance(objects, list) or len(objects) > 64:
        fail("CONTAINMENT_READBACK_INVALID")
    for wrapper in objects:
        if type(wrapper) is not dict or len(wrapper) != 1:
            fail("CONTAINMENT_READBACK_INVALID")
        kind, original = next(iter(wrapper.items()))
        if kind == "metainfo":
            continue
        if kind not in ("table", "chain", "rule") or type(original) is not dict:
            fail("CONTAINMENT_READBACK_INVALID")
        value = dict(original)
        value.pop("handle", None)
        if kind == "rule":
            expressions = []
            if type(value.get("expr")) is not list:
                fail("CONTAINMENT_READBACK_INVALID")
            for expression in value["expr"]:
                if type(expression) is not dict:
                    fail("CONTAINMENT_READBACK_INVALID")
                if set(expression) == {"counter"}:
                    counter = expression["counter"]
                    if type(counter) is not dict or set(counter) != {"packets", "bytes"} or any(type(n) is not int or n < 0 for n in counter.values()):
                        fail("CONTAINMENT_READBACK_INVALID")
                    chain = value.get("chain")
                    if chain not in dropped:
                        fail("CONTAINMENT_READBACK_INVALID")
                    dropped[chain] += counter["packets"]
                    expression = {"counter": {"packets": 0, "bytes": 0}}
                expressions.append(expression)
            value["expr"] = expressions
        result.append({kind: value})
    # nft lists each chain's rules together, independent of insertion order
    # between chains. Preserve rule order *within* each chain and reject extras.
    tables = [item for item in result if "table" in item]
    chains = sorted((item for item in result if "chain" in item), key=lambda item: str(item["chain"].get("name")))
    rules = []
    for name in ("forward", "input", "output"):
        rules.extend(item for item in result if "rule" in item and item["rule"].get("chain") == name)
    if len(tables) + len(chains) + len(rules) != len(result):
        fail("CONTAINMENT_READBACK_INVALID")
    return tables + chains + rules, dropped


def nft_difference_codes(expected, actual):
    """Bounded shape evidence only; does not normalize or accept any mismatch.

    Paths are constructed solely from this whitelist and bounded list indices.
    Unknown keys, addresses, names and raw values never enter diagnostics.
    """
    known_keys = frozenset(("table", "chain", "rule", "family", "name", "type", "hook", "prio",
        "policy", "expr", "match", "op", "left", "right", "meta", "key", "payload", "protocol",
        "field", "ct", "accept", "drop", "counter", "packets", "bytes", "set", "prefix", "addr", "len"))
    enums = {value: value.upper().replace("-", "_") for value in ("inet", "input", "output", "forward",
        "filter", "drop", "accept", "ip", "ip6", "tcp", "udp", "ipv4", "ipv6", "nfproto", "iifname",
        "oifname", "l4proto", "saddr", "daddr", "sport", "dport", "state", "established", "related",
        "new", "invalid", "untracked")}
    enums.update({"==": "EQ", "!=": "NE", "in": "IN", "&": "AND", "|": "OR"})
    kinds = {dict: "OBJECT", list: "LIST", str: "STRING", int: "INTEGER", bool: "BOOL", type(None): "NULL"}
    codes = []
    def number(value):
        return str(value) if 0 <= value <= 63 else "OVER63"
    def emit(path, suffix):
        if len(codes) < 16:
            code = "NFT_" + "_".join(path) + "_" + suffix
            codes.append(code if len(code) <= 160 else "NFT_UNCLASSIFIED_DEEP_DIFFERENCE")
    def visit(one, two, path=()):
        if len(codes) >= 16 or type(one) is type(two) and one == two:
            return
        if type(one) is not type(two):
            emit(path, "TYPE_" + kinds.get(type(one), "OTHER") + "_TO_" + kinds.get(type(two), "OTHER"))
        elif type(one) is dict:
            for key in sorted((set(one) | set(two)) & known_keys):
                if key not in one:
                    emit(path + (key.upper(),), "EXTRA_KEY")
                elif key not in two:
                    emit(path + (key.upper(),), "MISSING_KEY")
                else:
                    visit(one[key], two[key], path + (key.upper(),))
            if set(one) - known_keys or set(two) - known_keys:
                emit(path, "UNCLASSIFIED_KEY_DIFFERENCE")
        elif type(one) is list:
            if len(one) != len(two):
                emit(path, "COUNT_" + number(len(one)) + "_TO_" + number(len(two)))
            for index, (first, second) in enumerate(zip(one, two)):
                if index >= 64:
                    emit(path, "UNCLASSIFIED_INDEX_OVER63"); break
                visit(first, second, path + ("I" + str(index),))
        elif type(one) is str and one in enums and two in enums:
            emit(path, enums[one] + "_TO_" + enums[two])
        else:
            emit(path, kinds.get(type(one), "OTHER") + "_VALUE_DIFFERENT")
    visit(expected, actual)
    return codes or ["NFT_UNCLASSIFIED_DIFFERENCE"]


class ProbeNamespaces(OwnedNamespaces):
    def setup_pair(self):
        client, server = self.topology.names
        for name in (client, server):
            self.create(name)
        self.assert_owned(server)
        self.command(client, "link", "add", "ff-under", "type", "veth", "peer", "name", "ff-under", "netns", server)
        for index, name in enumerate((client, server)):
            self.command(name, "link", "set", "lo", "up")
            self.command(name, "address", "add", self.topology.underlay(index) + "/30", "dev", "ff-under")
            self.command(name, "link", "set", "ff-under", "up")

    def verify_routes(self, name):
        for family in ("-4", "-6"):
            routes = json_bytes(self.command(name, family, "-json", "route", "show", "table", "all"))
            if type(routes) is not list or any(type(route) is not dict or route.get("dst") in ("default", "0.0.0.0/0", "::/0") for route in routes):
                fail("DEFAULT_ROUTE_FORBIDDEN")

    def containment(self, name, nft, profile, install=False):
        self.assert_owned(name)
        expected = containment_rules(profile)
        if install:
            script = {"nftables": [{"add": item} for item in expected]}
            bounded_command(self.execute(name, [nft, "--json", "--file", "-"]), payload=encoded(script))
        raw = bounded_command(self.execute(name, [nft, "--json", "list", "ruleset"]))
        value = json_bytes(raw)
        if type(value) is not dict or set(value) != {"nftables"}:
            fail("CONTAINMENT_READBACK_INVALID")
        actual, dropped = normalize_nft(value["nftables"])
        normalized_expected = normalize_nft(expected)[0]
        if actual != normalized_expected:
            raise ContainmentMismatch(nft_difference_codes(normalized_expected, actual))
        self.verify_routes(name)
        return dropped


def load_build(path, lock):
    value = json_bytes(Path(path).read_bytes())
    if type(value) is not dict or type(lock) is not dict:
        fail("GUARD3_BUILD_IDENTITY_REQUIRED")
    patches = lock.get("patches")
    if (type(patches) is not list or len(patches) != 6 or any(type(item) is not dict for item in patches)
            or patches[-1].get("sha256") != PIN_GUARD or patches[-1].get("role") != "production-guard"
            or value.get("status") != "built_and_contracts_verified_not_admitted"
            or value.get("upstream") != lock["upstream"] or value.get("build_inputs") != lock["build"]
            or value.get("cargo_lock_sha256") != lock["cargo_lock_sha256"]
            or value.get("patch_manifest_sha256") != sha(encoded(patches))
            or value.get("runtime_executed") is not False or value.get("public_startup_allowed") is not False):
        fail("GUARD3_BUILD_IDENTITY_REQUIRED")
    required = {"fifth_freedom_secure_profile_contract": 4, "fifth_freedom_ingress_contract": 10,
        "peers::secure_datagram::tests": 9, "peers::peer_session::tests": 2,
        "peer_conn_secure_mode_": 7, "ff_explicit_pin_contract": 3}
    runs = value.get("runs")
    if type(runs) is not list or len(runs) != 7:
        fail("GUARD3_CONTRACTS_REQUIRED")
    for row in runs:
        if (type(row) is not dict or type(row.get("exit_code")) is not int or row["exit_code"] != 0
                or type(row.get("stage")) is not str):
            fail("GUARD3_CONTRACTS_REQUIRED")
        if row.get("stage") in required:
            if (any(type(row.get(field)) is not int for field in ("passed", "failed", "ignored"))
                    or row.get("passed") != required.pop(row["stage"])
                    or row.get("failed") != 0 or row.get("ignored") != 0):
                fail("GUARD3_CONTRACTS_REQUIRED")
        elif row.get("stage") != "release-build":
            fail("GUARD3_CONTRACTS_REQUIRED")
    images = value.get("built_executables")
    if required or type(images) is not dict or set(images) != {"easytier-core", "easytier-cli"}:
        fail("GUARD3_CONTRACTS_REQUIRED")
    for image in images.values():
        if (type(image) is not dict or set(image) != {"sha256", "bytes"}
                or type(image["bytes"]) is not int or not 64 <= image["bytes"] <= 512 * 1024 * 1024
                or type(image["sha256"]) is not str or re.fullmatch(r"[0-9a-f]{64}", image["sha256"]) is None):
            fail("GUARD3_IMAGE_IDENTITY_REQUIRED")
    core = images["easytier-core"]
    candidate = RuntimeCandidateIdentity(lock["upstream"]["commit"], value["source_tree_sha256"],
        value["patch_manifest_sha256"], lock["cargo_lock_sha256"], sha(encoded(lock["build"])),
        core["sha256"], core["bytes"])
    return value, candidate


def private_copy(source, destination, identity):
    if (type(identity.get("bytes")) is not int or not 64 <= identity["bytes"] <= 512 * 1024 * 1024
            or type(identity.get("sha256")) is not str or re.fullmatch(r"[0-9a-f]{64}", identity["sha256"]) is None):
        fail("IMAGE_IDENTITY_INVALID")
    descriptor = os.open(source, os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC | os.O_NOFOLLOW)
    try:
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode) or before.st_size != identity["bytes"]:
            fail("IMAGE_FILE_INVALID")
        data = bytearray()
        while len(data) <= identity["bytes"]:
            block = os.read(descriptor, min(1048576, identity["bytes"] + 1 - len(data)))
            if not block:
                break
            data.extend(block)
        after = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(before, key) != getattr(after, key) for key in fields) or len(data) != identity["bytes"] or sha(data) != identity["sha256"]:
            fail("IMAGE_HASH_MISMATCH")
    finally:
        os.close(descriptor)
    with Path(destination).open("xb") as target:
        os.fchmod(target.fileno(), 0o700)
        target.write(data)
        target.flush(); os.fsync(target.fileno())
    with VerifiedExecutable(destination, identity["sha256"], identity["bytes"]):
        pass
    return dict(path=str(destination), **identity)


class NamespaceWorker:
    def __init__(self, namespaces, name, manifest_path):
        self.process = None
        self.pidfd = -1
        self.cleaned = False
        self.worker_failure = None
        expected_profile = parse_profile(json.dumps(json_bytes(Path(manifest_path).read_bytes())["profile"]).encode()).sha256
        namespaces.assert_owned(name)
        fd = namespaces.owned[name]
        try:
            self.process = subprocess.Popen([sys.executable, "-I", "-B", str(ROOT / "scripts/easytier_probe_worker.py"),
                str(fd), str(manifest_path)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, bufsize=0, env=CLEAN_ENV, pass_fds=(fd,), close_fds=True)
            self.pidfd = os.pidfd_open(self.process.pid)
            self.process.stdin.write(b"START\n")
            reply = self.read(8)
            if reply != {"event": "started", "profile_sha256": expected_profile}:
                fail("WORKER_START_FAILED")
        except BaseException as error:
            try:
                cleaned = self.stop()
            except BaseException:
                cleaned = False
            if isinstance(error, WorkerFailure) and not cleaned and error.record["cleanup_reaped"]:
                error.record = dict(error.record, cleanup_reaped=False, cleanup_code="WORKER_CHILD_CLEANUP_FAILED")
            raise

    def read(self, timeout=8):
        deadline, data = time.monotonic() + timeout, bytearray()
        while b"\n" not in data:
            remaining = deadline - time.monotonic()
            if remaining <= 0 or not select.select([self.process.stdout], [], [], remaining)[0]:
                fail("WORKER_REPLY_TIMEOUT")
            block = os.read(self.process.stdout.fileno(), 4097 - len(data))
            if not block:
                fail("WORKER_REPLY_EOF")
            data.extend(block)
            if len(data) > 4096:
                fail("WORKER_REPLY_LIMIT")
        if data.count(b"\n") != 1 or not data.endswith(b"\n"):
            fail("WORKER_REPLY_INVALID")
        reply = json_bytes(bytes(data), 4096)
        if type(reply) is dict and reply.get("event") == "failed":
            if not valid_failure_record(reply):
                fail("WORKER_FAILURE_FRAME_INVALID")
            if getattr(self, "worker_failure", None) is None:
                self.worker_failure = reply
            raise WorkerFailure(self.worker_failure)
        return reply

    def command(self, action, **values):
        if self.pidfd < 0 or select.select([self.pidfd], [], [], 0)[0]:
            fail("WORKER_EXITED")
        self.process.stdin.write((json.dumps(dict(action=action, **values)) + "\n").encode())
        self.process.stdin.flush()
        return self.read()

    def stop(self):
        if self.process is None:
            return self.cleaned
        try:
            if getattr(self, "worker_failure", None) is None:
                if self.pidfd >= 0 and not select.select([self.pidfd], [], [], 0)[0]:
                    try:
                        response = self.command("stop")
                        self.cleaned = response == {"event": "stopped", "children_reaped": True}
                    except (ProbeError, OSError, subprocess.SubprocessError):
                        self.cleaned = False
                else:
                    self.cleaned = False
            if getattr(self, "worker_failure", None) is not None:
                # A failure frame follows child cleanup attempts and replaces
                # the ordinary stop ACK. The enclosing run still fails; worker
                # death alone never proves that its children were reaped.
                self.cleaned = self.worker_failure["cleanup_reaped"]
            if self.pidfd < 0 and not self.process.stdin.closed:
                self.process.stdin.close()  # Unreleased bootstrap receives EOF.
            try:
                # Reap even when pidfd already reports exit. Merely noticing a
                # readable pidfd does not reap our exact Popen child.
                self.process.wait(timeout=4)
            except subprocess.TimeoutExpired:
                self.cleaned = False
                if self.pidfd < 0:
                    raise
                try:
                    signal.pidfd_send_signal(self.pidfd, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                self.process.wait(timeout=3)
            expected_exit = 1 if getattr(self, "worker_failure", None) is not None else 0
            self.cleaned = self.cleaned and self.process.returncode == expected_exit
        finally:
            for stream in (self.process.stdin, self.process.stdout):
                if stream is not None and not stream.closed:
                    stream.close()
            if self.pidfd >= 0:
                os.close(self.pidfd); self.pidfd = -1
            self.process = None
        return self.cleaned


def await_ready(worker, *, authenticated=True, role="initiator"):
    if role not in ("initiator", "responder") or type(authenticated) is not bool:
        fail("OBSERVATION_INVALID")
    deadline = time.monotonic() + 15
    samples = 0
    while True:
        result = worker.command("observe")
        if (type(result) is not dict or result.get("event") != "observation"
                or not valid_readiness_record(result.get("readiness"))
                or type(result.get("tun_owned")) is not bool or type(result.get("peer_authenticated")) is not bool
                or result["tun_owned"] != result["readiness"]["tun_owned"]
                or result["peer_authenticated"] != result["readiness"]["peer_authenticated"]):
            fail("OBSERVATION_INVALID")
        samples += 1
        if result["tun_owned"] and (not authenticated or result["peer_authenticated"]):
            return result
        if samples >= 2048 or time.monotonic() >= deadline:
            raise ReadinessTimeout(role, authenticated, samples, result["readiness"])
        time.sleep(0.1)


def profile_pair(topology, transport, candidate, keys):
    network = "ffprobe-" + secrets.token_hex(16)
    endpoints = [dict(underlay_ipv4=topology.underlay(index), overlay_cidr=topology.overlay(index) + "/30",
        listen_port=22000 + index, public_key=base64.b64encode(key.public_key().public_bytes_raw()).decode())
        for index, key in enumerate(keys)]
    return [parse_profile(json.dumps(dict(schema="ff-easytier-owned-probe-v1", purpose="owned-isolated-development",
        transport=transport, role="initiator" if index == 0 else "responder", instance_id=str(uuid.uuid4()),
        network_name=network, interface_name="ffoverlay", rpc_port=15888, local=endpoints[index],
        remote=endpoints[1 - index], candidate=asdict(candidate))).encode()) for index in (0, 1)]


def manifest(stage, name, namespaces, profile, private_key, secret, images):
    config = stage / (name + ".toml")
    write_private_toml(config, emit_private_toml(profile, network_secret=secret, local_private_key=private_key.private_bytes_raw()))
    namespace_name = namespaces.topology.names[0 if profile.role == "initiator" else 1]
    info = os.fstat(namespaces.owned[namespace_name])
    value = dict(profile=asdict(profile), namespace=[info.st_dev, info.st_ino], config=str(config), **images)
    path = stage / (name + ".json")
    write_private_toml(path, encoded(value))
    return path


def echo(worker, server, ports, expected):
    before = server.command("observe")
    if before.get("tun_owned") is not True or before.get("probe_alive") is not True:
        fail("SERVER_INFRASTRUCTURE_UNHEALTHY")
    result = worker.command("echo", ports=ports)
    outcomes = result.get("outcomes")
    allowed = ("echo",) if expected else ("timeout", "refused")
    if (result.get("event") != "echo" or type(outcomes) is not dict or set(outcomes) != set(PORT_FIELDS)
            or any(value not in allowed for value in outcomes.values())):
        fail("OVERLAY_TRANSFER_CONTRACT_FAILED")
    after = server.command("observe")
    if after.get("tun_owned") is not True or after.get("probe_alive") is not True:
        fail("SERVER_INFRASTRUCTURE_UNHEALTHY")
    return outcomes


def diagnostic(error):
    # No OSError, subprocess error, runtime output or private input becomes a
    # receipt string. Our three exception classes contain only fixed codes.
    if isinstance(error, (ProbeError, IsolationError, LifecycleError)):
        code = str(error)
        if re.fullmatch(r"[A-Z][A-Z0-9_]{1,95}", code):
            return code
    return "DEVELOPMENT_PROBE_FAILED"


def run_transport(transport, candidate, images, stage, ip, nft):
    from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
    topology = ProbeTopology.fresh()
    namespaces = ProbeNamespaces(ip, topology, run=namespace_command)
    workers = []
    row = dict(transport=transport, purpose="development-only" if transport == "tcp" else "product-transport-development-probe",
        status="failed", phases=[], cleanup_errors=[], namespaces_created=0, namespaces_removed=0)
    try:
        namespaces.setup_pair()
        keys = [X25519PrivateKey.generate(), X25519PrivateKey.generate()]
        secret = secrets.token_bytes(32)
        profiles = profile_pair(topology, transport, candidate, keys)
        for name, profile in zip(topology.names, profiles):
            namespaces.containment(name, nft, profile, install=True)
        server_path = manifest(stage, transport + "-server", namespaces, profiles[1], keys[1], secret, images)
        server = NamespaceWorker(namespaces, topology.names[1], server_path); workers.append(server)
        client_path = manifest(stage, transport + "-client-good1", namespaces, profiles[0], keys[0], secret, images)
        client = NamespaceWorker(namespaces, topology.names[0], client_path); workers.append(client)
        await_ready(client); await_ready(server, role="responder")
        ready = server.command("start-probe")
        if ready.get("event") != "probe-ready":
            fail("SOCKET_PROBE_NOT_READY")
        ports = {key: ready[key] for key in PORT_FIELDS}
        row["phases"].append(dict(phase="correct-pin-before", profile_sha256=profiles[0].sha256,
            echo=echo(client, server, ports, True)))
        if not client.stop():
            fail("CLIENT_CLEANUP_FAILED")
        wrong = asdict(profiles[0]); wrong["instance_id"] = str(uuid.uuid4())
        wrong["remote"]["public_key"] = base64.b64encode(X25519PrivateKey.generate().public_key().public_bytes_raw()).decode()
        wrong = parse_profile(json.dumps(wrong).encode())
        wrong_path = manifest(stage, transport + "-client-wrong", namespaces, wrong, keys[0], secret, images)
        client = NamespaceWorker(namespaces, topology.names[0], wrong_path); workers.append(client)
        await_ready(client, authenticated=False)
        # Actual transfer, irrespective of the observation parser rejecting the
        # expected key, distinguishes an enforced pin from guard2's old behavior.
        # Let real connector retries run while repeatedly exercising traffic.
        # A single immediate pre-handshake timeout is not a negative control.
        wrong_deadline = time.monotonic() + 8
        wrong_rounds = []
        while True:
            wrong_rounds.append(echo(client, server, ports, False))
            if len(wrong_rounds) >= 2 and time.monotonic() >= wrong_deadline:
                break
            time.sleep(0.1)
        observation = client.command("observe")
        if observation.get("tun_owned") is not True or observation.get("peer_authenticated") is not False:
            fail("WRONG_PIN_INFRASTRUCTURE_OR_AUTH_INVALID")
        row["phases"].append(dict(phase="wrong-pin", profile_sha256=wrong.sha256, echo_rounds=wrong_rounds,
                                   minimum_observation_seconds=8, tun_owned=True, peer_authenticated=False))
        if not client.stop():
            fail("CLIENT_CLEANUP_FAILED")
        restored = asdict(profiles[0]); restored["instance_id"] = str(uuid.uuid4())
        restored = parse_profile(json.dumps(restored).encode())
        restored_path = manifest(stage, transport + "-client-good2", namespaces, restored, keys[0], secret, images)
        client = NamespaceWorker(namespaces, topology.names[0], restored_path); workers.append(client)
        await_ready(client); await_ready(server, role="responder")
        row["phases"].append(dict(phase="correct-pin-after", profile_sha256=restored.sha256,
            echo=echo(client, server, ports, True)))
        row["blocked_packets"] = {role: namespaces.containment(name, nft, profile)
            for role, name, profile in zip(("client", "server"), topology.names, profiles)}
        stopped = server.command("stop-core")
        if stopped != {"event": "core-stopped", "core_reaped": True, "tun_absent": True}:
            fail("OWNED_CORE_EXIT_NOT_CONFIRMED")
        result = client.command("echo", ports=ports)
        if (result.get("event") != "echo" or set(result.get("outcomes", {})) != set(PORT_FIELDS)
                or any(value not in ("timeout", "refused") for value in result["outcomes"].values())):
            fail("STOPPED_OVERLAY_STILL_TRANSFERS")
        row["core_exit"] = dict(core_reaped=True, tun_absent=True, new_traffic_denied=True,
                                 production_native_lease_exercised=False)
        row["status"] = "owned_overlay_probe_verified"
    except (ProbeError, IsolationError, LifecycleError, OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
        row["error"] = diagnostic(error)
        if isinstance(error, WorkerFailure):
            row["worker_failure"] = error.record
        if isinstance(error, ReadinessTimeout):
            row["readiness_timeout"] = error.record
        if isinstance(error, ContainmentMismatch):
            row["fixed_diagnostics"] = error.diagnostics
    except KeyboardInterrupt:
        row["error"] = "DEVELOPMENT_PROBE_CANCELLED"
    finally:
        for worker in reversed(workers):
            try:
                if not worker.stop():
                    row["cleanup_errors"].append("OWNED_WORKER_CLEANUP_FAILED")
            except BaseException:
                row["cleanup_errors"].append("OWNED_WORKER_CLEANUP_FAILED")
        row["namespaces_created"] = len(namespaces.owned)
        try:
            namespaces.cleanup()
        except (ProbeError, IsolationError, OSError):
            row["cleanup_errors"].append("OWNED_NAMESPACE_CLEANUP_FAILED")
        row["namespaces_removed"] = len(namespaces.deleted)
        row["cleanup_errors"].extend(namespaces.cleanup_errors)
        if row["cleanup_errors"]:
            row["status"] = "failed"
    return row


def checked_checkout():
    # sudo changes euid but not checkout ownership; scope trust to this
    # exact read-only repository invocation, never global git configuration.
    git = ["git", "-c", "safe.directory=" + str(ROOT), "-C", str(ROOT)]
    commit = bounded_command([*git, "rev-parse", "HEAD"]).decode().strip()
    if (commit != os.environ.get("GITHUB_SHA")
            or bounded_command([*git, "status", "--porcelain", "--untracked-files=no"])):
        fail("CHECKOUT_IDENTITY_MISMATCH")
    return commit


def run_containment():
    """Same kernel prerequisite and containment contract, no runtime startup."""
    receipt = dict(schema=CONTAINMENT_SCHEMA, status="failed", precondition_failures=preconditions(),
        transports=[], runtime_executed=False, runtime_binary_uploaded=False, production_admission=False,
        input_identity="synthetic_profile_only_no_executable", raw_runtime_output_retained=False)
    if receipt["precondition_failures"]:
        return receipt
    try:
        receipt["checkout_commit"] = checked_checkout()
        from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey
        # The profile parser requires an identity object, but this marked
        # synthetic identity is never loaded, executed, published or admitted.
        synthetic = RuntimeCandidateIdentity("0" * 40, "0" * 64, "0" * 64,
            "0" * 64, "0" * 64, "0" * 64, 64)
        for transport in TRANSPORTS:
            namespaces = ProbeNamespaces(shutil.which("ip"), ProbeTopology.fresh(), run=namespace_command)
            row = dict(transport=transport, status="failed", cleanup_errors=[], namespaces_created=0, namespaces_removed=0)
            try:
                namespaces.setup_pair()
                profiles = profile_pair(namespaces.topology, transport, synthetic,
                    [X25519PrivateKey.generate(), X25519PrivateKey.generate()])
                for name, profile in zip(namespaces.topology.names, profiles):
                    namespaces.containment(name, shutil.which("nft"), profile, install=True)
                row["status"] = "containment_pair_verified"
            except (ProbeError, IsolationError, LifecycleError, OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
                row["error"] = diagnostic(error)
                if isinstance(error, ContainmentMismatch):
                    row["fixed_diagnostics"] = error.diagnostics
            except KeyboardInterrupt:
                row["error"] = "DEVELOPMENT_PROBE_CANCELLED"
            finally:
                row["namespaces_created"] = len(namespaces.owned)
                try:
                    namespaces.cleanup()
                except (ProbeError, IsolationError, OSError):
                    row["cleanup_errors"].append("OWNED_NAMESPACE_CLEANUP_FAILED")
                row["namespaces_removed"] = len(namespaces.deleted)
                row["cleanup_errors"].extend(namespaces.cleanup_errors)
                if row["cleanup_errors"]:
                    row["status"] = "failed"
            receipt["transports"].append(row)
            if row["status"] != "containment_pair_verified":
                fail("CONTAINMENT_PREFLIGHT_INCOMPLETE")
        receipt["status"] = "containment_preflight_verified"
    except (ProbeError, IsolationError, LifecycleError, OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
        receipt["error"] = diagnostic(error)
    except KeyboardInterrupt:
        receipt["error"] = "DEVELOPMENT_PROBE_CANCELLED"
    return receipt


def run(args):
    receipt = dict(schema=SCHEMA, status="failed", precondition_failures=preconditions(), transports=[],
        runtime_binary_uploaded=False, raw_runtime_output_retained=False, game_executed=False,
        production_admission=False, native_owner_lease_verified=False,
        encryption_claim="source contracts and bounded runtime observations are separate; no packet-capture proof")
    if receipt["precondition_failures"]:
        return receipt
    stage, stage_identity = None, None
    try:
        receipt["checkout_commit"] = checked_checkout()
        lock = json_bytes((ROOT / "scripts/easytier-source.lock.json").read_bytes())
        build, candidate = load_build(args.build_receipt, lock)
        receipt["runtime_candidate"] = asdict(candidate)
        stage = Path(tempfile.mkdtemp(prefix="ff-easytier-owned-", dir="/tmp"))
        info = stage.lstat()
        stage_identity = (info.st_dev, info.st_ino)
        image_inputs = [("core", args.core, build["built_executables"]["easytier-core"]),
                        ("cli", args.cli, build["built_executables"]["easytier-cli"]),
                        ("probe", args.socket_probe, {"sha256": args.socket_probe_sha256, "bytes": args.socket_probe.stat().st_size})]
        images = {name: private_copy(path, stage / name, identity) for name, path, identity in image_inputs}
        receipt["cli_identity"] = build["built_executables"]["easytier-cli"]
        receipt["socket_probe_identity"] = {key: images["probe"][key] for key in ("sha256", "bytes")}
        for transport in TRANSPORTS:
            row = run_transport(transport, candidate, images, stage, shutil.which("ip"), shutil.which("nft"))
            receipt["transports"].append(row)
            if row["status"] != "owned_overlay_probe_verified":
                fail("TRANSPORT_MATRIX_INCOMPLETE")
        receipt["status"] = "isolated_development_matrix_verified"
    except (ProbeError, IsolationError, LifecycleError, OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
        receipt["error"] = diagnostic(error)
    except KeyboardInterrupt:
        receipt["error"] = "DEVELOPMENT_PROBE_CANCELLED"
    finally:
        if stage is not None:
            try:
                info = stage.lstat()
                if (not stat.S_ISDIR(info.st_mode) or (info.st_dev, info.st_ino) != stage_identity
                        or info.st_uid != os.geteuid() or info.st_mode & 0o077):
                    fail("PRIVATE_STAGE_IDENTITY_CHANGED")
                shutil.rmtree(stage)
                receipt["private_stage_removed"] = True
            except (OSError, ProbeError):
                receipt["private_stage_removed"] = False
                receipt["status"] = "failed"
                receipt["error"] = "PRIVATE_STAGE_CLEANUP_FAILED"
    return receipt


def write_receipt(path, receipt):
    # The unprivileged runner pre-creates its own artifact parent. Never chmod
    # shared ancestry; only this new sanitized public file gets readable mode.
    with path.open("xb") as stream:
        os.fchmod(stream.fileno(), 0o644)
        stream.write(encoded(receipt))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--containment-only", action="store_true")
    parser.add_argument("--build-receipt", type=Path)
    parser.add_argument("--core", type=Path)
    parser.add_argument("--cli", type=Path)
    parser.add_argument("--socket-probe", type=Path)
    parser.add_argument("--socket-probe-sha256")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    inputs = (args.build_receipt, args.core, args.cli, args.socket_probe, args.socket_probe_sha256)
    if args.containment_only and any(value is not None for value in inputs):
        parser.error("containment-only does not accept runtime build/image inputs")
    if not args.containment_only and any(value is None for value in inputs):
        parser.error("the full probe requires build-receipt, core, cli, socket-probe and socket-probe-sha256")
    def cancel(_signum, _frame):
        # First cancellation unwinds owned cleanup; repeated signals cannot
        # interrupt cleanup. SIGKILL cannot produce a cleanup receipt.
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, cancel)
    signal.signal(signal.SIGINT, cancel)
    receipt = run_containment() if args.containment_only else run(args)
    try:
        write_receipt(args.output, receipt)
    except OSError:
        sys.stderr.write("PUBLIC_RECEIPT_WRITE_FAILED\n")
        return 1
    verified = receipt["status"] in ("isolated_development_matrix_verified", "containment_preflight_verified")
    prefix = "EASYTIER_CONTAINMENT_" if args.containment_only else "EASYTIER_ISOLATED_DEVELOPMENT_"
    print(prefix + ("VERIFIED" if verified else "FAILED"))
    return 0 if verified else 1


if __name__ == "__main__":
    raise SystemExit(main())
