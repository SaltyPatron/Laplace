#!/usr/bin/env python3
"""Source-only contracts for the live recursive substrate proof gate."""
from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


def load_proof_module():
    path = ROOT / "scripts/prove-live-recursive-substrate.py"
    spec = importlib.util.spec_from_file_location("live_recursive_proof", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class LiveRecursiveProofGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.proof = load_proof_module()

    def test_json_parser_accepts_one_multiline_document(self):
        payload = "{\n  \"schema\": \"proof\",\n  \"examples\": [\n" + \
            ",\n".join(f"    {{\"n\": {n}}}" for n in range(20)) + \
            "\n  ]\n}\n"
        value = self.proof.parse_single_json_document(payload)
        self.assertEqual("proof", value["schema"])
        self.assertEqual(20, len(value["examples"]))

    def test_json_parser_accepts_surrounding_whitespace_only(self):
        value = self.proof.parse_single_json_document('\n\t {"schema":"proof"} \r\n')
        self.assertEqual({"schema": "proof"}, value)

    def test_json_parser_rejects_empty_output(self):
        with self.assertRaisesRegex(RuntimeError, "returned no JSON"):
            self.proof.parse_single_json_document(" \n\t ")

    def test_json_parser_rejects_a_second_document(self):
        with self.assertRaisesRegex(RuntimeError, "trailing output"):
            self.proof.parse_single_json_document('{"one":1}\n{"two":2}\n')

    def test_json_parser_rejects_non_object_root(self):
        with self.assertRaisesRegex(RuntimeError, "root is not an object"):
            self.proof.parse_single_json_document('[{"schema":"proof"}]')

    def test_recursive_proof_edits_use_fast_live_verification_lane(self):
        workflow = (ROOT / ".github/workflows/laplace.yml").read_text(encoding="utf-8")
        self.assertIn("scripts/prove-*|scripts/test-live-recursive-proof-gate.py", workflow)
        self.assertIn("LAPLACE_VERIFY_RECURSIVE_PROOF", workflow)
        self.assertIn("Verify changed recursive proof against the live product", workflow)
        self.assertIn('python3 scripts/prove-live-recursive-substrate.py "${PGDATABASE:-laplace}"', workflow)

    def test_storage_gate_checks_identity_duplicates_and_parent_bounds(self):
        sql = self.proof.storage_sql(1e-12, 20)
        self.assertIn("GROUP BY id HAVING count(*) > 1", sql)
        self.assertIn("GROUP BY entity_id HAVING count(*) > 1", sql)
        self.assertIn("radius_origin <= 1.0 +", sql)
        self.assertIn("trajectory IS NULL AND n_constituents <> 0", sql)
        self.assertIn("trajectory IS NOT NULL AND n_constituents = 0", sql)

    def test_trajectory_gate_decodes_manifest_then_resolves_child_physicality(self):
        sql = self.proof.trajectory_sql(1e-12, 20)
        self.assertIn("laplace_trajectory_constituents(p.trajectory)", sql)
        self.assertIn("realize.vertex_tier(c.flags)", sql)
        self.assertIn("p.entity_id=c.child_id AND p.type=1", sql)
        self.assertIn("logical_constituents <> n_constituents", sql)
        self.assertIn("child_tier >= parent_tier", sql)
        self.assertIn("child_id=parent_id", sql)
        # The packed carrier is identity/order payload.  This query must not run
        # spatial component accessors over the packed trajectory itself.
        self.assertNotIn("ST_X(c.", sql)
        self.assertNotIn("ST_Y(c.", sql)
        self.assertNotIn("ST_Z(c.", sql)
        self.assertNotIn("ST_M(c.", sql)

    def test_identity_gate_expands_rle_and_recomputes_ordered_content_id(self):
        sql = self.proof.identity_sql(20)
        self.assertIn("laplace_trajectory_expanded_constituents", sql)
        self.assertIn("array_agg(child_id ORDER BY ordinal)", sql)
        self.assertIn("laplace_hash128_merkle(0::smallint,child_ids)", sql)
        self.assertIn("recomputed_id IS DISTINCT FROM parent_id", sql)

    def test_reconstruction_gate_uses_exact_reconstructor_and_independent_byte_fingerprint(self):
        sql = self.proof.reconstruction_sql(20, 32)
        self.assertIn("realize.reconstruct_content(c.id)", sql)
        self.assertIn("octet_length(body)", sql)
        self.assertIn("md5(body)", sql)
        self.assertIn("Sentence", sql)
        self.assertIn("Document", sql)

    def test_live_floor_invokes_proof_after_foundation_readback(self):
        text = (ROOT / "scripts/check-substrate-floor.sh").read_text(encoding="utf-8")
        foundation = text.index("ensure-foundation.sh\" --check-only")
        proof = text.index("prove-live-recursive-substrate.py")
        self.assertGreater(proof, foundation)
        self.assertIn("RECURSIVE_SUBSTRATE_PROOF_FAIL", text)

    def test_receipt_failure_coordinates_cover_all_hard_contracts(self):
        text = (ROOT / "scripts/prove-live-recursive-substrate.py").read_text(encoding="utf-8")
        for coordinate in (
            "duplicate_entity_ids",
            "duplicate_content_physicality_entities",
            "parent_contract_failures",
            "count_mismatch_parents",
            "self_edges",
            "non_descending_edges",
            "missing_child_entities",
            "missing_child_content_physicalities",
            "child_bound_failures",
            "identity_or_expansion_failures",
            "failed_roots",
            "acyclicity_certificate_missing",
        ):
            self.assertIn(coordinate, text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
