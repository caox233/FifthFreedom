"""Runner refusal controls; inert strings/PE metadata are never executed."""
import io
import json
from pathlib import Path
import struct
import sys
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import ci_native_production_replay as replay


def summary(passed, failed, filtered=0):
    return f"test result: {'FAILED' if failed else 'ok'}. {passed} passed; {failed} failed; 0 ignored; 0 measured; {filtered} filtered out; finished in 0.01s\n"


class TestExecutionResults(unittest.TestCase):
    def camera(self, baseline=False):
        names = sorted(replay.Counter(replay.CAMERA_EXPECTED_FAILURES).elements()) if baseline else []
        marker = ''.join('STICKY_CALLBACK_FAIL ' + name + '\n' for name in names)
        marker += f'STICKY_CALLBACK_RESULT checks={replay.CALLBACK_CHECKS} failures={len(names)} native_audio=synthetic game=false\n'
        failed_name = 'failures:\n    ' + replay.CAMERA_TEST + '\n' if baseline else ''
        output = marker + failed_name + summary(0 if baseline else 1, int(baseline), 1300)
        output += failed_name + summary(9 if baseline else 10, int(baseline), 1291)
        return output, 'STICKY_CALLBACK_EXPECTATION_FAILED' if baseline else ''

    def test_camera_accepts_exact_executed_four_roles(self):
        for phase in ('baseline', 'modified', 'rollback', 'reapplied'):
            old = phase in ('baseline', 'rollback')
            out, err = self.camera(old)
            result = replay.inspect_result('sticky-camera', phase, 101 if old else 0, out, err)
            self.assertEqual(result['callback_checks'], replay.CALLBACK_CHECKS)

    def test_baseline_compile_failure_crash_or_missing_assertion_is_never_evidence(self):
        out, err = self.camera(True)
        for code, stdout, stderr in ((1, out, err), (101, 'error[E0308]: compile failed', err),
                                     (0, out, err), (-1073741819, out, err), (101, out, '')):
            with self.subTest(code=code, out=stdout[:30]), self.assertRaises(ValueError):
                replay.inspect_result('sticky-camera', 'baseline', code, stdout, stderr)

    def test_camera_rejects_extra_missing_and_wrong_child_summaries(self):
        out, err = self.camera(True)
        for changed in (out + summary(9, 1), out.replace(summary(0, 1, 1300), ''),
                        out.replace(summary(0, 1, 1300), summary(1, 0, 1300))):
            with self.subTest(out=changed[:40]), self.assertRaises(ValueError):
                replay.inspect_result('sticky-camera', 'baseline', 101, changed, err)

    def test_reduced_callback_coverage_wrong_name_or_unknown_failure_is_rejected(self):
        out, err = self.camera(True)
        changes = [out.replace(f'checks={replay.CALLBACK_CHECKS}', 'checks=1'),
                   out.replace(replay.CAMERA_TEST, 'sticky_camera_sound::unexpected_failure'),
                   out.replace('cancel_destroy_blocks_new_start', 'unknown_callback_case'),
                   out.replace('cancel_destroy_blocks_new_start', 'cancel_replacement_blocks_new_start')]
        for changed in changes:
            with self.subTest(out=changed[:40]), self.assertRaises(ValueError):
                replay.inspect_result('sticky-camera', 'baseline', 101, changed, err)

    def test_modified_failure_skip_and_duplicate_callback_summary_are_rejected(self):
        out, err = self.camera(False)
        for changed in (out.replace('0 ignored', '1 ignored'), out.replace('failures=0', 'failures=1'),
                        out + f'STICKY_CALLBACK_RESULT checks={replay.CALLBACK_CHECKS} failures=0 native_audio=synthetic game=false\n'):
            with self.subTest(out=changed[:40]), self.assertRaises(ValueError):
                replay.inspect_result('sticky-camera', 'modified', 0, changed, err)

    def progression(self):
        names = ('progression_region_single_owned_allocation_accepts_all_writable_regions',
                 'progression_region_invalid_tail_or_unrelated_allocation_preserves_original')
        out = ''.join(f'test progression::region_tests::{name} ... FAILED\n' for name in names) + summary(0, 2)
        return out, 'PROGRESSION_VALID_SAME_ALLOCATION_SPAN\nPROGRESSION_INVALID_REPLACEMENT variant=5'

    def test_progression_requires_two_exact_real_failures_and_successful_reapply(self):
        out, err = self.progression()
        self.assertEqual(replay.inspect_result('progression', 'baseline', 101, out, err)['failed'], 2)
        self.assertEqual(replay.inspect_result('progression', 'reapplied', 0, summary(2, 0), '')['passed'], 2)
        for changed, stderr in ((out.replace('single_owned_allocation', 'unrelated_case'), err),
                                (out, 'PROGRESSION_VALID_SAME_ALLOCATION_SPAN'), (out + summary(0, 2), err),
                                (out, err.replace('variant=5', 'variant=0'))):
            with self.subTest(out=changed[:40]), self.assertRaises(ValueError):
                replay.inspect_result('progression', 'rollback', 101, changed, stderr)


class TestCargoIdentity(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='ff-inert-replay-metadata-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.target = self.root / 'target'
        self.target.mkdir()
        self.exe = self.target / 'scbl_gameplay-012abc.exe'
        fake = bytearray(256)
        fake[:2] = b'MZ'
        struct.pack_into('<I', fake, 0x3c, 0x80)
        fake[0x80:0x84] = b'PE\0\0'
        struct.pack_into('<H', fake, 0x84, 0x14c)
        self.exe.write_bytes(fake)

    def rows(self):
        return [{'reason': 'compiler-artifact', 'target': {'name': 'scbl_gameplay'}, 'profile': {'test': True}, 'executable': str(self.exe)},
                {'reason': 'build-finished', 'success': True}]

    def parse(self, rows):
        return replay.select_executable('\n'.join(json.dumps(r) for r in rows), self.target)

    def test_only_this_successful_cargo_invocation_selects_the_i386_test_binary(self):
        self.assertEqual(self.parse(self.rows()), self.exe)
        rows = self.rows()
        for changed in (rows[:1], rows[1:], rows + rows[:1], rows + rows[1:],
                        [rows[0], {'reason': 'build-finished', 'success': False}]):
            with self.subTest(rows=changed), self.assertRaises(ValueError):
                self.parse(changed)

    def test_non_test_wrong_target_outside_path_and_wrong_architecture_are_rejected(self):
        for key, value in [('profile', {'test': False}), ('target', {'name': 'other'}), ('executable', 'relative.exe'),
                           ('executable', str(self.root / self.exe.name)), ('target', [])]:
            rows = self.rows()
            rows[0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.parse(rows)
        data = bytearray(self.exe.read_bytes())
        struct.pack_into('<H', data, 0x84, 0x8664)
        self.exe.write_bytes(data)
        with self.assertRaises(ValueError):
            self.parse(self.rows())

    def test_malformed_json_and_truncated_pe_are_rejected(self):
        for raw in ('private invalid text', '[]', 'null'):
            with self.subTest(raw=raw), self.assertRaisesRegex(ValueError, '^PRODUCTION_REPLAY_CARGO_ARTIFACT_INVALID$'):
                replay.select_executable(raw, self.target)
        self.exe.write_bytes(b'NOT PE')
        with self.assertRaises(ValueError):
            self.parse(self.rows())

    def test_archive_refuses_symlinks_and_path_escape_before_extraction(self):
        archive = io.BytesIO()
        content = b'owned, inert source fixture'
        with tarfile.open(fileobj=archive, mode='w') as tar:
            entry = tarfile.TarInfo('src/example/source.rs')
            entry.size = len(content)
            tar.addfile(entry, io.BytesIO(content))
        replay.extract_checkout(archive.getvalue(), self.root / 'valid-checkout')
        self.assertEqual((self.root / 'valid-checkout/src/example/source.rs').read_bytes(), content)
        for name, kind in (('../escaped', tarfile.REGTYPE), ('/absolute', tarfile.REGTYPE),
                           ('\\rooted', tarfile.REGTYPE), ('C:/absolute', tarfile.REGTYPE),
                           ('C:relative', tarfile.REGTYPE), ('//server/share', tarfile.REGTYPE),
                           ('folder\\..\\escaped', tarfile.REGTYPE), ('file:stream', tarfile.REGTYPE),
                           ('link', tarfile.SYMTYPE)):
            archive = io.BytesIO()
            with tarfile.open(fileobj=archive, mode='w') as tar:
                entry = tarfile.TarInfo(name)
                entry.type = kind
                tar.addfile(entry, io.BytesIO())
            with self.subTest(name=name), self.assertRaises(ValueError):
                replay.extract_checkout(archive.getvalue(), self.root / 'checkout')

class TestSharedAddressInstrumentation(unittest.TestCase):
    def sample(self, name):
        spec = replay.SHARED_ADDRESS_TRANSLATION[name]
        return (spec['word_import'] + '\n' + '\n'.join(
            'let leaf = std::mem::transmute(' + literal + ');' for literal in spec['leaf_literals'])
            + '\nconst UNRELATED_PIN: u32 = 0x02ab095c;\n').encode('utf-8')

    def test_same_exact_address_seam_and_reverse_applies_to_both_source_roles(self):
        for name in replay.SHARED_ADDRESS_TRANSLATION:
            raw = self.sample(name)
            base, before = replay.instrument_source(name, raw, True)
            modified, after = replay.instrument_source(name, raw, False)
            self.assertEqual(before['shared_address_replacements'], after['shared_address_replacements'])
            self.assertEqual(before['canonical_production_sha256'], replay.sha(raw))
            self.assertEqual(after['canonical_production_sha256'], replay.sha(raw))
            for compiled, record, baseline in ((base, before, True), (modified, after, False)):
                restored = compiled.decode('utf-8')
                self.assertTrue(restored.endswith(replay.TRANSLATED_LEAF_HELPER))
                restored = restored[:-len(replay.TRANSLATED_LEAF_HELPER)]
                for change in reversed(record['shared_address_replacements']):
                    self.assertEqual(restored.count(change['to']), 1)
                    restored = restored.replace(change['to'], change['from'])
                expected = raw + replay.INSTRUMENTATION[name].encode() if baseline else raw
                self.assertEqual(restored.encode(), expected)
                self.assertIn(b'const UNRELATED_PIN: u32 = 0x02ab095c;', compiled)
                self.assertEqual(record['compiled_test_source_sha256'], replay.sha(compiled))

    def test_unknown_duplicated_or_preinstrumented_address_sites_fail_closed(self):
        for name, spec in replay.SHARED_ADDRESS_TRANSLATION.items():
            raw = self.sample(name)
            changes = (raw.replace(spec['word_import'].encode(), b'use unexpected::word;'),
                       raw + ('\n' + spec['word_import']).encode(),
                       raw.replace(spec['leaf_literals'][0].encode(), b'0xdeadbeefusize'))
            compiled, _ = replay.instrument_source(name, raw, False)
            for changed in (*changes, compiled):
                with self.subTest(name=name), self.assertRaisesRegex(ValueError, 'ADDRESS_SEAM'):
                    replay.instrument_source(name, changed, False)

    def test_actual_current_sources_preserve_non_test_imports_and_progression_bytes(self):
        for name in replay.INSTRUMENTATION:
            raw = (ROOT / name).read_bytes()
            compiled, record = replay.instrument_source(name, raw, False)
            self.assertEqual(record['canonical_production_sha256'], replay.sha(raw))
            if name in replay.SHARED_ADDRESS_TRANSLATION:
                self.assertIn(b'#[cfg(not(test))]\nuse crate::equipment_native::word;', compiled)
                self.assertIn(b'#[cfg(not(test))]\nfn lifecycle_replay_address(address:usize)->usize {address}', compiled)
            else:
                self.assertEqual(compiled, raw)
                self.assertEqual(record['shared_address_replacements'], [])


if __name__ == '__main__':
    unittest.main()
