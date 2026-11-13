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
                "layer": 2, "status": "ok", "evidence_persisted": True, "files_done": proof.SEED.EXPECTED_ARTIFACTS,
                "files_total": proof.SEED.EXPECTED_ARTIFACTS, "units_failed": 0, "ended_at": "2026-09-15", "error": None},
            "witnesses": witnesses, "structures": structures, "execution": [emit, terminal],
            "synset_entities": [{"id": native_id(63), "type_id": roster["synset_type"]}],
            "realizations": {proof.id_hex(native_id(64)): "source supplied Unicode gloss: glaciér"}}


def recount(report):
    report["witness_counts"] = {}
    for row in report["witnesses"]:
        counts = report["witness_counts"]
        counts[row["route"]] = counts.get(row["route"], 0) + 1


def direct_receipt():
    """Two independent source targets; neither is selected as the HTTP oracle."""
    report = receipt()
    roster, task, slot = report["roster"], report["task"], report["slot"]
    task["predicate"] = task["ids"][2] = native_id(90)
    slot["accepted_type"] = task["ids"][5] = native_id(91)
    report["structures"][0]["constituent_ids"] = [proof.id_hex(x) for x in task["ids"]]
    report["witnesses"] = [w for w in report["witnesses"] if w["route"] in {"declaration", "parse", "containment"}]
    report["witnesses"][1]["object_id"] = task["predicate"]
    report["operand_entities"] = [{"id": roster["operand"], "type_id": slot["accepted_type"]}]
    report.pop("synset_entities")
    report["realizations"] = {}
    for i, text in enumerate(("source result α", "source result β " + "γ" * 1100)):
        target = native_id(200 + i)
        for route, subject, relation, obj in (
            ("relation", roster["operand"], task["predicate"], target),
            ("target_sense", native_id(210 + i), roster["sense_of"], target),
            ("target_name", native_id(220 + i), roster["has_sense"], native_id(210 + i))):
            report["witnesses"].append({"route": route, "id": native_id(100 + len(report["witnesses"])),
                "subject_id": subject, "type_id": relation, "object_id": obj,
                "source_id": roster["wordnet"], "context_id": None if i == 0 else native_id(230),
                "outcome": 2, "observation_count": 1, "positive_standing": True})
        report["realizations"][proof.id_hex(target)] = text
    report["execution"][0].update(entity=native_id(200), support_anchor=roster["operand"],
                                   support_relation=task["predicate"])
    recount(report)
    return report


def v2_receipt():
    report = direct_receipt()
    roster, task, slot = report["roster"], report["task"], report["slot"]
    roster.update(current_form_binding=native_id(300), witnessed_semantic_binding=native_id(301),
                  task_schema_v2=native_id(302), slots_end_v2=native_id(303))
    task["schema"] = proof.SCHEMA_V2
    slot["binding_mode"] = roster["current_form_binding"]
    task["ids"] = [roster["task_schema_v2"], task["exemplar"], task["predicate"], slot["id"],
                   slot["token_ref"], slot["accepted_type"], slot["binding_mode"], roster["slots_end_v2"]]
    report["structures"][0]["constituent_ids"] = [proof.id_hex(x) for x in task["ids"]]
    tokens = [{"ref_id": proof.id_hex(slot["token_ref"] if i == 3 else native_id(600+i)),
               "form_id": proof.id_hex(native_id(610+i))} for i in range(5)]
    report["structures"][1]["ud"]["tokens"] = tokens
    report["current_forms"] = [{"root_id": report["execution"][0]["root_id"], "node_index": 10*i,
                                "byte_offset": 4*i, "byte_length": 3,
                                "id": roster["operand"] if i == 3 else native_id(610+i)} for i in range(5)]
    return report


def chat(session="fresh", content="source supplied Unicode gloss: glaciér"):
    return {"status": 200, "session_header": session, "response": {
        "object": "chat.completion", "model": proof.MODEL,
        "metadata": {"session": session, "reply_rows": 1},
        "choices": [{"message": {"role": "assistant", "content": content}, "finish_reason": "stop"}]}}


class OperationalTaskTests(unittest.TestCase):
    def test_v2_modes_are_explicit_native_ids_and_v1_fields_remain_strict(self):
        shape = {"schema": proof.SCHEMA, "exemplar_parse_id": "01"*16, "predicate_id": "02"*16,
                 "slots": [{"exemplar_token_ref_id": "03"*16, "accepted_entity_type_id": "04"*16}]}
        self.assertEqual(proof.read_shape_definition(json.dumps(shape).encode()), shape)
        shape["slots"][0]["binding_mode_id"] = "05"*16
        with self.assertRaises(ValueError):
            proof.read_shape_definition(json.dumps(shape).encode())
        shape["schema"] = proof.SCHEMA_V2
        self.assertEqual(proof.read_shape_definition(json.dumps(shape).encode()), shape)
        for alteration in ("missing", "extra", "label", "schema", "nonobject_slot", "nonobject_root"):
            changed = copy.deepcopy(shape)
            if alteration == "missing":
                del changed["slots"][0]["binding_mode_id"]
            elif alteration == "extra":
                changed["slots"][0]["prefer_current"] = True
            elif alteration == "label":
                changed["slots"][0]["binding_mode_id"] = "current-form"
            elif alteration == "schema":
                changed["schema"] = proof.SCHEMA_V2 + "/unknown"
            elif alteration == "nonobject_slot":
                changed["slots"][0] = list(changed["slots"][0])
            else:
                changed = list(changed)
            with self.subTest(alteration=alteration), self.assertRaises(ValueError):
                proof.read_shape_definition(json.dumps(changed).encode())

    def test_v2_current_form_and_semantic_modes_keep_actual_anchor_constraint(self):
        for mode in ("current_form_binding", "witnessed_semantic_binding"):
            report = v2_receipt()
            report["slot"]["binding_mode"] = report["task"]["ids"][6] = report["roster"][mode]
            report["structures"][0]["constituent_ids"] = [proof.id_hex(x) for x in report["task"]["ids"]]
            with self.subTest(mode=mode):
                self.assertEqual(len(proof.validate_direct_native(report)), 2)
            report["execution"][0]["support_anchor"] = native_id(999)
            with self.assertRaises(ValueError):
                proof.validate_direct_native(report)

    def test_v2_unknown_mode_tampered_mode_and_invalid_native_hashes_are_rejected(self):
        for alteration in ("unknown", "changed_mode", "missing_mode", "extra_field", "wrong_marker",
                           "slot_hash", "shape_hash", "v1_with_mode"):
            report = v2_receipt()
            if alteration == "unknown":
                report["slot"]["binding_mode"] = report["task"]["ids"][6] = native_id(999)
                report["structures"][0]["constituent_ids"] = [proof.id_hex(x) for x in report["task"]["ids"]]
            elif alteration == "changed_mode":
                report["slot"]["binding_mode"] = report["roster"]["witnessed_semantic_binding"]
            elif alteration == "missing_mode":
                del report["slot"]["binding_mode"]
            elif alteration == "extra_field":
                report["slot"]["prefer_current"] = True
            elif alteration == "wrong_marker":
                report["task"]["ids"][0] = native_id(999)
            elif alteration == "slot_hash":
                report["structures"][0]["constituent_ids"][3] = proof.id_hex(native_id(999))
            elif alteration == "shape_hash":
                report["structures"][0]["canonical_identity"] = False
            else:
                report["task"]["schema"] = proof.SCHEMA
            with self.subTest(alteration=alteration), self.assertRaises(ValueError):
                proof.validate_direct_native(report)

    def test_v2_current_form_readback_proves_exact_slot_origin_without_electing_it(self):
        for alteration in ("alias", "invariant", "root", "missing", "reordered", "overflow"):
            report = v2_receipt()
            if alteration == "alias":
                report["current_forms"][3]["id"] = native_id(999)
            elif alteration == "invariant":
                report["current_forms"][0]["id"] = native_id(999)
            elif alteration == "root":
                report["current_forms"][3]["root_id"] = native_id(999)
            elif alteration == "missing":
                report["current_forms"].pop()
            elif alteration == "reordered":
                report["current_forms"][3]["byte_offset"] = 0
            else:
                report["current_forms"] *= 410
            with self.subTest(alteration=alteration), self.assertRaises(ValueError):
                proof.validate_direct_native(report)

    def test_direct_relation_retains_all_targets_and_independent_http_choice(self):
        report = direct_receipt()
        targets = proof.validate_direct_native(report)
        self.assertEqual(set(targets), {proof.id_hex(native_id(200)), proof.id_hex(native_id(201))})
        self.assertEqual(targets, report["realizations"])
        self.assertEqual(report["execution"][0]["entity"], native_id(200))
        for text in targets.values():
            proof.validate_chat(chat(content=text), "fresh", frozenset(targets.values()))
        with self.assertRaises(ValueError):
            proof.validate_chat(chat(content=targets[proof.id_hex(native_id(201))][:1024]),
                                "fresh", frozenset(targets.values()))

    def test_direct_target_ids_remain_distinct_when_native_text_converges(self):
        report = direct_receipt()
        report["realizations"][proof.id_hex(native_id(201))] = report["realizations"][proof.id_hex(native_id(200))]
        targets = proof.validate_direct_native(report)
        self.assertEqual(len(targets), 2)
        self.assertEqual(len(set(targets.values())), 1)

    def test_direct_incomplete_evidence_or_any_missing_target_realization_fails(self):
        for field in ("missing_witness", "overflow", "missing_text", "empty_text"):
            report = direct_receipt()
            if field == "missing_witness":
                report["witnesses"].pop()
            elif field == "overflow":
                report["witness_counts"]["relation"] = proof.WITNESS_LIMIT + 1
            elif field == "missing_text":
                del report["realizations"][proof.id_hex(native_id(201))]
            else:
                report["realizations"][proof.id_hex(native_id(201))] = ""
            with self.subTest(field=field), self.assertRaises(ValueError):
                proof.validate_direct_native(report)

    def test_direct_opposed_target_is_excluded_without_erasing_other_sources(self):
        report = direct_receipt()
        opposed = copy.deepcopy(next(w for w in report["witnesses"] if w["route"] == "relation"
                                     and w["object_id"] == native_id(201)))
        opposed.update(id=native_id(999), outcome=0)
        report["witnesses"].append(opposed)
        recount(report)
        targets = proof.validate_direct_native(report)
        self.assertEqual(set(targets), {proof.id_hex(native_id(200))})
        with self.assertRaises(ValueError):
            proof.validate_chat(chat(content=report["realizations"][proof.id_hex(native_id(201))]),
                                "fresh", frozenset(targets.values()))
        report["execution"][0]["entity"] = native_id(201)
        with self.assertRaises(ValueError):
            proof.validate_direct_native(report)
        # A different source's negative record remains visible but is not
        # falsely attributed to the WordNet scope; pooled standing still rules.
        opposed["source_id"] = native_id(998)
        self.assertEqual(len(proof.validate_direct_native(report)), 2)
        for row in report["witnesses"]:
            if row["route"] == "relation":
                row["positive_standing"] = False
        with self.assertRaises(ValueError):
            proof.validate_direct_native(report)

    def test_direct_requires_original_typed_operand_and_exact_emit_provenance(self):
        for field, value in (("entity", native_id(999)), ("support_anchor", native_id(999)),
                             ("support_relation", native_id(999)), ("support_outbound", False),
                             ("declared_result", False), ("support_witnesses", 0)):
            report = direct_receipt()
            report["execution"][0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                proof.validate_direct_native(report)
        for field in ("id", "type_id"):
            report = direct_receipt()
            report["operand_entities"][0][field] = native_id(999)
            with self.subTest(field=field), self.assertRaises(ValueError):
                proof.validate_direct_native(report)
        report = direct_receipt()
        report["operand_entities"].append({"id": report["roster"]["operand"], "type_id": native_id(999)})
        with self.assertRaises(ValueError):
            proof.validate_direct_native(report)

    def test_definition_keeps_its_single_target_requirement(self):
        report = receipt()
        alternate = copy.deepcopy(report["witnesses"][-1])
        alternate.update(id=native_id(999), object_id=native_id(998))
        report["witnesses"].append(alternate)
        report["realizations"][proof.id_hex(native_id(998))] = "another source definition"
        with self.assertRaises(ValueError):
            proof.validate_native(report)

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
            ("run", "files_total", 12), ("run", "files_total", 13), ("run", "files_total", 14),
            ("run", "evidence_persisted", False)):
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

    def test_direct_sql_reads_all_scopes_without_constraining_native_execution(self):
        shape = {"schema": proof.SCHEMA, "exemplar_parse_id": "01"*16, "predicate_id": "02"*16,
                 "slots": [{"exemplar_token_ref_id": "03"*16, "accepted_entity_type_id": "04"*16}]}
        files = {kind: {"relative_path": kind, "payload": b"bytes"} for kind in ("shape", "exemplar")}
        prompt = "The opposite of hót is"
        sql = proof.proof_sql(shape, files, 30, "11111111-2222-3333-4444-555555555555",
                              "direct-relation", prompt, "hót")
        relation = sql.split("relation_targets AS MATERIALIZED (", 1)[1].split("),", 1)[0]
        self.assertIn("a.subject_id=(SELECT operand FROM roster)", relation)
        self.assertIn("a.type_id=(SELECT predicate FROM task)", relation)
        self.assertNotIn("source_id=", relation)
        self.assertNotIn("outcome=", relation)
        self.assertIn("LIMIT " + str(proof.WITNESS_LIMIT + 1), relation)
        self.assertIn("target_senses AS MATERIALIZED", sql)
        self.assertIn("target_names AS MATERIALIZED", sql)
        self.assertIn("SELECT object_id AS id FROM relation_targets", sql)
        self.assertEqual(sql.count("realize.batch("), 1)
        self.assertNotIn("left(", sql)
        execution = sql.split("execution AS MATERIALIZED (", 1)[1].split("\n),", 1)[0]
        self.assertIn("generation.forward_program(" + proof.SOURCE.text_sql(prompt), execution)
        self.assertIn("128,5,0.6,10,", execution)
        self.assertIn("2,8,NULL::bytea[],NULL::bytea[]", execution)
        for forbidden in ("predicate", "operand", "task", "direct-relation", "02020202"):
            self.assertNotIn(forbidden, execution)
        self.assertTrue(sql.rstrip().endswith("ROLLBACK;"))

    def test_v2_sql_hashes_mode_in_slot_and_preserves_complete_native_occurrences(self):
        shape = {"schema": proof.SCHEMA_V2, "exemplar_parse_id": "01"*16, "predicate_id": "02"*16,
                 "slots": [{"exemplar_token_ref_id": "03"*16, "accepted_entity_type_id": "04"*16,
                            "binding_mode_id": "05"*16}]}
        files = {kind: {"relative_path": kind, "payload": b"bytes"} for kind in ("shape", "exemplar")}
        args = (shape, files, 30, "11111111-2222-3333-4444-555555555555")
        sql = proof.proof_sql(*args, "direct-relation", "The opposite of hot is", "hot")
        for marker in (proof.CURRENT_FORM, proof.WITNESSED_SEMANTIC, proof.SCHEMA_V2,
                       "laplace/task-shape/token-slot/v2", "laplace/task-shape/slots-end/v2"):
            self.assertIn("realize.canonical_id('" + marker + "')", sql)
        slot = sql.split("slot AS MATERIALIZED (", 1)[1].split("\n),", 1)[0]
        native_hash = slot.split("public.laplace_hash128_merkle", 1)[1]
        self.assertIn("decode('" + "05"*16 + "','hex')", native_hash)
        self.assertIn("s.id,s.token_ref,s.accepted_type,s.binding_mode", sql)
        self.assertIn("n.tier<=2 AND (n.parent_index IS NULL OR p.tier>2)", sql)
        self.assertIn("NOT realize.is_all_whitespace(n.surface)", sql)
        self.assertIn("ORDER BY n.byte_offset,n.node_index LIMIT 2049", sql)
        execution = sql.split("execution AS MATERIALIZED (", 1)[1].split("\n),", 1)[0]
        for forbidden in ("binding", "slot", "mode", "05050505", "operand", "predicate"):
            self.assertNotIn(forbidden, execution)
        shape["schema"] = proof.SCHEMA
        del shape["slots"][0]["binding_mode_id"]
        old = proof.proof_sql(*args)
        self.assertIn("realize.canonical_id('laplace/task-shape/token-slot/v1')", old)
        self.assertNotIn("binding_mode", old)
        self.assertNotIn("current_forms", old)

    def test_direct_cli_requires_explicit_inputs_and_definition_does_not_accept_overrides(self):
        baseline = ["verify-operational-task", "--shape-file", "shape.json", "--receipt", "unused.json",
                    "--seed-run-id", "11111111-2222-3333-4444-555555555555"]
        for extra in (["--proof-mode", "direct-relation"], ["--proof-mode", "direct-relation", "--prompt", "p"],
                      ["--prompt", "changed"], ["--operand", "changed"]):
            with self.subTest(extra=extra), patch.object(proof.sys, "argv", baseline + extra), \
                    patch.object(proof.sys, "stderr", new_callable=io.StringIO), \
                    self.assertRaises(SystemExit) as failure:
                proof.main()
            self.assertEqual(failure.exception.code, 2)

    def test_direct_main_retains_all_supported_targets_and_separate_http_execution(self):
        native = direct_receipt()
        shape = {"schema": proof.SCHEMA, "exemplar_parse_id": proof.id_hex(native["task"]["exemplar"]),
                 "predicate_id": proof.id_hex(native["task"]["predicate"]), "slots": [{
                     "exemplar_token_ref_id": proof.id_hex(native["slot"]["token_ref"]),
                     "accepted_entity_type_id": proof.id_hex(native["slot"]["accepted_type"])}]}
        files = {kind: {"relative_path": kind, "payload": b"data", "sha256": "test-only"}
                 for kind in ("shape", "exemplar")}
        prompt = "The opposite of hót is"
        for valid_http in (True, False):
            with self.subTest(valid_http=valid_http), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "antonym-task.json"
                args = ["verify-operational-task", "--shape-file", "unused.json", "--receipt", str(path),
                        "--seed-run-id", "11111111-2222-3333-4444-555555555555", "--proof-mode", "direct-relation",
                        "--prompt", prompt, "--operand", "hót"]
                def response(base, request, timeout):
                    return chat(session=request["session"], content=(native["realizations"][proof.id_hex(native_id(201))]
                                                                  if valid_http else "unsupported answer"))
                with patch.object(proof.sys, "argv", args), \
                     patch.object(proof, "selected_files", return_value=(shape, files)), \
                     patch.object(proof.SOURCE, "bounded_query", return_value=json.dumps(native).encode()), \
                     patch.object(proof.SOURCE, "decode_candidates", side_effect=lambda r, p: r.update(
                         decoder_path="test-only", decoder_sha256="test-only")), \
                     patch.object(proof, "normal_chat", side_effect=response) as http, patch("builtins.print"):
                    self.assertEqual(proof.main(), 0 if valid_http else 1)
                report = json.loads(path.read_text())
                self.assertEqual(report["native_execution"], native)
                self.assertEqual(report["supported_target_realizations"], native["realizations"])
                self.assertEqual(report["http_request"]["messages"], [{"role": "user", "content": prompt}])
                self.assertEqual(set(report["http_request"]), {"model", "messages", "session", "stream"})
                self.assertIn("independent", report["execution_separation"])
                self.assertEqual(report["proof_mode"], "direct-relation")
                self.assertEqual(report["disposition"], "native-and-ordinary-http-verified" if valid_http else "failed")
                http.assert_called_once()
                self.assertTrue(path.with_suffix(".sql").exists())

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
