#!/usr/bin/env python3
"""Measure complete Chess Lab matches through committed, exact game readback.

Uses the ordinary API and its normal writer. Native chess code owns parsing,
identity and reconstruction. This collector verifies the application receipts;
it does not implement another chess parser or manufacture substrate identities.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
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
MAX_JOB_METADATA_BYTES = 1 << 20
TERMINAL = {"completed", "failed", "cancelled"}
RATE_BOUNDARIES = {
    "gamesPerSecondServerWorkflow": "server job entry through full play, recording, shared writer completion and exact readback; includes engine startup and job overhead; excludes API queue, final recording receipt serialization and artifact transfer",
    "gamesPerSecondEndToEnd": "collector start request through observed completion and artifact download; includes queue, polling delay and transfer",
    "gamesPerSecondRecordingAndReadback": "parse, prepare, novelty, calculated lanes, shared writer completion and exact readback; excludes play and job overhead",
    "gamesPerSecondRecording": "parse, prepare, novelty, calculated lanes and shared writer completion; excludes play, readback and job overhead",
}
TARGET_METRIC = "gamesPerSecondServerWorkflow"
SUSTAINED_METRIC = "gamesPerSecondSustainedEndToEnd"
MIN_DURATION_SECONDS = 30
DEFAULT_MAX_SUSTAINED_CASES = 4096
DEFAULT_API_BASE = "http://127.0.0.1:5187"


def default_api_base() -> str:
    return os.environ.get("LAPLACE_API_BASE", DEFAULT_API_BASE)


@contextmanager
def time_limit(seconds: float):
    """Bound the complete synchronous operation, including blocked HTTP reads.

    This collector runs on the Unix host's main thread. A socket timeout alone
    resets on progress and cannot enforce a whole-case deadline.
    """
    require(seconds > 0, "collector execution deadline expired")
    require(hasattr(signal, "setitimer"), "collector deadlines require Unix interval timers")
    started = time.monotonic()
    previous = signal.getsignal(signal.SIGALRM)
    timer = signal.getitimer(signal.ITIMER_REAL)
    def expired(signum, frame):
        raise TimeoutError("collector execution timed out")
    signal.signal(signal.SIGALRM, expired)
    signal.setitimer(signal.ITIMER_REAL, min(seconds, timer[0]) if timer[0] > 0 else seconds)
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)
        remaining = timer[0] - (time.monotonic() - started)
        if timer[0] > 0 and remaining > 0:
            signal.setitimer(signal.ITIMER_REAL, remaining, timer[1])


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def save_json(path: Path, value: object) -> None:
    temporary = path.with_name(path.name + ".pending")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    temporary.replace(path)


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

    def request(self, path: str, data: object = None, raw: bool = False,
                maximum_bytes: int = MAX_BYTES):
        require(0 < maximum_bytes <= MAX_BYTES, "API response byte envelope is exhausted or invalid")
        request = urllib.request.Request(self.base + path, headers=self.headers,
            data=None if data is None else json.dumps(data).encode("utf-8"),
            method="GET" if data is None else "POST")
        try:
            with time_limit(self.timeout), urllib.request.urlopen(request, timeout=self.timeout) as response:
                payload = response.read(maximum_bytes + 1)
                require(len(payload) <= maximum_bytes, "API artifact exceeds collector byte envelope")
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
    require(recording.get("schema") == "laplace.chess-recording/v2"
            and recording.get("status") == "completed", "committed recording receipt is missing or failed")
    durability = recording.get("durability", {})
    require(isinstance(durability, dict) and durability.get("synchronousCommit") == "on"
            and all(durability.get(field) is True for field in
                    ("fsync", "fullPageWrites", "writeCommitAcknowledged", "localWalFlushAcknowledged")),
            "recording lacks an established synchronous local PostgreSQL WAL acknowledgement")
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
                  "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge", "roundTrips", "journalReplayHits",
                  "copyTransactionsStarted", "copyTransactionsCommitted"):
        require(type(writer.get(field)) is int and writer[field] >= 0, f"missing writer work counter: {field}")
    require(writer.get("roundTripsKind") == "logical-writer-accounting/v1",
            "writer round trips must declare their logical accounting boundary")
    require(writer["applyCalls"] > 0 and writer["copyTransactionsStarted"] > 0
            and writer["copyTransactionsCommitted"] == writer["copyTransactionsStarted"],
            "recording lacks completed COPY transaction counts")
    record_wall = times["recording"] + times["commit"]
    verified_record_wall = record_wall + times["readback"]
    return {"verifiedRecordedGames": count, "verifiedRecordedPlies": plies,
            "verifiedPlayingIds": sorted(playing_ids),
            "gamesPerSecondServerWorkflow": count / times["total"],
            "pliesPerSecondServerWorkflow": plies / times["total"],
            "gamesPerSecondEndToEnd": count / elapsed,
            "pliesPerSecondEndToEnd": plies / elapsed,
            "gamesPerSecondRecordingAndReadback": count / verified_record_wall if verified_record_wall > 0 else None,
            "gamesPerSecondRecording": count / record_wall if record_wall > 0 else None,
            "rateBoundaries": RATE_BOUNDARIES,
            "elapsedSeconds": {"collectorEndToEnd": elapsed,
                **{field: times[field] for field in ("total", "play", "recording", "commit", "readback", "overhead")}},
            "writer": {field: writer[field] for field in (
                "applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted",
                "physicalitiesInserted", "attestationsAttempted", "attestationsInserted",
                "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge", "roundTrips", "roundTripsKind",
                "journalReplayHits", "copyTransactionsStarted", "copyTransactionsCommitted")},
            "durability": {field: durability[field] for field in (
                "synchronousCommit", "fsync", "fullPageWrites", "writeCommitAcknowledged", "localWalFlushAcknowledged")}}


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
            "sampleTargetRateReached": max(medians[TARGET_METRIC].values()) >= target,
            "collectorSampleTargetRateReached": max(medians["gamesPerSecondEndToEnd"].values()) >= target,
            "durationQualified": False, "targetMet": False,
            "collectorEndToEndTargetMet": False}


def run_case(client: Client, directory: Path, request: dict, timeout: float, poll: float,
             artifact_budget: dict | None = None, maximum_artifact_bytes: int = MAX_BYTES) -> dict:
    directory.mkdir(parents=True, exist_ok=False)
    save_json(directory / "request.json", request)
    started = time.monotonic()
    result = {"status": "failed", "startedAt": datetime.now(timezone.utc).isoformat()}
    try:
        with time_limit(timeout):
            return _run_case_body(client, directory, request, timeout, poll, artifact_budget,
                                  maximum_artifact_bytes, result, started)
    except (ValueError, KeyError, TypeError, OSError, TimeoutError, KeyboardInterrupt) as error:
        result["status"] = "failed"
        result["error"] = str(error) if isinstance(error, (ValueError, TimeoutError)) else type(error).__name__
        job_id = result.get("jobId")
        owned_terminal = result.get("ownedTerminal", False)
        if job_id and not owned_terminal:
            cleanup_started = time.monotonic()
            try:
                with time_limit(10):
                    result["stop"] = client.request("/chess/lab/stop/" + job_id, {})
            except (ValueError, OSError, TimeoutError, KeyboardInterrupt) as stop_error:
                result["stop"] = {"error": type(stop_error).__name__}
            result["cleanupWallSeconds"] = time.monotonic() - cleanup_started
    finally:
        result["collectorWallSeconds"] = time.monotonic() - started
        save_json(directory / "receipt.json", result)
    return result


def _run_case_body(client, directory, request, timeout, poll, artifact_budget,
                   maximum_artifact_bytes, result, started):
    response = client.request("/chess/lab/start", request, maximum_bytes=MAX_JOB_METADATA_BYTES)
    job_id = response.get("jobId")
    require(isinstance(job_id, str) and re.fullmatch(r"[a-zA-Z0-9_-]+", job_id), "API did not return a safe job identity")
    result["jobId"] = job_id
    save_json(directory / "start.json", response)
    save_json(directory / "receipt.json", result)
    while True:
        require(time.monotonic() - started < timeout, "application match timed out")
        job = client.request("/chess/lab/jobs/" + job_id, maximum_bytes=MAX_JOB_METADATA_BYTES)
        require(job.get("id") == job_id, "polled job differs from the started experiment")
        save_json(directory / "job.json", job)
        if job.get("state", "").lower() in TERMINAL:
            result["ownedTerminal"] = True
            break
        time.sleep(poll)
    # Retain failure evidence too; success is established only below.
    artifacts = result["artifacts"] = {}
    for name in ("experiment.json", "recording.json", "games.pgn", "transcript.log"):
        if name not in job.get("artifacts", {}):
            continue
        path = f"/chess/lab/jobs/{job_id}/artifact/{name}"
        try:
            available = maximum_artifact_bytes
            if artifact_budget is not None:
                available = min(available, artifact_budget["limit"] - artifact_budget["used"])
            require(available > 0, "retained artifact byte envelope exhausted")
            payload = client.request(path, raw=True, maximum_bytes=available)
            require(len(payload) <= available, "API artifact exceeds collector byte envelope")
            complete, partial = directory / name, directory / (name + ".partial")
            try:
                partial.write_bytes(payload)
                partial.replace(complete)
            finally:
                retained = complete if complete.exists() else partial
                actual_bytes = retained.stat().st_size if retained.exists() else 0
                if artifact_budget is not None:
                    artifact_budget["used"] += actual_bytes
                artifacts[name] = {"bytes": actual_bytes, "complete": complete.exists(),
                    "sha256": hashlib.sha256(payload).hexdigest() if complete.exists() else None}
        except TimeoutError:
            raise
        except (ValueError, OSError) as error:
            artifacts.setdefault(name, {}).update(error=str(error) if isinstance(error, ValueError) else type(error).__name__)
        save_json(directory / "receipt.json", result)
    elapsed = time.monotonic() - started
    require(job.get("state", "").lower() == "completed", "application job failed or was cancelled")
    require((directory / "transcript.log").is_file(), "requested full process transcript was not retained")
    metrics = validate_recording(json.loads((directory / "recording.json").read_bytes()),
        json.loads((directory / "experiment.json").read_bytes()), job, request,
        (directory / "games.pgn").read_bytes(), (directory / "experiment.json").read_bytes(), elapsed)
    result.update(status="passed", **metrics)
    return result


def include_new_case(case: dict, seen_jobs: set, seen_playings: set) -> None:
    """Transport identity membership, after the ordinary native receipt validated."""
    require(case["status"] == "passed", "recorded-game acceptance failed; retained case identifies the failure")
    require(case["jobId"] not in seen_jobs, "a previous experiment was reused as new recorded work")
    ids = case["verifiedPlayingIds"]
    require(len(ids) == case["verifiedRecordedGames"] and not seen_playings.intersection(ids),
            "a previous playing was reused as newly recorded throughput")
    seen_jobs.add(case["jobId"])
    seen_playings.update(ids)


def run_sustained(client, directory, request, duration, deadline, case_timeout, poll,
                  maximum_cases, artifact_budget, maximum_artifact_bytes,
                  seen_jobs, seen_playings, result, checkpoint):
    """Run sequential complete bounded jobs. The denominator is one wall interval.

    Engine generation, queueing, synchronous writing, exact readback, polling,
    transfer and inter-case bookkeeping all consume this interval. Stage times
    remain diagnostics and are never added as its denominator.
    """
    started = time.monotonic()
    result.update(status="running", startedAt=datetime.now(timezone.utc).isoformat(), requestedDurationSeconds=duration, cases=[],
                  concurrency=request["config"]["concurrency"], gamesPerCase=request["config"]["rounds"],
                  targetMetric=SUSTAINED_METRIC, targetMet=False, durationQualified=False,
                  boundary="sequential complete generated-and-recorded workflows, including collector request/poll/transfer and inter-case bookkeeping",
                  excludes="initial correctness sweep; final aggregate receipt serialization; no retained-input or replay substitution")
    try:
        with time_limit(deadline - time.monotonic()):
            checkpoint()
            while time.monotonic() - started < duration or len(result["cases"]) < 2:
                require(len(result["cases"]) < maximum_cases, "sustained complete-case envelope exhausted before duration qualification")
                remaining = deadline - time.monotonic()
                require(remaining > 0, "collector total execution deadline expired before duration qualification")
                require(artifact_budget["used"] < artifact_budget["limit"], "retained artifact byte envelope exhausted")
                case = run_case(client, directory / f"s{len(result['cases'])+1}", request,
                                min(case_timeout, remaining), poll, artifact_budget, maximum_artifact_bytes)
                result["cases"].append(case)
                include_new_case(case, seen_jobs, seen_playings)
                case["aggregateAccepted"] = True
                checkpoint()
                require(time.monotonic() < deadline, "collector total execution deadline expired during case bookkeeping")
            result["status"] = "passed"
    except (ValueError, KeyError, TypeError, OSError, TimeoutError, KeyboardInterrupt) as error:
        result["status"] = "failed"
        result["error"] = str(error) if isinstance(error, (ValueError, TimeoutError)) else type(error).__name__
    finally:
        elapsed = time.monotonic() - started
        # A partial failed run retains successful work but never establishes capacity.
        valid = [case for case in result["cases"] if case.get("aggregateAccepted") is True]
        games = sum(case["verifiedRecordedGames"] for case in valid)
        plies = sum(case["verifiedRecordedPlies"] for case in valid)
        qualified = result["status"] == "passed" and elapsed >= duration >= MIN_DURATION_SECONDS and len(valid) >= 2
        rate = games / elapsed if elapsed > 0 else 0
        result.update(elapsedSeconds=elapsed, finishedAt=datetime.now(timezone.utc).isoformat(), completedCases=len(valid),
                      verifiedRecordedGames=games, verifiedRecordedPlies=plies,
                      gamesPerSecondSustainedEndToEnd=rate,
                      pliesPerSecondSustainedEndToEnd=plies / elapsed if elapsed > 0 else 0,
                      gamesPerDayExtrapolated=rate * 86400,
                      extrapolation="measured complete-window rate multiplied by 86400; not a day-long observation",
                      durationQualified=qualified, targetMet=qualified and rate >= 2500,
                      writerTotals={key: sum(case["writer"][key] for case in valid)
                                    for key in valid[0]["writer"] if type(valid[0]["writer"][key]) is int} if valid else {})
        checkpoint()
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base", default=default_api_base())
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
    parser.add_argument("--duration-seconds", type=float, default=0,
                        help="After the correctness sweep, repeat bounded complete jobs for 30..3600 seconds; 0 runs only correctness/latency")
    parser.add_argument("--total-timeout", type=float, default=1800,
                        help="Whole collector execution deadline in seconds; an owned-job cancellation may take up to 10 additional seconds")
    parser.add_argument("--max-sustained-cases", type=int, default=DEFAULT_MAX_SUSTAINED_CASES)
    parser.add_argument("--max-artifact-bytes", type=int, default=MAX_BYTES,
                        help="Maximum bytes for each retained API artifact, at most 256 MiB")
    parser.add_argument("--max-total-artifact-bytes", type=int, default=1 << 30,
                        help="Total retained original API artifact bytes across sweep and sustained cases, at most 4 GiB")
    args = parser.parse_args(argv)
    if args.output_dir.exists() and any(args.output_dir.iterdir()):
        parser.error("output directory already contains evidence; choose a new directory")
    report = {"schema": "laplace.recorded-chess-benchmark/v2", "status": "failed", "cases": [],
        "measurementKind": "generated-and-recorded",
        "collectorScriptSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "boundary": "existing API service: start request through complete games, commit, exact readback and artifact download",
        "rateBoundaries": RATE_BOUNDARIES, "targetMetric": TARGET_METRIC,
        "warmth": "API is not restarted; engine startup remains included per match; repetition does not prove a warm cache",
        "targetRecordedGamesPerSecond": 2500, "targetMet": False, "durationQualified": False,
        "sustained": {"status": "not_requested"}}
    args.output_dir.mkdir(parents=True, exist_ok=True)
    try:
        with (args.output_dir / "collector-owner.json").open("x", encoding="utf-8") as owner:
            json.dump({"pid": os.getpid(), "startedAt": datetime.now(timezone.utc).isoformat()}, owner)
    except FileExistsError:
        parser.error("another collector owns the output directory")
    def interrupted(signum, frame):
        raise KeyboardInterrupt("collector interrupted")
    previous_term = signal.signal(signal.SIGTERM, interrupted)
    started = time.monotonic()
    budget = {"limit": args.max_total_artifact_bytes, "used": 0,
              "scope": "retained experiment/recording/PGN/transcript payload bytes; local JSON checkpoints are bounded separately by case/game limits"}
    report["artifactBudget"] = budget
    report["limits"] = {key: getattr(args, key) for key in (
        "games", "repeats", "duration_seconds", "total_timeout", "max_sustained_cases", "max_artifact_bytes", "max_total_artifact_bytes")}
    for key, value in report["limits"].items():
        if type(value) is float and not math.isfinite(value):
            report["limits"][key] = str(value)  # Preserve rejected input without writing non-JSON NaN/Infinity.
    report["limits"]["jobMetadataBytesPerResponse"] = MAX_JOB_METADATA_BYTES
    try:
        require(1 <= args.repeats <= 10 and 2 <= args.games <= 512,
                "correctness repeats must be in 1..10 and bounded game batches in 2..512")
        require(all(math.isfinite(value) and value > 0 for value in
                    (args.case_timeout, args.request_timeout, args.poll_seconds, args.total_timeout)),
                "time budgets must be finite and positive")
        require(args.case_timeout <= 3600 and args.request_timeout <= 120 and args.poll_seconds <= 10
                and args.total_timeout <= 7200, "collector time budget exceeds its admitted envelope")
        require(args.duration_seconds == 0 or MIN_DURATION_SECONDS <= args.duration_seconds <= 3600,
                "duration must be 0 or in 30..3600 seconds")
        require(args.duration_seconds < args.total_timeout and 2 <= args.max_sustained_cases <= 4096,
                "duration must leave time for correctness; sustained case limit must be in 2..4096")
        require(1 <= args.max_artifact_bytes <= MAX_BYTES
                and args.max_artifact_bytes <= args.max_total_artifact_bytes <= 4 << 30,
                "artifact bounds must fit one response and the total retained envelope")
        concurrencies = [int(value) for value in args.concurrency.split(",")]
        require(1 <= len(concurrencies) <= 8 and len(concurrencies) == len(set(concurrencies))
                and all(1 <= value <= 64 for value in concurrencies), "concurrency values must be unique, 1..64, at most 8 points")
        require(1 <= args.depth <= 64 and 1 <= args.stockfish_threads <= 64
                and 1 <= args.stockfish_hash_mb <= 4096, "engine settings exceed the bounded workload envelope")
        maximum_games = args.games * args.max_sustained_cases
        report["targetResourceEnvelope"] = {
            "targetGamesPerSecond": report["targetRecordedGamesPerSecond"],
            "gamesPerCase": args.games, "maximumSustainedCases": args.max_sustained_cases,
            "maximumSustainedGames": maximum_games,
            "requestedDurationSeconds": args.duration_seconds,
            "minimumGamesAtTargetDuration": math.ceil(args.duration_seconds * report["targetRecordedGamesPerSecond"])
                if args.duration_seconds > 0 else None,
            "maximumRateAllowedByCaseCountAtMinimumDuration": maximum_games / args.duration_seconds
                if args.duration_seconds > 0 else None,
            "caseCountAllowsTargetAtMinimumDuration": maximum_games >= args.duration_seconds * report["targetRecordedGamesPerSecond"]
                if args.duration_seconds > 0 else None,
            "pollSeconds": args.poll_seconds,
            "scope": "count-only envelope, not achieved capacity; actual job duration, polling, transfer, total deadline and artifact bytes still constrain measured throughput",
        }
        deadline = started + args.total_timeout
        client = Client(args.api_base, args.request_timeout)
        report["apiBase"] = client.base
        with time_limit(deadline - time.monotonic()):
            save_json(args.output_dir / "catalog.json", client.request("/chess/lab/catalog", maximum_bytes=MAX_JOB_METADATA_BYTES))
        seen_jobs, seen_playings = set(), set()
        for concurrency in concurrencies:
            request = game_request(args.games, args.depth, concurrency, args.stockfish_threads, args.stockfish_hash_mb)
            for repeat in range(args.repeats):
                remaining = deadline - time.monotonic()
                require(remaining > 0, "collector total execution deadline expired during correctness sweep")
                case = run_case(client, args.output_dir / f"c{concurrency}-r{repeat+1}", request,
                                min(args.case_timeout, remaining), args.poll_seconds, budget, args.max_artifact_bytes)
                case.update(concurrency=concurrency, repeat=repeat+1)
                report["cases"].append(case)
                with time_limit(deadline - time.monotonic()):
                    save_json(args.output_dir / "receipt.json", report)
                print(json.dumps({key: case[key] for key in ("status", "concurrency", "repeat", "jobId", "gamesPerSecondServerWorkflow", "gamesPerSecondEndToEnd", "error") if key in case}), flush=True)
                include_new_case(case, seen_jobs, seen_playings)
        with time_limit(deadline - time.monotonic()):
            report.update(summarize_rates(report["cases"], concurrencies, report["targetRecordedGamesPerSecond"]))
        if args.duration_seconds > 0:
            # The sweep selects one candidate; the following independent duration window
            # establishes its own rate and includes all ordinary request overhead.
            rates = report["medianRatesByConcurrency"]["gamesPerSecondEndToEnd"]
            selected = max(concurrencies, key=lambda value: (rates[str(value)], -value))
            report["sustainedSelection"] = {"concurrency": selected,
                "law": "highest median complete collector throughput in correctness sweep; lower concurrency wins ties"}
            request = game_request(args.games, args.depth, selected, args.stockfish_threads, args.stockfish_hash_mb)
            sustained = run_sustained(client, args.output_dir, request, args.duration_seconds,
                deadline, args.case_timeout, args.poll_seconds, args.max_sustained_cases,
                budget, args.max_artifact_bytes, seen_jobs, seen_playings, report["sustained"],
                lambda: save_json(args.output_dir / "receipt.json", report))
            report.update(targetMetric=SUSTAINED_METRIC, targetMet=sustained["targetMet"],
                          durationQualified=sustained["durationQualified"],
                          collectorEndToEndTargetMet=sustained["targetMet"])
            require(sustained["status"] == "passed", "sustained recording measurement failed; retained cases identify the failure")
        require(time.monotonic() < deadline, "collector total execution deadline expired before completion")
        report["status"] = "passed"
    except (ValueError, KeyError, TypeError, OSError, TimeoutError, KeyboardInterrupt) as error:
        report["error"] = str(error) if isinstance(error, (ValueError, TimeoutError)) else type(error).__name__
        report.update(targetMet=False, durationQualified=False, collectorEndToEndTargetMet=False)
    finally:
        signal.signal(signal.SIGTERM, previous_term)
        report["collectorTotalWallSeconds"] = time.monotonic() - started
        save_json(args.output_dir / "receipt.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
