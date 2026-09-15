#!/usr/bin/env python3
"""Exact operational seed admission contracts without a local PostgreSQL server."""
from __future__ import annotations

import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import uuid

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("operational_seed", ROOT / "scripts/verify-operational-seed.py")
assert SPEC and SPEC.loader
SEED = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SEED)


class OperationalSeedTests(unittest.TestCase):
    def setUp(self):
        self.receipt = dict(run_id=str(uuid.uuid4()), source_name=SEED.SOURCE,
                            source_id="ab" * 16, layer=2)
        self.selected = {f"docs/contract-{i}.md": f"Contract {i}: 水 🧪\n".encode()
                         for i in range(SEED.EXPECTED_ARTIFACTS)}
        self.report = dict(expected_source_id=self.receipt["source_id"],
                           run=dict(self.receipt, status="ok", evidence_persisted=True,
                                    files_done=len(self.selected), files_total=len(self.selected), units_failed=0,
                                    ended_at="2026-09-15T12:00:00Z", error=None), files=[])
        for i, (path, content) in enumerate(self.selected.items()):
            fingerprint = f"{i + 1:032x}"
            self.report["files"].append(dict(
                relative_path=path, file_label="operational/" + path,
                source_name=SEED.SOURCE, disposition="admitted", status="ok",
                bytes=len(content), expected_bytes=len(content), ended_at="2026-09-15T12:00:00Z",
                error=None, file_id="cd" * 16, resume_fingerprint=fingerprint,
                expected_fingerprint=fingerprint, completion_present=True))

    def verify(self, report=None):
        SEED.validate_readback(self.receipt, self.selected, report or self.report)

    def test_complete_exact_set_and_marker_skips_pass(self):
        self.verify()
        for file in self.report["files"][::2]:
            file.update(status="skipped-complete", file_id=None)
        self.verify()

    def test_capped_and_dependency_noop_are_not_complete_seeds(self):
        for status in ("capped", "dependency-unset", "skipped-complete", "failed", "running"):
            with self.subTest(status=status):
                report = copy.deepcopy(self.report)
                report["run"]["status"] = status
                with self.assertRaisesRegex(RuntimeError, "complete durable success"):
                    self.verify(report)

    def test_absent_or_unrelated_run_cannot_replace_invocation(self):
        for run in (None, dict(self.report["run"], run_id=str(uuid.uuid4()))):
            with self.subTest(run=run):
                with self.assertRaisesRegex(RuntimeError, "matching durable run"):
                    self.verify(dict(self.report, run=run))

    def test_wrong_source_identity_is_rejected(self):
        self.report["run"]["source_id"] = "cc" * 16
        with self.assertRaisesRegex(RuntimeError, "source identity"):
            self.verify()

    def test_missing_file_fails_even_when_run_totals_claim_complete_selection(self):
        self.report["files"].pop()
        with self.assertRaisesRegex(RuntimeError, "exact selected artifact set"):
            self.verify()

    def test_duplicate_or_substituted_path_is_rejected(self):
        for path in (self.report["files"][0]["relative_path"], "docs/unselected.md"):
            with self.subTest(path=path):
                report = copy.deepcopy(self.report)
                report["files"][-1]["relative_path"] = path
                with self.assertRaisesRegex(RuntimeError, "paths differ"):
                    self.verify(report)

    def test_partial_or_unselected_file_is_rejected(self):
        for change in (dict(status="cancelled"), dict(status="composed"),
                       dict(disposition="unsupported-with-why-not"), dict(ended_at=None)):
            with self.subTest(change=change):
                report = copy.deepcopy(self.report)
                report["files"][0].update(change)
                with self.assertRaisesRegex(RuntimeError, "did not complete admission"):
                    self.verify(report)

    def test_same_size_wrong_byte_fingerprint_is_rejected(self):
        self.report["files"][0]["resume_fingerprint"] = "ef" * 16
        with self.assertRaisesRegex(RuntimeError, "byte fingerprint differs"):
            self.verify()

    def test_missing_native_completion_marker_is_rejected(self):
        self.report["files"][0]["completion_present"] = False
        with self.assertRaisesRegex(RuntimeError, "no exact source completion marker"):
            self.verify()

    def test_nonpersistent_evidence_or_incomplete_counts_are_rejected(self):
        for change in (dict(evidence_persisted=False), dict(files_done=10), dict(files_total=10),
                       dict(units_failed=1)):
            with self.subTest(change=change):
                report = copy.deepcopy(self.report)
                report["run"].update(change)
                with self.assertRaises(RuntimeError):
                    self.verify(report)

    def test_sql_pins_uuid_and_native_identity_recipe_without_latest_fallback(self):
        sql = SEED.readback_sql(self.receipt, self.selected)
        self.assertEqual(2, sql.count(f"run_id = '{self.receipt['run_id']}'::uuid"))
        self.assertNotIn("ORDER BY started_at", sql)
        self.assertIn("substrate/file-resume-fingerprint/v1", sql)
        self.assertIn("public.laplace_hash128_merkle(0::smallint", sql)
        self.assertIn("a.subject_id = e.fingerprint AND a.object_id = e.fingerprint", sql)
        self.assertIn("a.context_id = laplace.source_id('OperationalDecomposer')", sql)
        self.assertIn(next(iter(self.selected.values())).hex(), sql)

    def test_native_fingerprint_uses_length_and_complete_fixed_size_blocks(self):
        payload = b"a" * (4 << 20) + b"z"
        sql = SEED.fingerprint_sql(payload)
        self.assertEqual(3, sql.count("public.laplace_hash128_blake3("))
        self.assertIn(len(payload).to_bytes(8, "little", signed=True).hex(), sql)
        self.assertIn("decode('7a','hex')", sql)

    def test_literal_project_selection_and_exact_deployed_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = root / "app/Laplace.Decomposers/Laplace.Decomposers.csproj"
            project.parent.mkdir(parents=True)
            bundle = root / "bundle"
            (root / "docs").mkdir()
            (bundle / "docs").mkdir(parents=True)
            includes = []
            for path, payload in self.selected.items():
                (root / path).write_bytes(payload)
                (bundle / path).write_bytes(payload)
                includes.append("../../" + path)
            project.write_text('<Project><ItemGroup><Content Include="' + ";".join(includes)
                               + '" Link="seeds/operational/docs/%(Filename)%(Extension)" />'
                               + '</ItemGroup></Project>', encoding="utf-8")
            self.assertEqual({path: root / path for path in self.selected}, SEED.authored_selection(root))
            with patch.object(SEED, "authored_selection", wraps=SEED.authored_selection) as selection:
                self.assertEqual(self.selected, SEED.authored_bundle(root, bundle))
                selection.assert_called_once_with(root)
            path = next(iter(self.selected))
            (bundle / path).write_bytes(b"X" * len(self.selected[path]))
            with self.assertRaisesRegex(RuntimeError, "bytes differ"):
                SEED.authored_bundle(root, bundle)

    def test_source_path_cli_uses_selection_without_bundle_or_ingestion(self):
        selected = {"different/bundle/name.md": ROOT / "docs/INVENTION.md"}
        with patch.object(SEED.sys, "argv", ["verify-operational-seed", "--list-source-paths"]), \
                patch.object(SEED, "authored_selection", return_value=selected) as selection, \
                patch.object(SEED, "authored_bundle") as bundle, \
                patch.object(SEED, "seed_and_verify") as ingest, \
                patch.object(SEED.sys, "stdout", new_callable=io.StringIO) as output:
            self.assertEqual(0, SEED.main())
        self.assertEqual("docs/INVENTION.md\n", output.getvalue())
        selection.assert_called_once_with(ROOT)
        bundle.assert_not_called()
        ingest.assert_not_called()

    def test_source_path_cli_rejects_inventory_failure_without_partial_success(self):
        with patch.object(SEED.sys, "argv", ["verify-operational-seed", "--list-source-paths"]), \
                patch.object(SEED, "authored_selection", side_effect=RuntimeError("incomplete selection")), \
                patch.object(SEED.sys, "stdout", new_callable=io.StringIO) as output, \
                patch.object(SEED.sys, "stderr", new_callable=io.StringIO) as error:
            self.assertEqual(1, SEED.main())
        self.assertEqual("", output.getvalue())
        self.assertIn("OPERATIONAL_SELECTION_FAIL", error.getvalue())

    def test_repository_bundle_includes_exact_authored_annotation_and_task(self):
        project = ROOT / "app/Laplace.Decomposers/Laplace.Decomposers.csproj"
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory)
            for item in SEED.ET.parse(project).getroot().iter("Content"):
                link = item.get("Link", "")
                if not link.startswith("seeds/operational/"):
                    continue
                for include in item.get("Include", "").split(";"):
                    source = (project.parent / include).resolve()
                    relative = link.removeprefix("seeds/operational/").replace(
                        "%(Filename)%(Extension)", source.name)
                    target = bundle / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(source.read_bytes())
            selected = SEED.authored_bundle(ROOT, bundle)
            self.assertEqual(13, len(selected))
            for relative in ("seeds/operational/exemplars/en_define.conllu",
                             "seeds/operational/tasks/en_define.json"):
                self.assertEqual((ROOT / relative).read_bytes(), selected[relative])
            task = "seeds/operational/tasks/en_define.json"
            (bundle / task).write_bytes(selected[task].replace(b"token-slots/v1", b"token-slots/v2"))
            with self.assertRaisesRegex(RuntimeError, "bytes differ"):
                SEED.authored_bundle(ROOT, bundle)

    def test_invocation_owns_new_receipt_and_overrides_inherited_cap_and_force(self):
        observed = []

        def invoke(command, **kwargs):
            environment = kwargs["env"]
            self.assertEqual("0", environment["LAPLACE_INGEST_MAX_UNITS"])
            self.assertEqual("0", environment["LAPLACE_INGEST_FORCE"])
            path = Path(environment["LAPLACE_INGEST_RUN_RECEIPT_PATH"])
            self.assertFalse(path.exists())
            observed.append(path)
            path.write_text(json.dumps(self.receipt), encoding="utf-8")
            return subprocess.CompletedProcess(command, 0)

        with tempfile.TemporaryDirectory() as directory, \
                patch.dict(os.environ, TMPDIR=directory, LAPLACE_INGEST_MAX_UNITS="1",
                           LAPLACE_INGEST_FORCE="1", LAPLACE_INGEST_RUN_RECEIPT_PATH="stale.json"), \
                patch.object(SEED.subprocess, "run", side_effect=invoke), \
                patch.object(SEED, "authored_bundle", return_value=self.selected), \
                patch.object(SEED, "database_readback", return_value=self.report) as readback:
            self.assertEqual(self.report, SEED.seed_and_verify(ROOT))
            readback.assert_called_once_with(self.receipt, self.selected)
        self.assertEqual(1, len(observed))
        self.assertFalse(observed[0].exists())

    def test_invocation_without_receipt_cannot_reuse_older_success(self):
        with tempfile.TemporaryDirectory() as directory, \
                patch.dict(os.environ, TMPDIR=directory), \
                patch.object(SEED.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)), \
                patch.object(SEED, "database_readback", return_value=self.report) as readback:
            with self.assertRaises(FileNotFoundError):
                SEED.seed_and_verify(ROOT)
            readback.assert_not_called()

    def test_report_cli_retains_exact_success_and_replaces_stale_success_on_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "seed.json"
            with patch.object(SEED.sys, "argv", ["verify-operational-seed", "--ingest", "--report", str(path)]), \
                    patch.object(SEED, "seed_and_verify", return_value=self.report), \
                    patch("builtins.print"):
                self.assertEqual(0, SEED.main())
            saved = json.loads(path.read_text())
            self.assertEqual({**self.report, "disposition": "verified"}, saved)
            with patch.object(SEED.sys, "argv", ["verify-operational-seed", "--ingest", "--report", str(path)]), \
                    patch.object(SEED, "seed_and_verify", side_effect=RuntimeError("new run failed")), \
                    patch("builtins.print"):
                self.assertEqual(1, SEED.main())
            failed = json.loads(path.read_text())
            self.assertEqual("failed", failed["disposition"])
            self.assertNotIn("run", failed)
            self.assertNotIn(self.receipt["run_id"], path.read_text())

    def test_database_failure_does_not_expose_source_payloads(self):
        result = subprocess.CompletedProcess([], 1, stdout="", stderr="private source payload")
        with patch.object(SEED.subprocess, "run", return_value=result) as run:
            with self.assertRaisesRegex(RuntimeError, "psql exit 1") as error:
                SEED.database_readback(self.receipt, self.selected)
            self.assertNotIn("private source", str(error.exception))
            self.assertNotIn(next(iter(self.selected.values())).hex(), str(run.call_args.args[0]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
