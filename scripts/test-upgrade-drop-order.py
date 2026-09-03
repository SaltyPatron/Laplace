#!/usr/bin/env python3
"""Regression tests for scripts/check-upgrade-drop-order.py.

These tests are source-only: they synthesize upgrade manifests in a temporary
folder and exercise the exact dependency-release model without requiring a live
PostgreSQL catalog.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
CHECKER = ROOT / "scripts" / "check-upgrade-drop-order.py"

spec = importlib.util.spec_from_file_location("upgrade_drop_order", CHECKER)
assert spec is not None and spec.loader is not None
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class UpgradeDropOrderTests(unittest.TestCase):
    def test_declaration_signature_strips_names_defaults_and_aliases(self) -> None:
        self.assertEqual(
            "generation.forward_text(text,integer,double precision,bytea[])",
            checker.canonical_signature(
                "generation.forward_text",
                "p_prompt text, p_steps int DEFAULT 24, "
                "p_spread float8 DEFAULT 0.7, p_frontier bytea[] DEFAULT NULL",
                declaration=True,
            ),
        )

    def test_no_earlier_event_is_unsafe(self) -> None:
        safe, detail = checker.dependency_released_before(
            "generation.dep(text)",
            "generation.base(text)",
            100,
            {},
            {},
        )
        self.assertFalse(safe)
        self.assertIn("no earlier", detail)

    def test_drop_dependent_before_base_is_safe(self) -> None:
        safe, _ = checker.dependency_released_before(
            "generation.dep(text)",
            "generation.base(text)",
            100,
            {"generation.dep(text)": [40]},
            {},
        )
        self.assertTrue(safe)

    def test_rebind_without_base_reference_releases_dependency(self) -> None:
        safe, detail = checker.dependency_released_before(
            "generation.dep(text)",
            "generation.base(text)",
            100,
            {},
            {"generation.dep(text)": [checker.Rebind(40, "BEGIN ATOMIC SELECT 1; END;")]},
        )
        self.assertTrue(safe)
        self.assertIn("rebound", detail)

    def test_rebind_that_still_calls_base_is_unsafe(self) -> None:
        safe, detail = checker.dependency_released_before(
            "generation.dep(text)",
            "generation.base(text)",
            100,
            {},
            {"generation.dep(text)": [
                checker.Rebind(40, "BEGIN ATOMIC SELECT generation.base('x'); END;")
            ]},
        )
        self.assertFalse(safe)
        self.assertIn("still references", detail)

    def test_last_event_wins_drop_then_rebind_to_base_is_unsafe(self) -> None:
        safe, _ = checker.dependency_released_before(
            "generation.dep(text)",
            "generation.base(text)",
            100,
            {"generation.dep(text)": [20]},
            {"generation.dep(text)": [
                checker.Rebind(60, "BEGIN ATOMIC SELECT base('x'); END;")
            ]},
        )
        self.assertFalse(safe)

    def test_string_literal_mention_does_not_create_dependency(self) -> None:
        self.assertFalse(
            checker.body_references_base(
                "BEGIN ATOMIC SELECT 'generation.base(''x'')'; END;",
                "generation.base(text)",
            )
        )

    def test_manifest_parser_preserves_order_and_exact_overload(self) -> None:
        sql = """
CREATE OR REPLACE FUNCTION generation.dep(
    p_prompt text,
    p_steps int DEFAULT 24)
RETURNS integer
LANGUAGE sql STABLE
BEGIN ATOMIC
SELECT length(p_prompt) + p_steps;
END;

DROP FUNCTION IF EXISTS generation.base(text);
DROP FUNCTION IF EXISTS generation.dep(text, integer);
"""
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            (root / "manifest.upgrade").write_text("synthetic.sql.in\n")
            (root / "synthetic.sql.in").write_text(sql)
            old_root = checker.SQL_ROOT
            checker.SQL_ROOT = root
            try:
                drops, rebinds, _ = checker.parse_upgrade_events()
            finally:
                checker.SQL_ROOT = old_root

        dep = "generation.dep(text,integer)"
        base = "generation.base(text)"
        self.assertIn(dep, rebinds)
        self.assertIn(base, drops)
        self.assertIn(dep, drops)
        self.assertLess(rebinds[dep][0].position, drops[base][0])
        self.assertGreater(drops[dep][0], drops[base][0])

        safe, _ = checker.dependency_released_before(
            dep, base, drops[base][0], drops, rebinds
        )
        self.assertTrue(safe)


if __name__ == "__main__":
    unittest.main(verbosity=2)
