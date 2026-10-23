"""Finite composition controls, real local sockets; no namespace/runtime/game."""
import asyncio
from dataclasses import asdict
import json
import hashlib
import shutil
import signal
import subprocess
import os
from pathlib import Path
import socket
import sys
import tempfile
import time
import unittest
from unittest.mock import AsyncMock, Mock, patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import ci_owned_stack as stack
import owned_stack_worker as worker
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey


def candidate():
    return stack.probe.RuntimeCandidateIdentity("0" * 40, "1" * 64, "2" * 64,
        "3" * 64, "4" * 64, "5" * 64, 1000)


def pair():
    keys = [X25519PrivateKey.generate(), X25519PrivateKey.generate()]
    profiles = stack.probe.profile_pair(stack.probe.ProbeTopology("a" * 16, 1), "tcp", candidate(), keys)
    return keys, profiles


class InputTests(unittest.TestCase):
    def test_build_must_match_all_native_run_coordinates(self):
        expected = dict(checkout_commit="1" * 40, run_id="123", run_attempt="2")
        stack.same_run(expected, "1" * 40, "123", "2")
        for field, value in (("checkout_commit", "2" * 40), ("run_id", "124"),
                             ("run_attempt", "1"), ("run_id", 123)):
            changed = dict(expected, **{field: value})
            with self.assertRaisesRegex(stack.probe.ProbeError, "STACK_RUNTIME_RUN_MISMATCH"):
                stack.same_run(changed, "1" * 40, "123", "2")

    def test_missing_privilege_is_failure_before_any_package_or_runtime_load(self):
        with patch.object(stack.probe, "preconditions", return_value=["ROOT_REQUIRED"]), \
             patch.object(stack.probe, "load_build") as load, patch.object(stack, "run_case") as run:
            result = stack.run(None)
        self.assertEqual(result["status"], "failed")
        self.assertFalse(result["scope"]["production_admission"])
        self.assertFalse(result["copied_package_native_verified"])
        load.assert_not_called(); run.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "POSIX private file modes")
    def test_manifest_uses_actual_recipe_digest_and_private_inputs_outside_package(self):
        keys, profiles = pair()
        with tempfile.TemporaryDirectory() as directory:
            stage = Path(directory)
            namespace = os.open(directory, os.O_RDONLY)
            self.addCleanup(os.close, namespace)
            class Namespaces:
                topology = stack.probe.ProbeTopology("a" * 16, 1)
                owned = dict.fromkeys(topology.names, namespace)
            images = {name: dict(path=str(stage / name), sha256="5" * 64, bytes=1000)
                      for name in ("core", "cli", "native")}
            path = stack.make_manifest(stage, "synthetic-only", Namespaces(), profiles[1], keys[1],
                b"x" * 32, images, "peer-loss", dict(service={}, admitted_client={}))
            manifest = json.loads(path.read_bytes())
            config = json.loads(Path(manifest["native_config"]).read_bytes())
            self.assertEqual(config["runtime_profile_sha256"], profiles[1].sha256)
            self.assertEqual(config["runtime_executable_sha256"], images["core"]["sha256"])
            keyfile, data = Path(config["auth_key_file"]), Path(config["data_directory"])
            self.assertEqual(len(keyfile.read_bytes()), 32)
            self.assertEqual(keyfile.stat().st_mode & 0o777, 0o600)
            self.assertEqual(data.stat().st_mode & 0o777, 0o700)
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            self.assertNotIn(keyfile.read_bytes(), path.read_bytes())

    def test_native_ready_requires_exact_five_owned_listener_response(self):
        class Worker:
            def command(self, action): return {"event": "ready", "native_listeners": 4}
        with self.assertRaisesRegex(stack.probe.ProbeError, "STACK_STATE_INVALID"):
            stack.await_state(Worker(), timeout=.01)

    def test_containment_failure_starts_no_worker_and_cleans_only_owned_namespace(self):
        created = []
        class Namespaces:
            def __init__(self, _ip, topology, **_):
                self.topology, self.owned, self.deleted, self.cleanup_errors = topology, {}, [], []
                created.append(self)
            def setup_pair(self): self.owned = dict.fromkeys(self.topology.names)
            def containment(self, *_args, **_kwargs): raise stack.probe.ContainmentMismatch(["NFT_I1_RULE_INVALID"])
            def cleanup(self): self.deleted = list(self.owned)
        with patch.object(stack.probe, "ProbeNamespaces", Namespaces), patch.object(stack, "StackWorker") as spawned:
            result = stack.run_case("peer-loss", candidate(), {}, Path("unused"), {}, "ip", "nft")
        self.assertEqual(result["status"], "failed")
        self.assertEqual((result["namespaces_created"], result["namespaces_removed"]), (2, 2))
        self.assertEqual(result["fixed_diagnostics"], ["NFT_I1_RULE_INVALID"])
        spawned.assert_not_called()


class FaultBarrierTests(unittest.IsolatedAsyncioTestCase):
    def proxy(self, hold=.3):
        _, profiles = pair()
        class Monitor:
            profile = profiles[1]
            def __init__(self): self.finished = asyncio.Event(); self.result = object()
            async def observe(self, child):
                await self.finished.wait()
                return self.result
        monitor = Monitor()
        return monitor, worker.PauseAfterObservation(monitor, hold)

    async def test_pause_ack_requires_completed_fresh_observation_and_never_returns_it(self):
        monitor, proxy = self.proxy()
        observe = asyncio.create_task(proxy.observe(None))
        request = asyncio.create_task(proxy.pause())
        await asyncio.sleep(.01)
        self.assertFalse(request.done()); self.assertFalse(proxy.entered.is_set())
        monitor.finished.set()
        await request
        self.assertTrue(proxy.entered.is_set()); self.assertFalse(observe.done())
        proxy.release.set()
        with self.assertRaisesRegex(worker.ProbeError, "STACK_FAULT_CONTROL_COMPLETE"):
            await observe

    async def test_expiry_is_fixed_failure_not_stale_success_or_unbounded_pause(self):
        monitor, proxy = self.proxy(.04)
        monitor.finished.set()
        proxy.requested = True
        started = time.monotonic()
        with self.assertRaisesRegex(worker.ProbeError, "STACK_PAUSE_EXPIRED"):
            await proxy.observe(None)
        self.assertLess(time.monotonic() - started, .5)
        with self.assertRaises(worker.ProbeError): proxy.remaining()

    async def test_observation_latency_consumes_same_hold_deadline(self):
        monitor, proxy = self.proxy(.02)
        proxy.requested = True
        task = asyncio.create_task(proxy.observe(None))
        await asyncio.sleep(.04)
        monitor.finished.set()
        with self.assertRaisesRegex(worker.ProbeError, "STACK_PAUSE_EXPIRED"):
            await task
        self.assertFalse(proxy.entered.is_set())

    async def test_pending_observation_does_not_enter_revoke_barrier(self):
        monitor, proxy = self.proxy()
        monitor.result = None; monitor.finished.set(); proxy.requested = True
        self.assertIsNone(await proxy.observe(None))
        self.assertFalse(proxy.entered.is_set())

    async def test_failed_real_observation_cannot_ack_pause(self):
        _, proxy = self.proxy()
        async def failed(_): raise worker.LifecycleError("SYNTHETIC_LOSS")
        proxy.monitor.observe = failed; proxy.requested = True
        with self.assertRaises(worker.LifecycleError): await proxy.observe(None)
        self.assertFalse(proxy.entered.is_set())

    async def test_actual_established_tcp_eof_is_success_after_bounded_protocol_bytes(self):
        listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
        self.addCleanup(listener.close)
        client = socket.socket(); client.setblocking(False); self.addCleanup(client.close)
        await asyncio.get_running_loop().sock_connect(client, listener.getsockname())
        server, _ = listener.accept(); self.addCleanup(server.close)
        server.sendall(b"synthetic protocol bytes")
        server.shutdown(socket.SHUT_WR)
        await worker.held_closed(client, .5)

    async def test_actual_open_tcp_timeout_is_not_connection_closure(self):
        listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
        self.addCleanup(listener.close)
        client = socket.socket(); client.setblocking(False); self.addCleanup(client.close)
        await asyncio.get_running_loop().sock_connect(client, listener.getsockname())
        server, _ = listener.accept(); self.addCleanup(server.close)
        with self.assertRaisesRegex(worker.ProbeError, "STACK_HELD_CONNECTION_TIMEOUT"):
            await worker.held_closed(client, .03)

    async def test_held_connection_excess_protocol_bytes_fail(self):
        left, right = socket.socketpair()
        self.addCleanup(left.close); self.addCleanup(right.close)
        left.setblocking(False); right.sendall(b"x" * 20000)
        with self.assertRaisesRegex(worker.ProbeError, "STACK_HELD_CONNECTION_LIMIT"):
            await worker.held_closed(left, .5)

    @unittest.skipUnless(sys.platform == "linux", "Linux selectable pipe")
    async def test_command_pipe_preserves_two_commands_and_cancel_removes_reader(self):
        read, write = os.pipe()
        self.addCleanup(os.close, read); self.addCleanup(os.close, write)
        os.write(write, b'{"action":"status"}\n{"action":"stop"}\n')
        self.assertEqual(await worker.input_line(read), {"action": "status"})
        self.assertEqual(await worker.input_line(read), {"action": "stop"})
        task = asyncio.create_task(worker.input_line(read)); await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError): await task
        os.write(write, b'{"action":"stop"}\n')
        self.assertEqual(await worker.input_line(read), {"action": "stop"})


class CleanupTests(unittest.IsolatedAsyncioTestCase):
    async def test_failed_server_stop_still_closes_query_and_every_image_without_bypassing_order(self):
        events = []
        class Supervisor:
            async def stop(self):
                events.append("native-stop-failed")
                raise worker.LifecycleError("SYNTHETIC_NATIVE_STOP_FAILED")
        class Monitor:
            async def close(self): events.append("query-and-namespace-closed")
        class Image:
            def __init__(self, name, fail=False): self.name, self.fail = name, fail
            def close(self):
                events.append(self.name)
                if self.fail: raise OSError("synthetic")
        clean = await worker.cleanup_stack(held=None, owner=None, pause=None, supervisor=Supervisor(),
            startup=None, core=None, monitor=Monitor(), handles=[Image("first"), Image("second", True)])
        self.assertFalse(clean)
        self.assertEqual(events, ["native-stop-failed", "query-and-namespace-closed", "second", "first"])

    async def test_failed_spawn_never_claims_reap_and_cleanup_cannot_replace_original_code(self):
        _, profiles = pair()
        document = dict(profile=asdict(profiles[0]), config="/tmp/synthetic-only-config",
            **{role: dict(path="/tmp/synthetic-" + role, sha256="1" * 64, bytes=64) for role in ("core", "cli")})
        for close_fails in (False, True):
            state = dict(phase="arguments", cleanup_code=None)
            handles = [Mock(), Mock()]
            monitor = Mock(); monitor.prepare = AsyncMock()
            monitor.close = AsyncMock(side_effect=OSError("PRIVATE_SECRET_SENTINEL") if close_fails else None)
            monitor.core_spec.return_value = worker.ChildSpec(None, ())
            with patch.object(worker, "VerifiedExecutable", side_effect=handles), \
                 patch.object(worker, "OwnedOverlayMonitor", return_value=monitor), \
                 patch.object(worker.OwnedChild, "spawn", new=AsyncMock(side_effect=worker.LifecycleError("CHILD_EXEC_FAILED"))):
                with self.assertRaisesRegex(worker.LifecycleError, "CHILD_EXEC_FAILED") as caught:
                    await worker.serve(document, -1, state)
            record = worker.failure_record(caught.exception, state)
            self.assertEqual((record["phase"], record["code"]), ("core-start", "CHILD_EXEC_FAILED"))
            self.assertFalse(record["cleanup_reaped"])
            self.assertEqual(record["cleanup_code"], "WORKER_CHILD_CLEANUP_FAILED" if close_fails
                             else "CHILD_START_CLEANUP_UNCONFIRMED")
            self.assertNotIn("PRIVATE_SECRET_SENTINEL", json.dumps(record))
            for handle in handles: handle.close.assert_called_once()
            monitor.close.assert_awaited_once()

    async def test_monitor_failure_preserves_failure_and_releases_remaining_real_descriptors(self):
        read, write = os.pipe()
        self.addCleanup(os.close, write)
        class Monitor:
            async def close(self): raise worker.LifecycleError("SYNTHETIC_QUERY_STOP_FAILED")
        class Image:
            def close(self): os.close(read)
        clean = await worker.cleanup_stack(held=None, owner=None, pause=None, supervisor=None,
            startup=None, core=None, monitor=Monitor(), handles=[Image()])
        self.assertFalse(clean)
        with self.assertRaises(OSError): os.fstat(read)


class HeldConnectionWorkerTests(unittest.IsolatedAsyncioTestCase):
    """Production worker commands on real TCP; core/monitor are explicit stubs."""

    async def run_client_commands(self, *, early_fin, protocol_bytes):
        _, profiles = pair()
        document = dict(profile=asdict(profiles[0]), config='/tmp/synthetic-only-config',
            **{role: dict(path='/tmp/synthetic-' + role, sha256='1' * 64, bytes=64)
               for role in ('core', 'cli')})
        listener = socket.socket(); listener.bind(('127.0.0.1', 0)); listener.listen(1)
        listener.setblocking(False); self.addCleanup(listener.close)
        loop = asyncio.get_running_loop()
        accept = asyncio.create_task(loop.sock_accept(listener))
        real_connect = loop.sock_connect
        async def connect(stream, _address):
            return await real_connect(stream, listener.getsockname())
        core = Mock(); core.image_matches.return_value = True; core.stop = AsyncMock()
        monitor = Mock(); monitor.prepare = AsyncMock(); monitor.close = AsyncMock()
        monitor.observe = AsyncMock(return_value=object())
        monitor.core_spec.return_value = worker.ChildSpec(None, ())
        actions = iter(('hold', 'open', 'closed', 'stop'))
        accepted = None
        async def command(_descriptor):
            nonlocal accepted
            action = next(actions)
            if action == 'open':
                accepted, _ = await asyncio.wait_for(accept, 2)
                self.addCleanup(accepted.close)
                accepted.setblocking(False)
                if protocol_bytes:
                    await loop.sock_sendall(accepted, protocol_bytes)
                if early_fin:
                    accepted.shutdown(socket.SHUT_WR)
            elif action == 'closed':
                accepted.shutdown(socket.SHUT_WR)
            return {'action': action}
        events = []
        def reply(event, **values): events.append(dict(event=event, **values))
        state = dict(phase='arguments', cleanup_code=None)
        with patch.object(worker, 'VerifiedExecutable', side_effect=[Mock(), Mock()]), \
             patch.object(worker, 'OwnedOverlayMonitor', return_value=monitor), \
             patch.object(worker.OwnedChild, 'spawn', new=AsyncMock(return_value=core)), \
             patch.object(worker, 'input_line', new=command), patch.object(worker, 'reply', new=reply), \
             patch.object(loop, 'sock_connect', new=connect):
            try:
                if early_fin:
                    with self.assertRaisesRegex(worker.ProbeError, '^STACK_HELD_CONNECTION_NOT_OPEN$'):
                        await worker.serve(document, -1, state)
                else:
                    await worker.serve(document, -1, state)
            finally:
                if not accept.done(): accept.cancel()
                await asyncio.gather(accept, return_exceptions=True)
        core.stop.assert_awaited_once(); monitor.close.assert_awaited_once()
        return events

    async def test_worker_rejects_fin_before_open_and_preserves_cleanup(self):
        for content in (b'', b'synthetic protocol bytes before FIN'):
            with self.subTest(queued_bytes=bool(content)):
                events = await self.run_client_commands(early_fin=True, protocol_bytes=content)
                self.assertEqual([event['event'] for event in events], ['started', 'connected'])

    async def test_worker_open_ack_precedes_later_real_close_ack(self):
        events = await self.run_client_commands(early_fin=False, protocol_bytes=b'synthetic protocol bytes')
        self.assertEqual([event['event'] for event in events], ['started', 'connected', 'open', 'closed', 'stopped'])
        self.assertEqual(events[2], dict(event='open', held_connection=True))
        self.assertEqual(events[3], dict(event='closed', actual_eof_or_reset=True, client_core_alive=True))


@unittest.skipUnless(sys.platform == "linux" and hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal"),
                     "Linux verified ELF/pidfd lifecycle")
class RealChildCompositionTests(unittest.IsolatedAsyncioTestCase):
    async def test_barrier_holds_actual_supervisor_core_until_real_native_lease_eof(self):
        # Same production supervisor/SCM parent as hosted. Only peer/native wire
        # and TUN observations are synthetic; no public server is started here.
        sys.path.insert(0, str(ROOT / "tests/server"))
        try:
            from test_native_lease import CHILD, BINDING
        finally:
            sys.path.pop(0)
        from fifth_freedom_control.overlay_supervisor import OverlayObservation
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            executable = directory / "synthetic-python"
            shutil.copyfile(Path(sys.executable).resolve(), executable); executable.chmod(0o700)
            (directory / "pyvenv.cfg").write_text(
                f"home = {Path(sys.executable).resolve().parent}\ninclude-system-site-packages = false\n")
            with worker.VerifiedExecutable(executable, hashlib.sha256(executable.read_bytes()).hexdigest(),
                                           executable.stat().st_size) as image:
                _, profiles = pair()
                class SyntheticMonitor:
                    profile = profiles[1]
                    async def prepare(self): pass
                    async def observe(self, child):
                        if not child.image_matches(): raise worker.LifecycleError("SYNTHETIC_CORE_EXIT")
                        return OverlayObservation(1, 2, 3, "10.23.0.1")
                class SyntheticTun:
                    async def prepare(self): pass
                    async def observe(self, child): return OverlayObservation(1, 2, 3, "10.23.0.1")
                owner = worker.NativeOwnerLease(lambda: BINDING)
                proxy = worker.PauseAfterObservation(SyntheticMonitor(), 1.5)
                proxy.kernel = SyntheticTun()
                supervisor = worker.OwnedSupervisor(
                    worker.ChildSpec(image, ("-I", "-S", "-c", "import time;time.sleep(60)")),
                    worker.ChildSpec(image, ("-I", "-S", "-c", CHILD, str(owner.child_socket.fileno()),
                        json.dumps(asdict(BINDING)), "valid"), owner.child_socket),
                    proxy, owner, worker.LifecycleLimits(startup=4, observation=2, interval=.01))
                try:
                    await supervisor.start()
                    self.assertTrue(supervisor.ready)
                    await proxy.pause()
                    self.assertTrue(supervisor.overlay.image_matches())
                    owner.revoke()
                    self.assertEqual(await asyncio.wait_for(supervisor.native.wait(), .5), 0)
                    self.assertTrue(supervisor.overlay.image_matches())
                    self.assertFalse(supervisor.overlay.closed)
                    await proxy.prove_overlay(supervisor.overlay)
                    proxy.release.set()
                    await asyncio.wait_for(supervisor.wait(), 3)
                    self.assertTrue(supervisor.native.closed)
                    self.assertTrue(supervisor.overlay.closed)
                finally:
                    owner.revoke(); proxy.release.set()
                    await supervisor.stop()


@unittest.skipUnless(sys.platform == "linux" and hasattr(os, "pidfd_open"), "Linux exact worker pidfd")
class RealWorkerFailureTests(unittest.TestCase):
    def failure(self, arguments, command):
        # This launches only the actual worker's argument/private-input failure
        # path. No namespace entry, runtime, native server or game is possible.
        handle = stack.StackWorker.__new__(stack.StackWorker)
        handle.cleaned, handle.worker_failure, handle.pidfd = False, None, -1
        handle.process = subprocess.Popen([sys.executable, "-I", "-B",
            str(ROOT / "scripts/owned_stack_worker.py"), *arguments], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=stack.probe.CLEAN_ENV, bufsize=0)
        handle.pidfd = os.pidfd_open(handle.process.pid)
        process = handle.process
        try:
            if command:
                handle.process.stdin.write(command)
            with self.assertRaises(stack.probe.WorkerFailure) as raised:
                handle.read(3)
            self.assertTrue(handle.stop())
            self.assertEqual(process.returncode, 1)
            self.assertIsNone(handle.process)
            self.assertEqual(handle.pidfd, -1)
            self.assertNotIn("PRIVATE_SECRET_SENTINEL", json.dumps(raised.exception.record))
            return raised.exception.record
        finally:
            if handle.process is not None: handle.stop()

    def test_actual_argument_failure_retains_fixed_code_and_exact_reap(self):
        record = self.failure([], None)
        self.assertEqual((record["phase"], record["code"]), ("arguments", "WORKER_ARGUMENTS_INVALID"))
        self.assertTrue(record["cleanup_reaped"])

    def test_actual_private_input_failure_is_sanitized_and_never_enters_namespace(self):
        record = self.failure(["-1", "/tmp/PRIVATE_SECRET_SENTINEL/missing.json"], b"START\n")
        self.assertEqual((record["phase"], record["code"]), ("manifest", "PRIVATE_FILE_UNAVAILABLE"))
        self.assertTrue(record["cleanup_reaped"])


if __name__ == "__main__": unittest.main()
