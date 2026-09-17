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
        self.database = {"server_version": "180000", "running_ingests": 0,
                         "extension_functions": "fixture-functions", "database": "fixture",
                         "postmaster_started": "fixture-start", "migrations": ["001.sql"],
                         "extensions": {"laplace_geom": "fixture", "laplace_substrate": "fixture"},
                         "roms": {}}
        self.write(self.root / "build/CMakeCache.txt",
                   "CMAKE_HOME_DIRECTORY:INTERNAL=" + str(self.root) + "\n"
                   "CMAKE_CACHEFILE_DIR:INTERNAL=" + str(self.root / "build") + "\n"
                   "CMAKE_INSTALL_PREFIX:PATH=" + str(self.prefix) + "\n")
        self.write(self.root / "build/cmake_install.cmake", "# explicit install protocol fixture\n")
        self.write(self.root / "db/migrations/001.sql", "SELECT 1;")
        for built, installed in guard.MODULES.items():
            # Build and installed bytes are deliberately different. CMake rewrites ELF
            # runtime paths during installation, so equality must be against the staged
            # installed form, not directly against build/*.so.
            self.write(self.root / "build" / built, "build-form:" + built)
            installed_form = "installed-form:" + installed
            self.write(self.staged / installed, installed_form)
            self.write(self.prefix / installed, installed_form)
        self.execution_name = "laplace_execution_0123456789abcdef"
        self.execution_artifact = f"lib/postgresql/18/{self.execution_name}.so"
        self.execution_manifest = "share/postgresql/18/extension/laplace_execution_module.txt"
        for target in (self.staged, self.prefix):
            self.write(target / self.execution_artifact, "installed execution module")
            self.write(target / self.execution_manifest, self.execution_name + "\n")
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
        self.database["native_mappings"] = [
            self.native_mapping(self.prefix / relative)
            for relative in ("lib/liblaplace_core.so", "lib/postgresql/18/laplace_geom.so",
                             "lib/postgresql/18/laplace_substrate.so")]

    def native_mapping(self, path, *, start=4096):
        details = path.stat()
        return (f"{start:x}-{start + 4096:x} r-xp 00000000 "
                f"{os.major(details.st_dev):x}:{os.minor(details.st_dev):x} "
                f"{details.st_ino} {path.resolve()}")

    def write(self, path, content):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)

    @contextmanager
    def staged_install(self, _root, _prefix):
        yield self.staged

    def snapshot(self, *, purpose="publication"):
        with patch.object(guard, "staged_install", self.staged_install):
            return guard.snapshot(self.root, self.prefix, self.database, purpose=purpose)

    def test_exact_installed_runtime_passes_even_when_build_elf_bytes_differ(self):
        self.assertNotEqual(
            guard.digest(self.root / "build/engine/core/liblaplace_core.so"),
            guard.digest(self.prefix / "lib/liblaplace_core.so"),
        )
        state = self.snapshot()
        self.assertEqual(12, len(state["artifacts"]))
        self.assertEqual(2, state["format"])
        self.assertEqual(guard.build_identity(self.root, self.prefix), state["build"])
        self.assertNotIn("native_fingerprint", state)
        self.assertFalse((self.root / "build/.stamps").exists())


    def test_backend_mappings_bind_installed_generation_without_pid_or_aslr_identity(self):
        before = self.snapshot()
        self.database["native_mappings"] = [
            self.native_mapping(self.prefix / relative, start=131072)
            for relative in ("lib/liblaplace_core.so", "lib/postgresql/18/laplace_geom.so",
                             "lib/postgresql/18/laplace_substrate.so")]
        after = self.snapshot()
        self.assertTrue(guard.compatible(before, after))
        self.assertEqual(3, len(after["database"]["native_mappings"]))
        self.assertIsInstance(self.database["native_mappings"], list)

    def test_deleted_preload_mapping_is_rejected_even_when_installed_bytes_match(self):
        self.database["native_mappings"][0] += " (deleted)"
        with self.assertRaisesRegex(ValueError, "replaced native library"):
            self.snapshot()

    def test_replaced_inode_is_rejected_without_relying_on_deleted_suffix(self):
        path = self.prefix / "lib/liblaplace_core.so"
        replacement = path.with_name("replacement.so")
        replacement.write_bytes(path.read_bytes())
        replacement.replace(path)
        with self.assertRaisesRegex(ValueError, "mapping differs from the installed file"):
            self.snapshot()
        self.database["native_mappings"][0] = self.native_mapping(path)
        self.snapshot()

    def test_unselected_native_generation_is_rejected_even_with_matching_bytes(self):
        other = self.prefix / "lib/postgresql/18/laplace_execution_fedcba9876543210.so"
        other.write_bytes((self.prefix / self.execution_artifact).read_bytes())
        self.database["native_mappings"].append(self.native_mapping(other))
        with self.assertRaisesRegex(ValueError, "unselected native library"):
            self.snapshot()
        self.database["native_mappings"][-1] = self.native_mapping(self.prefix / self.execution_artifact)
        self.assertIn(self.execution_artifact, self.snapshot()["database"]["native_mappings"])

    def test_missing_nonexecuting_or_malformed_backend_mappings_are_rejected(self):
        original = list(self.database["native_mappings"])
        cases = [None, [], original[1:],
                 [line.replace("r-xp", "r--p") for line in original],
                 ["not a mapping", *original[1:]],
                 [original[0].replace(" r-xp ", " invalid "), *original[1:]]]
        for rows in cases:
            with self.subTest(rows=rows):
                self.database["native_mappings"] = rows
                with self.assertRaises(ValueError):
                    self.snapshot()
        self.database["native_mappings"] = original

    def test_backend_mapping_device_is_checked_independently_of_path_and_inode(self):
        fields = self.database["native_mappings"][0].split(None, 5)
        fields[3] = "ffff:ffff"
        self.database["native_mappings"][0] = " ".join(fields)
        with self.assertRaisesRegex(ValueError, "mapping differs from the installed file"):
            self.snapshot()


    def test_explicit_preload_scope_accepts_only_its_required_mappings(self):
        core = "lib/liblaplace_core.so"
        database = {"native_mappings": [self.native_mapping(self.prefix / core)]}
        hashes = {relative: "fixture" for relative in guard.MODULES.values()}
        result = guard.mapped_native_identities(self.prefix, database, hashes, required=(core,))
        self.assertEqual([core], list(result))
        with self.assertRaisesRegex(ValueError, "required native library"):
            guard.mapped_native_identities(self.prefix, database, hashes)

    def test_only_explicit_empty_preload_scope_accepts_empty_mapping_list(self):
        hashes = {relative: "fixture" for relative in guard.MODULES.values()}
        self.assertEqual({}, guard.mapped_native_identities(
            self.prefix, {"native_mappings": []}, hashes, required=()))
        with self.assertRaisesRegex(ValueError, "mappings are missing"):
            guard.mapped_native_identities(self.prefix, {"native_mappings": []}, hashes)
        with self.assertRaisesRegex(ValueError, "mappings are missing"):
            guard.mapped_native_identities(self.prefix, {}, hashes, required=())

    def install_pair_receipt(self):
        files = {}
        for name in ("laplace_chess_position_perfcache.bin", "laplace_chess_transition_perfcache.bin"):
            files[name] = {"sha256": guard.digest(self.prefix / "share/laplace" / name)}
        pair = {"schema": "laplace.chess-floor-pair/v1", "files": files, "export": None}
        built = self.root / "build/engine/core/perfcache/chess-floor-pair.json"
        installed = self.prefix / "share/laplace/chess-floor/current/receipt.json"
        for path in (built, installed):
            self.write(path, json.dumps(pair))
        return built, installed, pair

    def test_pair_receipt_binds_generation_even_when_floor_bytes_are_unchanged(self):
        built, installed, pair = self.install_pair_receipt()
        state = self.snapshot()
        self.assertEqual(guard.digest(installed), state["artifacts"]["chess_floor_pair_receipt"])
        changed = dict(pair, export={"unverified": "different corpus"})
        self.write(installed, json.dumps(changed))
        with self.assertRaisesRegex(ValueError, "sealed build pair"):
            self.snapshot()
        self.write(installed, json.dumps(pair))
        built.unlink()
        with self.assertRaises(FileNotFoundError):
            self.snapshot()

    def test_pair_receipt_cannot_certify_other_bytes_or_missing_generation(self):
        built, installed, pair = self.install_pair_receipt()
        pair["files"]["laplace_chess_transition_perfcache.bin"]["sha256"] = "0" * 64
        for path in (built, installed):
            self.write(path, json.dumps(pair))
        with self.assertRaisesRegex(ValueError, "installed-file bytes"):
            self.snapshot()
        installed.unlink()
        with self.assertRaises(FileNotFoundError):
            self.snapshot()

    def test_build_identity_uses_real_configuration_without_obsolete_stamps(self):
        baseline = self.snapshot()
        for name in ("build-native", "install-native"):
            self.write(self.root / "build/.stamps" / name, "obsolete unrelated value")
        self.assertEqual(baseline, self.snapshot())
        cache = self.root / "build/CMakeCache.txt"
        original = cache.read_text()
        cache.write_text(original + "# observed configuration changed\n")
        changed = self.snapshot()
        self.assertFalse(guard.compatible(baseline, changed))
        cache.write_text(original)
        program = self.root / "build/cmake_install.cmake"
        program.write_text(program.read_text() + "# actual install program changed\n")
        self.assertFalse(guard.compatible(baseline, self.snapshot()))

    def test_build_identity_rejects_missing_wrong_or_ambiguous_configured_directories(self):
        cache = self.root / "build/CMakeCache.txt"
        original = cache.read_text()
        for key in ("CMAKE_HOME_DIRECTORY", "CMAKE_CACHEFILE_DIR", "CMAKE_INSTALL_PREFIX"):
            line = next(line for line in original.splitlines() if line.startswith(key + ":"))
            for value in ("missing", "wrong", "duplicate", "relative"):
                with self.subTest(key=key, defect=value):
                    replacement = {"missing": "", "wrong": line.split("=", 1)[0] + "=" + str(self.prefix),
                                   "duplicate": line + "\n" + line,
                                   "relative": line.split("=", 1)[0] + "=relative"}[value]
                    if value == "wrong" and key == "CMAKE_INSTALL_PREFIX":
                        replacement = line.split("=", 1)[0] + "=" + str(self.root)
                    cache.write_text(original.replace(line, replacement))
                    with self.assertRaisesRegex(ValueError, "configured CMake directory"):
                        self.snapshot()
        cache.write_text(original)
        (self.root / "build/cmake_install.cmake").unlink()
        with self.assertRaisesRegex(ValueError, "install program missing"):
            self.snapshot()

    def test_build_configuration_change_during_materialization_is_detected(self):
        @contextmanager
        def changed_install(_root, _prefix):
            yield self.staged
            cache = self.root / "build/CMakeCache.txt"
            cache.write_text(cache.read_text() + "# changed during installed-form comparison\n")
        with patch.object(guard, "staged_install", changed_install):
            with self.assertRaisesRegex(ValueError, "build changed during"):
                guard.snapshot(self.root, self.prefix, self.database)

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

    def test_execution_module_and_manifest_drift_are_detected(self):
        for artifact in (self.execution_artifact, self.execution_manifest):
            with self.subTest(artifact=artifact):
                path = self.prefix / artifact
                original = path.read_bytes()
                path.write_bytes(b"stale execution artifact")
                with self.assertRaisesRegex(ValueError, "tested installed form"):
                    self.snapshot()
                path.unlink()
                with self.assertRaisesRegex(ValueError, "artifact missing"):
                    self.snapshot()
                path.write_bytes(original)

    def test_staged_execution_manifest_is_required_and_validated(self):
        path = self.staged / self.execution_manifest
        original = path.read_bytes()
        path.unlink()
        with self.assertRaisesRegex(ValueError, "one execution module manifest"):
            self.snapshot()
        path.write_text("unversioned-module")
        with self.assertRaisesRegex(ValueError, "invalid native execution module identity"):
            self.snapshot()
        path.write_bytes(original)

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

    def test_publication_and_recording_retain_journal_progress_without_a_global_gate(self):
        for purpose in ("publication", "recording"):
            with self.subTest(purpose=purpose):
                self.database["running_ingests"] = 3
                before = self.snapshot(purpose=purpose)
                self.assertEqual(3, before["database"]["running_ingests"])
                for count in (0, 1, 4):
                    self.database["running_ingests"] = count
                    after = self.snapshot(purpose=purpose)
                    self.assertEqual(count, after["database"]["running_ingests"])
                    self.assertTrue(guard.compatible(before, after, purpose=purpose))
                    self.assertEqual(3, before["database"]["running_ingests"])
                    self.assertEqual(count, after["database"]["running_ingests"])

    def test_each_scope_preserves_every_other_runtime_field(self):
        paths = [
            ("build", "cacheSha256"), ("artifacts", self.execution_artifact),
            ("database", "database"), ("database", "server_version"),
            ("database", "postmaster_started"), ("database", "extension_functions"),
            ("database", "extensions"), ("database", "migrations"),
            ("database", "roms"), ("database", "native_mappings"),
        ]
        for purpose in ("publication", "recording"):
            before = self.snapshot(purpose=purpose)
            for path in paths:
                with self.subTest(purpose=purpose, path=path):
                    after = json.loads(json.dumps(before))
                    target = after
                    for key in path[:-1]:
                        target = target[key]
                    target[path[-1]] = "changed"
                    self.assertFalse(guard.compatible(before, after, purpose=purpose))
        publication = self.snapshot()
        recording = self.snapshot(purpose="recording")
        with self.assertRaisesRegex(ValueError, "recording snapshots"):
            guard.compatible(publication, recording, purpose="recording")
        self.assertFalse(guard.compatible(recording, recording))

    def test_each_scope_rejects_native_drift_and_invalid_journal_observation(self):
        self.database["running_ingests"] = 2
        path = self.prefix / self.execution_artifact
        original = path.read_bytes()
        for purpose in ("publication", "recording"):
            with self.subTest(purpose=purpose):
                path.write_bytes(b"changed execution module")
                with self.assertRaisesRegex(ValueError, "tested installed form"):
                    self.snapshot(purpose=purpose)
                path.write_bytes(original)
                baseline = self.snapshot(purpose=purpose)
                for invalid in (True, -1, None, "2"):
                    self.database["running_ingests"] = invalid
                    with self.subTest(invalid=invalid):
                        with self.assertRaisesRegex(ValueError, "journal observation"):
                            self.snapshot(purpose=purpose)
                        changed = json.loads(json.dumps(baseline))
                        changed["database"]["running_ingests"] = invalid
                        with self.assertRaisesRegex(ValueError, "journal observation"):
                            guard.compatible(baseline, changed, purpose=purpose)
                self.database["running_ingests"] = 2
        with self.assertRaisesRegex(ValueError, "purpose"):
            self.snapshot(purpose="unknown")

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
