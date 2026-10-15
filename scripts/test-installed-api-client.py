#!/usr/bin/env python3
"""Installed-client verification checks: real process arguments and failure retention."""
import base64
import importlib.util
import json
import contextlib
import io
import os
from pathlib import Path
import subprocess
import tempfile
import sys
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location('installed_client', Path(__file__).with_name('verify-installed-api-client.py'))
CHECK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECK)
IDENTITY = '12' * 16
ORIGIN_SPEC = importlib.util.spec_from_file_location('client_origin', Path(__file__).resolve().parents[1] / 'deploy/linux/configure-nginx.py')
ORIGIN = importlib.util.module_from_spec(ORIGIN_SPEC)
ORIGIN_SPEC.loader.exec_module(ORIGIN)


class DeclaredOriginChecks(unittest.TestCase):
    def test_selected_host_origin_is_read_without_certificate_access_or_installation(self):
        output = io.StringIO()
        with patch.object(sys, 'argv', ['configure-nginx.py', '--print-origin']), \
                patch.object(ORIGIN.socket, 'gethostname', return_value='hart-server.example.test'), \
                patch.object(ORIGIN.subprocess, 'run') as execute, contextlib.redirect_stdout(output):
            ORIGIN.main()
        self.assertEqual('https://staging.hartonomous.com', output.getvalue().strip())
        execute.assert_not_called()

    def test_undeclared_host_fails_instead_of_selecting_another_hosts_origin(self):
        with patch.object(sys, 'argv', ['configure-nginx.py', '--print-origin']), \
                patch.object(ORIGIN.socket, 'gethostname', return_value='undeclared-host'):
            with self.assertRaisesRegex(SystemExit, 'No private nginx browser route declared'):
                ORIGIN.main()

    def test_multiple_matching_declarations_are_rejected(self):
        declaration = json.loads((Path(ORIGIN.__file__).parent.parent / 'private-origins.json').read_text())
        declaration['environments'] *= 2
        with tempfile.TemporaryDirectory() as directory:
            configuration = Path(directory) / 'origins.json'
            configuration.write_text(json.dumps(declaration))
            with patch.object(sys, 'argv', ['configure-nginx.py', '--configuration', str(configuration), '--print-origin']), \
                    patch.object(ORIGIN.socket, 'gethostname', return_value='hart-server'):
                with self.assertRaisesRegex(SystemExit, 'Select exactly one'):
                    ORIGIN.main()


class InstalledClientChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.cli = self.root / 'Laplace.Cli'
        self.cli.write_text('fixture executable')
        self.calls = []
        self.key_paths = []
        self.environment = patch.dict(os.environ, {
            'LAPLACE_API_KEY': 'fixture-verification-key',
            'LAPLACE_API_URL': 'http://wrong-host',
            'LAPLACE_API_KEY_FILE': 'wrong-key-file',
            'LAPLACE_OPERATOR_TOKEN': 'operator-tripwire',
            'LAPLACE_DB': 'database-tripwire', 'PGPASSWORD': 'password-tripwire'})
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def execute(self, arguments, **options):
        self.assertEqual([str(self.cli), '--api-client'], arguments[:2])
        self.assertNotIn('fixture-verification-key', arguments)
        environment = options['env']
        self.assertEqual('https://example.test', environment['LAPLACE_API_URL'])
        for name in ('LAPLACE_API_KEY', 'LAPLACE_OPERATOR_TOKEN', 'LAPLACE_DB', 'PGPASSWORD'):
            self.assertNotIn(name, environment)
        key = Path(environment['LAPLACE_API_KEY_FILE'])
        self.assertEqual('fixture-verification-key', key.read_text())
        if os.name != 'nt':
            self.assertEqual(0o600, key.stat().st_mode & 0o777)
        self.key_paths.append(key)
        command = arguments[2:]
        self.calls.append(command)
        if command == ['ops']:
            value = {'object': 'op.catalog', 'operations': [{'name': 'ops.substrate_counts'}]}
        elif command[0] == 'op':
            value = {'object': 'op.result', 'name': 'ops.substrate_counts',
                     'rows': [{'metric': 'entities(ESTIMATE)', 'value': 17}]}
        elif command[0] == 'ingest':
            self.assertEqual(['ingest', 'document', 'laplace-client-verification.txt', '--verify'], command)
            source = Path(options['cwd']) / command[2]
            self.assertEqual(CHECK.DOCUMENT, source.read_bytes())
            self.assertEqual(946684800, source.stat().st_mtime)
            value = {'file_id': IDENTITY, 'bytes': len(CHECK.DOCUMENT)}
        elif command[1] == 'get':
            self.assertEqual(IDENTITY, command[2])
            value = {'file_id': IDENTITY, 'content_base64': base64.b64encode(CHECK.DOCUMENT).decode()}
        else:
            self.assertEqual(['content', 'export', IDENTITY], command[:3])
            Path(command[3]).write_bytes(CHECK.DOCUMENT)
            return subprocess.CompletedProcess(arguments, 0, '', '')
        return subprocess.CompletedProcess(arguments, 0, json.dumps(value), '')

    def verify(self):
        CHECK.verify(self.cli, 'https://example.test', self.root)

    def test_complete_flow_keeps_credentials_out_of_arguments_and_cleans_temporary_key(self):
        with patch.object(CHECK.subprocess, 'run', side_effect=self.execute):
            self.verify()
        self.assertEqual(5, len(self.calls))
        self.assertTrue(self.key_paths)
        self.assertTrue(all(not key.exists() for key in self.key_paths))

    def test_failed_write_stops_without_retry_and_cleans_temporary_key(self):
        def fail_write(arguments, **options):
            result = self.execute(arguments, **options)
            if arguments[2] == 'ingest':
                result.returncode = 1
                result.stderr = 'arbitrary-server-text-is-not-published'
            return result
        with patch.object(CHECK.subprocess, 'run', side_effect=fail_write):
            with self.assertRaisesRegex(ValueError, 'ingest failed with exit code 1'):
                self.verify()
        self.assertEqual(3, len(self.calls))
        self.assertTrue(all(not key.exists() for key in self.key_paths))

    def test_empty_catalog_is_rejected_before_any_write(self):
        def empty_catalog(arguments, **options):
            result = self.execute(arguments, **options)
            result.stdout = json.dumps({'object': 'op.catalog', 'operations': []})
            return result
        with patch.object(CHECK.subprocess, 'run', side_effect=empty_catalog):
            with self.assertRaisesRegex(ValueError, 'empty or invalid operation catalog'):
                self.verify()
        self.assertEqual([['ops']], self.calls)
        self.assertTrue(all(not key.exists() for key in self.key_paths))

    def test_successful_process_with_wrong_export_is_rejected(self):
        def corrupt_export(arguments, **options):
            result = self.execute(arguments, **options)
            if arguments[2:4] == ['content', 'export']:
                Path(arguments[-1]).write_bytes(b'wrong bytes')
            return result
        with patch.object(CHECK.subprocess, 'run', side_effect=corrupt_export):
            with self.assertRaisesRegex(ValueError, 'exported bytes differ'):
                self.verify()
        self.assertTrue(all(not key.exists() for key in self.key_paths))


if __name__ == '__main__':
    unittest.main()
