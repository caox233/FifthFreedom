"""Portable input guards and Linux owned-interpreter cleanup fault injections.

Synthetic package bytes are never executed; no native service/network is started.
"""
import copy
from contextlib import redirect_stderr
import io
import json
import os
from pathlib import Path
import signal
import shutil
import subprocess
import sys
import tempfile
import textwrap
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
try:
    import ci_native_lifecycle as probe
finally:
    sys.path.pop(0)

COMMIT = 'a' * 40


def fixture(root):
    package = root / 'candidate'
    package.mkdir()
    files = {}
    for name in ('bin/FifthFreedom.Server', 'tools/verify_public_package',
                 'package/PUBLIC-SERVER-CATALOG.json', 'client/package/RELEASE-CATALOG.json',
                 'client/FifthFreedom.exe', 'client/runtime/uplay_r1_loader.dll',
                 'client/runtime/scbl_gameplay.dll', 'client/runtime/FifthFreedom.Server.exe'):
        path = package / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'SYNTHETIC INERT METADATA TEST; NEVER EXECUTABLE\n' + name.encode())
        files[name] = probe.candidate.sha_file(path)
    identity = {'schema': 1, 'result': 'verified', 'platform': 'linux-x86_64',
                'service': {'executable_sha256': files['bin/FifthFreedom.Server']['sha256'],
                            'manifest_sha256': files['package/PUBLIC-SERVER-CATALOG.json']['sha256']},
                'admitted_client': {'storage_key': 'SYNTHETIC ONLY'}}
    receipt = {'schema': 1, 'kind': 'public-server-component-candidate', 'source_commit': COMMIT,
               'run_id': '123', 'run_attempt': '1', 'scope': dict(probe.candidate.SCOPE),
               'status': 'native_dual_identity_verified', 'files': files,
               'native_verification': identity, 'expected_native_result': copy.deepcopy(identity)}
    (package / 'CANDIDATE-RECEIPT.json').write_text(json.dumps(receipt))
    (package / 'SHA256SUMS.txt').write_text('synthetic metadata only\n')
    return package, receipt


@unittest.skipUnless(shutil.which("git"), "Git executable required")
class CheckoutOwnershipTests(unittest.TestCase):
    def test_exact_command_trusts_foreign_owner_but_still_rejects_dirty_or_wrong_revision(self):
        with tempfile.TemporaryDirectory(prefix="ff-synthetic-foreign-checkout-") as directory:
            root = Path(directory)
            global_config = root / "empty-global-git-config"
            global_config.write_bytes(b"")
            environment = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(global_config))
            git = ["git", "-C", str(root)]
            subprocess.run([*git, "init", "--quiet"], check=True, env=environment,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            tracked = root / "tracked.txt"; tracked.write_text("synthetic checkout\n")
            subprocess.run([*git, "add", "tracked.txt"], check=True, env=environment,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            subprocess.run([*git, "-c", "user.name=Synthetic Test", "-c", "user.email=test@example.invalid",
                "commit", "--quiet", "-m", "synthetic ownership test"], check=True, env=environment,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            expected = subprocess.check_output([*git, "rev-parse", "HEAD"], text=True, env=environment).strip()
            environment["GIT_TEST_ASSUME_DIFFERENT_OWNER"] = "1"
            denied = subprocess.run([*git, "rev-parse", "HEAD"], env=environment, capture_output=True)
            self.assertEqual(denied.returncode, 128)
            self.assertIn(b"dubious ownership", denied.stderr)
            with mock.patch.object(probe, "ROOT", root), mock.patch.dict(os.environ, environment, clear=True):
                probe.verify_checkout(expected)
                with self.assertRaisesRegex(probe.ProbeError, "CHECKOUT_REVISION_MISMATCH"):
                    probe.verify_checkout("0" * 40)
                tracked.write_text("changed synthetic checkout\n")
                with self.assertRaisesRegex(probe.ProbeError, "CHECKOUT_TRACKED_SOURCE_DIRTY"):
                    probe.verify_checkout(expected)
            self.assertEqual(global_config.read_bytes(), b"")


class NativeLifecycleInputTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='ff-lifecycle-input-tests-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.package, self.receipt = fixture(self.root)

    def validate(self, receipt=None):
        return probe.validate_input(self.package, receipt or self.receipt, COMMIT, '123', '1')

    def test_same_run_metadata_and_copy_preserve_every_original_byte(self):
        rows = self.validate()
        original = {name: (self.package / name).read_bytes() for name in rows}
        destination = self.root / 'owned-copy'
        probe.copy_private_package(self.package, destination, rows)
        for name, expected in rows.items():
            self.assertEqual(probe.candidate.sha_file(destination / name), expected)
            self.assertEqual((self.package / name).read_bytes(), original[name])
        self.assertEqual(len(rows), 10)

    def test_run_commit_scope_and_verification_mismatch_reject_before_copy(self):
        for field, value in (('source_commit', 'b' * 40), ('run_id', '124'), ('run_attempt', '2'),
                             ('status', 'assembled_pending_native_verification'), ('schema', True),
                             ('scope', {**probe.candidate.SCOPE, 'complete_windows_client': True}),
                             ('expected_native_result', {})):
            with self.subTest(field=field):
                receipt = copy.deepcopy(self.receipt)
                receipt[field] = value
                with self.assertRaises(probe.ProbeError):
                    self.validate(receipt)
        self.assertEqual(sorted(path.name for path in self.root.iterdir()), ['candidate'])

    def test_missing_extra_and_changed_files_fail(self):
        path = self.package / 'client/runtime/scbl_gameplay.dll'
        original = path.read_bytes()
        path.write_bytes(b'changed synthetic data')
        with self.assertRaisesRegex(probe.ProbeError, 'CANDIDATE_BYTES_CHANGED'):
            self.validate()
        path.write_bytes(original)
        (self.package / 'unexpected-private-data').write_bytes(b'not admitted')
        with self.assertRaisesRegex(probe.ProbeError, 'CANDIDATE_EXTRA_FILES'):
            self.validate()
        (self.package / 'unexpected-private-data').unlink()
        receipt = copy.deepcopy(self.receipt)
        del receipt['files']['client/runtime/scbl_gameplay.dll']
        with self.assertRaisesRegex(probe.ProbeError, 'CANDIDATE_FILES_INVALID'):
            self.validate(receipt)

    def test_receipt_paths_cannot_escape_or_alias_owned_copy(self):
        for name in ('/absolute', '../outside', 'client/../outside', 'client//alias', './alias', 'windows\\alias'):
            with self.subTest(name=name), self.assertRaisesRegex(probe.ProbeError, 'CANDIDATE_PATH_INVALID'):
                probe.safe_relative(name)
        receipt = copy.deepcopy(self.receipt)
        receipt['files']['../outside'] = next(iter(receipt['files'].values()))
        with self.assertRaisesRegex(probe.ProbeError, 'CANDIDATE_PATH_INVALID'):
            self.validate(receipt)
        self.assertFalse((self.root / 'outside').exists())

    def test_digest_sizes_and_service_identity_are_not_trusted_metadata(self):
        for row in ({'bytes': True, 'sha256': 'a' * 64}, {'bytes': 0, 'sha256': 'a' * 64},
                    {'bytes': 10, 'sha256': 'A' * 64}, {'bytes': 10, 'sha256': 'a' * 64, 'extra': 1}):
            receipt = copy.deepcopy(self.receipt)
            receipt['files']['bin/FifthFreedom.Server'] = row
            with self.assertRaisesRegex(probe.ProbeError, 'CANDIDATE_METADATA_INVALID'):
                self.validate(receipt)
        receipt = copy.deepcopy(self.receipt)
        receipt['native_verification']['service']['executable_sha256'] = 'b' * 64
        receipt['expected_native_result'] = copy.deepcopy(receipt['native_verification'])
        with self.assertRaisesRegex(probe.ProbeError, 'CANDIDATE_NATIVE_IDENTITY_INVALID'):
            self.validate(receipt)

    def test_private_files_never_overwrite_existing_evidence(self):
        target = self.root / 'unique-evidence'
        target.write_bytes(b'original')
        with self.assertRaises(FileExistsError):
            probe.private_file(target, b'replacement')
        self.assertEqual(target.read_bytes(), b'original')

    def test_only_sanitized_receipt_is_uploader_readable_without_overwriting(self):
        target, private = self.root / 'receipt.json', self.root / 'private-input'
        document = {'schema': 1, 'scope': dict(probe.SCOPE)}
        previous = os.umask(0o077)
        try:
            probe.write_report(target, document)
            probe.private_file(private, b'SYNTHETIC PRIVATE INPUT')
        finally:
            os.umask(previous)
        self.assertEqual(json.loads(target.read_bytes()), document)
        if os.name == 'posix':
            self.assertEqual(target.stat().st_mode & 0o777, 0o644)
            self.assertEqual(private.stat().st_mode & 0o777, 0o600)
        original = target.read_bytes()
        with self.assertRaises(FileExistsError):
            probe.write_report(target, {'overwritten': True})
        self.assertEqual(target.read_bytes(), original)
        missing_parent = self.root / 'not-created-by-runner'
        errors = io.StringIO()
        with mock.patch.object(probe, 'run') as run, redirect_stderr(errors):
            code = probe.dispatch(['--package', str(self.package), '--candidate-receipt', str(target),
                '--runner-temp', str(self.root), '--report', str(missing_parent / 'receipt.json'),
                '--commit', COMMIT, '--run-id', '123', '--run-attempt', '1'])
        self.assertEqual(code, 1)
        self.assertEqual(errors.getvalue(), 'REPORT_DIRECTORY_REQUIRED\n')
        self.assertFalse(missing_parent.exists())
        run.assert_not_called()

    def test_missing_namespace_privileges_fail_before_any_temporary_or_copy_work(self):
        with mock.patch.object(probe.isolation, 'preflight', side_effect=probe.isolation.IsolationError('NET_ADMIN_AND_SYS_ADMIN_REQUIRED')), \
                mock.patch.object(probe.tempfile, 'TemporaryDirectory') as temporary, \
                mock.patch.object(probe, 'copy_private_package') as copied:
            with self.assertRaisesRegex(probe.isolation.IsolationError, 'NET_ADMIN_AND_SYS_ADMIN_REQUIRED'):
                probe.run(self.package, self.package / 'CANDIDATE-RECEIPT.json', self.root, COMMIT, '123', '1')
            temporary.assert_not_called()
            copied.assert_not_called()

    def test_scope_and_case_contract_cannot_imply_encrypted_production_acceptance(self):
        self.assertEqual(probe.CASES, ('withheld', 'denied', 'eof', 'expiry', 'parent_death'))
        self.assertEqual(probe.PORTS, {'tcp': (80, 8000, 50051), 'udp': (21126, 21127)})
        self.assertTrue(probe.SCOPE['synthetic_runtime_bindings'])
        self.assertTrue(probe.SCOPE['controlled_native_startup_test'])
        for name in ('encrypted_overlay_verified', 'production_provider_open', 'game_executed',
                     'complete_windows_client', 'deployed', 'published'):
            self.assertIs(probe.SCOPE[name], False)


@unittest.skipUnless(sys.platform == 'linux', 'Linux pidfds, subreaper and signals')
class NativeLifecycleCleanupTests(unittest.TestCase):
    """Actual owned interpreter children, never native services or namespaces."""

    def isolated(self, body, *arguments):
        code = ('import sys\nfrom pathlib import Path\n'
                f'sys.path.insert(0, {str(ROOT / "scripts")!r})\n'
                'import ci_native_lifecycle as probe\n' + textwrap.dedent(body))
        result = subprocess.run([sys.executable, '-I', '-S', '-c', code, *map(str, arguments)],
                                capture_output=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        return result

    def test_image_mismatch_retains_exact_handle_and_reaps_adopted_child(self):
        self.isolated('''
            import os, subprocess, tempfile
            from types import SimpleNamespace
            child_code = 'import time; time.sleep(60)'
            worker_code = ('import subprocess, sys, time, json; '
                'child = subprocess.Popen([sys.executable, "-I", "-S", "-c", ' + repr(child_code) + ']); '
                'print(json.dumps({"event": "STARTED", "pid": child.pid}), flush=True); time.sleep(60)')
            class Namespaces:
                topology = SimpleNamespace(protected_ip='10.203.42.1', names=('owned-test',))
                def execute(self, name, arguments):
                    return [sys.executable, '-I', '-S', '-c', worker_code]
            observed = []
            real_validate = probe.validate_native_image
            def validate(pid, fd, image):
                observed.append(pid)
                real_validate(pid, fd, image)
            probe.validate_native_image = validate
            sentinel = subprocess.Popen([sys.executable, '-I', '-S', '-c', child_code])
            try:
                with tempfile.TemporaryDirectory() as temporary, probe.Subreaper():
                    root = Path(temporary)
                    package = root / 'package'
                    (package / 'bin').mkdir(parents=True)
                    (package / 'bin/FifthFreedom.Server').write_bytes(b'INERT EXPECTED IMAGE MISMATCH')
                    private = root / 'private'
                    private.mkdir()
                    try:
                        probe.execute_case(Namespaces(), package, private, 'eof')
                    except probe.ProbeError as error:
                        assert str(error) == 'NATIVE_IMAGE_CHANGED', str(error)
                    else:
                        raise AssertionError('image mismatch accepted')
                    assert len(observed) == 1
                    assert not Path('/proc/' + str(observed[0])).exists(), 'owned child leaked'
                    try:
                        os.waitpid(observed[0], os.WNOHANG)
                    except ChildProcessError:
                        pass
                    else:
                        raise AssertionError('owned child not reaped')
                    assert sentinel.poll() is None, 'unrelated child was signalled'
                    assert not list((private / 'data').iterdir())
            finally:
                sentinel.terminate()
                sentinel.wait(timeout=3)
        ''')

    def test_unconfirmed_parent_never_signals_unrelated_child_or_leaks_pidfd(self):
        self.isolated('''
            import os, subprocess
            from types import SimpleNamespace
            child = subprocess.Popen([sys.executable, '-I', '-S', '-c', 'import time; time.sleep(60)'])
            try:
                before = len(os.listdir('/proc/self/fd'))
                try:
                    probe.pin_native(child.pid, SimpleNamespace(pid=child.pid))
                except probe.ProbeError as error:
                    assert str(error) == 'NATIVE_PARENT_CHANGED'
                else:
                    raise AssertionError('unrelated child accepted')
                assert len(os.listdir('/proc/self/fd')) == before
                assert child.poll() is None
            finally:
                child.terminate()
                child.wait(timeout=3)
        ''')

    def test_repeated_sigterm_cannot_interrupt_finally_or_write_success_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            code = ('import sys\nfrom pathlib import Path\n'
                    f'sys.path.insert(0, {str(ROOT / "scripts")!r})\n'
                    'import ci_native_lifecycle as probe\n' + textwrap.dedent('''
                import signal, tempfile, time
                root = Path(sys.argv[1])
                def controlled_run(*arguments):
                    with tempfile.TemporaryDirectory(dir=root) as private:
                        (Path(private) / 'private-key').write_bytes(b'SYNTHETIC TEST ONLY')
                        try:
                            probe.emit('RUNNING')
                            signal.pause()
                        finally:
                            probe.emit('CLEANING')
                            # The parent sends repeated SIGTERM in this interval.
                            time.sleep(.5)
                            (root / 'finally-completed').write_bytes(b'yes')
                probe.run = controlled_run
                code = probe.main(['--package', str(root / 'package'),
                    '--candidate-receipt', str(root / 'input'), '--runner-temp', str(root),
                    '--report', str(root / 'report.json'), '--commit', 'a' * 40,
                    '--run-id', '123', '--run-attempt', '1'])
                assert code == 1
                assert sorted(path.name for path in root.iterdir()) == ['finally-completed']
                assert signal.getsignal(signal.SIGTERM) == signal.SIG_DFL
                probe.emit('CLEANED')
            '''))
            child = subprocess.Popen([sys.executable, '-I', '-S', '-c', code, str(root)],
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
            try:
                self.assertEqual(probe.read_event(child, 'RUNNING', 5), {'event': 'RUNNING'})
                child.send_signal(signal.SIGTERM)
                self.assertEqual(probe.read_event(child, 'CLEANING', 5), {'event': 'CLEANING'})
                child.send_signal(signal.SIGTERM)
                child.send_signal(signal.SIGTERM)
                self.assertEqual(probe.read_event(child, 'CLEANED', 5), {'event': 'CLEANED'})
                self.assertEqual(child.wait(timeout=3), 0)
                self.assertEqual(child.stderr.read(), b'NATIVE_LIFECYCLE_INTERRUPTED\n')
                self.assertFalse((root / 'report.json').exists())
            finally:
                if child.poll() is None:
                    child.kill()
                child.wait(timeout=3)
                child.stdout.close()
                child.stderr.close()


if __name__ == '__main__':
    unittest.main()
