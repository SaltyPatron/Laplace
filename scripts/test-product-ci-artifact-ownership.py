#!/usr/bin/env python3
"""Regression coverage for product lifecycle build and deployed-revision ownership."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PRODUCT = ROOT / "scripts" / "product-ci.sh"
PUBLISH = ROOT / "scripts" / "publish-applications.sh"
DEPLOYED_PROOF = ROOT / "scripts" / "check-deployed-revision.sh"


def function(name: str) -> str:
    source = PRODUCT.read_text(encoding="utf-8")
    start = source.index(f"{name}() {{\n")
    finish = source.index("\n}\n", start) + 3
    return source[start:finish]


def publish_function(name: str) -> str:
    source = PUBLISH.read_text(encoding="utf-8")
    start = source.index(f"{name}() {{\n")
    finish = source.index("\n}\n", start) + 3
    return source[start:finish]


class ProductStageOwnershipContract(unittest.TestCase):
    def test_build_consumers_require_the_selected_revision(self):
        for name in ("run_dev_tests", "run_install", "run_db_tests", "run_publish"):
            with self.subTest(function=name):
                self.assertIn("require_built_revision", function(name))

    def test_live_and_reconcile_prove_the_installed_application_revision(self):
        live = function("run_live_tests")
        self.assertNotIn("require_built_revision", live)
        self.assertLess(live.index("require_deployed_revision"),
                        live.index("test-parallel.sh"))
        reconcile = function("reconcile_installed_product")
        self.assertLess(reconcile.index("require_deployed_revision"),
                        reconcile.index("reconcile-highway-masks.sh"))

    def test_publication_recovers_before_rejecting_stale_build_then_deploys(self):
        owner = function("run_publish")
        recovery = owner.index("publish-applications.sh recover")
        proof = owner.index("require_built_revision")
        deployment = owner.index("publish-applications.sh deploy")
        self.assertLess(recovery, proof)
        self.assertLess(proof, deployment)

    def test_successful_build_records_checkout_after_planned_build_completion(self):
        owner = function("run_build")
        planned = owner.index('bash scripts/pipeline.sh "${args[@]}" "${phases[@]}"')
        marker = owner.index("git rev-parse HEAD > build/.laplace-source-revision")
        self.assertIn("phases+=(build-native)", owner)
        self.assertIn("phases+=(build-app)", owner)
        self.assertLess(planned, marker)

    def test_full_publication_commits_only_after_revision_receipt_verification(self):
        source = PUBLISH.read_text(encoding="utf-8")
        block = source.split("    deploy)\n", 1)[1].split("      ;;\n", 1)[0]
        publish = block.index('bash "$ROOT/scripts/pipeline.sh" publish')
        install = block.index("application_revision_install")
        restart = block.index("systemctl restart laplace-api")
        verify = block.index("application_revision_verify")
        commit = block.index("managed commit")
        self.assertLess(publish, install)
        self.assertLess(install, restart)
        self.assertLess(restart, verify)
        self.assertLess(verify, commit)

    def test_api_only_publication_keeps_revision_proof_inside_rollback_scope(self):
        source = PUBLISH.read_text(encoding="utf-8")
        start = source.index("application_api_main() (\n")
        finish = source.index("\n)\n\nif [[", start)
        block = source[start:finish]
        publish = block.index('application_api_publish "$backup/next.json"')
        install = block.index("application_revision_install")
        start_service = block.index("application_api_control start")
        verify_payload = block.index('application_api_verify "$backup/next.json"')
        verify_revision = block.index("application_revision_verify")
        commit_cleanup = block.index('rm "$ROOT/build/.api-publish-backup"')
        self.assertLess(publish, install)
        self.assertLess(install, start_service)
        self.assertLess(start_service, verify_payload)
        self.assertLess(verify_payload, verify_revision)
        self.assertLess(verify_revision, commit_cleanup)

    def test_revision_install_is_derived_from_the_selected_build_receipt(self):
        owner = publish_function("application_revision_install")
        self.assertIn('application_revision_expected', owner)
        self.assertIn('build/.laplace-source-revision', owner)
        self.assertIn('.laplace-source-revision.tmp.$$', owner)
        self.assertIn('check-deployed-revision.sh', owner)

    def test_repository_contract_owner_contains_every_static_control(self):
        owner = function("run_ci_contract_checks")
        for command in (
            "scripts/check-deployed-revision.sh",
            "python3 scripts/validate-pipeline.py",
            "python3 scripts/test-ci-workspace.py",
            "python3 scripts/test-product-ci-artifact-ownership.py",
            "python3 scripts/test-seed-workflow-ownership.py",
        ):
            with self.subTest(command=command):
                self.assertIn(command, owner)
        source = PRODUCT.read_text(encoding="utf-8")
        check_case = source.split('  check)\n', 1)[1].split('    ;;', 1)[0]
        self.assertIn("run_ci_contract_checks", check_case)

    def test_deploy_is_composed_from_release_modules_not_policy_work(self):
        deploy = function("run_deploy")
        self.assertNotIn("run_ci_contract_checks", deploy)
        stages = ["run_release_qualification", "run_release_candidate", "run_release_activation"]
        positions = [deploy.index(token) for token in stages]
        self.assertEqual(positions, sorted(positions))

        qualification = function("run_release_qualification")
        self.assertIn("check_deps", qualification)
        self.assertIn("run_build", qualification)
        self.assertIn("run_dev_test_matrix 1", qualification)
        for forbidden in ("run_install", "run_database_maintenance", "run_db_tests", "run_publish"):
            self.assertNotIn(forbidden, qualification)

        candidate = function("run_release_candidate")
        mutation = [
            "check_deps",
            "require_built_revision",
            "release_candidate_current_before_mutation",
            "run_install",
            "run_database_maintenance --prepare",
            "run_db_tests",
            "run_publish",
        ]
        positions = [candidate.index(token) for token in mutation]
        self.assertEqual(positions, sorted(positions))
        self.assertNotIn("run_build", candidate)
        self.assertNotIn("run_dev_tests", candidate)

        activation = function("run_release_activation")
        self.assertNotIn("run_publish", activation)
        delivery = ["reconcile_installed_product", "run_live_tests"]
        positions = [activation.index(token) for token in delivery]
        self.assertEqual(positions, sorted(positions))

    def test_qualified_main_delivery_executes_only_selected_mutations(self):
        delivery = function("run_release_delivery")
        self.assertIn("check_deps", delivery)
        self.assertIn("require_built_revision", delivery)
        self.assertIn("release_candidate_current_before_mutation", delivery)
        self.assertIn("export LAPLACE_SKIP_IF_SUPERSEDED=0", delivery)
        self.assertIn("LAPLACE_DELIVERY_ACTIONS", delivery)
        self.assertIn('csv_selected "$actions" install', delivery)
        self.assertIn('csv_selected "$actions" database', delivery)
        self.assertIn('csv_selected "$actions" reconcile', delivery)
        self.assertIn('csv_selected "$actions" publish', delivery)
        self.assertIn('csv_selected "$actions" live', delivery)
        self.assertIn("run_install", delivery)
        self.assertIn("run_database_maintenance --prepare", delivery)
        self.assertIn("run_db_tests", delivery)
        self.assertIn("run_publish", delivery)
        self.assertIn("verify_installed_product", delivery)
        self.assertIn("reconcile_installed_product", delivery)
        self.assertIn("run_live_tests", delivery)
        self.assertNotIn("run_release_qualification", delivery)
        self.assertNotIn("run_build", delivery)
        self.assertNotIn("run_release_activation", delivery)

    def test_automatic_delivery_carries_impact_from_the_installed_revision(self):
        carry = function("carry_forward_undelivered_impact")
        self.assertIn(".laplace-source-revision", carry)
        self.assertIn("ci-impact-plan.py", carry)
        self.assertIn("--base \"$deployed\" --head \"$target\"", carry)
        self.assertIn("LAPLACE_BUILD_COMPONENTS", carry)
        self.assertIn("LAPLACE_DEV_SUITES", carry)
        self.assertIn("LAPLACE_DELIVERY_ACTIONS", carry)
        self.assertIn("force_full_carry_forward_impact", carry)

        qualification = function("run_release_qualification")
        delivery = function("run_release_delivery")
        self.assertLess(
            qualification.index("carry_forward_undelivered_impact"),
            qualification.index("run_build"),
        )
        self.assertLess(
            delivery.index("carry_forward_undelivered_impact"),
            delivery.index("require_built_revision"),
        )

    def test_build_plan_reuses_matching_qualified_native_artifact(self):
        build = function("run_build")
        reuse = function("reuse_qualified_native_build")
        self.assertIn("LAPLACE_BUILD_COMPONENTS", build)
        self.assertIn("build-native", build)
        self.assertIn("build-app", build)
        self.assertIn("reuse_qualified_native_build", build)
        self.assertIn("ci-qualification-cache.py source --suite native-dev", reuse)
        self.assertIn("product-worktrees", reuse)
        self.assertIn('ln -s "$source_root/build/engine" build/engine', reuse)

    def test_database_and_live_matrices_are_impact_selectable(self):
        database = function("run_db_tests")
        live = function("run_live_tests")
        self.assertIn("LAPLACE_DB_SUITES", database)
        self.assertIn('csv_selected "$selected" db-health', database)
        self.assertIn('csv_selected "$selected" native-db', database)
        self.assertIn('csv_selected "$selected" managed-db', database)
        self.assertIn("LAPLACE_LIVE_SUITES", live)
        for suite in ("live-floor", "live-api", "managed-live", "generation-eval"):
            self.assertIn(f'csv_selected "$selected" {suite}', live)

    def test_competitive_proof_extends_the_same_release_modules(self):
        proof = function("run_proof")
        stages = [
            "run_release_qualification",
            "run_release_candidate",
            "run_proof_model",
            "run_release_activation",
        ]
        positions = [proof.index(token) for token in stages]
        self.assertEqual(positions, sorted(positions))

        model = function("run_proof_model")
        self.assertLess(model.index("require_built_revision"),
                        model.index("model-synthesize-ci.sh"))

    def test_mainline_is_only_build_and_development_tests(self):
        owner = function("run_mainline")
        self.assertIn("run_release_qualification", owner)
        qualification = function("run_release_qualification")
        self.assertIn("check_deps", qualification)
        self.assertIn("run_build", qualification)
        self.assertIn("run_dev_test_matrix 1", qualification)
        for forbidden in ("run_install", "run_database_maintenance", "run_db_tests", "run_publish", "run_live_tests"):
            self.assertNotIn(forbidden, qualification)

    def test_database_maintenance_never_recreates_or_seeds_implicitly(self):
        source = (ROOT / "scripts/maintain-installed-database.sh").read_text(encoding="utf-8")
        self.assertNotIn("ensure-foundation.sh", source)
        self.assertNotIn("needs_identity_reseed", source)
        self.assertIn("recreate is destructive and must be requested explicitly", source)
        self.assertIn('[[ "${LAPLACE_FRESH_DB:-}" != 1 ]] || args+=(--fresh-db)', source)


class ProductRevisionProofExecution(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-product-revision-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1")
        self.command("git", "init")
        self.command("git", "config", "user.name", "CI fixture")
        self.command("git", "config", "user.email", "fixture@example.invalid")
        (self.repo / "source.txt").write_text("current\n", encoding="utf-8")
        self.command("git", "add", "source.txt")
        self.command("git", "commit", "-m", "fixture")
        self.head = self.command("git", "rev-parse", "HEAD").stdout.strip()

    def command(self, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            list(arguments), cwd=self.repo, env=self.env, text=True,
            capture_output=True, check=check, timeout=10,
        )

    def prove(self) -> subprocess.CompletedProcess[str]:
        script = "set -euo pipefail\n" + function("require_built_revision") + "\nrequire_built_revision\n"
        return subprocess.run(
            ["bash", "-c", script], cwd=self.repo, env=self.env,
            text=True, capture_output=True, timeout=10,
        )

    def test_missing_marker_is_rejected(self):
        result = self.prove()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("prepared build does not belong to this checkout", result.stderr)
        self.assertIn("found missing", result.stderr)

    def test_stale_marker_is_rejected(self):
        (self.repo / "build").mkdir()
        (self.repo / "build/.laplace-source-revision").write_text("0" * 40 + "\n", encoding="utf-8")
        result = self.prove()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(self.head, result.stderr)
        self.assertIn("0" * 40, result.stderr)

    def test_current_marker_is_accepted(self):
        (self.repo / "build").mkdir()
        (self.repo / "build/.laplace-source-revision").write_text(self.head + "\n", encoding="utf-8")
        result = self.prove()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


class DeployedRevisionProofExecution(unittest.TestCase):
    EXPECTED = "1" * 40
    STALE = "2" * 40

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-deployed-revision-")
        self.addCleanup(self.temp.cleanup)
        self.app = Path(self.temp.name)
        self.env = dict(os.environ, LAPLACE_APP_DIR=str(self.app))

    def prove(self) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["bash", str(DEPLOYED_PROOF), self.EXPECTED],
            cwd=ROOT,
            env=self.env,
            text=True,
            capture_output=True,
            timeout=10,
        )

    def test_missing_deployed_revision_is_rejected(self):
        result = self.prove()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("found missing", result.stderr)

    def test_stale_deployed_revision_is_rejected(self):
        (self.app / ".laplace-source-revision").write_text(self.STALE + "\n", encoding="utf-8")
        result = self.prove()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn(self.EXPECTED, result.stderr)
        self.assertIn(self.STALE, result.stderr)

    def test_selected_deployed_revision_is_accepted(self):
        (self.app / ".laplace-source-revision").write_text(self.EXPECTED + "\n", encoding="utf-8")
        result = self.prove()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(self.EXPECTED, result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
