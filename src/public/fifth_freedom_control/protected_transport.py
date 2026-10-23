"""Bounded HTTPS-only, read-only session endpoint. No proxy identity headers.

This intentionally implements one HTTP/1.1 request per TLS connection. There is
no general HTTP router, static serving, chunking, upgrade, login or token issuer.
The native Public game-service profile remains a separate guarded integration.
"""
import asyncio
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict
import ipaddress
import json
import re
import socket
import ssl
import time

from .authenticated_bridge import AuthenticatedSessionBridge
from .secure_config import parse_json
from .session_reader import AdmissionError

MAX_CONNECTIONS = 64
MAX_QUERIES = 8
MAX_HEADER_BYTES = 16384
MAX_BODY_BYTES = 16384
MAX_RESPONSE_BYTES = 512 * 1024
REQUEST_TIMEOUT = 10.0
HANDSHAKE_TIMEOUT = 3.0
MAX_RATE_SOURCES = 4096


class _RequestError(Exception):
    def __init__(self, status, code):
        self.status, self.code = status, code


class RateLimiter:
    """Bounded, connection-source token buckets plus an aggregate allowance."""
    def __init__(self, *, clock=time.monotonic):
        self.clock = clock
        self.sources = {}
        self.global_tokens = 128.0
        self.last = clock()

    def admit(self, source):
        now = self.clock()
        self.global_tokens = min(128.0, self.global_tokens + max(0, now - self.last) * 32)
        self.last = now
        if self.global_tokens < 1:
            return False
        if source not in self.sources:
            if len(self.sources) >= MAX_RATE_SOURCES:
                self.sources = {key: value for key, value in self.sources.items() if now - value[1] < 60}
                if len(self.sources) >= MAX_RATE_SOURCES:
                    return False
            self.sources[source] = (10.0, now)
        tokens, previous = self.sources[source]
        tokens = min(10.0, tokens + max(0, now - previous) * 2)
        if tokens < 1:
            self.sources[source] = (tokens, now)
            return False
        self.sources[source] = (tokens - 1, now)
        self.global_tokens -= 1
        return True


async def _read_request(reader):
    total = 0
    async def line():
        nonlocal total
        try:
            value = await reader.readline()
        except ValueError:
            raise _RequestError(431, "HEADERS_TOO_LARGE") from None
        total += len(value)
        if total > MAX_HEADER_BYTES or len(value) > 8192:
            raise _RequestError(431, "HEADERS_TOO_LARGE")
        if not value.endswith(b"\r\n"):
            raise _RequestError(400, "REQUEST_INVALID")
        return value[:-2]
    if await line() != b"POST /v1/sessions HTTP/1.1":
        raise _RequestError(400, "REQUEST_INVALID")
    headers = {}
    for _ in range(33):
        value = await line()
        if not value:
            break
        if len(headers) >= 32 or b":" not in value:
            raise _RequestError(400, "REQUEST_INVALID")
        name, data = value.split(b":", 1)
        if re.fullmatch(rb"[A-Za-z0-9!#$%&'*+.^_`|~-]+", name) is None or any(c < 32 and c != 9 or c >= 127 for c in data):
            raise _RequestError(400, "REQUEST_INVALID")
        name, data = name.lower(), data.strip(b" \t")
        if name in headers:
            raise _RequestError(400, "REQUEST_INVALID")
        headers[name] = data
    else:
        raise _RequestError(431, "HEADERS_TOO_LARGE")
    if (not headers.get(b"host") or b"transfer-encoding" in headers or b"expect" in headers
            or headers.get(b"content-type") != b"application/json"):
        raise _RequestError(400, "REQUEST_INVALID")
    length = headers.get(b"content-length", b"")
    if re.fullmatch(rb"0|[1-9][0-9]{0,7}", length) is None:
        raise _RequestError(400, "REQUEST_INVALID")
    if not 1 <= int(length) <= MAX_BODY_BYTES:
        raise _RequestError(413, "BODY_TOO_LARGE")
    authorization = headers.get(b"authorization", b"")
    if not authorization.startswith(b"Bearer ") or len(authorization) > 4103:
        raise _RequestError(401, "AUTHENTICATION_REQUIRED")
    try:
        body = parse_json(await reader.readexactly(int(length)))
    except (AdmissionError, asyncio.IncompleteReadError):
        raise _RequestError(400, "REQUEST_INVALID") from None
    if not isinstance(body, dict) or set(body) != {"protocol_version", "package", "observer_requested"}:
        raise _RequestError(400, "REQUEST_INVALID")
    return authorization[7:].decode("ascii"), body


async def _write_response(writer, status, payload):
    body = json.dumps(payload, ensure_ascii=True, separators=(",", ":")).encode("ascii")
    if len(body) > MAX_RESPONSE_BYTES:
        status, body = 503, b'{"error":"SNAPSHOT_TOO_LARGE"}'
    reasons = {200: "OK", 400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 408: "Request Timeout",
               413: "Content Too Large", 429: "Too Many Requests", 431: "Request Header Fields Too Large", 503: "Service Unavailable"}
    writer.write(f"HTTP/1.1 {status} {reasons[status]}\r\nContent-Type: application/json\r\nContent-Length: {len(body)}\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n".encode("ascii") + body)
    await writer.drain()


class ProtectedSessionServer:
    def __init__(self, bridge, tls_context, listen_ip, listen_port):
        if (not isinstance(bridge, AuthenticatedSessionBridge) or not isinstance(tls_context, ssl.SSLContext)
                or tls_context.protocol != ssl.PROTOCOL_TLS_SERVER or tls_context.minimum_version < ssl.TLSVersion.TLSv1_2):
            raise AdmissionError("TLS_CONFIG_INVALID")
        try:
            address = ipaddress.IPv4Address(listen_ip)
            if str(address) != listen_ip or type(listen_port) is not int or not 0 <= listen_port <= 65535:
                raise ValueError()
        except (ValueError, TypeError):
            raise AdmissionError("PUBLIC_CONFIG_INVALID") from None
        self.bridge, self.context = bridge, tls_context
        self.address = (listen_ip, listen_port)
        self.listener = None
        self.accept_task = None
        self.connections = set()
        self.queries = set()
        self.executor = ThreadPoolExecutor(max_workers=MAX_QUERIES, thread_name_prefix="ff-query")
        self.limiter = RateLimiter()
        self.closed = False

    async def start(self):
        if self.closed or self.listener is not None:
            raise AdmissionError("SERVICE_LIFECYCLE_INVALID")
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.setblocking(False)
            listener.bind(self.address)
            listener.listen(MAX_CONNECTIONS)
        except OSError:
            listener.close()
            raise AdmissionError("LISTENER_UNAVAILABLE") from None
        self.listener = listener
        self.address = listener.getsockname()
        self.accept_task = asyncio.create_task(self._accept())
        return self.address

    async def _accept(self):
        loop = asyncio.get_running_loop()
        while not self.closed:
            client, peer = await loop.sock_accept(self.listener)
            source = peer[0]  # Always the kernel-observed peer, never HTTP headers.
            if len(self.connections) >= MAX_CONNECTIONS or not self.limiter.admit(source):
                client.close()
                continue
            task = asyncio.create_task(self._connection(client, source))
            self.connections.add(task)
            task.add_done_callback(self.connections.discard)

    async def _connection(self, client, source):
        writer = None
        try:
            async with asyncio.timeout(REQUEST_TIMEOUT):
                loop = asyncio.get_running_loop()
                reader = asyncio.StreamReader(limit=MAX_HEADER_BYTES)
                protocol = asyncio.StreamReaderProtocol(reader)
                transport, _ = await loop.connect_accepted_socket(lambda: protocol, client, ssl=self.context,
                    ssl_handshake_timeout=HANDSHAKE_TIMEOUT, ssl_shutdown_timeout=1.0)
                writer = asyncio.StreamWriter(transport, protocol, reader, loop)
                try:
                    token, body = await _read_request(reader)
                    if len(self.queries) >= MAX_QUERIES:
                        raise _RequestError(503, "SERVICE_BUSY")
                    def query():
                        return self.bridge.read(token, source, body["package"], protocol_version=body["protocol_version"],
                                                observer_requested=body["observer_requested"])
                    future = loop.run_in_executor(self.executor, query)
                    self.queries.add(future)
                    # A timed-out client must not release a still-running DB
                    # worker permit, otherwise cancellation could grow the queue.
                    def completed(value):
                        self.queries.discard(value)
                        # Retrieve late errors after request cancellation, without
                        # letting asyncio print an exception containing internals.
                        if not value.cancelled():
                            value.exception()
                    future.add_done_callback(completed)
                    try:
                        sessions = await asyncio.shield(future)
                    except AdmissionError:
                        raise _RequestError(403, "ADMISSION_DENIED") from None
                    await _write_response(writer, 200, {"schema": 1, "sessions": [asdict(value) for value in sessions]})
                except _RequestError as error:
                    await _write_response(writer, error.status, {"error": error.code})
                except Exception:
                    await _write_response(writer, 503, {"error": "SERVICE_UNAVAILABLE"})
        except (TimeoutError, OSError, ssl.SSLError, ConnectionError):
            pass  # Fixed behavior: close. Never echo/log credentials or parsing values.
        finally:
            if writer is not None:
                writer.close()
                try:
                    await asyncio.wait_for(writer.wait_closed(), timeout=1.5)
                except (Exception, asyncio.CancelledError):
                    pass
            client.close()

    async def wait(self):
        if self.accept_task is None:
            raise AdmissionError("SERVICE_LIFECYCLE_INVALID")
        await self.accept_task

    async def close(self):
        if self.closed:
            return
        self.closed = True
        if self.accept_task is not None:
            self.accept_task.cancel()
        if self.listener is not None:
            self.listener.close()
        for task in tuple(self.connections):
            task.cancel()
        await asyncio.gather(*(tuple(self.connections) + ((self.accept_task,) if self.accept_task else ())), return_exceptions=True)
        if self.queries:
            await asyncio.gather(*tuple(self.queries), return_exceptions=True)
        self.executor.shutdown(wait=True, cancel_futures=True)
