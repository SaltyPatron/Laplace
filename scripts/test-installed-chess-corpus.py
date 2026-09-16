#!/usr/bin/env python3
"""Disk/argument/receipt controls; no PostgreSQL or benchmark execution."""
import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location(
    "installed_chess_corpus", Path(__file__).with_name("measure-installed-chess-corpus.py"))
wrapper = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(wrapper)

WRITER_COUNTERS = (
    "applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted",
    "physicalitiesInserted", "attestationsAttempted", "attestationsInserted",
    "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge", "roundTrips",
    "copyTransactionsStarted", "copyTransactionsCommitted", "journalReplayHits",
)


def source_record(path):
    data = path.read_bytes()
    return {"path": str(path), "resolvedPath": str(path.resolve()), "bytes": len(data),
            "sha256": hashlib.sha256(data).hexdigest(), "status": "present"}


def receipt(source, games=75000, elapsed=30.0, qualified=True):
    rate = games / elapsed
    target = qualified and rate >= 2500
    return {
        "schema": "laplace.chess-corpus-capacity/v1", "status": "completed",
        "newlyRecordedGames": games, "readbackGames": games, "alreadyPresentGames": 0,
        "source": {"source": {"path": source["path"], "bytes": source["bytes"],
                              "sha256": source["sha256"]}, "distinctLines": 2},
        "phases": [
            {"status": "completed", "novelGames": games, "appliedGames": games,
             "committedGames": games, "readbackGames": games,
             "durability": {"localWalFlushAcknowledged": True}},
            {"status": "completed", "novelGames": 0, "appliedGames": 0,
             "readbackGames": games, "durability": None,
             "writer": dict.fromkeys(WRITER_COUNTERS, 0)},
        ],
        "recordedGamesPerSecond": rate, "elapsedSeconds": {"freshAdmission": elapsed},
        "qualifiedWindow": qualified, "targetMet": target,
        "targetVerdict": "established-for-this-workload" if target else
            "below-target-for-this-workload" if qualified else
            "unqualified-duration-or-content-variation",
    }


class InstalledCorpusTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="laplace-installed-corpus-control-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.chess = self.root / "Games/Chess"
        self.otb = self.chess / "Lumbras/otb"
        self.otb.mkdir(parents=True)
        self.pgn = self.otb / "authentic-source-2025.pgn"
        # This file is a transport fixture only; no chess parser or DB is invoked.
        self.pgn.write_bytes(b'[Event "transport control"]\n\n1. e4 e5 1-0\n')
        self.source = source_record(self.pgn)

    def test_inventory_observes_plain_and_compressed_files_without_modifying_or_unpacking(self):
        for name in ("archive.pgn.zst", "archive.pgn.gz", "weekly.zip", "ignore.txt"):
            (self.otb / name).write_bytes(b"opaque original bytes")
        nested = self.otb / "nested"
        nested.mkdir()
        (nested / "not-a-direct-input.pgn").write_bytes(b"nested")
        elite = self.chess / "Lumbras/pgn"
        elite.mkdir()
        (elite / "export_ELO2400.pgn").write_bytes(b"elite fixture")
        (elite / "unselected.pgn").write_bytes(b"not selected by this family")
        original = {str(p): p.read_bytes() for p in self.chess.rglob("*") if p.is_file()}
        observed = wrapper.inventory(self.chess)
        self.assertTrue(observed["complete"])
        names = {Path(row["path"]).name: row for row in observed["files"]}
        self.assertEqual({"authentic-source-2025.pgn", "archive.pgn.zst", "archive.pgn.gz",
                          "weekly.zip", "export_ELO2400.pgn"}, set(names))
        self.assertTrue(names[self.pgn.name]["eligiblePlainPgn"])
        self.assertFalse(names["archive.pgn.zst"]["eligiblePlainPgn"])
        self.assertEqual(original, {str(p): p.read_bytes() for p in self.chess.rglob("*") if p.is_file()})
        for row in observed["files"]:
            self.assertEqual(Path(row["path"]).stat().st_size, row["bytes"])
            self.assertEqual(str(Path(row["path"]).resolve()), row["resolvedPath"])
        self.assertIn("remote acquisition not authenticated", observed["scope"])

    @unittest.skipUnless(hasattr(os, "symlink"), "symlink controls require OS support")
    def test_inventory_does_not_follow_file_direct_or_intermediate_directory_symlinks(self):
        for kind in ("file", "direct-directory", "intermediate-directory"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                base = Path(temporary)
                chess = base / "chess"
                chess.mkdir()
                outside = base / "outside"
                (outside / "otb").mkdir(parents=True)
                target = outside / "otb/escape-2025.pgn"
                target.write_bytes(b"outside inventory root")
                if kind == "intermediate-directory":
                    (chess / "Lumbras").symlink_to(outside, target_is_directory=True)
                else:
                    (chess / "Lumbras").mkdir()
                    if kind == "direct-directory":
                        (chess / "Lumbras/otb").symlink_to(outside / "otb", target_is_directory=True)
                    else:
                        (chess / "Lumbras/otb").mkdir()
                        (chess / "Lumbras/otb/escape-2025.pgn").symlink_to(target)
                observed = wrapper.inventory(chess)
                self.assertEqual([], observed["files"])

    def test_inventory_envelopes_are_explicit_and_cannot_auto_select_partial_results(self):
        (self.otb / "second-2025.pgn").write_bytes(b"second")
        limited = wrapper.inventory(self.chess, limit=1)
        self.assertFalse(limited["complete"])
        self.assertEqual("inventory envelope exceeded", limited["reason"])
        with self.assertRaises(ValueError):
            wrapper.select_source(limited, None, "otb-2025")
        with patch.object(wrapper.time, "monotonic", side_effect=[0.0, 2.0]):
            expired = wrapper.inventory(self.chess, seconds=1)
        self.assertFalse(expired["complete"])
        with self.assertRaises(ValueError):
            wrapper.select_source(expired, None, "otb-2025")

    def test_selection_requires_one_nonempty_family_file_or_an_explicit_plain_path(self):
        observed = wrapper.inventory(self.chess)
        self.assertEqual(self.pgn, wrapper.select_source(observed, None, "otb-2025"))
        (self.otb / "duplicate-2025.pgn").write_bytes(b"second candidate")
        with self.assertRaises(ValueError):
            wrapper.select_source(wrapper.inventory(self.chess), None, "otb-2025")
        self.assertEqual(self.pgn.resolve(),
                         wrapper.select_source({"complete": False}, self.pgn, "otb-2025"))
        with self.assertRaises(ValueError):
            wrapper.select_source(observed, Path("relative.pgn"), "otb-2025")
        compressed = self.otb / "explicit.pgn.zst"
        compressed.write_bytes(b"compressed transport")
        with self.assertRaises(ValueError):
            wrapper.select_source(observed, compressed, "otb-2025")
        empty = {"complete": True, "files": [{**observed["files"][0], "bytes": 0}]}
        with self.assertRaises(ValueError):
            wrapper.select_source(empty, None, "otb-2025")

    def test_source_identity_reuses_real_file_observer_and_refuses_unstable_results(self):
        observed = wrapper.source_identity(wrapper.acceptance_owner(), self.pgn)
        self.assertEqual(self.source["sha256"], observed["sha256"])
        self.assertEqual(self.source["bytes"], observed["bytes"])
        self.assertEqual(str(self.pgn), observed["path"])
        self.assertIn("remote download provenance is not inferred", observed["acquisitionScope"])
        for status in ("absent", "unreadable", "changed-during-observation"):
            with self.subTest(status=status):
                helper = SimpleNamespace(_file=Mock(return_value={"status": status}))
                owner = SimpleNamespace(module=Mock(return_value=helper))
                with self.assertRaises(ValueError):
                    wrapper.source_identity(owner, self.pgn)
                owner.module.assert_called_once_with("corpus_source_file_identity",
                                                     "lib/chess_corpus_inventory.py")

    def test_measurement_argv_preserves_original_path_hash_and_owned_output(self):
        path = self.otb / "original game $(literal).pgn"
        path.write_bytes(self.pgn.read_bytes())
        source = source_record(path)
        output = self.root / "new evidence"
        arguments = wrapper.measure_arguments(source, output, 75000, 3600)
        self.assertEqual(["bash", str(wrapper.ROOT / "scripts/laplace"), "chess", "measure-corpus"],
                         arguments[:4])
        self.assertEqual(str(path), arguments[arguments.index("--pgn") + 1])
        self.assertEqual(source["sha256"], arguments[arguments.index("--expected-sha256") + 1])
        self.assertEqual(str(output / "measurement"),
                         arguments[arguments.index("--evidence-root") + 1])
        self.assertEqual("75000", arguments[arguments.index("--games") + 1])
        self.assertEqual("30", arguments[arguments.index("--minimum-seconds") + 1])
        self.assertEqual("1", arguments[arguments.index("--replays") + 1])
        self.assertEqual("3600", arguments[arguments.index("--deadline-seconds") + 1])
        self.assertEqual(source, source_record(path))
        self.assertFalse(output.exists())

    def test_summary_keeps_completed_short_window_unqualified_and_accepts_exact_sustained_math(self):
        for elapsed, qualified, expected_target in ((10.0, False, False),
                                                     (30.0, True, True),
                                                     (60.0, True, False)):
            with self.subTest(elapsed=elapsed):
                proof = wrapper.summary(receipt(self.source, elapsed=elapsed, qualified=qualified),
                                        75000, self.source)
                self.assertEqual(expected_target, proof["targetMet"])
                self.assertEqual(qualified, proof["qualifiedWindow"])
                self.assertEqual(75000 / elapsed, proof["recordedGamesPerSecond"])

    def test_summary_rejects_every_replay_writer_counter_when_nonzero_wrong_type_or_missing(self):
        for name in WRITER_COUNTERS:
            for invalid in (1, -1, False, 0.0, None, "missing"):
                with self.subTest(counter=name, invalid=invalid):
                    changed = receipt(self.source)
                    writer = changed["phases"][1]["writer"]
                    if invalid == "missing":
                        del writer[name]
                    else:
                        writer[name] = invalid
                    with self.assertRaises(ValueError):
                        wrapper.summary(changed, 75000, self.source)

    def test_summary_rejects_bad_rate_duration_or_target_qualification(self):
        mutations = [
            ("rate", float("nan")), ("rate", float("inf")), ("rate", True),
            ("rate", 2499), ("elapsed", 0), ("elapsed", -30),
            ("elapsed", float("nan")), ("elapsed", True),
            ("qualified", 1), ("target", 1), ("target", False),
            ("short-qualified", None), ("one-line-qualified", None),
        ]
        for field, value in mutations:
            with self.subTest(field=field, value=value):
                changed = receipt(self.source)
                if field == "rate": changed["recordedGamesPerSecond"] = value
                elif field == "elapsed": changed["elapsedSeconds"]["freshAdmission"] = value
                elif field == "qualified": changed["qualifiedWindow"] = value
                elif field == "target": changed["targetMet"] = value
                elif field == "short-qualified":
                    changed["elapsedSeconds"]["freshAdmission"] = 10
                    changed["recordedGamesPerSecond"] = 7500
                else: changed["source"]["distinctLines"] = 1
                with self.assertRaises(ValueError):
                    wrapper.summary(changed, 75000, self.source)

    def test_summary_rejects_incomplete_counts_source_binding_or_replay(self):
        for field in ("schema", "status", "newlyRecordedGames", "readbackGames",
                      "alreadyPresentGames", "source-hash", "source-bytes",
                      "phase-count", "phase-status", "replay-novel", "replay-applied",
                      "replay-readback", "replay-durability"):
            with self.subTest(field=field):
                changed = receipt(self.source)
                if field in ("schema", "status"): changed[field] = "invalid"
                elif field in ("newlyRecordedGames", "readbackGames"): changed[field] -= 1
                elif field == "alreadyPresentGames": changed[field] = 1
                elif field == "source-hash": changed["source"]["source"]["sha256"] = "f" * 64
                elif field == "source-bytes": changed["source"]["source"]["bytes"] += 1
                elif field == "phase-count": changed["phases"].pop()
                elif field == "phase-status": changed["phases"][1]["status"] = "failed"
                elif field == "replay-novel": changed["phases"][1]["novelGames"] = 1
                elif field == "replay-applied": changed["phases"][1]["appliedGames"] = 1
                elif field == "replay-readback": changed["phases"][1]["readbackGames"] -= 1
                else: changed["phases"][1]["durability"] = {"localWalFlushAcknowledged": True}
                with self.assertRaises(ValueError):
                    wrapper.summary(changed, 75000, self.source)

    @staticmethod
    def save(path, value):
        path.write_text(json.dumps(value, allow_nan=False), encoding="utf-8")

    def arguments(self, mode="measure", name="output"):
        return argparse.Namespace(mode=mode, output_dir=self.root / name, chess_root=self.chess,
                                  expected_source="a" * 40, pgn=self.pgn, family="otb-2025",
                                  games=75000, deadline_seconds=3600)

    def test_database_binding_rejects_native_managed_target_mismatch_before_commands(self):
        expected = "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace"
        with patch.dict(wrapper.os.environ, {}, clear=True):
            target = wrapper.database_target()
            self.assertEqual(expected, wrapper.os.environ["LAPLACE_DB"])
            self.assertEqual({"socket": "/var/run/postgresql", "port": 5432,
                              "database": "laplace", "role": "laplace_admin"}, target)
        for key, value in (("PGHOST", "/different/socket"), ("PGDATABASE", "different"),
                           ("PGUSER", "different"), ("PGPORT", "5433"),
                           ("LAPLACE_DB", "Host=remote;Password=transport-test-secret;Database=laplace")):
            with self.subTest(key=key), patch.dict(wrapper.os.environ, {key: value}, clear=True):
                with self.assertRaises(ValueError) as failed:
                    wrapper.database_target()
                self.assertNotIn("transport-test-secret", str(failed.exception))
        args = self.arguments(name="mismatched-target")
        owner = SimpleNamespace(save=self.save, command=Mock())
        with patch.object(wrapper, "exact_source", return_value="a" * 40), \
             patch.object(wrapper, "source_identity", return_value=self.source), \
             patch.object(wrapper, "cli_identity") as cli, \
             patch.dict(wrapper.os.environ, {"PGDATABASE": "different"}, clear=True), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(1, wrapper.run(args, owner=owner))
        owner.command.assert_not_called()
        cli.assert_not_called()
        proof = json.loads((args.output_dir / "receipt.json").read_text())
        self.assertEqual("failed", proof["status"])
        self.assertFalse(proof["targetMet"])
        self.assertEqual("failed", next(phase["status"] for phase in proof["phases"]
                                      if phase["name"] == "database-target"))

    def test_late_postflight_failures_keep_measurement_evidence_but_revoke_outer_target(self):
        for failure in ("cli", "source", "native"):
            with self.subTest(failure=failure):
                args = self.arguments(name="output-" + failure)
                commands = []

                def command(argv, log, timeout):
                    command_line = [str(value) for value in argv]
                    commands.append(command_line)
                    log.write_text("synthetic command control\n", encoding="utf-8")
                    if "measure-corpus" in command_line:
                        destination = args.output_dir / "measurement"
                        destination.mkdir()
                        self.save(destination / "corpus-recording.json", receipt(self.source))
                    if failure == "native" and "--compare" in command_line:
                        raise RuntimeError("synthetic native postflight failure")

                owner = SimpleNamespace(save=self.save, command=command)
                identities = [{"directory": "source-built", "sha256": {"cli": "unchanged"}}] * 2
                if failure == "cli":
                    identities[1] = {"directory": "source-built", "sha256": {"cli": "changed"}}
                sources = ["a" * 40, ValueError("synthetic source drift") if failure == "source" else "a" * 40]
                with patch.object(wrapper, "exact_source", side_effect=sources), \
                     patch.object(wrapper, "source_identity", return_value=self.source), \
                     patch.object(wrapper, "cli_identity", side_effect=identities), \
                     patch.dict(wrapper.os.environ, {}, clear=True), \
                     contextlib.redirect_stdout(io.StringIO()):
                    result = wrapper.run(args, owner=owner)
                self.assertEqual(1, result)
                proof = json.loads((args.output_dir / "receipt.json").read_text())
                self.assertEqual("failed", proof["status"])
                self.assertFalse(proof["targetMet"])
                self.assertEqual(75000, proof["measurement"]["newlyRecordedGames"])
                self.assertTrue((args.output_dir / "measurement/corpus-recording.json").is_file())
                self.assertTrue(any("--compare" in command for command in commands))
                self.assertIn("cli-identity-after", [phase["name"] for phase in proof["phases"]])
                self.assertIn("source-after", [phase["name"] for phase in proof["phases"]])
                self.assertIn("native-after", [phase["name"] for phase in proof["phases"]])

    def test_inventory_mode_uses_no_native_database_or_measurement_commands_and_preserves_existing_output(self):
        args = self.arguments(mode="inventory")
        owner = SimpleNamespace(save=self.save, command=Mock())
        with patch.object(wrapper, "exact_source", return_value="a" * 40), \
             contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, wrapper.run(args, owner=owner))
        owner.command.assert_not_called()
        saved = json.loads((args.output_dir / "receipt.json").read_text())
        self.assertEqual("observed", saved["status"])
        self.assertFalse(saved["targetMet"])
        self.assertEqual(1, len(json.loads((args.output_dir / "source-inventory.json").read_text())["files"]))
        original = (args.output_dir / "receipt.json").read_bytes()
        with self.assertRaises(FileExistsError):
            wrapper.run(args, owner=owner)
        self.assertEqual(original, (args.output_dir / "receipt.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
