#!/usr/bin/env python3
"""Measured desktop selection controls; fixtures are not machine benchmark results."""
import copy
import importlib.util
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PROVISION = load("calibration_provision", "provision-cutechess.py")
SESSION = load("calibration_session", "cutechess-user-engines.py")


class CalibrationControls(unittest.TestCase):
    def setUp(self):
        directory = os.environ.get("TMPDIR")
        if not directory or not Path(directory).is_absolute() or not Path(directory).is_dir():
            raise RuntimeError("controls require an explicit permanent TMPDIR")
        scratch = tempfile.TemporaryDirectory(prefix="calibration-controls-", dir=directory)
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name)
        self.host = {"hostname": "fixture", "platform": "fixture-kernel", "machine": "x86_64",
                     "cpu_models": ["fixture-model"], "affinity_cpu_ids": list(range(8)),
                     "physical_core_groups_within_affinity": [[i, i + 4] for i in range(4)],
                     "physical_memory_bytes": 16 << 30,
                     "cgroup": {"cpu_quota": None, "memory_limit_bytes": None},
                     "effective_cpu_capacity": 8, "available_memory_bytes": 8 << 30}
        self.identity = {"sha256": "a" * 64, "source_commit": "b" * 40,
                         "build_receipt_matches_binary_and_source": True,
                         "source_networks": [{"name": "nn-0123456789ab.nnue", "present": True,
                                              "sha256": "c" * 64, "size_bytes": 123}],
                         "uci_options": {
                             "Threads": {"type": "spin", "min": "1", "max": "1024", "default": "1"},
                             "Hash": {"type": "spin", "min": "1", "max": "33554432", "default": "16"}}}
        self.report = {"schema": "laplace.benchmark.chess-environment/v1", "status": "complete",
                       "parameters": {"repeats": 3, "max_moves": 0},
                       "stockfish_identity": copy.deepcopy(self.identity),
                       "host": copy.deepcopy(self.host), "host_after": copy.deepcopy(self.host),
                       "plan": {"reserved_cpu_capacity": 2,
                                "memory_budget_bytes": 4 << 30,
                                "engine_overhead_estimate_bytes": 128 << 20}}
        self.selection = {"bench_suite_latency": {
            "threads": 4, "hash_mib": 256, "scope": "finite declared bench workload"}}

    def select(self):
        return PROVISION.calibration_configuration(self.report, self.identity,
                                                    self.host, self.selection)

    def test_exact_measured_selection_generates_official_spin_options_and_scoped_receipt(self):
        options, receipt = self.select()
        self.assertEqual(["Threads", "Hash"], [row["name"] for row in options])
        self.assertEqual([4, 256], [row["value"] for row in options])
        self.assertEqual([1, 16], [row["default"] for row in options])
        self.assertEqual("selected", receipt["status"])
        self.assertFalse(receipt["selfplayConcurrencyApplied"])
        self.assertFalse(receipt["evaluatorPoolConfigurationApplied"])
        self.assertEqual(self.selection["bench_suite_latency"], receipt["configuration"])

    def test_incomplete_smoke_or_capped_evidence_cannot_configure_defaults(self):
        for mutate in (
            lambda r: r.update(status="failed"),
            lambda r: r.update(evidence_invalid=True),
            lambda r: r.update(failures=["fixture failure"]),
            lambda r: r["parameters"].update(repeats=2),
            lambda r: r["parameters"].update(max_moves=12),
        ):
            original = copy.deepcopy(self.report)
            mutate(self.report)
            with self.assertRaisesRegex(ValueError, "complete repeated uncapped"):
                self.select()
            self.report = original

    def test_binary_source_build_and_nnue_drift_rejected(self):
        for mutate in (
            lambda i: i.update(sha256="d" * 64),
            lambda i: i.update(source_commit="e" * 40),
            lambda i: i.update(build_receipt_matches_binary_and_source=False),
            lambda i: i["source_networks"][0].update(sha256="f" * 64),
            lambda i: i["source_networks"][0].update(present=False),
        ):
            original = copy.deepcopy(self.identity)
            mutate(self.identity)
            with self.assertRaises(ValueError):
                self.select()
            self.identity = original

    def test_stable_machine_limits_bind_but_transient_available_memory_does_not(self):
        self.host["available_memory_bytes"] -= 1 << 30
        self.select()
        for mutate in (
            lambda h: h.update(hostname="another"),
            lambda h: h.update(affinity_cpu_ids=[0, 1]),
            lambda h: h["cgroup"].update(cpu_quota=4),
            lambda h: h["cgroup"].update(memory_limit_bytes=4 << 30),
            lambda h: h.update(cpu_models=["other"]),
        ):
            original = copy.deepcopy(self.host)
            mutate(self.host)
            with self.assertRaisesRegex(ValueError, "machine"):
                self.select()
            self.host = original
        self.report["host_after"]["machine"] = "another"
        with self.assertRaisesRegex(ValueError, "machine"):
            self.select()

    def test_current_headroom_and_cpu_allowance_are_checked(self):
        self.host["available_memory_bytes"] = 128 << 20
        with self.assertRaisesRegex(ValueError, "memory headroom"):
            self.select()
        self.host["available_memory_bytes"] = 8 << 30
        self.host["effective_cpu_capacity"] = 5
        with self.assertRaisesRegex(ValueError, "CPU allowance"):
            self.select()

    def test_recommendation_must_fit_advertised_engine_options(self):
        self.report["stockfish_identity"]["uci_options"]["Threads"]["max"] = "2"
        with self.assertRaisesRegex(ValueError, "advertised range"):
            self.select()
        self.report["stockfish_identity"]["uci_options"]["Threads"]["max"] = "1024"
        self.selection["bench_suite_latency"]["threads"] = True
        with self.assertRaisesRegex(ValueError, "invalid engine options"):
            self.select()

    def test_report_digest_is_checked_before_any_engine_or_machine_observation(self):
        path = self.root / "report.json"
        path.write_text(json.dumps(self.report))
        with self.assertRaisesRegex(ValueError, "report differs"):
            PROVISION.desktop_calibration(path, "0" * 64, self.root / "absent-stockfish")

    def test_user_options_win_and_missing_defaults_merge_idempotently(self):
        defaults, _ = self.select()
        entry = {"name": "Stockfish", "command": "/direct/stockfish", "protocol": "uci",
                 "options": [{"name": "Threads", "value": 2, "type": "spin"},
                             {"name": "MultiPV", "value": 3, "type": "spin"}],
                 "userSetting": {"preserved": True}}
        unchanged = copy.deepcopy(entry)
        merged, added = SESSION.fill_missing_options(entry, {"options": defaults})
        self.assertEqual(["Hash"], added)
        self.assertEqual(unchanged, entry)
        self.assertEqual(2, merged["options"][0]["value"])
        self.assertEqual(3, merged["options"][1]["value"])
        self.assertEqual(256, merged["options"][2]["value"])
        self.assertEqual(entry["userSetting"], merged["userSetting"])
        self.assertEqual((merged, []), SESSION.fill_missing_options(merged, {"options": defaults}))


    def test_evaluation_defaults_keep_single_analysis_scope_and_explicit_overrides(self):
        options, _ = self.select()
        values, proof = PROVISION.evaluation_configuration(options, {}, self.report, self.host)
        self.assertEqual(1, values["LAPLACE_STOCKFISH_EVAL_PROCESSES"])
        self.assertEqual(4, values["LAPLACE_STOCKFISH_EVAL_THREADS"])
        self.assertEqual(256, values["LAPLACE_STOCKFISH_EVAL_HASH_MB"])
        self.assertFalse(proof["enginePoolThroughputMeasured"])
        override = {"LAPLACE_STOCKFISH_EVAL_THREADS": "2", "LAPLACE_STOCKFISH_EVAL_HASH_MB": "64",
                    "LAPLACE_STOCKFISH_EVAL_PROCESSES": "3"}
        values, proof = PROVISION.evaluation_configuration(options, override, self.report, self.host)
        self.assertEqual({key: int(value) for key, value in override.items()}, values)
        self.assertEqual(6, proof["requestedThreadSlots"])
        self.assertEqual(set(override), set(proof["explicitOverrides"]))

    def test_explicit_evaluator_pool_must_fit_total_cpu_and_memory(self):
        options, _ = self.select()
        for explicit in ({"LAPLACE_STOCKFISH_EVAL_PROCESSES": "2"},
                         {"LAPLACE_STOCKFISH_EVAL_HASH_MB": "8192"},
                         {"LAPLACE_STOCKFISH_EVAL_THREADS": "0"},
                         {"LAPLACE_STOCKFISH_EVAL_HASH_MB": "64\nother=1"}):
            with self.subTest(explicit=explicit), self.assertRaises(ValueError):
                PROVISION.evaluation_configuration(options, explicit, self.report, self.host)

    def test_actual_cold_configuration_preserves_user_values_and_file_mode_then_removes_stale_defaults(self):
        prefix = self.root / "installed"
        env = prefix / "app/laplace-api.env"
        manifest = prefix / "share/laplace/cutechess-desktop.json"
        env.parent.mkdir(parents=True)
        manifest.parent.mkdir(parents=True)
        original = "# caller-owned\nOTHER_SETTING=preserved\nLAPLACE_STOCKFISH_EVAL_HASH_MB=2\n"
        env.write_text(original)
        env.chmod(0o600)
        ownership = env.stat().st_uid, env.stat().st_gid
        stockfish = self.root / "direct-official-engine"
        stockfish.write_text("cold configuration fixture")
        manifest.write_text(json.dumps({"calibration": {
            "status": "selected", "report": "fixture-report", "reportSha256": "a" * 64}}))
        benchmark = load("cold_machine_observer", "benchmark-chess-environment.py")
        host = benchmark.machine()
        options = [{"name": "Threads", "value": 1}, {"name": "Hash", "value": 1}]
        report = copy.deepcopy(self.report)
        report["plan"].update(reserved_cpu_capacity=0, memory_budget_bytes=1 << 30,
                              engine_overhead_estimate_bytes=0)
        identity = {"reportSha256": "a" * 64, "stockfish": self.identity,
                    "machine": PROVISION.calibration_machine(host)}
        keys = {"LAPLACE_STOCKFISH_EVAL_" + key: "" for key in ("THREADS", "HASH_MB", "PROCESSES")}
        keys["LAPLACE_STOCKFISH_EVAL_HASH_MB"] = "3"
        with mock.patch.dict(os.environ, keys), mock.patch.object(
                PROVISION, "desktop_calibration", return_value=(options, identity, json.dumps(report).encode())):
            receipt = PROVISION.configure_evaluation(prefix, stockfish)
        self.assertTrue(receipt["configurationApplied"])
        self.assertEqual(3, receipt["configuration"]["LAPLACE_STOCKFISH_EVAL_HASH_MB"])
        self.assertEqual(2, receipt["installedConfiguration"]["LAPLACE_STOCKFISH_EVAL_HASH_MB"])
        keys["LAPLACE_STOCKFISH_EVAL_HASH_MB"] = ""
        with mock.patch.dict(os.environ, keys), mock.patch.object(
                PROVISION, "desktop_calibration", return_value=(options, identity, json.dumps(report).encode())):
            restored = PROVISION.configure_evaluation(prefix, stockfish)
        self.assertEqual(2, restored["configuration"]["LAPLACE_STOCKFISH_EVAL_HASH_MB"])
        self.assertEqual(1, env.read_text().count("LAPLACE_STOCKFISH_EVAL_HASH_MB="))
        self.assertIn("LAPLACE_STOCKFISH_EVAL_THREADS=1\n", env.read_text())
        self.assertIn("LAPLACE_STOCKFISH_EVAL_PROCESSES=1\n", env.read_text())
        self.assertEqual(0o600, env.stat().st_mode & 0o777)
        self.assertEqual(ownership, (env.stat().st_uid, env.stat().st_gid))
        manifest.write_text(json.dumps({"calibration": {"status": "stale"}}))
        receipt = PROVISION.configure_evaluation(prefix, stockfish)
        self.assertFalse(receipt["configurationApplied"])
        self.assertEqual(original, env.read_text())

    def test_evaluation_block_parser_preserves_unrelated_text_and_rejects_ambiguous_blocks(self):
        text = "USER=value\n" + PROVISION.EVALUATION_BEGIN + "\nowned=1\n" + PROVISION.EVALUATION_END + "\n# retained\n"
        self.assertEqual("USER=value\n# retained\n", PROVISION.without_evaluation_defaults(text))
        for broken in (PROVISION.EVALUATION_BEGIN + "\n",
                       PROVISION.EVALUATION_END + "\n", text + text):
            with self.assertRaises(ValueError):
                PROVISION.without_evaluation_defaults(broken)

    def test_no_selection_does_not_rewrite_existing_user_options(self):
        entry = {"options": [{"name": "Threads", "value": 7}]}
        self.assertEqual((entry, []), SESSION.fill_missing_options(entry, {}))


if __name__ == "__main__":
    unittest.main()
