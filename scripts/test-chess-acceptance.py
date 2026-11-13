#!/usr/bin/env python3
"""Failure and provenance controls for the explicit acceptance orchestrator."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("chess_acceptance", ROOT / "scripts/accept-chess-environment.py")
owner = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = owner
spec.loader.exec_module(owner)


class AcceptanceTests(unittest.TestCase):
    BOOT = "01234567-89ab-cdef-0123-456789abcdef"

    def test_failed_phase_does_not_suppress_independent_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            out = Path(temporary) / "proof"
            proof = owner.Acceptance(out)
            def fail():
                raise ValueError("exact readback failed")
            self.assertFalse(proof.phase("recorded", fail))
            observed = []
            self.assertTrue(proof.phase("geometry", lambda: observed.append("measured")))
            self.assertEqual(["measured"], observed)
            self.assertEqual(1, proof.finish())
            saved = json.loads((out / "receipt.json").read_text())
            self.assertEqual("failed", saved["status"])
            self.assertEqual(["failed", "passed"], [x["status"] for x in saved["phases"]])
            self.assertNotIn("gamesPerSecond", saved)

    def test_blocked_binding_never_runs_mutating_phase_or_passes(self):
        with tempfile.TemporaryDirectory() as temporary:
            proof = owner.Acceptance(Path(temporary) / "proof")
            def forbidden():
                self.fail("blocked corpus operation was executed")
            self.assertFalse(proof.phase("corpus", forbidden, allowed=False))
            self.assertEqual(1, proof.finish())
            self.assertEqual("blocked", proof.receipt["phases"][0]["status"])

    def test_nested_http_deadline_cannot_extend_startup_deadline(self):
        started = owner.time.monotonic()
        with self.assertRaises(TimeoutError):
            with owner.deadline(.05):
                with owner.deadline(10):
                    owner.time.sleep(1)
        self.assertLess(owner.time.monotonic() - started, .75)
        self.assertEqual(0, owner.signal.getitimer(owner.signal.ITIMER_REAL)[0])

    def test_existing_evidence_directory_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            out = Path(temporary)
            (out / "receipt.json").write_text("prior")
            with self.assertRaises(FileExistsError):
                owner.Acceptance(out)
            self.assertEqual("prior", (out / "receipt.json").read_text())

    def test_command_failure_and_timeout_are_not_success(self):
        with tempfile.TemporaryDirectory() as temporary:
            out = Path(temporary)
            with self.assertRaises(RuntimeError):
                owner.command([sys.executable, "-c", "raise SystemExit(7)"], out / "exit.log", 5)
            with self.assertRaises(TimeoutError):
                owner.command([sys.executable, "-c", "import time; time.sleep(60)"], out / "timeout.log", .05)

    @unittest.skipUnless(Path("/proc").is_dir(), "requires Linux process-state evidence")
    def test_timeout_kills_resistant_descendant_after_leader_exits_and_preserves_unrelated(self):
        with tempfile.TemporaryDirectory() as temporary:
            out = Path(temporary)
            child_pid = out / "child.pid"
            child = ("import os,signal,time; from pathlib import Path; "
                     "signal.signal(signal.SIGTERM, signal.SIG_IGN); "
                     f"Path({str(child_pid)!r}).write_text(str(os.getpid())); time.sleep(60)")
            parent = ("import subprocess,sys,time; from pathlib import Path; "
                      f"subprocess.Popen([sys.executable,'-c',{child!r}]); "
                      f"p=Path({str(child_pid)!r}); "
                      "exec('while not p.exists(): time.sleep(.01)'); time.sleep(60)")
            unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"],
                                         start_new_session=True)
            try:
                with self.assertRaises(TimeoutError):
                    owner.command([sys.executable, "-c", parent], out / "group.log", 2)
                self.assertTrue(child_pid.is_file(), "resistant child must have started")
                pid = int(child_pid.read_text())
                state = Path(f"/proc/{pid}/stat")
                def active():
                    try:
                        return state.read_text().rsplit(")", 1)[1].split()[0] not in ("Z", "X")
                    except FileNotFoundError:
                        return False
                deadline = owner.time.monotonic() + 2
                while active() and owner.time.monotonic() < deadline:
                    owner.time.sleep(.01)
                self.assertFalse(active(), "owned resistant descendant survived cleanup")
                self.assertIsNone(unrelated.poll())
            finally:
                unrelated.terminate()
                unrelated.wait(timeout=5)

    def test_keyboard_interrupt_still_cleans_owned_process_group(self):
        from unittest.mock import Mock
        process = Mock(pid=123456)
        process.wait.side_effect = [KeyboardInterrupt(), 0, 0]
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(owner.subprocess, "Popen", return_value=process), \
             patch.object(owner.os, "killpg") as kill:
            with self.assertRaises(KeyboardInterrupt):
                owner.command(["fixture"], Path(temporary) / "interrupt.log", 1)
            self.assertEqual([(123456, owner.signal.SIGTERM), (123456, owner.signal.SIGKILL)],
                             [call.args for call in kill.call_args_list])

    def test_managed_binding_requires_exact_installed_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            out = Path(temporary)
            app = out / "prefix/app"
            app.mkdir(parents=True)
            (app / "Laplace.Example.dll").write_bytes(b"old")
            def publish(command, log, timeout):
                target = Path(command[-1])
                target.mkdir()
                (target / "Laplace.Example.dll").write_bytes(b"new")
                log.write_text("fixture")
            with patch.object(owner, "MANAGED", (("api", "Laplace.Example", None),)), \
                 patch.object(owner, "command", side_effect=publish), \
                 patch.dict(os.environ, {"LAPLACE_APP_DIR": str(app)}):
                with self.assertRaisesRegex(ValueError, "installed payload differs"):
                    owner.managed_binding(out, out / "prefix", out)
                (app / "Laplace.Example.dll").write_bytes(b"new")
                result = owner.managed_binding(out, out / "prefix", out)
            self.assertEqual(["api"], result["matchedServices"])
            saved = json.loads((out / "managed-binding.json").read_text())
            self.assertEqual(owner.sha256(app / "Laplace.Example.dll"),
                             saved["services"]["api"]["sha256"]["Laplace.Example.dll"])

    def startup_states(self, pid, stopped=False):
        return {name: {"pid": 0 if stopped and name != "laplace-api" else pid,
                       "startMonotonicUsec": pid * 100, "activeState": "inactive" if stopped and name != "laplace-api" else "active",
                       "subState": "running", "unitFileState": "enabled"}
                for name in ("laplace-api", "laplace-mcp", "laplace-lichess")}

    def test_startup_requires_new_process_instances_and_full_readiness(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(owner, "boot_id", return_value=self.BOOT), \
             patch.object(owner, "unit_states", side_effect=[self.startup_states(11), self.startup_states(22)]), \
             patch.object(owner, "command") as run, \
             patch.object(owner, "services", return_value={"apiAndUiReady": True, "lichessReady": True}):
            out = Path(temporary)
            proof = owner.measure_service_startup(out)
            self.assertTrue(proof["serviceRestartStartupMeasured"])
            self.assertTrue(all(x["started"] for x in proof["services"].values()))
            self.assertEqual(3, run.call_count)
            self.assertIn("verify-application-release.py", " ".join(map(str, run.call_args_list[-1].args[0])))
            saved = json.loads((out / "service-startup/receipt.json").read_text())
            self.assertEqual("passed", saved["status"])
            self.assertFalse(saved["machineColdBootMeasured"])
            self.assertEqual(self.BOOT, saved["bootIdBefore"])
            self.assertEqual(self.BOOT, saved["bootIdAfter"])
            self.assertEqual(self.BOOT, proof["bootId"])
            self.assertTrue(all(x["unitFileState"] == "enabled" for x in saved["after"].values()))

    def test_startup_cannot_claim_unchanged_pid_or_failed_full_readiness(self):
        for failing_ready in (False, True):
            with self.subTest(failing_ready=failing_ready), tempfile.TemporaryDirectory() as temporary, \
                 patch.object(owner, "boot_id", return_value=self.BOOT), \
                 patch.object(owner, "unit_states", return_value=self.startup_states(11)), \
                 patch.object(owner, "command"), \
                 patch.object(owner, "services", return_value={}, side_effect=ValueError("not ready") if failing_ready else None):
                out = Path(temporary)
                with self.assertRaises(ValueError):
                    owner.measure_service_startup(out, readiness_timeout=.02)
                self.assertEqual("failed", json.loads((out / "service-startup/receipt.json").read_text())["status"])

    def test_startup_retries_async_readiness_with_separate_attempt_evidence(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(owner, "boot_id", return_value=self.BOOT), \
             patch.object(owner, "unit_states", side_effect=[self.startup_states(11), self.startup_states(22)]), \
             patch.object(owner, "command"), patch.object(owner.time, "sleep"), \
             patch.object(owner, "services", side_effect=[ValueError("stream connecting"), {"lichessReady": True}]) as ready:
            out = Path(temporary)
            owner.measure_service_startup(out, readiness_timeout=1)
            proof = json.loads((out / "service-startup/receipt.json").read_text())
            self.assertEqual(["failed", "passed"], [x["status"] for x in proof["readinessAttempts"]])
            self.assertEqual("ValueError", proof["readinessAttempts"][0]["failureType"])
            paths = [call.args[0] for call in ready.call_args_list]
            self.assertEqual(2, len(set(paths)))
            self.assertTrue(all(path.is_dir() for path in paths))

    def test_startup_does_not_claim_preserved_stopped_managed_services(self):
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(owner, "boot_id", return_value=self.BOOT), \
             patch.object(owner, "unit_states", side_effect=[self.startup_states(11, True), self.startup_states(22, True)]), \
             patch.object(owner, "command"), patch.object(owner, "services", return_value={"lichessReady": False}):
            proof = owner.measure_service_startup(Path(temporary))
            self.assertTrue(proof["services"]["laplace-api"]["started"])
            self.assertFalse(proof["services"]["laplace-mcp"]["started"])
            self.assertFalse(proof["services"]["laplace-lichess"]["started"])


    def test_boot_identity_reads_bounded_complete_kernel_uuid(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "boot_id"
            with patch.object(owner, "BOOT_ID_PATH", path):
                for value in (self.BOOT, self.BOOT + "\n"):
                    path.write_text(value, encoding="ascii")
                    self.assertEqual(self.BOOT, owner.boot_id())
                for value in ("", "not-a-boot", self.BOOT + "\nextra", self.BOOT + " " * 100000):
                    path.write_text(value, encoding="ascii")
                    with self.subTest(length=len(value)), self.assertRaises(ValueError):
                        owner.boot_id()
                path.unlink()
                with self.assertRaises(FileNotFoundError):
                    owner.boot_id()

    def test_unit_state_preserves_observed_enablement_without_inventing_it(self):
        states = ("enabled", "disabled", None)
        responses = []
        for state in states:
            body = "MainPID=41\nExecMainStartTimestampMonotonic=123\nActiveState=active\nSubState=running\n"
            if state is not None:
                body += "UnitFileState=" + state + "\n"
            responses.append(subprocess.CompletedProcess([], 0, stdout=body))
        with patch.object(owner.subprocess, "run", side_effect=responses) as run:
            actual = owner.unit_states()
        self.assertEqual(list(states), [x["unitFileState"] for x in actual.values()])
        for call in run.call_args_list:
            self.assertIn("UnitFileState", call.args[0][-1])
            self.assertEqual(15, call.kwargs["timeout"])
            self.assertTrue(call.kwargs["check"])

    def test_status_receipt_binds_boot_and_actual_unit_file_states(self):
        payload = {"configured": True, "running": True, "connected": True,
                   "account": {"tokenValid": True, "botAccount": True,
                               "botPlayScope": True, "ready": True}}
        replies = [(b'{"status":"ok"}', {}), (b"<html>installed</html>", {"contentType": "text/html"}),
                   (json.dumps(payload).encode(), {})]
        states = {"laplace-api": "enabled", "laplace-mcp": "disabled", "laplace-lichess": "static"}
        def systemctl(argv, log, timeout):
            self.assertEqual(["systemctl", "show"], argv[:2])
            self.assertIn("UnitFileState", argv[-1])
            log.write_text("Id=" + argv[2] + "\nUnitFileState=" + states[argv[2]] + "\n")
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(owner, "boot_id", return_value=self.BOOT), \
             patch.object(owner, "response", side_effect=replies), \
             patch.object(owner, "command", side_effect=systemctl):
            out = Path(temporary)
            result = owner.services(out)
            saved = json.loads((out / "services.json").read_text())
        self.assertTrue(result["apiAndUiReady"])
        self.assertTrue(result["lichessReady"])
        self.assertEqual(self.BOOT, saved["bootId"])
        self.assertEqual(states, saved["unitFileStates"])
        self.assertFalse(saved["coldBootMeasured"])

    def test_startup_retains_changed_boot_as_failed_restart_observation(self):
        after_boot = "fedcba98-7654-3210-fedc-ba9876543210"
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(owner, "boot_id", side_effect=[self.BOOT, after_boot]), \
             patch.object(owner, "unit_states", side_effect=[self.startup_states(11), self.startup_states(22)]), \
             patch.object(owner, "command"), \
             patch.object(owner, "services", return_value={"apiAndUiReady": True, "lichessReady": True}):
            out = Path(temporary)
            with self.assertRaisesRegex(ValueError, "boot identity changed"):
                owner.measure_service_startup(out)
            saved = json.loads((out / "service-startup/receipt.json").read_text())
        self.assertEqual("failed", saved["status"])
        self.assertEqual(self.BOOT, saved["bootIdBefore"])
        self.assertEqual(after_boot, saved["bootIdAfter"])
        self.assertIn("wallSecondsIncludingFailedReadiness", saved)
        self.assertFalse(saved["machineColdBootMeasured"])

    def test_lichess_process_health_is_not_account_stream_readiness(self):
        status = owner.lichess_status({"configured": True, "running": True, "connected": False,
                                      "account": {"tokenValid": True, "botAccount": True,
                                                  "botPlayScope": True, "ready": True}})
        self.assertFalse(status["ready"])
        self.assertFalse(owner.lichess_status({"running": True})["ready"])

    def test_lichess_receipt_drops_auth_and_free_text(self):
        status = owner.lichess_status({"configured": True, "running": True, "connected": True,
                                      "tokenPreview": "secret-preview", "logs": ["secret-log"],
                                      "account": {"tokenValid": True, "botAccount": True,
                                                  "botPlayScope": True, "ready": True,
                                                  "token": "secret-token"}})
        self.assertTrue(status["ready"])
        self.assertNotIn("secret", json.dumps(status))
        status["account"]["ready"] = False

    def test_public_summary_retains_validated_rates_but_never_echoes_raw_error_or_auth(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as temporary:
            out = Path(temporary)
            path = out / "recorded/recorded-chess"
            path.mkdir(parents=True)
            (path / "receipt.json").write_text(json.dumps({
                "status": "passed", "targetMet": False, "durationQualified": True,
                "error": "secret-error", "tokenPreview": "secret-token", "cases": [],
                "sustained": {"status": "passed", "durationQualified": True,
                              "elapsedSeconds": 31, "verifiedRecordedGames": 72,
                              "gamesPerSecondSustainedEndToEnd": 72 / 31}}))
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                owner.public_summary("recorded", out)
            summary = json.loads(output.getvalue().split(" ", 1)[1])
            self.assertEqual(72, summary["sustained"]["verifiedRecordedGames"])
            self.assertEqual(72 / 31, summary["sustained"]["gamesPerSecondSustainedEndToEnd"])
            self.assertFalse(summary["targetMet"])
            self.assertNotIn("secret", output.getvalue())

    def test_http_observer_rejects_remote_or_credentialed_endpoint(self):
        for base in ("https://lichess.org", "http://user:pass@127.0.0.1:5187",
                     "http://127.0.0.1:5187?token=secret"):
            with self.subTest(base=base), self.assertRaises(ValueError):
                owner.response(base, "/health", 65536)

    def test_existing_archive_directory_is_not_reported_absent(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)
            (path / "src").mkdir()
            (path / "src/Makefile").write_text("do not read")
            result = owner.source_directory_inventory(path)
            self.assertTrue(result["exists"])
            self.assertEqual("directory", result["kind"])
            self.assertTrue(result["sourceLayout"]["src/Makefile"])
            self.assertEqual([], result["nestedGitCandidates"])
            self.assertNotIn("do not read", json.dumps(result))

    def test_nested_checkout_clears_inherited_git_state_and_sanitizes_origin(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)
            nested = path / "Stockfish"
            (nested / ".git").mkdir(parents=True)
            calls = []
            def git(command, **kwargs):
                calls.append(kwargs["env"])
                if command[-1] == "--is-inside-work-tree":
                    return "true\n"
                if command[-1] == "HEAD":
                    return "a" * 40 + "\n"
                return "https://user:secret@example.invalid/repo\n"
            with patch.object(subprocess, "check_output", side_effect=git), \
                 patch.dict(os.environ, {"GIT_DIR": "/wrong", "GIT_WORK_TREE": "/wrong"}):
                result = owner.source_directory_inventory(path)
            item = result["nestedGitCandidates"][0]
            self.assertTrue(item["verifiedCheckout"])
            self.assertFalse(item["officialOrigin"])
            self.assertEqual("a" * 40, item["commit"])
            self.assertNotIn("secret", json.dumps(result))
            self.assertTrue(all("GIT_DIR" not in env and "GIT_WORK_TREE" not in env for env in calls))

    def test_inventory_is_bounded_and_does_not_follow_directory_symlinks(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)
            for i in range(150):
                (path / str(i)).mkdir()
            (path / "loop").symlink_to(path, target_is_directory=True)
            result = owner.source_directory_inventory(path)
            self.assertEqual(128, len(result["entries"]))
            self.assertTrue(result["entriesTruncated"])
            self.assertLessEqual(result["directoriesScanned"], 128)


    def test_capacity_preparation_finishes_before_measured_admission(self):
        calls = []
        with tempfile.TemporaryDirectory() as temporary:
            proof = owner.Acceptance(Path(temporary) / "proof")
            def run(argv, log, timeout):
                calls.append((list(map(str, argv)), timeout))
                mode = argv[2]
                output = Path(argv[argv.index("--output-dir") + 1])
                output.mkdir()
                if mode == "prepare":
                    owner.save(output / "corpus.json", {"status": "prepared", "jobs": [], "requestedGames": 48})
                else:
                    corpus = Path(argv[argv.index("--corpus-dir") + 1])
                    self.assertEqual("prepared", json.loads((corpus / "corpus.json").read_text())["status"])
                    owner.save(output / "receipt.json", {"status": "passed", "measurement": {
                        "newlyRecordedPlayings": 48, "observedNewPlayingsPerSecond": 16,
                        "sustainedIngestionCapacityEstablished": False, "targetVerdict": "unqualified"}})
            with patch.object(owner, "command", side_effect=run):
                self.assertTrue(owner.retained_capacity(proof, sys.executable, allowed=True))
            self.assertEqual(["prepare", "measure"], [argv[2] for argv, _ in calls])
            self.assertEqual([3630, 3630], [timeout for _, timeout in calls])
            self.assertEqual(["passed", "passed"], [row["status"] for row in proof.receipt["phases"]])
            self.assertNotIn("--games-per-job", calls[1][0])
            self.assertEqual(0, proof.finish())

    def test_failed_or_blocked_capacity_preparation_never_admits_a_partial_pool(self):
        for allowed in (True, False):
            with self.subTest(allowed=allowed), tempfile.TemporaryDirectory() as temporary:
                proof = owner.Acceptance(Path(temporary) / "proof")
                def fail(argv, log, timeout):
                    self.assertEqual("prepare", argv[2])
                    output = Path(argv[argv.index("--output-dir") + 1])
                    output.mkdir()
                    owner.save(output / "corpus.json", {"status": "failed", "jobs": []})
                    raise RuntimeError("generation failed")
                with patch.object(owner, "command", side_effect=fail) as call:
                    self.assertFalse(owner.retained_capacity(proof, sys.executable, allowed=allowed))
                self.assertEqual(1 if allowed else 0, call.call_count)
                self.assertEqual(["failed" if allowed else "blocked", "blocked"],
                                 [row["status"] for row in proof.receipt["phases"]])
                self.assertEqual(1, proof.finish())
                if allowed:
                    self.assertTrue((proof.output / "retained-capacity-corpus/corpus.json").is_file())

    def test_capacity_summary_keeps_admission_scope_and_unqualified_result(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            out = root / "retained-capacity-measurement"
            out.mkdir()
            owner.save(out / "receipt.json", {"status": "passed", "requestedGames": 48,
                "admissionConcurrency": 1, "error": "secret", "measurement": {
                    "newlyRecordedPlayings": 48, "observedNewPlayingsPerSecond": 16,
                    "minimumDurationReached": False, "exactReplayControlsPassed": True,
                    "sustainedIngestionCapacityEstablished": False, "targetVerdict": "unqualified",
                    "contentInventory": {"playings": 48, "distinctOrderedLines": 2},
                    "writer": {"entitiesInserted": 5, "physicalitiesInserted": 7}}})
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                owner.public_summary("retained-capacity-measure", root)
            result = json.loads(output.getvalue().split(" ", 1)[1])
            self.assertIn("generation excluded", result["scope"])
            self.assertEqual("unqualified", result["measurement"]["targetVerdict"])
            self.assertFalse(result["measurement"]["sustainedIngestionCapacityEstablished"])
            self.assertEqual(16, result["measurement"]["observedNewPlayingsPerSecond"])
            self.assertEqual(5, result["measurement"]["writer"]["entitiesInserted"])
            self.assertEqual(7, result["measurement"]["writer"]["physicalitiesInserted"])
            self.assertNotIn("secret", output.getvalue())


    def test_x11_dependency_selection_never_claims_gui_interaction(self):
        import contextlib
        import io
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            owner.save(root / "x11-runtime.json", {
                "schema": "laplace.x11-runtime-selection/v1", "status": "tools-selected",
                "mode": "private", "gui_ready": False, "host_packages_installed": False,
                "initially_missing_tools": ["Xvfb", "xdotool"], "selection_sha256": "a" * 64,
                "tools": {"Xvfb": "/private/usr/bin/Xvfb", "xdotool": "/private/usr/bin/xdotool"},
                "private_runtime": {"runtime_id": "b" * 64, "manifest_sha256": "c" * 64,
                    "packages": [{"name": "xvfb", "version": "fixture-1", "architecture": "amd64",
                                  "sha256": "d" * 64}]},
                "error": "secret", "tokenPreview": "secret"})
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                owner.public_summary("gui-x11-runtime", root)
            result = json.loads(output.getvalue().split(" ", 1)[1])
            self.assertEqual("tools-selected", result["status"])
            self.assertFalse(result["gui_ready"])
            self.assertFalse(result["host_packages_installed"])
            self.assertEqual(["Xvfb", "xdotool"], result["initiallyMissingTools"])
            self.assertEqual("xvfb", result["privateRuntime"]["packages"][0]["name"])
            self.assertNotIn("virtual_x11_interaction_verified", result)
            self.assertNotIn("secret", output.getvalue())


    def test_early_complete_retained_control_does_not_suppress_independent_rate_sweep(self):
        with tempfile.TemporaryDirectory() as temporary:
            proof = owner.Acceptance(Path(temporary) / "proof")
            calls = []
            def run(argv, log, timeout):
                args = list(map(str, argv))
                calls.append((args, timeout))
                if "scripts/benchmark-retained-chess-ingestion.py" in args:
                    raise RuntimeError("complete-game readback failed")
            with patch.object(owner, "command", side_effect=run):
                owner.recording_checks(proof, sys.executable, allowed=True)
            self.assertEqual(2, len(calls))
            retained, recorded = calls
            self.assertIn("scripts/benchmark-retained-chess-ingestion.py", retained[0])
            self.assertEqual("16", retained[0][retained[0].index("--games") + 1])
            self.assertEqual("2", retained[0][retained[0].index("--replays") + 1])
            self.assertEqual(930, retained[1])
            self.assertEqual("24", recorded[0][recorded[0].index("--recorded-games") + 1])
            self.assertEqual("1,2,4", recorded[0][recorded[0].index("--recorded-concurrency") + 1])
            self.assertEqual("3", recorded[0][recorded[0].index("--repeats") + 1])
            self.assertEqual(7230, recorded[1])
            self.assertEqual([("retained", "failed"), ("recorded", "passed")],
                             [(row["name"], row["status"]) for row in proof.receipt["phases"]])
            self.assertEqual(1, proof.finish())


if __name__ == "__main__":
    unittest.main(verbosity=2)
