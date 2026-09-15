#!/usr/bin/env python3
"""Protocol/resource and native ABI checks for the read-only source diagnostic."""
import ctypes
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

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

    def test_exact_unicode_lookup_cannot_inject_sql(self):
        surface = "défine'); DELETE FROM laplace.entities; --"
        escaped = module.text_sql(surface)
        self.assertNotIn("DELETE", escaped)
        self.assertIn(surface.encode("utf-8").hex(), escaped)
        self.assertNotIn("lower(", self.sql())

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
