#!/usr/bin/env python3
"""Repository-level CI/CD architecture invariants."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"


class MainPushQueueContract(unittest.TestCase):
    def test_main_push_is_one_chain_with_job_scoped_preemption(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        reusable = (WORKFLOWS / "product-stage.yml").read_text(encoding="utf-8")

        self.assertIn("name: Product — main delivery", lifecycle)
        self.assertIn("\n  push:\n", lifecycle)
        self.assertNotIn("workflow_dispatch:", lifecycle)
        self.assertNotIn("\nconcurrency:\n", lifecycle)
        self.assertFalse((WORKFLOWS / "mainline-delivery.yml").exists())

        self.assertIn("laplace-main-qualification", reusable)
        self.assertIn("laplace-main-delivery", reusable)
        self.assertIn(
            "cancel-in-progress: ${{ inputs.skip_if_superseded && inputs.stage == 'release-qualification' }}",
            reusable,
        )


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
        self.assertIn("cancel-in-progress: true", text)

    def test_main_delivery_plans_then_qualifies_then_delivers(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("  plan:", lifecycle)
        self.assertIn("scripts/ci-impact-plan.py", lifecycle)
        self.assertIn("  mainline-qualification:", lifecycle)
        self.assertIn("needs: plan", lifecycle)
        self.assertIn("stage: release-qualification", lifecycle)
        self.assertIn("dev_suites: ${{ needs.plan.outputs.dev_suites }}", lifecycle)
        self.assertIn("build_components: ${{ needs.plan.outputs.build_components }}", lifecycle)
        self.assertIn("  mainline-delivery:", lifecycle)
        self.assertIn("needs: [plan, mainline-qualification]", lifecycle)
        self.assertIn("stage: release-delivery", lifecycle)
        self.assertIn("build_components: ${{ needs.plan.outputs.build_components }}", lifecycle)
        self.assertIn("delivery_actions: ${{ needs.plan.outputs.delivery_actions }}", lifecycle)
        self.assertIn("db_suites: ${{ needs.plan.outputs.db_suites }}", lifecycle)
        self.assertIn("live_suites: ${{ needs.plan.outputs.live_suites }}", lifecycle)
        self.assertIn("publish_scope: ${{ needs.plan.outputs.publish_scope }}", lifecycle)
        self.assertEqual(2, lifecycle.count("uses: ./.github/workflows/product-stage.yml"))
        self.assertEqual(2, lifecycle.count("skip_if_superseded: true"))

    def test_targeted_code_player_does_not_duplicate_mainline_build(self):
        self.assertFalse((WORKFLOWS / "code-player-ci.yml").exists())
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn("stage: release-qualification", lifecycle)
        self.assertIn("stage: release-delivery", lifecycle)

    def test_superseded_push_is_rejected_only_when_source_tree_changes(self):
        reusable = (WORKFLOWS / "product-stage.yml").read_text(encoding="utf-8")
        preflight = reusable.split("  preflight:\n", 1)[1].split("\n  stage:\n", 1)[0]
        stage = reusable.split("  stage:\n", 1)[1]

        self.assertIn("runs-on: ubuntu-24.04", preflight)
        self.assertIn("git ls-remote --heads", preflight)
        self.assertIn('git -C "$probe_repo" fetch --no-tags --depth=1 origin "$TARGET_SHA"', preflight)
        self.assertIn('git -C "$probe_repo" fetch --no-tags --depth=1 origin "$latest_main"', preflight)
        self.assertIn('target_tree="$(git -C "$probe_repo" rev-parse "$TARGET_SHA^{tree}")"', preflight)
        self.assertIn('latest_tree="$(git -C "$probe_repo" rev-parse "$latest_main^{tree}")"', preflight)
        self.assertIn('[[ "$latest_tree" != "$target_tree" ]]', preflight)
        self.assertIn("tree-equivalent", preflight)
        self.assertIn("execute=false", preflight)
        self.assertNotIn("runs-on: [self-hosted, laplace]", preflight)
        self.assertNotIn("host-resource.lock", preflight)
        self.assertIn("needs: preflight", stage)
        self.assertIn("if: needs.preflight.outputs.execute == 'true'", stage)
        self.assertIn("runs-on: [self-hosted, laplace]", stage)

        fetch = stage.index('git fetch --no-tags --depth=2 origin "$TARGET_SHA"')
        resolve = stage.index("git ls-remote --heads origin refs/heads/main")
        tree_probe = stage.index('git fetch --no-tags --depth=1 origin "$latest_main"')
        worktree = stage.index('git worktree add --detach "$candidate_workspace" "$TARGET_SHA"')
        candidate_lock = stage.index('product-$TARGET_SHA.lock')
        self.assertLess(fetch, resolve)
        self.assertLess(resolve, tree_probe)
        self.assertLess(tree_probe, candidate_lock)
        self.assertIn('target_tree="$(git rev-parse "$TARGET_SHA^{tree}")"', stage)
        self.assertIn('latest_tree="$(git rev-parse "$latest_main^{tree}")"', stage)
        self.assertIn('[[ "$latest_tree" != "$target_tree" ]]', stage)
        self.assertIn("tree-equivalent", stage)
        self.assertLess(candidate_lock, worktree)
        self.assertIn("product-worktrees", stage)
        self.assertIn("git-metadata.lock", stage)
        self.assertIn("host-resource.lock", stage)
        stale_lock = stage.index('stale_lock="$work_root/product-$stale_sha.lock"')
        self.assertLess(stale_lock, candidate_lock)
        self.assertIn('[[ -e "$stale_lock" ]] || continue', stage)
        self.assertIn('exec {stale_fd}<>"$stale_lock"', stage)
        self.assertIn('flock -n "$stale_fd"', stage)
        self.assertIn('rm -rf "$stale_workspace"', stage)
        self.assertIn('status --porcelain --untracked-files=no', stage)
        self.assertNotIn('exec {stale_fd}>"$work_root/product-$stale_sha.lock"', stage)
        self.assertNotIn("build-resource.lock", stage)

    def test_full_qualification_audit_is_manual_and_weekly(self):
        audit = (WORKFLOWS / "full-qualification.yml").read_text(encoding="utf-8")
        self.assertIn("name: Audit — full product qualification", audit)
        self.assertIn("workflow_dispatch:", audit)
        self.assertIn("schedule:", audit)
        self.assertIn('cron: "17 7 * * 0"', audit)
        self.assertIn("stage: release-qualification", audit)
        self.assertIn("build_components: all", audit)
        self.assertIn("dev_suites: all", audit)
        self.assertIn("skip_if_superseded: false", audit)

    def test_candidate_reclamation_preserves_one_native_qualified_artifact_owner(self):
        reusable = (WORKFLOWS / "product-stage.yml").read_text(encoding="utf-8")
        self.assertIn("latest-source --suite native-dev", reusable)
        self.assertIn("preserve_native_source", reusable)
        self.assertIn("preserving latest native-qualified candidate", reusable)

    def test_planned_web_component_is_built_before_delivery(self):
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        pipeline = (ROOT / "scripts/pipeline.sh").read_text(encoding="utf-8")
        run_build = product.split("run_build() {", 1)[1].split("\n}\n\nrun_dev_test_matrix", 1)[0]
        self.assertIn("need_web", run_build)
        self.assertIn('phases+=(build-web)', run_build)
        self.assertIn("phase_build_web()", pipeline)
        self.assertIn("npm run build", pipeline)
        self.assertIn("build-web) phase_build_web", pipeline)

    def test_automatic_publication_consumes_qualified_or_installed_web_without_rebuild(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        pipeline = (ROOT / "scripts/pipeline.sh").read_text(encoding="utf-8")
        deploy = (ROOT / "deploy/linux/deploy.sh").read_text(encoding="utf-8")

        delivery = lifecycle.split("  mainline-delivery:\n", 1)[1]
        self.assertIn("build_components: ${{ needs.plan.outputs.build_components }}", delivery)
        self.assertIn("LAPLACE_REQUIRE_QUALIFIED_WEB", product)
        self.assertIn("LAPLACE_REUSE_INSTALLED_WEB", product)
        self.assertIn("web-artifact.py", pipeline)
        self.assertIn(" seal ", pipeline)
        self.assertIn("web-artifact.py", deploy)
        self.assertIn(" verify ", deploy)
        self.assertIn("use exact qualified front-end artifact", deploy)
        self.assertIn("preserve installed front-end artifact", deploy)
        self.assertIn('cp -r "$APP_DIR/wwwroot/." "$STAGE/wwwroot/"', deploy)
    def test_main_qualification_reuses_exact_valid_suite_receipts(self):
        reusable = (WORKFLOWS / "product-stage.yml").read_text(encoding="utf-8")
        self.assertIn("dev_suites:", reusable)
        self.assertIn("build_components:", reusable)
        self.assertIn("db_suites:", reusable)
        self.assertIn("live_suites:", reusable)
        self.assertIn("delivery_actions:", reusable)
        self.assertIn("publish_scope:", reusable)
        self.assertIn("LAPLACE_DEV_SUITES:", reusable)
        self.assertIn("LAPLACE_BUILD_COMPONENTS:", reusable)
        self.assertIn("LAPLACE_DB_SUITES:", reusable)
        self.assertIn("LAPLACE_LIVE_SUITES:", reusable)
        self.assertIn("LAPLACE_DELIVERY_ACTIONS:", reusable)
        self.assertIn("LAPLACE_PUBLISH_SCOPE:", reusable)
        self.assertIn("LAPLACE_USE_QUALIFICATION_CACHE:", reusable)

        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        matrix = product.split("run_dev_test_matrix() {", 1)[1].split("\n}\n\nrun_dev_tests", 1)[0]
        self.assertIn("LAPLACE_DEV_SUITES", matrix)
        self.assertIn("ci-qualification-cache.py check", matrix)
        self.assertIn("ci-qualification-cache.py record", matrix)
        self.assertIn("qualification planner kept", matrix)
        self.assertNotIn("|| rc=$?", matrix)

    def test_main_delivery_crosses_mutation_boundary_once_and_executes_impact_plan(self):
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        qualification = product.split("run_release_qualification() {", 1)[1].split("\n}", 1)[0]
        automatic = product.split("run_release_delivery() {", 1)[1].split("\n}", 1)[0]

        self.assertIn("run_build", qualification)
        self.assertIn("run_dev_test_matrix 1", qualification)
        self.assertNotIn("run_build", automatic)
        self.assertIn("release_candidate_current_before_mutation", automatic)
        self.assertIn("export LAPLACE_SKIP_IF_SUPERSEDED=0", automatic)
        self.assertIn("LAPLACE_DELIVERY_ACTIONS", automatic)
        for selector in ("install", "database", "reconcile", "publish", "live"):
            self.assertIn(f'csv_selected "$actions" {selector}', automatic)
        self.assertIn("run_install", automatic)
        self.assertIn("run_database_maintenance --prepare", automatic)
        self.assertIn("run_db_tests", automatic)
        self.assertIn("run_publish", automatic)
        self.assertIn("verify_installed_product", automatic)
        self.assertIn("run_live_tests", automatic)
        self.assertNotIn("run_release_activation", automatic)

    def test_manual_product_operations_are_outside_main_delivery_graph(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        manual = (WORKFLOWS / "product-operator.yml").read_text(encoding="utf-8")
        self.assertNotIn("workflow_dispatch:", lifecycle)
        self.assertIn("workflow_dispatch:", manual)
        self.assertEqual(1, manual.count("uses: ./.github/workflows/product-stage.yml"))
        self.assertIn("stage: ${{ inputs.operation }}", manual)
        self.assertIn("normal main delivery is automatic", manual)

    def test_competitive_proof_remains_explicit_and_composed(self):
        proof = (WORKFLOWS / "competitive-proof.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_dispatch:", proof)
        self.assertNotIn("\n  push:\n", proof)
        for stage in ("release-qualification", "release-candidate", "proof-model", "release-activation"):
            self.assertIn(f"stage: {stage}", proof)
        self.assertNotIn("scripts/product-ci.sh", proof)
        self.assertNotIn("runs-on: [self-hosted, laplace]", proof)

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

    def test_product_execution_has_hosted_preflight_and_one_self_hosted_stage_owner(self):
        reusable = (WORKFLOWS / "product-stage.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_call:", reusable)
        self.assertEqual(1, reusable.count("runs-on: ubuntu-24.04"))
        self.assertEqual(1, reusable.count("runs-on: [self-hosted, laplace]"))
        self.assertIn("needs: preflight", reusable)
        self.assertIn("product-worktrees", reusable)
        self.assertIn('product-$TARGET_SHA.lock', reusable)
        self.assertIn("host-resource.lock", reusable)
        self.assertNotIn("build-resource.lock", reusable)
        self.assertIn('exec bash scripts/product-ci.sh "$LAPLACE_STAGE"', reusable)

        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertNotIn("runs-on: [self-hosted, laplace]", lifecycle)
        self.assertNotIn("host-resource.lock", lifecycle)


if __name__ == "__main__":
    unittest.main(verbosity=2)
