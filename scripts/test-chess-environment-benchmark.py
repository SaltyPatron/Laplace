#!/usr/bin/env python3
"""Resource admission, parsing and owned-process cleanup contracts for chess calibration."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("chess_benchmark", ROOT / "scripts/benchmark-chess-environment.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


class ChessEnvironmentTests(unittest.TestCase):
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
