#!/usr/bin/env python3
"""Receipt-only controls; these fixtures do not claim actual PG execution."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import signal
import threading
import tempfile
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest.mock import patch


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


bench = load("retained_ingestion", "benchmark-retained-chess-ingestion.py")
existing = load("recorded_fixture", "test-recorded-chess-benchmark.py")


class RetainedIngestionTests(unittest.TestCase):
    def test_sigterm_retains_failure_and_stops_only_the_owned_unfinished_match(self):
        stopped = []
        owned = "a" * 32
        class Client:
            def __init__(self, *_): pass
            def remaining(self): return 10
            def request(self, path, data=None, raw=False):
                if path == "/chess/lab/start": return {"jobId": owned}
                signal.raise_signal(signal.SIGTERM)
            def stop_owned(self, job_id):
                stopped.append(job_id); return {"stopped": True}
        prior = signal.getsignal(signal.SIGTERM)
        with tempfile.TemporaryDirectory() as directory, patch.object(bench, "DeadlineClient", Client):
            output = Path(directory) / "measurement"
            self.assertEqual(1, bench.main(["--output-dir", str(output)]))
            receipt = json.loads((output / "receipt.json").read_text())
            self.assertEqual("failed", receipt["status"])
            self.assertEqual("KeyboardInterrupt", receipt["error"])
        self.assertEqual([owned], stopped)
        self.assertEqual(prior, signal.getsignal(signal.SIGTERM))

    def test_one_deadline_clips_each_request_and_forbids_expired_work(self):
        observed = []
        client = bench.DeadlineClient("http://127.0.0.1:8080", 30, 100)
        def request(instance, path, data=None, raw=False, maximum_bytes=bench.transport.MAX_BYTES):
            observed.append((path, instance.timeout)); return {"ok": True}
        with patch.object(bench.transport.Client, "request", request), patch.object(bench.time, "monotonic", side_effect=[97, 98]):
            client.request("/ingest", {})
        self.assertEqual([("/ingest", 3)], observed)
        with patch.object(bench.transport.Client, "request", request), patch.object(bench.time, "monotonic", return_value=101):
            with self.assertRaisesRegex(ValueError, "whole-run"):
                client.request("/replay", {})
        self.assertEqual(1, len(observed))
        with patch.object(bench.transport.Client, "request", request), patch.object(bench.time, "monotonic", return_value=101):
            client.stop_owned("owned")
        self.assertEqual(("/chess/lab/stop/owned", 5), observed[-1])

    def test_real_http_failure_retains_new_service_receipt_without_retry_or_error_body(self):
        self.assert_http_failure_receipt({"status": "failed", "error": "exact witness failure"})

    def test_real_http_early_validation_failure_collects_receipt_without_recording(self):
        self.assert_http_failure_receipt(None)

    def assert_http_failure_receipt(self, recording):
        job_id = "a" * 32
        old, new = "ingest-" + "1" * 32 + ".json", "ingest-" + "2" * 32 + ".json"
        requests = []
        body = json.dumps({"schema": "laplace.chess-retained-ingestion/v1", "jobId": job_id,
                           "status": "failed", "disposition": "failed", "error": "service validation failed",
                           "newlyRecordedGames": None, "alreadyPresentGames": None,
                           "recording": recording}).encode()
        class Handler(BaseHTTPRequestHandler):
            failed = False
            def log_message(self, *_): pass
            def send(self, status, payload):
                self.send_response(status); self.send_header("Content-Length", str(len(payload)))
                self.end_headers(); self.wfile.write(payload)
            def do_POST(self):
                requests.append(("POST", self.path)); Handler.failed = True
                self.rfile.read(int(self.headers.get("Content-Length", "0")))
                self.send(500, b"untrusted HTTP error body must not enter evidence")
            def do_GET(self):
                requests.append(("GET", self.path))
                if self.path.endswith("/artifact/" + new): self.send(200, body)
                else:
                    artifacts = {old: "/server/old"}
                    if Handler.failed: artifacts[new] = "/server/new"
                    self.send(200, json.dumps({"id": job_id, "artifacts": artifacts}).encode())
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            client = bench.DeadlineClient(f"http://127.0.0.1:{server.server_port}", 5, bench.time.monotonic() + 10)
            with tempfile.TemporaryDirectory() as directory:
                output, report = Path(directory), {"attempts": []}
                with self.assertRaisesRegex(ValueError, "HTTP 500"):
                    bench.request_ingestion(client, output, report, job_id, 0)
                self.assertEqual(body, (output / new).read_bytes())
                self.assertFalse((output / old).exists())
                self.assertEqual([], report["attempts"])
                self.assertTrue(report["failedIngestion"]["collectionCompleted"])
                artifact = report["failedIngestion"]["artifacts"][0]
                self.assertEqual(hashlib.sha256(body).hexdigest(), artifact["sha256"])
                self.assertEqual("failed", artifact["serviceStatus"])
                self.assertEqual("service validation failed", artifact["serviceError"])
                self.assertEqual(recording["status"] if recording is not None else None, artifact["recordingStatus"])
                self.assertNotIn("untrusted HTTP error body", json.dumps(report))
                self.assertEqual(1, sum(method == "POST" for method, _ in requests))
        finally:
            server.shutdown(); server.server_close(); thread.join()

    def test_failure_evidence_cannot_override_original_error_or_escape_receipt_scope(self):
        job_id, requests = "a" * 32, []
        class Client:
            def request(self, path, data=None, **_):
                requests.append(path)
                if path.endswith("/ingest"): raise ValueError("original HTTP 500")
                if len(requests) == 1: return {"id": job_id, "artifacts": {}}
                raise ValueError("recovery deadline expired")
        with tempfile.TemporaryDirectory() as directory:
            report = {"attempts": []}
            with self.assertRaisesRegex(ValueError, "original HTTP 500"):
                bench.request_ingestion(Client(), Path(directory), report, job_id, 1)
            self.assertFalse(report["failedIngestion"]["collectionCompleted"])
            self.assertEqual("recovery deadline expired", report["failedIngestion"]["collectionError"])
            self.assertEqual([], report["attempts"])
        self.assertEqual(set(), bench.ingestion_artifacts(
            {"id": job_id, "artifacts": {"../ingest-" + "1" * 32 + ".json": "/irrelevant"}}, job_id))
        with self.assertRaisesRegex(ValueError, "identity"):
            bench.ingestion_artifacts({"id": "b" * 32, "artifacts": {}}, job_id)

    def test_failed_receipt_inventory_and_body_are_bounded_and_bound_to_the_job(self):
        job_id, name = "a" * 32, "ingest-" + "1" * 32 + ".json"
        with self.assertRaisesRegex(ValueError, "retention bound"):
            bench.ingestion_artifacts({"id": job_id,
                "artifacts": {f"ingest-{n:032x}.json": "ignored" for n in range(33)}}, job_id)
        for wrong_identity in (False, True):
            calls = []
            class Client:
                def request(self, path, data=None, **kwargs):
                    calls.append((path, kwargs.get("maximum_bytes")))
                    if path.endswith("/ingest"): raise ValueError("original HTTP 500")
                    if "/artifact/" in path:
                        if not wrong_identity: raise ValueError("API artifact exceeds collector byte envelope")
                        return json.dumps({"schema": "laplace.chess-retained-ingestion/v1", "jobId": "b" * 32}).encode()
                    return {"id": job_id, "artifacts": {} if len(calls) == 1 else {name: "ignored"}}
            with tempfile.TemporaryDirectory() as directory:
                report = {}
                with self.assertRaisesRegex(ValueError, "original HTTP 500"):
                    bench.request_ingestion(Client(), Path(directory), report, job_id, 0)
                self.assertFalse(report["failedIngestion"]["collectionCompleted"])
                self.assertEqual(bench.MAX_FAILURE_EVIDENCE_BYTES, calls[-1][1])

    def test_request_finishing_after_deadline_is_not_accepted(self):
        client = bench.DeadlineClient("http://127.0.0.1:8080", 30, 100)
        with patch.object(bench.transport.Client, "request", return_value={}), \
                patch.object(bench.time, "monotonic", side_effect=[99, 101]):
            with self.assertRaisesRegex(ValueError, "whole-run"):
                client.request("/artifact")

    def fixture(self):
        recording, experiment, job, request, pgn, _ = existing.RecordedChessTests().fixture()
        experiment["ingested"] = None
        raw_experiment = json.dumps(experiment).encode()
        recording["experimentArtifactSha256"] = hashlib.sha256(raw_experiment).hexdigest()
        recording["purpose"] = "retained-pgn-ingestion"
        recording["elapsedSeconds"] = dict(total=1, play=0, recording=.2, commit=.3, readback=.2, overhead=.3)
        after = [{"kind": 1, "id": "1" * 32, "observationCount": 0},
                 {"kind": 2, "id": "2" * 32, "observationCount": 0},
                 {"kind": 3, "id": "3" * 32, "observationCount": 7}]
        recording["replayScopes"] = [{"entityIds": ["1" * 32], "physicalityIds": ["2" * 32],
            "witnessIds": ["3" * 32], "before": [], "after": after, "unchanged": False}]
        receipt = {"schema": "laplace.chess-retained-ingestion/v1", "jobId": job["id"],
            "serviceElapsedSeconds": 2, "newlyRecordedGames": 2, "alreadyPresentGames": 0,
            "noOpReplayVerified": False, "disposition": "fresh", "recording": recording}
        return receipt, experiment, raw_experiment, pgn, job["id"], 2, 3

    def test_absent_recording_cannot_be_accepted_as_zero_recorded_games(self):
        args = self.fixture()
        args[0]["recording"] = None
        with self.assertRaisesRegex(ValueError, "retained native readback did not complete"):
            bench.validate(*args, replay=False)

    def replay(self):
        args = self.fixture()
        baseline = bench.validate(*args, replay=False)
        receipt = copy.deepcopy(args[0]); recording = receipt["recording"]
        receipt.update(newlyRecordedGames=0, alreadyPresentGames=2, noOpReplayVerified=True, disposition="replay")
        recording.update(novelGames=0, appliedGames=0, durability=None)
        recording["writer"] = {k: (v if isinstance(v, str) else 0) for k, v in recording["writer"].items()}
        scope = recording["replayScopes"][0]
        scope.update(before=copy.deepcopy(scope["after"]), unchanged=True)
        return (receipt, *args[1:]), baseline

    def test_fresh_admission_counts_include_service_overhead(self):
        actual = bench.validate(*self.fixture(), replay=False)
        self.assertEqual(1, actual["metrics"]["newlyRecordedGamesPerSecondService"])
        self.assertEqual(2 / 3, actual["metrics"]["processedGamesPerSecondCollector"])

    def test_exact_replay_is_processed_work_with_zero_new_recording(self):
        args, baseline = self.replay()
        actual = bench.validate(*args, replay=True, previous=baseline)
        self.assertEqual(0, actual["metrics"]["newlyRecordedGamesPerSecondService"])
        self.assertEqual(1, actual["metrics"]["processedGamesPerSecondService"])

    def test_same_line_first_admission_and_exact_replay_keep_distinct_denominators(self):
        args, baseline = self.replay()
        replay = bench.validate(*args, replay=True, previous=baseline)
        first_metrics, replay_metrics = baseline["metrics"], replay["metrics"]
        self.assertEqual(2, first_metrics["newlyRecordedPlayings"])
        self.assertEqual(0, replay_metrics["newlyRecordedPlayings"])
        self.assertEqual(first_metrics["contentInventory"], replay_metrics["contentInventory"])
        inventory = replay_metrics["contentInventory"]
        self.assertEqual(2, inventory["playings"])
        self.assertEqual(1, inventory["distinctStartPositions"])
        self.assertEqual(1, inventory["distinctLines"])
        self.assertEqual(1, inventory["distinctOrderedLines"])
        self.assertFalse(inventory["multipleStartPositionsAndLines"])
        self.assertEqual(1, first_metrics["newlyRecordedGamesPerSecondService"])
        self.assertEqual(0, replay_metrics["newlyRecordedGamesPerSecondService"])
        self.assertEqual(1, replay_metrics["processedGamesPerSecondService"])

    def test_replay_count_growth_cannot_hide_behind_same_witness_id(self):
        args, baseline = self.replay()
        args[0]["recording"]["replayScopes"][0]["after"][2]["observationCount"] += 1
        with self.assertRaisesRegex(ValueError, "unchanged flag|grew"):
            bench.validate(*args, replay=True, previous=baseline)

    def test_correlated_changed_snapshots_cannot_replace_prior_committed_baseline(self):
        args, baseline = self.replay()
        for side in ("before", "after"):
            args[0]["recording"]["replayScopes"][0][side][2]["observationCount"] += 1
        with self.assertRaisesRegex(ValueError, "replay changed"):
            bench.validate(*args, replay=True, previous=baseline)

    def test_replay_rejects_fresh_commit_claim_or_writer_work(self):
        for mutation in ("durability", "applyCalls", "entitiesInserted", "attestationsAttempted", "copyTransactionsCommitted"):
            with self.subTest(mutation=mutation):
                args, baseline = self.replay()
                if mutation == "durability": args[0]["recording"][mutation] = {"writeCommitAcknowledged": True}
                else: args[0]["recording"]["writer"][mutation] = 1
                with self.assertRaises(ValueError): bench.validate(*args, replay=True, previous=baseline)

    def test_native_identity_or_source_byte_changes_reject(self):
        for mutation in ("games", "pgn", "experimentArtifactSha256"):
            with self.subTest(mutation=mutation):
                args, baseline = self.replay()
                recording = args[0]["recording"]
                if mutation == "games": recording["games"][0]["moveIds"][0] = "9" * 32
                elif mutation == "pgn": recording["pgn"]["bytes"] += 1
                else: recording[mutation] = "0" * 64
                with self.assertRaises(ValueError): bench.validate(*args, replay=True, previous=baseline)

    def test_wrong_scope_missing_rows_duplicate_or_boolean_count_reject(self):
        for mutation in ("missing", "duplicate", "boolean", "scope"):
            with self.subTest(mutation=mutation):
                args = self.fixture(); scope = args[0]["recording"]["replayScopes"][0]
                if mutation == "missing": scope["after"].pop()
                elif mutation == "duplicate": scope["after"].append(scope["after"][0])
                elif mutation == "boolean": scope["after"][2]["observationCount"] = True
                else: scope["entityIds"] = ["9" * 32]
                with self.assertRaises(ValueError): bench.validate(*args, replay=False)

    def test_replay_does_not_accept_boolean_or_novel_game_count(self):
        args, baseline = self.replay()
        args[0]["newlyRecordedGames"] = False
        with self.assertRaises(ValueError): bench.validate(*args, replay=True, previous=baseline)

    def test_normal_game_cutoff_and_missing_wal_proof_reject(self):
        args = self.fixture()
        args[0]["recording"]["durability"]["fsync"] = False
        with self.assertRaises(ValueError): bench.validate(*args, replay=False)

    def test_retained_request_reuses_full_game_orchestration_without_cutoff(self):
        request = bench.transport.game_request(16, 4, 2, 1, 16)
        request["config"]["ingest"] = False
        self.assertTrue(request["config"]["persistPgn"])
        self.assertFalse(request["config"]["ingest"])
        self.assertNotIn("maxmoves", request["config"])


if __name__ == "__main__":
    unittest.main()
