#!/usr/bin/env python3
"""Contract and architecture gate for the deployed eval operation lane."""

from __future__ import annotations

import importlib.util
import json
import os
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stdout, redirect_stderr
from io import BytesIO, StringIO
from pathlib import Path
from unittest.mock import patch

import yaml

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

from laplace_api import op_rows  # noqa: E402


def _load_eval_generation():
    path = ROOT / "scripts" / "eval-generation.py"
    spec = importlib.util.spec_from_file_location("eval_generation", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def _load_primary_workflow():
    path = ROOT / ".github" / "workflows" / "laplace.yml"
    workflow = yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
    if not isinstance(workflow, dict):
        raise ValueError("laplace.yml must be a mapping")
    return workflow


def _load_test_profiles():
    value = json.loads((ROOT / "scripts/test-profiles.json").read_text(encoding="utf-8"))
    return value["suites"]


class _Response(BytesIO):
    def __init__(self, data: bytes, headers: dict[str, str] | None = None):
        super().__init__(data)
        self.headers = headers or {}

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        self.close()


class EvalOperationLaneTests(unittest.TestCase):
    @staticmethod
    def diagnostic_rows(event="unresolved", **changes):
        return [{"event": "route", "entity": "\\x" + "12" * 16, "declared_result": False}, {
            "event": event, "completion": event == "complete", "disposition": "exhausted",
            "required_obligations": 3, "satisfied_obligations": 0,
            "remaining_required": 3, "output_count": 0, **changes}]

    @staticmethod
    def diagnostic_op(rows):
        def read(_api, name, args, **limits):
            if name == "public.laplace_hash128_blake3":
                return [{"laplace_hash128_blake3": "\\x" + "89" * 16}]
            if name == "hash128_lo":
                return [{"hash128_lo": -123}]
            return rows
        return read

    def test_failure_diagnostic_uses_native_seed_and_preserves_both_terminal_receipts(self):
        module = _load_eval_generation()
        rows = self.diagnostic_rows()
        probes = [{"id": "failure", "class": "forward", "prompt": "What is a glacier?"}]
        with patch.object(module, "op_rows", side_effect=self.diagnostic_op(rows)) as read:
            report = module.collect_forward_diagnostics("http://laplace", probes)
        self.assertEqual(4, read.call_count)
        self.assertEqual({"data": "\\x" + probes[0]["prompt"].encode().hex()}, read.call_args_list[0].args[2])
        self.assertEqual(["normal", "wider"], [row["configuration"] for row in report["executions"]])
        for item, bounds in zip(report["executions"], [(2, 8), (8, 32)]):
            self.assertEqual("retained", item["status"])
            self.assertEqual(rows, item["rows"])
            self.assertEqual(rows[-1], item["terminal"])
            args = item["arguments"]
            self.assertEqual((128, 5, 0.6, 10, -123), tuple(args[key] for key in
                ("p_steps", "p_max_stride", "p_spread", "p_top_k", "p_seed")))
            self.assertEqual(bounds, (args["p_hops"], args["p_fanout"]))
            self.assertIsNone(args["p_prior_frontier"])
            self.assertIsNone(args["p_output_relation_types"])
        self.assertFalse(report["executions"][0]["terminal"]["completion"])

    def test_failure_diagnostic_retains_exact_completed_control_without_rewriting_failure(self):
        module = _load_eval_generation()
        rows = self.diagnostic_rows("complete", disposition="complete", satisfied_obligations=3,
                                    remaining_required=0, output_count=1)
        probe = {"id": "control", "class": "forward", "prompt": "The opposite of hot is"}
        with patch.object(module, "op_rows", side_effect=self.diagnostic_op(rows)):
            report = module.collect_forward_diagnostics("http://laplace", [probe])
        self.assertEqual(rows[-1], report["executions"][0]["terminal"])
        self.assertNotIn("ok", report)

    def test_failure_diagnostic_rejects_missing_duplicate_and_malformed_terminal_rows(self):
        module = _load_eval_generation()
        probe = {"class": "forward", "prompt": "Water is made of"}
        for rows in ([], self.diagnostic_rows() * 2,
                     self.diagnostic_rows(completion="false"),
                     self.diagnostic_rows(remaining_required=True)):
            with self.subTest(rows=rows), patch.object(module, "op_rows", side_effect=self.diagnostic_op(rows)):
                report = module.collect_forward_diagnostics("http://laplace", [probe])
            self.assertTrue(all(item["status"] == "unavailable" for item in report["executions"]))
            self.assertEqual(rows, report["executions"][0]["rows"])

    def test_failure_diagnostic_records_unavailable_or_truncated_ops_and_continues(self):
        module = _load_eval_generation()
        reader = self.diagnostic_op(self.diagnostic_rows())
        def truncated(api, name, args, **limits):
            if name == "generation.forward_program" and args["p_hops"] == 2:
                raise module.LaplaceApiError("operation truncated at 2048 rows")
            return reader(api, name, args, **limits)
        with patch.object(module, "op_rows", side_effect=truncated):
            report = module.collect_forward_diagnostics("http://laplace", [{"class":"forward","prompt":"dog"}])
        self.assertEqual(["unavailable", "retained"], [item["status"] for item in report["executions"]])
        self.assertIn("truncated", report["executions"][0]["error"])

    def test_failure_diagnostic_enforces_prompt_and_retained_byte_limits(self):
        module = _load_eval_generation()
        probes = [{"id":str(i),"class":"forward","prompt":str(i)} for i in range(6)]
        with patch.object(module, "op_rows", side_effect=self.diagnostic_op(self.diagnostic_rows())) as read:
            report = module.collect_forward_diagnostics("http://laplace", probes + probes)
        self.assertEqual(16, read.call_count)
        self.assertEqual(2, len(report["omitted"]))
        with patch.object(module, "DIAGNOSTIC_MAX_BYTES", 1), patch.object(
                module, "op_rows", side_effect=self.diagnostic_op(self.diagnostic_rows())):
            report = module.collect_forward_diagnostics("http://laplace", probes)
        self.assertEqual("byte-budget-exhausted", report["status"])
        self.assertNotIn("rows", report["executions"][0])

    def test_failure_diagnostic_whole_deadline_interrupts_wait_and_restores_handler(self):
        module = _load_eval_generation()
        handler = signal.getsignal(signal.SIGALRM)
        started = time.monotonic()
        def stalled(*_args, **_kwargs):
            time.sleep(2)
            self.fail("deadline did not interrupt the operation")
        # Exercise the real client retry boundary: a TimeoutError subclass would
        # be caught and retried there after the one-shot alarm had already fired.
        with patch.object(module, "DIAGNOSTIC_SECONDS", 0.03), patch("laplace_api.urlopen", side_effect=stalled):
            report = module.collect_forward_diagnostics("http://laplace", [{"class":"forward","prompt":"dog"}])
        self.assertLess(time.monotonic() - started, 1)
        self.assertEqual("deadline-exhausted", report["status"])
        self.assertEqual(handler, signal.getsignal(signal.SIGALRM))
        self.assertEqual((0.0, 0.0), signal.getitimer(signal.ITIMER_REAL))

    def test_failure_diagnostic_preserves_earlier_external_deadline_and_exception(self):
        module = _load_eval_generation()
        original = signal.getsignal(signal.SIGALRM)
        error = TimeoutError("outer caller deadline")
        calls = []
        def outer(signum, frame):
            calls.append(signum)
            raise error
        signal.signal(signal.SIGALRM, outer)
        signal.setitimer(signal.ITIMER_REAL, 0.03)
        started = time.monotonic()
        try:
            with patch.object(module, "DIAGNOSTIC_SECONDS", 0.4), patch(
                    "laplace_api.urlopen", side_effect=lambda *_args, **_kwargs: time.sleep(2)), \
                    self.assertRaises(TimeoutError) as stopped:
                module.collect_forward_diagnostics("http://laplace", [{"class":"forward","prompt":"dog"}])
            self.assertIs(error, stopped.exception)
            self.assertLess(time.monotonic() - started, 0.25)
            self.assertEqual([signal.SIGALRM], calls)
            self.assertIs(outer, signal.getsignal(signal.SIGALRM))
            self.assertEqual((0.0, 0.0), signal.getitimer(signal.ITIMER_REAL))
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, original)

    def test_failure_diagnostic_restores_external_period_after_delivering_alarm(self):
        module = _load_eval_generation()
        original = signal.getsignal(signal.SIGALRM)
        calls = []
        def outer(signum, frame):
            calls.append(signum)
        signal.signal(signal.SIGALRM, outer)
        signal.setitimer(signal.ITIMER_REAL, 0.03, 0.5)
        try:
            with patch.object(module, "DIAGNOSTIC_SECONDS", 0.4), patch(
                    "laplace_api.urlopen", side_effect=lambda *_args, **_kwargs: time.sleep(2)):
                report = module.collect_forward_diagnostics("http://laplace", [{"class":"forward","prompt":"dog"}])
            self.assertEqual([signal.SIGALRM], calls)
            self.assertEqual("outer-caller", report["deadline_owner"])
            remaining, interval = signal.getitimer(signal.ITIMER_REAL)
            self.assertGreater(remaining, 0.25)
            self.assertLessEqual(remaining, 0.5)
            self.assertEqual(0.5, interval)
            self.assertIs(outer, signal.getsignal(signal.SIGALRM))
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, original)

    def test_failure_diagnostic_restores_later_external_deadline_without_delivering_it(self):
        module = _load_eval_generation()
        original = signal.getsignal(signal.SIGALRM)
        calls = []
        def outer(signum, frame):
            calls.append(signum)
        signal.signal(signal.SIGALRM, outer)
        signal.setitimer(signal.ITIMER_REAL, 0.5, 0.5)
        try:
            with patch.object(module, "DIAGNOSTIC_SECONDS", 0.03), patch(
                    "laplace_api.urlopen", side_effect=lambda *_args, **_kwargs: time.sleep(2)):
                report = module.collect_forward_diagnostics("http://laplace", [{"class":"forward","prompt":"dog"}])
            self.assertEqual([], calls)
            self.assertEqual("collector", report["deadline_owner"])
            remaining, interval = signal.getitimer(signal.ITIMER_REAL)
            self.assertGreater(remaining, 0.25)
            self.assertLess(remaining, 0.5)
            self.assertEqual(0.5, interval)
            self.assertIs(outer, signal.getsignal(signal.SIGALRM))
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, original)

    def test_failed_evaluation_retains_diagnostics_without_changing_exit_or_expected_answers(self):
        module = _load_eval_generation()
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", "/build/laplace/work")) as directory:
            root = Path(directory)
            probes = {"probes":[{"id":"held-out","class":"forward","prompt":"a query", "expected_answer_surface":"required answer"}]}
            (root/"probes.json").write_text(json.dumps(probes))
            (root/"baseline.json").write_text(json.dumps({"fingerprint":{"entities":1},"sources":["source"]}))
            row = {"id":"held-out","class":"forward","surface":"chat","prompt":"a query",
                   "nonempty":False,"leaks":[],"answer_reached":False,"session_present":True,"miss":True}
            diagnostics = {"status":"collected","executions":[{"terminal":{"completion":True}}]}
            with patch.object(sys, "argv", ["eval-generation.py","--api","http://laplace",
                    "--surfaces","chat","--probes",str(root/"probes.json"),"--baseline",str(root/"baseline.json"),
                    "--report",str(root/"report.json")]), \
                 patch.object(module, "substrate_fingerprint", return_value={"entities":1}), \
                 patch.object(module, "seeded_sources", return_value=["source"]), \
                 patch.object(module, "entity_type_names", return_value=set()), \
                 patch.object(module, "run_chat", return_value=row), \
                 patch.object(module, "collect_forward_diagnostics", return_value=diagnostics), \
                 redirect_stdout(StringIO()), redirect_stderr(StringIO()), self.assertRaises(SystemExit) as stopped:
                module.main()
            self.assertEqual(1, stopped.exception.code)
            report = json.loads((root/"report.json").read_text())
            self.assertFalse(report["ok"])
            self.assertFalse(report["verdicts"]["chat_output"]["all_nonempty"])
            self.assertEqual(diagnostics, report["forward_diagnostics"])
            self.assertEqual(probes, json.loads((root/"probes.json").read_text()))

    def test_client_posts_named_operation_with_timeout(self):
        captured = {}

        def fake_urlopen(request, timeout):
            captured["url"] = request.full_url
            captured["tenant"] = request.get_header("X-laplace-tenant")
            captured["body"] = json.loads(request.data)
            captured["timeout"] = timeout
            return _Response(json.dumps({
                "object": "op.result",
                "name": captured["body"]["name"],
                "rows": [{"ok": True}],
                "truncated_at": None,
            }).encode("utf-8"))

        with patch("laplace_api.urlopen", fake_urlopen):
            rows = op_rows(
                "http://127.0.0.1:8080",
                "generation.probe",
                {"p_prompt": "dog", "p_seeds": [7]},
                max_rows=4,
                timeout_seconds=300,
            )

        self.assertEqual([{"ok": True}], rows)
        self.assertEqual("http://127.0.0.1:8080/v1/op", captured["url"])
        self.assertEqual("ci-eval", captured["tenant"])
        self.assertEqual(310, captured["timeout"])
        self.assertEqual(
            {
                "name": "generation.probe",
                "args": {"p_prompt": "dog", "p_seeds": [7]},
                "max_rows": 4,
                "timeout_seconds": 300,
            },
            captured["body"],
        )

    def test_elector_comparator_preserves_all_six_keys(self):
        module = _load_eval_generation()
        key = module._elector_key
        base = {
            "specificity": 1,
            "rel_mass": 1,
            "peers": 1,
            "ord": 1,
            "denote_mu": 1,
            "synset_id": "b",
        }

        def changed(**values):
            return {**base, **values}

        self.assertLess(key(base), key(changed(specificity=None)))
        self.assertLess(key(changed(rel_mass=2)), key(base))
        self.assertLess(key(changed(peers=2)), key(base))
        self.assertLess(key(changed(ord=2)), key(base))
        self.assertLess(key(changed(denote_mu=2)), key(base))
        self.assertLess(key(changed(synset_id="a")), key(base))

    def test_prompt_election_scores_topic_token_and_reports_sense_separately(self):
        module = _load_eval_generation()
        row = {
            "tok": "topic-id",
            "synset_id": "sense-id",
            "specificity": 0.5,
            "rel_mass": 1,
            "peers": 1,
            "ord": 2,
            "denote_mu": 1,
        }

        surfaces = {"topic-id": "hot", "sense-id": "beautiful"}
        with patch.object(module, "op_rows", return_value=[row]), patch.object(
            module, "label", side_effect=lambda _api, entity_id: surfaces[entity_id]
        ):
            topic, sense, specificity, _latency = module.prompt_coherence_rank1(
                "http://laplace", "The opposite of hot is"
            )

        self.assertEqual("hot", topic)
        self.assertEqual("beautiful", sense)
        self.assertEqual(0.5, specificity)

    def test_baseline_is_a_source_floor_not_a_row_count_threshold(self):
        module = _load_eval_generation()
        baseline = {
            "sources": ["PredicateMatrixDecomposer", "WordNetDecomposer"],
            "fingerprint": {"laplace.entities(ESTIMATE)": 100},
        }

        self.assertIsNone(module.fingerprint_drift(
            baseline,
            {"entities(ESTIMATE)": 500},
            [
                "substrate/source/PredicateMatrixDecomposer/v1",
                "WordNetDecomposer",
                "OMWDecomposer",
                "UserPrompt",
            ],
        ))
        self.assertIn(
            "WordNetDecomposer",
            module.fingerprint_drift(
                baseline,
                {"entities(ESTIMATE)": 100},
                ["substrate/source/PredicateMatrixDecomposer/v1"],
            ),
        )
        self.assertIsNone(module.fingerprint_drift(
            baseline,
            {"entities(ESTIMATE)": 1},
            ["PredicateMatrixDecomposer", "WordNetDecomposer"],
        ))
        self.assertIn(
            "empty",
            module.fingerprint_drift(
                baseline,
                {},
                ["PredicateMatrixDecomposer", "WordNetDecomposer"],
            ),
        )

    def test_eval_scripts_have_no_private_database_lane(self):
        forbidden = ["subprocess", "ps" + "ql", "SET " + "search_path", '"--' + 'db"']
        for relative in ("scripts/eval-generation.py", "scripts/verify-generation.py"):
            text = (ROOT / relative).read_text(encoding="utf-8")
            for token in forbidden:
                self.assertNotIn(token, text, f"{relative} reintroduced {token!r}")

        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        self.assertNotIn("--" + "db", product)
        self.assertNotIn("verify-generation.py", product)
        self.assertNotIn("eval-generation.py", product)
        self.assertNotIn("--api http://127.0.0.1:8080", product)
        self.assertEqual(1, product.count("test-parallel.sh --perf"))

        perf = next(s for s in _load_test_profiles() if s["id"] == "generation-perf")
        perf_command = " ".join(perf["command"])
        self.assertIn("verify-generation.py", perf_command)
        self.assertIn("--api ${LAPLACE_API_BASE:-http://127.0.0.1:8080}", perf_command)
        self.assertNotIn("--" + "db", perf_command)

        probes = json.loads((ROOT / "scripts/eval-probes.json").read_text(encoding="utf-8"))
        self.assertNotIn("sql", {probe.get("surface") for probe in probes["probes"]})

    def test_language_score_reuses_native_operation(self):
        text = (ROOT / "scripts/verify-generation.py").read_text(encoding="utf-8")
        self.assertGreaterEqual(text.count('"converse.prompt_language"'), 2)
        self.assertNotIn("word_" + "language", text)

    def test_long_generation_benchmark_is_dispatch_only(self):
        workflow = _load_primary_workflow()
        dispatch = workflow["on"]["workflow_dispatch"]
        benchmark = dispatch["inputs"]["generation_benchmark"]
        self.assertEqual("boolean", benchmark["type"])
        self.assertEqual("false", benchmark["default"])

        workflow_text = (ROOT / ".github/workflows/laplace.yml").read_text(encoding="utf-8")
        self.assertIn(
            "github.event_name == 'workflow_dispatch' && inputs.generation_benchmark && '1' || ''",
            workflow_text,
        )
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        for requested in ("0", "1"):
            result = subprocess.run(
                ["bash", str(ROOT / "scripts/product-ci.sh"), "all", "--list-phases"],
                env={**os.environ, "LAPLACE_GENERATION_BENCHMARK": requested,
                     "LAPLACE_FRESH_DB": "0", "LAPLACE_RESTORE_FOUNDATION": "0"},
                capture_output=True, text=True, check=True, timeout=10,
            )
            self.assertEqual(requested == "1", "performance" in result.stdout.splitlines())
        self.assertEqual(1, product.count("test-parallel.sh --perf"))

        perf = next(s for s in _load_test_profiles() if s["id"] == "generation-perf")
        perf_command = " ".join(perf["command"])
        self.assertIn("verify-generation.py", perf_command)
        self.assertIn("--enforce", perf_command)

    def test_generation_probe_builds_each_lane_plan_once_per_seed_batch(self):
        sql_root = ROOT / "extension/laplace_substrate/sql/functions/converse"
        probe = (sql_root / "generation_probe.sql.in").read_text(encoding="utf-8")
        walk = (sql_root / "converse_walk.sql.in").read_text(encoding="utf-8")
        compose = (sql_root / "converse_compose.sql.in").read_text(encoding="utf-8")

        self.assertIn("generation.walk_batch(p_prompt, p_steps, p_seeds)", probe)
        self.assertIn(
            "generation.compose_batch(p_prompt, p_steps, p_lang, p_seeds)",
            probe,
        )
        self.assertNotIn("converse.walk(p_prompt, p_steps, s.seed)", probe)
        self.assertNotIn("converse.compose(p_prompt, p_steps, p_lang, s.seed)", probe)

        self.assertIn("CREATE OR REPLACE FUNCTION generation.walk_batch", walk)
        self.assertIn("FROM generation.walk_batch(", walk)
        self.assertIn("CREATE OR REPLACE FUNCTION generation.compose_batch", compose)
        self.assertIn("FROM generation.compose_batch(", compose)

        harness = (ROOT / "scripts/verify-generation.py").read_text(encoding="utf-8")
        self.assertIn('"p_seeds": [int(seed) for seed in seeds]', harness)
        self.assertNotIn('"p_seeds": [int(seed)]', harness)


if __name__ == "__main__":
    unittest.main()
