"""The installed client selects one runtime, API origin and identity."""
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location('laplace_client', Path(__file__).with_name('laplace-mono.py'))
CLIENT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CLIENT)


class ClientLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='laplace-launcher-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.executable = self.root / 'Laplace.Cli'
        self.executable.write_text('fixture')
        self.executable.chmod(0o755)
        self.configuration = self.root / 'client.json'
        self.profile = {'runtime': {'kind': 'directory', 'directory': str(self.root)},
                        'api': {'origin': 'https://example.test', 'key_file': str(self.root / 'api-key')}}

    def launch(self, arguments):
        self.configuration.write_text(json.dumps({'version': 2, 'default_profile': 'selected',
                                                  'profiles': {'selected': self.profile}}))
        with mock.patch.dict(os.environ, {'LAPLACE_MONO_CLIENT_CONFIG': str(self.configuration),
                                         'LAPLACE_MONO_PROFILE': 'ambient-wrong-profile',
                                         'LAPLACE_DB': 'ambient-database', 'PGPASSWORD': 'ambient-password',
                                         'PGHOST': 'ambient-host', 'LAPLACE_API_URL': 'https://wrong.test'}), \
             mock.patch.object(sys, 'argv', ['laplace-mono', '--profile', 'selected', *arguments]), \
             mock.patch.object(CLIENT.os, 'execve') as execute:
            CLIENT.main()
        return execute

    def test_api_launch_keeps_arguments_and_clears_database_environment(self):
        self.profile['environment'] = {'LAPLACE_DB': 'profile-database', 'PGSERVICE': 'profile-service'}
        before = Path.cwd()
        execute = self.launch(['chat', '--session', 'session-one', '日本語 with spaces'])
        executable, arguments, environment = execute.call_args.args
        self.assertEqual(self.executable, executable)
        self.assertEqual([str(self.executable), '--api-client', 'chat', '--session', 'session-one',
                          '日本語 with spaces'], arguments)
        self.assertEqual('https://example.test', environment['LAPLACE_API_URL'])
        self.assertEqual(str(self.root / 'api-key'), environment['LAPLACE_API_KEY_FILE'])
        self.assertFalse(any(k.upper().startswith('PG') for k in environment))
        self.assertNotIn('LAPLACE_DB', environment)
        self.assertNotIn('LAPLACE_APP_DB', environment)
        self.assertEqual(before, Path.cwd())

    def test_api_profile_rejects_database_and_credential_fields(self):
        for field in ('database', 'credential'):
            with self.subTest(field=field):
                self.profile[field] = {}
                with self.assertRaisesRegex(ValueError, 'must not include database'):
                    self.launch(['ops'])
                del self.profile[field]

    def test_api_profile_rejects_non_origin_credential_destinations(self):
        for origin in ('http://example.test', 'https://user:password@example.test',
                       'https://example.test/path', 'https://example.test/?key=x', 'https://example.test/#fragment'):
            with self.subTest(origin=origin):
                self.profile['api']['origin'] = origin
                with self.assertRaisesRegex(ValueError, 'HTTPS origin'):
                    self.launch(['ops'])


if __name__ == '__main__':
    unittest.main()
