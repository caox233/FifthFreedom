"""Linux exact-child execution primitives for the gated public supervisor.

No PID search, process-group signal, shell, inherited environment or raw child
logging. The tiny isolated Python bootstrap arms parent-death protection before
the parent releases it to execute the already verified ELF descriptor.
"""
import asyncio
from dataclasses import dataclass, field
from enum import Enum
import errno
import hashlib
import os
from pathlib import Path
import re
import select
import signal
import socket
import stat
import subprocess
import sys


class LifecycleError(RuntimeError):
    """Only fixed implementation reason codes may reach callers."""


def _identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


@dataclass(frozen=True)
class ImageIdentity:
    device: int
    inode: int
    size: int
    sha256: str = field(repr=False)


class VerifiedExecutable:
    def __init__(self, path, sha256, size):
        self.fd = -1
        if not isinstance(path, (str, Path)):
            raise LifecycleError("EXECUTABLE_PATH_INVALID")
        self.path = Path(path)
        self.identity = None
        if (sys.platform != "linux" or not hasattr(os, "pidfd_open") or not hasattr(signal, "pidfd_send_signal")
                or os.execve not in os.supports_fd):
            raise LifecycleError("OWNED_PROCESS_UNSUPPORTED")
        if (not isinstance(sha256, str) or re.fullmatch(r"[0-9a-f]{64}", sha256) is None
                or type(size) is not int or not 64 <= size <= 512 * 1024 * 1024):
            raise LifecycleError("EXECUTABLE_IDENTITY_INVALID")
        raw = str(path)
        if not raw.startswith("/") or any(value in ("", ".", "..") for value in raw.split("/")[1:]):
            raise LifecycleError("EXECUTABLE_PATH_INVALID")
        directories = []
        try:
            directory = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
            directories.append(directory)
            for component in raw.split("/")[1:-1]:
                directory = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=directory)
                directories.append(directory)
                info = os.fstat(directory)
                sticky_root = info.st_uid == 0 and bool(info.st_mode & stat.S_ISVTX)
                if info.st_uid not in (0, os.geteuid()) or info.st_mode & 0o022 and not sticky_root:
                    raise LifecycleError("EXECUTABLE_PATH_INVALID")
            descriptor = os.open(raw.split("/")[-1], os.O_RDONLY | os.O_NONBLOCK | os.O_CLOEXEC | os.O_NOFOLLOW, dir_fd=directory)
            self.fd = descriptor
            before = os.fstat(descriptor)
            if (not stat.S_ISREG(before.st_mode) or before.st_uid not in (0, os.geteuid())
                    or before.st_nlink != 1 or before.st_mode & 0o6022 or not before.st_mode & 0o111
                    or before.st_size != size):
                raise LifecycleError("EXECUTABLE_FILE_INVALID")
            try:
                os.getxattr(descriptor, "security.capability")
            except OSError as error:
                if error.errno != errno.ENODATA:
                    raise LifecycleError("EXECUTABLE_CAPABILITIES_UNVERIFIED") from None
            else:
                # A credential-changing exec can clear PR_SET_PDEATHSIG.
                raise LifecycleError("EXECUTABLE_CAPABILITIES_FORBIDDEN")
            digest = hashlib.sha256()
            header = os.pread(descriptor, 64, 0)
            if (header[:7] != b"\x7fELF\x02\x01\x01" or header[16:18] not in (b"\x02\x00", b"\x03\x00")
                    or header[18:20] != b"\x3e\x00"):
                raise LifecycleError("EXECUTABLE_ELF_INVALID")
            offset = 0
            while offset < size:
                block = os.pread(descriptor, min(1024 * 1024, size - offset), offset)
                if not block:
                    raise LifecycleError("EXECUTABLE_CHANGED")
                digest.update(block)
                offset += len(block)
            if _identity(before) != _identity(os.fstat(descriptor)) or digest.hexdigest() != sha256:
                raise LifecycleError("EXECUTABLE_HASH_MISMATCH")
            self.identity = ImageIdentity(before.st_dev, before.st_ino, size, sha256)
            self._metadata = _identity(before)
        except (OSError, ValueError, TypeError):
            self.close()
            raise LifecycleError("EXECUTABLE_UNAVAILABLE") from None
        except BaseException:
            self.close()
            raise
        finally:
            for descriptor in reversed(directories):
                os.close(descriptor)

    def check_unchanged(self):
        try:
            if self.fd < 0 or _identity(os.fstat(self.fd)) != self._metadata:
                raise LifecycleError("EXECUTABLE_CHANGED")
        except OSError:
            raise LifecycleError("EXECUTABLE_CHANGED") from None

    def close(self):
        if self.fd >= 0:
            os.close(self.fd)
            self.fd = -1

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


# This code is a process-launch barrier, not a new native/Public activation
# protocol. No preexec_fn is used in the potentially multithreaded supervisor.
_BOOTSTRAP = r'''
import ctypes, hashlib, os, select, signal, sys
try:
    parent, image, ready, release, expected_size = map(int, sys.argv[1:6])
    expected_hash, argv = sys.argv[6], sys.argv[7:]
    libc = ctypes.CDLL(None, use_errno=True)
    libc.prctl.argtypes = [ctypes.c_int] + [ctypes.c_ulong] * 4
    libc.prctl.restype = ctypes.c_int
    if libc.prctl(38, 1, 0, 0, 0) != 0: os._exit(71)
    if libc.prctl(1, signal.SIGKILL, 0, 0, 0) != 0: os._exit(72)
    if os.getppid() != parent: os._exit(73)
    digest = hashlib.sha256()
    offset = 0
    before = os.fstat(image)
    while offset < expected_size:
        block = os.pread(image, min(1048576, expected_size-offset), offset)
        if not block: os._exit(74)
        digest.update(block)
        offset += len(block)
    after = os.fstat(image)
    fields = ('st_dev','st_ino','st_size','st_mode','st_uid','st_gid','st_nlink','st_mtime_ns','st_ctime_ns')
    if before.st_size != expected_size or any(getattr(before,k)!=getattr(after,k) for k in fields): os._exit(74)
    if digest.hexdigest() != expected_hash: os._exit(74)
    os.set_inheritable(ready, False)
    os.set_inheritable(image, False)
    os.write(ready, b'A')
    if not select.select([release], [], [], 10)[0] or os.read(release,1) != b'X': os._exit(75)
    os.close(release)
    if os.getppid() != parent: os._exit(73)
    if any(getattr(before,k)!=getattr(os.fstat(image),k) for k in fields): os._exit(74)
    os.execve(image, argv, {'PATH':'/usr/bin:/bin','LANG':'C.UTF-8','TZ':'UTC'})
except BaseException:
    os._exit(79)
'''


async def _pipe_byte(descriptor, timeout):
    loop = asyncio.get_running_loop()
    future = loop.create_future()
    os.set_blocking(descriptor, False)
    def readable():
        if future.done():
            return
        try:
            future.set_result(os.read(descriptor, 1))
        except BlockingIOError:
            pass
        except OSError:
            future.set_exception(LifecycleError("CHILD_HANDSHAKE_FAILED"))
    loop.add_reader(descriptor, readable)
    try:
        return await asyncio.wait_for(future, timeout)
    finally:
        loop.remove_reader(descriptor)


class OwnedChild:
    """A single exact child and pidfd; never adopt an existing PID or process."""
    def __init__(self, process, pidfd, procfd, identity):
        self._process, self.pidfd, self.procfd = process, pidfd, procfd
        self.image = identity
        self._wait_task = None
        self.closed = False
        self._stop_lock = asyncio.Lock()

    @classmethod
    async def spawn(cls, program, arguments, *, startup_timeout=10.0, lease_socket=None):
        return await cls._spawn(program, arguments, startup_timeout=startup_timeout,
                                lease_socket=lease_socket)

    @classmethod
    async def _spawn(cls, program, arguments, *, startup_timeout, lease_socket=None,
                     _query_output=False, _query_failure=None, _query_owned=None):
        if _query_output and (lease_socket is not None or cls is not OwnedQueryChild):
            raise LifecycleError("CHILD_SPEC_INVALID")
        if not isinstance(program, VerifiedExecutable) or not 0.1 <= startup_timeout <= 30:
            raise LifecycleError("CHILD_SPEC_INVALID")
        if (not isinstance(arguments, tuple) or len(arguments) > 32
                or any(not isinstance(value, str) or len(value) > 4096 or "\x00" in value for value in arguments)):
            raise LifecycleError("CHILD_SPEC_INVALID")
        program.check_unchanged()
        inherited = ()
        if lease_socket is not None:
            # Only one explicitly connected local owner-lease endpoint can be
            # inherited. This is not a general pass_fds/configuration surface.
            try:
                import struct
                if (not isinstance(lease_socket, socket.socket) or lease_socket.fileno() < 3
                        or lease_socket.family != socket.AF_UNIX
                        or lease_socket.getsockopt(socket.SOL_SOCKET, socket.SO_TYPE) != socket.SOCK_SEQPACKET
                        or lease_socket.getsockopt(socket.SOL_SOCKET, socket.SO_PASSCRED) != 1
                        or struct.unpack("3i", lease_socket.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
                        != (os.getpid(), os.geteuid(), os.getegid())):
                    raise LifecycleError("CHILD_LEASE_SOCKET_INVALID")
                lease_socket.getpeername()
                inherited = (lease_socket.fileno(),)
            except (OSError, ValueError, AttributeError):
                raise LifecycleError("CHILD_LEASE_SOCKET_INVALID") from None
        deadline = asyncio.get_running_loop().time() + startup_timeout
        ready_read = ready_write = release_read = release_write = -1
        child = None
        process = None
        try:
            ready_read, ready_write = os.pipe2(os.O_CLOEXEC)
            release_read, release_write = os.pipe2(os.O_CLOEXEC)
            process = subprocess.Popen([sys.executable, "-I", "-S", "-c", _BOOTSTRAP,
                str(os.getpid()), str(program.fd), str(ready_write), str(release_read), str(program.identity.size),
                program.identity.sha256, str(program.path), *arguments],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE if _query_output else subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                env={"PATH": "/usr/bin:/bin", "LANG": "C.UTF-8", "TZ": "UTC"},
                close_fds=True, pass_fds=(program.fd, ready_write, release_read, *inherited))
            # Do not poll/wait/reap before pinning the PID. The bootstrap cannot
            # exec the target until this exact ownership handle has been acquired.
            pidfd = os.pidfd_open(process.pid)
            try:
                procfd = os.open(f"/proc/{process.pid}", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
            except BaseException:
                try:
                    signal.pidfd_send_signal(pidfd, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                finally:
                    os.close(pidfd)
                raise
            child = cls(process, pidfd, procfd, program.identity)
            if _query_owned is not None:
                # Transfer cleanup ownership before the first await; this is
                # not a declaration that exec/readiness has succeeded.
                _query_owned(child)
            os.close(ready_write)
            ready_write = -1
            os.close(release_read)
            release_read = -1
            if await _pipe_byte(ready_read, max(0.001, deadline - asyncio.get_running_loop().time())) != b"A":
                raise LifecycleError("CHILD_BOOTSTRAP_FAILED")
            if not child.alive:
                raise LifecycleError("CHILD_EXITED_DURING_START")
            os.write(release_write, b"X")
            # CLOEXEC on ready_write gives EOF only at image transition/exit.
            if await _pipe_byte(ready_read, max(0.001, deadline - asyncio.get_running_loop().time())) != b"":
                raise LifecycleError("CHILD_EXEC_FAILED")
            if _query_output:
                # Only the separate short-lived query type permits completion
                # before the parent's post-exec /proc observation. Its verified
                # bootstrap acknowledged the hash, was explicitly released, and
                # closed its CLOEXEC status pipe. Every bootstrap failure exits
                # nonzero (71..79); exit0 here is the verified target completing,
                # not a fallback for an unobserved or failed bootstrap.
                child._exec_confirmed = True
                observation = child.query_image_state()
                if observation in (QueryImageState.MATCH, QueryImageState.EXIT_PENDING):
                    # Linux can remove the leader's exe link before the entire
                    # thread group's pidfd becomes readable. Pending is not an
                    # image match or successful query: only the bounded reader
                    # may finish, after exact group exit0 and output checks.
                    pass
                elif observation is QueryImageState.EXITED and await child.wait() == 0:
                    program.check_unchanged()
                else:
                    raise LifecycleError("CHILD_EXEC_FAILED")
            elif not child.image_matches():
                raise LifecycleError("CHILD_EXEC_FAILED")
            return child
        except BaseException as error:
            if _query_failure is not None:
                try:
                    _query_failure()
                except BaseException:
                    pass
            if child is not None:
                cleanup = asyncio.create_task(child.stop(0.2, 2.0))
                try:
                    await asyncio.shield(cleanup)
                except asyncio.CancelledError:
                    # A second cancellation during bootstrap unwind must not
                    # orphan the pinned child before ownership can be returned.
                    await asyncio.shield(cleanup)
            elif process is not None:
                # No exec release occurred. EOF wakes the bounded bootstrap; no
                # PID signal is needed or permitted without an acquired pidfd.
                os.close(release_write)
                release_write = -1
                try:
                    await asyncio.to_thread(process.wait, timeout=12)
                except subprocess.TimeoutExpired:
                    raise LifecycleError("CHILD_BOOTSTRAP_CLEANUP_FAILED") from None
                finally:
                    if process.stdout is not None:
                        process.stdout.close()
            if isinstance(error, asyncio.CancelledError):
                raise
            if isinstance(error, LifecycleError):
                raise error from None
            raise LifecycleError("CHILD_START_FAILED") from None
        finally:
            for descriptor in (ready_read, ready_write, release_read, release_write):
                if descriptor >= 0:
                    os.close(descriptor)

    @property
    def pid(self):
        return self._process.pid

    @property
    def alive(self):
        return not self.closed and not select.select([self.pidfd], [], [], 0)[0]

    def image_matches(self):
        if not self.alive:
            return False
        try:
            info = os.stat("exe", dir_fd=self.procfd)
            matches = (info.st_dev, info.st_ino, info.st_size) == (self.image.device, self.image.inode, self.image.size)
        except OSError:
            return False
        return matches and self.alive

    async def wait(self):
        if self._wait_task is None:
            self._wait_task = asyncio.create_task(asyncio.to_thread(self._process.wait))
        return await asyncio.shield(self._wait_task)

    async def stop(self, terminate_timeout=1.0, kill_timeout=2.0):
        if not (0 < terminate_timeout <= 10 and 0 < kill_timeout <= 10):
            raise LifecycleError("CHILD_STOP_BUDGET_INVALID")
        async with self._stop_lock:
            await self._stop(terminate_timeout, kill_timeout)

    async def _stop(self, terminate_timeout, kill_timeout):
        if self.closed:
            return
        for sig, timeout in ((signal.SIGTERM, terminate_timeout), (signal.SIGKILL, kill_timeout)):
            if self.alive:
                try:
                    signal.pidfd_send_signal(self.pidfd, sig)
                except ProcessLookupError:
                    pass
                except OSError:
                    raise LifecycleError("CHILD_SIGNAL_FAILED") from None
            try:
                await asyncio.wait_for(self.wait(), timeout)
                break
            except TimeoutError:
                continue
        else:
            raise LifecycleError("CHILD_STOP_TIMEOUT")
        os.close(self.procfd)
        os.close(self.pidfd)
        if self._process.stdout is not None:
            self._process.stdout.close()
        self.closed = True


class QueryImageState(Enum):
    MATCH = "match"
    MISMATCH = "mismatch"
    EXIT_PENDING = "exit_pending"
    EXITED = "exited"
    UNAVAILABLE = "unavailable"


class OwnedQueryChild(OwnedChild):
    """Internal short-lived CLI handle; never a native/core activation child.

    Used only by the bounded observation reader. No stdin, stderr or lease FD is
    captured/inherited. Ordinary OwnedChild.spawn retains its live-image rule.
    """
    def __init__(self, process, pidfd, procfd, identity):
        super().__init__(process, pidfd, procfd, identity)
        self._exec_confirmed = False
        self._image_failure = None
        self._exit_pending = False

    def query_image_state(self):
        """Keep observed identity failure distinct from exact child completion.

        EXITED is only a pidfd observation after the verified exec barrier, not
        success. The caller must still drain bounded stdout and require exit0.
        An ENOENT before pidfd readiness is an unresolved exit transition;
        it never establishes a live-image match or permission to publish output.
        Mismatch and other procfs failures cannot be cleared by a later exit.
        """
        if self._image_failure is not None:
            return self._image_failure
        if self.closed or not self._exec_confirmed:
            self._image_failure = QueryImageState.UNAVAILABLE
            return self._image_failure
        if not self.alive:
            return QueryImageState.EXITED
        try:
            info = os.stat("exe", dir_fd=self.procfd)
        except OSError as error:
            # exit_mm removes exe before exit_notify signals the pidfd. A
            # thread-group leader can also finish while other threads remain.
            # Retain that uncertainty; do not label a still-live group EXITED.
            if error.errno == errno.ENOENT:
                self._exit_pending = True
                return QueryImageState.EXIT_PENDING if self.alive else QueryImageState.EXITED
            self._image_failure = QueryImageState.UNAVAILABLE
            return self._image_failure
        if (info.st_dev, info.st_ino, info.st_size) != (self.image.device, self.image.inode, self.image.size):
            self._image_failure = QueryImageState.MISMATCH
            return self._image_failure
        if self._exit_pending:
            # A reappearing live image does not resolve the earlier absence as
            # exact completion. Never revive this attempt, even for the same ELF.
            self._image_failure = QueryImageState.UNAVAILABLE
            return self._image_failure
        return QueryImageState.MATCH if self.alive else QueryImageState.EXITED

    @classmethod
    async def spawn(cls, program, arguments, *, on_failure, on_owned, startup_timeout=2.0):
        if (type(startup_timeout) not in (int, float) or not 0.1 <= startup_timeout <= 2
                or not callable(on_failure) or not callable(on_owned)):
            raise LifecycleError("QUERY_BUDGET_INVALID")
        return await cls._spawn(program, arguments, startup_timeout=startup_timeout,
                                _query_output=True, _query_failure=on_failure, _query_owned=on_owned)

    @property
    def output_fd(self):
        if self.closed or self._process.stdout is None:
            raise LifecycleError("QUERY_OUTPUT_UNAVAILABLE")
        return self._process.stdout.fileno()
