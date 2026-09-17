#!/usr/bin/env python3
"""Source-only contract tests for the versioned benchmark suite."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
from argparse import Namespace

ROOT = Path(__file__).resolve().parents[1]


def load_module(name: str, relative: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class BenchmarkSuiteTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.suite = load_module("benchmark_suite", "scripts/benchmark_suite.py")
        cls.scale = load_module("bench_compose_scale", "scripts/bench-compose-scale.py")
        cls.stream = load_module("bench_compose_stream_scale", "scripts/bench-compose-stream-scale.py")
        cls.dag = load_module("bench_compose_dag_scale", "scripts/bench-compose-dag-scale.py")
        cls.scale_plan = load_module("benchmark_scale_plan", "scripts/benchmark_scale_plan.py")
        cls.forward = load_module("bench_forward_program", "scripts/bench-forward-program.py")
        cls.registry = json.loads((ROOT / "scripts/benchmark-profiles.json").read_text(encoding="utf-8"))

    def test_registry_validates_and_every_suite_resolves(self):
        self.suite.validate_registry(self.registry)
        profiles = {item["id"] for item in self.registry["profiles"]}
        self.assertEqual(
            {"core-single", "core-scale", "core-scale-streams", "core-dag-scale", "moby-roundtrip", "query-forward", "chess-environment", "postgres-geometry", "recorded-chess"},
            profiles,
        )
        for suite in self.registry["suites"]:
            self.assertTrue(set(suite["profiles"]) <= profiles)

    def test_throughput_uses_stream_scaling_while_all_preserves_makespan(self):
        suites = {item["id"]: item for item in self.registry["suites"]}
        self.assertEqual(["core-single", "core-scale-streams"], suites["throughput"]["profiles"])
        self.assertEqual(["core-dag-scale", "core-scale-streams"], suites["scale"]["profiles"])
        self.assertEqual(["core-dag-scale"], suites["dag"]["profiles"])
        self.assertNotIn("core-scale", suites["throughput"]["profiles"])
        self.assertIn("core-dag-scale", suites["all"]["profiles"])
        self.assertIn("core-scale-streams", suites["all"]["profiles"])
        self.assertIn("core-scale", suites["all"]["profiles"])
        self.assertEqual(["query-forward"], suites["query"]["profiles"])
        self.assertNotIn("query-forward", suites["all"]["profiles"])
        self.assertEqual(["chess-environment"], suites["chess"]["profiles"])
        self.assertEqual({"quick", "throughput", "core", "scale", "dag", "moby", "query", "chess", "geometry", "recorded", "all"}, set(suites))

    def test_chess_suite_does_not_require_unrelated_native_artifacts(self):
        with tempfile.TemporaryDirectory() as folder:
            args = Namespace(suite="chess", repeats=2, receipt_dir=folder,
                             core="/missing/core", t0="/missing/t0", corpus_dir=folder,
                             moby_path="/missing/book", scale_workers=None, database="unused",
                             chess_stockfish="/selected/source/src/stockfish",
                             chess_cutechess="/selected/build/cutechess-cli", chess_cpu_budget=4,
                             chess_memory_mib=1024, chess_laplace_uci=None, chess_reserve_cpus=1)
            with mock.patch.object(self.suite, "git_sha", return_value="source-revision"), \
                 mock.patch.object(self.suite, "exact_env", side_effect=AssertionError("core must not be required")), \
                 mock.patch.object(self.suite, "run_profile", return_value={"profile":"chess-environment"}) as run:
                self.assertEqual(0, self.suite.run_suite(args))
            forwarded = run.call_args.args[-3]
            self.assertEqual(["--stockfish", args.chess_stockfish, "--cutechess", args.chess_cutechess,
                              "--cpu-budget", "4", "--memory-mib", "1024", "--reserve-cpus", "1"], forwarded)
            receipt = json.loads((Path(folder) / "suite-receipt.json").read_text())
            self.assertEqual({"repository_sha":"source-revision"}, receipt["artifact_identity"])

    def test_geometry_suite_uses_existing_pg_without_a_core_build(self):
        with tempfile.TemporaryDirectory() as folder:
            args = Namespace(suite="geometry", repeats=2, receipt_dir=folder,
                             core="/missing/core", t0="/missing/t0", corpus_dir=folder,
                             moby_path="/missing/book", scale_workers=None, database="laplace",
                             geometry_rows=1234, geometry_transaction_rows=500,
                             geometry_concurrency="1,2", geometry_max_bytes=1000000)
            with mock.patch.object(self.suite, "git_sha", return_value="source-revision"), \
                 mock.patch.object(self.suite, "exact_env", side_effect=AssertionError("core must not be required")), \
                 mock.patch.object(self.suite, "run_profile", return_value={"profile":"postgres-geometry"}) as run:
                self.assertEqual(0, self.suite.run_suite(args))
            self.assertEqual(["--rows", "1234", "--transaction-rows", "500",
                              "--concurrency", "1,2", "--max-bytes", "1000000"], run.call_args.args[-2])
            self.assertNotIn("postgres-geometry", self.suite.suite_map(self.registry)["all"]["profiles"])

    def test_core_suite_still_rejects_missing_native_artifact(self):
        with tempfile.TemporaryDirectory() as folder:
            args = Namespace(suite="core", repeats=2, receipt_dir=folder,
                             core=str(Path(folder) / "missing-core"), t0=str(Path(folder) / "missing-t0"))
            with mock.patch.object(self.suite, "git_sha", return_value="source-revision"):
                with self.assertRaisesRegex(SystemExit, "built core library not found"):
                    self.suite.run_suite(args)

    def test_recorded_suite_uses_installed_api_and_forwards_duration_bounds(self):
        with tempfile.TemporaryDirectory() as folder:
            args = Namespace(suite="recorded", repeats=1, receipt_dir=folder,
                             core="/missing/core", t0="/missing/t0", corpus_dir=folder,
                             moby_path="/missing/book", scale_workers=None, database="unused",
                             recorded_games=24, recorded_duration_seconds=60,
                             recorded_total_timeout=900, recorded_max_sustained_cases=128)
            with mock.patch.object(self.suite, "git_sha", return_value="collector-revision"), \
                 mock.patch.object(self.suite, "exact_env", side_effect=AssertionError("collector core build is unrelated")), \
                 mock.patch.object(self.suite, "run_profile", return_value={"profile":"recorded-chess"}) as run:
                self.assertEqual(0, self.suite.run_suite(args))
            self.assertEqual(["--games", "24", "--duration-seconds", "60", "--total-timeout", "900",
                              "--max-sustained-cases", "128"], run.call_args.args[-1])
            self.assertNotIn("recorded-chess", self.suite.suite_map(self.registry)["all"]["profiles"])

    def test_recorded_profile_executes_existing_collector_with_explicit_duration(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "recorded-chess/receipt.json"
            target.parent.mkdir()
            target.write_text('{"status":"passed","durationQualified":false,"targetMet":false}')
            with mock.patch.object(self.suite, "capture_rapl", return_value=[]), \
                 mock.patch.object(self.suite, "run_and_tee", return_value=(0, 100)) as run:
                result = self.suite.run_profile(self.suite.profile_map(self.registry)["recorded-chess"],
                    Path(folder), {}, 1, Path(folder), Path(folder), None, "unused", recorded_args=["--games", "24"])
            command = run.call_args.args[0]
            self.assertEqual("scripts/benchmark-recorded-chess.py", command[1])
            self.assertIn("--duration-seconds", command)
            self.assertNotIn("--max-moves", command)
            self.assertFalse(result["result"]["targetMet"])

    def test_chess_configuration_profile_requests_complete_games(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "chess-environment/report.json"
            target.parent.mkdir()
            target.write_text('{"status":"complete"}')
            with mock.patch.object(self.suite, "capture_rapl", return_value=[]), \
                 mock.patch.object(self.suite, "run_and_tee", return_value=(0,100)) as run:
                self.suite.run_profile(self.suite.profile_map(self.registry)["chess-environment"],
                    Path(folder), {}, 3, Path(folder), Path(folder), None, "unused")
            command=run.call_args.args[0]
            self.assertEqual("0",command[command.index("--max-moves")+1])
            self.assertEqual("8",command[command.index("--match-depth")+1])

    def test_raw_harness_scaling_points_still_expose_full_topology_for_explicit_use(self):
        self.assertEqual([1, 2, 3, 4, 6, 8, 10, 12], self.scale.default_worker_counts(6, 12))
        self.assertEqual([1, 2, 3, 4, 8], self.scale.default_worker_counts(8, 8))

    def test_explicit_scaling_points_are_bounded_by_allowed_logical_cpus(self):
        self.assertEqual([1, 6, 12], self.scale.parse_worker_counts("12,1,6,6", 6, 12))
        with self.assertRaises(ValueError):
            self.scale.parse_worker_counts("13", 6, 12)

    def test_managed_host_default_reserves_headroom_on_6c12t(self):
        points, cap, source = self.scale_plan.resolve_points(6, 12, None, 2, False)
        self.assertEqual([1, 2, 3, 4, 6, 8, 10], points)
        self.assertEqual(10, cap)
        self.assertEqual("derived", source)
        self.assertNotIn(12, points)

    def test_saturation_requires_explicit_opt_in(self):
        with self.assertRaises(ValueError):
            self.scale_plan.resolve_points(6, 12, "1,6,12", 2, False)
        points, cap, source = self.scale_plan.resolve_points(6, 12, "1,6,12", 2, True)
        self.assertEqual([1, 6, 12], points)
        self.assertEqual(12, cap)
        self.assertEqual("explicit", source)

    def test_saturation_default_can_include_full_logical_boundary(self):
        points, cap, source = self.scale_plan.resolve_points(6, 12, None, 2, True)
        self.assertEqual([1, 2, 3, 4, 6, 9, 12], points)
        self.assertEqual(12, cap)
        self.assertEqual("derived", source)

    def test_workflow_stages_immutable_driver_and_preserves_every_dispatch(self):
        workflow = (ROOT / ".github/workflows/benchmark-evidence.yml").read_text(encoding="utf-8")
        driver = (ROOT / "scripts/benchmark-evidence-ci.sh").read_text(encoding="utf-8")
        contract = json.loads((ROOT / "scripts/benchmark-suite-contract.json").read_text(encoding="utf-8"))
        registry_suites = {item["id"] for item in self.registry["suites"]}

        self.assertIn("on:\n  workflow_dispatch:", workflow)
        self.assertNotIn("\n  push:\n", workflow)
        self.assertNotIn("\n  workflow_call:\n", workflow)
        for suite in registry_suites:
            self.assertIn(suite, workflow)

        self.assertEqual(1, workflow.count("runs-on: [self-hosted, laplace]"))
        self.assertEqual(1, workflow.count("host-resource.lock"))
        self.assertNotIn("\nconcurrency:\n", workflow)

        lock = workflow.index('exec 9>"$lock_root/host-resource.lock"')
        workflow_fetch = workflow.index('git fetch --no-tags --depth=1 origin "$DISPATCH_SHA"')
        stage = workflow.index('git show "$workflow_sha:scripts/benchmark-evidence-ci.sh"')
        target_fetch = workflow.index('git fetch --no-tags --prune origin "$target"')
        checkout = workflow.index('git checkout --no-overwrite-ignore --detach "$resolved"')
        execute = workflow.index('exec bash "$driver"')
        self.assertEqual(
            [lock, workflow_fetch, stage, target_fetch, checkout, execute],
            sorted([lock, workflow_fetch, stage, target_fetch, checkout, execute]),
        )

        self.assertIn("python3 scripts/benchmark_suite.py validate", driver)
        self.assertIn('python3 scripts/benchmark_suite.py "${run_args[@]}"', driver)
        self.assertNotIn("python3 scripts/bench-compose.py", workflow)
        self.assertNotIn("python3 scripts/bench-compose-scale.py", workflow)
        self.assertNotIn("python3 scripts/bench-compose-stream-scale.py", workflow)
        self.assertNotIn("python3 scripts/bench-compose-dag-scale.py", workflow)
        self.assertNotIn("python3 scripts/bench-forward-program.py", workflow)

        self.assertEqual(["workflow_dispatch"], contract["triggers"])
        self.assertEqual("scripts/benchmark-evidence-ci.sh", contract["workflow_driver"])
        self.assertTrue(contract["host_ownership"]["preserve_every_dispatch"])
        self.assertIsNone(contract["host_ownership"]["github_actions_concurrency_group"])
        self.assertEqual("core-dag-scale", contract["scaling"]["single_dag_profile"])
        self.assertTrue(contract["scaling"]["single_dag_fixed_work"])
        self.assertTrue(contract["scaling"]["single_dag_require_complete_tree_parity"])
        self.assertFalse(contract["scaling"]["replicated_streams_are_single_dag_proof"])

    def test_workflow_requires_explicit_saturation_and_records_scale_plan(self):
        workflow = (ROOT / ".github/workflows/benchmark-evidence.yml").read_text(encoding="utf-8")
        driver = (ROOT / "scripts/benchmark-evidence-ci.sh").read_text(encoding="utf-8")
        self.assertIn("reserve_logical_cpus:", workflow)
        self.assertIn('default: "2"', workflow)
        self.assertIn("allow_saturation:", workflow)
        self.assertIn("default: false", workflow)
        self.assertIn("scale-plan.json", driver)
        self.assertIn("--reserve-logical", driver)
        self.assertIn("--allow-saturation", driver)
        self.assertIn("resolved_workers_csv", driver)
        self.assertIn('"$SUITE" == dag', driver)

    def test_workflow_driver_binds_built_core_t0_and_execution_identity(self):
        text = (ROOT / "scripts/benchmark-evidence-ci.sh").read_text(encoding="utf-8")
        self.assertIn("build/engine/core/liblaplace_core.so", text)
        self.assertIn("build/engine/core/perfcache/laplace_t0_perfcache.bin", text)
        self.assertIn("build/extension/laplace_substrate/laplace_substrate.control", text)
        self.assertIn("build/extension/laplace_substrate/laplace_execution_module.txt", text)
        self.assertIn('run_args+=(--core "$core" --t0 "$t0")', text)
        self.assertIn('sha256sum "$core" "$t0" "$control" "$execution"', text)
        self.assertIn('if [[ "$SUITE" != chess && "$SUITE" != geometry && "$SUITE" != recorded ]]', text)

    def test_suite_runner_binds_build_tree_loader_before_measurement(self):
        text = (ROOT / "scripts/benchmark_suite.py").read_text(encoding="utf-8")
        self.assertIn('env["LAPLACE_CORE"] = str(core)', text)
        self.assertIn('env["LAPLACE_T0"] = str(t0)', text)
        self.assertIn('env["LAPLACE_PERFCACHE_BIN"] = str(t0)', text)
        self.assertIn('env["LD_LIBRARY_PATH"]', text)
        self.assertIn("bench-compose-stream-scale.py", text)
        self.assertIn("bench-compose-dag-scale.py", text)
        self.assertIn("bench-forward-program.py", text)

    def test_core_single_is_one_worker_floor_not_core_count_extrapolation(self):
        text = (ROOT / "scripts/bench-compose.py").read_text(encoding="utf-8")
        self.assertIn("measured one-worker floor", text)
        self.assertIn("never derive it by multiplying", text)
        self.assertIn("WORK_INPUT", text)
        self.assertIn("WORK_SHAPE", text)
        self.assertIn("nodes_per_codepoint", text)
        self.assertIn("nodes_per_tok4", text)
        self.assertNotIn("a core count multiplies this", text)

    def test_core_single_parser_preserves_exact_work_amplification(self):
        log = """corpus      : 1,158 documents, 51.2 MB, 51,223,726 codepoints
core        : /tmp/liblaplace_core.so
WORK_INPUT documents=1158 bytes=51200000 codepoints=51223726
  run 1:  29.079 s      1761.5k codepoints/s      440.4k BPE-equiv tok/s   125,793,955 nodes
BEST, single-threaded, no DB:
  1,761.5k codepoints/s   440.4k BPE-equiv tokens/s
  4,325.9k tier-tree nodes/s   (125,793,955 nodes built)
  2.455775 tier-tree nodes/codepoint   9.823101 tier-tree nodes/4-char token-equivalent
WORK_SHAPE tier_tree_nodes=125793955 nodes_per_codepoint=2.455775181212 nodes_per_tok4=9.823100724848 chars_per_tok4=4
"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "core-single.log"
            path.write_text(log, encoding="utf-8")
            result = self.suite.parse_core_single(path)
        self.assertEqual(1158, result["input_documents"])
        self.assertEqual(51200000, result["input_bytes"])
        self.assertEqual(51223726, result["input_codepoints"])
        self.assertEqual(125793955, result["tier_tree_nodes"])
        self.assertAlmostEqual(2.455775181212, result["tier_tree_nodes_per_codepoint"], places=12)
        self.assertAlmostEqual(9.823100724848, result["tier_tree_nodes_per_bpe_equivalent_token_4chars"], places=12)

    def test_core_single_parser_rejects_inconsistent_work_shape(self):
        log = """WORK_INPUT documents=1 bytes=4 codepoints=4
BEST, single-threaded, no DB:
  1.0k codepoints/s   0.2k BPE-equiv tokens/s
  2.0k tier-tree nodes/s   (8 nodes built)
WORK_SHAPE tier_tree_nodes=8 nodes_per_codepoint=1.000000000000 nodes_per_tok4=4.000000000000 chars_per_tok4=4
"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.log"
            path.write_text(log, encoding="utf-8")
            with self.assertRaises(ValueError):
                self.suite.parse_core_single(path)

    def test_query_cases_are_versioned_and_span_hops_fanout_and_ambiguity(self):
        cases = self.forward.load_cases(ROOT / "scripts/benchmark-forward-cases.json")
        self.assertGreaterEqual(len(cases), 5)
        dog = [case for case in cases if case["prompt"] == "What is a dog?"]
        self.assertEqual({1, 2, 3}, {case["hops"] for case in dog})
        self.assertEqual({4, 8, 16}, {case["fanout"] for case in dog})
        self.assertTrue(any("pawn" in case["prompt"].lower() for case in cases))
        self.assertTrue(any("opposite" in case["prompt"].lower() for case in cases))
        self.assertTrue(all(case["spread"] == 0.0 for case in cases))

    def test_query_runtime_binding_is_content_versioned_and_exact(self):
        with tempfile.TemporaryDirectory() as directory:
            control = Path(directory) / "laplace_substrate.control"
            module = Path(directory) / "laplace_execution_module.txt"
            control.write_text("default_version = '0123456789abcdef'\n", encoding="utf-8")
            module.write_text("laplace_execution_fedcba9876543210\n", encoding="utf-8")
            binding = self.forward.parse_built_binding(control, module)
        self.assertEqual("0123456789abcdef", binding["extension_version"])
        self.assertEqual("laplace_execution_fedcba9876543210", binding["execution_module"])
        sql = self.forward.runtime_binding_sql()
        self.assertIn("pg_extension", sql)
        self.assertIn("generation", sql)
        self.assertIn("forward_program", sql)
        self.assertIn("p.probin", sql)

    def test_query_preflight_and_measurement_use_the_same_forward_call(self):
        case = {
            "prompt": "What is a dog?", "steps": 24, "max_stride": 5,
            "spread": 0.0, "top_k": 10, "seed": 17, "hops": 2, "fanout": 8,
        }
        call = self.forward.forward_call(case)
        self.assertIn("generation.forward_program(", call)
        self.assertIn("'What is a dog?'", call)
        text = (ROOT / "scripts/bench-forward-program.py").read_text(encoding="utf-8")
        self.assertIn("EXPLAIN ({opts}) SELECT * FROM {call}", text)
        self.assertIn("ANALYZE TRUE, BUFFERS TRUE, WAL TRUE", text)
        self.assertIn("default_transaction_read_only=on", text)
        self.assertIn("planner_cost_is_billing_unit", text)
        self.assertIn("world_must_remain_stable", text)

    def test_query_trace_summary_preserves_typed_work_and_terminal_fingerprint(self):
        rows = [
            {
                "event": "route", "routing_round": 1, "candidate_count": 7,
                "ordered_context_count": 3, "proposal_channel_count": 9,
                "exact_channel_count": 5, "sequence_occurrences": 0,
                "covered_occurrences": 2, "relation_families": 3,
                "opposed_occurrences": 1, "support_sources": 4, "support_contexts": 2,
            },
            {
                "event": "emit", "routing_round": 1, "candidate_count": 4,
                "ordered_context_count": 4, "proposal_channel_count": 8,
                "exact_channel_count": 6, "sequence_occurrences": 11,
                "covered_occurrences": 3, "relation_families": 2,
                "opposed_occurrences": 0, "support_sources": 5, "support_contexts": 3,
            },
            {
                "event": "complete", "routing_round": 1, "candidate_count": 0,
                "program_id": "\\x01", "output_fingerprint": "\\x02",
                "semantic_act_id": "\\x03", "completion": True,
                "disposition": "complete", "output_count": 1,
                "required_obligations": 2, "satisfied_obligations": 2,
                "remaining_required": 0,
            },
        ]
        result = self.forward.summarize_trace(rows)
        self.assertEqual(3, result["trace_rows"])
        self.assertEqual(7, result["routing_rounds"][0]["max_candidates"])
        self.assertEqual(11, result["routing_rounds"][0]["sequence_occurrences_sum"])
        self.assertEqual(5, result["routing_rounds"][0]["max_support_sources"])
        self.assertEqual("\\x02", result["semantic_fingerprint"]["output_fingerprint"])
        self.assertEqual("complete", result["terminal_event"])

    def test_makespan_scale_harness_does_not_extrapolate_single_thread_result(self):
        text = (ROOT / "scripts/bench-compose-scale.py").read_text(encoding="utf-8")
        self.assertIn("os.sched_setaffinity", text)
        self.assertIn("content_witness_tree_build", text)
        self.assertIn("speedup_vs_1_worker", text)
        self.assertIn("parallel_efficiency", text)
        self.assertNotIn("464800", text)
        self.assertNotIn("3.4M", text)

    def test_stream_scale_executes_real_work_per_worker(self):
        text = (ROOT / "scripts/bench-compose-stream-scale.py").read_text(encoding="utf-8")
        self.assertIn("replicated-independent-streams", text)
        self.assertIn("scale._worker", text)
        self.assertIn("corpus_codepoints * workers", text)
        self.assertIn("total_codepoints_executed", text)
        self.assertIn("nodes_per_worker", text)
        self.assertNotIn("464800", text)
        self.assertNotIn("3.4M", text)

    def test_dag_scale_holds_one_object_fixed_and_gates_on_complete_tree_parity(self):
        text = (ROOT / "scripts/bench-compose-dag-scale.py").read_text(encoding="utf-8")
        self.assertIn("single-semantic-dag-frontier", text)
        self.assertIn("content_witness_tree_build_workers", text)
        self.assertIn('"work_held_fixed": True', text)
        self.assertIn("tree_fingerprint_sha256", text)
        self.assertIn("coord_binary64", text)
        self.assertIn("hilbert128", text)
        self.assertIn("semantic drift", text)
        self.assertIn("os.sched_setaffinity", text)
        self.assertNotIn("multiprocessing", text)
        self.assertNotIn("partition_docs", text)
        self.assertNotIn("scale._worker", text)

    def test_first_scaling_receipt_is_documented_as_makespan_not_serialization(self):
        text = (ROOT / "docs/benchmarks/SCALING_MODES.md").read_text(encoding="utf-8")
        self.assertIn("33608791817", text)
        self.assertIn("41,601,961", text)
        self.assertRegex(text.lower(), r"unique-corpus(?:, file-grain)? makespan")
        self.assertIn("independent-stream", text.lower())


class ChessRuntimeEnvironmentTests(unittest.TestCase):
    def test_installed_runtime_reaches_child_without_artifact_or_log_export(self):
        import contextlib
        import io
        import os
        wrapper = load_module("chess_runtime_env", "scripts/chess-runtime-env.py")
        with tempfile.TemporaryDirectory() as folder:
            prefix = Path(folder)
            (prefix / "app").mkdir()
            (prefix / "app/laplace-api.env").write_text(
                'LAPLACE_DB="Host=installed;Password=fixture-password;Database=corpus"\n'
                'LAPLACE_PERFCACHE_BIN=/configured/native/perfcache.bin\n'
                'LAPLACE_UCI_SUBSTRATE=substrate\n'
                'LD_LIBRARY_PATH=/configured/native/lib\n'
                'LAPLACE_OPERATOR_TOKEN=must-not-forward\n')
            output = io.StringIO()
            with mock.patch.dict(os.environ, {"PATH": "/usr/bin", "LAPLACE_PERFCACHE_BIN": "/explicit/cache.bin"}, clear=True), \
                 mock.patch.object(wrapper.os, "execvpe") as execute, \
                 contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
                wrapper.main(["--prefix", str(prefix), "--", "python3", "check.py", "argument with spaces"])
            executable, command, environment = execute.call_args.args
            self.assertEqual("python3", executable)
            self.assertEqual(["python3", "check.py", "argument with spaces"], command)
            self.assertEqual("Host=installed;Password=fixture-password;Database=corpus", environment["LAPLACE_DB"])
            self.assertEqual("/explicit/cache.bin", environment["LAPLACE_PERFCACHE_BIN"])
            self.assertEqual("/configured/native/lib", environment["LD_LIBRARY_PATH"])
            self.assertEqual("substrate", environment["LAPLACE_UCI_SUBSTRATE"])
            self.assertNotIn("LAPLACE_OPERATOR_TOKEN", environment)
            self.assertEqual("", output.getvalue())
            self.assertEqual({"app"}, {item.name for item in prefix.iterdir()})



if __name__ == "__main__":
    unittest.main(verbosity=2)
