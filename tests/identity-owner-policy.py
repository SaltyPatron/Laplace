#!/usr/bin/env python3
"""Exercise private owner policy transport without database or service access."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch

MODULE = Path(__file__).parents[1] / 'deploy/linux/platform-owner-policy.py'
spec = importlib.util.spec_from_file_location('owner_transport', MODULE)
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class OwnerTransportTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / 'policy.json'
        self.policy = {'version': 1, 'owners': [{'provider': 'google', 'issuer': 'https://accounts.google.com',
            'subject': 'private-subject', 'client_id': 'client-test', 'verified_at': '2025-01-01T00:00:00Z',
            'display_name': 'Example "person" $HOME `command` \\ unicode \u00e9'}]}
        self.write()

    def tearDown(self):
        self.directory.cleanup()

    def write(self):
        self.path.write_text(json.dumps(self.policy), encoding='utf-8')
        self.path.chmod(0o600)

    def test_exact_private_policy_and_client_selection(self):
        values = owner.load_policy(self.path)
        self.assertEqual(json.loads(values[owner.POLICY_ENV]), self.policy)
        self.assertEqual(values['LAPLACE_AUTH_GOOGLE_CLIENT_ID'], 'client-test')
        self.assertNotIn('CLIENT_SECRET', ''.join(values))

    def test_absent_and_explicit_empty_are_distinct(self):
        self.assertEqual(owner.load_policy(self.path.with_name('absent'), missing_ok=True), {})
        with self.assertRaises(ValueError):
            owner.load_policy(self.path.with_name('absent'))
        self.policy['owners'] = []
        self.write()
        self.assertEqual(json.loads(owner.load_policy(self.path)[owner.POLICY_ENV]), self.policy)

    def test_invalid_policy_stops_before_child(self):
        self.policy['owners'][0]['client_id'] = ''
        self.write()
        with patch.object(owner.subprocess, 'run') as child:
            with self.assertRaises(ValueError):
                owner.run(self.path, ['fixture'])
            child.assert_not_called()

    def test_conflicting_client_or_duplicate_binding_refused(self):
        self.policy['owners'].append(dict(self.policy['owners'][0], subject='different', client_id='other'))
        self.write()
        with self.assertRaises(ValueError):
            owner.load_policy(self.path)
        self.policy['owners'][1] = dict(self.policy['owners'][0])
        self.write()
        with self.assertRaises(ValueError):
            owner.load_policy(self.path)

    def test_duplicate_json_property_refused(self):
        self.path.write_text('{"version":1,"version":1,"owners":[]}')
        with self.assertRaises(ValueError):
            owner.load_policy(self.path)

    def test_exact_provider_issuers_and_private_field_controls(self):
        self.assertTrue(owner.valid_issuer('google', 'accounts.google.com'))
        self.assertFalse(owner.valid_issuer('google', 'https://accounts.google.com/'))
        issuer = 'https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0'
        self.assertTrue(owner.valid_issuer('microsoft', issuer))
        self.assertFalse(owner.valid_issuer('microsoft', issuer + '?'))
        self.assertFalse(owner.valid_issuer('microsoft', issuer.replace('.com/', '.com:4430/')))
        self.assertFalse(owner.valid_issuer('microsoft', issuer.replace('9188040d', 'not-a-tenant')))
        for value in ('not-an-issuer', issuer):
            self.policy['owners'][0]['issuer'] = value
            self.write()
            with self.assertRaises(ValueError):
                owner.load_policy(self.path)
        self.policy['owners'][0]['issuer'] = 'https://accounts.google.com'
        for value in (' ', ' padded', 'private\x7fvalue', 'private\x85value'):
            self.policy['owners'][0]['display_name'] = value
            self.write()
            with self.assertRaises(ValueError):
                owner.load_policy(self.path)

    def test_symlink_refused(self):
        link = self.path.with_name('link')
        try:
            link.symlink_to(self.path)
        except OSError:
            self.skipTest('Symlink creation is unavailable')
        with self.assertRaises(ValueError):
            owner.load_policy(link)

    @unittest.skipIf(os.name == 'nt', 'POSIX file mode contract')
    def test_shared_file_refused(self):
        self.path.chmod(0o640)
        with self.assertRaises(ValueError):
            owner.load_policy(self.path)

    def test_child_gets_policy_and_private_values_are_not_printed(self):
        output = io.StringIO()
        with patch.dict(os.environ, {owner.POLICY_ENV: 'ambient', 'LAPLACE_AUTH_MICROSOFT_CLIENT_ID': 'ambient'}):
            with patch.object(owner.subprocess, 'run', return_value=subprocess.CompletedProcess([], 23)) as child:
                with contextlib.redirect_stdout(output):
                    self.assertEqual(owner.run(self.path, ['dotnet', 'fixture', 'app-nuke']), 23)
                supplied = child.call_args.kwargs['env']
        self.assertEqual(json.loads(supplied[owner.POLICY_ENV]), self.policy)
        self.assertEqual(supplied['LAPLACE_AUTH_MICROSOFT_CLIENT_ID'], '')
        self.assertEqual(output.getvalue(), 'Platform owner policy: configured\n')

    def test_missing_policy_clears_ambient_owner_configuration(self):
        with patch.dict(os.environ, {owner.POLICY_ENV: 'ambient'}):
            with patch.object(owner.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)) as child:
                with contextlib.redirect_stdout(io.StringIO()) as output:
                    owner.run(None, ['fixture'])
                self.assertNotIn(owner.POLICY_ENV, child.call_args.kwargs['env'])
                self.assertIn('no-owner-config', output.getvalue())

    def test_api_environment_preserves_oauth_and_quotes_private_json(self):
        api = self.path.with_name('api.env')
        original = 'LAPLACE_AUTH_GOOGLE_CLIENT_ID=actual-client\nOTHER=unchanged\n'
        api.write_text(original + owner.POLICY_ENV + '=old\n')
        owner.apply_api_environment(api, owner.load_policy(self.path))
        content = api.read_text()
        self.assertTrue(content.startswith(original))
        self.assertEqual(content.count(owner.POLICY_ENV + '='), 1)
        assignment = content.splitlines()[-1].partition('=')[2]
        self.assertEqual(json.loads(json.loads(assignment)), self.policy)
        if os.name != 'nt':
            self.assertEqual(stat.S_IMODE(api.stat().st_mode), 0o600)
        owner.apply_api_environment(api, {})
        self.assertEqual(api.read_text(), original)

    def test_error_cli_does_not_print_private_body(self):
        self.path.write_text('{"private-subject":invalid}')
        result = subprocess.run([os.sys.executable, str(MODULE), '--policy-file', str(self.path), '--', 'never-run'],
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('private-subject', result.stderr + result.stdout)

    def test_machine_path_convention(self):
        with patch.dict(os.environ, {'LAPLACE_WORK': self.directory.name}, clear=True):
            self.assertEqual(owner.policy_path(), Path(self.directory.name) / 'laplace-mono/platform-owners/policy.json')


if __name__ == '__main__':
    unittest.main()
