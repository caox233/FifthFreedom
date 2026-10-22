#!/usr/bin/env python3
from __future__ import annotations

import json
import hashlib
import io
import socket
import tempfile
import threading
import time
import urllib.error
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor
from http import HTTPStatus
from pathlib import Path

from scbl_update_server import (
    BUNDLE_ENDPOINT,
    STALE_GENERATION_GRACE_SECONDS,
    BundleRequestError,
    ComponentBundleCache,
    ScblUpdateServer,
    ScblUpdateServerV6,
    create_servers,
)


assert ScblUpdateServer.request_queue_size == 128
assert ScblUpdateServer.daemon_threads is True
assert ScblUpdateServer.block_on_close is False
assert ScblUpdateServerV6.address_family == socket.AF_INET6

with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    manifest = {"schemaVersion": 1, "server": {"mode": "stable"}}
    (root / "scbl-server.json").write_text(
        json.dumps(manifest, ensure_ascii=False), encoding="utf-8"
    )
    (root / "client_update_manifest.json").write_text("{}", encoding="utf-8")
    full_package = root / "SCBL.zip"
    full_package.write_bytes(b"full-package-bytes")

    server = create_servers(root, 0, False)[0]
    port = int(server.server_address[1])
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=3) as raw:
            raw.sendall(b"NOT HTTP\r\n\r\n")
            malformed_response = raw.recv(4096)
            assert b"400" in malformed_response

        with opener.open(
            f"http://127.0.0.1:{port}/scbl-server.json", timeout=3
        ) as response:
            assert response.status == 200
            assert response.headers.get_content_type() == "application/json"
            assert response.headers.get_content_charset() == "utf-8"
            assert response.headers.get("Cache-Control") == "no-store"
            assert response.headers.get("Accept-Ranges") == "bytes"
            etag = response.headers.get("ETag")
            assert etag
            assert json.loads(response.read().decode("utf-8")) == manifest

        unchanged = urllib.request.Request(
            f"http://127.0.0.1:{port}/scbl-server.json",
            headers={"If-None-Match": etag},
        )
        try:
            opener.open(unchanged, timeout=3)
        except urllib.error.HTTPError as error:
            assert error.code == 304
        else:
            raise AssertionError("matching ETag must return 304")

        raw_manifest = (root / "scbl-server.json").read_bytes()
        range_request = urllib.request.Request(
            f"http://127.0.0.1:{port}/scbl-server.json",
            headers={"Range": "bytes=5-"},
        )
        with opener.open(range_request, timeout=3) as response:
            assert response.status == 206
            assert response.headers.get("Accept-Ranges") == "bytes"
            assert response.headers.get("Content-Range") == (
                f"bytes 5-{len(raw_manifest) - 1}/{len(raw_manifest)}"
            )
            assert response.headers.get("ETag")
            assert response.read() == raw_manifest[5:]

        stale_if_range = urllib.request.Request(
            f"http://127.0.0.1:{port}/scbl-server.json",
            headers={"Range": "bytes=5-", "If-Range": '"stale"'},
        )
        with opener.open(stale_if_range, timeout=3) as response:
            assert response.status == 200
            assert response.read() == raw_manifest

        unsatisfied = urllib.request.Request(
            f"http://127.0.0.1:{port}/scbl-server.json",
            headers={"Range": f"bytes={len(raw_manifest)}-"},
        )
        try:
            opener.open(unsatisfied, timeout=3)
        except urllib.error.HTTPError as error:
            assert error.code == 416
            assert error.headers.get("Content-Range") == f"bytes */{len(raw_manifest)}"
        else:
            raise AssertionError("unsatisfied byte range must be rejected")

        for hidden in ("/", "/client_update_manifest.json", "/active_announcement.json"):
            try:
                opener.open(f"http://127.0.0.1:{port}{hidden}", timeout=3)
            except urllib.error.HTTPError as error:
                assert error.code == 404
            else:
                raise AssertionError(f"internal update path must be hidden: {hidden}")
        for traversal in (
            "/releases/../client_update_manifest.json",
            "/releases/%2e%2e/client_update_manifest.json",
            "/components/artifacts/../../client_update_manifest.json",
        ):
            try:
                opener.open(f"http://127.0.0.1:{port}{traversal}", timeout=3)
            except urllib.error.HTTPError as error:
                assert error.code == 404
            else:
                raise AssertionError(f"public path traversal must be rejected: {traversal}")

        with opener.open(f"http://127.0.0.1:{port}/SCBL.zip", timeout=3) as response:
            assert response.status == 200
            assert response.headers.get("Cache-Control") == "no-store"
            assert response.read() == full_package.read_bytes()
        package_range = urllib.request.Request(
            f"http://127.0.0.1:{port}/SCBL.zip",
            headers={"Range": "bytes=5-10"},
        )
        with opener.open(package_range, timeout=3) as response:
            assert response.status == 206
            assert response.headers.get("Content-Range") == (
                f"bytes 5-10/{full_package.stat().st_size}"
            )
            assert response.read() == full_package.read_bytes()[5:11]
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=3)

if socket.has_ipv6:
    v6_server = ScblUpdateServerV6(("::1", 0), lambda *args, **kwargs: None)
    try:
        assert v6_server.socket.getsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY) == 1
    finally:
        v6_server.server_close()

print("SCBL dual-stack update server checks passed")


def write_component_manifest(
    root: Path,
    channel: str,
    payloads: dict[str, tuple[str, bytes]],
) -> tuple[str, dict[str, object]]:
    components: dict[str, object] = {}
    version = "1.0.1" if channel == "test" else "1.0.0"
    for component, (filename, payload) in payloads.items():
        digest = hashlib.sha256(payload).hexdigest()
        artifact = root / "components" / "artifacts" / component / version / filename
        artifact.parent.mkdir(parents=True, exist_ok=True)
        artifact.write_bytes(payload)
        components[component] = {
            "version": version,
            "sha256": digest,
            "size": len(payload),
            "url": f"/components/artifacts/{component}/{version}/{filename}",
            "updateMode": "next-launch",
            "required": True,
        }
    manifest = {
        "schemaVersion": 2,
        "channel": channel,
        "generatedAt": "2026-08-08T00:00:00Z",
        "components": components,
        "files": {},
    }
    raw = (json.dumps(manifest, sort_keys=True) + "\n").encode()
    path = root / "components" / "channels" / channel / "client_components_v2.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(raw)
    return hashlib.sha256(raw).hexdigest(), manifest


def write_server_mode(root: Path, mode: str) -> None:
    (root / "scbl-server.json").write_text(
        json.dumps({"schemaVersion": 1, "server": {"mode": mode}}),
        encoding="utf-8",
    )


def request_document(
    manifest_sha: str,
    manifest: dict[str, object],
    names: list[str],
) -> dict[str, object]:
    available = manifest["components"]
    assert isinstance(available, dict)
    return {
        "schemaVersion": 1,
        "manifestSha256": manifest_sha,
        "components": [
            {"name": name, "sha256": available[name]["sha256"]}  # type: ignore[index]
            for name in names
        ],
    }


def post_bundle(
    opener: urllib.request.OpenerDirector,
    port: int,
    document: dict[str, object],
) -> tuple[int, bytes, object]:
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}{BUNDLE_ENDPOINT}",
        data=json.dumps(document).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with opener.open(request, timeout=5) as response:
            return response.status, response.read(), response.headers
    except urllib.error.HTTPError as error:
        return error.code, error.read(), error.headers


class CountingBundleCache(ComponentBundleCache):
    def __init__(self, *args: object, **kwargs: object) -> None:
        super().__init__(*args, **kwargs)  # type: ignore[arg-type]
        self.builds = 0
        self.count_lock = threading.Lock()

    def _write_bundle(self, *args: object, **kwargs: object) -> Path:
        with self.count_lock:
            self.builds += 1
        time.sleep(0.05)
        return super()._write_bundle(*args, **kwargs)  # type: ignore[arg-type]


with tempfile.TemporaryDirectory(prefix="scbl-update-bundles-") as temporary:
    scenario_root = Path(temporary)
    static_root = scenario_root / "updates"
    cache_root = scenario_root / "bundle-cache"
    stable_sha, stable_manifest = write_component_manifest(
        static_root,
        "stable",
        {
            "hooks": ("uplay_r1_loader.dll", b"hooks-v1"),
            "launcher": ("SCBL.exe", b"launcher-v1"),
        },
    )
    test_sha, test_manifest = write_component_manifest(
        static_root,
        "test",
        {"hooks": ("uplay_r1_loader.dll", b"test-hooks")},
    )
    write_server_mode(static_root, "stable")
    counting_cache = CountingBundleCache(static_root, cache_root)
    bundle_server = create_servers(static_root, 0, False, cache_root)[0]
    bundle_server.bundle_cache = counting_cache  # type: ignore[attr-defined]
    bundle_port = int(bundle_server.server_address[1])
    bundle_thread = threading.Thread(target=bundle_server.serve_forever, daemon=True)
    bundle_thread.start()
    no_proxy = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        hooks_request = request_document(stable_sha, stable_manifest, ["hooks"])
        status, payload, headers = post_bundle(no_proxy, bundle_port, hooks_request)
        assert status == 200
        assert headers.get("Content-Type") == "application/zip"
        assert headers.get("X-SCBL-Bundle-Cache") == "miss"
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            assert set(archive.namelist()) == {
                "bundle-manifest.json",
                "components/hooks/uplay_r1_loader.dll",
            }
            internal = json.loads(archive.read("bundle-manifest.json"))
            assert internal["manifestSha256"] == stable_sha
            expected_hooks_sha = stable_manifest["components"]["hooks"]["sha256"]  # type: ignore[index]
            assert internal["components"][0]["sha256"] == expected_hooks_sha

        status, _payload, headers = post_bundle(no_proxy, bundle_port, hooks_request)
        assert status == 200 and headers.get("X-SCBL-Bundle-Cache") == "hit"
        assert counting_cache.builds == 1

        pair_request = request_document(stable_sha, stable_manifest, ["launcher", "hooks"])
        with ThreadPoolExecutor(max_workers=8) as executor:
            concurrent = list(
                executor.map(
                    lambda _: post_bundle(no_proxy, bundle_port, pair_request)[0],
                    range(8),
                )
            )
        assert concurrent == [200] * 8
        assert counting_cache.builds == 2

        malicious = dict(hooks_request)
        malicious["components"] = [{"name": "../hooks", "sha256": "0" * 64}]
        assert post_bundle(no_proxy, bundle_port, malicious)[0] == 400
        with_path = dict(hooks_request)
        with_path["path"] = "/etc/passwd"
        assert post_bundle(no_proxy, bundle_port, with_path)[0] == 400
        empty = dict(hooks_request)
        empty["components"] = []
        assert post_bundle(no_proxy, bundle_port, empty)[0] == 400
        seven = dict(hooks_request)
        seven["components"] = [
            {"name": f"item-{index}", "sha256": "0" * 64} for index in range(7)
        ]
        assert post_bundle(no_proxy, bundle_port, seven)[0] == 400
        with_channel = dict(hooks_request)
        with_channel["channel"] = "test"
        assert post_bundle(no_proxy, bundle_port, with_channel)[0] == 400
        cross_channel = request_document(stable_sha, test_manifest, ["hooks"])
        assert post_bundle(no_proxy, bundle_port, cross_channel)[0] == 409

        with no_proxy.open(
            f"http://127.0.0.1:{bundle_port}/components/artifacts/hooks/1.0.0/uplay_r1_loader.dll",
            timeout=3,
        ) as response:
            assert response.read() == b"hooks-v1"

        old_sha = stable_sha
        stable_sha, stable_manifest = write_component_manifest(
            static_root,
            "stable",
            {
                "hooks": ("uplay_r1_loader.dll", b"hooks-v2"),
                "launcher": ("SCBL.exe", b"launcher-v2"),
            },
        )
        assert post_bundle(no_proxy, bundle_port, hooks_request)[0] == 409
        assert old_sha != stable_sha
    finally:
        bundle_server.shutdown()
        bundle_server.server_close()
        bundle_thread.join(timeout=3)

    # A source changed behind its manifest must never leave a final or half bundle.
    half_root = scenario_root / "half-cache"
    half_cache = ComponentBundleCache(static_root, half_root)
    half_request = request_document(stable_sha, stable_manifest, ["hooks"])
    hook_path = static_root / "components" / "artifacts" / "hooks" / "1.0.0" / "uplay_r1_loader.dll"
    hook_path.write_bytes(b"tampered")
    try:
        half_cache.get_or_create(half_request)
    except BundleRequestError as error:
        assert error.status == HTTPStatus.SERVICE_UNAVAILABLE
    else:
        raise AssertionError("tampered source must fail closed")
    assert not list(half_root.glob("*.zip"))
    assert not list(half_root.glob("*.tmp"))

    for invalid_url in (
        "../../outside/payload.bin",
        "https://untrusted.example/payload.bin",
    ):
        unsafe_root = scenario_root / ("unsafe-" + hashlib.sha256(invalid_url.encode()).hexdigest()[:8])
        unsafe_static = unsafe_root / "updates"
        unsafe_sha, unsafe_manifest = write_component_manifest(
            unsafe_static,
            "stable",
            {"hooks": ("uplay_r1_loader.dll", b"unsafe-source")},
        )
        unsafe_manifest["components"]["hooks"]["url"] = invalid_url  # type: ignore[index]
        unsafe_raw = (json.dumps(unsafe_manifest, sort_keys=True) + "\n").encode()
        unsafe_manifest_path = (
            unsafe_static / "components" / "channels" / "stable" / "client_components_v2.json"
        )
        unsafe_manifest_path.write_bytes(unsafe_raw)
        unsafe_sha = hashlib.sha256(unsafe_raw).hexdigest()
        write_server_mode(unsafe_static, "stable")
        unsafe_request = request_document(unsafe_sha, unsafe_manifest, ["hooks"])
        try:
            ComponentBundleCache(unsafe_static, unsafe_root / "cache").get_or_create(unsafe_request)
        except BundleRequestError as error:
            assert error.status == HTTPStatus.SERVICE_UNAVAILABLE
        else:
            raise AssertionError("manifest paths and arbitrary URLs must fail closed")

    hook_path.write_bytes(b"hooks-v2")
    limited_root = scenario_root / "limited-cache"
    limited = ComponentBundleCache(static_root, limited_root, maximum_combinations=1)
    limited_request = request_document(stable_sha, stable_manifest, ["hooks"])
    limited.get_or_create(limited_request)
    restarted = ComponentBundleCache(static_root, limited_root, maximum_combinations=1)
    _restarted_path, restarted_hit = restarted.get_or_create(limited_request)
    assert restarted_hit is True
    _restarted_path.write_bytes(b"corrupt-cache")
    repaired_path, repaired_hit = restarted.get_or_create(limited_request)
    assert repaired_hit is False
    assert repaired_path.read_bytes().startswith(b"PK")
    orphan = limited_root / (("0" * 64) + ".zip")
    orphan.write_bytes(b"partial")
    restarted.get_or_create(limited_request)
    assert not orphan.exists()
    try:
        limited.get_or_create(request_document(stable_sha, stable_manifest, ["launcher"]))
    except BundleRequestError as error:
        assert error.status == HTTPStatus.TOO_MANY_REQUESTS
    else:
        raise AssertionError("combination limit must reject a new bundle")

    capacity_root = scenario_root / "capacity-cache"
    capacity = ComponentBundleCache(static_root, capacity_root, maximum_bytes=1)
    try:
        capacity.get_or_create(request_document(stable_sha, stable_manifest, ["hooks"]))
    except BundleRequestError as error:
        assert error.status == HTTPStatus.TOO_MANY_REQUESTS
    else:
        raise AssertionError("capacity limit must reject an oversized cache entry")
    assert not list(capacity_root.glob("*.zip"))

    now = [1_000_000.0]
    generation_root = scenario_root / "generation-cache"
    generations = ComponentBundleCache(static_root, generation_root, clock=lambda: now[0])
    generation_one_sha = stable_sha
    generation_one_manifest = stable_manifest
    generations.get_or_create(
        request_document(generation_one_sha, generation_one_manifest, ["hooks"])
    )
    generation_two_sha, generation_two_manifest = write_component_manifest(
        static_root,
        "stable",
        {
            "hooks": ("uplay_r1_loader.dll", b"hooks-v3"),
            "launcher": ("SCBL.exe", b"launcher-v3"),
        },
    )
    current_request = request_document(generation_two_sha, generation_two_manifest, ["hooks"])
    generations.get_or_create(current_request)
    assert len(list(generation_root.glob("*.zip"))) == 2
    now[0] += STALE_GENERATION_GRACE_SECONDS - 1
    generations.get_or_create(current_request)
    assert len(list(generation_root.glob("*.zip"))) == 2
    now[0] += 2
    generations.get_or_create(current_request)
    assert len(list(generation_root.glob("*.zip"))) == 1

print("SCBL component bundle security/cache checks passed: 20")
