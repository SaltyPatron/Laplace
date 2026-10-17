#!/usr/bin/env python3
"""db-reset.cmd never drops a defaulted database.

A default LAPLACE_DBNAME in env.cmd once sent db-reset.cmd's DROP DATABASE to the wrong
database. The guard: env.cmd names no default, db-reset.cmd fails fast unless LAPLACE_DBNAME
is set, prints the target, and requires the same name as --confirm before any psql call. On
Windows the script is also executed (no PostgreSQL is reached: it must stop first).
"""
import os
import re
import subprocess
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


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)