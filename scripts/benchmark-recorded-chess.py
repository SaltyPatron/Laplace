#!/usr/bin/env python3
"""Measure complete Chess Lab matches through committed, exact game readback.

Uses the ordinary API and its normal writer. Native chess code owns parsing,
identity and reconstruction. This collector verifies the application receipts;
it does not implement another chess parser or manufacture substrate identities.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import signal
import statistics
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

MAX_BYTES = 256 << 20
TERMINAL = {"completed", "failed", "cancelled"}
RATE_BOUNDARIES = {
    "gamesPerSecondServerWorkflow": "server job entry through full play, recording, shared writer completion and exact readback; includes engine startup and job overhead; excludes API queue, final recording receipt serialization and artifact transfer",
    "gamesPerSecondEndToEnd": "collector start request through observed completion and artifact download; includes queue, polling delay and transfer",
    "gamesPerSecondRecordingAndReadback": "parse, prepare, novelty, calculated lanes, shared writer completion and exact readback; excludes play and job overhead",
    "gamesPerSecondRecording": "parse, prepare, novelty, calculated lanes and shared writer completion; excludes play, readback and job overhead",
}
TARGET_METRIC = "gamesPerSecondServerWorkflow"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def save_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


class Client:
    def __init__(self, base: str, timeout: float):
        parsed = urllib.parse.urlsplit(base)
        require(parsed.scheme in {"http", "https"} and bool(parsed.hostname)
                and not parsed.username and not parsed.password and not parsed.query
                and not parsed.fragment, "API base must be an HTTP(S) URL without credentials or query")
        self.base, self.timeout = base.rstrip("/"), timeout
        self.headers = {"Content-Type": "application/json",
                        "X-Laplace-Tenant": os.environ.get("LAPLACE_PROOF_TENANT", "ci")}
        for variable, header in (("LAPLACE_API_KEY", "Authorization"),
                                 ("LAPLACE_QUOTE_ID", "X-Laplace-Quote-Id")):
            if os.environ.get(variable):
                self.headers[header] = ("Bearer " if header == "Authorization" else "") + os.environ[variable]

    def request(self, path: str, data: object = None, raw: bool = False):
        request = urllib.request.Request(self.base + path, headers=self.headers,
            data=None if data is None else json.dumps(data).encode("utf-8"),
            method="GET" if data is None else "POST")
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                payload = response.read(MAX_BYTES + 1)
                require(len(payload) <= MAX_BYTES, "API artifact exceeds collector byte envelope")
        except urllib.error.HTTPError as error:
            # Server bodies and authentication headers never enter benchmark logs.
            raise ValueError(f"API {path} returned HTTP {error.code}") from None
        return payload if raw else json.loads(payload)


def game_request(games: int, depth: int, concurrency: int, threads: int, hash_mb: int) -> dict:
    require(games >= 2 and games % 2 == 0, "paired matches require a positive even game count")
    require(min(depth, concurrency, threads, hash_mb) > 0, "depth and resource settings must be positive")
    return {"kind": "cutechess", "config": {
        "rounds": games, "depth": depth, "concurrency": concurrency,
        "limitStrength": False, "stockfishThreads": threads, "stockfishHashMb": hash_mb,
        "ingest": True, "persistPgn": True, "persistTranscript": True}}


def validate_recording(recording: dict, experiment: dict, job: dict, request: dict,
                       pgn: bytes, experiment_bytes: bytes, elapsed: float) -> dict:
    count = request["config"]["rounds"]
    job_id = job["id"]
    require(job.get("state", "").lower() == "completed", "application job did not complete")
    require(recording.get("schema") == "laplace.chess-recording/v1"
            and recording.get("status") == "completed", "committed recording receipt is missing or failed")
    require(recording.get("experimentId") == experiment.get("experimentId") == job_id
            and recording.get("pgnEvent") == experiment.get("pgnEvent") == "chess-lab/cutechess/" + job_id,
            "job, match and recording provenance differ")
    require(experiment.get("matchState", "").lower() == "completed"
            and experiment.get("ingested") is True
            and experiment.get("artifactIdentitiesUnchanged") is True,
            "match or executable identity verification did not complete")
    options = experiment.get("requestedOptions", {})
    require(options.get("rounds") == count and options.get("depth") == request["config"]["depth"]
            and options.get("concurrency") == request["config"]["concurrency"]
            and options.get("stockfishThreads") == request["config"]["stockfishThreads"]
            and options.get("stockfishHashMb") == request["config"]["stockfishHashMb"]
            and options.get("stockfishLimitStrength") is False,
            "actual match settings differ from the requested experiment")
    arguments = experiment.get("command", {}).get("arguments", [])
    require(isinstance(arguments, list) and "tc=inf" in arguments
            and f"depth={options['depth']}" in arguments,
            "matched-depth command was not retained")
    require(not any(str(arg).lower() in {"-maxmoves", "-draw", "-resign"}
                    for arg in arguments), "move cutoff or adjudication cannot establish complete-game throughput")
    for field in ("requestedGames", "parsedGames", "novelGames", "appliedGames", "committedGames", "readbackGames"):
        require(type(recording.get(field)) is int and recording[field] == count,
                f"{field} does not prove all requested games were newly recorded")
    verification = recording.get("verification", {})
    for field in ("uniquePlayingIds", "exactGameBodies", "exactWitnessMembership", "exactExperimentBody", "completedGames"):
        require(verification.get(field) is True, f"independent readback failed: {field}")
    require(recording.get("pgn") == {"bytes": len(pgn), "sha256": hashlib.sha256(pgn).hexdigest()},
            "downloaded PGN differs from the recorded input")
    require(re.fullmatch(r"[0-9a-f]{64}", recording.get("experimentReceiptSha256", "")) is not None,
            "canonical recorded experiment fingerprint is absent")
    require(recording.get("experimentArtifactSha256") == hashlib.sha256(experiment_bytes).hexdigest(),
            "downloaded experiment differs from the artifact bound by committed readback")
    bodies = recording.get("games", [])
    require(isinstance(bodies, list) and len(bodies) == count, "complete game readback bodies are absent")
    playing_ids, plies = set(), 0
    for game in bodies:
        for field in ("playingId", "lineId", "startPositionId", "whitePlayerId", "blackPlayerId"):
            require(re.fullmatch(r"[0-9a-fA-F]{32}", game.get(field, "")) is not None,
                    f"readback lacks native {field}")
        playing_ids.add(game["playingId"].lower())
        require(game.get("result") in {"1-0", "0-1", "1/2-1/2"}, "readback contains an unfinished game")
        moves = game.get("moveIds")
        require(isinstance(moves, list) and all(isinstance(move, str)
                and re.fullmatch(r"[0-9a-fA-F]{32}", move) for move in moves),
                "readback lacks ordered native move identities")
        plies += len(moves)
    require(len(playing_ids) == count and recording.get("readbackPlies") == plies,
            "readback game uniqueness or exact ply count differs")
    times = recording.get("elapsedSeconds", {})
    for field in ("total", "play", "recording", "commit", "readback", "overhead"):
        value = times.get(field)
        require(type(value) in {int, float} and math.isfinite(value) and value >= 0,
                f"invalid measured duration: {field}")
    require(math.isfinite(elapsed) and elapsed > 0 and times["total"] > 0,
            "positive measured total duration is required")
    require(times["total"] <= elapsed + 0.1, "server work exceeds collector measurement boundary")
    require(sum(times[field] for field in ("play", "recording", "commit", "readback", "overhead"))
            <= times["total"] + max(.001, times["total"] * .001),
            "exclusive stage durations exceed total measured work")
    writer = recording.get("writer", {})
    for field in ("applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted",
                  "physicalitiesInserted", "attestationsAttempted", "attestationsInserted",
                  "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge", "roundTrips", "journalReplayHits"):
        require(type(writer.get(field)) is int and writer[field] >= 0, f"missing writer work counter: {field}")
    record_wall = times["recording"] + times["commit"]
    verified_record_wall = record_wall + times["readback"]
    return {"verifiedRecordedGames": count, "verifiedRecordedPlies": plies,
            "gamesPerSecondServerWorkflow": count / times["total"],
            "pliesPerSecondServerWorkflow": plies / times["total"],
            "gamesPerSecondEndToEnd": count / elapsed,
            "pliesPerSecondEndToEnd": plies / elapsed,
            "gamesPerSecondRecordingAndReadback": count / verified_record_wall if verified_record_wall > 0 else None,
            "gamesPerSecondRecording": count / record_wall if record_wall > 0 else None,
            "rateBoundaries": RATE_BOUNDARIES,
            "elapsedSeconds": {"collectorEndToEnd": elapsed, **times}, "writer": writer}


def summarize_rates(cases: list[dict], concurrencies: list[int], target: float) -> dict:
    medians = {}
    for metric in RATE_BOUNDARIES:
        medians[metric] = {}
        for concurrency in concurrencies:
            selected = [case for case in cases if case["concurrency"] == concurrency]
            require(selected and all(case["status"] == "passed" for case in selected),
                    "rate summary requires successful recorded readback for every case")
            values = [case[metric] for case in selected]
            medians[metric][str(concurrency)] = (
                statistics.median(values) if all(value is not None for value in values) else None)
    return {"medianRatesByConcurrency": medians,
            "targetMetric": TARGET_METRIC,
            "targetMet": max(medians[TARGET_METRIC].values()) >= target,
            "collectorEndToEndTargetMet": max(medians["gamesPerSecondEndToEnd"].values()) >= target}


def run_case(client: Client, directory: Path, request: dict, timeout: float, poll: float) -> dict:
    directory.mkdir(parents=True, exist_ok=False)
    save_json(directory / "request.json", request)
    started, job_id = time.monotonic(), None
    result = {"status": "failed", "startedAt": datetime.now(timezone.utc).isoformat()}
    job = None
    owned_terminal = False
    try:
        response = client.request("/chess/lab/start", request)
        job_id = response.get("jobId")
        require(isinstance(job_id, str) and re.fullmatch(r"[a-zA-Z0-9_-]+", job_id), "API did not return a safe job identity")
        result["jobId"] = job_id
        save_json(directory / "start.json", response)
        while True:
            require(time.monotonic() - started < timeout, "application match timed out")
            job = client.request("/chess/lab/jobs/" + job_id)
            require(job.get("id") == job_id, "polled job differs from the started experiment")
            save_json(directory / "job.json", job)
            if job.get("state", "").lower() in TERMINAL:
                owned_terminal = True
                break
            time.sleep(poll)
        # Retain failure evidence too; success is established only below.
        artifacts = {}
        for name in ("experiment.json", "recording.json", "games.pgn", "transcript.log"):
            if name not in job.get("artifacts", {}):
                continue
            path = f"/chess/lab/jobs/{job_id}/artifact/{name}"
            try:
                payload = client.request(path, raw=True)
                (directory / name).write_bytes(payload)
                artifacts[name] = {"bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest()}
            except (ValueError, OSError) as error:
                artifacts[name] = {"error": type(error).__name__}
        result["artifacts"] = artifacts
        elapsed = time.monotonic() - started
        require(job.get("state", "").lower() == "completed", "application job failed or was cancelled")
        require((directory / "transcript.log").is_file(), "requested full process transcript was not retained")
        metrics = validate_recording(json.loads((directory / "recording.json").read_bytes()),
            json.loads((directory / "experiment.json").read_bytes()), job, request,
            (directory / "games.pgn").read_bytes(), (directory / "experiment.json").read_bytes(), elapsed)
        result.update(status="passed", **metrics)
    except (ValueError, KeyError, TypeError, OSError, TimeoutError, KeyboardInterrupt) as error:
        result["error"] = str(error) if isinstance(error, ValueError) else type(error).__name__
        if job_id and not owned_terminal:
            try:
                result["stop"] = client.request("/chess/lab/stop/" + job_id, {})
            except (ValueError, OSError) as stop_error:
                result["stop"] = {"error": type(stop_error).__name__}
    finally:
        result["collectorWallSeconds"] = time.monotonic() - started
        save_json(directory / "receipt.json", result)
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base", default=os.environ.get("LAPLACE_API_BASE", "http://127.0.0.1:8080"))
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--games", type=int, default=24)
    parser.add_argument("--depth", type=int, default=4)
    parser.add_argument("--concurrency", default="1,2,4")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--stockfish-threads", type=int, default=1)
    parser.add_argument("--stockfish-hash-mb", type=int, default=16)
    parser.add_argument("--case-timeout", type=float, default=600)
    parser.add_argument("--request-timeout", type=float, default=30)
    parser.add_argument("--poll-seconds", type=float, default=0.2)
    args = parser.parse_args(argv)
    report = {"schema": "laplace.recorded-chess-benchmark/v1", "status": "failed", "cases": [],
        "boundary": "existing API service: start request through complete games, commit, exact readback and artifact download",
        "rateBoundaries": RATE_BOUNDARIES, "targetMetric": TARGET_METRIC,
        "warmth": "API is not restarted; engine startup remains included per match; repetition does not prove a warm cache",
        "targetRecordedGamesPerSecond": 2500}
    args.output_dir.mkdir(parents=True, exist_ok=True)
    def interrupted(signum, frame):
        raise KeyboardInterrupt("collector interrupted")
    previous_term = signal.signal(signal.SIGTERM, interrupted)
    try:
        require(args.repeats > 0 and min(args.case_timeout, args.request_timeout, args.poll_seconds) > 0,
                "repeat count and time budgets must be positive")
        concurrencies = [int(value) for value in args.concurrency.split(",")]
        require(len(concurrencies) == len(set(concurrencies)), "concurrency values must be unique")
        client = Client(args.api_base, args.request_timeout)
        report["apiBase"] = client.base
        save_json(args.output_dir / "catalog.json", client.request("/chess/lab/catalog"))
        for concurrency in concurrencies:
            request = game_request(args.games, args.depth, concurrency, args.stockfish_threads, args.stockfish_hash_mb)
            for repeat in range(args.repeats):
                case = run_case(client, args.output_dir / f"c{concurrency}-r{repeat+1}", request,
                                args.case_timeout, args.poll_seconds)
                case.update(concurrency=concurrency, repeat=repeat+1)
                report["cases"].append(case)
                save_json(args.output_dir / "receipt.json", report)
                print(json.dumps({key: case[key] for key in ("status", "concurrency", "repeat", "jobId", "gamesPerSecondServerWorkflow", "gamesPerSecondEndToEnd", "error") if key in case}), flush=True)
                require(case["status"] == "passed", "recorded-game acceptance failed; retained case identifies the failure")
        report.update(summarize_rates(report["cases"], concurrencies, report["targetRecordedGamesPerSecond"]))
        report["status"] = "passed"
    except (ValueError, KeyError, OSError, KeyboardInterrupt) as error:
        report["error"] = str(error) if isinstance(error, ValueError) else type(error).__name__
    finally:
        signal.signal(signal.SIGTERM, previous_term)
        save_json(args.output_dir / "receipt.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
