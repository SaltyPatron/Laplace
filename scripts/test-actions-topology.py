#!/usr/bin/env python3
"""Executable contracts for one-product Actions orchestration."""
from __future__ import annotations

from pathlib import Path
import copy
import os
import shutil
import subprocess
import sys
import tempfile
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
            "run_repair_installed_corpus",
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


class ActionsAuditFailurePropagationTests(unittest.TestCase):
    """Execute the real audit against mutated workflows, never a parallel model."""

    @classmethod
    def setUpClass(cls):
        cls.originals = {path.name: load(path) for path in WORKFLOWS.glob("*.yml")}
        cls.scratch = tempfile.TemporaryDirectory(prefix="actions-audit-", dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        cls.root = Path(cls.scratch.name)
        (cls.root / ".github/workflows").mkdir(parents=True)
        (cls.root / "scripts").mkdir()
        for name in ("actions-audit.py", "product-ci.sh", "pr-proof.sh", "bootstrap-laplace-runner.sh", "test-profile-registry.py", "test-profiles.json", "test-parallel.sh", "ci-policy.sh", "maintain-installed-database.sh", "repair-legacy-content-lifecycle.sh"):
            shutil.copy2(ROOT / "scripts" / name, cls.root / "scripts" / name)

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def check_audit(self, mutate=None, diagnostic=None):
        workflows = copy.deepcopy(self.originals)
        if mutate:
            mutate(workflows)
        for name, workflow in workflows.items():
            (self.root / ".github/workflows" / name).write_text(yaml.safe_dump(workflow, sort_keys=False), encoding="utf-8")
        result = subprocess.run([sys.executable, str(self.root / "scripts/actions-audit.py")], capture_output=True, text=True, timeout=20)
        if diagnostic:
            self.assertEqual(1, result.returncode, result.stdout + result.stderr)
            self.assertIn(diagnostic, result.stderr)
        else:
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("ACTIONS_AUDIT_OK", result.stdout)

    @staticmethod
    def step(workflows, filename, key, value):
        job = next(iter(workflows[filename]["jobs"].values()))
        return next(step for step in job["steps"] if step.get(key) == value)

    def test_deferred_readiness_and_optional_baseline_are_accepted(self):
        self.check_audit()

    def test_database_maintenance_cannot_bypass_managed_quiescence(self):
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        try:
            path.write_text(original.replace("python3 scripts/quiesce-managed-database.py", "python3 scripts/other.py"))
            self.check_audit(diagnostic="one managed-quiescence owner")
        finally:
            path.write_text(original)

    def test_database_maintenance_order_and_failure_propagation_are_required(self):
        path = self.root / "scripts/maintain-installed-database.sh"
        original = path.read_text()
        reconcile = 'bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"'
        migrate = 'bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env'
        for mutation in (original.replace(migrate, ""), original.replace(reconcile, reconcile + " || true"),
                         original.replace(migrate, "MIGRATION_PLACEHOLDER").replace(reconcile, migrate).replace("MIGRATION_PLACEHOLDER", reconcile)):
            try:
                path.write_text(mutation)
                self.check_audit(diagnostic="maintenance sequence must migrate, reconcile, and verify once")
            finally:
                path.write_text(original)

    def test_corpus_repair_requires_successful_publication_and_its_own_quiescence(self):
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        repair = 'bash scripts/repair-legacy-content-lifecycle.sh "${PGDATABASE:-laplace}"'
        mutations = (
            (original.replace("\nrun_publish\n", "\nREPAIR_ORDER_PLACEHOLDER\n").replace("\nrun_repair_installed_corpus\n", "\nrun_publish\n").replace("\nREPAIR_ORDER_PLACEHOLDER\n", "\nrun_repair_installed_corpus\n"), "product lifecycle order drifted"),
            (original.replace("\nrun_repair_installed_corpus\n", "\nrun_repair_installed_corpus || true\n"), "one unsuppressed lifecycle invocation"),
            (original.replace(repair, repair + " || true"), "post-publication corpus repair"),
            (original.replace('LAPLACE_REPAIR_PUBLISHED_SOURCE="$(git rev-parse HEAD)"', 'LAPLACE_REPAIR_PUBLISHED_SOURCE="unknown"'), "post-publication corpus repair"),
            (original.replace('--max-current-readback-bytes 8589934592', ''), "post-publication corpus repair"),
        )
        for mutation, diagnostic in mutations:
            with self.subTest(diagnostic=diagnostic):
                try:
                    self.assertNotEqual(original, mutation)
                    path.write_text(mutation)
                    self.check_audit(diagnostic=diagnostic)
                finally:
                    path.write_text(original)

    def test_owned_repair_resume_cannot_be_omitted_swallowed_or_moved_after_build(self):
        path=self.root / "scripts/product-ci.sh"
        original=path.read_text()
        call="  reconcile|deploy|integrate|all|applications) resume_held_repair_if_needed ;;"
        block='case "$stage" in\n'+call+'\nesac\n'
        for mutation in (original.replace("--resume-if-needed", "--wrong-resume-mode"),
                         original.replace(call,call.replace(" ;;"," || true ;;")),
                         original.replace(block,"").replace("\nrun_build\n","\nrun_build\n"+block)):
            try:
                self.assertNotEqual(original,mutation)
                path.write_text(mutation)
                self.check_audit(diagnostic="owned repair resume must run unsuppressed")
            finally:
                path.write_text(original)

    def test_publication_recovery_cannot_restart_api_after_unknown_repair_transaction(self):
        source = PRODUCT.read_text()
        footer = "trap recover_publish EXIT\nrun_publish\n" + source.rsplit(
            "trap recover_publish EXIT\nrun_publish\n", 1)[1]
        recovery = "recover_publish() {" + source.split("recover_publish() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        script = """set -euo pipefail
bash() { echo publication-recovery; }
ensure_api_running() { echo API-START; }
run_publish() { echo published; }
run_repair_installed_corpus() { echo repair-transaction-unknown; return 37; }
run_integration() { echo unexpected-integration; }
run_live_if_expected() { echo unexpected-live; }
run_perf() { echo unexpected-perf; }
""" + recovery + footer
        result = subprocess.run(["bash", "-c", script], text=True, capture_output=True, timeout=10)
        self.assertEqual(37, result.returncode, result.stdout + result.stderr)
        self.assertEqual(["published", "repair-transaction-unknown"], result.stdout.splitlines())
        path = self.root / "scripts/product-ci.sh"
        original = path.read_text()
        try:
            path.write_text(original.replace("\nrun_publish\ntrap - EXIT\n", "\nrun_publish\n"))
            self.check_audit(diagnostic="publication recovery must end before repair")
        finally:
            path.write_text(original)

    def test_database_repair_cannot_bypass_measurement_lane(self):
        path = self.root / "scripts/repair-legacy-content-lifecycle.sh"
        original = path.read_text()
        try:
            path.write_text(original.replace("bash scripts/measure-lane.sh --", "bash scripts/unlocked.sh --"))
            self.check_audit(diagnostic="authoritative measurement lane")
        finally:
            path.write_text(original)

    def test_failed_proof_cannot_be_hidden_at_step_or_job(self):
        mutations = [
            lambda ws: self.step(ws, "pr-validation.yml", "id", "proof").update({"continue-on-error": "true"}),
            lambda ws: ws["pr-validation.yml"]["jobs"]["prove"].update({"continue-on-error": "${{ true }}"}),
            lambda ws: self.step(ws, "laplace.yml", "name", "Run full product lifecycle").update({"continue-on-error": "True"}),
            lambda ws: self.step(ws, "benchmark-evidence.yml", "name", "Upload complete benchmark evidence").update({"continue-on-error": "true"}),
        ]
        for index, mutation in enumerate(mutations):
            with self.subTest(index=index):
                self.check_audit(mutation, "hidden red")

    def test_readiness_uses_raw_outcome_and_is_not_swallowed(self):
        def set_gate(ws, **changes):
            self.step(ws, "benchmark-evidence.yml", "name", "Require installed chess tools and verified release checks").update(changes)
        mutations = [
            (lambda ws: set_gate(ws, **{"if": "success()"}), "raw failed outcome"),
            (lambda ws: set_gate(ws, **{"if": "${{ !cancelled() && inputs.suite == 'chess' && steps.chess_readiness.conclusion == 'failure' }}"}), "raw failed outcome"),
            (lambda ws: set_gate(ws, run="echo failed\nexit 0\n"), "fail unconditionally"),
            (lambda ws: set_gate(ws, **{"continue-on-error": "true"}), "hidden red"),
            (lambda ws: self.step(ws, "benchmark-evidence.yml", "id", "chess_readiness").update({"if": "false"}), "every attempted chess path"),
        ]
        for index, (mutation, diagnostic) in enumerate(mutations):
            with self.subTest(index=index):
                self.check_audit(mutation, diagnostic)

    def test_readiness_gate_must_follow_success_independent_upload(self):
        def move_gate(ws):
            steps = ws["benchmark-evidence.yml"]["jobs"]["benchmark"]["steps"]
            gate = steps.pop()
            steps.insert(0, gate)
        self.check_audit(move_gate, "enforced after evidence upload")
        self.check_audit(lambda ws: self.step(ws, "benchmark-evidence.yml", "name", "Upload complete benchmark evidence").update({"if": "success()"}), "upload on failure")
        self.check_audit(lambda ws: self.step(ws, "benchmark-evidence.yml", "name", "Upload complete benchmark evidence")["with"].update({"if-no-files-found": "warn"}), "missing benchmark evidence must fail")

    def test_baseline_cannot_acquire_or_gate_real_proof(self):
        def move_proof_into_baseline(ws):
            self.step(ws, "pr-validation.yml", "id", "baseline_diagnostic")["run"] += "\nbash scripts/pr-proof.sh\n"
        self.check_audit(move_proof_into_baseline, "authoritative or mutating operation")
        self.check_audit(lambda ws: self.step(ws, "pr-validation.yml", "id", "proof").update({"if": "steps.baseline_diagnostic.outcome == 'success'"}), "independent of optional baseline")
        self.check_audit(lambda ws: self.step(ws, "pr-validation.yml", "id", "baseline_diagnostic").update({"if": "always()"}), "limited to full proof scope")
        self.check_audit(lambda ws: self.step(ws, "pr-validation.yml", "name", "Upload retained baseline before full proof").update({"run": "bash scripts/pr-proof.sh"}), "only retain the attempted diagnostic")

    def test_optional_readiness_cannot_swallow_authoritative_proof(self):
        def hide_proof(ws):
            self.step(ws, "benchmark-evidence.yml", "id", "chess_readiness")["run"] += "\nbash scripts/pr-proof.sh\n"
        self.check_audit(hide_proof, "authoritative or mutating operation")

    def test_reusable_measurement_cannot_gain_a_deployment_job(self):
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"].update({"deploy": {"runs-on": "self-hosted", "timeout-minutes": "30", "steps": [{"run": "bash scripts/pipeline.sh install"}]}}), "only its measurement job")
        self.check_audit(lambda ws: ws["benchmark-evidence.yml"]["jobs"]["benchmark"]["steps"].insert(0, {"run": "bash scripts/pipeline.sh install"}), "product mutation authority")

    def test_duplicate_optional_step_cannot_mask_another_failure(self):
        def duplicate(ws):
            steps = ws["pr-validation.yml"]["jobs"]["prove"]["steps"]
            steps.append(copy.deepcopy(self.step(ws, "pr-validation.yml", "id", "baseline_diagnostic")))
        self.check_audit(duplicate, "requires exactly one id=baseline_diagnostic")

    def test_no_extra_product_mutation_job(self):
        self.check_audit(lambda ws: ws["laplace.yml"]["jobs"].update({"deploy": {"runs-on": "self-hosted", "timeout-minutes": "30", "steps": [{"run": "bash scripts/pipeline.sh install"}]}}), "one product job and its chess measurement")
        self.check_audit(lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"].update({"steps": [{"run": "bash scripts/pipeline.sh install"}]}), "may only call the bounded reusable workflow")

    def test_measurement_requires_successful_activation_and_exact_source(self):
        mutations = [
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"].update({"if": "always()"}), "requires successful product activation"),
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"].update({"needs": []}), "single product authority"),
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"]["with"].update({"target_ref": "main"}), "activated source"),
            (lambda ws: ws["laplace.yml"]["jobs"]["chess_environment"]["with"].update({"suite": "all"}), "activated source"),
            (lambda ws: ws["laplace.yml"]["jobs"]["product"]["outputs"].update({"chess_benchmark_ready": "true"}), "successful activation gate"),
            (lambda ws: self.step(ws, "laplace.yml", "id", "chess_benchmark_gate").update({"if": "always()"}), "activation must precede measurement authorization"),
        ]
        for index, (mutation, diagnostic) in enumerate(mutations):
            with self.subTest(index=index):
                self.check_audit(mutation, diagnostic)

    def test_native_only_deploy_cannot_authorize_installed_corpus_or_measurement(self):
        old_gate = "env.LAPLACE_FAST_ONLY != '1' && (env.LAPLACE_STAGE == 'all' || env.LAPLACE_STAGE == 'deploy' || env.LAPLACE_STAGE == 'applications')"
        for key, name, condition, diagnostic in (
            ("id", "chess_benchmark_gate", old_gate, "activation must precede measurement authorization"),
            ("name", "Admit official Stockfish source and verify native corpus readback", old_gate, "application-publishing stage"),
            ("name", "Retain official Stockfish corpus admission evidence", "always() && " + old_gate, "application-publishing stage"),
        ):
            with self.subTest(name=name):
                self.check_audit(lambda ws: self.step(ws, "laplace.yml", key, name).update({"if": condition}), diagnostic)


if __name__ == "__main__":
    unittest.main(verbosity=2)
