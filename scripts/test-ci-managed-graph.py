#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "ci_managed_graph.py"
SPEC = importlib.util.spec_from_file_location("ci_managed_graph", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class ManagedGraphTests(unittest.TestCase):
    def plan(self, *paths: str):
        return MODULE.plan(ROOT, list(paths))

    def test_openai_endpoint_change_selects_only_openai_managed_test_project(self):
        value = self.plan("app/Laplace.Endpoints.OpenAICompat/CodeEndpoints.cs")
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj",
            value["test_projects"],
        )
        self.assertNotIn(
            "app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj",
            value["test_projects"],
        )
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
            value["build_projects"],
        )

    def test_chess_change_selects_chess_and_downstream_endpoint_tests(self):
        value = self.plan("app/Laplace.Chess/ChessService.cs")
        self.assertIn(
            "app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj",
            value["test_projects"],
        )
        self.assertIn(
            "app/Laplace.Endpoints.Lichess.Tests/Laplace.Endpoints.Lichess.Tests.csproj",
            value["test_projects"],
        )
        self.assertNotIn(
            "app/Laplace.Cli.Tests/Laplace.Cli.Tests.csproj",
            value["test_projects"],
        )

    def test_core_change_legitimately_fans_out_through_reverse_dependencies(self):
        value = self.plan("app/Laplace.Core/Core/Hash128.cs")
        self.assertGreater(len(value["test_projects"]), 3)
        self.assertIn(
            "app/Laplace.Core.Tests/Laplace.Core.Tests.csproj",
            value["test_projects"],
        )
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj",
            value["test_projects"],
        )

    def test_test_project_change_selects_only_that_test_and_its_dependents(self):
        value = self.plan("app/Laplace.Cli.Tests/CliTests.cs")
        self.assertEqual(
            value["test_projects"],
            ["app/Laplace.Cli.Tests/Laplace.Cli.Tests.csproj"],
        )

    def test_shared_managed_configuration_forces_all_projects(self):
        value = self.plan("Directory.Packages.props")
        self.assertTrue(value["force_all"])
        discovered = MODULE.discover(ROOT)
        self.assertEqual(
            value["test_projects"],
            sorted(k for k, v in discovered.items() if v["is_test"]),
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
