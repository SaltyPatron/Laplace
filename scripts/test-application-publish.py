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
managed() { event "managed $1"; }
sudo() { event "systemctl ${*: -2}"; }
bash() { event "pipeline ${*: -1}"; }
curl() {
  event readiness >&2 || return 9
  printf '{"ready":%s}\n' "${READY:-true}"
}
sleep() { :; }
main "$3"
'''

def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


class ApplicationTransactionTests(unittest.TestCase):
    """Exercise current direct deployment; no removed session/stamp owner."""
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(
            prefix="application-publish-contract-", dir=os.environ["TMPDIR"])
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        (self.root / "build").mkdir()

    def run_release(self, mode="deploy", fail="", ready="true", script=SCRIPT):
        (self.root / "events").unlink(missing_ok=True)
        return subprocess.run(
            ["bash", "-c", ADAPTERS, "test", str(script), str(self.root), mode],
            env=dict(os.environ, FAIL_AT=fail, READY=ready),
            capture_output=True, text=True, timeout=10)

    def events(self):
        path = self.root / "events"
        return path.read_text().splitlines() if path.exists() else []

    def test_check_only_delegates_current_managed_preflight(self):
        result = self.run_release("check")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["managed preflight"], self.events())

    def test_success_preserves_direct_publish_restart_readiness_commit(self):
        result = self.run_release()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([
            "managed preflight", "pipeline publish", "systemctl restart laplace-api",
            "managed activate", "readiness", "managed commit"], self.events())
        self.assertFalse((self.root / "build/.applications-verified.json").exists())

    def test_preflight_failure_does_not_publish_or_recover(self):
        result = self.run_release(fail="managed preflight")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(["managed preflight"], self.events())

    def test_precommit_failures_use_existing_rollback_and_api_restore(self):
        for fail in ("pipeline publish", "systemctl restart laplace-api",
                     "managed activate", "managed commit"):
            with self.subTest(fail=fail):
                result = self.run_release(fail=fail)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(
                    ["managed rollback", "systemctl start laplace-api"], self.events()[-2:])

    def test_not_ready_never_commits_and_restores_previous_payload(self):
        result = self.run_release(ready="false")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(60, self.events().count("readiness"))
        self.assertNotIn("managed commit", self.events())
        self.assertEqual(
            ["managed rollback", "systemctl start laplace-api"], self.events()[-2:])

    def test_recover_retains_broad_owner_without_api_receipt(self):
        result = self.run_release("recover")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["managed rollback", "systemctl start laplace-api"], self.events())

    def test_recovery_failure_is_not_reported_success(self):
        for fail in ("managed rollback", "systemctl start laplace-api"):
            with self.subTest(fail=fail):
                result = self.run_release("recover", fail=fail)
                self.assertNotEqual(0, result.returncode)

    def test_pending_api_transaction_refuses_full_deploy_before_any_action(self):
        for name in (".api-publish-backup", ".application-publish-owner"):
            with self.subTest(name=name):
                path = self.root / "build" / name
                path.write_bytes(b"retained previous owner")
                result = self.run_release()
                self.assertNotEqual(0, result.returncode)
                self.assertEqual([], self.events())
                self.assertEqual(b"retained previous owner", path.read_bytes())
                path.unlink()

    def test_shared_managed_begin_refuses_api_state_before_host_or_payload_work(self):
        directory = self.root / "deploy/linux"
        directory.mkdir(parents=True)
        for name in ("payload-sync.sh", "managed-publish.sh"):
            content = (ROOT / "deploy/linux" / name).read_text()
            if name == "managed-publish.sh":
                # Only the host precondition is a sentinel. The actual begin
                # dispatcher and pending-state check execute unchanged.
                content = content.replace(
                    "ensure_host() {\n",
                    'ensure_host() {\n  printf "host preflight reached\\n" >&2; return 97\n')
            (directory / name).write_text(content)
        for name in (".api-publish-backup", ".application-publish-owner"):
            with self.subTest(name=name):
                path = self.root / "build" / name
                path.write_bytes(b"retained API owner")
                result = subprocess.run(
                    ["bash", str(directory / "managed-publish.sh"), "begin"],
                    env=dict(os.environ, LAPLACE_APP_DIR=str(self.root / "app")),
                    capture_output=True, text=True, timeout=10)
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertIn("API publication recovery is unresolved", result.stderr)
                self.assertNotIn("host preflight reached", result.stderr)
                self.assertEqual(b"retained API owner", path.read_bytes())
                path.unlink()

    def test_unknown_mode_is_refused_without_publication(self):
        result = self.run_release("unknown")
        self.assertEqual(2, result.returncode)
        self.assertEqual([], self.events())


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



API_ADAPTERS = r'''
source "$1"
ROOT="$2"
event() { printf '%s\n' "$*" >> "$ROOT/events"; [[ "$*" != "${FAIL_AT:-}" ]]; }
application_guard() {
  event "guard $1" || return 9
  if [[ "$1" == --snapshot ]]; then printf '{}' > "$2"; fi
}
application_api_active() { event active >&2; printf '%s\n' "${WAS_ACTIVE:-1}"; }
application_api_manifest() { event manifest; printf '{}' > "$2"; }
application_api_control() { event "control $1"; }
application_api_publish() {
  event publish
  application_api_control stop
  application_api_sync "$ROOT/newapp"
  printf '{}' > "$1"
  event replaced
}
application_api_verify() {
  event "verify ${1##*/}" || return 9
  printf '{"status":"passed"}' > "$2"
}
# Any unintended broad transaction action is a hard failure in these controls.
application_managed() { event "FORBIDDEN managed $*"; return 91; }
managed() { event "FORBIDDEN current managed $*"; return 91; }
application_stamp() { event "FORBIDDEN stamp"; return 92; }
application_host_check() { event "FORBIDDEN managed preflight"; return 93; }
main "$3"
'''


class ApiOnlyTransactionTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="api-publication-contract-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.app = self.root / "app"
        self.backups = self.root / "backups"
        for name in ("app", "newapp", "backups", "build", "deploy/linux"):
            (self.root / name).mkdir(parents=True, exist_ok=True)
        source = SCRIPT.read_text()
        self.assertIn("/opt/laplace/app-backups/api.", source)
        # Redirect only fixed host paths into this real filesystem fixture.
        source = source.replace("/opt/laplace/app-backups", str(self.backups))
        source = source.replace("/var/lib/laplace-managed/transaction.json",
                                str(self.root / "root-transaction.json"))
        self.script = self.root / "publish.sh"
        self.script.write_text(source)
        for name in ("payload-sync.sh", "managed-publish.sh"):
            (self.root / "deploy/linux" / name).write_text(
                (ROOT / "deploy/linux" / name).read_text())
        for location, version in ((self.app, "old"), (self.root / "newapp", "new")):
            (location / "Api.dll").write_text(version)
            (location / "wwwroot").mkdir()
            (location / "wwwroot/index.html").write_text(version)
        (self.app / "old-only.dll").write_text("obsolete")
        (self.root / "newapp/new-only.dll").write_text("new")
        self.preserved = {}
        for name in ("laplace-api.env", "agents.json", "logs/uci.csv",
                     "chess-lab-work/owned", "mcp-runtime/old.dll", "mcp/session",
                     "releases/runtime.one/mcp/runtime.dll", "managed-services/unit.service"):
            path = self.app / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("preserve-" + name)
            self.preserved[name] = (path.read_bytes(), path.stat().st_ino)
        for name in ("laplace-uci", "laplace-mcp", "laplace-lichess"):
            (self.app / name).symlink_to("releases/runtime.one/mcp/runtime.dll")

    def run_api(self, mode="api-deploy", fail="", active="1", run="fixture-api"):
        return subprocess.run(
            ["bash", "-c", API_ADAPTERS, "test", str(self.script), str(self.root), mode],
            env=dict(os.environ, GITHUB_RUN_ID=run, LAPLACE_APP_DIR=str(self.app),
                     FAIL_AT=fail, WAS_ACTIVE=active),
            capture_output=True, text=True, timeout=15)

    def events(self):
        path = self.root / "events"
        return path.read_text().splitlines() if path.exists() else []

    def assert_preserved(self):
        for name, identity in self.preserved.items():
            path = self.app / name
            self.assertEqual(identity, (path.read_bytes(), path.stat().st_ino), name)
        for name in ("laplace-uci", "laplace-mcp", "laplace-lichess"):
            self.assertEqual("releases/runtime.one/mcp/runtime.dll",
                             os.readlink(self.app / name))
        self.assertFalse(any("FORBIDDEN" in line for line in self.events()))
        self.assertFalse((self.root / "build/.applications-verified.json").exists())

    def test_api_commit_replaces_only_api_and_spa_without_full_publish_stamp(self):
        result = self.run_api()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("new", (self.app / "Api.dll").read_text())
        self.assertFalse((self.app / "old-only.dll").exists())
        self.assertTrue((self.app / "new-only.dll").exists())
        self.assertEqual(["active", "guard --snapshot", "manifest", "publish", "control stop",
                          "replaced", "control start", "verify next.json", "guard --compare"],
                         self.events())
        self.assertTrue((self.root / "build/.api-publish-verified.json").is_file())
        self.assertEqual([], list(self.backups.iterdir()))
        self.assert_preserved()

    def test_actual_replacement_and_verification_failure_restore_full_old_payload(self):
        for fail in ("replaced", "verify next.json", "guard --compare"):
            with self.subTest(fail=fail):
                result = self.run_api(fail=fail)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual("old", (self.app / "Api.dll").read_text())
                self.assertTrue((self.app / "old-only.dll").exists())
                self.assertFalse((self.app / "new-only.dll").exists())
                self.assertEqual(["control stop", "control start", "verify previous.json"],
                                 self.events()[-3:])
                self.assertFalse((self.root / "build/.application-publish-owner").exists())
                self.assert_preserved()

    def test_failed_restore_retains_owned_payload_and_is_retryable(self):
        # Make activation succeed but fail both the new and restored verification.
        adapter = API_ADAPTERS.replace(
            'event "verify ${1##*/}" || return 9',
            'event "verify ${1##*/}" || return 9; [[ -f "$ROOT/allow-verify" ]] || return 9')
        result = subprocess.run(
            ["bash", "-c", adapter, "test", str(self.script), str(self.root), "api-deploy"],
            env=dict(os.environ, GITHUB_RUN_ID="fixture-api", LAPLACE_APP_DIR=str(self.app)),
            capture_output=True, text=True, timeout=15)
        self.assertNotEqual(0, result.returncode)
        receipt = self.root / "build/.api-publish-backup"
        self.assertTrue(receipt.is_file())
        backup = Path(receipt.read_text().strip())
        self.assertEqual("old", (backup / "app/Api.dll").read_text())
        self.assertNotEqual(0, self.run_api("api-recover", run="foreign").returncode)
        self.assertTrue(backup.is_dir())
        retry = self.run_api("api-recover")
        self.assertEqual(0, retry.returncode, retry.stderr)
        self.assertFalse(receipt.exists())
        self.assertEqual("old", (self.app / "Api.dll").read_text())
        self.assert_preserved()

    def test_preflight_and_foreign_managed_transaction_fail_before_publish(self):
        result = self.run_api(fail="guard --snapshot")
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("publish", self.events())
        self.assertEqual([], list(self.backups.iterdir()))
        (self.root / "root-transaction.json").write_text("{}")
        result = self.run_api()
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("publish", self.events())
        self.assert_preserved()

    def test_rollback_preserves_previously_inactive_api(self):
        result = self.run_api(fail="replaced", active="0")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("old", (self.app / "Api.dll").read_text())
        self.assertNotIn("control start", self.events())
        self.assertNotIn("verify previous.json", self.events())
        self.assert_preserved()

    def test_term_after_replacement_runs_owned_rollback(self):
        adapter = API_ADAPTERS.replace("  event replaced", '  kill -TERM "$BASHPID"')
        result = subprocess.run(
            ["bash", "-c", adapter, "test", str(self.script), str(self.root), "api-deploy"],
            env=dict(os.environ, GITHUB_RUN_ID="fixture-api", LAPLACE_APP_DIR=str(self.app)),
            capture_output=True, text=True, timeout=15)
        self.assertEqual(143, result.returncode, result.stderr)
        self.assertEqual("old", (self.app / "Api.dll").read_text())
        self.assertEqual(["control stop", "control start", "verify previous.json"],
                         self.events()[-3:])
        self.assert_preserved()



    def test_same_size_same_mtime_payload_is_replaced_and_backup_restores_exact_bytes(self):
        # Old/new are both three bytes. Preserve the old metadata deliberately:
        # rsync's default quick check otherwise leaves the wrong executable bytes.
        for name in ("Api.dll", "wwwroot/index.html"):
            old = self.app / name
            new = self.root / "newapp" / name
            os.utime(new, ns=(old.stat().st_atime_ns, old.stat().st_mtime_ns))
        # Fail restoration verification so the real hardlinked snapshot remains
        # available for direct comparison after the attempted update and restore.
        adapter = API_ADAPTERS.replace(
            "  event replaced", '  cp "$ROOT/app/Api.dll" "$ROOT/copied-new"; event replaced; return 19'
        ).replace(
            '  event "verify ${1##*/}" || return 9',
            '  event "verify ${1##*/}" || return 9; return 20')
        result = subprocess.run(
            ["bash", "-c", adapter, "test", str(self.script), str(self.root), "api-deploy"],
            env=dict(os.environ, GITHUB_RUN_ID="fixture-api", LAPLACE_APP_DIR=str(self.app)),
            capture_output=True, text=True, timeout=15)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("new", (self.root / "copied-new").read_text())
        self.assertEqual("old", (self.app / "Api.dll").read_text())
        backup = Path((self.root / "build/.api-publish-backup").read_text().strip())
        self.assertEqual("old", (backup / "app/Api.dll").read_text())
        retry = self.run_api("api-recover")
        self.assertEqual(0, retry.returncode, retry.stderr)
        self.assertEqual("old", (self.app / "Api.dll").read_text())
        self.assert_preserved()

    def test_ambiguous_managed_and_api_recovery_cannot_choose_an_owner(self):
        marker = self.root / "build/.application-publish-owner"
        api_receipt = self.root / "build/.api-publish-backup"
        marker.write_text("fixture-api")
        api_receipt.write_text("retained API backup")
        for pending in (self.root / "build/.managed-publish-backup",
                        self.root / "root-transaction.json"):
            with self.subTest(pending=pending.name):
                pending.write_text("retained managed state")
                for mode in ("recover", "api-recover"):
                    result = self.run_api(mode)
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("ambiguous", result.stderr)
                    self.assertEqual([], self.events())
                    self.assertEqual("retained API backup", api_receipt.read_text())
                    self.assertEqual("fixture-api", marker.read_text())
                    self.assertEqual("retained managed state", pending.read_text())
                pending.unlink()
        self.assert_preserved()

    def test_local_transaction_owner_uses_actual_process_id(self):
        adapter = API_ADAPTERS.replace(
            '  event replaced', '  event replaced; return 19').replace(
            '  printf \'{"status":"passed"}\' > "$2"',
            '  if [[ "$1" == */previous.json ]]; then return 20; fi; printf \'{"status":"passed"}\' > "$2"')
        env = dict(os.environ, LAPLACE_APP_DIR=str(self.app))
        env.pop("GITHUB_RUN_ID", None)
        result = subprocess.run(
            ["bash", "-c", adapter, "test", str(self.script), str(self.root), "api-deploy"],
            env=env, capture_output=True, text=True, timeout=15)
        self.assertNotEqual(0, result.returncode)
        marker = self.root / "build/.application-publish-owner"
        value = marker.read_text()
        self.assertRegex(value, r"^local-[1-9][0-9]*$")
        self.assertTrue((self.root / "build/.api-publish-backup").is_file())


class ApiPayloadVerificationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.api = load_module("verify_api_payload", ROOT / "scripts/verify-api-payload.py")

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="api-payload-proof-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.stage = self.root / "stage"
        self.stage.mkdir()
        self.build = self.root / "build"
        self.build.mkdir()
        (self.build / "CMakeCache.txt").write_text(
            "CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.root) + "\n")
        for name in self.api.REQUIRED:
            (self.stage / name).write_bytes(("controlled-file:" + name).encode())
        (self.stage / "wwwroot").mkdir()
        (self.stage / "wwwroot/index.html").write_text('<div id="root"></div>')
        for name in self.api.REQUIRED[3:]:
            component = "dynamics" if "dynamics" in name else "synthesis" if "synthesis" in name else "core"
            path = self.build / "engine" / component / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes((self.stage / name).read_bytes())

    def seal(self):
        return self.api.seal(self.stage, self.build / "engine", self.root)

    def test_exact_native_copy_set_accepts_build_form_and_refuses_stale_alias(self):
        manifest = self.seal()
        self.assertEqual("selected-build", manifest["provenance"])
        self.assertEqual(set(self.api.REQUIRED[3:]), set(manifest["native_sources"]))
        source = self.build / "engine/core/liblaplace_core.so.0"
        source.symlink_to("liblaplace_core.so")
        with self.assertRaisesRegex(ValueError, "native bytes differ"):
            self.seal()
        (self.stage / source.name).write_bytes(source.read_bytes())
        self.seal()
        (self.stage / source.name).write_bytes(b"stale alias")
        with self.assertRaisesRegex(ValueError, "native bytes differ"):
            self.seal()

    def test_wrong_build_source_and_unexpected_native_library_refuse(self):
        cache = self.build / "CMakeCache.txt"
        cache.write_text("CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.stage) + "\n")
        with self.assertRaisesRegex(ValueError, "another source"):
            self.seal()
        cache.write_text("CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.root) + "\n")
        (self.stage / "libThirdParty.so").write_bytes(b"normal package dependency")
        self.seal()
        (self.stage / "liblaplace_core.so.999").write_bytes(b"stale engine alias")
        with self.assertRaisesRegex(ValueError, "publication set"):
            self.seal()

    def test_manifest_and_installed_bytes_reject_tamper_links_and_path_escape(self):
        manifest = self.seal()
        self.api.installed(self.stage, manifest)
        (self.stage / "Laplace.Core.dll").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            self.api.installed(self.stage, manifest)
        (self.stage / "Laplace.Core.dll").unlink()
        (self.stage / "Laplace.Core.dll").symlink_to(self.build / "engine/core/liblaplace_core.so")
        with self.assertRaisesRegex(ValueError, "escapes"):
            self.api.installed(self.stage, manifest)
        manifest["files"]["../outside"] = dict(next(iter(manifest["files"].values())))
        path = self.root / "manifest.json"
        self.api.save(path, manifest)
        with self.assertRaisesRegex(ValueError, "file identity"):
            self.api.read_manifest(path)

    def test_real_kernel_mapping_requires_selected_inode_and_stable_process(self):
        import mmap
        first = self.stage / "Laplace.Core.dll"
        other = self.stage / "Laplace.Chess.dll"
        with first.open("rb") as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ):
            proof = self.api.floor.process(os.getpid(), {"controlled_file": self.api.floor.fact(first)})
            self.assertEqual(os.getpid(), proof["pid"])
            with self.assertRaisesRegex(ValueError, "exact selected"):
                self.api.floor.process(os.getpid(), {"controlled_file": self.api.floor.fact(other)})

    def test_runtime_proof_checks_pid_payload_and_only_used_lazy_modules(self):
        manifest = self.seal()
        service = {"MainPID": "123", "Id": "laplace-api.service"}
        health = {"chess_perfcache": {"process_id": 123}}
        process = {"pid": 123, "start_ticks": 99}
        with patch.object(self.api.release, "wait_for_readiness", return_value=(False, False)), \
             patch.object(self.api.release, "verify_spa"), \
             patch.object(self.api.release, "verify_typed_operation") as operation, \
             patch.object(self.api, "service", return_value=service), \
             patch.object(self.api, "health", return_value=health), \
             patch.object(self.api.floor, "process", return_value=process) as observe:
            result = self.api.verify(self.stage, manifest, "http://127.0.0.1:5187", 1)
            self.assertEqual("passed", result["status"])
            self.assertFalse(result["product_ready"])
            self.assertEqual(2, operation.call_count)
            self.assertEqual(set(self.api.MAPPED), set(observe.call_args.args[1]))
            self.assertEqual(list(self.api.REQUIRED[4:]), result["unexercised_native"])
            observe.side_effect = [process, dict(process, start_ticks=100)]
            with self.assertRaisesRegex(ValueError, "process changed"):
                self.api.verify(self.stage, manifest, "http://127.0.0.1:5187", 1)

    def test_readiness_pid_mismatch_and_invalid_types_are_not_accepted(self):
        value = DeploymentReadinessTests.readiness()
        for pid in (122, True, "123", None):
            value["chess_perfcache"] = {"process_id": pid}
            with patch.object(self.api.release, "request",
                              return_value=(503, "application/json", json.dumps(value).encode())):
                with self.assertRaisesRegex(ValueError, "process differs"):
                    self.api.health("http://127.0.0.1:5187", 123)


    def test_actual_deploy_api_scope_builds_only_api_and_preserves_service_links(self):
        import shutil
        repo = self.root
        (repo / "deploy/linux").mkdir(parents=True)
        (repo / "scripts").mkdir()
        (repo / "tools").mkdir()
        (repo / "web/openapi").mkdir(parents=True)
        (repo / "web/dist").mkdir()
        (repo / "web/openapi/openapi.json").write_text("{}")
        (repo / "web/package-lock.json").write_text('{"lockfileVersion":3}')
        (repo / "web/dist/index.html").write_text('<div id="root">fresh web</div>')
        for name in ("deploy.sh", "payload-sync.sh"):
            shutil.copyfile(ROOT / "deploy/linux" / name, repo / "deploy/linux" / name)
        # The host directory ownership contract has its own real rsync controls;
        # this fixture substitutes only that privileged precondition.
        (repo / "deploy/linux/app-dir-contract.sh").write_text(
            'laplace_reconcile_app_dir_contract() { test -d "$1"; }\n'
            'laplace_require_app_dir_contract() { test -d "$1"; }\n')
        for name in ("verify-api-payload.py", "verify-application-release.py",
                     "verify-chess-floor-serving.py", "chess-floor-artifacts.py",
                     "accept-chess-environment.py"):
            shutil.copyfile(ROOT / "scripts" / name, repo / "scripts" / name)
        dotnet = repo / "tools/dotnet"
        dotnet.write_text(
            "#!/usr/bin/env python3\n"
            "import os,sys,shutil\n"
            "from pathlib import Path\n"
            "assert sys.argv[1]=='publish'\n"
            "assert Path(sys.argv[2]).name=='Laplace.Endpoints.OpenAICompat.csproj'\n"
            "destination=Path(sys.argv[sys.argv.index('-o')+1])\n"
            "shutil.copytree(os.environ['API_FIXTURE_SOURCE'],destination,dirs_exist_ok=True)\n"
            "Path(os.environ['API_TOOL_LOG']).open('a').write('dotnet api\\n')\n")
        npm = repo / "tools/npm"
        npm.write_text("#!/bin/sh\nprintf 'npm %s\\n' \"$*\" >> \"$API_TOOL_LOG\"\n")
        sudo = repo / "tools/sudo"
        sudo.write_text(
            "#!/bin/sh\n"
            "[ \"$*\" = '-n systemctl stop laplace-api' ] || exit 93\n"
            "printf 'stop api\\n' >> \"$API_TOOL_LOG\"\n")
        for tool in (dotnet, npm, sudo):
            tool.chmod(0o755)
        app = repo / "installed"
        app.mkdir()
        (app / "laplace-api.env").write_text("private configuration")
        (app / "managed-services").mkdir()
        (app / "managed-services/unit").write_text("preserved")
        for name in ("laplace-mcp", "laplace-lichess", "laplace-uci"):
            (app / name).symlink_to("existing/immutable/runtime")
        manifest = repo / "next.json"
        env = dict(os.environ, PATH=str(repo / "tools") + os.pathsep + os.environ["PATH"],
                   API_FIXTURE_SOURCE=str(self.stage), API_TOOL_LOG=str(repo / "tools.log"),
                   LAPLACE_APP_DIR=str(app), LAPLACE_API_TRANSACTION="1",
                   LAPLACE_ENGINE_BUILD=str(self.build / "engine"),
                   LAPLACE_API_PAYLOAD_MANIFEST=str(manifest))
        result = subprocess.run(["bash", str(repo / "deploy/linux/deploy.sh"), "--api-only"],
                                env=env, text=True, capture_output=True, timeout=30)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("selected-build", self.api.read_manifest(manifest)["provenance"])
        self.assertEqual('<div id="root">fresh web</div>', (app / "wwwroot/index.html").read_text())
        self.assertEqual("private configuration", (app / "laplace-api.env").read_text())
        self.assertEqual("preserved", (app / "managed-services/unit").read_text())
        for name in ("laplace-mcp", "laplace-lichess", "laplace-uci"):
            self.assertEqual("existing/immutable/runtime", os.readlink(app / name))
        log = (repo / "tools.log").read_text().splitlines()
        self.assertEqual(1, log.count("dotnet api"))
        self.assertEqual(["stop api"], [line for line in log if line.startswith("stop ")])


if __name__ == "__main__":
    unittest.main(verbosity=2)
