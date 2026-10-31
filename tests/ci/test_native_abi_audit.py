"""Source-inventory failure controls; these tests do not execute native code."""
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import audit_native_abi as audit


class NativeAbiInventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = self.root / 'src/native/client/hooks/example.rs'
        self.path.parent.mkdir(parents=True)
        self.git('init', '-q')
        self.git('config', 'user.name', 'ABI source fixture')
        self.git('config', 'user.email', 'fixture@example.invalid')
        self.write('static_detour! { static Hook: unsafe extern "thiscall" fn(usize, u32); }')
        self.git('add', '.')
        self.git('commit', '-qm', 'Synthetic source fixture')

    def git(self, *args):
        return subprocess.check_output(['git', *args], cwd=self.root).decode('utf-8').strip()

    def write(self, value):
        self.path.write_text(value, encoding='utf-8')

    def test_comments_and_literals_do_not_create_hooks_but_cfg_is_not_resolved(self):
        fake = 'static_detour! { static Fake: unsafe extern "stdcall" fn(u32); }'
        self.write('// 中文注释 ' + fake + '\n/* nested /* ' + fake + ' */ */\n'
                   + 'const TEXT: &str = r#"' + fake + '"#;\n'
                   + '#[cfg(any())] static_detour! { static Present: unsafe extern "thiscall" fn(usize, u32); }')
        result = audit.inventory(self.root)
        self.assertEqual([row['name'] for row in result['declarations']], ['Present'])

    def test_immutable_revision_does_not_read_or_overwrite_changed_working_file(self):
        original = self.path.read_bytes()
        self.write('not a detour declaration')
        changed = self.path.read_bytes()
        result = audit.inventory(self.root, 'HEAD')
        self.assertEqual(self.path.read_bytes(), changed)
        self.assertEqual(result['source_basis'], 'immutable_git_tree')
        self.assertEqual(result['source_sha256'][self.path.relative_to(self.root).as_posix()], hashlib.sha256(original).hexdigest())
        self.assertEqual([row['name'] for row in result['declarations']], ['Hook'])

    def test_unrecognized_macro_or_signature_fails_instead_of_silently_omitting(self):
        for declaration in ('static_detour! { static Callback: unsafe extern "C" fn(extern "C" fn()); }',
                            'static_detour! (static Hook: unsafe extern "C" fn(););',
                            'static_detour! { static Hook: unsafe extern "C" fn({}); }'):
            self.write(declaration)
            with self.subTest(declaration=declaration), self.assertRaises(ValueError):
                audit.inventory(self.root)

    def test_aggregate_and_fat_pointer_layouts_are_not_assigned_stack_cleanup(self):
        for arguments, returned in ((['usize', 'Aggregate'], '()'), (['usize', '&str'], '()'),
                                    (['usize', '*const [u8]'], '()'), (['usize'], 'Aggregate')):
            result = audit.layout('thiscall', arguments, returned)
            with self.subTest(arguments=arguments, returned=returned):
                self.assertNotIn('callee_pop_bytes', result)
                self.assertNotEqual(result['status'], 'declared_i686_msvc_scalar_layout_only')

    def test_unsupported_register_assignments_are_not_guessed(self):
        for convention, arguments in (('fastcall', ['usize', 'u64', 'u32']), ('thiscall', ['f32', 'u32'])):
            with self.subTest(convention=convention, arguments=arguments):
                self.assertNotIn('callee_pop_bytes', audit.layout(convention, arguments, '()'))


if __name__ == '__main__':
    unittest.main()
