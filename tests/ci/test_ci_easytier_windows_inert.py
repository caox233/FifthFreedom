"""Real patch composition and bounded compiler-result controls; no core executes."""
import copy
import difflib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import ci_easytier_windows_inert as build


class Fixture:
    def __init__(self, root):
        self.work, self.repository = root / 'work', root / 'repository'
        self.repository.mkdir()
        for folder in ('easytier-source', 'tun-source'):
            (self.work / folder).mkdir(parents=True)
        tree = self.work / 'easytier-source'
        fragments, rows = [], []
        for name in sorted(build.FILES):
            before = b'' if name.endswith('.toml') else b'// original inert contract fixture\n'
            after = b'// derived inert contract fixture\n'
            if before:
                path = tree / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(before)
            fragments.extend(difflib.unified_diff(before.decode().splitlines(True), after.decode().splitlines(True),
                fromfile='a/' + name if before else '/dev/null', tofile='b/' + name))
            rows.append({'path': name, 'original_bytes': len(before) if before else None,
                'original_sha256': build.source.sha(before) if before else None,
                'patched_bytes': len(after), 'patched_sha256': build.source.sha(after)})
        (tree / 'Cargo.lock').write_bytes(b'# inert synthetic lock\n')
        (tree / 'unrelated.rs').write_bytes(b'// retain unrelated\n')
        (self.work / 'tun-source/lib.rs').write_bytes(b'// retain paired backend\n')
        data = ''.join(fragments).encode()
        (self.repository / 'inert.patch').write_bytes(data)
        pin = {'path': 'inert.patch', 'bytes': len(data), 'sha256': build.source.sha(data), 'role': 'production-plus-contracts'}
        inventory = {'easytier': build.paired.inventory(tree), 'tun': build.paired.inventory(self.work / 'tun-source')}
        patches = [{'synthetic_original_order': n} for n in range(12)]
        self.prepared = {'source_files': inventory, 'source_tree_sha256': build.source.sha(build.source.encoded(inventory)),
            'patches': patches, 'patch_manifest_sha256': build.source.sha(build.source.encoded(patches)),
            'cargo_lock_patched_sha256': inventory['easytier']['Cargo.lock']['sha256'], 'runtime_executed': False}
        expected = copy.deepcopy(inventory)
        for row in rows:
            expected['easytier'][row['path']] = {'bytes': row['patched_bytes'], 'sha256': row['patched_sha256']}
        self.previous = {'expected_prepared': {key: self.prepared[key] for key in
            ('source_tree_sha256','patch_manifest_sha256','cargo_lock_patched_sha256')}}
        profile = next(row for row in rows if row['path'].endswith('.toml'))
        self.lock = {'schema': 1, 'kind': build.KIND, 'base_recipe': {}, 'inert_contract': {}, 'patch': pin, 'files': rows,
            'inert_profile': {'source_path': profile['path'], 'bytes': profile['patched_bytes'], 'sha256': profile['patched_sha256']},
            'contracts': {'filter': 'fifth_freedom_stun_', 'expected_passed': 13},
            'build': {'rust_toolchain': '1.95.0', 'target': 'x86_64-pc-windows-msvc', 'features': ['ff-windows-session-observer']},
            'expected_prepared': {'source_tree_sha256': build.source.sha(build.source.encoded(expected)),
                'patch_manifest_sha256': build.source.sha(build.source.encoded([*patches,pin])),
                'cargo_lock_patched_sha256': self.prepared['cargo_lock_patched_sha256']}}

    def derive(self):
        with patch.object(build, 'load_inputs', return_value=(self.previous, {}, {})):
            return build.derive(self.work, self.prepared, self.lock, self.repository)


class InertBuildTests(unittest.TestCase):
    def test_exact_reviewed_recipe_profile_and_thirteen_patch_identity(self):
        lock = json.loads(build.LOCK.read_bytes())
        previous, _, _ = build.load_inputs(lock)
        self.assertNotEqual(previous['expected_prepared']['source_tree_sha256'], lock['expected_prepared']['source_tree_sha256'])
        self.assertEqual(lock['inert_profile']['ipv4'], '10.254.253.1/24')
        self.assertFalse(lock['build']['execute_binaries'])
        self.assertFalse(lock['admission']['public_startup_allowed'])

    def test_old_identity_altered_patch_features_or_execute_flag_cannot_reuse_recipe(self):
        original = json.loads(build.LOCK.read_bytes())
        for path, value in ((('expected_prepared','source_tree_sha256'),original['base_prepared']['source_tree_sha256']),
            (('patch','sha256'),'0'*64), (('build','execute_binaries'),True), (('build','features'),['faketcp']),
            (('contracts','expected_passed'),12), (('inert_profile','ipv4'),'10.254.253.1/32')):
            row = copy.deepcopy(original)
            row[path[0]][path[1]] = value
            with self.assertRaises((build.paired.BuildError,build.source.PreparationError)):
                build.load_inputs(row)

    def test_real_patch_appends_once_preserves_every_unrelated_input(self):
        with tempfile.TemporaryDirectory() as folder:
            f = Fixture(Path(folder)); result = f.derive()
            self.assertEqual(len(result['patches']),13)
            self.assertEqual(result['patches'][:12],f.prepared['patches'])
            self.assertEqual(result['source_files']['tun'],f.prepared['source_files']['tun'])
            self.assertEqual(result['source_files']['easytier']['unrelated.rs'],f.prepared['source_files']['easytier']['unrelated.rs'])
            build.check_prepared(f.work,result,f.lock)
            with patch.object(build,'load_inputs',return_value=(f.previous,{},{})):
                with self.assertRaises(build.paired.BuildError): build.derive(f.work,result,f.lock,f.repository)

    def test_unexpected_extra_file_or_modified_paired_tree_reject(self):
        for target in ('easytier-source/extra.rs','tun-source/lib.rs'):
            with tempfile.TemporaryDirectory() as folder:
                f = Fixture(Path(folder)); result = f.derive()
                (f.work / target).write_bytes(b'unreviewed')
                with self.assertRaises(build.paired.BuildError): build.check_prepared(f.work,result,f.lock)

    def test_patch_order_or_profile_tamper_reject(self):
        with tempfile.TemporaryDirectory() as folder:
            f = Fixture(Path(folder)); result = f.derive()
            changed = copy.deepcopy(result); changed['patches'].reverse()
            with self.assertRaises(build.paired.BuildError): build.check_prepared(f.work,changed,f.lock)
            (f.work/'easytier-source'/f.lock['inert_profile']['source_path']).write_bytes(b'changed profile')
            with self.assertRaises(build.paired.BuildError): build.check_prepared(f.work,result,f.lock)

    def _compile(self, folder, passed=13, mutate=False, fail_delegate=False):
        f = Fixture(Path(folder)); prepared = f.derive(); calls=[]
        def compiler(command, **kwargs):
            calls.append(command)
            self.assertIn('--locked',command); self.assertEqual(kwargs['env']['CARGO_INCREMENTAL'],'0')
            count = passed if 'fifth_freedom_stun_' in command else 8
            kwargs['stdout'].write(f'test result: ok. {count} passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.01s\n'.encode())
            return subprocess.CompletedProcess(command,0)
        def delegate(work,output,actual,*args):
            self.assertEqual(actual,prepared)
            if fail_delegate: raise build.paired.BuildError('NO_PACKET_IMPORT')
            if mutate: (work/'tun-source/lib.rs').write_bytes(b'late change')
            return {'runs':[{'stage':'core-cli-build','exit_code':0}], 'built_executables':{}, 'selected_graph':{},
                'paired_tun_resolution_workspace_union':{}, 'build_inputs':{}}
        output=Path(folder)/'output'
        with patch.object(build,'load_inputs',return_value=(f.previous,{},{})), patch.object(build.base,'verify_read'), \
                patch.object(build.subprocess,'run',side_effect=compiler), patch.object(build.base.base,'build',side_effect=delegate):
            result=build.build(f.work,output,prepared,f.lock,f.repository)
        return result,calls

    def test_exact_thirteen_and_eight_before_retained_full_build_gates(self):
        with tempfile.TemporaryDirectory() as folder:
            result,calls=self._compile(folder)
            self.assertEqual([r['stage'] for r in result['runs']],['inert-profile-contract','owned-read-contract','core-cli-build'])
            self.assertIn('--no-default-features',calls[0]); self.assertIn('x86_64-pc-windows-msvc',calls[0])
            self.assertFalse(result['runtime_executed'])

    def test_twelve_tests_delegate_failure_or_late_mutation_retains_failed_receipt(self):
        for kwargs in ({'passed':12},{'mutate':True},{'fail_delegate':True}):
            with tempfile.TemporaryDirectory() as folder:
                with self.assertRaises(ValueError): self._compile(folder,**kwargs)
                result=json.loads((Path(folder)/'output/receipt.json').read_bytes())
                self.assertEqual(result['status'],'failed'); self.assertFalse(result['runtime_executed'])

    def test_output_cannot_overwrite_original_receipt(self):
        with tempfile.TemporaryDirectory() as folder:
            f=Fixture(Path(folder)); prepared=f.derive(); output=Path(folder)/'output'; output.mkdir()
            (output/'receipt.json').write_bytes(b'original')
            with patch.object(build,'load_inputs',return_value=(f.previous,{},{})):
                with self.assertRaises(FileExistsError): build.build(f.work,output,prepared,f.lock,f.repository)
            self.assertEqual((output/'receipt.json').read_bytes(),b'original')
