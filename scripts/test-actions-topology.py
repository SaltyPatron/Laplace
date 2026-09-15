#!/usr/bin/env python3
"""Executable contracts for one-product Actions orchestration."""
from __future__ import annotations

from pathlib import Path
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
MAIN = WORKFLOWS / "laplace.yml"
PR = WORKFLOWS / "pr-validation.yml"
PRODUCT = ROOT / "scripts" / "product-ci.sh"


def load(path: Path):
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def commands(job: dict) -> str:
    return "\n".join(step.get("run", "") for step in job.get("steps", []))


class ActionsAuthorityTests(unittest.TestCase):
    def test_main_has_one_product_job_and_one_mutation_authority(self):
        workflow = load(MAIN)
        self.assertEqual("laplace-substrate-lifecycle", workflow["concurrency"]["group"])
        self.assertEqual(["product", "chess_environment"], list(workflow["jobs"]))
        calibration = workflow["jobs"]["chess_environment"]
        self.assertEqual("product", calibration["needs"])
        self.assertEqual("./.github/workflows/benchmark-evidence.yml", calibration["uses"])
        self.assertIn("needs.product.outputs", calibration["if"])
        self.assertEqual("chess", calibration["with"]["suite"])
        product = workflow["jobs"]["product"]
        command = commands(product)
        self.assertIn('bash scripts/product-ci.sh "$LAPLACE_STAGE"', command)
        self.assertIn("bash scripts/product-ci.sh reconcile", command)
        for split_job in ("deploy", "db-ops", "publish", "restore-api", "smoke", "integration-test"):
            self.assertNotIn(split_job, workflow["jobs"])

    def test_product_script_owns_order_once(self):
        text = PRODUCT.read_text(encoding="utf-8")
        ordered = [
            "run_policy",
            "run_deps",
            "run_build",
            "run_dev",
            "run_install_and_db",
            "run_publish",
            "run_integration",
            "run_live_if_expected",
        ]
        positions = [text.rfind(f"\n{name}\n") for name in ordered]
        self.assertTrue(all(pos >= 0 for pos in positions), positions)
        self.assertEqual(sorted(positions), positions)
        self.assertIn('bash scripts/check-database-health.sh', text)
        self.assertIn('bash scripts/ensure-foundation.sh --check-only', text)
        self.assertIn('bash scripts/ensure-foundation.sh', text)
        self.assertIn('bash scripts/publish-applications.sh deploy', text)
        self.assertIn('bash scripts/publish-applications.sh recover', text)

    def test_tooling_only_main_changes_reconcile_without_native_rebuild_or_seed(self):
        source = MAIN.read_text(encoding="utf-8")
        self.assertIn("LAPLACE_FAST_ONLY", source)
        self.assertIn("scripts/check-*", source)
        self.assertIn("bash scripts/product-ci.sh reconcile", source)
        product = PRODUCT.read_text(encoding="utf-8")
        reconcile = product.split("reconcile_installed_product() {", 1)[1].split("\n}", 1)[0]
        self.assertIn("check-database-health.sh", reconcile)
        self.assertIn("verify-application-release.py", reconcile)
        self.assertNotIn("ensure_product_foundation", reconcile)
        self.assertNotIn("ensure-foundation.sh", reconcile)
        self.assertNotIn("pipeline.sh", reconcile)

    def test_foundation_restore_is_explicit_in_main_lifecycle(self):
        workflow = load(MAIN)
        restore = workflow["on"]["workflow_dispatch"]["inputs"]["restore_foundation"]
        self.assertEqual("false", restore["default"])
        self.assertIn("LAPLACE_RESTORE_FOUNDATION", MAIN.read_text(encoding="utf-8"))
        product = PRODUCT.read_text(encoding="utf-8")
        self.assertIn('if [[ "${LAPLACE_RESTORE_FOUNDATION:-}" == 1 ]]', product)
        self.assertIn("foundation restore not requested — no ingest", product)

    def test_pr_proof_is_proportional_and_nonmutating(self):
        workflow = load(PR)
        prove = workflow["jobs"]["prove"]
        command = commands(prove)
        self.assertIn("LAPLACE_PR_FULL_PROOF", command)
        self.assertIn("test-parallel.sh --policy", command)
        self.assertIn("scripts/pr-proof.sh", command)
        self.assertIn("git worktree add --detach", command)
        self.assertIn("git worktree remove --force", command)
        for forbidden in ("pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh deploy", "sudo "):
            self.assertNotIn(forbidden, command)
        self.assertEqual("true", workflow["concurrency"]["cancel-in-progress"])

    def test_manual_db_mutation_shares_product_lifecycle_lock(self):
        db = load(WORKFLOWS / "db-ops.yml")
        self.assertEqual("laplace-substrate-lifecycle", db["concurrency"]["group"])
        trigger = db["on"]
        names = {trigger} if isinstance(trigger, str) else set(trigger)
        self.assertEqual({"workflow_dispatch"}, names)

    def test_database_recreate_does_not_seed_unless_explicitly_requested(self):
        path = WORKFLOWS / "db-ops.yml"
        db = load(path)
        inputs = db["on"]["workflow_dispatch"]["inputs"]
        self.assertIn("restore_foundation", inputs)
        self.assertEqual("false", inputs["restore_foundation"]["default"])
        steps = db["jobs"]["db"]["steps"]
        recreate = next(step for step in steps if step.get("name") == "Recreate database structure and runtime")
        restore = next(step for step in steps if step.get("name") == "Restore canonical foundation (explicit opt-in)")
        self.assertNotIn("ensure-foundation.sh", recreate["run"])
        self.assertIn("ensure-foundation.sh --force", restore["run"])
        self.assertEqual("inputs.operation == 'recreate' && inputs.restore_foundation", restore["if"])

    def test_seed_workflows_are_manual_or_reusable_not_source_triggered(self):
        for path in sorted(WORKFLOWS.glob("seed-*.yml")):
            trigger = load(path)["on"]
            names = {trigger} if isinstance(trigger, str) else set(trigger)
            self.assertFalse(names & {"push", "pull_request"}, path.name)
            self.assertTrue(names & {"workflow_dispatch", "workflow_call"}, path.name)

    def test_all_external_actions_are_commit_pinned(self):
        import re
        for path in WORKFLOWS.glob("*.yml"):
            for line in path.read_text(encoding="utf-8").splitlines():
                if "uses:" not in line or "./" in line:
                    continue
                use = line.split("uses:", 1)[1].strip()
                self.assertRegex(use, r"^[^@\s]+@[0-9a-f]{40}$", path.name)


if __name__ == "__main__":
    unittest.main(verbosity=2)
