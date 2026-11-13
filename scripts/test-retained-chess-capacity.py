#!/usr/bin/env python3
"""Failure controls for the retained capacity collector, not PostgreSQL evidence."""
import copy
from contextlib import contextmanager, redirect_stdout
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import io
import json
from pathlib import Path
import signal
import tempfile
import threading
import unittest
from unittest.mock import patch


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


bench = load("retained_capacity", "benchmark-retained-chess-capacity.py")
fixtures = load("retained_capacity_fixture", "test-retained-chess-ingestion.py")


def job_fixture(number):
    receipt, experiment, _, pgn, _, count, _ = fixtures.RetainedIngestionTests().fixture()
    job_id = f"{number:032x}"
    experiment.update(experimentId=job_id, pgnEvent="chess-lab/cutechess/" + job_id,
                      games=[{"index": i, "result": "1-0 (White mates)",
                              "white": "Laplace", "black": "Stockfish"} for i in (1, 2)])
    experiment["command"]["arguments"] = ["-engine", "name=Laplace", "cmd=/fixture/laplace",
        "-engine", "name=Stockfish", "cmd=/fixture/stockfish", "-each", "tc=inf", "depth=4"]
    artifacts = {name: {"path": "/fixture/" + name, "bytes": 100,
                       "sha256": str(index) * 64, "error": None}
                 for index, name in enumerate(("Laplace", "Stockfish", "cutechess"), 1)}
    experiment.update(artifacts=artifacts, artifactsAfterMatch=copy.deepcopy(artifacts))
    raw = json.dumps(experiment).encode()
    receipt.update(jobId=job_id, status="completed", serviceElapsedSeconds=.000001)
    recording = receipt["recording"]
    recording.update(experimentId=job_id, pgnEvent=experiment["pgnEvent"],
                     experimentArtifactSha256=hashlib.sha256(raw).hexdigest(),
                     elapsedSeconds=dict(total=.0000005, play=0, recording=.0000001,
                                         commit=.0000001, readback=.0000001, overhead=.0000002))
    for scope in recording["replayScopes"]:
        scope["after"][2]["observationCount"] = 6 + number
        if number > 1:
            scope["before"] = copy.deepcopy(scope["after"])
            scope["before"][2]["observationCount"] = 5 + number
    for index, game in enumerate(recording["games"]):
        game.update(playingId=f"{number * 10 + index:032x}", lineId=f"{number + 100:032x}",
                    moveIds=[f"{number + 200:032x}"] * 60)
    recording["writer"].update(entitiesAttempted=15, entitiesInserted=3,
        physicalitiesAttempted=9, physicalitiesInserted=2,
        attestationsAttempted=21, attestationsInserted=17)
    return {"jobId": job_id, "experiment": experiment, "experimentBytes": raw, "pgn": pgn,
            "generation": bench.source_context(experiment, job_id), "fresh": receipt}


def replay_receipt(job, latest_count=None):
    receipt = copy.deepcopy(job["fresh"])
    receipt.update(newlyRecordedGames=0, alreadyPresentGames=2,
                   noOpReplayVerified=True, disposition="replay")
    recording = receipt["recording"]
    recording.update(novelGames=0, appliedGames=0, durability=None)
    for key in bench.WRITER_COUNTS:
        recording["writer"][key] = 0
    for scope in recording["replayScopes"]:
        if latest_count is not None:
            scope["after"][2]["observationCount"] = latest_count
        scope.update(before=copy.deepcopy(scope["after"]), unchanged=True)
    return receipt


def write_corpus(directory, jobs):
    directory.mkdir()
    entries = []
    for job in jobs:
        target = directory / job["jobId"]
        target.mkdir()
        data = {"games.pgn": job["pgn"], "experiment.json": job["experimentBytes"]}
        for name, raw in data.items():
            (target / name).write_bytes(raw)
        entries.append({"jobId": job["jobId"], "files": {name: bench.file_identity(raw) for name, raw in data.items()},
                        "generation": job["generation"]})
    bench.save(directory / "corpus.json", {"schema": bench.CORPUS_SCHEMA,
                                         "status": "prepared", "jobs": entries})


@contextmanager
def service(jobs, *, failed_job=None, replay_writer=False):
    calls, counts, receipts = [], {job["jobId"]: 0 for job in jobs}, {}
    lookup = {job["jobId"]: job for job in jobs}
    latest_count = [0]
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass
        def send(self, code, data):
            raw = data if isinstance(data, bytes) else json.dumps(data).encode()
            self.send_response(code)
            self.send_header("Content-Length", str(len(raw)))
            self.end_headers()
            self.wfile.write(raw)
        def do_GET(self):
            calls.append(("GET", self.path))
            parts = self.path.split("/")
            job_id = parts[4]
            job = lookup[job_id]
            if len(parts) == 5:
                self.send(200, {"id": job_id, "state": "Completed",
                    "artifacts": {name: "retained fixture" for owner, name in receipts if owner == job_id}})
            elif parts[6] == "games.pgn":
                self.send(200, job["pgn"])
            elif parts[6] == "experiment.json":
                self.send(200, job["experimentBytes"])
            else:
                self.send(200, receipts[(job_id, parts[6])])
        def do_POST(self):
            calls.append(("POST", self.path))
            self.rfile.read(int(self.headers.get("Content-Length", "0")))
            parts = self.path.split("/")
            job_id = parts[4]
            job = lookup[job_id]
            index = counts[job_id]
            counts[job_id] += 1
            name = "ingest-" + f"{int(job_id, 16) * 100 + index:032x}" + ".json"
            value = copy.deepcopy(job["fresh"]) if index == 0 else replay_receipt(job, latest_count[0])
            if index == 0:
                latest_count[0] = value["recording"]["replayScopes"][0]["after"][2]["observationCount"]
            if index > 0 and replay_writer:
                value["recording"]["writer"]["entitiesAttempted"] = 1
            if failed_job == job_id and index == 0:
                value.update(status="failed", error="fixture native readback failed", recording=None)
            receipts[(job_id, name)] = value
            self.send(500 if value["status"] == "failed" else 200,
                      {"measurementArtifact": name})
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}", calls
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


class CapacityControls(unittest.TestCase):
    def test_real_http_preparation_reuses_authentic_jobs_without_start_or_ingestion(self):
        jobs = [job_fixture(1), job_fixture(2)]
        with tempfile.TemporaryDirectory() as temporary, service(jobs) as (url, calls), redirect_stdout(io.StringIO()):
            output = Path(temporary) / "corpus"
            argv = ["prepare", "--output-dir", str(output), "--api-base", url]
            for job in jobs:
                argv += ["--job-id", job["jobId"]]
            self.assertEqual(0, bench.main(argv))
            retained, _ = bench.load_corpus(output)
            self.assertEqual([job["jobId"] for job in jobs], [job["jobId"] for job in retained])
            self.assertTrue(all(method == "GET" for method, _ in calls))
            self.assertEqual(jobs[0]["pgn"], (output / jobs[0]["jobId"] / "games.pgn").read_bytes())
            manifest = json.loads((output / "corpus.json").read_text())
            self.assertIn("Unmeasured", manifest["occurrenceNovelty"])
            self.assertEqual(["Laplace (substrate UCI)", "Stockfish"],
                             manifest["jobs"][0]["generation"]["opponents"])

    def test_real_http_fresh_admission_and_zero_replay_have_separate_count_and_timing(self):
        jobs = [job_fixture(1), job_fixture(2)]
        with tempfile.TemporaryDirectory() as temporary, service(jobs) as (url, calls), redirect_stdout(io.StringIO()):
            root = Path(temporary)
            write_corpus(root / "corpus", jobs)
            output = root / "measured"
            self.assertEqual(0, bench.main(["measure", "--corpus-dir", str(root / "corpus"),
                                           "--output-dir", str(output), "--api-base", url]))
            report = json.loads((output / "receipt.json").read_text())
            measured = report["measurement"]
            self.assertEqual(4, measured["newlyRecordedPlayings"])
            self.assertEqual(2, measured["contentInventory"]["distinctOrderedLines"])
            self.assertEqual(6, measured["writer"]["entitiesInserted"])
            self.assertEqual(4, measured["writer"]["physicalitiesInserted"])
            self.assertEqual(34, measured["writer"]["attestationsInserted"])
            self.assertTrue(measured["exactReplayControlsPassed"])
            shared = json.loads((output / "post-pool-scope.json").read_text())
            self.assertEqual(8, next(row["observationCount"] for row in shared if row["kind"] == 3))
            self.assertFalse(measured["sustainedIngestionCapacityEstablished"])
            self.assertEqual("unqualified", measured["targetVerdict"])
            self.assertEqual([0, 0], [item["newlyRecordedPlayings"] for item in report["replays"]])
            posts = [path for method, path in calls if method == "POST"]
            expected = [f'/chess/lab/jobs/{job["jobId"]}/ingest' for job in jobs]
            self.assertEqual(expected + expected, posts)
            self.assertFalse(any(path == "/chess/lab/start" for _, path in calls))
            self.assertGreaterEqual(report["commandSecondsIncludingCleanup"], measured["endToEndAdmissionSeconds"])

    def test_failed_later_admission_retains_prior_counts_but_cannot_publish_capacity(self):
        jobs = [job_fixture(1), job_fixture(2)]
        with tempfile.TemporaryDirectory() as temporary, service(jobs, failed_job=jobs[1]["jobId"]) as (url, calls), redirect_stdout(io.StringIO()):
            root = Path(temporary)
            write_corpus(root / "corpus", jobs)
            output = root / "failed"
            self.assertEqual(1, bench.main(["measure", "--corpus-dir", str(root / "corpus"),
                                           "--output-dir", str(output), "--api-base", url]))
            report = json.loads((output / "receipt.json").read_text())
            self.assertEqual("failed", report["status"])
            self.assertEqual(2, report["acceptedBeforeFailure"]["newlyRecordedPlayings"])
            self.assertNotIn("measurement", report)
            self.assertTrue(report["failedIngestion"]["collectionCompleted"])
            self.assertEqual(2, sum(method == "POST" for method, _ in calls))
            self.assertEqual([], report["replays"])

    def test_any_writer_work_on_exact_replay_invalidates_capacity(self):
        jobs = [job_fixture(1), job_fixture(2)]
        with tempfile.TemporaryDirectory() as temporary, service(jobs, replay_writer=True) as (url, _), redirect_stdout(io.StringIO()):
            root = Path(temporary)
            write_corpus(root / "corpus", jobs)
            output = root / "failed"
            self.assertEqual(1, bench.main(["measure", "--corpus-dir", str(root / "corpus"),
                                           "--output-dir", str(output), "--api-base", url]))
            report = json.loads((output / "receipt.json").read_text())
            self.assertFalse(report["measurement"]["exactReplayControlsPassed"])
            self.assertEqual("unqualified", report["measurement"]["targetVerdict"])
            self.assertIn("exact replay performed", report["error"])

    def test_cutoffs_incomplete_games_wrong_opponents_or_changed_engines_are_not_corpus(self):
        for mutation in ("cutoff", "result", "indices", "opponents", "artifact", "auto-ingested"):
            with self.subTest(mutation=mutation):
                job = job_fixture(1)
                data = job["experiment"]
                if mutation == "cutoff": data["command"]["arguments"] += ["-maxmoves", "12"]
                elif mutation == "result": data["games"][0]["result"] = "1-0 (Black loses on time)"
                elif mutation == "indices": data["games"][1]["index"] = data["games"][0]["index"]
                elif mutation == "opponents": data["command"]["arguments"][1] = "name=Stockfish"
                elif mutation == "artifact": data["artifactsAfterMatch"]["Laplace"]["sha256"] = "f" * 64
                else: data["ingested"] = True
                with self.assertRaises(ValueError):
                    bench.source_context(data, job["jobId"])

    def test_corpus_bytes_context_and_duplicate_jobs_cannot_change_after_preparation(self):
        for mutation in ("pgn", "context", "duplicate"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                jobs = [job_fixture(1), job_fixture(2)]
                corpus = Path(temporary) / "corpus"
                write_corpus(corpus, jobs)
                manifest = json.loads((corpus / "corpus.json").read_text())
                if mutation == "pgn":
                    (corpus / jobs[0]["jobId"] / "games.pgn").write_bytes(b"changed")
                else:
                    if mutation == "context": manifest["jobs"][0]["generation"]["games"] = 99
                    else: manifest["jobs"][1] = copy.deepcopy(manifest["jobs"][0])
                    bench.save(corpus / "corpus.json", manifest)
                with self.assertRaises(ValueError):
                    bench.load_corpus(corpus)

    def test_one_occurrence_cannot_be_counted_twice_across_jobs(self):
        job = job_fixture(1)
        result = bench.retained.validate(job["fresh"], job["experiment"], job["experimentBytes"],
            job["pgn"], job["jobId"], 2, 1, replay=False)
        result["writer"] = job["fresh"]["recording"]["writer"]
        total = {"newlyRecordedPlayings": 0, "pliesReadback": 0, "sumServiceSeconds": 0.0,
                 "writer": {key: 0 for key in bench.WRITER_COUNTS}}
        games, seen = [], set()
        bench.add_fresh(total, games, result, seen)
        with self.assertRaisesRegex(ValueError, "PLAYING occurrence repeats"):
            bench.add_fresh(total, games, result, seen)
        self.assertEqual(2, total["newlyRecordedPlayings"])
        self.assertEqual(3, total["writer"]["entitiesInserted"])

    def test_qualification_requires_one_long_window_variety_and_zero_replay_proof(self):
        jobs = [job_fixture(1), job_fixture(2)]
        games = jobs[0]["fresh"]["recording"]["games"] + jobs[1]["fresh"]["recording"]["games"]
        total = {"newlyRecordedPlayings": 4, "pliesReadback": 240,
                 "sumServiceSeconds": 10, "writer": {}}
        accepted = bench.qualify(total, games, 40, 30, .05, True)
        self.assertEqual(.1, accepted["observedNewPlayingsPerSecond"])
        self.assertEqual("met", accepted["targetVerdict"])
        self.assertEqual("below-target", bench.qualify(total, games, 40, 30, 2500, True)["targetVerdict"])
        self.assertEqual("unqualified", bench.qualify(total, games, 20, 30, .05, True)["targetVerdict"])
        self.assertEqual("unqualified", bench.qualify(total, games, 40, 30, .05, False)["targetVerdict"])
        same_line = copy.deepcopy(games)
        for game in same_line[2:]:
            game.update(lineId=same_line[0]["lineId"], moveIds=same_line[0]["moveIds"])
        actual = bench.qualify(total, same_line, 40, 30, .05, True)
        self.assertFalse(actual["variedOrderedLineCorpus"])
        self.assertEqual("unqualified", actual["targetVerdict"])

    def test_shared_counts_rebase_only_from_later_fresh_observations_and_never_from_replay(self):
        first, second = job_fixture(1), job_fixture(2)
        baseline = bench.retained.validate(first["fresh"], first["experiment"], first["experimentBytes"],
            first["pgn"], first["jobId"], 2, 1, replay=False)
        latest = {}
        for job in (first, second):
            bench.merge_scope_observations(latest, job["fresh"]["recording"]["replayScopes"])
        rebased = bench.rebase_scope(baseline, latest)
        self.assertEqual(baseline["games"], rebased["games"])
        self.assertEqual(7, baseline["scope"][(3, "3" * 32)])
        self.assertEqual(8, rebased["scope"][(3, "3" * 32)])
        valid = replay_receipt(first, 8)
        bench.retained.validate(valid, first["experiment"], first["experimentBytes"],
                                first["pgn"], first["jobId"], 2, 1, replay=True, previous=rebased)
        for during_replay in (True, False):
            invalid = replay_receipt(first, 9)
            if during_replay:
                invalid["recording"]["replayScopes"][0]["before"][2]["observationCount"] = 8
                invalid["recording"]["replayScopes"][0]["unchanged"] = False
            with self.subTest(during_replay=during_replay), self.assertRaises(ValueError):
                bench.retained.validate(invalid, first["experiment"], first["experimentBytes"],
                    first["pgn"], first["jobId"], 2, 1, replay=True, previous=rebased)

    def test_fresh_scope_snapshots_cannot_lose_rows_or_decrease_observations(self):
        first, second = job_fixture(1), job_fixture(2)
        for mutation in ("before-decrease", "after-decrease", "disappeared"):
            latest = {}
            bench.merge_scope_observations(latest, first["fresh"]["recording"]["replayScopes"])
            stage = copy.deepcopy(second["fresh"]["recording"]["replayScopes"][0])
            if mutation == "before-decrease": stage["before"][2]["observationCount"] = 6
            elif mutation == "after-decrease": stage["after"][2]["observationCount"] = 6
            else: stage["before"].pop()
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                bench.merge_scope_observations(latest, [stage])
        with self.assertRaisesRegex(ValueError, "lacks"):
            bench.rebase_scope({"scope": {(3, "a" * 32): 1}}, {})

    def test_short_duration_and_missing_replay_controls_are_rejected_before_admission(self):
        for extra in (["--minimum-seconds", "1"], ["--replays", "0"]):
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as temporary, redirect_stdout(io.StringIO()):
                output = Path(temporary) / "failed"
                with patch.object(bench.retained, "DeadlineClient") as client:
                    self.assertEqual(1, bench.main(["measure", "--corpus-dir", "/unused",
                                                   "--output-dir", str(output), *extra]))
                    client.assert_not_called()

    def test_interruption_stops_only_this_collectors_unfinished_generation(self):
        owned = "a" * 32
        stopped = []
        class Client:
            def __init__(self, *_): pass
            def remaining(self): return 10
            def request(self, path, data=None, **_):
                if path == "/chess/lab/start": return {"jobId": owned}
                signal.raise_signal(signal.SIGTERM)
            def stop_owned(self, job_id):
                stopped.append(job_id)
                return {"stopped": True}
        previous = signal.getsignal(signal.SIGTERM)
        with tempfile.TemporaryDirectory() as temporary, patch.object(bench.retained, "DeadlineClient", Client), redirect_stdout(io.StringIO()):
            output = Path(temporary) / "failed"
            self.assertEqual(1, bench.main(["prepare", "--output-dir", str(output)]))
            report = json.loads((output / "corpus.json").read_text())
            self.assertEqual("failed", report["status"])
            self.assertEqual("KeyboardInterrupt", report["error"])
        self.assertEqual([owned], stopped)
        self.assertEqual(previous, signal.getsignal(signal.SIGTERM))


if __name__ == "__main__":
    unittest.main()
