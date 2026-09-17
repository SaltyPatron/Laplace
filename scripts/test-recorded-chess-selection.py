#!/usr/bin/env python3
"""Disk/transport/receipt controls; these do not execute PostgreSQL or native chess."""
import copy
import hashlib
import importlib.util
import io
import json
import signal
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location("recorded_selection_adapter",
    Path(__file__).with_name("verify-recorded-chess-selection.py"))
adapter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(adapter)


def file_identity(path):
    data = Path(path).read_bytes()
    return {"path": str(path), "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def save(path, value):
    path.write_text(json.dumps(value, allow_nan=False) + "\n")


class RecordedSelectionAdapterTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="laplace-recorded-selection-control-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.source = self.root / "original.pgn"
        self.source.write_bytes(b"opaque transport source; native verifier owns chess parsing\n")
        self.original = self.root / "original"
        self.original.mkdir()
        self.entries, self.chunks, self.games = [], [], []
        for index in range(2):
            playing = str(index + 1) * 32
            game = {"playingId": playing, "lineId": "a" * 32, "startPositionId": "b" * 32,
                    "moveIds": ["c" * 32, "d" * 32], "result": "1-0",
                    "whitePlayerId": None, "blackPlayerId": None, "termination": ""}
            self.games.append(game)
            self.entries.append({"sourceOrdinal": index + 1, "framedGameSha256": "e" * 64,
                "playingId": playing, "lineId": game["lineId"], "startPositionId": game["startPositionId"],
                "plies": 2, "result": game["result"]})
            body = self.original / f"chunk-{index+1:07}.json"
            scope = self.original / f"scope-{index+1:07}.jsonl"
            save(body, {"schema": "laplace.chess-corpus-chunk/v1", "index": index + 1,
                "firstSelectedGame": index, "newlyRecordedGames": 1, "games": [game], "scopes": [{}]})
            scope.write_bytes(b'{"transport":"scope fixture"}\n')
            self.chunks.append({"index": index + 1, "firstSelectedGame": index, "games": 1,
                "novelGames": 1, "plies": 2, "gameBodiesSha256": "f" * 64,
                "body": file_identity(body), "scope": file_identity(scope)})
        self.selection = self.original / "selection.jsonl"
        # The unsealed third occurrence must never be selected from the failed parent.
        self.selection.write_text("".join(json.dumps(entry) + "\n" for entry in [
            *self.entries, {**self.entries[-1], "sourceOrdinal": 3, "playingId": "3" * 32}]))
        self.inventory = self.original / "chunks.jsonl"
        self.write_inventory()
        self.parent = self.original / "corpus-recording.json"
        self.parent_value = {
            "schema": "laplace.chess-corpus-capacity/v1", "status": "failed",
            "options": {"games": 5}, "newlyRecordedGames": 2, "alreadyPresentGames": 0,
            "parsedGamesWithoutCompleteChunkEvidence": 1,
            "recordedGamesPerSecond": None, "qualifiedWindow": False, "targetMet": False,
            "source": {"source": file_identity(self.source), "selectionManifest": file_identity(self.selection)},
            "phases": [{"status": "failed", "durability": {"localWalFlushAcknowledged": True},
                "corpusEvidence": {"completed": False, "readbackGames": 2,
                                  "newlyRecordedGames": 2, "chunks": 2}}],
        }
        save(self.parent, self.parent_value)
        self.run_file = self.original / "run.json"
        self.run_value = {"id": 123456, "status": "completed", "conclusion": "failure",
                          "untouched": {"original": ["all", "metadata"]}}
        save(self.run_file, self.run_value)
        self.args = SimpleNamespace(expected_source="a" * 40, output_dir=self.root / "output",
            pgn=self.source, source_sha256=file_identity(self.source)["sha256"],
            selection_manifest=self.selection, selection_sha256=file_identity(self.selection)["sha256"],
            chunk_manifest=self.inventory, chunk_manifest_sha256=file_identity(self.inventory)["sha256"],
            expected_games=2, parent_receipt=self.parent, parent_receipt_sha256=file_identity(self.parent)["sha256"],
            parent_run_outcome=self.run_file, parent_run_outcome_sha256=file_identity(self.run_file)["sha256"],
            parent_run_id=123456, deadline_seconds=30)
        self.owner = SimpleNamespace(save=save)
        self.standard = SimpleNamespace(
            source_identity=lambda owner, path: file_identity(path),
            exact_source=Mock(return_value="a" * 40),
            database_target=Mock(return_value={"database": "laplace"}),
            cli_identity=Mock(return_value={"directory": "/exact/cli", "sha256": {"Laplace.Cli.dll": "b" * 64}}))
        self.guard = adapter.module("recorded_selection_guard_tests", "check-application-runtime.py")

    def write_inventory(self):
        self.inventory.write_text("".join(json.dumps(chunk) + "\n" for chunk in self.chunks))

    def prepare(self):
        self.args.output_dir.mkdir()
        return adapter.prepare(self.args, self.owner, self.standard)

    def rebind_parent(self):
        save(self.parent, self.parent_value)
        self.args.parent_receipt_sha256 = file_identity(self.parent)["sha256"]

    def test_builds_only_explicit_complete_prefix_and_retains_failed_parent_verbatim(self):
        original = {path: path.read_bytes() for path in self.original.iterdir()}
        selected = self.prepare()
        manifest = adapter.read_json(selected["manifest"]["path"])
        self.assertEqual(2, selected["selectedGames"])
        self.assertEqual(4, selected["readbackPlies"])
        self.assertEqual(["1" * 32, "2" * 32], selected["playingIds"])
        self.assertEqual(self.chunks, manifest["chunks"])
        self.assertEqual("failed", selected["parent"]["status"])
        self.assertEqual("failure", selected["parent"]["conclusion"])
        self.assertEqual(5, selected["parent"]["requestedGames"])
        self.assertEqual(1, selected["parent"]["parsedGamesWithoutCompleteChunkEvidence"])
        self.assertEqual(original[self.parent], (self.args.output_dir / "retained-parent/corpus-recording.json").read_bytes())
        self.assertEqual(original[self.run_file], (self.args.output_dir / "retained-parent/run-outcome.json").read_bytes())
        self.assertEqual(original, {path: path.read_bytes() for path in self.original.iterdir()})
        self.assertNotIn("recordedGamesPerSecond", selected)

    def test_changed_explicit_hashes_reject_before_manifest_or_parent_copy(self):
        for field in ("source_sha256", "selection_sha256", "chunk_manifest_sha256",
                      "parent_receipt_sha256", "parent_run_outcome_sha256"):
            with self.subTest(field=field):
                args = copy.copy(self.args)
                setattr(args, field, "0" * 64)
                args.output_dir = self.root / field
                args.output_dir.mkdir()
                with self.assertRaises(ValueError):
                    adapter.prepare(args, self.owner, self.standard)
                self.assertFalse((args.output_dir / "selection.json").exists())

    def test_explicit_completed_count_is_not_the_parent_requested_count(self):
        for games in (1, 3, 5):
            with self.subTest(games=games):
                args = copy.copy(self.args)
                args.expected_games = games
                args.output_dir = self.root / f"count-{games}"
                args.output_dir.mkdir()
                with self.assertRaises(ValueError):
                    adapter.prepare(args, self.owner, self.standard)

    def test_parent_cannot_be_relabelled_completed_or_given_a_capacity_rate(self):
        changes = [("status", "completed"), ("recordedGamesPerSecond", 0.5),
                   ("qualifiedWindow", True), ("targetMet", True)]
        for field, value in changes:
            with self.subTest(field=field):
                candidate = copy.deepcopy(self.parent_value)
                candidate[field] = value
                save(self.parent, candidate)
                args = copy.copy(self.args)
                args.parent_receipt_sha256 = file_identity(self.parent)["sha256"]
                args.output_dir = self.root / f"parent-{field}"
                args.output_dir.mkdir()
                with self.assertRaises(ValueError):
                    adapter.prepare(args, self.owner, self.standard)

    def test_successful_running_or_wrong_remote_run_is_not_the_failed_parent(self):
        for field, value in (("id", 999), ("status", "in_progress"), ("conclusion", "success")):
            with self.subTest(field=field):
                candidate = {**self.run_value, field: value}
                save(self.run_file, candidate)
                args = copy.copy(self.args)
                args.parent_run_outcome_sha256 = file_identity(self.run_file)["sha256"]
                args.output_dir = self.root / f"run-{field}"
                args.output_dir.mkdir()
                with self.assertRaises(ValueError):
                    adapter.prepare(args, self.owner, self.standard)

    def test_missing_wal_acknowledgement_or_unsealed_inventory_count_is_not_durable_subset(self):
        for kind in ("wal", "completed", "chunks", "readback"):
            with self.subTest(kind=kind):
                candidate = copy.deepcopy(self.parent_value)
                phase = candidate["phases"][0]
                if kind == "wal":
                    phase["durability"]["localWalFlushAcknowledged"] = False
                else:
                    phase["corpusEvidence"][{"completed": "completed", "chunks": "chunks",
                        "readback": "readbackGames"}[kind]] = True if kind == "completed" else 3
                save(self.parent, candidate)
                args = copy.copy(self.args)
                args.parent_receipt_sha256 = file_identity(self.parent)["sha256"]
                args.output_dir = self.root / f"durability-{kind}"
                args.output_dir.mkdir()
                with self.assertRaises(ValueError):
                    adapter.prepare(args, self.owner, self.standard)

    def test_chunk_gaps_duplicates_incomplete_ranges_and_extra_complete_games_reject(self):
        mutations = ("gap", "duplicate", "partial", "extra")
        original = copy.deepcopy(self.chunks)
        for kind in mutations:
            with self.subTest(kind=kind):
                self.chunks = copy.deepcopy(original)
                if kind == "gap":
                    self.chunks[1]["firstSelectedGame"] = 2
                elif kind == "duplicate":
                    self.chunks[1]["index"] = 1
                elif kind == "partial":
                    self.chunks[1]["novelGames"] = 0
                else:
                    self.chunks.append({**self.chunks[-1], "index": 3, "firstSelectedGame": 2})
                self.write_inventory()
                args = copy.copy(self.args)
                args.chunk_manifest_sha256 = file_identity(self.inventory)["sha256"]
                args.output_dir = self.root / f"chunks-{kind}"
                args.output_dir.mkdir()
                with self.assertRaises(ValueError):
                    adapter.prepare(args, self.owner, self.standard)

    def test_changed_body_and_scope_cannot_be_hidden_by_the_original_manifest(self):
        for kind in ("body", "scope"):
            with self.subTest(kind=kind):
                path = Path(self.chunks[0][kind]["path"])
                original = path.read_bytes()
                path.write_bytes(original + b"\n")
                args = copy.copy(self.args)
                args.output_dir = self.root / f"changed-{kind}"
                args.output_dir.mkdir()
                with self.assertRaises(ValueError):
                    adapter.prepare(args, self.owner, self.standard)
                path.write_bytes(original)

    def test_duplicate_json_properties_and_rebound_source_selection_reject(self):
        text = self.selection.read_text()
        first = json.loads(text.splitlines()[0])
        first["playingId"] = "9" * 32
        self.selection.write_text(json.dumps(first) + "\n" + "\n".join(text.splitlines()[1:]) + "\n")
        self.args.selection_sha256 = file_identity(self.selection)["sha256"]
        self.parent_value["source"]["selectionManifest"] = file_identity(self.selection)
        self.rebind_parent()
        with self.assertRaises(ValueError):
            self.prepare()
        duplicate = self.root / "duplicate.json"
        duplicate.write_text('{"id":1,"id":2}')
        with self.assertRaises(ValueError):
            adapter.read_json(duplicate)

    def verification_receipt(self, selected):
        root = self.args.output_dir / "verification"
        root.mkdir(exist_ok=True)
        def evidence(name, novel):
            directory = root / name
            directory.mkdir(exist_ok=True)
            chunk = directory / "chunks.jsonl"
            state = directory / "state.jsonl"
            chunk.write_bytes(b"transport chunk manifest\n")
            state.write_bytes(b"exact unchanged scoped rows\n")
            return {"directory": str(directory), "completed": True, "chunks": 2,
                    "readbackGames": 2, "newlyRecordedGames": novel,
                    "chunkManifest": file_identity(chunk),
                    "exactScopeState": {"rows": 1, "file": file_identity(state)}}
        phases = []
        for name in ("readback", "replay"):
            phases.append({"name": name, "recording": {
                "schema": "laplace.chess-recording/v2", "status": "completed",
                "requestedGames": 2, "parsedGames": 2, "committedGames": 2,
                "readbackGames": 2, "readbackPlies": 4, "novelGames": 0, "appliedGames": 0,
                "durability": None, "writer": dict.fromkeys(adapter.COUNTERS, 0),
                "verification": {"uniquePlayingIds": True, "exactGameBodies": True,
                    "exactWitnessMembership": True, "exactExperimentBody": False, "completedGames": True},
                "corpusSource": {"source": selected["source"], "selectionManifest": selected["selectionManifest"]},
                "corpusEvidence": evidence(name, 0),
            }})
        return {"schema": "laplace.chess-recorded-corpus-verification/v1", "status": "completed",
                "selectedGames": 2, "readbackGames": 2, "readbackPlies": 4, "retainedScopeReplayVerified": True,
                **{key: selected[key] for key in ("manifest", "source", "selectionManifest", "playingIds")},
                "retainedBaseline": evidence("retained", 2), "phases": phases}

    def test_summary_requires_both_complete_current_readback_and_noop_replay(self):
        selected = self.prepare()
        receipt = self.verification_receipt(selected)
        self.assertEqual({"status": "completed", "selectedGames": 2, "readbackGames": 2,
                          "readbackPlies": 4, "retainedScopeReplayVerified": True},
                         adapter.summary(receipt, selected, self.owner, self.standard))
        for mutation in ("missing", "order", "false-count", "wrong-id", "incomplete", "unverified"):
            with self.subTest(mutation=mutation):
                changed = copy.deepcopy(receipt)
                if mutation == "missing":
                    changed["phases"].pop()
                elif mutation == "order":
                    changed["phases"].reverse()
                elif mutation == "false-count":
                    changed["selectedGames"] = True
                elif mutation == "wrong-id":
                    changed["playingIds"][0] = "9" * 32
                elif mutation == "incomplete":
                    changed["phases"][0]["recording"]["readbackGames"] = 1
                else:
                    changed["phases"][1]["recording"]["verification"]["exactGameBodies"] = False
                with self.assertRaises(ValueError):
                    adapter.summary(changed, selected, self.owner, self.standard)

    def test_every_writer_counter_must_be_an_exact_integer_zero_in_each_phase(self):
        selected = self.prepare()
        receipt = self.verification_receipt(selected)
        for phase in range(2):
            for counter in adapter.COUNTERS:
                for invalid in (1, False, "0", None):
                    with self.subTest(phase=phase, counter=counter, invalid=invalid):
                        changed = copy.deepcopy(receipt)
                        changed["phases"][phase]["recording"]["writer"][counter] = invalid
                        with self.assertRaises(ValueError):
                            adapter.summary(changed, selected, self.owner, self.standard)

    def test_scope_file_hash_rows_and_recorded_source_must_match(self):
        selected = self.prepare()
        receipt = self.verification_receipt(selected)
        for kind in ("scope-hash", "scope-rows", "source", "manifest"):
            with self.subTest(kind=kind):
                changed = copy.deepcopy(receipt)
                recording = changed["phases"][1]["recording"]
                if kind == "scope-hash":
                    recording["corpusEvidence"]["exactScopeState"]["file"]["sha256"] = "0" * 64
                elif kind == "scope-rows":
                    recording["corpusEvidence"]["exactScopeState"]["rows"] = 2
                elif kind == "source":
                    recording["corpusSource"]["source"]["sha256"] = "0" * 64
                else:
                    changed["manifest"]["sha256"] = "0" * 64
                with self.assertRaises(ValueError):
                    adapter.summary(changed, selected, self.owner, self.standard)

    @staticmethod
    def snapshot():
        return {"format": 3, "purpose": "recording", "build": {"directory": "/canonical/build"},
                "artifacts": {"core": "a" * 64}, "database": {
                    "database_oid": 38468924, "system_identifier": "7672946663471807927",
                    "running_ingests": 1}}

    def test_runtime_snapshot_requires_exact_current_database_incarnation(self):
        path = self.root / "runtime.json"
        baseline = self.snapshot()
        save(path, baseline)
        self.assertEqual(baseline, adapter.recording_snapshot(path, self.guard))
        for field, invalid in (("format", 2), ("format", True), ("purpose", "publication"),
                               ("database_oid", "38468924"), ("system_identifier", 7672946663471807927)):
            with self.subTest(field=field, invalid=invalid):
                value = copy.deepcopy(baseline)
                if field in ("format", "purpose"):
                    value[field] = invalid
                else:
                    value["database"][field] = invalid
                save(path, value)
                with self.assertRaises(ValueError):
                    adapter.recording_snapshot(path, self.guard)

    def test_command_selects_canonical_verification_without_measurement_or_repair_flags(self):
        selected = self.prepare()
        command = adapter.verification_arguments(selected, self.args.output_dir, 30)
        self.assertEqual(["chess", "verify-recorded-corpus"], command[2:4])
        self.assertIn(selected["manifest"]["sha256"], command)
        self.assertIn(selected["manifest"]["path"], command)
        self.assertNotIn("measure-corpus", command)
        self.assertNotIn("--games", command)

    def run_transport(self, failure=None):
        commands = []
        def command(argv, log, timeout, **kwargs):
            args = [str(value) for value in argv]
            commands.append(args)
            log.write_text("mocked transport command; no native/DB operation\n")
            if "--snapshot" in args:
                value = self.snapshot()
                if "native-after.json" in args[-1] and failure == "database-recreated":
                    value["database"]["database_oid"] += 1
                save(Path(args[args.index("--snapshot") + 1]), value)
            else:
                if failure == "cli-failed":
                    raise RuntimeError("transport CLI failure")
                proof = adapter.read_json(self.args.output_dir / "receipt.json")["selection"]
                receipt = self.verification_receipt(proof)
                save(self.args.output_dir / "verification/recorded-verification.json", receipt)
                if failure == "parent-changed":
                    self.parent.write_bytes(self.parent.read_bytes() + b"\n")
                if failure == "cli-changed":
                    self.standard.cli_identity.side_effect = [
                        {"directory": "/changed", "sha256": {"Laplace.Cli.dll": "c" * 64}}]
        self.owner.command = command
        with redirect_stdout(io.StringIO()):
            code = adapter.run(self.args, owner=self.owner, standard=self.standard, guard=self.guard)
        return code, adapter.read_json(self.args.output_dir / "receipt.json"), commands

    def test_orchestration_retains_independent_success_and_failed_parent_without_installing(self):
        code, receipt, commands = self.run_transport()
        self.assertEqual(0, code)
        self.assertEqual("completed", receipt["status"])
        self.assertEqual("failed", receipt["selection"]["parent"]["status"])
        self.assertEqual("failure", receipt["selection"]["parent"]["conclusion"])
        self.assertEqual(38468924, receipt["databaseIncarnation"]["databaseOid"])
        self.assertEqual("7672946663471807927", receipt["databaseIncarnation"]["systemIdentifier"])
        self.assertTrue(receipt["verification"]["retainedScopeReplayVerified"])
        flattened = [part for command in commands for part in command]
        self.assertIn("--compare", flattened)
        self.assertNotIn("sudo", flattened)
        self.assertNotIn("install", flattened)
        self.assertNotIn("measure-corpus", flattened)

    def test_cli_failure_still_runs_source_cli_input_and_native_postflight(self):
        code, receipt, commands = self.run_transport("cli-failed")
        self.assertEqual(1, code)
        self.assertEqual("failed", receipt["status"])
        names = {phase["name"] for phase in receipt["phases"]}
        self.assertTrue({"source-after", "cli-after", "retained-inputs-after", "native-after"} <= names)
        self.assertTrue(any("--compare" in command for command in commands))

    def test_database_recreation_downgrades_an_otherwise_complete_selected_verification(self):
        code, receipt, _ = self.run_transport("database-recreated")
        self.assertEqual(1, code)
        self.assertEqual("failed", receipt["status"])
        self.assertEqual("native-after", receipt["postflightErrors"][-1]["phase"])

    def test_parent_changes_after_success_cannot_be_hidden_by_retained_copy(self):
        code, receipt, _ = self.run_transport("parent-changed")
        self.assertEqual(1, code)
        self.assertEqual("failed", receipt["status"])
        self.assertTrue(any(row["phase"] == "retained-inputs-after" for row in receipt["postflightErrors"]))

    def test_cli_payload_change_downgrades_an_otherwise_complete_verification(self):
        code, receipt, _ = self.run_transport("cli-changed")
        self.assertEqual(1, code)
        self.assertTrue(any(row["phase"] == "cli-after" for row in receipt["postflightErrors"]))

    def test_interrupt_handler_latches_cleanup_signals_and_restores_previous_handlers(self):
        before = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}
        with self.assertRaises(KeyboardInterrupt):
            with adapter.interruption_scope():
                handler = signal.getsignal(signal.SIGTERM)
                try:
                    handler(signal.SIGTERM, None)
                finally:
                    self.assertIs(signal.SIG_IGN, signal.getsignal(signal.SIGTERM))
                    self.assertIs(signal.SIG_IGN, signal.getsignal(signal.SIGINT))
        self.assertEqual(before, {number: signal.getsignal(number) for number in before})


if __name__ == "__main__":
    unittest.main()
