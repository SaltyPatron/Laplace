#!/usr/bin/env python3
"""Protocol/resource and native ABI checks for the read-only source diagnostic."""
import ctypes
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("read_operational_source", ROOT / "scripts/read-operational-source.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class SourceReadbackTests(unittest.TestCase):
    def sql(self):
        return module.readback_sql(["define", "Define"], ["justice"], 16, 2048, 512, 180)

    def test_read_only_full_structure_and_bounded_indexed_nomination(self):
        sql = self.sql()
        self.assertIn("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY", sql)
        self.assertTrue(sql.rstrip().endswith("ROLLBACK;"))
        self.assertIn("SET LOCAL client_encoding='UTF8'", sql)
        self.assertIn("WHERE p.type=8 AND p.trajectory IS NOT NULL", sql)
        self.assertIn("@> ARRAY[realize.canonical_id('ud/parse/schema/v1')]", sql)
        self.assertIn("&& ARRAY(SELECT id FROM cue_ids)", sql)
        self.assertIn("ORDER BY p.n_constituents,p.entity_id,p.id LIMIT 17", sql)
        self.assertIn("p.n_constituents BETWEEN 1 AND 2048", sql)
        self.assertIn("public.st_npoints(p.trajectory) BETWEEN 1 AND 2048", sql)
        self.assertIn("z.logical_count=p.n_constituents AND z.logical_count BETWEEN 1 AND 2048", sql)
        self.assertIn("public.laplace_trajectory_expanded_constituents", sql)
        self.assertIn("array_agg(u.entity_id ORDER BY u.ordinal)", sql)
        self.assertEqual(sql.count("realize.render_text_batch("), 1)
        self.assertNotIn("WHERE a.outcome", sql)
        self.assertIn("'has_parse_witness_overflow'", sql)
        self.assertIn("'opponent_rating_fp1e9'", sql)
        self.assertIn("'context_id',encode(a.context_id,'hex')", sql)

    def test_zero_candidate_diagnostics_distinguish_schema_source_and_cue(self):
        sql = self.sql()
        self.assertIn("'ud_schema_type8_present',EXISTS", sql)
        self.assertIn("a.source_id=(SELECT ud_source FROM roster)\n LIMIT 5", sql)
        self.assertIn("'ud_has_parse_sample_more'", sql)
        self.assertIn("'cue_type8_sample_more'", sql)
        self.assertIn("'source_parse_samples'", sql)
        self.assertIn("'cue_structure_samples'", sql)
        self.assertIn("'trajectory_ewkb_hex',encode(public.st_asewkb(p.trajectory),'hex')", sql)
        self.assertIn("'canonical_type8_present',EXISTS", sql)

    def test_exact_unicode_lookup_cannot_inject_sql(self):
        surface = "défine'); DELETE FROM laplace.entities; --"
        escaped = module.text_sql(surface)
        self.assertNotIn("DELETE", escaped)
        self.assertIn(surface.encode("utf-8").hex(), escaped)
        self.assertNotIn("lower(", self.sql())

    def test_requested_relations_are_native_ids_with_bounded_unfiltered_frontiers(self):
        sql = module.readback_sql(["opposite"], ["hot", "empty", "cold"], 16, 2048, 7, 180,
                                  ["IS_ANTONYM_OF", "HAS_NAME", "IS_EXAMPLE_OF", "CALLS", "HAS_INPUT"])
        self.assertIn("SELECT DISTINCT laplace.relation_type_id(name) AS id", sql)
        # No Python-owned dispatch relation or winner: every requested identity
        # uses the same two indexed, bounded frontiers and keeps all testimony.
        self.assertNotIn("IS_ANTONYM_OF", sql)
        self.assertNotIn("WHERE a.outcome", sql)
        self.assertNotIn("v_consensus_unrefuted", sql)
        for cte in ("relation_witnesses", "relation_followup_witnesses"):
            query = sql.split(cte + " AS MATERIALIZED (", 1)[1].split("\n),", 1)[0]
            self.assertIn("a.type_id=r.id", query)
            self.assertIn("ORDER BY a.subject_id,a.id LIMIT 8", query)
            self.assertNotIn("a.source_id=", query)
            self.assertNotIn("a.context_id=", query)
        self.assertIn("UNION SELECT entity_id FROM selected UNION SELECT subject_id FROM parse_witnesses", sql)
        self.assertIn("EXCEPT SELECT id FROM relation_subjects", sql)
        self.assertIn("AS followup_witness_overflow", sql)
        self.assertIn("AS inverse_witness_overflow", sql)
        self.assertIn("AS followup_inverse_witness_overflow", sql)
        for cte in ("relation_inverse_witnesses", "relation_followup_inverse_witnesses"):
            query = sql.split(cte + " AS MATERIALIZED (", 1)[1].split("\n),", 1)[0]
            self.assertIn("a.type_id=r.id AND a.object_id=ANY", query)
            self.assertIn("ORDER BY a.object_id,a.subject_id,a.id LIMIT 8", query)
            self.assertNotIn("a.source_id=", query)
            self.assertNotIn("a.context_id=", query)
        self.assertIn("UNION SELECT subject_id FROM relation_inverse_witnesses", sql)
        self.assertIn("'requested_relation_inverse',a.* FROM relation_inverse_witnesses", sql)
        self.assertIn("'requested_relation_followup_inverse',a.* FROM relation_followup_inverse_witnesses", sql)
        self.assertIn("'lookup_has_sense_witness_overflow'", sql)
        self.assertIn("'lookup_is_sense_of_witness_overflow'", sql)
        for direction in ("is_sense_of", "has_sense"):
            self.assertIn("'target_inverse_" + direction + "_witness_overflow'", sql)
        self.assertIn("a.object_id=ANY(ARRAY(SELECT id FROM relation_targets))", sql)
        self.assertIn("a.object_id=ANY(ARRAY(SELECT subject_id FROM target_sense_witnesses))", sql)
        self.assertEqual(sql.count("realize.render_text_batch("), 1)
        self.assertEqual(sql.count("realize.label_batch("), 1)
        self.assertEqual(sql.count("realize.batch("), 1)
        self.assertIn("left(r.surfaces[u.ord],1024)", sql)
        self.assertIn("left(r.labels[u.ord],1024) AS label", sql)
        self.assertIn("AS label_truncated", sql)
        self.assertIn("left(r.realized_texts[u.ord],1024) AS realized_text", sql)
        self.assertIn("AS native_realized_text_bytes", sql)
        self.assertIn("AS realized_text_truncated", sql)
        self.assertIn("AS surface_truncated", sql)
        self.assertIn("'type_canonical_name',n.name", sql)
        self.assertIn("UNION SELECT type_id FROM evidence", sql)

    def test_relation_lookup_is_optional_and_escaped_like_surfaces(self):
        self.assertNotIn("relation_requests(name)", self.sql())
        self.assertNotIn("requested_relation_followup", self.sql())
        self.assertNotIn("left(r.surfaces[u.ord]", self.sql())
        self.assertNotIn("realize.label_batch(", self.sql())
        self.assertNotIn("realize.batch(", self.sql())
        name = "relation/Δ'); DELETE FROM laplace.entities; --"
        sql = module.readback_sql(["define"], ["justice"], 1, 8, 2, 3, [name])
        self.assertNotIn(name, sql)
        self.assertNotIn("DELETE", sql)
        self.assertIn(name.encode("utf-8").hex(), sql)
        self.assertIn("'has_definition_witness_overflow'", sql)

    def test_cli_retains_positive_opposed_scoped_rows_and_overflow_without_election(self):
        rows = [dict(route="requested_relation", id=str(i), subject_id="hot-id", type_id="antonym-id",
                     object_id="target-id", source_id="source-" + str(i), context_id=context,
                     outcome=outcome, observation_count=3, sum_score_fp1e9=score)
                for i, (context, outcome, score) in enumerate(((None, 2, 3000000000), ("context-id", 0, -3000000000)))]
        # Incoming naming testimony retains its original direction and scope;
        # the diagnostic must not reverse it into a fabricated outgoing fact.
        rows.append(dict(route="requested_relation_inverse", id="incoming-witness",
                         subject_id="alternate-word-id", type_id="lemma-relation-id", object_id="hot-id",
                         source_id="different-source", context_id="naming-context", outcome=0,
                         observation_count=2, sum_score_fp1e9=-2000000000))
        observed = dict(transaction_read_only="on", parse_candidates=[], witnesses=rows,
                        limits={"requested_relations": [dict(relation_id="antonym-id", witness_overflow=True,
                                                            inverse_witness_overflow=True,
                                                            followup_witness_overflow=False,
                                                            followup_inverse_witness_overflow=False)]},
                        dictionary=[dict(id="target-id", surface="cold", surface_truncated=False,
                                         entity_rows=[dict(type_id="type-id", type_canonical_name="ConceptAnchor")])])
        with tempfile.TemporaryDirectory() as directory:
            receipt = Path(directory) / "readback.json"
            argv = ["read-operational-source.py", "fixture", "--cue", "opposite", "--operand", "hot",
                    "--relation", "IS_ANTONYM_OF", "--relation", "HAS_NAME", "--receipt", str(receipt)]
            with patch.object(sys, "argv", argv), patch.object(module, "bounded_query", return_value=json.dumps(observed).encode()) as query, \
                    patch.object(module, "decode_candidates"), patch("sys.stdout", new_callable=io.StringIO):
                self.assertEqual(module.main(), 0)
            retained = json.loads(receipt.read_text())
            self.assertEqual(retained["witnesses"], rows)
            self.assertEqual(retained["limits"], observed["limits"])
            self.assertEqual(retained["dictionary"], observed["dictionary"])
            self.assertEqual(retained["disposition"], "readback-only-no-declaration")
            query.assert_called_once_with("fixture", receipt.with_suffix(".sql"), 180, 16 << 20)
            self.assertIn("relation_requests(name)", receipt.with_suffix(".sql").read_text())

    def test_cli_rejects_relation_input_over_envelope_before_query(self):
        for names in (["HAS_NAME"] * 17, [""], ["Δ" * 513]):
            with self.subTest(names=names), patch.object(sys, "argv",
                    ["read-operational-source.py", "fixture", "--receipt", "unused.json"]
                    + [part for name in names for part in ("--relation", name)]), \
                    patch.object(module, "bounded_query") as query, patch("sys.stderr", new_callable=io.StringIO):
                with self.assertRaises(SystemExit) as error:
                    module.main()
                self.assertEqual(error.exception.code, 2)
                query.assert_not_called()

    def test_native_library_hash_is_bounded_without_python311_file_digest(self):
        payload = b"native artifact\x00" * 10000
        requests = []
        class Stream(io.BytesIO):
            def read(self, size=-1):
                requests.append(size)
                return super().read(size)
        path = Mock()
        path.open.return_value = Stream(payload)
        # Older runner Python has no usable file_digest. The replacement must
        # consume the complete artifact through bounded reads on those runners.
        with patch.object(module.hashlib, "file_digest", None, create=True):
            result = module.file_sha256(path)
        self.assertEqual(result, module.hashlib.sha256(payload).hexdigest())
        self.assertGreater(len(requests), 2)
        self.assertTrue(all(0 < n <= 64 << 10 for n in requests))
        path.open.assert_called_once_with("rb")

    def test_native_binding_layout_matches_checked_in_c_header(self):
        # The helper marshals existing native structures; it has no Python UD
        # parser. Compile a size/offset probe against the actual defining header.
        source = '''#include <stdio.h>
#include <stddef.h>
#include "laplace/core/ud_parse.h"
int main(void) {
 printf("%zu %zu %zu %zu %zu %zu %zu %zu\\n", sizeof(hash128_t),
 sizeof(laplace_ud_pairs_t),sizeof(laplace_ud_token_t),sizeof(laplace_ud_mwt_t),
 sizeof(laplace_ud_parse_t),offsetof(laplace_ud_token_t,features),
 offsetof(laplace_ud_token_t,head_ref_id),offsetof(laplace_ud_parse_t,tokens));
 return 0;
}'''
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "abi.c").write_text(source)
            subprocess.run(["cc", "-I", str(ROOT / "engine/core/include"), str(path / "abi.c"),
                            "-o", str(path / "abi")], check=True, capture_output=True)
            measured = list(map(int, subprocess.check_output([str(path / "abi")], text=True).split()))
        self.assertEqual(measured, [ctypes.sizeof(c) for c in
                         (module.Id, module.Pairs, module.Token, module.Mwt, module.Parse)]
                         + [module.Token.features.offset, module.Token.head_ref_id.offset, module.Parse.tokens.offset])

    def fake_query(self, code, max_bytes=1024, timeout=1):
        real_popen = subprocess.Popen
        def launch(*args, **kwargs):
            return real_popen([sys.executable, "-c", code], **kwargs)
        with patch.object(module.subprocess, "Popen", side_effect=launch):
            return module.bounded_query("unused", Path("unused.sql"), timeout, max_bytes)

    def test_bounded_output_rejects_overflow(self):
        with self.assertRaisesRegex(RuntimeError, "output exceeded declared byte envelope"):
            self.fake_query("import sys;sys.stdout.write('x'*4096)")

    def test_nonzero_psql_exit_does_not_look_like_valid_readback(self):
        with self.assertRaisesRegex(RuntimeError, "read-only source query failed"):
            self.fake_query("import sys;print('{}');sys.exit(2)")

    def test_exact_utf8_output_retained(self):
        output = self.fake_query("import sys;sys.stdout.buffer.write('ñébulo'.encode('utf-8'))")
        self.assertEqual(output, "ñébulo".encode("utf-8"))


if __name__ == "__main__":
    unittest.main()
