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
                # With the lock still held, source selection cannot occur.
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


class DatabaseWorkspaceReservation(WorkspaceFixture):
    def prepare_seed(self):
        # Real Git and flock are exercised. Only the database commands are
        # fixtures: this suite must not connect to or mutate a developer DB.
        ownership = (
            'exec 8>"$LAPLACE_WORK_ROOT/host-resource.lock"\n'
            'if flock -n 8; then echo "reservation was not inherited" >&2; exit 97; fi\n'
            'exec 8>&-\n'
            '[[ "$(git rev-parse HEAD)" == "$TARGET_SHA" ]] || exit 98\n')
        (self.seed / "scripts/ci-environment.sh").write_text(
            ownership
            + '[[ "$LAPLACE_SETUP_USE_CMAKE" == false ]] || exit 95\n'
            + '[[ "$LAPLACE_SETUP_REQUIRE_BUILT_REVISION" == true ]] || exit 96\n'
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
            ("create", ["migration:up", pipeline, "health:fixture_database"]),
            ("drop", ["migration:nuke --yes"]),
            ("recreate", ["migration:nuke --yes", "migration:up", pipeline,
                           "health:fixture_database"]),
            ("update", ["maintenance:"]),
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
                         ["environment", "migration:nuke --yes", "migration:up"])

    def test_seed_and_unknown_operations_never_start_database_work(self):
        for operation in ("seed", "unknown"):
            with self.subTest(operation=operation):
                self.events.unlink(missing_ok=True)
                result = self.execute_database(operation)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertEqual(self.events.read_text().splitlines(), ["environment"])

class IntegratedLifecycle(unittest.TestCase):
    def execute(self, test_status):
        source = (ROOT / "scripts/product-ci.sh").read_text()
        start = source.index("run_deploy() {\n")
        finish = source.index("\n}\n", start) + 3
        owner = source[start:finish]
        names = ("check_deps", "run_build", "run_dev_tests", "run_install",
                 "run_database_maintenance", "run_db_tests", "run_publish",
                 "reconcile_installed_product", "run_live_tests")
        with tempfile.TemporaryDirectory(prefix="laplace-lifecycle-order-") as directory:
            events = Path(directory) / "events"
            functions = []
            for name in names:
                status = test_status if name == "run_dev_tests" else 0
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
                "run_database_maintenance:--prepare", "run_db_tests", "run_publish",
                "reconcile_installed_product", "run_live_tests"]

    def test_failed_dev_controls_retain_downstream_product_evidence(self):
        result, events = self.execute(23)
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertEqual(events, self.expected_lifecycle())

    def test_product_lifecycle_reaches_database_and_live_product_checks(self):
        result, events = self.execute(0)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(events, self.expected_lifecycle())

class DatabaseMaintenanceMode(unittest.TestCase):
    def execute(self, arguments, pipeline_status=0, fresh=False):
        with tempfile.TemporaryDirectory(prefix="laplace-database-order-") as directory:
            root = Path(directory)
            scripts = root / "scripts"
            scripts.mkdir()
            source = ROOT / "scripts/maintain-installed-database.sh"
            (scripts / source.name).write_bytes(source.read_bytes())
            trace = root / "events"
            for name, label in (("pipeline.sh", "pipeline"), ("reconcile-highway-masks.sh", "highway"),
                                ("check-database-health.sh", "health")):
                status = pipeline_status if label == "pipeline" else 0
                (scripts / name).write_text(
                    'printf "%s\\n" "' + label + ':$*" >> "$TEST_EVENTS"\nexit ' + str(status) + '\n')
            result = subprocess.run(["bash", str(scripts / source.name), *arguments],
                env=dict(os.environ, TEST_EVENTS=str(trace), PGDATABASE="fixture_database",
                         LAPLACE_FRESH_DB="1" if fresh else ""),
                text=True, capture_output=True, timeout=10)
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

if __name__ == "__main__":
    unittest.main(verbosity=2)
