#!/usr/bin/env python3
"""Repository-level CI/CD architecture invariants."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"




class MainPushQueueContract(unittest.TestCase):
    def test_main_push_coalesces_superseded_pending_runs_without_cancelling_active_delivery(self):
        text = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("concurrency:", text)
        self.assertIn("laplace-main-product-lifecycle", text)
        self.assertIn("cancel-in-progress: false", text)
        self.assertIn("github.run_id", text)

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
        self.assertIn("stage: release-qualification", mainline)
        self.assertIn("stage: release-candidate", mainline)
        self.assertIn("stage: release-activation", mainline)

    def test_mainline_preserves_every_run_and_skips_only_superseded_work(self):
        text = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        mainline = text.split("  mainline-qualification:\n", 1)[1].split("\n  operator:\n", 1)[0]
        self.assertNotIn("concurrency:", mainline)

        reusable = (WORKFLOWS / "product-stage.yml").read_text(encoding="utf-8")
        self.assertNotIn("concurrency:", reusable)
        self.assertNotIn("cancel-in-progress:", reusable)
        lock = reusable.index("flock 9")
        workspace = reusable.index('cd "$GITHUB_WORKSPACE"')
        preserve = reusable.index("nonempty workspace has no repository")
        resolve = reusable.index("git ls-remote --heads origin refs/heads/main")
        skip = reusable.index('if [[ "$latest_main" != "$TARGET_SHA" ]]')
        fetch = reusable.index('git fetch --no-tags --depth=2 origin "$TARGET_SHA"')
        self.assertEqual(
            [lock, workspace, preserve, resolve, skip, fetch],
            sorted([lock, workspace, preserve, resolve, skip, fetch]),
        )
        self.assertIn("no product stage executed", reusable)
        self.assertIn("exit 0", reusable)

    def test_main_push_qualifies_installs_publishes_and_live_verifies(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("mainline-qualification:", lifecycle)
        self.assertIn("stage: release-qualification", lifecycle)
        self.assertIn("mainline-candidate:", lifecycle)
        self.assertIn("needs: mainline-qualification", lifecycle)
        self.assertIn("stage: release-candidate", lifecycle)
        self.assertIn("mainline-activation:", lifecycle)
        self.assertIn("needs: mainline-candidate", lifecycle)
        self.assertIn("stage: release-activation", lifecycle)
        self.assertEqual(3, lifecycle.count("skip_if_superseded: true"))

        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        qualification = product.split("run_release_qualification() {", 1)[1].split("\n}", 1)[0]
        candidate = product.split("run_release_candidate() {", 1)[1].split("\n}", 1)[0]
        activation = product.split("run_release_activation() {", 1)[1].split("\n}", 1)[0]
        self.assertIn("run_build", qualification)
        self.assertIn("run_dev_test_matrix 1", qualification)
        self.assertIn("release_selected_revision_current", qualification)
        self.assertNotIn("run_install", qualification)
        self.assertNotIn("run_database_maintenance", qualification)
        self.assertNotIn("run_db_tests", qualification)
        for token in ("require_built_revision", "release_candidate_current_before_mutation",
                      "run_install", "run_database_maintenance --prepare", "run_db_tests"):
            self.assertIn(token, candidate)
        self.assertNotIn("run_build", candidate)
        self.assertNotIn("run_dev_tests", candidate)
        self.assertLess(candidate.index("release_candidate_current_before_mutation"),
                        candidate.index("run_install"))
        supersession = product.split(
            "release_selected_revision_current() {", 1)[1].split("\n}", 1)[0]
        self.assertIn("git ls-remote --heads origin refs/heads/main", supersession)
        mutation_guard = product.split(
            "release_candidate_current_before_mutation() {", 1)[1].split("\n}", 1)[0]
        self.assertIn("install/database mutation", mutation_guard)
        self.assertIn("return 3", mutation_guard)
        for token in ("run_publish", "reconcile_installed_product", "run_live_tests"):
            self.assertIn(token, activation)
    def test_observability_is_explicit_evidence_not_push_queue_load(self):
        for name in ("ui-observability.yml", "api-observability.yml"):
            text = (WORKFLOWS / name).read_text(encoding="utf-8")
            self.assertIn("workflow_dispatch:", text)
            self.assertNotIn("\n  push:\n", text)

    def test_benchmark_uses_immutable_driver_under_real_host_lock(self):
        text = (WORKFLOWS / "benchmark-evidence.yml").read_text(encoding="utf-8")
        driver = (ROOT / "scripts/benchmark-evidence-ci.sh").read_text(encoding="utf-8")
        self.assertNotIn("\nconcurrency:\n", text)
        self.assertEqual(1, text.count("host-resource.lock"))
        self.assertIn('git fetch --no-tags --depth=1 origin "$DISPATCH_SHA"', text)
        self.assertIn('git show "$workflow_sha:scripts/benchmark-evidence-ci.sh"', text)
        self.assertIn('git fetch --no-tags --prune origin "$target"', text)
        self.assertIn('exec bash "$driver"', text)
        self.assertIn("scripts/benchmark_scale_plan.py", driver)
        self.assertNotIn('echo "- Commit: `$sha`"', text)
        self.assertIn('echo "- Commit: \\`$sha\\`"', text)

    def test_observability_uploads_failure_evidence_then_fails_truthfully(self):
        for name in ("ui-observability.yml", "api-observability.yml"):
            text = (WORKFLOWS / name).read_text(encoding="utf-8")
            self.assertEqual(1, text.count("host-resource.lock"))
            self.assertIn("collector-exit-code.txt", text)
            self.assertIn("Enforce collector result", text)
            self.assertIn("if-no-files-found: error", text)
            self.assertNotIn("exit 0", text)
        ui = (WORKFLOWS / "ui-observability.yml").read_text(encoding="utf-8")
        self.assertNotIn("build/.stamps/npm-lock", ui)
        self.assertIn("ui-observability", ui)

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
        self.assertIn("deploy-qualification:", lifecycle)
        self.assertIn("deploy-candidate:", lifecycle)
        self.assertIn("needs: deploy-qualification", lifecycle)
        self.assertIn("deploy-activation:", lifecycle)
        self.assertIn("needs: deploy-candidate", lifecycle)
        self.assertIn("proof-qualification:", lifecycle)
        self.assertIn("proof-candidate:", lifecycle)
        self.assertIn("needs: proof-qualification", lifecycle)
        self.assertIn("proof-model:", lifecycle)
        self.assertIn("proof-activation:", lifecycle)
        self.assertIn("needs: proof-candidate", lifecycle)
        self.assertIn("needs: proof-model", lifecycle)

        proof = (WORKFLOWS / "competitive-proof.yml").read_text(encoding="utf-8")
        for stage in ("release-qualification", "release-candidate", "proof-model", "release-activation"):
            self.assertIn(f"stage: {stage}", proof)
        self.assertNotIn("scripts/product-ci.sh", proof)
        self.assertNotIn("runs-on: [self-hosted, laplace]", proof)


if __name__ == "__main__":
    unittest.main(verbosity=2)
