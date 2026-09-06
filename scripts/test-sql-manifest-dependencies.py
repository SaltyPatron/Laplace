#!/usr/bin/env python3
"""Fresh-install binding order and existing-writer upgrade regressions."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "checker", ROOT / "scripts/check-sql-manifest-dependencies.py")
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class ManifestTests(unittest.TestCase):
    def test_atomic_consumer_requires_prior_function_binding(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "producer.sql.in").write_text(
                "CREATE FUNCTION consensus.mask(bytea) RETURNS bytea[] "
                "AS 'library', 'symbol' LANGUAGE C;")
            (root / "consumer.sql.in").write_text(
                "CREATE FUNCTION consensus.neighbors(bytea) RETURNS bytea[] "
                "LANGUAGE sql BEGIN ATOMIC SELECT consensus.mask($1); END;")
            manifest = root / "manifest.install"
            with patch.object(checker, "SQL_ROOT", root):
                manifest.write_text("consumer.sql.in\nproducer.sql.in\n")
                errors = checker.validate_atomic_dependencies(manifest)
                self.assertEqual(1, len(errors))
                self.assertIn("consensus.mask", errors[0])
                manifest.write_text("producer.sql.in\nconsumer.sql.in\n")
                self.assertEqual([], checker.validate_atomic_dependencies(manifest))

    def test_install_and_upgrade_bind_all_atomic_dependencies_in_order(self):
        for manifest in checker.MANIFESTS:
            self.assertEqual([], checker.validate_atomic_dependencies(manifest))

    def test_existing_attestation_tables_receive_additive_writer_columns(self):
        upgrade = checker.SQL_ROOT / "manifest.upgrade"
        modules = checker.manifest_modules(upgrade)
        owner = "schema/tables/attestations.sql.in"
        self.assertIn(owner, modules)
        self.assertLess(modules.index(owner), modules.index("functions/fold/attestation_merge.sql.in"))
        self.assertIn("ADD COLUMN IF NOT EXISTS fold_replayable", checker.available_module_text(owner))


if __name__ == "__main__":
    unittest.main()
