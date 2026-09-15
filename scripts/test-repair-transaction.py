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
for line in sys.stdin:
    with transcript.open('a') as out: out.write(line)
    if line.startswith('\\echo PLAN_'):
        rows = [dict(kind='context', database='fixture'),
                dict(kind='physicality', original={'trajectory_ewkb':'00000080ff'},
                     proposed={'type':3}, evidence=[{'outcome':2}]),
                dict(kind='plan', count=2 if mode == 'bad-count' else 1,
                     unresolved=1 if mode == 'unresolved' else 0)]
        if mode == 'missing-original': del rows[1]['original']
        if mode == 'non-object': rows[1] = []
        if mode.startswith('native-') or mode.startswith('measured-'):
            children = [dict(kind='native-input', entity_id=identity,
                             entity={'id':'\\x'+identity,'tier':2},
                             content={'entity_id':identity,'trajectory_ewkb':trajectory})
                        for identity,trajectory in [('01'*16,'00000080ff'),
                                                    ('02'*16,'00800000fe')]]
            if mode.startswith('native-missing-'):
                del children[1][mode.removeprefix('native-missing-')]
            rows[1:1] = children
            rows[-1]['native_input_count'] = 1 if mode == 'native-bad-count' else 2
        if mode.startswith('measured-'):
            rows[1]['entity']['label'] = '♞ café'
            if mode == 'measured-many':
                rows[1:3] = [dict(kind='native-input', entity_id=f'{index:032x}',
                                 entity={'tier':2}, content={'trajectory_ewkb':'00000080ff'})
                             for index in range(100001)]
                rows[-1]['native_input_count'] = 100001
            body = [(json.dumps(row, ensure_ascii=False)+'\n').encode() for row in rows[1:]]
            inventory = dict(native_input_rows=rows[-1]['native_input_count'], physicality_rows=1,
                             body_records=len(body), body_bytes=sum(map(len,body)),
                             max_body_line_bytes=max(map(len,body)))
            rows[0]['resource_inventory'] = inventory
            if mode == 'measured-wrong-bytes': inventory['body_bytes'] += 1
            if mode == 'measured-wrong-native-count':
                inventory['native_input_rows'] += 1
                inventory['body_records'] += 1
            if mode == 'measured-wrong-max-line': inventory['max_body_line_bytes'] += 1
            if mode == 'measured-boolean-count': inventory['physicality_rows'] = True
            if mode == 'measured-missing-summary':
                inventory['body_bytes'] -= len(body[-1])
                inventory['body_records'] -= 1
            if mode == 'measured-truncated': rows.pop()
        if mode == 'plan-disconnect': sys.exit(3)
        for row in rows: print(json.dumps(row, ensure_ascii=False), flush=True)
        print(line.split()[1], flush=True)
    elif line.strip() == 'APPLY_RETAINED_PLAN;':
        receipt = (directory/'plan.jsonl').read_bytes()
        manifest = json.loads((directory/'manifest.json').read_text())
        assert manifest['plan_sha256'] == hashlib.sha256(receipt).hexdigest()
        assert (directory/'submission.json').is_file()
        if mode == 'apply-disconnect': sys.exit(4)
        print(json.dumps(dict(kind='applied',count=1,epoch=9)), flush=True)
    elif line.startswith('\\echo COMMITTED_'):
        if mode == 'commit-disconnect': sys.exit(5)
        print(line.split()[1], flush=True)
    elif line.startswith('\\echo DURABILITY_'):
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
            source_sha="a" * 40, max_rows=bounds.pop("max_rows", 2),
            timeout=bounds.pop("timeout", 5), **bounds)

    def assert_not_submitted(self):
        self.assertNotIn("APPLY_RETAINED_PLAN;", self.transcript.read_text())
        self.assertNotIn("COMMIT;", self.transcript.read_text())
        self.assertEqual("not-submitted", json.loads((self.directory / "failure.json").read_text())["disposition"])

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

    def test_complete_measured_plan_is_reserved_before_body_and_matches_utf8_bytes(self):
        reserved = []
        reserve = REPAIR.os.posix_fallocate
        def record_reservation(fd, offset, length):
            self.assertEqual(0, os.lseek(fd, 0, os.SEEK_CUR))
            self.assertFalse((self.directory / "submission.json").exists())
            reserved.append(length)
            reserve(fd, offset, length)
        with patch.object(REPAIR.os, "posix_fallocate", side_effect=record_reservation):
            result = self.run_repair("measured-success", max_bytes=None,
                                     max_native_inputs=None, require_resource_inventory=True)
        payload = (self.directory / "plan.jsonl").read_bytes()
        self.assertEqual([len(payload)], reserved)
        self.assertEqual(len(payload), result["max_bytes"])
        self.assertEqual(2, result["max_native_inputs"])
        self.assertEqual(hashlib.sha256(payload).hexdigest(), result["plan_sha256"])
        admission = json.loads((self.directory / "resource-admission.json").read_text())
        self.assertEqual(admission, result["resource_admission"])
        self.assertEqual(len(payload), admission["expected_plan_bytes"])
        self.assertEqual(4, admission["inventory"]["body_records"])
        self.assertIn("♞ café".encode(), payload)
        self.assertGreater(len(payload), len(payload.decode()))

    def test_measured_inventory_admits_more_than_old_native_input_limit_without_omission(self):
        result = self.run_repair("measured-many", max_bytes=None, max_native_inputs=None,
                                 require_resource_inventory=True, timeout=30)
        self.assertEqual(100001, result["native_input_rows"])
        self.assertEqual(100001, result["max_native_inputs"])
        with (self.directory / "plan.jsonl").open("rb") as source:
            self.assertEqual(100004, sum(1 for _ in source))
        self.assertEqual((self.directory / "plan.jsonl").stat().st_size, result["plan_bytes"])
        self.assertEqual(result["plan_bytes"], result["resource_admission"]["expected_plan_bytes"])

    def test_measured_inventory_mismatches_cannot_submit_and_leave_only_actual_prefix(self):
        for mode in ("measured-wrong-bytes", "measured-wrong-native-count", "measured-wrong-max-line",
                     "measured-boolean-count", "measured-missing-summary", "measured-truncated"):
            with self.subTest(mode=mode):
                self.directory = self.root / mode
                self.transcript.unlink(missing_ok=True)
                with self.assertRaises(REPAIR.RepairProtocolError):
                    self.run_repair(mode, max_bytes=None, max_native_inputs=None)
                self.assert_not_submitted()
                payload = (self.directory / "plan.jsonl").read_bytes()
                self.assertNotIn(b"\0", payload)
                failure = json.loads((self.directory / "failure.json").read_text())
                self.assertEqual(hashlib.sha256(payload).hexdigest(), failure["plan_sha256"])

    def test_measured_inventory_keeps_explicit_caller_limits(self):
        for label, bounds in (("owners", {"max_rows":0}),
                              ("native", {"max_native_inputs":1}),
                              ("bytes", {"max_bytes":550}),
                              ("line", {"max_line_bytes":150})):
            with self.subTest(label=label):
                self.directory = self.root / label
                self.transcript.unlink(missing_ok=True)
                with self.assertRaises(REPAIR.RepairProtocolError):
                    self.run_repair("measured-success", **bounds)
                self.assert_not_submitted()

    def test_missing_measurement_cannot_enable_derived_limits(self):
        for label, bounds in (("bytes", {"max_bytes":None}),
                              ("native", {"max_native_inputs":None}),
                              ("required", {"require_resource_inventory":True})):
            with self.subTest(label=label):
                self.directory = self.root / label
                self.transcript.unlink(missing_ok=True)
                with self.assertRaisesRegex(REPAIR.RepairProtocolError, "complete measured resource inventory"):
                    self.run_repair("success", **bounds)
                self.assert_not_submitted()

    def test_low_free_storage_cannot_submit(self):
        with patch.object(REPAIR.os, "fstatvfs", return_value=SimpleNamespace(f_bavail=1, f_frsize=4096)), \
                patch.object(REPAIR.os, "posix_fallocate") as reserve, \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "insufficient receipt storage"):
            self.run_repair("measured-success", max_bytes=None, max_native_inputs=None)
        reserve.assert_not_called()
        self.assert_not_submitted()

    def test_reservation_failure_cannot_submit_even_when_free_space_check_passes(self):
        with patch.object(REPAIR.os, "posix_fallocate", side_effect=OSError("injected quota failure")), \
                self.assertRaises(OSError):
            self.run_repair("measured-success", max_bytes=None, max_native_inputs=None)
        self.assert_not_submitted()
        self.assertEqual(b"", (self.directory / "plan.jsonl").read_bytes())

    def test_durability_overrun_never_sends_apply_or_commit(self):
        fsync = REPAIR.os.fsync
        monotonic = REPAIR.time.monotonic
        elapsed = [0]
        def delayed_sync(fd):
            fsync(fd)
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "plan.jsonl"):
                elapsed[0] += 6
        with patch.object(REPAIR.os, "fsync", side_effect=delayed_sync), \
                patch.object(REPAIR.time, "monotonic", side_effect=lambda: monotonic() + elapsed[0]), \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "receipt durability timed out"):
            self.run_repair(durability_timeout=5)
        self.assert_not_submitted()

    def test_selected_timeout_covers_database_idle_and_is_retained(self):
        result = self.run_repair(timeout=7, durability_timeout=11)
        self.assertIn(f"SET LOCAL statement_timeout='{result['initial_database_timeout_milliseconds']}ms';",
                      self.transcript.read_text())
        self.assertIn(f"SET LOCAL idle_in_transaction_session_timeout='{result['durability_idle_timeout_milliseconds']}ms';",
                      self.transcript.read_text())
        self.assertEqual(7, result["timeout_seconds"])
        self.assertEqual(11, result["durability_timeout_seconds"])
        self.assertLessEqual(result["remaining_seconds_at_connection_start"], 7)
        self.assertLess(result["durability_idle_timeout_milliseconds"], 7000)
        self.assertEqual({"planning_and_capture_seconds", "durability_seconds",
                          "apply_and_commit_confirmation_seconds", "protocol_seconds_through_commit_confirmation"},
                         set(result["phase_timings"]))
        completion = json.loads((self.directory / "completion-timing.json").read_text())
        self.assertFalse(completion["maintenance_deadline_exceeded"])
        self.assertGreaterEqual(completion["protocol_seconds_through_durable_outcome"],
                                result["phase_timings"]["protocol_seconds_through_commit_confirmation"])

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

    def test_shared_deadline_includes_planning_and_fsync_without_a_fresh_phase_budget(self):
        fsync = REPAIR.os.fsync
        monotonic = REPAIR.time.monotonic
        admit = REPAIR.resource_admission
        elapsed = [0]
        def slow_admission(*args, **kwargs):
            result = admit(*args, **kwargs)
            elapsed[0] += 3
            return result
        def delayed_sync(fd):
            fsync(fd)
            if os.readlink(f"/proc/self/fd/{fd}") == str(self.directory / "plan.jsonl"):
                elapsed[0] += 3
        with patch.object(REPAIR, "resource_admission", side_effect=slow_admission), \
                patch.object(REPAIR.os, "fsync", side_effect=delayed_sync), \
                patch.object(REPAIR.time, "monotonic", side_effect=lambda: monotonic() + elapsed[0]), \
                self.assertRaisesRegex(REPAIR.RepairProtocolError, "receipt durability timed out"):
            self.run_repair(timeout=5, durability_timeout=5)
        self.assert_not_submitted()

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
                with self.assertRaisesRegex(REPAIR.RepairProtocolError, "submission timed out"):
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

    def test_invalid_timeouts_fail_before_launch(self):
        for bounds in ({"timeout":0}, {"timeout":-1}, {"timeout":True},
                       {"durability_timeout":0}, {"durability_timeout":1.5}):
            with self.subTest(bounds=bounds), self.assertRaises(ValueError):
                self.run_repair(**bounds)
        self.assertFalse(self.transcript.exists())

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
        payload = (json.dumps(context) + "\n" + json.dumps({"kind":"plan","count":0,"unresolved":0}) + "\n").encode()
        (directory / "plan.jsonl").write_bytes(payload)
        manifest = {"schema":"laplace.legacy-content-repair-plan/v1",
                    "plan_sha256":hashlib.sha256(payload).hexdigest(), "plan_bytes":len(payload), "planned_rows":0}
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


if __name__ == "__main__":
    unittest.main()
