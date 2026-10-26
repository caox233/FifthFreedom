"""Exercise the real firewall Prepare/unarmed Restore without starting any core.

The only image input is a fresh copy of the runner's Python executable, used as
file data for an application-path rule. It is never executed. Manifest digests
are explicitly synthetic fixture identities, not a build or runtime receipt.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import uuid

import ci_owned_core_runtime as runtime

ROOT = runtime.ROOT


def exercise(report, control, temp):
    probe = uuid.uuid4().hex
    stage = temp / ('ff-core-runtime-' + probe)
    stage.mkdir(exist_ok=False)
    image = stage / 'easytier-core.exe'
    with image.open('xb') as output, Path(sys.executable).open('rb') as source:
        shutil.copyfileobj(source, output)
    pin = runtime.dependency.image_identity(image)
    fixture = hashlib.sha256(b'firewall-only fixture; no EasyTier source/build/runtime').hexdigest()
    manifest = dict(schema=1, probe_id=probe, core_path=str(image), core_bytes=pin['bytes'],
                    core_sha256=pin['sha256'], source_tree_sha256=fixture,
                    build_recipe_sha256=fixture, inert_profile_sha256=fixture)
    path = stage / 'runtime-input.json'
    runtime.new_json(path, manifest)
    session = {'manifest': manifest, 'manifest_path': str(path)}
    runtime.new_json(control / 'session.json', session)
    report['prepare'] = runtime.firewall('Prepare', session, control, report['firewall_checks'])
    runtime.require(report['prepare']['armed'] is False, 'RUNTIME_FIREWALL_FAILED')
    # Deliberately do not Arm: no process/job exists here, so a matching actual
    # job-drain receipt cannot be supplied. Full runtime tests own that boundary.


def main():
    report = dict(schema=1, kind='firewall-prepare-unarmed-restore-prerequisite', passed=False,
                  firewall_checks=[], synthetic_image_input=True, image_executed=False,
                  core_executed=False, driver_loaded=False, game_executed=False,
                  armed=False, exact_job_drain_exercised=False, pid_isolation=False,
                  packet_behavior_verified=False, full_runtime_verified=False,
                  complete_package_ready=False, raw_policy_published=False)
    control = None
    try:
        runtime.require(sys.platform == 'win32' and os.environ.get('GITHUB_ACTIONS') == 'true'
                        and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted'
                        and os.environ.get('ImageOS') in ('win25', 'win25-vs2026'),
                        'RUNTIME_HOST_REQUIRED')
        temp = Path(os.path.abspath(os.environ['RUNNER_TEMP']))
        candidate_control = temp / 'ff-firewall-prerequisite-control'
        candidate_control.mkdir(exist_ok=False)
        # Rejecting an existing directory does not confer cleanup authority for
        # its prior session or rules. Only this successful creation owns it.
        control = candidate_control
        exercise(report, control, temp)
        report['passed'] = True
    except Exception as error:
        report['error_code'] = runtime.fixed_error(error)
    finally:
        if control is not None and (control / 'firewall-plan.json').exists():
            try:
                session = runtime.decode((control / 'session.json').read_bytes())
                report['restore'] = runtime.firewall('Restore', session, control, report['firewall_checks'])
                runtime.require(report['restore']['armed'] is False
                                and report['restore']['exact_drain_verified'] is False,
                                'RUNTIME_RESTORE_UNCONFIRMED')
            except Exception as error:
                report['restore_error_code'] = runtime.fixed_error(error)
                report['passed'] = False
        elif report['passed']:
            report.update(passed=False, restore_error_code='RUNTIME_RESTORE_UNCONFIRMED')
    try:
        output = ROOT / 'artifacts/ci/windows-firewall-prerequisite.json'
        output.parent.mkdir(parents=True, exist_ok=True)
        runtime.new_json(output, report)
    except OSError:
        print('::error::FIREWALL_PREREQUISITE_RECEIPT_FAILED')
        return 1
    print(json.dumps({key: report[key] for key in ('kind', 'passed', 'firewall_checks')}))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
