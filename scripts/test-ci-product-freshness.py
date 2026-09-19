#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from ci_product_scope import GITHUB_PATH_IGNORES, candidate_equivalent, ignored
ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "ci-product-freshness.py"
SPEC = importlib.util.spec_from_file_location("ci_product_freshness", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class ProductFreshnessTests(unittest.TestCase):
    def test_ignore_law_matches_nonproduct_surfaces(self):
        for path in (
            ".github/workflows/ci-contract.yml",
            "docs/plan/anything.md",
            "README.md",
            "scripts/api-diagnostics.py",
            "web/scripts/capture-ui-diagnostics.mjs",
            "scripts/ci-impact-plan.py",
            "scripts/ci-qualification-cache.py",
            "scripts/ci-product-freshness.py",
            "scripts/ci_product_scope.py",
            "scripts/ci_managed_projects.py",
            "scripts/product-ci.sh",
            "scripts/check-deployed-revision.sh",
            "scripts/test-parallel.sh",
            "scripts/publish-applications.sh",
            "scripts/verify-api-payload.py",
            "deploy/linux/deploy.sh",
            "scripts/test-suites/live-api.sh",
            "scripts/test-ci-workspace.py",
            "scripts/test-workflow-architecture.py",
            "scripts/test-seed-workflow-ownership.py",
            "scripts/test-product-ci-artifact-ownership.py",
            "scripts/test-benchmark-suite.py",
            "scripts/validate-pipeline.py",
            "web/e2e/chat.spec.ts",
            "web/scripts/test-chess-ui.mjs",
            "web/scripts/verify-storage-proof-live.mjs",
            "scripts/tests/classify-ingest-exit.test.sh",
            "scripts/test-application-publish.py",
            "scripts/test-managed-db-scheduling.py",
        ):
            with self.subTest(path=path):
                self.assertTrue(MODULE.ignored(path))
        for path in (
            "web/src/App.tsx",
            "app/Laplace.Core/Foo.cs",
            "engine/core/src/foo.c",
        ):
            with self.subTest(path=path):
                self.assertFalse(MODULE.ignored(path))

    def test_main_delivery_trigger_matches_shared_product_ignore_law(self):
        workflow = (ROOT / ".github" / "workflows" / "laplace.yml").read_text(encoding="utf-8")
        for pattern in GITHUB_PATH_IGNORES:
            with self.subTest(pattern=pattern):
                self.assertIn(f'- "{pattern}"', workflow)

    def test_shared_scope_function_is_the_freshness_function(self):
        for path in ("scripts/test-ci-workspace.py", "scripts/product-ci.sh"):
            self.assertEqual(MODULE.ignored(path), ignored(path))
            self.assertEqual(MODULE.candidate_equivalent(path), candidate_equivalent(path))

    def test_native_test_change_qualifies_without_invalidating_candidate(self):
        for path in (
            "engine/core/tests/test_content_root_placement.cpp",
            "extension/laplace_substrate/tests/physicality_descriptor_native_probe.c",
        ):
            with self.subTest(path=path):
                self.assertFalse(ignored(path))
                self.assertTrue(candidate_equivalent(path))
                value = MODULE.product_delta([path])
                self.assertTrue(value["product_equivalent"])
                self.assertEqual(value["product_paths"], [])

    def test_managed_test_change_qualifies_without_invalidating_candidate(self):
        path = "app/Laplace.Substrate.Tests/Abstractions/ExampleTests.cs"
        self.assertFalse(ignored(path))
        self.assertTrue(candidate_equivalent(path))
        value = MODULE.product_delta([path])
        self.assertTrue(value["product_equivalent"])
        self.assertEqual(value["product_paths"], [])
    def test_only_nonproduct_changes_are_product_equivalent(self):
        value = MODULE.product_delta(
            [".github/workflows/a.yml", "docs/x.md", "README.md"]
        )
        self.assertTrue(value["product_equivalent"])
        self.assertEqual(value["product_paths"], [])

    def test_any_product_change_invalidates_candidate(self):
        value = MODULE.product_delta(
            [".github/workflows/a.yml", "app/Laplace.Core/Foo.cs"]
        )
        self.assertFalse(value["product_equivalent"])
        self.assertEqual(value["product_paths"], ["app/Laplace.Core/Foo.cs"])

    def test_cli_compares_exact_git_revisions(self):
        with tempfile.TemporaryDirectory(prefix="freshness-") as tmp:
            repo = Path(tmp)
            (repo / "app").mkdir()
            (repo / ".github/workflows").mkdir(parents=True)
            (repo / "app/a.cs").write_text("a\n", encoding="utf-8")
            subprocess.run(["git", "init", "-q"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.email", "ci@example.invalid"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.name", "CI"], cwd=repo, check=True)
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "base"], cwd=repo, check=True)
            base = subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=repo, text=True, capture_output=True, check=True
            ).stdout.strip()

            (repo / ".github/workflows/a.yml").write_text("name: A\n", encoding="utf-8")
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "workflow"], cwd=repo, check=True)
            policy = subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=repo, text=True, capture_output=True, check=True
            ).stdout.strip()
            equivalent = subprocess.run(
                [sys.executable, str(SCRIPT), "--root", str(repo), "--base", base, "--head", policy],
                text=True, capture_output=True,
            )
            self.assertEqual(0, equivalent.returncode, equivalent.stdout + equivalent.stderr)

            (repo / "app/a.cs").write_text("b\n", encoding="utf-8")
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "product"], cwd=repo, check=True)
            product = subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=repo, text=True, capture_output=True, check=True
            ).stdout.strip()
            superseded = subprocess.run(
                [sys.executable, str(SCRIPT), "--root", str(repo), "--base", policy, "--head", product],
                text=True, capture_output=True,
            )
            self.assertEqual(3, superseded.returncode, superseded.stdout + superseded.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
