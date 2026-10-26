"""Real owned interpreter CLI/core/Unix lease tests; injected TUN observations.

No EasyTier, native game service, namespace, TUN or production provider is run.
"""
import asyncio
from dataclasses import asdict, replace
import hashlib
import json
import os
from pathlib import Path
import select
import shutil
import signal
import socket
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/public"))
from fifth_freedom_control import owned_overlay as owned
from fifth_freedom_control.native_lease import LeaseBinding, NativeOwnerLease
from fifth_freedom_control.owned_process import (LifecycleError, OwnedChild, OwnedQueryChild,
                                                QueryImageState, VerifiedExecutable)
from fifth_freedom_control.overlay_supervisor import (ChildSpec, LifecycleLimits, OverlayObservation,
    OwnedSupervisor, State, create_public_supervisor)
from fifth_freedom_control.easytier_profile import ProfileError, parse_profile
from test_easytier_profile import recipe, peer_json
from test_native_lease import CHILD as SYNTHETIC_LEASE_PEER

SUPPORTED = sys.platform == "linux" and hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal")
CORE = r'''
import socket,sys
from pathlib import Path
listener=socket.socket();listener.bind(('127.0.0.1',int(sys.argv[1])));listener.listen(8)
while True:
    stream,_=listener.accept()
    with stream:
        stream.recv(16)
        stream.sendall(Path(sys.argv[2]).read_bytes())
'''
QUERY = r'''
import os,socket,sys,time
with socket.create_connection(('127.0.0.1',int(sys.argv[1])),1) as stream:
    stream.sendall(b'SYNTHETIC RPC')
    data=bytearray()
    while True:
        part=stream.recv(8192)
        if not part:break
        data.extend(part)
time.sleep(float(sys.argv[2]))
os.write(1,data)
'''
LEADER_EXIT_QUERY = r'''
import ctypes,os,sys,threading,time
from pathlib import Path
ready,leave,finish=map(Path,sys.argv[1:4])
status=int(sys.argv[4])
def worker():
    if len(sys.argv)>5 and sys.argv[5]=='eof': os.close(1)
    while not finish.exists(): time.sleep(.001)
    if len(sys.argv)<=5 or sys.argv[5]!='eof': os.write(1,b'verified-threaded-output')
    os._exit(status)
threading.Thread(target=worker).start()
ready.touch()
while not leave.exists(): time.sleep(.001)
# Linux x86_64 SYS_exit terminates only this leader, unlike exit_group.
# The actual remaining worker keeps the exact pidfd live and later ends it.
ctypes.CDLL(None).syscall(ctypes.c_long(60),ctypes.c_long(0))
'''


class InjectedTun:
    """Only this kernel/TUN layer is injected; process/RPC/CLI remain real."""
    def __init__(self, observation):
        self.value = observation
        self.calls = 0
        self.after = None

    async def prepare(self):
        return None

    async def observe(self, child):
        self.calls += 1
        return self.after if self.calls > 1 and self.after is not None else self.value


class QueryContractTests(unittest.TestCase):
    def test_query_limits_reject_unbounded_or_boolean_inputs(self):
        for values in ({"timeout": True}, {"timeout": 0}, {"timeout": 2.1},
                       {"timeout": float("nan")}, {"maximum_bytes": True},
                       {"maximum_bytes": 65537}, {"maximum_bytes": 0}):
            with self.subTest(values=values), self.assertRaises(LifecycleError):
                owned.QueryLimits(**values)


@unittest.skipUnless(SUPPORTED, "Linux pidfds, verified ELF execution and procfs")
class OwnedOverlayTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        directory = tempfile.TemporaryDirectory(prefix="ff-owned-observation-")
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        executable = self.directory / "synthetic-python"
        shutil.copyfile(Path(sys.executable).resolve(), executable)
        executable.chmod(0o700)
        (self.directory / "pyvenv.cfg").write_text(
            f"home = {Path(sys.executable).resolve().parent}\ninclude-system-site-packages = false\n")
        self.program = VerifiedExecutable(executable, hashlib.sha256(executable.read_bytes()).hexdigest(),
                                          executable.stat().st_size)
        self.addCleanup(self.program.close)
        self.namespace_fd = os.open("/proc/self/ns/net", os.O_RDONLY | os.O_CLOEXEC)
        self.addCleanup(os.close, self.namespace_fd)
        ns = os.fstat(self.namespace_fd)
        self.kernel = InjectedTun(OverlayObservation(ns.st_dev, ns.st_ino, 123, "10.78.0.1"))
        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            self.port = reserve.getsockname()[1]
        value = recipe()
        value["rpc_port"] = self.port
        value["candidate"].update(executable_sha256=self.program.identity.sha256,
                                  executable_size=self.program.identity.size)
        self.recipe = json.dumps(value).encode()
        self.profile = parse_profile(self.recipe)
        self.reply = self.directory / "synthetic-peer.json"
        self.reply.write_text(json.dumps(peer_json(self.profile)))
        self.revocations = []
        self.delay = "0"
        self.patch_kernel = mock.patch.object(owned, "KernelTunMonitor", return_value=self.kernel)
        self.patch_kernel.start()
        self.addCleanup(self.patch_kernel.stop)
        self.patch_query = mock.patch.object(owned, "peer_query_arguments", side_effect=lambda profile:
            ("-I", "-S", "-c", QUERY, str(profile.rpc_port), self.delay))
        self.patch_query.start()
        self.addCleanup(self.patch_query.stop)

    def query(self, **limits):
        value = owned.OwnedCliQuery(self.program, owned.QueryLimits(**limits))
        self.addAsyncCleanup(value.close)
        return value

    def monitor(self, *, revoke=None, **limits):
        value = owned.OwnedOverlayMonitor(self.recipe, self.program, self.program, self.namespace_fd,
            revoke=revoke or (lambda: self.revocations.append("revoked")), limits=owned.QueryLimits(**limits))
        self.addAsyncCleanup(value.close)
        return value

    async def core(self):
        child = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", CORE, str(self.port), str(self.reply)))
        self.addAsyncCleanup(child.stop)
        async with asyncio.timeout(2):
            while owned.owned_rpc_listener(child, self.port) is None:
                await asyncio.sleep(.01)
        return child

    async def test_real_fast_query_and_default_long_lived_contract_remain_distinct(self):
        from fifth_freedom_control import owned_process
        query = self.query()
        before = len(os.listdir("/proc/self/fd"))
        for _ in range(3):
            result = await query.run(("-I", "-S", "-c", "import os;os.write(1,b'actual-output')"),
                                     on_failure=lambda: self.revocations.append("failed"))
            self.assertEqual(result, b"actual-output")
        self.assertEqual(self.revocations, [])
        self.assertEqual(len(os.listdir("/proc/self/fd")), before)
        original_byte = owned_process._pipe_byte
        async def delayed_exec_observation(*args):
            value = await original_byte(*args)
            if value == b"":
                await asyncio.sleep(.1)  # Target really exits before /proc check.
            return value
        with mock.patch.object(owned_process, "_pipe_byte", delayed_exec_observation):
            self.assertEqual(await query.run(("-I", "-S", "-c", "print('fast-completed')"),
                on_failure=lambda: self.revocations.append("failed")), b"fast-completed\n")
        self.assertEqual(self.revocations, [])
        child = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", "import time;time.sleep(60)"))
        self.addAsyncCleanup(child.stop)
        self.assertIsNone(child._process.stdout)
        with self.assertRaises(TypeError):
            await OwnedQueryChild.spawn(self.program, (), on_failure=lambda: None,
                                        on_owned=lambda child: None, lease_socket=object())

    async def test_verified_header_cannot_turn_failed_fd_exec_into_fast_success(self):
        # Explicitly inert ELF-header input. Kernel exec must reject it; this is
        # not a runtime binary, fixture package or production candidate.
        malformed = bytearray(64)
        malformed[:7] = b"\x7fELF\x02\x01\x01"
        malformed[16:20] = b"\x02\x00\x3e\x00"
        path = self.directory / "inert-invalid-exec"
        path.write_bytes(malformed)
        path.chmod(0o700)
        with VerifiedExecutable(path, hashlib.sha256(malformed).hexdigest(), 64) as program:
            query = owned.OwnedCliQuery(program)
            try:
                with self.assertRaisesRegex(LifecycleError, "^OWNED_QUERY_FAILED$"):
                    await query.run((), on_failure=lambda: self.revocations.append("revoked"))
                self.assertTrue(self.revocations)
                self.assertIsNone(query._child)
            finally:
                await query.close()

    async def test_query_normal_exit_between_real_image_stat_and_liveness_is_completion(self):
        release, ready = self.directory / "exit-release", self.directory / "exit-ready"
        code = """import os,sys,time
from pathlib import Path
os.write(1,b'verified-cli-output')
Path(sys.argv[1]).touch()
while not Path(sys.argv[2]).exists(): time.sleep(.001)
"""
        original_read, original_stat = owned._read_output, os.stat
        children, observed = [], []
        async def read_at_exit(child, maximum):
            children.append(child)
            async with asyncio.timeout(1):
                while not ready.exists():
                    await asyncio.sleep(.001)
            def stat_then_exit(path, *args, **kwargs):
                info = original_stat(path, *args, **kwargs)
                if path == "exe" and kwargs.get("dir_fd") == child.procfd:
                    self.assertEqual((info.st_dev, info.st_ino, info.st_size),
                        (child.image.device, child.image.inode, child.image.size))
                    observed.append(True)
                    release.touch()
                    # The exact child really exits after a successful image
                    # stat, before the production guard's liveness recheck.
                    self.assertTrue(select.select([child.pidfd], [], [], 1)[0])
                return info
            with mock.patch("os.stat", side_effect=stat_then_exit):
                return await original_read(child, maximum)
        query = self.query(timeout=2)
        with mock.patch.object(owned, "_read_output", side_effect=read_at_exit):
            result = await query.run(("-I", "-S", "-c", code, str(ready), str(release)),
                on_failure=lambda: self.revocations.append("revoked"))
        self.assertEqual(result, b"verified-cli-output")
        self.assertEqual(observed, [True])
        self.assertEqual(self.revocations, [])
        self.assertEqual(len(children), 1)
        self.assertTrue(children[0].closed)
        self.assertEqual(children[0]._process.returncode, 0)

    async def test_query_real_leader_exit_does_not_confuse_pending_group_exit_with_image_change(self):
        ready, leave, finish = (self.directory / name for name in ("thread-ready", "leave-leader", "finish-worker"))
        original_read = owned._read_output
        children, transitions = [], []
        async def read_during_real_exit(child, maximum):
            children.append(child)
            async with asyncio.timeout(1):
                while not ready.exists():
                    await asyncio.sleep(.001)
                self.assertEqual(child.query_image_state(), QueryImageState.MATCH)
                leave.touch()
                while True:
                    try:
                        os.stat("exe", dir_fd=child.procfd)
                    except FileNotFoundError:
                        break
                    await asyncio.sleep(.001)
            # A real procfs ENOENT while the exact thread group is still alive;
            # no os.stat, pidfd or process result is mocked for this transition.
            self.assertTrue(child.alive)
            transitions.append(True)
            reading = asyncio.create_task(original_read(child, maximum))
            try:
                await asyncio.sleep(0)
                self.assertFalse(reading.done())
            finally:
                finish.touch()
                if reading.done() and not reading.cancelled():
                    _ = reading.exception()
            return await reading
        query = self.query(timeout=2)
        with mock.patch.object(owned, "_read_output", side_effect=read_during_real_exit):
            result = await query.run(("-I", "-S", "-c", LEADER_EXIT_QUERY,
                str(ready), str(leave), str(finish), "0"), on_failure=lambda: self.revocations.append("revoked"))
        self.assertEqual(result, b"verified-threaded-output")
        self.assertEqual(transitions, [True])
        self.assertEqual(self.revocations, [])
        self.assertTrue(children[0].closed)
        self.assertEqual(children[0]._process.returncode, 0)

    async def test_query_real_pending_exit_requires_zero_completion_and_bounded_exact_cleanup(self):
        for behavior in ("nonzero", "timeout", "cancel"):
            with self.subTest(behavior=behavior):
                ready, leave, finish = (self.directory / (behavior + suffix)
                    for suffix in ("-ready", "-leave", "-finish"))
                original_read = owned._read_output
                children, entered, revoked = [], asyncio.Event(), []
                async def read_during_real_exit(child, maximum):
                    children.append(child)
                    async with asyncio.timeout(1):
                        while not ready.exists():
                            await asyncio.sleep(.001)
                        leave.touch()
                        while True:
                            try:
                                os.stat("exe", dir_fd=child.procfd)
                            except FileNotFoundError:
                                break
                            await asyncio.sleep(.001)
                    self.assertTrue(child.alive)
                    self.assertEqual(child.query_image_state(), QueryImageState.EXIT_PENDING)
                    entered.set()
                    if behavior == "nonzero":
                        finish.touch()
                    return await original_read(child, maximum)
                query = self.query(timeout=.7 if behavior == "timeout" else 2)
                with mock.patch.object(owned, "_read_output", side_effect=read_during_real_exit):
                    # EOF with a live worker cannot publish even empty output.
                    arguments = ("-I", "-S", "-c", LEADER_EXIT_QUERY, str(ready), str(leave),
                                 str(finish), "7" if behavior == "nonzero" else "0",
                                 "output" if behavior == "nonzero" else "eof")
                    task = asyncio.create_task(query.run(arguments, on_failure=lambda: revoked.append(True)))
                    await asyncio.wait_for(entered.wait(), 1.5)
                    if behavior == "cancel":
                        await asyncio.sleep(0)
                        self.assertFalse(task.done())
                        task.cancel()
                        with self.assertRaises(asyncio.CancelledError):
                            await task
                    else:
                        with self.assertRaisesRegex(LifecycleError, "^OWNED_QUERY_FAILED$"):
                            await task
                self.assertTrue(revoked)
                self.assertTrue(children[0].closed)
                self.assertFalse(Path(f"/proc/{children[0].pid}").exists())
                self.assertIsNone(query._child)
                if behavior == "nonzero":
                    self.assertEqual(children[0]._process.returncode, 7)

    async def test_query_real_pending_exit_at_exec_barrier_still_requires_complete_output(self):
        from fifth_freedom_control import owned_process
        ready, leave, finish = (self.directory / name for name in ("early-ready", "early-leave", "early-finish"))
        original_byte, original_read = owned_process._pipe_byte, owned._read_output
        query, observations = self.query(timeout=2), []
        async def leader_exits_before_post_exec_check(*args):
            result = await original_byte(*args)
            if result == b"":
                async with asyncio.timeout(1):
                    while not ready.exists():
                        await asyncio.sleep(.001)
                    leave.touch()
                    while True:
                        try:
                            os.stat("exe", dir_fd=query._child.procfd)
                        except FileNotFoundError:
                            break
                        await asyncio.sleep(.001)
                self.assertTrue(query._child.alive)
                observations.append(query._child)
            return result
        async def finish_only_after_spawn_returns(child, maximum):
            self.assertEqual(child.query_image_state(), QueryImageState.EXIT_PENDING)
            self.assertTrue(child.alive)
            finish.touch()
            return await original_read(child, maximum)
        with mock.patch.object(owned_process, "_pipe_byte", side_effect=leader_exits_before_post_exec_check), \
                mock.patch.object(owned, "_read_output", side_effect=finish_only_after_spawn_returns):
            result = await query.run(("-I", "-S", "-c", LEADER_EXIT_QUERY,
                str(ready), str(leave), str(finish), "0"), on_failure=lambda: self.revocations.append(True))
        self.assertEqual(result, b"verified-threaded-output")
        self.assertEqual(len(observations), 1)
        self.assertTrue(observations[0].closed)
        self.assertEqual(observations[0]._process.returncode, 0)
        self.assertEqual(self.revocations, [])

    async def test_query_actual_other_image_stays_rejected_even_after_exit_zero(self):
        transition, release = self.directory / "exec-release", self.directory / "exit-release"
        original_image = Path(sys.executable).resolve()
        final = """import os,sys,time
from pathlib import Path
os.write(1,b'foreign-image-output')
while not Path(sys.argv[1]).exists(): time.sleep(.001)
"""
        first = """import os,sys,time
from pathlib import Path
while not Path(sys.argv[1]).exists(): time.sleep(.001)
os.execv(sys.argv[2], [sys.argv[2], '-I', '-S', '-c', sys.argv[3], sys.argv[4]])
"""
        child = await OwnedQueryChild.spawn(self.program,
            ("-I", "-S", "-c", first, str(transition), str(original_image), final, str(release)),
            on_failure=lambda: None, on_owned=lambda child: None)
        self.addAsyncCleanup(child.stop)
        self.assertEqual(child.query_image_state(), QueryImageState.MATCH)
        transition.touch()
        async with asyncio.timeout(1):
            while os.stat("exe", dir_fd=child.procfd).st_ino == child.image.inode:
                await asyncio.sleep(.001)
        original_stat = os.stat
        observed = []
        def foreign_stat_then_exit(path, *args, **kwargs):
            info = original_stat(path, *args, **kwargs)
            if path == "exe" and kwargs.get("dir_fd") == child.procfd:
                self.assertNotEqual((info.st_dev, info.st_ino), (child.image.device, child.image.inode))
                observed.append(True)
                release.touch()
                self.assertTrue(select.select([child.pidfd], [], [], 1)[0])
            return info
        with mock.patch("os.stat", side_effect=foreign_stat_then_exit):
            with self.assertRaisesRegex(LifecycleError, "^QUERY_IMAGE_CHANGED$"):
                await owned._read_output(child, 128)
        self.assertEqual(observed, [True])
        self.assertEqual(await child.wait(), 0)
        self.assertEqual(child.query_image_state(), QueryImageState.MISMATCH)
        with self.assertRaisesRegex(LifecycleError, "^QUERY_IMAGE_CHANGED$"):
            await owned._read_output(child, 128)
        self.program.check_unchanged()

    async def test_query_procfs_error_needs_exact_exit_and_never_clears_known_failure(self):
        import errno
        original_stat = os.stat
        code = """import os,sys,time
from pathlib import Path
os.write(1,b'exact-output')
while not Path(sys.argv[1]).exists(): time.sleep(.001)
"""
        for number, exit_during_stat in ((errno.EACCES, False), (errno.EACCES, True),
                                       (errno.ENOENT, False), (errno.ENOENT, True)):
            with self.subTest(errno=number, exited=exit_during_stat):
                release = self.directory / (str(number) + str(exit_during_stat))
                child = await OwnedQueryChild.spawn(self.program,
                    ("-I", "-S", "-c", code, str(release)), on_failure=lambda: None,
                    on_owned=lambda child: None)
                self.addAsyncCleanup(child.stop)
                def injected_error(path, *args, **kwargs):
                    if path == "exe" and kwargs.get("dir_fd") == child.procfd:
                        self.assertTrue(child.alive)
                        if exit_during_stat:
                            release.touch()
                            self.assertTrue(select.select([child.pidfd], [], [], 1)[0])
                        raise OSError(number, "PRIVATE PROCFS SENTINEL")
                    return original_stat(path, *args, **kwargs)
                with mock.patch("os.stat", side_effect=injected_error):
                    state = child.query_image_state()
                if number == errno.ENOENT and exit_during_stat:
                    self.assertEqual(state, QueryImageState.EXITED)
                    self.assertEqual(await owned._read_output(child, 128), b"exact-output")
                elif number == errno.ENOENT:
                    self.assertEqual(state, QueryImageState.EXIT_PENDING)
                    # The injected absence resolves to a genuinely live image,
                    # not exact exit. It must not revive this query attempt.
                    self.assertEqual(child.query_image_state(), QueryImageState.UNAVAILABLE)
                    release.touch()
                    self.assertEqual(await asyncio.wait_for(child.wait(), 1), 0)
                    self.assertEqual(child.query_image_state(), QueryImageState.UNAVAILABLE)
                    with self.assertRaisesRegex(LifecycleError, "^QUERY_IMAGE_CHANGED$"):
                        await owned._read_output(child, 128)
                else:
                    self.assertEqual(state, QueryImageState.UNAVAILABLE)
                    release.touch()
                    self.assertEqual(await asyncio.wait_for(child.wait(), 1), 0)
                    self.assertEqual(child.query_image_state(), QueryImageState.UNAVAILABLE)
                    with self.assertRaisesRegex(LifecycleError, "^QUERY_IMAGE_CHANGED$"):
                        await owned._read_output(child, 128)
                await child.stop()
                self.assertTrue(child.closed)

    async def test_query_spawn_cannot_accept_observed_foreign_image_by_later_exit_zero(self):
        from fifth_freedom_control import owned_process
        transition, release = self.directory / "exec-release", self.directory / "exit-release"
        final = """import os,sys,time
from pathlib import Path
os.write(1,b'foreign-output')
while not Path(sys.argv[1]).exists(): time.sleep(.001)
"""
        first = """import os,sys,time
from pathlib import Path
while not Path(sys.argv[1]).exists(): time.sleep(.001)
os.execv(sys.argv[2], [sys.argv[2], '-I', '-S', '-c', sys.argv[3], sys.argv[4]])
"""
        original_byte, original_stat = owned_process._pipe_byte, os.stat
        children, at_image_check, revoked = [], False, []
        async def transition_after_exec(*arguments):
            nonlocal at_image_check
            value = await original_byte(*arguments)
            if value == b"":
                transition.touch()
                child = children[0]
                async with asyncio.timeout(1):
                    while original_stat("exe", dir_fd=child.procfd).st_ino == child.image.inode:
                        await asyncio.sleep(.001)
                at_image_check = True
            return value
        def stat_then_exit(path, *args, **kwargs):
            info = original_stat(path, *args, **kwargs)
            if at_image_check and path == "exe" and kwargs.get("dir_fd") == children[0].procfd:
                self.assertNotEqual(info.st_ino, children[0].image.inode)
                release.touch()
                self.assertTrue(select.select([children[0].pidfd], [], [], 1)[0])
            return info
        with mock.patch.object(owned_process, "_pipe_byte", side_effect=transition_after_exec), \
                mock.patch("os.stat", side_effect=stat_then_exit):
            with self.assertRaisesRegex(LifecycleError, "^CHILD_EXEC_FAILED$"):
                await OwnedQueryChild.spawn(self.program,
                    ("-I", "-S", "-c", first, str(transition), str(Path(sys.executable).resolve()),
                     final, str(release)), on_failure=lambda: revoked.append(True), on_owned=children.append)
        self.assertEqual(revoked, [True])
        self.assertEqual(len(children), 1)
        self.assertTrue(children[0].closed)
        self.assertEqual(children[0]._process.returncode, 0)
        self.assertEqual(children[0].query_image_state(), QueryImageState.MISMATCH)

    async def test_nonzero_overflow_timeout_revoke_before_cleanup_without_raw_output(self):
        original_stop, stopped = OwnedChild.stop, []
        async def stop(child, *args, **kwargs):
            if isinstance(child, OwnedQueryChild):
                stopped.append((bool(self.revocations), child))
            return await original_stop(child, *args, **kwargs)
        with mock.patch.object(OwnedChild, "stop", stop):
            for code in ("import os;os.write(1,b'PRIVATE RAW');raise SystemExit(7)",
                         "import os;os.write(1,b'x'*256)", "import time;time.sleep(60)"):
                self.revocations.clear()
                query = self.query(timeout=.2, maximum_bytes=128)
                with self.assertRaisesRegex(LifecycleError, "^OWNED_QUERY_FAILED$"):
                    await query.run(("-I", "-S", "-c", code),
                                    on_failure=lambda: self.revocations.append("revoked"))
                self.assertTrue(self.revocations)
        self.assertTrue(stopped)
        self.assertTrue(all(revoked and child.closed for revoked, child in stopped))

    async def test_query_cancellation_reaps_exact_child_and_preserves_other_child(self):
        sentinel = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", "import time;time.sleep(60)"))
        self.addAsyncCleanup(sentinel.stop)
        query = self.query(timeout=2)
        task = asyncio.create_task(query.run(("-I", "-S", "-c", "import time;time.sleep(60)"),
                                             on_failure=lambda: self.revocations.append("revoked")))
        async with asyncio.timeout(1):
            while query._child is None:
                await asyncio.sleep(.005)
        child = query._child
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertTrue(child.closed)
        self.assertTrue(self.revocations)
        self.assertTrue(sentinel.image_matches())
        self.assertFalse(Path(f"/proc/{child.pid}").exists())

    async def test_cancel_during_bootstrap_revokes_before_exact_cleanup_even_when_cancelled_twice(self):
        from fifth_freedom_control import owned_process
        reached, cleaning, release_cleanup = asyncio.Event(), asyncio.Event(), asyncio.Event()
        original_stop, children = OwnedChild.stop, []
        async def blocked_barrier(*arguments):
            reached.set()
            await asyncio.Event().wait()
        async def stop(child, *arguments, **kwargs):
            children.append(child)
            self.assertTrue(self.revocations)
            cleaning.set()
            await release_cleanup.wait()
            return await original_stop(child, *arguments, **kwargs)
        query = self.query(timeout=2)
        with mock.patch.object(owned_process, "_pipe_byte", blocked_barrier), \
                mock.patch.object(OwnedChild, "stop", stop):
            task = asyncio.create_task(query.run(("-I", "-S", "-c", "import time;time.sleep(60)"),
                on_failure=lambda: self.revocations.append("revoked")))
            await asyncio.wait_for(reached.wait(), 1)
            task.cancel()
            await asyncio.wait_for(cleaning.wait(), 1)
            task.cancel()
            release_cleanup.set()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertTrue(children)
        self.assertTrue(all(child is children[0] and child.closed for child in children))
        self.assertFalse(Path(f"/proc/{children[0].pid}").exists())

    async def test_changed_cli_metadata_never_executes(self):
        query = self.query()
        self.program.path.chmod(0o500)
        with self.assertRaisesRegex(LifecycleError, "^OWNED_QUERY_FAILED$"):
            await query.run(("-I", "-S", "-c", "raise AssertionError('must not execute')"),
                            on_failure=lambda: self.revocations.append("revoked"))
        self.assertIsNone(query._child)
        self.assertTrue(self.revocations)

    async def test_failed_query_stop_retains_handle_and_blocks_replacement_until_close(self):
        query = self.query(timeout=.2)
        async def failed_stop(child, *args, **kwargs):
            raise LifecycleError("INJECTED_STOP_FAILURE")
        with mock.patch.object(OwnedQueryChild, "stop", failed_stop):
            with self.assertRaisesRegex(LifecycleError, "INJECTED_STOP_FAILURE"):
                await query.run(("-I", "-S", "-c", "import time;time.sleep(60)"),
                                on_failure=lambda: self.revocations.append("revoked"))
        retained = query._child
        self.assertIsNotNone(retained)
        self.assertTrue(retained.alive)
        with mock.patch.object(OwnedQueryChild, "spawn") as spawn:
            with self.assertRaisesRegex(LifecycleError, "QUERY_STATE_INVALID"):
                await query.run((), on_failure=lambda: None)
            spawn.assert_not_called()
        self.assertIs(query._child, retained)
        await query.close()
        self.assertTrue(retained.closed)
        self.assertIsNone(query._child)

    async def test_bootstrap_cleanup_failure_keeps_preawait_handle_for_retry(self):
        from fifth_freedom_control import owned_process
        reached = asyncio.Event()
        async def blocked_barrier(*arguments):
            reached.set()
            await asyncio.Event().wait()
        async def failed_stop(child, *arguments, **keywords):
            raise LifecycleError("INJECTED_BOOTSTRAP_STOP_FAILURE")
        query = self.query(timeout=2)
        with mock.patch.object(owned_process, "_pipe_byte", blocked_barrier), \
                mock.patch.object(OwnedQueryChild, "stop", failed_stop):
            task = asyncio.create_task(query.run(("-I", "-S", "-c", "import time;time.sleep(60)"),
                on_failure=lambda: self.revocations.append("revoked")))
            await asyncio.wait_for(reached.wait(), 1)
            retained = query._child
            self.assertIsNotNone(retained)
            task.cancel()
            with self.assertRaisesRegex(LifecycleError, "INJECTED_BOOTSTRAP_STOP_FAILURE"):
                await task
        self.assertIs(query._child, retained)
        self.assertFalse(retained.closed)
        with mock.patch.object(OwnedQueryChild, "spawn") as spawn:
            with self.assertRaisesRegex(LifecycleError, "QUERY_STATE_INVALID"):
                await query.run((), on_failure=lambda: None)
            spawn.assert_not_called()
        await query.close()
        self.assertTrue(retained.closed)
        self.assertFalse(Path(f"/proc/{retained.pid}").exists())

    async def test_exact_loopback_rpc_inode_rejects_another_same_uid_child(self):
        core = await self.core()
        self.assertGreater(owned.owned_rpc_listener(core, self.port), 0)
        other = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", "import time;time.sleep(60)"))
        self.addAsyncCleanup(other.stop)
        with self.assertRaisesRegex(LifecycleError, "RPC_LISTENER_NOT_OWNED_LOOPBACK"):
            owned.owned_rpc_listener(other, self.port)
        original = owned._proc_bytes
        def changed(child, name, maximum):
            data = original(child, name, maximum)
            return data.replace(f"0100007F:{self.port:04X}".encode(), f"00000000:{self.port:04X}".encode())
        with mock.patch.object(owned, "_proc_bytes", side_effect=changed):
            with self.assertRaisesRegex(LifecycleError, "RPC_LISTENER_NOT_OWNED_LOOPBACK"):
                owned.owned_rpc_listener(core, self.port)
        def duplicate_ipv6(child, name, maximum):
            if name == "net/tcp6":
                row = next(row for row in original(child, "net/tcp", maximum).splitlines()
                           if f"0100007F:{self.port:04X}".encode() in row)
                return b"header\n" + row.replace(b"0100007F", b"0" * 31 + b"1") + b"\n"
            return original(child, name, maximum)
        with mock.patch.object(owned, "_proc_bytes", side_effect=duplicate_ipv6):
            with self.assertRaisesRegex(LifecycleError, "RPC_LISTENER_NOT_OWNED_LOOPBACK"):
                owned.owned_rpc_listener(core, self.port)

    async def test_pending_peer_then_actual_cli_success_and_loss_is_permanent(self):
        core, monitor = await self.core(), self.monitor()
        await monitor.prepare()
        self.reply.write_bytes(b"[]")
        self.assertIsNone(await monitor.observe(core))
        self.assertFalse(self.revocations)
        self.reply.write_text(json.dumps(peer_json(self.profile)))
        self.assertEqual(await monitor.observe(core), self.kernel.value)
        self.reply.write_bytes(b"[]")
        with self.assertRaisesRegex(LifecycleError, "OWNED_OVERLAY_OBSERVATION_FAILED"):
            await monitor.observe(core)
        self.assertEqual(self.revocations, ["revoked"])
        self.reply.write_text(json.dumps(peer_json(self.profile)))
        with self.assertRaises(LifecycleError):
            await monitor.observe(core)
        self.assertEqual(self.revocations, ["revoked"])

    async def test_tun_change_across_real_cli_await_rejects_even_matching_peer_json(self):
        core, monitor = await self.core(), self.monitor()
        await monitor.prepare()
        self.kernel.after = replace(self.kernel.value, interface_index=124)
        with self.assertRaisesRegex(LifecycleError, "OWNED_OVERLAY_OBSERVATION_FAILED"):
            await monitor.observe(core)
        self.assertEqual(self.revocations, ["revoked"])

    async def test_core_death_during_cli_read_cannot_publish_stale_success(self):
        core, monitor = await self.core(), self.monitor()
        await monitor.prepare()
        self.delay = ".2"
        task = asyncio.create_task(monitor.observe(core))
        async with asyncio.timeout(1):
            while monitor._query._child is None:
                await asyncio.sleep(.005)
        # Query startup is observed, then allow its synthetic RPC read to finish.
        await asyncio.sleep(.05)
        await core.stop()
        with self.assertRaisesRegex(LifecycleError, "OWNED_OVERLAY_OBSERVATION_FAILED"):
            await task
        self.assertTrue(self.revocations)
        self.assertIsNone(monitor._accepted)

    async def test_namespace_change_after_query_and_rpc_replacement_cannot_publish_success(self):
        core, monitor = await self.core(), self.monitor()
        await monitor.prepare()
        original_run = monitor._query.run
        changed = False
        async def run(*arguments, **keywords):
            nonlocal changed
            result = await original_run(*arguments, **keywords)
            changed = True
            return result
        actual = owned._current_namespace()
        with mock.patch.object(monitor._query, "run", run), \
                mock.patch.object(owned, "_current_namespace", side_effect=lambda:
                                  (actual[0], actual[1] + 1) if changed else actual):
            with self.assertRaisesRegex(LifecycleError, "OWNED_OVERLAY_OBSERVATION_FAILED"):
                await monitor.observe(core)
        self.assertEqual(self.revocations, ["revoked"])
        monitor2 = self.monitor()
        await monitor2.prepare()
        original_rpc = owned.owned_rpc_listener
        observations = 0
        def replaced(child, port):
            nonlocal observations
            observations += 1
            return original_rpc(child, port) + (1 if observations > 1 else 0)
        with mock.patch.object(owned, "owned_rpc_listener", side_effect=replaced):
            with self.assertRaisesRegex(LifecycleError, "OWNED_OVERLAY_OBSERVATION_FAILED"):
                await monitor2.observe(core)
        self.assertEqual(self.revocations, ["revoked", "revoked"])

    async def test_close_during_observation_revokes_then_reaps_query_without_reviving(self):
        core, monitor = await self.core(), self.monitor()
        await monitor.prepare()
        self.delay = "1"
        observation = asyncio.create_task(monitor.observe(core))
        async with asyncio.timeout(1):
            while monitor._query._child is None:
                await asyncio.sleep(.005)
        query_child = monitor._query._child
        await monitor.close()
        with self.assertRaises(asyncio.CancelledError):
            await observation
        self.assertTrue(query_child.closed)
        self.assertTrue(self.revocations)
        self.assertEqual(monitor._namespace_fd, -1)
        with self.assertRaises(LifecycleError):
            await monitor.observe(core)

    async def test_stale_binding_revokes_without_renewal_and_never_enables_public(self):
        core, monitor = await self.core(), self.monitor()
        await monitor.prepare()
        await monitor.observe(core)
        arguments = dict(config_sha256="a" * 64, service_manifest_sha256="b" * 64,
                         admitted_client_storage_key="synthetic-admitted-client")
        binding = monitor.lease_binding(**arguments)
        self.assertEqual(binding.runtime_profile_sha256, self.profile.sha256)
        self.assertEqual(binding.runtime_executable_sha256, self.program.identity.sha256)
        monitor._observed_at -= 1
        with self.assertRaisesRegex(LifecycleError, "OVERLAY_LEASE_BINDING_INVALID"):
            monitor.lease_binding(**arguments)
        self.assertTrue(self.revocations)
        with self.assertRaisesRegex(LifecycleError, "PUBLIC_PROFILE_NOT_READY"):
            create_public_supervisor(self.profile.candidate)

    async def test_repolling_unchanged_handshake_state_never_authorizes_production(self):
        core, monitor = await self.core(), self.monitor()
        await monitor.prepare()
        historical_reply = self.reply.read_bytes()
        arguments = dict(config_sha256="a" * 64, service_manifest_sha256="b" * 64,
                         admitted_client_storage_key="synthetic-admitted-client")
        first = await monitor.observe(core)
        first_local_time = monitor._observed_at
        development_binding = monitor.lease_binding(**arguments)
        second = await monitor.observe(core)
        self.assertEqual(first, second)
        self.assertEqual(self.reply.read_bytes(), historical_reply)
        self.assertGreater(monitor._observed_at, first_local_time)
        self.assertEqual(monitor.lease_binding(**arguments), development_binding)
        # Real owned CLI queries refreshed only local state. This inert peer
        # sent no authenticated runtime data and cannot become an admission
        # proof merely by being polled again.
        with mock.patch.object(OwnedChild, "spawn") as spawn:
            with self.assertRaisesRegex(LifecycleError, "PUBLIC_PROFILE_NOT_READY"):
                create_public_supervisor(self.profile.candidate)
            spawn.assert_not_called()
        self.assertEqual(self.revocations, [])

    async def test_profile_image_and_namespace_inputs_cannot_be_substituted(self):
        changed = json.loads(self.recipe)
        changed["candidate"]["executable_sha256"] = "f" * 64
        with self.assertRaisesRegex(LifecycleError, "OVERLAY_CORE_PROFILE_MISMATCH"):
            owned.OwnedOverlayMonitor(json.dumps(changed).encode(), self.program, self.program,
                self.namespace_fd, revoke=lambda: None)
        changed = json.loads(self.recipe)
        changed["purpose"] = "production"
        with self.assertRaises(ProfileError):
            owned.OwnedOverlayMonitor(json.dumps(changed).encode(), self.program, self.program,
                self.namespace_fd, revoke=lambda: None)
        descriptor = os.open(self.reply, os.O_RDONLY)
        try:
            with self.assertRaisesRegex(LifecycleError, "OVERLAY_NAMESPACE_CHANGED"):
                owned.OwnedOverlayMonitor(self.recipe, self.program, self.program, descriptor, revoke=lambda: None)
        finally:
            os.close(descriptor)

    async def test_real_supervisor_cli_and_native_owner_lease_compose_and_revoke(self):
        monitor = None
        arguments = dict(config_sha256="a" * 64, service_manifest_sha256="b" * 64,
                         admitted_client_storage_key="synthetic-admitted-client")
        owner = NativeOwnerLease(lambda: monitor.lease_binding(**arguments), lease_ms=3000, renew_seconds=.1)
        self.addCleanup(owner.revoke)
        monitor = self.monitor(revoke=owner.revoke, timeout=1)
        observation = self.kernel.value
        expected = LeaseBinding(**arguments, runtime_profile_sha256=self.profile.sha256,
            runtime_executable_sha256=self.program.identity.sha256, netns_device=observation.namespace_device,
            netns_inode=observation.namespace_inode, interface_index=observation.interface_index,
            overlay_ipv4=observation.address, overlay_prefix=30)
        # The real helper supplies fixed production core arguments. Only this
        # fixture replaces them with an inert loopback JSON service interpreter.
        actual_spec = monitor.core_spec(str(self.directory / "private.toml"))
        self.assertIn(f"127.0.0.1:{self.port}", actual_spec.arguments)
        overlay = replace(actual_spec, arguments=("-I", "-S", "-c", CORE, str(self.port), str(self.reply)))
        native = ChildSpec(self.program, ("-I", "-S", "-c", SYNTHETIC_LEASE_PEER,
            str(owner.child_socket.fileno()), json.dumps(asdict(expected)), "normal"), owner.child_socket)
        supervisor = OwnedSupervisor(overlay, native, monitor, owner,
            LifecycleLimits(startup=5, observation=1.5, interval=.03, terminate=.1, kill=1))
        self.addAsyncCleanup(supervisor.stop)
        original_stop, observed_stops = OwnedChild.stop, []
        async def ordered_stop(child, *args, **kwargs):
            if child is supervisor.native:
                self.assertTrue(owner._revoked)  # Before the first cleanup await.
                observed_stops.append("native")
            elif child is supervisor.overlay:
                self.assertTrue(supervisor.native.closed)
                observed_stops.append("overlay")
            return await original_stop(child, *args, **kwargs)
        with mock.patch.object(OwnedChild, "stop", ordered_stop):
            await supervisor.start()
            self.assertTrue(supervisor.ready)
            self.assertTrue(owner._ready)
            self.reply.write_bytes(b"[]")
            await asyncio.wait_for(supervisor.wait(), 3)
        self.assertEqual(supervisor.state, State.STOPPED)
        self.assertTrue(owner._revoked)
        self.assertTrue(supervisor.native.closed and supervisor.overlay.closed)
        self.assertEqual(observed_stops, ["native", "overlay"])


if __name__ == "__main__":
    unittest.main()
