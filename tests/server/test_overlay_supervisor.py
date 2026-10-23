"""Real owned synthetic children/TCP, injected observations; never EasyTier/TUN."""
import asyncio
from dataclasses import replace
import hashlib
import json
import os
from pathlib import Path
import select
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/public"))
try:
    from fifth_freedom_control.owned_process import LifecycleError, OwnedChild, VerifiedExecutable
    from fifth_freedom_control.overlay_supervisor import (ChildSpec, KernelTunMonitor, LifecycleLimits,
        OverlayObservation, OwnedSupervisor, RuntimeCandidateIdentity, State, create_public_supervisor)
finally:
    sys.path.pop(0)

SUPPORTED = sys.platform == "linux" and hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal")
SLEEP = "import time; time.sleep(60)"
IGNORE_TERM = "import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); time.sleep(60)"
NATIVE = r'''
import json,os,selectors,socket,sys
listener=socket.socket()
listener.bind(('127.0.0.1',0)); listener.listen(8); listener.setblocking(False)
with open(sys.argv[1], 'w') as output:
    json.dump({'pid':os.getpid(),'port':listener.getsockname()[1]},output)
selector=selectors.DefaultSelector(); selector.register(listener,selectors.EVENT_READ)
while True:
    for key,_ in selector.select(.2):
        if key.fileobj is listener:
            client,_=listener.accept(); client.setblocking(False)
            selector.register(client,selectors.EVENT_READ)
        else:
            try: data=key.fileobj.recv(256)
            except ConnectionError: data=b''
            if data: key.fileobj.sendall(data)
            else: selector.unregister(key.fileobj); key.fileobj.close()
'''


def image(path=None):
    path = Path(path or sys.executable).resolve()
    return VerifiedExecutable(path, hashlib.sha256(path.read_bytes()).hexdigest(), path.stat().st_size)


class SyntheticMonitor:
    """Deliberately injected; cannot be reached via closed Public factory."""
    def __init__(self):
        self.value = OverlayObservation(1, 2, 3, "10.23.0.1")
        self.failure = None
        self.prepared = False

    async def prepare(self):
        self.prepared = True

    async def observe(self, child):
        if self.failure:
            raise self.failure
        return self.value


class SyntheticTcpReadiness:
    def __init__(self, path):
        self.path = path
        self.port = None

    async def observe(self, child):
        if not self.path.exists():
            return False
        try:
            self.port = json.loads(self.path.read_text())["port"]
            reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
            try:
                writer.write(b"synthetic-ready")
                await writer.drain()
                return await reader.readexactly(15) == b"synthetic-ready"
            finally:
                writer.close()
                await writer.wait_closed()
        except (OSError, ValueError, KeyError, asyncio.IncompleteReadError):
            return False


@unittest.skipUnless(SUPPORTED, "Linux pidfd/parent-death execution required")
class ExecutableIdentityTests(unittest.TestCase):
    def test_fixed_digest_elf_permissions_symlink_and_file_capability_gate(self):
        with tempfile.TemporaryDirectory(prefix="ff-owned-image-") as directory:
            binary = Path(directory) / "program"
            shutil.copyfile(Path(sys.executable).resolve(), binary)
            binary.chmod(0o700)
            digest, size = hashlib.sha256(binary.read_bytes()).hexdigest(), binary.stat().st_size
            with VerifiedExecutable(binary, digest, size) as verified:
                verified.check_unchanged()
            with self.assertRaisesRegex(LifecycleError, "HASH_MISMATCH"):
                VerifiedExecutable(binary, "0" * 64, size)
            for mode in (0o702, 0o720, 0o4700, 0o2700):
                binary.chmod(mode)
                with self.assertRaisesRegex(LifecycleError, "FILE_INVALID"):
                    VerifiedExecutable(binary, digest, size)
            binary.chmod(0o700)
            alias = Path(directory) / "alias"
            alias.symlink_to(binary)
            with self.assertRaisesRegex(LifecycleError, "UNAVAILABLE"):
                VerifiedExecutable(alias, digest, size)
            fifo = Path(directory) / "fifo"
            os.mkfifo(fifo, 0o700)
            with self.assertRaisesRegex(LifecycleError, "FILE_INVALID"):
                VerifiedExecutable(fifo, digest, size)
            with patch("fifth_freedom_control.owned_process.os.getxattr", return_value=b"capability"):
                with self.assertRaisesRegex(LifecycleError, "CAPABILITIES_FORBIDDEN"):
                    VerifiedExecutable(binary, digest, size)
            with patch("fifth_freedom_control.owned_process.os.getxattr", side_effect=PermissionError()):
                with self.assertRaisesRegex(LifecycleError, "CAPABILITIES_UNVERIFIED"):
                    VerifiedExecutable(binary, digest, size)
            with VerifiedExecutable(binary, digest, size) as verified:
                binary.chmod(0o500)
                with self.assertRaisesRegex(LifecycleError, "EXECUTABLE_CHANGED"):
                    verified.check_unchanged()

    def test_candidate_integrity_never_enables_public(self):
        identity = RuntimeCandidateIdentity("a" * 40, *("b" * 64 for _ in range(5)), 1024)
        with self.assertRaisesRegex(LifecycleError, "PUBLIC_PROFILE_NOT_READY"):
            create_public_supervisor(identity)
        for kwargs in ({"target": "x86_64-unknown-linux-musl"}, {"role": "easytier-cli"},
                       {"patch_manifest_sha256": "invalid"}, {"executable_size": True}):
            with self.assertRaisesRegex(LifecycleError, "RUNTIME_CANDIDATE_INVALID"):
                replace(identity, **kwargs)


@unittest.skipUnless(SUPPORTED, "Linux pidfd/parent-death execution required")
class OwnedLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="ff-owned-lifecycle-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        # Hosted Python tool caches may have shared writable ancestors. Keep the
        # production installation policy strict: the synthetic fixture owns an
        # exact private copy of the actual interpreter ELF it will execute.
        owned_image = self.directory / "synthetic-python"
        shutil.copyfile(Path(sys.executable).resolve(), owned_image)
        owned_image.chmod(0o700)
        # A relocated CPython ELF still needs its installed standard library.
        # Explicit venv home uses that existing host runtime without inheriting
        # PYTHONHOME/PYTHONPATH or weakening any executable ownership checks.
        runtime_home = Path(sys.executable).resolve().parent
        runtime_config = self.directory / "pyvenv.cfg"
        runtime_config.write_text(f"home = {runtime_home}\ninclude-system-site-packages = false\n")
        runtime_config.chmod(0o600)
        self.program = image(owned_image)
        self.addCleanup(self.program.close)
        self.monitor = SyntheticMonitor()
        self.service = SyntheticTcpReadiness(self.directory / "synthetic-ready.json")
        self.limits = LifecycleLimits(startup=3, observation=.3, interval=.03, terminate=.15, kill=1)

    def supervisor(self, overlay=SLEEP, native=NATIVE):
        supervisor = OwnedSupervisor(ChildSpec(self.program, ("-I", "-S", "-c", overlay)),
            ChildSpec(self.program, ("-I", "-S", "-c", native, str(self.service.path))),
            self.monitor, self.service, self.limits)
        self.addAsyncCleanup(supervisor.stop)
        return supervisor

    async def test_overlay_exit_revokes_and_closes_established_native_tcp(self):
        unrelated = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", SLEEP))
        self.addAsyncCleanup(unrelated.stop)
        supervisor = self.supervisor()
        await supervisor.start()
        self.assertTrue(supervisor.ready)
        reader, writer = await asyncio.open_connection("127.0.0.1", self.service.port)
        try:
            writer.write(b"held-connection")
            await writer.drain()
            self.assertEqual(await reader.readexactly(15), b"held-connection")
            signal.pidfd_send_signal(supervisor.overlay.pidfd, signal.SIGKILL)
            await asyncio.wait_for(supervisor.wait(), 3)
            self.assertFalse(supervisor.ready)
            self.assertEqual(supervisor.reason, "OVERLAY_EXITED")
            self.assertTrue(supervisor.native.closed)
            self.assertTrue(supervisor.overlay.closed)
            self.assertEqual(await asyncio.wait_for(reader.read(1), 1), b"")
            with self.assertRaises(OSError):
                await asyncio.open_connection("127.0.0.1", self.service.port)
            self.assertTrue(unrelated.alive)
        finally:
            writer.close()
            await writer.wait_closed()

    async def test_concurrent_start_reserves_one_runner_and_one_child_pair(self):
        supervisor = self.supervisor()
        with patch("fifth_freedom_control.overlay_supervisor.OwnedChild.spawn", wraps=OwnedChild.spawn) as spawn:
            results = await asyncio.gather(supervisor.start(), supervisor.start(), return_exceptions=True)
        self.assertEqual(sum(result is None for result in results), 1)
        errors = [result for result in results if isinstance(result, LifecycleError)]
        self.assertEqual([str(error) for error in errors], ["SUPERVISOR_ALREADY_STARTED"])
        self.assertEqual(spawn.call_count, 2)
        self.assertTrue(supervisor.ready)
        await supervisor.stop()
        self.assertTrue(supervisor.overlay.closed and supervisor.native.closed)

    async def test_failed_native_stop_retains_handles_and_retry_preserves_order(self):
        supervisor = self.supervisor()
        await supervisor.start()
        real_stop = supervisor.native.stop
        async def unavailable(*_):
            raise LifecycleError("CHILD_STOP_TIMEOUT")
        supervisor.native.stop = unavailable
        with self.assertRaisesRegex(LifecycleError, "CHILD_STOP_TIMEOUT"):
            await supervisor.stop()
        self.assertFalse(supervisor.ready)
        self.assertTrue(supervisor.native.alive and supervisor.overlay.alive)
        supervisor.native.stop = real_stop
        await supervisor.stop()
        self.assertTrue(supervisor.native.closed and supervisor.overlay.closed)
        self.assertEqual(supervisor.state, State.STOPPED)

    async def test_changed_index_address_namespace_and_missing_observation_revoke(self):
        for value in (None, OverlayObservation(1, 2, 4, "10.23.0.1"),
                      OverlayObservation(1, 2, 3, "10.23.0.2"), OverlayObservation(1, 9, 3, "10.23.0.1")):
            self.monitor.value = OverlayObservation(1, 2, 3, "10.23.0.1")
            self.service.path.unlink(missing_ok=True)
            supervisor = self.supervisor()
            await supervisor.start()
            self.monitor.value = value
            await asyncio.wait_for(supervisor.wait(), 3)
            self.assertEqual(supervisor.reason, "OVERLAY_OWNERSHIP_LOST")
            self.assertTrue(supervisor.native.closed and supervisor.overlay.closed)

    async def test_monitor_errors_are_sanitized_and_revocation_precedes_ordered_cleanup(self):
        supervisor = self.supervisor(IGNORE_TERM)
        await supervisor.start()
        order = []
        native_stop, overlay_stop = supervisor.native.stop, supervisor.overlay.stop
        async def stop_native(*args):
            self.assertFalse(supervisor.ready)
            self.assertTrue(supervisor.overlay.alive)
            order.append("native")
            await native_stop(*args)
        async def stop_overlay(*args):
            self.assertTrue(supervisor.native.closed)
            order.append("overlay")
            await overlay_stop(*args)
        supervisor.native.stop, supervisor.overlay.stop = stop_native, stop_overlay
        self.monitor.failure = LifecycleError("private-path-secret-canary")
        await asyncio.wait_for(supervisor.wait(), 3)
        self.assertEqual(order, ["native", "overlay"])
        self.assertEqual(supervisor.reason, "OVERLAY_MONITOR_FAILED")
        self.assertEqual(await supervisor.overlay.wait(), -signal.SIGKILL)

    async def test_startup_timeout_never_launches_native_and_cancellation_cleans_overlay(self):
        self.monitor.value = None
        self.limits = replace(self.limits, startup=.3)
        supervisor = self.supervisor()
        with self.assertRaisesRegex(LifecycleError, "STARTUP_TIMEOUT"):
            await supervisor.start()
        self.assertIsNone(supervisor.native)
        self.assertTrue(supervisor.overlay.closed)
        self.limits = replace(self.limits, startup=3)
        supervisor = self.supervisor()
        start = asyncio.create_task(supervisor.start())
        while supervisor.overlay is None:
            await asyncio.sleep(.01)
        start.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await asyncio.wait_for(start, 3)
        self.assertFalse(supervisor.ready)
        self.assertTrue(supervisor.overlay.closed)
        self.assertIsNone(supervisor.native)
        self.assertEqual(supervisor.reason, "SUPERVISOR_CANCELLED")

    async def test_observation_timeout_and_native_exit_fail_closed(self):
        supervisor = self.supervisor()
        await supervisor.start()
        async def stalled(_):
            await asyncio.sleep(30)
        self.monitor.observe = stalled
        await asyncio.wait_for(supervisor.wait(), 3)
        self.assertEqual(supervisor.reason, "OVERLAY_MONITOR_FAILED")
        self.monitor = SyntheticMonitor()
        self.service.path.unlink()
        supervisor = self.supervisor()
        await supervisor.start()
        signal.pidfd_send_signal(supervisor.native.pidfd, signal.SIGKILL)
        await asyncio.wait_for(supervisor.wait(), 3)
        self.assertEqual(supervisor.reason, "NATIVE_EXITED")

    async def test_late_observations_cannot_publish_readiness_after_overlay_death(self):
        for pause_overlay in (True, False):
            self.monitor = SyntheticMonitor()
            self.service = SyntheticTcpReadiness(self.directory / "synthetic-ready.json")
            entered, release = asyncio.Event(), asyncio.Event()
            async def delayed(_):
                entered.set()
                await release.wait()
                return self.monitor.value if pause_overlay else True
            if pause_overlay:
                self.monitor.observe = delayed
            else:
                self.service.observe = delayed
            supervisor = self.supervisor()
            start = asyncio.create_task(supervisor.start())
            await asyncio.wait_for(entered.wait(), 2)
            signal.pidfd_send_signal(supervisor.overlay.pidfd, signal.SIGKILL)
            async with asyncio.timeout(1):
                while supervisor.overlay.alive:
                    await asyncio.sleep(.001)
            release.set()
            with self.assertRaisesRegex(LifecycleError, "OVERLAY_EXITED"):
                await asyncio.wait_for(start, 2)
            self.assertFalse(supervisor.ready)
            self.assertTrue(supervisor.overlay.closed)
            if pause_overlay:
                self.assertIsNone(supervisor.native)
            else:
                self.assertTrue(supervisor.native.closed)

    async def test_exec_failure_is_reaped_and_clean_environment_pdeath_survives_exec(self):
        marker = self.directory / "flags.json"
        code = "import ctypes,json,os,time,sys; v=ctypes.c_int(); n=ctypes.c_int(); c=ctypes.CDLL(None); c.prctl(2,ctypes.byref(v),0,0,0); c.prctl(39,ctypes.byref(n),0,0,0); open(sys.argv[1],'w').write(json.dumps([v.value,c.prctl(39,0,0,0,0), 'FF_LIFECYCLE_CANARY' in os.environ])); time.sleep(60)"
        with patch.dict(os.environ, {"FF_LIFECYCLE_CANARY": "must-not-inherit"}):
            child = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", code, str(marker)))
        self.addAsyncCleanup(child.stop)
        async with asyncio.timeout(3):
            while not marker.exists():
                await asyncio.sleep(.01)
        self.assertEqual(json.loads(marker.read_text()), [signal.SIGKILL, 1, False])
        with patch("fifth_freedom_control.owned_process._BOOTSTRAP", "import os; os._exit(79)"):
            with self.assertRaisesRegex(LifecycleError, "CHILD_BOOTSTRAP_FAILED"):
                await OwnedChild.spawn(self.program, (), startup_timeout=.5)

    async def test_kernel_monitor_does_not_adopt_loopback_or_nonexistent_interface(self):
        with self.assertRaisesRegex(LifecycleError, "INTERFACE_INVALID"):
            KernelTunMonitor("lo", "10.23.0.1")
        monitor = KernelTunMonitor("ffsynabsent09", "10.23.0.1")
        await monitor.prepare()
        child = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", SLEEP))
        self.addAsyncCleanup(child.stop)
        self.assertIsNone(await monitor.observe(child))

    async def test_kernel_monitor_real_missing_link_ioctl_reports_only_fixed_stage(self):
        monitor = KernelTunMonitor("ffsynabsent09", "10.23.0.1")
        await monitor.prepare()
        child = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", SLEEP))
        self.addAsyncCleanup(child.stop)
        descriptors = len(os.listdir("/proc/self/fd"))
        # Only discovery is injected. The actual read-only kernel ioctl sees
        # the nonexistent interface and fails; no interface is created.
        with patch("socket.if_nametoindex", return_value=123):
            with self.assertRaisesRegex(LifecycleError, "^OVERLAY_LINK_QUERY_UNAVAILABLE$"):
                await monitor.observe(child)
        self.assertIsNone(monitor._index)
        self.assertTrue(child.image_matches())
        self.assertEqual(len(os.listdir("/proc/self/fd")), descriptors)

    async def test_kernel_monitor_syscall_stages_remain_fail_closed_and_close_fds(self):
        import contextlib
        import errno
        import fcntl
        import stat
        import struct
        from types import SimpleNamespace
        monitor = KernelTunMonitor("ffsynabsent09", "10.23.0.1")
        await monitor.prepare()
        child = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", SLEEP))
        self.addAsyncCleanup(child.stop)
        original_stat, original_open = os.stat, os.open
        descriptors = len(os.listdir("/proc/self/fd"))
        stages = ("NAMESPACE_OBSERVATION", "ADDRESS_QUERY", "FDINFO_DIRECTORY", "FDINFO_LIST",
                  "FDINFO_ENTRY", "FDINFO_READ", "TUN_DESCRIPTOR", "INTERFACE_RECHECK")
        for stage in stages:
            failure = PermissionError(errno.EACCES, "PRIVATE KERNEL ERROR SENTINEL")
            def kernel_stat(path, *args, **kwargs):
                if stage == "NAMESPACE_OBSERVATION" and path == "/proc/self/ns/net":
                    raise failure
                if isinstance(path, str) and path.startswith("fd/") and kwargs.get("dir_fd") == child.procfd:
                    if stage == "TUN_DESCRIPTOR":
                        raise failure
                    if stage == "INTERFACE_RECHECK":
                        return SimpleNamespace(st_mode=stat.S_IFCHR, st_rdev=os.makedev(10, 200))
                return original_stat(path, *args, **kwargs)
            def kernel_open(path, *args, **kwargs):
                if path == "fdinfo" and stage == "FDINFO_DIRECTORY":
                    raise failure
                return original_open(path, *args, **kwargs)
            def ioctl(_fd, request, _argument):
                # Explicit synthetic interface observations, never real TUN
                # readiness. Each case must fail before committing its index.
                value = bytearray(256)
                if request == 0x8913:
                    struct.pack_into("H", value, 16, 1)
                elif request == 0x8915:
                    if stage == "ADDRESS_QUERY":
                        raise failure
                    value[20:24] = socket.inet_aton(monitor.address)
                else:
                    raise AssertionError("unexpected ioctl")
                return bytes(value)
            with self.subTest(stage=stage), contextlib.ExitStack() as patches:
                patches.enter_context(patch("os.stat", side_effect=kernel_stat))
                patches.enter_context(patch("os.open", side_effect=kernel_open))
                patches.enter_context(patch.object(fcntl, "ioctl", side_effect=ioctl))
                patches.enter_context(patch("socket.if_nametoindex", side_effect=
                    [123, failure] if stage == "INTERFACE_RECHECK" else lambda _: 123))
                if stage == "FDINFO_LIST":
                    patches.enter_context(patch("os.listdir", side_effect=failure))
                elif stage == "FDINFO_ENTRY":
                    # A real procfs open of an absent entry represents the
                    # disappeared-descriptor case; it is not silently skipped.
                    patches.enter_context(patch("os.listdir", return_value=["999999999"]))
                elif stage == "FDINFO_READ":
                    patches.enter_context(patch("os.read", side_effect=failure))
                elif stage in ("TUN_DESCRIPTOR", "INTERFACE_RECHECK"):
                    patches.enter_context(patch("os.read", return_value=b"iff:\tffsynabsent09\n"))
                with self.assertRaises(LifecycleError) as raised:
                    await monitor.observe(child)
                self.assertEqual(str(raised.exception), "OVERLAY_" + stage + "_UNAVAILABLE")
            self.assertIsNone(monitor._index)
            self.assertTrue(child.image_matches())
            self.assertEqual(len(os.listdir("/proc/self/fd")), descriptors)

    async def test_pidfd_failure_withholds_release_and_reaps_bootstrap(self):
        marker = self.directory / "must-not-run"
        processes = []
        original_popen = subprocess.Popen
        def capture(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process
        with patch("fifth_freedom_control.owned_process.subprocess.Popen", side_effect=capture), \
                patch("fifth_freedom_control.owned_process.os.pidfd_open", side_effect=PermissionError()):
            with self.assertRaisesRegex(LifecycleError, "CHILD_START_FAILED"):
                await OwnedChild.spawn(self.program, ("-I", "-S", "-c",
                    "from pathlib import Path; import sys; Path(sys.argv[1]).touch()", str(marker)))
        self.assertFalse(marker.exists())
        self.assertEqual(len(processes), 1)
        self.assertIsNotNone(processes[0].returncode)

    async def test_proc_open_failure_and_exited_signal_race_close_pidfd_and_reap(self):
        processes, descriptors = [], []
        original_popen, original_open, original_pidfd = subprocess.Popen, os.open, os.pidfd_open
        def capture(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process
        def pidfd(*args, **kwargs):
            descriptor = original_pidfd(*args, **kwargs)
            descriptors.append(descriptor)
            return descriptor
        def opened(path, *args, **kwargs):
            if isinstance(path, str) and path.startswith("/proc/"):
                raise PermissionError()
            return original_open(path, *args, **kwargs)
        with patch("fifth_freedom_control.owned_process.subprocess.Popen", side_effect=capture), \
                patch("fifth_freedom_control.owned_process.os.pidfd_open", side_effect=pidfd), \
                patch("fifth_freedom_control.owned_process.os.open", side_effect=opened), \
                patch("fifth_freedom_control.owned_process.signal.pidfd_send_signal", side_effect=ProcessLookupError()):
            with self.assertRaisesRegex(LifecycleError, "CHILD_START_FAILED"):
                await OwnedChild.spawn(self.program, ("-I", "-S", "-c", SLEEP))
        self.assertEqual(len(descriptors), 1)
        with self.assertRaises(OSError):
            os.fstat(descriptors[0])
        self.assertIsNotNone(processes[0].returncode)

    async def test_bootstrap_exec_failure_and_start_cancellation_leave_no_child(self):
        import fifth_freedom_control.owned_process as module
        processes = []
        original_popen = subprocess.Popen
        def capture(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process
        with image(shutil.which("sleep")) as sleep:
            invalid_exec = module._BOOTSTRAP.replace("os.execve(image, argv,", "os.execve(-1, argv,")
            with patch.object(module, "_BOOTSTRAP", invalid_exec), \
                    patch.object(module.subprocess, "Popen", side_effect=capture):
                with self.assertRaisesRegex(LifecycleError, "CHILD_EXEC_FAILED"):
                    await OwnedChild.spawn(sleep, ("60",))
        paused = module._BOOTSTRAP.replace("os.write(ready, b'A')", "import time; time.sleep(5); os.write(ready, b'A')")
        with patch.object(module, "_BOOTSTRAP", paused), \
                patch.object(module.subprocess, "Popen", side_effect=capture):
            task = asyncio.create_task(OwnedChild.spawn(self.program, ("-I", "-S", "-c", SLEEP)))
            await asyncio.sleep(.03)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await asyncio.wait_for(task, 2)
        self.assertEqual(len(processes), 2)
        self.assertTrue(all(process.returncode is not None for process in processes))

    async def test_parent_death_kills_exact_child_and_closes_live_connection(self):
        report = self.directory / "orphan-proof.json"
        script = """
import asyncio,hashlib,sys
from pathlib import Path
sys.path.insert(0,sys.argv[1])
from fifth_freedom_control.owned_process import OwnedChild,VerifiedExecutable
async def main():
    p=Path(sys.argv[4])
    with VerifiedExecutable(p,hashlib.sha256(p.read_bytes()).hexdigest(),p.stat().st_size) as image:
        child=await OwnedChild.spawn(image,('-I','-S','-c',sys.argv[3],sys.argv[2]))
        await child.wait()
asyncio.run(main())
"""
        parent = subprocess.Popen([sys.executable, "-I", "-S", "-c", script,
            str(ROOT / "src/public"), str(report), NATIVE, str(self.program.path)], stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, close_fds=True,
            env={"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8"})
        parentfd = os.pidfd_open(parent.pid)
        childfd = None
        writer = None
        try:
            async with asyncio.timeout(5):
                while not report.exists():
                    await asyncio.sleep(.01)
            data = json.loads(report.read_text())
            childfd = os.pidfd_open(data["pid"])
            reader, writer = await asyncio.open_connection("127.0.0.1", data["port"])
            writer.write(b"parent-death")
            await writer.drain()
            self.assertEqual(await reader.readexactly(12), b"parent-death")
            signal.pidfd_send_signal(parentfd, signal.SIGKILL)
            await asyncio.to_thread(parent.wait, timeout=3)
            async with asyncio.timeout(3):
                while not select.select([childfd], [], [], 0)[0]:
                    await asyncio.sleep(.01)
            self.assertEqual(await asyncio.wait_for(reader.read(1), 1), b"")
            with self.assertRaises(OSError):
                await asyncio.open_connection("127.0.0.1", data["port"])
        finally:
            for descriptor in (childfd, parentfd):
                if descriptor is not None:
                    if not select.select([descriptor], [], [], 0)[0]:
                        signal.pidfd_send_signal(descriptor, signal.SIGKILL)
                    os.close(descriptor)
            await asyncio.to_thread(parent.wait, timeout=3)
            if writer:
                writer.close()
                await writer.wait_closed()


if __name__ == "__main__":
    unittest.main()
