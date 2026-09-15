#!/usr/bin/env python3
"""Stockfish source update/build contracts; isolated git repos and UCI peers only."""
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("stockfish_install", ROOT / "scripts/install-stockfish.py")
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)

PROGRAM = ("#!/bin/sh\nwhile IFS= read -r command; do\ncase \"$command\" in\n"
                   "uci) printf 'id name Stockfish 19\\n"
                   "option name Threads type spin default 1 min 1 max 1024\\n"
                   "option name Hash type spin default 16 min 1 max 1024\\n"
                   "option name UCI_LimitStrength type check default false\\n"
                   "option name UCI_Elo type spin default 3190 min 1320 max 3190\\nuciok\\n';;\n"
                   "isready) printf 'readyok\\n';;\n"
                   "'go depth 1') printf 'info depth 1 score cp 10\\nbestmove e2e4\\n';;\n"
                   "quit) exit 0;;\nesac\ndone\n").encode()

class StockfishSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="stockfish-source-contract-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base / "external/stockfish"
        self.source.mkdir(parents=True)
        self.original_root = installer.ROOT
        installer.ROOT = self.base
        self.addCleanup(setattr, installer, "ROOT", self.original_root)
        self.environment = patch.dict(os.environ, {"LAPLACE_EXTERNAL": str(self.base / "external")})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.git("init", "--quiet")
        self.git("config", "user.name", "Stockfish contract")
        self.git("config", "user.email", "stockfish-contract@example.invalid")
        self.git("remote", "add", "origin", "https://github.com/official-stockfish/Stockfish.git")
        (self.source / "src").mkdir()
        (self.source / "src/source.cpp").write_text("release source\n")
        (self.source / ".gitignore").write_text("/src/stockfish\n/src/*.nnue\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "fixture release")
        self.commit = self.git("rev-parse", "HEAD")
        self.git("tag", "sf_19")
        self.lock = {"version": "19", "tag": "sf_19", "commit": self.commit,
                     "repository": "https://github.com/official-stockfish/Stockfish.git"}
        (self.base / "deploy/linux").mkdir(parents=True)
        (self.base / "deploy/linux/stockfish-release.json").write_text(json.dumps(self.lock))

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.source), *args],
                              text=True, capture_output=True, check=True).stdout.strip()

    def executable(self, program=PROGRAM):
        binary = self.source / "src/stockfish"
        binary.write_bytes(program)
        binary.chmod(0o755)
        return binary

    def test_source_selection_uses_existing_external_tree(self):
        self.assertEqual(self.source, installer.source_root())
        self.assertEqual(self.source / "src/stockfish", installer.binary_path())
        with patch.dict(os.environ, {"LAPLACE_STOCKFISH_SOURCE": str(self.base / "own-checkout")}):
            self.assertEqual(self.base / "own-checkout", installer.source_root())

    def test_clean_pinned_checkout_is_reused_without_network(self):
        self.assertEqual(self.commit, installer.update_source(self.source, self.lock))
        self.assertEqual(self.commit, self.git("rev-parse", "HEAD"))

    def test_dirty_and_untracked_source_files_are_preserved(self):
        for filename in ("src/source.cpp", "operator-note.txt"):
            with self.subTest(filename=filename):
                path = self.source / filename
                path.write_text("operator changes\n")
                with self.assertRaisesRegex(ValueError, "local changes"):
                    installer.update_source(self.source, self.lock)
                self.assertEqual("operator changes\n", path.read_text())
                self.assertEqual(self.commit, self.git("rev-parse", "HEAD"))
                if filename == "src/source.cpp":
                    path.write_text("release source\n")
                else:
                    path.unlink()

    def test_prior_branch_tip_remains_reachable_after_update(self):
        (self.source / "src/source.cpp").write_text("local committed implementation\n")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "preserved local branch")
        previous = self.git("rev-parse", "HEAD")
        installer.update_source(self.source, self.lock)
        self.assertEqual(self.commit, self.git("rev-parse", "HEAD"))
        self.assertEqual(previous, self.git("rev-parse", "refs/laplace/stockfish-before-update/" + previous))

    def test_unofficial_origin_and_wrong_pin_are_rejected(self):
        self.git("remote", "set-url", "origin", "https://example.invalid/Stockfish.git")
        with self.assertRaisesRegex(ValueError, "official repository"):
            installer.update_source(self.source, self.lock)
        self.git("remote", "set-url", "origin", self.lock["repository"])
        self.lock["commit"] = "0" * 40
        with self.assertRaisesRegex(ValueError, "pinned official"):
            installer.update_source(self.source, self.lock)

    def test_external_pin_update_preserves_unrelated_dependencies(self):
        pins = self.source.parent / "PINS.tsv"
        pins.write_text("# host pins\nexternal/other\thttps://example.invalid/other\t123\n"
                        "external/stockfish\thttps://old.invalid/Stockfish\told\n")
        pins.chmod(0o664)
        previous = pins.stat()
        installer.record_external_pin(self.source, self.lock)
        updated = pins.stat()
        self.assertEqual((previous.st_ino, previous.st_uid, previous.st_gid, previous.st_mode),
                         (updated.st_ino, updated.st_uid, updated.st_gid, updated.st_mode))
        self.assertIn("external/other\thttps://example.invalid/other\t123\n", pins.read_text())
        self.assertEqual(1, pins.read_text().count("external/stockfish\t"))
        self.assertIn(self.commit, pins.read_text())

    def test_probe_requires_search_and_rejects_missing_network(self):
        binary = self.executable()
        self.assertIn("UCI_Elo", installer.probe(binary, "19"))
        self.executable(PROGRAM.replace(b"printf 'info depth 1 score cp 10\\nbestmove e2e4\\n'", b"exit 7"))
        with self.assertRaises(subprocess.CalledProcessError):
            installer.probe(binary, "19")
        self.executable(PROGRAM.replace(b"bestmove e2e4", b"bestmove 0000"))
        with self.assertRaisesRegex(ValueError, "legal move"):
            installer.probe(binary, "19")

    def test_failed_source_build_restores_prior_executable(self):
        binary = self.executable()
        previous = binary.read_bytes()
        original_run = installer.subprocess.run

        def run(args, **kwargs):
            if args[0] == "sh":
                return subprocess.CompletedProcess(args, 0, stdout="x86-64-avx2\n")
            if args[0] == "g++":
                return subprocess.CompletedProcess(args, 0, stdout="gcc fixture\n")
            if args[0] == "make":
                candidate = binary.with_name(next(arg.split("=", 1)[1] for arg in args if arg.startswith("EXE=")))
                candidate.write_text("failed build output")
                raise subprocess.CalledProcessError(2, args)
            return original_run(args, **kwargs)

        with patch.object(installer.subprocess, "run", side_effect=run):
            with self.assertRaises(subprocess.CalledProcessError):
                installer.build(self.source, jobs=1)
        self.assertEqual(previous, binary.read_bytes())
        self.assertIn("UCI_Elo", installer.probe(binary, "19"))

    def test_configured_path_prefers_explicit_environment_then_application_config(self):
        prefix = self.base / "install"
        config = prefix / "app/laplace-api.env"
        config.parent.mkdir(parents=True)
        config.write_text("OTHER=untouched\nLAPLACE_STOCKFISH=/operator/stockfish\n")
        self.assertEqual(Path("/operator/stockfish"), installer.configured_binary(prefix))
        with patch.dict(os.environ, {"LAPLACE_STOCKFISH": "/explicit/stockfish"}):
            self.assertEqual(Path("/explicit/stockfish"), installer.configured_binary(prefix))

    def test_latest_check_verifies_release_tag_and_source_commit(self):
        with patch.object(installer, "github_json", side_effect=[{"tag_name": "sf_19"}, {"sha": self.commit}]):
            installer.check_latest()
        with patch.object(installer, "github_json", return_value={"tag_name": "sf_20"}):
            with self.assertRaisesRegex(ValueError, "stale"):
                installer.check_latest()
        with patch.object(installer, "github_json", side_effect=[{"tag_name": "sf_19"}, {"sha": "0" * 40}]):
            with self.assertRaisesRegex(ValueError, "source pin"):
                installer.check_latest()


if __name__ == "__main__":
    unittest.main(verbosity=2)
