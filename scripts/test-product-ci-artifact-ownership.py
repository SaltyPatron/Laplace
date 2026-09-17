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
        for name in ("run_dev_tests", "run_install", "run_db_tests", "run_live_tests"):
            with self.subTest(function=name):
                self.assertIn("require_built_revision", function(name))

    def test_live_and_reconcile_prove_the_installed_application_revision(self):
        live = function("run_live_tests")
        self.assertLess(live.index("require_built_revision"),
                        live.index("require_deployed_revision"))
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

    def test_successful_build_records_checkout_after_build_completion(self):
        owner = function("run_build")
        build = owner.index('bash scripts/pipeline.sh "${args[@]}" build')
        marker = owner.index("git rev-parse HEAD > build/.laplace-source-revision")
        self.assertLess(build, marker)

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
        self.assertLess(deploy.index("run_release_candidate"),
                        deploy.index("run_release_activation"))

        candidate = function("run_release_candidate")
        qualification = [
            "check_deps",
            "run_build",
            "run_dev_tests",
            "run_install",
            "run_database_maintenance --prepare",
            "run_db_tests",
        ]
        positions = [candidate.index(token) for token in qualification]
        self.assertEqual(positions, sorted(positions))

        activation = function("run_release_activation")
        delivery = ["run_publish", "reconcile_installed_product", "run_live_tests"]
        positions = [activation.index(token) for token in delivery]
        self.assertEqual(positions, sorted(positions))

    def test_competitive_proof_extends_the_same_release_modules(self):
        proof = function("run_proof")
        self.assertLess(proof.index("run_release_candidate"),
                        proof.index("run_proof_model"))
        self.assertLess(proof.index("run_proof_model"),
                        proof.index("run_release_activation"))

        model = function("run_proof_model")
        self.assertLess(model.index("require_built_revision"),
                        model.index("model-synthesize-ci.sh"))

    def test_mainline_is_only_build_and_development_tests(self):
        owner = function("run_mainline")
        self.assertIn("check_deps", owner)
        self.assertIn("run_build", owner)
        self.assertIn("run_dev_tests", owner)
        for forbidden in ("run_install", "run_database_maintenance", "run_db_tests", "run_publish", "run_live_tests"):
            self.assertNotIn(forbidden, owner)

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
