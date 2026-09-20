#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "ci-impact-plan.py"


def plan(*paths: str) -> dict:
    command = [sys.executable, str(SCRIPT)]
    for path in paths:
        command += ["--changed-file", path]
    result = subprocess.run(command, cwd=ROOT, check=True, text=True, capture_output=True)
    return json.loads(result.stdout)


class ImpactPlanTests(unittest.TestCase):
    def test_native_suite_executes_ctest_with_planned_case_filter(self):
        with tempfile.TemporaryDirectory(prefix="native-filter-") as tmp:
            tools = Path(tmp)
            log = tools / "calls.json"
            python = tools / "python3"
            python.write_text(
                "#!/usr/bin/python3\n"
                "import json,os,sys\n"
                "open(os.environ['CALL_LOG'],'w').write(json.dumps(sys.argv[1:]))\n"
            )
            python.chmod(0o755)
            planned = r"LaplaceDynamicsProcrustes\.RecoversScale"
            env = dict(
                os.environ,
                PATH=str(tools) + os.pathsep + os.environ["PATH"],
                CALL_LOG=str(log),
                LAPLACE_NATIVE_TEST_FILTER=planned,
                LAPLACE_WORK_ROOT=str(tools / "work"),
            )
            result = subprocess.run(
                ["bash", "scripts/test-suites/native-dev.sh"],
                cwd=ROOT,
                env=env,
                text=True,
                capture_output=True,
                timeout=10,
            )
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            command = json.loads(log.read_text())
            self.assertEqual(command[command.index("-R") + 1], planned)
            self.assertIn("-LE", command)
            self.assertNotIn("regress_laplace_substrate", command)

    def test_web_only_change_qualifies_and_publishes_without_native_or_database_mutation(self):
        value = plan("web/src/App.tsx")
        self.assertEqual(value["components"], ["web"])
        self.assertEqual(value["build_components"], ["web"])
        self.assertEqual(value["managed_build_projects"], [])
        self.assertEqual(value["managed_test_projects"], [])
        self.assertEqual(value["managed_test_filter"], "")
        self.assertEqual(value["dev_suites"], ["browser-dev"])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["delivery_actions"], ["publish"])
        self.assertEqual(value["publish_scope"], "web")
        self.assertEqual(value["live_suites"], [])
        self.assertFalse(value["full_qualification"])

    def test_native_change_invalidates_native_managed_db_and_full_live(self):
        value = plan("engine/core/src/example.cpp")
        self.assertEqual(value["components"], ["database", "managed", "native", "uci"])
        self.assertEqual(value["build_components"], ["managed", "native"])
        self.assertNotEqual(value["managed_build_projects"], ["all"])
        for project in (
            "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
            "app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj",
            "app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj",
            "app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj",
            "app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj",
        ):
            self.assertIn(project, value["managed_build_projects"])
        self.assertEqual(
            value["managed_test_projects"],
            [
                "app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj",
                "app/Laplace.Core.Tests/Laplace.Core.Tests.csproj",
                "app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj",
            ],
        )
        self.assertNotEqual(value["managed_test_projects"], ["all"])
        self.assertEqual(
            value["managed_db_test_projects"],
            [
                "app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj",
                "app/Laplace.Core.Tests/Laplace.Core.Tests.csproj",
                "app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj",
            ],
        )
        self.assertEqual(value["managed_live_test_projects"], [])
        self.assertEqual(
            value["dev_suites"], ["native-dev", "managed-dev", "uci-dev"]
        )
        self.assertEqual(
            value["db_suites"], ["db-health", "native-db", "managed-db"]
        )
        self.assertEqual(
            value["delivery_actions"],
            ["install", "database", "reconcile", "publish"],
        )
        self.assertEqual(value["publish_scope"], "full")
        self.assertEqual(value["live_suites"], [])
        self.assertFalse(value["full_qualification"])

    def test_managed_api_change_avoids_native_install_but_runs_product_live_checks(self):
        value = plan("app/Laplace.Endpoints.OpenAICompat/Foo.cs")
        self.assertEqual(value["dev_suites"], ["managed-dev", "browser-dev"])
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
            value["managed_build_projects"],
        )
        self.assertEqual(
            value["managed_test_projects"],
            ["app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj"],
        )
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["delivery_actions"], ["publish"])
        self.assertEqual(value["publish_scope"], "api")
        self.assertEqual(value["live_suites"], [])

    def test_substrate_managed_change_adds_database_prepare_and_regression_without_native_install(self):
        value = plan("app/Laplace.Substrate/Crud/Npgsql/Foo.cs")
        self.assertIn("managed-dev", value["dev_suites"])
        self.assertEqual(
            value["db_suites"], ["db-health", "managed-db"]
        )
        self.assertEqual(
            value["delivery_actions"], ["database", "reconcile", "publish"]
        )
        self.assertNotIn("install", value["delivery_actions"])
        self.assertEqual(value["publish_scope"], "full")

    def test_shared_managed_library_requires_full_publication(self):
        value = plan("app/Laplace.Core/Core/Foo.cs")
        self.assertEqual(value["build_components"], ["managed"])
        self.assertNotEqual(value["managed_build_projects"], ["all"])
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
            value["managed_build_projects"],
        )
        self.assertIn(
            "app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj",
            value["managed_build_projects"],
        )
        self.assertEqual(value["publish_scope"], "full")
        self.assertEqual(value["delivery_actions"], ["publish"])

    def test_uci_executable_change_isolated_from_api_database_and_live_matrix(self):
        value = plan("app/Laplace.Chess.Uci/Program.cs")
        self.assertEqual(value["components"], ["managed", "uci"])
        self.assertEqual(value["build_components"], ["managed"])
        self.assertEqual(
            value["managed_build_projects"],
            [
                "app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj",
                "app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj",
            ],
        )
        self.assertEqual(
            value["managed_test_projects"],
            ["app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj"],
        )
        self.assertEqual(value["dev_suites"], ["managed-dev", "uci-dev"])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["live_suites"], [])
        self.assertEqual(value["delivery_actions"], ["publish"])
        self.assertEqual(value["publish_scope"], "uci")
        self.assertFalse(value["full_qualification"])

    def test_chess_change_keeps_full_publication_and_uci_qualification(self):
        value = plan("app/Laplace.Chess/Service/Foo.cs")
        self.assertIn("managed-dev", value["dev_suites"])
        self.assertIn("uci-dev", value["dev_suites"])
        self.assertEqual(value["publish_scope"], "full")
        self.assertNotIn("native-dev", value["dev_suites"])
        self.assertEqual(value["live_suites"], [])

    def test_database_sql_change_skips_native_install_but_runs_db_and_full_live(self):
        value = plan("db/migrations/example.sql")
        self.assertEqual(value["dev_suites"], [])
        self.assertEqual(
            value["db_suites"], ["db-health", "managed-db"]
        )
        self.assertEqual(value["build_components"], ["managed"])
        self.assertNotEqual(value["managed_build_projects"], ["all"])
        self.assertIn(
            "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
            value["managed_build_projects"],
        )
        self.assertEqual(
            value["delivery_actions"], ["database", "reconcile", "publish"]
        )
        self.assertEqual(value["publish_scope"], "api")

    def test_unknown_production_path_fails_safe_to_everything(self):
        value = plan("mystery/runtime.dat")
        self.assertEqual(
            value["dev_suites"],
            ["native-dev", "managed-dev", "uci-dev", "browser-dev"],
        )
        self.assertEqual(value["build_components"], ["managed", "native", "web"])
        self.assertEqual(value["managed_build_projects"], ["all"])
        self.assertEqual(value["managed_test_projects"], ["all"])
        self.assertEqual(value["managed_db_test_projects"], ["all"])
        self.assertEqual(value["managed_live_test_projects"], [])
        self.assertEqual(
            value["db_suites"], ["db-health", "native-db", "managed-db"]
        )
        self.assertEqual(
            value["delivery_actions"],
            ["install", "database", "reconcile", "publish"],
        )
        self.assertEqual(value["live_suites"], [])
        self.assertEqual(value["publish_scope"], "full")
        self.assertTrue(value["full_qualification"])
        self.assertEqual(value["unknown_paths"], ["mystery/runtime.dat"])

    def test_docs_and_workflow_paths_do_not_create_product_work(self):
        value = plan("docs/README.md", ".github/workflows/foo.yml")
        self.assertEqual(value["dev_suites"], [])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["live_suites"], [])
        self.assertEqual(value["delivery_actions"], [])
        self.assertEqual(value["components"], [])
        self.assertEqual(value["build_components"], [])
        self.assertFalse(value["full_qualification"])

    def test_ci_policy_script_does_not_create_product_work(self):
        value = plan("scripts/test-workflow-architecture.py")
        self.assertEqual(value["components"], [])
        self.assertEqual(value["build_components"], [])
        self.assertEqual(value["dev_suites"], [])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["live_suites"], [])
        self.assertEqual(value["delivery_actions"], [])
        self.assertFalse(value["full_qualification"])
        self.assertEqual(value["ignored_paths"], ["scripts/test-workflow-architecture.py"])

    def test_test_harness_change_does_not_create_product_work(self):
        value = plan("scripts/test-parallel.sh")
        self.assertEqual(value["components"], [])
        self.assertEqual(value["build_components"], [])
        self.assertEqual(value["dev_suites"], [])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["live_suites"], [])
        self.assertEqual(value["delivery_actions"], [])
        self.assertFalse(value["full_qualification"])
        self.assertEqual(value["ignored_paths"], ["scripts/test-parallel.sh"])

    def test_pipeline_and_delivery_control_changes_create_no_product_work(self):
        for path in (
            "scripts/pipeline.sh",
            "scripts/bootstrap-chess-lab.sh",
            "scripts/verify-application-release.py",
            "scripts/ingest-source.sh",
            "scripts/check-substrate-floor.sh",
            "scripts/ensure-foundation.sh",
        ):
            with self.subTest(path=path):
                value = plan(path)
                self.assertEqual(value["managed_test_projects"], [])
                self.assertEqual(value["managed_build_projects"], [])
                if path in (
                    "scripts/pipeline.sh",
                    "scripts/bootstrap-chess-lab.sh",
                    "scripts/verify-application-release.py",
                ):
                    self.assertEqual(value["delivery_actions"], [])
                    self.assertIn(path, value["ignored_paths"])
                else:
                    self.assertEqual(value["delivery_actions"], ["install"])
                self.assertNotIn("publish", value["delivery_actions"])
                self.assertNotIn("managed-dev", value["dev_suites"])
                self.assertEqual(value["live_suites"], [])
                self.assertFalse(value["full_qualification"])

        value = plan("scripts/check-deployed-revision.sh")
        self.assertEqual(value["delivery_actions"], [])
        self.assertEqual(value["build_components"], [])
        self.assertIn("scripts/check-deployed-revision.sh", value["ignored_paths"])

    def test_extension_sql_does_not_schedule_the_managed_ocean(self):
        value = plan(
            "extension/laplace_substrate/sql/functions/ops/ingest_run_close.sql.in"
        )
        self.assertEqual(value["dev_suites"], ["native-dev"])
        self.assertEqual(value["managed_test_projects"], [])
        self.assertEqual(value["managed_test_filter"], "")
        self.assertIn("native", value["build_components"])
        self.assertNotIn("managed-dev", value["dev_suites"])
        self.assertIn("install", value["delivery_actions"])
        self.assertFalse(value["full_qualification"])

    def test_native_test_change_runs_native_qualification_without_delivery(self):
        for path in (
            "engine/core/tests/test_content_root_placement.cpp",
            "extension/laplace_substrate/tests/physicality_descriptor_native_probe.c",
        ):
            with self.subTest(path=path):
                value = plan(path)
                self.assertEqual(value["components"], [])
                self.assertEqual(value["build_components"], ["native"])
                self.assertEqual(value["dev_suites"], ["native-dev"])
                self.assertEqual(value["db_suites"], [])
                self.assertEqual(value["live_suites"], [])
                self.assertEqual(value["delivery_actions"], [])
                self.assertEqual(value["managed_build_projects"], [])
                self.assertFalse(value["full_qualification"])
                self.assertTrue(value["native_test_filter"])

    def test_native_component_change_selects_only_that_components_ctest_cases(self):
        value = plan("engine/dynamics/src/procrustes.cpp")
        selected = value["native_test_filter"]
        self.assertIn("LaplaceDynamicsProcrustes", selected)
        self.assertNotIn("LaplaceSynthesis", selected)
        self.assertNotIn("Hash128", selected)

    def test_extension_change_selects_only_its_regression_suite(self):
        value = plan("extension/laplace_geom/src/laplace_geom.c")
        self.assertEqual(value["native_db_test_filter"], "^(?:regress_laplace_geom)$")

    def test_db_tier_test_change_selects_exact_project_and_class(self):
        path = "app/Laplace.Substrate.Tests/Ingestion/SyntheticDecomposerTests.cs"
        value = plan(path)
        self.assertEqual(value["dev_suites"], [])
        self.assertEqual(value["db_suites"], ["managed-db"])
        self.assertEqual(
            value["managed_db_test_projects"],
            ["app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj"],
        )
        self.assertIn("SyntheticDecomposerTests", value["managed_db_test_filter"])

    def test_test_project_change_qualifies_only_that_managed_project(self):
        target = "app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj"
        value = plan(
            "app/Laplace.Substrate.Tests/Abstractions/DecomposerArchitectureGateTests.cs"
        )
        self.assertEqual(value["components"], [])
        self.assertEqual(value["build_components"], ["managed"])
        self.assertEqual(value["managed_build_projects"], [target])
        self.assertEqual(value["managed_test_projects"], [target])
        self.assertEqual(
            value["managed_test_filter"],
            "FullyQualifiedName~Laplace.Decomposers.Abstractions.Tests.DecomposerArchitectureGateTests",
        )
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["live_suites"], [])
        self.assertEqual(value["delivery_actions"], [])
        self.assertFalse(value["full_qualification"])

    def test_mixed_policy_and_web_change_only_invalidates_web(self):
        value = plan("scripts/ci-impact-plan.py", "web/src/App.tsx")
        self.assertEqual(value["components"], ["web"])
        self.assertEqual(value["build_components"], ["web"])
        self.assertEqual(value["managed_build_projects"], [])
        self.assertEqual(value["dev_suites"], ["browser-dev"])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["delivery_actions"], ["publish"])
        self.assertEqual(value["publish_scope"], "web")
        self.assertIn("scripts/ci-impact-plan.py", value["ignored_paths"])

    def test_git_diff_includes_deleted_production_files(self):
        import tempfile
        with tempfile.TemporaryDirectory(prefix="impact-delete-") as tmp:
            repo = Path(tmp)
            (repo / "web/src").mkdir(parents=True)
            target = repo / "web/src/Removed.tsx"
            target.write_text("export const removed = 1;\n", encoding="utf-8")
            subprocess.run(["git", "init", "-q"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.email", "ci@example.invalid"], cwd=repo, check=True)
            subprocess.run(["git", "config", "user.name", "CI"], cwd=repo, check=True)
            subprocess.run(["git", "add", "."], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "base"], cwd=repo, check=True)
            base = subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=repo, check=True, text=True, capture_output=True
            ).stdout.strip()
            target.unlink()
            subprocess.run(["git", "add", "-u"], cwd=repo, check=True)
            subprocess.run(["git", "commit", "-qm", "delete"], cwd=repo, check=True)
            head = subprocess.run(
                ["git", "rev-parse", "HEAD"], cwd=repo, check=True, text=True, capture_output=True
            ).stdout.strip()

            result = subprocess.run(
                [
                    sys.executable, str(SCRIPT),
                    "--root", str(repo),
                    "--base", base,
                    "--head", head,
                ],
                check=True, text=True, capture_output=True,
            )
            value = json.loads(result.stdout)
            self.assertEqual(value["changed_files"], ["web/src/Removed.tsx"])
            self.assertIn("browser-dev", value["dev_suites"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
