#!/usr/bin/env python3
"""Exercise repair receipt/commit failure boundaries with a real child process.

These tests verify orchestration and durability ordering, not PostgreSQL/native
eligibility or the live corpus. Domain repair tests own those claims.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("repair_transaction", ROOT / "scripts/lib/repair_transaction.py")
assert SPEC and SPEC.loader
REPAIR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPAIR)

CHILD = r'''
import hashlib, json, pathlib, sys
directory, mode, transcript = pathlib.Path(sys.argv[1]), sys.argv[2], pathlib.Path(sys.argv[3])
fetch_rows = None
for line in sys.stdin:
    with transcript.open('a') as out: out.write(line)
    if line.startswith('\\set FETCH_COUNT '): fetch_rows = int(line.split()[2])
    if line.startswith('\\echo PLAN_'):
        rows = [dict(kind='context', database='fixture'),
                dict(kind='physicality', original={'trajectory_ewkb':'00000080ff'},
                     proposed={'type':3}, evidence=[{'outcome':2}]),
                dict(kind='plan', count=2 if mode == 'bad-count' else 1,
                     unresolved=1 if mode == 'unresolved' else 0)]
        if mode == 'missing-original': del rows[1]['original']
        if mode == 'non-object': rows[1] = []
        if mode.startswith(('native-','resources-')):
            children = [dict(kind='native-input', entity_id=identity,
                             entity={'id':'\\x'+identity,'tier':2},
                             content={'entity_id':identity,'trajectory_ewkb':trajectory})
                        for identity,trajectory in [('01'*16,'00000080ff'),
                                                    ('02'*16,'00800000fe')]]
            if mode.startswith('native-missing-'):
                del children[1][mode.removeprefix('native-missing-')]
            if mode.startswith('resources-'): children[0]['entity']['label'] = 'ñébulo 水'
            if mode == 'resources-many':
                children = [dict(kind='native-input', entity_id=f'{identity:032x}',
                                 entity={'tier':2}, content={'trajectory_ewkb':'00000080ff'})
                            for identity in range(100001)]
            if mode == 'native-chunked':
                assert fetch_rows is not None and 0 < fetch_rows <= 8
                children = [dict(kind='native-input', entity_id=f'{identity:032x}',
                                 entity={'id':'\\x'+f'{identity:032x}','tier':2},
                                 content={'entity_id':f'{identity:032x}',
                                          'trajectory_ewkb':'00800000fe'})
                            for identity in range(1,18)]
            rows[1:1] = children
            rows[-1]['native_input_count'] = 1 if mode == 'native-bad-count' else len(children)
        if mode == 'plan-disconnect': sys.exit(3)
        if mode.startswith('resources-'):
            encoded = [(json.dumps(row,ensure_ascii=False)+'\n').encode() for row in rows]
            resources = dict(kind='resources',schema='laplace.legacy-content-repair-resources/v1',
                physicality_rows=1,native_input_rows=len(children),context_rows=1,summary_rows=1,
                plan_bytes=sum(map(len,encoded)),max_line_bytes=max(map(len,encoded))-1,
                max_jsonl_line_bytes=max(map(len,encoded)))
            if mode == 'resources-mismatch-bytes': resources['plan_bytes'] += 1
            if mode == 'resources-mismatch-line':
                resources['max_line_bytes'] += 1
                resources['max_jsonl_line_bytes'] += 1
            if mode == 'resources-mismatch-parents': resources['physicality_rows'] += 1
            if mode == 'resources-mismatch-children': resources['native_input_rows'] += 1
            if mode == 'resources-invalid-count': resources['native_input_rows'] = True
            if mode == 'resources-impossible-short-total': resources['plan_bytes'] = resources['max_jsonl_line_bytes']
            if mode == 'resources-impossible-long-total': resources['plan_bytes'] = len(rows)*resources['max_jsonl_line_bytes']+1
            if mode == 'resources-impossible-line':
                resources['max_line_bytes'] = 1
                resources['max_jsonl_line_bytes'] = 2
            print(json.dumps(resources),flush=True)
        elif mode == 'native-chunked':
            # Exercise protocol transport boundaries; native PostgreSQL tests own
            # proving that psql uses libpq chunked mode for the configured value.
            for offset in range(0,len(rows),fetch_rows):
                sys.stdout.write(''.join(json.dumps(row)+'\n' for row in rows[offset:offset+fetch_rows]))
                sys.stdout.flush()
        else:
            for row in rows: print(json.dumps(row), flush=True)
        print(line.split()[1], flush=True)
    elif line.startswith('\\echo DURABILITY_'):
        print(line.split()[1],flush=True)
    elif line.strip() == 'STREAM_RETAINED_PLAN;':
        assert (directory/'resources.json').is_file()
    elif line.startswith('\\echo RECEIPT_'):
        for row in rows: print(json.dumps(row,ensure_ascii=False),flush=True)
        print(line.split()[1],flush=True)
    elif line.startswith('\\echo ROLLED_BACK_'):
        if mode == 'resources-rollback-disconnect': sys.exit(6)
        print(line.split()[1],flush=True)
    elif line.strip() == 'APPLY_RETAINED_PLAN;':
        receipt = (directory/'plan.jsonl').read_bytes()
        manifest = json.loads((directory/'manifest.json').read_text())
        assert manifest['plan_sha256'] == hashlib.sha256(receipt).hexdigest()
        assert (directory/'submission.json').is_file()
        if mode == 'apply-disconnect': sys.exit(4)
        if mode != 'missing-applied':
            print(json.dumps(dict(kind='applied',count=2 if mode == 'wrong-applied-count' else 1,epoch=9)), flush=True)
        if mode == 'extra-applied': print(json.dumps(dict(kind='applied',count=1,epoch=9)), flush=True)
    elif line.startswith('\\echo COMMITTED_'):
        if mode == 'commit-disconnect': sys.exit(5)
        print(line.split()[1], flush=True)
'''


class RepairTransactionTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(os.environ.get("TMPDIR", "/build/laplace/work"))
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="repair-transaction-test-", dir=scratch)
        self.root = Path(self.temp.name)
        self.directory = self.root / "receipt"
        self.transcript = self.root / "commands.sql"
        self.child = self.root / "protocol.py"
        self.child.write_text(CHILD)

    def tearDown(self):
        self.temp.cleanup()

    def run_repair(self, mode="success", **bounds):
        return REPAIR.preserve_and_apply(
            [sys.executable, str(self.child), str(self.directory), mode, str(self.transcript)],
            "SELECT_LOCKED_EVIDENCE;", "APPLY_RETAINED_PLAN;", self.directory,
            source_sha="a" * 40, max_rows=bounds.pop("max_rows", 2), timeout=bounds.pop("timeout", 5), **bounds)

    def assert_not_submitted(self):
        self.assertNotIn("APPLY_RETAINED_PLAN;", self.transcript.read_text())
        self.assertNotIn("COMMIT;", self.transcript.read_text())
        self.assertEqual("not-submitted", json.loads((self.directory / "failure.json").read_text())["disposition"])

    def run_resource_repair(self, mode="resources-success", **bounds):
        return self.run_repair(mode, receipt_sql="STREAM_RETAINED_PLAN;", **bounds)

    def assert_resource_plan_not_streamed(self):
        self.assert_not_submitted()
        self.assertNotIn("STREAM_RETAINED_PLAN;", self.transcript.read_text())
        resources = json.loads((self.directory / "resources.json").read_text())
        self.assertEqual(1, resources["physicality_rows"])
        self.assertEqual(2, resources["native_input_rows"])
        self.assertFalse((self.directory / "plan.jsonl").exists())
        self.assertFalse((self.directory / "submission.json").exists())

    def test_resource_barrier_is_durable_before_exact_utf8_receipt_stream(self):
        fsync, send = REPAIR.os.fsync, REPAIR.PsqlTransaction.send
        synced = []
        def record_sync(fd):
            fsync(fd)
            synced.append(os.readlink(f"/proc/self/fd/{fd}"))
        def record_send(tx, sql):
            if "STREAM_RETAINED_PLAN;" in sql:
                self.assertIn(str(self.directory / "resources.json"), synced)
                self.assertIn(str(self.directory), synced)
            send(tx, sql)
        with patch.object(REPAIR.os, "fsync", side_effect=record_sync), \
                patch.object(REPAIR.PsqlTransaction, "send", new=record_send):
            outcome = self.run_resource_repair()
        raw = (self.directory / "plan.jsonl").read_bytes()
        resources = json.loads((self.directory / "resources.json").read_text())
        self.assertIn("ñébulo 水".encode(), raw)
        self.assertEqual(len(raw), resources["plan_bytes"])
        self.assertEqual(max(map(len, raw.splitlines())), resources["max_line_bytes"])
        self.assertEqual(resources["max_line_bytes"] + 1, resources["max_jsonl_line_bytes"])
        self.assertEqual(hashlib.sha256((self.directory / "resources.json").read_bytes()).hexdigest(),
                         outcome["resources_sha256"])
        self.assertEqual("commit-confirmed", outcome["disposition"])
        self.assertIn("SET LOCAL client_encoding='UTF8';", self.transcript.read_text())

    def test_resource_limits_reject_before_stream_with_complete_measured_metadata(self):
        for index, bounds in enumerate((dict(max_rows=0), dict(max_native_inputs=1),
                                        dict(max_bytes=1), dict(max_line_bytes=1))):
            with self.subTest(bounds=bounds):
                self.directory = self.root / f"resource-limit-{index}"
                self.transcript.unlink(missing_ok=True)
                with self.assertRaisesRegex(REPAIR.RepairProtocolError, "resource envelope rejected") as caught:
                    self.run_resource_repair(**bounds)
                self.assert_resource_plan_not_streamed()
                message = str(caught.exception)
                for field in REPAIR.RESOURCE_FIELDS:
                    self.assertIn(field, message)
                self.assertIn(str(self.directory / "resources.json"), message)
                self.assertNotIn("trajectory_ewkb", message)

    def test_resource_disk_admission_includes_declared_auxiliary_reserve(self):
        with patch.object(REPAIR.shutil, "disk_usage", return_value=SimpleNamespace(free=1024)), \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "receipt and auxiliary reserve"):
            self.run_resource_repair(auxiliary_reserve_bytes=1024)
        self.assert_resource_plan_not_streamed()

    def test_dependent_readback_admission_precedes_journal_reservation_and_stream(self):
        calls = []
        def reject(resources):
            self.assertTrue((self.directory / "resources.json").is_file())
            self.assertFalse((self.directory / "plan.jsonl").exists())
            calls.append(resources["plan_bytes"])
            return ["current receipt readback requires more than its admitted allowance"]
        with patch.object(REPAIR.os, "posix_fallocate") as reserve, \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "current receipt readback"):
            self.run_resource_repair(resource_validator=reject)
        reserve.assert_not_called()
        self.assertEqual(1, len(calls))
        self.assert_resource_plan_not_streamed()

    def test_measurement_reports_dependent_readback_rejection_and_confirms_rollback(self):
        with patch.object(REPAIR.os, "posix_fallocate") as reserve:
            result = self.run_resource_repair(measurement_only=True,
                resource_validator=lambda resources: ["current readback needs three complete reads"])
        reserve.assert_not_called()
        self.assertEqual("measurement-only-rollback-confirmed", result["disposition"])
        self.assertFalse(result["envelope_admitted"])
        self.assertIn("current readback needs three complete reads", result["resource_rejections"])
        self.assertNotIn("STREAM_RETAINED_PLAN;", self.transcript.read_text())
        self.assertNotIn("APPLY_RETAINED_PLAN;", self.transcript.read_text())
        self.assertFalse((self.directory / "plan.jsonl").exists())
        self.assertFalse((self.directory / "submission.json").exists())

    def test_resource_fsync_failure_never_submits_receipt_stream(self):
        fsync = REPAIR.os.fsync
        def fail_resource_sync(fd):
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "resources.json"):
                raise OSError("injected resource receipt fsync failure")
            fsync(fd)
        with patch.object(REPAIR.os, "fsync", side_effect=fail_resource_sync), self.assertRaises(OSError):
            self.run_resource_repair()
        self.assert_resource_plan_not_streamed()

    def test_resource_barrier_keeps_the_original_planning_deadline(self):
        monotonic, fsync = REPAIR.time.monotonic, REPAIR.os.fsync
        elapsed = [0]
        def slow_resource_sync(fd):
            fsync(fd)
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "resources.json"):
                elapsed[0] += 6
        with patch.object(REPAIR.os, "fsync", side_effect=slow_resource_sync), \
                patch.object(REPAIR.time, "monotonic", side_effect=lambda: monotonic() + elapsed[0]), \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "timed out|deadline"):
            self.run_resource_repair()
        self.assert_resource_plan_not_streamed()

    def test_resource_receipt_must_match_every_measured_count_and_size(self):
        for suffix in ("bytes", "line", "parents", "children"):
            with self.subTest(suffix=suffix):
                self.directory = self.root / ("mismatch-" + suffix)
                self.transcript.unlink(missing_ok=True)
                with self.assertRaisesRegex(REPAIR.RepairProtocolError, "does not match its retained resource summary"):
                    self.run_resource_repair("resources-mismatch-" + suffix)
                self.assert_not_submitted()
                self.assertIn("STREAM_RETAINED_PLAN;", self.transcript.read_text())
                self.assertTrue((self.directory / "resources.json").is_file())
                self.assertTrue((self.directory / "plan.jsonl").is_file())
                self.assertFalse((self.directory / "submission.json").exists())

    def test_resource_header_rejects_boolean_count_before_stream(self):
        with self.assertRaisesRegex(REPAIR.RepairProtocolError, "nonnegative integer native_input_rows"):
            self.run_resource_repair("resources-invalid-count")
        self.assert_not_submitted()
        self.assertNotIn("STREAM_RETAINED_PLAN;", self.transcript.read_text())
        self.assertTrue((self.directory / "resources.json").is_file())

    def test_impossible_resource_sizes_reject_before_reservation_or_measurement_success(self):
        for suffix in ("short-total", "long-total", "line"):
            for measurement_only in (False, True):
                with self.subTest(suffix=suffix, measurement_only=measurement_only):
                    self.directory = self.root / ("impossible-" + suffix + str(measurement_only))
                    self.transcript.unlink(missing_ok=True)
                    with patch.object(REPAIR.os, "posix_fallocate") as reserve, \
                            self.assertRaisesRegex(REPAIR.RepairProtocolError, "resource summary"):
                        self.run_resource_repair("resources-impossible-" + suffix,
                                                 measurement_only=measurement_only)
                    reserve.assert_not_called()
                    self.assert_resource_plan_not_streamed()
                    self.assertFalse((self.directory / "measurement.json").exists())

    def test_complete_plan_reservation_and_admission_are_durable_before_stream(self):
        reserved = []
        synced = []
        reserve, fsync, send = REPAIR.os.posix_fallocate, REPAIR.os.fsync, REPAIR.PsqlTransaction.send
        def reserve_plan(fd, offset, length):
            self.assertEqual(str(self.directory / "plan.jsonl"), os.readlink(f"/proc/self/fd/{fd}"))
            self.assertEqual(0, offset)
            self.assertEqual(0, os.lseek(fd, 0, os.SEEK_CUR))
            self.assertTrue((self.directory / "resources.json").is_file())
            self.assertNotIn("STREAM_RETAINED_PLAN;", self.transcript.read_text())
            reserved.append(length)
            reserve(fd, offset, length)
        def sync(fd):
            fsync(fd)
            synced.append(os.readlink(f"/proc/self/fd/{fd}"))
        def submit(tx, sql):
            if "STREAM_RETAINED_PLAN;" in sql:
                resources = json.loads((self.directory / "resources.json").read_text())
                self.assertEqual([resources["plan_bytes"]], reserved)
                self.assertEqual(resources["plan_bytes"], (self.directory / "plan.jsonl").stat().st_size)
                self.assertIn(str(self.directory / "resource-admission.json"), synced)
                self.assertIn(str(self.directory), synced)
            send(tx, sql)
        with patch.object(REPAIR.os, "posix_fallocate", side_effect=reserve_plan), \
                patch.object(REPAIR.os, "fsync", side_effect=sync), \
                patch.object(REPAIR.PsqlTransaction, "send", new=submit):
            outcome = self.run_resource_repair()
        raw = (self.directory / "plan.jsonl").read_bytes()
        admission = json.loads((self.directory / "resource-admission.json").read_text())
        self.assertEqual([len(raw)], reserved)
        self.assertEqual(len(raw), admission["expected_plan_bytes"])
        self.assertEqual("posix_fallocate-succeeded", admission["journal_preallocation"])
        self.assertEqual(hashlib.sha256(raw).hexdigest(), outcome["plan_sha256"])

    def test_explicit_larger_envelope_preserves_more_than_old_native_input_limit(self):
        outcome = self.run_resource_repair("resources-many", max_native_inputs=100001, timeout=30)
        self.assertEqual(100001, outcome["native_input_rows"])
        self.assertEqual(100001, outcome["max_native_inputs"])
        with (self.directory / "plan.jsonl").open("rb") as source:
            self.assertEqual(100004, sum(1 for _ in source))
        resources = json.loads((self.directory / "resources.json").read_text())
        self.assertEqual(resources["plan_bytes"], (self.directory / "plan.jsonl").stat().st_size)
        self.assertEqual(100001, resources["native_input_rows"])

    def test_reservation_failure_forbids_receipt_stream_even_after_disk_admission(self):
        with patch.object(REPAIR.os, "posix_fallocate", side_effect=OSError("injected quota failure")), \
                self.assertRaises(OSError):
            self.run_resource_repair()
        self.assert_not_submitted()
        self.assertNotIn("STREAM_RETAINED_PLAN;", self.transcript.read_text())
        self.assertEqual(b"", (self.directory / "plan.jsonl").read_bytes())

    def test_failed_reserved_receipt_truncates_unused_tail_to_hashed_prefix(self):
        for suffix in ("bytes", "line", "parents", "children"):
            with self.subTest(suffix=suffix):
                self.directory = self.root / ("reserved-mismatch-" + suffix)
                self.transcript.unlink(missing_ok=True)
                with self.assertRaises(REPAIR.RepairProtocolError):
                    self.run_resource_repair("resources-mismatch-" + suffix)
                self.assert_not_submitted()
                raw = (self.directory / "plan.jsonl").read_bytes()
                self.assertTrue(raw.endswith(b"\n"))
                self.assertNotIn(b"\0", raw)
                self.assertEqual(5, len(raw.splitlines()))
                failure = json.loads((self.directory / "failure.json").read_text())
                self.assertEqual(hashlib.sha256(raw).hexdigest(), failure["plan_sha256"])
                self.assertEqual(len(raw), failure["retained_bytes"])

    def test_metadata_reserve_is_required_in_addition_to_explicit_auxiliary_reserve(self):
        with patch.object(REPAIR.shutil, "disk_usage", return_value=SimpleNamespace(free=1024 * 1024)), \
                patch.object(REPAIR.os, "posix_fallocate") as reserve, \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "reserve require|insufficient"):
            self.run_resource_repair(auxiliary_reserve_bytes=0)
        reserve.assert_not_called()
        self.assert_resource_plan_not_streamed()

    def test_server_apply_timeout_is_refreshed_from_budget_remaining_after_durable_plan(self):
        fsync = REPAIR.os.fsync
        monotonic = REPAIR.time.monotonic
        elapsed = [0]
        def consume_budget(fd):
            fsync(fd)
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "plan.jsonl"):
                elapsed[0] += 2
        with patch.object(REPAIR.os,"fsync",side_effect=consume_budget), \
                patch.object(REPAIR.time,"monotonic",side_effect=lambda:monotonic()+elapsed[0]):
            result=self.run_repair(timeout=5)
        self.assertLessEqual(result["apply_statement_timeout_milliseconds"],3000)
        self.assertGreater(result["initial_database_timeout_milliseconds"],4000)
        sql=self.transcript.read_text()
        refresh=f"SET LOCAL statement_timeout='{result['apply_statement_timeout_milliseconds']}ms';"
        self.assertIn(refresh+"\nAPPLY_RETAINED_PLAN;",sql)

    def test_admission_and_persistence_share_one_deadline_without_reset(self):
        reject, fsync, monotonic = REPAIR.resource_rejections, REPAIR.os.fsync, REPAIR.time.monotonic
        elapsed = [0]
        def slow_admission(*args, **kwargs):
            result = reject(*args, **kwargs)
            if elapsed[0] == 0:
                elapsed[0] += 3
            return result
        def slow_plan_sync(fd):
            fsync(fd)
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "plan.jsonl"):
                elapsed[0] += 3
        with patch.object(REPAIR, "resource_rejections", side_effect=slow_admission), \
                patch.object(REPAIR.os, "fsync", side_effect=slow_plan_sync), \
                patch.object(REPAIR.time, "monotonic", side_effect=lambda: monotonic() + elapsed[0]), \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "timed out|deadline"):
            self.run_resource_repair(timeout=5, persistence_timeout=5)
        self.assert_not_submitted()
        self.assertIn("STREAM_RETAINED_PLAN;", self.transcript.read_text())
        self.assertEqual("durability", json.loads((self.directory / "failure.json").read_text())["phase_at_failure"])

    def test_measurement_reports_excess_envelope_after_confirmed_rollback_only(self):
        with patch.object(REPAIR.os, "posix_fallocate") as reserve:
            measured = self.run_resource_repair(measurement_only=True, max_native_inputs=1, max_bytes=1)
        reserve.assert_not_called()
        self.assertEqual("measurement-only-rollback-confirmed", measured["disposition"])
        self.assertFalse(measured["envelope_admitted"])
        self.assertEqual(2, measured["resources"]["native_input_rows"])
        self.assertGreater(measured["resources"]["plan_bytes"], 1)
        commands = self.transcript.read_text()
        self.assertIn("ROLLBACK;", commands)
        self.assertNotIn("STREAM_RETAINED_PLAN;", commands)
        self.assertNotIn("APPLY_RETAINED_PLAN;", commands)
        self.assertNotIn("COMMIT;", commands)
        self.assertTrue((self.directory / "measurement.json").is_file())
        for artifact in ("plan.jsonl", "submission.json", "outcome.json"):
            self.assertFalse((self.directory / artifact).exists())

    def test_measurement_requires_complete_rollback_acknowledgement(self):
        with self.assertRaises(REPAIR.RepairProtocolError):
            self.run_resource_repair("resources-rollback-disconnect", measurement_only=True)
        self.assert_resource_plan_not_streamed()
        self.assertFalse((self.directory / "measurement.json").exists())

    def test_original_bytes_and_plan_are_durable_before_mutation(self):
        events = []
        fsync, send = REPAIR.os.fsync, REPAIR.PsqlTransaction.send
        def record_sync(fd):
            events.append(os.readlink(f"/proc/self/fd/{fd}"))
            fsync(fd)
        def record_send(tx, sql):
            if "APPLY_RETAINED_PLAN;" in sql:
                for name in ("plan.jsonl", "manifest.json", "submission.json"):
                    self.assertIn(str(self.directory / name), events)
                self.assertGreaterEqual(events.count(str(self.directory)), 3)
            send(tx, sql)
        with patch.object(REPAIR.os, "fsync", side_effect=record_sync), \
                patch.object(REPAIR.PsqlTransaction, "send", new=record_send):
            result = self.run_repair()
        self.assertEqual("commit-confirmed", result["disposition"])
        rows = [json.loads(line) for line in (self.directory / "plan.jsonl").read_bytes().splitlines()]
        self.assertEqual("00000080ff", rows[1]["original"]["trajectory_ewkb"])
        self.assertEqual(hashlib.sha256((self.directory / "plan.jsonl").read_bytes()).hexdigest(), result["plan_sha256"])

    def test_receipt_fsync_failure_never_submits_mutation(self):
        fsync = REPAIR.os.fsync
        def fail_plan_sync(fd):
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "plan.jsonl"):
                raise OSError("injected durable storage failure")
            fsync(fd)
        with patch.object(REPAIR.os, "fsync", side_effect=fail_plan_sync), self.assertRaises(OSError):
            self.run_repair()
        self.assert_not_submitted()

    def native_records(self):
        return [json.loads(line) for line in (self.directory / "plan.jsonl").read_bytes().splitlines()]

    def assert_native_prefix_retained(self, kinds):
        self.assert_not_submitted()
        receipt = (self.directory / "plan.jsonl").read_bytes()
        records = self.native_records()
        self.assertEqual(kinds, [record["kind"] for record in records])
        self.assertEqual("01" * 16, records[1]["entity_id"])
        self.assertEqual("00000080ff", records[1]["content"]["trajectory_ewkb"])
        failure = json.loads((self.directory / "failure.json").read_text())
        self.assertEqual(hashlib.sha256(receipt).hexdigest(), failure["plan_sha256"])
        self.assertFalse((self.directory / "submission.json").exists())
        self.assertFalse((self.directory / "outcome.json").exists())

    def test_native_input_snapshots_are_durable_before_apply_and_counted_separately(self):
        sync_events = []
        fsync, send = REPAIR.os.fsync, REPAIR.PsqlTransaction.send
        def record_sync(fd):
            path = os.readlink(f"/proc/self/fd/{fd}")
            fsync(fd)
            sync_events.append(path)
        def record_send(tx, sql):
            if "APPLY_RETAINED_PLAN;" in sql:
                for name in ("plan.jsonl", "manifest.json", "submission.json"):
                    self.assertIn(str(self.directory / name), sync_events)
                self.assertGreaterEqual(sync_events.count(str(self.directory)), 3)
                records = self.native_records()
                self.assertEqual(["context", "native-input", "native-input", "physicality", "plan"],
                                 [record["kind"] for record in records])
                self.assertEqual(["01" * 16, "02" * 16],
                                 [record["entity_id"] for record in records[1:3]])
                self.assertEqual(["00000080ff", "00800000fe"],
                                 [record["content"]["trajectory_ewkb"] for record in records[1:3]])
                for record in records[1:3]:
                    self.assertEqual("\\x" + record["entity_id"], record["entity"]["id"])
                    self.assertEqual(record["entity_id"], record["content"]["entity_id"])
                self.assertEqual(1, records[-1]["count"])
                self.assertEqual(2, records[-1]["native_input_count"])
            send(tx, sql)
        with patch.object(REPAIR.os, "fsync", side_effect=record_sync), \
                patch.object(REPAIR.PsqlTransaction, "send", new=record_send):
            result = self.run_repair("native-success", max_rows=1, max_native_inputs=2)
        self.assertEqual("commit-confirmed", result["disposition"])
        self.assertEqual(1, result["planned_rows"])
        self.assertEqual(1, result["applied"]["count"])
        self.assertEqual(2, result["native_input_rows"])
        self.assertEqual(2, result["max_native_inputs"])
        self.assertEqual(hashlib.sha256((self.directory / "plan.jsonl").read_bytes()).hexdigest(),
                         result["plan_sha256"])

    def test_native_input_requires_identity_entity_and_content_snapshots(self):
        for missing in ("entity_id", "entity", "content"):
            with self.subTest(missing=missing):
                self.directory = self.root / ("missing-" + missing)
                self.transcript.unlink(missing_ok=True)
                with self.assertRaisesRegex(REPAIR.RepairProtocolError,
                                            "native input lacks identity or exact content snapshot"):
                    self.run_repair("native-missing-" + missing)
                self.assert_native_prefix_retained(["context", "native-input"])

    def test_native_input_summary_count_must_match_complete_retained_evidence(self):
        with self.assertRaisesRegex(REPAIR.RepairProtocolError, "count does not match retained rows"):
            self.run_repair("native-bad-count")
        self.assert_native_prefix_retained(
            ["context", "native-input", "native-input", "physicality", "plan"])
        self.assertEqual("00800000fe", self.native_records()[2]["content"]["trajectory_ewkb"])

    def test_native_input_bound_rejects_excess_children_within_parent_bound(self):
        with self.assertRaisesRegex(REPAIR.RepairProtocolError, "native input evidence exceeds row bound"):
            self.run_repair("native-success", max_rows=1, max_native_inputs=1)
        self.assert_native_prefix_retained(["context", "native-input"])

    def test_bounded_psql_fetch_is_configured_before_plan_without_changing_receipts(self):
        outcome = self.run_repair("native-chunked", max_rows=1, max_native_inputs=17)
        commands = self.transcript.read_text().splitlines()
        self.assertIn("\\set FETCH_COUNT 8", commands)
        self.assertIn("\\set SHOW_ALL_RESULTS on", commands)
        self.assertLess(commands.index("\\set FETCH_COUNT 8"), commands.index("SELECT_LOCKED_EVIDENCE;"))
        records = self.native_records()
        self.assertEqual(["context"] + ["native-input"] * 17 + ["physicality", "plan"],
                         [record["kind"] for record in records])
        self.assertEqual([f"{identity:032x}" for identity in range(1, 18)],
                         [record["entity_id"] for record in records[1:18]])
        receipt = (self.directory / "plan.jsonl").read_bytes()
        self.assertEqual(b"".join((json.dumps(record) + "\n").encode() for record in records), receipt)
        self.assertEqual(hashlib.sha256(receipt).hexdigest(), outcome["plan_sha256"])
        self.assertEqual(17, outcome["native_input_rows"])
        self.assertEqual(1, outcome["applied"]["count"])
        self.assertEqual(8, outcome["psql_fetch_rows"])
        self.assertEqual(2 * 1024 * 1024, outcome["max_line_bytes"])
        self.assertEqual(512 * 1024 * 1024, outcome["max_bytes"])
        self.assertEqual(5, outcome["timeout_seconds"])

    def test_larger_record_envelope_reduces_fetch_group_instead_of_scaling_memory(self):
        outcome = self.run_repair(max_line_bytes=8 * 1024 * 1024)
        self.assertIn("\\set FETCH_COUNT 2", self.transcript.read_text().splitlines())
        self.assertEqual(2, outcome["psql_fetch_rows"])
        self.assertEqual(8 * 1024 * 1024, outcome["max_line_bytes"])

    def test_sql_send_cannot_block_past_the_phase_deadline(self):
        with (self.root / "blocked-child.log").open("wb") as errors:
            tx = REPAIR.PsqlTransaction(
                [sys.executable, "-c", "import time; time.sleep(0.5)"],
                errors, timeout=0.05, max_line_bytes=1024)
            try:
                # The child never reads: this exceeds the pipe capacity and must
                # reach the deadline instead of blocking in FileIO.write.
                with self.assertRaisesRegex(REPAIR.RepairProtocolError, "timed out|deadline"):
                    tx.send("X" * (2 * 1024 * 1024))
            finally:
                tx.close()

    def test_receive_uses_the_sql_send_phase_deadline(self):
        with (self.root / "phase-child.log").open("wb") as errors:
            tx = REPAIR.PsqlTransaction(
                [sys.executable, "-c", "import sys; print(sys.stdin.readline().strip(), flush=True)"],
                errors, timeout=5, max_line_bytes=1024)
            try:
                tx.send("PHASE_COMPLETE\n")
                with patch.object(REPAIR.time, "monotonic", return_value=tx.deadline + 1), \
                        self.assertRaisesRegex(REPAIR.RepairProtocolError, "timed out|deadline"):
                    list(tx.lines("PHASE_COMPLETE"))
            finally:
                tx.close()

    def test_persistence_overrun_retains_evidence_without_sending_apply(self):
        fsync, monotonic = REPAIR.os.fsync, REPAIR.time.monotonic
        for artifact in ("plan.jsonl", "manifest.json", "submission.json"):
            with self.subTest(artifact=artifact):
                self.directory = self.root / ("slow-" + artifact)
                self.transcript.unlink(missing_ok=True)
                elapsed = [0]
                def slow_sync(fd):
                    fsync(fd)
                    if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / artifact):
                        elapsed[0] += 2
                with patch.object(REPAIR.os, "fsync", side_effect=slow_sync), \
                        patch.object(REPAIR.time, "monotonic", side_effect=lambda: monotonic() + elapsed[0]), \
                        self.assertRaisesRegex(REPAIR.RepairProtocolError, "persistence timed out|deadline"):
                    self.run_repair("native-success", persistence_timeout=1)
                self.assert_not_submitted()
                self.assertEqual(["context", "native-input", "native-input", "physicality", "plan"],
                                 [record["kind"] for record in self.native_records()])
                receipt = (self.directory / "plan.jsonl").read_bytes()
                failure = json.loads((self.directory / "failure.json").read_text())
                self.assertEqual(hashlib.sha256(receipt).hexdigest(), failure["plan_sha256"])
                self.assertFalse((self.directory / "outcome.json").exists())

    def test_phase_and_persistence_limits_are_recorded_and_finite(self):
        outcome = self.run_repair(persistence_timeout=7)
        self.assertEqual(5, outcome["timeout_seconds"])
        self.assertEqual(7, outcome["persistence_timeout_seconds"])
        self.assertLessEqual(outcome["remaining_seconds_at_connection_start"], 5)
        self.assertLessEqual(outcome["initial_database_timeout_milliseconds"], 5000)
        self.assertIn(f"SET LOCAL idle_in_transaction_session_timeout='{outcome['initial_database_timeout_milliseconds']}ms';",
                      self.transcript.read_text())
        for parameter in ("timeout", "persistence_timeout"):
            for timeout in (0, -1, True, float("inf"), float("nan")):
                with self.subTest(parameter=parameter, timeout=timeout), \
                        self.assertRaisesRegex(ValueError, "finite and positive|positive integer"):
                    self.run_repair(**{parameter: timeout})

    def test_resource_limits_reject_noninteger_or_invalid_values_before_launch(self):
        for parameter in ("max_rows", "max_native_inputs", "max_bytes", "max_line_bytes"):
            invalid = [True, None, float("inf"), float("nan"), 1.5, -1]
            if parameter in ("max_bytes", "max_line_bytes"):
                invalid.append(0)
            for value in invalid:
                with self.subTest(parameter=parameter, value=value), self.assertRaises(ValueError):
                    self.run_repair(**{parameter: value})
                self.assertFalse(self.transcript.exists())
                self.assertFalse(self.directory.exists())

    def test_apply_acknowledgement_retains_at_most_one_matching_record(self):
        for mode in ("missing-applied", "wrong-applied-count", "extra-applied"):
            with self.subTest(mode=mode):
                self.directory = self.root / mode
                with self.assertRaises(REPAIR.RepairProtocolError):
                    self.run_repair(mode)
                failure = json.loads((self.directory / "failure.json").read_text())
                self.assertEqual("submission-outcome-unknown", failure["disposition"])
                self.assertTrue((self.directory / "plan.jsonl").is_file())
                self.assertTrue((self.directory / "manifest.json").is_file())
                self.assertTrue((self.directory / "submission.json").is_file())
                self.assertFalse((self.directory / "outcome.json").exists())

    def test_rejects_incomplete_ambiguous_or_unbounded_plans(self):
        for mode, bounds in (("bad-count", {}), ("unresolved", {}),
                             ("missing-original", {}), ("non-object", {}),
                             ("success", {"max_rows": 0}),
                             ("success", {"max_bytes": 40}),
                             ("success", {"max_line_bytes": 12})):
            with self.subTest(mode=mode, bounds=bounds):
                self.directory = self.root / (mode + str(len(list(self.root.iterdir()))))
                self.transcript.unlink(missing_ok=True)
                with self.assertRaises(REPAIR.RepairProtocolError):
                    self.run_repair(mode, **bounds)
                self.assert_not_submitted()

    def test_connection_loss_before_plan_does_not_submit(self):
        with self.assertRaises(REPAIR.RepairProtocolError):
            self.run_repair("plan-disconnect")
        self.assert_not_submitted()

    def test_lost_apply_or_commit_acknowledgement_remains_unknown(self):
        for mode in ("apply-disconnect", "commit-disconnect"):
            with self.subTest(mode=mode):
                self.directory = self.root / mode
                with self.assertRaises(REPAIR.RepairProtocolError):
                    self.run_repair(mode)
                outcome = json.loads((self.directory / "failure.json").read_text())
                self.assertEqual("submission-outcome-unknown", outcome["disposition"])
                self.assertTrue((self.directory / "plan.jsonl").is_file())
                self.assertTrue((self.directory / "submission.json").is_file())
                self.assertFalse((self.directory / "outcome.json").exists())

    def test_external_deadline_includes_budget_spent_before_protocol_entry(self):
        monotonic = REPAIR.time.monotonic
        result = self.run_repair(timeout=1800, deadline_monotonic=monotonic() + 4)
        self.assertEqual(1800, result["timeout_seconds"])
        self.assertLessEqual(result["remaining_seconds_at_connection_start"], 4)
        self.assertLessEqual(result["initial_database_timeout_milliseconds"], 4000)


    def test_expired_external_deadline_does_not_launch_database_connection(self):
        with self.assertRaisesRegex(REPAIR.RepairProtocolError, "maintenance deadline"):
            self.run_repair(deadline_monotonic=REPAIR.time.monotonic() - 1)
        self.assertFalse(self.transcript.exists())


    def test_full_sql_input_pipe_cannot_block_past_shared_deadline(self):
        with (self.root / "unread-child-errors.log").open("wb") as errors:
            tx = REPAIR.PsqlTransaction(
                [sys.executable, "-c", "import time; time.sleep(5)"], errors,
                timeout=1, max_line_bytes=4096,
                deadline_monotonic=REPAIR.time.monotonic() + 0.25)
            try:
                with self.assertRaisesRegex(REPAIR.RepairProtocolError, "timed out|deadline"):
                    tx.send("x" * (1024 * 1024))
            finally:
                tx.process.terminate()
                tx.close()


    def test_deadline_expires_after_submission_remains_unknown(self):
        send = REPAIR.PsqlTransaction.send
        monotonic = REPAIR.time.monotonic
        elapsed = [0]
        def expire_after_submission(tx, sql):
            send(tx, sql)
            if "APPLY_RETAINED_PLAN;" in sql:
                elapsed[0] += 6
        with patch.object(REPAIR.PsqlTransaction, "send", new=expire_after_submission), \
                patch.object(REPAIR.time, "monotonic", side_effect=lambda: monotonic() + elapsed[0]), \
                self.assertRaises(REPAIR.RepairProtocolError):
            self.run_repair(timeout=5)
        failure = json.loads((self.directory / "failure.json").read_text())
        self.assertEqual("submission-outcome-unknown", failure["disposition"])
        self.assertEqual("apply_and_commit_confirmation", failure["phase_at_failure"])
        self.assertTrue((self.directory / "submission.json").exists())
        self.assertFalse((self.directory / "outcome.json").exists())


    def test_deadline_overrun_during_final_receipt_retains_confirmed_commit_and_actual_timing(self):
        fsync = REPAIR.os.fsync
        monotonic = REPAIR.time.monotonic
        elapsed = [0]
        def delayed_outcome_sync(fd):
            fsync(fd)
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "outcome.json"):
                elapsed[0] += 6
        with patch.object(REPAIR.os, "fsync", side_effect=delayed_outcome_sync), \
                patch.object(REPAIR.time, "monotonic", side_effect=lambda: monotonic() + elapsed[0]), \
                self.assertRaises(REPAIR.RepairProtocolError):
            self.run_repair(timeout=5)
        self.assertEqual("commit-confirmed", json.loads((self.directory / "outcome.json").read_text())["disposition"])
        self.assertEqual("commit-confirmed-deadline-exceeded",
                         json.loads((self.directory / "failure.json").read_text())["disposition"])
        completion = json.loads((self.directory / "completion-timing.json").read_text())
        self.assertTrue(completion["maintenance_deadline_exceeded"])
        self.assertGreaterEqual(completion["outcome_durability_seconds"], 6)


    def test_existing_receipt_is_never_replaced(self):
        self.directory.mkdir()
        old = self.directory / "plan.jsonl"
        old.write_bytes(b"preserved historical evidence\n")
        with self.assertRaises(FileExistsError):
            self.run_repair()
        self.assertEqual(b"preserved historical evidence\n", old.read_bytes())
        self.assertFalse(self.transcript.exists())


class RepairReceiptReconciliationTests(unittest.TestCase):
    def setUp(self):
        sys.path.insert(0, str(ROOT / "scripts"))
        spec = importlib.util.spec_from_file_location("legacy_repair", ROOT / "scripts/repair-legacy-content.py")
        self.repair = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.repair)
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def receipt(self, name, *, reconciles=None, confirmed=False):
        directory = self.root / name
        directory.mkdir()
        context = {"kind": "context", "database": "fixture",
                   "prior_submission_reconciliation": reconciles or []}
        payload = (json.dumps(context) + "\n" + json.dumps({"kind":"plan","count":0,"native_input_count":0,"unresolved":0}) + "\n").encode()
        (directory / "plan.jsonl").write_bytes(payload)
        manifest = {"schema":"laplace.legacy-content-repair-plan/v1",
                    "plan_sha256":hashlib.sha256(payload).hexdigest(), "plan_bytes":len(payload), "planned_rows":0,
                    "native_input_rows":0}
        (directory / "manifest.json").write_text(json.dumps(manifest))
        (directory / "submission.json").write_text("{}")
        if confirmed:
            (directory / "outcome.json").write_text(json.dumps({**manifest,"disposition":"commit-confirmed","applied":{"count":0}}))
        return directory

    def test_partial_or_mismatched_outcome_remains_unknown(self):
        old = self.receipt("old")
        for payload in ('{', '{}', '{"disposition":"commit-confirmed","applied":null}'):
            with self.subTest(payload=payload):
                (old / "outcome.json").write_text(payload)
                self.assertEqual([old / "plan.jsonl"], self.repair.unresolved_submissions(self.root))

    def test_successful_reconciliation_closes_unknown_without_overwriting_old_evidence(self):
        old=self.receipt("old")
        original=(old / "plan.jsonl").read_bytes()
        current=self.receipt("new",confirmed=True,reconciles=[{
            "receipt":str(old / "plan.jsonl"),"rows":0,"disposition":"zero-row-no-mutation"}])
        self.repair.close_reconciled_submissions(self.root,current)
        self.assertEqual([],self.repair.unresolved_submissions(self.root))
        self.assertEqual(original,(old / "plan.jsonl").read_bytes())
        self.assertFalse((old / "outcome.json").exists())
        (current / "plan.jsonl").write_bytes(b"changed reconciliation evidence")
        with self.assertRaises(ValueError):self.repair.unresolved_submissions(self.root)

    def test_failed_reconciliation_cannot_close_old_unknown(self):
        old=self.receipt("old")
        current=self.receipt("new",reconciles=[{"receipt":str(old / "plan.jsonl"),"disposition":"originals-confirmed"}])
        with self.assertRaisesRegex(ValueError,"successful durable reconciliation"):
            self.repair.close_reconciled_submissions(self.root,current)
        self.assertFalse((old / "reconciliation.json").exists())

    def resource_receipt(self, name, *, padding=0, tail_padding=0, limits=None):
        directory = self.receipt(name)
        context = {"kind": "context", "database": "fixture", "padding": "x" * padding,
                   "prior_submission_reconciliation": []}
        summary = {"kind": "plan", "count": 0, "native_input_count": 0, "unresolved": 0,
                   "padding": "y" * tail_padding}
        payload = (json.dumps(context) + "\n" + json.dumps(summary) + "\n").encode()
        (directory / "plan.jsonl").write_bytes(payload)
        manifest = json.loads((directory / "manifest.json").read_text())
        manifest.update(plan_bytes=len(payload), plan_sha256=hashlib.sha256(payload).hexdigest())
        manifest.update(limits or {})
        (directory / "manifest.json").write_text(json.dumps(manifest))
        return directory

    def test_explicit_larger_receipt_envelope_overrides_a_smaller_module_default(self):
        directory = self.resource_receipt("admitted", padding=1024,
            limits={"max_bytes": 4096, "max_line_bytes": 2048})
        payload = (directory / "plan.jsonl").read_bytes()
        budget = self.repair.PriorReceiptBudget(4096)
        with patch.object(self.repair, "MAX_BYTES", 64):
            manifest, context = self.repair.verified_plan(directory, max_bytes=4096,
                max_line_bytes=2048, budget=budget)
        self.assertGreater(len(payload), 64)
        self.assertEqual(4096, manifest["max_bytes"])
        self.assertEqual("x" * 1024, context["padding"])
        self.assertEqual(len(payload), budget.bytes_read)
        self.assertEqual(payload, (directory / "plan.jsonl").read_bytes())

    def test_legacy_manifest_without_envelopes_keeps_historical_limits(self):
        directory = self.resource_receipt("legacy", padding=256)
        original_manifest = (directory / "manifest.json").read_bytes()
        original_plan = (directory / "plan.jsonl").read_bytes()
        with patch.object(self.repair, "MAX_BYTES", 32), \
                patch.object(self.repair, "MAX_LINE_BYTES", 32):
            manifest, context = self.repair.verified_plan(directory,
                max_bytes=4096, max_line_bytes=2048)
        self.assertNotIn("max_bytes", manifest)
        self.assertNotIn("max_line_bytes", manifest)
        self.assertEqual("fixture", context["database"])
        self.assertEqual(original_manifest, (directory / "manifest.json").read_bytes())
        self.assertEqual(original_plan, (directory / "plan.jsonl").read_bytes())

    def test_recorded_and_current_total_byte_limits_both_apply(self):
        for bounded_by in ("recorded", "current"):
            with self.subTest(bounded_by=bounded_by):
                directory = self.resource_receipt(bounded_by, padding=128,
                    limits={"max_bytes": 4096, "max_line_bytes": 2048})
                size = (directory / "plan.jsonl").stat().st_size
                current_limit = size
                if bounded_by == "recorded":
                    manifest = json.loads((directory / "manifest.json").read_text())
                    manifest["max_bytes"] = size - 1
                    (directory / "manifest.json").write_text(json.dumps(manifest))
                else:
                    current_limit = size - 1
                with self.assertRaises(ValueError):
                    self.repair.verified_plan(directory, max_bytes=current_limit, max_line_bytes=2048)
        directory = self.resource_receipt("exact-total", limits={"max_bytes": 4096, "max_line_bytes": 2048})
        size = (directory / "plan.jsonl").stat().st_size
        self.repair.verified_plan(directory, max_bytes=size, max_line_bytes=2048)

    def test_context_line_limit_excludes_lf_and_honors_both_envelopes(self):
        for bounded_by in ("recorded", "current"):
            with self.subTest(bounded_by=bounded_by):
                directory = self.resource_receipt("line-" + bounded_by, padding=256,
                    limits={"max_bytes": 4096, "max_line_bytes": 2048})
                line_size = len((directory / "plan.jsonl").read_bytes().split(b"\n", 1)[0])
                manifest = json.loads((directory / "manifest.json").read_text())
                manifest["max_line_bytes"] = line_size
                (directory / "manifest.json").write_text(json.dumps(manifest))
                self.repair.verified_plan(directory, max_bytes=4096, max_line_bytes=line_size)
                if bounded_by == "recorded":
                    manifest["max_line_bytes"] = line_size - 1
                    (directory / "manifest.json").write_text(json.dumps(manifest))
                with self.assertRaises(ValueError):
                    self.repair.verified_plan(directory, max_bytes=4096,
                        max_line_bytes=line_size - (bounded_by == "current"))

    def test_later_jsonl_line_cannot_exceed_recorded_or_current_limit(self):
        for bounded_by in ("recorded", "current"):
            with self.subTest(bounded_by=bounded_by):
                directory = self.resource_receipt("tail-" + bounded_by, tail_padding=512,
                    limits={"max_bytes": 4096, "max_line_bytes": 128 if bounded_by == "recorded" else 2048})
                self.assertLessEqual(len((directory / "plan.jsonl").read_bytes().split(b"\n", 1)[0]), 128)
                with self.assertRaises(ValueError):
                    self.repair.verified_plan(directory, max_bytes=4096,
                        max_line_bytes=128 if bounded_by == "current" else 2048)

    def test_resource_limits_require_positive_integers_not_booleans(self):
        directory = self.resource_receipt("invalid-current")
        for value in (True, False, 0, -1, 1.5, "1024", None):
            with self.subTest(budget=value), self.assertRaises(ValueError):
                self.repair.PriorReceiptBudget(value)
            for parameter in ("max_bytes", "max_line_bytes"):
                limits = {"max_bytes": 4096, "max_line_bytes": 2048, parameter: value}
                with self.subTest(parameter=parameter, value=value), self.assertRaises(ValueError):
                    self.repair.verified_plan(directory, **limits)

    def test_recorded_limits_and_plan_bytes_reject_invalid_numeric_fields(self):
        for parameter in ("max_bytes", "max_line_bytes", "plan_bytes"):
            for value in (True, False, 0, -1, 1.5, "1024", None):
                with self.subTest(parameter=parameter, value=value):
                    directory = self.resource_receipt(f"invalid-{parameter}-{type(value).__name__}-{value}",
                        limits={"max_bytes": 4096, "max_line_bytes": 2048, parameter: value})
                    with self.assertRaises(ValueError):
                        self.repair.verified_plan(directory, max_bytes=4096, max_line_bytes=2048)

    def test_manifest_metadata_is_bounded_independently_of_plan_envelope(self):
        directory = self.resource_receipt("oversized-manifest")
        manifest = json.loads((directory / "manifest.json").read_text())
        manifest["padding"] = "x" * (64 * 1024)
        (directory / "manifest.json").write_text(json.dumps(manifest))
        with self.assertRaises(ValueError):
            self.repair.verified_plan(directory, max_bytes=128 * 1024, max_line_bytes=2048)

    def test_resource_admission_does_not_replace_hash_and_size_verification(self):
        for corrupt in ("hash", "size"):
            with self.subTest(corrupt=corrupt):
                directory = self.resource_receipt("changed-" + corrupt,
                    limits={"max_bytes": 4096, "max_line_bytes": 2048})
                payload = (directory / "plan.jsonl").read_bytes()
                manifest = json.loads((directory / "manifest.json").read_text())
                manifest["plan_sha256" if corrupt == "hash" else "plan_bytes"] = \
                    "0" * 64 if corrupt == "hash" else len(payload) + 1
                (directory / "manifest.json").write_text(json.dumps(manifest))
                with self.assertRaises(ValueError):
                    self.repair.verified_plan(directory, max_bytes=4096, max_line_bytes=2048)
                self.assertEqual(payload, (directory / "plan.jsonl").read_bytes())

    def test_aggregate_budget_rejects_two_individually_admitted_receipts(self):
        first = self.resource_receipt("a-first", padding=128)
        second = self.resource_receipt("b-second", padding=128)
        total = sum((directory / "plan.jsonl").stat().st_size for directory in (first, second))
        for directory in (first, second):
            self.repair.verified_plan(directory, max_bytes=4096, max_line_bytes=2048)
        with self.assertRaises(ValueError):
            self.repair.unresolved_submissions(self.root, max_bytes=4096, max_line_bytes=2048,
                budget=self.repair.PriorReceiptBudget(total - 1))
        budget = self.repair.PriorReceiptBudget(total)
        self.assertEqual([first / "plan.jsonl", second / "plan.jsonl"],
            self.repair.unresolved_submissions(self.root, max_bytes=4096, max_line_bytes=2048, budget=budget))
        self.assertEqual(total, budget.bytes_read)

    def test_reconciliation_reference_reads_share_the_aggregate_budget(self):
        old = self.receipt("a-old")
        current = self.receipt("b-current", confirmed=True, reconciles=[{
            "receipt": str(old / "plan.jsonl"), "rows": 0, "disposition": "zero-row-no-mutation"}])
        self.repair.close_reconciled_submissions(self.root, current,
            max_bytes=4096, max_line_bytes=2048, budget=self.repair.PriorReceiptBudget(8192))
        old_size = (old / "plan.jsonl").stat().st_size
        current_size = (current / "plan.jsonl").stat().st_size
        with self.assertRaises(ValueError):
            self.repair.unresolved_submissions(self.root, max_bytes=4096, max_line_bytes=2048,
                budget=self.repair.PriorReceiptBudget(old_size + current_size))
        budget = self.repair.PriorReceiptBudget(old_size + 2 * current_size)
        self.assertEqual([], self.repair.unresolved_submissions(self.root,
            max_bytes=4096, max_line_bytes=2048, budget=budget))
        self.assertEqual(old_size + 2 * current_size, budget.bytes_read)

    def test_closure_cannot_ignore_the_shared_read_budget(self):
        old = self.receipt("old-budget")
        current = self.receipt("new-budget", confirmed=True, reconciles=[{
            "receipt": str(old / "plan.jsonl"), "rows": 0, "disposition": "zero-row-no-mutation"}])
        original = (old / "plan.jsonl").read_bytes()
        with self.assertRaises(ValueError):
            self.repair.close_reconciled_submissions(self.root, current,
                max_bytes=4096, max_line_bytes=2048,
                budget=self.repair.PriorReceiptBudget(len(original) - 1))
        self.assertFalse((old / "reconciliation.json").exists())
        self.assertEqual(original, (old / "plan.jsonl").read_bytes())


if __name__ == "__main__":
    unittest.main()
