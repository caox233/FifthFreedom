"""Finite actual Wintun session checks, only on a clean disposable hosted VM."""
from __future__ import annotations
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import threading
import time
import urllib.request
import uuid
import zipfile

import ci_easytier_build as git_source
import ci_easytier_windows_build as paired
import ci_prepare_easytier_source as source

ROOT = source.ROOT
LOCK = ROOT / 'scripts/wintun-session-probe.lock.json'
PROBE_CODES = frozenset('OK ARGUMENTS DLL INVENTORY COLLISION CREATE_IO CREATE_DRIVER CREATE_LOADER CREATE_SIGNER_OR_STRING CREATE_CONFIGURATION OBSERVER IDENTITY DROP_NOT_REVOKED ADAPTER_RETAINED REVOCATION_NOT_STICKY ASYNC_CREATE SHUTDOWN SHUTDOWN_NOT_REVOKED WORKER_TIMEOUT WORKER_READ UNEXPECTED_PACKET IDENTITY_CHANGED PANIC'.split())
ENV_CODES = frozenset('OK HOST PNP DRIVER_STORE DRIVER_SERVICE PREEXISTING_STATE'.split())

HARNESS_CODES = frozenset('BUILD CHECKOUT COMMAND_TIMEOUT DEVICE_RETAINED DLL_DIGEST DLL_SIZE ENV_DIRTY ENV_DRIVER_SERVICE ENV_DRIVER_STORE ENV_HOST ENV_OK ENV_PNP ENV_PREEXISTING_STATE ENV_SCHEMA ENV_STDERR GUID_COLLISION HOST JSON_DUPLICATE JSON_INVALID JSON_LIMIT METADATA OUTPUT_LIMIT OUTPUT_READ OUTPUT_THREAD PNP_CHANGED PROBE_EXIT PROBE_FAILED PROBE_INCOMPLETE PROBE_SCHEMA PROBE_SCOPE PROBE_STDERR RUNTIME_INPUT_CHANGED SOURCE_BASE SOURCE_CHANGED SOURCE_PATCH SOURCE_SCOPE TUN_FEATURES TUN_RESOLUTION UNEXPECTED_DEPENDENCY ZIP_DIGEST ZIP_MEMBER'.split())

class ProbeError(ValueError):
    pass


def require(value, code):
    if not value:
        raise ProbeError(code)


def bounded_command(command, *, cwd, timeout, limit=65536, env=None):
    """Drain fixed memory; every post-Popen path reaps only this exact child."""
    buffers = [bytearray(), bytearray()]
    overflow = threading.Event()
    faults = threading.Event()
    def drain(stream, output):
        try:
            while data := stream.read(4096):
                if len(output) + len(data) > limit:
                    overflow.set()
                else:
                    output.extend(data)
        except OSError:
            faults.set()
        finally:
            stream.close()
    started = []
    owned_streams = []
    failure = None
    cleanup_failure = None
    process = subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        for stream, output in zip((process.stdout, process.stderr), buffers):
            thread = threading.Thread(target=drain, args=(stream, output), daemon=True)
            thread.start()
            started.append(thread); owned_streams.append(stream)
        deadline = time.monotonic() + timeout
        while process.poll() is None:
            require(not overflow.is_set(), 'OUTPUT_LIMIT')
            require(not faults.is_set(), 'OUTPUT_READ')
            require(time.monotonic() < deadline, 'COMMAND_TIMEOUT')
            time.sleep(0.025)
    except BaseException as error:
        failure = error
    finally:
        try:
            if process.poll() is None:
                process.terminate()
        except BaseException as error:
            cleanup_failure = error
        # Even terminate failure must reach wait/kill on this exact Popen.
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            try:
                process.kill(); process.wait(timeout=3)
            except BaseException as error:
                cleanup_failure = cleanup_failure or error
        except BaseException as error:
            cleanup_failure = cleanup_failure or error
        for stream in (process.stdout, process.stderr):
            if stream not in owned_streams:
                try:
                    stream.close()
                except BaseException as error:
                    cleanup_failure = cleanup_failure or error
        for thread in started:
            try:
                thread.join(timeout=3)
                require(not thread.is_alive(), 'OUTPUT_THREAD')
            except BaseException as error:
                cleanup_failure = cleanup_failure or error
    if failure is not None:
        raise failure
    if cleanup_failure is not None:
        raise cleanup_failure
    require(not overflow.is_set() and not faults.is_set(), 'OUTPUT_LIMIT')
    return process.returncode, bytes(buffers[0]), bytes(buffers[1])


def strict_json(data):
    require(len(data) <= 65536, 'JSON_LIMIT')
    def unique(items):
        result = {}
        for key, value in items:
            require(key not in result, 'JSON_DUPLICATE')
            result[key] = value
        return result
    try:
        value = json.loads(data, object_pairs_hook=unique)
    except (UnicodeError, json.JSONDecodeError):
        raise ProbeError('JSON_INVALID') from None
    require(type(value) is dict, 'JSON_INVALID')
    return value


def validate_probe(data, exit_code):
    row = strict_json(data)
    require(set(row) == {'schema','code','passed','unrelated_identities_equal','public_admission','network_configured','packets_sent'}, 'PROBE_SCHEMA')
    require(type(row['schema']) is int and row['schema'] == 1 and row['code'] in PROBE_CODES, 'PROBE_SCHEMA')
    require(type(row['passed']) is int and 0 <= row['passed'] <= 2, 'PROBE_SCHEMA')
    require(all(type(row[x]) is bool for x in ('unrelated_identities_equal','public_admission','network_configured','packets_sent')), 'PROBE_SCHEMA')
    require(not any(row[x] for x in ('public_admission','network_configured','packets_sent')), 'PROBE_SCOPE')
    require((exit_code == 0) == (row['code'] == 'OK'), 'PROBE_EXIT')
    if exit_code == 0:
        require(row['passed'] == 2 and row['unrelated_identities_equal'], 'PROBE_INCOMPLETE')
    return row


def environment(phase, work):
    code, stdout, stderr = bounded_command(['pwsh','-NoLogo','-NoProfile','-NonInteractive','-File',
        str(ROOT/'scripts/ci_wintun_session_environment.ps1'), '-Phase', phase], cwd=work, timeout=120)
    require(not stderr, 'ENV_STDERR')
    return validate_environment(stdout, code, phase)


def validate_environment(stdout, code, phase):
    row = strict_json(stdout)
    require(type(row.get('schema')) is int and row.get('schema') == 1 and row.get('phase') == phase and row.get('code') in ENV_CODES, 'ENV_SCHEMA')
    if row['code'] != 'OK':
        require(set(row) == {'schema','code','phase'} and code != 0, 'ENV_SCHEMA')
        raise ProbeError('ENV_' + row['code'])
    require(set(row) == {'schema','code','phase','clean','wintun_devices','wintun_packages','wintun_services',
        'driver_registry','driver_file','unrelated_pnp_sha256','unrelated_pnp_count'}, 'ENV_SCHEMA')
    require(code == 0 and all(type(row[x]) is bool for x in ('clean','driver_registry','driver_file')), 'ENV_SCHEMA')
    require(all(type(row[x]) is int and 0 <= row[x] <= 20000 for x in ('wintun_devices','wintun_packages','wintun_services','unrelated_pnp_count')), 'ENV_SCHEMA')
    require(type(row['unrelated_pnp_sha256']) is str and re.fullmatch('[0-9a-f]{64}',row['unrelated_pnp_sha256']) is not None, 'ENV_SCHEMA')
    require(phase != 'Before' or row['clean'] and not any(row[x] for x in ('wintun_devices','wintun_packages','wintun_services','driver_registry','driver_file')), 'ENV_DIRTY')
    return row


def validate_postflight(before, after):
    require(after['unrelated_pnp_sha256'] == before['unrelated_pnp_sha256']
            and after['unrelated_pnp_count'] == before['unrelated_pnp_count'], 'PNP_CHANGED')
    require(after['wintun_devices'] == 0, 'DEVICE_RETAINED')


def prepare(archive, work, lock):
    files, _ = source.archive_files(source.checked_bytes(archive, lock['tun']['archive']), lock['tun'])
    directory = work/'tun-source'
    paired.write_tree(directory, files)
    expected = paired.inventory(directory)
    recipe = json.loads(source.checked_bytes(ROOT/lock['session_recipe']['path'],lock['session_recipe']))
    group, = [x for x in recipe['groups'] if x['name'] == 'tun']
    for item in group['files']:
        require(expected.get(item['path'],{}).get('sha256') == item['original_sha256'], 'SOURCE_BASE')
    paired.apply_patch(directory,lock['patch'])
    for item in group['files']:
        data = (directory/item['path']).read_bytes()
        require(source.sha(data) == item['patched_sha256'], 'SOURCE_PATCH')
        expected[item['path']] = {'bytes':len(data),'sha256':source.sha(data)}
    require(paired.inventory(directory) == expected, 'SOURCE_SCOPE')
    probe = work/'probe'
    probe.mkdir()
    for item in lock['probe_files']:
        data = source.checked_bytes(ROOT/item['path'],item)
        path = probe/item['destination']; path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(data)
    return probe, expected


def validate_resolution(metadata, directory):
    rows = [p for p in metadata['packages'] if p['name'] == 'tun-easytier']
    require(len(rows) == 1 and rows[0].get('source') is None
            and Path(rows[0]['manifest_path']).resolve() == (directory/'Cargo.toml').resolve(), 'TUN_RESOLUTION')
    nodes = [n for n in metadata['resolve']['nodes'] if n['id'] == rows[0]['id']]
    require(len(nodes) == 1 and set(nodes[0]['features']) == {'async','futures-core','tokio','tokio-util'}, 'TUN_FEATURES')
    require(not any(p['name'] in {'easytier','pcap','pcap-sys','dns-lookup'} for p in metadata['packages']), 'UNEXPECTED_DEPENDENCY')


def extract_dll(data, directory, lock):
    require(len(data) == lock['official_zip']['bytes'] and source.sha(data) == lock['official_zip']['sha256'], 'ZIP_DIGEST')
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        member = lock['dll']['member']
        require(archive.namelist().count(member) == 1, 'ZIP_MEMBER')
        info = archive.getinfo(member)
        require(info.file_size == lock['dll']['bytes'], 'DLL_SIZE')
        dll = archive.read(member)
    require(source.sha(dll) == lock['dll']['sha256'], 'DLL_DIGEST')
    path = directory/'wintun.dll'
    with path.open('xb') as stream:
        stream.write(dll)
    return path


def main():
    receipt = {'schema':1,'status':'failed','stage':'HOST','public_admission':False,'runtime_executed':False,
               'binary_uploaded':False,'network_configured':False,'packets_sent':False}
    output = ROOT/'artifacts/ci/wintun-session-probe.json'
    output.parent.mkdir(parents=True,exist_ok=True)
    try:
        require(sys.platform == 'win32' and os.environ.get('GITHUB_ACTIONS') == 'true'
                and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted' and os.environ.get('ImageOS') == 'win25','HOST')
        commit = subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True,timeout=10).strip()
        require(commit == os.environ.get('GITHUB_SHA') and not subprocess.check_output(
            ['git','status','--porcelain','--untracked-files=no'],cwd=ROOT,timeout=10),'CHECKOUT')
        lock = json.loads(LOCK.read_bytes())
        work = Path(os.environ['RUNNER_TEMP'])/('ff-wintun-probe-'+uuid.uuid4().hex); work.mkdir()
        receipt.update(checkout_commit=commit, run_id=os.environ.get('GITHUB_RUN_ID'),run_attempt=os.environ.get('GITHUB_RUN_ATTEMPT'),lock_sha256=source.sha(LOCK.read_bytes()))
        receipt['stage'] = 'SOURCE'
        archive = git_source.git_archive(work,lock['tun'])
        probe, expected = prepare(archive,work,lock)
        receipt['source_tree_sha256'] = source.sha(source.encoded(expected))
        receipt['cargo_lock_sha256'] = source.sha((probe/'Cargo.lock').read_bytes())
        receipt['stage'] = 'BUILD'
        env = dict(os.environ,CARGO_TARGET_DIR=str(work/'target'),CARGO_INCREMENTAL='0',CARGO_BUILD_JOBS='2',CARGO_PROFILE_DEV_DEBUG='0',CARGO_ENCODED_RUSTFLAGS='')
        code, metadata, _ = bounded_command(['cargo','+1.95.0','metadata','--locked','--format-version','1',
            '--filter-platform','x86_64-pc-windows-msvc'],cwd=probe,timeout=300,limit=4*1024*1024,env=env)
        require(code == 0, 'METADATA')
        validate_resolution(json.loads(metadata),work/'tun-source')
        code, stdout, stderr = bounded_command(['cargo','+1.95.0','build','--locked','--target','x86_64-pc-windows-msvc'],cwd=probe,timeout=900,limit=4*1024*1024,env=env)
        receipt['compiler_codes'] = sorted(set(re.findall(r'error\[(E[0-9]{4})\]',stderr.decode(errors='replace'))))
        require(code == 0,'BUILD')
        require(paired.inventory(work/'tun-source') == expected and source.sha((probe/'Cargo.lock').read_bytes()) == receipt['cargo_lock_sha256'],'SOURCE_CHANGED')
        binary = work/'target/x86_64-pc-windows-msvc/debug/ff-wintun-session-probe.exe'
        data = binary.read_bytes(); paired.pe_identity(data)
        receipt['executable_sha256'] = source.sha(data)
        receipt['stage'] = 'DLL'
        with urllib.request.urlopen(lock['official_zip']['url'],timeout=60) as response:
            data = response.read(lock['official_zip']['bytes']+1)
        dll = extract_dll(data,work,lock)
        receipt['dll_sha256'] = lock['dll']['sha256']
        receipt['stage'] = 'PREFLIGHT'
        before = environment('Before',work); receipt['before'] = before
        receipt['stage'] = 'RUNTIME'
        first, second = uuid.uuid4().hex, uuid.uuid4().hex
        require(first != second, 'GUID_COLLISION')
        receipt['runtime_executed'] = True
        failure = None
        try:
            code, stdout, stderr = bounded_command([str(binary),str(dll),first,second],cwd=work,timeout=90)
            require(not stderr, 'PROBE_STDERR')
            receipt['probe'] = validate_probe(stdout,code)
            require(code == 0, 'PROBE_FAILED')
        except Exception as error:
            failure = error
        try:
            after = environment('After',work); receipt['after'] = after
            validate_postflight(before,after)
            require(source.sha(binary.read_bytes()) == receipt['executable_sha256']
                    and source.sha(dll.read_bytes()) == receipt['dll_sha256'],'RUNTIME_INPUT_CHANGED')
        except Exception as error:
            receipt['postflight_failed'] = True
            if failure is None:
                receipt['stage'] = 'POSTFLIGHT'; failure = error
        if failure is not None:
            raise failure
        receipt.update(status='two_real_sessions_verified_not_admitted',stage='COMPLETE')
        return 0
    except Exception as error:
        # Exception text may include URLs or system paths; only our static codes
        # and source-known compiler E numbers reach public artifacts.
        receipt['error_code'] = str(error) if isinstance(error,ProbeError) and str(error) in HARNESS_CODES else 'PROBE_HARNESS_FAILED'
        print('::error::WINTUN_SESSION_PROBE_FAILED stage='+receipt['stage']+' code='+receipt['error_code'])
        return 1
    finally:
        output.write_bytes(source.encoded(receipt))

if __name__ == '__main__':
    raise SystemExit(main())
