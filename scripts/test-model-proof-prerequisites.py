#!/usr/bin/env python3
"""Controls for bounded read-only model proof prerequisite inspection."""
from __future__ import annotations
import importlib.util
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

OWNER = Path(__file__).with_name("check-model-proof-prerequisites.py")
spec = importlib.util.spec_from_file_location("model_preflight", OWNER)
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


def varint(value):
    out = bytearray()
    while value > 127:
        out.append((value & 127) | 128)
        value >>= 7
    out.append(value)
    return bytes(out)


def string(value):
    encoded = value.encode("utf-8")
    return varint(len(encoded)) + encoded


def element(name, children=None):
    if children is not None:
        return b"\x48" + string(name) + b"\x15" + varint(children << 1) + b"\0"
    return b"\x15\x0c\x25\x02\x18" + string(name) + b"\0"


def parquet(path, elements, rows=42):
    count = len(elements)
    list_head = bytes([(count << 4) | 12]) if count < 15 else b"\xfc" + varint(count)
    footer = b"\x15\x02\x19" + list_head + b"".join(elements) + b"\x16" + varint(rows << 1) + b"\0"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"PAR1" + footer + struct.pack("<I", len(footer)) + b"PAR1")


def model(path, value=1.0):
    path.mkdir()
    (path / "config.json").write_text('{"model_type":"llama"}')
    (path / "tokenizer.json").write_text('{"model":{"vocab":{"x":0}}}')
    header = json.dumps({"x": {"dtype": "F32", "shape": [1], "data_offsets": [0, 4]}}).encode()
    (path / "model.safetensors").write_bytes(struct.pack("<Q", len(header)) + header + struct.pack("<f", value))


class ModelPreflightControls(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def test_flat_footer_reports_exact_columns_and_rows(self):
        path = self.root / "flat.parquet"
        parquet(path, [element("schema", 2), element("content"), element("language")], 321)
        observed = owner.parquet_schema(path)
        self.assertEqual(observed["rows"], 321)
        self.assertEqual([f["name"] for f in observed["fields"]], ["content", "language"])
        self.assertTrue(all(f["physical_type"] == 6 for f in observed["fields"]))

    def test_nested_children_do_not_become_top_level_columns(self):
        path = self.root / "nested.parquet"
        parquet(path, [element("schema", 2), element("nested", 1),
                       element("content"), element("language")])
        observed = owner.parquet_schema(path)
        self.assertEqual([f["name"] for f in observed["fields"]], ["nested", "language"])

    def test_bad_footer_is_rejected(self):
        path = self.root / "bad.parquet"
        path.write_bytes(b"PAR1" + struct.pack("<I", owner.MAX_META + 1) + b"PAR1")
        with self.assertRaisesRegex(ValueError, "footer"):
            owner.parquet_schema(path)

    def test_stack_blob_inventory_cannot_claim_content(self):
        data = self.root / "Data"
        path = data / "stack-v2" / "Python" / "part.parquet"
        parquet(path, [element("schema", 2), element("blob_id"), element("language")])
        observed = owner.corpus_info(data, "stack-v2")
        schema = observed["representative_schemas"][0]
        self.assertTrue(schema["blob_reference_without_content"])
        self.assertFalse(schema["required_columns_present"])

    def test_empty_primary_preserves_actual_fallback_masking(self):
        data = self.root / "Data"
        (data / "tiny-codes").mkdir(parents=True)
        fallback = self.root / "models" / "tiny-codes"
        parquet(fallback / "part.parquet", [element("schema", 3), element("prompt"),
                                         element("response"), element("programming_language")])
        observed = owner.corpus_info(data, "tiny-codes")
        self.assertEqual(observed["selected"], str(data / "tiny-codes"))
        self.assertTrue(observed["empty_primary_masks_fallback"])
        self.assertEqual(observed["shards_observed"], 0)

    def test_complete_models_report_real_sample_difference_without_native_id_claim(self):
        left, right = self.root / "left", self.root / "right"
        model(left, 1.0)
        model(right, 2.0)
        a, b = owner.model_info(left), owner.model_info(right)
        self.assertTrue(a["structurally_complete"])
        self.assertTrue(b["structurally_complete"])
        self.assertNotEqual(owner.model_signature(a), owner.model_signature(b))
        self.assertFalse(a["canonical_source_id_computed"])
        self.assertFalse(a["full_weight_hashes_checked"])

    def test_file_rename_does_not_manufacture_distinct_content(self):
        path = self.root / "model"
        model(path)
        before = owner.model_info(path)
        (path / "model.safetensors").rename(path / "renamed.safetensors")
        self.assertEqual(owner.model_signature(before), owner.model_signature(owner.model_info(path)))

    def test_missing_indexed_shard_is_rejected(self):
        path = self.root / "model"
        model(path)
        (path / "model.safetensors.index.json").write_text(
            '{"weight_map":{"x":"missing.safetensors"}}')
        observed = owner.model_info(path)
        self.assertFalse(observed["structurally_complete"])
        self.assertIn("indexed weight shards missing", observed["error"])

    def test_actual_executable_help_success_and_failure(self):
        for rc in (0, 7):
            with self.subTest(rc=rc):
                path = self.root / ("llama-" + str(rc))
                path.write_text('#!/bin/sh\n[ "$1" = --help ] || exit 98\nexit ' + str(rc) + '\n')
                path.chmod(0o700)
                observed = owner.inspect_llama(path)
                self.assertEqual(observed["exit_code"], rc)
                self.assertEqual(observed["runnable"], rc == 0)

    def test_internal_deadline_is_not_swallowed_as_one_file_error(self):
        with patch.object(owner, "read_bounded", side_effect=owner.PreflightDeadline("deadline")):
            with self.assertRaises(owner.PreflightDeadline):
                owner.model_info(self.root)

if __name__ == "__main__":
    unittest.main(verbosity=2)
