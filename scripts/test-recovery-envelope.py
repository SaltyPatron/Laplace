#!/usr/bin/env python3
"""Source/parser contracts for exhaustive read-only legacy recovery diagnostics."""
from __future__ import annotations

import hashlib
import copy
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

    def lineage_envelope(self, conflicts=4):
        examples = []
        for index in range(min(conflicts, 10)):
            old, target = f"{index + 100:032x}", f"{index + 200:032x}"
            examples.append({"parent_id":f"{index:032x}", "content_physicality_id":old,
                "canonical_target_id":target, "semantic_equivalent":False,
                "carriers":[{"role":role, "physicality":{"id":identity}, "expanded_count":1,
                             "ordered_alias_occurrences":[{"ordinal":1,"entity_id":"name-root"}]}
                            for role,identity in (("old-content",old),("existing-projection",target))]})
        return {"native_inputs":{"prospective_snapshot_records":872007,
                                 "joined_entity_content_snapshot_rows":872007},
                "player_session_destinations":{
                    "canonical_target_equivalence_by_kind":[{
                        "parent_kind":"player", "occupied_target_pairs":198,
                        "semantic_equivalent_pairs":198-conflicts, "semantic_difference_pairs":conflicts}],
                    "player_projection_conflict_lineage":{
                        "schema":"laplace.player-projection-conflict-lineage/v1", "conflicting_pairs":conflicts,
                        "example_limit":10, "captured_pairs":len(examples), "examples_complete":conflicts<=10,
                        "examples":examples}}}

    def test_conflict_lineage_reconciles_complete_and_bounded_capture_separately(self):
        for conflicts in (0, 4, 10, 11, 198):
            with self.subTest(conflicts=conflicts):
                CLASSIFIER.validate_recovery_envelope(self.lineage_envelope(conflicts))
        envelope = self.lineage_envelope(11)
        envelope["player_session_destinations"]["player_projection_conflict_lineage"]["examples_complete"] = True
        with self.assertRaisesRegex(RuntimeError, "complete conflict set"):
            CLASSIFIER.validate_recovery_envelope(envelope)

    def test_conflict_lineage_rejects_missing_repeated_or_relabelled_rows(self):
        original = self.lineage_envelope()
        for corruption in ("count", "missing-target", "wrong-target", "missing-occurrence", "duplicate", "equivalent"):
            with self.subTest(corruption=corruption):
                envelope = copy.deepcopy(original)
                lineage = envelope["player_session_destinations"]["player_projection_conflict_lineage"]
                example = lineage["examples"][0]
                if corruption == "count": lineage["captured_pairs"] = 3
                elif corruption == "missing-target": example["carriers"].pop()
                elif corruption == "wrong-target": example["carriers"][1]["physicality"]["id"] = "different"
                elif corruption == "missing-occurrence": example["carriers"][1]["ordered_alias_occurrences"] = []
                elif corruption == "duplicate": lineage["examples"][1] = copy.deepcopy(example)
                elif corruption == "equivalent": example["semantic_equivalent"] = True
                with self.assertRaises(RuntimeError):
                    CLASSIFIER.validate_recovery_envelope(envelope)

    def test_lineage_selection_uses_complete_conflict_set_before_fixed_example_bound(self):
        sql = CLASSIFIER.projection_conflict_lineage_ctes()
        self.assertIn("FROM recovery_projection_difference_examples", sql)
        self.assertIn("WHERE parent_kind='player' AND example_number<=10", sql)
        output = CLASSIFIER.projection_conflict_lineage_json()
        self.assertIn("'conflicting_pairs',(SELECT count(*) FROM recovery_projection_difference_examples WHERE parent_kind='player')", output)
        self.assertIn("'captured_pairs',(SELECT count(*) FROM recovery_conflict_lineage)", output)
        self.assertIn("'examples_complete'", output)

    def test_lineage_preserves_full_rows_and_all_testimony_outcomes_for_both_aliases(self):
        sql = CLASSIFIER.projection_conflict_lineage_ctes()
        self.assertIn(snapshot_expression("carrier") + " AS snapshot", sql)
        self.assertIn(snapshot_expression("content") + " AS snapshot", sql)
        self.assertIn("('old-content',example.content_physicality_id)", sql)
        self.assertIn("('existing-projection',example.target_id)", sql)
        testimony = sql.split("'has_name_alias_testimony'", 1)[1]
        self.assertIn("jsonb_agg(to_jsonb(witness) ORDER BY witness.id)", testimony)
        self.assertIn("witness.type_id=laplace.relation_type_id('HAS_NAME_ALIAS')", testimony)
        self.assertIn("witness.object_id IN (SELECT occurrence.child_id FROM recovery_conflict_occurrences", testimony)
        for forbidden in ("witness.outcome", "witness.source_id=", "witness.observation_count", "LIMIT"):
            self.assertNotIn(forbidden, testimony)

    def test_lineage_reports_native_checks_and_direct_singleton_recipe_without_replacement_mean(self):
        sql = CLASSIFIER.projection_conflict_lineage_ctes()
        for fragment in ("public.laplace_trajectory_constituents(",
                         "public.laplace_trajectory_expanded_constituents(",
                         "public.laplace_hash128_merkle(0::smallint,proof.ids)",
                         "public.laplace_radius_origin(content.coord)",
                         "public.laplace_hilbert_encode(content.coord)",
                         "ST_AsEWKB(carrier.coord)=ST_AsEWKB(content.coord)",
                         "carrier.hilbert_index=content.hilbert_index",
                         "item.flags", "'entities'", "'content_rows'", "'logical_manifest_complete'",
                         "realize.reconstruct_content(child_id)", "'reconstruction_is_null'"):
            self.assertIn(fragment, sql)
        self.assertNotIn("karcher", sql.lower())
        self.assertNotIn("centroid", sql.lower())
        self.assertIn("TryDecomposeRoot(name)", CLASSIFIER.projection_conflict_lineage_json())

    def test_lineage_occurrences_remain_scoped_to_the_exact_old_target_pair(self):
        sql = CLASSIFIER.projection_conflict_lineage_ctes()
        self.assertEqual(2, sql.count("AND occurrence.content_physicality_id=carrier.content_physicality_id"))
        self.assertEqual(2, sql.count("AND occurrence.role=carrier.role"))
        self.assertIn("CASE WHEN carrier.packed_bounds_valid THEN carrier.trajectory ELSE NULL END", sql)
        self.assertIn("CASE WHEN packed.valid THEN content.trajectory ELSE NULL END", sql)

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
