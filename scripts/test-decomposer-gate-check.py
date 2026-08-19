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

        with patch.object(GATE, "load_gates", return_value=config), patch.object(
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


if __name__ == "__main__":
    unittest.main()
