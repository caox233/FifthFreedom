"""Portable boundary checks. None of these claims to load a Windows driver."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'scripts'))
import ci_wintun_session_probe as probe


def good_result():
    return dict(schema=1,code='OK',passed=2,unrelated_identities_equal=True,
                public_admission=False,network_configured=False,packets_sent=False)


def good_environment(phase='Before'):
    return dict(schema=1,code='OK',phase=phase,clean=True,wintun_devices=0,
                wintun_packages=0,wintun_services=0,driver_registry=False,driver_file=False,
                unrelated_pnp_sha256='a'*64,unrelated_pnp_count=5)


class WintunProbeBoundaryTests(unittest.TestCase):
    def test_exact_official_2025_aliases_keep_host_boundary_closed(self):
        base={'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','ImageOS':'win25'}
        for alias in ('win25','win25-vs2026'):
            probe.require_host('win32',{**base,'ImageOS':alias})
        for change in ({'ImageOS':'win22'},{'ImageOS':'win25-unknown'},{'ImageOS':''},
                       {'RUNNER_ENVIRONMENT':'self-hosted'},{'GITHUB_ACTIONS':'false'}):
            with self.assertRaisesRegex(probe.ProbeError,'HOST'):probe.require_host('win32',{**base,**change})
        with self.assertRaisesRegex(probe.ProbeError,'HOST'):probe.require_host('linux',base)

    def test_exact_success_and_observed_failure_are_distinct(self):
        row=good_result()
        self.assertEqual(probe.validate_probe(json.dumps(row).encode(),0),row)
        row.update(code='CREATE_DRIVER',passed=0,unrelated_identities_equal=True)
        self.assertEqual(probe.validate_probe(json.dumps(row).encode(),1),row)
        with self.assertRaises(probe.ProbeError): probe.validate_probe(json.dumps(row).encode(),0)

    def test_partial_count_or_mutation_cannot_be_success(self):
        for key,value in [('passed',1),('unrelated_identities_equal',False),('public_admission',True),
                          ('network_configured',True),('packets_sent',True),('passed',True)]:
            with self.subTest(key=key,value=value):
                row=good_result();row[key]=value
                with self.assertRaises(probe.ProbeError): probe.validate_probe(json.dumps(row).encode(),0)

    def test_unknown_duplicate_and_extra_result_fields_rejected(self):
        for change in [{'code':'secret endpoint value'},{'token':'private'},{'schema':True}]:
            row=good_result();row.update(change)
            with self.assertRaises(probe.ProbeError): probe.validate_probe(json.dumps(row).encode(),0)
        with self.assertRaises(probe.ProbeError): probe.strict_json(b'{"schema":1,"schema":1}')
        with self.assertRaises(probe.ProbeError): probe.strict_json(b' '*65537)

    def test_clean_flag_cannot_override_driver_or_nonpresent_device(self):
        for key in ('wintun_devices','wintun_packages','wintun_services','driver_registry','driver_file'):
            row=good_environment();row[key]=True if key.startswith('driver_') else 1
            with self.subTest(key=key),self.assertRaises(probe.ProbeError):
                probe.validate_environment(json.dumps(row).encode(),0,'Before')

    def test_unknown_inventory_or_nonzero_exit_fails_closed(self):
        for row,code in [(dict(schema=1,code='PNP',phase='Before'),1),
                         (good_environment(),1),({**good_environment(),'wintun_devices':None},0),
                         ({**good_environment(),'schema':True},0),
                         ({**good_environment(),'unrelated_pnp_sha256':'raw-system-id'},0)]:
            with self.assertRaises(probe.ProbeError): probe.validate_environment(json.dumps(row).encode(),code,'Before')

    def test_installed_driver_may_remain_but_device_or_identity_change_fails(self):
        before=good_environment();after=good_environment('After')
        after.update(clean=False,wintun_packages=1,wintun_services=1,driver_file=True,driver_registry=True)
        probe.validate_environment(json.dumps(after).encode(),0,'After')
        probe.validate_postflight(before,after)
        for key,value in [('wintun_devices',1),('unrelated_pnp_sha256','b'*64),('unrelated_pnp_count',4)]:
            changed=dict(after);changed[key]=value
            with self.assertRaises(probe.ProbeError): probe.validate_postflight(before,changed)

    def test_bounded_child_output_and_exit_are_not_interchangeable(self):
        code,out,err=probe.bounded_command([sys.executable,'-c',"import sys;print('safe');sys.exit(7)"],cwd=ROOT,timeout=5)
        self.assertEqual((code,out.strip(),err),(7,b'safe',b''))
        with self.assertRaisesRegex(probe.ProbeError,'OUTPUT_LIMIT'):
            probe.bounded_command([sys.executable,'-c',"print('x'*8192)"],cwd=ROOT,timeout=5,limit=512)

    def test_timeout_stops_owned_child_without_touching_other_child(self):
        other=subprocess.Popen([sys.executable,'-c','import time;time.sleep(10)'])
        try:
            with tempfile.TemporaryDirectory() as directory:
                marker=Path(directory)/'late'
                code="import pathlib,time;time.sleep(2);pathlib.Path('late').write_text('unexpected')"
                start=time.monotonic()
                with self.assertRaisesRegex(probe.ProbeError,'COMMAND_TIMEOUT'):
                    probe.bounded_command([sys.executable,'-c',code],cwd=directory,timeout=.1)
                self.assertLess(time.monotonic()-start,3)
                self.assertIsNone(other.poll())
                self.assertFalse(marker.exists())
        finally:
            other.terminate();other.wait(timeout=5)

    def test_second_reader_start_failure_reaps_child_and_first_reader(self):
        import threading
        original_start=threading.Thread.start
        original_popen=subprocess.Popen
        threads=[];children=[]
        def start(thread):
            threads.append(thread)
            if len(threads)==2:raise RuntimeError('injected fixed failure')
            return original_start(thread)
        def spawn(*args,**kwargs):
            child=original_popen(*args,**kwargs);children.append(child);return child
        with mock.patch.object(threading.Thread,'start',start),mock.patch.object(probe.subprocess,'Popen',spawn):
            with self.assertRaisesRegex(RuntimeError,'injected fixed failure'):
                probe.bounded_command([sys.executable,'-c','import time;time.sleep(10)'],cwd=ROOT,timeout=5)
        self.assertEqual(len(children),1)
        self.assertIsNotNone(children[0].poll())
        self.assertEqual(len(threads),2)
        self.assertFalse(threads[0].is_alive())
        self.assertTrue(children[0].stdout.closed and children[0].stderr.closed)

    def test_dll_wrong_digest_fails_before_any_file_is_written(self):
        lock=json.loads(probe.LOCK.read_bytes())
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(probe.ProbeError,'ZIP_DIGEST'):
                probe.extract_dll(b'not the official zip',Path(directory),lock)
            self.assertEqual(list(Path(directory).iterdir()),[])

    def test_tracked_probe_and_same_tun_recipe_bytes_are_pinned(self):
        lock=json.loads(probe.LOCK.read_bytes())
        paired=json.loads((ROOT/'scripts/easytier-windows-source.lock.json').read_bytes())
        self.assertEqual(lock['tun'],paired['tun'])
        self.assertEqual(lock['session_recipe']['sha256'],paired['session_recipe']['sha256'])
        for item in [*lock['probe_files'],lock['session_recipe'],lock['patch']]:
            data=(ROOT/item['path']).read_bytes()
            self.assertEqual((len(data),hashlib.sha256(data).hexdigest()),(item['bytes'],item['sha256']))
        self.assertFalse(lock['public_admission'])

    def test_locked_resolution_rejects_default_dns_or_unpaired_backend(self):
        directory=ROOT/'ignored-resolution-only'
        metadata={'packages':[{'name':'tun-easytier','id':'tun','manifest_path':str(directory/'Cargo.toml'),'source':None}],
                  'resolve':{'nodes':[{'id':'tun','features':['async','futures-core','tokio','tokio-util']}]}}
        probe.validate_resolution(metadata,directory)
        for mutate in (lambda x:x['resolve']['nodes'][0]['features'].append('wintun-dns'),
                       lambda x:x['packages'][0].update(source='git+unreviewed'),
                       lambda x:x['packages'].append({'name':'pcap'}),
                       lambda x:x['packages'][0].update(manifest_path=str(ROOT/'Cargo.toml'))):
            changed=copy.deepcopy(metadata);mutate(changed)
            with self.assertRaises(probe.ProbeError):probe.validate_resolution(changed,directory)

    def test_owned_destination_is_not_overwritten(self):
        # Even valid pinned data cannot overwrite an existing DLL. Use a small
        # independent ZIP here to exercise the real extraction write boundary.
        import io,zipfile
        out=io.BytesIO()
        with zipfile.ZipFile(out,'w') as archive:archive.writestr('held.dll',b'bytes')
        data=out.getvalue()
        lock={'official_zip':{'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()},
              'dll':{'member':'held.dll','bytes':5,'sha256':hashlib.sha256(b'bytes').hexdigest()}}
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'wintun.dll';path.write_bytes(b'original')
            with self.assertRaises(FileExistsError):probe.extract_dll(data,Path(directory),lock)
            self.assertEqual(path.read_bytes(),b'original')

    def test_preparation_rejects_unreviewed_base_before_applying_patch(self):
        lock=json.loads(probe.LOCK.read_bytes())
        with tempfile.TemporaryDirectory() as directory, \
             mock.patch.object(probe.source,'checked_bytes',return_value=b'x'), \
             mock.patch.object(probe.source,'archive_files',return_value=({'Cargo.toml':{'data':b'changed','mode':0o644}},[])), \
             mock.patch.object(probe.json,'loads',return_value={'groups':[{'name':'tun','files':[{'path':'Cargo.toml','original_sha256':'a'*64}]}]}), \
             mock.patch.object(probe.paired,'apply_patch') as apply:
            with self.assertRaisesRegex(probe.ProbeError,'SOURCE_BASE'):
                probe.prepare(Path(directory)/'archive',Path(directory),lock)
            apply.assert_not_called()

if __name__ == '__main__': unittest.main()
