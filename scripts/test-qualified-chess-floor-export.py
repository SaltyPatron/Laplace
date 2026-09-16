#!/usr/bin/env python3
"""Real filesystem/process publication controls; no corpus or native chess claim."""
import importlib.util
import copy
import hashlib
import json
import os
import re
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import textwrap
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


class RetainedBuildTests(unittest.TestCase):
    """Real checkout/build files exercise the current placement contract."""
    def setUp(self):
        root = os.environ.get("TMPDIR")
        if not root or not Path(root).is_absolute() or not Path(root).is_dir():
            self.fail("TMPDIR must select an existing permanent test workspace")
        self.temp = tempfile.TemporaryDirectory(prefix="native-floor-qualification-", dir=root)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.checkout = self.root / "persistent-checkout"
        self.checkout.mkdir()
        self.build_root = self.root / "build"
        self.build = self.build_root / ("laplace-" + hashlib.sha256(os.fsencode(self.checkout)).hexdigest()[:16])
        (self.build / ".stamps").mkdir(parents=True)
        (self.checkout / "build").symlink_to(self.build, target_is_directory=True)
        (self.build / "CMakeCache.txt").write_text(
            "CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.checkout) + "\n"
            "CMAKE_CACHEFILE_DIR:INTERNAL=" + str(self.build) + "\n")
        for name in ("build-native", "install-native"):
            (self.build / ".stamps" / name).write_text("a" * 64 + "\n")
        self.plan = {"proof_run_id": 123, "proof_run_attempt": 1,
                     "candidate_commit": "1" * 40, "candidate_tree": "2" * 40,
                     "installed_source": "1" * 40}
        patcher = mock.patch.object(driver, "BUILD_ROOT", self.build_root)
        patcher.start()
        self.addCleanup(patcher.stop)

    def qualify(self):
        return driver.retained_build(self.checkout)

    def test_implicit_or_main_session_proof_cannot_replace_native_installation(self):
        with mock.patch.object(driver, "run_json") as remote:
            for plan in (None, [], self.plan, {**self.plan, "proof_kind": "main-lifecycle"},
                         {**self.plan, "proof_kind": "unsupported"}):
                with self.subTest(plan=plan), self.assertRaisesRegex(
                        ValueError, "authenticated native-only installation"):
                    driver.qualification(plan, ROOT)
            remote.assert_not_called()

    def test_actual_workflow_selects_existing_git_checkout_without_mutation(self):
        def git_command(*args):
            return subprocess.check_output(
                ["git", "-C", str(self.checkout), *args], text=True,
                stderr=subprocess.PIPE, timeout=10).strip()
        git_command("init", "-q")
        tracked = self.checkout / "selected.txt"
        tracked.write_text("selected source\n")
        git_command("add", "selected.txt")
        git_command("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid",
                    "commit", "-q", "-m", "source fixture")
        plan = {**self.plan, "candidate_commit": git_command("rev-parse", "HEAD"),
                "candidate_tree": git_command("rev-parse", "HEAD^{tree}"),
                "proof_kind": "native-only-install", "native_checkout": str(self.checkout)}
        plan["installed_source"] = plan["candidate_commit"]
        selected = self.root / "selection.json"
        selected.write_text(json.dumps(plan))
        workflow = (ROOT / ".github/workflows/chess-floor-export.yml").read_text()
        match = re.search(r'<< "PY_SOURCE"\n(.*?)\n          PY_SOURCE', workflow, re.DOTALL)
        self.assertIsNotNone(match)
        program = textwrap.dedent(match.group(1))
        before = (git_command("rev-parse", "HEAD"), git_command("worktree", "list", "--porcelain"),
                  git_command("show-ref", "--head"), tracked.read_bytes(),
                  os.readlink(self.checkout / "build"))
        command = [sys.executable, "-", str(selected),
                   str(ROOT / "scripts/export-qualified-chess-floors.py")]
        result = subprocess.run(command, input=program, text=True, capture_output=True,
                                timeout=10, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(str(self.checkout), result.stdout.strip())
        self.assertEqual(before, (git_command("rev-parse", "HEAD"),
                                 git_command("worktree", "list", "--porcelain"),
                                 git_command("show-ref", "--head"), tracked.read_bytes(),
                                 os.readlink(self.checkout / "build")))
        for mutation in ({"candidate_commit": "f" * 40, "installed_source": "f" * 40},
                         {"candidate_tree": "f" * 40},
                         {"native_checkout": "relative-checkout"},
                         {"native_checkout": str(self.root / "missing")},
                         {"proof_kind": "main-lifecycle"}):
            with self.subTest(mutation=mutation), self.assertRaises(
                    (ValueError, OSError)):
                driver.candidate_checkout({**plan, **mutation})
        alias = self.root / "checkout-alias"
        alias.symlink_to(self.checkout, target_is_directory=True)
        with self.assertRaises(ValueError):
            driver.candidate_checkout({**plan, "native_checkout": str(alias)})
        tracked.write_text("uncommitted change\n")
        selected.write_text(json.dumps(plan))
        refused = subprocess.run(command, input=program, text=True, capture_output=True,
                                 timeout=10, env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
        self.assertNotEqual(0, refused.returncode)
        self.assertEqual("uncommitted change\n", tracked.read_text())
        self.assertEqual(before[:3], (git_command("rev-parse", "HEAD"),
                                     git_command("worktree", "list", "--porcelain"),
                                     git_command("show-ref", "--head")))

    def test_selection_rejects_mutable_incomplete_and_different_installed_sources(self):
        self.assertEqual({"commit": self.plan["candidate_commit"], "tree": self.plan["candidate_tree"]},
                         driver.selection_identity(self.plan))
        invalid = [None, [], "main"]
        for key in ("candidate_commit", "candidate_tree", "installed_source"):
            for value in (None, 123, "", "main", "a" * 39, "A" * 40, "a" * 41):
                invalid.append({**self.plan, key: value})
        invalid.append({**self.plan, "installed_source": "f" * 40})
        for plan in invalid:
            with self.subTest(plan=plan), self.assertRaises(ValueError):
                driver.selection_identity(plan)

    def test_retained_build_follows_placement_link_despite_obsolete_directory(self):
        legacy = self.build_root / ("legacy-" + hashlib.sha256(os.fsencode(self.checkout)).hexdigest()[:16])
        legacy.mkdir()
        # A leftover historical directory must not replace the actual placed build.
        (legacy / "CMakeCache.txt").write_text("obsolete unrelated build\n")
        link_before = os.readlink(self.checkout / "build")
        build, fingerprint = self.qualify()
        self.assertEqual(self.build, build)
        self.assertEqual("a" * 64, fingerprint)
        self.assertEqual(link_before, os.readlink(self.checkout / "build"))
        self.assertEqual("obsolete unrelated build\n", (legacy / "CMakeCache.txt").read_text())

    def test_retained_build_requires_placement_link_inside_canonical_root(self):
        link = self.checkout / "build"
        link.unlink()
        with self.assertRaisesRegex(ValueError, "build link"):
            self.qualify()
        outside = self.root / "unrelated-build"
        outside.mkdir()
        link.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "canonical build root"):
            self.qualify()

    def test_retained_cache_directory_must_resolve_to_selected_build(self):
        cache = self.build / "CMakeCache.txt"
        original = cache.read_text()
        other = self.build_root / "other-build"
        other.mkdir()
        cache.write_text(original.replace("CMAKE_CACHEFILE_DIR:INTERNAL=" + str(self.build),
                                          "CMAKE_CACHEFILE_DIR:INTERNAL=" + str(other)))
        with self.assertRaisesRegex(ValueError, "another build directory"):
            self.qualify()
        cache.write_text(original.replace("CMAKE_CACHEFILE_DIR:INTERNAL=" + str(self.build),
                                          "CMAKE_CACHEFILE_DIR:INTERNAL=" + str(self.checkout / "build")))
        self.assertEqual(self.build, self.qualify()[0])

    def test_retained_build_requires_selected_checkout_and_matching_install_stamp(self):
        self.qualify()
        cache = self.build / "CMakeCache.txt"
        original = cache.read_text()
        cache.write_text("CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.root / "other") + "\n")
        with self.assertRaisesRegex(ValueError, "another checkout"):
            self.qualify()
        cache.write_text(original)
        stamp = self.build / ".stamps/install-native"
        stamp.write_text("b" * 64 + "\n")
        with self.assertRaisesRegex(ValueError, "stamps"):
            self.qualify()
        stamp.write_text("a" * 64 + "\n")


class NativeQualificationTests(unittest.TestCase):
    """Private files and protocol metadata, not PostgreSQL or native execution."""
    def setUp(self):
        self.layout = RetainedBuildTests(methodName="runTest")
        self.layout.setUp()
        self.addCleanup(self.layout.doCleanups)
        self.root, self.checkout, self.build = (
            self.layout.root, self.layout.checkout, self.layout.build)
        self.plan = {**self.layout.plan, "proof_kind": "native-only-install",
                     "proof_operator_commit": "3" * 40, "native_workflow_blob": "4" * 40,
                     "native_artifact_id": 789, "native_artifact_sha256": "5" * 64,
                     "native_checkout": str(self.checkout)}
        self.operator_root = self.root / "native-install"
        self.directory = self.operator_root / "123-1"
        self.directory.mkdir(parents=True, mode=0o700)
        self.directory.chmod(0o700)
        self.prefix = self.root / "installed-fixture"
        for name in ("lib/liblaplace_core.so", "pgsql-18/bin/postgres", "pgsql-18/bin/pg_config"):
            path = self.prefix / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(("protocol-only installed file " + name).encode())
        self.identities = {
            "/opt/laplace/" + name: driver.file_identity(self.prefix / name)
            for name in ("lib/liblaplace_core.so", "pgsql-18/bin/postgres", "pgsql-18/bin/pg_config")}
        common = {"schema": "laplace.native-only-install/v1",
                  "source": self.plan["candidate_commit"], "tree": self.plan["candidate_tree"],
                  "runId": "123", "attempt": "1", "managedPublication": "not_attempted",
                  "fullLifecyclePassed": False, "databaseRecreation": False, "foundationIngestion": False}
        self.state = {**common, "status": "completed", "completedPhases": list(driver.NATIVE_PHASES),
                      "identities": self.identities, "serverVersionNum": 180006}
        self.selection = {**common, "status": "running", "operatorSource": self.plan["proof_operator_commit"],
                          "uid": os.getuid(), "postgresqlSelection": driver.load(
                              ROOT / "deploy/postgresql-release.json")}
        self.outcome = {"workflowOutcome": "success", "source": self.plan["candidate_commit"],
                        "applicationPublication": "not_attempted", "fullLifecyclePassed": False}
        self.remote = {"id": 123, "status": "completed", "conclusion": "success",
                       "event": "push", "run_attempt": 1,
                       "head_branch": "verify/chess-floor-serving-controls-20260916",
                       "path": driver.NATIVE_WORKFLOW, "head_sha": self.plan["proof_operator_commit"]}
        steps = ["Prepare retained native execution paths",
                 "Execute existing native dependency build proof and install owners",
                 "Observe fixed public managed service state",
                 "Retain native operator outcome", "Retain native execution evidence"]
        self.job = {"id": 456, "run_id": 123, "head_sha": self.plan["proof_operator_commit"],
                    "status": "completed", "conclusion": "success",
                    "steps": [{"name": name, "status": "completed", "conclusion": "success"} for name in steps]}
        self.artifact = {"id": 789, "name": "original-native-install-123-1", "expired": False,
                         "digest": "sha256:" + self.plan["native_artifact_sha256"],
                         "workflow_run": {"id": 123, "head_sha": self.plan["proof_operator_commit"]}}
        self.workflow = {"path": driver.NATIVE_WORKFLOW, "sha": self.plan["native_workflow_blob"]}
        self.git_values = {("rev-parse", "HEAD"): self.plan["candidate_commit"],
                           ("rev-parse", "HEAD^{tree}"): self.plan["candidate_tree"],
                           ("status", "--porcelain", "--untracked-files=no"): ""}
        for name, value in (("NATIVE_ROOT", self.operator_root), ("NATIVE_PREFIX", self.prefix)):
            patcher = mock.patch.object(driver, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        self.seal()

    def seal(self):
        for name, value in (("receipt.json", self.state), ("selection.json", self.selection),
                            ("workflow-outcome.json", self.outcome)):
            (self.directory / name).write_text(json.dumps(value) + "\n")
        (self.directory / "completed-phases.txt").write_text(
            "\n".join(self.state["completedPhases"]) + "\n")
        for key, name in (("native_receipt_sha256", "receipt.json"),
                          ("native_selection_sha256", "selection.json")):
            self.plan[key] = driver.file_identity(self.directory / name)["sha256"]

    def qualify(self):
        def run(run_id, suffix=""):
            self.assertEqual(123, run_id)
            if not suffix:
                return self.remote
            if suffix == "/attempts/1/jobs?per_page=100":
                return {"total_count": 1, "jobs": [self.job]}
            self.assertEqual("/artifacts?per_page=100", suffix)
            return {"total_count": 1, "artifacts": [self.artifact]}
        def workflow(path):
            self.assertEqual("contents/" + driver.NATIVE_WORKFLOW + "?ref=" + self.plan["proof_operator_commit"], path)
            return self.workflow
        def git(root, *args):
            self.assertEqual(self.checkout, root)
            return self.git_values[args]
        with (mock.patch.object(driver, "run_json", side_effect=run),
              mock.patch.object(driver, "github_json", side_effect=workflow),
              mock.patch.object(driver, "git", side_effect=git)):
            return driver.qualification(self.plan, ROOT)

    def test_native_installation_retains_independent_scope_and_optional_fetch(self):
        for fetch in (False, True):
            with self.subTest(fetch=fetch):
                self.state["completedPhases"] = list(driver.NATIVE_PHASES)
                if fetch:
                    self.state["completedPhases"].insert(1, "pg-fetch")
                self.seal()
                build, receipt = self.qualify()
                self.assertEqual(self.build, build)
                self.assertFalse(receipt["full_lifecycle_passed"])
                self.assertEqual("not_attempted", receipt["managed_publication"])
                self.assertFalse(receipt["database_recreation"])
                self.assertFalse(receipt["foundation_ingestion"])
                self.assertEqual(180006, receipt["server_version_num"])
                self.assertEqual(self.identities, receipt["installed_identities"])
                self.assertEqual("a" * 64, receipt["native_fingerprint"])
                self.assertNotIn("session_receipt", receipt)
                self.assertIn("current", receipt["checkout_build_observation"])
                self.assertEqual(4, len(receipt["evidence_receipts"]))
                driver.verify_qualification_receipts(receipt)

    def test_native_operator_remote_workflow_job_and_artifact_refusals(self):
        for owner, changes in (
            ("remote", [{"conclusion": "failure"}, {"status": "in_progress"}, {"event": "workflow_dispatch"},
                        {"head_sha": "6" * 40}, {"head_branch": "main"}, {"run_attempt": 2}]),
            ("workflow", [{"sha": "6" * 40}, {"path": ".github/workflows/laplace.yml"}]),
            ("job", [{"conclusion": "failure"}, {"head_sha": "6" * 40},
                     {"steps": self.job["steps"][:-1]}, {"steps": list(reversed(self.job["steps"]))}]),
            ("artifact", [{"digest": "sha256:" + "6" * 64}, {"expired": True},
                          {"id": 790}, {"name": "other"},
                          {"workflow_run": {"id": 123, "head_sha": "6" * 40}}]),
        ):
            original = copy.deepcopy(getattr(self, owner))
            for change in changes:
                with self.subTest(owner=owner, change=change):
                    setattr(self, owner, {**original, **change})
                    with self.assertRaisesRegex(ValueError, "native installation"):
                        self.qualify()
            setattr(self, owner, original)

    def test_native_phase_and_scope_mutations_fail_even_with_reselected_receipt_hashes(self):
        original = copy.deepcopy(self.state)
        changes = [{"completedPhases": original["completedPhases"][:index]
                    + original["completedPhases"][index + 1:]} for index in range(len(driver.NATIVE_PHASES))]
        changes += [{"completedPhases": list(reversed(driver.NATIVE_PHASES))},
                    {"source": "6" * 40}, {"tree": "6" * 40}, {"attempt": "2"},
                    {"status": "running"}, {"serverVersionNum": 180003},
                    {"managedPublication": "completed"}, {"fullLifecyclePassed": True},
                    {"databaseRecreation": True}, {"foundationIngestion": True}]
        for change in changes:
            with self.subTest(change=change):
                self.state = {**original, **change}
                self.seal()
                with self.assertRaisesRegex(ValueError, "native installation"):
                    self.qualify()
        self.state = original
        for owner, change in (("selection", {"operatorSource": "6" * 40}),
                              ("selection", {"uid": os.getuid() + 1}),
                              ("selection", {"postgresqlSelection": {}}),
                              ("outcome", {"workflowOutcome": "failure"})):
            saved = copy.deepcopy(getattr(self, owner))
            setattr(self, owner, {**saved, **change})
            self.seal()
            with self.subTest(owner=owner), self.assertRaisesRegex(ValueError, "native installation"):
                self.qualify()
            setattr(self, owner, saved)

    def test_native_receipt_bytes_and_installed_files_are_actually_checked(self):
        receipt = self.directory / "receipt.json"
        receipt.write_text(receipt.read_text() + " ")
        with self.assertRaisesRegex(ValueError, "receipt bytes changed"):
            self.qualify()
        self.seal()
        installed = self.prefix / "lib/liblaplace_core.so"
        installed.write_bytes(installed.read_bytes() + b"mutation")
        with self.assertRaisesRegex(ValueError, "file identity changed"):
            self.qualify()

    def test_native_current_checkout_build_and_fingerprint_are_not_inferred(self):
        for key, value in ((("rev-parse", "HEAD"), "6" * 40),
                           (("rev-parse", "HEAD^{tree}"), "6" * 40),
                           (("status", "--porcelain", "--untracked-files=no"), " M real-source")):
            original = self.git_values[key]
            self.git_values[key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "current checkout"):
                self.qualify()
            self.git_values[key] = original
        (self.build / ".stamps/install-native").write_text("b" * 64)
        with self.assertRaisesRegex(ValueError, "stamps"):
            self.qualify()
        (self.build / ".stamps/install-native").write_text("a" * 64)
        (self.checkout / "build").unlink()
        with self.assertRaisesRegex(ValueError, "build link"):
            self.qualify()

    def test_native_private_evidence_and_recheck_detect_real_mutation(self):
        self.directory.chmod(0o755)
        with self.assertRaisesRegex(ValueError, "private"):
            self.qualify()
        self.directory.chmod(0o700)
        _, receipt = self.qualify()
        (self.directory / "workflow-outcome.json").write_text("{}")
        with self.assertRaisesRegex(ValueError, "evidence changed"):
            driver.verify_qualification_receipts(receipt)

    def baseline(self):
        return {"format": 1, "purpose": "recording", "native_fingerprint": "a" * 64,
                "database": {"server_version": "180006", "running_ingests": 2,
                             "system_identifier": "fixture-database", "extensions": {"real-owner": "1"}},
                "artifacts": {"lib/liblaplace_core.so": {"sha256": "b" * 64}},
                "stamps": {"build-native": "a" * 64, "install-native": "a" * 64}}

    def runtime_guard(self, observed):
        guard = mock.Mock()
        actual = module("export_actual_recording_guard", "check-application-runtime.py")
        guard.compatible = actual.compatible
        guard.read_database.return_value = observed["database"]
        guard.snapshot.return_value = observed
        return guard

    def test_native_installed_state_still_requires_running_release_and_full_pilot_guard(self):
        _, receipt = self.qualify()
        baseline = self.baseline()
        observed = copy.deepcopy(baseline)
        observed["database"]["server_version"] = "180003"
        guard = self.runtime_guard(observed)
        with self.assertRaisesRegex(ValueError, "running PostgreSQL"):
            driver.installed_state(guard, self.prefix, self.prefix / "pgsql-18", baseline, receipt)
        guard.snapshot.assert_not_called()
        observed["database"]["server_version"] = "180006"
        observed["database"]["system_identifier"] = "different-database"
        with self.assertRaisesRegex(ValueError, "recording baseline"):
            driver.installed_state(guard, self.prefix, self.prefix / "pgsql-18", baseline, receipt)

    def test_recording_journal_progress_preserves_full_installed_guard_and_receipts(self):
        _, receipt = self.qualify()
        baseline = self.baseline()
        original = copy.deepcopy(baseline)
        for count in (0, 1, 2, 7):
            with self.subTest(count=count):
                observed = copy.deepcopy(baseline)
                observed["database"]["running_ingests"] = count
                guard = self.runtime_guard(observed)
                self.assertIs(observed, driver.installed_state(
                    guard, self.prefix, self.prefix / "pgsql-18", baseline, receipt))
                guard.snapshot.assert_called_once_with(
                    self.checkout, self.prefix, observed["database"], "a" * 64,
                    purpose="recording")
                driver.recording_compatible(guard, baseline, observed)
                self.assertEqual(original, baseline)
                if count != 2:
                    self.assertFalse(guard.compatible(baseline, observed))

    def test_recording_comparison_rejects_other_changes_and_wrong_purpose(self):
        baseline = self.baseline()
        guard = self.runtime_guard(baseline)
        variants = []
        for section, key, value in (
                ("database", "system_identifier", "other"),
                ("database", "extensions", {"real-owner": "2"}),
                ("artifacts", "lib/liblaplace_core.so", {"sha256": "c" * 64}),
                ("stamps", "install-native", "c" * 64)):
            changed = copy.deepcopy(baseline)
            changed[section][key] = value
            changed["database"]["running_ingests"] = 0
            variants.append(changed)
        variants += [{**baseline, "native_fingerprint": "c" * 64},
                     {**baseline, "purpose": "publication"},
                     {key: value for key, value in baseline.items() if key != "purpose"}]
        for count in (-1, True, "2"):
            changed = copy.deepcopy(baseline)
            changed["database"]["running_ingests"] = count
            variants.append(changed)
        for value in variants:
            with self.subTest(value=value), self.assertRaises(ValueError):
                driver.recording_compatible(guard, baseline, value)

    def test_recorded_native_placement_is_bound_when_present(self):
        placement = {"checkout": str(self.checkout), "buildDirectory": str(self.build),
                     "buildNativeStamp": "a" * 64, "installNativeStamp": "a" * 64}
        self.state.update(placement)
        self.selection["checkout"] = str(self.checkout)
        self.seal()
        _, receipt = self.qualify()
        self.assertEqual(placement, receipt["recorded_placement"])
        for key in placement:
            original = self.state[key]
            self.state[key] = str(self.root / "other") if key.endswith("Directory") or key == "checkout" else "b" * 64
            self.seal()
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "placement"):
                self.qualify()
            self.state[key] = original
        self.selection["checkout"] = str(self.root / "other")
        self.seal()
        with self.assertRaisesRegex(ValueError, "selected checkout"):
            self.qualify()

if __name__ == "__main__":
    unittest.main()
