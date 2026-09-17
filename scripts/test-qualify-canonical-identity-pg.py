#!/usr/bin/env python3
"""Hosted controls for the isolated real-PG qualification operator.

These tests do not start PostgreSQL or claim database behavior. They exercise
receipt/refusal, canonical-suite completeness and sampled library identity.
"""
from __future__ import annotations

import copy
import importlib.util
import json
import os
from pathlib import Path
import signal
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
        with self.assertRaisesRegex(RuntimeError, "missing exact argument"):
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
