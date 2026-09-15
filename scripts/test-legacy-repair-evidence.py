#!/usr/bin/env python3
"""Exercise bounded source-linked metadata export using private filesystem fixtures."""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("legacy_repair_evidence", ROOT / "scripts/collect-legacy-repair-evidence.py")
EVIDENCE = importlib.util.module_from_spec(spec)
spec.loader.exec_module(EVIDENCE)

SOURCE = "a" * 40
OTHER_SOURCE = "b" * 40
RESOURCE = "maintenance-resources-" + "1" * 32
ATTEMPT = "repair-attempt-" + "2" * 32 + ".json"
HOLD = "database-quiescence-held-" + "3" * 32 + ".json"
TRANSACTION = {"device": 7, "inode": 11, "size": 128, "modified_ns": 13, "changed_ns": 17}


class LegacyRepairEvidenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR"))
        self.addCleanup(temporary.cleanup)
        self.base = Path(temporary.name)
        self.repairs = self.base / "legacy-content-repair"
        self.quiescence = self.base / "legacy-content-service-quiescence"
        self.repairs.mkdir()
        self.quiescence.mkdir()
        self.output_number = 0

    def write(self, path: Path, value: dict) -> bytes:
        raw = (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        path.write_bytes(raw)
        return raw

    def repair(self, name="current", source=SOURCE) -> Path:
        directory = self.repairs / name
        directory.mkdir()
        self.write(directory / "failure.json", {
            "source_sha": source, "error_type": "ValueError", "fixture": "λ棋"})
        return directory

    def producer(self, directory: Path, source=SOURCE):
        self.write(directory / "prior-services.json", {
            "schema": "laplace.database-service-quiescence/v1", "database": "laplace",
            "services": {}, "producer_generation": {
                "schema": "laplace.repair-producer-generation/v1", "source_sha": source,
                "publication": "completed-before-quiescence", "assemblies": {},
                "application_verification_sha256": "9" * 64}, "fixture": "λ棋"})

    def ledger(self, directory: Path, current: Path):
        self.write(directory / (RESOURCE + ".json"), {
            "schema": "laplace.legacy-content-maintenance-resources/v1",
            "max_bytes": 100, "max_line_bytes": 100, "max_prior_bytes": 200,
            "max_current_readback_bytes": 300, "timeout_seconds": 180,
            "deadline_monotonic": 123.5, "boot_id": "fixture-boot",
            "repair_root": str(self.repairs)})
        self.write(directory / (RESOURCE + "-usage.json"), {
            "schema": "laplace.legacy-content-maintenance-usage/v1",
            "resource_receipt": RESOURCE + ".json", "current_receipt": str(current),
            "prior_bytes_read": 80, "current_bytes_read": 30, "reservations": 2})
        self.write(directory / (RESOURCE + "-current-readback-admission.json"), {
            "schema": "laplace.legacy-content-current-readback-admission/v1",
            "resource_receipt": RESOURCE + ".json", "current_receipt": str(current),
            "measured_plan_bytes": 100, "read_multiplicity": 3,
            "required_readback_bytes": 300, "already_consumed_bytes": 30,
            "max_current_readback_bytes": 300,
            "rejections": ["complete current receipt readback exceeds aggregate byte bound"]})

    def attempt(self, directory: Path, current: Path, source=SOURCE):
        self.write(directory / ATTEMPT, {
            "schema": "laplace.quiescence-repair-attempt/v1", "transaction_identity": TRANSACTION,
            "repair_directory": str(current), "repair_source_sha": source,
            "resource_receipt": str(directory / (RESOURCE + ".json")),
            "repair_process": {"pid": 123, "start_ticks": 456, "boot_id": "fixture-boot"},
            "at_unix_nanoseconds": 1000})

    def collect(self, **kwargs):
        self.output_number += 1
        output = self.base / ("output-" + str(self.output_number))
        result = EVIDENCE.collect(self.repairs, output, SOURCE, **kwargs)
        self.assertEqual(result, json.loads((output / "collection.json").read_bytes()))
        return result, output

    def assert_files(self, original: Path, exported: Path, receipt: dict, names: set[str]):
        files = {record["name"]: record for record in receipt["files"]}
        self.assertEqual(names, set(files))
        for name in names:
            raw = (original / name).read_bytes()
            self.assertEqual(raw, (exported / name).read_bytes())
            self.assertEqual(len(raw), files[name]["bytes"])
            self.assertEqual(hashlib.sha256(raw).hexdigest(), files[name]["sha256"])

    def test_repair_metadata_baseline_preserves_exact_bytes_and_excludes_plan(self):
        current = self.repair()
        self.write(current / "resources.json", {"fixture": "λ棋", "measured_plan_bytes": 100})
        (current / "plan.jsonl").write_bytes(b"not exported\n" * 20000)
        (current / "arbitrary.json").write_bytes(b"not metadata")
        self.repair("other", OTHER_SOURCE)
        result, output = self.collect()
        self.assertEqual("complete", result["status"])
        self.assertEqual(["current"], [r["directory"] for r in result["receipts"]])
        self.assert_files(current, output / "current", result["receipts"][0], {"failure.json", "resources.json"})
        self.assertEqual([], result["quiescence"]["receipts"])
        self.assertEqual([], list(output.rglob("plan.jsonl")))
        self.assertEqual([], list(output.rglob("arbitrary.json")))

    def test_producer_selects_current_source_before_any_repair_attempt_exists(self):
        selected = self.quiescence / "producer-only"
        selected.mkdir()
        self.producer(selected)
        self.write(selected / "begin-confirmed.json", {"transaction_identity": TRANSACTION})
        (selected / "plan.jsonl").write_bytes(b"never export plan")
        (selected / "incidental.json").write_bytes(b"not metadata")
        unrelated = self.quiescence / "other-producer"
        unrelated.mkdir()
        self.producer(unrelated, OTHER_SOURCE)
        result, output = self.collect()
        self.assertEqual("complete", result["status"])
        self.assertEqual([], result["receipts"])
        receipts = result["quiescence"]["receipts"]
        self.assertEqual(["producer-only"], [r["directory"] for r in receipts])
        self.assertIn("producer_generation", receipts[0]["matched_by"])
        self.assert_files(selected, output / "quiescence/producer-only", receipts[0],
                          {"prior-services.json", "begin-confirmed.json"})
        raw = (selected / "prior-services.json").read_bytes()
        self.assertGreater(len(raw), len(raw.decode("utf-8")))
        self.assertEqual([], list(output.rglob("plan.jsonl")))
        self.assertEqual([], list(output.rglob("incidental.json")))

    def test_exact_attempt_selects_current_repair_with_older_retained_producer(self):
        current = self.repair()
        selected = self.quiescence / "held-current"
        selected.mkdir()
        self.producer(selected, OTHER_SOURCE)
        self.ledger(selected, current)
        self.attempt(selected, current)
        self.write(selected / "quiescence-confirmed.json", {"transaction_identity": TRANSACTION,
                                                             "managed_main_pids": [0, 0, 0]})
        self.write(selected / HOLD, {"transaction_identity": TRANSACTION, "reason_type": "ValueError",
            "disposition": "managed-writers-held-pending-exact-database-reconciliation"})
        result, output = self.collect()
        self.assertEqual("complete", result["status"])
        receipt, = result["quiescence"]["receipts"]
        self.assertIn("repair_attempt", receipt["matched_by"])
        self.assert_files(selected, output / "quiescence/held-current", receipt, {
            "prior-services.json", "quiescence-confirmed.json", ATTEMPT, HOLD,
            RESOURCE + ".json", RESOURCE + "-usage.json", RESOURCE + "-current-readback-admission.json"})
        retained = json.loads((output / "quiescence/held-current/prior-services.json").read_bytes())
        self.assertEqual(OTHER_SOURCE, retained["producer_generation"]["source_sha"])

    def test_usage_current_receipt_selects_ledger_without_source_fields(self):
        current = self.repair()
        selected = self.quiescence / "usage-only"
        selected.mkdir()
        self.ledger(selected, current)
        other = self.repair("other", OTHER_SOURCE)
        unrelated = self.quiescence / "other-usage"
        unrelated.mkdir()
        self.ledger(unrelated, other)
        result, output = self.collect()
        self.assertEqual("complete", result["status"])
        receipt, = result["quiescence"]["receipts"]
        self.assertEqual("usage-only", receipt["directory"])
        self.assertIn("current_receipt", receipt["matched_by"])
        self.assert_files(selected, output / "quiescence/usage-only", receipt, {
            RESOURCE + ".json", RESOURCE + "-usage.json", RESOURCE + "-current-readback-admission.json"})

    def test_explicit_quiescence_root_is_used(self):
        alternate = self.base / "explicit-quiescence"
        alternate.mkdir()
        selected = alternate / "selected"
        selected.mkdir()
        self.producer(selected)
        result, output = self.collect(quiescence_root=alternate)
        self.assertEqual("complete", result["status"])
        self.assertEqual(str(alternate), result["quiescence"]["receipt_root"])
        self.assertTrue((output / "quiescence/selected/prior-services.json").is_file())

    def test_symlink_estate_roots_are_rejected(self):
        for root in (self.repairs, self.quiescence):
            with self.subTest(root=root.name):
                real = root.with_name(root.name + "-real")
                root.rename(real)
                root.symlink_to(real, target_is_directory=True)
                try:
                    result, _ = self.collect()
                    self.assertEqual("incomplete", result["status"])
                finally:
                    root.unlink()
                    real.rename(root)

    def test_symlink_receipt_directories_are_rejected_without_export(self):
        for root in (self.repairs, self.quiescence):
            with self.subTest(root=root.name):
                outside = self.base / (root.name + "-outside")
                outside.mkdir()
                self.write(outside / "failure.json", {"source_sha": SOURCE})
                self.producer(outside)
                link = root / "linked-receipt"
                link.symlink_to(outside, target_is_directory=True)
                try:
                    result, output = self.collect()
                    self.assertEqual("incomplete", result["status"])
                    self.assertEqual([], list(output.rglob("prior-services.json")))
                    self.assertEqual([], list(output.rglob("failure.json")))
                finally:
                    link.unlink()

    def test_symlink_metadata_is_rejected_without_following_target(self):
        current = self.repair()
        selected = self.quiescence / "selected"
        selected.mkdir()
        self.producer(selected)
        outside = self.base / "outside.json"
        self.write(outside, {"source_sha": SOURCE, "fixture": "must not follow"})
        for path in (current / "failure.json", selected / "prior-services.json"):
            with self.subTest(path=path.name):
                original = path.read_bytes()
                path.unlink()
                path.symlink_to(outside)
                try:
                    result, output = self.collect()
                    self.assertEqual("incomplete", result["status"])
                    self.assertFalse(any(p.read_bytes() == outside.read_bytes()
                                         for p in output.rglob("*.json")))
                finally:
                    path.unlink()
                    path.write_bytes(original)

    def test_oversized_selected_metadata_is_incomplete(self):
        current = self.repair()
        selected = self.quiescence / "selected"
        selected.mkdir()
        self.producer(selected)
        self.ledger(selected, current)
        for path in (current / "resources.json", selected / (RESOURCE + "-current-readback-admission.json")):
            with self.subTest(path=path.name):
                original = path.read_bytes() if path.exists() else None
                self.write(path, {"padding": "x" * EVIDENCE.METADATA_BYTES})
                try:
                    result, _ = self.collect()
                    self.assertEqual("incomplete", result["status"])
                finally:
                    if original is None:
                        path.unlink()
                    else:
                        path.write_bytes(original)

    def test_directory_scan_bound_applies_to_both_estates(self):
        for root in (self.repairs, self.quiescence):
            with self.subTest(root=root.name), patch.object(EVIDENCE, "MAX_DIRECTORIES", 1):
                directories = [root / str(i) for i in range(2)]
                for directory in directories:
                    directory.mkdir()
                try:
                    result, _ = self.collect()
                    self.assertEqual("incomplete", result["status"])
                finally:
                    for directory in directories:
                        directory.rmdir()

    def test_quiescence_entry_scan_is_bounded_even_for_unexported_files(self):
        selected = self.quiescence / "selected"
        selected.mkdir()
        self.producer(selected)
        (selected / "arbitrary.json").write_bytes(b"not metadata")
        with patch.object(EVIDENCE, "MAX_DIRECTORY_ENTRIES", 1):
            result, output = self.collect()
        self.assertEqual("incomplete", result["status"])
        self.assertEqual([], result["quiescence"]["receipts"])
        self.assertEqual([], list(output.rglob("arbitrary.json")))

    def test_aggregate_read_budget_rejects_before_unadmitted_file_read(self):
        self.repair()
        selected = self.quiescence / "selected"
        selected.mkdir()
        self.producer(selected)
        limit = EVIDENCE.METADATA_BYTES - 1
        with patch.object(EVIDENCE, "MAX_COLLECTION_BYTES", limit):
            result, _ = self.collect()
        self.assertEqual("incomplete", result["status"])
        self.assertEqual(limit, result["maximum_collection_bytes"])
        self.assertEqual(0, result["metadata_bytes_read"])
        self.assertEqual([], result["receipts"])
        self.assertEqual([], result["quiescence"]["receipts"])

    def test_conflicting_repair_source_metadata_is_incomplete(self):
        current = self.repair()
        self.write(current / "manifest.json", {"source_sha": OTHER_SOURCE})
        result, output = self.collect()
        self.assertEqual("incomplete", result["status"])
        self.assertEqual([], result["receipts"])
        self.assertFalse((output / "current").exists())

    def test_invalid_source_does_not_create_output(self):
        for source in ("a" * 39, "A" * 40, "a" * 41, "../receipt"):
            with self.subTest(source=source), self.assertRaises(ValueError):
                EVIDENCE.collect(self.repairs, self.base / "invalid-output", source)
        self.assertFalse((self.base / "invalid-output").exists())


if __name__ == "__main__":
    unittest.main()
