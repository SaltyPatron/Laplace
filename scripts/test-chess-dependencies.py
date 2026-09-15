#!/usr/bin/env python3
"""Dependency report must fail missing tools and distinguish partial data coverage."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("chess_dependencies", Path(__file__).with_name("check-chess-dependencies.py"))
doctor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(doctor)


class DependencyReportTests(unittest.TestCase):
    def test_failed_executable_is_required_failure(self):
        report = []
        def absent():
            raise FileNotFoundError("missing executable")
        doctor.check(report, "stockfish", absent)
        self.assertEqual("failed", report[0]["status"])
        self.assertTrue(report[0]["required"])

    def test_missing_companion_and_missing_opening_files_are_visible(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tables = root / "Games/Chess/syzygy/3-4-5"
            tables.mkdir(parents=True)
            (tables / "KQvK.rtbw").write_bytes(b"fixture")
            report = doctor.data_inventory({}, root)
            self.assertEqual("incomplete", report[0]["status"])
            self.assertEqual(["KQvK"], report[0]["detail"]["missing_dtz"])
            (tables / "KQvK.rtbz").write_bytes(b"fixture")
            report = doctor.data_inventory({}, root)
            self.assertEqual("present", report[0]["status"])
            self.assertIn("checksums not verified", report[0]["detail"]["verification"])
            self.assertEqual("incomplete", report[1]["status"])

    def test_native_openings_layout_and_explicit_override(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            openings = root / "Games/Chess/lichess-openings"
            openings.mkdir(parents=True)
            for letter in "abcde":
                (openings / (letter + ".tsv")).write_text("eco\tname\tpgn\n")
            self.assertEqual("present", doctor.data_inventory({}, root)[1]["status"])
            report = doctor.data_inventory({"LAPLACE_CHESS_OPENINGS": str(root / "missing")}, root)
            self.assertEqual("incomplete", report[1]["status"])

    def test_configuration_never_collects_credentials(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "app").mkdir()
            (root / "app/laplace-api.env").write_text("LICHESS_API=secret\nLAPLACE_STOCKFISH=/old\n")
            with patch.dict(os.environ, {"LAPLACE_STOCKFISH": "/new", "LICHESS_TOKEN": "secret"}):
                result = doctor.configuration(root)
            self.assertEqual("/new", result["LAPLACE_STOCKFISH"])
            self.assertNotIn("LICHESS_API", result)
            self.assertNotIn("LICHESS_TOKEN", result)


if __name__ == "__main__":
    unittest.main(verbosity=2)
