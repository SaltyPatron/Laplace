#!/usr/bin/env python3
"""Bounded receipt scheduling checks; no database or repair execution."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("history", ROOT / "scripts/lib/legacy_content_history.py")
HISTORY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HISTORY)


class HistoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def receipt(self, name, *, confirmed=False, reconciles=(), limit=None):
        path = self.root / name
        path.mkdir()
        raw = (json.dumps({"kind": "context", "prior_submission_reconciliation": list(reconciles)})
               + '\n{"kind":"plan","count":0,"unresolved":0}\n').encode()
        (path / "plan.jsonl").write_bytes(raw)
        manifest = {"schema": "laplace.legacy-content-repair-plan/v1", "planned_rows": 0,
                    "plan_bytes": len(raw), "plan_sha256": hashlib.sha256(raw).hexdigest()}
        if limit is not None:
            manifest["max_bytes"] = limit
        (path / "manifest.json").write_text(json.dumps(manifest))
        (path / "submission.json").write_text("{}")
        if confirmed:
            (path / "outcome.json").write_text(json.dumps({**manifest, "disposition": "commit-confirmed", "applied": {"count": 0}}))
        return path, manifest

    def reference(self, old, target):
        old_path, old_manifest = old
        target_path, target_manifest = target
        (old_path / "reconciliation.json").write_text(json.dumps({
            "original_plan_sha256": old_manifest["plan_sha256"],
            "reconciliation_directory": str(target_path),
            "reconciliation_plan_sha256": target_manifest["plan_sha256"]}))

    def test_repeated_target_reads_and_pending_closure_match_existing_schedule(self):
        one = self.receipt("one")
        two = self.receipt("two")
        pending = self.receipt("pending")
        target = self.receipt("target", confirmed=True, reconciles=[
            {"receipt": str(old[0] / "plan.jsonl"), "disposition": "originals-confirmed"}
            for old in (one, two)])
        self.reference(one, target)
        self.reference(two, target)
        result = HISTORY.measure(self.root)
        expected = sum(item[1]["plan_bytes"] for item in (one, two, pending)) + 3 * target[1]["plan_bytes"]
        self.assertEqual("complete", result["status"])
        self.assertEqual(expected, result["scheduled_discovery_plan_bytes"])
        self.assertEqual(6, result["discovery_read_count"])
        self.assertEqual(["pending"], result["pending_receipts"])
        self.assertEqual(pending[1]["plan_bytes"], result["scheduled_closure_prior_plan_bytes"])
        self.assertEqual(pending[1]["plan_bytes"], result["scheduled_replay_authentication_plan_bytes"])
        self.assertEqual(3 * expected + 5 * pending[1]["plan_bytes"], result["required_max_prior_bytes"])
        self.assertEqual(2 * expected + 3 * pending[1]["plan_bytes"], result["fresh_success_prior_bytes_upper_bound"])

    def test_pending_journal_accounts_for_resume_and_failed_closure_restoration(self):
        _, manifest = self.receipt("pending")
        size = manifest["plan_bytes"]
        result = HISTORY.measure(self.root, max_prior_bytes=7 * size)
        self.assertEqual(size, result["scheduled_discovery_plan_bytes"])
        self.assertEqual(5 * size, result["fresh_success_prior_bytes_upper_bound"])
        self.assertEqual(7 * size, result["resumed_success_prior_bytes_upper_bound"])
        self.assertEqual(8 * size, result["required_max_prior_bytes"])
        self.assertFalse(result["declared_prior_budget_fits"])
        self.assertTrue(HISTORY.measure(self.root, max_prior_bytes=8 * size)["declared_prior_budget_fits"])

    def test_confirmed_journal_is_read_in_recipe_restore_and_optional_resume(self):
        _, manifest = self.receipt("complete", confirmed=True)
        result = HISTORY.measure(self.root, max_prior_bytes=3 * manifest["plan_bytes"])
        self.assertEqual(3 * manifest["plan_bytes"], result["required_max_prior_bytes"])
        self.assertEqual(2 * manifest["plan_bytes"], result["fresh_success_prior_bytes_upper_bound"])
        self.assertEqual(0, result["scheduled_replay_authentication_plan_bytes"])
        self.assertEqual(0, result["scheduled_closure_prior_plan_bytes"])
        self.assertTrue(result["declared_prior_budget_fits"])

    def test_current_receipt_bound_includes_each_pending_reconciliation_reference(self):
        self.receipt("pending-one")
        self.receipt("pending-two")
        result = HISTORY.measure(self.root, max_bytes=1000, max_current_readback_bytes=3000)
        self.assertEqual(4, result["current_plan_read_count"])
        self.assertEqual(4000, result["current_readback_bytes_at_maximum_plan"])
        self.assertEqual(750, result["maximum_new_plan_bytes_within_current_readback"])
        self.assertFalse(result["declared_current_readback_covers_maximum_plan"])

    def test_reference_without_matching_context_keeps_old_pending(self):
        old = self.receipt("old")
        target = self.receipt("target", confirmed=True)
        self.reference(old, target)
        result = HISTORY.measure(self.root)
        self.assertEqual(["old"], result["pending_receipts"])
        self.assertEqual(old[1]["plan_bytes"] + 2 * target[1]["plan_bytes"], result["scheduled_discovery_plan_bytes"])

    def test_size_and_aggregate_limits_report_without_claiming_admission(self):
        item = self.receipt("old", limit=1)
        result = HISTORY.measure(self.root, max_prior_bytes=1)
        self.assertEqual("complete", result["status"])
        self.assertEqual(item[1]["plan_bytes"], result["scheduled_discovery_plan_bytes"])
        self.assertFalse(result["declared_prior_budget_fits"])
        self.assertFalse(result["declared_plan_size_bounds_fit"])
        self.assertTrue(result["plans"][0]["blockers"])

    def test_changed_file_size_is_explicit_incomplete(self):
        path, _ = self.receipt("old")
        with (path / "plan.jsonl").open("ab") as output:
            output.write(b"changed")
        result = HISTORY.measure(self.root)
        self.assertEqual("incomplete", result["status"])
        self.assertFalse(result["schedule_complete"])
        self.assertTrue(result["errors"])

    def test_oversized_metadata_and_directory_bound_are_not_silent(self):
        path, _ = self.receipt("one")
        (path / "manifest.json").write_bytes(b" " * (HISTORY.METADATA_BYTES + 1))
        self.assertEqual("incomplete", HISTORY.measure(self.root)["status"])
        self.receipt("two")
        with patch.object(HISTORY, "MAX_DIRECTORIES", 1):
            result = HISTORY.measure(self.root)
        self.assertEqual("incomplete", result["status"])
        self.assertFalse(result["schedule_complete"])

    def test_unbounded_reconciliation_context_is_explicit_incomplete(self):
        old = self.receipt("old")
        target = self.receipt("target", confirmed=True, reconciles=[{
            "receipt": "x" * HISTORY.METADATA_BYTES, "disposition": "originals-confirmed"}])
        self.reference(old, target)
        result = HISTORY.measure(self.root)
        self.assertEqual("incomplete", result["status"])
        self.assertIn("context is unbounded", result["errors"][0]["error"])

    def test_payload_is_not_hashed_or_read_for_unreferenced_plans(self):
        path, _ = self.receipt("old", confirmed=True)
        original_open = os.open
        def no_payload_open(name, *args, **kwargs):
            self.assertNotEqual(path / "plan.jsonl", Path(name))
            return original_open(name, *args, **kwargs)
        with patch.object(HISTORY.os, "open", side_effect=no_payload_open):
            result = HISTORY.measure(self.root)
        self.assertEqual("complete", result["status"])

    def test_symlink_receipt_does_not_escape_estate(self):
        item = self.receipt("old")
        (self.root / "linked").symlink_to(item[0], target_is_directory=True)
        self.assertEqual("incomplete", HISTORY.measure(self.root)["status"])

    def test_excessive_json_nesting_retains_incomplete_diagnostic(self):
        path, _ = self.receipt("old")
        (path / "manifest.json").write_text('[' * 2000 + '0' + ']' * 2000)
        result = HISTORY.measure(self.root)
        self.assertEqual("incomplete", result["status"])
        self.assertTrue(result["errors"])


if __name__ == "__main__":
    unittest.main()
