"""Actual owned Unix seqpacket/SCM credentials; synthetic child, never a game."""
import asyncio
from dataclasses import asdict, replace
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "src/public"))
try:
    from fifth_freedom_control import native_lease as lease_module
    from fifth_freedom_control.native_lease import LeaseBinding, NativeOwnerLease
    from fifth_freedom_control.owned_process import LifecycleError, OwnedChild, VerifiedExecutable
    from fifth_freedom_control.overlay_supervisor import (ChildSpec, LifecycleLimits, OverlayObservation,
                                                         OwnedSupervisor, State)
finally:
    sys.path.pop(0)

SUPPORTED = sys.platform == "linux" and hasattr(os, "pidfd_open") and hasattr(signal, "pidfd_send_signal")
BINDING = LeaseBinding("a" * 64, "b" * 64, "synthetic-client-identity", "c" * 64, "d" * 64,
                       1, 2, 3, "10.23.0.1", 24)
# This is intentionally an independent small wire peer, not the production
# decoder/serializer. No keys, real account data or authorization are fixtures.
CHILD = r'''
import array,json,os,socket,sys,time
peer=socket.socket(fileno=int(sys.argv[1]));peer.settimeout(3)
binding=json.loads(sys.argv[2]);mode=sys.argv[3]
if mode=='slow_preflight':time.sleep(3.1)
frame={'version':'ff-public-owner-lease-v1','kind':'hello','nonce':'e'*64,'sequence':0,'binding':binding}
if mode=='wrong_binding':frame['binding']['interface_index']+=1
if mode=='ready_first':frame['kind']='ready';frame['sequence']=1
if mode=='lease_field':frame['lease_ms']=None
if mode=='bool_sequence':frame['sequence']=False
data=json.dumps(frame).encode()
if mode=='duplicate':data=data[:-1]+b',"kind":"hello"}'
if mode=='oversized':data+=b' '*4096
if mode=='grandchild':
    child=os.fork()
    if child==0:
        peer.send(data);os._exit(0)
    os.waitpid(child,0);time.sleep(5);os._exit(0)
if mode=='rights':
    fd=os.open('/dev/null',os.O_RDONLY)
    peer.sendmsg([data],[(socket.SOL_SOCKET,socket.SCM_RIGHTS,array.array('i',[fd]))]);os.close(fd)
else:peer.send(data)
grant=json.loads(peer.recv(4096))
assert grant['kind']=='admit' and grant['sequence']==1 and grant['binding']==binding
frame['kind']='ready';frame['sequence']=1
if mode=='wrong_nonce':frame['nonce']='f'*64
if mode=='no_ready':
    time.sleep(5);os._exit(0)
peer.send(json.dumps(frame).encode())
if mode=='duplicate_ready':peer.send(json.dumps(frame).encode())
last=1
while True:
    data=peer.recv(4096)
    if not data:break
    renew=json.loads(data)
    assert renew['kind']=='renew' and renew['sequence']==last+1 and renew['nonce']=='e'*64
    assert renew['binding']==binding and 100<=renew['lease_ms']<=5000
    last=renew['sequence']
if mode=='five_renewals':assert last==6
'''


class LeaseClock:
    """Parent lease time only; asyncio and the synthetic process keep real time."""
    def __init__(self):
        self.value = 100.0

    def monotonic(self):
        return self.value

    def advance(self, seconds):
        self.value += seconds


class BindingTests(unittest.TestCase):
    def test_rejects_invalid_domains_types_and_secretlike_control_content(self):
        for change in ({"overlay_ipv4": "127.0.0.1"}, {"overlay_prefix": 7}, {"overlay_ipv4": "10.23.0.0"},
                       {"overlay_prefix": 32}, {"overlay_ipv4": "192.168.1.1", "overlay_prefix": 15},
                       {"interface_index": True}, {"netns_inode": 0}, {"runtime_profile_sha256": "A" * 64},
                       {"admitted_client_storage_key": "bad\ninput"}):
            with self.subTest(change=change), self.assertRaisesRegex(LifecycleError, "^PUBLIC_OWNER_LEASE_INVALID$"):
                replace(BINDING, **change)


@unittest.skipUnless(SUPPORTED, "Linux pidfd and SCM credentials required")
class NativeLeaseTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="ff-parent-lease-")
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        executable = self.directory / "synthetic-python"
        shutil.copyfile(Path(sys.executable).resolve(), executable)
        executable.chmod(0o700)
        config = self.directory / "pyvenv.cfg"
        config.write_text(f"home = {Path(sys.executable).resolve().parent}\ninclude-system-site-packages = false\n")
        config.chmod(0o600)
        self.program = VerifiedExecutable(executable, hashlib.sha256(executable.read_bytes()).hexdigest(),
                                          executable.stat().st_size)
        self.addCleanup(self.program.close)
        self.current = BINDING

    def owner(self, **kwargs):
        owner = NativeOwnerLease(lambda: self.current, **kwargs)
        self.addCleanup(owner.revoke)
        return owner

    def arguments(self, owner, mode="normal"):
        return ("-I", "-S", "-c", CHILD, str(owner.child_socket.fileno()), json.dumps(asdict(BINDING)), mode)

    async def child(self, owner, mode="normal"):
        child = await OwnedChild.spawn(self.program, self.arguments(owner, mode), lease_socket=owner.child_socket)
        self.addAsyncCleanup(child.stop)
        return child

    async def ready(self, owner, child):
        # This bounds real IPC/process coordination, not the granted lease TTL.
        # The two timing-contract tests control only the parent's lease clock;
        # they do not certify a native peer surviving delayed READY in real time.
        async with asyncio.timeout(10):
            while not await owner.observe(child):
                await asyncio.sleep(.01)

    async def test_real_child_hello_ready_renewal_and_endpoint_close(self):
        clock = LeaseClock()
        # Replace this module's binding, never stdlib time.monotonic: changing
        # the latter would also invalidate asyncio's real I/O/cleanup watchdogs.
        with patch.object(lease_module, "time", SimpleNamespace(monotonic=clock.monotonic)):
            owner = self.owner(lease_ms=400, renew_seconds=.05)
            child = await self.child(owner, "five_renewals")
            await self.ready(owner, child)
            self.assertEqual(owner._sequence, 1)
            for sequence in range(2, 7):
                clock.advance(.06)
                self.assertTrue(await owner.observe(child))
                self.assertEqual(owner._sequence, sequence)
                await asyncio.sleep(0)
            owner.revoke()
            # Exit0 also requires the independent wire peer to consume and
            # validate all five sequential renewals before endpoint EOF.
            self.assertEqual(await asyncio.wait_for(child.wait(), 2), 0)
            with self.assertRaisesRegex(LifecycleError, "^PUBLIC_OWNER_LEASE_INVALID$"):
                await owner.observe(child)

    async def test_wrong_schema_binding_nonce_sequence_and_duplicate_frames_close(self):
        for mode in ("wrong_binding", "ready_first", "lease_field", "bool_sequence", "duplicate", "oversized",
                     "wrong_nonce", "duplicate_ready", "grandchild"):
            with self.subTest(mode=mode):
                owner = self.owner()
                child = await self.child(owner, mode)
                async with asyncio.timeout(2):
                    with self.assertRaisesRegex(LifecycleError, "^PUBLIC_OWNER_LEASE_INVALID$"):
                        while True:
                            await owner.observe(child)
                            await asyncio.sleep(.01)
                self.assertTrue(owner._revoked)
                await child.stop()

    async def test_received_rights_are_rejected_and_closed_without_fd_leak(self):
        owner = self.owner()
        child = await self.child(owner, "rights")
        owner.child_socket.close()
        before = len(os.listdir("/proc/self/fd"))
        async with asyncio.timeout(2):
            with self.assertRaisesRegex(LifecycleError, "^PUBLIC_OWNER_LEASE_INVALID$"):
                while True:
                    await owner.observe(child)
                    await asyncio.sleep(.01)
        # The parent socket closes on revoke; received /dev/null is not leaked.
        self.assertLessEqual(len(os.listdir("/proc/self/fd")), before - 1)

    async def test_stalled_observation_cannot_reanimate_expired_lease(self):
        clock = LeaseClock()
        with patch.object(lease_module, "time", SimpleNamespace(monotonic=clock.monotonic)):
            owner = self.owner(lease_ms=100, renew_seconds=.03)
            child = await self.child(owner)
            await self.ready(owner, child)
            self.assertEqual(owner._sequence, 1)
            clock.advance(.1)
            # At the actual granted boundary, a fresh observation cannot renew
            # or reanimate the lease. No real wall sleep stands in for expiry.
            self.assertEqual(clock.monotonic(), owner._expires)
            with self.assertRaisesRegex(LifecycleError, "^PUBLIC_OWNER_LEASE_INVALID$"):
                await owner.observe(child)
            self.assertEqual(owner._sequence, 1)
            self.assertTrue(owner._revoked)
            self.assertFalse(owner._ready)
            self.assertEqual(await asyncio.wait_for(child.wait(), 2), 0)

    async def test_admission_without_listener_ready_does_not_publish_readiness(self):
        owner = self.owner(lease_ms=400, renew_seconds=.05)
        child = await self.child(owner, "no_ready")
        for _ in range(12):
            self.assertFalse(await owner.observe(child))
            await asyncio.sleep(.02)
        self.assertIsNotNone(owner._nonce)
        self.assertFalse(owner._ready)
        owner.revoke()
        self.assertFalse(owner._ready)

    async def test_preflight_budget_is_distinct_from_native_post_hello_deadline(self):
        owner = self.owner(hello_timeout=5)
        child = await self.child(owner, "slow_preflight")
        async with asyncio.timeout(5):
            while not await owner.observe(child):
                await asyncio.sleep(.02)
        owner.revoke()
        self.assertEqual(await asyncio.wait_for(child.wait(), 2), 0)
        owner = self.owner(hello_timeout=.1)
        child = await self.child(owner, "slow_preflight")
        async with asyncio.timeout(1):
            with self.assertRaisesRegex(LifecycleError, "^PUBLIC_OWNER_LEASE_INVALID$"):
                while True:
                    await owner.observe(child)
                    await asyncio.sleep(.02)
        owner.revoke()
        self.assertFalse(owner._ready)

    async def test_binding_change_and_different_owned_child_irreversibly_revoke(self):
        for different in (False, True):
            self.current = BINDING
            owner = self.owner()
            child = await self.child(owner)
            await self.ready(owner, child)
            if different:
                other = await OwnedChild.spawn(self.program, ("-I", "-S", "-c", "import time;time.sleep(20)"))
                self.addAsyncCleanup(other.stop)
                selected = other
            else:
                self.current = replace(BINDING, interface_index=4)
                selected = child
            with self.assertRaisesRegex(LifecycleError, "^PUBLIC_OWNER_LEASE_INVALID$"):
                await owner.observe(selected)
            self.assertEqual(await asyncio.wait_for(child.wait(), 2), 0)
            if different:
                self.assertTrue(other.alive)
                await other.stop()

    async def test_unconnected_or_wrong_socket_type_cannot_be_inherited(self):
        for family, kind in ((socket.AF_UNIX, socket.SOCK_STREAM), (socket.AF_INET, socket.SOCK_DGRAM)):
            with socket.socket(family, kind) as endpoint:
                with self.assertRaisesRegex(LifecycleError, "^CHILD_LEASE_SOCKET_INVALID$"):
                    await OwnedChild.spawn(self.program, ("-I", "-S", "-c", "pass"), lease_socket=endpoint)

    async def test_supervisor_closes_lease_before_cleanup_and_overlay_loss_revokes(self):
        current = self
        class Monitor:
            async def prepare(self):
                pass
            async def observe(self, child):
                value = current.current
                return OverlayObservation(value.netns_device, value.netns_inode, value.interface_index, value.overlay_ipv4)
        owner = self.owner(lease_ms=400, renew_seconds=.05)
        supervisor = OwnedSupervisor(
            ChildSpec(self.program, ("-I", "-S", "-c", "import time;time.sleep(20)")),
            ChildSpec(self.program, self.arguments(owner), owner.child_socket), Monitor(), owner,
            LifecycleLimits(startup=3, observation=.2, interval=.02, terminate=.1, kill=1))
        self.addAsyncCleanup(supervisor.stop)
        await supervisor.start()
        self.assertTrue(supervisor.ready)
        stop_native = supervisor.native.stop
        async def checked_stop(*args):
            self.assertTrue(owner._revoked)
            self.assertFalse(supervisor.ready)
            self.assertTrue(supervisor.overlay.alive)
            await stop_native(*args)
        supervisor.native.stop = checked_stop
        self.current = replace(BINDING, interface_index=4)
        await asyncio.wait_for(supervisor.wait(), 3)
        self.assertEqual(supervisor.reason, "OVERLAY_OWNERSHIP_LOST")
        self.assertEqual(supervisor.state, State.STOPPED)
        self.assertTrue(supervisor.native.closed and supervisor.overlay.closed)


if __name__ == "__main__":
    unittest.main()
