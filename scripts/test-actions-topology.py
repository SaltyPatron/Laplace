#!/usr/bin/env python3
"""Architecture tests for GitHub Actions.

These tests deliberately avoid freezing every workflow line and every internal
phase as policy. They protect the boundaries that matter: ordinary pushes are
development validation, database lifecycle is structural, ingestion is explicit,
and exceptional maintenance is not repeated on every commit.
"""
from __future__ import annotations

from pathlib import Path
import os
import re
import subprocess
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
MAIN = WORKFLOWS / "laplace.yml"
PRODUCT = ROOT / "scripts" / "product-ci.sh"


def load(path: Path):
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def triggers(workflow: dict) -> set[str]:
    value = workflow.get("on", {})
    return {value} if isinstance(value, str) else set(value or {})


def uses(value):
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "uses" and isinstance(child, str):
                yield child
            yield from uses(child)
    elif isinstance(value, list):
        for child in value:
            yield from uses(child)


class ActionsArchitectureTests(unittest.TestCase):
    def product_plan(self, stage: str, **environment: str) -> list[str]:
        result = subprocess.run(
            ["bash", str(PRODUCT), stage, "--list-phases"],
            cwd=ROOT,
            env={**os.environ, "LAPLACE_GENERATION_BENCHMARK": "", **environment},
            capture_output=True,
            text=True,
            check=True,
            timeout=10,
        )
        return result.stdout.splitlines()

    def test_main_push_is_development_validation_not_delivery(self):
        plan = self.product_plan("all", GITHUB_EVENT_NAME="push")
        self.assertEqual(
            ["dependencies", "build", "native-dev", "managed-dev", "uci-dev", "browser-dev"],
            plan,
        )
        for forbidden in (
            "policy", "native-install", "database-maintenance", "foundation", "lexical-foundation",
            "operational-seed", "publish", "operational-execution", "db-health",
            "native-db", "managed-db", "live-floor", "performance",
        ):
            self.assertNotIn(forbidden, plan)

    def test_push_dependency_phase_is_read_only(self):
        source = PRODUCT.read_text(encoding="utf-8")
        block = source.split("run_deps() {", 1)[1].split("\n}", 1)[0]
        self.assertIn('"${GITHUB_EVENT_NAME:-}" == "push"', block)
        self.assertIn("bash scripts/ci-deps.sh --check-only", block)
        self.assertIn("bash scripts/ci-deps.sh", block)

    def test_operator_all_remains_explicit_and_separate_from_push(self):
        plan = self.product_plan("all", GITHUB_EVENT_NAME="workflow_dispatch")
        for required in ("policy", "native-install", "database-maintenance", "publish", "db-health"):
            self.assertIn(required, plan)
        self.assertGreater(len(plan), len(self.product_plan("all", GITHUB_EVENT_NAME="push")))

    def test_source_only_push_does_not_start_policy_campaign_or_host_work(self):
        result = subprocess.run(
            ["bash", str(PRODUCT), "check"],
            cwd=ROOT,
            env={**os.environ, "GITHUB_EVENT_NAME": "push"},
            capture_output=True,
            text=True,
            timeout=20,
        )
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("source-only syntax check passed", result.stdout)

    def test_database_recreate_restores_a_usable_product(self):
        db = load(WORKFLOWS / "db-ops.yml")
        self.assertEqual({"workflow_dispatch"}, triggers(db))
        self.assertEqual("laplace-substrate-lifecycle", db["concurrency"]["group"])
        inputs = db["on"]["workflow_dispatch"]["inputs"]
        self.assertIn("restore_foundation", inputs)
        steps = db["jobs"]["db"]["steps"]
        install = next(step for step in steps if step.get("name") == "Build and install the exact runtime used by recreation")
        self.assertIn("pipeline.sh build install", install["run"])
        recreate = next(step for step in steps if step.get("name") == "Recreate database structure and runtime")
        command = recreate["run"]
        self.assertIn("--fresh-db migrate sync-extension tune-pg tune-laplace perfcache-guc api-env", command)
        self.assertIn("check-database-health.sh", command)
        commands = "\n".join(step.get("run", "") for step in steps)
        self.assertIn("ensure-foundation.sh --required-lexical", commands)
        self.assertIn("check-substrate-floor.sh", commands)

    def test_seed_workflows_are_explicit_not_source_triggered(self):
        for path in sorted(WORKFLOWS.glob("seed-*.yml")):
            event = triggers(load(path))
            with self.subTest(path=path.name):
                self.assertFalse(event & {"push", "pull_request"})
                self.assertTrue(event & {"workflow_dispatch", "workflow_call"})

    def test_branch_consolidation_is_manual_and_does_not_upload_recovery_bundles(self):
        hygiene = load(WORKFLOWS / "repo-hygiene.yml")
        job = hygiene["jobs"]["integrated-branches"]
        self.assertEqual("github.event_name == 'workflow_dispatch'", job["if"])
        self.assertFalse(any(str(use).startswith("actions/upload-artifact@") for use in uses(job)))

    def test_main_separates_development_and_operator_ownership(self):
        workflow = load(MAIN)
        self.assertEqual(["development", "operator"], list(workflow["jobs"]))
        self.assertEqual("laplace-substrate-lifecycle", workflow["jobs"]["operator"]["concurrency"]["group"])
        source = MAIN.read_text(encoding="utf-8")
        self.assertIn("LAPLACE_FAST_ONLY", source)
        self.assertIn("bash scripts/product-ci.sh check", source)

    def test_external_actions_are_commit_pinned(self):
        pattern = re.compile(r"^[^@\s]+@[0-9a-f]{40}$")
        for path in sorted(WORKFLOWS.glob("*.yml")):
            workflow = load(path)
            for use in uses(workflow):
                if use.startswith("./"):
                    continue
                with self.subTest(path=path.name, use=use):
                    self.assertRegex(use, pattern)

    def test_no_seed_workflow_is_a_delivery_dependency(self):
        main = load(MAIN)
        text = str(main.get("jobs") or {})
        for path in WORKFLOWS.glob("seed-*.yml"):
            self.assertNotIn("./.github/workflows/" + path.name, text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
