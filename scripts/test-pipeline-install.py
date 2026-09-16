#!/usr/bin/env python3
"""Execute actual pipeline functions with isolated artifacts and fake OS/SQL calls."""
import os
from pathlib import Path
import re
import subprocess
import shutil
import sys
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

    def chess_publication_fixture(self):
        sources = [self.base / name for name in ("source-one", "source-two")]
        for source in sources:
            binary = source / "src/stockfish"
            binary.parent.mkdir(parents=True)
            binary.write_text("#!/bin/sh\nexit 0\n")
            binary.chmod(0o755)
        cc = self.base / "install/bin/cutechess-cli"
        cc.parent.mkdir(parents=True)
        cc.write_text("#!/bin/sh\nexit 0\n")
        cc.chmod(0o755)
        # Exercise the real publication state machine. The controlled bootstrap
        # materializes the GUI artifacts that its production contract now owns;
        # GUI source/runtime verification is exercised by the verifier's own tests.
        return sources, function("phase_chess_lab") + r'''
ROOT="$PWD"
export LAPLACE_CUTECHESS_GUI="$LAPLACE_INSTALL_PREFIX/bin/cutechess"
export LAPLACE_CUTECHESS_GUI_RECEIPT="$PWD/cutechess-gui-build.json"
export LAPLACE_CUTECHESS_BUILD="$PWD/cutechess-build"
GUI_CHECKS="$PWD/gui-checks"
fp_compute() { printf '%s\n' unchanged-files; }
fp_check() { [[ -f "$FP_STAMP_DIR/$1" && $(cat "$FP_STAMP_DIR/$1") == "$2" ]]; }
fp_record() { printf '%s\n' "$2" > "$FP_STAMP_DIR/$1"; }
python3() {
  if [[ "$*" == *--print-path* ]]; then
    printf '%s/src/stockfish\n' "$LAPLACE_STOCKFISH_SOURCE"
  elif [[ "$*" == *" --gui "* ]]; then
    printf '%s\n' "$*" >> "$GUI_CHECKS"
    [[ "${GUI_VERIFICATION_FAILURE:-0}" == 0 ]] || return 23
  fi
}
bash() {
  [[ "$#" == 2 && "$1" == "$ROOT/scripts/bootstrap-chess-lab.sh" && "$2" == --cutechess-gui ]] || return 77
  printf 'published source=%s explicit=%s\n' "$LAPLACE_STOCKFISH_SOURCE" "${LAPLACE_STOCKFISH:-}" >> "$CALLS"
  cp "$LAPLACE_INSTALL_PREFIX/bin/cutechess-cli" "$LAPLACE_CUTECHESS_GUI"
  chmod +x "$LAPLACE_CUTECHESS_GUI"
  printf '%s\n' '{"scope":"controlled-publication-fixture"}' > "$LAPLACE_CUTECHESS_GUI_RECEIPT"
}
export LAPLACE_STOCKFISH_SOURCE="$PWD/source-one"
unset LAPLACE_STOCKFISH LAPLACE_CUTECHESS GUI_VERIFICATION_FAILURE
'''

    def test_chess_source_and_executable_selection_invalidate_publish_stamp(self):
        sources, script = self.chess_publication_fixture()
        result = self.run_shell(script + r'''
phase_chess_lab
phase_chess_lab
export LAPLACE_STOCKFISH_SOURCE="$PWD/source-two"
phase_chess_lab
export LAPLACE_STOCKFISH="$PWD/source-one/src/stockfish"
phase_chess_lab
''')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        calls = self.calls().splitlines()
        self.assertEqual(3, len(calls), calls)
        self.assertIn("source=", calls[0])
        self.assertIn("source-two", calls[1])
        self.assertIn("explicit=" + str(sources[0] / "src/stockfish"), calls[2])
        checks = (self.base / "gui-checks").read_text().splitlines()
        self.assertEqual(1, len(checks), checks)
        self.assertIn("--binary " + str(self.base / "install/bin/cutechess"), checks[0])
        self.assertIn("--verify-receipt " + str(self.base / "cutechess-gui-build.json"), checks[0])

    def test_chess_unchanged_stamp_requires_gui_artifacts_and_successful_reverification(self):
        _, script = self.chess_publication_fixture()
        result = self.run_shell(script + r'''
phase_chess_lab
phase_chess_lab
rm "$LAPLACE_CUTECHESS_GUI"
phase_chess_lab
phase_chess_lab
rm "$LAPLACE_CUTECHESS_GUI_RECEIPT"
phase_chess_lab
phase_chess_lab
chmod -x "$LAPLACE_CUTECHESS_GUI"
phase_chess_lab
phase_chess_lab
GUI_VERIFICATION_FAILURE=1 phase_chess_lab
''')
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        # Missing or non-executable artifacts force publication even when the
        # source fingerprint is unchanged. A verifier refusal cannot pass or
        # silently replace the installed artifact with another bootstrap.
        self.assertEqual(4, len(self.calls().splitlines()), self.calls())
        checks = (self.base / "gui-checks").read_text().splitlines()
        self.assertEqual(5, len(checks), checks)

    def test_install_manifest_detects_replacement_deletion_and_symlink_change(self):
        import runpy
        digest = runpy.run_path(str(ROOT / "scripts/installed-artifact-digest.py"))["installed_digest"]
        first = self.base / "first.so"
        second = self.base / "second.so"
        link = self.base / "library.so"
        first.write_bytes(b"same")
        second.write_bytes(b"same")
        link.symlink_to(first.name)
        manifest = self.base / "build/install_manifest.txt"
        manifest.write_text(str(link) + "\n")
        before = digest(manifest)
        self.assertEqual(before, digest(manifest))
        first.write_bytes(b"changed")
        self.assertNotEqual(before, digest(manifest))
        first.write_bytes(b"same")
        link.unlink()
        link.symlink_to(second.name)
        self.assertNotEqual(before, digest(manifest))
        second.unlink()
        with self.assertRaises(FileNotFoundError):
            digest(manifest)

    def test_preload_digest_tracks_engine_dependencies(self):
        modules = Path(self.env["LAPLACE_EXT_LIBDIR"])
        modules.mkdir(parents=True)
        core = self.base / "install/lib/liblaplace_core.so"
        dynamics = self.base / "install/lib/liblaplace_dynamics.so"
        for artifact in (modules / "laplace_substrate.so", modules / "laplace_geom.so", core, dynamics):
            artifact.write_bytes(b"installed image")

        def digest():
            result = self.run_shell(function("preloaded_so_digest") + "\npreloaded_so_digest\n")
            self.assertEqual(0, result.returncode, result.stderr)
            return result.stdout.strip()

        before = digest()
        self.assertEqual(before, digest())
        # An execution module can need new exports even when neither preload
        # module changed. Each engine dependency independently requires reload.
        for artifact in (core, dynamics):
            with self.subTest(library=artifact.name):
                artifact.write_bytes(b"new engine image")
                self.assertNotEqual(before, digest())
                artifact.write_bytes(b"installed image")
                self.assertEqual(before, digest())
        (modules / "new-version.sql").write_text("SELECT 1;")
        self.assertEqual(before, digest())

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
        env.setdefault("LAPLACE_BUILD_DIRECTORY", str(self.base / "build"))
        return self.run_shell((source or function("phase_install")) + r'''
fp_native() { echo known; }
fp_check() {
  if [[ "$1" == install-native ]]; then return "${NATIVE_MATCH_RC:-1}"; fi
  return "${ARTIFACT_MATCH_RC:-1}"
}
installed_artifact_digest() { echo live; }
fp_record() { echo stamp >> "$CALLS"; }
ensure_extension_library_path() { return "${PATH_RC:-1}"; }
systemctl() { [[ "${API_ACTIVE:-1}" == 1 ]]; }
sudo() {
  echo "$*" >> "$CALLS"
  if [[ "$*" == *"start laplace-api"* ]]; then return "${START_RC:-0}"; fi
}
postgresql_restart_required() {
  if [[ -f pg-restarted ]]; then return "${PG_AFTER_RC:-1}"; fi
  return "${PG_RESTART_RC:-1}"
}
preloaded_so_digest() {
  if [[ "${SAME_PRELOAD:-0}" == 1 ]]; then echo same;
  elif [[ -f installed ]]; then echo new; else echo old; fi
}
cmake() { echo install >> "$CALLS"; touch installed; return "${COPY_RC:-0}"; }
psql() { echo probe >> "$CALLS"; return "${PROBE_RC:-0}"; }
restart_postgres() { echo bounce >> "$CALLS"; touch pg-restarted; return "${BOUNCE_RC:-0}"; }
phase_install
''', **env)


    def test_server_release_mismatch_cannot_skip_and_restarts_unchanged_preloads(self):
        result = self.phase(NATIVE_MATCH_RC="0", ARTIFACT_MATCH_RC="0",
                            PG_RESTART_RC="0", SAME_PRELOAD="1")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        calls = self.calls().splitlines()
        self.assertEqual(1, calls.count("install"))
        self.assertEqual(1, calls.count("bounce"))
        self.assertLess(calls.index("install"), calls.index("bounce"))
        self.assertLess(calls.index("bounce"), calls.index("stamp"))

    def test_server_release_read_failure_refuses_install_and_service_actions(self):
        result = self.phase(PG_RESTART_RC="2")
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual("", self.calls())

    def test_failed_restart_or_stale_readback_never_stamps_activation(self):
        for error in ({"BOUNCE_RC": "27"}, {"PG_AFTER_RC": "0"}, {"PG_AFTER_RC": "2"}):
            with self.subTest(error=error):
                (self.base / "calls").write_text("")
                (self.base / "installed").unlink(missing_ok=True)
                (self.base / "pg-restarted").unlink(missing_ok=True)
                result = self.phase(PG_RESTART_RC="0", SAME_PRELOAD="1", **error)
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("bounce", self.calls())
                self.assertIn("start laplace-api", self.calls())
                self.assertNotIn("stamp", self.calls())

    def test_running_release_comparison_uses_actual_helper_and_refuses_bad_observation(self):
        binary_root = self.base / "pg/bin"
        binary_root.mkdir(parents=True)
        for name, label in (("postgres", "postgres (PostgreSQL)"), ("pg_config", "PostgreSQL")):
            executable = binary_root / name
            executable.write_text("#!/bin/sh\nprintf '%s\\n' '" + label + " 18.6'\n")
            executable.chmod(0o755)
        script = function("postgresql_restart_required") + r'''
psql() {
  [[ "$*" == "-d postgres -U laplace_admin -tAc SHOW server_version_num" ]] || return 98
  [[ "${READ_FAIL:-0}" == 0 ]] || return 29
  printf '%s\n' "$RUNNING_VERSION"
}
if postgresql_restart_required; then exit 0; else exit $?; fi
'''
        for version, expected in (("180003", 0), ("180006", 1), ("", 2), ("invalid", 2)):
            with self.subTest(version=version):
                result = self.run_shell(script, ROOT=str(ROOT), PYTHON=sys.executable,
                                        RUNNING_VERSION=version)
                self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        self.assertEqual(2, self.run_shell(script, ROOT=str(ROOT), PYTHON=sys.executable,
                                          RUNNING_VERSION="180006", READ_FAIL="1").returncode)
        executable = binary_root / "postgres"
        executable.write_text(executable.read_text().replace("18.6", "18.3"))
        self.assertEqual(2, self.run_shell(script, ROOT=str(ROOT), PYTHON=sys.executable,
                                          RUNNING_VERSION="180003").returncode)

    def test_native_and_runtime_fingerprints_change_with_tracked_postgresql_release(self):
        source = self.base / "fingerprint-source"
        (source / "scripts/lib").mkdir(parents=True)
        (source / "deploy").mkdir()
        shutil.copy2(ROOT / "scripts/lib/fp.sh", source / "scripts/lib/fp.sh")
        release = source / "deploy/postgresql-release.json"
        release.write_bytes((ROOT / "deploy/postgresql-release.json").read_bytes())
        subprocess.run(["git", "init", "--quiet", str(source)], check=True, timeout=10)
        subprocess.run(["git", "-C", str(source), "add", "."], check=True, timeout=10)
        body = 'source "$ROOT/scripts/lib/fp.sh"\nfp_native\nfp_runtime\n'
        env = {"ROOT": str(source), "LAPLACE_CHESS_OPENINGS": str(source / "absent-openings")}
        before = self.run_shell(body, **env)
        self.assertEqual(0, before.returncode, before.stderr)
        self.assertEqual(before.stdout, self.run_shell(body, **env).stdout)
        release.write_text(release.read_text().replace('"18.6"', '"18.7"'))
        after = self.run_shell(body, **env)
        self.assertEqual(0, after.returncode, after.stderr)
        old_hashes, new_hashes = before.stdout.splitlines(), after.stdout.splitlines()
        self.assertEqual(2, len(old_hashes))
        self.assertEqual(2, len(new_hashes))
        for old, new in zip(old_hashes, new_hashes):
            self.assertNotEqual(old, new)

    def test_matching_source_with_changed_install_reinstalls(self):
        result = self.phase(NATIVE_MATCH_RC="0", ARTIFACT_MATCH_RC="1")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("install\n", self.calls())

    def test_matching_source_and_live_artifacts_skip_service_actions(self):
        result = self.phase(NATIVE_MATCH_RC="0", ARTIFACT_MATCH_RC="0")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", self.calls())

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
        match = re.search(r"bash -c '(.*?)' _ \"\$REPO_DIR\" \"\$setvars\"", setup, re.S)
        self.assertIsNotNone(match)
        self.assertIn('sudo -u "$RUNNER_USER" -H env', setup)
        script = self.base / "scripts/pipeline.sh"
        script.parent.mkdir()
        script.write_text('#!/bin/bash\necho "$1" >> "$CALLS"\n[[ "$1" != build ]]\n')
        oneapi = self.base / "setvars.sh"
        oneapi.write_text(':\n')
        body = 'set -- "$REPO_DIR" "$ONEAPI"\n' + match.group(1)
        result = self.run_shell(body, REPO_DIR=str(self.base), ONEAPI=str(oneapi))
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("build\n", self.calls())
        (self.base / "calls").write_text("")
        broken = body.replace('bash scripts/pipeline.sh build &&', 'bash scripts/pipeline.sh build').replace('set -e', 'set +e')
        self.assertNotEqual(body, broken)
        self.assertEqual(0, self.run_shell(broken, REPO_DIR=str(self.base), ONEAPI=str(oneapi)).returncode)
        self.assertEqual("build\ninstall\n", self.calls())



if __name__ == "__main__":
    unittest.main(verbosity=2)
