#!/usr/bin/env python3
"""Source/parser contracts for exhaustive read-only legacy recovery diagnostics."""
from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from lib.legacy_content_snapshot import snapshot_expression  # noqa: E402

SPEC = importlib.util.spec_from_file_location("recovery_classifier", ROOT / "scripts/classify-legacy-content.py")
assert SPEC and SPEC.loader
CLASSIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CLASSIFIER)

try:
    from pglast.parser import parse_sql_json
except ImportError:
    parse_sql_json = None


class RecoveryEnvelopeTests(unittest.TestCase):
    def test_snapshot_expression_matches_published_ea1905_exactly(self):
        # Independently extracted and executed the published function AST at
        # ea1905; this digest includes every output byte of snapshot_expression('p').
        self.assertEqual("f724ff6308e7e0a8ae1f4c56833c7642f5d2a3e84595f11935febf414159b23f",
                         hashlib.sha256(snapshot_expression("p").encode()).hexdigest())

    def test_snapshot_keeps_exact_geometry_float_and_timestamp_binary(self):
        expression = snapshot_expression("source")
        for fragment in ("ST_AsEWKB(source.coord)", "ST_AsEWKB(source.trajectory)",
                         "encode(source.hilbert_index,'hex')", "float8send(source.radius_origin)",
                         "float8send(source.alignment_residual)", "source.n_constituents",
                         "source.source_dim", "timestamptz_send(source.observed_at)"):
            self.assertIn(fragment, expression)

    def test_snapshot_alias_cannot_insert_sql(self):
        for alias in ("source;DELETE", "source.coord", "source's", "source  ", ""):
            with self.subTest(alias=alias), self.assertRaises(ValueError):
                snapshot_expression(alias)

    def test_native_input_scope_has_complete_entity_content_join_without_caps(self):
        sql = CLASSIFIER.recovery_envelope_ctes(10)
        input_query = sql.split("recovery_native_snapshot_line_sizes AS MATERIALIZED (", 1)[1].split(
            "recovery_native_snapshot_sizes AS MATERIALIZED (", 1)[0]
        self.assertIn("FROM recovery_needed_ids needed JOIN laplace.entities entity", input_query)
        self.assertIn("content.entity_id=needed.child_id AND content.type=1", input_query)
        self.assertIn("'kind','native-input','entity_id',encode(needed.child_id,'hex')", input_query)
        self.assertIn("'entity',to_jsonb(entity),'content'," + snapshot_expression("content"), input_query)
        self.assertNotIn("LIMIT", input_query)
        self.assertNotIn("valid_content", input_query)
        self.assertNotIn("MAX_", input_query)

    def test_record_length_measures_utf8_jsonb_output_plus_one_lf_only_in_total(self):
        sql = CLASSIFIER.recovery_envelope_ctes(10)
        self.assertIn("octet_length(convert_to(jsonb_build_object(", sql)
        self.assertIn(")::text,'UTF8'))::bigint AS line_bytes", sql)
        self.assertIn("sum(line_bytes::numeric + 1)", sql)
        self.assertIn("max(line_bytes),0) AS maximum_record_utf8_bytes", sql)
        self.assertIn("max(line_bytes + 1),0) AS maximum_jsonl_line_utf8_bytes", sql)
        self.assertNotIn("length(to_jsonb", sql)

    def test_projection_proposal_only_replaces_id_and_type(self):
        sql = CLASSIFIER.recovery_envelope_ctes(10)
        self.assertIn(snapshot_expression("source") + " || jsonb_build_object(", sql)
        self.assertIn("'type',3) AS proposed", sql)
        self.assertIn(snapshot_expression("target") + " AS existing", sql)
        self.assertIn("proposed - ARRAY['observed_at','observed_at_binary'] =", sql)
        self.assertIn("existing - ARRAY['observed_at','observed_at_binary'] AS semantic_equivalent", sql)
        self.assertIn("proposed->'observed_at_binary' = existing->'observed_at_binary'", sql)

    def test_player_and_session_counts_are_unbounded_while_examples_are_bounded(self):
        first = CLASSIFIER.recovery_envelope_ctes(1)
        last = CLASSIFIER.recovery_envelope_ctes(100)
        self.assertEqual(first.replace("example.example_number<=1)", "example.example_number<=100)"), last)
        self.assertIn("VALUES ('player'),('session')", first)
        self.assertIn("WHERE target_occupied AND NOT semantic_equivalent", first)
        example = first.split("'parent_id',encode(example.parent_id,'hex')", 1)[1]
        self.assertIn("'different_semantic_fields'", example)
        self.assertNotIn("'proposed',", example)
        self.assertNotIn("'existing',", example)
        self.assertNotIn("'trajectory_ewkb',", example)
        self.assertNotIn("'coord_ewkb',", example)

    def test_complete_row_and_target_count_reconciliation(self):
        envelope = {"native_inputs": {"prospective_snapshot_records": 872007,
                                      "joined_entity_content_snapshot_rows": 872007},
                    "player_session_destinations": {"canonical_target_equivalence_by_kind": [
                        {"occupied_target_pairs": 198, "semantic_equivalent_pairs": 197,
                         "semantic_difference_pairs": 1}]}}
        CLASSIFIER.validate_recovery_envelope(envelope)
        envelope["native_inputs"]["prospective_snapshot_records"] = 25000
        with self.assertRaisesRegex(RuntimeError, "all joined dependency rows"):
            CLASSIFIER.validate_recovery_envelope(envelope)
        envelope["native_inputs"]["prospective_snapshot_records"] = 872007
        envelope["player_session_destinations"]["canonical_target_equivalence_by_kind"][0]["semantic_difference_pairs"] = 0
        with self.assertRaisesRegex(RuntimeError, "every occupied target pair"):
            CLASSIFIER.validate_recovery_envelope(envelope)

    @unittest.skipIf(parse_sql_json is None, "optional PostgreSQL parser is not installed")
    def test_postgresql_parser_accepts_only_select_query_without_mutating_statements(self):
        tree = json.loads(parse_sql_json(CLASSIFIER.classification_sql(10)))
        self.assertEqual(1, len(tree["stmts"]))
        self.assertIn("SelectStmt", tree["stmts"][0]["stmt"])
        encoded = json.dumps(tree)
        for kind in ("InsertStmt", "UpdateStmt", "DeleteStmt", "MergeStmt", "CreateStmt",
                     "CreateTableAsStmt", "LockStmt", "intoClause"):
            self.assertNotIn('"' + kind + '"', encoded)


if __name__ == "__main__":
    unittest.main(verbosity=2)
