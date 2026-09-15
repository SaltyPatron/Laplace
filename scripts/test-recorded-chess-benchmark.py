#!/usr/bin/env python3
"""Exercise the API measurement boundary and rejection of unrecorded results."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import time
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest import mock
import unittest

spec = importlib.util.spec_from_file_location("recorded_chess", Path(__file__).with_name("benchmark-recorded-chess.py"))
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class RecordedChessTests(unittest.TestCase):
    def test_default_api_uses_managed_port_and_preserves_explicit_environment(self):
        with mock.patch.dict(bench.os.environ, {}, clear=True):
            self.assertEqual("http://127.0.0.1:5187", bench.default_api_base())
        with mock.patch.dict(bench.os.environ, {"LAPLACE_API_BASE": "http://configured-host:6123"}):
            self.assertEqual("http://configured-host:6123", bench.default_api_base())

    def test_default_case_count_envelope_can_admit_the_sustained_target(self):
        for override in ([], ["--max-sustained-cases", "256"]):
            with self.subTest(override=override), tempfile.TemporaryDirectory() as root, \
                 mock.patch.object(bench, "Client", side_effect=ValueError("controlled pre-network stop")):
                self.assertEqual(1, bench.main(["--output-dir", root, "--duration-seconds", "30", *override]))
                envelope = json.loads((Path(root) / "receipt.json").read_text())["targetResourceEnvelope"]
                self.assertEqual(24, envelope["gamesPerCase"])
                self.assertEqual(75000, envelope["minimumGamesAtTargetDuration"])
                self.assertEqual(not override, envelope["caseCountAllowsTargetAtMinimumDuration"])
                if not override:
                    self.assertEqual(4096, envelope["maximumSustainedCases"])
                    self.assertEqual(98304, envelope["maximumSustainedGames"])
                    self.assertAlmostEqual(3276.8, envelope["maximumRateAllowedByCaseCountAtMinimumDuration"])
                else:
                    self.assertAlmostEqual(204.8, envelope["maximumRateAllowedByCaseCountAtMinimumDuration"])

    def fixture(self):
        request = bench.game_request(2, 4, 1, 1, 16)
        pgn = b"fixture: native parser and hydrator own game semantics\n"
        job = {"id": "match-1", "state": "Completed", "artifacts": {
            name: name for name in ("experiment.json", "recording.json", "games.pgn", "transcript.log")}}
        experiment = {"experimentId": "match-1", "pgnEvent": "chess-lab/cutechess/match-1",
            "matchState": "Completed", "ingested": True, "artifactIdentitiesUnchanged": True,
            "requestedOptions": {"rounds": 2, "depth": 4, "concurrency": 1, "stockfishLimitStrength": False,
                                 "stockfishThreads": 1, "stockfishHashMb": 16},
            "command": {"arguments": ["-each", "tc=inf", "depth=4"]}}
        recording = {"schema": "laplace.chess-recording/v2", "status": "completed",
            "durability": {"synchronousCommit": "on", "fsync": True, "fullPageWrites": True,
                           "writeCommitAcknowledged": True, "localWalFlushAcknowledged": True},
            "experimentId": "match-1", "pgnEvent": "chess-lab/cutechess/match-1",
            **{name: 2 for name in ("requestedGames", "parsedGames", "novelGames", "appliedGames", "committedGames", "readbackGames")},
            "readbackPlies": 120, "pgn": {"bytes": len(pgn), "sha256": hashlib.sha256(pgn).hexdigest()},
            "experimentReceiptSha256": "b" * 64,
            "verification": {name: True for name in ("uniquePlayingIds", "exactGameBodies", "exactWitnessMembership", "exactExperimentBody", "completedGames")},
            "elapsedSeconds": {"total": 2, "play": 1, "recording": .2, "commit": .3, "readback": .2, "overhead": .3},
            "games": [{"playingId": str(i) * 32, "lineId": "3" * 32, "startPositionId": "4" * 32,
                       "whitePlayerId": "5" * 32, "blackPlayerId": "6" * 32,
                       "result": "1-0", "moveIds": ["7" * 32] * 60} for i in (1, 2)],
            "writer": {name: 0 for name in ("applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted", "physicalitiesInserted", "attestationsAttempted", "attestationsInserted", "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge", "roundTrips", "journalReplayHits")}}
        recording["writer"].update(applyCalls=1, roundTripsKind="logical-writer-accounting/v1",
                                   copyTransactionsStarted=1, copyTransactionsCommitted=1)
        experiment_bytes = json.dumps(experiment).encode()
        recording["experimentArtifactSha256"] = hashlib.sha256(experiment_bytes).hexdigest()
        return recording, experiment, job, request, pgn, experiment_bytes

    def test_rate_uses_complete_collector_wall_and_verified_records(self):
        result = bench.validate_recording(*self.fixture(), 4)
        self.assertEqual(1, result["gamesPerSecondServerWorkflow"])
        self.assertEqual(60, result["pliesPerSecondServerWorkflow"])
        self.assertEqual(.5, result["gamesPerSecondEndToEnd"])
        self.assertEqual(4, result["gamesPerSecondRecording"])
        self.assertAlmostEqual(2 / .7, result["gamesPerSecondRecordingAndReadback"])
        self.assertEqual(120, result["verifiedRecordedPlies"])

    def test_poll_and_transfer_delay_do_not_cap_server_throughput_or_disappear(self):
        data = self.fixture()
        data[0]["elapsedSeconds"] = {"total": .0005, "play": .0002, "recording": .0001,
                                    "commit": .0001, "readback": .00005, "overhead": .00005}
        cases = [dict(bench.validate_recording(*data, delay), status="passed", concurrency=1)
                 for delay in (.2, .3, .4)]
        rates = bench.summarize_rates(cases, [1], 2500)
        self.assertEqual(4000, rates["medianRatesByConcurrency"]["gamesPerSecondServerWorkflow"]["1"])
        self.assertAlmostEqual(2 / .3, rates["medianRatesByConcurrency"]["gamesPerSecondEndToEnd"]["1"])
        self.assertEqual("gamesPerSecondServerWorkflow", rates["targetMetric"])
        self.assertTrue(rates["sampleTargetRateReached"])
        self.assertFalse(rates["targetMet"])
        self.assertFalse(rates["durationQualified"])
        self.assertFalse(rates["collectorEndToEndTargetMet"])
        cases[0]["status"] = "failed"
        with self.assertRaises(ValueError):
            bench.summarize_rates(cases, [1], 2500)

    def test_request_uses_normal_full_game_ingestion_without_ply_cutoff(self):
        request = bench.game_request(24, 4, 4, 1, 16)
        self.assertTrue(request["config"]["ingest"])
        self.assertTrue(request["config"]["persistPgn"])
        self.assertFalse(request["config"]["limitStrength"])
        self.assertNotIn("maxPlies", request["config"])
        with self.assertRaises(ValueError):
            bench.game_request(3, 4, 1, 1, 16)

    def test_durability_requires_actual_settings_and_fresh_write_acknowledgement(self):
        for field in ("fsync", "fullPageWrites", "writeCommitAcknowledged", "localWalFlushAcknowledged"):
            for invalid in (False, None, 1, "true"):
                with self.subTest(field=field, invalid=invalid):
                    data = self.fixture()
                    data[0]["durability"][field] = invalid
                    with self.assertRaises(ValueError):
                        bench.validate_recording(*data, 4)
        for mode in ("off", "remote_write", None):
            data = self.fixture()
            data[0]["durability"]["synchronousCommit"] = mode
            with self.assertRaises(ValueError):
                bench.validate_recording(*data, 4)
        data = self.fixture()
        data[0]["schema"] = "laplace.chess-recording/v1"
        with self.assertRaises(ValueError):
            bench.validate_recording(*data, 4)
        data = self.fixture()
        del data[0]["durability"]
        with self.assertRaises(ValueError):
            bench.validate_recording(*data, 4)

    def test_copy_counters_cannot_be_missing_or_replace_the_logical_round_trip_label(self):
        for field, value in (("copyTransactionsStarted", 0), ("copyTransactionsCommitted", 0),
                             ("copyTransactionsCommitted", 2), ("copyTransactionsStarted", True),
                             ("roundTripsKind", "physical-network-trips"), ("applyCalls", 0)):
            data = self.fixture()
            data[0]["writer"][field] = value
            with self.assertRaises(ValueError):
                bench.validate_recording(*data, 4)

    def test_successful_play_without_committed_readback_cannot_claim_a_rate(self):
        for field in ("appliedGames", "committedGames", "readbackGames", "novelGames"):
            data = self.fixture()
            data[0][field] = 1
            with self.subTest(field=field), self.assertRaises(ValueError):
                bench.validate_recording(*data, 4)
        for field in ("uniquePlayingIds", "exactGameBodies", "exactWitnessMembership", "exactExperimentBody", "completedGames"):
            data = self.fixture()
            data[0]["verification"][field] = False
            with self.subTest(field=field), self.assertRaises(ValueError):
                bench.validate_recording(*data, 4)

    def test_receipt_provenance_pgn_and_executable_mutations_are_rejected(self):
        changes = [lambda d: d[0].update(experimentId="another-job"),
                   lambda d: d[0]["pgn"].update(sha256="0" * 64),
                   lambda d: d[1].update(artifactIdentitiesUnchanged=False),
                   lambda d: d[1]["requestedOptions"].update(stockfishThreads=2),
                   lambda d: d[1]["requestedOptions"].update(stockfishHashMb=64),
                   lambda d: d[0].update(experimentArtifactSha256="f" * 64),
                   lambda d: d[0]["games"][1].update(playingId=d[0]["games"][0]["playingId"]),
                   lambda d: d[0]["games"][0].update(result="*"),
                   lambda d: d[0]["elapsedSeconds"].update(commit=float("nan")),
                   lambda d: d[0]["elapsedSeconds"].update(commit=3),
                   lambda d: d[1]["command"]["arguments"].extend(["-maxmoves", "12"])]
        for mutate in changes:
            data = self.fixture()
            mutate(data)
            with self.subTest(mutation=changes.index(mutate)), self.assertRaises(ValueError):
                bench.validate_recording(*data, 4)

    def test_api_sequence_retains_artifacts_and_checks_receipt(self):
        recording, experiment, job, request, pgn, experiment_bytes = self.fixture()
        recording["elapsedSeconds"] = {key: .00001 for key in recording["elapsedSeconds"]}
        payloads = {"recording.json": json.dumps(recording).encode(),
                    "experiment.json": experiment_bytes, "games.pgn": pgn,
                    "transcript.log": b"observed engine transcript\n"}
        calls = []

        class Client:
            def request(self, path, data=None, raw=False, maximum_bytes=bench.MAX_BYTES):
                calls.append((path, data, raw))
                if path == "/chess/lab/start":
                    return {"jobId": "match-1"}
                if path == "/chess/lab/jobs/match-1":
                    return job
                return payloads[path.rsplit("/", 1)[-1]]

        with tempfile.TemporaryDirectory() as root:
            result = bench.run_case(Client(), Path(root) / "case", request, 5, .01)
            self.assertEqual("passed", result["status"], result)
            self.assertEqual(pgn, (Path(root) / "case/games.pgn").read_bytes())
            self.assertEqual(hashlib.sha256(pgn).hexdigest(), result["artifacts"]["games.pgn"]["sha256"])
            self.assertEqual(("/chess/lab/start", request, False), calls[0])

    def test_timeout_stops_only_the_job_it_started_and_retains_failure(self):
        calls = []

        class Client:
            def request(self, path, data=None, raw=False, maximum_bytes=bench.MAX_BYTES):
                calls.append(path)
                if path == "/chess/lab/start":
                    return {"jobId": "owned-job"}
                if path.endswith("/stop/owned-job"):
                    return {"stopped": True}
                return {"id": "owned-job", "state": "Running"}

        with tempfile.TemporaryDirectory() as root:
            result = bench.run_case(Client(), Path(root) / "case", self.fixture()[3], .001, .002)
            self.assertEqual("failed", result["status"])
            self.assertIn("timed out", result["error"])
            self.assertNotIn("gamesPerSecondEndToEnd", result)
            self.assertEqual("/chess/lab/stop/owned-job", calls[-1])
            self.assertEqual("failed", json.loads((Path(root) / "case/receipt.json").read_text())["status"])

    def test_interrupt_or_swapped_job_cancels_original_owned_job(self):
        for response in (KeyboardInterrupt(), {"id": "unrelated", "state": "Completed"}):
            calls = []

            class Client:
                def request(self, path, data=None, raw=False, maximum_bytes=bench.MAX_BYTES):
                    calls.append(path)
                    if path == "/chess/lab/start":
                        return {"jobId": "owned-job"}
                    if path.endswith("/stop/owned-job"):
                        return {"stopped": True}
                    if isinstance(response, BaseException):
                        raise response
                    return response

            with self.subTest(response=response), tempfile.TemporaryDirectory() as root:
                result = bench.run_case(Client(), Path(root) / "case", self.fixture()[3], 2, .01)
                self.assertEqual("failed", result["status"])
                self.assertNotIn("gamesPerSecondEndToEnd", result)
                self.assertEqual("/chess/lab/stop/owned-job", calls[-1])

    def sustained(self, *, durations=(20, 20), duration=30, deadline=500, max_cases=10,
                  duplicate=False, fail_at=None, games_per_case=1, checkpoint_seconds=2):
        clock = [0.0]
        calls = []
        result = {}
        def case(client, directory, request, timeout, poll, budget, artifact_limit):
            index = len(calls)
            calls.append((request, timeout))
            clock[0] += durations[min(index, len(durations)-1)]
            identity = 0 if duplicate else index
            return {"status": "failed" if index == fail_at else "passed",
                    "jobId": f"job-{identity}", "verifiedPlayingIds": [f"game-{identity}-{n}" for n in range(games_per_case)],
                    "verifiedRecordedGames": games_per_case, "verifiedRecordedPlies": 80 * games_per_case,
                    "elapsedSeconds": {"total": 1}, "writer": {"applyCalls": 1}}
        def checkpoint():
            clock[0] += checkpoint_seconds  # Real coordinator work belongs in the aggregate denominator.
        with mock.patch.object(bench.time, "monotonic", side_effect=lambda: clock[0]), \
             mock.patch.object(bench, "run_case", side_effect=case):
            bench.run_sustained(None, Path("unused"), bench.game_request(2, 4, 1, 1, 16),
                duration, deadline, 600, .2, max_cases, {"limit": 100, "used": 0},
                50, set(), set(), result, checkpoint)
        return result, calls

    def test_sustained_denominator_encloses_jobs_and_bookkeeping_instead_of_stage_sum(self):
        result, calls = self.sustained()
        self.assertEqual("passed", result["status"])
        self.assertTrue(result["durationQualified"])
        self.assertEqual(2, result["completedCases"])
        self.assertEqual(46, result["elapsedSeconds"])
        self.assertAlmostEqual(2 / 46, result[bench.SUSTAINED_METRIC])
        self.assertAlmostEqual(86400 * 2 / 46, result["gamesPerDayExtrapolated"])
        self.assertEqual(2, result["writerTotals"]["applyCalls"])
        self.assertFalse(result["targetMet"])
        self.assertTrue(all(call[0]["config"]["rounds"] == 2 for call in calls))

    def test_sustained_requires_multiple_complete_new_jobs_and_retains_failed_partial(self):
        result, calls = self.sustained(durations=(40, 40))
        self.assertEqual(2, len(calls))
        self.assertTrue(result["durationQualified"])
        for kwargs in ({"duplicate": True}, {"fail_at": 1}):
            with self.subTest(kwargs=kwargs):
                result, calls = self.sustained(**kwargs)
                self.assertEqual("failed", result["status"])
                self.assertFalse(result["durationQualified"])
                self.assertFalse(result["targetMet"])
                self.assertEqual(1, result["verifiedRecordedGames"])
                self.assertEqual(2, len(result["cases"]))

    def test_sustained_case_and_total_deadline_envelopes_stop_without_new_job(self):
        result, calls = self.sustained(duration=100, max_cases=2)
        self.assertEqual(2, len(calls))
        self.assertIn("case envelope", result["error"])
        result, calls = self.sustained(duration=100, deadline=24)
        self.assertEqual(1, len(calls))
        self.assertEqual(22, calls[0][1])
        self.assertIn("deadline", result["error"])
        self.assertFalse(result["targetMet"])

    def test_final_case_bookkeeping_cannot_cross_total_deadline_and_claim_success(self):
        result, calls = self.sustained(deadline=45)
        self.assertEqual(2, len(calls))
        self.assertEqual(2, result["verifiedRecordedGames"])
        self.assertEqual(46, result["elapsedSeconds"])
        self.assertEqual("failed", result["status"])
        self.assertFalse(result["durationQualified"])
        self.assertFalse(result["targetMet"])
        self.assertIn("deadline", result["error"])

    def test_explicit_short_duration_cannot_qualify_capacity(self):
        result, _ = self.sustained(duration=1, durations=(.1, .1))
        self.assertFalse(result["durationQualified"])
        self.assertFalse(result["targetMet"])

    def test_target_requires_rate_and_duration_with_bounded_cases(self):
        result, calls = self.sustained(durations=(.1,), games_per_case=512,
                                      checkpoint_seconds=0, max_cases=512)
        self.assertGreaterEqual(len(calls), 300)
        self.assertTrue(result["durationQualified"])
        self.assertTrue(result["targetMet"])
        self.assertAlmostEqual(5120, result[bench.SUSTAINED_METRIC], places=6)

    def test_main_selects_fastest_collector_candidate_then_new_complete_cases(self):
        clock, requests = [0.0], []
        class Client:
            base = "http://127.0.0.1:5187"
            def request(self, *args, **kwargs):
                return {}
        def case(client, directory, request, timeout, poll, budget, limit):
            requests.append(request)
            index = len(requests)
            clock[0] += 20
            rate = {1: 1, 2: 3, 4: 2}[request["config"]["concurrency"]]
            return {"status": "passed", "jobId": str(index),
                "verifiedPlayingIds": [f"{index * 2:032x}", f"{index * 2 + 1:032x}"],
                "verifiedRecordedGames": 2, "verifiedRecordedPlies": 160,
                **{field: rate for field in bench.RATE_BOUNDARIES}, "writer": {"applyCalls": 1}}
        with tempfile.TemporaryDirectory() as root, \
             mock.patch.object(bench, "Client", return_value=Client()), \
             mock.patch.object(bench.time, "monotonic", side_effect=lambda: clock[0]), \
             mock.patch.object(bench, "run_case", side_effect=case), \
             mock.patch("builtins.print"):
            self.assertEqual(0, bench.main(["--output-dir", root, "--games", "2", "--repeats", "1",
                                           "--duration-seconds", "30", "--total-timeout", "200"]))
            result = json.loads((Path(root) / "receipt.json").read_text())
        self.assertEqual([1, 2, 4, 2, 2], [request["config"]["concurrency"] for request in requests])
        self.assertEqual(3, len(result["cases"]))
        self.assertEqual(4, result["sustained"]["verifiedRecordedGames"])
        self.assertEqual(40, result["sustained"]["elapsedSeconds"])
        self.assertEqual(100, result["collectorTotalWallSeconds"])
        self.assertTrue(result["durationQualified"])

    def test_invalid_workload_envelopes_make_no_api_call(self):
        for extra in (["--games", "75000"], ["--duration-seconds", "1"],
                      ["--duration-seconds", "nan"], ["--total-timeout", "inf"],
                      ["--max-sustained-cases", "4097"], ["--concurrency", "1,65"],
                      ["--max-total-artifact-bytes", "1"]):
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as root, \
                 mock.patch.object(bench, "Client", side_effect=AssertionError("invalid workload reached API")):
                self.assertEqual(1, bench.main(["--output-dir", root, *extra]))
                result = json.loads((Path(root) / "receipt.json").read_text())
                self.assertEqual("failed", result["status"])
                self.assertFalse(result["targetMet"])

    def test_cumulative_artifact_budget_is_enforced_before_writing(self):
        recording, experiment, job, request, pgn, experiment_bytes = self.fixture()
        limits = []
        class Client:
            def request(self, path, data=None, raw=False, maximum_bytes=bench.MAX_BYTES):
                if path == "/chess/lab/start":
                    return {"jobId": "match-1"}
                if path == "/chess/lab/jobs/match-1":
                    return job
                limits.append(maximum_bytes)
                return b"123456"  # Transport counterexample: second payload exceeds remaining budget.
        budget = {"limit": 10, "used": 0}
        with tempfile.TemporaryDirectory() as root:
            result = bench.run_case(Client(), Path(root) / "case", request, 5, .01, budget, 8)
            self.assertEqual("failed", result["status"])
            self.assertEqual([8, 4, 4, 4], limits)
            self.assertEqual(6, budget["used"])
            self.assertEqual(6, (Path(root) / "case/experiment.json").stat().st_size)
            self.assertFalse((Path(root) / "case/recording.json").exists())

    def test_partial_file_write_is_retained_and_charged_to_artifact_budget(self):
        _, _, job, request, _, _ = self.fixture()
        class Client:
            def request(self, path, data=None, raw=False, maximum_bytes=bench.MAX_BYTES):
                if path == "/chess/lab/start":
                    return {"jobId": "match-1"}
                if path == "/chess/lab/jobs/match-1":
                    return job
                return b"123"
        original = Path.write_bytes
        def partial_write(path, data):
            original(path, data[:2])
            raise OSError("controlled disk-write failure")
        budget = {"limit": 3, "used": 0}
        with tempfile.TemporaryDirectory() as root, mock.patch.object(Path, "write_bytes", partial_write):
            result = bench.run_case(Client(), Path(root) / "case", request, 5, .01, budget, 3)
            self.assertEqual("failed", result["status"])
            self.assertEqual(2, budget["used"])
            self.assertEqual(b"12", (Path(root) / "case/experiment.json.partial").read_bytes())
            self.assertFalse(result["artifacts"]["experiment.json"]["complete"])

    def test_deadline_covers_slow_progressing_actual_http_response(self):
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Length", "100")
                self.end_headers()
                try:
                    for _ in range(100):
                        self.wfile.write(b"x")
                        self.wfile.flush()
                        time.sleep(.01)
                except (BrokenPipeError, ConnectionResetError):
                    pass
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            client = bench.Client(f"http://127.0.0.1:{server.server_port}", .08)
            started = time.monotonic()
            with self.assertRaises(TimeoutError):
                client.request("/dribble", raw=True)
            self.assertLess(time.monotonic() - started, .5)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_artifact_timeout_is_not_swallowed_after_job_completed(self):
        calls = []
        class Client:
            def request(self, path, data=None, raw=False, maximum_bytes=bench.MAX_BYTES):
                calls.append(path)
                if path == "/chess/lab/start":
                    return {"jobId": "match-1"}
                if path == "/chess/lab/jobs/match-1":
                    return {"id": "match-1", "state": "Completed", "artifacts": {"experiment.json": "x", "games.pgn": "y"}}
                raise TimeoutError("deadline during artifact")
        with tempfile.TemporaryDirectory() as root:
            result = bench.run_case(Client(), Path(root) / "case", self.fixture()[3], 5, .01)
        self.assertEqual("failed", result["status"])
        self.assertEqual(3, len(calls))
        self.assertEqual("deadline during artifact", result["error"])

    def test_existing_evidence_directory_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "receipt.json"
            path.write_text("retained evidence")
            with self.assertRaises(SystemExit), mock.patch("sys.stderr"):
                bench.main(["--output-dir", root])
            self.assertEqual("retained evidence", path.read_text())


if __name__ == "__main__":
    unittest.main(verbosity=2)
