#!/usr/bin/env python3
"""Focused tests for decomposer consensus gate bounds."""

from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("decomposer-gate-check.py")
SPEC = importlib.util.spec_from_file_location("decomposer_gate_check", SCRIPT)
assert SPEC and SPEC.loader
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class SourceScopedGateBoundsTests(unittest.TestCase):
    def run_gate(self, source_count: int, global_count: int = 99) -> dict:
        config = {
            "sources": {
                "chess": {
                    "decomposer": "ChessPgn",
                    "layer": 20,
                    "skip_layer_complete": True,
                    "consensus_gates": [
                        {"relation": "MOVE", "max": 0, "scope": "source_evidence"}
                    ],
                }
            }
        }

        def fake_psql(_dbname: str, sql: str, **_kwargs: object) -> str:
            if "substrate_health" in sql:
                return "t"
            if "evidence_count" in sql:
                return str(source_count) if "p_type =>" in sql else "1"
            if "physicalities" in sql:
                return "t"
            if "consensus_count" in sql:
                return str(global_count)
            raise AssertionError(f"unexpected SQL: {sql}")

        with patch.object(GATE, "canonical_source", side_effect=str.lower), patch.object(GATE, "load_catalog", return_value={"sources": []}), patch.object(GATE, "load_gates", return_value=config), patch.object(
            GATE, "psql", side_effect=fake_psql
        ):
            return GATE.check_source(
                "chess", "laplace", host="localhost", user="laplace_admin",
                allow_health_tier=False,
            )

    def test_zero_source_evidence_passes_even_when_other_sources_emit_relation(self) -> None:
        report = self.run_gate(0)
        move = next(c for c in report["checks"] if c["check"] == "source_evidence:MOVE")
        self.assertTrue(report["passed"])
        self.assertTrue(move["passed"])
        self.assertEqual(0, move["max"])
        self.assertEqual(0, move["source_evidence"])

    def test_reintroduced_move_evidence_from_source_fails_normalized_gate(self) -> None:
        report = self.run_gate(1)
        move = next(c for c in report["checks"] if c["check"] == "source_evidence:MOVE")
        self.assertFalse(report["passed"])
        self.assertFalse(move["passed"])
        self.assertIn("max 0", move["detail"])


class DocumentCompositionGateTests(unittest.TestCase):
    def test_optional_attestations_pass_at_zero_and_do_not_pin_a_tier(self) -> None:
        config = {
            "sources": {
                "document": {
                    "decomposer": "DocumentDecomposer",
                    "layer": 2,
                    "attestations": "optional",
                }
            }
        }
        seen: list[str] = []

        def fake_psql(_dbname: str, sql: str, **_kwargs: object) -> str:
            seen.append(sql)
            if "substrate_health" in sql:
                return "t"
            if "evidence_count" in sql:
                return "0"
            if "layer_completed" in sql:
                return "t"
            raise AssertionError(f"unexpected SQL: {sql}")

        with patch.object(GATE, "canonical_source", side_effect=str.lower), patch.object(GATE, "load_catalog", return_value={"sources": []}), patch.object(GATE, "load_gates", return_value=config), patch.object(
            GATE, "psql", side_effect=fake_psql
        ):
            report = GATE.check_source(
                "document", "laplace-mono", host="/tmp", user="laplace",
                allow_health_tier=False,
            )

        self.assertTrue(report["passed"])
        attestations = next(c for c in report["checks"] if c["check"] == "attestations")
        self.assertTrue(attestations["passed"])
        self.assertEqual(0, attestations["count"])
        self.assertNotIn("tier = 4", "\n".join(seen))
        self.assertNotIn("CONTAINS", "\n".join(seen))


class NativeSourceProofTests(unittest.TestCase):
    def run_gate(self, **changes: object) -> dict:
        proof = {"run_id": "run", "source_id": "actual-trunk", "layer": 1,
                 "source_complete": True, "files_total": 2, "files_received": 2,
                 "files_complete": 2, "ids_requested": 3, "ids_present": 3, "series": 4}
        proof.update(changes)
        config = {"sources": {"iso-639": {"decomposer": "ISO639", "layer": 1,
                   "witness": ["retired-authority", "retired-release"]}}}
        catalog = {"sources": [{"name": "iso-639", "aliases": ["iso639"]}]}
        import json
        with patch.object(GATE, "load_catalog", return_value=catalog), \
             patch.object(GATE, "load_gates", return_value=config), \
             patch.object(GATE, "canonical_source", return_value="iso-639"), \
             patch.object(GATE, "psql", return_value=json.dumps(proof)) as query:
            report = GATE.check_source("iso639", "laplace-mono", host="/tmp", user="laplace",
                                       allow_health_tier=False)
        self.assertEqual(1, query.call_count)
        sql = query.call_args.args[1]
        self.assertIn("'iso-639'", sql)
        self.assertIn("public.laplace_contained_series", sql)
        self.assertNotIn("retired-authority", sql)
        return report

    def test_complete_native_output_uses_actual_published_trunk(self) -> None:
        self.assertTrue(self.run_gate()["passed"])

    def test_partial_or_missing_native_output_fails(self) -> None:
        for changes in ({"source_complete": False}, {"files_received": 1},
                        {"files_complete": 1}, {"ids_present": 2}, {"series": 0}, {"layer": 2}):
            with self.subTest(changes=changes):
                self.assertFalse(self.run_gate(**changes)["passed"])


if __name__ == "__main__":
    unittest.main()
