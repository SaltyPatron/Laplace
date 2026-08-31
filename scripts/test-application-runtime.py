#!/usr/bin/env python3
"""Application release guards: disposable files and mocked read-only DB client."""
from contextlib import contextmanager
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("application_guard", ROOT / "scripts/check-application-runtime.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class RuntimeGuardTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="laplace-application-contract-")
        self.addCleanup(temporary.cleanup)
        base = Path(temporary.name)
        self.root = base / "repo"
        self.prefix = base / "install"
        self.staged = base / "staged-install"
        self.fingerprint = "f" * 64
        self.database = {"server_version": "180000", "running_ingests": 0,
                         "extension_functions": "fixture-functions", "database": "fixture",
                         "postmaster_started": "fixture-start", "migrations": ["001.sql"],
                         "extensions": {"laplace_geom": "fixture", "laplace_substrate": "fixture"},
                         "roms": {}}
        for stamp in ("build-native", "install-native"):
            self.write(self.root / "build/.stamps" / stamp, self.fingerprint)
        self.write(self.root / "db/migrations/001.sql", "SELECT 1;")
        for built, installed in guard.MODULES.items():
            # Build and installed bytes are deliberately different. CMake rewrites ELF
            # runtime paths during installation, so equality must be against the staged
            # installed form, not directly against build/*.so.
            self.write(self.root / "build" / built, "build-form:" + built)
            installed_form = "installed-form:" + installed
            self.write(self.staged / installed, installed_form)
            self.write(self.prefix / installed, installed_form)
        for name in ("laplace_geom", "laplace_substrate"):
            for path in (self.root / "build/extension" / name / f"{name}.control",
                         self.prefix / "share/postgresql/18/extension" / f"{name}.control"):
                self.write(path, "default_version = 'fixture'\n")
        for setting, filename in guard.ROMS.items():
            path = self.prefix / "share/laplace" / filename
            self.database["roms"][setting] = str(path)
            self.write(path, filename)
            self.write(self.root / "build/engine/core/perfcache" / filename, filename)
        for filename in ("laplace_chess_transition_perfcache.bin", "laplace_modality_number_perfcache.bin"):
            self.write(self.prefix / "share/laplace" / filename, filename)
            self.write(self.root / "build/engine/core/perfcache" / filename, filename)

    def write(self, path, content):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)

    @contextmanager
    def staged_install(self, _root, _prefix):
        yield self.staged

    def snapshot(self):
        with patch.object(guard, "staged_install", self.staged_install):
            return guard.snapshot(self.root, self.prefix, self.database, self.fingerprint)

    def test_exact_installed_runtime_passes_even_when_build_elf_bytes_differ(self):
        self.assertNotEqual(
            guard.digest(self.root / "build/engine/core/liblaplace_core.so"),
            guard.digest(self.prefix / "lib/liblaplace_core.so"),
        )
        state = self.snapshot()
        self.assertEqual(10, len(state["artifacts"]))
        self.assertEqual(self.fingerprint, state["native_fingerprint"])

    def test_each_stale_or_missing_stamp_fails(self):
        for name in ("build-native", "install-native"):
            path = self.root / "build/.stamps" / name
            for value in ("stale", None):
                with self.subTest(name=name, value=value):
                    if value is None:
                        path.unlink()
                    else:
                        path.write_text(value)
                    with self.assertRaisesRegex(ValueError, name):
                        self.snapshot()
                    path.write_text(self.fingerprint)

    def test_each_native_artifact_drift_is_detected_against_staged_install(self):
        for _built, installed in guard.MODULES.items():
            with self.subTest(artifact=installed):
                path = self.prefix / installed
                original = path.read_bytes()
                path.write_bytes(b"deliberately broken installed binary")
                with self.assertRaisesRegex(ValueError, "tested installed form"):
                    self.snapshot()
                path.write_bytes(original)
                self.snapshot()

    def test_missing_native_artifact_fails(self):
        (self.prefix / "lib/liblaplace_core.so").unlink()
        with self.assertRaisesRegex(ValueError, "artifact missing"):
            self.snapshot()

    def test_staged_install_must_contain_every_declared_native_artifact(self):
        (self.staged / "lib/liblaplace_core.so").unlink()
        with self.assertRaisesRegex(ValueError, "CMake install omitted"):
            self.snapshot()

    def test_destdir_install_materializes_without_targeting_live_prefix(self):
        self.write(self.root / "build/cmake_install.cmake", "# fixture")
        live_marker = self.prefix / "live-marker"
        self.write(live_marker, "unchanged")
        observed = {}

        def fake_run(argv, **kwargs):
            observed["argv"] = argv
            observed["cwd"] = kwargs["cwd"]
            observed["destdir"] = kwargs["env"]["DESTDIR"]
            stage = Path(observed["destdir"]) / self.prefix.resolve().relative_to("/")
            self.write(stage / "lib/liblaplace_core.so", "fixture")
            return subprocess.CompletedProcess(argv, 0, "", "")

        with patch.object(guard.subprocess, "run", side_effect=fake_run):
            with guard.staged_install(self.root, self.prefix) as staged:
                self.assertTrue((staged / "lib/liblaplace_core.so").is_file())
                self.assertNotEqual(self.prefix.resolve(), staged.resolve())
        self.assertEqual(["cmake", "--install", str(self.root.resolve() / "build")], observed["argv"])
        self.assertEqual(self.root.resolve(), observed["cwd"])
        self.assertEqual("unchanged", live_marker.read_text())

    def test_live_or_installed_sql_version_drift_fails(self):
        self.database["extensions"]["laplace_substrate"] = "old"
        with self.assertRaisesRegex(ValueError, "SQL version"):
            self.snapshot()
        self.database["extensions"]["laplace_substrate"] = "fixture"
        self.write(self.prefix / "share/postgresql/18/extension/laplace_geom.control",
                   "default_version = 'old'\n")
        with self.assertRaisesRegex(ValueError, "SQL version"):
            self.snapshot()

    def test_pending_migration_or_empty_journal_fails(self):
        for migrations in ([], None, ["different.sql"]):
            with self.subTest(migrations=migrations):
                self.database["migrations"] = migrations
                with self.assertRaisesRegex(ValueError, "migrations"):
                    self.snapshot()

    def test_running_ingest_fails(self):
        self.database["running_ingests"] = 1
        with self.assertRaisesRegex(ValueError, "ingest"):
            self.snapshot()

    def test_unknown_postgres_or_function_contract_fails(self):
        self.database["server_version"] = "170000"
        with self.assertRaisesRegex(ValueError, "PostgreSQL 18"):
            self.snapshot()
        self.database["server_version"] = "180000"
        self.database["extension_functions"] = None
        with self.assertRaisesRegex(ValueError, "function contract"):
            self.snapshot()

    def test_rom_drift_and_path_escape_fail(self):
        setting, filename = next(iter(guard.ROMS.items()))
        path = Path(self.database["roms"][setting])
        path.write_text("drift")
        with self.assertRaisesRegex(ValueError, "ROM differs"):
            self.snapshot()
        path.write_text(filename)
        self.database["roms"][setting] = "/outside/rom.bin"
        with self.assertRaisesRegex(ValueError, "ROM path"):
            self.snapshot()

    def test_snapshot_observes_native_database_and_postmaster_changes(self):
        before = self.snapshot()
        for field in ("postmaster_started", "extension_functions", "database"):
            with self.subTest(field=field):
                original = self.database[field]
                self.database[field] = "different"
                self.assertNotEqual(before, self.snapshot())
                self.database[field] = original

    def test_db_client_is_peer_only_and_read_only(self):
        env = {"PGHOST": "/tmp/test-socket", "PGPASSWORD": "test-do-not-use", "PGSERVICE": "wrong",
               "PGHOSTADDR": "198.51.100.1", "PGOPTIONS": "wrong", "PGDATABASE": "fixture"}
        result = subprocess.CompletedProcess([], 0, json.dumps(self.database))
        with patch.dict(os.environ, env, clear=True), patch.object(guard.subprocess, "run", return_value=result) as run:
            self.assertEqual(self.database, guard.read_database(Path("/fixture/pg")))
        argv = run.call_args.args[0]
        self.assertIn("-w", argv)
        self.assertIn("laplace_admin", argv)
        self.assertIn("BEGIN READ ONLY", argv[-1])
        self.assertIn("ON_ERROR_STOP=1", argv)
        self.assertEqual({"PGOPTIONS": "-c default_transaction_read_only=on -c statement_timeout=10000"},
                         run.call_args.kwargs["env"])

    def test_tcp_or_multi_host_db_is_rejected_before_process_start(self):
        for host in ("127.0.0.1", "hart-server", "/tmp/socket,remote"):
            with patch.dict(os.environ, {"PGHOST": host}), patch.object(guard.subprocess, "run") as run:
                with self.assertRaisesRegex(ValueError, "local PostgreSQL socket"):
                    guard.read_database(Path("/fixture/pg"))
                run.assert_not_called()

    def test_failed_or_empty_db_read_is_not_treated_as_idle(self):
        for result in (subprocess.CalledProcessError(1, "psql"),
                       subprocess.CompletedProcess([], 0, "")):
            with patch.dict(os.environ, {"PGHOST": "/tmp/socket"}):
                kwargs = {"side_effect": result} if isinstance(result, Exception) else {"return_value": result}
                with patch.object(guard.subprocess, "run", **kwargs):
                    with self.assertRaises((subprocess.SubprocessError, ValueError)):
                        guard.read_database(Path("/fixture/pg"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
