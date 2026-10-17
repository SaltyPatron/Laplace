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


class SeedRoleAnalyzes(unittest.TestCase):
    """A seed leaves statistics the site can read, as the seed role, by construction (Laplace#1879).

    The site's counts are pg_class.reltuples / pg_stats. ANALYZE by a role without MAINTAIN on a
    table or a leaf partition skips it with only a WARNING, so the seed role is granted MAINTAIN by
    the declared access db-reset applies, db-reset checks it, the CLI's post-ingest ANALYZE fails on
    any skipped table, and no reseed step runs ANALYZE as the tables' owner.
    """

    def test_reset_checks_the_seed_role_may_maintain_every_substrate_table(self):
        script = text("apply-declared-access.ps1")
        usage = script.index("has_schema_privilege('$role', 'laplace', 'USAGE')")
        maintain = script.index("NOT has_table_privilege('$role', c.oid, 'MAINTAIN')")
        self.assertLess(usage, maintain)
        self.assertIn("c.relkind IN ('r', 'p')", script)
        self.assertIn("'laplace', 'realize', 'consensus', 'generation'", script)
        self.assertRegex(script[maintain:], r"if \(\$count -ne '0'\) \{ throw ")

    def test_no_reseed_step_analyzes_as_the_owner(self):
        self.assertFalse((WIN / "analyze-substrate.cmd").exists(),
                         "the postgres ANALYZE workaround is gone: the seed role analyzes what it writes")
        for name in ("seed-foundation.cmd", "seed-step.cmd", "seed-chain.cmd", "seed-everything.cmd"):
            script = text(name)
            self.assertNotIn("analyze-substrate", script, name)
            self.assertNotIn("LAPLACE_SEED_NO_ANALYZE", script, name)
            self.assertIsNone(re.search(r'-U postgres\b.*ANALYZE', script, re.I), name)

    def test_cli_post_ingest_analyze_fails_on_a_skipped_table(self):
        ops = (ROOT / "app" / "Laplace.Substrate" / "Crud" / "Npgsql" / "NpgsqlIngestOps.cs").read_text(encoding="utf-8")
        body = ops[ops.index("public static Task AnalyzePostIngestValidationAsync"):]
        self.assertLess(body.index("AnalyzeOrThrowAsync(conn,"), body.index("public sealed record GinMaintenance"))
        self.assertIn('InvariantSeverity, "WARNING"', ops)
        cli = (ROOT / "app" / "Laplace.Cli" / "IngestCommands.cs").read_text(encoding="utf-8")
        catch = cli.index("catch (AnalyzeIncompleteException ex) { analyzeFailure = ex; }")
        self.assertLess(catch, cli.index('warn: ingest validation failed'),
                        "a skipped ANALYZE is caught before the advisory validation warning")
        tail = cli[catch:]
        fail = tail.index("if (analyzeFailure is not null)")
        self.assertLess(fail, tail.index("return 1;", fail))
        self.assertLess(tail.index("return 1;", fail), tail.index("return 0;"))

    def test_analyze_substrate_raises_on_a_skipped_or_failed_table(self):
        sql = (ROOT / "extension" / "laplace_substrate" / "sql" / "functions" / "ops" / "repair.sql.in").read_text(encoding="utf-8")
        proc = sql[sql.index("CREATE OR REPLACE PROCEDURE ops.analyze_substrate"):]
        self.assertLess(proc.index("NOT pg_catalog.has_table_privilege(p.relid, 'MAINTAIN')"),
                        proc.index("EXECUTE format('ANALYZE %s', t.rel)"))
        self.assertIn("pg_partition_tree(c.oid)", proc)
        self.assertIn("failed ANALYZE", proc)
        self.assertTrue((ROOT / "tests" / "substrate-analyze-substrate.sh").exists())


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)