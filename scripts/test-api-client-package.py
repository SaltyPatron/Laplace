#!/usr/bin/env python3
"""Validate distributable closure and installer handoff without service credentials."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile


ROOT = Path(__file__).resolve().parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


PACKAGE = module('client_package', ROOT / 'scripts/package-api-client.py')
INSTALL = module('client_setup', ROOT / 'deploy/client/install-api-client.py')


class ClientPackageTests(unittest.TestCase):
    def test_both_archives_retain_payload_identity_and_platform_installers(self):
        for platform in ('win-x64', 'linux-x64'):
            with self.subTest(platform=platform), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                published = root / 'published'
                published.mkdir()
                names = ['Laplace.Cli.dll', 'Laplace.Cli.runtimeconfig.json'] + (
                    ['Laplace.Cli.exe', 'coreclr.dll'] if platform == 'win-x64' else ['Laplace.Cli', 'libcoreclr.so'])
                for name in names:
                    (published / name).write_bytes(name.encode())
                archive = PACKAGE.package(published, root / 'out', platform, 'a' * 40)
                unpacked = root / 'unpacked'
                if platform == 'win-x64':
                    with zipfile.ZipFile(archive) as source:
                        source.extractall(unpacked)
                else:
                    with tarfile.open(archive) as source:
                        source.extractall(unpacked, filter='data')
                bundle = next(unpacked.iterdir())
                manifest = json.loads((bundle / 'manifest.json').read_text())
                self.assertEqual('a' * 40, manifest['revision'])
                for name, digest in manifest['files'].items():
                    self.assertEqual(digest, hashlib.sha256((bundle / name).read_bytes()).hexdigest())
                helper = 'Install-LaplaceApiClient.ps1' if platform == 'win-x64' else 'install-api-client.py'
                self.assertTrue((bundle / 'deploy/client' / helper).is_file())
                with self.assertRaises(FileExistsError):
                    PACKAGE.package(published, root / 'out', platform, 'a' * 40)
                self.assertEqual([archive], list((root / 'out').iterdir()))

    def test_framework_dependent_publish_is_rejected_before_archiving(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'Laplace.Cli.exe').write_bytes(b'executable')
            with self.assertRaisesRegex(ValueError, 'coreclr.dll'):
                PACKAGE.package(root, root / 'output', 'win-x64', 'a' * 40)
            self.assertFalse((root / 'output').exists())

    def test_database_dependency_is_rejected_from_the_api_distribution(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ('Laplace.Cli.exe', 'coreclr.dll', 'Laplace.Cli.dll',
                         'Laplace.Cli.runtimeconfig.json', 'Npgsql.dll'):
                (root / name).write_bytes(b'fixture')
            with self.assertRaisesRegex(ValueError, 'local substrate dependency: Npgsql.dll'):
                PACKAGE.package(root, root / 'output', 'win-x64', 'a' * 40)
            self.assertFalse((root / 'output').exists())

    def test_setup_passes_key_path_and_removes_temporary_configuration_on_failure(self):
        with tempfile.TemporaryDirectory(prefix='client setup ') as temporary:
            root = Path(temporary).resolve()
            key = root / 'private key'
            key.write_text('private-key-tripwire')
            configuration = None

            def execute(command, **options):
                nonlocal configuration
                configuration = Path(command[command.index('--configuration') + 1])
                content = configuration.read_text()
                self.assertNotIn('private-key-tripwire', content)
                profile = json.loads(content)['profiles']['service']
                self.assertEqual(str(key), profile['api']['key_file'])
                self.assertEqual('https://example.test', profile['api']['origin'])
                self.assertEqual(str(ROOT / 'runtime'), profile['runtime']['directory'])
                self.assertNotIn('private-key-tripwire', command)
                return subprocess.CompletedProcess(command, 9)

            with patch.object(sys, 'argv', ['install-api-client.py', '--origin', 'https://example.test',
                    '--key-file', str(key), '--destination', str(root / 'installed'),
                    '--command-directory', str(root / 'commands')]), \
                    patch.object(INSTALL.subprocess, 'run', side_effect=execute):
                self.assertEqual(9, INSTALL.main())
            self.assertIsNotNone(configuration)
            self.assertFalse(configuration.exists())
            self.assertEqual('private-key-tripwire', key.read_text())


if __name__ == '__main__':
    unittest.main()
