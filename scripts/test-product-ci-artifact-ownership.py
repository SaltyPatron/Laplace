#!/usr/bin/env python3
"""Regression coverage for product lifecycle build-artifact ownership."""
from __future__ import annotations

import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PRODUCT = ROOT / "scripts" / "product-ci.sh"


def function(name: str) -> str:
    source = PRODUCT.read_text(encoding="utf-8")
    start = source.index(f"{name}() {{\n")
    finish = source.index("\n}\n", start) + 3
    return source[start:finish]


class ProductStageOwnershipContract(unittest.TestCase):
    def test_every_build_consumer_requires_the_selected_revision(self):
        for name in (
            "run_dev_tests",
            "run_install",
            "run_db_tests",
            "run_live_tests",
            "run_competitive_model_proof",
        ):
            with self.subTest(function=name):
                owner = function(name)
                self.assertIn("require_built_revision", owner)

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

    def test_operator_check_runs_the_repository_ci_contracts(self):
        source = PRODUCT.read_text(encoding="utf-8")
        owner = function("run_ci_contract_checks")
        for command in (
            "python3 scripts/validate-pipeline.py",
            "python3 scripts/test-ci-workspace.py",
            "python3 scripts/test-product-ci-artifact-ownership.py",
            "python3 scripts/test-seed-workflow-ownership.py",
        ):
            with self.subTest(command=command):
                self.assertIn(command, owner)
        check_case = source.split('  check)\n', 1)[1].split('    ;;', 1)[0]
        self.assertIn("run_ci_contract_checks", check_case)
        self.assertNotIn("bash -n scripts/product-ci.sh", check_case)


class ProductRevisionProofExecution(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-product-revision-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1")
        self.env = env
        self.command("git", "init")
        self.command("git", "config", "user.name", "CI fixture")
        self.command("git", "config", "user.email", "fixture@example.invalid")
        (self.repo / "source.txt").write_text("current\n", encoding="utf-8")
        self.command("git", "add", "source.txt")
        self.command("git", "commit", "-m", "fixture")
        self.head = self.command("git", "rev-parse", "HEAD").stdout.strip()

    def command(self, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            list(arguments),
            cwd=self.repo,
            env=self.env,
            text=True,
            capture_output=True,
            check=check,
            timeout=10,
        )

    def prove(self) -> subprocess.CompletedProcess[str]:
        script = "set -euo pipefail\n" + function("require_built_revision") + "\nrequire_built_revision\n"
        return subprocess.run(
            ["bash", "-c", script],
            cwd=self.repo,
            env=self.env,
            text=True,
            capture_output=True,
            timeout=10,
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
