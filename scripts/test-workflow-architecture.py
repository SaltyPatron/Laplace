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

    def test_ci_contract_has_a_lightweight_hosted_lane(self):
        text = (WORKFLOWS / "ci-contract.yml").read_text(encoding="utf-8")
        self.assertIn("pull_request:", text)
        self.assertIn("push:", text)
        self.assertIn("runs-on: ubuntu-24.04", text)
        self.assertNotIn("self-hosted", text)
        self.assertIn("bash scripts/product-ci.sh check", text)
        self.assertIn("cancel-in-progress: false", text)

    def test_targeted_code_player_does_not_duplicate_mainline_build(self):
        self.assertFalse((WORKFLOWS / "code-player-ci.yml").exists())
        mainline = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("uses: ./.github/workflows/product-stage.yml", mainline)
        self.assertIn("stage: mainline", mainline)

    def test_mainline_has_no_actions_level_cancellation_queue(self):
        text = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        mainline = text.split("  mainline:\n", 1)[1].split("\n  operator:\n", 1)[0]
        self.assertNotIn("concurrency:", mainline)
        self.assertNotIn("cancel-in-progress:", mainline)

    def test_benchmark_concurrency_uses_only_supported_keys(self):
        text = (WORKFLOWS / "benchmark-evidence.yml").read_text(encoding="utf-8")
        self.assertNotIn("queue:", text)
        self.assertIn("cancel-in-progress: false", text)

    def test_seed_preflight_is_not_coupled_to_cli_help_rendering(self):
        text = (WORKFLOWS / "seed.yml").read_text(encoding="utf-8")
        self.assertNotIn("scripts/laplace --help", text)
        self.assertIn('exec bash scripts/ensure-foundation.sh', text)

    def test_product_execution_has_one_reusable_self_hosted_owner(self):
        reusable = (WORKFLOWS / "product-stage.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_call:", reusable)
        self.assertEqual(1, reusable.count("runs-on: [self-hosted, laplace]"))
        self.assertEqual(1, reusable.count("host-resource.lock"))
        self.assertIn('exec bash scripts/product-ci.sh "$LAPLACE_STAGE"', reusable)

        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertNotIn("runs-on: [self-hosted, laplace]", lifecycle)
        self.assertNotIn("host-resource.lock", lifecycle)

    def test_deploy_and_proof_are_composed_from_retryable_stage_jobs(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("deploy-candidate:", lifecycle)
        self.assertIn("deploy-activation:", lifecycle)
        self.assertIn("needs: deploy-candidate", lifecycle)
        self.assertIn("proof-candidate:", lifecycle)
        self.assertIn("proof-model:", lifecycle)
        self.assertIn("proof-activation:", lifecycle)
        self.assertIn("needs: proof-candidate", lifecycle)
        self.assertIn("needs: proof-model", lifecycle)

        proof = (WORKFLOWS / "competitive-proof.yml").read_text(encoding="utf-8")
        for stage in ("release-candidate", "proof-model", "release-activation"):
            self.assertIn(f"stage: {stage}", proof)
        self.assertNotIn("scripts/product-ci.sh", proof)
        self.assertNotIn("runs-on: [self-hosted, laplace]", proof)


if __name__ == "__main__":
    unittest.main(verbosity=2)
