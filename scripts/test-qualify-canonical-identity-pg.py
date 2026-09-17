#!/usr/bin/env python3
"""Hosted controls for the isolated real-PG qualification operator.

These tests do not start PostgreSQL or claim database behavior. They exercise
receipt/refusal, canonical-suite completeness and sampled library identity.
"""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location(
    "qualify_canonical_identity_pg", Path(__file__).with_name("qualify-canonical-identity-pg.py"))
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


class InstallationBindingTests(unittest.TestCase):
    def setUp(self):
        self.expected = {
            "source": {"commit": "1" * 40, "tree": "2" * 40,
                       "directory": "/candidate/source", "build_directory": "/candidate/build"},
            "cmake_cache": {"path": "/candidate/build/CMakeCache.txt", "sha256": "3" * 64, "size": 31},
            "installed_libraries": {"laplace_core": {
                "path": "/candidate/install/lib/liblaplace_core.so", "sha256": "4" * 64, "size": 41}},
            "build_libraries": {"laplace_core": {
                "path": "/candidate/build/engine/core/liblaplace_core.so", "sha256": "5" * 64, "size": 51}},
            "managed_assembly": {"path": "/candidate/managed/Laplace.Substrate.Tests.dll",
                                 "sha256": "6" * 64, "size": 61},
        }
        self.report = {"schema": "laplace.canonical-identity-isolated-install/v1", "status": "completed",
                       **copy.deepcopy(self.expected)}

    def test_completed_exact_binding_allows_real_install_rpath_difference(self):
        self.report["build_commands"] = ["retained actual build log"]
        module.validate_install_receipt(self.report, self.expected)
        self.assertNotEqual(self.expected["build_libraries"]["laplace_core"]["sha256"],
                            self.expected["installed_libraries"]["laplace_core"]["sha256"])

    def test_other_source_commit_or_tree_refused(self):
        for field in ("commit", "tree"):
            with self.subTest(field=field):
                changed = copy.deepcopy(self.report)
                changed["source"][field] = "7" * 40
                with self.assertRaisesRegex(RuntimeError, "installation identity differs"):
                    module.validate_install_receipt(changed, self.expected)

    def test_changed_installed_module_refused(self):
        changed = copy.deepcopy(self.report)
        changed["installed_libraries"]["laplace_core"]["sha256"] = "8" * 64
        with self.assertRaisesRegex(RuntimeError, "installed_libraries"):
            module.validate_install_receipt(changed, self.expected)

    def test_unbound_managed_assembly_refused(self):
        del self.report["managed_assembly"]
        with self.assertRaisesRegex(RuntimeError, "managed_assembly"):
            module.validate_install_receipt(self.report, self.expected)

    def test_failed_or_untyped_receipt_refused(self):
        for key, value in (("status", "running"), ("schema", "unrelated/v1")):
            with self.subTest(key=key):
                changed = copy.deepcopy(self.report)
                changed[key] = value
                with self.assertRaises(RuntimeError):
                    module.validate_install_receipt(changed, self.expected)


class CanonicalPlanTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.source, self.build, self.pg = (self.root / name for name in ("source", "build", "pg"))
        self.bindir = self.pg / "bin"
        self.bindir.mkdir(parents=True)
        executable = self.pg / "lib/pgxs/src/test/regress/pg_regress"
        executable.parent.mkdir(parents=True)
        executable.write_text("owned isolated fixture executable")
        tests = []
        for suffix in ("geom", "substrate"):
            source = self.source / "extension" / ("laplace_" + suffix) / "tests"
            source.mkdir(parents=True)
            (source / "CMakeLists.txt").write_text(
                'set(REGRESS_DB "laplace_regress_' + suffix + '")\n'
                "set(REGRESS_TESTS first second)\nlist(APPEND REGRESS_TESTS third)\n")
            output = self.build / "extension" / ("laplace_" + suffix) / "tests/regress_output"
            db = "laplace_unique_" + suffix
            tests.extend([
                {"name": "regress_laplace_" + suffix, "command": [
                    str(executable), "--bindir=" + str(self.bindir), "--inputdir=" + str(source),
                    "--outputdir=" + str(output), "--dbname=" + db,
                    "--user=laplace_admin", "--use-existing", "first", "second", "third"]},
                {"name": "regress_setup_laplace_" + suffix, "command": [
                    "/bin/bash", "-c", f'"{self.bindir}/dropdb" -U laplace_admin --if-exists {db} && '
                    f'"{self.bindir}/createdb" -U laplace_admin -O laplace_admin {db}']},
                {"name": "regress_teardown_laplace_" + suffix, "command": [
                    str(self.bindir / "dropdb"), "-U", "laplace_admin", "--if-exists", db]},
            ])
        for name in module.NATIVE_TESTS:
            path = self.build / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("owned native fixture executable")
            tests.append({"name": name, "command": [str(path)]})
        self.plan = {"tests": tests}

    def validate(self):
        return module.validate_test_plan(self.plan, self.source, self.build, self.bindir,
                                         self.pg, "laplace_unique")

    def test_all_registered_sql_and_fixtures_required(self):
        result = self.validate()
        self.assertEqual(result["substrate"]["cases"], ["first", "second", "third"])
        self.assertEqual(len(self.plan["tests"]), 9)

    def test_missing_canonical_case_refused(self):
        self.plan["tests"][0]["command"].remove("third")
        with self.assertRaisesRegex(RuntimeError, "entire canonical SQL suite"):
            self.validate()

    def test_configure_time_other_database_refused(self):
        command = self.plan["tests"][0]["command"]
        command[command.index("--dbname=laplace_unique_geom")] = "--dbname=laplace_other_geom"
        with self.assertRaisesRegex(RuntimeError, "neither its source default"):
            self.validate()

    def test_external_pg_regress_refused(self):
        path = self.root / "old/pg_regress"
        path.parent.mkdir()
        path.write_text("old provider")
        self.plan["tests"][0]["command"][0] = str(path)
        with self.assertRaisesRegex(RuntimeError, "escapes isolated PG prefix"):
            self.validate()

    def test_explicit_live_host_override_refused(self):
        self.plan["tests"][0]["command"].insert(1, "--host=/var/run/postgresql")
        with self.assertRaisesRegex(RuntimeError, "overrides private cluster routing"):
            self.validate()


class MappingTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.library = self.root / "liblaplace_core.so"
        self.library.write_bytes(b"candidate library generation")
        self.proc = self.root / "proc/321"
        self.proc.mkdir(parents=True)
        self.maps = self.proc / "maps"
        self.write_maps()

    def write_maps(self, *, deleted=False, wrong_inode=False):
        stat = self.library.stat()
        device = f"{os.major(stat.st_dev):x}:{os.minor(stat.st_dev):x}"
        inode = stat.st_ino + (1 if wrong_inode else 0)
        suffix = " (deleted)" if deleted else ""
        self.maps.write_text("".join(
            f"{start:x}-{start + 4096:x} r-xp 00000000 {device} {inode} {self.library}{suffix}\n"
            for start in (4096, 8192, 12288)))

    def remap(self, *args):
        return self.root / "proc" if args == ("/proc",) else Path(*args)

    def test_section_and_poll_cache_still_observes_changed_generation(self):
        cache = {}
        with patch.object(module, "Path", side_effect=self.remap), \
                patch.object(module, "fingerprint", wraps=module.fingerprint) as hashing:
            first = module.mapped_files(321, cache)
            again = module.mapped_files(321, cache)
            self.assertEqual(first, again)
            self.assertEqual(hashing.call_count, 1)
            self.library.write_bytes(b"a genuinely different candidate library generation")
            changed = module.mapped_files(321, cache)
            self.assertEqual(hashing.call_count, 2)
            self.assertNotEqual(changed[str(self.library)]["sha256"], first[str(self.library)]["sha256"])

    def test_deleted_mapping_refused(self):
        self.write_maps(deleted=True)
        with patch.object(module, "Path", side_effect=self.remap), \
                self.assertRaisesRegex(RuntimeError, "deleted mapped library"):
            module.mapped_files(321, {})

    def test_path_inode_not_actual_mapping_refused(self):
        self.write_maps(wrong_inode=True)
        with patch.object(module, "Path", side_effect=self.remap), \
                self.assertRaisesRegex(RuntimeError, "mapped inode no longer matches"):
            module.mapped_files(321, {})


class ManagedResultsTests(unittest.TestCase):
    def write_trx(self, directory, *, skipped=False, omit=None):
        namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
        root = ET.Element("TestRun", xmlns=namespace)
        definitions = ET.SubElement(root, "TestDefinitions")
        results = ET.SubElement(root, "Results")
        total = 0
        for name, expected in module.MANAGED_CLASSES.items():
            if name == omit:
                continue
            for index in range(expected or 1):
                identifier = name + str(index)
                unit = ET.SubElement(definitions, "UnitTest", id=identifier)
                ET.SubElement(unit, "TestMethod", className="Laplace.SubstrateCRUD.Tests." + name)
                ET.SubElement(results, "UnitTestResult", testId=identifier,
                              outcome="NotExecuted" if skipped and total == 0 else "Passed")
                total += 1
        summary = ET.SubElement(root, "ResultSummary")
        ET.SubElement(summary, "Counters", total=str(total), executed=str(total), passed=str(total))
        path = Path(directory) / "result.trx"
        ET.ElementTree(root).write(path)
        return path

    def test_every_requested_class_and_fixed_case_count_required(self):
        with tempfile.TemporaryDirectory() as temp:
            result = module.validate_trx(self.write_trx(temp))
            self.assertEqual(result["total"], 21)
            self.assertEqual(set(result["classes"]), set(module.MANAGED_CLASSES))

    def test_skip_is_failure_even_if_summary_claims_pass(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaisesRegex(RuntimeError, "did not pass"):
                module.validate_trx(self.write_trx(temp, skipped=True))

    def test_missing_requested_class_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaisesRegex(RuntimeError, "was not executed"):
                module.validate_trx(self.write_trx(temp, omit="HighwayMaskRefreshConcurrencyTests"))


class OwnershipTests(unittest.TestCase):
    def test_existing_work_is_refused_before_any_subprocess(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(module.subprocess, "Popen") as process:
            with self.assertRaisesRegex(RuntimeError, "already exists"):
                module.Qualification(SimpleNamespace(work=Path(temp), timeout=300))
            process.assert_not_called()

    def test_changed_postmaster_identity_is_never_signalled(self):
        instance = module.Qualification.__new__(module.Qualification)
        instance.backend = None
        instance.postmaster = Mock(pid=123)
        instance.postmaster.poll.return_value = None
        instance.postmaster_identity = {"pid": 123, "start_ticks": "old"}
        instance.receipt = {}
        instance.write = Mock()
        with patch.object(module, "proc_identity", return_value={"pid": 123, "start_ticks": "different"}):
            with self.assertRaisesRegex(RuntimeError, "refusing stop"):
                instance.stop()
        instance.postmaster.send_signal.assert_not_called()

    def test_only_verified_direct_child_gets_fast_shutdown(self):
        with tempfile.TemporaryDirectory() as temp:
            instance = module.Qualification.__new__(module.Qualification)
            instance.backend = None
            instance.postmaster = Mock(pid=123, returncode=0)
            instance.postmaster.poll.return_value = None
            instance.postmaster_identity = {"pid": 123, "start_ticks": "same"}
            instance.data = Path(temp)
            instance.logs = Path(temp)
            (instance.logs / "postmaster.log").write_text("owned shutdown")
            instance.pglog = Mock()
            instance.receipt = {}
            instance.write = Mock()
            with patch.object(module, "proc_identity", return_value=instance.postmaster_identity), \
                    patch.object(module.os, "killpg") as groups:
                instance.stop()
            instance.postmaster.send_signal.assert_called_once_with(signal.SIGINT)
            groups.assert_not_called()
            self.assertEqual(instance.receipt["cluster_shutdown"]["status"], "stopped")



class ActualCTestSelectionTests(unittest.TestCase):
    def setUp(self):
        executable = shutil.which("ctest")
        self.assertIsNotNone(executable, "these controls require an actual installed CTest executable")
        self.ctest = Path(executable).resolve()
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.build = self.root / "configured"
        self.build.mkdir()
        self.expected = {
            "generated_stage_sink_pg_native_helpers",
            "physicality_descriptor_pg_native_helpers",
            "physicality_readback_pg_native_helpers",
            "regress_laplace_geom", "regress_setup_laplace_geom", "regress_teardown_laplace_geom",
            "regress_laplace_substrate", "regress_setup_laplace_substrate", "regress_teardown_laplace_substrate",
        }
        names = sorted(self.expected | {"unrelated_regress_laplace_geom", "regress_laplace_substrate_extra"})
        lines = [
            "add_test(" + name + " " + json.dumps(sys.executable) +
            ' "-c" "raise SystemExit(97)")' for name in names
        ]
        for suffix in ("geom", "substrate"):
            lines.extend([
                "set_tests_properties(regress_laplace_" + suffix +
                " PROPERTIES FIXTURES_REQUIRED regress_db_" + suffix + ")",
                "set_tests_properties(regress_setup_laplace_" + suffix +
                " PROPERTIES FIXTURES_SETUP regress_db_" + suffix + ")",
                "set_tests_properties(regress_teardown_laplace_" + suffix +
                " PROPERTIES FIXTURES_CLEANUP regress_db_" + suffix + ")",
            ])
        (self.build / "CTestTestfile.cmake").write_text("\n".join(lines) + "\n")
        self.operator = module.Qualification(SimpleNamespace(work=self.root / "proof", timeout=300))
        self.operator.env = os.environ.copy()

    def plan(self, label, expression):
        # Invoke the real production command runner, including --test-dir and
        # combined diagnostic retention, from the separate proof working directory.
        return self.operator.command(label, [
            self.ctest, "--test-dir", self.build, "--show-only=json-v1", "-R", expression], limit=30)

    def test_actual_ctest_selects_all_nine_native_sql_and_fixture_cases(self):
        document = json.loads(self.plan("ctest-compatible-selection", module.CTEST_SELECTION_REGEX))
        names = {test["name"] for test in document["tests"]}
        self.assertEqual(names, self.expected)
        self.assertEqual(len(document["tests"]), 9)
        version = subprocess.run([str(self.ctest), "--version"], check=True, text=True,
                                 capture_output=True, timeout=30).stdout.splitlines()[0]
        print("ACTUAL_CTEST_SELECTION " + json.dumps({
            "version": version, "regex": module.CTEST_SELECTION_REGEX,
            "selected_names": sorted(names), "executed_test_commands": 0}), flush=True)

    def test_exact_old_noncapturing_pattern_reproduces_compile_error(self):
        old = module.CTEST_SELECTION_REGEX.replace("(", "(?:")
        self.assertEqual(old,
            "^(?:regress_laplace_(?:geom|substrate)|generated_stage_sink_pg_native_helpers|"
            "physicality_descriptor_pg_native_helpers|physicality_readback_pg_native_helpers)$")
        output = self.plan("ctest-old-pattern-negative-control", old)
        self.assertIn("RegularExpression::compile()", output)
        self.assertIn("Error in compile", output)
        with self.assertRaises(json.JSONDecodeError):
            json.loads(output)
        print("ACTUAL_CTEST_OLD_PATTERN_REFUSED " + json.dumps({
            "returncode": self.operator.receipt["commands"][-1]["returncode"],
            "regex": old, "strict_json_parse_rejected": True,
            "diagnostic": output.splitlines()[:2]}), flush=True)



class RetainedActualCTestPlanTests(unittest.TestCase):
    """Replay the authenticated host plan after only relocating its physical root."""
    def setUp(self):
        fixture = Path(__file__).with_name("testdata") / "canonical-identity-pg-ctest-plan.json"
        raw = fixture.read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(),
                         "f9c2c2a891f1162fc0044ecfaa19b96acc572afa3b7bbb753c2a82eb6b709f1c")
        self.original = json.loads(raw)
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        prior_root = "/build/laplace/recovery/canonical-identity/35191859349-1"
        def relocate(value):
            if isinstance(value, str):
                return value.replace(prior_root, str(self.root))
            if isinstance(value, list):
                return [relocate(item) for item in value]
            if isinstance(value, dict):
                return {key: relocate(item) for key, item in value.items()}
            return value
        self.plan = relocate(self.original)
        self.source, self.build, self.pg = (self.root / name for name in ("source", "build", "pg"))
        self.bindir = self.pg / "bin"
        self.bindir.mkdir(parents=True)
        for test in self.plan["tests"]:
            executable = Path(test["command"][0])
            if module.beneath(executable, self.root):
                executable.parent.mkdir(parents=True, exist_ok=True)
                executable.write_text("read-only plan fixture; never executed")
        for suffix in ("geom", "substrate"):
            test = next(row for row in self.plan["tests"] if row["name"] == "regress_laplace_" + suffix)
            self.assertIn("--dbname=laplace_regress_" + suffix, test["command"])
            cases = [item for item in test["command"][1:] if not item.startswith("-")]
            self.assertEqual(len(cases), 4 if suffix == "geom" else 52)
            declaration = self.source / "extension" / ("laplace_" + suffix) / "tests/CMakeLists.txt"
            declaration.parent.mkdir(parents=True)
            # The fixture preserves the actual registered case sequence and the
            # exact literal default declared by the authenticated e8 CMake owner.
            declaration.write_text('set(REGRESS_DB "laplace_regress_' + suffix + '")\n' +
                                   "set(REGRESS_TESTS " + " ".join(cases) + ")\n")

    def validate(self):
        return module.validate_test_plan(self.plan, self.source, self.build, self.bindir,
                                         self.pg, "laplace_identity_35191859349_1")

    def test_actual_default_databases_are_valid_with_private_prefixes(self):
        result = self.validate()
        self.assertEqual(result["geom"]["database"], "laplace_regress_geom")
        self.assertEqual(result["substrate"]["database"], "laplace_regress_substrate")
        self.assertEqual(len(self.plan["tests"]), 9)
        print("RETAINED_CTEST_DEFAULT_DATABASES_ACCEPTED " + json.dumps({
            "plan_git_blob": "4872612d4fa6c035588e1d6d0815eff0dfd5c17a",
            "source_commit": "e8e99359fc27c4a78a7d56779bc4f495d3bb17ff",
            "databases": {key: value["database"] for key, value in result.items()},
            "case_counts": {key: len(value["cases"]) for key, value in result.items()},
            "relocation_only": True, "executed_fixture_commands": 0}), flush=True)

    def test_divergent_fixture_cleanup_database_is_refused(self):
        cleanup = next(row for row in self.plan["tests"] if row["name"] == "regress_teardown_laplace_geom")
        cleanup["command"][-1] = "laplace_other_geom"
        with self.assertRaisesRegex(RuntimeError, "unexpected registered fixture cleanup"):
            self.validate()

    def test_source_undeclared_database_is_refused_even_when_all_commands_agree(self):
        for test in self.plan["tests"]:
            test["command"] = [value.replace("laplace_regress_geom", "laplace_foreign_geom")
                               for value in test["command"]]
        with self.assertRaisesRegex(RuntimeError, "neither its source default"):
            self.validate()

    def test_default_database_does_not_allow_live_pg_executable(self):
        test = next(row for row in self.plan["tests"] if row["name"] == "regress_laplace_geom")
        foreign = self.root.parent / (self.root.name + "-foreign-pg-regress")
        foreign.write_text("foreign executable fixture; never executed")
        self.addCleanup(foreign.unlink)
        test["command"][0] = str(foreign)
        with self.assertRaisesRegex(RuntimeError, "escapes isolated PG prefix"):
            self.validate()



class HeldBackendGeometryTests(unittest.TestCase):
    """Exercise the production held-session path; real SQL remains host-qualified."""
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name).resolve()
        self.operator = module.Qualification(SimpleNamespace(work=self.root / "proof", timeout=300))
        self.operator.pgprefix = self.root / "pg"
        self.operator.nativeprefix = self.root / "native"
        self.operator.bindir = self.operator.pgprefix / "bin"
        self.operator.module = "laplace_execution_0123456789abcdef"
        self.operator.versions = {"postgis": "3.6.3", "laplace_geom": "fixture-geom",
                                  "laplace_substrate": "fixture-substrate"}
        self.operator.postmaster = Mock(pid=123)
        self.operator.postmaster_identity = {"pid": 123, "exe": str(self.operator.bindir / "postgres")}
        self.identity = {"pid": 321, "ppid": 123, "exe": str(self.operator.bindir / "postgres")}
        self.operator.installed = {
            name: self.operator.pgprefix / "lib" / (name + ".so")
            for name in ("laplace_geom", "laplace_substrate", self.operator.module)}
        self.operator.installed.update({
            name: self.operator.nativeprefix / "lib" / ("lib" + name + ".so")
            for name in ("laplace_core", "laplace_dynamics")})
        paths = [*self.operator.installed.values(), self.operator.pgprefix / "lib/postgis-3.so"]
        self.mappings = {}
        for path in paths:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("controlled candidate " + path.name)
            self.mappings[str(path)] = module.fingerprint(path)
        self.operator.prepared_files = {
            str(path): self.mappings[str(path)] for path in self.operator.installed.values()}
        self.observed = {"qualification_backend": 321, "extensions": self.operator.versions,
                         "execution_modules": [self.operator.module], "geometry_distance_4d": 1}
        self.operator.sql = Mock(return_value="")
        self.process = Mock()
        self.process.poll.return_value = None
        self.process.wait.return_value = 0

    def start_probe(self, command, **kwargs):
        self.assertEqual(command[0], str(self.operator.bindir / "psql"))
        self.assertIn("--dbname=laplace_identity_probe", command)
        statement = Path(command[-1]).read_text()
        # The real operation and observed value must belong to the held backend's
        # own SELECT, not CREATE EXTENSION's earlier connection or a LOAD shortcut.
        expected = ("'geometry_distance_4d',public.laplace_distance_4d("
                    "public.ST_MakePoint(0.0,0.0,0.0,0.0),"
                    "public.ST_MakePoint(1.0,0.0,0.0,0.0))")
        self.assertIn(expected, statement)
        self.assertLess(statement.index("'qualification_backend'"), statement.index(expected))
        self.assertLess(statement.index(expected), statement.index("SELECT pg_sleep(15)"))
        self.assertNotIn("LOAD ", statement)
        kwargs["stdout"].write((json.dumps(self.observed) + "\n").encode())
        kwargs["stdout"].flush()
        return self.process

    def execute(self):
        with patch.object(module.subprocess, "Popen", side_effect=self.start_probe) as launch, \
                patch.object(module, "proc_identity", return_value=self.identity) as identity, \
                patch.object(module, "mapped_files", return_value=self.mappings) as mappings:
            self.operator.backend_proof()
        launch.assert_called_once()
        identity.assert_called_once_with(321)
        mappings.assert_called_once_with(321)

    def test_real_geom_call_is_in_same_held_backend_and_result_is_retained(self):
        self.execute()
        self.assertEqual(self.operator.receipt["backend_observation"]["sql"]["geometry_distance_4d"], 1)
        self.assertEqual(self.operator.receipt["backend_observation"]["mapped_libraries"], self.mappings)
        self.assertEqual(self.operator.receipt["phases"][-1]["name"],
                         "installed-backend-library-identity-verified")

    def test_wrong_missing_or_boolean_geom_result_is_refused(self):
        for value in (0, None, True):
            with self.subTest(value=value):
                self.observed["geometry_distance_4d"] = value
                with self.assertRaisesRegex(RuntimeError, "geometry unit-distance result differs"):
                    self.execute()
        self.assertFalse(self.operator.receipt["phases"])

    def test_successful_geom_result_does_not_bypass_actual_mapping(self):
        del self.mappings[str(self.operator.installed["laplace_geom"])]
        with self.assertRaisesRegex(RuntimeError, "backend did not map installed laplace_geom"):
            self.execute()
        self.assertFalse(self.operator.receipt["phases"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
