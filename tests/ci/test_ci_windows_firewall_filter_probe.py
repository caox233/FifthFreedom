"""Bounded diagnostic parsing and pin/failure controls; no Windows API runs here."""
import copy
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import ci_windows_firewall_filter_probe as probe


def result():
    row = {key: False for key in probe.FALSE_FIELDS}
    row.update(schema=1, kind='wfp-exact-app-filter-diagnostic', passed=True, coverage='UNKNOWN', observer_executed=True,
        read_only_transaction=True, transaction_closed=True, error_code=None,
        layers=[dict(name=name, complete=True, **{key: 0 for key in probe.COUNTS}) for name in probe.LAYERS])
    return row


def encoded(value):
    return json.dumps(value).encode()


def fake_pe():
    data = bytearray(512)
    data[:2] = b'MZ'
    struct.pack_into('<I', data, 0x3c, 0x80)
    data[0x80:0x84] = b'PE\0\0'
    struct.pack_into('<H', data, 0x84, 0x8664)
    struct.pack_into('<H', data, 0x96, 2)
    struct.pack_into('<H', data, 0x98, 0x20b)
    return bytes(data)


def inputs(root):
    stage = root / ('ff-core-runtime-' + 'a' * 32)
    stage.mkdir()
    image = stage / 'easytier-core.exe'
    data = b'inert fixture bytes, never an executable'
    image.write_bytes(data)
    manifest = dict(schema=1, probe_id='a'*32, core_path=str(image), core_bytes=len(data), core_sha256=probe.sha(data),
        source_tree_sha256='1'*64, build_recipe_sha256='2'*64, inert_profile_sha256='3'*64)
    path = stage / 'runtime-input.json'
    path.write_bytes(encoded(manifest))
    return {'manifest': manifest, 'manifest_path': str(path)}, image


class WfpDiagnosticTests(unittest.TestCase):
    def test_zero_filters_is_only_a_completed_diagnostic_not_coverage(self):
        value = probe.observation(encoded(result()))
        self.assertEqual(value['coverage'], 'UNKNOWN')
        self.assertFalse(value['arbitration_verified'])
        self.assertFalse(value['admission_changed'])

    def test_raw_fields_false_claims_and_incomplete_transactions_are_rejected(self):
        for key, value in [('raw_path','private'), ('coverage','FULL'), ('arbitration_verified',True),
            ('admission_changed',True), ('core_executed',True), ('schema',True),
            ('read_only_transaction',False), ('transaction_closed',False), ('error_code',[])]:
            with self.assertRaises(ValueError):
                probe.observation(encoded({**result(), key:value}))
        for data in (b'{"schema":1,"schema":1}', b'x'*65537):
            with self.assertRaises(ValueError): probe.observation(data)

    def test_native_error_stays_fixed_and_never_becomes_coverage(self):
        value = result()
        value.update(passed=False, error_code='WFP_ENUMERATION_FAILED')
        value['layers'][1]['complete'] = False
        self.assertFalse(probe.observation(encoded(value))['passed'])
        value['error_code'] = 'private raw provider data'
        with self.assertRaises(ValueError): probe.observation(encoded(value))

    def test_counts_and_four_layer_identity_have_aggregate_bounds(self):
        for key, value in [('enumerated',True), ('enumerated',20001), ('exact_app',1),
            ('unsupported',1), ('block',1), ('weight_uint64',1), ('name','RECV_V6'), ('private','raw')]:
            changed = result(); changed['layers'][0][key] = value
            with self.assertRaises(ValueError): probe.observation(encoded(changed))
        changed = result()
        for row in changed['layers']: row['enumerated'] = 6000
        with self.assertRaises(ValueError): probe.observation(encoded(changed))
        changed = result(); changed['layers'].pop()
        with self.assertRaises(ValueError): probe.observation(encoded(changed))

    def test_pure_contract_receipt_refuses_skips_partial_or_actual_api_calls(self):
        value = dict(schema=1, kind='wfp-filter-pure-contracts', passed=True, required_checks=20,
            passed_checks=20, skipped_checks=0, wfp_api_called=False, policy_changed=False, core_executed=False)
        probe.contracts(encoded(value))
        for key, item in [('passed_checks',19), ('skipped_checks',1), ('wfp_api_called',True), ('schema',True), ('raw','private')]:
            with self.assertRaises(ValueError): probe.contracts(encoded({**value,key:item}))

    def test_owned_manifest_requires_exact_staging_path_hash_and_schema(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); session, image = inputs(root)
            self.assertEqual(probe.validate_session(session,root), image)
            for key, value in [('schema',True), ('probe_id','B'*32), ('core_bytes',True),
                ('core_path',str(root/'unrelated.exe')), ('core_sha256','0'*64), ('extra','private')]:
                changed = copy.deepcopy(session); changed['manifest'][key] = value
                Path(changed['manifest_path']).write_bytes(encoded(changed['manifest']))
                with self.assertRaises(ValueError): probe.validate_session(changed,root)
            Path(session['manifest_path']).write_bytes(encoded(session['manifest']))
            image.write_bytes(b'changed bytes')
            with self.assertRaisesRegex(ValueError,'WFP_IMAGE_CHANGED'): probe.validate_session(session,root)

    def test_reparse_file_is_not_an_owned_input(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); session, image = inputs(root)
            original = root/'original'; original.write_bytes(image.read_bytes())
            image.unlink(); image.symlink_to(original)
            with self.assertRaises(ValueError): probe.validate_session(session,root)

    def test_same_job_source_and_image_pins_precede_native_invocation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); session, image = inputs(root)
            source = root / probe.SOURCE; source.parent.mkdir(); source.write_bytes(b'fixture source')
            work = root / probe.WORK; work.mkdir()
            executable = work / 'WfpFilterProbe.exe'; executable.write_bytes(fake_pe())
            identity = dict(checkout_commit='a'*40, run_id='42', run_attempt='1')
            pins = dict(**identity, source_sha256=probe.sha(source.read_bytes()), image=probe.image_pin(executable))
            pin_path = work / 'build-session.json'; pin_path.write_bytes(encoded(pins))
            process = subprocess.CompletedProcess([],0,encoded(result()),b'')
            with patch.object(probe,'ROOT',root), patch.object(probe,'host',return_value=root), \
                patch.object(probe,'identity',return_value=identity), patch.object(probe.subprocess,'run',return_value=process) as run:
                self.assertEqual(probe.observe(session)['coverage'],'UNKNOWN')
                self.assertEqual(run.call_args.args[0],[str(executable),'--app',str(image)])
                for mutation in ('run_id','source_sha256','image'):
                    bad=copy.deepcopy(pins)
                    if mutation=='image': bad['image']['sha256']='0'*64
                    else: bad[mutation]='0'
                    pin_path.write_bytes(encoded(bad)); run.reset_mock()
                    with self.assertRaises(ValueError): probe.observe(session)
                    run.assert_not_called()

    def test_changed_helper_after_read_and_private_stderr_cannot_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder); session,_=inputs(root)
            source=root/probe.SOURCE; source.parent.mkdir(); source.write_bytes(b'fixture source')
            work=root/probe.WORK; work.mkdir(); executable=work/'WfpFilterProbe.exe'
            executable.write_bytes(fake_pe())
            identity=dict(checkout_commit='a'*40,run_id='42',run_attempt='1')
            pins=dict(**identity,source_sha256=probe.sha(source.read_bytes()),image=probe.image_pin(executable))
            (work/'build-session.json').write_bytes(encoded(pins))
            def process(*args,**kwargs):
                executable.write_bytes(fake_pe()+b'changed')
                return subprocess.CompletedProcess([],0,encoded(result()),b'')
            with patch.object(probe,'ROOT',root),patch.object(probe,'host',return_value=root), \
                patch.object(probe,'identity',return_value=identity),patch.object(probe.subprocess,'run',side_effect=process):
                with self.assertRaises(ValueError):probe.observe(session)
            executable.write_bytes(fake_pe())
            raw_error=subprocess.CompletedProcess([],0,encoded(result()),b'private provider exception')
            with patch.object(probe,'ROOT',root),patch.object(probe,'host',return_value=root), \
                patch.object(probe,'identity',return_value=identity),patch.object(probe.subprocess,'run',return_value=raw_error):
                with self.assertRaisesRegex(ValueError,'^WFP_RESULT_INVALID$'):probe.observe(session)
            self.assertEqual(probe.fixed_error(RuntimeError('private path')), 'WFP_OPERATION_FAILED')

    def test_wrong_host_does_not_compile_and_existing_build_directory_is_not_reused(self):
        with patch.object(probe.sys,'platform','linux'),patch.object(probe.subprocess,'run') as run:
            with self.assertRaisesRegex(ValueError,'WFP_HOST_REQUIRED'):probe.build({})
            run.assert_not_called()
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);work=root/probe.WORK;work.mkdir();prior=work/'prior';prior.write_bytes(b'unchanged')
            identity=dict(checkout_commit='a'*40,run_id='42',run_attempt='1')
            git=[subprocess.CompletedProcess([],0,b'a'*40+b'\n',b''),subprocess.CompletedProcess([],0,b'',b'')]
            with patch.object(probe,'host',return_value=root),patch.object(probe,'identity',return_value=identity), \
                patch.object(probe.shutil,'which',return_value='cl.exe'),patch.object(probe.subprocess,'run',side_effect=git) as run:
                with self.assertRaises(FileExistsError):probe.build({})
                self.assertEqual(run.call_count,2)
            self.assertEqual(prior.read_bytes(),b'unchanged')


if __name__ == '__main__':
    unittest.main()
