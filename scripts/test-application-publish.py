#!/usr/bin/env python3
"""Exercise the real application transaction and deployment-health classifier."""
from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/publish-applications.sh"
VERIFY = ROOT / "scripts/verify-application-release.py"
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


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
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
        return subprocess.run(
            ["bash", "-c", ADAPTERS, "test", str(script), str(self.root), mode],
            env=env, capture_output=True, text=True, timeout=10,
        )

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
        self.assertEqual([
            "guard --snapshot", "host", "publish", "restart", "verify",
            "guard --compare", "managed commit", "stamp",
        ], self.events())
        self.assertTrue((self.root / "build/.applications-verified.json").exists())
        self.assertFalse((self.root / "build/.application-release-state.json").exists(),
                         "test adapter does not create a state receipt")

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
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("managed commit", self.events())


class DeploymentReadinessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.verify = load_module("verify_application_release", VERIFY)

    @staticmethod
    def readiness(**updates):
        value = {
            "ready": False,
            "substrate_reachable": True,
            "perfcache_ready": True,
            "entities": 0,
            "consensus_relations": 0,
        }
        value.update(updates)
        return value

    def test_empty_database_is_healthy_delivery_without_product_data(self):
        self.assertEqual((False, False), self.verify.classify_readiness(self.readiness()))

    def test_thin_database_is_delivered_then_owned_by_substrate_floor_smoke(self):
        self.assertEqual(
            (True, False),
            self.verify.classify_readiness(self.readiness(entities=1)),
        )
        self.assertEqual(
            (True, False),
            self.verify.classify_readiness(self.readiness(consensus_relations=1)),
        )

    def test_populated_but_not_ready_receipt_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "populated substrate"):
            self.verify.classify_readiness(
                self.readiness(entities=10, consensus_relations=20)
            )

    def test_populated_ready_database_reports_product_ready(self):
        self.assertEqual(
            (True, True),
            self.verify.classify_readiness(
                self.readiness(ready=True, entities=10, consensus_relations=20)
            ),
        )

    def test_runtime_failures_and_impossible_ready_state_fail_closed(self):
        cases = [
            self.readiness(substrate_reachable=False),
            self.readiness(perfcache_ready=False),
            self.readiness(ready=True),
            self.readiness(ready=True, entities=1),
            self.readiness(ready=True, consensus_relations=1),
        ]
        for value in cases:
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.verify.classify_readiness(value)

    def test_negative_boolean_and_untyped_fields_do_not_pass_as_health(self):
        cases = (
            ("entities", False),
            ("entities", -1),
            ("consensus_relations", "0"),
            ("consensus_relations", -1),
            ("ready", 0),
            ("substrate_reachable", 1),
            ("perfcache_ready", None),
        )
        for key, value in cases:
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                self.verify.classify_readiness(self.readiness(**{key: value}))

    def test_transient_startup_readiness_is_retried_before_verification(self):
        with patch.object(
            self.verify,
            "readiness",
            side_effect=[ValueError("starting"), (False, False)],
        ) as readiness, patch.object(
            self.verify.time, "monotonic", side_effect=[0.0, 0.0]
        ), patch.object(self.verify.time, "sleep") as sleep:
            self.assertEqual(
                (False, False),
                self.verify.wait_for_readiness("http://unit", 5.0, 0.25),
            )
        self.assertEqual(2, readiness.call_count)
        sleep.assert_called_once_with(0.25)

    def test_readiness_retry_timeout_is_bounded_and_named(self):
        with patch.object(
            self.verify,
            "readiness",
            side_effect=ValueError("structurally unavailable"),
        ), patch.object(
            self.verify.time, "monotonic", side_effect=[0.0, 2.0]
        ), patch.object(self.verify.time, "sleep") as sleep:
            with self.assertRaisesRegex(RuntimeError, "within 2 seconds"):
                self.verify.wait_for_readiness("http://unit", 2.0, 0.25)
        sleep.assert_not_called()

    def test_invalid_readiness_retry_bounds_fail_before_network(self):
        with patch.object(self.verify, "readiness") as readiness:
            for timeout, retry in ((-1.0, 1.0), (1.0, 0.0), (1.0, -1.0)):
                with self.subTest(timeout=timeout, retry=retry), self.assertRaises(ValueError):
                    self.verify.wait_for_readiness("http://unit", timeout, retry)
        readiness.assert_not_called()

    def test_full_verification_executes_readiness_spa_and_typed_db_operation(self):
        calls = []
        responses = [
            (503, "application/json", json.dumps(self.readiness()).encode()),
            (200, "text/html; charset=utf-8", b'<!doctype html><div id="root"></div>'),
            (200, "application/json", b'{"object":"op.result","name":"ops.substrate_counts"}'),
        ]

        def fake_request(method, url, body=None, headers=None):
            calls.append((method, url, body, headers))
            return responses.pop(0)

        with patch.object(self.verify, "request", side_effect=fake_request), \
             patch.object(self.verify, "verify_stockfish") as stockfish:
            self.assertEqual((False, False), self.verify.verify("http://unit"))
        stockfish.assert_called_once_with()
        self.assertEqual(
            ["/health/ready", "/", "/v1/op"],
            [call[1].removeprefix("http://unit") for call in calls],
        )
        self.assertEqual("POST", calls[-1][0])
        self.assertIn(b"ops.substrate_counts", calls[-1][2])

    def test_readiness_only_does_not_pretend_to_verify_spa_or_typed_operation(self):
        with patch.object(
            self.verify,
            "request",
            return_value=(503, "application/json", json.dumps(self.readiness()).encode()),
        ) as request:
            self.assertEqual(
                (False, False),
                self.verify.verify("http://unit", readiness_only=True),
            )
        request.assert_called_once()

    def test_state_and_github_outputs_preserve_delivery_and_product_dimensions(self):
        with tempfile.TemporaryDirectory(prefix="application-state-") as td:
            state = Path(td) / "state.json"
            output = Path(td) / "github-output"
            self.verify.write_state(state, True, False)
            self.verify.write_github_output(output, True, False)
            self.assertEqual(
                {
                    "has_data": True,
                    "product_ready": False,
                    "substrate_reachable": True,
                    "perfcache_ready": True,
                },
                json.loads(state.read_text()),
            )
            self.assertEqual(
                ["has_data=true", "product_ready=false"],
                output.read_text().splitlines(),
            )


if __name__ == "__main__":
    unittest.main(verbosity=2)
