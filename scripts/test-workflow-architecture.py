#!/usr/bin/env python3
"""Repository-level CI/CD architecture invariants."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"


class WorkflowArchitecture(unittest.TestCase):
    def test_no_ephemeral_repair_workflows_remain(self):
        names = {path.name for path in WORKFLOWS.glob("*.yml")}
        repairs = sorted(name for name in names if "repair" in name)
        self.assertEqual([], repairs)

    def test_redundant_chess_micro_workflows_are_retired(self):
        for name in (
            "seed-chess-books.yml",
            "seed-chess-eval.yml",
            "seed-chess-games.yml",
            "seed-chess-openings.yml",
        ):
            self.assertFalse((WORKFLOWS / name).exists())

    def test_targeted_code_player_does_not_duplicate_mainline_build(self):
        self.assertFalse((WORKFLOWS / "code-player-ci.yml").exists())
        mainline = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("scripts/product-ci.sh mainline", mainline)

    def test_running_mainline_validation_is_never_cancelled_by_a_new_push(self):
        text = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        mainline = text.split("  mainline:\n", 1)[1].split("\n  operator:\n", 1)[0]
        self.assertIn("group: laplace-mainline-validation", mainline)
        self.assertIn("cancel-in-progress: false", mainline)
        self.assertNotIn("cancel-in-progress: true", mainline)

    def test_benchmark_concurrency_uses_only_supported_keys(self):
        text = (WORKFLOWS / "benchmark-evidence.yml").read_text(encoding="utf-8")
        self.assertNotIn("queue:", text)
        self.assertIn("cancel-in-progress: false", text)

    def test_seed_preflight_is_not_coupled_to_cli_help_rendering(self):
        text = (WORKFLOWS / "seed.yml").read_text(encoding="utf-8")
        self.assertNotIn("scripts/laplace --help", text)
        self.assertIn('exec bash scripts/ensure-foundation.sh', text)

    def test_competitive_proof_uses_the_canonical_product_stage(self):
        text = (WORKFLOWS / "competitive-proof.yml").read_text(encoding="utf-8")
        self.assertIn("scripts/product-ci.sh proof", text)
        for duplicated in (
            "scripts/product-ci.sh build",
            "scripts/product-ci.sh test-dev",
            "scripts/product-ci.sh install",
            "scripts/product-ci.sh test-db",
            "scripts/product-ci.sh applications",
            "scripts/product-ci.sh reconcile",
            "scripts/product-ci.sh test-live",
        ):
            self.assertNotIn(duplicated, text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
