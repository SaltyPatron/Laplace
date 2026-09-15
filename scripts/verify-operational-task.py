#!/usr/bin/env python3
"""Prove an admitted default task through independent native and HTTP executions.

Run after the operational bundle, corrected corpus and application are installed.
This verifier admits no task/source facts. The HTTP request is a normal witnessed
conversation; its receipt is explicitly separate from the read-only native run.
The expected answer is read from WordNet testimony, never supplied to execution.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
PROMPT = "define glacier"
SCHEMA = "laplace/task-shape/relation-read/token-slots/v1"
MODEL = "laplace-converse-001"
MAX_BYTES = 4 << 20
WITNESS_LIMIT = 32


def load_script(name: str):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


SOURCE = load_script("read-operational-source")
SEED = load_script("verify-operational-seed")


def id_hex(value: object) -> str:
    if isinstance(value, str) and value.startswith("\\x"):
        value = value[2:]
    if not isinstance(value, str) or not re.fullmatch("[0-9a-f]{32}", value) or value == "0" * 32:
        raise ValueError("receipt requires a nonzero native 16-byte identity")
    return value


def selected_files(shape_path: Path, exemplar_path: Path, root: Path = ROOT,
                   bundle: Path | None = None) -> tuple[dict, dict]:
    """Frame the exact authored selection; native code still owns schema/identity."""
    selected = SEED.authored_bundle(root, bundle or root / "app/Laplace.Cli/bin/Release/net10.0/seeds/operational")
    require(SEED.EXPECTED_ARTIFACTS == 13, "the final thirteen-artifact operational bundle is not installed")
    files = {}
    for kind, path in (("shape", shape_path), ("exemplar", exemplar_path)):
        path = path.resolve()
        relative = path.relative_to(root.resolve()).as_posix()
        if path.stat().st_size > 64 << 10:
            raise ValueError("default task artifact exceeds the 64 KiB proof envelope")
        payload = path.read_bytes()
        if not payload:
            raise ValueError("default task artifact is empty")
        require(selected.get(relative) == payload, "task artifact is not the exact declared product bundle selection")
        files[kind] = {"relative_path": relative, "payload": payload,
                       "sha256": hashlib.sha256(payload).hexdigest()}
    shape = json.loads(files["shape"]["payload"])
    if (set(shape) != {"schema", "exemplar_parse_id", "predicate_id", "slots"}
            or shape["schema"] != SCHEMA or not isinstance(shape["slots"], list)
            or len(shape["slots"]) != 1):
        raise ValueError("this proof requires the one-token default relation-read declaration")
    slot = shape["slots"][0]
    if set(slot) != {"exemplar_token_ref_id", "accepted_entity_type_id"}:
        raise ValueError("default slot fields differ from the authored source schema")
    for value in (shape["exemplar_parse_id"], shape["predicate_id"], *slot.values()):
        id_hex(value)
    return shape, files


def proof_sql(shape: dict, files: dict, timeout: int, seed_run_id: str) -> str:
    require(str(uuid.UUID(seed_run_id)) == seed_run_id, "seed run ID must be a canonical UUID")
    slot = shape["slots"][0]
    binary = lambda value: "decode('" + id_hex(value) + "','hex')"
    expected = ",".join("(" + SOURCE.text_sql(kind) + "," + SOURCE.text_sql(file["relative_path"])
        + f",{len(file['payload'])}::bigint," + SEED.fingerprint_sql(file["payload"]) + ")"
        for kind, file in files.items())
    # Fixed, indexed source reads. Extra witnesses are retained and explicitly
    # reject an incomplete proof; limits never select an interpretation.
    return f"""
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL client_encoding='UTF8';
SET LOCAL bytea_output='hex';
SET LOCAL statement_timeout='{timeout}s';
SET LOCAL lock_timeout='5s';
SET LOCAL idle_in_transaction_session_timeout='60s';
WITH
seed_run AS MATERIALIZED (
 SELECT * FROM laplace.ingest_run_journal WHERE run_id='{seed_run_id}'::uuid
),
roster AS MATERIALIZED (
 SELECT laplace.source_id('OperationalDecomposer') AS operational,
        laplace.source_id('WordNetDecomposer') AS wordnet,
        laplace.relation_type_id('IS_EXAMPLE_OF') AS example_of,
        laplace.relation_type_id('CALLS') AS calls,
        laplace.relation_type_id('HAS_INPUT') AS has_input,
        laplace.relation_type_id('HAS_PARSE') AS has_parse,
        laplace.relation_type_id('CONTAINS') AS contains,
        laplace.relation_type_id('HAS_SENSE') AS has_sense,
        laplace.relation_type_id('IS_SENSE_OF') AS sense_of,
        laplace.relation_type_id('HAS_DEFINITION') AS definition,
        laplace.entity_type_id('WordNet_Synset') AS synset_type,
        laplace.word_id('glacier') AS operand
),
slot AS MATERIALIZED (
 SELECT {binary(slot['exemplar_token_ref_id'])} AS token_ref,
        {binary(slot['accepted_entity_type_id'])} AS accepted_type,
        public.laplace_hash128_merkle(4::smallint, ARRAY[
          realize.canonical_id('laplace/task-shape/token-slot/v1'),
          {binary(slot['exemplar_token_ref_id'])},{binary(slot['accepted_entity_type_id'])}]) AS id
),
declaration AS MATERIALIZED (
 SELECT {binary(shape['exemplar_parse_id'])} AS exemplar,
        {binary(shape['predicate_id'])} AS predicate,
        ARRAY[realize.canonical_id('{SCHEMA}'),{binary(shape['exemplar_parse_id'])},
          {binary(shape['predicate_id'])},s.id,s.token_ref,s.accepted_type,
          realize.canonical_id('laplace/task-shape/slots-end/v1')] AS ids FROM slot s
),
task AS MATERIALIZED (
 SELECT d.*,public.laplace_hash128_merkle(4::smallint,d.ids) AS id FROM declaration d
),
expected(kind,relative_path,bytes,fingerprint) AS (VALUES {expected}),
files AS MATERIALIZED (
 SELECT e.kind,e.relative_path,e.bytes AS expected_bytes,e.fingerprint,
        f.run_id,f.file_id AS current_file_id,
        COALESCE(f.file_id,CASE WHEN cardinality(history.ids)=1 THEN history.ids[1] END) AS file_id,
        history.ids AS historical_file_ids,
        f.resume_fingerprint,f.bytes,f.status,f.disposition,f.ended_at,f.error,
        EXISTS(SELECT 1 FROM laplace.attestations a
          WHERE a.type_id=realize.canonical_id('substrate/type/HasLayerCompleted/2/v1')
          AND a.subject_id=e.fingerprint AND a.object_id=e.fingerprint AND a.source_id=e.fingerprint
          AND a.context_id=(SELECT operational FROM roster)) AS completed
 FROM expected e LEFT JOIN laplace.ingest_file_journal f
 ON f.run_id='{seed_run_id}'::uuid AND f.file_label='operational/'||e.relative_path
 AND f.source_name='OperationalDecomposer'
 LEFT JOIN LATERAL (
   SELECT array_agg(h.file_id) AS ids FROM (
     SELECT DISTINCT h.file_id FROM laplace.ingest_file_journal h
     WHERE f.status='skipped-complete' AND f.file_id IS NULL
     AND h.source_name='OperationalDecomposer' AND h.file_label='operational/'||e.relative_path
     AND h.relative_path=e.relative_path AND h.resume_fingerprint=e.fingerprint AND h.bytes=e.bytes
     AND h.status='ok' AND h.disposition='admitted' AND h.ended_at IS NOT NULL AND h.error IS NULL
     AND h.file_id IS NOT NULL LIMIT 2
   ) h
 ) history ON true
),
declarations AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a WHERE a.source_id=(SELECT operational FROM roster)
 AND a.subject_id=ANY(ARRAY[(SELECT id FROM task),(SELECT exemplar FROM task)])
 AND a.type_id=ANY(ARRAY[(SELECT example_of FROM roster),(SELECT calls FROM roster),(SELECT has_input FROM roster)])
 ORDER BY a.id LIMIT {WITNESS_LIMIT+1}
),
parses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a WHERE a.source_id=(SELECT operational FROM roster)
 AND a.type_id=(SELECT has_parse FROM roster) AND a.object_id=(SELECT exemplar FROM task)
 ORDER BY a.id LIMIT {WITNESS_LIMIT+1}
),
containment AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a WHERE a.source_id=(SELECT operational FROM roster)
 AND a.type_id=(SELECT contains FROM roster)
 AND a.subject_id=ANY(ARRAY(SELECT file_id FROM files WHERE kind='exemplar'))
 AND a.object_id=ANY(ARRAY(SELECT context_id FROM parses)) ORDER BY a.id LIMIT {WITNESS_LIMIT+1}
),
senses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a WHERE a.source_id=(SELECT wordnet FROM roster)
 AND a.type_id=(SELECT has_sense FROM roster) AND a.subject_id=laplace.word_id('glacier')
 ORDER BY a.id LIMIT {WITNESS_LIMIT+1}
),
synsets AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a WHERE a.source_id=(SELECT wordnet FROM roster)
 AND a.type_id=(SELECT sense_of FROM roster) AND a.subject_id=ANY(ARRAY(SELECT object_id FROM senses))
 ORDER BY a.id LIMIT {WITNESS_LIMIT+1}
),
definitions AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a WHERE a.source_id=(SELECT wordnet FROM roster)
 AND a.type_id=(SELECT definition FROM roster) AND a.subject_id=ANY(ARRAY(SELECT object_id FROM synsets))
 ORDER BY a.id LIMIT {WITNESS_LIMIT+1}
),
evidence AS MATERIALIZED (
 SELECT 'declaration'::text AS route,a.* FROM declarations a
 UNION ALL SELECT 'parse',a.* FROM parses a UNION ALL SELECT 'containment',a.* FROM containment a
 UNION ALL SELECT 'sense',a.* FROM senses a UNION ALL SELECT 'synset',a.* FROM synsets a
 UNION ALL SELECT 'definition',a.* FROM definitions a
),
standing AS MATERIALIZED (
 SELECT e.*,to_jsonb(c) AS pooled_consensus,
        consensus.walk_edge_weight(c.rating,c.rd)>0 AS positive_standing
 FROM evidence e LEFT JOIN laplace.consensus c
 ON c.subject_id=e.subject_id AND c.type_id=e.type_id AND c.object_id=e.object_id
),
selected AS MATERIALIZED (
 SELECT p.* FROM laplace.physicalities p WHERE p.type=8 AND p.id=ANY(ARRAY[
    public.laplace_hash128_blake3((SELECT id FROM task)||decode('0800','hex')),
    public.laplace_hash128_blake3((SELECT exemplar FROM task)||decode('0800','hex'))])
),
packed_sizes AS MATERIALIZED (
 SELECT p.id,sum(u.run_length)::bigint AS logical_count FROM selected p
 CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) u
 WHERE p.n_constituents BETWEEN 1 AND 2048 AND public.st_npoints(p.trajectory) BETWEEN 1 AND 2048 GROUP BY p.id
),
structures AS MATERIALIZED (
 SELECT p.entity_id,p.id,p.n_constituents,array_agg(u.entity_id ORDER BY u.ordinal) AS ids,
        encode(public.st_asewkb(p.trajectory),'hex') AS trajectory_ewkb_hex
 FROM selected p JOIN packed_sizes z ON z.id=p.id
 CROSS JOIN LATERAL public.laplace_trajectory_expanded_constituents(p.trajectory) u
 WHERE z.logical_count=p.n_constituents AND z.logical_count BETWEEN 1 AND 2048
 GROUP BY p.entity_id,p.id,p.n_constituents,p.trajectory
),
type8_frontier AS MATERIALIZED (
 SELECT p.id,p.entity_id,p.n_constituents FROM laplace.physicalities p
 WHERE p.type=8 AND p.trajectory IS NOT NULL
 AND public.laplace_trajectory_constituent_ids(p.trajectory)
     && ARRAY[laplace.word_id('define'),laplace.word_id('glacier')]
 LIMIT 9
),
execution AS MATERIALIZED (
 SELECT * FROM generation.forward_program({SOURCE.text_sql(PROMPT)},128,5,0.6,10,
   laplace.hash128_lo(public.laplace_hash128_blake3(convert_to({SOURCE.text_sql(PROMPT)},'UTF8'))),
   2,8,NULL::bytea[],NULL::bytea[])
),
realization_ids AS MATERIALIZED (
 SELECT array_agg(id ORDER BY id) AS ids FROM (
   SELECT object_id AS id FROM definitions UNION SELECT entity FROM execution WHERE event='emit'
   UNION SELECT sep_entity FROM execution WHERE event='emit' AND sep_entity IS NOT NULL) x
),
rendered AS MATERIALIZED (SELECT ids,realize.batch(ids) AS surfaces FROM realization_ids)
SELECT jsonb_build_object(
 'schema','laplace.operational-task-native-proof/v1','database',current_database(),
 'snapshot',pg_current_snapshot()::text,'observed_at',transaction_timestamp(),
 'transaction_read_only',current_setting('transaction_read_only'),
 'extensions',(SELECT jsonb_object_agg(extname,extversion) FROM pg_extension WHERE extname IN ('laplace_geom','laplace_substrate')),
 'seed_run',(SELECT to_jsonb(r) FROM seed_run r),
 'roster',(SELECT to_jsonb(r) FROM roster r),'task',(SELECT to_jsonb(t) FROM task t),
 'slot',(SELECT to_jsonb(s) FROM slot s),'files',(SELECT jsonb_agg(to_jsonb(f) ORDER BY kind) FROM files f),
 'witnesses',COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY route,id) FROM standing s),'[]'),
 'witness_counts',(SELECT jsonb_object_agg(route,n) FROM (SELECT route,count(*) AS n FROM evidence GROUP BY route) c),
 'type8_nomination_diagnostic',jsonb_build_object(
    'native_fanout',8,'sample_limit',9,'over_default_fanout',(SELECT count(*)>8 FROM type8_frontier),
    'sample',COALESCE((SELECT jsonb_agg(to_jsonb(f)) FROM type8_frontier f),'[]')),
 'structures',COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'parse_id',encode(s.entity_id,'hex'),'physicality_id',encode(s.id,'hex'),
    'n_constituents',s.n_constituents,'trajectory_ewkb_hex',s.trajectory_ewkb_hex,
    'canonical_identity',s.entity_id=public.laplace_hash128_merkle(4::smallint,s.ids),
    'constituent_ids',(SELECT jsonb_agg(encode(x,'hex')) FROM unnest(s.ids) x))) FROM structures s),'[]'),
 'synset_entities',COALESCE((SELECT jsonb_agg(to_jsonb(e)) FROM laplace.entities e
    WHERE e.id=ANY(ARRAY(SELECT object_id FROM synsets))),'[]'),
 'execution',(SELECT jsonb_agg(to_jsonb(g) ORDER BY step,routing_round,event) FROM execution g),
 'realizations',COALESCE((SELECT jsonb_object_agg(encode(u.id,'hex'),r.surfaces[u.ord])
    FROM rendered r CROSS JOIN LATERAL unnest(r.ids) WITH ORDINALITY u(id,ord)),'{{}}'),
 'current_root_has_invocation',EXISTS(SELECT 1 FROM laplace.attestations a
    WHERE a.subject_id=ANY(ARRAY(SELECT DISTINCT root_id FROM execution))
    AND a.type_id=ANY(ARRAY[(SELECT calls FROM roster),(SELECT has_input FROM roster)])),
 'current_root_has_parse',EXISTS(SELECT 1 FROM laplace.attestations a
    WHERE a.subject_id=ANY(ARRAY(SELECT DISTINCT root_id FROM execution)) AND a.type_id=(SELECT has_parse FROM roster))
)::text;
ROLLBACK;
"""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def validate_native(report: dict) -> str:
    require(report.get("transaction_read_only") == "on", "native proof was not read-only")
    require(not report["current_root_has_invocation"] and not report["current_root_has_parse"],
            "default prompt already has explicit invocation/parse testimony; novelty proof is invalid")
    require(all(type(n) is int and 0 < n <= WITNESS_LIMIT for n in report["witness_counts"].values()),
            "source witness envelope was exceeded")
    roster, task, slot = report["roster"], report["task"], report["slot"]
    run = report["seed_run"]
    require(run["source_name"] == "OperationalDecomposer" and run["source_id"] == roster["operational"]
            and run["layer"] == 2 and run["status"] == "ok" and run["evidence_persisted"] is True
            and run["files_done"] == run["files_total"] == 13 and run["units_failed"] == 0
            and run["ended_at"] and not run["error"], "the exact full operational seed run did not complete")
    require(task["predicate"] == roster["definition"] and slot["accepted_type"] == roster["synset_type"],
            "declaration does not request a definition of a typed WordNet synset")
    files = {f["kind"]: f for f in report["files"]}
    require(set(files) == {"shape", "exemplar"}, "missing authored file receipts")
    for file in files.values():
        id_hex(file["file_id"])
        if file["current_file_id"] is None:
            require(file["status"] == "skipped-complete" and file["historical_file_ids"] == [file["file_id"]],
                    "skipped artifact lacks a unique prior completed exact-fingerprint file identity")
        else:
            require(file["current_file_id"] == file["file_id"], "current file identity was replaced")
        require(file["status"] in {"ok", "skipped-complete"} and file["disposition"] == "admitted"
                and file["completed"] is True and file["ended_at"] and not file["error"]
                and file["bytes"] == file["expected_bytes"] and file["resume_fingerprint"] == file["fingerprint"],
                "authored file lacks exact completed byte admission")
    witnesses = report["witnesses"]

    def positive(subject, predicate, obj, source, context):
        cell = [w for w in witnesses if (w["subject_id"], w["type_id"], w["object_id"], w["source_id"], w["context_id"])
                == (subject, predicate, obj, source, context)]
        return any(w["outcome"] == 2 and w["observation_count"] > 0 and w["positive_standing"] is True for w in cell) \
            and not any(w["outcome"] == 0 and w["observation_count"] > 0 for w in cell)

    op, context = roster["operational"], files["shape"]["file_id"]
    require(positive(task["exemplar"], roster["example_of"], task["id"], op, context)
            and positive(task["id"], roster["calls"], task["predicate"], op, context)
            and positive(task["id"], roster["has_input"], slot["id"], op, context),
            "missing, opposed, or nonpositive source-scoped task declarations")
    scoped_inputs = {w["object_id"] for w in witnesses if w["subject_id"] == task["id"]
                     and w["type_id"] == roster["has_input"] and w["source_id"] == op and w["context_id"] == context}
    require(scoped_inputs == {slot["id"]}, "source declares an unmatched input slot")
    scoped_calls = {w["object_id"] for w in witnesses if w["subject_id"] == task["id"]
                    and w["type_id"] == roster["calls"] and w["source_id"] == op and w["context_id"] == context}
    require(scoped_calls == {task["predicate"]}, "source declares an unmatched predicate")
    parse_rows = [w for w in witnesses if w["route"] == "parse" and w["object_id"] == task["exemplar"]]
    require(any(positive(w["subject_id"], roster["has_parse"], task["exemplar"], op, w["context_id"])
                and positive(files["exemplar"]["file_id"], roster["contains"], w["context_id"], op, files["exemplar"]["file_id"])
                for w in parse_rows), "exemplar lacks its exact own-source file/occurrence/parse chain")
    structures = {s["parse_id"]: s for s in report["structures"]}
    require(set(structures) == {id_hex(task["id"]), id_hex(task["exemplar"])}
            and all(s["canonical_identity"] is True for s in structures.values()),
            "missing or noncanonical complete native structures")
    require(structures[id_hex(task["id"])]["constituent_ids"] == [id_hex(x) for x in task["ids"]],
            "admitted task structure differs from the authored declaration")
    parse = structures[id_hex(task["exemplar"])]
    require(parse.get("complete_native_decode") is True, "exemplar did not pass the installed native UD decoder")
    require(any(t["ref_id"] == id_hex(slot["token_ref"]) for t in parse["ud"]["tokens"]),
            "declared slot is absent from the complete exemplar")
    synset_ids = {e["id"] for e in report["synset_entities"] if e["type_id"] == slot["accepted_type"]}
    valid_definitions = set()
    for definition in (w for w in witnesses if w["route"] == "definition"):
        synset = definition["subject_id"]
        if synset not in synset_ids or not positive(synset, roster["definition"], definition["object_id"], roster["wordnet"], definition["context_id"]):
            continue
        for sense_of in (w for w in witnesses if w["route"] == "synset" and w["object_id"] == synset):
            if positive(sense_of["subject_id"], roster["sense_of"], synset, roster["wordnet"], sense_of["context_id"]):
                if any(positive(s["subject_id"], roster["has_sense"], s["object_id"], roster["wordnet"], s["context_id"])
                       for s in witnesses if s["route"] == "sense" and s["subject_id"] == roster["operand"]
                       and s["object_id"] == sense_of["subject_id"]):
                    valid_definitions.add(definition["object_id"])
    require(len(valid_definitions) == 1, "WordNet operand does not have one complete, unopposed definition chain")
    expected = next(iter(valid_definitions))
    execution = report["execution"]
    terminals = [r for r in execution if r["event"] in {"complete", "unresolved"}]
    require(len(terminals) == 1, "native execution did not return exactly one terminal receipt")
    terminal = terminals[0]
    require(terminal["event"] == "complete" and terminal["completion"] is True
            and terminal["disposition"] == "complete" and terminal["remaining_required"] == 0
            and terminal["required_obligations"] == terminal["satisfied_obligations"]
            and terminal["required_obligations"] > 0, "native execution left required obligations open")
    for name in ("root_id", "program_id", "output_fingerprint", "semantic_act_id"):
        id_hex(terminal[name])
    require(all(r["root_id"] == terminal["root_id"] and r["program_id"] == terminal["program_id"] for r in execution),
            "native rows do not belong to one exact root/program")
    emitted = [r for r in execution if r["event"] == "emit"]
    require(len(emitted) == terminal["output_count"] == 1 and emitted[0]["entity"] == expected,
            "native result differs from the source-witnessed WordNet definition")
    require(emitted[0]["declared_result"] is True and emitted[0]["support_relation"] == roster["definition"],
            "native result lacks the declared relation-read support")
    require(emitted[0]["support_anchor"] in synset_ids and emitted[0]["support_outbound"] is True
            and emitted[0]["support_witnesses"] > 0,
            "native result does not retain the actual witnessed WordNet operand")
    text = report["realizations"].get(id_hex(expected))
    require(isinstance(text, str) and bool(text.strip()), "native realization of the WordNet result is empty")
    return text


def chat_request(session: str) -> dict:
    # No caller task/shape/predicate, output mask, answer, or special execution context.
    return {"model": MODEL, "messages": [{"role": "user", "content": PROMPT}],
            "session": session, "stream": False}


def normal_chat(base: str, request: dict, timeout: int) -> dict:
    headers = {"Content-Type": "application/json", "X-Laplace-Tenant": os.environ.get("LAPLACE_PROOF_TENANT", "ci")}
    for variable, header in (("LAPLACE_API_KEY", "Authorization"), ("LAPLACE_QUOTE_ID", "X-Laplace-Quote-Id")):
        if os.environ.get(variable):
            headers[header] = ("Bearer " if header == "Authorization" else "") + os.environ[variable]
    req = urllib.request.Request(base.rstrip("/") + "/v1/chat/completions",
                                 data=json.dumps(request).encode("utf-8"), headers=headers, method="POST")
    try:
        response = urllib.request.urlopen(req, timeout=timeout)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read(MAX_BYTES + 1)
        require(len(payload) <= MAX_BYTES, "HTTP response exceeds the proof byte envelope")
        return {"status": response.code, "session_header": response.headers.get("X-Laplace-Session"),
                "response": json.loads(payload.decode("utf-8"))}


def validate_chat(result: dict, session: str, expected: str) -> None:
    require(result["status"] == 200, f"ordinary chat failed with HTTP {result['status']}")
    response = result["response"]
    require(response.get("object") == "chat.completion" and response.get("model") == MODEL,
            "ordinary endpoint returned another response contract")
    require(result["session_header"] == session and response["metadata"].get("session") == session,
            "ordinary endpoint did not retain the fresh session key")
    choices = response["choices"]
    require(len(choices) == 1 and choices[0]["message"]["role"] == "assistant"
            and choices[0]["finish_reason"] == "stop" and response["metadata"]["reply_rows"] == 1,
            "ordinary conversation did not complete exactly one response")
    require(choices[0]["message"]["content"] == expected, "ordinary response differs from native WordNet realization")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shape-file", type=Path, required=True)
    parser.add_argument("--seed-run-id", required=True, help="exact successful default seed invocation UUID")
    parser.add_argument("--exemplar-file", type=Path, default=ROOT / "seeds/operational/exemplars/en_define.conllu")
    parser.add_argument("--database", default=os.environ.get("PGDATABASE", "laplace"))
    parser.add_argument("--base", default=os.environ.get("LAPLACE_API_BASE", "http://127.0.0.1:8080"))
    parser.add_argument("--bundle", type=Path, default=ROOT / "app/Laplace.Cli/bin/Release/net10.0/seeds/operational")
    parser.add_argument("--core", type=Path, default=Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace")) / "lib/liblaplace_core.so")
    parser.add_argument("--timeout-seconds", type=int, default=180)
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.timeout_seconds <= 600:
        parser.error("timeout-seconds must be within 1..600")
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    report = {"schema": "laplace.operational-task-proof/v1", "disposition": "failed", "prompt": PROMPT,
              "execution_separation": "Native receipt is an independent read-only invocation; HTTP executes a fresh witnessed session. No shared invocation identity is asserted.",
              "native_parameters": {"steps": 128, "max_stride": 5, "spread": 0.6, "top_k": 10,
                  "hops": 2, "fanout": 8, "prior_frontier": None, "output_relation_types": None,
                  "seed": "native low 64 bits of BLAKE3(exact prompt UTF8), matching forward_text"}}
    started = time.monotonic_ns()
    try:
        shape, files = selected_files(args.shape_file, args.exemplar_file, bundle=args.bundle)
        report["authored_files"] = {k: {n: v for n, v in f.items() if n != "payload"} for k, f in files.items()}
        sql = proof_sql(shape, files, args.timeout_seconds, args.seed_run_id)
        sql_path = args.receipt.with_suffix(".sql")
        sql_path.write_text(sql, encoding="utf-8")
        report["sql_sha256"] = hashlib.sha256(sql.encode("utf-8")).hexdigest()
        native = json.loads(SOURCE.bounded_query(args.database, sql_path, args.timeout_seconds, MAX_BYTES))
        report["native_execution"] = native
        decoder = {"parse_candidates": [s for s in native["structures"] if s["parse_id"] == shape["exemplar_parse_id"]]}
        SOURCE.decode_candidates(decoder, args.core)
        report["decoder"] = {k: decoder[k] for k in ("decoder_path", "decoder_sha256")}
        expected = validate_native(native)
        # Preserve the native evidence even if transport fails after the normal
        # endpoint has witnessed the prompt. No failed request is retried here.
        report["http_request"] = chat_request("operational-proof-" + uuid.uuid4().hex)
        report["http_base"] = args.base
        report["http_tenant_header"] = os.environ.get("LAPLACE_PROOF_TENANT", "ci")
        report["http_started_at_unix_ns"] = time.time_ns()
        args.receipt.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        report["http_execution"] = normal_chat(args.base, report["http_request"], args.timeout_seconds)
        report["http_finished_at_unix_ns"] = time.time_ns()
        validate_chat(report["http_execution"], report["http_request"]["session"], expected)
        report["disposition"] = "native-and-ordinary-http-verified"
        code = 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, subprocess.SubprocessError) as error:
        report["error"] = str(error)
        code = 1
    report["wall_milliseconds"] = (time.monotonic_ns() - started) // 1_000_000
    args.receipt.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("schema", "disposition", "error", "wall_milliseconds") if k in report}))
    print(f"OPERATIONAL_TASK_RECEIPT={args.receipt}")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
