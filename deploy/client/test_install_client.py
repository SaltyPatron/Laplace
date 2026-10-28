"""Regression checks for Linux client installation boundaries."""
import importlib.util
import json
import os
import shlex
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

SPEC = importlib.util.spec_from_file_location('laplace_client_installer', Path(__file__).with_name('install-client.py'))
INSTALLER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSTALLER)


class ClientInstallationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='laplace-client-install-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.destination = self.root / 'installed'
        self.commands = self.root / 'commands'
        self.configuration = self.root / 'selected.json'
        self.configuration.write_text(json.dumps({
            'version': 2,
            'default_profile': 'selected',
            'profiles': {'selected': {}},
        }))
        self.argv = [str(Path(INSTALLER.__file__)), '--configuration', str(self.configuration),
                     '--destination', str(self.destination), '--command-directory', str(self.commands)]

    def run_installer(self):
        with mock.patch.object(sys, 'argv', self.argv):
            INSTALLER.main()

    def existing_installation(self):
        self.destination.mkdir()
        (self.destination / 'laplace-mono.py').write_text('original launcher')
        (self.destination / 'client.json').write_text('original configuration')

    def assert_existing_installation_preserved(self):
        self.assertEqual('original launcher', (self.destination / 'laplace-mono.py').read_text())
        self.assertEqual('original configuration', (self.destination / 'client.json').read_text())

    def test_command_conflict_preserves_installation_before_preflight(self):
        self.existing_installation()
        self.commands.mkdir()
        command = self.commands / 'laplace-mono'
        command.write_text('another command')
        with mock.patch.object(INSTALLER.subprocess, 'run') as execute:
            with self.assertRaisesRegex(ValueError, 'Another command already owns'):
                self.run_installer()
        execute.assert_not_called()
        self.assert_existing_installation_preserved()
        self.assertEqual('another command', command.read_text())

    def test_failed_api_preflight_preserves_installation(self):
        self.existing_installation()
        with mock.patch.object(INSTALLER.subprocess, 'run',
                               side_effect=subprocess.CalledProcessError(1, ['ops'])):
            with self.assertRaises(subprocess.CalledProcessError):
                self.run_installer()
        self.assert_existing_installation_preserved()
        self.assertFalse(self.commands.exists())

    def test_operator_enrollment_uses_declared_machine_work_instead_of_stale_shell_paths(self):
        work = self.root / 'declared-work'
        machine = self.root / 'machine.env'
        machine.write_text('LAPLACE_WORK=' + shlex.quote(str(work)) + '\n')
        profile = {'runtime': {'directory': str(self.root / 'runtime')}, 'environment': {'LAPLACE_DATA': str(self.root / 'data')}}
        with mock.patch.dict(os.environ, {'LAPLACE_MACHINE_ENV': str(machine),
                                         'LAPLACE_WORK_ROOT': str(self.root / 'obsolete-install/work')}):
            INSTALLER.operator_environment(profile)
        self.assertEqual(str(work / 'laplace-mono'), profile['environment']['LAPLACE_WORK_ROOT'])
        self.assertEqual(str(work), profile['environment']['LAPLACE_WORK'])
        self.assertTrue((work / 'laplace-mono').is_dir())

    @unittest.skipIf(os.name == 'nt', 'POSIX command symlink is exercised in Linux CI')
    def test_installed_checks_pin_configuration_and_profile(self):
        with mock.patch.dict(os.environ, {
            'LAPLACE_MONO_CLIENT_CONFIG': str(self.root / 'unrelated.json'),
            'LAPLACE_MONO_PROFILE': 'unrelated',
        }), mock.patch.object(INSTALLER.subprocess, 'run') as execute:
            self.run_installer()
        self.assertEqual(3, execute.call_count)
        preflight, location, operation = execute.call_args_list
        self.assertEqual(str(self.configuration), preflight.kwargs['env']['LAPLACE_MONO_CLIENT_CONFIG'])
        self.assertEqual(['--profile', 'selected', 'ops'], preflight.args[0][-3:])
        for call, operation_name in ((location, '--where'), (operation, 'ops')):
            self.assertEqual(str(self.destination / 'client.json'), call.kwargs['env']['LAPLACE_MONO_CLIENT_CONFIG'])
            self.assertEqual([str(self.commands / 'laplace-mono'), '--profile', 'selected', operation_name], call.args[0])
            self.assertTrue(call.kwargs['check'])
        self.assertEqual((self.destination / 'laplace-mono.py').resolve(),
                         (self.commands / 'laplace-mono').resolve())


if __name__ == '__main__':
    unittest.main()
