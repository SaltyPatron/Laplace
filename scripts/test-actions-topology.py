#!/usr/bin/env python3
"""Executable regression tests for Actions delivery and QA authority."""
from __future__ import annotations

from pathlib import Path
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
MAIN = WORKFLOWS / "laplace.yml"


def load(path: Path = MAIN):
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def jobs(path: Path = MAIN):
    return load(path)["jobs"]


def commands(job: dict) -> str:
    return "\n".join(step.get("run", "") for step in job.get("steps", []))


class ActionsAuthorityTests(unittest.TestCase):
    def test_single_runner_fails_policy_before_dependency_work_and_does_not_repeat_it(self):
        graph = jobs()
        self.assertEqual("policy", graph["deps"]["needs"])
        self.assertEqual("deps", graph["build"]["needs"])
        unit = commands(graph["unit-test"])
        self.assertEqual(1, unit.count("test-parallel.sh --engine"))
        for policy_only in (
            "test-pg-machine-tuning.py",
            "test-application-runtime.py",
            "test-application-publish.py",
            "test-actions-topology.py",
        ):
            self.assertNotIn(policy_only, unit)
        proof = (ROOT / "scripts/pr-proof.sh").read_text(encoding="utf-8")
        self.assertEqual(1, proof.count("bash scripts/ci-policy.sh"))
        self.assertNotIn("python3 scripts/test-application-publish.py", proof)

    def test_delivery_commits_before_environment_qa(self):
        graph = jobs()
        publish = graph["publish"]
        integration = graph["integration-test"]
        smoke = graph["smoke"]
        restore = graph["restore-api"]

        self.assertEqual("db-ops", publish["needs"])
        self.assertIn("needs.db-ops.result == 'success'", publish["if"])
        self.assertNotIn("integration-test", publish["if"])
        self.assertIn("publish-applications.sh deploy", commands(publish))
        self.assertIn("check-database-health.sh", commands(graph["db-ops"]))

        self.assertEqual(["db-ops", "publish"], integration["needs"])
        self.assertIn("needs.publish.result == 'success'", integration["if"])
        self.assertIn("inputs.stage == 'integrate'", integration["if"])
        self.assertIn("test-parallel.sh --integration", commands(integration))

        self.assertEqual(["publish", "integration-test"], smoke["needs"])
        self.assertIn("always()", smoke["if"])
        self.assertIn("needs.publish.result == 'success'", smoke["if"])
        self.assertNotIn("needs.integration-test.result == 'success'", smoke["if"])

        self.assertEqual(["deploy", "publish"], restore["needs"])
        self.assertIn("needs.publish.result != 'success'", restore["if"])
        self.assertNotIn("SMOKE_RESULT", commands(restore))
        self.assertNotIn("EVAL_RESULT", commands(restore))

    def test_dev_db_and_product_profiles_control_execution(self):
        graph = jobs()
        unit = commands(graph["unit-test"])
        integration = commands(graph["integration-test"])
        smoke = commands(graph["smoke"])
        self.assertEqual(1, unit.count("test-parallel.sh --engine"))
        self.assertIn("test-parallel.sh --integration", integration)
        self.assertNotIn("Tier=live", integration)
        self.assertIn("Tier=live", smoke)
        self.assertNotIn("what does dog mean?", integration)

    def test_empty_database_is_delivered_then_fails_the_named_product_floor(self):
        graph = jobs()
        publish = graph["publish"]
        smoke = graph["smoke"]
        self.assertIn(".application-release-state.json", commands(publish))
        self.assertNotIn("check-substrate-floor.sh", commands(publish))
        self.assertIn("needs.publish.result == 'success'", smoke["if"])
        self.assertNotIn("needs.publish.outputs.has_data", smoke["if"])
        self.assertIn("check-substrate-floor.sh", commands(smoke))
        self.assertIn("needs.publish.outputs.product_ready == 'true'", graph["eval"]["if"])

    def test_no_post_run_delivery_workaround_remains(self):
        self.assertFalse((WORKFLOWS / "application-delivery.yml").exists())
        self.assertFalse((ROOT / "scripts/application-delivery-source.py").exists())
        for path in WORKFLOWS.glob("*.yml"):
            trigger = load(path).get("on", {})
            names = {trigger} if isinstance(trigger, str) else set(trigger)
            self.assertNotIn("workflow_run", names, path.name)

    def test_pull_request_proof_is_source_build_only_same_repository_and_cannot_replace_main(self):
        path = WORKFLOWS / "pr-validation.yml"
        workflow = load(path)
        prove = workflow["jobs"]["prove"]
        self.assertIn("pull_request", workflow["on"])
        self.assertIn("head.repo.full_name == github.repository", prove["if"])
        source = path.read_text(encoding="utf-8")
        self.assertNotIn("${{ secrets.", source)
        command = commands(prove)
        self.assertIn("scripts/pr-proof.sh", command)
        for forbidden in ("pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh deploy", "sudo "):
            self.assertNotIn(forbidden, command)

        proof = (ROOT / "scripts/pr-proof.sh").read_text(encoding="utf-8")
        self.assertNotIn("publish-applications.sh check", proof)
        self.assertNotIn("check-application-runtime.py", proof)
        for forbidden in (
            "pipeline.sh install",
            "pipeline.sh migrate",
            "sync-extension",
            "publish-applications.sh deploy",
            "systemctl ",
            "sudo ",
            "--fresh-db",
        ):
            self.assertNotIn(forbidden, proof)

        main_concurrency = load()["concurrency"]
        self.assertEqual("laplace-shared-workspace", main_concurrency["group"])
        self.assertEqual("false", main_concurrency["cancel-in-progress"])
        pr_concurrency = workflow["concurrency"]
        self.assertEqual("laplace-shared-workspace", pr_concurrency["group"])
        self.assertEqual("false", pr_concurrency["cancel-in-progress"])
        self.assertEqual("max", pr_concurrency["queue"])

    def test_manual_mutation_workflows_are_not_source_triggered(self):
        paths = [WORKFLOWS / "db-ops.yml", *sorted(WORKFLOWS.glob("seed-*.yml"))]
        for path in paths:
            with self.subTest(path=path.name):
                trigger = load(path)["on"]
                names = {trigger} if isinstance(trigger, str) else set(trigger)
                self.assertFalse(names & {"push", "pull_request"})
                self.assertTrue(names & {"workflow_dispatch", "workflow_call"})

    def test_all_external_actions_are_commit_pinned(self):
        import re
        for path in WORKFLOWS.glob("*.yml"):
            for line in path.read_text(encoding="utf-8").splitlines():
                if "uses:" not in line or "./" in line:
                    continue
                use = line.split("uses:", 1)[1].strip()
                self.assertRegex(use, r"^[^@\s]+@[0-9a-f]{40}$", path.name)


if __name__ == "__main__":
    unittest.main(verbosity=2)
