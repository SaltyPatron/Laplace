#!/usr/bin/env python3
"""Resource admission, parsing and owned-process cleanup contracts for chess calibration."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("chess_benchmark", ROOT / "scripts/benchmark-chess-environment.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class ChessEnvironmentTests(unittest.TestCase):
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

    def test_missing_service_manager_and_gpu_utility_are_reported_without_starting_services(self):
        with patch.object(bench.subprocess, "run", side_effect=FileNotFoundError("systemctl")) as run, \
                patch.object(bench.shutil, "which", return_value=None), \
                patch.object(bench, "http_readiness", return_value={"ready": False}):
            result = bench.runtime_capabilities(time.monotonic() + 2)
        self.assertFalse(result["service_mutations_performed"])
        self.assertFalse(result["application_startup_measured"])
        self.assertEqual(result["systemd"]["status"], "unavailable")
        self.assertFalse(result["nvidia"]["utility_installed"])
        self.assertFalse(result["nvidia"]["compute_execution_measured"])
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

    def test_incomplete_pgn_and_transcript_cannot_be_success(self):
        pgn = '[Event "test"]\n[White "A"]\n[Black "B"]\n[Result "1/2-1/2"]\n[PlyCount "2"]\n[Termination "adjudication"]\n\n1. e4 e5 1/2-1/2\n'
        games = bench.parse_pgn(pgn, 1)
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
