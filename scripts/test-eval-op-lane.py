#!/usr/bin/env python3
"""Contract and architecture gate for the deployed eval operation lane."""

from __future__ import annotations

import importlib.util
import json
import sys
import unittest
from io import BytesIO
from pathlib import Path
from unittest.mock import patch

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


class _Response(BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *_args):
        self.close()


class EvalOperationLaneTests(unittest.TestCase):
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

    def test_baseline_is_a_required_floor_not_an_exact_live_snapshot(self):
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
        self.assertIn(
            "shrank",
            module.fingerprint_drift(
                baseline,
                {"entities(ESTIMATE)": 70},
                ["PredicateMatrixDecomposer", "WordNetDecomposer"],
            ),
        )

    def test_eval_scripts_have_no_private_database_lane(self):
        forbidden = ["subprocess", "ps" + "ql", "SET " + "search_path", '"--' + 'db"']
        for relative in ("scripts/eval-generation.py", "scripts/verify-generation.py"):
            text = (ROOT / relative).read_text(encoding="utf-8")
            for token in forbidden:
                self.assertNotIn(token, text, f"{relative} reintroduced {token!r}")

        workflow = (ROOT / ".github/workflows/laplace.yml").read_text(encoding="utf-8")
        eval_block = workflow[workflow.index("  eval:"):workflow.index("  restore-api:")]
        self.assertNotIn("--" + "db", eval_block)
        self.assertEqual(2, eval_block.count("--api http://127.0.0.1:8080"))

        probes = json.loads((ROOT / "scripts/eval-probes.json").read_text(encoding="utf-8"))
        self.assertNotIn("sql", {probe.get("surface") for probe in probes["probes"]})

    def test_language_score_reuses_native_operation(self):
        text = (ROOT / "scripts/verify-generation.py").read_text(encoding="utf-8")
        self.assertGreaterEqual(text.count('"converse.prompt_language"'), 2)
        self.assertNotIn("word_" + "language", text)

    def test_long_generation_benchmark_is_dispatch_only(self):
        workflow = (ROOT / ".github/workflows/laplace.yml").read_text(encoding="utf-8")
        input_block = workflow[
            workflow.index("      generation_benchmark:"):
            workflow.index("\n\n# NO WORKFLOW-LEVEL CONCURRENCY")
        ]
        self.assertIn("type: boolean", input_block)
        self.assertIn("default: false", input_block)

        eval_block = workflow[workflow.index("  eval:"):workflow.index("  restore-api:")]
        detector_block = eval_block[eval_block.index("      - name: Lane detectors") :]
        self.assertIn("github.event_name == 'workflow_dispatch'", detector_block)
        self.assertIn("inputs.generation_benchmark == true", detector_block)
        self.assertNotIn("if: ${{ !cancelled() }}", detector_block)

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
