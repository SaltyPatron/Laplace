#!/usr/bin/env python3
"""Real filesystem/process controls for the PostgreSQL dependency selection."""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import shlex
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("postgresql_release", ROOT / "scripts/postgresql-release.py")
assert spec and spec.loader
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class PostgreSQLReleaseTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(os.environ.get("TMPDIR", ""))
        if not scratch.is_absolute() or not scratch.is_dir():
            raise RuntimeError("tests require an existing absolute permanent TMPDIR")
        self.temporary = tempfile.TemporaryDirectory(prefix="postgresql-release-", dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.external = self.root / "external"
        self.source = self.external / "postgresql"
        self.source.mkdir(parents=True)
        self.selected = copy.deepcopy(owner.contract(owner.DEFAULT_CONTRACT))
        (self.source / "configure.ac").write_text(
            "AC_INIT([PostgreSQL], [18.6], [pgsql-bugs@lists.postgresql.org])\n")
        (self.source / "COPYRIGHT").write_bytes(b"fixture license\n")
        self.selected["license_sha256"] = owner.sha256(self.source / "COPYRIGHT")
        self.git("init", "--quiet")
        self.git("add", "configure.ac", "COPYRIGHT")
        self.git("-c", "user.name=PG selection fixture", "-c",
                 "user.email=pg-fixture@example.invalid", "commit", "--quiet", "-m", "fixture")
        self.selected["commit"] = self.git("rev-parse", "HEAD").strip()
        self.pins = self.external / "PINS.tsv"
        self.pins.write_bytes(b"# independent cache selections\nexternal/eigen\thttps://example.invalid/eigen.git\t"
                              + b"1" * 40 + b"\n" + owner.pin_line(self.selected))
        self.pins.chmod(0o664)
        self.prefix = self.root / "prefix" / "pgsql-18"
        (self.prefix / "bin").mkdir(parents=True)
        self.tools("18.6")

    def git(self, *argv):
        return subprocess.run(["git", "-C", str(self.source), *argv], check=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              text=True, timeout=15).stdout

    def tools(self, version):
        for name, label in (("postgres", "postgres (PostgreSQL)"), ("pg_config", "PostgreSQL")):
            path = self.prefix / "bin" / name
            path.write_text("#!/bin/sh\nprintf '%s\\n' '" + label + " " + version + "'\n")
            path.chmod(0o755)

    def test_pin_update_preserves_other_entries_owner_mode_and_idempotent_inode(self):
        stale = self.pins.read_bytes().replace(self.selected["commit"].encode(), b"0" * 40)
        self.pins.write_bytes(stale)
        before = self.pins.stat()
        self.assertTrue(owner.select_pin(self.pins, self.selected))
        after = self.pins.stat()
        self.assertEqual((before.st_uid, before.st_gid, stat.S_IMODE(before.st_mode)),
                         (after.st_uid, after.st_gid, stat.S_IMODE(after.st_mode)))
        self.assertEqual(stale.splitlines(keepends=True)[:2],
                         self.pins.read_bytes().splitlines(keepends=True)[:2])
        owner.verify_pin(self.pins, self.selected)
        self.assertFalse(owner.select_pin(self.pins, self.selected))
        self.assertEqual(after.st_ino, self.pins.stat().st_ino)

    def test_duplicate_alias_or_malformed_pin_is_not_rewritten(self):
        initial = self.pins.read_bytes()
        for extra in (b"postgresql\tx\t" + b"2" * 40 + b"\n", b"external/bad\tmissing-field\n"):
            with self.subTest(extra=extra):
                self.pins.write_bytes(initial + extra)
                before = self.pins.read_bytes()
                with self.assertRaises(ValueError):
                    owner.select_pin(self.pins, self.selected)
                self.assertEqual(before, self.pins.read_bytes())

    def test_exact_git_source_accepts_then_rejects_pin_head_and_dirty_source(self):
        self.assertTrue(owner.verify_source(self.external, self.selected)["tracked_source_clean"])
        self.pins.write_bytes(self.pins.read_bytes().replace(
            self.selected["commit"].encode(), b"0" * 40))
        with self.assertRaisesRegex(ValueError, "PINS"):
            owner.verify_source(self.external, self.selected)
        owner.select_pin(self.pins, self.selected)
        (self.source / "configure.ac").write_text("tampered\n")
        with self.assertRaisesRegex(ValueError, "local changes"):
            owner.verify_source(self.external, self.selected)
        self.git("add", "configure.ac")
        with self.assertRaisesRegex(ValueError, "local changes"):
            owner.verify_source(self.external, self.selected)
        self.git("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid",
                 "commit", "--quiet", "-m", "different source")
        with self.assertRaisesRegex(ValueError, "HEAD"):
            owner.verify_source(self.external, self.selected)

    def test_source_version_and_license_cannot_disagree_with_selected_commit(self):
        selected = copy.deepcopy(self.selected)
        selected["version"] = "18.3"
        with self.assertRaisesRegex(ValueError, "configure.ac"):
            owner.verify_source(self.external, selected)
        selected = copy.deepcopy(self.selected)
        selected["license_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "license digest"):
            owner.verify_source(self.external, selected)

    def test_both_installed_tools_must_match_without_contacting_server(self):
        result = owner.verify_installed(self.prefix, self.selected)
        self.assertFalse(result["running_server_checked"])
        for name in ("postgres", "pg_config"):
            with self.subTest(name=name):
                self.tools("18.6")
                path = self.prefix / "bin" / name
                path.write_text(path.read_text().replace("18.6", "18.3"))
                with self.assertRaisesRegex(ValueError, name):
                    owner.verify_installed(self.prefix, self.selected)

    def test_archive_checksum_rejects_same_size_mutation_and_truncation(self):
        archive = self.root / "release.tar.bz2"
        original = b"checksum fixture archive bytes"
        archive.write_bytes(original)
        selected = copy.deepcopy(self.selected)
        selected["archive"].update(size_bytes=len(original), sha256=hashlib.sha256(original).hexdigest())
        self.assertEqual(selected["archive"]["sha256"],
                         owner.verify_archive(archive, selected)["release_archive_sha256"])
        for content in (bytes([original[0] ^ 1]) + original[1:], original[:-1]):
            archive.write_bytes(content)
            with self.assertRaisesRegex(ValueError, "checksum or size"):
                owner.verify_archive(archive, selected)

    def test_real_build_owner_rebuilds_stale_tools_with_missing_or_matching_stamp(self):
        project = self.root / "project"
        (project / "scripts").mkdir(parents=True)
        (project / "deploy").mkdir()
        (project / "external").mkdir()
        for name in ("build-system-deps.sh", "postgresql-release.py", "provision-cmake.py"):
            shutil.copy2(ROOT / "scripts" / name, project / "scripts" / name)
        (project / "deploy/postgresql-release.json").write_text(json.dumps(self.selected))
        # The merged dependency owner selects CMake before rebuilding PostgreSQL.
        # Use its real local receipt verifier with the existing compiler process double.
        cmake_release = json.loads((ROOT / "deploy/cmake-release.json").read_text())
        (project / "deploy/cmake-release.json").write_text(json.dumps(cmake_release))
        (project / "external/CMakeLists.txt").write_text("# fixture USES_TERMINAL_BUILD\n")
        prefix = self.prefix.parent
        for name in ("proj", "geos", "gdal", "tree-sitter"):
            (prefix / name / "lib").mkdir(parents=True)
        (prefix / "tree-sitter/lib/libtree-sitter.a").touch()
        (self.prefix / "lib").mkdir()
        (self.prefix / "lib/postgis-3.so").touch()
        doubles = self.root / "tools"
        doubles.mkdir()
        generation = prefix / "tools/cmake" / cmake_release["version"]
        (generation / "bin").mkdir(parents=True)
        cmake = generation / "bin/cmake"
        cmake.write_text(
            "#!" + sys.executable + "\n"
            "import os, sys\nfrom pathlib import Path\n"
            "if sys.argv[1:] == ['--version']:\n"
            " print('cmake version " + cmake_release["version"] + "'); raise SystemExit(0)\n"
            "build=Path(os.environ['LAPLACE_DEPS_BUILD'])\n"
            "assert not (build/'postgresql-build/config.status').exists()\n"
            "assert not (build/'postgis-build/config.status').exists()\n"
            "log=Path(os.environ['PG_FIXTURE_LOG'])\n"
            "with log.open('a') as stream: stream.write('cmake reached\\n')\n"
            "prefix=Path(os.environ['LAPLACE_DEPS_PREFIX'])/'pgsql-18/bin'\n"
            "for name,label in [('postgres','postgres (PostgreSQL)'),('pg_config','PostgreSQL')]:\n"
            " p=prefix/name\n"
            " p.write_text(\"#!/bin/sh\\nprintf '%s\\\\n' '\"+label+\" 18.6'\\n\")\n"
            " p.chmod(0o755)\n")
        cmake.chmod(0o755)
        for tool in ("ctest", "cpack"):
            executable = generation / "bin" / tool
            executable.write_text("#!/bin/sh\nprintf '%s\\n' '" + tool + " version " + cmake_release["version"] + "'\n")
            executable.chmod(0o755)
        cmake_spec = importlib.util.spec_from_file_location("cmake_fixture_owner", project / "scripts/provision-cmake.py")
        cmake_owner = importlib.util.module_from_spec(cmake_spec)
        cmake_spec.loader.exec_module(cmake_owner)
        (generation / cmake_owner.RECEIPT).write_text(json.dumps({
            "schema": "laplace.cmake-install/v1", "release": cmake_release,
            "files": cmake_owner.inventory(generation)}))
        for initial_stamp in ("missing", "matching"):
            with self.subTest(initial_stamp=initial_stamp):
                build = self.root / ("build-" + initial_stamp)
                log = self.root / ("cmake-" + initial_stamp + ".log")
                env = dict(os.environ, LAPLACE_EXTERNAL=str(self.external),
                           LAPLACE_DEPS_BUILD=str(build), LAPLACE_DEPS_PREFIX=str(prefix),
                           LAPLACE_DEPS_USER="postgresql-fixture-nonexistent-user",
                           LAPLACE_DEPS_GENERATOR="Unix Makefiles", LAPLACE_FORCE_DEPS="0",
                           PG_FIXTURE_LOG=str(log), PATH=str(doubles) + os.pathsep + os.environ["PATH"])
                command = ["bash", str(project / "scripts/build-system-deps.sh")]
                if initial_stamp == "matching":
                    self.tools("18.6")
                    result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=30)
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertFalse(log.exists(), "current tools should adopt the initial stamp")
                self.tools("18.3")
                for name in ("postgresql-build", "postgis-build"):
                    directory = build / name
                    directory.mkdir(parents=True)
                    (directory / "config.status").write_text("stale autoconf state")
                result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=30)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual(2, len(log.read_text().splitlines()))
                owner.verify_installed(self.prefix, self.selected)
                self.assertTrue((build / ".laplace-deps.fingerprint").is_file())


    def prepare_fixture(self):
        project = self.root / "prepare-project"
        (project / "scripts").mkdir(parents=True)
        (project / "deploy").mkdir()
        shutil.copy2(ROOT / "scripts/postgresql-release.py",
                     project / "scripts/postgresql-release.py")
        (project / "deploy/postgresql-release.json").write_text(json.dumps(self.selected))
        log = self.root / "bootstrap-calls.jsonl"
        # Only the privileged/bootstrap boundary is doubled. Source verification,
        # pin selection, Git checkout, the deadline executable and caller run for real.
        (project / "scripts/bootstrap-laplace-runner.sh").write_text(
            "#!/usr/bin/env bash\nset -euo pipefail\n"
            "[[ $# == 1 && $1 == prefix ]]\n"
            + "test \"$LAPLACE_EXTERNAL\" = " + shlex.quote(str(self.external)) + "\n"
            + "printf '%s\\n' prefix >> " + shlex.quote(str(log)) + "\n"
            + "case \"$" + "{PG_PREPARE_FIXTURE_MODE:-update}\" in\n"
            + " fail) echo 'fixture bootstrap refused' >&2; exit 7 ;;\n"
            + " unchanged) exit 0 ;;\n"
            + "esac\n"
            + "python3 " + shlex.quote(str(project / "scripts/postgresql-release.py"))
            + " select-pin --external \"$LAPLACE_EXTERNAL\"\n"
            + "git -C \"$LAPLACE_EXTERNAL/postgresql\" checkout --quiet --detach "
            + self.selected["commit"] + "\n")
        doubles = self.root / "prepare-tools"
        doubles.mkdir()
        sudo = doubles / "sudo"
        sudo.write_text(
            "#!" + sys.executable + "\n"
            "import os,sys\n"
            "assert sys.argv[1:3] == ['-n', '--'], sys.argv\n"
            "assert sys.argv[3:7] == ['timeout', '--signal=TERM', '--kill-after=10s', '600s'], sys.argv\n"
            "os.execvp(sys.argv[3], sys.argv[3:])\n")
        sudo.chmod(0o755)
        environment = dict(os.environ, PATH=str(doubles) + os.pathsep + os.environ["PATH"],
                           LAPLACE_EXTERNAL=str(self.external), PYTHONDONTWRITEBYTECODE="1")
        command = [sys.executable, "-B", str(project / "scripts/postgresql-release.py")]
        return project, log, environment, command

    def test_prepare_current_source_does_not_invoke_privileged_owner(self):
        _, log, env, command = self.prepare_fixture()
        env["PG_PREPARE_FIXTURE_MODE"] = "fail"
        before = (self.pins.read_bytes(), self.pins.stat().st_ino, self.git("rev-parse", "HEAD"))
        result = subprocess.run(command + ["prepare-source", "--external", str(self.external)],
                                env=env, capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("already-current", json.loads(result.stdout)["source_preparation"])
        self.assertFalse(log.exists())
        self.assertEqual(before, (self.pins.read_bytes(), self.pins.stat().st_ino,
                                  self.git("rev-parse", "HEAD")))

    def test_prepare_converges_stale_pin_and_head_then_becomes_read_only(self):
        _, log, env, command = self.prepare_fixture()
        (self.source / "configure.ac").write_text(
            "AC_INIT([PostgreSQL], [18.3], [pgsql-bugs@lists.postgresql.org])\n")
        self.git("add", "configure.ac")
        self.git("-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid",
                 "commit", "--quiet", "-m", "old generation")
        self.pins.write_bytes(self.pins.read_bytes().replace(
            self.selected["commit"].encode(), self.git("rev-parse", "HEAD").strip().encode()))
        stale = (self.pins.read_bytes(), self.git("rev-parse", "HEAD"))
        check = subprocess.run(command + ["source", "--external", str(self.external)],
                               env=env, capture_output=True, text=True, timeout=15)
        self.assertNotEqual(0, check.returncode)
        self.assertFalse(log.exists())
        self.assertEqual(stale, (self.pins.read_bytes(), self.git("rev-parse", "HEAD")))
        result = subprocess.run(command + ["prepare-source", "--external", str(self.external)],
                                env=env, capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        proof = json.loads(result.stdout.splitlines()[-1])
        self.assertEqual("bootstrap-prefix", proof["source_preparation"])
        self.assertEqual(self.selected["commit"], proof["source_commit"])
        self.assertTrue(proof["tracked_source_clean"])
        self.assertEqual(["prefix"], log.read_text().splitlines())
        self.assertEqual(stale[0].splitlines()[:2], self.pins.read_bytes().splitlines()[:2])
        owner.verify_source(self.external, self.selected)
        repeat = subprocess.run(command + ["prepare-source", "--external", str(self.external)],
                                env=env, capture_output=True, text=True, timeout=15)
        self.assertEqual(0, repeat.returncode, repeat.stdout + repeat.stderr)
        self.assertEqual("already-current", json.loads(repeat.stdout)["source_preparation"])
        self.assertEqual(["prefix"], log.read_text().splitlines())

    def test_prepare_refuses_failed_or_ineffective_bootstrap_without_retry(self):
        _, log, env, command = self.prepare_fixture()
        self.pins.write_bytes(self.pins.read_bytes().replace(
            self.selected["commit"].encode(), b"0" * 40))
        stale = (self.pins.read_bytes(), self.git("rev-parse", "HEAD"))
        for mode in ("fail", "unchanged"):
            with self.subTest(mode=mode):
                log.unlink(missing_ok=True)
                env["PG_PREPARE_FIXTURE_MODE"] = mode
                result = subprocess.run(command + ["prepare-source", "--external", str(self.external)],
                                        env=env, capture_output=True, text=True, timeout=15)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(["prefix"], log.read_text().splitlines())
                self.assertEqual(stale, (self.pins.read_bytes(), self.git("rev-parse", "HEAD")))
                self.assertIn("PostgreSQL selection failed", result.stderr)
                if mode == "fail":
                    self.assertIn("fixture bootstrap refused", result.stderr)
                else:
                    self.assertIn("PINS.tsv", result.stderr)

    def test_prepare_nonroot_invokes_bounded_noninteractive_privileged_owner(self):
        project, log, env, _ = self.prepare_fixture()
        self.pins.write_bytes(self.pins.read_bytes().replace(
            self.selected["commit"].encode(), b"0" * 40))
        with mock.patch.object(owner, "ROOT", project), \
                mock.patch.object(owner.os, "geteuid", return_value=12345), \
                mock.patch.dict(os.environ, env, clear=True):
            result = owner.prepare_source(self.external, self.selected)
        self.assertEqual("bootstrap-prefix", result["source_preparation"])
        self.assertEqual(["prefix"], log.read_text().splitlines())
        owner.verify_source(self.external, self.selected)

    def test_changed_shell_owners_parse(self):
        for name in ("bootstrap-laplace-runner.sh", "build-system-deps.sh", "ci-deps.sh"):
            subprocess.run(["bash", "-n", str(ROOT / "scripts" / name)], check=True, timeout=10)


if __name__ == "__main__":
    unittest.main(verbosity=2)
