#!/usr/bin/env python3
"""Acceptance/rejection tests for the default task proof receipt, not cognition."""
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("operational_task", ROOT / "scripts/verify-operational-task.py")
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


def native_id(number):
    return "\\x" + f"{number:032x}"


def receipt():
    roster = {name: native_id(i + 1) for i, name in enumerate((
        "operational", "wordnet", "example_of", "calls", "has_input", "has_parse",
        "contains", "has_sense", "sense_of", "definition", "synset_type", "operand"))}
    task = {"id": native_id(30), "exemplar": native_id(31), "predicate": roster["definition"],
            "ids": [native_id(40), native_id(31), roster["definition"], native_id(32),
                    native_id(33), roster["synset_type"], native_id(41)]}
    slot = {"id": native_id(32), "token_ref": native_id(33), "accepted_type": roster["synset_type"]}
    files = [{"kind": kind, "file_id": native_id(i), "current_file_id": native_id(i),
              "historical_file_ids": None, "status": "ok", "disposition": "admitted",
              "completed": True, "ended_at": "2026-09-15T12:00:00Z", "error": None,
              "bytes": 123, "expected_bytes": 123, "fingerprint": native_id(i+100),
              "resume_fingerprint": native_id(i+100)} for kind, i in (("shape", 50), ("exemplar", 51))]
    witnesses = []

    def witness(route, subject, predicate, obj, source, context):
        witnesses.append({"route": route, "id": native_id(100+len(witnesses)),
            "subject_id": subject, "type_id": roster[predicate], "object_id": obj,
            "source_id": roster[source], "context_id": context, "outcome": 2,
            "observation_count": 1, "positive_standing": True})

    witness("declaration", task["exemplar"], "example_of", task["id"], "operational", native_id(50))
    witness("declaration", task["id"], "calls", task["predicate"], "operational", native_id(50))
    witness("declaration", task["id"], "has_input", slot["id"], "operational", native_id(50))
    witness("parse", native_id(60), "has_parse", task["exemplar"], "operational", native_id(61))
    witness("containment", native_id(51), "contains", native_id(61), "operational", native_id(51))
    witness("sense", roster["operand"], "has_sense", native_id(62), "wordnet", None)
    witness("synset", native_id(62), "sense_of", native_id(63), "wordnet", None)
    witness("definition", native_id(63), "definition", native_id(64), "wordnet", None)
    common = {"root_id": native_id(70), "program_id": native_id(71)}
    terminal = {**common, "event": "complete", "completion": True, "disposition": "complete",
        "remaining_required": 0, "required_obligations": 2, "satisfied_obligations": 2,
        "output_fingerprint": native_id(72), "semantic_act_id": native_id(73), "output_count": 1}
    emit = {**common, "event": "emit", "entity": native_id(64), "declared_result": True,
            "support_relation": roster["definition"], "support_anchor": native_id(63),
            "support_outbound": True, "support_witnesses": 1}
    structures = [{"parse_id": proof.id_hex(task["id"]), "canonical_identity": True,
                   "constituent_ids": [proof.id_hex(x) for x in task["ids"]]},
                  {"parse_id": proof.id_hex(task["exemplar"]), "canonical_identity": True,
                   "complete_native_decode": True, "ud": {"tokens": [{"ref_id": proof.id_hex(slot["token_ref"])}]}}]
    return {"transaction_read_only": "on", "current_root_has_invocation": False,
            "current_root_has_parse": False, "witness_counts": {"declaration": 3, "parse": 1,
             "containment": 1, "sense": 1, "synset": 1, "definition": 1},
            "roster": roster, "task": task, "slot": slot, "files": files,
            "seed_run": {"source_name": "OperationalDecomposer", "source_id": roster["operational"],
                "layer": 2, "status": "ok", "evidence_persisted": True, "files_done": 14,
                "files_total": 14, "units_failed": 0, "ended_at": "2026-09-15", "error": None},
            "witnesses": witnesses, "structures": structures, "execution": [emit, terminal],
            "synset_entities": [{"id": native_id(63), "type_id": roster["synset_type"]}],
            "realizations": {proof.id_hex(native_id(64)): "source supplied Unicode gloss: glaciér"}}


def chat(session="fresh", content="source supplied Unicode gloss: glaciér"):
    return {"status": 200, "session_header": session, "response": {
        "object": "chat.completion", "model": proof.MODEL,
        "metadata": {"session": session, "reply_rows": 1},
        "choices": [{"message": {"role": "assistant", "content": content}, "finish_reason": "stop"}]}}


class OperationalTaskTests(unittest.TestCase):
    def test_complete_native_witness_chain_uses_current_result_text(self):
        report = receipt()
        self.assertEqual(proof.validate_native(report), "source supplied Unicode gloss: glaciér")
        report["realizations"][proof.id_hex(native_id(64))] = "changed admitted gloss"
        self.assertEqual(proof.validate_native(report), "changed admitted gloss")

    def test_confirm_plus_native_refute_is_rejected(self):
        for route in ("declaration", "parse", "containment", "sense", "synset", "definition"):
            report = receipt()
            opposed = copy.deepcopy(next(w for w in report["witnesses"] if w["route"] == route))
            opposed["outcome"] = 0  # Native attestation_engine.h REFUTE, not an invented status.
            report["witnesses"].append(opposed)
            with self.subTest(route=route), self.assertRaises(ValueError):
                proof.validate_native(report)

    def test_declared_witness_scope_and_occurrence_file_chain_are_required(self):
        for route, key in (("declaration", "context_id"), ("parse", "context_id"),
                           ("containment", "subject_id"), ("definition", "source_id")):
            report = receipt()
            next(w for w in report["witnesses"] if w["route"] == route)[key] = native_id(999)
            with self.subTest(route=route), self.assertRaises(ValueError):
                proof.validate_native(report)

    def test_missing_semantic_hop_and_wrong_typed_input_are_rejected(self):
        report = receipt()
        report["witnesses"] = [w for w in report["witnesses"] if w["route"] != "synset"]
        with self.assertRaises(ValueError):
            proof.validate_native(report)
        report = receipt()
        report["synset_entities"][0]["type_id"] = native_id(999)
        with self.assertRaises(ValueError):
            proof.validate_native(report)

    def test_declared_result_and_terminal_identity_are_not_optional(self):
        for target, field, value in ((0, "declared_result", False), (0, "entity", native_id(999)),
            (0, "support_anchor", native_id(999)), (0, "support_outbound", False),
            (1, "program_id", native_id(999)), (1, "output_fingerprint", None),
            (1, "completion", False), (1, "remaining_required", 1), (1, "disposition", "ambiguous"),
            (1, "required_obligations", 0), (1, "output_count", 0)):
            report = receipt()
            report["execution"][target][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                proof.validate_native(report)

    def test_exact_file_and_full_seed_receipts_are_required(self):
        for target, field, value in (("file", "resume_fingerprint", native_id(999)),
            ("file", "completed", False), ("file", "status", "failed"),
            ("run", "files_total", 12), ("run", "files_total", 13), ("run", "evidence_persisted", False)):
            report = receipt()
            (report["files"][0] if target == "file" else report["seed_run"])[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                proof.validate_native(report)

    def test_structure_corruption_and_extra_slot_fail(self):
        report = receipt()
        report["structures"][0]["constituent_ids"].pop()
        with self.assertRaises(ValueError):
            proof.validate_native(report)
        report = receipt()
        row = copy.deepcopy(report["witnesses"][2])
        row["object_id"] = native_id(999)
        report["witnesses"].append(row)
        with self.assertRaises(ValueError):
            proof.validate_native(report)

    def test_skipped_file_identity_requires_unique_exact_completed_history(self):
        report = receipt()
        for file in report["files"]:
            file.update(current_file_id=None, status="skipped-complete", historical_file_ids=[file["file_id"]])
        proof.validate_native(report)
        for candidates in (None, [], [native_id(50), native_id(999)]):
            changed = copy.deepcopy(report)
            changed["files"][0]["historical_file_ids"] = candidates
            with self.subTest(candidates=candidates), self.assertRaises(ValueError):
                proof.validate_native(changed)
    def test_truncated_evidence_or_predeclared_prompt_fails_novelty_proof(self):
        for field in ("current_root_has_invocation", "current_root_has_parse"):
            report = receipt()
            report[field] = True
            with self.subTest(field=field), self.assertRaises(ValueError):
                proof.validate_native(report)
        report = receipt()
        report["witness_counts"]["parse"] = proof.WITNESS_LIMIT + 1
        with self.assertRaises(ValueError):
            proof.validate_native(report)

    def test_normal_http_request_has_only_ordinary_surface_and_fresh_session(self):
        request = proof.chat_request("new-session")
        self.assertEqual(set(request), {"model", "messages", "session", "stream"})
        self.assertEqual(request["messages"], [{"role": "user", "content": "define glacier"}])
        proof.validate_chat(chat(), "fresh", proof.validate_native(receipt()))

    def test_http_empty_failed_wrong_session_or_wrong_text_is_not_success(self):
        cases = [chat(content=""), chat(session="old"), chat(content="unrelated answer")]
        failed = chat()
        failed["status"] = 402
        cases.append(failed)
        for result in cases:
            with self.subTest(result=result), self.assertRaises(ValueError):
                proof.validate_chat(result, "fresh", "source supplied Unicode gloss: glaciér")

    def test_http_uses_existing_ci_protocol_and_does_not_publish_credentials(self):
        class Response(io.BytesIO):
            code = 200
            headers = {"X-Laplace-Session": "fresh"}
        seen = []
        def receive(request, timeout):
            seen.append(request)
            return Response(json.dumps(chat()["response"]).encode())
        with patch.dict(os.environ, {"LAPLACE_API_KEY": "secret-key", "LAPLACE_QUOTE_ID": "approved-existing"}, clear=True), \
             patch.object(proof.urllib.request, "urlopen", side_effect=receive):
            result = proof.normal_chat("http://127.0.0.1:8080", proof.chat_request("fresh"), 30)
        self.assertEqual(seen[0].get_header("X-laplace-tenant"), "ci")
        self.assertEqual(seen[0].get_header("X-laplace-quote-id"), "approved-existing")
        self.assertEqual(seen[0].get_header("Authorization"), "Bearer secret-key")
        self.assertNotIn("secret-key", json.dumps(result))
        self.assertEqual(len(seen), 1)

    def test_generated_sql_is_read_only_ordinary_defaults_and_exact_seed_run(self):
        shape = {"schema": proof.SCHEMA, "exemplar_parse_id": "01"*16, "predicate_id": "02"*16,
                 "slots": [{"exemplar_token_ref_id": "03"*16, "accepted_entity_type_id": "04"*16}]}
        files = {"shape": {"relative_path": "seeds/operation'payload.json", "payload": b"{}"},
                 "exemplar": {"relative_path": "seeds/operational/exemplar.conllu", "payload": b"define justice"}}
        run = "11111111-2222-3333-4444-555555555555"
        sql = proof.proof_sql(shape, files, 30, run)
        self.assertIn("READ ONLY", sql)
        self.assertTrue(sql.rstrip().endswith("ROLLBACK;"))
        self.assertIn("128,5,0.6,10,", sql)
        self.assertIn("2,8,NULL::bytea[],NULL::bytea[]", sql)
        self.assertIn("f.run_id='" + run + "'::uuid", sql)
        self.assertNotIn("ORDER BY f.started_at", sql)
        self.assertNotIn("operation'payload", sql)
        self.assertNotIn("WHERE a.outcome", sql)
        self.assertEqual(sql.count("generation.forward_program("), 1)
        self.assertEqual(sql.count("realize.batch("), 1)
        self.assertIn("public.laplace_hash128_merkle", sql)
        self.assertIn("laplace.entity_type_id('WordNet_Synset')", sql)
        registry = (ROOT / "app/Laplace.Substrate/Abstractions/EntityTypeRegistry.cs").read_text()
        self.assertIn('WordNetSynset = Id("WordNet_Synset")', registry)

    def test_failed_native_or_http_retains_evidence_without_retry(self):
        native = receipt()
        shape = {"schema": proof.SCHEMA, "exemplar_parse_id": proof.id_hex(native["task"]["exemplar"]),
                 "predicate_id": proof.id_hex(native["task"]["predicate"]), "slots": [{
                     "exemplar_token_ref_id": proof.id_hex(native["slot"]["token_ref"]),
                     "accepted_entity_type_id": proof.id_hex(native["slot"]["accepted_type"])}]}
        files = {kind: {"relative_path": kind, "payload": b"data", "sha256": "test-only"}
                 for kind in ("shape", "exemplar")}
        failed = chat()
        failed["status"] = 402
        for native_budget_failure in (False, True):
            if native_budget_failure:
                native["execution"][-1].update(event="unresolved", completion=False, disposition="budget_exhausted")
            with self.subTest(native_budget_failure=native_budget_failure), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "proof.json"
                args = ["verify-operational-task", "--shape-file", "unused.json", "--receipt", str(path),
                        "--seed-run-id", "11111111-2222-3333-4444-555555555555"]
                with patch.object(proof.sys, "argv", args), \
                     patch.object(proof, "selected_files", return_value=(shape, files)), \
                     patch.object(proof.SOURCE, "bounded_query", return_value=json.dumps(native).encode()), \
                     patch.object(proof.SOURCE, "decode_candidates", side_effect=lambda r, p: r.update(
                         decoder_path="test-only", decoder_sha256="test-only")), \
                     patch.object(proof, "normal_chat", return_value=failed) as http, \
                     patch("builtins.print"):
                    self.assertEqual(proof.main(), 1)
                report = json.loads(path.read_text())
                self.assertEqual(report["native_execution"], native)
                self.assertEqual(report["disposition"], "failed")
                self.assertIn("independent", report["execution_separation"])
                self.assertTrue(path.with_suffix(".sql").exists())
                if native_budget_failure:
                    http.assert_not_called()
                    self.assertNotIn("http_execution", report)
                else:
                    self.assertEqual(report["http_execution"]["status"], 402)
                    self.assertTrue(report["http_request"]["session"].startswith("operational-proof-"))
                    http.assert_called_once()


if __name__ == "__main__":
    unittest.main()
