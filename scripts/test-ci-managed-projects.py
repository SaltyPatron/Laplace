#!/usr/bin/env python3
from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path

from ci_managed_projects import plan_changed, test_filter_for_paths, write_solution

ROOT = Path(__file__).resolve().parents[1]


class ManagedProjectImpactTests(unittest.TestCase):
    def test_api_change_builds_and_tests_only_affected_managed_closure(self):
        value = plan_changed(ROOT, ["app/Laplace.Endpoints.OpenAICompat/Program.cs"])
        self.assertFalse(value["full"])
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
            value["build_projects"],
        )
        self.assertEqual(
            ["app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj"],
            value["test_projects"],
        )
        self.assertNotIn(
            "app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj",
            value["test_projects"],
        )

    def test_test_only_change_does_not_pull_unrelated_projects(self):
        target = "app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj"
        value = plan_changed(
            ROOT, ["app/Laplace.Substrate.Tests/Abstractions/DecomposerArchitectureGateTests.cs"]
        )
        self.assertEqual([target], value["changed_projects"])
        self.assertEqual([target], value["build_projects"])
        self.assertEqual([target], value["test_projects"])

    def test_test_only_source_change_builds_exact_class_filter(self):
        value = test_filter_for_paths(
            ROOT,
            ["app/Laplace.Decomposers.Tests/Unicode/UnicodeDecomposerTests.cs"],
        )
        self.assertIn(
            "FullyQualifiedName~Laplace.Decomposers.Unicode.Tests.UnicodeDecomposerTests",
            value,
        )

    def test_non_test_or_shared_input_falls_back_to_project_wide_filter(self):
        self.assertEqual(
            "",
            test_filter_for_paths(ROOT, ["app/Laplace.Core/Core/Hash128.cs"]),
        )
        self.assertEqual(
            "",
            test_filter_for_paths(
                ROOT, ["app/Laplace.Decomposers.Tests/Laplace.Decomposers.Tests.csproj"]
            ),
        )

    def test_shared_core_change_expands_through_reverse_project_references(self):
        value = plan_changed(ROOT, ["app/Laplace.Core/Core/Hash128.cs"])
        self.assertIn(
            "app/Laplace.Core.Tests/Laplace.Core.Tests.csproj",
            value["test_projects"],
        )
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj",
            value["test_projects"],
        )
        self.assertGreater(len(value["test_projects"]), 1)

    def test_unclassified_app_build_input_fails_safe_to_all_managed_projects(self):
        value = plan_changed(ROOT, ["app/Directory.Build.props"])
        self.assertTrue(value["full"])
        self.assertEqual(["all"], value["build_projects"])
        self.assertEqual(["all"], value["test_projects"])

    def test_solution_writer_emits_only_selected_projects(self):
        root = Path(os.environ.get("RUNNER_TEMP") or os.environ.get("TMPDIR") or ROOT / "build")
        root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="managed-impact-", dir=root) as tmp:
            output = Path(tmp) / "selected.slnx"
            write_solution(
                ROOT,
                "app/Laplace.Core.Tests/Laplace.Core.Tests.csproj",
                output,
            )
            text = output.read_text(encoding="utf-8")
            self.assertIn("Laplace.Core.Tests.csproj", text)
            self.assertNotIn("Laplace.Chess.Tests.csproj", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
