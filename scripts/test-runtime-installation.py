"""Failure boundaries for runtime setup. No installed services or databases are changed."""
import copy
import fcntl
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import urllib.error
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'deploy/linux/runtime'
spec = importlib.util.spec_from_file_location('runtime', RUNTIME / 'install.py')
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)
CONFIG = json.loads((RUNTIME / 'hart-server-runtime.json').read_text())


class RuntimeBoundaries(unittest.TestCase):
    def workload_fixture(self, temporary, *, failure=None, conflict=False):
        config = copy.deepcopy(CONFIG)
        credential = Path(temporary) / 'approle.json'
        credential.write_text('{}')
        credential.chmod(0o600)
        config['credentials'] = str(credential)
        calls = []

        def request(req, **kwargs):
            path = req.full_url.split('/v1/', 1)[1]
            payload = json.loads(req.data) if req.data else None
            calls.append((path, req.get_method(), payload))
            if path == failure:
                raise urllib.error.HTTPError(req.full_url, 403, 'private-server-error', {},
                    io.BytesIO(b'do-not-print-this-response'))
            if path == 'auth/approle/login':
                value = {'auth': {'client_token': 'workload-token', 'metadata': {'role_name': config['bao_role']}}}
            elif path == 'auth/token/revoke-self':
                return io.BytesIO(b'')
            elif path == 'sys/capabilities-self':
                value = {'capabilities': ['read']}
            else:
                name = path.rsplit('/', 1)[1]
                item = config if name == config['record'] else config['knowledge']
                record = {key: item[key] for key in ('database_host', 'database', 'role', 'port')}
                record['password'] = 'a' * 64
                if conflict:
                    record['database'] = 'another-installation'
                value = {'data': {'data': record}}
            return io.BytesIO(json.dumps(value).encode())
        return config, calls, request

    def test_repeat_setup_never_uses_an_expired_operator_session(self):
        with tempfile.TemporaryDirectory() as temporary:
            config, calls, request = self.workload_fixture(temporary)
            with patch.object(runtime, 'Bao', side_effect=AssertionError('operator session must not be used')), \
                 patch.object(runtime, 'enrollment', return_value={'role_id': 'role', 'secret_id': 'secret'}), \
                 patch.object(runtime.urllib.request, 'urlopen', side_effect=request):
                first = runtime.runtime_records(config)
                second = runtime.runtime_records(config)
            self.assertEqual(first, second)
            self.assertEqual([v[0] for v in calls].count('auth/approle/login'), 2)
            self.assertEqual([v[0] for v in calls].count('auth/token/revoke-self'), 2)
            self.assertTrue(all(method == 'GET' for path, method, _ in calls if path.startswith('kv/')))
            self.assertFalse(any('policy' in path or '/role/' in path for path, _, _ in calls))

    def test_rejected_or_conflicting_workload_never_falls_back_to_operator(self):
        for failure, conflict in (('auth/approle/login', False),
                                  ('kv/data/hosts/hart-server/application-runtime', False), (None, True)):
            with self.subTest(failure=failure, conflict=conflict), tempfile.TemporaryDirectory() as temporary:
                config, calls, request = self.workload_fixture(temporary, failure=failure, conflict=conflict)
                with patch.object(runtime, 'Bao', side_effect=AssertionError('forbidden fallback')), \
                     patch.object(runtime, 'enrollment', return_value={'role_id': 'role', 'secret_id': 'secret'}), \
                     patch.object(runtime.urllib.request, 'urlopen', side_effect=request):
                    with self.assertRaises(ValueError) as error:
                        runtime.runtime_records(config)
                self.assertNotIn('private-server-error', str(error.exception))
                self.assertNotIn('do-not-print', str(error.exception))
                if failure != 'auth/approle/login':
                    self.assertEqual(calls[-1][0], 'auth/token/revoke-self')

    def test_enrollment_rejects_links_and_unprivileged_ownership(self):
        with tempfile.TemporaryDirectory() as temporary:
            credential = Path(temporary) / 'approle.json'
            credential.write_text('{"role_id":"role","secret_id":"secret"}')
            credential.chmod(0o644)
            with self.assertRaisesRegex(ValueError, 'root-owned private'):
                runtime.enrollment(credential)
            link = Path(temporary) / 'link.json'
            link.symlink_to(credential)
            with self.assertRaisesRegex(ValueError, 'root-owned private'):
                runtime.enrollment(link)

    def test_only_missing_enrollment_invokes_operator_bootstrap(self):
        with tempfile.TemporaryDirectory() as temporary:
            config, calls, request = self.workload_fixture(temporary)
            Path(config['credentials']).unlink()
            with patch.object(runtime, 'Bao') as operator, \
                 patch.object(runtime, 'enrollment', return_value={'role_id': 'role', 'secret_id': 'secret'}), \
                 patch.object(runtime.urllib.request, 'urlopen', side_effect=request):
                runtime.runtime_records(config)
                operator.return_value.call.assert_called_once_with(['token', 'lookup', '-field=ttl'])
                operator.return_value.enroll.assert_called_once_with(config)
                self.assertEqual(operator.return_value.record.call_count, 2)
            self.assertEqual(calls[-1][0], 'auth/token/revoke-self')

    def test_authentication_failure_receipt_is_saved_before_mutation(self):
        with tempfile.TemporaryDirectory() as temporary:
            config, calls, request = self.workload_fixture(temporary, failure='auth/approle/login')
            config.update(work=temporary, host_lock=str(Path(temporary) / 'host.lock'))
            manifest = Path(temporary) / 'manifest.json'
            manifest.write_text(json.dumps(config))
            with patch.object(sys, 'argv', ['runtime.py', '--manifest', str(manifest)]), \
                 patch.object(runtime.os, 'geteuid', return_value=0), \
                 patch.object(runtime, 'validate_declaration'), patch.object(runtime, 'preflight', return_value={}), \
                 patch.object(runtime, 'Bao', side_effect=AssertionError('forbidden operator session')), \
                 patch.object(runtime, 'enrollment', return_value={'role_id': 'role', 'secret_id': 'secret'}), \
                 patch.object(runtime.urllib.request, 'urlopen', side_effect=request), \
                 patch.object(runtime, 'checked', return_value='a' * 40) as command, \
                 patch.object(runtime.SYNC, 'atomic', side_effect=lambda path, text, *args: path.write_text(text)):
                with self.assertRaisesRegex(ValueError, 'workload operation'):
                    runtime.main()
            receipt = json.loads(next(Path(temporary).glob('*-runtime-*.json')).read_text())
            self.assertEqual(receipt['status'], 'failed')
            self.assertIn('HTTP 403', receipt['error'])
            self.assertNotIn('verification', receipt)
            self.assertEqual(command.call_count, 1)  # source identity only; no SQL/service mutation

    def test_nonroot_preflight_does_not_claim_workload_authentication(self):
        with tempfile.TemporaryDirectory() as temporary:
            config, _, _ = self.workload_fixture(temporary)
            manifest = Path(temporary) / 'manifest.json'
            manifest.write_text(json.dumps(config))
            output = io.StringIO()
            with patch.object(sys, 'argv', ['runtime.py', '--manifest', str(manifest), '--check']), \
                 patch.object(sys, 'stdout', output), patch.object(runtime.os, 'geteuid', return_value=1000), \
                 patch.object(runtime, 'preflight', return_value={}), \
                 patch.object(runtime, 'Bao', side_effect=AssertionError('forbidden operator session')), \
                 patch.object(runtime, 'runtime_records', side_effect=AssertionError('private enrollment')):
                runtime.main()
            receipt = json.loads(output.getvalue())
            self.assertEqual(receipt['workload_authentication'], 'requires_privileged_check')
            self.assertFalse(receipt['activated'])

    def test_manifest_selection_honors_explicit_process_and_machine_configuration(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            installed, process, explicit = (root / name for name in ('installed manifest.json', 'process.json', 'explicit.json'))
            for path in (installed, process, explicit):
                path.write_text('{}')
            machine = root / 'machine.env'
            machine.write_text('LAPLACE_RUNTIME_MANIFEST=' + str(installed) + '\n')
            with patch.dict(os.environ, {'LAPLACE_MACHINE_ENV': str(machine)}, clear=True):
                self.assertEqual(runtime.selected_manifest(), installed)
                with patch.dict(os.environ, {'LAPLACE_RUNTIME_MANIFEST': str(process)}):
                    self.assertEqual(runtime.selected_manifest(), process)
                    self.assertEqual(runtime.selected_manifest(explicit), explicit)
                machine.write_text('LAPLACE_RUNTIME_MANIFEST=/missing/declared-runtime.json\n')
                with self.assertRaisesRegex(ValueError, 'Missing declared'):
                    runtime.selected_manifest()
                machine.write_text('LAPLACE_RUNTIME_MANIFEST=relative.json\n')
                with self.assertRaisesRegex(ValueError, 'absolute path'):
                    runtime.selected_manifest()
                machine.write_text('')
                with patch.object(runtime.socket, 'gethostname', return_value='hart-server'):
                    self.assertEqual(runtime.selected_manifest(), RUNTIME / 'hart-server-runtime.json')
                with patch.object(runtime.socket, 'gethostname', return_value='not-a-declared-host'):
                    self.assertIsNone(runtime.selected_manifest())

    def test_application_connection_cannot_request_another_cluster(self):
        current = {'LAPLACE_INSTALL_PREFIX': CONFIG['prefix'], 'LAPLACE_PGDATA': CONFIG['database_data'],
                   'LAPLACE_PG_DIR': str(Path(CONFIG['pgbin']).parent), 'LAPLACE_ROLE': CONFIG['database_owner']}
        with patch.dict(os.environ, {}, clear=True), patch.object(runtime, 'declarations', return_value=current), \
             patch.object(runtime.socket, 'gethostname', return_value=CONFIG['host']), patch.object(runtime, 'checked') as command:
            runtime.validate_declaration(CONFIG)
            for key, value in {'LAPLACE_APP_DBNAME': 'another-db', 'LAPLACE_APP_PGDATA': '/another/cluster',
                               'LAPLACE_APP_PGPORT': '5433', 'LAPLACE_APP_PGHOST': 'remote-host'}.items():
                with self.subTest(key=key), patch.dict(os.environ, {key: value}):
                    with self.assertRaisesRegex(ValueError, 'differs from runtime declaration'):
                        runtime.validate_declaration(CONFIG)
            command.assert_not_called()

    def test_command_diagnostics_do_not_leak_into_public_exception(self):
        with self.assertRaises(runtime.CommandFailure) as failure:
            runtime.checked([sys.executable, '-c', "import sys; print('sensitive diagnostic',file=sys.stderr); sys.exit(1)"])
        self.assertNotIn('sensitive diagnostic', str(failure.exception))
        self.assertIn('sensitive diagnostic', failure.exception.diagnostics)

    def test_certificate_identity_key_and_renewal_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            key, certificate = root / 'server.key', root / 'server.crt'
            runtime.TLS.command(['openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', str(key)])
            policy = copy.deepcopy(CONFIG['tls'])
            runtime.TLS.generate(certificate, key, policy)
            self.assertTrue(runtime.TLS.desired(certificate, key, policy))
            policy['ip_addresses'] = ['127.0.0.2']
            self.assertFalse(runtime.TLS.desired(certificate, key, policy))
            policy = copy.deepcopy(CONFIG['tls'])
            policy['renew_before_days'] = policy['valid_days'] + 1
            self.assertFalse(runtime.TLS.desired(certificate, key, policy))

    def test_wrong_host_never_runs_commands(self):
        with patch.object(runtime.socket, 'gethostname', return_value='another-host'), patch.object(runtime, 'checked') as run:
            with self.assertRaisesRegex(ValueError, 'another host'):
                runtime.preflight(CONFIG)
            run.assert_not_called()

    def test_invalid_privilege_and_path_declarations(self):
        for change in ('role', 'pool', 'state', 'user'):
            config = copy.deepcopy(CONFIG)
            if change == 'role':
                config['knowledge']['role'] = config['database_owner']
            elif change == 'pool':
                config['mcp']['pool_max'] = config['mcp']['connection_limit'] + 1
            elif change == 'state':
                config['mcp']['state'] = config['state']
            else:
                config['mcp']['user'] = 'root'
            with self.subTest(change=change), self.assertRaises(ValueError):
                runtime.validate(config)

    def test_existing_bao_selection_is_preserved_and_conflict_refused(self):
        bao = runtime.Bao.__new__(runtime.Bao)
        selectors = {'database_host': '127.0.0.1', 'database': 'app', 'role': 'runtime', 'port': 5432}
        record = dict(selectors, password='a' * 64)
        with patch.object(bao, 'call', return_value=json.dumps({'data': {'data': record}})) as call:
            self.assertEqual(bao.record('hosts/example/application', selectors), record)
            self.assertEqual(call.call_count, 1)
            with self.assertRaisesRegex(ValueError, 'different installation'):
                bao.record('hosts/example/application', dict(selectors, database='other'))
            self.assertTrue(all(c.args[0][:2] == ['kv', 'get'] for c in call.call_args_list))

    def test_failed_activation_restores_files_and_removes_only_new_dropin(self):
        with tempfile.TemporaryDirectory() as temporary:
            existing, created = Path(temporary) / 'api.env', Path(temporary) / 'dropin.conf'
            existing.write_text('original\n')
            existing.chmod(0o640)
            recovered = []
            with self.assertRaisesRegex(RuntimeError, 'injected readiness failure'):
                with runtime.SYNC.configuration_transaction([existing, created], lambda: recovered.append(existing.read_text())):
                    existing.write_text('changed\n')
                    created.write_text('new service identity\n')
                    raise RuntimeError('injected readiness failure')
            self.assertEqual(recovered, ['original\n'])
            self.assertEqual(existing.stat().st_mode & 0o777, 0o640)
            self.assertFalse(created.exists())

    def test_nested_helper_reuses_host_lock_without_releasing_parent(self):
        with tempfile.TemporaryDirectory() as temporary:
            lock_path = Path(temporary) / 'host.lock'
            with lock_path.open('a') as lock:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                config = {'database': 'fixture', 'role': 'runtime', 'admin_role': 'owner',
                          'password': 'a' * 64, 'client_address': '127.0.0.1', 'lock': str(lock_path),
                          'psql': '/no-executable', 'admin_socket': temporary, 'port': 5432}
                env = dict(os.environ, LAPLACE_HOST_LOCK_FD=str(lock.fileno()))
                command = [sys.executable, str(RUNTIME / 'knowledge-role.py')]
                result = subprocess.run(command, input=json.dumps(config), text=True, capture_output=True,
                                        env=env, pass_fds=(lock.fileno(),), timeout=10)
                # It passed the nested lock, reaching the deliberately missing SQL executable.
                self.assertIn('FileNotFoundError', result.stderr)
                self.assertNotIn('BlockingIOError', result.stderr)
                with lock_path.open('a') as contender:
                    with self.assertRaises(BlockingIOError):
                        fcntl.flock(contender, fcntl.LOCK_EX | fcntl.LOCK_NB)
                other = Path(temporary) / 'other.lock'
                other.touch()
                config['lock'] = str(other)
                result = subprocess.run(command, input=json.dumps(config), text=True, capture_output=True,
                                        env=env, pass_fds=(lock.fileno(),), timeout=10)
                self.assertIn('different installation', result.stderr)
                self.assertNotIn('FileNotFoundError', result.stderr)


if __name__ == '__main__':
    unittest.main()
