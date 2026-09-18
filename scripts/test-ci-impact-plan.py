#!/usr/bin/env python3
from __future__ import annotations

import json
import subprocess
import sys
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
    def test_web_only_change_qualifies_and_publishes_without_native_or_database_mutation(self):
        value = plan("web/src/App.tsx")
        self.assertEqual(value["components"], ["web"])
        self.assertEqual(value["build_components"], ["web"])
        self.assertEqual(value["dev_suites"], ["browser-dev"])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["delivery_actions"], ["publish", "live"])
        self.assertEqual(value["publish_scope"], "api")
        self.assertEqual(value["live_suites"], ["live-floor", "live-api"])
        self.assertFalse(value["full_qualification"])

    def test_native_change_invalidates_native_managed_db_and_full_live(self):
        value = plan("engine/core/src/example.cpp")
        self.assertEqual(value["components"], ["database", "managed", "native", "uci"])
        self.assertEqual(value["build_components"], ["managed", "native"])
        self.assertEqual(
            value["dev_suites"], ["native-dev", "managed-dev", "uci-dev"]
        )
        self.assertEqual(
            value["db_suites"], ["db-health", "native-db", "managed-db"]
        )
        self.assertEqual(
            value["delivery_actions"],
            ["install", "database", "reconcile", "publish", "live"],
        )
        self.assertEqual(value["publish_scope"], "full")
        self.assertEqual(
            value["live_suites"],
            ["live-floor", "live-api", "managed-live", "generation-eval"],
        )
        self.assertFalse(value["full_qualification"])

    def test_managed_api_change_avoids_native_install_but_runs_product_live_checks(self):
        value = plan("app/Laplace.Endpoints.OpenAICompat/Foo.cs")
        self.assertEqual(value["dev_suites"], ["managed-dev", "browser-dev"])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["delivery_actions"], ["publish", "live"])
        self.assertEqual(value["publish_scope"], "api")
        self.assertEqual(
            value["live_suites"],
            ["live-floor", "live-api", "managed-live", "generation-eval"],
        )

    def test_substrate_managed_change_adds_database_prepare_and_regression_without_native_install(self):
        value = plan("app/Laplace.Substrate/Crud/Npgsql/Foo.cs")
        self.assertIn("managed-dev", value["dev_suites"])
        self.assertEqual(
            value["db_suites"], ["db-health", "managed-db"]
        )
        self.assertEqual(
            value["delivery_actions"], ["database", "reconcile", "publish", "live"]
        )
        self.assertNotIn("install", value["delivery_actions"])
        self.assertEqual(value["publish_scope"], "full")

    def test_shared_managed_library_requires_full_publication(self):
        value = plan("app/Laplace.Core/Core/Foo.cs")
        self.assertEqual(value["build_components"], ["managed"])
        self.assertEqual(value["publish_scope"], "full")
        self.assertEqual(value["delivery_actions"], ["publish", "live"])

    def test_chess_change_keeps_full_publication_and_uci_qualification(self):
        value = plan("app/Laplace.Chess/Service/Foo.cs")
        self.assertIn("managed-dev", value["dev_suites"])
        self.assertIn("uci-dev", value["dev_suites"])
        self.assertEqual(value["publish_scope"], "full")
        self.assertNotIn("native-dev", value["dev_suites"])

    def test_database_sql_change_skips_native_install_but_runs_db_and_full_live(self):
        value = plan("db/migrations/example.sql")
        self.assertEqual(value["dev_suites"], [])
        self.assertEqual(
            value["db_suites"], ["db-health", "managed-db"]
        )
        self.assertEqual(value["build_components"], ["managed"])
        self.assertEqual(
            value["delivery_actions"], ["database", "reconcile", "publish", "live"]
        )
        self.assertEqual(value["publish_scope"], "api")

    def test_unknown_production_path_fails_safe_to_everything(self):
        value = plan("mystery/runtime.dat")
        self.assertEqual(
            value["dev_suites"],
            ["native-dev", "managed-dev", "uci-dev", "browser-dev"],
        )
        self.assertEqual(value["build_components"], ["managed", "native", "web"])
        self.assertEqual(
            value["db_suites"], ["db-health", "native-db", "managed-db"]
        )
        self.assertEqual(
            value["delivery_actions"],
            ["install", "database", "reconcile", "publish", "live"],
        )
        self.assertEqual(
            value["live_suites"],
            ["live-floor", "live-api", "managed-live", "generation-eval"],
        )
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

    def test_mixed_policy_and_web_change_only_invalidates_web(self):
        value = plan("scripts/ci-impact-plan.py", "web/src/App.tsx")
        self.assertEqual(value["components"], ["web"])
        self.assertEqual(value["build_components"], ["web"])
        self.assertEqual(value["dev_suites"], ["browser-dev"])
        self.assertEqual(value["db_suites"], [])
        self.assertEqual(value["delivery_actions"], ["publish", "live"])
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
