#!/usr/bin/env python3
"""SCBL client update file server with independent IPv4 and IPv6 listeners."""
from __future__ import annotations

import argparse
import functools
import hashlib
import json
import os
import re
import signal
import socket
import tempfile
import threading
import time
import zipfile
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable
from urllib.parse import urlsplit


BUNDLE_SCHEMA_VERSION = 1
BUNDLE_ENDPOINT = "/components/bundles/v1"
MAX_BUNDLE_REQUEST_BYTES = 16 * 1024
MAX_BUNDLE_COMPONENTS = 6
MAX_COMPONENT_BYTES = 512 * 1024 * 1024
MAX_BUNDLE_SOURCE_BYTES = 1024 * 1024 * 1024
DEFAULT_CACHE_BYTES = 8 * 1024 * 1024 * 1024
DEFAULT_CACHE_COMBINATIONS = 128
STALE_GENERATION_GRACE_SECONDS = 7 * 24 * 60 * 60
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
COMPONENT_PATTERN = re.compile(r"^[a-z][a-z0-9-]{0,31}$")
PUBLIC_DOCUMENT = "/scbl-server.json"
PUBLIC_CLIENT_PACKAGE = "/SCBL.zip"
PUBLIC_DOWNLOAD_PREFIXES = ("/releases/", "/components/artifacts/")


class BundleRequestError(RuntimeError):
    def __init__(self, status: HTTPStatus, message: str) -> None:
        super().__init__(message)
        self.status = status


class ComponentBundleCache:
    """Build immutable component bundles from the current signed-manifest payloads."""

    def __init__(
        self,
        root: Path,
        cache_root: Path,
        *,
        clock: Callable[[], float] = time.time,
        maximum_bytes: int = DEFAULT_CACHE_BYTES,
        maximum_combinations: int = DEFAULT_CACHE_COMBINATIONS,
    ) -> None:
        self.root = root.resolve()
        self.cache_root = cache_root.resolve()
        self.clock = clock
        self.maximum_bytes = maximum_bytes
        self.maximum_combinations = maximum_combinations
        self._lock = threading.Lock()

    def get_or_create(self, request: Any) -> tuple[Path, bool]:
        channel, manifest_sha, requested = self._validate_request(request)
        cache_key = self._cache_key(channel, manifest_sha, requested)
        target = self.cache_root / f"{cache_key}.zip"
        metadata = self.cache_root / f"{cache_key}.json"

        # A single bounded critical section prevents duplicate builds for the
        # same combination and keeps capacity accounting exact across combinations.
        with self._lock:
            self.cache_root.mkdir(parents=True, exist_ok=True)
            self._cleanup_orphans()
            manifest, sources = self._load_current_manifest(channel, manifest_sha, requested)
            self._refresh_generation(channel, manifest_sha)
            self._cleanup_expired_generations()
            if self._cache_pair_valid(
                target, metadata, channel, manifest_sha, requested
            ):
                return target, True
            target.unlink(missing_ok=True)
            metadata.unlink(missing_ok=True)
            count, used = self._cache_usage()
            if count >= self.maximum_combinations:
                raise BundleRequestError(HTTPStatus.TOO_MANY_REQUESTS, "bundle cache combination limit reached")

            temporary: Path | None = None
            try:
                temporary = self._write_bundle(channel, manifest_sha, manifest, sources)
                if self._current_manifest_sha(channel) != manifest_sha:
                    raise BundleRequestError(HTTPStatus.CONFLICT, "manifest generation changed")
                size = temporary.stat().st_size
                if size > self.maximum_bytes or used + size > self.maximum_bytes:
                    raise BundleRequestError(HTTPStatus.TOO_MANY_REQUESTS, "bundle cache capacity limit reached")
                os.replace(temporary, target)
                self._atomic_json(
                    metadata,
                    {
                        "schemaVersion": BUNDLE_SCHEMA_VERSION,
                        "channel": channel,
                        "manifestSha256": manifest_sha,
                        "archiveSize": size,
                        "archiveSha256": self._sha256(target),
                        "createdAt": self.clock(),
                        "components": [
                            {"name": name, "sha256": digest}
                            for name, digest in requested
                        ],
                    },
                )
                return target, False
            except BundleRequestError:
                target.unlink(missing_ok=True)
                metadata.unlink(missing_ok=True)
                raise
            except Exception as exc:
                target.unlink(missing_ok=True)
                metadata.unlink(missing_ok=True)
                print(
                    f"bundle generation failed: {type(exc).__name__}: {exc}",
                    flush=True,
                )
                raise BundleRequestError(HTTPStatus.SERVICE_UNAVAILABLE, "bundle generation failed") from exc
            finally:
                if temporary is not None:
                    temporary.unlink(missing_ok=True)

    def _cache_pair_valid(
        self,
        archive: Path,
        metadata: Path,
        channel: str,
        manifest_sha: str,
        requested: tuple[tuple[str, str], ...],
    ) -> bool:
        if not archive.is_file() or archive.is_symlink() or not metadata.is_file():
            return False
        try:
            raw = json.loads(metadata.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return False
        expected_components = [
            {"name": name, "sha256": digest} for name, digest in requested
        ]
        if (
            not isinstance(raw, dict)
            or raw.get("schemaVersion") != BUNDLE_SCHEMA_VERSION
            or raw.get("channel") != channel
            or raw.get("manifestSha256") != manifest_sha
            or raw.get("components") != expected_components
            or type(raw.get("archiveSize")) is not int
            or not isinstance(raw.get("archiveSha256"), str)
            or not SHA256_PATTERN.fullmatch(raw["archiveSha256"])
        ):
            return False
        try:
            return (
                archive.stat().st_size == raw["archiveSize"]
                and self._sha256(archive) == raw["archiveSha256"]
            )
        except OSError:
            return False

    def _current_manifest_sha(self, channel: str) -> str:
        path = self.root / "components" / "channels" / channel / "client_components_v2.json"
        try:
            return hashlib.sha256(path.read_bytes()).hexdigest()
        except OSError as exc:
            raise BundleRequestError(HTTPStatus.SERVICE_UNAVAILABLE, "component manifest unavailable") from exc

    def _validate_request(self, request: Any) -> tuple[str, str, tuple[tuple[str, str], ...]]:
        if not isinstance(request, dict) or set(request) != {
            "schemaVersion", "manifestSha256", "components"
        }:
            raise BundleRequestError(HTTPStatus.BAD_REQUEST, "invalid bundle request")
        if request.get("schemaVersion") != BUNDLE_SCHEMA_VERSION:
            raise BundleRequestError(HTTPStatus.BAD_REQUEST, "unsupported bundle schema")
        channel = self._active_channel()
        manifest_sha = request.get("manifestSha256")
        components = request.get("components")
        if not isinstance(manifest_sha, str) or not SHA256_PATTERN.fullmatch(manifest_sha):
            raise BundleRequestError(HTTPStatus.BAD_REQUEST, "invalid manifest digest")
        if not isinstance(components, list) or not 1 <= len(components) <= MAX_BUNDLE_COMPONENTS:
            raise BundleRequestError(HTTPStatus.BAD_REQUEST, "invalid component count")
        normalized: list[tuple[str, str]] = []
        names: set[str] = set()
        for component in components:
            if not isinstance(component, dict) or set(component) != {"name", "sha256"}:
                raise BundleRequestError(HTTPStatus.BAD_REQUEST, "invalid component request")
            name = component.get("name")
            digest = component.get("sha256")
            if (
                not isinstance(name, str)
                or not COMPONENT_PATTERN.fullmatch(name)
                or name in names
                or not isinstance(digest, str)
                or not SHA256_PATTERN.fullmatch(digest)
            ):
                raise BundleRequestError(HTTPStatus.BAD_REQUEST, "invalid component identity")
            names.add(name)
            normalized.append((name, digest))
        return channel, manifest_sha, tuple(sorted(normalized))

    def _active_channel(self) -> str:
        path = self.root / "scbl-server.json"
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise BundleRequestError(
                HTTPStatus.SERVICE_UNAVAILABLE, "server descriptor unavailable"
            ) from exc
        server = payload.get("server") if isinstance(payload, dict) else None
        channel = server.get("mode") if isinstance(server, dict) else None
        if payload.get("schemaVersion") != 1 or channel not in {"stable", "test"}:
            raise BundleRequestError(
                HTTPStatus.SERVICE_UNAVAILABLE, "server descriptor invalid"
            )
        return str(channel)

    def _load_current_manifest(
        self,
        channel: str,
        requested_sha: str,
        requested: tuple[tuple[str, str], ...],
    ) -> tuple[dict[str, Any], tuple[tuple[str, str, int, Path], ...]]:
        manifest_path = self.root / "components" / "channels" / channel / "client_components_v2.json"
        try:
            manifest_bytes = manifest_path.read_bytes()
            manifest = json.loads(manifest_bytes)
        except (OSError, json.JSONDecodeError) as exc:
            raise BundleRequestError(HTTPStatus.SERVICE_UNAVAILABLE, "component manifest unavailable") from exc
        actual_sha = hashlib.sha256(manifest_bytes).hexdigest()
        if actual_sha != requested_sha:
            raise BundleRequestError(HTTPStatus.CONFLICT, "manifest generation changed")
        if (
            not isinstance(manifest, dict)
            or manifest.get("schemaVersion") != 2
            or manifest.get("channel") != channel
            or not isinstance(manifest.get("components"), dict)
            or not isinstance(manifest.get("files", {}), dict)
        ):
            raise BundleRequestError(HTTPStatus.SERVICE_UNAVAILABLE, "component manifest invalid")
        available = {**manifest["components"], **manifest.get("files", {})}
        sources: list[tuple[str, str, int, Path]] = []
        total = 0
        for name, digest in requested:
            definition = available.get(name)
            if not isinstance(definition, dict) or definition.get("sha256") != digest:
                raise BundleRequestError(HTTPStatus.CONFLICT, "component is not in current manifest")
            size = definition.get("size")
            url = definition.get("url")
            if type(size) is not int or not 0 < size <= MAX_COMPONENT_BYTES or not isinstance(url, str):
                raise BundleRequestError(HTTPStatus.SERVICE_UNAVAILABLE, "component definition invalid")
            source = self._resolve_manifest_source(url)
            total += size
            if total > MAX_BUNDLE_SOURCE_BYTES:
                raise BundleRequestError(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "bundle source size limit exceeded")
            sources.append((name, digest, size, source))
        return manifest, tuple(sources)

    def _resolve_manifest_source(self, value: str) -> Path:
        parsed = urlsplit(value)
        if (
            parsed.scheme
            or parsed.netloc
            or parsed.query
            or parsed.fragment
            or not parsed.path.startswith("/components/artifacts/")
            or "\\" in parsed.path
            or "%" in parsed.path
            or "\x00" in parsed.path
        ):
            raise BundleRequestError(HTTPStatus.SERVICE_UNAVAILABLE, "component source path invalid")
        candidate = self.root / parsed.path.lstrip("/")
        source = candidate.resolve()
        artifacts = (self.root / "components" / "artifacts").resolve()
        if (
            not source.is_relative_to(artifacts)
            or candidate.is_symlink()
            or not source.is_file()
        ):
            raise BundleRequestError(HTTPStatus.SERVICE_UNAVAILABLE, "component source unavailable")
        return source

    def _write_bundle(
        self,
        channel: str,
        manifest_sha: str,
        _manifest: dict[str, Any],
        sources: tuple[tuple[str, str, int, Path], ...],
    ) -> Path:
        descriptor, name = tempfile.mkstemp(prefix=".bundle-", suffix=".tmp", dir=self.cache_root)
        os.close(descriptor)
        temporary = Path(name)
        entries = [
            {
                "name": component,
                "sha256": digest,
                "size": size,
                "path": f"components/{component}/{source.name}",
            }
            for component, digest, size, source in sources
        ]
        try:
            with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
                archive.writestr(
                    "bundle-manifest.json",
                    json.dumps(
                        {
                            "schemaVersion": BUNDLE_SCHEMA_VERSION,
                            "channel": channel,
                            "manifestSha256": manifest_sha,
                            "components": entries,
                        },
                        ensure_ascii=False,
                        separators=(",", ":"),
                    ).encode("utf-8"),
                )
                for entry, (_, digest, size, source) in zip(entries, sources, strict=True):
                    if source.stat().st_size != size or self._sha256(source) != digest:
                        raise BundleRequestError(
                            HTTPStatus.SERVICE_UNAVAILABLE,
                            "component source verification failed",
                        )
                    archive.write(source, entry["path"])
            with temporary.open("r+b") as stream:
                os.fsync(stream.fileno())
            return temporary
        except Exception:
            temporary.unlink(missing_ok=True)
            raise

    def _refresh_generation(self, channel: str, manifest_sha: str) -> None:
        state_path = self.cache_root / "generations.json"
        state: dict[str, Any] = {"schemaVersion": 1, "channels": {}, "staleSince": {}}
        if state_path.is_file():
            try:
                loaded = json.loads(state_path.read_text(encoding="utf-8"))
                if isinstance(loaded, dict) and loaded.get("schemaVersion") == 1:
                    state = loaded
            except (OSError, json.JSONDecodeError):
                pass
        channels = state.setdefault("channels", {})
        stale_since = state.setdefault("staleSince", {})
        previous = channels.get(channel)
        if isinstance(previous, str) and previous != manifest_sha:
            stale_since.setdefault(previous, self.clock())
        channels[channel] = manifest_sha
        stale_since.pop(manifest_sha, None)
        self._atomic_json(state_path, state)

    def _cleanup_expired_generations(self) -> None:
        state_path = self.cache_root / "generations.json"
        try:
            state = json.loads(state_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return
        stale_since = state.get("staleSince", {})
        if not isinstance(stale_since, dict):
            return
        expired = {
            digest for digest, since in stale_since.items()
            if isinstance(digest, str)
            and isinstance(since, (int, float))
            and self.clock() - since >= STALE_GENERATION_GRACE_SECONDS
        }
        if not expired:
            return
        for metadata in self.cache_root.glob("*.json"):
            if metadata.name == "generations.json":
                continue
            try:
                value = json.loads(metadata.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                continue
            if value.get("manifestSha256") in expired:
                (self.cache_root / f"{metadata.stem}.zip").unlink(missing_ok=True)
                metadata.unlink(missing_ok=True)
        for digest in expired:
            stale_since.pop(digest, None)
        self._atomic_json(state_path, state)

    def _cleanup_orphans(self) -> None:
        for temporary in self.cache_root.glob(".*.tmp"):
            temporary.unlink(missing_ok=True)
        for archive in self.cache_root.glob("*.zip"):
            if SHA256_PATTERN.fullmatch(archive.stem) and not archive.with_suffix(".json").is_file():
                archive.unlink(missing_ok=True)
        for metadata in self.cache_root.glob("*.json"):
            if (
                metadata.name != "generations.json"
                and SHA256_PATTERN.fullmatch(metadata.stem)
                and not metadata.with_suffix(".zip").is_file()
            ):
                metadata.unlink(missing_ok=True)

    def _cache_usage(self) -> tuple[int, int]:
        archives = tuple(self.cache_root.glob("*.zip"))
        return len(archives), sum(path.stat().st_size for path in archives)

    @staticmethod
    def _cache_key(channel: str, manifest_sha: str, requested: tuple[tuple[str, str], ...]) -> str:
        canonical = json.dumps(
            {
                "bundleSchema": BUNDLE_SCHEMA_VERSION,
                "channel": channel,
                "manifestSha256": manifest_sha,
                "components": requested,
            },
            separators=(",", ":"),
            sort_keys=True,
        ).encode("utf-8")
        return hashlib.sha256(canonical).hexdigest()

    @staticmethod
    def _sha256(path: Path) -> str:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        return digest.hexdigest()

    @staticmethod
    def _atomic_json(path: Path, value: Any) -> None:
        descriptor, name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
        temporary = Path(name)
        try:
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                json.dump(value, stream, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)


class ScblUpdateServer(ThreadingHTTPServer):
    request_queue_size = 128
    daemon_threads = True
    block_on_close = False
    allow_reuse_address = True


class ScblUpdateServerV6(ScblUpdateServer):
    address_family = socket.AF_INET6

    def server_bind(self) -> None:
        # Keep IPv4 and IPv6 as independent listeners on the same TCP port.
        self.socket.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
        super().server_bind()


class UpdateRequestHandler(SimpleHTTPRequestHandler):
    server_version = "SCBLUpdate/1.0"
    protocol_version = "HTTP/1.1"
    _range_bytes_remaining: int | None = None
    _static_etag: str | None = None

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(20.0)

    def list_directory(self, path: str):  # type: ignore[override]
        self.send_error(HTTPStatus.FORBIDDEN, "Directory listing is disabled")
        return None

    def do_POST(self) -> None:  # noqa: N802
        if self.path.split("?", 1)[0] != BUNDLE_ENDPOINT or "?" in self.path:
            self.send_error(HTTPStatus.NOT_FOUND, "Unknown bundle endpoint")
            return
        content_type = self.headers.get_content_type()
        raw_length = self.headers.get("Content-Length", "")
        if content_type != "application/json" or not raw_length.isdigit():
            self.send_error(HTTPStatus.BAD_REQUEST, "JSON Content-Length is required")
            return
        length = int(raw_length)
        if not 1 <= length <= MAX_BUNDLE_REQUEST_BYTES:
            self.send_error(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "Bundle request is too large")
            return
        try:
            request = json.loads(self.rfile.read(length))
            cache: ComponentBundleCache = getattr(self.server, "bundle_cache")
            bundle, cache_hit = cache.get_or_create(request)
        except json.JSONDecodeError:
            self.send_error(HTTPStatus.BAD_REQUEST, "Invalid JSON")
            return
        except BundleRequestError as exc:
            self.send_error(exc.status, str(exc))
            return
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "application/zip")
        self.send_header("Content-Length", str(bundle.stat().st_size))
        self.send_header("X-SCBL-Bundle-Cache", "hit" if cache_hit else "miss")
        self.end_headers()
        with bundle.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                self.wfile.write(block)

    def send_head(self):  # type: ignore[override]
        """Serve a single immutable byte range for resumable component downloads."""
        self._range_bytes_remaining = None
        self._static_etag = None
        request = urlsplit(self.path)
        if self.command in {"GET", "HEAD"} and not _valid_public_request_path(request):
            self.send_error(HTTPStatus.NOT_FOUND, "Unknown public update path")
            return None
        path = self.translate_path(self.path)
        if self.command in {"GET", "HEAD"} and not self._translated_path_is_public(
            request.path, path
        ):
            self.send_error(HTTPStatus.NOT_FOUND, "Unknown public update path")
            return None
        if self.command not in {"GET", "HEAD"} or os.path.isdir(path):
            return super().send_head()
        try:
            stream = open(path, "rb")
        except OSError:
            self.send_error(HTTPStatus.NOT_FOUND, "File not found")
            return None
        try:
            stat = os.fstat(stream.fileno())
            size = stat.st_size
            etag = f'"{size:x}-{stat.st_mtime_ns:x}"'
            self._static_etag = etag
            if self.headers.get("If-None-Match", "").strip() == etag:
                stream.close()
                self.send_response(HTTPStatus.NOT_MODIFIED)
                self.end_headers()
                return None
            range_header = self.headers.get("Range")
            if not range_header:
                self.send_response(HTTPStatus.OK)
                self.send_header("Content-Type", self.guess_type(path))
                self.send_header("Content-Length", str(size))
                self.send_header("Last-Modified", self.date_time_string(stat.st_mtime))
                self.end_headers()
                return stream
            if_range = self.headers.get("If-Range")
            if if_range and if_range.strip() != etag:
                self.send_response(HTTPStatus.OK)
                self.send_header("Content-Type", self.guess_type(path))
                self.send_header("Content-Length", str(size))
                self.send_header("Last-Modified", self.date_time_string(stat.st_mtime))
                self.end_headers()
                return stream

            match = re.fullmatch(r"bytes=(\d*)-(\d*)", range_header.strip())
            if match is None or (not match.group(1) and not match.group(2)):
                raise ValueError("invalid range")

            if match.group(1):
                start = int(match.group(1))
                end = int(match.group(2)) if match.group(2) else size - 1
            else:
                suffix = int(match.group(2))
                if suffix <= 0:
                    raise ValueError("invalid suffix")
                start = max(0, size - suffix)
                end = size - 1

            if start < 0 or start >= size or end < start:
                raise ValueError("unsatisfiable range")
            end = min(end, size - 1)
            length = end - start + 1
            stream.seek(start)
            self.send_response(HTTPStatus.PARTIAL_CONTENT)
            self.send_header("Content-Type", self.guess_type(path))
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.send_header("Content-Length", str(length))
            self.send_header("Last-Modified", self.date_time_string(stat.st_mtime))
            self.end_headers()
            self._range_bytes_remaining = length
            return stream
        except ValueError:
            stream.close()
            self.send_response(HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
            self.send_header("Content-Range", f"bytes */{size}")
            self.send_header("Content-Length", "0")
            self.end_headers()
            return None

    def _translated_path_is_public(self, request_path: str, translated: str) -> bool:
        root = Path(self.directory).resolve()
        target = Path(translated).resolve()
        if request_path == PUBLIC_DOCUMENT:
            return target == root / PUBLIC_DOCUMENT.lstrip("/")
        if request_path == PUBLIC_CLIENT_PACKAGE:
            return target == root / PUBLIC_CLIENT_PACKAGE.lstrip("/")
        for prefix in PUBLIC_DOWNLOAD_PREFIXES:
            if request_path.startswith(prefix):
                return target.is_relative_to(root / prefix.strip("/"))
        return False

    def copyfile(self, source, outputfile) -> None:  # type: ignore[override]
        remaining = self._range_bytes_remaining
        if remaining is None:
            super().copyfile(source, outputfile)
            return
        while remaining > 0:
            block = source.read(min(1024 * 1024, remaining))
            if not block:
                break
            outputfile.write(block)
            remaining -= len(block)
        self._range_bytes_remaining = None

    def end_headers(self) -> None:
        # parse_request can reject malformed or TLS-on-HTTP traffic before it
        # creates self.path. Error responses must still be safe to send.
        request_path = getattr(self, "path", "")
        if self.command == "POST" or request_path.split("?", 1)[0] in {
            PUBLIC_DOCUMENT,
            PUBLIC_CLIENT_PACKAGE,
        }:
            self.send_header("Cache-Control", "no-store")
        else:
            self.send_header("Cache-Control", "public, max-age=300")
        if self.command in {"GET", "HEAD"}:
            self.send_header("Accept-Ranges", "bytes")
        if self._static_etag:
            self.send_header("ETag", self._static_etag)
        self.send_header("X-Content-Type-Options", "nosniff")
        super().end_headers()
        self._static_etag = None

    def guess_type(self, path: str) -> str:
        if path.lower().endswith(".json"):
            return "application/json; charset=utf-8"
        return super().guess_type(path)

    def log_message(self, format: str, *args: object) -> None:
        address = self.client_address[0] if self.client_address else "unknown"
        print(f'{address} - {format % args}', flush=True)


def create_servers(
    root: Path,
    port: int,
    enable_ipv6: bool,
    cache_root: Path | None = None,
) -> list[ScblUpdateServer]:
    handler = functools.partial(UpdateRequestHandler, directory=str(root))
    bundle_cache = ComponentBundleCache(root, cache_root or root.parent / "client-bundle-cache")
    servers: list[ScblUpdateServer] = []
    try:
        servers.append(ScblUpdateServer(("0.0.0.0", port), handler))
        servers[-1].bundle_cache = bundle_cache  # type: ignore[attr-defined]
        if enable_ipv6:
            servers.append(ScblUpdateServerV6(("::", port), handler))
            servers[-1].bundle_cache = bundle_cache  # type: ignore[attr-defined]
        return servers
    except Exception:
        for server in servers:
            server.server_close()
        raise


def parse_enabled(value: str) -> bool:
    return value.strip().lower() in {"1", "y", "yes", "true", "on"}


def _valid_public_request_path(request: Any) -> bool:
    path = request.path
    if (
        request.query
        or not isinstance(path, str)
        or not path.startswith("/")
        or any(marker in path for marker in ("%", "\\", "\x00", "//"))
    ):
        return False
    parts = path.split("/")[1:]
    if any(part in {"", ".", ".."} for part in parts):
        return False
    return path in {PUBLIC_DOCUMENT, PUBLIC_CLIENT_PACKAGE} or path.startswith(
        PUBLIC_DOWNLOAD_PREFIXES
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Serve SCBL client updates over IPv4 and IPv6")
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--port", required=True, type=int)
    parser.add_argument("--ipv6", default="y")
    parser.add_argument("--bundle-cache", type=Path)
    args = parser.parse_args()

    root = args.root.resolve()
    if not root.is_dir():
        raise SystemExit(f"Update directory does not exist: {root}")
    if not 1 <= args.port <= 65535:
        raise SystemExit(f"Invalid TCP port: {args.port}")

    ipv6_enabled = parse_enabled(args.ipv6)
    cache_root = (args.bundle_cache or root.parent / "client-bundle-cache").resolve()
    if cache_root == root or cache_root.is_relative_to(root):
        raise SystemExit("Bundle cache must be outside the static update root")
    cache_root.mkdir(parents=True, exist_ok=True)
    servers = create_servers(root, args.port, ipv6_enabled, cache_root)
    stop_event = threading.Event()

    def stop(_signum: int, _frame: object) -> None:
        stop_event.set()

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    threads: list[threading.Thread] = []
    for server in servers:
        family = "IPv6" if server.address_family == socket.AF_INET6 else "IPv4"
        print(f"SCBL update server listening on {family} TCP {args.port}; root={root}", flush=True)
        thread = threading.Thread(
            target=server.serve_forever,
            kwargs={"poll_interval": 0.25},
            name=f"scbl-update-{family.lower()}",
            daemon=True,
        )
        thread.start()
        threads.append(thread)

    stop_event.wait()
    for server in servers:
        server.shutdown()
        server.server_close()
    for thread in threads:
        thread.join(timeout=3.0)


if __name__ == "__main__":
    main()
