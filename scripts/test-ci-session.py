#!/usr/bin/env python3
"""Real session IPC, flock, process cancellation and exact-source contracts."""
from __future__ import annotations
import fcntl
import json
import os
from pathlib import Path
import signal
import select
import subprocess
import sys
import tempfile
import time
import unittest

HELPER = Path(__file__).with_name("ci-session.py").resolve()


class SessionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.checkout = self.root / "repo"
        (self.checkout / "scripts").mkdir(parents=True)
        self.directory = self.root / "session"
        self.lock = self.root / "host.lock"
        self.env = dict(os.environ, GITHUB_RUN_ID="123", GITHUB_RUN_ATTEMPT="2", GITHUB_JOB="session-test",
                        GITHUB_REPOSITORY="test/Laplace", RUNNER_TRACKING_ID="test-tracked-child",
                        GITHUB_ENV=str(self.root / "github-env"), CI_FIXTURE_ROOT=str(self.root), SECRET_FIXTURE="not-in-receipt")
        self.script = self.checkout / "scripts/pr-proof.sh"
        self.script.write_text('''#!/bin/bash
set -eu
if [[ "${*: -1}" == --list-phases ]]; then printf '%s\\n' first second third; exit; fi
phase="${*: -1}"
printf '%s:%s:%s\\n' "$phase" "${LAPLACE_CI_COMPLETED_PHASES:-}" "$RUNNER_TRACKING_ID" >> "$CI_FIXTURE_ROOT/executed"
echo "visible $phase output"
if [[ -f "$CI_FIXTURE_ROOT/redirect" ]]; then
    exec >/dev/null 2>&1
    sleep 0.2
    touch "$CI_FIXTURE_ROOT/redirect-complete"
fi
if [[ -f "$CI_FIXTURE_ROOT/bulk" ]]; then head -c 131072 /dev/zero | tr '\\0' x; fi
if [[ -f "$CI_FIXTURE_ROOT/fail" && "$phase" == second ]]; then exit 17; fi
if [[ -f "$CI_FIXTURE_ROOT/wait" && "$phase" == first ]]; then
    sleep 60 &
    echo "$!" > "$CI_FIXTURE_ROOT/grandchild"
    wait
fi
''')
        (self.checkout / "scripts/pr-db-proof.sh").write_text('''#!/bin/bash
set -eu
[[ "$*" == '--phase cleanup' ]]
echo cleanup >> "$CI_FIXTURE_ROOT/cleaned"
''')
        subprocess.run(["git", "init", "-q", str(self.checkout)], check=True)
        self.git("add", ".")
        self.git("-c", "user.name=CI Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture")
        self.addCleanup(self.stop)

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.checkout), *args], check=True, capture_output=True)

    def invoke(self, operation, *args, env=None, timeout=20):
        return subprocess.run([sys.executable, str(HELPER), operation, "--directory", str(self.directory), *args],
                              env=env or self.env, capture_output=True, text=True, timeout=timeout)

    def start(self, *args):
        result = self.invoke("start", "--checkout", str(self.checkout), "--kind", "pr", "--stage", "all", "--lock", str(self.lock), *args)
        self.assertEqual(0, result.returncode, result.stderr)
        return result

    def stop(self):
        if (self.directory / "session.json").exists():
            result = self.invoke("stop", timeout=150)
            if result.returncode:
                self.fail(result.stderr)

    def state(self):
        return json.loads((self.directory / "session.json").read_text())

    def assert_lock(self, held):
        with self.lock.open("a") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                actual = False
            except BlockingIOError:
                actual = True
        self.assertEqual(held, actual)

    def await_condition(self, condition, timeout=10):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if condition():
                return
            time.sleep(.05)
        self.fail("condition did not complete")

    def live(self, pid):
        try:
            descriptor = os.pidfd_open(pid)
            try:
                return not select.select([descriptor], [], [], 0)[0]
            finally:
                os.close(descriptor)
        except ProcessLookupError:
            return False

    def running(self):
        (self.root / "wait").touch()
        self.start()
        child = subprocess.Popen([sys.executable, str(HELPER), "run", "--directory", str(self.directory), "--phase", "first"],
                                 env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(lambda: child.poll() is None and child.kill())
        self.await_condition(lambda: (self.root / "grandchild").exists())
        return child, int((self.root / "grandchild").read_text())

    def test_order_source_identity_logs_environment_and_continuous_lock(self):
        self.start()
        self.assert_lock(True)
        self.assertEqual("LAPLACE_CI_PHASES=|first|second|third|\n", (self.root / "github-env").read_text())
        rejected = self.invoke("run", "--phase", "second")
        self.assertEqual(2, rejected.returncode)
        self.assertFalse((self.root / "executed").exists())
        for phase in ("first", "second", "third"):
            result = self.invoke("run", "--phase", phase)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn(f"visible {phase} output", result.stdout)
            self.assert_lock(True)
        self.assertEqual(["first:||:test-tracked-child", "second:|first|:test-tracked-child", "third:|first|second|:test-tracked-child"],
                         (self.root / "executed").read_text().splitlines())
        self.assertNotIn("not-in-receipt", (self.directory / "session.json").read_text())
        self.stop()
        self.assert_lock(False)
        self.assertEqual("cleanup\n", (self.root / "cleaned").read_text())
        self.assertEqual(2, self.invoke("run", "--phase", "first").returncode)

    def test_stdout_eof_does_not_kill_work_still_running(self):
        (self.root / "redirect").touch()
        self.start()
        result = self.invoke("run", "--phase", "first")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue((self.root / "redirect-complete").exists())
        self.assert_lock(True)

    def test_output_larger_than_pipe_capacity_streams_without_loss(self):
        (self.root / "bulk").touch()
        self.start()
        result = self.invoke("run", "--phase", "first")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("visible first output\n" + "x" * 131072, result.stdout)

    def test_failure_retains_real_exit_code_blocks_remaining_and_cleans(self):
        (self.root / "fail").touch()
        self.start()
        self.assertEqual(0, self.invoke("run", "--phase", "first").returncode)
        self.assertEqual(17, self.invoke("run", "--phase", "second").returncode)
        self.assertEqual(2, self.invoke("run", "--phase", "third").returncode)
        self.await_condition(lambda: not self.live(self.state()["supervisor"]["pid"]))
        self.assert_lock(False)
        self.assertEqual("cleanup\n", (self.root / "cleaned").read_text())
        self.assertEqual(17, self.state()["results"][-1]["exit_code"])

    def test_cancelled_client_terminates_actual_grandchild_and_releases_lock(self):
        client, grandchild = self.running()
        self.assertTrue(self.live(grandchild))
        client.terminate()
        client.communicate(timeout=20)
        self.await_condition(lambda: not self.live(grandchild))
        self.await_condition(lambda: not self.live(self.state()["supervisor"]["pid"]))
        self.assert_lock(False)
        self.assertEqual("cleanup\n", (self.root / "cleaned").read_text())

    def test_stop_terminates_active_phase(self):
        client, grandchild = self.running()
        self.stop()
        client.communicate(timeout=20)
        self.assertFalse(self.live(grandchild))
        self.assert_lock(False)

    def test_supervisor_sigkill_guardian_terminates_group_and_cleans(self):
        client, grandchild = self.running()
        os.kill(self.state()["supervisor"]["pid"], signal.SIGKILL)
        client.communicate(timeout=20)
        self.await_condition(lambda: not self.live(grandchild))
        self.await_condition(lambda: self.state()["status"] == "failed")
        self.assert_lock(False)
        self.assertEqual("cleanup\n", (self.root / "cleaned").read_text())

    def test_idle_expiration_releases_host_lock(self):
        self.start("--idle-timeout-seconds", "0.3")
        self.await_condition(lambda: self.state()["status"] == "failed")
        self.assert_lock(False)
        self.assertIn("idle timeout", self.state()["failure"])

    def test_phase_timeout_terminates_child(self):
        (self.root / "wait").touch()
        self.start("--phase-timeout-seconds", "0.5")
        result = self.invoke("run", "--phase", "first")
        self.assertEqual(124, result.returncode, result.stderr)
        self.assertFalse(self.live(int((self.root / "grandchild").read_text())))
        self.await_condition(lambda: not self.live(self.state()["supervisor"]["pid"]))
        self.assert_lock(False)

    def test_foreign_job_and_changed_checkout_are_rejected(self):
        self.start()
        result = self.invoke("run", "--phase", "first", env=dict(self.env, GITHUB_RUN_ATTEMPT="3"))
        self.assertEqual(2, result.returncode)
        self.assertIn("another run", result.stderr)
        self.script.write_text(self.script.read_text() + "\n# tracked modification\n")
        result = self.invoke("run", "--phase", "first")
        self.assertEqual(2, result.returncode)
        self.assertIn("tracked changes", result.stderr)
        self.assertFalse((self.root / "executed").exists())

    def test_stale_pid_never_signals_foreign_process(self):
        self.start()
        original = self.state()
        altered = dict(original, supervisor={"pid": os.getpid(), "start": "wrong-generation"})
        (self.directory / "session.json").write_text(json.dumps(altered))
        result = self.invoke("stop")
        self.assertEqual(2, result.returncode)
        self.assertIn("stale", result.stderr)
        (self.directory / "session.json").write_text(json.dumps(original))

    def test_cancelling_lock_wait_does_not_leave_waiter(self):
        with self.lock.open("a") as owner:
            fcntl.flock(owner, fcntl.LOCK_EX)
            client = subprocess.Popen([sys.executable, str(HELPER), "start", "--directory", str(self.directory),
                                       "--checkout", str(self.checkout), "--kind", "pr", "--lock", str(self.lock)],
                                      env=self.env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            self.await_condition(lambda: (self.directory / "session.json").exists() and self.state().get("supervisor"))
            client.terminate()
            client.communicate(timeout=20)
        self.await_condition(lambda: self.state()["status"] == "failed")
        self.assert_lock(False)


if __name__ == "__main__":
    unittest.main()
