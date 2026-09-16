#!/usr/bin/env python3
"""Real filesystem/process publication controls; no corpus or native chess claim."""
import importlib.util
import copy
import hashlib
import json
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


class MainQualificationTests(unittest.TestCase):
    """Protocol metadata fixtures exercise the real lifecycle selection owner."""
    def setUp(self):
        root = os.environ.get("TMPDIR")
        if not root or not Path(root).is_absolute() or not Path(root).is_dir():
            self.fail("TMPDIR must select an existing permanent test workspace")
        self.temp = tempfile.TemporaryDirectory(prefix="main-floor-qualification-", dir=root)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.checkout = self.root / "persistent-main-checkout"
        self.checkout.mkdir()
        self.session_root = self.root / "sessions"
        self.session = self.session_root / "123-1-product/session.json"
        self.session.parent.mkdir(parents=True, mode=0o700)
        self.session.parent.chmod(0o700)
        self.build_root = self.root / "build"
        self.build = self.build_root / ("laplace-" + hashlib.sha256(os.fsencode(self.checkout)).hexdigest()[:16])
        (self.build / ".stamps").mkdir(parents=True)
        (self.checkout / "build").symlink_to(self.build, target_is_directory=True)
        (self.build / "CMakeCache.txt").write_text(
            "CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.checkout) + "\n"
            "CMAKE_CACHEFILE_DIR:INTERNAL=" + str(self.build) + "\n")
        for name in ("build-native", "install-native"):
            (self.build / ".stamps" / name).write_text("a" * 64 + "\n")
        environment = dict(os.environ, LAPLACE_FRESH_DB="", LAPLACE_RESTORE_FOUNDATION="",
                           LAPLACE_GENERATION_BENCHMARK="")
        self.phases = subprocess.check_output(
            ["bash", str(ROOT / "scripts/product-ci.sh"), "all", "--list-phases"],
            cwd=ROOT, env=environment, text=True, timeout=10).splitlines()
        self.assertIn("native-install", self.phases)
        self.assertIn("publish", self.phases)
        self.assertIn("live-api", self.phases)
        self.plan = {"proof_run_id": 123, "proof_run_attempt": 1,
                     "candidate_commit": "1" * 40, "candidate_tree": "2" * 40,
                     "installed_source": "1" * 40}
        self.state = {"schema": "laplace.ci-session.v1", "kind": "product", "stage": "all",
                      "status": "stopped", "cleanup_exit_code": 0, "active": None,
                      "source": {"commit": self.plan["candidate_commit"], "tree": self.plan["candidate_tree"]},
                      "checkout": str(self.checkout), "phases": self.phases,
                      "next": len(self.phases),
                      "results": [{"phase": p, "exit_code": 0} for p in self.phases],
                      "token": "fixture-session-token-must-not-be-retained"}
        self.remote = {"id": 123, "status": "completed", "conclusion": "success", "event": "push",
                       "head_branch": "main", "path": ".github/workflows/laplace.yml",
                       "run_attempt": 1, "head_sha": self.plan["candidate_commit"]}
        for name, value in (("SESSION_ROOT", self.session_root), ("BUILD_ROOT", self.build_root)):
            patcher = mock.patch.object(driver, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)

    def qualify(self, state=None, remote=None, jobs=None):
        self.session.write_text(json.dumps(self.state if state is None else state))
        def response(run_id, suffix=""):
            self.assertEqual(123, run_id)
            if suffix:
                self.assertEqual("/attempts/1/jobs?per_page=100", suffix)
                if jobs is None:
                    self.fail("failed lifecycle needs explicitly supplied job evidence")
                return jobs
            return self.remote if remote is None else remote
        with mock.patch.object(driver, "run_json", side_effect=response):
            return driver.qualification(self.plan, ROOT)

    def test_exact_main_requires_real_canonical_all_phase_list_independent_of_export_environment(self):
        with mock.patch.dict(os.environ, {"LAPLACE_FRESH_DB": "1",
                                         "LAPLACE_RESTORE_FOUNDATION": "1",
                                         "LAPLACE_GENERATION_BENCHMARK": "1"}):
            build, receipt = self.qualify()
        self.assertEqual(self.build, build)
        self.assertEqual(str(self.checkout), receipt["lifecycle_checkout"])
        self.assertEqual(self.state["results"], receipt["phases"])
        self.assertEqual("a" * 64, receipt["native_fingerprint"])
        self.assertEqual(hashlib.sha256(self.session.read_bytes()).hexdigest(), receipt["session_sha256"])
        self.assertNotIn(self.state["token"], json.dumps(receipt))
        self.assertNotIn("pr_checkout", receipt)
        self.assertTrue(receipt["full_lifecycle_passed"])
        self.assertEqual("success", receipt["lifecycle_conclusion"])
        self.assertIsNone(receipt["lexical_failure"])

    def test_cancelled_pr_other_source_branch_attempt_or_incomplete_main_cannot_qualify(self):
        mutations = (
            {"event": "pull_request", "path": ".github/workflows/pr-validation.yml"},
            {"conclusion": "cancelled"}, {"conclusion": "timed_out"}, {"status": "in_progress"},
            {"head_branch": "verify/operator"}, {"head_sha": "0" * 40},
            {"run_attempt": 2}, {"id": 124}, {"path": ".github/workflows/other.yml"},
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation), self.assertRaisesRegex(ValueError, "exact-main"):
                self.qualify(remote={**self.remote, **mutation})

    def test_incomplete_wrong_source_or_failed_retained_phases_are_rejected(self):
        mutations = [
            {"kind": "pr"}, {"stage": "build"}, {"status": "failed"},
            {"cleanup_exit_code": 1}, {"active": {"phase": "publish"}},
            {"next": len(self.phases) - 1},
            {"source": {"commit": "0" * 40, "tree": self.plan["candidate_tree"]}},
            {"phases": list(reversed(self.phases))},
            {"results": self.state["results"][:-1]},
            {"phases": [p for p in self.phases if p != "live-api"],
             "results": [r for r in self.state["results"] if r["phase"] != "live-api"]},
        ]
        failed = copy.deepcopy(self.state["results"])
        failed[self.phases.index("native-install")]["exit_code"] = 1
        mutations.append({"results": failed})
        for mutation in mutations:
            with self.subTest(mutation=mutation), self.assertRaisesRegex(ValueError, "canonical product phase"):
                self.qualify(state={**self.state, **mutation})

    def lexical_fixture(self):
        # Protocol records only; no lexical failure or chess export is executed.
        state = copy.deepcopy(self.state)
        boundary = self.phases.index("lexical-foundation")
        state.update(status="failed", next=boundary + 1,
                     results=state["results"][:boundary] + [{"phase": "lexical-foundation", "exit_code": 1}],
                     failure="phase lexical-foundation failed")
        remote = {**self.remote, "conclusion": "failure"}
        installed = [
            "Resolve the Stockfish checkout for application publication", "Reserve host for product phases",
            "Check source and policy", "Resolve build dependencies", "Build native and managed artifacts",
            "Test native engine", "Test managed code", "Test UCI runtime", "Test browser product",
            "Install native artifacts", "Migrate and reconcile installed database",
        ]
        downstream = [
            "Admit operational memory", "Publish applications",
            "Verify the installed direct build uses the selected Stockfish checkout",
            "Verify ordinary operational execution", "Verify database health",
            "Test native PostgreSQL extensions", "Test managed database integration",
            "Prove live recursive substrate", "Verify live API endpoints", "Test live product behavior",
            "Evaluate witnessed generation",
        ]
        steps = [{"name": name, "status": "completed", "conclusion": "success"} for name in installed]
        steps += [{"name": "Admit required lexical foundation", "status": "completed", "conclusion": "failure"}]
        steps += [{"name": name, "status": "completed", "conclusion": "skipped"} for name in downstream]
        steps += [{"name": "Release product host reservation", "status": "completed", "conclusion": "success"}]
        jobs = {"total_count": 1, "jobs": [{
            "id": 456, "run_id": 123, "head_sha": self.plan["candidate_commit"],
            "status": "completed", "conclusion": "failure", "steps": steps,
        }]}
        return state, remote, jobs

    def test_lexical_only_failure_keeps_exact_failed_skipped_and_cleanup_evidence(self):
        state, remote, jobs = self.lexical_fixture()
        build, receipt = self.qualify(state, remote, jobs)
        self.assertEqual(self.build, build)
        self.assertFalse(receipt["full_lifecycle_passed"])
        self.assertEqual("failure", receipt["lifecycle_conclusion"])
        self.assertEqual("failed", receipt["session_status"])
        self.assertEqual(0, receipt["cleanup_exit_code"])
        self.assertEqual(state["results"], receipt["phases"])
        observed = receipt["lexical_failure"]
        self.assertEqual(456, observed["job_id"])
        self.assertEqual("lexical-foundation", observed["failed_phase"])
        self.assertEqual(1, observed["failed_exit_code"])
        self.assertEqual(self.phases[state["next"]:], observed["not_executed_phases"])
        self.assertEqual(jobs["jobs"][0]["steps"], observed["workflow_steps"])
        self.assertNotIn(state["token"], json.dumps(receipt))

    def test_lexical_route_rejects_missing_prerequisite_wrong_phase_or_reordered_receipts(self):
        state, remote, jobs = self.lexical_fixture()
        mutations = []
        for name in ("policy", "dependencies", "build", "native-dev", "managed-dev",
                     "uci-dev", "browser-dev", "native-install", "database-maintenance"):
            changed = copy.deepcopy(state)
            changed["results"] = [row for row in changed["results"] if row["phase"] != name]
            mutations.append(changed)
        for replacement in ("database-maintenance", "operational-seed"):
            changed = copy.deepcopy(state)
            changed["results"][-1]["phase"] = replacement
            mutations.append(changed)
        for exit_code in (0, -1, True, "1"):
            changed = copy.deepcopy(state)
            changed["results"][-1]["exit_code"] = exit_code
            mutations.append(changed)
        changed = copy.deepcopy(state)
        changed["results"][0], changed["results"][1] = changed["results"][1], changed["results"][0]
        mutations.append(changed)
        mutations.append({**state, "next": state["next"] - 1})
        mutations.append({**state, "status": "stopped"})
        mutations.append(self.state)  # A failed remote run cannot borrow a complete-success session.
        for changed in mutations:
            with self.subTest(changed=changed["results"]), self.assertRaisesRegex(ValueError, "canonical product phase"):
                self.qualify(changed, remote, jobs)

    def test_lexical_route_rejects_cleanup_active_source_and_phase_plan_mismatches(self):
        state, remote, jobs = self.lexical_fixture()
        mutations = [
            {"cleanup_exit_code": 1}, {"cleanup_exit_code": None},
            {"active": {"phase": "lexical-foundation"}}, {"kind": "pr"},
            {"source": {"commit": "0" * 40, "tree": self.plan["candidate_tree"]}},
            {"source": {"commit": self.plan["candidate_commit"], "tree": "0" * 40}},
            {"phases": list(reversed(self.phases))},
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutation), self.assertRaisesRegex(ValueError, "canonical product phase"):
                self.qualify({**state, **mutation}, remote, jobs)
        for absent in ("cleanup_exit_code", "active"):
            changed = copy.deepcopy(state)
            del changed[absent]
            with self.subTest(absent=absent), self.assertRaisesRegex(ValueError, "canonical product phase"):
                self.qualify(changed, remote, jobs)
        with self.assertRaisesRegex(ValueError, "exact-main"):
            self.qualify(state, {**remote, "head_sha": "f" * 40}, jobs)

    def test_lexical_route_requires_remote_prerequisites_skips_release_and_sole_failure(self):
        state, remote, jobs = self.lexical_fixture()
        mutations = []
        for index, step in enumerate(jobs["jobs"][0]["steps"]):
            changed = copy.deepcopy(jobs)
            changed["jobs"][0]["steps"].pop(index)
            mutations.append(changed)
            changed = copy.deepcopy(jobs)
            changed["jobs"][0]["steps"][index]["conclusion"] = (
                "success" if step["conclusion"] != "success" else "skipped")
            mutations.append(changed)
        changed = copy.deepcopy(jobs)
        changed["jobs"][0]["steps"][:2] = reversed(changed["jobs"][0]["steps"][:2])
        mutations.append(changed)
        changed = copy.deepcopy(jobs)
        changed["jobs"][0]["steps"].append(
            {"name": "Other failure", "status": "completed", "conclusion": "failure"})
        mutations.append(changed)
        for changed in mutations:
            with self.subTest(steps=changed["jobs"][0]["steps"]), self.assertRaisesRegex(ValueError, "exact-main"):
                self.qualify(state, remote, changed)

    def test_lexical_route_rejects_ambiguous_or_mismatched_remote_job(self):
        state, remote, jobs = self.lexical_fixture()
        mutations = [{"total_count": 0, "jobs": []},
                     {"total_count": 2, "jobs": jobs["jobs"] * 2}]
        for change in ({"id": 0}, {"run_id": 124}, {"head_sha": "0" * 40},
                       {"status": "in_progress"}, {"conclusion": "success"}, {"steps": None}):
            changed = copy.deepcopy(jobs)
            changed["jobs"][0].update(change)
            mutations.append(changed)
        for changed in mutations:
            with self.subTest(jobs=changed), self.assertRaisesRegex(ValueError, "exact-main"):
                self.qualify(state, remote, changed)

    def test_lexical_route_preserves_build_placement_and_installed_stamp_refusals(self):
        state, remote, jobs = self.lexical_fixture()
        link = self.checkout / "build"
        link.unlink()
        with self.assertRaisesRegex(ValueError, "build link"):
            self.qualify(state, remote, jobs)
        outside = self.root / "outside-build"
        outside.mkdir()
        link.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "canonical build root"):
            self.qualify(state, remote, jobs)
        link.unlink()
        link.symlink_to(self.build, target_is_directory=True)
        (self.build / ".stamps/install-native").write_text("b" * 64 + "\n")
        with self.assertRaisesRegex(ValueError, "stamps"):
            self.qualify(state, remote, jobs)

    def test_successor_source_requires_its_own_matching_remote_and_retained_source(self):
        original = copy.deepcopy(self.state)
        self.plan.update(candidate_commit="3" * 40, candidate_tree="4" * 40,
                         installed_source="3" * 40)
        self.remote["head_sha"] = self.plan["candidate_commit"]
        self.state["source"] = {"commit": self.plan["candidate_commit"],
                                "tree": self.plan["candidate_tree"]}
        _, receipt = self.qualify()
        self.assertEqual(self.state["source"], receipt["source"])
        with self.assertRaisesRegex(ValueError, "canonical product phase"):
            self.qualify(state=original)
        with self.assertRaisesRegex(ValueError, "exact-main"):
            self.qualify(remote={**self.remote, "head_sha": "1" * 40})

    def test_selection_rejects_mutable_incomplete_and_different_installed_sources(self):
        self.assertEqual(self.state["source"], driver.selection_identity(self.plan))
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
        build, receipt = self.qualify()
        self.assertEqual(self.build, build)
        self.assertEqual(str(self.build), receipt["native_build"])
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
        self.session.parent.chmod(0o755)
        with self.assertRaisesRegex(ValueError, "private"):
            self.qualify()


if __name__ == "__main__":
    unittest.main()
