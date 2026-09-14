#!/usr/bin/env python3
import hashlib
import importlib.util
import pathlib
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("check-installed-extension-current.py")
spec = importlib.util.spec_from_file_location("installed_extension_current", SCRIPT)
checker = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(checker)


class InstalledExtensionParityTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
