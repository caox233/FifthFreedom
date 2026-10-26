"""One same-job inert core/NIC experiment; all private inputs remain on the runner."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import urllib.request
import uuid

import ci_easytier_windows_inert as build
import ci_owned_dependencies as dependency

ROOT = build.ROOT
CONTRACT_CASES = ('kernel GUID byte order and all identity components bind', 'kernel source needs actual preferred non-skipped exact prefix', 'invalid or truncated tables fail rather than imply absence', 'duplicate zero and malformed kernel identities reject', 'orphan addresses and invalid route shapes reject', 'subnet conflict includes broader connected networks and specific routes', 'identity reuse does not count as owned interface absence', 'foreign identity and route changes affect sanitized digest', 'owned changes allow only fixed subnet and exact kernel automatic routes', 'cleanup comparison retains foreign addresses routes and exact identity', 'normal cleanup rejects absence already observed before EndReads', 'normal cleanup requires a new sample after the actual trigger', 'post-trigger core exit cannot count as observed live cleanup', 'only fresh instance and adapter name are added to exact inert template', 'strict runtime manifest rejects duplicates unknown fields and wrong schema', 'old twelve-patch kind and wrong prepared source cannot launch', 'both embedded recipe bytes and inert profile bytes are bound', 'runtime image requires canonical owned staging parent and exact basename', 'manifest refuses zero uppercase malformed identities and invalid image lengths', 'template insertion rejects ambiguous flags and preexisting identity keys', 'private paths and digests are absent from input representation', 'embedded source recipe and profile agree before runtime input can bind')

BOOL_FIELDS = frozenset('passed owned_process_start_attempted core_read_hello_completed correct_source_kernel_verified wrong_source_unavailable read_channel_ended nic_absent_while_core_alive exact_owned_job_drained foreign_kernel_identities_equal input_pins_released configuration_removed public_admission peer_authentication_exercised peer_traffic_exercised game_executed driver_uninstalled'.split())
HASH_FIELDS = frozenset('core_sha256 source_tree_sha256 build_recipe_sha256 inert_profile_sha256 configuration_sha256 kernel_before_sha256 kernel_after_sha256'.split())
FIELDS = BOOL_FIELDS | HASH_FIELDS | {'schema', 'kind', 'stage', 'error_code', 'cleanup_error_code', 'probe_id', 'instance', 'core_bytes'}
STAGES = frozenset('INPUT HOST PREFLIGHT CONFIGURATION START OBSERVE WRONG_SOURCE END_READS COMPLETE'.split())
ERRORS = frozenset('INPUT_REJECTED HOST_REJECTED KERNEL_PREFLIGHT_FAILED CONFIGURATION_FAILED CORE_START_FAILED CORE_OBSERVATION_FAILED WRONG_SOURCE_FAILED NORMAL_CLEANUP_OBSERVATION_FAILED RUNTIME_FAILED CORE_EXITED_BEFORE_NIC_ABSENT NIC_ABSENT_BEFORE_END_READS FOREIGN_KERNEL_CHANGED NIC_CLEANUP_OBSERVATION_TIMEOUT SUBNET_CONFLICT ADAPTER_COLLISION NIC_UNAVAILABLE CORE_EXITED_DURING_OBSERVATION WRONG_SOURCE_ACCEPTED RPC_PORT_UNAVAILABLE'.split())
CLEANUP_ERRORS = frozenset('READ_CLOSE_FAILED EXACT_JOB_DRAIN_FAILED KERNEL_NOT_RESTORED KERNEL_POSTFLIGHT_FAILED INPUT_STILL_PINNED CONFIGURATION_CLEANUP_FAILED'.split())
FIREWALL_CODES = frozenset('FIREWALL_INPUT_INVALID FIREWALL_POLICY_UNAVAILABLE FIREWALL_RULE_CHANGED FIREWALL_HOST_REQUIRED FIREWALL_STATE_EXISTS FIREWALL_IMAGE_CHANGED FIREWALL_DRAIN_UNCONFIRMED FIREWALL_RESTORE_FAILED FIREWALL_BASELINE_CHANGED FIREWALL_OPERATION_FAILED'.split())
FIREWALL_CHECKS = frozenset('ENUMERATE ABSENT RULE_COUNT GROUP DISPLAY_NAME ENABLED PROFILE DIRECTION ACTION EDGE_TRAVERSAL LOOSE_SOURCE_MAPPING LOCAL_ONLY_MAPPING PRIMARY_STATUS ENFORCEMENT_STATUS FILTER_QUERY APPLICATION_COUNT APPLICATION_PATH APPLICATION_PACKAGE PORT_COUNT PROTOCOL LOCAL_PORT REMOTE_PORT ADDRESS_COUNT LOCAL_ADDRESS REMOTE_ADDRESS SERVICE_COUNT SERVICE INTERFACE_COUNT INTERFACE_ALIAS INTERFACE_TYPE_COUNT INTERFACE_TYPE VERIFIED'.split())
CODES = FIREWALL_CODES | frozenset('RUNTIME_HOST_REQUIRED RUNTIME_BUILD_INVALID RUNTIME_INPUT_CHANGED RUNTIME_REPORT_INVALID RUNTIME_DRIVER_STATE_INVALID RUNTIME_FIREWALL_FAILED RUNTIME_PROCESS_FAILED RUNTIME_RESULT_FAILED RUNTIME_RESTORE_UNCONFIRMED RUNTIME_INPUT_INVALID'.split())


def require(value, code):
    if not value:
        raise ValueError(code)


def unique(items):
    result = {}
    for key, value in items:
        require(key not in result, 'RUNTIME_INPUT_INVALID')
        result[key] = value
    return result


def decode(data, limit=65536):
    require(0 < len(data) <= limit, 'RUNTIME_INPUT_INVALID')
    return json.loads(data, object_pairs_hook=unique)


def new_json(path, value):
    with path.open('xb') as output:
        output.write(build.source.encoded(value))
        output.flush()
        os.fsync(output.fileno())


def fixed_error(error):
    return str(error) if type(error) is ValueError and str(error) in CODES else 'RUNTIME_OPERATION_FAILED'


def valid_runtime(value, manifest):
    require(type(value) is dict and set(value) == FIELDS and value['schema'] == 'ff-owned-core-runtime-v1'
        and value['kind'] == 'finite-owned-core-nic-runtime' and value['stage'] in STAGES, 'RUNTIME_REPORT_INVALID')
    require(all(type(value[key]) is bool for key in BOOL_FIELDS), 'RUNTIME_REPORT_INVALID')
    require(value['error_code'] is None or value['error_code'] in ERRORS, 'RUNTIME_REPORT_INVALID')
    require(value['cleanup_error_code'] is None or value['cleanup_error_code'] in CLEANUP_ERRORS, 'RUNTIME_REPORT_INVALID')
    for key in HASH_FIELDS:
        require(value[key] is None or (type(value[key]) is str and re.fullmatch('[0-9a-f]{64}', value[key])), 'RUNTIME_REPORT_INVALID')
    require(value['probe_id'] in (None, manifest['probe_id']) and value['core_bytes'] in (None, manifest['core_bytes'])
        and (value['core_bytes'] is None or type(value['core_bytes']) is int), 'RUNTIME_REPORT_INVALID')
    require(value['instance'] is None or (type(value['instance']) is str and
        re.fullmatch('[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}', value['instance'])), 'RUNTIME_REPORT_INVALID')
    for key in ('core_sha256', 'source_tree_sha256', 'build_recipe_sha256', 'inert_profile_sha256'):
        require(value[key] in (None, manifest[key]), 'RUNTIME_REPORT_INVALID')
    require(not any(value[key] for key in ('public_admission', 'peer_authentication_exercised',
        'peer_traffic_exercised', 'game_executed', 'driver_uninstalled')), 'RUNTIME_REPORT_INVALID')
    if value['exact_owned_job_drained'] or value['owned_process_start_attempted']:
        require(value['probe_id'] == manifest['probe_id'] and value['instance'] is not None and
            all(value[key] == manifest[key] for key in ('core_bytes', 'core_sha256', 'source_tree_sha256', 'build_recipe_sha256', 'inert_profile_sha256')),
            'RUNTIME_REPORT_INVALID')
    if value['passed']:
        require(value['stage'] == 'COMPLETE' and value['error_code'] is None and value['cleanup_error_code'] is None
            and all(value[key] for key in BOOL_FIELDS - {'public_admission', 'peer_authentication_exercised',
                'peer_traffic_exercised', 'game_executed', 'driver_uninstalled'})
            and all(value[key] is not None for key in HASH_FIELDS)
            and value['kernel_before_sha256'] == value['kernel_after_sha256'], 'RUNTIME_REPORT_INVALID')
    else:
        require(value['error_code'] is not None or value['cleanup_error_code'] is not None, 'RUNTIME_REPORT_INVALID')
    return value


def verify_build(receipt, prepared, run, lock, commit, run_id, attempt):
    require(receipt['kind'] == build.KIND and receipt['status'] == 'windows_inert_owned_read_built_not_executed'
        and run['status'] == 'windows_inert_owned_read_build_verified_not_executed', 'RUNTIME_BUILD_INVALID')
    for item in (receipt, run):
        require(item['checkout_commit'] == commit and item['run_id'] == run_id and item['run_attempt'] == attempt
            and item['recipe_sha256'] == build.source.sha(build.LOCK.read_bytes())
            and all(item[key] is False for key in ('runtime_executed', 'runtime_binary_uploaded', 'public_startup_allowed')), 'RUNTIME_BUILD_INVALID')
    require(all(prepared[key] == value for key, value in lock['expected_prepared'].items())
        and receipt['source_tree_sha256'] == prepared['source_tree_sha256']
        and receipt['patch_manifest_sha256'] == prepared['patch_manifest_sha256']
        and receipt['cargo_lock_sha256'] == prepared['cargo_lock_patched_sha256']
        and receipt['inert_profile'] == lock['inert_profile'] and receipt['driver_loaded'] is False, 'RUNTIME_BUILD_INVALID')
    stages = ['inert-profile-contract', 'owned-read-contract', 'interface-contract', 'typed-session-contract', 'metadata', 'selected-graph', 'core-cli-build']
    require([row['stage'] for row in receipt['runs']] == stages and all(type(row['exit_code']) is int and row['exit_code'] == 0
        for row in receipt['runs']), 'RUNTIME_BUILD_INVALID')
    require(set(receipt['built_executables']) == {'easytier-core.exe', 'easytier-cli.exe'}, 'RUNTIME_BUILD_INVALID')


def powershell(script, arguments, timeout=120):
    return subprocess.run(['pwsh', '-NoLogo', '-NoProfile', '-NonInteractive', '-File', str(ROOT / 'scripts' / script),
        *arguments], cwd=ROOT, capture_output=True, timeout=timeout)


def driver_inventory(phase):
    result = powershell('ci_wintun_session_environment.ps1', ['-Phase', phase])
    value = decode(result.stdout)
    require(result.returncode == 0 and result.stderr == b'' and value.get('schema') == 1 and value.get('code') == 'OK'
        and value.get('phase') == phase, 'RUNTIME_DRIVER_STATE_INVALID')
    fields = {'schema','code','phase','clean','wintun_devices','wintun_packages','wintun_services','driver_registry','driver_file',
        'unrelated_pnp_sha256','unrelated_pnp_count'}
    require(set(value) == fields and all(type(value[key]) is bool for key in ('clean','driver_registry','driver_file'))
        and all(type(value[key]) is int and 0 <= value[key] <= 20000 for key in ('wintun_devices','wintun_packages','wintun_services','unrelated_pnp_count'))
        and type(value['unrelated_pnp_sha256']) is str and re.fullmatch('[0-9a-f]{64}', value['unrelated_pnp_sha256']), 'RUNTIME_DRIVER_STATE_INVALID')
    require(value['wintun_devices'] == 0 and value['wintun_packages'] <= 1 and value['wintun_services'] <= 1, 'RUNTIME_DRIVER_STATE_INVALID')
    if phase == 'Before':
        require(value['clean'] and value['wintun_packages'] == value['wintun_services'] == 0
            and not value['driver_registry'] and not value['driver_file'], 'RUNTIME_DRIVER_STATE_INVALID')
    return value


def firewall_observation(value, mode):
    require(type(value) is dict and type(value.get('schema')) is int and value['schema'] == 1
        and value.get('mode') == mode, 'RUNTIME_FIREWALL_FAILED')
    fields = ('rule_check', 'rule_store', 'rule_direction', 'application_path_case_insensitive_match', 'application_package_kind',
        'enforcement', 'enforcement_trace', 'enforcement_trace_query')
    require(all(key in value for key in fields), 'RUNTIME_FIREWALL_FAILED')
    require(value['rule_check'] is None or (type(value['rule_check']) is str
        and value['rule_check'] in FIREWALL_CHECKS), 'RUNTIME_FIREWALL_FAILED')
    require(value['rule_store'] in (None, 'ActiveStore', 'PersistentStore')
        and value['rule_direction'] in (None, 'Inbound', 'Outbound')
        and (value['application_path_case_insensitive_match'] is None
             or type(value['application_path_case_insensitive_match']) is bool), 'RUNTIME_FIREWALL_FAILED')
    require(value['application_package_kind'] in (None, 'MISSING', 'NULL', 'EMPTY', 'ANY',
        'ANY_CASE_VARIANT', 'SID', 'OTHER_STRING', 'OTHER_TYPE'), 'RUNTIME_FIREWALL_FAILED')
    statuses = ('MISSING NULL EMPTY MULTIPLE NULL_ITEM OTHER_TYPE OTHER_STRING OTHER_NUMBER Invalid Full '
        'FirewallOffInProfile CategoryOff DisabledObject InactiveProfile LocalAddressResolutionEmpty '
        'RemoteAddressResolutionEmpty LocalPortResolutionEmpty RemotePortResolutionEmpty InterfaceResolutionEmpty '
        'ApplicationResolutionEmpty RemoteMachineEmpty RemoteUserEmpty LocalGlobalOpenPortsDisallowed '
        'LocalAuthorizedApplicationsDisallowed LocalFirewallRulesDisallowed LocalConsecRulesDisallowed '
        'NotTargetPlatform OptimizedOut LocalUserEmpty TransportMachinesEmpty TunnelMachinesEmpty '
        'TupleResolutionEmpty NotApplicable').split()
    for key in ('enforcement', 'enforcement_trace'):
        item = value[key]
        if item is not None:
            require(type(item) is dict and set(item) == {'shape', 'count', 'item_type', 'status'}
                and item['shape'] in ('MISSING', 'NULL', 'SCALAR', 'ARRAY')
                and type(item['count']) is int and 0 <= item['count'] <= 65
                and item['item_type'] in ('NONE', 'NULL', 'STRING', 'ENUM', 'UINT16', 'UINT32', 'INT32', 'OTHER')
                and item['status'] in statuses, 'RUNTIME_FIREWALL_FAILED')
    require(value['enforcement_trace_query'] in ('NOT_RUN', 'SINGLE_MATCH', 'COUNT_MISMATCH', 'IDENTITY_MISMATCH', 'QUERY_FAILED')
        and ((value['enforcement_trace_query'] == 'SINGLE_MATCH') == (value['enforcement_trace'] is not None)), 'RUNTIME_FIREWALL_FAILED')
    return {'mode': mode, **{key: value[key] for key in fields}}


def firewall(mode, session, control, observations=None):
    report = control / (mode.lower() + '.json')
    args = ['-Mode', mode, '-Manifest', session['manifest_path'], '-State', str(control / 'firewall-plan.json'), '-Report', str(report)]
    if mode == 'Restore':
        args += ['-DrainReport', str(control / 'managed.json')]
    result = powershell('ci_windows_core_firewall.ps1', args)
    value = decode(report.read_bytes())
    observation = firewall_observation(value, mode)
    if observations is not None:
        observations.append(observation)
    if type(value) is dict and value.get('error_code') in FIREWALL_CODES:
        raise ValueError(value['error_code'])
    require(result.returncode == 0 and type(value) is dict and value.get('schema') == 1 and value.get('mode') == mode
        and value.get('succeeded') is True and value.get('probe_id') == session['manifest']['probe_id'], 'RUNTIME_FIREWALL_FAILED')
    # No raw PowerShell output is emitted or retained. Keep only this fixed subset.
    keys = ('active_rules_verified','armed','owned_rules_removed','original_policy_unchanged','exact_drain_verified',
        'application_path_rules','pid_isolation','packet_behavior_verified','policy_enabled_or_changed')
    require(all(type(value.get(key)) is bool for key in keys) and value['application_path_rules']
        and not value['pid_isolation'] and not value['packet_behavior_verified'] and not value['policy_enabled_or_changed'], 'RUNTIME_FIREWALL_FAILED')
    if mode in ('Prepare','Arm'):
        require(value['active_rules_verified'], 'RUNTIME_FIREWALL_FAILED')
    if mode == 'Restore':
        require(value['owned_rules_removed'] and value['original_policy_unchanged']
            and (not value['armed'] or value['exact_drain_verified']), 'RUNTIME_RESTORE_UNCONFIRMED')
    return {'mode': mode, **{key: value[key] for key in keys}}


def run(report, control, temp):
    lock = decode(build.LOCK.read_bytes())
    build.load_inputs(lock)
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    require(commit == os.environ.get('GITHUB_SHA') and not subprocess.check_output(
        ['git', 'status', '--porcelain', '--untracked-files=no'], cwd=ROOT), 'RUNTIME_BUILD_INVALID')
    output = ROOT / 'artifacts/ci/easytier-windows-inert'
    compiled = decode((output / 'receipt.json').read_bytes(), 2 * 1024 * 1024)
    prepared = decode((output / 'source-receipt.json').read_bytes(), 2 * 1024 * 1024)
    build_run = decode((output.parent / 'easytier-windows-inert-run.json').read_bytes())
    verify_build(compiled, prepared, build_run, lock, commit, os.environ.get('GITHUB_RUN_ID'), os.environ.get('GITHUB_RUN_ATTEMPT'))
    work = temp / 'fifth-easytier-windows-inert'
    build.check_prepared(work, prepared, lock)
    originals = work / 'target' / lock['build']['target'] / 'release'
    for name, pin in compiled['built_executables'].items():
        require(build.base.base.inspect_without_packet((originals / name).read_bytes()) == pin, 'RUNTIME_INPUT_CHANGED')
    probe_id = uuid.uuid4().hex
    stage = temp / ('ff-core-runtime-' + probe_id)
    stage.mkdir(exist_ok=False)
    core = stage / 'easytier-core.exe'
    with core.open('xb') as handle:
        handle.write((originals / core.name).read_bytes())
    pin = dependency.image_identity(core)
    require(all(compiled['built_executables'][core.name][key] == pin[key] for key in pin), 'RUNTIME_INPUT_CHANGED')
    with urllib.request.urlopen(dependency.URL, timeout=60) as response:
        archive = response.read(dependency.ARCHIVE_BYTES + 1)
    dll = dependency.official_member(archive)
    with (control / 'wintun-0.14.1.zip').open('xb') as handle:
        handle.write(archive)
    with (stage / 'wintun.dll').open('xb') as handle:
        handle.write(dll)
    manifest = {'schema': 1, 'probe_id': probe_id, 'core_path': str(core), 'core_bytes': pin['bytes'], 'core_sha256': pin['sha256'],
        'source_tree_sha256': prepared['source_tree_sha256'], 'build_recipe_sha256': build.source.sha(build.LOCK.read_bytes()),
        'inert_profile_sha256': lock['inert_profile']['sha256']}
    manifest_path = stage / 'runtime-input.json'
    new_json(manifest_path, manifest)
    helper = temp / 'ff-owned-core-runtime-tests/OwnedCoreRuntime.exe'
    helper_pin = dependency.image_identity(helper)
    contracts = subprocess.run([str(helper), '--contracts'], cwd=ROOT, capture_output=True, timeout=120)
    require(contracts.returncode == 0 and contracts.stderr == b'' and
        contracts.stdout.decode('utf-8').splitlines() == [*('PASS ' + label for label in CONTRACT_CASES), 'RESULT 22 passed; 0 skipped']
        and dependency.image_identity(helper) == helper_pin, 'RUNTIME_REPORT_INVALID')
    report['managed_contracts_passed'] = 22
    session = {'manifest': manifest, 'manifest_path': str(manifest_path), 'helper_identity': helper_pin}
    new_json(control / 'session.json', session)
    report.update(checkout_commit=commit, run_id=os.environ.get('GITHUB_RUN_ID'), run_attempt=os.environ.get('GITHUB_RUN_ATTEMPT'),
        source_tree_sha256=manifest['source_tree_sha256'], build_recipe_sha256=manifest['build_recipe_sha256'],
        core_identity=pin, helper_identity=helper_pin, inert_profile_sha256=manifest['inert_profile_sha256'],
        official_dll={'bytes': dependency.MEMBER_BYTES, 'sha256': dependency.MEMBER_SHA})
    report['driver_before'] = driver_inventory('Before')
    report['firewall_prepare'] = firewall('Prepare', session, control, report.setdefault('firewall_checks', []))
    report['firewall_arm'] = firewall('Arm', session, control, report.setdefault('firewall_checks', []))
    require(dependency.image_identity(helper) == helper_pin and dependency.image_identity(core) == pin
        and dependency.sha((stage / 'wintun.dll').read_bytes()) == dependency.MEMBER_SHA, 'RUNTIME_INPUT_CHANGED')
    # Arm is durable before dispatch. Any missing cleanup report leaves the two path blocks in place.
    process = subprocess.run([str(helper), '--manifest', str(manifest_path)], cwd=ROOT, capture_output=True, timeout=120)
    require(process.stderr == b'', 'RUNTIME_REPORT_INVALID')
    managed = valid_runtime(decode(process.stdout), manifest)
    new_json(control / 'managed.json', managed)
    report['managed'] = managed
    report['images_unchanged'] = dependency.image_identity(helper) == helper_pin and dependency.image_identity(core) == pin
    report['dll_unchanged'] = dependency.sha((stage / 'wintun.dll').read_bytes()) == dependency.MEMBER_SHA
    require(report['images_unchanged'] and report['dll_unchanged'], 'RUNTIME_INPUT_CHANGED')
    require(process.returncode == 0 and managed['passed'], 'RUNTIME_RESULT_FAILED')


def restore(report, control):
    plan = control / 'firewall-plan.json'
    if not plan.exists():
        return
    session = decode((control / 'session.json').read_bytes())
    if (control / 'restore.json').exists():
        # A previous failed restore must remain failed, not be overwritten by a second attempt.
        existing = decode((control / 'restore.json').read_bytes())
        require(existing.get('succeeded') is True and existing.get('owned_rules_removed') is True
            and existing.get('original_policy_unchanged') is True, 'RUNTIME_RESTORE_UNCONFIRMED')
    else:
        report['firewall_restore'] = firewall('Restore', session, control, report.setdefault('firewall_checks', []))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--finalize', action='store_true', help='Failure/cancellation recovery only; never starts a core.')
    args = parser.parse_args()
    output = ROOT / 'artifacts/ci/owned-core-runtime.json'
    if args.finalize and output.exists():
        # A completed immutable public record is an idempotent read, not another
        # restoration attempt. In particular no finally block can swallow a
        # failed restoration after an already computed successful return value.
        try:
            existing = decode(output.read_bytes())
            require(existing.get('schema') == 1 and existing.get('kind') == 'finite-owned-core-runtime-adapter'
                and type(existing.get('passed')) is bool, 'RUNTIME_REPORT_INVALID')
            if existing['passed']:
                require(existing.get('managed', {}).get('exact_owned_job_drained') is True
                    and existing.get('managed', {}).get('passed') is True
                    and all(existing.get('firewall_restore', {}).get(key) is True for key in
                        ('owned_rules_removed', 'original_policy_unchanged', 'exact_drain_verified')), 'RUNTIME_RESTORE_UNCONFIRMED')
            return 0 if existing['passed'] else 1
        except Exception:
            print('::error::RUNTIME_EXISTING_RECEIPT_INVALID')
            return 1
    report = {'schema': 1, 'kind': 'finite-owned-core-runtime-adapter', 'passed': False,
        'public_admission': False, 'peer_traffic_verified': False, 'pid_isolation': False,
        'packet_behavior_verified': False, 'runtime_binary_uploaded': False, 'raw_output_retained': False,
        'driver_uninstalled': False}
    control = None
    try:
        require(sys.platform == 'win32' and os.environ.get('GITHUB_ACTIONS') == 'true'
            and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted' and os.environ.get('ImageOS') in ('win25','win25-vs2026'), 'RUNTIME_HOST_REQUIRED')
        # Preserve the runner's lexical absolute spelling, as .NET GetFullPath
        # and the PowerShell path rule do; production input pinning checks the
        # actual handles/reparse boundaries independently.
        temp = Path(os.path.abspath(os.environ['RUNNER_TEMP']))
        candidate_control = temp / 'ff-owned-core-runtime-control'
        if args.finalize:
            control = candidate_control
            require(control.is_dir(), 'RUNTIME_RESTORE_UNCONFIRMED')
            restore(report, control)
            raise ValueError('RUNTIME_PROCESS_FAILED')
        candidate_control.mkdir(exist_ok=False)
        # A failed exclusive mkdir must not authorize Restore on another
        # invocation's existing session. Explicit --finalize is separate.
        control = candidate_control
        run(report, control, temp)
        report['passed'] = True
    except Exception as error:
        report['error_code'] = fixed_error(error)
        report['passed'] = False
    finally:
        if control is not None and control.is_dir():
            try:
                restore(report, control)
            except Exception as error:
                report['restore_error_code'] = fixed_error(error)
                report['passed'] = False
            if 'driver_before' in report:
                try:
                    after = driver_inventory('After')
                    report['driver_after'] = after
                    require(all(report['driver_before'][key] == after[key] for key in ('unrelated_pnp_sha256','unrelated_pnp_count')),
                        'RUNTIME_DRIVER_STATE_INVALID')
                except Exception as error:
                    report['driver_error_code'] = fixed_error(error)
                    report['passed'] = False
    try:
        output.parent.mkdir(parents=True, exist_ok=True)
        new_json(output, report)
    except OSError:
        print('::error::RUNTIME_PUBLIC_RECEIPT_FAILED')
        return 1
    if not report['passed']:
        codes = [report[key] for key in ('error_code', 'restore_error_code', 'driver_error_code') if key in report]
        managed = report.get('managed', {})
        codes += [managed[key] for key in ('stage', 'error_code', 'cleanup_error_code') if managed.get(key) is not None]
        print('::error::OWNED_CORE_RUNTIME_FAILED; ' + '; '.join(codes))
        return 1
    print('OWNED_CORE_RUNTIME_VERIFIED_FINITE_SCOPE')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
