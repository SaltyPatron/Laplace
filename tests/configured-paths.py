#!/usr/bin/env python3
"""Exercise configured directories through the actual shell and placement owners."""
import importlib.util
import json
import grp
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('machine', ROOT / 'scripts/machine-env.py')
machine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(machine)


class ConfiguredPaths(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='configured paths ', dir=os.environ['TMPDIR'])
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.config = self.base / 'machine.env'
        self.build = self.base / 'compiler output'
        self.work = self.base / 'operation output'
        self.config.write_text(f'LAPLACE_BUILD="{self.build}"\nLAPLACE_WORK="{self.work}"\nLAPLACE_PG_DIR=/usr/local/pgsql\nLAPLACE_ROLE=laplace\n')
        self.env = {k: v for k, v in os.environ.items() if not k.startswith(('LAPLACE_', 'PG'))}
        self.env['LAPLACE_MACHINE_ENV'] = str(self.config)

    def test_shell_literal_and_override(self):
        self.env['LAPLACE_BUILD'] = str(self.base / "chosen ' ; $(touch SHOULD_NOT_EXIST)")
        run = subprocess.run(['bash', '-c', 'source "$1/scripts/lib/installation.sh"; python3 -c \'import os,json; print(json.dumps({k:v for k,v in os.environ.items() if k.startswith("LAPLACE_")}))\'', 'bash', str(ROOT)], env=self.env, check=True, capture_output=True, text=True)
        values = json.loads(run.stdout)
        self.assertEqual(values['LAPLACE_BUILD'], self.env['LAPLACE_BUILD'])
        self.assertTrue(values['LAPLACE_BUILD_DIRECTORY'].startswith(self.env['LAPLACE_BUILD'] + '/'))
        self.assertEqual(values['LAPLACE_SCRATCH_ROOT'], str(self.work / 'laplace-mono/scratch'))
        self.assertEqual(values['LAPLACE_PG_PREFIX'], '/usr/local/pgsql')
        self.assertFalse((ROOT / 'SHOULD_NOT_EXIST').exists())

    def test_two_checkouts_preserve_existing_output_without_mount(self):
        results = []
        for name in ('checkout one', 'checkout two'):
            checkout = self.base / name
            (checkout / 'build').mkdir(parents=True)
            (checkout / 'build/keep.txt').write_text(name)
            run = subprocess.run(['python3', str(ROOT / 'scripts/place-build-directory.py'), str(checkout)], env=self.env, check=True, capture_output=True, text=True)
            target = Path(run.stdout.strip())
            self.assertTrue(target.is_relative_to(self.build))
            self.assertEqual((target / 'keep.txt').read_text(), name)
            self.assertEqual((checkout / 'build').resolve(), target)
            again = subprocess.run(['python3', str(ROOT / 'scripts/place-build-directory.py'), str(checkout)], env=self.env, check=True, capture_output=True, text=True)
            self.assertEqual(run.stdout, again.stdout)
            results.append(target)
        self.assertNotEqual(*results)

    def test_database_does_not_require_build_or_work(self):
        self.config.write_text('LAPLACE_PG_DIR=/usr/local/pgsql\nLAPLACE_ROLE=declared_owner\nLAPLACE_DBNAME=selected_content\n')
        values = machine.database(self.config, self.env)
        self.assertEqual(values['PGUSER'], 'declared_owner')
        self.assertEqual(values['PGDATABASE'], 'selected_content')
        paths = machine.paths(self.config, self.env, ROOT)
        self.assertNotIn('LAPLACE_BUILD', paths)
        self.assertNotIn('LAPLACE_WORK', paths)

    def test_missing_build_fails_before_placement(self):
        self.config.write_text('LAPLACE_ROLE=laplace\n')
        checkout = self.base / 'checkout'
        checkout.mkdir()
        run = subprocess.run(['python3', str(ROOT / 'scripts/place-build-directory.py'), str(checkout)], env=self.env, capture_output=True, text=True)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn('Declare LAPLACE_BUILD and LAPLACE_WORK', run.stderr)
        self.assertFalse((checkout / 'build').exists())

    def test_workspace_repair_rejects_database_overlap_before_mutation(self):
        database = self.work / 'laplace-mono/database'
        database.mkdir(parents=True)
        (database / 'PG_VERSION').write_text('18\n')
        database.chmod(0o700)
        with self.config.open('a') as stream:
            stream.write(f'LAPLACE_PGDATA="{database}"\nLAPLACE_BUILD_GROUP={grp.getgrgid(os.getgid()).gr_name}\n')
        run = subprocess.run(['bash', str(ROOT / 'scripts/setup-storage.sh'), '--repair'], env=self.env, capture_output=True, text=True)
        self.assertNotEqual(run.returncode, 0)
        self.assertIn('Workspace overlaps database or serving state', run.stderr)
        self.assertEqual(database.stat().st_mode & 0o777, 0o700)
        self.assertFalse(self.build.exists())

    def test_database_preserves_declared_tls(self):
        self.env.update(PGHOST='selected.internal', PGSSLMODE='verify-full', PGSSLROOTCERT='/trust/ca.pem')
        values = machine.database(self.config, self.env)
        self.assertIn('SSL Mode="verifyfull"', values['LAPLACE_DB'])
        self.assertIn('Root Certificate="/trust/ca.pem"', values['LAPLACE_DB'])

    def test_legacy_database_route_preserves_restricted_identity(self):
        self.config.write_text('LAPLACE_ROLE=migration_owner\nLAPLACE_KNOWLEDGE_ROLE=runtime\n'
                               'LAPLACE_KNOWLEDGE_RUNTIME_DB="Host=hart-server;Username=runtime;Database=selected"\n')
        values = machine.database(self.config, self.env)
        self.assertEqual(values['PGUSER'], 'runtime')
        self.assertEqual(values['LAPLACE_DATABASE_RUNTIME_DB'], 'Host=hart-server;Username=runtime;Database=selected')
        self.assertNotIn('LAPLACE_KNOWLEDGE_RUNTIME_DB', values)
        self.env['LAPLACE_DATABASE_ROLE'] = 'process_runtime'
        self.assertEqual(machine.database(self.config, self.env)['PGUSER'], 'process_runtime')
        self.env.pop('LAPLACE_DATABASE_ROLE')
        self.env['LAPLACE_KNOWLEDGE_ROLE'] = 'legacy_process_runtime'
        self.assertEqual(machine.database(self.config, self.env)['PGUSER'], 'legacy_process_runtime')

    def test_conflicting_database_aliases_fail_without_exposing_values(self):
        self.config.write_text('LAPLACE_KNOWLEDGE_RUNTIME_DB=old_secret\nLAPLACE_DATABASE_RUNTIME_DB=new_secret\n')
        with self.assertRaisesRegex(ValueError, '^Conflicting configuration aliases for: LAPLACE_DATABASE_RUNTIME_DB$'):
            machine.resolve(self.config, self.env)
        self.env.update(LAPLACE_KNOWLEDGE_ROLE='old', LAPLACE_DATABASE_ROLE='new')
        with self.assertRaisesRegex(ValueError, 'Conflicting configuration keys:'):
            machine.database(self.config, self.env)

    def test_no_machine_file_shell_execution(self):
        self.config.write_text('LAPLACE_BUILD="$(touch /not-a-command)"\nLAPLACE_WORK="/work"\n')
        with self.assertRaises(ValueError):
            machine.paths(self.config, self.env, ROOT)


if __name__ == '__main__':
    unittest.main()
