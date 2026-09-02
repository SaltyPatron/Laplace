#!/usr/bin/env python3
"""Executable regression tests for Actions delivery and test-profile authority."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
MAIN = WORKFLOWS / "laplace.yml"
REGISTRY = ROOT / "scripts" / "test-profiles.json"
BENCHMARK_REGISTRY = ROOT / "scripts" / "benchmark-profiles.json"


def load(path: Path = MAIN):
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def jobs(path: Path = MAIN):
    return load(path)["jobs"]


def commands(job: dict) -> str:
    return "\n".join(step.get("run", "") for step in job.get("steps", []))


def suites():
    return {suite["id"]: suite for suite in json.loads(REGISTRY.read_text())["suites"]}


def benchmark_module():
    path = ROOT / "scripts" / "benchmark_suite.py"
    spec = importlib.util.spec_from_file_location("benchmark_suite_contract", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ActionsAuthorityTests(unittest.TestCase):
    def test_single_runner_uses_profile_authority_before_dependency_work(self):
        graph = jobs()
        self.assertEqual("policy", graph["deps"]["needs"])
        self.assertEqual("deps", graph["build"]["needs"])
        self.assertEqual(1, commands(graph["policy"]).count("bash scripts/ci-policy.sh"))
        self.assertEqual(1, commands(graph["unit-test"]).count("test-parallel.sh --engine"))
        policy_alias = (ROOT / "scripts/ci-policy.sh").read_text(encoding="utf-8")
        self.assertIn("test-profile-registry.py run --profile policy", policy_alias)
        self.assertNotIn("test-managed-host.py", policy_alias)
        proof = (ROOT / "scripts/pr-proof.sh").read_text(encoding="utf-8")
        self.assertEqual(1, proof.count("test-parallel.sh --policy"))
        self.assertEqual(1, proof.count("test-parallel.sh --engine"))
        self.assertNotIn("bash scripts/ci-policy.sh", proof)

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

    def test_dev_db_live_and_perf_profiles_control_execution(self):
        graph = jobs()
        self.assertEqual(1, commands(graph["unit-test"]).count("test-parallel.sh --engine"))
        self.assertEqual(1, commands(graph["integration-test"]).count("test-parallel.sh --integration"))
        self.assertEqual(1, commands(graph["smoke"]).count("test-parallel.sh --app-live"))
        self.assertEqual(1, commands(graph["eval"]).count("test-parallel.sh --perf"))
        self.assertIn("inputs.generation_benchmark == true", graph["eval"]["if"])

        source = MAIN.read_text(encoding="utf-8")
        for direct in (
            "dotnet test ", "ctest ", "npm run test:", "npx playwright",
            "test-uci-publish.py", "test-cutechess-runtime.py", "eval-generation.py",
            "verify-generation.py --api",
        ):
            self.assertNotIn(direct, source)

    def test_empty_database_is_delivered_then_fails_the_named_product_floor(self):
        graph = jobs()
        publish = graph["publish"]
        smoke = graph["smoke"]
        self.assertIn(".application-release-state.json", commands(publish))
        self.assertNotIn("check-substrate-floor.sh", commands(publish))
        self.assertIn("needs.publish.result == 'success'", smoke["if"])
        self.assertNotIn("needs.publish.outputs.has_data", smoke["if"])
        self.assertIn("test-parallel.sh --app-live", commands(smoke))
        self.assertIn("check-substrate-floor.sh", str(suites()["live-floor"]["command"]))
        self.assertIn("needs.publish.outputs.product_ready == 'true'", graph["eval"]["if"])

    def test_registry_owns_previous_direct_dev_live_and_policy_commands(self):
        registered = suites()
        self.assertIn("test-uci-publish.py", str(registered["uci-dev"]["command"]))
        self.assertIn("test-cutechess-runtime.py", str(registered["uci-dev"]["command"]))
        self.assertIn("npm run typecheck", str(registered["browser-dev"]["command"]))
        self.assertIn("npm run test:chess-ui", str(registered["browser-dev"]["command"]))
        self.assertEqual("Tier=live", registered["managed-live"]["selector"]["filter"])
        self.assertIn("eval-generation.py", str(registered["generation-eval"]["command"]))
        self.assertIn("verify-generation.py", str(registered["generation-perf"]["command"]))
        self.assertIn("test-test-profile-registry.py", str(registered["policy-registry"]["command"]))
        self.assertIn("test-managed-host.py", str(registered["policy-managed-host"]["command"]))

    def test_no_post_run_delivery_workaround_remains(self):
        self.assertFalse((WORKFLOWS / "application-delivery.yml").exists())
        self.assertFalse((ROOT / "scripts/application-delivery-source.py").exists())
        for path in WORKFLOWS.glob("*.yml"):
            trigger = load(path).get("on", {})
            names = {trigger} if isinstance(trigger, str) else set(trigger)
            self.assertNotIn("workflow_run", names, path.name)

    def test_pull_request_proof_is_isolated_nonmutating_and_same_repository(self):
        path = WORKFLOWS / "pr-validation.yml"
        workflow = load(path)
        prove = workflow["jobs"]["prove"]
        self.assertIn("pull_request", workflow["on"])
        self.assertIn("head.repo.full_name == github.repository", prove["if"])
        source = path.read_text(encoding="utf-8")
        self.assertNotIn("${{ secrets.", source)
        command = commands(prove)
        self.assertIn("scripts/pr-proof.sh", command)
        self.assertIn("git worktree add --detach", command)
        self.assertIn("git worktree remove --force", command)
        self.assertIn("LAPLACE_PR_WORKTREE", command)
        self.assertNotIn("git checkout --force", command)
        for forbidden in ("pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh deploy", "sudo "):
            self.assertNotIn(forbidden, command)

        concurrency = workflow["concurrency"]
        self.assertEqual("laplace-pr-${{ github.event.pull_request.number }}", concurrency["group"])
        self.assertEqual("true", concurrency["cancel-in-progress"])
        self.assertNotEqual(load(MAIN)["concurrency"]["group"], concurrency["group"])

        proof = (ROOT / "scripts/pr-proof.sh").read_text(encoding="utf-8")
        for forbidden in (
            "publish-applications.sh check", "check-application-runtime.py",
            "pipeline.sh install", "pipeline.sh migrate", "sync-extension",
            "publish-applications.sh deploy", "systemctl ", "sudo ", "--fresh-db",
            "dotnet test ", "npx playwright", "test-uci-publish.py", "npm run test:",
        ):
            self.assertNotIn(forbidden, proof)

    def test_clean_pr_ui_typecheck_owns_api_client_generation_through_registry(self):
        browser = suites()["browser-dev"]
        command = str(browser["command"])
        self.assertIn("npm run typecheck", command)
        self.assertNotIn("npm run gen:api", command)
        package = (ROOT / "web/package.json").read_text(encoding="utf-8")
        self.assertIn('"pretypecheck": "npm run gen:api"', package)
        self.assertIn('"gen:api": "openapi-typescript ./openapi/openapi.json -o ./src/api/types.gen.ts"', package)

    def test_manual_mutation_workflows_are_not_source_triggered(self):
        paths = [WORKFLOWS / "db-ops.yml", *sorted(WORKFLOWS.glob("seed-*.yml"))]
        for path in paths:
            with self.subTest(path=path.name):
                trigger = load(path)["on"]
                names = {trigger} if isinstance(trigger, str) else set(trigger)
                self.assertFalse(names & {"push", "pull_request"})
                self.assertTrue(names & {"workflow_dispatch", "workflow_call"})

    def test_benchmark_workflow_is_dispatch_only_registry_driven_and_exact_artifact_bound(self):
        path = WORKFLOWS / "benchmark-evidence.yml"
        workflow = load(path)
        trigger = workflow["on"]
        names = {trigger} if isinstance(trigger, str) else set(trigger)
        self.assertEqual({"workflow_dispatch"}, names)
        self.assertEqual("laplace-shared-workspace", workflow["concurrency"]["group"])
        self.assertEqual("false", workflow["concurrency"]["cancel-in-progress"])
        command = commands(workflow["jobs"]["benchmark"])
        self.assertIn("python3 scripts/benchmark_suite.py validate", command)
        self.assertIn("python3 scripts/benchmark_suite.py \"${args[@]}\"", command)
        self.assertNotIn("python3 scripts/bench-compose.py", command)
        self.assertNotIn("python3 scripts/bench-compose-scale.py", command)
        self.assertIn("build/engine/core/liblaplace_core.so", command)
        self.assertIn("build/engine/core/perfcache/laplace_t0_perfcache.bin", command)
        module = benchmark_module()
        registry = json.loads(BENCHMARK_REGISTRY.read_text(encoding="utf-8"))
        module.validate_registry(registry)
        suite_ids = {suite["id"] for suite in registry["suites"]}
        self.assertEqual({"quick", "throughput", "core", "scale", "moby", "all"}, suite_ids)

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
