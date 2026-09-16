#!/usr/bin/env python3
"""Measure new recorded PLAYING occurrences from retained complete matches.

Preparation and engine play happen before measurement. The ordinary API/native
ingestor owns all chess parsing, canonical identities, commits and exact readback.
A reused occurrence is a replay, never a newly recorded game.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import signal
import sys
import time

spec = importlib.util.spec_from_file_location("retained_capacity_owner",
    Path(__file__).with_name("benchmark-retained-chess-ingestion.py"))
retained = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retained)
transport = retained.transport
require, save = transport.require, transport.save_json

SCHEMA = "laplace.retained-chess-capacity/v1"
CORPUS_SCHEMA = "laplace.retained-chess-capacity-corpus/v1"
MAX_JOBS = 32  # Existing ChessLabService terminal-job retention, not a new store.
MAX_CORPUS_BYTES = 2 << 30
NORMAL_RESULTS = {
    "1-0 (White mates)", "0-1 (Black mates)",
    "1/2-1/2 (Draw by stalemate)", "1/2-1/2 (Draw by insufficient mating material)",
    "1/2-1/2 (Draw by fifty moves rule)", "1/2-1/2 (Draw by 3-fold repetition)",
}
WRITER_COUNTS = ("applyCalls", "entitiesAttempted", "entitiesInserted",
    "physicalitiesAttempted", "physicalitiesInserted", "attestationsAttempted",
    "attestationsInserted", "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge",
    "roundTrips", "journalReplayHits", "copyTransactionsStarted", "copyTransactionsCommitted")


def identity(value):
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{32}", value),
            "invalid retained job identity")
    return value


def file_identity(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def read_bounded(path, maximum=transport.MAX_BYTES):
    require(path.is_file() and not path.is_symlink(), "corpus file must be a retained regular file")
    with path.open("rb") as stream:
        data = stream.read(maximum + 1)
    require(len(data) <= maximum, "retained corpus file exceeds its byte envelope")
    return data


def source_context(experiment, job_id):
    """Inspect retained transport provenance, not PGN syntax or chess identities."""
    require(experiment.get("experimentId") == job_id
            and experiment.get("pgnEvent") == "chess-lab/cutechess/" + job_id,
            "retained experiment identity differs")
    require(experiment.get("matchState", "").lower() == "completed"
            and experiment.get("artifactIdentitiesUnchanged") is True,
            "corpus requires an actual completed match with unchanged executables")
    require(experiment.get("ingested") is None,
            "the source match was already auto-ingested; retain a match with ingest=false")
    games = experiment.get("games")
    options = experiment.get("requestedOptions", {})
    count = options.get("rounds")
    require(type(count) is int and 2 <= count <= 8192 and count % 2 == 0
            and isinstance(games, list) and len(games) == count,
            "completed match game inventory differs from its requested rounds")
    require(all(isinstance(row, dict) and type(row.get("index")) is int
                and row.get("result") in NORMAL_RESULTS for row in games)
            and len({row["index"] for row in games}) == count,
            "corpus includes an incomplete, duplicate, forfeited or adjudicated game")
    command = experiment.get("command", {})
    arguments = command.get("arguments")
    require(isinstance(arguments, list) and all(isinstance(arg, str) for arg in arguments)
            and not any(arg in ("-maxmoves", "-draw", "-resign") for arg in arguments),
            "artificially capped games cannot establish recorded-game capacity")
    names = [arg[5:] for arg in arguments if arg.startswith("name=")]
    require(names == ["Laplace", "Stockfish"],
            "this corpus owner requires the actual Laplace-versus-Stockfish match")
    artifacts = experiment.get("artifacts", {})
    after = experiment.get("artifactsAfterMatch", {})
    engines = {}
    for name in ("Laplace", "Stockfish", "cutechess"):
        before = artifacts.get(name)
        current = after.get(name)
        require(isinstance(before, dict) and isinstance(current, dict)
                and before.get("error") is None and current.get("error") is None
                and isinstance(before.get("path"), str) and before.get("path")
                and type(before.get("bytes")) is int and before["bytes"] > 0
                and isinstance(before.get("sha256"), str)
                and re.fullmatch(r"[0-9a-f]{64}", before["sha256"])
                and all(before.get(key) == current.get(key) for key in ("path", "bytes", "sha256")),
                "retained engine identity is missing or changed")
        engines[name] = before
    return {"games": count, "opponents": ["Laplace (substrate UCI)", "Stockfish"],
            "engineArtifacts": engines, "command": command, "requestedOptions": options,
            "implementationArtifacts": artifacts,
            "generationScope": "Actual retained Laplace-versus-Stockfish match; not Stockfish self-play. Generation is outside ingestion timing."}


def fetch_job(client, directory, job_id):
    job = client.request("/chess/lab/jobs/" + job_id,
                         maximum_bytes=transport.MAX_JOB_METADATA_BYTES)
    require(job.get("id") == job_id and job.get("state", "").lower() == "completed",
            "retained job is absent, changed or no longer completed")
    directory.mkdir(parents=True, exist_ok=False)
    save(directory / "job.json", job)
    payloads = {}
    for name in ("games.pgn", "experiment.json"):
        payloads[name] = client.request(f"/chess/lab/jobs/{job_id}/artifact/{name}", raw=True)
        (directory / name).write_bytes(payloads[name])
    context = source_context(json.loads(payloads["experiment.json"]), job_id)
    return {"jobId": job_id, "files": {name: file_identity(data) for name, data in payloads.items()},
            "generation": context}


def prepare(args, client, report):
    jobs = args.job_id or []
    require(len(jobs) == len(set(jobs)), "retained job pool repeats one occurrence context")
    if jobs:
        require(2 <= len(jobs) <= MAX_JOBS, "a corpus pool requires 2..32 distinct retained jobs")
        for job in jobs:
            identity(job)
    else:
        require(2 <= args.jobs <= MAX_JOBS, "preparation requires 2..32 actual matches")
        transport.game_request(args.games_per_job, args.depth, args.concurrency, 1, 16)
        require(args.games_per_job <= 8192, "games per retained job exceed the collector envelope")
    report.update(schema=CORPUS_SCHEMA, phase="preparation", jobs=[],
                  occurrenceNovelty="Unmeasured until admission; this collector never rewrites source Event/Round/player identity.",
                  preparationKind="existing-retained-jobs" if jobs else "new-complete-matches")
    total_bytes = 0
    for index in range(len(jobs) if jobs else args.jobs):
        client.remaining()
        if jobs:
            job_id = jobs[index]
        else:
            request = transport.game_request(args.games_per_job, args.depth, args.concurrency, 1, 16)
            request["config"]["ingest"] = False
            save(args.output_dir / f"request-{index}.json", request)
            started = client.request("/chess/lab/start", request)
            job_id = identity(started.get("jobId"))
            report["ownedUnfinishedJob"] = job_id
            save(args.output_dir / "corpus.json", report)
            while True:
                job = client.request("/chess/lab/jobs/" + job_id,
                                     maximum_bytes=transport.MAX_JOB_METADATA_BYTES)
                require(job.get("id") == job_id, "polled job identity differs")
                if job.get("state", "").lower() in transport.TERMINAL:
                    report.pop("ownedUnfinishedJob", None)
                    require(job.get("state", "").lower() == "completed", "corpus generation failed")
                    break
                time.sleep(min(.25, client.remaining()))
        entry = fetch_job(client, args.output_dir / job_id, job_id)
        if not jobs:
            require(entry["generation"]["games"] == args.games_per_job,
                    "generated match count differs from preparation request")
        total_bytes += sum(value["bytes"] for value in entry["files"].values())
        require(total_bytes <= MAX_CORPUS_BYTES, "retained corpus exceeds the aggregate byte envelope")
        report["jobs"].append(entry)
        report["retainedBytes"] = total_bytes
        save(args.output_dir / "corpus.json", report)
    report.update(status="prepared", requestedGames=sum(job["generation"]["games"] for job in report["jobs"]),
                  persistenceScope="Local evidence retains source bytes. Admission still requires these existing jobs in the same API process; service retention is 32 terminal jobs.")


def load_corpus(directory):
    raw = read_bounded(directory / "corpus.json", 8 << 20)
    manifest = json.loads(raw)
    entries = manifest.get("jobs")
    require(manifest.get("schema") == CORPUS_SCHEMA and manifest.get("status") == "prepared"
            and isinstance(entries, list) and 2 <= len(entries) <= MAX_JOBS,
            "prepared retained corpus manifest is absent or incomplete")
    seen, loaded, total_bytes = set(), [], 0
    for entry in entries:
        job_id = identity(entry.get("jobId"))
        require(job_id not in seen, "retained corpus repeats one job context")
        seen.add(job_id)
        payloads = {}
        for name in ("games.pgn", "experiment.json"):
            data = read_bounded(directory / job_id / name)
            total_bytes += len(data)
            require(total_bytes <= MAX_CORPUS_BYTES, "retained corpus exceeds the aggregate byte envelope")
            require(file_identity(data) == entry.get("files", {}).get(name),
                    "retained corpus bytes differ from preparation")
            payloads[name] = data
        experiment = json.loads(payloads["experiment.json"])
        context = source_context(experiment, job_id)
        require(context == entry.get("generation"), "retained generation context changed")
        loaded.append({"jobId": job_id, "experiment": experiment,
                       "experimentBytes": payloads["experiment.json"], "pgn": payloads["games.pgn"],
                       "generation": context})
    return loaded, file_identity(raw)


def admission(client, output, report, job, replay=False, baseline=None, index=0):
    started = time.monotonic()
    response = retained.request_ingestion(client, output, report, job["jobId"], index)
    save(output / f"ingest-{index}-response.json", response)
    name = response.get("measurementArtifact")
    require(isinstance(name, str) and re.fullmatch(r"ingest-[0-9a-f]{32}\.json", name),
            "ordinary admission did not return its retained measurement artifact")
    raw = client.request(f'/chess/lab/jobs/{job["jobId"]}/artifact/{name}', raw=True)
    (output / name).write_bytes(raw)
    receipt = json.loads(raw)
    require(receipt.get("status") == "completed", "retained service operation did not complete")
    result = retained.validate(receipt, job["experiment"], job["experimentBytes"], job["pgn"],
        job["jobId"], job["generation"]["games"], time.monotonic() - started,
        replay=replay, previous=baseline)
    result["scopeStages"] = receipt["recording"]["replayScopes"]
    result["writer"] = receipt["recording"]["writer"]
    result["artifact"] = {"name": name, **file_identity(raw)}
    return result



def merge_scope_observations(latest, stages):
    """Fold actual ordered fresh-admission snapshots without assuming isolation."""
    for stage in stages:
        before = retained.scope_rows(stage["before"])
        after = retained.scope_rows(stage["after"])
        require(set(before) <= set(after), "a selected scope row disappeared during admission")
        for key, count in before.items():
            require(count >= latest.get(key, 0), "a previously observed scope count decreased before admission")
        for key, count in after.items():
            if key in latest:
                require(key in before, "a previously observed scope row disappeared before admission")
            require(count >= before.get(key, 0) and count >= latest.get(key, 0),
                    "an observed scope count decreased during fresh admission")
            latest[key] = count


def rebase_scope(baseline, latest):
    require(set(baseline["scope"]) <= set(latest), "post-pool snapshot lacks an earlier job scope")
    require(all(latest[key] >= count for key, count in baseline["scope"].items()),
            "post-pool snapshot decreased an earlier observation")
    # Only previously verified identities are selected. Canonical game bodies stay
    # identical, while shared rows reflect later genuine fresh observations.
    return {**baseline, "scope": {key: latest[key] for key in baseline["scope"]}}


def add_fresh(total, accepted, result, seen):
    # Content can repeat legitimately. An already admitted PLAYING cannot.
    playing_ids = {game["playingId"] for game in result["games"]}
    require(not seen.intersection(playing_ids), "a PLAYING occurrence repeats across retained jobs")
    require(result["metrics"]["newlyRecordedPlayings"] == len(playing_ids),
            "new playing count differs from native exact readback")
    seen.update(playing_ids)
    accepted.extend(result["games"])
    total["newlyRecordedPlayings"] += len(playing_ids)
    total["pliesReadback"] += result["metrics"]["pliesReadback"]
    total["sumServiceSeconds"] += result["metrics"]["serviceElapsedSeconds"]
    for key in WRITER_COUNTS:
        total["writer"][key] += result["writer"][key]


def qualify(total, games, elapsed, minimum, target, replays_verified):
    retained.positive(elapsed, "enclosing admission duration")
    inventory = transport.content_inventory(games)
    require(inventory["playings"] == total["newlyRecordedPlayings"], "aggregate content inventory differs")
    duration = elapsed >= minimum
    varied = inventory["distinctOrderedLines"] >= 2
    qualified = duration and varied and replays_verified
    return {"newlyRecordedPlayings": total["newlyRecordedPlayings"],
            "newlyRecordedCompleteGames": total["newlyRecordedPlayings"],
            "endToEndAdmissionSeconds": elapsed,
            "observedNewPlayingsPerSecond": total["newlyRecordedPlayings"] / elapsed,
            "minimumAdmissionSeconds": minimum, "minimumDurationReached": duration,
            "variedOrderedLineCorpus": varied, "exactReplayControlsPassed": replays_verified,
            "sustainedIngestionCapacityEstablished": qualified,
            "targetGamesPerSecond": target,
            "targetVerdict": ("unqualified" if not qualified else
                              "met" if total["newlyRecordedPlayings"] / elapsed >= target else "below-target"),
            "contentInventory": inventory, "pliesReadback": total["pliesReadback"],
            "writer": total["writer"], "sumServiceSeconds": total["sumServiceSeconds"],
            "writerCountScope": "Observed per-operation shared writer counters, including canonical entities, physicality rows and attestations as separate categories. These are not counts of new game lines or disjoint kinds of objects.",
            "timingScope": "One interval from the first retained admission request through the last committed native exact readback, artifact download and collector verification. Includes parse/normalization, preparation, calculated lanes, commit, service overhead and transfer. Excludes engine generation, initial corpus verification, later exact-replay controls and final summary serialization.",
            "contentScope": "PLAYING occurrences are the numerator. Existing content may be shared across authentic playings. Distinct line count does not establish new canonical entity count or representative chess coverage."}


def measure(args, client, report):
    corpus, manifest_identity = load_corpus(args.corpus_dir)
    report.update(phase="admission", corpusManifest=manifest_identity,
                  corpusDirectory=str(args.corpus_dir.absolute()),
                  generation=[{"jobId": job["jobId"], **job["generation"]} for job in corpus],
                  admissionConcurrency=1, requestedGames=sum(job["generation"]["games"] for job in corpus),
                  admissions=[], replays=[],
                  persistenceScope="No copied or manufactured job metadata; existing API retained-job route only.")
    # Verify every job is still retained before any admission. Exact source bytes
    # are bound again by the native receipt inside the measured operation.
    for job in corpus:
        current = client.request("/chess/lab/jobs/" + job["jobId"],
                                 maximum_bytes=transport.MAX_JOB_METADATA_BYTES)
        require(current.get("id") == job["jobId"]
                and current.get("state", "").lower() == "completed",
                "prepared job no longer exists as a completed retained service job")
        (args.output_dir / job["jobId"]).mkdir()
    total = {"newlyRecordedPlayings": 0, "pliesReadback": 0, "sumServiceSeconds": 0.0,
             "writer": {key: 0 for key in WRITER_COUNTS}}
    accepted, seen, baselines, latest_scope = [], set(), [], {}
    report["acceptedBeforeFailure"] = total
    began = time.monotonic()
    try:
        for index, job in enumerate(corpus):
            result = admission(client, args.output_dir / job["jobId"], report, job)
            merge_scope_observations(latest_scope, result["scopeStages"])
            add_fresh(total, accepted, result, seen)
            baselines.append(result)
            report["admissions"].append({"jobId": job["jobId"], "artifact": result["artifact"],
                                         **result["metrics"], "writer": result["writer"]})
            save(args.output_dir / "receipt.json", report)
        # Verify exact native ordered-line agreement across jobs inside timing.
        transport.content_inventory(accepted)
        baselines = [rebase_scope(baseline, latest_scope) for baseline in baselines]
        scope_rows = [{"kind": kind, "id": key, "observationCount": count}
                      for (kind, key), count in sorted(latest_scope.items())]
        save(args.output_dir / "post-pool-scope.json", scope_rows)
        report["postPoolScope"] = {
            "artifact": "post-pool-scope.json",
            **file_identity((args.output_dir / "post-pool-scope.json").read_bytes()),
            "rows": len(scope_rows),
            "scope": "Latest actually observed value for each selected identity across ordered fresh admissions; not a simultaneous database-wide snapshot. Exact replay must keep every selected row unchanged."}
        require(total["newlyRecordedPlayings"] == report["requestedGames"],
                "not every requested occurrence was newly committed and read back")
    finally:
        elapsed = time.monotonic() - began
        report["admissionWindowSeconds"] = elapsed
    require(total["sumServiceSeconds"] <= elapsed + .1 * len(corpus),
            "sequential service work exceeds the enclosing admission interval")
    report["phase"] = "exact-replay-controls"
    report["measurement"] = qualify(total, accepted, elapsed, args.minimum_seconds,
                                    args.target_games_per_second, False)
    save(args.output_dir / "receipt.json", report)
    for index in range(1, args.replays + 1):
        for job, baseline in zip(corpus, baselines):
            result = admission(client, args.output_dir / job["jobId"], report, job,
                               replay=True, baseline=baseline, index=index)
            report["replays"].append({"jobId": job["jobId"], "attempt": index,
                "artifact": result["artifact"], **result["metrics"], "writer": result["writer"]})
            save(args.output_dir / "receipt.json", report)
    report["measurement"] = qualify(total, accepted, elapsed, args.minimum_seconds,
                                    args.target_games_per_second, True)
    report.update(status="passed", phase="complete")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="mode", required=True)
    prepare_parser = sub.add_parser("prepare", help="Retain authentic complete matches outside measurement")
    prepare_parser.add_argument("--job-id", action="append", help="Existing completed job; repeat for a pool")
    prepare_parser.add_argument("--jobs", type=int, default=2)
    prepare_parser.add_argument("--games-per-job", type=int, default=24)
    prepare_parser.add_argument("--depth", type=int, default=4)
    prepare_parser.add_argument("--concurrency", type=int, default=1)
    measure_parser = sub.add_parser("measure", help="Admit the fixed corpus once, then verify exact zero-writer replay")
    measure_parser.add_argument("--corpus-dir", type=Path, required=True)
    measure_parser.add_argument("--replays", type=int, default=1)
    measure_parser.add_argument("--minimum-seconds", type=float, default=30)
    measure_parser.add_argument("--target-games-per-second", type=float, default=2500)
    for command in (prepare_parser, measure_parser):
        command.add_argument("--output-dir", type=Path, required=True)
        command.add_argument("--api-base", default=transport.default_api_base())
        command.add_argument("--timeout-seconds", type=float, default=3600)
    args = parser.parse_args(argv)
    args.output_dir.mkdir(parents=True, exist_ok=False)
    report = {"schema": SCHEMA, "status": "failed", "phase": args.mode,
              "startedAt": datetime.now(timezone.utc).isoformat(),
              "scope": "Ingestion-only capacity through the ordinary retained-job API/shared writer/native readback. No engine work belongs to the measured admission interval."}
    started = time.monotonic()
    client = None
    previous = signal.getsignal(signal.SIGTERM)
    def interrupted(_signum, _frame):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupted)
    try:
        retained.positive(args.timeout_seconds, "whole command timeout")
        if args.mode == "measure":
            require(1 <= args.replays <= 4, "require 1..4 exact replay controls per source job")
            require(math.isfinite(args.minimum_seconds) and args.minimum_seconds >= 30,
                    "sustained capacity requires at least 30 seconds")
            retained.positive(args.target_games_per_second, "target rate")
        client = retained.DeadlineClient(args.api_base, args.timeout_seconds,
                                         started + args.timeout_seconds)
        report["deadlineSeconds"] = args.timeout_seconds
        with transport.time_limit(args.timeout_seconds):
            (prepare if args.mode == "prepare" else measure)(args, client, report)
    except (ValueError, KeyError, TypeError, AttributeError, OSError, TimeoutError, KeyboardInterrupt) as error:
        report["status"] = "failed"
        report["error"] = str(error) if isinstance(error, ValueError) else type(error).__name__
        if "ownedUnfinishedJob" in report and client is not None:
            try:
                report["stopOwnedMatch"] = client.stop_owned(report["ownedUnfinishedJob"])
            except (ValueError, OSError):
                report["stopOwnedMatch"] = "failed"
    finally:
        signal.signal(signal.SIGTERM, previous)
        report["commandSecondsIncludingCleanup"] = time.monotonic() - started
        save(args.output_dir / ("corpus.json" if args.mode == "prepare" else "receipt.json"), report)
    print(json.dumps({"schema": report["schema"], "status": report["status"],
                      "output": str(args.output_dir),
                      "measurement": report.get("measurement")}, allow_nan=False))
    return 0 if report["status"] in ("prepared", "passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
