#!/usr/bin/env python3
"""Real filesystem/process publication controls; no corpus or native chess claim."""
import importlib.util
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    value = importlib.util.module_from_spec(spec)
    sys.modules[name] = value
    spec.loader.exec_module(value)
    return value


driver = module("qualified_floor_export", "export-qualified-chess-floors.py")
runtime = module("export_process_owner", "accept-chess-environment.py")
fixtures = module("export_artifact_fixtures", "test-chess-floor-artifacts.py")
artifact = fixtures.owner


class ExportLifecycleTests(unittest.TestCase):
    def setUp(self):
        root = os.environ.get("TMPDIR")
        if not root or not Path(root).is_absolute() or not Path(root).is_dir():
            self.fail("TMPDIR must select an existing permanent test workspace")
        self.temp = tempfile.TemporaryDirectory(prefix="qualified-floor-export-", dir=root)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        # Reuse the artifact owner's explicitly synthetic complete-file fixture.
        # It supplies framing for publisher state checks, not a chess acceptance.
        self.fixture = fixtures.ArtifactTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.environment = mock.patch.dict(os.environ)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        os.environ.pop("LAPLACE_CHESS_CORPUS_EXPORT", None)
        os.environ["PYTHONDONTWRITEBYTECODE"] = "1"

    def publish(self, receipt):
        subprocess.run([sys.executable, str(ROOT / "scripts/chess-floor-artifacts.py"),
                        "select-export", "--receipt", str(receipt),
                        "--prefix", str(self.fixture.prefix)],
                       check=True, timeout=10, stdout=subprocess.DEVNULL,
                       stderr=subprocess.PIPE)

    def test_atomic_publication_then_failure_retains_true_and_original_error(self):
        receipt, _ = self.fixture.fixture_export()
        exported = artifact.validate_export(receipt)
        failure = RuntimeError("protocol failure after the publisher exited")
        proof = {"status": "incomplete", "selection_completed": False}
        def publish_then_fail():
            self.publish(receipt)
            raise failure
        with self.assertRaises(RuntimeError) as caught:
            driver.publish_selection(artifact, runtime, self.fixture.prefix,
                                     exported, proof, publish_then_fail)
        self.assertIs(failure, caught.exception)
        self.assertEqual("failed", proof["status"])
        self.assertTrue(proof["selection_completed"])
        self.assertEqual("matching-export", proof["selection_observation"])
        path = self.fixture.prefix / "etc/chess-corpus-export.json"
        self.assertEqual({"path": str(path), "sha256": artifact.sha256(path)}, proof["selection"])
        self.assertEqual(exported, artifact.selected_export(self.fixture.prefix))

    def test_failure_before_publication_observes_absence_or_different_export(self):
        receipt, _ = self.fixture.fixture_export("wanted")
        exported = artifact.validate_export(receipt)
        other, _ = self.fixture.fixture_export("prior")
        failure = RuntimeError("protocol failure before publication")
        for existing in (False, True):
            with self.subTest(existing=existing):
                if existing:
                    self.publish(other)
                proof = {"status": "incomplete"}
                def fail():
                    raise failure
                with self.assertRaises(RuntimeError) as caught:
                    driver.publish_selection(artifact, runtime, self.fixture.prefix,
                                             exported, proof, fail)
                self.assertIs(failure, caught.exception)
                self.assertEqual("failed", proof["status"])
                self.assertIs(False, proof["selection_completed"])
                self.assertEqual("different-export" if existing else "no-persisted-selection",
                                 proof["selection_observation"])

    def test_failed_readback_is_unknown_and_preserves_primary_failure(self):
        receipt, _ = self.fixture.fixture_export()
        exported = artifact.validate_export(receipt)
        failure = RuntimeError("protocol failure after publication and file corruption")
        proof = {"status": "incomplete"}
        def publish_corrupt_then_fail():
            self.publish(receipt)
            (receipt.parent / "positions.txt").write_bytes(b"changed")
            raise failure
        with self.assertRaises(RuntimeError) as caught:
            driver.publish_selection(artifact, runtime, self.fixture.prefix,
                                     exported, proof, publish_corrupt_then_fail)
        self.assertIs(failure, caught.exception)
        self.assertEqual("failed", proof["status"])
        self.assertIsNone(proof["selection_completed"])
        self.assertEqual("unknown", proof["selection_observation"])
        self.assertEqual("ValueError", proof["selection_readback_failure_type"])
        self.assertTrue((self.fixture.prefix / "etc/chess-corpus-export.json").is_file())

    def test_sigterm_keeps_actual_flock_until_child_cleanup_and_finalization(self):
        helper = self.root / "locked-driver.py"
        helper.write_text(
            "import importlib.util, os, signal, sys, time\n"
            "from pathlib import Path\n"
            "root, work = Path(sys.argv[1]), Path(sys.argv[2])\n"
            "spec = importlib.util.spec_from_file_location('actual_process_owner', root / 'scripts/accept-chess-environment.py')\n"
            "owner = importlib.util.module_from_spec(spec); spec.loader.exec_module(owner)\n"
            "def interrupted(signum, frame): raise KeyboardInterrupt('protocol cancellation')\n"
            "signal.signal(signal.SIGTERM, interrupted)\n"
            "child = \"import os,signal,sys,time; from pathlib import Path; signal.signal(signal.SIGTERM, signal.SIG_IGN); Path(sys.argv[1]).write_text(str(os.getpid())); time.sleep(60)\"\n"
            "try:\n"
            "    owner.command([sys.executable, '-c', child, str(work / 'child-ready')], work / 'child.log', 30)\n"
            "finally:\n"
            "    (work / 'finalizing').write_text('owned child cleanup returned')\n"
            "    end = time.monotonic() + 5\n"
            "    while not (work / 'release-finalization').exists():\n"
            "        if time.monotonic() >= end: raise TimeoutError('test finalization release missing')\n"
            "        time.sleep(0.01)\n"
            "    (work / 'finalized').write_text('complete')\n",
            encoding="utf-8")
        lock = self.root / "host-resource.lock"
        process = subprocess.Popen(
            ["flock", "--exclusive", "--no-fork", "--timeout", "5", str(lock),
             "bash", "-c", 'exec "$@"', "owned-export",
             sys.executable, str(helper), str(ROOT), str(self.root)],
            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, start_new_session=True)
        child_pid = None
        def wait_file(name, seconds):
            path = self.root / name
            deadline = time.monotonic() + seconds
            while not path.exists():
                if process.poll() is not None or time.monotonic() >= deadline:
                    self.fail("locked process did not reach " + name)
                time.sleep(0.01)
            return path
        def competing_lock():
            return subprocess.run(["flock", "--exclusive", "--nonblock", str(lock), "true"],
                                  check=False, timeout=2, stdout=subprocess.DEVNULL,
                                  stderr=subprocess.DEVNULL).returncode
        try:
            child_pid = int(wait_file("child-ready", 5).read_text())
            self.assertEqual(1, competing_lock())
            os.killpg(process.pid, signal.SIGTERM)
            # The child lives in its separately owned session and ignores TERM.
            # Its owner must keep the lock throughout the actual five-second cleanup.
            self.assertEqual(1, competing_lock())
            os.kill(child_pid, 0)
            wait_file("finalizing", 12)
            with self.assertRaises(ProcessLookupError):
                os.kill(child_pid, 0)
            self.assertEqual(1, competing_lock())
            (self.root / "release-finalization").write_text("release")
            process.communicate(timeout=5)
            self.assertNotEqual(0, process.returncode)
            self.assertEqual("complete", (self.root / "finalized").read_text())
            self.assertEqual(0, competing_lock())
        finally:
            (self.root / "release-finalization").touch()
            if child_pid is not None:
                try:
                    os.killpg(child_pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            if process.poll() is None:
                process.kill()
            process.communicate(timeout=5)


if __name__ == "__main__":
    unittest.main()
