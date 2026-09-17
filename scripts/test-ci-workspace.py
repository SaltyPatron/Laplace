#!/usr/bin/env python3
"""Exercise the real CI checkout bodies against temporary local Git repositories."""
import fcntl
import os
from pathlib import Path
import subprocess
import shutil
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]

def body(job, workflow="laplace.yml"):
    text = (ROOT / ".github/workflows" / workflow).read_text()
    section = text.split("  " + job + ":\n", 1)[1]
    if job == "mainline":
        section = section.split("\n  operator:\n", 1)[0]
    raw = section.split("        run: |\n", 1)[1]
    return "\n".join(line[10:] for line in raw.splitlines()
                     if line.startswith("          ")) + "\n"

class WorkspaceFixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-ci-workspace-")
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.origin = self.directory / "origin.git"
        self.seed = self.directory / "seed"
        self.workspace = self.directory / "workspace"
        self.work = self.directory / "work"
        self.events = self.directory / "events"
        self.global_config = self.directory / "git-config"
        self.global_config.touch()
        self.work.mkdir()
        self.env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=str(self.global_config),
                        GIT_AUTHOR_NAME="CI fixture", GIT_AUTHOR_EMAIL="fixture@example.invalid",
                        GIT_COMMITTER_NAME="CI fixture", GIT_COMMITTER_EMAIL="fixture@example.invalid")
        self.git(None, "init", "--bare", str(self.origin))
        self.git(None, "init", str(self.seed))
        (self.seed / "scripts").mkdir()
        (self.seed / ".gitignore").write_text("build/\n")
        (self.seed / "source.txt").write_text("old\n")
        (self.seed / "scripts/ci-environment.sh").write_text(
            'printf "environment\\n" >> "$TEST_EVENTS"\n')
        (self.seed / "scripts/product-ci.sh").write_text(
            '#!/usr/bin/env bash\nprintf "%s\\n" "$1" >> "$TEST_EVENTS"\n')
        self.prepare_seed()
        self.git(self.seed, "add", ".")
        self.git(self.seed, "commit", "-m", "initial fixture")
        self.git(self.seed, "remote", "add", "origin", str(self.origin))
        self.git(self.seed, "push", "origin", "HEAD:refs/heads/main")
        self.git(None, "clone", "--branch", "main", str(self.origin), str(self.workspace))
        self.old = self.git(self.workspace, "rev-parse", "HEAD").strip()
        (self.seed / "source.txt").write_text("selected\n")
        (self.seed / "new-source.txt").write_text("selected new file\n")
        self.git(self.seed, "add", ".")
        self.git(self.seed, "commit", "-m", "selected fixture")
        self.git(self.seed, "push", "origin", "HEAD:refs/heads/main")
        self.target = self.git(self.seed, "rev-parse", "HEAD").strip()
        (self.workspace / "build").mkdir()
        self.marker = self.workspace / "build/retained"
        self.marker.write_text("existing qualified build\n")
        self.env.update(GITHUB_WORKSPACE=str(self.workspace), LAPLACE_WORK_ROOT=str(self.work),
                        TARGET_SHA=self.target, CHECKOUT_TOKEN="fixture-only",
                        TEST_EVENTS=str(self.events), LAPLACE_STAGE="check", GITHUB_REPOSITORY="fixture/repository")

    def prepare_seed(self):
        pass

    def git(self, cwd, *arguments):
        return subprocess.run(["git", *arguments], cwd=cwd, env=self.env,
                              check=True, text=True, capture_output=True, timeout=15).stdout

    def execute(self, job):
        return subprocess.run(["bash", "-c", body(job)], cwd=self.workspace, env=self.env,
                              text=True, capture_output=True, timeout=15)

class WorkspaceReservation(WorkspaceFixture):
    def test_checkout_waits_for_reservation_and_preserves_build(self):
        with (self.work / "host-resource.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            process = subprocess.Popen(["bash", "-c", 'printf "started\\n";\n' + body("mainline")],
                                       cwd=self.workspace, env=self.env,
                                       text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                self.assertEqual(process.stdout.readline(), "started\n")
                time.sleep(0.25)
                self.assertIsNone(process.poll())
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
                self.assertFalse(self.events.exists())
                self.assertEqual(self.marker.read_text(), "existing qualified build\n")
                fcntl.flock(lock, fcntl.LOCK_UN)
                stdout, stderr = process.communicate(timeout=15)
                self.assertEqual(process.returncode, 0, stdout + stderr)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate(timeout=5)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.marker.read_text(), "existing qualified build\n")
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "mainline"])

    def test_operator_uses_same_reserved_checkout(self):
        result = self.execute("operator")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.marker.read_text(), "existing qualified build\n")
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "check"])

    def test_empty_runner_initializes_inside_reserved_workspace(self):
        shutil.rmtree(self.workspace)
        self.workspace.mkdir()
        self.git(None, "config", "--file", str(self.global_config),
                 "url." + str(self.origin) + ".insteadOf", "https://github.com/fixture/repository.git")
        result = self.execute("mainline")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "mainline"])

    def test_nonrepository_files_are_preserved(self):
        shutil.rmtree(self.workspace)
        self.workspace.mkdir()
        path = self.workspace / "existing-work.txt"
        path.write_text("existing work\\n")
        result = self.execute("mainline")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_text(), "existing work\\n")
        self.assertFalse((self.workspace / ".git").exists())
        self.assertFalse(self.events.exists())

    def test_tracked_local_work_is_preserved(self):
        path = self.workspace / "source.txt"
        path.write_text("uncommitted work\n")
        result = self.execute("mainline")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_text(), "uncommitted work\n")
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
        self.assertFalse(self.events.exists())

    def test_untracked_checkout_collision_is_preserved(self):
        path = self.workspace / "new-source.txt"
        path.write_text("untracked work\n")
        result = self.execute("operator")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_text(), "untracked work\n")
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
        self.assertFalse(self.events.exists())

    def test_mainline_and_operator_preserve_ignored_checkout_collisions(self):
        path = self.workspace / "new-source.txt"
        path.write_text("ignored local work\n")
        with (self.workspace / ".git/info/exclude").open("a") as stream:
            stream.write("\nnew-source.txt\n")
        self.assertEqual(self.git(self.workspace, "status", "--porcelain",
                                  "--untracked-files=all").strip(), "")
        self.assertEqual(self.git(self.workspace, "check-ignore",
                                  "new-source.txt").strip(), "new-source.txt")
        before = path.stat()
        for job in ("mainline", "operator"):
            with self.subTest(job=job):
                result = self.execute(job)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(path.read_text(), "ignored local work\n")
                self.assertEqual(path.stat().st_ino, before.st_ino)
                self.assertEqual(path.stat().st_mtime_ns, before.st_mtime_ns)
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
                self.assertEqual(self.marker.read_text(), "existing qualified build\n")
                self.assertFalse(self.events.exists())


class DatabaseWorkspaceReservation(WorkspaceFixture):
    def prepare_seed(self):
        ownership = (
            'exec 8>"$LAPLACE_WORK_ROOT/host-resource.lock"\n'
            'if flock -n 8; then echo "reservation was not inherited" >&2; exit 97; fi\n'
            'exec 8>&-\n'
            '[[ "$(git rev-parse HEAD)" == "$TARGET_SHA" ]] || exit 98\n')
        (self.seed / "scripts/ci-environment.sh").write_text(
            ownership
            + '[[ "$LAPLACE_SETUP_USE_CMAKE" == false ]] || exit 95\n'
            + '[[ "$LAPLACE_SETUP_REQUIRE_BUILT_REVISION" == false ]] || exit 96\n'
            + '[[ ! -v CHECKOUT_TOKEN && ! -v checkout_auth ]] || exit 94\n'
            + 'printf "environment\\n" >> "$TEST_EVENTS"\n')
        for name, label in (
                ("db-migrations.sh", "migration"), ("pipeline.sh", "pipeline"),
                ("check-database-health.sh", "health"),
                ("maintain-installed-database.sh", "maintenance")):
            (self.seed / "scripts" / name).write_text(
                '#!/usr/bin/env bash\nset -euo pipefail\n' + ownership
                + 'event="' + label + ':$*"\n'
                + 'printf "%s\\n" "$event" >> "$TEST_EVENTS"\n'
                + '[[ "$event" != "$TEST_FAIL_EVENT" ]] || exit 23\n')

        (self.seed / "scripts/check-installed-extension-current.py").write_text(
            'import os, subprocess\nfrom pathlib import Path\n'
            'head = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()\n'
            'if head != os.environ["TARGET_SHA"]: raise SystemExit(98)\n'
            'with Path(os.environ["TEST_EVENTS"]).open("a") as stream:\n'
            '    stream.write("artifact-proof\\n")\n'
            'raise SystemExit(23 if os.environ["TEST_FAIL_EVENT"] == "artifact-proof" else 0)\n')

    def setUp(self):
        super().setUp()
        self.env.update(LAPLACE_DB_OPERATION="status", PGDATABASE="fixture_database",
                        TEST_FAIL_EVENT="")

    def execute_database(self, operation="status"):
        environment = dict(self.env, LAPLACE_DB_OPERATION=operation)
        return subprocess.run(["bash", "-c", body("db", "db-ops.yml")],
                              cwd=self.workspace, env=environment,
                              text=True, capture_output=True, timeout=15)

    def test_database_selection_and_setup_wait_for_the_shared_reservation(self):
        fetch_head = self.workspace / ".git/FETCH_HEAD"
        prior_fetch = fetch_head.read_bytes() if fetch_head.exists() else None
        with (self.work / "host-resource.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            process = subprocess.Popen(
                ["bash", "-c", 'printf "started\\n";\n' + body("db", "db-ops.yml")],
                cwd=self.workspace, env=self.env, text=True,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                self.assertEqual(process.stdout.readline(), "started\n")
                time.sleep(0.25)
                self.assertIsNone(process.poll())
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
                self.assertEqual(fetch_head.read_bytes() if fetch_head.exists() else None, prior_fetch)
                self.assertFalse(self.events.exists())
                self.assertEqual(self.marker.read_text(), "existing qualified build\n")
                fcntl.flock(lock, fcntl.LOCK_UN)
                stdout, stderr = process.communicate(timeout=15)
                self.assertEqual(process.returncode, 0, stdout + stderr)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate(timeout=5)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.marker.read_text(), "existing qualified build\n")
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "migration:status"])
        self.assertNotIn("extraheader", self.git(self.workspace, "config", "--local", "--list"))

    def test_database_operations_own_lifecycle_only(self):
        pipeline = "pipeline:sync-extension tune-pg tune-laplace perfcache-guc api-env"
        cases = (
            ("status", ["migration:status"]),
            ("create", ["artifact-proof", "migration:up", pipeline, "health:fixture_database"]),
            ("drop", ["migration:nuke --yes"]),
            ("recreate", ["artifact-proof", "migration:nuke --yes", "migration:up", pipeline,
                           "health:fixture_database"]),
            ("update", ["artifact-proof", "maintenance:"]),
            ("verify", ["health:fixture_database"]),
        )
        for operation, expected in cases:
            with self.subTest(operation=operation):
                self.events.unlink(missing_ok=True)
                result = self.execute_database(operation)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(self.events.read_text().splitlines(), ["environment", *expected])
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.target)

    def test_database_selects_requested_commit_even_after_origin_advances(self):
        (self.seed / "source.txt").write_text("later revision\n")
        self.git(self.seed, "add", ".")
        self.git(self.seed, "commit", "-m", "later fixture")
        self.git(self.seed, "push", "origin", "HEAD:refs/heads/main")
        later = self.git(self.seed, "rev-parse", "HEAD").strip()
        self.assertNotEqual(later, self.target)
        result = self.execute_database()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual((self.workspace / "source.txt").read_text(), "selected\n")

    def test_database_empty_runner_uses_the_authenticated_origin_path(self):
        shutil.rmtree(self.workspace)
        self.workspace.mkdir()
        self.git(None, "config", "--file", str(self.global_config),
                 "url." + str(self.origin) + ".insteadOf", "https://github.com/fixture/repository.git")
        result = self.execute_database()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "migration:status"])
        self.assertEqual(self.git(self.workspace, "config", "--local", "--get", "remote.origin.url").strip(),
                         "https://github.com/fixture/repository.git")

    def test_database_preserves_nonrepository_files_without_starting_setup(self):
        shutil.rmtree(self.workspace)
        self.workspace.mkdir()
        path = self.workspace / "existing-work.txt"
        path.write_text("existing work\n")
        result = self.execute_database()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_text(), "existing work\n")
        self.assertFalse((self.workspace / ".git").exists())
        self.assertFalse(self.events.exists())

    def test_database_preserves_unstaged_and_staged_tracked_work(self):
        path = self.workspace / "source.txt"
        path.write_text("uncommitted work\n")
        for staged in (False, True):
            with self.subTest(staged=staged):
                if staged:
                    self.git(self.workspace, "add", "source.txt")
                result = self.execute_database()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(path.read_text(), "uncommitted work\n")
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
                if staged:
                    self.assertEqual(self.git(self.workspace, "show", ":source.txt"),
                                     "uncommitted work\n")
                self.assertFalse(self.events.exists())

    def test_database_preserves_untracked_and_ignored_checkout_collisions(self):
        path = self.workspace / "new-source.txt"
        path.write_text("untracked work\n")
        for ignored in (False, True):
            with self.subTest(ignored=ignored):
                if ignored:
                    with (self.workspace / ".git/info/exclude").open("a") as stream:
                        stream.write("\nnew-source.txt\n")
                result = self.execute_database()
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(path.read_text(), "untracked work\n")
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
                self.assertFalse(self.events.exists())

    def test_database_operation_failure_prevents_later_phases(self):
        self.env["TEST_FAIL_EVENT"] = "migration:up"
        result = self.execute_database("recreate")
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertEqual(self.events.read_text().splitlines(),
                         ["environment", "artifact-proof", "migration:nuke --yes", "migration:up"])

    def test_artifact_mismatch_stops_before_database_changes(self):
        self.env["TEST_FAIL_EVENT"] = "artifact-proof"
        for operation in ("create", "recreate", "update"):
            with self.subTest(operation=operation):
                self.events.unlink(missing_ok=True)
                result = self.execute_database(operation)
                self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
                self.assertEqual(self.events.read_text().splitlines(), ["environment", "artifact-proof"])

    def test_seed_and_unknown_operations_never_start_database_work(self):
        for operation in ("seed", "unknown"):
            with self.subTest(operation=operation):
                self.events.unlink(missing_ok=True)
                result = self.execute_database(operation)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertEqual(self.events.read_text().splitlines(), ["environment"])

class IntegratedLifecycle(unittest.TestCase):
    def execute(self, test_status, failure_phase=None):
        source = (ROOT / "scripts/product-ci.sh").read_text()
        start = source.index("run_deploy() {\n")
        finish = source.index("\n}\n", start) + 3
        owner = source[start:finish]
        names = ("check_deps", "run_build", "run_dev_tests", "run_install",
                 "run_database_maintenance", "run_db_tests", "run_competitive_model_proof",
                 "run_publish", "reconcile_installed_product", "run_live_tests")
        with tempfile.TemporaryDirectory(prefix="laplace-lifecycle-order-") as directory:
            events = Path(directory) / "events"
            functions = []
            for name in names:
                status = (test_status if name == "run_dev_tests" else
                          31 if name == failure_phase else 0)
                event = name + (":$*" if name == "run_database_maintenance" else "")
                functions.append(name + '() { printf "%s\\n" "' + event
                                 + '" >> "$TEST_EVENTS"; return ' + str(status) + '; }')
            script = "set -euo pipefail\n" + "\n".join(functions) + "\n" + owner + "\nrun_deploy\n"
            result = subprocess.run(["bash", "-c", script],
                                    env=dict(os.environ, TEST_EVENTS=str(events)),
                                    text=True, capture_output=True, timeout=10)
            return result, events.read_text().splitlines()

    @staticmethod
    def expected_lifecycle():
        return ["check_deps", "run_build", "run_dev_tests", "run_install",
                "run_database_maintenance:--prepare", "run_db_tests", "run_competitive_model_proof",
                "run_publish", "reconcile_installed_product", "run_live_tests"]

    def test_failed_dev_controls_block_installed_product_mutation(self):
        result, events = self.execute(23)
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertEqual(events, ["check_deps", "run_build", "run_dev_tests"])

    def test_build_and_runtime_failures_stop_before_later_phases(self):
        order = self.expected_lifecycle()
        for phase in ("run_build", "run_install", "run_database_maintenance", "run_db_tests",
                      "run_competitive_model_proof", "run_publish",
                      "reconcile_installed_product", "run_live_tests"):
            with self.subTest(phase=phase):
                result, events = self.execute(0, phase)
                self.assertEqual(result.returncode, 31, result.stdout + result.stderr)
                index = next(i for i, event in enumerate(order)
                             if event.split(":", 1)[0] == phase)
                self.assertEqual(events, order[:index + 1])

    def test_each_development_suite_runs_after_an_earlier_suite_failure(self):
        source = (ROOT / "scripts/product-ci.sh").read_text()
        start = source.index("run_dev_tests() {\n")
        finish = source.index("\n}\n", start) + 3
        owner = source[start:finish]
        with tempfile.TemporaryDirectory(prefix="laplace-dev-suite-order-") as directory:
            events = Path(directory) / "events"
            # This control owns suite continuation; the artifact ownership suite
            # separately executes the real revision proof against Git and its marker.
            fixture = (
                'require_built_revision() { return 0; }\n'
                'bash() { printf "%s\\n" "$*" >> "$TEST_EVENTS"; '
                'case "$*" in *native-dev) return 7;; *uci-dev) return 11;; '
                '*) return 0;; esac; }\n')
            result = subprocess.run(
                ["bash", "-c", "set -euo pipefail\n" + fixture + owner + "\nrun_dev_tests\n"],
                env=dict(os.environ, TEST_EVENTS=str(events)),
                text=True, capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 11, result.stdout + result.stderr)
            self.assertEqual(events.read_text().splitlines(), [
                "scripts/test-parallel.sh --profile dev-native --suite native-dev",
                "scripts/test-parallel.sh --profile dev-managed --suite managed-dev",
                "scripts/test-parallel.sh --profile dev-managed --suite uci-dev",
                "scripts/test-parallel.sh --profile dev-managed --suite browser-dev"])

    def test_product_lifecycle_reaches_database_live_and_competitive_product_checks(self):
        result, events = self.execute(0)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(events, self.expected_lifecycle())

class DatabaseMaintenanceMode(unittest.TestCase):
    def execute(self, arguments, pipeline_status=0, fresh=False, classification="canonical", probe_status=0):
        with tempfile.TemporaryDirectory(prefix="laplace-database-order-") as directory:
            root = Path(directory)
            scripts = root / "scripts"
            scripts.mkdir()
            source = ROOT / "scripts/maintain-installed-database.sh"
            (scripts / source.name).write_bytes(source.read_bytes())
            trace = root / "events"
            probe = root / "probe"
            fake_bin = root / "bin"
            fake_bin.mkdir()
            (fake_bin / "psql").write_text(
                '#!/usr/bin/env bash\nset -euo pipefail\n'
                'printf "%s\\n" "$*" >> "$TEST_PROBE"\n'
                'cat >> "$TEST_PROBE"\n'
                'printf "%s\\n" "$TEST_CLASSIFICATION"\n'
                'if [[ "$TEST_PROBE_STATUS" != 0 ]]; then echo "catalog connection failed" >&2; fi\n'
                'exit "$TEST_PROBE_STATUS"\n')
            (fake_bin / "psql").chmod(0o755)
            for name, label in (("pipeline.sh", "pipeline"), ("reconcile-highway-masks.sh", "highway"),
                                ("check-database-health.sh", "health"), ("ensure-foundation.sh", "foundation")):
                status = pipeline_status if label == "pipeline" else 0
                (scripts / name).write_text(
                    'printf "%s\\n" "' + label + ':$*" >> "$TEST_EVENTS"\nexit ' + str(status) + '\n')
            result = subprocess.run(["bash", str(scripts / source.name), *arguments],
                env=dict(os.environ, TEST_EVENTS=str(trace), TEST_PROBE=str(probe),
                         TEST_CLASSIFICATION=classification, TEST_PROBE_STATUS=str(probe_status),
                         PATH=str(fake_bin) + os.pathsep + os.environ["PATH"],
                         PGDATABASE="fixture_database", PGUSER="fixture_owner",
                         LAPLACE_FRESH_DB="1" if fresh else ""),
                text=True, capture_output=True, timeout=10)
            self.last_probe = probe.read_text() if probe.exists() else ""
            return result, trace.read_text().splitlines() if trace.exists() else []

    def test_prepare_defers_data_reconciliation_and_preserves_fresh_argument(self):
        result, events = self.execute(["--prepare"], fresh=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(events, [
            "pipeline:--fresh-db migrate sync-extension tune-pg tune-laplace perfcache-guc api-env",
            "health:fixture_database"])

    def test_standalone_database_maintenance_retains_full_reconciliation(self):
        result, events = self.execute([])
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(events, [
            "pipeline:migrate sync-extension tune-pg tune-laplace perfcache-guc api-env",
            "highway:fixture_database", "health:fixture_database"])

    def test_schema_failure_prevents_reconciliation_and_health(self):
        result, events = self.execute([], pipeline_status=23)
        self.assertEqual(result.returncode, 23)
        self.assertEqual(len(events), 1)
        self.assertTrue(events[0].startswith("pipeline:"))

    def test_unknown_mode_or_extra_arguments_cannot_start_database_work(self):
        for arguments in (["unknown"], ["--prepare", "extra"]):
            with self.subTest(arguments=arguments):
                result, events = self.execute(arguments)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(events, [])


    def test_successful_complete_probe_alone_can_request_inferred_recreation(self):
        for classification, expected_fresh in (("absent", False), ("canonical", False), ("incompatible", True)):
            with self.subTest(classification=classification):
                result, events = self.execute(["--prepare"], classification=classification)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                prefix = "--fresh-db " if expected_fresh else ""
                self.assertEqual(events, [
                    "pipeline:" + prefix + "migrate sync-extension tune-pg tune-laplace perfcache-guc api-env",
                    *(["foundation:"] if expected_fresh else []),
                    "health:fixture_database"])
                self.assertIn("-X -d fixture_database -U fixture_owner -tAX -v ON_ERROR_STOP=1", self.last_probe)
                self.assertEqual(self.last_probe.count("SELECT CASE"), 1)

    def test_failed_probe_cannot_turn_any_partial_output_into_recreation(self):
        for output in ("", "incompatible", "canonical", "incompatible\ncanonical"):
            with self.subTest(output=output):
                result, events = self.execute(["--prepare"], classification=output, probe_status=2)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(events, [])
                self.assertIn("catalog connection failed", result.stderr)
                self.assertIn("could not classify entity storage", result.stderr)

    def test_empty_unsupported_or_multiple_results_cannot_start_maintenance(self):
        for output in ("", "unsupported", "h", "incompatible\ncanonical"):
            with self.subTest(output=output):
                result, events = self.execute(["--prepare"], classification=output)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(events, [])
                self.assertIn("unrecognized entity storage classification", result.stderr)

    def test_explicit_fresh_selection_keeps_existing_reset_owner_without_probe(self):
        result, events = self.execute(["--prepare"], fresh=True, probe_status=2)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.last_probe, "")
        self.assertEqual(events, [
            "pipeline:--fresh-db migrate sync-extension tune-pg tune-laplace perfcache-guc api-env",
            "health:fixture_database"])

if __name__ == "__main__":
    unittest.main(verbosity=2)
