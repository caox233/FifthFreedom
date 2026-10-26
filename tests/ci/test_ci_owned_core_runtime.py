"""Strict runtime/drain receipts and same-job binding; no Windows core is started."""
import copy
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts'))
import ci_owned_core_runtime as runtime


def manifest():
    return {'schema':1,'probe_id':'1'*32,'core_path':'synthetic-inert-path', 'core_bytes':123,
        'core_sha256':'2'*64,'source_tree_sha256':'3'*64,'build_recipe_sha256':'4'*64,'inert_profile_sha256':'5'*64}


def firewall_diagnostic():
    return {'rule_check':'APPLICATION_PATH', 'rule_store':'PersistentStore',
        'rule_direction':'Inbound', 'application_path_case_insensitive_match':True, 'application_package_kind':'EMPTY'}


def managed():
    inputs=manifest()
    row={key:True for key in runtime.BOOL_FIELDS}
    row.update({key:inputs.get(key,'a'*64) for key in runtime.HASH_FIELDS})
    row.update(schema='ff-owned-core-runtime-v1',kind='finite-owned-core-nic-runtime',stage='COMPLETE',
        error_code=None,cleanup_error_code=None,probe_id=inputs['probe_id'],core_bytes=inputs['core_bytes'],
        instance='12345678-1234-1234-1234-123456789abc')
    row.update({key:False for key in ('public_admission','peer_authentication_exercised','peer_traffic_exercised','game_executed','driver_uninstalled')})
    return row


def compiled():
    lock=json.loads(runtime.build.LOCK.read_bytes())
    identity={'checkout_commit':'c'*40,'run_id':'12','run_attempt':'1','recipe_sha256':runtime.build.source.sha(runtime.build.LOCK.read_bytes()),
        'runtime_executed':False,'runtime_binary_uploaded':False,'public_startup_allowed':False}
    prepared=dict(lock['expected_prepared'])
    receipt={**identity,'kind':runtime.build.KIND,'status':'windows_inert_owned_read_built_not_executed',
        'source_tree_sha256':prepared['source_tree_sha256'],'patch_manifest_sha256':prepared['patch_manifest_sha256'],
        'cargo_lock_sha256':prepared['cargo_lock_patched_sha256'],'inert_profile':lock['inert_profile'],'driver_loaded':False,
        'built_executables':{'easytier-core.exe':{},'easytier-cli.exe':{}},'runs':[{'stage':name,'exit_code':0} for name in
        ('inert-profile-contract','owned-read-contract','interface-contract','typed-session-contract','metadata','selected-graph','core-cli-build')]}
    run={**identity,'status':'windows_inert_owned_read_build_verified_not_executed'}
    return receipt,prepared,run,lock


class RuntimeAdapterTests(unittest.TestCase):
    def test_non_finalize_cannot_restore_a_preexisting_control_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);control=root/'ff-owned-core-runtime-control';control.mkdir()
            (control/'firewall-plan.json').write_text('{"prior":"do not modify"}')
            (control/'session.json').write_text('{"prior":"do not modify"}')
            before={p.name:p.read_bytes() for p in control.iterdir()}
            env={'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','ImageOS':'win25','RUNNER_TEMP':str(root)}
            with patch.object(runtime,'ROOT',root),patch.object(runtime.sys,'platform','win32'),patch.dict(runtime.os.environ,env), \
                patch.object(runtime.sys,'argv',['adapter']),patch.object(runtime,'run') as run,patch.object(runtime,'restore') as restore:
                self.assertEqual(runtime.main(),1)
                run.assert_not_called();restore.assert_not_called()
            self.assertEqual({p.name:p.read_bytes() for p in control.iterdir()},before)

    def test_completed_finalize_is_read_only_and_cannot_swallow_restore_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);output=root/'artifacts/ci/owned-core-runtime.json';output.parent.mkdir(parents=True)
            original={'schema':1,'kind':'finite-owned-core-runtime-adapter','passed':True,
                'managed':{'passed':True,'exact_owned_job_drained':True},
                'firewall_restore':{'owned_rules_removed':True,'original_policy_unchanged':True,'exact_drain_verified':True}}
            runtime.new_json(output,original);before=output.read_bytes()
            with patch.object(runtime,'ROOT',root),patch.object(runtime.sys,'argv',['adapter','--finalize']), \
                patch.object(runtime,'restore',side_effect=ValueError('RUNTIME_RESTORE_UNCONFIRMED')) as restore:
                self.assertEqual(runtime.main(),0);restore.assert_not_called()
            self.assertEqual(output.read_bytes(),before)
            original['firewall_restore']['exact_drain_verified']=False;output.write_text(json.dumps(original))
            with patch.object(runtime,'ROOT',root),patch.object(runtime.sys,'argv',['adapter','--finalize']):
                self.assertEqual(runtime.main(),1)

    def test_producer_source_fixed_reasons_are_covered_without_dynamic_allowlist(self):
        source=(runtime.ROOT/'tests/OwnedCoreRuntime/RuntimeExperiment.cs').read_text()
        literals=set(re.findall(r'"([A-Z][A-Z_]+)"',source))
        self.assertEqual(literals-runtime.ERRORS-runtime.CLEANUP_ERRORS-runtime.STAGES,
            {'GITHUB_ACTIONS','RUNNER_ENVIRONMENT'})
        row=managed();row.update(passed=False,stage='PREFLIGHT',error_code='RPC_PORT_UNAVAILABLE')
        self.assertFalse(runtime.valid_runtime(row,manifest())['passed'])

    def test_actual_managed_contract_labels_are_exact_and_unique(self):
        labels=[]
        for name in ('InventoryContracts.cs','InputContracts.cs'):
            labels+=re.findall(r'Case\("([^"\n]+)"',(runtime.ROOT/'tests/OwnedCoreRuntime'/name).read_text())
        self.assertEqual(tuple(labels),runtime.CONTRACT_CASES)
        self.assertEqual(len(labels),22);self.assertEqual(len(set(labels)),22)

    def test_positive_full_producer_shape_binds_all_digests(self):
        self.assertEqual(runtime.valid_runtime(managed(),manifest()),managed())
        for key in ('core_sha256','source_tree_sha256','build_recipe_sha256','inert_profile_sha256','probe_id','instance'):
            row=managed();row[key]='bad'
            with self.assertRaises(ValueError):runtime.valid_runtime(row,manifest())

    def test_false_cleanup_must_never_be_certified_from_passed_string(self):
        for key in runtime.BOOL_FIELDS-{'public_admission','peer_authentication_exercised','peer_traffic_exercised','game_executed','driver_uninstalled'}:
            row=managed();row[key]=False
            with self.assertRaises(ValueError):runtime.valid_runtime(row,manifest())
        row=managed();row['kernel_after_sha256']='b'*64
        with self.assertRaises(ValueError):runtime.valid_runtime(row,manifest())

    def test_failed_experiment_with_real_drain_remains_failure_but_can_restore(self):
        row=managed();row.update(passed=False,stage='END_READS',error_code='NIC_ABSENT_BEFORE_END_READS',nic_absent_while_core_alive=False)
        result=runtime.valid_runtime(row,manifest())
        self.assertFalse(result['passed']);self.assertTrue(result['exact_owned_job_drained'])
        row['core_sha256']=None
        with self.assertRaises(ValueError):runtime.valid_runtime(row,manifest())

    def test_raw_unknown_duplicate_fields_invalid_booleans_and_scope_escalation_reject(self):
        variants=[]
        for key,value in (('extra','private path'),('stage','COMPLETE/private'),('error_code','UNKNOWN_PRIVATE'),
            ('cleanup_error_code','EXACT_JOB_DRAIN_FAILED/private'),('exact_owned_job_drained',1),('public_admission',True),('core_bytes',True)):
            row=managed();row[key]=value;variants.append(row)
        for row in variants:
            with self.assertRaises((ValueError,TypeError)):runtime.valid_runtime(row,manifest())
        with self.assertRaises(ValueError):runtime.decode(b'{"schema":1,"schema":1}')

    def test_prior_twelve_patch_or_cross_run_candidate_cannot_execute(self):
        args=compiled();runtime.verify_build(*args,'c'*40,'12','1')
        for part,key,value in ((0,'kind','independent-windows-owned-read-build-only'),(0,'run_id','11'),
            (2,'run_attempt','2'),(0,'checkout_commit','d'*40),(0,'recipe_sha256','0'*64),
            (0,'source_tree_sha256',args[3]['base_prepared']['source_tree_sha256']), (0,'driver_loaded',True)):
            changed=copy.deepcopy(args);changed[part][key]=value
            with self.assertRaises(ValueError):runtime.verify_build(*changed,'c'*40,'12','1')

    def test_missing_ordered_stage_or_bool_exit_never_certifies_compilation(self):
        for mutation in ('missing','reordered','bool','failure'):
            args=compiled();runs=args[0]['runs']
            if mutation=='missing':runs.pop(0)
            elif mutation=='reordered':runs.reverse()
            else:runs[0]['exit_code']=False if mutation=='bool' else 101
            with self.assertRaises(ValueError):runtime.verify_build(*args,'c'*40,'12','1')

    def test_firewall_operation_only_fixed_reason_is_projected(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);session={'manifest':manifest(),'manifest_path':str(root/'private.json')}
            def process(*args,**kwargs):
                (root/'prepare.json').write_text(json.dumps({'schema':1,'mode':'Prepare','succeeded':False,
                    'error_code':'FIREWALL_POLICY_UNAVAILABLE', **firewall_diagnostic(), 'private_extra':'raw private data'}))
                return subprocess.CompletedProcess(args,1,b'private raw output',b'private exception')
            observations=[]
            with patch.object(runtime,'powershell',side_effect=process):
                with self.assertRaisesRegex(ValueError,'^FIREWALL_POLICY_UNAVAILABLE$'):runtime.firewall('Prepare',session,root,observations)
            self.assertEqual(observations,[{'mode':'Prepare',**firewall_diagnostic()}])
            self.assertNotIn('private',json.dumps(observations))
            self.assertEqual(runtime.fixed_error(ValueError('private raw path')),'RUNTIME_OPERATION_FAILED')

    def test_firewall_observation_rejects_unknown_or_raw_diagnostics(self):
        original={'schema':1,'mode':'Prepare',**firewall_diagnostic()}
        runtime.firewall_observation(original,'Prepare')
        for key,value in (('schema',True),('rule_check',[]),('rule_check','APPLICATION_PATH/private'),('rule_store','private'),
            ('rule_direction','Inbound/private'),('application_path_case_insensitive_match',1),('application_package_kind','private')):
            with self.assertRaises(ValueError):runtime.firewall_observation({**original,key:value},'Prepare')
        for key in firewall_diagnostic():
            changed=dict(original);del changed[key]
            with self.assertRaises(ValueError):runtime.firewall_observation(changed,'Prepare')

    def test_armed_restore_without_matching_drain_must_fail(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);session={'manifest':manifest(),'manifest_path':str(root/'private.json')}
            def process(*args,**kwargs):
                keys=('active_rules_verified','armed','owned_rules_removed','original_policy_unchanged','exact_drain_verified',
                    'application_path_rules','pid_isolation','packet_behavior_verified','policy_enabled_or_changed')
                row={key:False for key in keys};row.update(schema=1,mode='Restore',succeeded=True,probe_id=manifest()['probe_id'],
                    armed=True,owned_rules_removed=True,original_policy_unchanged=True,application_path_rules=True,
                    **firewall_diagnostic())
                (root/'restore.json').write_text(json.dumps(row))
                return subprocess.CompletedProcess(args,0,b'',b'')
            with patch.object(runtime,'powershell',side_effect=process):
                with self.assertRaisesRegex(ValueError,'RUNTIME_RESTORE_UNCONFIRMED'):runtime.firewall('Restore',session,root)

    def test_failure_cleanup_calls_restore_after_execute_and_never_overwrites_receipt(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);calls=[]
            def run(report,control,temp):
                calls.append('execute');raise ValueError('RUNTIME_RESULT_FAILED')
            def restore(report,control):calls.append('restore')
            env={'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','ImageOS':'win25','RUNNER_TEMP':str(root)}
            with patch.object(runtime,'ROOT',root),patch.object(runtime.sys,'platform','win32'),patch.dict(runtime.os.environ,env), \
                patch.object(runtime,'run',side_effect=run),patch.object(runtime,'restore',side_effect=restore), \
                patch.object(runtime.sys,'argv',['adapter']):
                self.assertEqual(runtime.main(),1)
            self.assertEqual(calls,['execute','restore'])
            receipt=root/'artifacts/ci/owned-core-runtime.json'
            self.assertFalse(json.loads(receipt.read_bytes())['passed'])
            with self.assertRaises(FileExistsError):runtime.new_json(receipt,{'passed':True})

    def test_driver_wrong_identity_or_preexisting_devices_never_count_as_clean(self):
        row={'schema':1,'code':'OK','phase':'Before','clean':True,'wintun_devices':0,'wintun_packages':0,'wintun_services':0,
            'driver_registry':False,'driver_file':False,'unrelated_pnp_sha256':'a'*64,'unrelated_pnp_count':7}
        for key,value in (('wintun_devices',1),('wintun_packages',1),('driver_file',True),('unrelated_pnp_sha256','raw'),('extra','raw')):
            bad={**row,key:value}
            with patch.object(runtime,'powershell',return_value=subprocess.CompletedProcess([],0,json.dumps(bad).encode(),b'')):
                with self.assertRaises(ValueError):runtime.driver_inventory('Before')
