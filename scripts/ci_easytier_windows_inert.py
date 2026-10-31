"""Build a distinct inert-profile owner-read candidate; never execute its core/CLI."""
from __future__ import annotations

import copy
import json
import os
from pathlib import Path
import subprocess
import sys

import ci_easytier_windows_owned_read as base

paired, source, ROOT = base.paired, base.source, base.ROOT
LOCK = ROOT / 'scripts/easytier-windows-inert-owned-read.lock.json'
KIND = 'independent-windows-inert-owned-read-build-only'
FILES = frozenset(('easytier/src/common/config.rs', 'easytier/src/common/global_ctx.rs',
    'easytier/src/common/stun.rs', 'easytier/src/rpc_service/api.rs',
    'easytier/tests/fifth_freedom_inert_profile.toml'))
CODES = frozenset('INERT_RECIPE INERT_BASE INERT_PATCH_SCOPE INERT_SOURCE_CHANGED INERT_CONTRACT INERT_COMMAND INERT_HOST_REQUIRED INERT_CHECKOUT'.split())


def require(condition, code):
    if not condition:
        raise paired.BuildError(code)


def fixed_error(error):
    if type(error) is paired.BuildError and str(error) in CODES:
        return {'error_code': str(error), 'error_type': 'BuildError'}
    return base.failure(error)


def load_inputs(lock, repository=ROOT):
    require(type(lock['schema']) is int and lock['schema'] == 1 and lock['kind'] == KIND, 'INERT_RECIPE')
    previous = json.loads(source.checked_bytes(repository / lock['base_recipe']['path'], lock['base_recipe'], 65536))
    no_packet, read = base.load_inputs(previous, repository)
    base.verify_read(read, repository)
    contract = json.loads(source.checked_bytes(repository / lock['inert_contract']['path'], lock['inert_contract'], 131072))
    require(lock['base_prepared'] == previous['expected_prepared'] == contract['base_prepared']
        and lock['base_recipe'] == contract['base_recipe'] and lock['build'] == previous['build']
        and lock['admission'] == previous['admission'], 'INERT_BASE')
    require(lock['expected_prepared'] == contract['expected_prepared'] and lock['patch'] == contract['patch']
        and lock['files'] == contract['files'] and lock['inert_profile'] == contract['profile']
        and lock['patch']['role'] == 'production-plus-contracts', 'INERT_RECIPE')
    require(len(lock['files']) == 5 and {row['path'] for row in lock['files']} == FILES
        and lock['contracts'] == {'filter': 'fifth_freedom_stun_', 'expected_passed': 13}
        and contract['tests']['filter'] == lock['contracts']['filter']
        and contract['tests']['expected_passed'] == 13, 'INERT_CONTRACT')
    for pin in (lock['patch'], lock['inert_profile']):
        require(source.safe_name(pin['path']), 'INERT_RECIPE')
        source.checked_bytes(repository / pin['path'], pin, 1024 * 1024)
    profile = lock['inert_profile']
    require(profile['source_path'] == 'easytier/tests/fifth_freedom_inert_profile.toml'
        and profile['ipv4'] == '10.254.253.1/24' and profile['conflict_scope'] == '10.254.253.0/24'
        and profile['rpc_arguments'] == ['--rpc-portal', '127.0.0.1:15998']
        and profile['runtime_insertions_only'] == ['instance_id', 'flags.dev_name']
        and profile['runtime_executed'] is False, 'INERT_CONTRACT')
    return previous, no_packet, read


def derive(work, prepared, lock, repository=ROOT):
    # git's ceiling variable requires an absolute path, including local audits
    # performed inside this repository's ignored evidence directory.
    work = work.resolve()
    previous, _, _ = load_inputs(lock, repository)
    base.check_prepared(work, prepared, previous)
    expected = copy.deepcopy(prepared['source_files'])
    for row in lock['files']:
        old = expected['easytier'].get(row['path'])
        require((old is None if row['original_sha256'] is None else
            old == {'bytes': row['original_bytes'], 'sha256': row['original_sha256']}), 'INERT_BASE')
        expected['easytier'][row['path']] = {'bytes': row['patched_bytes'], 'sha256': row['patched_sha256']}
    paired.apply_patch(work / 'easytier-source', lock['patch'], repository)
    actual = {'easytier': paired.inventory(work / 'easytier-source'), 'tun': paired.inventory(work / 'tun-source')}
    require(actual == expected, 'INERT_PATCH_SCOPE')
    result = copy.deepcopy(prepared)
    result.update(kind='independent-windows-inert-source-not-admitted', base_recipe=lock['base_recipe'],
        inert_contract=lock['inert_contract'], inert_profile=lock['inert_profile'], source_files=actual,
        source_tree_sha256=source.sha(source.encoded(actual)), patches=[*prepared['patches'], lock['patch']])
    result['patch_manifest_sha256'] = source.sha(source.encoded(result['patches']))
    check_prepared(work, result, lock)
    require(len(result['patches']) == 13, 'INERT_BASE')
    return result


def check_prepared(work, prepared, lock):
    base.base.require_prepared(work, prepared)
    require(all(prepared[key] == value for key, value in lock['expected_prepared'].items())
        and source.sha(source.encoded(prepared['patches'])) == prepared['patch_manifest_sha256']
        and prepared['source_files']['easytier']['Cargo.lock']['sha256'] == prepared['cargo_lock_patched_sha256'], 'INERT_SOURCE_CHANGED')
    pin = lock['inert_profile']
    source.checked_bytes(work / 'easytier-source' / pin['source_path'], pin, 16384)


def build(work, output, prepared, lock, repository=ROOT, environment=None, cargo='cargo'):
    _, no_packet, read = load_inputs(lock, repository)
    check_prepared(work, prepared, lock)
    output.mkdir(parents=True, exist_ok=False)
    logs = work / 'inert-contract-logs'
    logs.mkdir(exist_ok=False)
    receipt = {'schema': 1, 'kind': KIND, 'status': 'building', 'source_tree_sha256': prepared['source_tree_sha256'],
        'patch_manifest_sha256': prepared['patch_manifest_sha256'], 'cargo_lock_sha256': prepared['cargo_lock_patched_sha256'],
        'recipe_sha256': source.sha(source.encoded(lock)), 'inert_profile': lock['inert_profile'], 'runs': [],
        'runtime_executed': False, 'runtime_binary_uploaded': False, 'public_startup_allowed': False,
        'driver_loaded': False, 'dependency_license_closure_verified': False}
    (output / 'source-receipt.json').write_bytes(source.encoded(prepared))
    env = dict(os.environ if environment is None else environment)
    env.update(CARGO_TARGET_DIR=str(work / 'target'), CARGO_BUILD_JOBS='2', CARGO_INCREMENTAL='0',
        CARGO_NET_GIT_FETCH_WITH_CLI='true', GIT_CEILING_DIRECTORIES=str(work),
        CARGO_ENCODED_RUSTFLAGS='-C\x1ftarget-feature=+crt-static')
    prefix = [cargo, '+' + lock['build']['rust_toolchain'], 'test', '--locked', '--target', lock['build']['target']]
    commands = [('inert-profile-contract', prefix + ['-p', 'easytier', '--lib', '--no-default-features',
        '--features', ','.join(lock['build']['features']), lock['contracts']['filter'], '--', '--test-threads=1'], 13),
        ('owned-read-contract', prefix + ['--manifest-path', str(repository / 'tests/native/windows-owned-read/Cargo.toml'),
        '--lib', '--', '--test-threads=1'], 8)]
    try:
        for stage, command, count in commands:
            print('WINDOWS_INERT_STAGE ' + stage, flush=True)
            out, err = logs / (stage + '.stdout'), logs / (stage + '.stderr')
            with out.open('xb') as stdout, err.open('xb') as stderr:
                completed = subprocess.run(command, cwd=work / 'easytier-source', env=env,
                    stdout=stdout, stderr=stderr, timeout=1800)
            data = out.read_bytes() + err.read_bytes()
            receipt['runs'].append({'stage': stage, 'exit_code': completed.returncode, 'log_sha256': source.sha(data),
                **paired.safe_diagnostics(data, prepared['source_files'])})
            require(completed.returncode == 0, 'INERT_COMMAND')
            paired.linux_build.require_count(data.decode('utf-8', errors='replace'), count)
            base.verify_read(read, repository)
            check_prepared(work, prepared, lock)
        # Retain the reviewed interface8 / typed8 / selected graph / full core+CLI / no-Packet gates.
        compiled = base.base.build(work, output / 'core-build', prepared, no_packet, repository, environment, cargo)
        receipt['runs'].extend(compiled['runs'])
        for key in ('built_executables', 'selected_graph', 'paired_tun_resolution_workspace_union', 'build_inputs'):
            receipt[key] = compiled[key]
        check_prepared(work, prepared, lock)
        base.verify_read(read, repository)
        receipt['status'] = 'windows_inert_owned_read_built_not_executed'
        return receipt
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        receipt.update(status='failed', **fixed_error(error))
        delegated = base.delegate_diagnostics(output / 'core-build/receipt.json', prepared)
        if delegated is not None:
            receipt['runs'] = receipt['runs'][:2] + delegated
        raise
    finally:
        (output / 'receipt.json').write_bytes(source.encoded(receipt))


def main():
    require(sys.platform == 'win32' and os.environ.get('GITHUB_ACTIONS') == 'true'
        and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted', 'INERT_HOST_REQUIRED')
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    require(commit == os.environ.get('GITHUB_SHA') and not subprocess.check_output(
        ['git', 'status', '--porcelain', '--untracked-files=no'], cwd=ROOT), 'INERT_CHECKOUT')
    protoc = Path(os.environ.get('PROTOC', ''))
    paired.require(protoc.is_file() and subprocess.check_output([str(protoc), '--version'], text=True).strip() == 'libprotoc 35.1',
        'WINDOWS_PINNED_PROTOC_REQUIRED')
    lock = json.loads(LOCK.read_bytes())
    previous, no_packet, _ = load_inputs(lock)
    paired_lock = base.base.load_base(no_packet)
    linux, _ = paired.load_inputs(paired_lock)
    work = Path(os.environ['RUNNER_TEMP']) / 'fifth-easytier-windows-inert'
    work.mkdir(exist_ok=False)
    output = ROOT / 'artifacts/ci/easytier-windows-inert'
    output.parent.mkdir(parents=True, exist_ok=True)
    run_path = output.parent / 'easytier-windows-inert-run.json'
    run = {'schema': 1, 'status': 'running', 'checkout_commit': commit, 'run_id': os.environ.get('GITHUB_RUN_ID'),
        'run_attempt': os.environ.get('GITHUB_RUN_ATTEMPT'), 'recipe_sha256': source.sha(LOCK.read_bytes()),
        'runtime_executed': False, 'runtime_binary_uploaded': False, 'public_startup_allowed': False}
    with run_path.open('xb') as handle:
        handle.write(source.encoded(run))
    try:
        for name in ('easytier-fetch', 'tun-fetch'):
            (work / name).mkdir()
        run['stage'] = 'fetch-fixed-archives'
        archive = paired.linux_build.git_archive(work / 'easytier-fetch', linux)
        tun = paired.linux_build.git_archive(work / 'tun-fetch', paired_lock['tun'])
        baseline = paired.prepare(archive, tun, work, paired_lock)
        no_packet_source = base.base.derive(work, baseline, no_packet)
        previous_source = base.derive(work, no_packet_source, previous)
        prepared = derive(work, previous_source, lock)
        run['stage'] = 'contracts-resolution-build'
        result = build(work, output, prepared, lock)
        result.update(checkout_commit=commit, run_id=run['run_id'], run_attempt=run['run_attempt'], recipe_sha256=run['recipe_sha256'])
        (output / 'receipt.json').write_bytes(source.encoded(result))
        run['status'] = 'windows_inert_owned_read_build_verified_not_executed'
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError) as error:
        run.update(status='failed', **fixed_error(error))
        raise
    finally:
        run_path.write_bytes(source.encoded(run))
    print('WINDOWS_INERT_BUILD_VERIFIED_NO_RUNTIME_EXECUTION')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, StopIteration, subprocess.SubprocessError):
        print('WINDOWS_INERT_BUILD_FAILED', file=sys.stderr)
        raise SystemExit(1)
