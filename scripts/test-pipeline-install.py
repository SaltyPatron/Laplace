#!/usr/bin/env python3
"""Execute actual pipeline functions with isolated artifacts and fake OS/SQL calls."""
import importlib.util
import os
import json
import shlex
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
                        CALLS=str(self.base / "calls"), CURRENT="$libdir")
        (self.base / "build").mkdir()
        (self.base / "build/build.ninja").write_text("# configured build fixture\n")
        (self.base / "install/lib").mkdir(parents=True)
        (self.base / "install/lib/liblaplace_core.so").touch()

    def run_shell(self, body, **env):
        return subprocess.run(["bash", "-c", "set -euo pipefail\n" + body],
                              cwd=self.base, env=dict(self.env, **env), text=True,
                              capture_output=True, timeout=10)

    def calls(self):
        path = self.base / "calls"
        return path.read_text() if path.exists() else ""

    def chess_publication(self, **env):
        return self.run_shell(function("phase_chess_lab") + r'''
ROOT="$PWD"
bash() {
  [[ "$#" == 2 && "$1" == "$ROOT/scripts/bootstrap-chess-lab.sh" && "$2" == --cutechess-gui ]] || return 77
  printf 'published source=%s explicit=%s\n' "${LAPLACE_STOCKFISH_SOURCE:-}" "${LAPLACE_STOCKFISH:-}" >> "$CALLS"
  return "${BOOTSTRAP_RC:-0}"
}
phase_chess_lab
phase_chess_lab
''', **env)

    def test_chess_publication_delegates_each_invocation_and_preserves_selection(self):
        result = self.chess_publication(LAPLACE_STOCKFISH_SOURCE=str(self.base / "source"),
                                        LAPLACE_STOCKFISH=str(self.base / "selected-stockfish"))
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        expected = "published source=" + str(self.base / "source") + " explicit=" + str(self.base / "selected-stockfish")
        self.assertEqual([expected, expected], self.calls().splitlines())

    def test_chess_publication_preserves_bootstrap_failure(self):
        result = self.chess_publication(BOOTSTRAP_RC="23")
        self.assertEqual(23, result.returncode, result.stdout + result.stderr)
        self.assertEqual(1, len(self.calls().splitlines()))

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
        floors = self.base / "install/share/laplace"
        floors.mkdir(parents=True)
        position = floors / "laplace_chess_position_perfcache.bin"
        transition = floors / "laplace_chess_transition_perfcache.bin"
        for artifact in (modules / "laplace_substrate.so", modules / "laplace_geom.so", core, dynamics, position, transition):
            artifact.write_bytes(b"installed image")

        def digest():
            result = self.run_shell(function("preloaded_so_digest") + "\npreloaded_so_digest\n")
            self.assertEqual(0, result.returncode, result.stderr)
            return result.stdout.strip()

        before = digest()
        self.assertEqual(before, digest())
        # An execution module can need new exports even when neither preload
        # module changed. Each engine dependency and mapped chess floor independently requires reload.
        for artifact in (core, dynamics, position, transition):
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
        self.assertIn("root SQL requires local socket", result.stderr)

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
ensure_extension_library_path() { return "${PATH_RC:-1}"; }
systemctl() { [[ "${API_ACTIVE:-1}" == 1 ]]; }
sudo() {
  echo "$*" >> "$CALLS"
  if [[ "$*" == *"start laplace-api"* ]]; then return "${START_RC:-0}"; fi
}
postgresql_restart_required() {
  if [[ -f installed ]]; then
    echo release-after >> "$CALLS"
    return "${PG_AFTER_RC:-1}"
  fi
  [[ "${PG_RESTART_RC:-1}" != 2 ]] || return 2
  return "${PG_RESTART_RC:-1}"
}
preloaded_so_digest() {
  if [[ "${SAME_PRELOAD:-0}" == 1 ]]; then echo same;
  elif [[ -f installed ]]; then echo new; else echo old; fi
}
cmake() { echo install >> "$CALLS"; touch installed; return "${COPY_RC:-0}"; }
psql() {
  echo probe >> "$CALLS"
  [[ "${PROBE_RC:-0}" == 0 ]] || return "$PROBE_RC"
  printf '%s\n' "${PRELOAD:-laplace_substrate}"
}
restart_postgres() { echo bounce >> "$CALLS"; return "${BOUNCE_RC:-0}"; }
phase_install
''', **env)

    def test_server_release_mismatch_restarts_unchanged_preloads(self):
        result = self.phase(PG_RESTART_RC="0", SAME_PRELOAD="1")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        calls = self.calls().splitlines()
        self.assertEqual(1, calls.count("install"))
        self.assertEqual(1, calls.count("bounce"))
        self.assertLess(calls.index("install"), calls.index("bounce"))
        self.assertLess(calls.index("bounce"), calls.index("release-after"))
        self.assertLess(calls.index("release-after"), calls.index("-n systemctl start laplace-api"))

    def test_server_release_read_failure_refuses_install_and_service_actions(self):
        result = self.phase(PG_RESTART_RC="2")
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual("", self.calls())

    def test_failed_restart_or_stale_readback_fails_and_restores_api(self):
        for error in ({"BOUNCE_RC": "27"}, {"PG_AFTER_RC": "0"}, {"PG_AFTER_RC": "2"}):
            with self.subTest(error=error):
                (self.base / "calls").write_text("")
                (self.base / "installed").unlink(missing_ok=True)
                result = self.phase(PG_RESTART_RC="0", SAME_PRELOAD="1", **error)
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("bounce", self.calls())
                self.assertIn("start laplace-api", self.calls())

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
        for version, expected in (("180003", 0), ("180006", 1), ("170013", 2),
                                  ("190001", 2), ("", 2), ("invalid", 2)):
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

    def test_each_install_invocation_delegates_copy_and_observes_release(self):
        for _ in range(2):
            result = self.phase(SAME_PRELOAD="1")
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(2, self.calls().splitlines().count("install"))
        self.assertNotIn("bounce", self.calls())

    def test_failed_preflight_never_installs_or_touches_services(self):
        result = self.phase(PATH_RC="2")
        self.assertEqual(2, result.returncode)
        self.assertEqual("", self.calls())

    def test_failed_copy_and_probe_restore_active_api(self):
        for env in ({"COPY_RC": "23"}, {"PROBE_RC": "24"}):
            with self.subTest(env=env):
                (self.base / "calls").write_text("")
                (self.base / "installed").unlink(missing_ok=True)
                result = self.phase(**env)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("stop laplace-api", self.calls())
                self.assertIn("start laplace-api", self.calls())

    def test_success_rechecks_release_then_restores_active_api(self):
        result = self.phase(SAME_PRELOAD="1")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        calls = self.calls().splitlines()
        self.assertLess(calls.index("install"), calls.index("release-after"))
        self.assertLess(calls.index("release-after"), calls.index("-n systemctl start laplace-api"))

    def test_inactive_api_stays_stopped(self):
        self.assertEqual(0, self.phase(API_ACTIVE="0").returncode)
        self.assertNotIn("laplace-api", self.calls())

    def test_failed_api_restore_is_reported_as_failure(self):
        result = self.phase(START_RC="25")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("start laplace-api", self.calls())

    def test_deliberate_swallowed_preflight_error_is_detected(self):
        original = function("phase_install")
        broken = original.replace('[[ "$path_rc" == 1 ]] || exit "$path_rc"', ':')
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



class BuildInputTests(unittest.TestCase):
    """Execute phase_build with real selected tools/headers; compilers are protocol doubles."""
    def setUp(self):
        scratch = Path(os.environ.get("TMPDIR", ""))
        if not scratch.is_absolute() or not scratch.is_dir():
            raise RuntimeError("build input controls require an existing absolute TMPDIR")
        self.temporary = tempfile.TemporaryDirectory(prefix="pipeline-build-inputs-", dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.project = self.base / "project"
        for name in ("scripts", "deploy", "engine/core"):
            (self.project / name).mkdir(parents=True, exist_ok=True)
        for name in ("scripts/postgresql-release.py", "deploy/postgresql-release.json"):
            shutil.copy2(ROOT / name, self.project / name)
        # Only the export selector is replaced; the PG owner executes from source.
        (self.project / "scripts/chess-floor-artifacts.py").write_text(
            "import sys\nassert sys.argv[1] == 'selected-export'\nprint('')\n")
        (self.project / "engine/core/CMakeLists.txt").write_text("# no chess producer in this protocol fixture\n")
        self.build = self.project / "build"
        self.build.mkdir()
        for name in ("laplace_t0_perfcache_fixture.bin", "laplace_highway_perfcache_fixture.bin"):
            (self.build / name).write_bytes(b"protocol-only presence marker\n")
        self.calls = self.base / "calls"
        self.prefix = self.base / "pgsql-18"
        specification = importlib.util.spec_from_file_location(
            "pipeline_postgresql_fixture", ROOT / "scripts/test-postgresql-release.py")
        self.fixture = importlib.util.module_from_spec(specification)
        specification.loader.exec_module(self.fixture)
        self.fixture.write_postgresql_fixture(self.prefix)

    def build_once(self, **changes):
        environment = {**os.environ, "ROOT": str(self.project), "PYTHON": sys.executable,
                       "LAPLACE_PG_PREFIX": str(self.prefix),
                       "LAPLACE_INSTALL_PREFIX": str(self.base / "install"),
                       "LAPLACE_BUILD_DIRECTORY": str(self.build),
                       "LAPLACE_EXTERNAL": str(self.base / "external"),
                       "CALLS": str(self.calls), "FORCE_REBUILD": "0", "FORCE_CODEGEN": "0",
                       "CLEAN_FIRST": "0", **changes}
        program = function("phase_build") + r'''
phase_clean() { echo clean >> "$CALLS"; }
phase_codegen() { echo codegen >> "$CALLS"; }
chess_openings_path() { printf '%s\n' "$ROOT/openings"; }
cmake() { printf 'cmake %s\n' "$*" >> "$CALLS"; }
phase_build_app() { echo managed-build >> "$CALLS"; }
phase_build
'''
        return subprocess.run(["bash", "-c", "set -euo pipefail\n" + program],
                              cwd=self.project, env=environment, capture_output=True,
                              text=True, timeout=15)

    def test_valid_real_inputs_are_observed_before_direct_configure_and_build(self):
        for _ in range(2):
            result = self.build_once(FORCE_REBUILD="1", FORCE_CODEGEN="1")
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            receipt = json.loads(result.stdout.splitlines()[0])
            self.assertEqual("build-inputs", receipt["mode"])
            self.assertEqual("18.6", receipt["version"])
            self.assertFalse(receipt["running_server_checked"])
            self.assertEqual(str(self.prefix / "include/server"),
                             receipt["configuration_paths"]["includedir-server"])
        calls = self.calls.read_text().splitlines()
        self.assertEqual(2, calls.count("clean"))
        self.assertEqual(2, calls.count("codegen"))
        self.assertEqual(2, calls.count("managed-build"))
        configure = [row for row in calls if row.startswith("cmake -S ")]
        builds = [row for row in calls if row.startswith("cmake --build ")]
        self.assertEqual(2, len(configure))
        self.assertEqual(2, len(builds))
        self.assertTrue(all("-DLAPLACE_PG_PREFIX=" + str(self.prefix) in row for row in configure))

    def test_invalid_real_tools_or_headers_refuse_before_clean_or_any_build(self):
        for defect in ("postgres", "pg_config", "headers", "missing"):
            with self.subTest(defect=defect):
                self.fixture.write_postgresql_fixture(self.prefix)
                if defect == "headers":
                    (self.prefix / "include/server/pg_config.h").write_text(
                        '#define PG_VERSION "18.3"\n#define PG_VERSION_NUM 180003\n')
                elif defect == "missing":
                    (self.prefix / "bin/pg_config").unlink()
                else:
                    path = self.prefix / "bin" / defect
                    path.write_text(path.read_text().replace("18.6", "18.3"))
                self.calls.unlink(missing_ok=True)
                result = self.build_once(FORCE_REBUILD="1", FORCE_CODEGEN="1")
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("PostgreSQL selection failed", result.stderr)
                self.assertFalse(self.calls.exists())
        self.fixture.write_postgresql_fixture(self.prefix)
        self.assertEqual(0, self.build_once().returncode)


class ManagedPolicyPreparationTests(unittest.TestCase):
    """Actual shell/file checks; only privileged setup is a child-process double."""
    def setUp(self):
        scratch = Path(os.environ.get("TMPDIR", ""))
        if not scratch.is_absolute() or not scratch.is_dir():
            raise RuntimeError("policy preparation controls require an existing absolute TMPDIR")
        self.temporary = tempfile.TemporaryDirectory(prefix="managed-policy-prepare-", dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.project = self.base / "project"
        self.source = self.project / "deploy/linux"
        self.scripts = self.project / "scripts"
        self.installed = self.base / "installed"
        self.tools = self.base / "tools"
        for directory in (self.source, self.scripts, self.installed, self.tools):
            directory.mkdir(parents=True)
        for name in ("managed-publish.sh", "payload-sync.sh"):
            shutil.copy2(ROOT / "deploy/linux" / name, self.source / name)
        self.names = ("laplace-managed-deploy", "laplace-service-control")
        for name in self.names:
            (self.source / name).write_text("selected " + name + "\n")
        self.calls = self.base / "setup.jsonl"
        self.sudo_calls = self.base / "sudo.jsonl"
        driver = self.scripts / "fixture-setup.py"
        driver.write_text(
            "import json,os,sys\nfrom pathlib import Path\n"
            "assert sys.argv[1:] == ['managed-services'], sys.argv\n"
            "source=Path(__file__).resolve().parents[1]/'deploy/linux'\n"
            "destination=Path(os.environ['POLICY_FIXTURE_INSTALLED'])\n"
            "with Path(os.environ['POLICY_FIXTURE_CALLS']).open('a') as out:\n"
            " out.write(json.dumps({'argv':sys.argv[1:],'tmp':os.environ.get('TMPDIR')})+'\\n')\n"
            "mode=os.environ.get('POLICY_FIXTURE_MODE','update')\n"
            "if mode=='fail':\n"
            " print('fixture policy setup refused',file=sys.stderr); raise SystemExit(27)\n"
            "if mode=='unchanged': raise SystemExit(0)\n"
            "for name in ['laplace-managed-deploy','laplace-service-control']:\n"
            " if mode=='partial' and name=='laplace-service-control': continue\n"
            " path=destination/name\n"
            " path.write_bytes((source/name).read_bytes())\n"
            " path.chmod(0o775 if mode=='bad-mode' else 0o755)\n"
            "if mode=='updated-then-fail':\n"
            " print('fixture post-install refusal',file=sys.stderr); raise SystemExit(27)\n")
        (self.scripts / "setup-host.sh").write_text(
            "#!/bin/bash\nexec " + shlex.quote(sys.executable) + " "
            + shlex.quote(str(driver)) + ' "$@"\n')
        sudo = self.tools / "sudo"
        sudo.write_text(
            "#!" + sys.executable + "\n"
            "import json,os,sys\nfrom pathlib import Path\n"
            "assert sys.argv[1:3]==['-n','--'],sys.argv\n"
            "assert sys.argv[3:7]==['timeout','--signal=TERM','--kill-after=10s','600s'],sys.argv\n"
            "with Path(os.environ['POLICY_FIXTURE_SUDO']).open('a') as out:\n"
            " out.write(json.dumps(sys.argv[1:])+'\\n')\n"
            "os.execvp(sys.argv[3],sys.argv[3:])\n")
        sudo.chmod(0o755)
        self.environment = dict(
            os.environ, PATH=str(self.tools) + os.pathsep + os.environ["PATH"],
            POLICY_FIXTURE_INSTALLED=str(self.installed),
            POLICY_FIXTURE_CALLS=str(self.calls), POLICY_FIXTURE_SUDO=str(self.sudo_calls))
        self.install_matching()

    def install_matching(self):
        for name in self.names:
            target = self.installed / name
            if target.is_symlink():
                target.unlink()
            target.write_bytes((self.source / name).read_bytes())
            target.chmod(0o755)

    def invoke(self, operation, mode="update", uid=None):
        environment = dict(self.environment, POLICY_FIXTURE_MODE=mode)
        # Production execution sets TRUSTED_POLICY_UID=0 unconditionally. Source
        # the same functions with a private current-UID fixture, as root policy
        # tests do, without touching /usr/local or requiring real sudo.
        return subprocess.run([
            "bash", "-c",
            'source "$1"; HELPER="$2"; TRUSTED_POLICY_UID="$3"; "$4"',
            "policy-fixture", str(self.source / "managed-publish.sh"),
            str(self.installed / "laplace-managed-deploy"),
            str(os.getuid() if uid is None else uid), operation],
            env=environment, capture_output=True, text=True, timeout=15)

    def setup_rows(self):
        return [json.loads(line) for line in self.calls.read_text().splitlines()] \
            if self.calls.exists() else []

    def test_matching_policy_is_read_only_and_does_not_invoke_setup(self):
        before = {name: ((self.installed / name).stat().st_ino,
                         (self.installed / name).read_bytes()) for name in self.names}
        for operation in ("installed_policy", "prepare_policy"):
            result = self.invoke(operation, mode="fail")
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.setup_rows())
        self.assertFalse(self.sudo_calls.exists())
        self.assertEqual(before, {name: ((self.installed / name).stat().st_ino,
                                        (self.installed / name).read_bytes()) for name in self.names})

    def test_stale_and_missing_policy_delegate_once_then_reverify_both_files(self):
        for missing in (False, True):
            with self.subTest(missing=missing):
                self.install_matching()
                self.calls.unlink(missing_ok=True)
                target = self.installed / "laplace-service-control"
                if missing:
                    target.unlink()
                else:
                    target.write_text("old policy\n")
                result = self.invoke("prepare_policy")
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual([{"argv": ["managed-services"], "tmp": os.environ["TMPDIR"]}],
                                 self.setup_rows())
                self.assertEqual(0, self.invoke("installed_policy").returncode)
                self.assertEqual(0, self.invoke("prepare_policy", mode="fail").returncode)
                self.assertEqual(1, len(self.setup_rows()))
                if os.geteuid() != 0:
                    self.assertTrue(self.sudo_calls.is_file())

    def test_read_only_check_refuses_identity_owner_mode_and_symlink_without_setup(self):
        for defect in ("bytes", "mode", "symlink", "owner"):
            with self.subTest(defect=defect):
                self.install_matching()
                target = self.installed / "laplace-managed-deploy"
                uid = None
                if defect == "bytes":
                    target.write_text("wrong generation\n")
                elif defect == "mode":
                    target.chmod(0o775)
                elif defect == "symlink":
                    target.unlink()
                    target.symlink_to(self.source / "laplace-managed-deploy")
                else:
                    uid = os.getuid() + 1
                before = (target.lstat().st_ino, target.lstat().st_mode,
                          target.read_bytes(), target.is_symlink())
                result = self.invoke("installed_policy", uid=uid)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(before, (target.lstat().st_ino, target.lstat().st_mode,
                                          target.read_bytes(), target.is_symlink()))
                self.assertEqual([], self.setup_rows())

    def test_setup_failure_is_not_swallowed_even_after_publishing_matching_bytes(self):
        for mode in ("fail", "updated-then-fail"):
            with self.subTest(mode=mode):
                self.install_matching()
                (self.installed / "laplace-managed-deploy").write_text("old generation\n")
                self.calls.unlink(missing_ok=True)
                result = self.invoke("prepare_policy", mode=mode)
                self.assertEqual(27, result.returncode, result.stdout + result.stderr)
                self.assertEqual(1, len(self.setup_rows()))
                self.assertIn("refusal" if mode == "updated-then-fail" else "refused", result.stderr)
                if mode == "updated-then-fail":
                    self.assertEqual(0, self.invoke("installed_policy").returncode)

    def test_successful_setup_exit_cannot_cover_incomplete_or_untrusted_result(self):
        for mode in ("unchanged", "partial", "bad-mode"):
            with self.subTest(mode=mode):
                for name in self.names:
                    (self.installed / name).write_text("old generation\n")
                self.calls.unlink(missing_ok=True)
                result = self.invoke("prepare_policy", mode=mode)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(1, len(self.setup_rows()))
                self.assertNotEqual(0, self.invoke("installed_policy").returncode)

if __name__ == "__main__":
    unittest.main(verbosity=2)
