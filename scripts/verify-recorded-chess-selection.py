#!/usr/bin/env python3
"""Verify an explicit complete recorded subset without qualifying its failed parent.

The caller owns the shared host reservation and prepared exact source build.
This adapter reads retained evidence, runs the canonical read-only verifier and
runtime guard, and writes only its new evidence directory.
"""
import argparse
from contextlib import contextmanager
import datetime
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import stat
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "laplace.installed-recorded-selection-verification/v1"
COUNTERS = (
    "applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted",
    "physicalitiesInserted", "attestationsAttempted", "attestationsInserted",
    "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge", "roundTrips",
    "copyTransactionsStarted", "copyTransactionsCommitted", "journalReplayHits",
)


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate retained JSON property")
        result[key] = value
    return result


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"),
                      object_pairs_hook=unique_object,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError("nonfinite JSON value")))


def hex_value(value, length):
    return isinstance(value, str) and re.fullmatch("[0-9a-f]{" + str(length) + "}", value) is not None


def integer(value, minimum=0):
    return type(value) is int and value >= minimum


def identity(owner, standard, path, expected=None):
    path = Path(path)
    if not path.is_absolute() or Path(os.path.abspath(path)) != path or not stat.S_ISREG(path.stat().st_mode):
        raise ValueError("retained input must be an absolute normalized regular file")
    observed = standard.source_identity(owner, path)
    result = {key: observed[key] for key in ("path", "bytes", "sha256")}
    if (result["path"] != str(path) or not integer(result["bytes"])
            or not hex_value(result["sha256"], 64)
            or expected is not None and result["sha256"] != expected):
        raise ValueError("retained input differs from its explicit SHA256")
    return result


def bound_file(owner, standard, value):
    if (not isinstance(value, dict) or set(value) != {"path", "bytes", "sha256"}
            or not integer(value["bytes"]) or not hex_value(value["sha256"], 64)):
        raise ValueError("invalid retained file identity")
    observed = identity(owner, standard, value["path"], value["sha256"])
    if observed != value:
        raise ValueError("retained file bytes or path differ")
    return observed


def lines(path):
    with Path(path).open(encoding="utf-8", newline="") as stream:
        for line in stream:
            if not line.strip():
                raise ValueError("retained JSONL has an empty entry")
            yield json.loads(line, object_pairs_hook=unique_object,
                             parse_constant=lambda value: (_ for _ in ()).throw(ValueError("nonfinite JSON value")))


def retain_bytes(owner, standard, original, target):
    with Path(original["path"]).open("rb") as source, target.open("xb") as output:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            output.write(block)
    copied = identity(owner, standard, target, original["sha256"])
    if copied["bytes"] != original["bytes"]:
        raise ValueError("retained parent copy differs")
    return {"source": original, "copy": copied}


def prepare(args, owner, standard):
    source = identity(owner, standard, args.pgn, args.source_sha256)
    if Path(source["path"]).suffix.lower() != ".pgn":
        raise ValueError("selected source must be its original plain PGN")
    selected = identity(owner, standard, args.selection_manifest, args.selection_sha256)
    inventory = identity(owner, standard, args.chunk_manifest, args.chunk_manifest_sha256)
    parent = identity(owner, standard, args.parent_receipt, args.parent_receipt_sha256)
    run_file = identity(owner, standard, args.parent_run_outcome, args.parent_run_outcome_sha256)
    parent_value, run_value = read_json(parent["path"]), read_json(run_file["path"])
    if (parent_value.get("schema") != "laplace.chess-corpus-capacity/v1"
            or parent_value.get("status") not in ("failed", "cancelled")
            or parent_value.get("recordedGamesPerSecond") is not None
            or parent_value.get("qualifiedWindow") is not False
            or parent_value.get("targetMet") is not False):
        raise ValueError("parent must retain its actual failed/unqualified capacity outcome")
    requested = (parent_value.get("options") or {}).get("games")
    if (not integer(requested, 1) or requested < args.expected_games
            or type(parent_value.get("newlyRecordedGames")) is not int
            or parent_value["newlyRecordedGames"] != args.expected_games
            or type(parent_value.get("alreadyPresentGames")) is not int
            or parent_value["alreadyPresentGames"] != 0
            or not integer(parent_value.get("parsedGamesWithoutCompleteChunkEvidence"))):
        raise ValueError("parent does not identify the explicitly requested complete partial count")
    prepared = parent_value.get("source") or {}
    if prepared.get("source") != source or prepared.get("selectionManifest") != selected:
        raise ValueError("parent source/selection identity differs from explicit original inputs")
    if (type(run_value.get("id")) is not int or run_value["id"] != args.parent_run_id
            or run_value.get("status") != "completed"
            or run_value.get("conclusion") not in ("failure", "cancelled", "timed_out")):
        raise ValueError("retained parent run does not report the selected unsuccessful terminal outcome")
    phases = parent_value.get("phases") or []
    if len(phases) != 1 or phases[0].get("status") not in ("failed", "cancelled"):
        raise ValueError("partial parent must contain its failed fresh recording phase")
    fresh = phases[0]
    prior = fresh.get("corpusEvidence") or {}
    if (prior.get("completed") is not False
            or type(prior.get("readbackGames")) is not int or prior["readbackGames"] != args.expected_games
            or type(prior.get("newlyRecordedGames")) is not int or prior["newlyRecordedGames"] != args.expected_games
            or not integer(prior.get("chunks"), 1)
            or (fresh.get("durability") or {}).get("localWalFlushAcknowledged") is not True):
        raise ValueError("parent lacks complete durable chunk evidence for the selected count")
    chunks, inputs, game_bodies = [], [source, selected, inventory, parent, run_file], []
    observed_games, observed_plies = 0, 0
    playing_ids = set()
    for chunk in lines(inventory["path"]):
        if (not isinstance(chunk, dict)
                or type(chunk.get("index")) is not int or chunk["index"] != len(chunks) + 1
                or type(chunk.get("firstSelectedGame")) is not int or chunk["firstSelectedGame"] != observed_games
                or not integer(chunk.get("games"), 1)
                or type(chunk.get("novelGames")) is not int or chunk["novelGames"] != chunk["games"]
                or not integer(chunk.get("plies"), chunk["games"])
                or not hex_value(chunk.get("gameBodiesSha256"), 64)):
            raise ValueError("completed chunk inventory has a gap, duplicate or incomplete range")
        body, scope = (bound_file(owner, standard, chunk[key]) for key in ("body", "scope"))
        value = read_json(body["path"])
        if (value.get("schema") != "laplace.chess-corpus-chunk/v1"
                or type(value.get("index")) is not int or value["index"] != chunk["index"]
                or type(value.get("firstSelectedGame")) is not int or value["firstSelectedGame"] != observed_games
                or type(value.get("newlyRecordedGames")) is not int or value["newlyRecordedGames"] != chunk["games"]
                or not isinstance(value.get("games"), list)
                or len(value["games"]) != chunk["games"]
                or not isinstance(value.get("scopes"), list) or len(value["scopes"]) != 1):
            raise ValueError("complete chunk body differs from its inventory")
        chunk_plies = 0
        for game in value["games"]:
            if (not isinstance(game, dict) or not hex_value(game.get("playingId"), 32)
                    or game["playingId"] in playing_ids or not hex_value(game.get("lineId"), 32)
                    or not hex_value(game.get("startPositionId"), 32)
                    or game.get("result") not in ("1-0", "0-1", "1/2-1/2")
                    or not isinstance(game.get("moveIds"), list) or not game["moveIds"]
                    or any(not hex_value(move, 32) for move in game["moveIds"])):
                raise ValueError("retained complete playing inventory is invalid or duplicated")
            playing_ids.add(game["playingId"])
            game_bodies.append(game)
            chunk_plies += len(game["moveIds"])
        if chunk_plies != chunk["plies"]:
            raise ValueError("retained chunk plies differ from its complete body")
        observed_games += chunk["games"]
        observed_plies += chunk_plies
        if observed_games > args.expected_games:
            raise ValueError("retained complete inventory exceeds the explicit selected count")
        chunks.append(chunk)
        inputs.extend((body, scope))
    if (observed_games != args.expected_games or len(chunks) != prior["chunks"]
            or len(playing_ids) != args.expected_games):
        raise ValueError("retained complete inventory does not cover exactly the explicit selected count")
    original_entries = lines(selected["path"])
    previous_ordinal = 0
    for game in game_bodies:
        entry = next(original_entries, None)
        if (not isinstance(entry, dict) or not integer(entry.get("sourceOrdinal"), previous_ordinal + 1)
                or not hex_value(entry.get("framedGameSha256"), 64)
                or any(entry.get(key) != game[key] for key in ("playingId", "lineId", "startPositionId", "result"))
                or type(entry.get("plies")) is not int or entry["plies"] != len(game["moveIds"])):
            raise ValueError("retained complete playing differs from the original source selection")
        previous_ordinal = entry["sourceOrdinal"]
    original_entries.close()
    manifest = {"schema": "laplace.chess-recorded-selection/v1",
                "source": source, "selectionManifest": selected, "chunks": chunks}
    manifest_path = args.output_dir / "selection.json"
    owner.save(manifest_path, manifest)
    manifest_identity = identity(owner, standard, manifest_path)
    retained = args.output_dir / "retained-parent"
    retained.mkdir()
    parent_copy = retain_bytes(owner, standard, parent, retained / "corpus-recording.json")
    run_copy = retain_bytes(owner, standard, run_file, retained / "run-outcome.json")
    return {
        "manifest": manifest_identity, "source": source, "selectionManifest": selected,
        "chunkManifest": inventory, "selectedGames": observed_games, "readbackPlies": observed_plies,
        "playingIds": [game["playingId"] for game in game_bodies], "inputs": inputs,
        "parent": {"receipt": parent_copy, "runOutcome": run_copy, "runId": args.parent_run_id,
                   "status": parent_value["status"], "conclusion": run_value["conclusion"],
                   "requestedGames": requested,
                   "parsedGamesWithoutCompleteChunkEvidence":
                       parent_value["parsedGamesWithoutCompleteChunkEvidence"],
                   "scope": "Original failed capacity outcome retained verbatim; no success or throughput substitution"},
    }


def verification_arguments(selection, output, seconds):
    return ["bash", str(ROOT / "scripts/laplace"), "chess", "verify-recorded-corpus",
            "--selection-manifest", selection["manifest"]["path"],
            "--expected-sha256", selection["manifest"]["sha256"],
            "--evidence-root", str(output / "verification"), "--deadline-seconds", str(seconds)]


def evidence_identity(owner, standard, value):
    return bound_file(owner, standard, value)


def summary(receipt, selection, owner, standard):
    count, plies = selection["selectedGames"], selection["readbackPlies"]
    if (receipt.get("schema") != "laplace.chess-recorded-corpus-verification/v1"
            or receipt.get("status") != "completed" or receipt.get("retainedScopeReplayVerified") is not True
            or any(type(receipt.get(key)) is not int or receipt[key] != expected for key, expected in (
                ("selectedGames", count), ("readbackGames", count), ("readbackPlies", plies)))
            or any(receipt.get(key) != selection[key] for key in ("manifest", "source", "selectionManifest", "playingIds"))):
        raise ValueError("independent verification does not bind the exact complete selected games")
    baseline = receipt.get("retainedBaseline") or {}
    if (baseline.get("completed") is not True
            or type(baseline.get("readbackGames")) is not int or baseline["readbackGames"] != count
            or type(baseline.get("newlyRecordedGames")) is not int or baseline["newlyRecordedGames"] != count or not integer(baseline.get("chunks"), 1)):
        raise ValueError("selected retained baseline is not complete")
    evidence_identity(owner, standard, baseline["chunkManifest"])
    state = baseline.get("exactScopeState") or {}
    baseline_file = evidence_identity(owner, standard, state["file"])
    if not integer(state.get("rows"), 1):
        raise ValueError("selected retained scope is empty")
    phases = receipt.get("phases") or []
    if len(phases) != 2 or [phase.get("name") for phase in phases] != ["readback", "replay"]:
        raise ValueError("current readback and independent replay are both required")
    for phase in phases:
        recording = phase.get("recording") or {}
        writer = recording.get("writer") or {}
        verification = recording.get("verification") or {}
        corpus = recording.get("corpusSource") or {}
        evidence = recording.get("corpusEvidence") or {}
        if (recording.get("schema") != "laplace.chess-recording/v2"
                or recording.get("status") != "completed"
                or any(type(recording.get(key)) is not int or recording[key] != count
                       for key in ("requestedGames", "parsedGames", "committedGames", "readbackGames"))
                or type(recording.get("readbackPlies")) is not int or recording["readbackPlies"] != plies
                or any(type(recording.get(key)) is not int or recording[key] != 0
                       for key in ("novelGames", "appliedGames"))
                or recording.get("durability") is not None
                or any(type(writer.get(key)) is not int or writer[key] != 0 for key in COUNTERS)
                or any(verification.get(key) is not True for key in (
                    "uniquePlayingIds", "exactGameBodies", "exactWitnessMembership", "completedGames"))
                or verification.get("exactExperimentBody") is not False
                or corpus.get("source") != selection["source"]
                or corpus.get("selectionManifest") != selection["selectionManifest"]
                or evidence.get("completed") is not True
                or type(evidence.get("readbackGames")) is not int or evidence["readbackGames"] != count
                or type(evidence.get("newlyRecordedGames")) is not int or evidence["newlyRecordedGames"] != 0
                or type(evidence.get("chunks")) is not int or evidence["chunks"] != baseline["chunks"]):
            raise ValueError("selected verification phase lacks exact readback or zero-amplification proof")
        evidence_identity(owner, standard, evidence["chunkManifest"])
        observed = evidence.get("exactScopeState") or {}
        actual_file = evidence_identity(owner, standard, observed["file"])
        if (type(observed.get("rows")) is not int or observed["rows"] != state["rows"]
                or any(actual_file[key] != baseline_file[key] for key in ("bytes", "sha256"))):
            raise ValueError("selected entity/physicality/attestation scope changed")
    return {"status": "completed", "selectedGames": count, "readbackGames": count,
            "readbackPlies": plies, "retainedScopeReplayVerified": True}


def recording_snapshot(path, guard):
    value = read_json(path)
    if type(value.get("format")) is not int or value["format"] != 3 or value.get("purpose") != "recording":
        raise ValueError("selected verification requires actual FORMAT3 recording snapshots")
    guard.database_incarnation(value["database"])
    return value


def run(args, owner=None, standard=None, guard=None):
    standard = standard or module("recorded_selection_standard", "measure-installed-chess-corpus.py")
    owner = owner or standard.acceptance_owner()
    guard = guard or module("recorded_selection_runtime", "check-application-runtime.py")
    args.output_dir.mkdir(parents=True, exist_ok=False)
    proof = {"schema": SCHEMA, "status": "failed", "phases": [],
             "startedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
             "scope": "Exact completed recorded subset; source-built CLI and installed runtime observed under the caller's host reservation. Parent benchmark status is independent."}
    selection, before_cli, before_native = None, None, None

    def checkpoint():
        owner.save(args.output_dir / "receipt.json", proof)

    def phase(name, action):
        row = {"name": name, "status": "running"}
        proof["phases"].append(row)
        checkpoint()
        started = time.monotonic()
        print("RECORDED_SELECTION_PHASE " + json.dumps(row), flush=True)
        try:
            result = action()
            row["status"] = "passed"
            return result
        except BaseException as error:
            row.update(status="failed", errorType=type(error).__name__)
            raise
        finally:
            row["wallSeconds"] = time.monotonic() - started
            checkpoint()
            print("RECORDED_SELECTION_PHASE " + json.dumps(row), flush=True)

    def command(name, argv, timeout):
        return phase(name, lambda: owner.command(argv, args.output_dir / (name + ".log"),
                                                timeout, cleanup_seconds=30))
    try:
        proof["sourceSha"] = phase("source", lambda: standard.exact_source(args.expected_source))
        selection = phase("selection", lambda: prepare(args, owner, standard))
        proof["selection"] = {key: value for key, value in selection.items() if key != "inputs"}
        proof["databaseTarget"] = phase("database-target", standard.database_target)
        command("native-before", [sys.executable, ROOT / "scripts/check-application-runtime.py",
                "--purpose", "recording", "--snapshot", args.output_dir / "native-before.json"], 240)
        before_native = phase("native-before-receipt", lambda: recording_snapshot(
            args.output_dir / "native-before.json", guard))
        proof["buildIdentity"] = before_native["build"]
        proof["databaseIncarnation"] = dict(zip(("systemIdentifier", "databaseOid"),
                                               guard.database_incarnation(before_native["database"])))
        before_cli = phase("cli-identity", lambda: standard.cli_identity(owner))
        owner.save(args.output_dir / "cli-before.json", before_cli)
        command("verification", verification_arguments(selection, args.output_dir, args.deadline_seconds),
                args.deadline_seconds + 30)
        proof["verification"] = phase("verification-receipt", lambda: summary(read_json(
            args.output_dir / "verification/recorded-verification.json"), selection, owner, standard))
        proof["status"] = "completed"
    except KeyboardInterrupt:
        proof.update(status="interrupted", errorType="KeyboardInterrupt")
    except Exception as error:
        proof.update(status="failed", errorType=type(error).__name__, error=str(error))
    finally:
        def postflight(name, action):
            try:
                phase(name, action)
            except BaseException as error:
                proof.update(status="failed")
                proof.setdefault("postflightErrors", []).append({"phase": name, "errorType": type(error).__name__})
        if before_cli is not None:
            def cli_after():
                after = standard.cli_identity(owner)
                owner.save(args.output_dir / "cli-after.json", after)
                if after != before_cli:
                    raise ValueError("source-built CLI/native closure changed during selected verification")
            postflight("cli-after", cli_after)
        postflight("source-after", lambda: standard.exact_source(args.expected_source))
        if selection is not None:
            def retained_after():
                for value in [*selection["inputs"], selection["manifest"],
                              selection["parent"]["receipt"]["copy"], selection["parent"]["runOutcome"]["copy"]]:
                    bound_file(owner, standard, value)
            postflight("retained-inputs-after", retained_after)
        if before_native is not None:
            def native_after():
                owner.command([sys.executable, ROOT / "scripts/check-application-runtime.py",
                    "--purpose", "recording", "--compare", args.output_dir / "native-before.json",
                    "--snapshot", args.output_dir / "native-after.json"],
                    args.output_dir / "native-after.log", 240, cleanup_seconds=30)
                after = recording_snapshot(args.output_dir / "native-after.json", guard)
                if not guard.compatible(before_native, after, purpose="recording"):
                    raise ValueError("database incarnation or installed runtime changed during selected verification")
            postflight("native-after", native_after)
        proof["finishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        checkpoint()
        print("RECORDED_SELECTION_RESULT " + json.dumps({
            "status": proof["status"], "selectedGames": selection["selectedGames"] if selection else None,
            "parentStatus": selection["parent"]["status"] if selection else None}), flush=True)
    return 0 if proof["status"] == "completed" else 1


@contextmanager
def interruption_scope():
    previous = {number: signal.getsignal(number) for number in (signal.SIGTERM, signal.SIGINT)}
    def interrupted(signum, frame):
        for number in previous:
            signal.signal(number, signal.SIG_IGN)
        raise KeyboardInterrupt("recorded selection verification interrupted")
    try:
        for number in previous:
            signal.signal(number, interrupted)
        yield
    finally:
        for number, handler in previous.items():
            signal.signal(number, handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--pgn", type=Path, required=True)
    parser.add_argument("--source-sha256", required=True)
    parser.add_argument("--selection-manifest", type=Path, required=True)
    parser.add_argument("--selection-sha256", required=True)
    parser.add_argument("--chunk-manifest", type=Path, required=True)
    parser.add_argument("--chunk-manifest-sha256", required=True)
    parser.add_argument("--expected-games", type=int, required=True)
    parser.add_argument("--parent-receipt", type=Path, required=True,
                        help="Original failed measurement/corpus-recording.json, retained byte-for-byte")
    parser.add_argument("--parent-receipt-sha256", required=True)
    parser.add_argument("--parent-run-outcome", type=Path, required=True)
    parser.add_argument("--parent-run-outcome-sha256", required=True)
    parser.add_argument("--parent-run-id", type=int, required=True)
    parser.add_argument("--deadline-seconds", type=int, default=3600)
    args = parser.parse_args()
    if (not hex_value(args.expected_source, 40)
            or any(not path.is_absolute() for path in (args.output_dir, args.pgn, args.selection_manifest,
                args.chunk_manifest, args.parent_receipt, args.parent_run_outcome))
            or any(not hex_value(value, 64) for value in (args.source_sha256, args.selection_sha256,
                args.chunk_manifest_sha256, args.parent_receipt_sha256, args.parent_run_outcome_sha256))
            or not 1 <= args.expected_games <= 1000000 or args.parent_run_id < 1
            or not 30 <= args.deadline_seconds <= 86400):
        parser.error("explicit absolute inputs, exact hashes, positive count/run and supported deadline are required")
    with interruption_scope():
        return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
