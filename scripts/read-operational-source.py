#!/usr/bin/env python3
"""Read bounded, source-witnessed UD/WordNet material for operational curation.

This is a diagnostic, not a task compiler. Cue/operand strings are explicit
lookup inputs, never word-to-operation declarations. PostgreSQL/native functions
own indexed discovery, trajectory expansion, identity and rendering; the existing
native UD decoder owns annotation semantics. No source or task facts are written.
"""
from __future__ import annotations

import argparse
import ctypes as C
import hashlib
import json
import os
from pathlib import Path
import selectors
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


class Id(C.Structure):
    _fields_ = [("hi", C.c_uint64), ("lo", C.c_uint64)]


class Pairs(C.Structure):
    _fields_ = [("items", C.POINTER(Id)), ("count", C.c_size_t)]


class Token(C.Structure):
    _fields_ = [(n, Id) for n in ("ref_id", "form_id", "lemma_id", "upos_id", "xpos_id")]
    _fields_ += [("features", Pairs), ("head_ref_id", Id), ("deprel_id", Id),
                ("enhanced", Pairs), ("misc", Pairs)]


class Mwt(C.Structure):
    _fields_ = [("start_ref_id", Id), ("end_ref_id", Id), ("form_id", Id), ("misc", Pairs)]


class Parse(C.Structure):
    _fields_ = [("sentence_id", Id), ("language_id", Id), ("tokens", C.POINTER(Token)),
                ("token_count", C.c_size_t), ("mwts", C.POINTER(Mwt)), ("mwt_count", C.c_size_t)]


def identity(value: Id) -> str:
    return C.string_at(C.byref(value), 16).hex()


def file_sha256(path: Path) -> str:
    """Bounded hashing compatible with the installed runner's Python version."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            chunk = stream.read(64 << 10)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def decode_candidates(report: dict, core_path: Path) -> None:
    core = C.CDLL(str(core_path.resolve()))
    core.laplace_ud_parse_decode.argtypes = [C.POINTER(Id), C.c_size_t, C.POINTER(Parse)]
    core.laplace_ud_parse_decode.restype = C.c_int
    core.laplace_ud_parse_free.argtypes = [C.POINTER(Parse)]
    core.laplace_ud_parse_free.restype = None
    report["decoder_sha256"] = file_sha256(core_path)
    report["decoder_path"] = str(core_path.resolve())

    def pairs(value: Pairs) -> list[list[str]]:
        return [[identity(value.items[2*i]), identity(value.items[2*i+1])]
                for i in range(value.count)]

    records = (report["parse_candidates"] + report.get("source_parse_samples", [])
               + report.get("cue_structure_samples", []))
    for candidate in records:
        flat = candidate["constituent_ids"]
        candidate["complete_native_decode"] = False
        if not candidate["canonical_identity"] or len(flat) != candidate["n_constituents"]:
            candidate["decode_status"] = "identity-or-count-mismatch"
            continue
        data = (Id * len(flat)).from_buffer_copy(b"".join(bytes.fromhex(x) for x in flat))
        decoded = Parse()
        status = core.laplace_ud_parse_decode(data, len(flat), C.byref(decoded))
        candidate["decode_status"] = status
        if status != 0:
            continue
        try:
            candidate["ud"] = {
                "sentence_id": identity(decoded.sentence_id),
                "language_id": identity(decoded.language_id),
                "tokens": [{
                    **{name: identity(getattr(token, name)) for name in
                       ("ref_id", "form_id", "lemma_id", "upos_id", "xpos_id", "head_ref_id", "deprel_id")},
                    **{name: pairs(getattr(token, name)) for name in ("features", "enhanced", "misc")},
                } for token in decoded.tokens[:decoded.token_count]],
                "multiword_tokens": [{
                    **{name: identity(getattr(token, name)) for name in
                       ("start_ref_id", "end_ref_id", "form_id")}, "misc": pairs(token.misc),
                } for token in decoded.mwts[:decoded.mwt_count]],
            }
            candidate["complete_native_decode"] = True
        finally:
            core.laplace_ud_parse_free(C.byref(decoded))


def text_sql(value: str) -> str:
    return "convert_from(decode('" + value.encode("utf-8").hex() + "','hex'),'UTF8')"


def relation_readback_ctes(relations: list[str], witnesses: int) -> str:
    """Fixed, bounded diagnostic frontiers; no relation-specific execution rule."""
    values = ",".join("(" + text_sql(x) + ")" for x in relations)
    return f"""
relation_requests(name) AS (VALUES {values}),
relation_ids AS MATERIALIZED (
 SELECT DISTINCT laplace.relation_type_id(name) AS id FROM relation_requests
),
lookup_senses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT has_sense FROM roster)
   AND a.source_id=(SELECT wordnet_source FROM roster)
   AND a.subject_id=ANY(ARRAY(SELECT id FROM cue_ids UNION SELECT id FROM operand_ids))
 ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
),
lookup_synsets AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT sense_of FROM roster)
   AND a.source_id=(SELECT wordnet_source FROM roster)
   AND a.subject_id=ANY(ARRAY(SELECT object_id FROM lookup_senses))
 ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
),
relation_subjects AS MATERIALIZED (
 SELECT id FROM cue_ids UNION SELECT id FROM operand_ids
 UNION SELECT object_id FROM lookup_senses UNION SELECT object_id FROM lookup_synsets
 UNION SELECT entity_id FROM selected UNION SELECT subject_id FROM parse_witnesses
),
relation_witnesses AS MATERIALIZED (
 SELECT a.* FROM relation_ids r CROSS JOIN LATERAL (
   SELECT a.* FROM laplace.attestations a
   WHERE a.type_id=r.id AND a.subject_id=ANY(ARRAY(SELECT id FROM relation_subjects))
   ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
 ) a
),
relation_inverse_witnesses AS MATERIALIZED (
 SELECT a.* FROM relation_ids r CROSS JOIN LATERAL (
   SELECT a.* FROM laplace.attestations a
   WHERE a.type_id=r.id AND a.object_id=ANY(ARRAY(SELECT id FROM relation_subjects))
   ORDER BY a.object_id,a.subject_id,a.id LIMIT {witnesses+1}
 ) a
),
relation_followup_subjects AS MATERIALIZED (
 SELECT object_id AS id FROM relation_witnesses
 UNION SELECT subject_id FROM relation_inverse_witnesses
 EXCEPT SELECT id FROM relation_subjects
),
relation_followup_witnesses AS MATERIALIZED (
 SELECT a.* FROM relation_ids r CROSS JOIN LATERAL (
   SELECT a.* FROM laplace.attestations a
   WHERE a.type_id=r.id AND a.subject_id=ANY(ARRAY(SELECT id FROM relation_followup_subjects))
   ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
 ) a
),
relation_followup_inverse_witnesses AS MATERIALIZED (
 SELECT a.* FROM relation_ids r CROSS JOIN LATERAL (
   SELECT a.* FROM laplace.attestations a
   WHERE a.type_id=r.id AND a.object_id=ANY(ARRAY(SELECT id FROM relation_followup_subjects))
   ORDER BY a.object_id,a.subject_id,a.id LIMIT {witnesses+1}
 ) a
),
relation_targets AS MATERIALIZED (
 SELECT object_id AS id FROM relation_witnesses
 UNION SELECT object_id FROM relation_followup_witnesses
 UNION SELECT object_id FROM relation_inverse_witnesses
 UNION SELECT object_id FROM relation_followup_inverse_witnesses
),
target_sense_witnesses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT sense_of FROM roster)
   AND a.object_id=ANY(ARRAY(SELECT id FROM relation_targets))
 ORDER BY a.object_id,a.subject_id,a.id LIMIT {witnesses+1}
),
target_lemma_witnesses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT has_sense FROM roster)
   AND a.object_id=ANY(ARRAY(SELECT subject_id FROM target_sense_witnesses))
 ORDER BY a.object_id,a.subject_id,a.id LIMIT {witnesses+1}
),
relation_limits AS MATERIALIZED (
 SELECT encode(r.id,'hex') AS relation_id,
   (SELECT count(*)>{witnesses} FROM relation_witnesses a WHERE a.type_id=r.id) AS witness_overflow,
   (SELECT count(*)>{witnesses} FROM relation_inverse_witnesses a WHERE a.type_id=r.id) AS inverse_witness_overflow,
   (SELECT count(*)>{witnesses} FROM relation_followup_witnesses a WHERE a.type_id=r.id) AS followup_witness_overflow,
   (SELECT count(*)>{witnesses} FROM relation_followup_inverse_witnesses a WHERE a.type_id=r.id) AS followup_inverse_witness_overflow
 FROM relation_ids r
),
"""


def readback_sql(cues: list[str], operands: list[str], candidates: int,
                 constituents: int, witnesses: int, timeout: int,
                 relations: list[str] | None = None) -> str:
    cue_values = ",".join("(" + text_sql(x) + ")" for x in cues)
    operand_values = ",".join("(" + text_sql(x) + ")" for x in operands)
    relation_ctes = relation_readback_ctes(relations, witnesses) if relations else ""
    relation_evidence = """
 UNION ALL SELECT 'lookup_has_sense',a.* FROM lookup_senses a
 UNION ALL SELECT 'lookup_is_sense_of',a.* FROM lookup_synsets a
 UNION ALL SELECT 'requested_relation',a.* FROM relation_witnesses a
 UNION ALL SELECT 'requested_relation_inverse',a.* FROM relation_inverse_witnesses a
 UNION ALL SELECT 'requested_relation_followup',a.* FROM relation_followup_witnesses a
 UNION ALL SELECT 'requested_relation_followup_inverse',a.* FROM relation_followup_inverse_witnesses a
 UNION ALL SELECT 'target_inverse_is_sense_of',a.* FROM target_sense_witnesses a
 UNION ALL SELECT 'target_inverse_has_sense',a.* FROM target_lemma_witnesses a
""" if relations else ""
    relation_scope = f"""
   'relation_requests',(SELECT jsonb_agg(jsonb_build_object('name',name,
      'id',encode(laplace.relation_type_id(name),'hex')) ORDER BY name) FROM relation_requests),
   'relation_subject_scope','cue/operand roots, observed WordNet senses/synsets, selected parses and their witnessed sentences',
   'relation_directions','outgoing and incoming; witness subject/object direction retained',
   'relation_followup_hops',1,'target_label_inverse_hops',2,
   'relation_witness_limit_per_frontier_per_relation_per_direction',{witnesses},'render_limit_characters',1024,
   'label_renderer','realize.label_batch; presentation only, raw witnesses retained separately',
   'text_renderer','realize.batch with default NULL language; actual forward text realization ladder',
   'relation_witness_order','subject_id,attestation_id; all sources, contexts and outcomes',
   'relation_inverse_witness_order','object_id,subject_id,attestation_id; all sources, contexts and outcomes',
   'target_label_witness_order','object_id,subject_id,attestation_id; all sources, contexts and outcomes',
""" if relations else ""
    relation_overflows = f"""
   'lookup_has_sense_witness_overflow',(SELECT count(*)>{witnesses} FROM lookup_senses),
   'lookup_is_sense_of_witness_overflow',(SELECT count(*)>{witnesses} FROM lookup_synsets),
   'requested_relations',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.relation_id) FROM relation_limits r),
   'target_inverse_is_sense_of_witness_overflow',(SELECT count(*)>{witnesses} FROM target_sense_witnesses),
   'target_inverse_has_sense_witness_overflow',(SELECT count(*)>{witnesses} FROM target_lemma_witnesses),
""" if relations else ""
    relation_ids = " UNION SELECT id FROM relation_ids\n" if relations else ""
    labels = ",realize.label_batch(ids) AS labels,realize.batch(ids) AS realized_texts" if relations else ""
    surface = "left(r.surfaces[u.ord],1024)" if relations else "r.surfaces[u.ord]"
    surface_metadata = """,octet_length(r.surfaces[u.ord]) AS native_surface_bytes,
        char_length(r.surfaces[u.ord])>1024 AS surface_truncated,
        left(r.labels[u.ord],1024) AS label,
        octet_length(r.labels[u.ord]) AS native_label_bytes,
        char_length(r.labels[u.ord])>1024 AS label_truncated,
        left(r.realized_texts[u.ord],1024) AS realized_text,
        octet_length(r.realized_texts[u.ord]) AS native_realized_text_bytes,
        char_length(r.realized_texts[u.ord])>1024 AS realized_text_truncated""" if relations else ""
    # All bounds are explicit diagnostic envelopes. The extra row reports
    # incomplete enumeration; it is never silently treated as complete evidence.
    return f"""
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL client_encoding='UTF8';
SET LOCAL statement_timeout='{timeout}s';
SET LOCAL lock_timeout='5s';
SET LOCAL idle_in_transaction_session_timeout='60s';
WITH
cues(surface) AS (VALUES {cue_values}),
operands(surface) AS (VALUES {operand_values}),
cue_ids AS MATERIALIZED (SELECT surface,laplace.word_id(surface) AS id FROM cues),
operand_ids AS MATERIALIZED (SELECT surface,laplace.word_id(surface) AS id FROM operands),
roster AS MATERIALIZED (
 SELECT laplace.relation_type_id('HAS_PARSE') AS has_parse,
        laplace.relation_type_id('HAS_SENSE') AS has_sense,
        laplace.relation_type_id('IS_SENSE_OF') AS sense_of,
        laplace.relation_type_id('HAS_DEFINITION') AS definition,
        realize.canonical_id('ud/parse/schema/v1') AS ud_schema,
        laplace.source_id('UDDecomposer') AS ud_source,
        laplace.source_id('WordNetDecomposer') AS wordnet_source
),
nominated AS MATERIALIZED (
 SELECT p.id,p.entity_id,p.n_constituents,p.trajectory
 FROM laplace.physicalities p
 WHERE p.type=8 AND p.trajectory IS NOT NULL
   AND public.laplace_trajectory_constituent_ids(p.trajectory)
       @> ARRAY[realize.canonical_id('ud/parse/schema/v1')]
   AND public.laplace_trajectory_constituent_ids(p.trajectory)
       && ARRAY(SELECT id FROM cue_ids)
 ORDER BY p.n_constituents,p.entity_id,p.id LIMIT {candidates+1}
),
cue_selected AS MATERIALIZED (
 SELECT * FROM nominated ORDER BY n_constituents,entity_id,id LIMIT {candidates}
),
source_sample_witnesses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT has_parse FROM roster) AND a.source_id=(SELECT ud_source FROM roster)
 LIMIT 5
),
source_sample_ids AS MATERIALIZED (
 SELECT DISTINCT public.laplace_hash128_blake3(object_id||decode('0800','hex')) AS id
 FROM (SELECT object_id FROM source_sample_witnesses LIMIT 4) a
),
cue_sample_nominated AS MATERIALIZED (
 SELECT p.id FROM laplace.physicalities p WHERE p.type=8 AND p.trajectory IS NOT NULL
   AND public.laplace_trajectory_constituent_ids(p.trajectory) && ARRAY(SELECT id FROM cue_ids)
 ORDER BY p.n_constituents,p.entity_id,p.id LIMIT 5
),
cue_sample_ids AS MATERIALIZED (SELECT id FROM cue_sample_nominated LIMIT 4),
selected AS MATERIALIZED (
 SELECT p.id,p.entity_id,p.n_constituents,p.trajectory,p.coord,p.hilbert_index
 FROM laplace.physicalities p WHERE p.type=8 AND p.id=ANY(ARRAY(
   SELECT id FROM cue_selected UNION SELECT id FROM source_sample_ids UNION SELECT id FROM cue_sample_ids))
),
packed_sizes AS MATERIALIZED (
 SELECT p.id,sum(u.run_length)::bigint AS logical_count
 FROM selected p CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) u
 WHERE p.n_constituents BETWEEN 1 AND {constituents}
   AND public.st_npoints(p.trajectory) BETWEEN 1 AND {constituents}
 GROUP BY p.id
),
expanded AS MATERIALIZED (
 SELECT p.id,p.entity_id,p.n_constituents,
        array_agg(u.entity_id ORDER BY u.ordinal) AS ids
 FROM selected p JOIN packed_sizes z ON z.id=p.id CROSS JOIN LATERAL
   public.laplace_trajectory_expanded_constituents(p.trajectory) u
 WHERE z.logical_count=p.n_constituents AND z.logical_count BETWEEN 1 AND {constituents}
 GROUP BY p.id,p.entity_id,p.n_constituents
),
structure_records AS MATERIALIZED (
 SELECT p.id,jsonb_build_object(
    'parse_id',encode(p.entity_id,'hex'),'physicality_id',encode(p.id,'hex'),
    'physicality_type',8,'n_constituents',p.n_constituents,
    'coordinate_ewkb_hex',encode(public.st_asewkb(p.coord),'hex'),
    'hilbert_index',encode(p.hilbert_index,'hex'),
    'trajectory_ewkb_hex',encode(public.st_asewkb(p.trajectory),'hex'),
    'canonical_identity',p.entity_id=public.laplace_hash128_merkle(4::smallint,e.ids)
       AND p.id=public.laplace_hash128_blake3(p.entity_id||decode('0800','hex')),
    'constituent_ids',(SELECT jsonb_agg(encode(x,'hex')) FROM unnest(e.ids) x)) AS record
 FROM selected p JOIN expanded e ON e.id=p.id
),
parse_witnesses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT has_parse FROM roster)
   AND a.object_id=ANY(ARRAY(SELECT entity_id FROM selected))
 ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
),
senses AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT has_sense FROM roster)
   AND a.source_id=(SELECT wordnet_source FROM roster)
   AND a.subject_id=ANY(ARRAY(SELECT id FROM operand_ids))
 ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
),
synsets AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT sense_of FROM roster)
   AND a.source_id=(SELECT wordnet_source FROM roster)
   AND a.subject_id=ANY(ARRAY(SELECT object_id FROM senses))
 ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
),
definitions AS MATERIALIZED (
 SELECT a.* FROM laplace.attestations a
 WHERE a.type_id=(SELECT definition FROM roster)
   AND a.source_id=(SELECT wordnet_source FROM roster)
   AND a.subject_id=ANY(ARRAY(SELECT object_id FROM synsets))
 ORDER BY a.subject_id,a.id LIMIT {witnesses+1}
),
{relation_ctes}evidence AS MATERIALIZED (
 SELECT 'has_parse'::text AS route,a.* FROM parse_witnesses a
 UNION ALL SELECT 'has_sense',a.* FROM senses a
 UNION ALL SELECT 'is_sense_of',a.* FROM synsets a
 UNION ALL SELECT 'has_definition',a.* FROM definitions a
 {relation_evidence}
),
needed_ids AS MATERIALIZED (
 SELECT unnest(ids) AS id FROM expanded
 UNION SELECT id FROM cue_ids UNION SELECT id FROM operand_ids
 UNION SELECT subject_id FROM evidence UNION SELECT object_id FROM evidence
 UNION SELECT source_id FROM evidence UNION SELECT context_id FROM evidence
 UNION SELECT type_id FROM evidence
 {relation_ids}
),
render_input AS MATERIALIZED (
 SELECT array_agg(id ORDER BY id) AS ids FROM needed_ids WHERE id IS NOT NULL
),
rendered AS MATERIALIZED (
 SELECT ids,realize.render_text_batch(ids) AS surfaces{labels} FROM render_input
),
entity_rows AS MATERIALIZED (
 SELECT e.id,jsonb_agg(jsonb_build_object('tier',e.tier,'type_id',encode(e.type_id,'hex'),
        'type_canonical_name',n.name,
        'first_observed_by',encode(e.first_observed_by,'hex')) ORDER BY e.tier) AS rows
 FROM laplace.entities e LEFT JOIN laplace.canonical_names n ON n.id=e.type_id
 WHERE e.id=ANY(ARRAY(SELECT id FROM needed_ids WHERE id IS NOT NULL))
 GROUP BY e.id
),
dictionary AS MATERIALIZED (
 SELECT encode(u.id,'hex') AS id,{surface} AS surface,n.name AS canonical_name,
        e.rows AS entity_rows{surface_metadata}
 FROM rendered r CROSS JOIN LATERAL unnest(r.ids) WITH ORDINALITY u(id,ord)
 LEFT JOIN laplace.canonical_names n ON n.id=u.id
 LEFT JOIN entity_rows e ON e.id=u.id
),
evidence_standing AS MATERIALIZED (
 SELECT a.*,CASE WHEN c.id IS NOT NULL THEN jsonb_build_object(
      'id',encode(c.id,'hex'),'rating',c.rating,'rd',c.rd,'volatility',c.volatility,
      'witness_count',c.witness_count) END AS pooled_consensus
 FROM evidence a LEFT JOIN laplace.consensus c
 ON c.subject_id=a.subject_id AND c.type_id=a.type_id AND c.object_id=a.object_id
)
SELECT jsonb_build_object(
 'schema','laplace.operational-source-readback/v1',
 'database',current_database(),'observed_at',transaction_timestamp(),
 'snapshot',pg_current_snapshot()::text,'transaction_read_only',current_setting('transaction_read_only'),
 'server_version',current_setting('server_version'),
 'extensions',(SELECT jsonb_object_agg(extname,extversion) FROM pg_extension
               WHERE extname IN ('laplace_substrate','laplace_geom')),
 'scope',jsonb_build_object('cues',(SELECT jsonb_agg(surface) FROM cues),
   'operand_probes',(SELECT jsonb_agg(surface) FROM operands),
   {relation_scope}
   'candidate_order','n_constituents,entity_id,physicality_id',
   'candidate_limit',{candidates},'constituent_limit',{constituents},'witness_limit_per_route',{witnesses}),
 'roster',(SELECT jsonb_build_object('has_parse',encode(has_parse,'hex'),
    'has_sense',encode(has_sense,'hex'),'is_sense_of',encode(sense_of,'hex'),
    'has_definition',encode(definition,'hex'),'ud_source',encode(ud_source,'hex'),
    'wordnet_source',encode(wordnet_source,'hex'),'ud_schema',encode(ud_schema,'hex')) FROM roster),
 'source_diagnostics',jsonb_build_object(
    'ud_schema_type8_present',EXISTS(SELECT 1 FROM laplace.physicalities p
      WHERE p.type=8 AND p.trajectory IS NOT NULL
        AND public.laplace_trajectory_constituent_ids(p.trajectory) @> ARRAY[(SELECT ud_schema FROM roster)]),
    'ud_has_parse_sample_more',(SELECT count(*)>4 FROM source_sample_witnesses),
    'source_sample_order','first indexed source/relation witnesses; diagnostic sample, not exhaustive',
    'ud_has_parse_samples',COALESCE((SELECT jsonb_agg(jsonb_build_object(
      'id',encode(a.id,'hex'),'subject_id',encode(a.subject_id,'hex'),
      'object_id',encode(a.object_id,'hex'),'type_id',encode(a.type_id,'hex'),
      'source_id',encode(a.source_id,'hex'),'context_id',encode(a.context_id,'hex'),
      'outcome',a.outcome,'observation_count',a.observation_count,
      'last_observed_at',a.last_observed_at,'sum_score_fp1e9',a.sum_score_fp1e9,
      'opponent_rd_fp1e9',a.opponent_rd_fp1e9,'opponent_rating_fp1e9',a.opponent_rating_fp1e9,
      'fold_replayable',a.fold_replayable,
      'canonical_type8_present',EXISTS(SELECT 1 FROM selected p WHERE p.id=
        public.laplace_hash128_blake3(a.object_id||decode('0800','hex')))))
      FROM (SELECT * FROM source_sample_witnesses LIMIT 4) a),'[]'),
    'cue_type8_sample_more',(SELECT count(*)>4 FROM cue_sample_nominated)),
 'limits',jsonb_build_object('candidate_overflow',(SELECT count(*)>{candidates} FROM nominated),
   {relation_overflows}
   'excluded_parse_records',COALESCE((SELECT jsonb_agg(jsonb_build_object(
       'parse_id',encode(p.entity_id,'hex'),'physicality_id',encode(p.id,'hex'),
       'declared_count',p.n_constituents,'packed_vertices',public.st_npoints(p.trajectory),
       'native_logical_count',z.logical_count)) FROM selected p
       LEFT JOIN packed_sizes z ON z.id=p.id
       WHERE z.logical_count IS NULL OR z.logical_count<>p.n_constituents
          OR z.logical_count NOT BETWEEN 1 AND {constituents}),'[]'),
   'has_parse_witness_overflow',(SELECT count(*)>{witnesses} FROM parse_witnesses),
   'has_sense_witness_overflow',(SELECT count(*)>{witnesses} FROM senses),
   'is_sense_of_witness_overflow',(SELECT count(*)>{witnesses} FROM synsets),
   'has_definition_witness_overflow',(SELECT count(*)>{witnesses} FROM definitions)),
 'parse_candidates',COALESCE((SELECT jsonb_agg(r.record ORDER BY r.id)
    FROM structure_records r WHERE r.id IN (SELECT id FROM cue_selected)),'[]'),
 'source_parse_samples',COALESCE((SELECT jsonb_agg(r.record ORDER BY r.id)
    FROM structure_records r WHERE r.id IN (SELECT id FROM source_sample_ids)),'[]'),
 'cue_structure_samples',COALESCE((SELECT jsonb_agg(r.record ORDER BY r.id)
    FROM structure_records r WHERE r.id IN (SELECT id FROM cue_sample_ids)),'[]'),
 'witnesses',COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'route',a.route,'id',encode(a.id,'hex'),'subject_id',encode(a.subject_id,'hex'),
    'type_id',encode(a.type_id,'hex'),'object_id',encode(a.object_id,'hex'),
    'source_id',encode(a.source_id,'hex'),'context_id',encode(a.context_id,'hex'),
    'outcome',a.outcome,'observation_count',a.observation_count,'last_observed_at',a.last_observed_at,
    'sum_score_fp1e9',a.sum_score_fp1e9,'opponent_rd_fp1e9',a.opponent_rd_fp1e9,
    'opponent_rating_fp1e9',a.opponent_rating_fp1e9,'fold_replayable',a.fold_replayable,
    'pooled_consensus',a.pooled_consensus) ORDER BY a.route,a.subject_id,a.id)
    FROM evidence_standing a),'[]'),
 'dictionary',COALESCE((SELECT jsonb_agg(to_jsonb(d) ORDER BY d.id) FROM dictionary d),'[]')
)::text;
ROLLBACK;
"""


def bounded_query(database: str, sql_path: Path, timeout: int, max_bytes: int) -> bytes:
    argv = ["psql", "-X", "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
            "-p", os.environ.get("PGPORT", "5432"), "-U", os.environ.get("PGUSER", "laplace_admin"),
            "-d", database, "-v", "ON_ERROR_STOP=1", "-A", "-t", "-q", "-f", str(sql_path)]
    process = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    output, error = bytearray(), bytearray()
    deadline = time.monotonic() + timeout + 10
    try:
        with selectors.DefaultSelector() as selector:
            for stream, tag in ((process.stdout, "output"), (process.stderr, "error")):
                os.set_blocking(stream.fileno(), False)
                selector.register(stream, selectors.EVENT_READ, tag)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise RuntimeError("readback process deadline exceeded")
                for key, _ in selector.select(min(remaining, 1.0)):
                    chunk = os.read(key.fd, 65536)
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    target, bound = (output, max_bytes) if key.data == "output" else (error, 1 << 20)
                    if len(target) + len(chunk) > bound:
                        raise RuntimeError(f"readback {key.data} exceeded declared byte envelope {bound}")
                    target.extend(chunk)
        process.wait(timeout=max(0.01, deadline - time.monotonic()))
        if process.returncode:
            raise RuntimeError(f"read-only source query failed ({process.returncode}): "
                               + error.decode("utf-8", errors="replace"))
        return bytes(output)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        process.stdout.close()
        process.stderr.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database")
    parser.add_argument("--cue", action="append", help="exact source lookup surface; repeatable")
    parser.add_argument("--operand", action="append", help="exact WordNet surface probe; repeatable")
    parser.add_argument("--relation", action="append",
                        help="exact native relation-name lookup; repeatable, read-only evidence in both directions with one follow-up frontier")
    parser.add_argument("--candidate-limit", type=int, default=16)
    parser.add_argument("--constituent-limit", type=int, default=2048)
    parser.add_argument("--witness-limit", type=int, default=512)
    parser.add_argument("--timeout-seconds", type=int, default=180)
    parser.add_argument("--max-output-bytes", type=int, default=16 << 20)
    parser.add_argument("--core", type=Path, default=Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace")) / "lib/liblaplace_core.so")
    parser.add_argument("--receipt", type=Path, required=True)
    args = parser.parse_args()
    for name in ("candidate_limit", "constituent_limit", "witness_limit", "timeout_seconds", "max_output_bytes"):
        if getattr(args, name) < 1:
            parser.error(name.replace("_", "-") + " must be positive")
    cues, operands = args.cue or ["define", "Define"], args.operand or ["justice", "glacier", "whale"]
    if len(cues) > 16 or len(operands) > 16 or any(len(x.encode("utf-8")) > 1024 for x in cues + operands):
        parser.error("at most 16 cues/operands of at most 1024 UTF-8 bytes each")
    relations = args.relation or []
    if len(relations) > 16 or any(not x or len(x.encode("utf-8")) > 1024 for x in relations):
        parser.error("at most 16 nonempty relation names of at most 1024 UTF-8 bytes each")
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    sql = readback_sql(cues, operands, args.candidate_limit, args.constituent_limit,
                       args.witness_limit, args.timeout_seconds, relations)
    sql_path = args.receipt.with_suffix(".sql")
    sql_path.write_text(sql, encoding="utf-8")
    start = time.monotonic_ns()
    report = {"schema": "laplace.operational-source-readback/v1", "disposition": "failed",
              "sql_sha256": hashlib.sha256(sql.encode("utf-8")).hexdigest(),
              "max_output_bytes": args.max_output_bytes, "timeout_seconds": args.timeout_seconds}
    try:
        raw = bounded_query(args.database, sql_path, args.timeout_seconds, args.max_output_bytes)
        report.update(json.loads(raw.decode("utf-8")))
        if report.get("transaction_read_only") != "on":
            raise RuntimeError("database did not confirm read-only transaction")
        report["raw_result_bytes"] = len(raw)
        decode_candidates(report, args.core)
        report["disposition"] = "readback-only-no-declaration"
        return_code = 0
    except (OSError, ValueError, RuntimeError, AttributeError, subprocess.SubprocessError) as exc:
        report["error"] = str(exc)
        return_code = 1
    report["wall_milliseconds"] = (time.monotonic_ns() - start) // 1_000_000
    args.receipt.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("schema", "disposition", "wall_milliseconds", "limits", "error") if k in report}, ensure_ascii=False))
    print(f"SOURCE_READBACK_RECEIPT={args.receipt}")
    return return_code


if __name__ == "__main__":
    raise SystemExit(main())
