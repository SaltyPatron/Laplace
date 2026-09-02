#!/usr/bin/env python3
"""Source-only contract tests for the versioned benchmark suite."""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import unittest

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
        cls.registry = json.loads((ROOT / "scripts/benchmark-profiles.json").read_text(encoding="utf-8"))

    def test_registry_validates_and_every_suite_resolves(self):
        self.suite.validate_registry(self.registry)
        profiles = {item["id"] for item in self.registry["profiles"]}
        self.assertEqual(
            {"core-single", "core-scale", "core-scale-streams", "moby-roundtrip"},
            profiles,
        )
        for suite in self.registry["suites"]:
            self.assertTrue(set(suite["profiles"]) <= profiles)

    def test_throughput_uses_stream_scaling_while_all_preserves_makespan(self):
        suites = {item["id"]: item for item in self.registry["suites"]}
        self.assertEqual(["core-single", "core-scale-streams"], suites["throughput"]["profiles"])
        self.assertEqual(["core-scale-streams"], suites["scale"]["profiles"])
        self.assertNotIn("core-scale", suites["throughput"]["profiles"])
        self.assertIn("core-scale-streams", suites["all"]["profiles"])
        self.assertIn("core-scale", suites["all"]["profiles"])
        self.assertEqual({"quick", "throughput", "core", "scale", "moby", "all"}, set(suites))

    def test_scaling_points_include_physical_and_logical_boundaries(self):
        self.assertEqual([1, 2, 3, 4, 6, 8, 10, 12], self.scale.default_worker_counts(6, 12))
        self.assertEqual([1, 2, 3, 4, 8], self.scale.default_worker_counts(8, 8))

    def test_explicit_scaling_points_are_bounded_by_allowed_logical_cpus(self):
        self.assertEqual([1, 6, 12], self.scale.parse_worker_counts("12,1,6,6", 6, 12))
        with self.assertRaises(ValueError):
            self.scale.parse_worker_counts("13", 6, 12)

    def test_workflow_is_dispatch_only_and_routes_through_suite_runner(self):
        import yaml
        path = ROOT / ".github/workflows/benchmark-evidence.yml"
        workflow = yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
        triggers = workflow["on"]
        names = {triggers} if isinstance(triggers, str) else set(triggers)
        self.assertEqual({"workflow_dispatch"}, names)
        job = workflow["jobs"]["benchmark"]
        commands = "\n".join(step.get("run", "") for step in job["steps"] if isinstance(step, dict))
        self.assertIn("python3 scripts/benchmark_suite.py validate", commands)
        self.assertIn("python3 scripts/benchmark_suite.py \"${args[@]}\"", commands)
        self.assertNotIn("python3 scripts/bench-compose.py", commands)
        self.assertNotIn("python3 scripts/bench-compose-scale.py", commands)
        self.assertNotIn("python3 scripts/bench-compose-stream-scale.py", commands)
        self.assertEqual("laplace-shared-workspace", workflow["concurrency"]["group"])
        self.assertEqual("false", workflow["concurrency"]["cancel-in-progress"])

    def test_workflow_binds_built_core_and_t0_explicitly(self):
        text = (ROOT / ".github/workflows/benchmark-evidence.yml").read_text(encoding="utf-8")
        self.assertIn("build/engine/core/liblaplace_core.so", text)
        self.assertIn("build/engine/core/perfcache/laplace_t0_perfcache.bin", text)
        self.assertIn("LAPLACE_CORE", text)
        self.assertIn("LAPLACE_T0", text)
        self.assertIn("LAPLACE_PERFCACHE_BIN", text)

    def test_suite_runner_binds_build_tree_loader_before_measurement(self):
        text = (ROOT / "scripts/benchmark_suite.py").read_text(encoding="utf-8")
        self.assertIn('env["LAPLACE_CORE"] = str(core)', text)
        self.assertIn('env["LAPLACE_T0"] = str(t0)', text)
        self.assertIn('env["LAPLACE_PERFCACHE_BIN"] = str(t0)', text)
        self.assertIn('env["LD_LIBRARY_PATH"]', text)
        self.assertIn("bench-compose-stream-scale.py", text)

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

    def test_first_scaling_receipt_is_documented_as_makespan_not_serialization(self):
        text = (ROOT / "docs/benchmarks/SCALING_MODES.md").read_text(encoding="utf-8")
        self.assertIn("33608791817", text)
        self.assertIn("41,601,961", text)
        self.assertIn("unique-corpus makespan", text.lower())
        self.assertIn("independent-stream", text.lower())


if __name__ == "__main__":
    unittest.main(verbosity=2)
