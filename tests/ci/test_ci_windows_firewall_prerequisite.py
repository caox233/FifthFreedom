"""Failure/cleanup ordering only; actual Windows firewall runs in the hosted lane."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import ci_windows_firewall_prerequisite as prerequisite


class FirewallPrerequisiteTests(unittest.TestCase):
    def invoke(self, prepare_error=None, restore_error=None, diagnostic_error=None, diagnostic_passed=True):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            calls = []

            def firewall(mode, session, control, observations):
                calls.append(mode)
                self.assertNotEqual(mode, 'Arm')
                self.assertTrue(Path(session['manifest']['core_path']).is_file())
                # Represent the real script's durable plan before a failed
                # readback, so the finally path must attempt unarmed Restore.
                if mode == 'Prepare':
                    prerequisite.runtime.new_json(control / 'firewall-plan.json', {'fixture': True})
                    if prepare_error:
                        raise ValueError(prepare_error)
                if mode == 'Restore' and restore_error:
                    raise ValueError(restore_error)
                return {'armed': False, 'exact_drain_verified': False}

            def diagnostic(session):
                calls.append('WFP')
                self.assertTrue(Path(session['manifest']['core_path']).is_file())
                if diagnostic_error:
                    raise ValueError(diagnostic_error)
                return {'passed': diagnostic_passed, 'coverage': 'UNKNOWN', 'admission_changed': False}

            env = {'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
                   'ImageOS': 'win25', 'RUNNER_TEMP': str(root)}
            with patch.object(prerequisite, 'ROOT', root), patch.object(prerequisite.sys, 'platform', 'win32'), \
                    patch.dict(prerequisite.os.environ, env), patch.object(prerequisite.runtime, 'firewall', side_effect=firewall), \
                    patch.object(prerequisite.wfp, 'observe', side_effect=diagnostic):
                code = prerequisite.main()
            result = json.loads((root / 'artifacts/ci/windows-firewall-prerequisite.json').read_bytes())
            self.assertEqual(calls, ['Prepare', 'WFP', 'Restore'])
            self.assertFalse(result['image_executed'])
            self.assertFalse(result['exact_job_drain_exercised'])
            return code, result

    def test_unarmed_restore_success_is_only_firewall_prerequisite(self):
        code, result = self.invoke()
        self.assertEqual(code, 0)
        self.assertTrue(result['passed'])
        self.assertFalse(result['full_runtime_verified'])

    def test_prepare_readback_failure_is_retained_even_after_restore(self):
        code, result = self.invoke(prepare_error='FIREWALL_RULE_CHANGED')
        self.assertEqual(code, 1)
        self.assertFalse(result['passed'])
        self.assertEqual(result['error_code'], 'FIREWALL_RULE_CHANGED')

    def test_late_restore_failure_cannot_return_previous_success(self):
        code, result = self.invoke(restore_error='FIREWALL_BASELINE_CHANGED')
        self.assertEqual(code, 1)
        self.assertFalse(result['passed'])
        self.assertEqual(result['restore_error_code'], 'FIREWALL_BASELINE_CHANGED')

    def test_original_and_restore_failure_are_separate(self):
        code, result = self.invoke(prepare_error='FIREWALL_RULE_CHANGED', restore_error='FIREWALL_RULE_CHANGED')
        self.assertEqual(code, 1)
        self.assertEqual(result['error_code'], 'FIREWALL_RULE_CHANGED')
        self.assertEqual(result['restore_error_code'], 'FIREWALL_RULE_CHANGED')

    def test_diagnostic_failure_preserves_prepare_failure_and_still_restores(self):
        code, result = self.invoke(prepare_error='FIREWALL_POLICY_UNAVAILABLE', diagnostic_error='WFP_RESULT_INVALID')
        self.assertEqual(code, 1)
        self.assertEqual(result['error_code'], 'FIREWALL_POLICY_UNAVAILABLE')
        self.assertEqual(result['wfp_diagnostic_error'], 'WFP_RESULT_INVALID')
        self.assertIn('restore', result)

    def test_diagnostic_failure_after_prepare_success_still_fails_and_restores(self):
        code, result = self.invoke(diagnostic_error='private raw exception')
        self.assertEqual(code, 1)
        self.assertEqual(result['wfp_diagnostic_error'], 'WFP_OPERATION_FAILED')
        self.assertNotIn('private', json.dumps(result))
        self.assertIn('restore', result)

    def test_native_failure_receipt_stays_separate_from_prepare_and_restore(self):
        code, result = self.invoke(prepare_error='FIREWALL_POLICY_UNAVAILABLE', diagnostic_passed=False)
        self.assertEqual(code, 1)
        self.assertEqual(result['error_code'], 'FIREWALL_POLICY_UNAVAILABLE')
        self.assertFalse(result['wfp_diagnostic']['passed'])
        self.assertIn('restore', result)

    def test_wrong_host_cannot_copy_input_or_invoke_firewall(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with patch.object(prerequisite, 'ROOT', root), patch.object(prerequisite.sys, 'platform', 'linux'), \
                    patch.object(prerequisite, 'exercise') as exercise:
                self.assertEqual(prerequisite.main(), 1)
                exercise.assert_not_called()
            result = json.loads((root / 'artifacts/ci/windows-firewall-prerequisite.json').read_bytes())
            self.assertEqual(result['error_code'], 'RUNTIME_HOST_REQUIRED')

    def test_preexisting_control_is_not_owned_and_cannot_be_restored(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            control = root / 'ff-firewall-prerequisite-control'
            control.mkdir()
            for name in ('firewall-plan.json', 'session.json'):
                (control / name).write_text('{"prior":"do not modify"}')
            before = {p.name: p.read_bytes() for p in control.iterdir()}
            env = {'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
                   'ImageOS': 'win25', 'RUNNER_TEMP': str(root)}
            with patch.object(prerequisite, 'ROOT', root), patch.object(prerequisite.sys, 'platform', 'win32'), \
                    patch.dict(prerequisite.os.environ, env), patch.object(prerequisite, 'exercise') as exercise, \
                    patch.object(prerequisite.runtime, 'firewall', return_value={'armed':False,'exact_drain_verified':False}) as firewall:
                self.assertEqual(prerequisite.main(), 1)
                exercise.assert_not_called()
                firewall.assert_not_called()
            self.assertEqual({p.name: p.read_bytes() for p in control.iterdir()}, before)


if __name__ == '__main__':
    unittest.main()
