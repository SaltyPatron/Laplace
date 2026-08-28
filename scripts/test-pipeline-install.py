#!/usr/bin/env python3
"""Execute actual pipeline functions with isolated artifacts and fake OS/SQL calls."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = (ROOT / "scripts/pipeline.sh").read_text()


def function(name):
    match = re.search(r"^" + name + r"\(\) ([{(])\n.*?^[})]$", SOURCE, re.M | re.S)
    if match is None:
        raise AssertionError(f"pipeline function missing: {name}")
    return match.group()


class InstallTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-install-test-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.env = {k: v for k, v in os.environ.items() if not k.startswith("PG")}
        self.env.update(PGHOST="/var/run/postgresql", PGDATABASE="laplace",
                        LAPLACE_PG_PREFIX=str(self.base / "pg"),
                        LAPLACE_INSTALL_PREFIX=str(self.base / "install"),
                        LAPLACE_EXT_LIBDIR=str(self.base / "install/lib/postgresql/18"),
                        FP_STAMP_DIR=str(self.base / "stamps"),
                        CALLS=str(self.base / "calls"), CURRENT="$libdir")
        (self.base / "build").mkdir()
        (self.base / "stamps").mkdir()
        (self.base / "stamps/build-native").write_text("known\n")
        (self.base / "install/lib").mkdir(parents=True)
        (self.base / "install/lib/liblaplace_core.so").touch()

    def run_shell(self, body, **env):
        return subprocess.run(["bash", "-c", "set -euo pipefail\n" + body],
                              cwd=self.base, env=dict(self.env, **env), text=True,
                              capture_output=True, timeout=10)

    def calls(self):
        path = self.base / "calls"
        return path.read_text() if path.exists() else ""

    def library(self, **env):
        return self.run_shell(function("ensure_extension_library_path") + r'''
psql() {
  printf '%s\n' "$*" >> "$CALLS"
  if [[ "$*" == *"SHOW dynamic_library_path"* ]]; then
    [[ "${READ_FAIL:-0}" == 0 ]] || return 17
    printf '%s\n' "$CURRENT"
  else
    [[ "${WRITE_FAIL:-0}" == 0 ]] || return 18
    printf '%s\n' "${RELOADED-t}"
  fi
}
if ensure_extension_library_path; then exit 0; else exit $?; fi
''', **env)

    def test_failed_read_aborts_without_write_or_success(self):
        result = self.library(READ_FAIL="1")
        self.assertEqual(2, result.returncode)
        self.assertNotIn("ALTER SYSTEM", self.calls())
        self.assertNotIn(" -> ", result.stdout)

    def test_empty_read_aborts(self):
        self.assertEqual(2, self.library(CURRENT="").returncode)
        self.assertNotIn("ALTER SYSTEM", self.calls())

    def test_failed_write_and_reload_ack_abort(self):
        for env in ({"WRITE_FAIL": "1"}, {"RELOADED": "f"}, {"RELOADED": ""}):
            with self.subTest(env=env):
                result = self.library(**env)
                self.assertEqual(2, result.returncode)
                self.assertNotIn(" -> ", result.stdout)

    def test_unchanged_is_distinct_from_failure(self):
        result = self.library(CURRENT=self.env["LAPLACE_EXT_LIBDIR"] + ":$libdir")
        self.assertEqual(1, result.returncode)
        self.assertNotIn("ALTER SYSTEM", self.calls())

    def test_changed_path_preserves_and_quotes_operator_entries(self):
        result = self.library(CURRENT="$libdir:/operator's/modules")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn(":$libdir:/operator''s/modules'", self.calls())
        self.assertIn(" -> ", result.stdout)

    def test_root_sql_uses_service_peer_not_password_or_tcp(self):
        result = self.run_shell(function("psql") + r'''
id() { echo 0; }
runuser() { printf '%s\n' "$@" > "$CALLS"; }
psql -d postgres -U laplace_admin -c 'SHOW dynamic_library_path'
''')
        self.assertEqual(0, result.returncode, result.stderr)
        args = self.calls().splitlines()
        self.assertEqual(["-u", "laplace-runner", "--", "env"], args[:4])
        for key in ("PGPASSWORD", "PGPASSFILE", "PGSERVICE", "PGSERVICEFILE", "PGHOSTADDR"):
            self.assertEqual("-u", args[args.index(key) - 1])
        self.assertIn("PGHOST=/var/run/postgresql", args)
        self.assertIn("PGUSER=laplace_admin", args)
        self.assertIn("-X", args)
        self.assertIn("-w", args)

    def test_root_tcp_is_rejected(self):
        result = self.run_shell(function("psql") + '\nid() { echo 0; }; psql -c SELECT',
                                PGHOST="192.168.1.2")
        self.assertEqual(2, result.returncode)
        self.assertIn("local PostgreSQL socket", result.stderr)

    def test_nonroot_does_not_change_identity(self):
        binary = self.base / "pg/bin/psql"
        binary.parent.mkdir(parents=True)
        binary.write_text('#!/bin/sh\nprintf "%s\\n" "$@" > "$CALLS"\n')
        binary.chmod(0o700)
        result = self.run_shell(function("psql") + '\nid() { echo 994; }; psql -c SELECT')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["-X", "-w", "-c", "SELECT"], self.calls().splitlines())

    def phase(self, source=None, **env):
        return self.run_shell((source or function("phase_install")) + r'''
fp_native() { echo known; }
fp_check() { return 1; }
fp_record() { echo stamp >> "$CALLS"; }
ensure_extension_library_path() { return "${PATH_RC:-1}"; }
systemctl() { [[ "${API_ACTIVE:-1}" == 1 ]]; }
sudo() {
  echo "$*" >> "$CALLS"
  if [[ "$*" == *"start laplace-api"* ]]; then return "${START_RC:-0}"; fi
}
preloaded_so_digest() { if [[ -f installed ]]; then echo new; else echo old; fi; }
cmake() { echo install >> "$CALLS"; touch installed; return "${COPY_RC:-0}"; }
psql() { echo probe >> "$CALLS"; return "${PROBE_RC:-0}"; }
restart_postgres() { echo bounce >> "$CALLS"; }
phase_install
''', **env)

    def test_failed_preflight_never_installs_or_touches_services(self):
        result = self.phase(PATH_RC="2")
        self.assertEqual(2, result.returncode)
        self.assertEqual("", self.calls())

    def test_failed_copy_and_probe_restore_active_api_without_stamp(self):
        for env in ({"COPY_RC": "23"}, {"PROBE_RC": "24"}):
            with self.subTest(env=env):
                (self.base / "calls").write_text("")
                (self.base / "installed").unlink(missing_ok=True)
                result = self.phase(**env)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("stop laplace-api", self.calls())
                self.assertIn("start laplace-api", self.calls())
                self.assertNotIn("stamp", self.calls())

    def test_success_stamps_and_restores_active_api(self):
        result = self.phase()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("stamp", self.calls())
        self.assertIn("start laplace-api", self.calls())

    def test_inactive_api_stays_stopped(self):
        self.assertEqual(0, self.phase(API_ACTIVE="0").returncode)
        self.assertNotIn("laplace-api", self.calls())

    def test_failed_api_restore_does_not_stamp_success(self):
        result = self.phase(START_RC="25")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("start laplace-api", self.calls())
        self.assertNotIn("stamp", self.calls())

    def test_deliberate_swallowed_preflight_error_is_detected(self):
        original = function("phase_install")
        broken = original.replace('[[ "$path_rc" -eq 1 ]] || return "$path_rc"', ':')
        self.assertNotEqual(original, broken)
        result = self.phase(source=broken, PATH_RC="2")
        self.assertEqual(0, result.returncode)
        self.assertIn("install", self.calls())  # the no-install regression would fail
        (self.base / "calls").write_text("")
        self.assertEqual(2, self.phase(PATH_RC="2").returncode)
        self.assertEqual("", self.calls())

    def test_setup_does_not_install_after_failed_build(self):
        setup = (ROOT / "scripts/setup-host.sh").read_text()
        match = re.search(r"    \(\n        cd \"\$REPO_DIR\".*?\) \|\| _pipeline_rc=\$\?", setup, re.S)
        self.assertIsNotNone(match)
        script = self.base / "scripts/pipeline.sh"
        script.parent.mkdir()
        script.write_text('#!/bin/bash\necho "$1" >> "$CALLS"\n[[ "$1" != build ]]\n')
        body = '_pipeline_rc=0\n' + match.group() + '\nexit "$_pipeline_rc"'
        result = self.run_shell(body, REPO_DIR=str(self.base))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("build\n", self.calls())
        # The old subshell continued to a successful install and lost build's rc.
        (self.base / "calls").write_text("")
        broken = body.replace('bash scripts/pipeline.sh build &&', 'bash scripts/pipeline.sh build')
        self.assertNotEqual(body, broken)
        self.assertEqual(0, self.run_shell(broken, REPO_DIR=str(self.base)).returncode)
        self.assertEqual("build\ninstall\n", self.calls())


if __name__ == "__main__":
    unittest.main(verbosity=2)
