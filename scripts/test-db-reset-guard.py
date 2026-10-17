#!/usr/bin/env python3
"""db-reset.cmd never drops a defaulted database.

A default LAPLACE_DBNAME in env.cmd once sent db-reset.cmd's DROP DATABASE to the wrong
database. The guard: env.cmd names no default, db-reset.cmd fails fast unless LAPLACE_DBNAME
is set, prints the target, and requires the same name as --confirm before any psql call. On
Windows the script is also executed (no PostgreSQL is reached: it must stop first).
"""
import json
import os
import shutil
import re
import subprocess
import tempfile
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WIN = ROOT / "scripts" / "win"


def text(name):
    return (WIN / name).read_text(encoding="utf-8")


class DbResetGuard(unittest.TestCase):
    def test_env_names_no_default_database(self):
        env = text("env.cmd")
        self.assertIsNone(re.search(r'set\s+"LAPLACE_DBNAME=', env, re.I),
                          "env.cmd must not default LAPLACE_DBNAME")
        self.assertRegex(env, r'if defined LAPLACE_DBNAME if not defined LAPLACE_DB set "LAPLACE_DB=')

    def test_reset_requires_explicit_confirmed_target_before_psql(self):
        reset = text("db-reset.cmd")
        first_psql = reset.index("psql.exe")
        guard = reset.index('call "%~dp0require-dbname.cmd"')
        confirm = reset.index('if not "%CONFIRM%"=="%LAPLACE_DBNAME%"')
        target = reset.index("db-reset target:")
        self.assertLess(guard, first_psql)
        self.assertLess(target, first_psql)
        self.assertLess(confirm, first_psql)

    def test_require_dbname_fails_fast(self):
        req = text("require-dbname.cmd")
        self.assertIn("if not defined LAPLACE_DBNAME", req)
        self.assertIn("exit /b 2", req)

    @unittest.skipUnless(os.name == "nt", "executes the cmd script on Windows only")
    def test_reset_refuses_without_name_or_confirmation(self):
        base = {k: v for k, v in os.environ.items()
                if k not in ("LAPLACE_DBNAME", "LAPLACE_DB_RESET_CONFIRM", "LAPLACE_DB", "LAPLACE_ENV_LOADED")}
        base["PGBIN"] = str(ROOT / "nonexistent-pgbin")  # any psql call would fail differently
        cases = [({}, [], "LAPLACE_DBNAME is not set"),
                 ({"LAPLACE_DBNAME": "guard-test-db"}, [], "--confirm guard-test-db"),
                 ({"LAPLACE_DBNAME": "guard-test-db"}, ["--confirm", "other-db"], "--confirm guard-test-db")]
        for extra, args, expect in cases:
            proc = subprocess.run(["cmd", "/c", str(WIN / "db-reset.cmd"), *args],
                                  env={**base, **extra}, capture_output=True, text=True, timeout=120)
            out = proc.stdout + proc.stderr
            self.assertEqual(2, proc.returncode, out)
            self.assertIn(expect, out)
            self.assertNotIn("terminate laplace backends", out)


class DeclaredAccess(unittest.TestCase):
    """A reset leaves the database usable: the roles' declared access is applied after it is recreated."""

    def test_reset_applies_declared_access_before_complete(self):
        reset = text("db-reset.cmd")
        apply = reset.index('apply-declared-access.ps1" -Database "%LAPLACE_DBNAME%" || exit /b 1')
        self.assertLess(reset.index("CREATE EXTENSION IF NOT EXISTS laplace_substrate"), apply)
        self.assertLess(apply, reset.index("DB-RESET COMPLETE"))

    def test_access_goes_through_the_operations_step(self):
        script = text("apply-declared-access.ps1")
        self.assertIn("configure-windows.ps1", script)
        self.assertIn("Laplace-Operations\\development\\installation.json", script)
        self.assertIn("has_schema_privilege('$role', 'laplace', 'USAGE')", script)
        self.assertNotRegex(script, r"(?i)\bGRANT\b", "grants are the Operations step's, not written here")

    @unittest.skipUnless(os.name == "nt" and shutil.which("pwsh"), "executes the PowerShell script on Windows only")
    def test_plan_names_the_declared_database_only(self):
        with tempfile.TemporaryDirectory() as d:
            decl = Path(d) / "installation.json"
            decl.write_text(json.dumps({"substrate_database": "plan-sub", "substrate_role": "plan_sub_role",
                                        "application_database": "plan-app", "application_role": "plan_app_role"}))
            (Path(d) / "configure-windows.ps1").write_text("throw 'not run in a plan'\n")
            def run(db, path):
                env = {**os.environ, "LAPLACE_ACCESS_DECLARATION": str(path)}
                p = subprocess.run(["pwsh", "-NoProfile", "-File", str(WIN / "apply-declared-access.ps1"),
                                    "-Database", db, "-Plan"], env=env, capture_output=True, text=True, timeout=120)
                return p.returncode, p.stdout + p.stderr
            rc, out = run("plan-sub", decl)
            self.assertEqual(0, rc, out)
            self.assertIn("substrate database", out)
            self.assertIn("plan_sub_role", out)
            self.assertIn("configure-windows.ps1 -Installation", out)
            rc, out = run("plan-app", decl)
            self.assertEqual(0, rc, out)
            self.assertIn("application database", out)
            rc, out = run("other-db", decl)
            self.assertEqual(0, rc, out)
            self.assertIn("names no database other-db", out)
            self.assertNotIn("plan:", out)
            rc, out = run("plan-sub", Path(d) / "absent.json")
            self.assertEqual(0, rc, out)
            self.assertIn("no declaration at", out)


class ReseedAnalyze(unittest.TestCase):
    """A seed leaves statistics the site can read (Laplace#1879).

    The seed role has no MAINTAIN on the postgres-owned substrate tables, so its own post-ingest
    ANALYZE is skipped with a WARNING, and autovacuum never analyzes a partitioned parent: the
    reltuples/pg_stats census behind /v1/explore/catalog read 0 on a freshly seeded database.
    The reseed scripts ANALYZE as postgres, the owner, after the seed.
    """

    def test_analyze_runs_as_the_owner_on_the_named_database(self):
        script = text("analyze-substrate.cmd")
        guard = script.index('call "%~dp0require-dbname.cmd" || exit /b 2')
        call = script.index('-U postgres -d "%LAPLACE_DBNAME%" -v ON_ERROR_STOP=1 -c "CALL ops.analyze_substrate();" || exit /b 1')
        self.assertLess(guard, call)
        self.assertIn("ops.substrate_counts()", script[call:])

    def test_foundation_analyzes_once_after_its_last_step(self):
        seed = text("seed-foundation.cmd")
        self.assertLess(seed.index('set "LAPLACE_SEED_NO_ANALYZE=1"'), seed.index("for %%S in ("))
        done = seed.index(":done")
        analyze = seed.index('call "%~dp0analyze-substrate.cmd"')
        self.assertLess(done, analyze)
        self.assertLess(analyze, seed.index("seed-foundation finished"))
        self.assertIn('set "ERR=1"', seed[analyze:seed.index("seed-foundation finished")])

    def test_a_single_step_analyzes_after_it_verifies(self):
        step = text("seed-step.cmd")
        verify = step.index("call :verify_step\nif errorlevel 1 exit /b %ERRORLEVEL%")
        skip = step.index('if "%LAPLACE_SEED_NO_ANALYZE%"=="1" exit /b 0')
        analyze = step.index('call "%~dp0analyze-substrate.cmd"')
        self.assertLess(verify, skip)
        self.assertLess(skip, analyze)


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)