#!/usr/bin/env python3
"""Measure normal retained PGN admission and exact replay independently of play.

The existing Chess Lab match, native ingestor and readback own game semantics.
This collector verifies their retained receipts and never parses chess moves.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import signal
import sys
import time

spec = importlib.util.spec_from_file_location("recorded_chess_transport",
    Path(__file__).with_name("benchmark-recorded-chess.py"))
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)
require, save_json = transport.require, transport.save_json
MAX_FAILURE_EVIDENCE_BYTES = 16 << 20


class DeadlineClient(transport.Client):
    """All play, admission, replay and artifact requests share one deadline."""
    def __init__(self, base, timeout, deadline):
        super().__init__(base, timeout)
        self.deadline = deadline

    def remaining(self):
        remaining = self.deadline - time.monotonic()
        require(remaining > 0, "whole-run measurement deadline exceeded")
        return remaining

    def request(self, path, data=None, raw=False, maximum_bytes=transport.MAX_BYTES):
        previous = self.timeout
        self.timeout = min(previous, self.remaining())
        try:
            result = super().request(path, data, raw, maximum_bytes)
            self.remaining()
            return result
        finally:
            self.timeout = previous

    def stop_owned(self, job_id):
        # Bounded cleanup may run after the measurement deadline. It never starts
        # or repeats admission, and it can only stop this collector's own match.
        previous = self.timeout
        self.timeout = min(previous, 5.0)
        try:
            return super().request("/chess/lab/stop/" + job_id, {})
        finally:
            self.timeout = previous


def positive(value, name):
    require(type(value) in {int, float} and math.isfinite(value) and value > 0,
            f"{name} must be finite and positive")
    return value


def native_id(value):
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{32}", value), "invalid native identity")
    return value


def ingestion_artifacts(job, job_id):
    require(isinstance(job, dict) and job.get("id") == job_id, "ingestion job identity differs")
    artifacts = job.get("artifacts")
    require(isinstance(artifacts, dict), "ingestion job artifact inventory is absent")
    names = {name for name in artifacts if re.fullmatch(r"ingest-[0-9a-f]{32}\.json", name)}
    require(len(names) <= 32, "ingestion receipt inventory exceeds service retention bound")
    return names


def request_ingestion(client, output, report, job_id, index):
    # The normal service writes an ingest receipt in finally even when the POST
    # fails. Collect it by the existing artifact API, not the HTTP error body.
    before = client.request("/chess/lab/jobs/" + job_id,
                            maximum_bytes=transport.MAX_JOB_METADATA_BYTES)
    prior = ingestion_artifacts(before, job_id)
    save_json(output / f"ingest-{index}-before.json", before)
    try:
        return client.request(f"/chess/lab/jobs/{job_id}/ingest", {})
    except (ValueError, OSError, TimeoutError):
        failure = {"attemptIndex": index, "artifacts": [],
                   "scope": "New retained service receipts observed after the failed POST; concurrent invocations are not attributed to this request."}
        report["failedIngestion"] = failure
        try:
            after = client.request("/chess/lab/jobs/" + job_id,
                                   maximum_bytes=transport.MAX_JOB_METADATA_BYTES)
            save_json(output / f"ingest-{index}-after-failure.json", after)
            names = ingestion_artifacts(after, job_id) - prior
            remaining = MAX_FAILURE_EVIDENCE_BYTES
            for name in sorted(names):
                require(remaining > 0, "failed ingestion evidence byte envelope exhausted")
                payload = client.request(f"/chess/lab/jobs/{job_id}/artifact/{name}",
                                         raw=True, maximum_bytes=remaining)
                remaining -= len(payload)
                (output / name).write_bytes(payload)
                retained = {"artifact": name, "bytes": len(payload),
                            "sha256": hashlib.sha256(payload).hexdigest()}
                failure["artifacts"].append(retained)
                receipt = json.loads(payload)
                require(isinstance(receipt, dict)
                        and receipt.get("schema") == "laplace.chess-retained-ingestion/v1"
                        and receipt.get("jobId") == job_id, "failed ingestion artifact identity differs")
                recording = receipt.get("recording")
                require(recording is None or isinstance(recording, dict), "failed recording receipt has invalid shape")
                retained["serviceStatus"] = receipt.get("status")
                retained["serviceError"] = receipt.get("error")
                retained["recordingStatus"] = recording.get("status") if recording is not None else None
            failure["collectionCompleted"] = True
        except (ValueError, KeyError, TypeError, AttributeError, OSError, TimeoutError) as recovery:
            failure["collectionCompleted"] = False
            failure["collectionError"] = str(recovery) if isinstance(recovery, ValueError) else type(recovery).__name__
        raise


def scope_rows(rows):
    require(isinstance(rows, list), "scope row inventory missing")
    result = {}
    for row in rows:
        kind, count = row.get("kind"), row.get("observationCount")
        require(type(kind) is int and kind in (1, 2, 3) and type(count) is int
                and (count > 0 if kind == 3 else count == 0), "invalid scoped row/count")
        key = kind, native_id(row.get("id"))
        require(key not in result, "duplicate scoped row identity")
        result[key] = count
    return result


def validate(receipt, experiment, experiment_bytes, pgn, job_id, games, elapsed, replay, previous=None):
    require(receipt.get("schema") == "laplace.chess-retained-ingestion/v1"
            and receipt.get("jobId") == job_id, "retained ingestion receipt identity differs")
    recording = receipt.get("recording")
    require(isinstance(recording, dict)
            and recording.get("schema") == "laplace.chess-recording/v2"
            and recording.get("purpose") == "retained-pgn-ingestion"
            and recording.get("status") == "completed", "retained native readback did not complete")
    require(recording.get("experimentId") == experiment.get("experimentId") == job_id
            and recording.get("pgnEvent") == experiment.get("pgnEvent") == "chess-lab/cutechess/" + job_id,
            "experiment identity changed between play and admission")
    require(recording.get("experimentArtifactSha256") == hashlib.sha256(experiment_bytes).hexdigest()
            and re.fullmatch(r"[0-9a-f]{64}", recording.get("experimentReceiptSha256", "")),
            "retained experiment bytes do not match committed provenance")
    require(recording.get("pgn") == {"bytes": len(pgn), "sha256": hashlib.sha256(pgn).hexdigest()},
            "retained PGN bytes changed")
    require(experiment.get("matchState", "").lower() == "completed"
            and experiment.get("artifactIdentitiesUnchanged") is True, "original match evidence is incomplete")
    require(not any(arg in ("-maxmoves", "-draw", "-resign")
                    for arg in experiment.get("command", {}).get("arguments", [])),
            "artificial game cutoff cannot establish full normal game throughput")
    require(all(recording.get("verification", {}).get(key) is True for key in
            ("uniquePlayingIds", "exactGameBodies", "exactWitnessMembership", "exactExperimentBody", "completedGames")),
            "exact completed-game readback verification is absent")
    for field in ("requestedGames", "parsedGames", "committedGames", "readbackGames"):
        require(type(recording.get(field)) is int and recording[field] == games, f"invalid {field}")
    bodies = recording.get("games")
    require(isinstance(bodies, list) and len(bodies) == games, "native game identities are absent")
    plies = 0
    playing_ids = set()
    for game in bodies:
        for field in ("playingId", "lineId", "startPositionId", "whitePlayerId", "blackPlayerId"):
            native_id(game.get(field))
        playing_ids.add(game["playingId"])
        require(game.get("result") in ("1-0", "0-1", "1/2-1/2"), "incomplete game result")
        require(isinstance(game.get("moveIds"), list), "ordered native move inventory missing")
        for move in game["moveIds"]:
            native_id(move)
        plies += len(game["moveIds"])
    require(len(playing_ids) == games and recording.get("readbackPlies") == plies, "game/ply inventory differs")
    service = positive(receipt.get("serviceElapsedSeconds"), "service duration")
    positive(elapsed, "collector duration")
    require(service <= elapsed + .1, "service work exceeds observed HTTP boundary")
    times = recording.get("elapsedSeconds", {})
    for key in ("total", "play", "recording", "commit", "readback", "overhead"):
        value = times.get(key)
        require(type(value) in (float, int) and math.isfinite(value) and value >= 0, f"invalid {key} duration")
    require(times["play"] == 0 and times["total"] <= service + .1
            and sum(times[k] for k in ("recording", "commit", "readback", "overhead")) <= times["total"] + .001,
            "retained operation timing includes play or overlaps phases")
    writer = recording.get("writer", {})
    writer_fields = ("applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted",
        "physicalitiesInserted", "attestationsAttempted", "attestationsInserted", "entitiesSkippedAtMerge",
        "physicalitiesSkippedAtMerge", "roundTrips", "journalReplayHits",
        "copyTransactionsStarted", "copyTransactionsCommitted")
    for key in writer_fields:
        require(type(writer.get(key)) is int and writer[key] >= 0, f"invalid writer count {key}")
    require(writer.get("roundTripsKind") == "logical-writer-accounting/v1", "writer call accounting scope is absent")
    scopes = recording.get("replayScopes")
    require(isinstance(scopes, list) and scopes, "actual pre/post scope snapshots are absent")
    union_after = {}
    for scope in scopes:
        expected = set()
        for kind, name in ((1, "entityIds"), (2, "physicalityIds"), (3, "witnessIds")):
            require(isinstance(scope.get(name), list), "declared measurement scope missing")
            for identity in scope[name]:
                key = kind, native_id(identity)
                require(key not in expected, "duplicate declared scope identity")
                expected.add(key)
        before, after = scope_rows(scope.get("before")), scope_rows(scope.get("after"))
        require(set(after) == expected and set(before) <= expected, "committed scope coverage differs")
        require(type(scope.get("unchanged")) is bool and scope["unchanged"] == (before == after),
                "scope unchanged flag contradicts actual identities/observation counts")
        if replay:
            require(before == after, "exact replay grew or changed scoped testimony")
        for key, count in after.items():
            require(key not in union_after or union_after[key] == count, "overlapping chunk observations differ")
            union_after[key] = count
    expected_new = 0 if replay else games
    require(all(type(value) is int for value in (recording.get("novelGames"), recording.get("appliedGames"),
                receipt.get("newlyRecordedGames"), receipt.get("alreadyPresentGames"))), "invalid admission/replay counters")
    require(recording.get("novelGames") == recording.get("appliedGames") == receipt.get("newlyRecordedGames") == expected_new
            and receipt.get("alreadyPresentGames") == (games if replay else 0), "parsed replays were counted as new games")
    require(receipt.get("disposition") == ("replay" if replay else "fresh")
            and receipt.get("noOpReplayVerified") is replay, "operation disposition differs from requested measurement")
    if replay:
        require(all(writer[key] == 0 for key in writer_fields),
                "exact replay performed a new measured writer operation")
        require(recording.get("durability") is None, "no-op replay fabricated a fresh commit acknowledgement")
        require(previous is not None and previous["games"] == bodies
                and previous["scope"] == union_after, "replay changed canonical game identities or selected testimony")
    else:
        durability = recording.get("durability", {})
        require(writer["applyCalls"] > 0 and isinstance(durability, dict)
                and writer["copyTransactionsStarted"] == writer["copyTransactionsCommitted"] > 0
                and durability.get("synchronousCommit") == "on"
                and all(durability.get(k) is True for k in ("fsync", "fullPageWrites", "writeCommitAcknowledged", "localWalFlushAcknowledged")),
                "fresh admission lacks synchronous PostgreSQL acknowledgement")
    return {"metrics": {"parsedGames": games, "newlyRecordedGames": expected_new,
            "pliesReadback": plies, "serviceElapsedSeconds": service, "collectorElapsedSeconds": elapsed,
            "newlyRecordedGamesPerSecondService": expected_new / service,
            "processedGamesPerSecondService": games / service,
            "processedGamesPerSecondCollector": games / elapsed,
            "activePhaseSeconds": times}, "games": bodies, "scope": union_after}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--api-base", default=transport.default_api_base())
    parser.add_argument("--games", type=int, default=16)
    parser.add_argument("--depth", type=int, default=4)
    parser.add_argument("--concurrency", type=int, default=1)
    parser.add_argument("--replays", type=int, default=2)
    parser.add_argument("--timeout-seconds", type=float, default=900)
    parser.add_argument("--minimum-service-seconds", type=float, default=1)
    args = parser.parse_args(argv)
    overall_started = time.monotonic()
    args.output_dir.mkdir(parents=True, exist_ok=False)
    report = {"schema": "laplace.retained-chess-ingestion-benchmark/v1", "status": "failed",
              "startedAt": datetime.now(timezone.utc).isoformat(), "attempts": [],
              "scope": "Normal completed retained PGN through ordinary API/shared writer/native readback; generation measured separately. Replay processing is not new recording throughput."}
    job_id, terminal = None, False
    client = None
    prior_sigterm = signal.getsignal(signal.SIGTERM)
    def interrupted(_signal, _frame):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupted)
    try:
        require(args.replays >= 1, "at least one exact replay is required")
        positive(args.timeout_seconds, "timeout")
        positive(args.minimum_service_seconds, "minimum service duration")
        client = DeadlineClient(args.api_base, args.timeout_seconds, overall_started + args.timeout_seconds)
        report["deadlineSeconds"] = args.timeout_seconds
        report["deadlineScope"] = "One measurement deadline for generation, every admission/replay, and all artifact downloads; stopping this collector's unfinished match has a separate maximum five-second cleanup timeout."
        request = transport.game_request(args.games, args.depth, args.concurrency, 1, 16)
        request["config"]["ingest"] = False
        save_json(args.output_dir / "request.json", request)
        play_started = time.monotonic()
        start = client.request("/chess/lab/start", request)
        job_id = start.get("jobId")
        require(isinstance(job_id, str) and re.fullmatch(r"[0-9a-f]{32}", job_id), "unsafe returned job identity")
        report["jobId"] = job_id
        save_json(args.output_dir / "start.json", start)
        while True:
            client.remaining()
            job = client.request("/chess/lab/jobs/" + job_id)
            require(job.get("id") == job_id, "polled job identity differs")
            save_json(args.output_dir / "job.json", job)
            if job.get("state", "").lower() in transport.TERMINAL:
                terminal = True
                break
            time.sleep(min(.25, client.remaining()))
        report["playAndRetentionCollectorSeconds"] = time.monotonic() - play_started
        require(job.get("state", "").lower() == "completed", "normal match failed")
        for name in ("games.pgn", "experiment.json", "recording.json", "transcript.log"):
            payload = client.request(f"/chess/lab/jobs/{job_id}/artifact/{name}", raw=True)
            (args.output_dir / name).write_bytes(payload)
        pgn = (args.output_dir / "games.pgn").read_bytes()
        experiment_bytes = (args.output_dir / "experiment.json").read_bytes()
        experiment = json.loads(experiment_bytes)
        require(experiment.get("ingested") is None, "generated corpus was already auto-ingested")
        options = experiment.get("requestedOptions", {})
        require(options.get("rounds") == args.games and options.get("depth") == args.depth
                and options.get("concurrency") == args.concurrency and options.get("stockfishThreads") == 1
                and options.get("stockfishHashMb") == 16 and options.get("stockfishLimitStrength") is False,
                "actual completed match settings differ")
        baseline = None
        for index in range(args.replays + 1):
            started = time.monotonic()
            response = request_ingestion(client, args.output_dir, report, job_id, index)
            save_json(args.output_dir / f"ingest-{index}-response.json", response)
            artifact = response.get("measurementArtifact")
            require(isinstance(artifact, str) and re.fullmatch(r"ingest-[0-9a-f]{32}\.json", artifact), "measured ingestion artifact missing")
            raw = client.request(f"/chess/lab/jobs/{job_id}/artifact/{artifact}", raw=True)
            (args.output_dir / artifact).write_bytes(raw)
            elapsed = time.monotonic() - started
            validated = validate(json.loads(raw), experiment, experiment_bytes, pgn, job_id,
                args.games, elapsed, replay=index > 0, previous=baseline)
            if baseline is None:
                baseline = validated
            report["attempts"].append({"kind": "admission" if index == 0 else "replay",
                "artifact": artifact, "sha256": hashlib.sha256(raw).hexdigest(), **validated["metrics"]})
        report["minimumAdmissionDurationReached"] = report["attempts"][0]["serviceElapsedSeconds"] >= args.minimum_service_seconds
        report["minimumServiceSeconds"] = args.minimum_service_seconds
        report["sustainedAdmissionCapacityEstablished"] = False
        report["status"] = "passed"
    except (ValueError, KeyError, TypeError, OSError, TimeoutError, KeyboardInterrupt) as error:
        report["error"] = str(error) if isinstance(error, ValueError) else type(error).__name__
        if job_id and not terminal:
            try:
                report["stopOwnedMatch"] = client.stop_owned(job_id)
            except (ValueError, OSError):
                report["stopOwnedMatch"] = "failed"
    finally:
        report["collectorWallSecondsIncludingCleanup"] = time.monotonic() - overall_started
        signal.signal(signal.SIGTERM, prior_sigterm)
        save_json(args.output_dir / "receipt.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
