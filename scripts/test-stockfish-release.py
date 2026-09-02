#!/usr/bin/env python3
"""Isolated installer contracts; no network, host packages, or live engines touched."""
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("stockfish_install", ROOT / "scripts/install-stockfish.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)


class StockfishReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="stockfish-contract-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.prefix = self.base / "install"
        self.archive = self.base / "engine.tar"
        self.previous_root = installer.ROOT
        installer.ROOT = self.base
        self.addCleanup(setattr, installer, "ROOT", self.previous_root)
        (self.base / "deploy/linux").mkdir(parents=True)
        self.make_archive()

    def make_archive(self, name="stockfish/engine", link=False, broken=False):
        program = b"#!/bin/sh\nprintf 'id name Stockfish 18\\nuciok\\n'\n"
        if broken:
            program = b"#!/bin/sh\nexit 1\n"
        with tarfile.open(self.archive, "w") as archive:
            entry = tarfile.TarInfo(name)
            entry.mode = 0o755
            if link:
                entry.type = tarfile.SYMTYPE
                entry.linkname = "/bin/sh"
                archive.addfile(entry)
            else:
                entry.size = len(program)
                archive.addfile(entry, io.BytesIO(program))
        self.lock = {"version": "18", "test": {
            "url": "https://example.invalid/not-contacted", "sha256": installer.digest(self.archive),
            "binary": "stockfish/engine"}}
        self.write_lock()

    def write_lock(self):
        (self.base / "deploy/linux/stockfish-release.json").write_text(json.dumps(self.lock))

    def install(self):
        installer.install(self.prefix, "test", self.archive)

    def test_repeated_install_preserves_release_and_launch_link(self):
        self.install()
        link = self.prefix / "bin/stockfish"
        target = link.resolve()
        inode = link.lstat().st_ino
        self.install()
        self.assertEqual(target, link.resolve())
        self.assertEqual(inode, link.lstat().st_ino)
        self.assertTrue((target.parent.parent / "receipt.json").is_file())

    def test_checksum_failure_cannot_switch_previous_engine(self):
        self.install()
        link = self.prefix / "bin/stockfish"
        previous = link.resolve()
        self.lock["test"]["sha256"] = "0" * 64
        self.write_lock()
        with self.assertRaisesRegex(ValueError, "drift|checksum"):
            self.install()
        self.assertEqual(previous, link.resolve())

    def test_path_traversal_rejected_before_extraction(self):
        self.make_archive("stockfish/../../outside")
        with self.assertRaisesRegex(ValueError, "unsafe"):
            self.install()
        self.assertFalse((self.base / "outside").exists())

    def test_symlink_in_archive_rejected(self):
        self.make_archive(link=True)
        with self.assertRaisesRegex(ValueError, "unsupported"):
            self.install()

    def test_failed_handshake_does_not_create_launch_link(self):
        self.make_archive(broken=True)
        with self.assertRaises(installer.subprocess.CalledProcessError):
            self.install()
        self.assertFalse((self.prefix / "bin/stockfish").exists())

    def test_unmanaged_binary_is_never_overwritten(self):
        self.prefix.joinpath("bin").mkdir(parents=True)
        link = self.prefix / "bin/stockfish"
        link.write_text("operator-owned binary")
        with self.assertRaisesRegex(ValueError, "unmanaged"):
            self.install()
        self.assertEqual("operator-owned binary", link.read_text())

    def test_release_drift_fails_without_overwrite(self):
        self.install()
        link = self.prefix / "bin/stockfish"
        link.resolve().write_text("changed by operator")
        with self.assertRaisesRegex(ValueError, "drift"):
            self.install()
        self.assertEqual("changed by operator", link.read_text())

    def test_verified_reuse_materializes_exact_release_without_network(self):
        source_prefix = self.base / "source"
        destination_prefix = self.base / "destination"
        installer.install(source_prefix, "test", self.archive)
        source_binary = (source_prefix / "bin/stockfish").resolve()

        with patch.object(installer, "urlopen", side_effect=AssertionError("network must not be used")):
            installer.install(destination_prefix, "test", reuse_prefix=source_prefix)

        destination_binary = (destination_prefix / "bin/stockfish").resolve()
        self.assertNotEqual(source_binary, destination_binary)
        self.assertEqual(installer.digest(source_binary), installer.digest(destination_binary))
        receipt = json.loads((destination_binary.parent.parent / "receipt.json").read_text())
        self.assertEqual(self.lock["test"]["sha256"], receipt["archive_sha256"])
        self.assertEqual("18", receipt["version"])
        self.assertEqual(str(source_prefix), receipt["reused_from"])

    def test_drifted_reuse_source_fails_closed_without_network_fallback(self):
        source_prefix = self.base / "source"
        destination_prefix = self.base / "destination"
        installer.install(source_prefix, "test", self.archive)
        (source_prefix / "bin/stockfish").resolve().write_text("tampered")

        with patch.object(installer, "urlopen", side_effect=AssertionError("network fallback must not hide drift")):
            with self.assertRaisesRegex(ValueError, "drift"):
                installer.install(destination_prefix, "test", reuse_prefix=source_prefix)
        self.assertFalse((destination_prefix / "bin/stockfish").exists())

    def test_missing_reuse_source_may_use_explicit_offline_archive(self):
        destination_prefix = self.base / "destination"
        installer.install(
            destination_prefix, "test", archive=self.archive,
            reuse_prefix=self.base / "missing-source")
        self.assertTrue((destination_prefix / "bin/stockfish").is_symlink())

    def test_first_migration_rollback_restores_distro_config_and_keeps_other_changes(self):
        config = self.prefix / "app/laplace-api.env"
        config.parent.mkdir(parents=True)
        config.write_text("LAPLACE_STOCKFISH=/usr/games/stockfish\nUNRELATED=before\n")
        state = self.base / "state.json"
        installer.snapshot(self.prefix, state)
        self.install()
        target = (self.prefix / "bin/stockfish").resolve()
        config.write_text("LAPLACE_STOCKFISH=/managed/bin/stockfish\nUNRELATED=after\n")
        installer.restore(self.prefix, state)
        self.assertEqual("UNRELATED=after\nLAPLACE_STOCKFISH=/usr/games/stockfish\n", config.read_text())
        self.assertFalse((self.prefix / "bin/stockfish").exists())
        self.assertTrue(target.is_file())

    def test_rollback_restores_previous_managed_pointer(self):
        self.install()
        link = self.prefix / "bin/stockfish"
        previous = link.resolve()
        state = self.base / "state.json"
        installer.snapshot(self.prefix, state)
        link.unlink()
        replacement = self.prefix / "stockfish/replacement"
        replacement.write_text("retained release")
        link.symlink_to(replacement)
        installer.restore(self.prefix, state)
        self.assertEqual(previous, link.resolve())
        self.assertTrue(replacement.is_file())


if __name__ == "__main__":
    unittest.main(verbosity=2)