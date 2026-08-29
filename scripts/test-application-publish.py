#!/usr/bin/env python3
"""Execute the real transaction orchestrator with isolated effect adapters."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import yaml

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/publish-applications.sh"
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

    def test_full_release_eval_and_rollback_gate_remain_mandatory(self):
        jobs = yaml.safe_load((ROOT / ".github/workflows/laplace.yml").read_text())["jobs"]
        self.assertEqual(["publish", "smoke"], jobs["eval"]["needs"])
        self.assertIn("eval", jobs["restore-api"]["needs"])
        restore = jobs["restore-api"]["steps"][0]["run"]
        self.assertIn('"$EVAL_RESULT" != failure', restore)
        self.assertIn('"$EVAL_RESULT" != cancelled', restore)
        for name in ("deploy", "db-ops", "integration-test", "publish"):
            self.assertNotIn("applications", jobs[name]["if"])

    def test_runtime_readiness_rejects_false_or_untyped_results(self):
        spec = importlib.util.spec_from_file_location("verify_app", ROOT / "scripts/verify-application-release.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        ready = {"ready": True, "substrate_reachable": True, "perfcache_ready": True,
                 "entities": 1, "consensus_relations": 2}
        module.require_ready(ready)
        for key in ready:
            with self.subTest(key=key):
                bad = dict(ready, **{key: False})
                with self.assertRaises(ValueError):
                    module.require_ready(bad)


if __name__ == "__main__":
    unittest.main(verbosity=2)
