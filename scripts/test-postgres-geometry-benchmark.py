#!/usr/bin/env python3
"""Bounded harness and actual subprocess controls; these do not simulate PG acceptance."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('geometry_baseline', ROOT / 'scripts/benchmark-postgres-geometry.py')
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class GeometryBaselineTests(unittest.TestCase):
    def args(self, **overrides):
        values = dict(rows=100000, transaction_rows=10000, repeats=3, max_bytes=512 << 20,
                      timeout=900, concurrency='1,2,4', database='laplace', recorded_case_dir=None)
        values.update(overrides)
        return argparse.Namespace(**values)

    def process_pg(self, program, timeout=3):
        with mock.patch.object(owner.shutil, 'which', return_value=sys.executable):
            pg = owner.Pg('laplace', timeout)
        pg.command = lambda sql: [sys.executable, '-c', program]
        return pg

    def test_transaction_ranges_preserve_every_actual_row_once(self):
        for actual in (1, 99, 100, 101, 1031):
            chunks = owner.ranges(actual, 100)
            self.assertEqual(list(range(1, actual + 1)),
                [value for lo, hi in chunks for value in range(lo, hi + 1)])
            self.assertTrue(all(hi - lo + 1 <= 100 for lo, hi in chunks))
        self.assertEqual([(1, 73)], owner.ranges(73, 10000))

    def test_resource_and_connection_envelopes_reject_invalid_inputs(self):
        for fields in ({'rows':0}, {'rows':1000001}, {'transaction_rows':0}, {'repeats':11},
                       {'max_bytes':0}, {'max_bytes':(4<<30)+1}, {'timeout':float('nan')},
                       {'timeout':3601}, {'concurrency':'1,1'}, {'concurrency':'17'},
                       {'concurrency':'1,2,3,4,5,6,7'}, {'database':'postgresql://secret@host/db'},
                       {'database':'host=example password=secret'}):
            with self.subTest(fields=fields), self.assertRaises(ValueError):
                owner.validate(self.args(**fields))
        self.assertEqual([1,2,4], owner.validate(self.args()))

    def test_session_requests_synchronous_commit_without_global_edits(self):
        with mock.patch.dict(owner.os.environ, {'PGOPTIONS':'-c work_mem=64MB'}, clear=True), \
             mock.patch.object(owner.shutil, 'which', return_value=sys.executable):
            pg = owner.Pg('laplace', 30)
        self.assertIn('-c synchronous_commit=on', pg.env['PGOPTIONS'])
        self.assertIn('-c work_mem=64MB', pg.env['PGOPTIONS'])
        self.assertEqual('laplace_admin', pg.env['PGUSER'])
        self.assertIn('-X', pg.command('SELECT 1'))
        self.assertIn('ON_ERROR_STOP=1', pg.command('SELECT 1'))

    def test_actual_subprocess_input_and_failure_are_not_reported_as_success(self):
        pg = self.process_pg('import sys; print(len(sys.stdin.buffer.read()))')
        with tempfile.TemporaryFile() as source:
            source.write(b'\x00\xff\n\r\x00' * 1000)
            source.seek(0)
            output, seconds = pg.execute('transport fixture', source=source)
        self.assertEqual(b'5000\n', output)
        self.assertGreater(seconds, 0)
        pg = self.process_pg('raise SystemExit(3)')
        with self.assertRaisesRegex(RuntimeError, 'exit 3'):
            pg.execute('failure fixture')

    def test_actual_export_preserves_arbitrary_binary_bytes(self):
        pg = self.process_pg('import sys; sys.stdout.buffer.write(bytes(range(256))*100)')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'binary'
            pg.export('transport fixture', path, 25600)
            self.assertEqual(bytes(range(256))*100, path.read_bytes())

    def test_actual_oversized_export_is_stopped_at_the_byte_envelope(self):
        pg = self.process_pg('import sys,time; sys.stdout.buffer.write(b"x"*10000); sys.stdout.buffer.flush(); time.sleep(10)')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'binary'
            started = time.monotonic()
            with self.assertRaisesRegex(ValueError, 'byte envelope'):
                pg.export('oversized transport fixture', path, 10)
            self.assertLess(time.monotonic()-started, 2)
            self.assertLessEqual(path.stat().st_size, 10)

    def test_actual_stalled_export_is_killed_at_the_deadline(self):
        pg = self.process_pg('import time; time.sleep(10)', timeout=.1)
        with tempfile.TemporaryDirectory() as directory:
            started = time.monotonic()
            with self.assertRaisesRegex(ValueError, 'timed out'):
                pg.export('stalled transport fixture', Path(directory)/'binary', 10)
            self.assertLess(time.monotonic()-started, 2)

    def test_exact_binary_readback_reuses_retained_bytes_and_rejects_corruption(self):
        with tempfile.TemporaryDirectory() as directory:
            source, output = Path(directory)/'input', Path(directory)/'output'
            source.write_bytes(bytes(range(256)))
            output.write_bytes(source.read_bytes())
            proof = owner.verify_readback(owner.artifact(source), output)
            self.assertTrue(proof['byte_comparison'])
            self.assertEqual(str(source), proof['identical_retained_input'])
            self.assertFalse(output.exists())
            for corrupt in (source.read_bytes()[:-1], b'x' + source.read_bytes()[1:]):
                output.write_bytes(corrupt)
                with self.assertRaisesRegex(ValueError, 'readback differs'):
                    owner.verify_readback(owner.artifact(source), output)
                self.assertTrue(output.exists())

    def test_missing_psql_retains_a_failed_receipt(self):
        with tempfile.TemporaryDirectory() as directory, \
             mock.patch.object(owner.shutil, 'which', return_value=None):
            args = self.args(output_dir=Path(directory)/'case')
            report = owner.run(args)
            self.assertEqual('failed', report['status'])
            self.assertIn('psql is required', report['error'])
            self.assertEqual(report, json.loads((args.output_dir/'receipt.json').read_text()))

    def test_partial_copy_failure_preserves_acknowledged_transaction_outcomes(self):
        # Actual child processes exercise the collector's failure boundary;
        # their exits are transport controls, not PostgreSQL acceptance proof.
        pg = self.process_pg('import sys; data=sys.stdin.buffer.read(); raise SystemExit(3 if data==b"bad" else 0)')
        with tempfile.TemporaryDirectory() as directory:
            inputs = []
            for index, data in enumerate((b'good', b'bad')):
                path = Path(directory)/str(index)
                path.write_bytes(data)
                inputs.append({**owner.artifact(path), 'rows':1})
            case, checkpoints = {}, []
            with self.assertRaisesRegex(ValueError, 'partial acknowledged work'):
                owner.copy_transactions(pg, 'owned.target', 'id', inputs, 1, case,
                                        lambda: checkpoints.append(json.loads(json.dumps(case))))
            self.assertEqual('synchronous_commit_acknowledged', case['transactions'][0]['status'])
            self.assertEqual('failed_or_commit_not_acknowledged', case['transactions'][1]['status'])
            self.assertEqual(1, case['acknowledged_transactions'])
            self.assertEqual(case, checkpoints[-1])

    def test_ambiguous_schema_creation_cleanup_requires_exact_owner_and_marker(self):
        pg = mock.Mock()
        for identity in ({'same_owner':False,'marker':'token'},
                         {'same_owner':True,'marker':'someone-else'}):
            pg.json.return_value = identity
            with self.assertRaisesRegex(ValueError, 'ownership marker differs'):
                owner.cleanup_owned_schema(pg, 'owned_name', 'token')
            pg.execute.assert_not_called()
        pg.json.return_value = None
        self.assertEqual('benchmark schema absent', owner.cleanup_owned_schema(pg, 'owned_name', 'token'))
        pg.execute.assert_not_called()
        pg.json.return_value = {'same_owner':True,'marker':'token'}
        self.assertEqual('owned benchmark schema removed', owner.cleanup_owned_schema(pg, 'owned_name', 'token'))
        pg.execute.assert_called_once_with('DROP SCHEMA "owned_name" CASCADE')

    def test_recorded_receipt_absence_never_implies_replay_or_game_throughput(self):
        self.assertEqual({'status':'not_supplied','replay':'not_measured'}, owner.recording_link(None))


if __name__ == '__main__':
    unittest.main()
