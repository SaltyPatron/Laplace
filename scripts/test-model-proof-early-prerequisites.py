#!/usr/bin/env python3
"""Run the real model-proof shell and bounded probe before any DB/corpus work."""
from __future__ import annotations
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location(
    "prerequisite_fixtures", SCRIPTS / "test-model-proof-prerequisites.py")
fixtures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixtures)


class EarlyModelPrerequisites(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="laplace-early-model-")
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.repo = self.root / "work" / "checkout"
        (self.repo / "scripts").mkdir(parents=True)
        for name in ("model-synthesize-ci.sh", "check-model-proof-prerequisites.py"):
            shutil.copyfile(SCRIPTS / name, self.repo / "scripts" / name)
        self.first, self.second = self.root / "first", self.root / "second"
        fixtures.model(self.first, 1.0)
        fixtures.model(self.second, 2.0)
        self.data = self.root / "Data" / "Ingest"
        self.tiny = self.data / "tiny-codes" / "part.parquet"
        self.stack = self.data / "stack-v2" / "data" / "Python" / "part.parquet"
        fixtures.parquet(self.tiny, [
            fixtures.element("schema", 3), fixtures.element("prompt"),
            fixtures.element("response"), fixtures.element("programming_language")])
        self.stack_schema(content=True)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.events = self.root / "events"
        self.llama = self.bin / "llama-completion"
        self.llama.write_text(
            '#!/bin/sh\n[ "$1" = --help ] || exit 98\n'
            'printf "%s\\n" LLAMA_HELP >> "$EVENTS"\n'
            'exit "$LLAMA_FIXTURE_RC"\n')
        self.llama.chmod(0o700)
        psql = self.bin / "psql"
        psql.write_text('#!/bin/sh\nprintf "%s\\n" DB_CHECK >> "$EVENTS"\nexit 97\n')
        psql.chmod(0o700)
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith(("LAPLACE_MODEL_", "LAPLACE_QWEN25_", "LAPLACE_TINYLLAMA_"))}
        self.env.update(
            LAPLACE_MODEL_PROOF_DIR=str(self.first),
            LAPLACE_MODEL_CORROBORATION_DIR=str(self.second),
            LAPLACE_LLAMA_BIN=str(self.llama),
            LAPLACE_MODEL_HUB=str(self.root / "unused-hub"),
            PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
            EVENTS=str(self.events), LLAMA_FIXTURE_RC="0",
            TMPDIR=str(self.root))

    def stack_schema(self, content):
        fixtures.parquet(self.stack, [
            fixtures.element("schema", 2),
            fixtures.element("content" if content else "blob_id"),
            fixtures.element("language")])

    def run_proof(self, **changes):
        self.events.unlink(missing_ok=True)
        result = subprocess.run(
            ["bash", str(self.repo / "scripts" / "model-synthesize-ci.sh")],
            env=dict(self.env, **changes), text=True, capture_output=True, timeout=30)
        events = self.events.read_text().splitlines() if self.events.exists() else []
        lines = [line[len("MODEL_PROOF_PREFLIGHT "):] for line in result.stdout.splitlines()
                 if line.startswith("MODEL_PROOF_PREFLIGHT ")]
        report = json.loads(lines[0]) if len(lines) == 1 else None
        return result, events, report

    def test_metadata_only_stack_fails_before_first_database_command(self):
        self.stack_schema(content=False)
        result, events, report = self.run_proof()
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("DB_CHECK", events)
        self.assertFalse(report["preliminary_prerequisites_passed"])
        self.assertTrue(any("stack-v2" in reason for reason in report["preliminary_failures"]))
        self.assertIn("failed before admission", result.stderr)

    def test_missing_tinycodes_schema_also_fails_before_database(self):
        fixtures.parquet(self.tiny, [
            fixtures.element("schema", 2), fixtures.element("prompt"), fixtures.element("response")])
        result, events, report = self.run_proof()
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("DB_CHECK", events)
        self.assertTrue(any("tiny-codes" in reason for reason in report["preliminary_failures"]))

    def test_success_is_preliminary_and_uses_exact_selected_models_and_runtime(self):
        result, events, report = self.run_proof()
        self.assertEqual(events, ["LLAMA_HELP", "DB_CHECK"])
        self.assertIn("laplace DB unreachable", result.stderr)
        self.assertTrue(report["preliminary_prerequisites_passed"])
        self.assertFalse(report["full_model_proof_performed"])
        self.assertEqual(report["exact_selected_models"], [str(self.first), str(self.second)])
        self.assertEqual([row["path"] for row in report["models"]], [str(self.first), str(self.second)])
        self.assertEqual(report["selected_llama"], str(self.llama))
        self.assertTrue(all(not item["full_corpus_scanned"] for item in report["corpora"]))

    def test_bad_explicit_second_cannot_be_replaced_by_another_available_model(self):
        (self.second / "model.safetensors").write_bytes(b"broken")
        family = self.root / "unused-hub" / "models--Qwen--Qwen2.5-Coder-3B-Instruct" / "snapshots"
        family.mkdir(parents=True)
        fixtures.model(family / "other-complete-checkpoint")
        result, events, report = self.run_proof()
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("DB_CHECK", events)
        self.assertEqual(report["exact_selected_models"], [str(self.first), str(self.second)])
        self.assertEqual(len(report["models"]), 2)
        self.assertTrue(any("second selected checkpoint" in reason
                            for reason in report["preliminary_failures"]))

    def test_runtime_startup_failure_blocks_all_database_and_admission_work(self):
        result, events, report = self.run_proof(LLAMA_FIXTURE_RC="7")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(events, ["LLAMA_HELP"])
        self.assertFalse(report["preliminary_prerequisites_passed"])
        self.assertTrue(any("llama-completion" in reason for reason in report["preliminary_failures"]))

    def test_existing_disabled_corpus_switch_does_not_require_corpora(self):
        self.stack_schema(content=False)
        for disabled in ("0", "other"):
            with self.subTest(value=disabled):
                result, events, report = self.run_proof(LAPLACE_MODEL_PROOF_CODE_CORPORA=disabled)
                self.assertEqual(events, ["LLAMA_HELP", "DB_CHECK"])
                self.assertTrue(report["preliminary_prerequisites_passed"])
                self.assertEqual(report["corpora"], [])
                self.assertIn("laplace DB unreachable", result.stderr)

    def test_malformed_success_report_cannot_supply_a_runtime(self):
        probe = self.repo / "scripts" / "check-model-proof-prerequisites.py"
        probe.write_text('print(\'MODEL_PROOF_PREFLIGHT {"preliminary_prerequisites_passed":true}\')\n')
        result, events, report = self.run_proof()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(events, [])
        self.assertIn("invalid preliminary model-proof prerequisite report", result.stderr)

    def test_strict_probe_requires_both_selected_paths(self):
        result = subprocess.run(
            [sys.executable, "-I", str(SCRIPTS / "check-model-proof-prerequisites.py"),
             "--repo", str(self.repo), "--require-proof-ready"],
            text=True, capture_output=True, timeout=10)
        self.assertEqual(result.returncode, 2)
        self.assertIn("both exact selected model directories", result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
