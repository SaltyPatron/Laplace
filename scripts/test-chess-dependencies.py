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

    def test_multiple_syzygy_roots_require_every_directory_and_nonempty_pairs(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            wdl_root, dtz_root = root / "wdl", root / "dtz"
            wdl, dtz = wdl_root / "nested/KQvK.rtbw", dtz_root / "other/KQvK.rtbz"
            wdl.parent.mkdir(parents=True)
            dtz.parent.mkdir(parents=True)
            selected = os.pathsep.join(map(str, (wdl_root, dtz_root)))
            def inventory(path=selected):
                return doctor.data_inventory({"LAPLACE_SYZYGY": path}, root)[0]
            wdl.write_bytes(b"fixture")
            self.assertEqual("incomplete", inventory()["status"])
            self.assertEqual(["KQvK"], inventory()["detail"]["missing_dtz"])
            dtz.write_bytes(b"fixture")
            self.assertEqual("present", inventory()["status"])
            self.assertEqual(os.pathsep.join(sorted((str(wdl.parent), str(dtz.parent)))),
                             inventory()["detail"]["native_path"])
            missing = root / "missing"
            report = inventory(selected + os.pathsep + str(missing))
            self.assertEqual("incomplete", report["status"])
            self.assertEqual([str(missing)], report["detail"]["missing_roots"])
            dtz.write_bytes(b"")
            self.assertEqual("incomplete", inventory()["status"])
            self.assertEqual([str(dtz)], inventory()["detail"]["empty_files"])

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

    def test_installed_service_selection_matches_cli_and_explicit_environment(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "app").mkdir()
            (root / "secrets").mkdir()
            (root / "app/laplace-api.env").write_text(
                'LAPLACE_STOCKFISH_SOURCE=/old\nLAPLACE_STOCKFISH_SOURCE="/selected/source"\n'
                'LAPLACE_STOCKFISH=/installed/binary\nLAPLACE_STOCKFISH_EVAL_THREADS=4\n')
            (root / "secrets/chess-lab.env").write_text(
                'LAPLACE_STOCKFISH_SOURCE=/stale-secret\nLAPLACE_STOCKFISH_EVAL_HASH_MB=64\n')
            with patch.dict(os.environ, {}, clear=True):
                observed = doctor.configuration(root)
                self.assertEqual("/selected/source", observed["LAPLACE_STOCKFISH_SOURCE"])
                self.assertEqual("/installed/binary", observed["LAPLACE_STOCKFISH"])
                self.assertEqual("4", observed["LAPLACE_STOCKFISH_EVAL_THREADS"])
                self.assertEqual("64", observed["LAPLACE_STOCKFISH_EVAL_HASH_MB"])
                os.environ["LAPLACE_STOCKFISH_SOURCE"] = "/explicit/source"
                os.environ["LAPLACE_STOCKFISH_EVAL_THREADS"] = "2"
                observed = doctor.configuration(root)
                self.assertEqual("/explicit/source", observed["LAPLACE_STOCKFISH_SOURCE"])
                self.assertNotIn("LAPLACE_STOCKFISH", observed)
                self.assertEqual("2", observed["LAPLACE_STOCKFISH_EVAL_THREADS"])
                os.environ["LAPLACE_STOCKFISH"] = "/explicit/binary"
                self.assertEqual("/explicit/binary", doctor.configuration(root)["LAPLACE_STOCKFISH"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
