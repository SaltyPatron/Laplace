#!/usr/bin/env python3
"""Exercise the real CI operator bodies against temporary local Git repositories."""
import fcntl
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]


def body(job: str, workflow: str = "laplace.yml") -> str:
    if workflow == "laplace.yml" and job in ("mainline", "operator"):
        workflow = "product-stage.yml"
        job = "stage"
    text = (ROOT / ".github/workflows" / workflow).read_text(encoding="utf-8")
    section = text.split("\n  " + job + ":\n", 1)[1]
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
        self.env = dict(
            os.environ,
            GIT_CONFIG_NOSYSTEM="1",
            GIT_CONFIG_GLOBAL=str(self.global_config),
            GIT_AUTHOR_NAME="CI fixture",
            GIT_AUTHOR_EMAIL="fixture@example.invalid",
            GIT_COMMITTER_NAME="CI fixture",
            GIT_COMMITTER_EMAIL="fixture@example.invalid",
        )
        self.git(None, "init", "--bare", str(self.origin))
        self.git(None, "init", str(self.seed))
        (self.seed / "scripts").mkdir()
        shutil.copyfile(
            ROOT / "scripts" / "ci-product-freshness.py",
            self.seed / "scripts" / "ci-product-freshness.py",
        )
        shutil.copyfile(
            ROOT / "scripts" / "ci_product_scope.py",
            self.seed / "scripts" / "ci_product_scope.py",
        )
        (self.seed / ".gitignore").write_text("build/\n", encoding="utf-8")
        (self.seed / "source.txt").write_text("old\n", encoding="utf-8")
        (self.seed / "scripts/ci-environment.sh").write_text(
            'printf "environment\\n" >> "$TEST_EVENTS"\n', encoding="utf-8")
        (self.seed / "scripts/product-ci.sh").write_text(
            '#!/usr/bin/env bash\nprintf "%s\\n" "$1" >> "$TEST_EVENTS"\n', encoding="utf-8")
        self.prepare_seed()
        self.git(self.seed, "add", ".")
        self.git(self.seed, "commit", "-m", "initial fixture")
        self.git(self.seed, "remote", "add", "origin", str(self.origin))
        self.git(self.seed, "push", "origin", "HEAD:refs/heads/main")
        self.git(None, "clone", "--branch", "main", str(self.origin), str(self.workspace))
        self.old = self.git(self.workspace, "rev-parse", "HEAD").strip()

        (self.seed / "source.txt").write_text("selected\n", encoding="utf-8")
        (self.seed / "new-source.txt").write_text("selected new file\n", encoding="utf-8")
        self.git(self.seed, "add", ".")
        self.git(self.seed, "commit", "-m", "selected fixture")
        self.git(self.seed, "push", "origin", "HEAD:refs/heads/main")
        self.target = self.git(self.seed, "rev-parse", "HEAD").strip()

        (self.workspace / "build").mkdir()
        self.marker = self.workspace / "build/retained"
        self.marker.write_text("existing qualified build\n", encoding="utf-8")
        self.env.update(
            GITHUB_WORKSPACE=str(self.workspace),
            LAPLACE_WORK_ROOT=str(self.work),
            TARGET_SHA=self.target,
            CHECKOUT_TOKEN="fixture-only",
            TEST_EVENTS=str(self.events),
            GITHUB_STEP_SUMMARY=str(self.directory / "summary.md"),
            LAPLACE_STAGE="check",
            GITHUB_REPOSITORY="fixture/repository",
        )

    def prepare_seed(self):
        pass

    def git(self, cwd, *arguments):
        return subprocess.run(
            ["git", *arguments], cwd=cwd, env=self.env, check=True,
            text=True, capture_output=True, timeout=15,
        ).stdout

    def execute(self, job: str) -> subprocess.CompletedProcess[str]:
        environment = dict(self.env)
        if job == "mainline":
            environment["LAPLACE_STAGE"] = "mainline"
            environment["LAPLACE_SKIP_IF_SUPERSEDED"] = "1"
        return subprocess.run(
            ["bash", "-c", body(job)], cwd=self.workspace, env=environment,
            text=True, capture_output=True, timeout=15,
        )


class WorkspaceReservation(WorkspaceFixture):
    def candidate(self, sha=None):
        return self.work / "product-worktrees" / (sha or self.target)

    def test_mainline_uses_exact_revision_worktree_and_waits_only_for_that_candidate(self):
        lock_path = self.work / f"product-{self.target}.lock"
        lock_path.touch()
        with lock_path.open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            environment = dict(self.env, LAPLACE_STAGE="mainline", LAPLACE_SKIP_IF_SUPERSEDED="1")
            process = subprocess.Popen(
                ["bash", "-c", 'printf "started\\n";\n' + body("mainline")],
                cwd=self.workspace, env=environment, text=True,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            )
            try:
                self.assertEqual(process.stdout.readline(), "started\n")
                candidate = self.candidate()
                time.sleep(0.25)
                self.assertIsNone(process.poll())
                self.assertFalse(candidate.exists(),
                                 "candidate must not be mutated before its revision lock is owned")
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
                self.assertFalse(self.events.exists())
                self.assertEqual(self.marker.read_text(), "existing qualified build\n")
                fcntl.flock(lock, fcntl.LOCK_UN)
                stdout, stderr = process.communicate(timeout=15)
                self.assertEqual(process.returncode, 0, stdout + stderr)
                self.assertEqual(self.git(candidate, "rev-parse", "HEAD").strip(), self.target)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate(timeout=5)
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "mainline"])

    def test_inactive_superseded_candidate_is_reclaimed_before_checkout(self):
        stale = self.candidate(self.old)
        stale.parent.mkdir(parents=True, exist_ok=True)
        self.git(self.workspace, "worktree", "add", "--detach", str(stale), self.old)
        (stale / "build-junk.bin").write_bytes(b"x" * 4096)
        (self.work / f"product-{self.old}.lock").touch()
        # Candidates touched inside the two-hour handoff window are preserved.
        handed_off = time.time() - 3 * 3600
        os.utime(stale, (handed_off, handed_off))

        result = self.execute("operator")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(stale.exists())
        self.assertEqual(self.git(self.candidate(), "rev-parse", "HEAD").strip(), self.target)

    def test_recent_superseded_candidate_is_preserved_for_handoff(self):
        stale = self.candidate(self.old)
        stale.parent.mkdir(parents=True, exist_ok=True)
        self.git(self.workspace, "worktree", "add", "--detach", str(stale), self.old)
        (self.work / f"product-{self.old}.lock").touch()

        result = self.execute("operator")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(stale.exists())
        self.assertIn("preserving recent candidate handoff", result.stdout)

    def test_stale_cleanup_does_not_create_missing_revision_lock(self):
        stale = self.candidate(self.old)
        stale.parent.mkdir(parents=True, exist_ok=True)
        self.git(self.workspace, "worktree", "add", "--detach", str(stale), self.old)

        result = self.execute("operator")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(stale.exists())
        self.assertFalse((self.work / f"product-{self.old}.lock").exists())

    def test_active_superseded_candidate_is_never_reclaimed(self):
        stale = self.candidate(self.old)
        stale.parent.mkdir(parents=True, exist_ok=True)
        self.git(self.workspace, "worktree", "add", "--detach", str(stale), self.old)
        stale_lock = self.work / f"product-{self.old}.lock"
        stale_lock.touch()

        with stale_lock.open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            result = self.execute("operator")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertTrue(stale.exists())
            self.assertEqual(self.git(stale, "rev-parse", "HEAD").strip(), self.old)

    def test_superseded_mainline_is_auditable_noop(self):
        environment = dict(self.env, LAPLACE_STAGE="mainline", LAPLACE_SKIP_IF_SUPERSEDED="1", TARGET_SHA=self.old)
        result = subprocess.run(
            ["bash", "-c", body("mainline")],
            cwd=self.workspace, env=environment,
            text=True, capture_output=True, timeout=15,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("superseded by", result.stdout)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
        self.assertFalse(self.candidate(self.old).exists())
        self.assertEqual(self.marker.read_text(), "existing qualified build\n")
        self.assertFalse(self.events.exists())

    def test_product_ignored_main_advance_does_not_supersede_candidate(self):
        policy = self.seed / ".github" / "workflows"
        policy.mkdir(parents=True)
        (policy / "policy.yml").write_text("name: Policy only\n", encoding="utf-8")
        self.git(self.seed, "add", ".github/workflows/policy.yml")
        self.git(self.seed, "commit", "-m", "policy-only successor")
        self.git(self.seed, "push", "origin", "HEAD:refs/heads/main")

        result = self.execute("mainline")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("product-equivalent", result.stdout)
        self.assertNotIn("no product stage executed", result.stdout)
        self.assertEqual(self.git(self.candidate(), "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "mainline"])

    def test_operator_uses_requested_stage_in_isolated_candidate(self):
        result = self.execute("operator")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        candidate = self.candidate()
        self.assertEqual(self.git(candidate, "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
        self.assertEqual(self.marker.read_text(), "existing qualified build\n")
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "check"])

    def test_nonrepository_control_workspace_is_not_destroyed(self):
        shutil.rmtree(self.workspace)
        self.workspace.mkdir()
        path = self.workspace / "existing-work.txt"
        path.write_text("existing work\n", encoding="utf-8")
        result = self.execute("mainline")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_text(), "existing work\n")
        self.assertFalse((self.workspace / ".git").exists())
        self.assertFalse(self.events.exists())

    def test_tracked_local_control_work_is_not_destroyed(self):
        path = self.workspace / "source.txt"
        path.write_text("uncommitted work\n", encoding="utf-8")
        result = self.execute("mainline")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_text(), "uncommitted work\n")
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
        self.assertFalse(self.events.exists())

    def test_untracked_control_work_is_preserved_and_cannot_collide_with_candidate_checkout(self):
        path = self.workspace / "new-source.txt"
        path.write_text("untracked work\n", encoding="utf-8")
        result = self.execute("operator")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(path.read_text(), "untracked work\n")
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
        self.assertEqual(self.git(self.candidate(), "rev-parse", "HEAD").strip(), self.target)
        self.assertEqual(self.events.read_text().splitlines(), ["environment", "check"])


class DatabaseWorkspaceReservation(WorkspaceFixture):
    def prepare_seed(self):
        ownership = (
            'exec 8>"$LAPLACE_WORK_ROOT/host-resource.lock"\n'
            'if flock -n 8; then echo "reservation was not inherited" >&2; exit 97; fi\n'
            'exec 8>&-\n'
            '[[ "$(git rev-parse HEAD)" == "$TARGET_SHA" ]] || exit 98\n'
        )
        (self.seed / "scripts/ci-environment.sh").write_text(
            ownership
            + '[[ "$LAPLACE_SETUP_USE_CMAKE" == false ]] || exit 95\n'
            + '[[ "$LAPLACE_SETUP_REQUIRE_BUILT_REVISION" == false ]] || exit 96\n'
            + '[[ ! -v CHECKOUT_TOKEN && ! -v checkout_auth ]] || exit 94\n'
            + 'printf "environment\\n" >> "$TEST_EVENTS"\n',
            encoding="utf-8",
        )
        for name, label in (
            ("db-migrations.sh", "migration"),
            ("pipeline.sh", "pipeline"),
            ("check-database-health.sh", "health"),
        ):
            (self.seed / "scripts" / name).write_text(
                '#!/usr/bin/env bash\nset -euo pipefail\n' + ownership
                + 'event="' + label + ':$*"\n'
                + 'printf "%s\\n" "$event" >> "$TEST_EVENTS"\n'
                + '[[ "$event" != "$TEST_FAIL_EVENT" ]] || exit 23\n',
                encoding="utf-8",
            )

    def setUp(self):
        super().setUp()
        self.env.update(
            LAPLACE_DB_OPERATION="status",
            PGDATABASE="fixture_database",
            PGHOST="/test/no-database",
            PGUSER="fixture_user",
            TEST_FAIL_EVENT="",
        )

    def execute_database(self, operation="status"):
        environment = dict(self.env, LAPLACE_DB_OPERATION=operation)
        return subprocess.run(
            ["bash", "-c", body("db", "db-ops.yml")],
            cwd=self.workspace, env=environment,
            text=True, capture_output=True, timeout=15,
        )

    def test_database_waits_for_real_host_lock(self):
        fetch_head = self.workspace / ".git/FETCH_HEAD"
        prior_fetch = fetch_head.read_bytes() if fetch_head.exists() else None
        with (self.work / "host-resource.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            process = subprocess.Popen(
                ["bash", "-c", 'printf "started\\n";\n' + body("db", "db-ops.yml")],
                cwd=self.workspace, env=self.env, text=True,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            )
            try:
                self.assertEqual(process.stdout.readline(), "started\n")
                time.sleep(0.25)
                self.assertIsNone(process.poll())
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
                self.assertEqual(fetch_head.read_bytes() if fetch_head.exists() else None, prior_fetch)
                self.assertFalse(self.events.exists())
                fcntl.flock(lock, fcntl.LOCK_UN)
                stdout, stderr = process.communicate(timeout=15)
                self.assertEqual(process.returncode, 0, stdout + stderr)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate(timeout=5)
        self.assertEqual(self.events.read_text().splitlines(), [
            "environment", "migration:status", "health:fixture_database"
        ])

    def test_database_operations_are_direct(self):
        pipeline = "pipeline:sync-extension tune-pg tune-laplace perfcache-guc api-env"
        cases = (
            ("status", ["migration:status", "health:fixture_database"]),
            ("migrate", ["migration:up", "health:fixture_database"]),
            ("repair", [pipeline, "health:fixture_database"]),
            ("remigrate", ["migration:reset --yes", "migration:up", "health:fixture_database"]),
            ("recreate", ["migration:nuke --yes", "migration:up", pipeline, "health:fixture_database"]),
        )
        for operation, expected in cases:
            with self.subTest(operation=operation):
                self.events.unlink(missing_ok=True)
                result = self.execute_database(operation)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(self.events.read_text().splitlines(), ["environment", *expected])

    def test_recreate_dispatch_is_the_explicit_destructive_operation(self):
        self.events.unlink(missing_ok=True)
        result = self.execute_database("recreate")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(
            self.events.read_text().splitlines(),
            [
                "environment",
                "migration:nuke --yes",
                "migration:up",
                "pipeline:sync-extension tune-pg tune-laplace perfcache-guc api-env",
                "health:fixture_database",
            ],
        )

    def test_database_failure_stops_later_work(self):
        self.env["TEST_FAIL_EVENT"] = "migration:up"
        result = self.execute_database("recreate")
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertEqual(self.events.read_text().splitlines(), [
            "environment", "migration:nuke --yes", "migration:up"
        ])

    def test_removed_hidden_aliases_do_not_start_database_work(self):
        for operation in ("create", "drop", "update", "verify", "seed", "unknown"):
            with self.subTest(operation=operation):
                self.events.unlink(missing_ok=True)
                result = self.execute_database(operation)
                self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
                self.assertEqual(self.events.read_text().splitlines(), ["environment"])

    def test_database_preserves_local_work(self):
        path = self.workspace / "source.txt"
        path.write_text("uncommitted work\n", encoding="utf-8")
        result = self.execute_database()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(path.read_text(), "uncommitted work\n")
        self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD").strip(), self.old)
        self.assertFalse(self.events.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
