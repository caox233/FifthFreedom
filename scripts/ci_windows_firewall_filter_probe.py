"""Build and invoke a diagnostic-only, read-only WFP filter shape observer."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import struct
import subprocess
import sys

import ci_owned_core_runtime as runtime

ROOT = runtime.ROOT
SOURCE = 'scripts/windows_firewall_filter_probe.cpp'
WORK = 'ff-wfp-filter-probe'
CONTRACT_COUNT = 20
LAYERS = ('CONNECT_V4', 'CONNECT_V6', 'RECV_V4', 'RECV_V6')
COUNTS = frozenset('enumerated exact_app unsupported block permit callout other_action disabled profile_conditions '
    'interface_conditions other_conditions app_only clear_action_right provider_context weight_empty weight_uint8 '
    'weight_uint64 weight_other'.split())
FALSE_FIELDS = frozenset('arbitration_verified policy_changed admission_changed core_executed staging_image_executed '
    'driver_loaded raw_policy_published'.split())
NATIVE_ERRORS = frozenset('WFP_HOST_REQUIRED WFP_INPUT_INVALID WFP_APP_ID_FAILED WFP_ENGINE_FAILED '
    'WFP_TRANSACTION_FAILED WFP_ENUMERATION_FAILED WFP_ENUMERATION_LIMIT WFP_CLEANUP_FAILED WFP_OPERATION_FAILED'.split())
ERRORS = NATIVE_ERRORS | frozenset('WFP_BUILD_INPUT_INVALID WFP_COMPILER_REQUIRED WFP_COMPILE_FAILED '
    'WFP_CONTRACT_FAILED WFP_IMAGE_CHANGED WFP_RESULT_INVALID WFP_BUILD_FAILED'.split())


def require(value, code):
    if not value:
        raise ValueError(code)


def fixed_error(error):
    return str(error) if type(error) is ValueError and str(error) in ERRORS else 'WFP_OPERATION_FAILED'


def read_regular(path, limit):
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and not path.is_symlink()
        and not (getattr(before, 'st_file_attributes', 0) & 0x400)
        and 0 < before.st_size <= limit, 'WFP_INPUT_INVALID')
    with path.open('rb') as stream:
        data = stream.read(limit + 1)
    after = path.lstat()
    keys = ('st_dev', 'st_ino', 'st_size', 'st_mtime_ns', 'st_ctime_ns', 'st_mode')
    require(len(data) == before.st_size and all(getattr(before, key) == getattr(after, key) for key in keys), 'WFP_IMAGE_CHANGED')
    return data


def sha(data):
    return hashlib.sha256(data).hexdigest()


def decode(data):
    try:
        return runtime.decode(data, 65536)
    except Exception:
        raise ValueError('WFP_RESULT_INVALID') from None


def host():
    require(sys.platform == 'win32' and os.environ.get('GITHUB_ACTIONS') == 'true'
        and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted'
        and os.environ.get('ImageOS') in ('win25', 'win25-vs2026')
        and bool(os.environ.get('RUNNER_TEMP')), 'WFP_HOST_REQUIRED')
    return Path(os.path.abspath(os.environ['RUNNER_TEMP']))


def identity():
    fields = {'checkout_commit': os.environ.get('GITHUB_SHA'), 'run_id': os.environ.get('GITHUB_RUN_ID'),
              'run_attempt': os.environ.get('GITHUB_RUN_ATTEMPT')}
    require(type(fields['checkout_commit']) is str and re.fullmatch('[0-9a-f]{40}', fields['checkout_commit'])
        and all(type(fields[key]) is str and re.fullmatch('[1-9][0-9]{0,19}', fields[key])
                for key in ('run_id', 'run_attempt')), 'WFP_BUILD_INPUT_INVALID')
    return fields


def image_pin(path):
    data = read_regular(path, 8 * 1024 * 1024)
    require(len(data) >= 256 and data[:2] == b'MZ', 'WFP_BUILD_INPUT_INVALID')
    offset = struct.unpack_from('<I', data, 0x3c)[0]
    require(0x40 <= offset <= len(data)-26 and data[offset:offset+4] == b'PE\0\0'
        and struct.unpack_from('<H', data, offset+4)[0] == 0x8664
        and struct.unpack_from('<H', data, offset+24)[0] == 0x20b
        and struct.unpack_from('<H', data, offset+22)[0] & 2
        and not struct.unpack_from('<H', data, offset+22)[0] & 0x2000, 'WFP_BUILD_INPUT_INVALID')
    return {'bytes': len(data), 'sha256': sha(data)}


def contracts(data):
    value = decode(data)
    expected = {'schema': 1, 'kind': 'wfp-filter-pure-contracts', 'passed': True,
        'required_checks': CONTRACT_COUNT, 'passed_checks': CONTRACT_COUNT, 'skipped_checks': 0,
        'wfp_api_called': False, 'policy_changed': False, 'core_executed': False}
    require(type(value) is dict and set(value) == set(expected)
        and all(type(value[key]) is type(want) and value[key] == want for key, want in expected.items()), 'WFP_CONTRACT_FAILED')
    return value


def observation(data):
    value = decode(data)
    fields = FALSE_FIELDS | {'schema', 'kind', 'passed', 'coverage', 'observer_executed', 'read_only_transaction', 'transaction_closed', 'error_code', 'layers'}
    require(type(value) is dict and set(value) == fields and type(value['schema']) is int and value['schema'] == 1
        and value['kind'] == 'wfp-exact-app-filter-diagnostic' and value['coverage'] == 'UNKNOWN'
        and value['observer_executed'] is True
        and all(value[key] is False for key in FALSE_FIELDS)
        and all(type(value[key]) is bool for key in ('passed', 'read_only_transaction', 'transaction_closed'))
        and (value['error_code'] is None or (type(value['error_code']) is str and value['error_code'] in NATIVE_ERRORS)), 'WFP_RESULT_INVALID')
    require(type(value['layers']) is list and len(value['layers']) == len(LAYERS), 'WFP_RESULT_INVALID')
    total = 0
    for row, name in zip(value['layers'], LAYERS):
        require(type(row) is dict and set(row) == COUNTS | {'name', 'complete'}
            and row['name'] == name and type(row['complete']) is bool
            and all(type(row[key]) is int and 0 <= row[key] <= 20000 for key in COUNTS), 'WFP_RESULT_INVALID')
        total += row['enumerated']
        require(row['exact_app'] + row['unsupported'] <= row['enumerated']
            and row['block'] + row['permit'] + row['callout'] + row['other_action'] == row['exact_app']
            and sum(row[key] for key in ('weight_empty', 'weight_uint8', 'weight_uint64', 'weight_other')) == row['exact_app']
            and all(row[key] <= row['exact_app'] for key in COUNTS - {'enumerated', 'exact_app', 'unsupported'}), 'WFP_RESULT_INVALID')
    require(total <= 20000 and value['passed'] == (value['error_code'] is None), 'WFP_RESULT_INVALID')
    if value['passed']:
        require(value['read_only_transaction'] and value['transaction_closed']
            and all(row['complete'] for row in value['layers']), 'WFP_RESULT_INVALID')
    return value


def build(report):
    temp = host()
    pins = identity()
    revision = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True, timeout=10)
    require(revision.returncode == 0 and revision.stdout.decode('ascii').strip() == pins['checkout_commit'], 'WFP_BUILD_INPUT_INVALID')
    status = subprocess.run(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=ROOT, capture_output=True, timeout=10)
    require(status.returncode == 0 and not status.stdout, 'WFP_BUILD_INPUT_INVALID')
    compiler = shutil.which('cl.exe')
    require(compiler is not None, 'WFP_COMPILER_REQUIRED')
    work = temp / WORK
    work.mkdir(exist_ok=False)
    source = read_regular(ROOT / SOURCE, 65536)
    pins['source_sha256'] = sha(source)
    local = work / 'probe.cpp'
    with local.open('xb') as output:
        output.write(source)
    executable = work / 'WfpFilterProbe.exe'
    command = [compiler, '/nologo', '/std:c++17', '/EHsc', '/MT', '/O2', '/W4', '/WX', str(local),
        '/Fe:' + str(executable), '/Fo:' + str(work / 'probe.obj'), '/link', '/MACHINE:X64',
        '/INCREMENTAL:NO', 'Fwpuclnt.lib', 'Advapi32.lib']
    compiled = subprocess.run(command, cwd=work, capture_output=True, timeout=180)
    report['compiler_codes'] = sorted(set(re.findall(r'\b(?:C[0-9]{4}|LNK[0-9]{4})\b',
        (compiled.stdout + compiled.stderr).decode('utf-8', errors='replace'))))
    require(compiled.returncode == 0, 'WFP_COMPILE_FAILED')
    pins['image'] = image_pin(executable)
    result = subprocess.run([str(executable), '--contracts'], cwd=work, capture_output=True, timeout=15)
    require(result.returncode == 0 and not result.stderr, 'WFP_CONTRACT_FAILED')
    report['contracts'] = contracts(result.stdout)
    require(image_pin(executable) == pins['image'] and sha(read_regular(ROOT / SOURCE, 65536)) == pins['source_sha256'], 'WFP_IMAGE_CHANGED')
    runtime.new_json(work / 'build-session.json', pins)
    report.update(passed=True, **pins)


def validate_session(session, temp):
    require(type(session) is dict and set(session) == {'manifest', 'manifest_path'}, 'WFP_INPUT_INVALID')
    manifest = session['manifest']
    require(type(manifest) is dict and set(manifest) == {'schema','probe_id','core_path','core_bytes','core_sha256',
        'source_tree_sha256','build_recipe_sha256','inert_profile_sha256'}
        and type(manifest['schema']) is int and manifest['schema'] == 1
        and type(manifest['probe_id']) is str and re.fullmatch('[0-9a-f]{32}', manifest['probe_id'])
        and all(type(manifest[key]) is str and re.fullmatch('[0-9a-f]{64}', manifest[key])
            for key in ('core_sha256','source_tree_sha256','build_recipe_sha256','inert_profile_sha256')), 'WFP_INPUT_INVALID')
    image = temp / ('ff-core-runtime-' + manifest['probe_id']) / 'easytier-core.exe'
    parent = image.parent.lstat()
    require(stat.S_ISDIR(parent.st_mode) and not image.parent.is_symlink()
        and not (getattr(parent, 'st_file_attributes', 0) & 0x400), 'WFP_INPUT_INVALID')
    require(manifest.get('core_path') == str(image) and session['manifest_path'] == str(image.parent / 'runtime-input.json')
        and decode(read_regular(Path(session['manifest_path']), 65536)) == manifest, 'WFP_INPUT_INVALID')
    data = read_regular(image, 268435456)
    require(type(manifest.get('core_bytes')) is int and len(data) == manifest['core_bytes']
        and sha(data) == manifest.get('core_sha256'), 'WFP_IMAGE_CHANGED')
    return image


def observe(session):
    """Called only for the fresh prerequisite's own plan, before unarmed Restore."""
    temp = host()
    image = validate_session(session, temp)
    work = temp / WORK
    pins = decode(read_regular(work / 'build-session.json', 65536))
    require(type(pins) is dict and set(pins) == {'checkout_commit', 'run_id', 'run_attempt', 'source_sha256', 'image'}
        and all(pins[key] == value for key, value in identity().items())
        and pins['source_sha256'] == sha(read_regular(ROOT / SOURCE, 65536)), 'WFP_BUILD_INPUT_INVALID')
    executable = work / 'WfpFilterProbe.exe'
    require(image_pin(executable) == pins['image'], 'WFP_IMAGE_CHANGED')
    result = subprocess.run([str(executable), '--app', str(image)], cwd=work, capture_output=True, timeout=45)
    require(not result.stderr, 'WFP_RESULT_INVALID')
    value = observation(result.stdout)
    require(result.returncode == (0 if value['passed'] else 1) and image_pin(executable) == pins['image'], 'WFP_RESULT_INVALID')
    validate_session(session, temp)
    return value


def main():
    report = dict(schema=1, kind='wfp-filter-build-and-pure-contracts', passed=False,
                  policy_changed=False, core_executed=False, runtime_probe_executed=False)
    try:
        require(sys.argv[1:] == ['--build'], 'WFP_INPUT_INVALID')
        build(report)
    except Exception as error:
        report['error_code'] = fixed_error(error)
    try:
        path = ROOT / 'artifacts/ci/windows-firewall-filter-build.json'
        path.parent.mkdir(parents=True, exist_ok=True)
        runtime.new_json(path, report)
    except OSError:
        print('WFP_BUILD_RECEIPT_FAILED')
        return 1
    print(json.dumps({'kind': report['kind'], 'passed': report['passed'], 'error_code': report.get('error_code')}))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
