#!/usr/bin/env python3
"""Exercise the API measurement boundary and rejection of unrecorded results."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("recorded_chess", Path(__file__).with_name("benchmark-recorded-chess.py"))
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class RecordedChessTests(unittest.TestCase):
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
        recording = {"schema": "laplace.chess-recording/v1", "status": "completed",
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
        self.assertTrue(rates["targetMet"])
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
            def request(self, path, data=None, raw=False):
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
            def request(self, path, data=None, raw=False):
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
                def request(self, path, data=None, raw=False):
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
