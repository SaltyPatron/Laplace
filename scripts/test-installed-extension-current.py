#!/usr/bin/env python3
import hashlib
import importlib.util
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

SCRIPT = pathlib.Path(__file__).with_name("check-installed-extension-current.py")
spec = importlib.util.spec_from_file_location("installed_extension_current", SCRIPT)
checker = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(checker)


class InstalledExtensionParityTests(unittest.TestCase):
    def setUp(self):
        for name in ("ROOT", "EXT", "SQL"):
            self.addCleanup(setattr, checker, name, getattr(checker, name))

    def test_extension_version_includes_configured_execution_module(self):
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            ext = root / "extension" / "laplace_substrate"
            sql = ext / "sql"
            sql.mkdir(parents=True)

            files = {
                sql / "manifest.install": "a.sql.in\n",
                sql / "manifest.upgrade": "b.sql.in\n",
                sql / "a.sql.in": "SELECT 'install';\n",
                sql / "b.sql.in": "SELECT 'upgrade';\n",
                ext / "laplace_substrate.control.in": "default_version = '@EXT_VERSION@'\n",
                sql / "laplace_substrate.sql.in": "-- install shim\n",
                sql / "laplace_substrate_upgrade.sql.in": "-- upgrade shim\n",
                sql / "sqldefines.h.in": "-- defines\n",
            }
            for path, content in files.items():
                path.write_text(content, encoding="utf-8")

            checker.ROOT = root
            checker.EXT = ext
            checker.SQL = sql
            execution = "laplace_execution_0123456789abcdef"

            ordered = sorted(files, key=str)
            acc = "".join(hashlib.sha256(path.read_bytes()).hexdigest() for path in ordered)
            expected = hashlib.sha256(
                (acc + f"module_pathname=laplace_substrate;execution={execution}").encode()
            ).hexdigest()[:16]
            legacy = hashlib.sha256(
                (acc + "module_pathname=laplace_substrate").encode()
            ).hexdigest()[:16]

            actual = checker.source_version("laplace_substrate", execution)
            self.assertEqual(expected, actual)
            self.assertNotEqual(legacy, actual)

    def test_execution_manifest_identity_is_validated(self):
        self.assertEqual(
            "laplace_execution_0123456789abcdef",
            checker.configured_execution_module("laplace_execution_0123456789abcdef"),
        )
        self.assertIsNone(checker.configured_execution_module("laplace_execution_not-a-hash"))


class ConfiguredGeneratedFragmentParityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-extension-parity-")
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name) / "source"
        self.build = pathlib.Path(self.temp.name) / "build"
        repository = SCRIPT.resolve().parents[1]
        self.ext = self.root / "extension/laplace_substrate"
        self.sql = self.ext / "sql"
        real_ext = repository / "extension/laplace_substrate"
        shutil.copytree(real_ext / "sql", self.sql)
        (self.ext / "cmake").mkdir()
        shutil.copyfile(real_ext / "cmake/SqlManifest.cmake", self.ext / "cmake/SqlManifest.cmake")
        shutil.copyfile(real_ext / "laplace_substrate.control.in", self.ext / "laplace_substrate.control.in")
        for name in ("scripts", "engine/manifest", "engine/core/src/generated",
                     "engine/core/include/laplace/core"):
            (self.root / name).mkdir(parents=True, exist_ok=True)
        self.generator = self.root / "scripts/codegen-attestation-law.py"
        shutil.copyfile(repository / "scripts/codegen-attestation-law.py", self.generator)
        shutil.copytree(repository / "engine/manifest", self.root / "engine/manifest", dirs_exist_ok=True)
        self.fragments = [self.sql / "generated" / name for name in
                          ("relation_family_ids.sql.in", "relation_set_ids.sql.in")]

        # Execute the actual manifest and version producer in CMake. The
        # unrelated native compilation graph is omitted; its configured
        # execution-module identity is supplied as an explicit fixture input.
        source = (real_ext / "CMakeLists.txt").read_text()
        start = source.index('include(${CMAKE_CURRENT_SOURCE_DIR}/cmake/SqlManifest.cmake)')
        end = source.index("\n", source.index("string(SUBSTRING ${_ext_sql_hash}", start))
        (self.ext / "CMakeLists.txt").write_text(
            'set(EXT_NAME laplace_substrate)\n'
            'set(EXT_MODULE_PATHNAME laplace_substrate)\n'
            'set(EXT_EXECUTION_MODULE "${FIXTURE_EXECUTION_MODULE}")\n'
            + source[start:end]
            + '\nfile(WRITE "${CMAKE_BINARY_DIR}/extension-version.txt" "${EXT_VERSION}\\n")\n')
        (self.root / "CMakeLists.txt").write_text(
            "cmake_minimum_required(VERSION 3.20)\n"
            "project(extension_identity_fixture NONE)\n"
            "add_subdirectory(extension/laplace_substrate)\n")
        self.execution = "laplace_execution_0123456789abcdef"
        patch = mock.patch.multiple(checker, ROOT=self.root, EXT=self.ext, SQL=self.sql)
        patch.start()
        self.addCleanup(patch.stop)

    def configure_and_compare(self):
        result = subprocess.run(
            ["cmake", "-G", "Ninja", "-S", str(self.root), "-B", str(self.build),
             "-DFIXTURE_EXECUTION_MODULE=" + self.execution],
            text=True, capture_output=True, timeout=45)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        expected = (self.build / "extension-version.txt").read_text().strip()
        self.assertRegex(expected, r"^[0-9a-f]{16}$")
        self.assertEqual(checker.source_version("laplace_substrate", self.execution), expected)
        return expected

    def generate(self):
        result = subprocess.run([sys.executable, str(self.generator)],
                                text=True, capture_output=True, timeout=45)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_generator_reproduces_the_shipped_fragments(self):
        before = self.configure_and_compare()
        self.generate()
        self.assertTrue(all(path.stat().st_size > 0 for path in self.fragments))
        self.assertEqual(self.configure_and_compare(), before)

    def test_real_cmake_and_checker_bind_every_shipped_input(self):
        previous = self.configure_and_compare()
        ordinary = next(path for path in checker.manifest_files(self.sql / "manifest.install")
                        if path not in self.fragments and path.is_file())
        for path in (self.fragments[1], ordinary):
            with self.subTest(input=str(path.relative_to(self.root))):
                with path.open("a") as stream:
                    stream.write("\n-- identity regression\n")
                current = self.configure_and_compare()
                self.assertNotEqual(current, previous)
                previous = current
        self.execution = "laplace_execution_fedcba9876543210"
        self.assertNotEqual(self.configure_and_compare(), previous)

    def test_missing_generated_fragment_fails_both_owners(self):
        self.fragments[0].unlink()
        self.assertIsNone(checker.source_version("laplace_substrate", self.execution))
        result = subprocess.run(
            ["cmake", "-G", "Ninja", "-S", str(self.root), "-B", str(self.build),
             "-DFIXTURE_EXECUTION_MODULE=" + self.execution],
            text=True, capture_output=True, timeout=45)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("relation_family_ids.sql.in", result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
