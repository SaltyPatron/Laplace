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

def body(job):
    text = (ROOT / ".github/workflows/laplace.yml").read_text()
    section = text.split("  " + job + ":\n", 1)[1]
    if job == "mainline":
        section = section.split("\n  operator:\n", 1)[0]
    raw = section.split("        run: |\n", 1)[1]
    return "\n".join(line[10:] for line in raw.splitlines()
                     if line.startswith("          ")) + "\n"

class WorkspaceReservation(unittest.TestCase):
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

    def git(self, cwd, *arguments):
        return subprocess.run(["git", *arguments], cwd=cwd, env=self.env,
                              check=True, text=True, capture_output=True, timeout=15).stdout

    def execute(self, job):
        return subprocess.run(["bash", "-c", body(job)], cwd=self.workspace, env=self.env,
                              text=True, capture_output=True, timeout=15)

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


class IntegratedLifecycle(unittest.TestCase):
    def execute(self, test_status):
        source = (ROOT / "scripts/product-ci.sh").read_text()
        start = source.index("run_deploy() {\n")
        finish = source.index("\n}\n", start) + 3
        owner = source[start:finish]
        names = ("check_deps", "run_build", "run_dev_tests", "run_install",
                 "run_database_maintenance", "run_publish", "reconcile_installed_product",
                 "run_foundation")
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

    def test_failed_dev_controls_prevent_installation(self):
        result, events = self.execute(23)
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertEqual(events, ["check_deps", "run_build", "run_dev_tests"])

    def test_publication_and_readiness_precede_resumable_foundation(self):
        result, events = self.execute(0)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(events, ["check_deps", "run_build", "run_dev_tests", "run_install",
                                 "run_database_maintenance:--prepare", "run_publish",
                                 "reconcile_installed_product", "run_foundation"])

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
