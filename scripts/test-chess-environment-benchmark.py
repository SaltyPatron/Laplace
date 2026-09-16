#!/usr/bin/env python3
"""Resource admission, parsing and owned-process cleanup contracts for chess calibration."""
import argparse
import copy
import hashlib
import http.server
import importlib.util
import io
import json
import os
import re
import shutil
import tarfile
import types
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("chess_benchmark", ROOT / "scripts/benchmark-chess-environment.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)
PGN = bench.chess_pgn


class ChessEnvironmentTests(unittest.TestCase):
    def test_dotnet_readiness_timestamps_preserve_ticks_on_supported_python(self):
        # Exact ReadinessResponse/ChessPerfcacheObservation wire shape, including
        # explicit zero counters and System.Text.Json's trimmed tick precision.
        class Response(io.BytesIO):
            code = 200
        class Opener:
            def __init__(self, payload): self.payload = payload
            def open(self, request, timeout): return Response(json.dumps(self.payload).encode())
        body = json.loads((ROOT / "scripts/fixtures/chess-readiness-response.json").read_text())
        chess = body["chess_perfcache"]
        for fraction in ("", ".1", ".12", ".123", ".1234", ".12345", ".123456", ".1234567"):
            for offset in ("Z", "+00:00", "-05:30", "+14:00"):
                with self.subTest(fraction=fraction, offset=offset):
                    stamp = "2026-09-15T21:42:30" + fraction + offset
                    chess["observed_utc"] = stamp
                    before = bench.http_readiness(1, Opener(body))
                    self.assertTrue(before["ready"])
                    self.assertEqual(stamp, before["observed"]["chess_perfcache"]["observed_utc"])
                    self.assertEqual(chess, before["observed"]["chess_perfcache"])
                    after = copy.deepcopy(before)
                    after["observed"]["chess_perfcache"]["position"]["lookup_hits"] = 3
                    delta = bench.chess_lookup_deltas(before, after)
                    self.assertEqual("observed", delta["status"])
                    self.assertEqual(3, delta["deltas"]["position"]["lookup_hits"])
                    self.assertEqual(0, delta["deltas"]["transition"]["persistent_hits"])

    def test_chess_timestamp_validation_rejects_missing_or_invalid_calendar_and_offsets(self):
        for stamp in (None, 123, "2026-09-15T21:42:30", "2026-09-15T21:42:30.12345678Z",
                      "2026-09-15T21:42:30.Z", "2026-09-15T21:42:30+14:01",
                      "2026-09-15T21:42:30-15:00", "2026-09-15T21:42:30+01:60",
                      "2026-02-30T21:42:30.1234567Z", "2026-09-15T24:00:00Z",
                      "2026-09-15T21:42:60Z", "2026-09-15 21:42:30Z",
                      "2026-09-15T21:42:30Z\n"):
            with self.subTest(stamp=stamp), self.assertRaises(ValueError):
                bench.validate_chess_observation_timestamp(stamp)

    def test_chess_cache_observation_retains_map_sources_and_rejects_false_claims(self):
        value = {"process_id": 123, "observed_utc": "2026-09-15T10:00:00+00:00",
                 "counter_scope": "process-lifetime completed managed lookups; counters include earlier mappings",
                 "initialization_completed": True, "ready": False,
                 "position": {"is_loaded": True, "record_count": 400, "lookup_hits": 12, "lookup_misses": 3},
                 "transition": {"is_loaded": False, "record_count": 0, "novel_count": 2,
                                "persistent_hits": 4, "novel_hits": 5, "lookup_misses": 6}}
        self.assertEqual(value, bench.chess_perfcache_observation(value))
        empty = copy.deepcopy(value)
        empty["transition"].update(is_loaded=True, novel_count=0)
        self.assertTrue(bench.chess_perfcache_observation(empty)["transition"]["is_loaded"])
        loaded = copy.deepcopy(value)
        loaded["transition"].update(is_loaded=True, record_count=20)
        loaded["ready"] = True
        self.assertTrue(bench.chess_perfcache_observation(loaded)["ready"])
        for part, key, bad in [("position", "lookup_hits", True), ("position", "lookup_misses", -1),
                               ("position", "is_loaded", "true"), ("transition", "record_count", 1),
                               ("transition", "novel_hits", "5")]:
            with self.subTest(part=part, key=key, bad=bad):
                invalid = copy.deepcopy(value)
                invalid[part][key] = bad
                with self.assertRaises(ValueError):
                    bench.chess_perfcache_observation(invalid)
        invalid = copy.deepcopy(value)
        del invalid["position"]["lookup_hits"]
        with self.assertRaises(ValueError):
            bench.chess_perfcache_observation(invalid)
        invalid = copy.deepcopy(value)
        invalid["ready"] = True
        with self.assertRaises(ValueError):
            bench.chess_perfcache_observation(invalid)
        value["detail"] = "Password=fixture-secret"
        value["transition"]["filename"] = "private-path"
        observed = bench.chess_perfcache_observation(value)
        self.assertNotIn("fixture-secret", json.dumps(observed))
        self.assertNotIn("private-path", json.dumps(observed))

    def test_chess_request_retains_actual_depth_and_nodes_without_response_detail(self):
        class Response(io.BytesIO):
            code = 200
        class Opener:
            def open(self, request, timeout):
                self.request = request
                return Response(json.dumps({"depth": 1, "nodes": 20, "substrate": True,
                                            "detail": "Password=fixture-secret"}).encode())
        opener = Opener()
        result = bench.chess_read_request(2, opener)
        self.assertEqual("completed", result["status"])
        self.assertEqual({"depth": 1, "nodes": 20, "substrate": True}, result["observed"])
        self.assertEqual("POST", opener.request.get_method())
        self.assertTrue(json.loads(opener.request.data)["substrate"])
        self.assertNotIn("fixture-secret", json.dumps(result))

    def test_chess_request_uses_normal_auth_and_base_without_recording_credentials(self):
        class Response(io.BytesIO):
            code = 200
        class Opener:
            def open(self, request, timeout):
                self.request = request
                return Response(b'{"depth":1,"nodes":20,"substrate":true}')
        opener = Opener()
        with patch.dict(os.environ, {"LAPLACE_API_KEY": "fixture-api-key", "LAPLACE_QUOTE_ID": "fixture-quote",
                                     "LAPLACE_PROOF_TENANT": "fixture-tenant"}, clear=True):
            result = bench.chess_read_request(2, opener, "http://127.0.0.1:8080/installed/")
        self.assertEqual("http://127.0.0.1:8080/installed/chess/eval", opener.request.full_url)
        self.assertEqual("Bearer fixture-api-key", opener.request.get_header("Authorization"))
        self.assertEqual("fixture-quote", opener.request.get_header("X-laplace-quote-id"))
        self.assertEqual("fixture-tenant", opener.request.get_header("X-laplace-tenant"))
        for secret in ("fixture-api-key", "fixture-quote", "fixture-tenant"):
            self.assertNotIn(secret, json.dumps(result))
        with patch.dict(os.environ, {}, clear=True):
            bench.chess_read_request(2, opener)
        self.assertEqual("ci", opener.request.get_header("X-laplace-tenant"))
        self.assertIsNone(opener.request.get_header("Authorization"))
        self.assertIsNone(opener.request.get_header("X-laplace-quote-id"))
        with self.assertRaises(ValueError):
            bench.chess_read_request(2, opener, "http://user:fixture-password@127.0.0.1")
        class Unauthorized:
            def open(self, request, timeout):
                raise bench.urllib.error.HTTPError(request.full_url, 401, "fixture-api-key", {},
                    io.BytesIO(b'{"detail":"fixture-api-key"}'))
        refused = bench.chess_read_request(2, Unauthorized())
        self.assertEqual(401, refused["http_status"])
        self.assertEqual("unavailable", refused["status"])
        self.assertNotIn("fixture-api-key", json.dumps(refused))

    def test_chess_counter_deltas_refuse_missing_changed_and_regressed_evidence(self):
        value = {"process_id": 123,
                 "position": {"lookup_hits": 10, "lookup_misses": 2},
                 "transition": {"persistent_hits": 4, "novel_hits": 5, "lookup_misses": 6}}
        before = {"observed": {"chess_perfcache": value}}
        after = copy.deepcopy(before)
        after["observed"]["chess_perfcache"]["position"]["lookup_hits"] += 3
        delta = bench.chess_lookup_deltas(before, after)
        self.assertEqual("observed", delta["status"])
        self.assertEqual(3, delta["deltas"]["position"]["lookup_hits"])
        self.assertEqual(0, delta["deltas"]["transition"]["persistent_hits"])
        self.assertEqual("missing-chess-observation", bench.chess_lookup_deltas(before, {})["status"])
        after["observed"]["chess_perfcache"]["process_id"] = 456
        self.assertEqual("process-changed", bench.chess_lookup_deltas(before, after)["status"])
        after["observed"]["chess_perfcache"]["process_id"] = 123
        after["observed"]["chess_perfcache"]["transition"]["novel_hits"] = 0
        self.assertEqual("counter-regressed", bench.chess_lookup_deltas(before, after)["status"])

    def args(self, **changes):
        values = dict(cpu_budget=None, reserve_cpus=2, memory_mb=4096, memory_fraction=0.5,
                      engine_overhead_mb=256, threads=None, hash_mb="16,64,256", concurrency=None,
                      match_threads=1, match_hash_mb=16, games=None, max_seconds=180, case_timeout=60)
        values.update(changes)
        return argparse.Namespace(**values)

    def host(self, capacity=8):
        return {"effective_cpu_capacity": capacity, "available_memory_bytes": 16 * 1024 ** 3,
                "physical_first_cpu_order": list(range(168))}

    def test_quota_and_reserve_bound_threads_despite_large_visible_host(self):
        result = bench.plan(self.args(), self.host())
        self.assertEqual(6, result["cpu_budget"])
        self.assertEqual([1, 2, 4, 6], result["threads"])
        self.assertEqual(list(range(6)), result["cpu_affinity"])
        with self.assertRaisesRegex(ValueError, "minus reserve"):
            bench.plan(self.args(cpu_budget=8), self.host())

    def test_fractional_cpu_capacity_never_rounds_up(self):
        result = bench.plan(self.args(reserve_cpus=0), self.host(2.5))
        self.assertEqual(2, result["whole_search_thread_budget"])
        self.assertEqual(0.5, result["fractional_cpu_capacity_not_used"])
        with self.assertRaisesRegex(ValueError, "full search thread"):
            bench.plan(self.args(reserve_cpus=0), self.host(0.5))

    def test_six_physical_cores_are_measured_below_twelve_logical_threads(self):
        host = {**self.host(12), "physical_first_cpu_order": list(range(12)),
                "physical_core_groups_within_affinity": [[core, core + 6] for core in range(6)]}
        result = bench.plan(self.args(memory_mb=8192), host)
        self.assertEqual(result["cpu_budget"], 10)
        self.assertEqual(result["threads"], [1, 2, 4, 6, 8, 10])
        self.assertEqual(result["concurrency"], [1, 2, 4, 6, 8, 10])
        paired = bench.plan(self.args(memory_mb=8192, match_threads=2), host)
        self.assertEqual(paired["concurrency"], [1, 2, 3, 4, 5])

    def test_physical_boundary_respects_selected_affinity_and_explicit_points(self):
        host = {**self.host(12), "physical_first_cpu_order": list(range(12)),
                "physical_core_groups_within_affinity": [[core, core + 6] for core in range(6)]}
        limited = bench.plan(self.args(cpu_budget=3), host)
        self.assertEqual(limited["cpu_affinity"], [0, 1, 2])
        self.assertEqual(limited["threads"], [1, 2, 3])
        self.assertEqual(limited["concurrency"], [1, 2, 3])
        explicit = bench.plan(self.args(memory_mb=8192, threads="1,8", concurrency="1,4"), host)
        self.assertEqual(explicit["threads"], [1, 8])
        self.assertEqual(explicit["concurrency"], [1, 4])
        memory_limited = bench.plan(self.args(memory_mb=1100), host)
        self.assertEqual(memory_limited["concurrency"], [1, 2])

    def test_tournament_memory_accounts_for_both_resident_engines(self):
        result = bench.plan(self.args(memory_mb=1100), self.host())
        self.assertEqual([1, 2], result["concurrency"])
        with self.assertRaises(ValueError):
            bench.plan(self.args(memory_mb=1100, concurrency="3"), self.host())
        with self.assertRaises(ValueError):
            bench.plan(self.args(memory_mb=20000), self.host())

    def test_all_visible_cgroup_ancestors_constrain_quota_and_memory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            groups = []
            for name, cpu, limit, used in (("parent", "200000 100000", 1000, 400),
                                          ("leaf", "800000 100000", 2000, 500)):
                path = root / name
                path.mkdir()
                (path / "cpu.max").write_text(cpu)
                (path / "memory.max").write_text(str(limit))
                (path / "memory.current").write_text(str(used))
                groups.append({"path": str(path), "version": 2, "controller": ""})
            result = bench.cgroup_limits(groups)
            self.assertEqual(2, result["cpu_quota"])
            self.assertEqual(1000, result["memory_limit_bytes"])
            self.assertEqual(600, result["memory_available_bytes"])

    def test_bench_summaries_preserve_work_and_time_for_each_repeat(self):
        text = "Position: 48/48\nTotal time (ms) : 1200\nNodes searched  : 48000\nNodes/second    : 40000\n" * 3
        rows = bench.parse_benches(text)
        self.assertEqual(3, len(rows))
        self.assertEqual({"engine_seconds": 1.2, "nodes": 48000, "nodes_per_second": 40000, "positions": 48}, rows[0])
        with self.assertRaises(ValueError):
            bench.parse_benches("Nodes searched: 48000")

    def test_uci_ranges_are_parsed_from_actual_capabilities(self):
        options = bench.parse_options("option name Hash type spin default 16 min 1 max 33554432\n"
                                      "option name NumaPolicy type string default auto\n")
        self.assertEqual("33554432", options["Hash"]["max"])
        self.assertEqual("auto", options["NumaPolicy"]["default"])

    def test_service_installation_running_state_and_manager_timestamps_are_distinct(self):
        text = """Id=laplace-api.service
LoadState=loaded
UnitFileState=enabled
ActiveState=inactive
SubState=dead
MainPID=0
Type=simple
ExecMainStartTimestampMonotonic=1000000
ActiveEnterTimestampMonotonic=1500000
Environment=SECRET=must-not-be-retained

Id=laplace-lichess.service
LoadState=loaded
UnitFileState=disabled
ActiveState=active
SubState=running
MainPID=42
Type=simple
ExecMainStartTimestampMonotonic=3000000
ActiveEnterTimestampMonotonic=2000000
"""
        result = bench.service_observations(text)
        api, lichess = result["laplace-api.service"], result["laplace-lichess.service"]
        self.assertTrue(api["installed"])
        self.assertTrue(api["enabled"])
        self.assertFalse(api["running"])
        self.assertEqual(api["main_pid"], 0)
        self.assertEqual(api["manager_start_to_active_seconds"], 0.5)
        self.assertFalse(lichess["enabled"])
        self.assertTrue(lichess["running"])
        self.assertEqual(lichess["main_pid"], 42)
        self.assertIsNone(lichess["manager_start_to_active_seconds"])
        self.assertNotIn("SECRET", json.dumps(result))
        self.assertNotIn("application_startup_seconds", api)

    def test_http_readiness_retains_typed_status_without_sensitive_detail(self):
        class Response(io.BytesIO):
            code = 200
        class Opener:
            def __init__(self, payload, code=200): self.payload, self.code = payload, code
            def open(self, url, timeout):
                response = Response(json.dumps(self.payload).encode())
                response.code = self.code
                return response
        body = {"ready": True, "substrate_reachable": True, "perfcache_ready": True,
                "entities": 15, "consensus_relations": 9, "detail": "Password=do-not-disclose"}
        result = bench.http_readiness(1, Opener(body))
        self.assertTrue(result["ready"])
        self.assertEqual(result["observed"]["entities"], 15)
        self.assertNotIn("do-not-disclose", json.dumps(result))
        self.assertNotIn("detail", result["observed"])
        self.assertFalse(bench.http_readiness(1, Opener(body, 503))["ready"])
        self.assertFalse(bench.http_readiness(1, Opener({**body, "ready": "true"}))["ready"])
        self.assertFalse(bench.http_readiness(1, Opener({**body, "substrate_reachable": False}))["ready"])
        body["chess_perfcache"] = {"ready": True}
        invalid_chess = bench.http_readiness(1, Opener(body))
        self.assertTrue(invalid_chess["ready"])
        self.assertEqual("invalid-observation", invalid_chess["chess_perfcache_status"])
        self.assertNotIn("chess_perfcache", invalid_chess["observed"])

    def test_gpu_inventory_retains_compute_driver_and_mib_units(self):
        devices = bench.gpu_observations('0, GPU-123, "GPU, quoted name", 580.1, 8.6, 12288, 8192\n')
        self.assertEqual(devices[0]["name"], "GPU, quoted name")
        self.assertEqual(devices[0]["driver_version"], "580.1")
        self.assertEqual(devices[0]["compute_capability"], "8.6")
        self.assertEqual(devices[0]["memory_total_bytes"], 12288 * bench.MIB)
        self.assertEqual(devices[0]["memory_free_bytes"], 8192 * bench.MIB)
        fallback = bench.gpu_observations("0, GPU-123, Older GPU, 470.1, 4096, 2048", False)
        self.assertIsNone(fallback[0]["compute_capability"])
        with self.assertRaises(ValueError):
            bench.gpu_observations("0, incomplete")

    @staticmethod
    def lichess_status():
        return {"configured": True, "running": True, "connected": True, "substrate": True,
                "depth": 8, "maxConcurrent": 2, "gamesRecorded": 17, "error": None,
                "tokenPreview": "private-preview", "recentLog": ["private-chat"],
                "account": {"tokenValid": True, "botAccount": True, "botPlayScope": True,
                            "username": "private-account", "error": None, "ready": True}}

    def test_lichess_status_requires_actual_account_and_stream_without_retaining_secrets(self):
        class Response(io.BytesIO):
            code = 200
        class Opener:
            def __init__(self, body, code=200): self.body, self.code = body, code
            def open(self, request, timeout):
                reply = Response(json.dumps(self.body).encode())
                reply.code = self.code
                return reply
        body = self.lichess_status()
        observed = bench.lichess_readiness(1, Opener(body))
        self.assertTrue(observed["ready"])
        self.assertEqual(17, observed["observed"]["gamesRecorded"])
        self.assertNotIn("private-", json.dumps(observed))
        self.assertEqual(hashlib.sha256(json.dumps(body).encode()).hexdigest(), observed["response_sha256"])
        for changed in ({**body, "running": False}, {**body, "connected": False},
                        {**body, "configured": False}, {**body, "account": None},
                        {**body, "error": "private-error"}):
            result = bench.lichess_readiness(1, Opener(changed))
            self.assertFalse(result["ready"])
            self.assertEqual("not-ready", result["status"])
            self.assertNotIn("private-", json.dumps(result))
        self.assertFalse(bench.lichess_readiness(1, Opener(body, 503))["ready"])
        for changed in ({**body, "running": "true"}, {**body, "gamesRecorded": True},
                        {**body, "account": {**body["account"], "botPlayScope": False}},
                        {**body, "account": {**body["account"], "tokenValid": 1}},
                        {**body, "account": {**body["account"], "username": ""}},
                        {**body, "recentLog": ["x" * 65536]}):
            result = bench.lichess_readiness(1, Opener(changed))
            self.assertFalse(result["ready"])
            self.assertEqual("unavailable", result["status"])

    def test_lichess_probe_uses_only_existing_status_get_on_actual_http_transport(self):
        body = self.lichess_status()
        seen = []
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_GET(self):
                seen.append((self.command, self.path, self.headers.get("Authorization")))
                payload = json.dumps(body).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            try:
                with patch.dict(os.environ, {"LAPLACE_API_KEY": "fixture-key"}):
                    result = bench.lichess_readiness(2, base=f"http://127.0.0.1:{server.server_port}")
            finally:
                server.shutdown()
                worker.join(timeout=2)
        self.assertTrue(result["ready"])
        self.assertEqual([("GET", "/chess/lichess/status", "Bearer fixture-key")], seen)
        self.assertNotIn("fixture-key", json.dumps(result))

    def test_missing_service_manager_and_gpu_utility_are_reported_without_starting_services(self):
        with patch.object(bench.subprocess, "run", side_effect=FileNotFoundError("systemctl")) as run, \
                patch.object(bench.shutil, "which", return_value=None), \
                patch.object(bench, "http_readiness", return_value={"ready": False}), \
                patch.object(bench, "lichess_readiness", return_value={"ready": False, "status": "not-ready"}) as online:
            result = bench.runtime_capabilities(time.monotonic() + 2)
        self.assertFalse(result["service_mutations_performed"])
        self.assertFalse(result["application_startup_measured"])
        self.assertEqual(result["systemd"]["status"], "unavailable")
        self.assertFalse(result["nvidia"]["utility_installed"])
        self.assertFalse(result["nvidia"]["compute_execution_measured"])
        self.assertEqual({"ready": False, "status": "not-ready"}, result["lichess_readiness"])
        self.assertEqual("http://127.0.0.1:5187", online.call_args.kwargs["base"])
        self.assertEqual(run.call_count, 1)
        self.assertEqual(run.call_args.args[0][:2], ["systemctl", "show"])
        self.assertNotIn("Environment", run.call_args.args[0][3].split(","))

    def test_network_identity_hashes_the_installed_file_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)
            (source / "src").mkdir()
            binary = source / "src/stockfish"
            binary.write_bytes(b"fixture executable identity")
            network_bytes = b"fixture NNUE bytes"
            network_name = "nn-" + hashlib.sha256(network_bytes).hexdigest()[:12] + ".nnue"
            network = source / "src" / network_name
            network.write_bytes(network_bytes)
            (source / "src/evaluate.h").write_text('#define EvalFileDefaultName "' + network_name + '"\n')
            def git(command, **kwargs):
                return argparse.Namespace(stdout=("a" * 40 if command[-1] == "HEAD" else
                    ".git/laplace-stockfish-build.json" if "--git-path" in command else ""))
            with patch.object(bench.subprocess, "run", side_effect=git):
                identity = bench.source_identity(binary)
            self.assertEqual(identity["source_networks"], [{"name": network_name, "path": str(network),
                "present": True, "size_bytes": len(network_bytes),
                "sha256": hashlib.sha256(network_bytes).hexdigest()}])

    def test_runtime_only_cli_emits_complete_report_without_cutechess_or_stockfish_searches(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            binary = root / "stockfish"
            binary.write_bytes(b"fixture executable")
            output = root / "receipt"
            def bootstrap(executable, log, budget, timeout, repeats):
                Path(log).write_text("option name EvalFile type string default nn-123456789abc.nnue\nuciok\n")
                return {"success": True, "log": str(log), "bootstrap": {"scope": "mocked CLI orchestration fixture"}}
            with patch.object(sys, "argv", ["benchmark", "--runtime-only",
                    "--stockfish", str(binary), "--cutechess", str(root / "absent-cutechess"),
                    "--output-dir", str(output), "--reserve-cpus", "0", "--cpu-budget", "1", "--memory-mb", "512"]), \
                    patch.object(bench, "machine", return_value=self.host()), \
                    patch.object(bench, "module", return_value=argparse.Namespace(configured_binary=lambda: binary)), \
                    patch.object(bench, "runtime_capabilities", return_value={"api_readiness": {"ready": True}}), \
                    patch.object(bench, "source_identity", return_value={"sha256": bench.sha256(binary)}) as identity, \
                    patch.object(bench, "uci_bootstrap", side_effect=bootstrap), \
                    patch.object(bench, "run_process") as run:
                self.assertEqual(bench.main(), 0)
            report = json.loads((output / "report.json").read_text())
            self.assertEqual(report["status"], "complete")
            self.assertTrue(report["parameters"]["runtime_only"])
            self.assertEqual(report["stockfish_bench"], [])
            self.assertEqual(report["cutechess_matches"], [])
            self.assertNotIn("cutechess_identity", report)
            self.assertEqual(report["stockfish_identity"]["advertised_nnue_files"], {"EvalFile": "nn-123456789abc.nnue"})
            identity.assert_called_once_with(binary)
            run.assert_not_called()

    @unittest.skipUnless(os.name == "posix", "owned executable protocol fixture")
    def test_uci_bootstrap_times_new_process_and_reuses_it_for_each_ready_probe(self):
        with tempfile.TemporaryDirectory() as temporary:
            executable = Path(temporary) / "engine"
            executable.write_text("#!" + sys.executable + "\nimport sys,time\n"
                "for line in sys.stdin:\n"
                " command=line.strip()\n"
                " if command=='uci':\n"
                "  time.sleep(0.01); print('option name Hash type spin default 16 min 1 max 1024',flush=True); print('uciok',flush=True)\n"
                " elif command=='isready': print('readyok',flush=True)\n"
                " elif command=='compiler': print('fixture compiler',flush=True)\n"
                " elif command=='quit': break\n")
            executable.chmod(0o755)
            budget = {"cpu_affinity": sorted(os.sched_getaffinity(0))[:1], "memory_budget_bytes": 512 * bench.MIB}
            result = bench.uci_bootstrap(executable, Path(temporary) / "uci.log", budget, 3, repeats=3)
            self.assertTrue(result["success"], result)
            self.assertEqual(result["returncode"], 0)
            self.assertGreater(result["bootstrap"]["process_spawn_to_uciok_seconds"], 0)
            self.assertGreaterEqual(result["bootstrap"]["initial_isready_seconds"], 0)
            self.assertEqual(len(result["bootstrap"]["reused_process_isready_seconds"]), 3)
            self.assertTrue(all(value >= 0 for value in result["bootstrap"]["reused_process_isready_seconds"]))
            transcript = Path(result["log"]).read_text()
            self.assertEqual(transcript.count("uciok"), 1)
            self.assertEqual(transcript.count("readyok"), 5)
            self.assertIn("fixture compiler", transcript)
            with self.assertRaises(ProcessLookupError):
                os.kill(result["pid"], 0)

    @unittest.skipUnless(os.name == "posix", "owned executable timeout fixture")
    def test_uci_bootstrap_rejects_inexact_ack_and_kills_its_own_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            executable = Path(temporary) / "engine"
            executable.write_text("#!" + sys.executable + "\nimport time\n"
                "print('uciok-not-the-protocol-ack',flush=True)\ntime.sleep(30)\n")
            executable.chmod(0o755)
            budget = {"cpu_affinity": sorted(os.sched_getaffinity(0))[:1], "memory_budget_bytes": 512 * bench.MIB}
            result = bench.uci_bootstrap(executable, Path(temporary) / "uci.log", budget, 0.15)
            self.assertFalse(result["success"])
            self.assertIn("deadline exhausted", result["failure"])
            self.assertLess(result["wall_seconds"], 2)
            self.assertNotIn("process_spawn_to_uciok_seconds", result["bootstrap"])
            self.assertIn("uciok-not-the-protocol-ack", Path(result["log"]).read_text())
            with self.assertRaises(ProcessLookupError):
                os.kill(result["pid"], 0)

    def test_virtual_process_ids_do_not_attribute_unrelated_proc_entries(self):
        with patch.object(bench, "read", return_value="9000 (python) R"), patch.object(bench.os, "getpid", return_value=5):
            self.assertFalse(bench.proc_namespace_matches())

    def test_engine_children_of_worker_threads_are_enumerated(self):
        with tempfile.TemporaryDirectory() as temporary:
            proc = Path(temporary)
            for task, children in (("10", ""), ("11", "20 21"), ("12", "22")):
                path = proc / "10/task" / task / "children"
                path.parent.mkdir(parents=True)
                path.write_text(children)
            self.assertEqual([20, 21, 22], bench.proc_children(10, proc))

    def test_changed_executables_suppress_configuration_recommendations(self):
        report = {"evidence_invalid": True, "stockfish_bench": [], "cutechess_matches": [],
                  "parameters": {"repeats": 3}}
        result = bench.recommendations(report)
        self.assertIn("invalid_evidence", result)
        self.assertNotIn("search_node_throughput", result)

    def test_paired_match_inherits_substrate_unless_explicitly_disabled(self):
        args = self.args()
        args.match_depth, args.max_moves, args.laplace_substrate = 4, 8, "inherit"
        command = bench.match_command(Path("cutechess"), Path("stockfish"), Path("game.pgn"), 1, 2, args, Path("laplace"))
        self.assertFalse(any(value.startswith("option.Substrate=") for value in command))
        args.laplace_substrate = "off"
        self.assertIn("option.Substrate=off", bench.match_command(Path("cc"), Path("sf"), Path("game.pgn"), 1, 2, args, Path("laplace")))

    def test_complete_game_command_has_no_move_cap_and_diagnostics_cannot_recommend_capacity(self):
        args = self.args()
        args.match_depth, args.max_moves, args.laplace_substrate = 8, 0, "inherit"
        command = bench.match_command(Path("cc"), Path("sf"), Path("game.pgn"), 2, 4, args)
        self.assertNotIn("-maxmoves", command)
        self.assertNotIn("-draw", command)
        args.max_moves = 12
        capped = bench.match_command(Path("cc"), Path("sf"), Path("game.pgn"), 2, 4, args)
        self.assertEqual("12", capped[capped.index("-maxmoves") + 1])
        report = {"status":"complete", "stockfish_bench":[],
            "cutechess_matches":[{"status":"complete", "concurrency":2}],
            "parameters":{"repeats":3,"max_moves":12}}
        recommendations = bench.recommendations(report)
        self.assertIn("tournament_diagnostic_only", recommendations)
        self.assertNotIn("bounded_tournament_throughput", recommendations)

    def test_incomplete_pgn_and_transcript_cannot_be_success(self):
        pgn = '[Event "test"]\n[White "A"]\n[Black "B"]\n[Result "1/2-1/2"]\n[PlyCount "2"]\n[Termination "adjudication"]\n\n1. e4 e5 {Draw by adjudication: maximal game length} 1/2-1/2\n'
        with self.assertRaises(ValueError):
            bench.parse_pgn(pgn, 1)
        games = bench.parse_pgn(pgn, 1, allow_adjudication=True, max_moves=1)
        bench.verify_tournament("1 <A: bestmove e2e4\n2 <B: bestmove e7e5\nFinished game 1 (A vs B): 1/2-1/2\n", games)
        with self.assertRaises(ValueError):
            bench.parse_pgn(pgn.replace('Result "1/2-1/2"', 'Result "*"'), 1)
        with self.assertRaises(ValueError):
            bench.parse_pgn(pgn.rstrip()[:-7], 1)
        with self.assertRaises(ValueError):
            bench.verify_tournament("Finished game 1 (A vs B): 1/2-1/2\n", games)

    @unittest.skipUnless(os.name == "posix", "POSIX process-group cleanup contract")
    def test_timeout_stops_only_its_own_process_tree_and_retains_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            log = Path(temporary) / "child.log"
            budget = {"cpu_affinity": sorted(os.sched_getaffinity(0))[:1], "memory_budget_bytes": 512 * bench.MIB}
            result = bench.run_process([sys.executable, "-c", "import time; print('launched', flush=True); time.sleep(30)"], log, budget, 0.15)
            self.assertFalse(result["success"])
            self.assertEqual("wall_timeout", result["failure"])
            self.assertLess(result["wall_seconds"], 2)
            self.assertIn("launched", log.read_text())
            with self.assertRaises(ProcessLookupError):
                os.kill(result["pid"], 0)


class ChessPgnProvider(unittest.TestCase):
    """Real pinned rules execution, with independent archive/import refusals."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.artifact = PGN.artifact_lock()
        cache = Path(os.environ.get("LAPLACE_CHESS_PGN_TEST_CACHE", str(PGN.default_cache())))
        cache.mkdir(parents=True, exist_ok=True)
        cls.archive = PGN.acquire(cls.artifact, cache, os.environ.get("LAPLACE_CHESS_PGN_OFFLINE") == "1")

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        self.cache = self.directory / "provider"
        self.cache.mkdir()
        shutil.copyfile(self.archive, self.cache / self.artifact["filename"])
        self.previous_loaded = PGN._loaded.copy()
        self.previous = {key: value for key, value in sys.modules.items()
                         if key == "chess" or key.startswith("chess.")}
        for key in self.previous:
            del sys.modules[key]

    def tearDown(self) -> None:
        for key in list(sys.modules):
            if key == "chess" or key.startswith("chess."):
                del sys.modules[key]
        sys.modules.update(self.previous)
        PGN._loaded.clear()
        PGN._loaded.update(self.previous_loaded)
        self.temporary.cleanup()

    def load(self):
        return PGN.load_provider(self.cache, offline=True)

    def test_real_rules_parse_legal_checkmate_and_refuse_illegal_san(self) -> None:
        chess, pgn, receipt = self.load()
        game = pgn.read_game(io.StringIO('[Result "0-1"]\n\n1. f3 e5 2. g4 Qh4# 0-1\n'))
        self.assertEqual(game.errors, [])
        board = game.end().board()
        self.assertEqual(board.outcome().termination, chess.Termination.CHECKMATE)
        self.assertEqual(board.result(), "0-1")
        self.assertEqual(game.end().ply(), 4)
        with self.assertLogs("chess.pgn", level="ERROR"):
            illegal = pgn.read_game(io.StringIO('[Result "0-1"]\n\n1. e5 0-1\n'))
        self.assertTrue(illegal.errors)
        self.assertIsNone(illegal.end().board().outcome())
        self.assertEqual(receipt["archive"]["sha256"], self.artifact["sha256"])
        self.assertEqual(receipt["version"], "1.11.2")
        self.assertEqual(len(receipt["files"]), 10)
        self.assertEqual(len(receipt["modules"]), 8)
        self.assertTrue(Path(receipt["license"]["path"]).read_text().startswith("                    GNU GENERAL PUBLIC LICENSE"))
        self.assertFalse(list(self.cache.rglob("*.pyc")))

    def test_exact_cache_and_modules_reuse_without_download(self) -> None:
        chess, pgn, receipt = self.load()
        with patch.object(PGN.urllib.request, "urlopen", side_effect=AssertionError("offline reuse tried a download")):
            repeated = self.load()
        self.assertIs(repeated[0], chess)
        self.assertIs(repeated[1], pgn)
        self.assertEqual(repeated[2], receipt)

    def test_download_publishes_only_exact_pinned_bytes(self) -> None:
        target = self.cache / self.artifact["filename"]
        original = target.read_bytes()
        target.unlink()
        for payload, message in [(original[:-1], "truncated"), (original + b"x", "pinned size")]:
            with self.subTest(message=message), patch.object(PGN.urllib.request, "urlopen", return_value=io.BytesIO(payload)):
                with self.assertRaisesRegex(ValueError, message):
                    PGN.acquire(self.artifact, self.cache, offline=False)
            self.assertFalse(target.exists())
            self.assertEqual(list(self.cache.iterdir()), [])
        with patch.object(PGN.urllib.request, "urlopen", return_value=io.BytesIO(original)):
            published = PGN.acquire(self.artifact, self.cache, offline=False)
        self.assertEqual(published.read_bytes(), original)
        self.assertEqual(list(self.cache.iterdir()), [target])

    def test_corrupt_or_missing_archive_is_refused_before_import(self) -> None:
        archive = self.cache / self.artifact["filename"]
        data = archive.read_bytes()
        archive.write_bytes(bytes([data[0] ^ 1]) + data[1:])
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            self.load()
        self.assertNotIn("chess", sys.modules)
        archive.write_bytes(data[:-1])
        with self.assertRaisesRegex(ValueError, "size or regular-file mismatch"):
            self.load()
        archive.unlink()
        with self.assertRaisesRegex(ValueError, "offline PGN provider artifact missing"):
            self.load()

    def test_every_runtime_file_and_license_is_reverified_on_reuse(self) -> None:
        _, _, receipt = self.load()
        root = Path(receipt["runtime_root"])
        for item in receipt["files"]:
            with self.subTest(path=item["path"]):
                path = root / item["path"]
                original = path.read_bytes()
                path.write_bytes(original + b"\n# deliberate runtime mutation\n")
                with self.assertRaisesRegex(ValueError, "runtime bytes differ"):
                    self.load()
                path.write_bytes(original)
        (root / "chess/pgn.py").unlink()
        with self.assertRaisesRegex(ValueError, "inventory is incomplete"):
            self.load()

    def test_extra_importable_files_and_bytecode_directories_are_refused(self) -> None:
        self.load()
        root = self.cache / "runtime/chess"
        for name in ("extra.py", "extra.pyc", "extra.so"):
            with self.subTest(name=name):
                path = root / name
                path.write_bytes(b"untrusted extra")
                with self.assertRaisesRegex(ValueError, "extra or nonregular"):
                    self.load()
                path.unlink()
        (root / "__pycache__").mkdir()
        with self.assertRaisesRegex(ValueError, "extra.*directory"):
            self.load()

    def test_runtime_and_archive_links_are_refused_even_for_identical_bytes(self) -> None:
        self.load()
        for relative in ("runtime/chess/pgn.py", self.artifact["filename"]):
            with self.subTest(path=relative):
                path = self.cache / relative
                original = path.read_bytes()
                outside = self.directory / "outside"
                outside.write_bytes(original)
                path.unlink()
                path.symlink_to(outside)
                with self.assertRaisesRegex(ValueError, "link|physical regular"):
                    self.load()
                path.unlink()
                os.link(outside, path)
                with self.assertRaisesRegex(ValueError, "nonregular|physical regular"):
                    self.load()
                path.unlink()
                outside.unlink()
                path.write_bytes(original)

    def test_ambient_module_is_not_replaced_or_used(self) -> None:
        substitute = types.ModuleType("chess")
        substitute.__version__ = "1.11.2"
        sys.modules["chess"] = substitute
        with self.assertRaisesRegex(ValueError, "module inventory differs"):
            self.load()
        self.assertIs(sys.modules["chess"], substitute)

    def test_cache_and_runtime_directory_symlinks_are_refused(self) -> None:
        self.load()
        alias = self.directory / "alias"
        alias.symlink_to(self.cache, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "cache must not be a symlink"):
            PGN.load_provider(alias, offline=True)
        runtime = self.cache / "runtime"
        outside = self.directory / "original-runtime"
        runtime.rename(outside)
        runtime.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "runtime must be a physical directory"):
            self.load()

    def test_loaded_module_version_origin_and_identity_substitutions_are_refused(self) -> None:
        chess, pgn, _ = self.load()
        for owner, attribute, value, message in (
                (chess, "__version__", "substituted", "version differs"),
                (pgn, "__file__", "/ambient/chess/pgn.py", "origin differs"),
                (pgn.__spec__, "origin", "/ambient/chess/pgn.py", "origin differs"),
                (chess, "__path__", ["/ambient/chess"], "search path differs"),
                (pgn, "chess", types.ModuleType("chess"), "absolute import substitution"),
                (chess, "pgn", types.ModuleType("chess.pgn"), "package module substitution")):
            with self.subTest(attribute=attribute):
                previous = getattr(owner, attribute)
                setattr(owner, attribute, value)
                with self.assertRaisesRegex(ValueError, message):
                    self.load()
                setattr(owner, attribute, previous)
        sys.modules["chess.pgn"] = types.ModuleType("chess.pgn")
        with self.assertRaisesRegex(ValueError, "module substitution"):
            self.load()

    def test_unsafe_tar_paths_links_and_duplicate_names_are_refused(self) -> None:
        # These archives test extraction rejection only; game validation always
        # uses the exact official artifact acquired in setUpClass.
        cases = [("../escape.py", tarfile.REGTYPE), ("/escape.py", tarfile.REGTYPE),
                 ("chess-1.11.2/chess/../escape.py", tarfile.REGTYPE),
                 ("chess-1.11.2/chess\\escape.py", tarfile.REGTYPE),
                 ("chess-1.11.2/chess/pgn.py", tarfile.SYMTYPE),
                 ("chess-1.11.2/chess/pgn.py", tarfile.LNKTYPE)]
        for index, (name, kind) in enumerate(cases):
            with self.subTest(name=name, kind=kind):
                path = self.directory / f"unsafe-{index}.tar.gz"
                with tarfile.open(path, "w:gz") as archive:
                    member = tarfile.TarInfo(name)
                    member.type = kind
                    member.linkname = "/outside"
                    archive.addfile(member, io.BytesIO(b""))
                with self.assertRaisesRegex(ValueError, "unsafe|links or special"):
                    PGN._runtime_files(path, "1.11.2")
        path = self.directory / "duplicate.tar.gz"
        with tarfile.open(path, "w:gz") as archive:
            for _ in range(2):
                archive.addfile(tarfile.TarInfo("chess-1.11.2/chess/pgn.py"), io.BytesIO(b""))
        with self.assertRaisesRegex(ValueError, "duplicate"):
            PGN._runtime_files(path, "1.11.2")


class PgnLegalityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.provider = PGN.load_provider(PGN.default_cache(), offline=os.environ.get("LAPLACE_CHESS_PGN_OFFLINE") == "1")
        with tarfile.open(cls.provider[2]["archive"]["path"], "r:gz") as archive:
            fixture = archive.extractfile("chess-1.11.2/data/pgn/molinari-bordais-1979.pgn")
            assert fixture is not None
            cls.checkmate = fixture.read().decode("utf-8")
        cls.retained_game = (ROOT / "app/Laplace.Chess.Tests/Fixtures/position-playing-game-1.pgn").read_text(encoding="utf-8")

    def validate(self, text, expected=1, max_moves=0):
        return bench.parse_pgn(text, expected, provider=self.provider,
                               allow_adjudication=max_moves > 0, max_moves=max_moves)

    def diagnostic(self):
        # The first four actual moves of the pinned upstream game, deliberately
        # capped and identified as diagnostic rather than a complete game.
        tags = self.checkmate.split("\n\n", 1)[0].replace('[Result "0-1"]', '[Result "1/2-1/2"]')
        tags = tags.replace('[PlyCount "10"]', '[PlyCount "4"]')
        return tags + '\n[Termination "adjudication"]\n\n1. e4 c5 2. c4 Nc6 {Draw by adjudication: maximal game length} 1/2-1/2\n'

    def test_real_upstream_checkmate_has_replayed_outcome_and_exact_move_receipt(self):
        game, = self.validate(self.checkmate)
        self.assertEqual((game["plies"], game["board_outcome"], game["normal_completion"]), (10, "CHECKMATE", True))
        self.assertTrue(game["legal_moves_validated"])
        self.assertEqual(game, self.validate(self.checkmate.replace('[Result "0-1"]', '[Result "0-1"]\n[Termination "normal"]'))[0])
        self.assertEqual(len(game["moves_sha256"]), 64)
        self.assertIn(" w ", game["final_fen"])

    def test_old_header_only_counterexamples_are_refused(self):
        for moves, plies in [("1. e5", 1), ("1. e4 e5", 2)]:
            text = f'[Event "Negative control"]\n[Result "1-0"]\n[PlyCount "{plies}"]\n\n{moves} 1-0\n'
            with self.subTest(moves=moves), self.assertRaises(ValueError):
                self.validate(text)

    def test_actual_terminal_result_cannot_be_replaced_or_retained_on_a_prefix(self):
        for text in [self.checkmate.replace('0-1', '1-0'),
                     self.checkmate.replace('5. g3 Nd3# ', '').replace('[PlyCount "10"]', '[PlyCount "8"]')]:
            with self.subTest(text=text), self.assertRaisesRegex(ValueError, "terminal outcome"):
                self.validate(text)

    def test_illegal_null_unknown_and_postterminal_moves_are_refused(self):
        for old, new in [('1. e4', '1. e5'), ('1. e4', '1. --'),
                         ('1. e4', '1. e4garbage'), ('1. e4', '1. e4 garbage'),
                         ('Nd3# 0-1', 'Nd3# 6. Kg2 0-1')]:
            with self.subTest(new=new), self.assertRaises(ValueError):
                self.validate(self.checkmate.replace(old, new))

    def test_each_header_move_and_result_token_is_accounted(self):
        for changed in [self.checkmate.replace('Nd3# 0-1', 'Nd3# 1-0'),
                        self.checkmate.replace('Nd3# 0-1', 'Nd3#'),
                        self.checkmate.replace('[PlyCount "10"]', '[PlyCount "9"]'),
                        self.checkmate.replace('[Result "0-1"]', '[Result "0-1"]\n[Result "0-1"]'),
                        self.checkmate.replace('[Result "0-1"]', '[Result "*"]'),
                        self.checkmate + '{Engine disconnected}',
                        'unaccounted prefix\n' + self.checkmate]:
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                self.validate(changed)
        with self.assertRaisesRegex(ValueError, "expected 2"):
            self.validate(self.checkmate, expected=2)

    def test_history_supports_legal_draw_claim_and_refuses_shorter_prefix(self):
        text = '[Event "Rules control"]\n[Result "1/2-1/2"]\n[PlyCount "8"]\n\n1. Nf3 Nf6 2. Ng1 Ng8 3. Nf3 Nf6 4. Ng1 Ng8 1/2-1/2\n'
        game, = self.validate(text)
        self.assertEqual(game["board_outcome"], "THREEFOLD_REPETITION")
        self.assertTrue(game["normal_completion"])
        with self.assertRaisesRegex(ValueError, "terminal outcome"):
            self.validate(text.replace('3. Nf3 Nf6 4. Ng1 Ng8 ', '').replace('[PlyCount "8"]', '[PlyCount "4"]'))

    def test_declared_standard_start_cannot_be_replaced_by_terminal_fen(self):
        altered = self.checkmate.replace('[Event "cr"]', '[Event "cr"]\n[SetUp "1"]\n[FEN "8/8/8/8/8/5k2/6q1/7K w - - 0 1"]')
        with self.assertRaises(ValueError):
            self.validate(altered)
        with self.assertRaises(ValueError):
            self.validate(self.checkmate.replace('[Event "cr"]', '[Event "cr"]\n[Variant "Atomic"]'))

    def test_retained_setup_without_fen_cannot_use_the_upstream_default_board(self):
        changed = self.retained_game.replace('\n', '\n[SetUp "1"]\n', 1)
        upstream = self.provider[1].read_game(io.StringIO(changed))
        self.assertEqual([], upstream.errors)
        self.assertEqual("1", upstream.headers["SetUp"])
        self.assertNotIn("FEN", upstream.headers)
        self.assertEqual(self.provider[0].STARTING_FEN, upstream.board().fen())
        with self.assertRaisesRegex(ValueError, "SetUp=1 requires a nonempty FEN"):
            self.validate(changed)

    def test_retained_setup_and_fen_headers_must_be_coherent(self):
        standard_fen = self.provider[0].STARTING_FEN
        for headers, message in [
                ('[SetUp "1"]\n[FEN ""]', "SetUp=1 requires a nonempty FEN"),
                ('[SetUp "1"]\n[FEN "   "]', "SetUp=1 requires a nonempty FEN"),
                (f'[FEN "{standard_fen}"]', "FEN requires SetUp=1"),
                (f'[SetUp "0"]\n[FEN "{standard_fen}"]', "FEN requires SetUp=1"),
                ('[FEN ""]', "FEN requires SetUp=1")]:
            changed = self.retained_game.replace('\n', '\n' + headers + '\n', 1)
            with self.subTest(headers=headers), self.assertRaisesRegex(ValueError, message):
                self.validate(changed)

    def test_coherent_standard_setup_preserves_the_complete_retained_game(self):
        expected, = self.validate(self.retained_game)
        self.assertEqual((154, "CHECKMATE", True),
                         (expected["plies"], expected["board_outcome"], expected["normal_completion"]))
        for headers in ['[SetUp "0"]', f'[SetUp "1"]\n[FEN "{self.provider[0].STARTING_FEN}"]']:
            changed = self.retained_game.replace('\n', '\n' + headers + '\n', 1)
            with self.subTest(headers=headers):
                self.assertEqual([expected], self.validate(changed))

    def test_capped_diagnostic_requires_exact_moves_reason_and_declared_cap(self):
        with self.assertRaisesRegex(ValueError, "non-normal"):
            self.validate(self.diagnostic())
        game, = self.validate(self.diagnostic(), max_moves=2)
        self.assertFalse(game["normal_completion"])
        self.assertIsNone(game["board_outcome"])
        for changed, cap in [(self.diagnostic(), 3), (self.diagnostic(), 1),
                             (self.diagnostic().replace('maximal game length', 'evaluation'), 2)]:
            with self.subTest(cap=cap), self.assertRaises(ValueError):
                self.validate(changed, max_moves=cap)
        with self.assertRaisesRegex(ValueError, "explicit diagnostic"):
            bench.parse_pgn(self.diagnostic(), 1, allow_adjudication=True, provider=self.provider)

    def test_comments_do_not_hide_duplicate_results_or_san_tokens(self):
        self.assertEqual(self.validate(self.checkmate), self.validate(self.checkmate.replace('1. e4', '1. e4 {0.12/6 0.002s}')))
        for suffix in [' 0-1', ' garbage', ' (1. d4)']:
            with self.subTest(suffix=suffix), self.assertRaises(ValueError):
                self.validate(self.checkmate.rstrip() + suffix)


if __name__ == "__main__":
    unittest.main(verbosity=2)
