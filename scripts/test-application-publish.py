#!/usr/bin/env python3
"""Execute the real transaction orchestrator with isolated effect adapters."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import yaml

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/publish-applications.sh"
DELIVERY_SELECTOR = ROOT / "scripts/application-delivery-source.py"
ADAPTERS = r'''
source "$1"
ROOT="$2"
event() { printf '%s\n' "$*" >> "$ROOT/events"; [[ "$*" != "${FAIL_AT:-}" ]]; }
application_guard() {
  event "guard $1" || return 9
  if [[ "$1" == --snapshot ]]; then printf '{}' > "$2"; fi
}
application_host_check() { event host; }
application_publish() {
  touch "$ROOT/build/.managed-publish-backup"
  event publish
}
application_restart() { event restart; }
application_restore() { event restore; }
application_verify() { event verify; }
application_stamp() { event stamp; }
application_managed() {
  event "managed $1" || return 9
  if [[ "$1" == commit || "$1" == rollback ]]; then rm "$ROOT/build/.managed-publish-backup"; fi
}
application_main "$3"
'''


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ApplicationTransactionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="application-publish-contract-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / "build").mkdir()

    def run_release(self, mode="deploy", fail="", script=SCRIPT):
        env = dict(os.environ, GITHUB_RUN_ID="fixture-run", FAIL_AT=fail)
        return subprocess.run(["bash", "-c", ADAPTERS, "test", str(script), str(self.root), mode],
                              env=env, capture_output=True, text=True, timeout=10)

    def events(self):
        path = self.root / "events"
        return path.read_text().splitlines() if path.exists() else []

    def test_check_is_read_only_and_never_publishes(self):
        result = self.run_release("check")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["guard --snapshot", "host"], self.events())
        self.assertFalse((self.root / "build/.application-publish-owner").exists())

    def test_success_requires_verification_and_unchanged_engine_before_commit(self):
        result = self.run_release()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["guard --snapshot", "host", "publish", "restart", "verify",
                          "guard --compare", "managed commit", "stamp"], self.events())
        self.assertTrue((self.root / "build/.applications-verified.json").exists())

    def test_preflight_failure_never_mutates_or_rolls_back(self):
        for fail in ("guard --snapshot", "host"):
            with self.subTest(fail=fail):
                result = self.run_release(fail=fail)
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("publish", self.events())
                self.assertNotIn("managed rollback", self.events())

    def test_every_precommit_failure_rolls_back_and_restores_api(self):
        for fail in ("publish", "restart", "verify", "guard --compare", "managed commit"):
            with self.subTest(fail=fail):
                result = self.run_release(fail=fail)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(["managed rollback", "restore"], self.events()[-2:])
                self.assertNotIn("stamp", self.events())
                self.assertFalse((self.root / "build/.application-publish-owner").exists())

    def test_unresolved_receipt_is_not_overwritten_or_rolled_back(self):
        (self.root / "build/.managed-publish-backup").write_text("previous")
        result = self.run_release()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.events())

    def test_cancellation_recovery_is_owned_by_run_id(self):
        (self.root / "build/.application-publish-owner").write_text("fixture-run")
        (self.root / "build/.managed-publish-backup").write_text("backup")
        result = self.run_release("recover")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["managed rollback", "restore"], self.events())
        self.assertEqual(0, self.run_release("recover").returncode)
        self.assertEqual(["managed rollback", "restore"], self.events())

    def test_recovery_cannot_touch_another_runs_transaction(self):
        (self.root / "build/.application-publish-owner").write_text("other-run")
        (self.root / "build/.managed-publish-backup").write_text("backup")
        self.assertNotEqual(0, self.run_release("recover").returncode)
        self.assertEqual([], self.events())

    def test_failed_api_restore_remains_retryable(self):
        (self.root / "build/.application-publish-owner").write_text("fixture-run")
        (self.root / "build/.managed-publish-backup").write_text("backup")
        self.assertNotEqual(0, self.run_release("recover", fail="restore").returncode)
        self.assertTrue((self.root / "build/.application-restore-pending").exists())
        self.assertEqual(0, self.run_release("recover").returncode)
        self.assertEqual(["managed rollback", "restore", "restore"], self.events())

    def test_stamp_failure_does_not_undo_an_already_verified_commit(self):
        self.assertNotEqual(0, self.run_release(fail="stamp").returncode)
        self.assertNotIn("managed rollback", self.events())
        self.assertEqual(0, self.run_release("recover").returncode)

    def test_deliberately_removing_post_publish_guard_is_detected(self):
        broken = self.root / "broken.sh"
        source = SCRIPT.read_text()
        line = '  application_guard --compare "$proof/runtime-before.json"'
        self.assertIn(line, source)
        broken.write_text(source.replace(line, "  : # deliberate missing postcondition"))
        result = self.run_release(fail="guard --compare", script=broken)
        # The same failure injection cannot block commit once the guard is
        # deliberately removed. The ordinary failure test would therefore fail.
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("managed commit", self.events())


class DeliveryDecisionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.selector = load_module("application_delivery_source", DELIVERY_SELECTOR)

    def payload(self, *, unit="success", publish="skipped", integration="failure"):
        conclusions = {
            "Build — engine, extensions, app, perfcache": "success",
            "Unit tests — native engine and managed ABI": unit,
            "Deploy — stage install to /opt/laplace": "success",
            "DB — migrate, extension sync, perfcache GUC": "success",
            "Integration tests — pg_regress || dotnet (parallel)": integration,
            "Publish — API + SPA, restart service": publish,
        }
        jobs = [{"name": name, "conclusion": conclusion}
                for name, conclusion in conclusions.items()]
        return {"total_count": len(jobs), "jobs": jobs}

    def test_failed_environment_qa_still_authorizes_owed_delivery(self):
        decision = self.selector.decide(self.payload(), "push")
        self.assertTrue(decision.deliver, decision.reason)
        self.assertIn("publish was skipped", decision.reason)

    def test_failed_dev_proof_never_authorizes_delivery(self):
        decision = self.selector.decide(self.payload(unit="failure"), "push")
        self.assertFalse(decision.deliver)
        self.assertIn("Unit tests", decision.reason)

    def test_already_published_run_does_not_publish_twice(self):
        decision = self.selector.decide(
            self.payload(publish="success", integration="success"), "push")
        self.assertFalse(decision.deliver)
        self.assertIn("already delivered", decision.reason)

    def test_failed_activation_is_not_retried_as_qa_recovery(self):
        decision = self.selector.decide(self.payload(publish="failure"), "push")
        self.assertFalse(decision.deliver)
        self.assertIn("not auto-retried", decision.reason)

    def test_non_push_and_incomplete_job_payload_fail_closed(self):
        self.assertFalse(self.selector.decide(self.payload(), "workflow_dispatch").deliver)
        incomplete = self.payload()
        incomplete["total_count"] += 1
        with self.assertRaises(ValueError):
            self.selector.decide(incomplete, "push")


class ReleaseWorkflowTests(unittest.TestCase):
    def test_application_lane_cannot_install_or_migrate(self):
        jobs = yaml.safe_load((ROOT / ".github/workflows/laplace.yml").read_text())["jobs"]
        app = jobs["application-release"]
        self.assertEqual("unit-test", app["needs"])
        self.assertIn("workflow_dispatch", app["if"])
        self.assertEqual("laplace-substrate-lifecycle", app["concurrency"]["group"])
        self.assertFalse(app["concurrency"]["cancel-in-progress"])
        commands = "\n".join(step.get("run", "") for step in app["steps"])
        for forbidden in ("pipeline.sh install", "pipeline.sh migrate", "sync-extension", "tune-pg", "perfcache-guc"):
            self.assertNotIn(forbidden, commands)
        self.assertIn("publish-applications.sh check", commands)
        self.assertIn("publish-applications.sh deploy", commands)
        recovery = app["steps"][-1]
        self.assertIn("always()", recovery["if"])
        self.assertIn("recover", recovery["run"])
        self.assertTrue(any("Validate application-only" in step.get("name", "") for step in jobs["policy"]["steps"]))

    def test_application_delivery_is_independent_of_environment_qa(self):
        path = ROOT / ".github/workflows/application-delivery.yml"
        source = path.read_text()
        workflow = yaml.safe_load(source)
        job = workflow["jobs"]["deliver"]

        self.assertIn('workflows: ["Laplace — build, deploy, test"]', source)
        self.assertIn("types: [completed]", source)
        self.assertIn("branches: [main]", source)
        self.assertIn("workflow_run.event == 'push'", job["if"])
        self.assertNotIn("integration", job["if"].lower())
        self.assertEqual("laplace-substrate-lifecycle", job["concurrency"]["group"])
        self.assertFalse(job["concurrency"]["cancel-in-progress"])

        commands = "\n".join(step.get("run", "") for step in job["steps"])
        self.assertIn("application-delivery-source.py", commands)
        self.assertIn("SOURCE_SHA", commands)
        self.assertIn("CURRENT_MAIN_SHA", commands)
        self.assertIn("publish-applications.sh deploy", commands)
        self.assertIn("publish-applications.sh recover", commands)
        self.assertNotIn("needs.integration-test", commands)

        managed = (ROOT / "deploy/linux/managed-publish.sh").read_text()
        self.assertIn('"${PUBLISH_RESULT:-}" == "success"', managed)
        self.assertIn("retaining the activated application payload", managed)

    def test_runtime_readiness_rejects_false_or_untyped_results(self):
        module = load_module("verify_app", ROOT / "scripts/verify-application-release.py")
        ready = {"ready": True, "substrate_reachable": True, "perfcache_ready": True,
                 "entities": 1, "consensus_relations": 2}
        module.require_ready(ready)
        for key in ready:
            with self.subTest(key=key):
                bad = dict(ready, **{key: False})
                with self.assertRaises(ValueError):
                    module.require_ready(bad)

    def test_model_payload_enforcement_is_at_model_ingest_boundary(self):
        main_jobs = yaml.safe_load((ROOT / ".github/workflows/laplace.yml").read_text())["jobs"]
        policy_commands = "\n".join(
            step.get("run", "") for step in main_jobs["policy"]["steps"])
        self.assertIn("python3 scripts/model-payload-gate-check.py", policy_commands)
        self.assertNotIn("model-payload-gate-check.py --enforce", policy_commands)

        seed = yaml.safe_load((ROOT / ".github/workflows/seed-models.yml").read_text())
        steps = seed["jobs"]["ingest"]["steps"]
        runs = [step.get("run", "") for step in steps]
        ingest_index = next(i for i, run in enumerate(runs) if "ingest-source.sh model" in run)
        gate_indexes = [
            i for i, run in enumerate(runs)
            if "model-payload-gate-check.py --strict --enforce" in run
        ]
        self.assertEqual(2, len(gate_indexes))
        self.assertLess(gate_indexes[0], ingest_index)
        self.assertGreater(gate_indexes[1], ingest_index)

    def test_model_payload_violation_is_advisory_unless_enforced(self):
        module = load_module("model_payload_gate", ROOT / "scripts/model-payload-gate-check.py")
        with tempfile.TemporaryDirectory(prefix="model-payload-gate-contract-") as td:
            baseline = Path(td) / "baseline.json"
            baseline.write_text(json.dumps({
                "total_payload_bytes": 0,
                "total_vertices": 0,
                "per_source": {},
            }))
            rows = [{"source": "fixture", "rows": 1,
                     "payload_bytes": 1024, "vertices": 4}]
            with patch.object(module, "BASELINE", baseline), \
                 patch.object(module, "measure", return_value=rows):
                self.assertEqual(0, module.main([]))
                self.assertEqual(1, module.main(["--enforce"]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
