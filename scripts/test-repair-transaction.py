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
        if mode == 'plan-disconnect': sys.exit(3)
        for row in rows: print(json.dumps(row), flush=True)
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
            source_sha="a" * 40, max_rows=bounds.pop("max_rows", 2), timeout=5, **bounds)

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
