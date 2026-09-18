#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path

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
        ):
            with self.subTest(path=path):
                self.assertTrue(MODULE.ignored(path))
        for path in (
            "scripts/product-ci.sh",
            "web/src/App.tsx",
            "app/Laplace.Core/Foo.cs",
            "engine/core/src/foo.c",
        ):
            with self.subTest(path=path):
                self.assertFalse(MODULE.ignored(path))

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
                [str(SCRIPT), "--root", str(repo), "--base", base, "--head", policy],
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
                [str(SCRIPT), "--root", str(repo), "--base", policy, "--head", product],
                text=True, capture_output=True,
            )
            self.assertEqual(3, superseded.returncode, superseded.stdout + superseded.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
