#!/usr/bin/env python3
"""Repair retained legacy handle/line physicalities from exact substrate evidence.

Run inside the installed product's service-quiescence and exclusive measurement
lane. All identity, trajectory and geometry calculations use installed native SQL
functions. Original rows, proposed rows and independent lineage are fsynced before
the one transaction can mutate anything. Unknown earlier submissions are compared
to their retained original/proposed bytes under the same database locks.
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
import time
import uuid

from lib.repair_transaction import preserve_and_apply, write_new_json


ROOT = Path(__file__).resolve().parents[1]
MAX_ROWS = 25000
MAX_CONSTITUENTS = 4096
MAX_BYTES = 512 * 1024 * 1024


def snapshot_expression(alias: str) -> str:
    if not re.fullmatch(r"[a-z_]+", alias):
        raise ValueError("snapshot alias must be a fixed SQL identifier")
    p = alias
    return f"""jsonb_build_object(
      'id',encode({p}.id,'hex'),'entity_id',encode({p}.entity_id,'hex'),'type',{p}.type,
      'coord_ewkb',encode(ST_AsEWKB({p}.coord),'hex'),
      'hilbert_index',encode({p}.hilbert_index,'hex'),
      'trajectory_ewkb',encode(ST_AsEWKB({p}.trajectory),'hex'),
      'radius_origin_bits',encode(float8send({p}.radius_origin),'hex'),
      'n_constituents',{p}.n_constituents,
      'alignment_residual_bits',encode(float8send({p}.alignment_residual),'hex'),
      'source_dim',{p}.source_dim,
      'observed_at',{p}.observed_at,
      'observed_at_binary',encode(timestamptz_send({p}.observed_at),'hex'))"""


def verified_plan(directory: Path) -> tuple[dict, dict]:
    manifest = json.loads((directory / "manifest.json").read_text())
    if manifest.get("schema") != "laplace.legacy-content-repair-plan/v1":
        raise ValueError("unknown prior repair receipt schema")
    size = 0
    digest = hashlib.sha256()
    with (directory / "plan.jsonl").open("rb") as source:
        first = source.readline(MAX_BYTES + 1)
        size += len(first)
        digest.update(first)
        context = json.loads(first)
        while chunk := source.read(1024 * 1024):
            size += len(chunk)
            if size > MAX_BYTES:
                raise ValueError("prior repair receipt exceeds byte bound")
            digest.update(chunk)
    if size != manifest.get("plan_bytes") or digest.hexdigest() != manifest.get("plan_sha256"):
        raise ValueError("prior repair evidence changed")
    if not isinstance(context, dict) or context.get("kind") != "context":
        raise ValueError("prior repair context is missing")
    return manifest, context


def confirmed_outcome(directory: Path, manifest: dict) -> bool:
    try:
        outcome = json.loads((directory / "outcome.json").read_text())
    except (OSError, ValueError):
        return False
    return isinstance(outcome, dict) and outcome.get("disposition") == "commit-confirmed" \
        and outcome.get("plan_sha256") == manifest["plan_sha256"] \
        and outcome.get("planned_rows") == manifest["planned_rows"] \
        and isinstance(outcome.get("applied"), dict) \
        and outcome["applied"].get("count") == manifest["planned_rows"]


def verified_reconciliation(directory: Path, manifest: dict, root: Path) -> bool:
    path = directory / "reconciliation.json"
    if not path.exists():
        return False
    reference = json.loads(path.read_text())
    target = Path(reference["reconciliation_directory"])
    if target.resolve().parent != root.resolve() or target.resolve() == directory.resolve() \
            or reference.get("original_plan_sha256") != manifest["plan_sha256"]:
        raise ValueError("prior reconciliation reference does not identify this receipt estate")
    current_manifest, current_context = verified_plan(target)
    if not confirmed_outcome(target, current_manifest) \
            or current_manifest["plan_sha256"] != reference.get("reconciliation_plan_sha256"):
        raise ValueError("prior reconciliation has no matching successful durable outcome")
    return any(item.get("receipt") == str((directory / "plan.jsonl").resolve()) and
               item.get("disposition") in ("originals-confirmed", "prior-commit-confirmed", "zero-row-no-mutation")
               for item in current_context.get("prior_submission_reconciliation", []))


def unresolved_submissions(root: Path) -> list[Path]:
    pending = []
    for directory in sorted(root.iterdir()):
        if not directory.is_dir() or not (directory / "submission.json").exists():
            continue
        if directory.is_symlink():
            raise ValueError("repair receipt directories cannot be symlinks")
        manifest, _ = verified_plan(directory)
        if confirmed_outcome(directory, manifest) or verified_reconciliation(directory, manifest, root):
            continue
        pending.append(directory / "plan.jsonl")
    if len(pending) > 32:
        raise ValueError("too many unresolved submissions for one repair transaction")
    return pending


def close_reconciled_submissions(root: Path, current: Path) -> None:
    manifest, context = verified_plan(current)
    if not confirmed_outcome(current, manifest):
        raise ValueError("cannot close prior submissions without a successful durable reconciliation")
    for item in context.get("prior_submission_reconciliation", []):
        directory = Path(item["receipt"]).parent
        if directory.resolve().parent != root.resolve() or directory.resolve() == current.resolve():
            raise ValueError("reconciliation refers outside its receipt estate")
        old_manifest, _ = verified_plan(directory)
        reference = {"schema": "laplace.legacy-content-repair-reconciliation/v1",
                     "original_plan_sha256": old_manifest["plan_sha256"],
                     "reconciliation_directory": str(current.resolve()),
                     "reconciliation_plan_sha256": manifest["plan_sha256"],
                     "disposition": item["disposition"]}
        write_new_json(directory / "reconciliation.json", reference)


def prior_sql(paths: list[Path]) -> str:
    statements = ["""CREATE TEMP TABLE repair_prior(document jsonb, receipt text) ON COMMIT DROP;
CREATE TEMP TABLE repair_prior_input(document jsonb) ON COMMIT DROP;"""]
    for path in paths:
        # psql's COPY path is a local filesystem operand, not SQL from a user.
        # Keep its accepted alphabet narrower than either parser's quoting rules.
        name = str(path.resolve())
        if not re.fullmatch(r"/[A-Za-z0-9/_.-]+", name):
            raise ValueError("prior receipt path is not a supported absolute path")
        statements += [f"\\copy repair_prior_input(document) FROM '{name}' WITH (FORMAT csv, DELIMITER E'\\x01', QUOTE E'\\x02')",
                       f"INSERT INTO repair_prior SELECT document,'{name}' FROM repair_prior_input;",
                       "TRUNCATE repair_prior_input;"]
    statements.append("""
CREATE TEMP TABLE repair_prior_summary ON COMMIT DROP AS
SELECT receipt,count(*) FILTER(WHERE document->>'kind'='context') AS contexts,
  count(*) FILTER(WHERE document->>'kind'='plan') AS plans,
  count(*) FILTER(WHERE document->>'kind'='physicality') AS physicalities,
  count(*) FILTER(WHERE document->>'kind'='native-input') AS native_inputs,
  (jsonb_agg(document) FILTER(WHERE document->>'kind'='context'))->0 AS context,
  (jsonb_agg(document) FILTER(WHERE document->>'kind'='plan'))->0 AS plan
FROM repair_prior GROUP BY receipt;
DO $prior_contract$
BEGIN
  IF EXISTS(SELECT FROM repair_prior_summary s WHERE s.contexts<>1 OR s.plans<>1
      OR (s.plan->>'count')::bigint IS DISTINCT FROM s.physicalities
      OR (s.plan->>'native_input_count')::bigint IS DISTINCT FROM s.native_inputs
      OR (s.plan->>'unresolved')::bigint IS DISTINCT FROM 0
      OR s.context->>'database' IS DISTINCT FROM current_database()
      OR s.context->>'database_oid' IS DISTINCT FROM (SELECT oid::text FROM pg_database WHERE datname=current_database())
      OR s.context->>'system_identifier' IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())) THEN
    RAISE EXCEPTION 'Prior repair receipt contract or database identity does not match';
  END IF;
END $prior_contract$;
CREATE TEMP TABLE repair_reconciliation ON COMMIT DROP AS
WITH compared AS (
  SELECT r.receipt,r.document,
         pg_temp.repair_snapshot(oldrow)=r.document->'original'
           AND (oldrow.id=newrow.id OR newrow.id IS NULL) AS matches_original,
         pg_temp.repair_snapshot(newrow)=r.document->'proposed'
           AND (oldrow.id=newrow.id OR oldrow.id IS NULL) AS matches_proposed
  FROM repair_prior r
  LEFT JOIN laplace.physicalities oldrow ON oldrow.id=decode(r.document->'original'->>'id','hex')
  LEFT JOIN laplace.physicalities newrow ON newrow.id=decode(r.document->'proposed'->>'id','hex')
  WHERE r.document->>'kind'='physicality'
)
SELECT receipt,count(*) AS rows,
       CASE WHEN bool_and(COALESCE(matches_original,false)) THEN 'originals-confirmed'
            WHEN bool_and(COALESCE(matches_proposed,false)) THEN 'prior-commit-confirmed'
            ELSE 'diverged-or-partial' END AS disposition
FROM compared GROUP BY receipt
UNION ALL
SELECT receipt,0,'zero-row-no-mutation' FROM repair_prior_summary WHERE physicalities=0;
DO $reconcile$
BEGIN
  IF EXISTS (SELECT FROM repair_reconciliation WHERE disposition='diverged-or-partial') THEN
    RAISE EXCEPTION 'Unknown repair submission diverged from its complete original/proposed plan';
  END IF;
END $reconcile$;
""")
    return "\n".join(statements)


def plan_sql(max_rows: int, prior_paths: list[Path], producer_generation: dict | None = None) -> str:
    if not 1 <= max_rows <= 100000:
        raise ValueError("repair row envelope must be between 1 and 100000")
    producer_json = json.dumps(producer_generation, sort_keys=True).replace("'", "''")
    return f"""
SET LOCAL timezone='UTC';
SET LOCAL max_parallel_workers_per_gather=0;
-- Acquire table locks in canonical apply order. These protect absent target IDs
-- as well as retained rows. The outer managed measurement lane excludes full
-- ingest runs; table locks alone cannot clear a writer's remembered presence IDs.
LOCK TABLE laplace.entities,laplace.physicalities,laplace.attestations IN SHARE ROW EXCLUSIVE MODE;
CREATE FUNCTION pg_temp.repair_snapshot(p laplace.physicalities) RETURNS jsonb
LANGUAGE sql IMMUTABLE STRICT AS $snapshot$ SELECT {snapshot_expression('p')} $snapshot$;
{prior_sql(prior_paths)}

CREATE TEMP TABLE repair_owner_inventory ON COMMIT DROP AS
SELECT p.*,e.tier,e.type_id,e.first_observed_by,to_jsonb(e) AS entity_evidence,
       (SELECT count(*) FROM laplace.entities duplicate WHERE duplicate.id=e.id) AS entity_rows,
       CASE e.type_id
         WHEN realize.canonical_id('Chess_Game') THEN 'chess-line'
         WHEN realize.canonical_id('Chess_Player') THEN 'player-projection'
         ELSE 'session-projection' END AS repair_kind
FROM laplace.physicalities p JOIN laplace.entities e ON e.id=p.entity_id
WHERE p.type=1 AND e.type_id IN (realize.canonical_id('Chess_Game'),
    realize.canonical_id('Chess_Player'),realize.canonical_id('Conversation_Session'));
-- The row envelope bounds defects requiring repair, not corpus size. Screen all
-- selected metadata, but expand/hash each carrier only after its packed run sum
-- and declared counts prove the native logical-work envelope.
CREATE TEMP TABLE repair_owner_bounds ON COMMIT DROP AS
SELECT o.id,COALESCE(o.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
  AND ST_NPoints(o.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS}
  AND b.packed_count=ST_NPoints(o.trajectory) AND b.logical_count=o.n_constituents
  AND b.first_ordinal=1 AND b.last_ordinal=o.n_constituents,false) AS bounded
FROM repair_owner_inventory o
CROSS JOIN LATERAL (
  SELECT count(*) AS packed_count,sum(GREATEST(v.run_length,1)) AS logical_count,
    min(v.ordinal) AS first_ordinal,max(v.ordinal+GREATEST(v.run_length,1)-1) AS last_ordinal
  FROM public.laplace_trajectory_constituents(CASE
    WHEN o.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
     AND ST_NPoints(o.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS}
    THEN o.trajectory ELSE NULL END) v
) b;
CREATE TEMP TABLE repair_owner_identity ON COMMIT DROP AS
SELECT o.id,b.bounded AND o.entity_rows=1 AND x.logical_count=o.n_constituents
    AND x.distinct_ordinals=o.n_constituents AND x.resolved AS well_formed,
  CASE WHEN x.logical_count=1 THEN x.child_ids[1]
       WHEN x.logical_count>1 THEN public.laplace_hash128_merkle(0::smallint,x.child_ids)
       ELSE NULL::bytea END AS content_id
FROM repair_owner_inventory o JOIN repair_owner_bounds b ON b.id=o.id
CROSS JOIN LATERAL (
  SELECT count(*) AS logical_count,count(DISTINCT v.ordinal) AS distinct_ordinals,
    array_agg(v.entity_id ORDER BY v.ordinal) AS child_ids,
    bool_and(e.id IS NOT NULL AND p.id IS NOT NULL
      AND p.id=public.laplace_hash128_blake3(v.entity_id||decode('0100','hex'))) AS resolved
  FROM public.laplace_trajectory_expanded_constituents(CASE WHEN b.bounded THEN o.trajectory ELSE NULL END) v
  LEFT JOIN laplace.entities e ON e.id=v.entity_id
  LEFT JOIN laplace.physicalities p ON p.entity_id=v.entity_id AND p.type=1
) x;
CREATE TEMP TABLE repair_owners ON COMMIT DROP AS
SELECT o.* FROM repair_owner_inventory o JOIN repair_owner_identity i ON i.id=o.id
WHERE NOT COALESCE(i.well_formed,false) OR i.content_id IS DISTINCT FROM o.entity_id
ORDER BY o.id LIMIT {max_rows + 1};
DO $bound$
BEGIN
  IF (SELECT count(*) FROM repair_owners)>{max_rows} THEN
    RAISE EXCEPTION 'Legacy physicality repair exceeds its declared owner envelope';
  END IF;
END $bound$;

CREATE TEMP TABLE repair_carriers ON COMMIT DROP AS
SELECT p.*,o.repair_kind,o.tier AS parent_tier
FROM repair_owners o JOIN laplace.physicalities p ON p.entity_id=o.entity_id
WHERE p.type=1 OR (o.repair_kind='chess-line' AND p.type=3);
CREATE TEMP TABLE repair_packed ON COMMIT DROP AS
SELECT p.id AS physicality_id,c.ordinal,c.entity_id,c.run_length,c.flags
FROM repair_carriers p CROSS JOIN LATERAL public.laplace_trajectory_constituents(
  CASE WHEN p.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
         AND ST_NPoints(p.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS}
       THEN p.trajectory ELSE NULL END) c;
CREATE TEMP TABLE repair_bounded ON COMMIT DROP AS
SELECT p.id
FROM repair_carriers p JOIN repair_packed v ON v.physicality_id=p.id
GROUP BY p.id,p.n_constituents,p.trajectory
HAVING count(*)=ST_NPoints(p.trajectory)
   AND sum(GREATEST(v.run_length,1))=p.n_constituents
   AND min(v.ordinal)=1
   AND max(v.ordinal+GREATEST(v.run_length,1)-1)=p.n_constituents;
CREATE TEMP TABLE repair_logical ON COMMIT DROP AS
SELECT p.id AS physicality_id,v.ordinal,v.entity_id,v.flags
FROM repair_bounded b JOIN repair_carriers p ON p.id=b.id
CROSS JOIN LATERAL public.laplace_trajectory_expanded_constituents(p.trajectory) v;
CREATE TEMP TABLE repair_manifests ON COMMIT DROP AS
SELECT c.id,c.entity_id,c.type,c.n_constituents,
       array_agg(v.entity_id ORDER BY v.ordinal) AS child_ids,
       bool_and(v.flags=0) AS zero_flags,
       count(*)=c.n_constituents AND count(DISTINCT v.ordinal)=c.n_constituents
          AND bool_and(e.id IS NOT NULL AND p.id IS NOT NULL
              AND p.id=public.laplace_hash128_blake3(v.entity_id||decode('0100','hex')))
          AS resolved,
       bool_and(e.tier<c.parent_tier) AS descending,
       bool_and(e.type_id=realize.canonical_id('MOVE')) AS all_moves,
       bool_and(e.type_id=realize.canonical_id('Chess_Position')) AS all_positions,
       bool_and(e.type_id=realize.canonical_id('Conversation_Message')) AS all_messages,
       array_agg(p.coord ORDER BY v.ordinal) AS child_coords
FROM repair_carriers c JOIN repair_logical v ON v.physicality_id=c.id
LEFT JOIN laplace.entities e ON e.id=v.entity_id
LEFT JOIN laplace.physicalities p ON p.entity_id=v.entity_id AND p.type=1
GROUP BY c.id,c.entity_id,c.type,c.n_constituents;

CREATE TEMP TABLE repair_candidates ON COMMIT DROP AS
SELECT o.*,m.child_ids,m.zero_flags,m.resolved,m.descending,m.all_moves,m.all_messages,
       m.child_coords,
       p.id AS projection_id,p.n_constituents AS projection_count,
       pm.child_ids AS position_ids,pm.resolved AS positions_resolved,
       pm.all_positions,pm.child_ids[1] AS start_id,
       start_entity.tier AS start_tier,start_physicality.coord AS start_coord,
       pg_temp.repair_snapshot(p) AS projection_evidence,
       to_jsonb(start_entity) AS start_entity_evidence,
       pg_temp.repair_snapshot(start_physicality) AS start_physicality_evidence,
       (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM laplace.attestations a
         WHERE a.subject_id=o.entity_id AND a.type_id=laplace.relation_type_id('HAS_NAME_ALIAS')
           AND a.object_id=m.child_ids[1]) AS name_witnesses,
       (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM laplace.attestations a
         WHERE a.subject_id=o.entity_id AND a.type_id=laplace.relation_type_id('HAS_SETUP')) AS setup_witnesses,
       (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM laplace.attestations a
         WHERE a.subject_id=ANY(m.child_ids) AND a.object_id=o.entity_id
           AND a.context_id=o.entity_id AND a.type_id=laplace.relation_type_id('APPEARS_IN')) AS membership_witnesses,
       (SELECT count(DISTINCT a.subject_id) FROM laplace.attestations a
         WHERE a.subject_id=ANY(m.child_ids) AND a.object_id=o.entity_id
           AND a.context_id=o.entity_id AND a.type_id=laplace.relation_type_id('APPEARS_IN')
           AND a.outcome=2 AND a.observation_count>0
           AND a.sum_score_fp1e9::numeric>a.observation_count::numeric*500000000) AS witnessed_messages,
       EXISTS(SELECT FROM laplace.physicalities occupied
         WHERE (occupied.entity_id=o.entity_id AND occupied.type=3)
            OR occupied.id=public.laplace_hash128_blake3(o.entity_id||decode('0300','hex')))
         AS projection_target_exists,
       (SELECT count(*) FROM laplace.physicalities occupied
         WHERE occupied.entity_id=o.entity_id AND occupied.type=3) AS projection_rows,
       (SELECT jsonb_agg(pg_temp.repair_snapshot(occupied) ORDER BY occupied.id)
        FROM laplace.physicalities occupied
        WHERE (occupied.entity_id=o.entity_id AND occupied.type=3)
           OR occupied.id=public.laplace_hash128_blake3(o.entity_id||decode('0300','hex')))
         AS occupied_projection_evidence
FROM repair_owners o LEFT JOIN repair_manifests m ON m.id=o.id
LEFT JOIN LATERAL (
  SELECT candidate.* FROM laplace.physicalities candidate
  WHERE candidate.entity_id=o.entity_id AND candidate.type=3 AND o.repair_kind='chess-line'
  ORDER BY candidate.id LIMIT 1
) p ON true
LEFT JOIN repair_manifests pm ON pm.id=p.id
LEFT JOIN laplace.entities start_entity ON start_entity.id=pm.child_ids[1]
LEFT JOIN laplace.physicalities start_physicality ON start_physicality.entity_id=pm.child_ids[1]
  AND start_physicality.type=1;

-- Membership uses the existing indexed native trajectory ID extraction. Keep
-- complete incoming row images: changing/removing one realization must not
-- silently invalidate a containing Content realization.
CREATE TEMP TABLE repair_incoming ON COMMIT DROP AS
SELECT p.id,p.entity_id,public.laplace_trajectory_constituent_ids(p.trajectory) AS members,
       pg_temp.repair_snapshot(p) AS original
FROM laplace.physicalities p WHERE p.type=1
  AND public.laplace_trajectory_constituent_ids(p.trajectory)
      && ARRAY(SELECT entity_id FROM repair_owners);

-- One retained set of native coordinate/identity inputs, reused across all
-- affected parents. Occurrence order and duplicates remain in each manifest.
CREATE TEMP TABLE repair_native_inputs ON COMMIT DROP AS
WITH needed AS (
  SELECT v.entity_id FROM repair_carriers carrier
    JOIN repair_logical v ON v.physicality_id=carrier.id
  UNION SELECT start_id FROM repair_candidates WHERE repair_kind='chess-line' AND start_id IS NOT NULL
)
SELECT n.entity_id,to_jsonb(e) AS entity,pg_temp.repair_snapshot(p) AS content,
       p.id AS physicality_id,
       p.id=public.laplace_hash128_blake3(n.entity_id||decode('0100','hex'))
         AND p.radius_origin<=1.0+1e-12
         AND p.hilbert_index=public.laplace_hilbert_encode(p.coord)
         AND CASE WHEN p.trajectory IS NULL THEN p.n_constituents=0
              ELSE bounded.valid AND proof.n=p.n_constituents AND proof.ordinals=p.n_constituents
                AND CASE WHEN proof.n=1 THEN proof.ids[1]
                         WHEN proof.n>1 THEN public.laplace_hash128_merkle(0::smallint,proof.ids)
                         ELSE NULL::bytea END=n.entity_id END AS valid_content
FROM needed n JOIN laplace.entities e ON e.id=n.entity_id
JOIN laplace.physicalities p ON p.entity_id=n.entity_id AND p.type=1
CROSS JOIN LATERAL (
  SELECT COALESCE(p.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
      AND ST_NPoints(p.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS}
      AND count(*)=ST_NPoints(p.trajectory)
      AND sum(GREATEST(v.run_length,1))=p.n_constituents
      AND min(v.ordinal)=1
      AND max(v.ordinal+GREATEST(v.run_length,1)-1)=p.n_constituents,false) AS valid
  FROM public.laplace_trajectory_constituents(CASE
    WHEN p.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
      AND ST_NPoints(p.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS}
    THEN p.trajectory ELSE NULL END) v
) bounded
CROSS JOIN LATERAL (
  SELECT count(*) AS n,count(DISTINCT v.ordinal) AS ordinals,
         array_agg(v.entity_id ORDER BY v.ordinal) AS ids
  FROM public.laplace_trajectory_expanded_constituents(
    CASE WHEN bounded.valid THEN p.trajectory ELSE NULL END) v
) proof;

CREATE TEMP TABLE repair_eligibility ON COMMIT DROP AS
SELECT c.*,
  CASE WHEN c.entity_rows<>1 THEN 'duplicate-owner-identity'
       WHEN NOT COALESCE(c.resolved,false) THEN 'unresolved-or-malformed-original-manifest'
       WHEN c.id<>public.laplace_hash128_blake3(c.entity_id||decode('0100','hex')) THEN 'noncanonical-physicality-id'
       WHEN EXISTS(SELECT FROM repair_incoming parent WHERE c.entity_id=ANY(parent.members))
         THEN 'incoming-content-requires-coupled-recovery'
       WHEN EXISTS(SELECT FROM repair_native_inputs i
                    WHERE (i.entity_id=ANY(c.child_ids) OR i.entity_id=ANY(c.position_ids))
                      AND NOT COALESCE(i.valid_content,false)) THEN 'invalid-native-input-content'
       WHEN c.repair_kind='player-projection' THEN
         CASE WHEN cardinality(c.child_ids)<>1 OR NOT EXISTS(
                SELECT FROM jsonb_array_elements(c.name_witnesses) a
                WHERE (a->>'outcome')::integer=2 AND (a->>'observation_count')::bigint>0
                  AND (a->>'sum_score_fp1e9')::numeric>(a->>'observation_count')::numeric*500000000)
                THEN 'missing-exact-confirmed-name'
              WHEN EXISTS(SELECT FROM jsonb_array_elements(c.name_witnesses) a
                WHERE (a->>'outcome')::integer<>2 OR (a->>'observation_count')::bigint<=0
                   OR (a->>'sum_score_fp1e9')::numeric<=(a->>'observation_count')::numeric*500000000)
                THEN 'opposing-name-testimony'
              WHEN c.projection_target_exists THEN 'occupied-projection-target' ELSE 'eligible' END
       WHEN c.repair_kind='session-projection' THEN
         CASE WHEN NOT c.all_messages OR c.witnessed_messages<>(SELECT count(DISTINCT id) FROM unnest(c.child_ids) id)
                THEN 'missing-exact-confirmed-session-membership'
              WHEN EXISTS(SELECT FROM jsonb_array_elements(c.membership_witnesses) a
                WHERE (a->>'outcome')::integer<>2 OR (a->>'observation_count')::bigint<=0
                   OR (a->>'sum_score_fp1e9')::numeric<=(a->>'observation_count')::numeric*500000000)
                THEN 'opposing-session-membership'
              WHEN c.projection_target_exists THEN 'occupied-projection-target' ELSE 'eligible' END
       WHEN NOT c.zero_flags THEN 'nonzero-move-occurrence-flags'
       WHEN NOT c.all_moves THEN 'legacy-line-has-non-move-constituents'
       WHEN c.projection_rows>1 THEN 'ambiguous-position-projections'
       WHEN NOT COALESCE(c.positions_resolved,false) OR NOT COALESCE(c.all_positions,false)
            OR c.projection_count<>c.n_constituents+1 THEN 'missing-exact-position-projection'
       WHEN c.projection_id<>public.laplace_hash128_blake3(c.entity_id||decode('0300','hex')) THEN 'noncanonical-projection-id'
       WHEN NOT c.descending OR c.start_tier>=c.tier THEN 'non-descending-repaired-content'
       WHEN public.laplace_hash128_merkle(0::smallint,ARRAY[c.start_id]||c.child_ids)<>c.entity_id THEN 'retained-start-does-not-recover-line-id'
       WHEN EXISTS(SELECT FROM jsonb_array_elements(c.setup_witnesses) a
                   WHERE decode(substr(a->>'object_id',3),'hex')<>c.start_id
                     AND (a->>'outcome')::integer=2 AND (a->>'observation_count')::bigint>0)
         THEN 'conflicting-confirmed-setup'
       WHEN EXISTS(SELECT FROM jsonb_array_elements(c.setup_witnesses) a
                   WHERE decode(substr(a->>'object_id',3),'hex')=c.start_id
                     AND ((a->>'outcome')::integer<>2 OR (a->>'observation_count')::bigint<=0
                       OR (a->>'sum_score_fp1e9')::numeric<=(a->>'observation_count')::numeric*500000000))
         THEN 'opposing-setup-testimony'
       WHEN ST_AsEWKB(c.coord) IS DISTINCT FROM ST_AsEWKB(
              public.laplace_karcher_mean_4d(ST_Collect(c.child_coords)))
         OR c.hilbert_index IS DISTINCT FROM public.laplace_hilbert_encode(c.coord)
         THEN 'unexpected-legacy-game-placement'
       ELSE 'eligible' END AS disposition
FROM repair_candidates c;

CREATE TEMP TABLE repair_plan ON COMMIT DROP AS
SELECT c.id AS old_id,c.entity_id,c.repair_kind,c.disposition,
       c.id AS new_id,c.type AS new_type,c.coord AS new_coord,c.hilbert_index AS new_hilbert,
       c.trajectory AS new_trajectory,c.n_constituents AS new_count,
       pg_temp.repair_snapshot(p) AS original,
       NULL::jsonb AS proposed,
       jsonb_build_object('entity',c.entity_evidence,'position_projection',c.projection_evidence,
           'start_entity',c.start_entity_evidence,'start_content',c.start_physicality_evidence,
           'name_witnesses',c.name_witnesses,'setup_witnesses',c.setup_witnesses,
           'membership_witnesses',c.membership_witnesses,
           'occupied_projections',c.occupied_projection_evidence,
           'incoming_content',(SELECT jsonb_agg(parent.original ORDER BY parent.id)
              FROM repair_incoming parent WHERE c.entity_id=ANY(parent.members)),
           'ordered_children',c.child_ids,
           'original_move_occurrence_flags_all_zero',c.zero_flags,
           'native_placement_input_ewkb',CASE WHEN c.repair_kind='chess-line' AND c.disposition='eligible'
             THEN encode(ST_AsEWKB(ST_Collect(ARRAY[c.start_coord]||c.child_coords)),'hex') ELSE NULL END) AS evidence
FROM repair_eligibility c JOIN laplace.physicalities p ON p.id=c.id;
UPDATE repair_plan p SET
  new_id=CASE WHEN p.repair_kind='chess-line' THEN p.old_id
              ELSE public.laplace_hash128_blake3(p.entity_id||decode('0300','hex')) END,
  new_type=CASE WHEN p.repair_kind='chess-line' THEN 1 ELSE 3 END,
  new_coord=CASE WHEN p.repair_kind='chess-line'
                 THEN public.laplace_karcher_mean_4d(ST_Collect(ARRAY[c.start_coord]||c.child_coords))
                 ELSE c.coord END,
  new_trajectory=CASE WHEN p.repair_kind='chess-line'
                      THEN public.laplace_trajectory_build(ARRAY[c.start_id]||c.child_ids)
                      ELSE c.trajectory END,
  new_count=c.n_constituents+CASE WHEN p.repair_kind='chess-line' THEN 1 ELSE 0 END
FROM repair_eligibility c WHERE c.id=p.old_id AND p.disposition='eligible';
UPDATE repair_plan SET new_hilbert=public.laplace_hilbert_encode(new_coord)
WHERE repair_kind='chess-line' AND disposition='eligible';
UPDATE repair_plan p SET proposed=p.original || jsonb_build_object(
  'id',encode(p.new_id,'hex'),'type',p.new_type,
  'coord_ewkb',encode(ST_AsEWKB(p.new_coord),'hex'),
  'hilbert_index',encode(p.new_hilbert,'hex'),
  'trajectory_ewkb',encode(ST_AsEWKB(p.new_trajectory),'hex'),
  'radius_origin_bits',encode(float8send(public.laplace_radius_origin(p.new_coord)),'hex'),
  'n_constituents',p.new_count);

SELECT jsonb_build_object('kind','context','database',current_database(),
  'producer_generation','{producer_json}'::jsonb,
  'chess_coordinate_recipe',jsonb_build_object('sql_function','public.laplace_karcher_mean_4d',
    'native_kernel','math4d_karcher_mean','tolerance',1e-12,'max_iterations',64,
    'weights','one per logical constituent occurrence','inputs','retained start Content followed by ordered move Content'),
  'database_oid',(SELECT oid::text FROM pg_database WHERE datname=current_database()),
  'system_identifier',(SELECT system_identifier::text FROM pg_control_system()),
  'transaction',pg_current_xact_id()::text,'observed_at',clock_timestamp(),
  'server_version',current_setting('server_version'),
  'substrate_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'),
  'geometry_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_geom'),
  'write_epoch_before',(SELECT last_value FROM laplace.apply_write_epoch),
  'prior_submission_reconciliation',COALESCE((SELECT jsonb_agg(to_jsonb(r)) FROM repair_reconciliation r),'[]'::jsonb),
  'owner_envelope',{max_rows},'typed_content_owners_screened',(SELECT count(*) FROM repair_owner_inventory),'logical_constituent_envelope',{MAX_CONSTITUENTS});
SELECT jsonb_build_object('kind','native-input','entity_id',encode(entity_id,'hex'),
  'entity',entity,'content',content) FROM repair_native_inputs ORDER BY entity_id;
SELECT jsonb_build_object('kind','physicality','repair_kind',repair_kind,'disposition',disposition,
  'original',original,'proposed',proposed,'evidence',evidence)
FROM repair_plan ORDER BY old_id;
SELECT jsonb_build_object('kind','plan','count',count(*),
  'native_input_count',(SELECT count(*) FROM repair_native_inputs),
  'unresolved',count(*) FILTER(WHERE disposition<>'eligible'),
  'classifications',COALESCE((SELECT jsonb_agg(to_jsonb(g)) FROM (
    SELECT repair_kind,disposition,count(*) AS rows FROM repair_plan GROUP BY repair_kind,disposition) g),'[]'::jsonb))
FROM repair_plan;
"""


def apply_sql() -> str:
    return """
CREATE TEMP TABLE repair_applied_epoch(epoch bigint) ON COMMIT DROP;
DO $apply$
DECLARE expected bigint; changed bigint;
BEGIN
  SELECT count(*) INTO expected FROM repair_plan;
  IF EXISTS(SELECT FROM repair_plan WHERE disposition<>'eligible') THEN
    RAISE EXCEPTION 'Repair eligibility changed before mutation';
  END IF;
  IF EXISTS(SELECT FROM repair_plan r LEFT JOIN laplace.physicalities p ON p.id=r.old_id
            WHERE pg_temp.repair_snapshot(p) IS DISTINCT FROM r.original) THEN
    RAISE EXCEPTION 'Original physicality changed after its durable receipt';
  END IF;
  IF EXISTS(SELECT FROM repair_native_inputs i
            LEFT JOIN laplace.entities e ON e.id=i.entity_id
            LEFT JOIN laplace.physicalities p ON p.id=i.physicality_id
            WHERE to_jsonb(e) IS DISTINCT FROM i.entity
               OR pg_temp.repair_snapshot(p) IS DISTINCT FROM i.content)
     OR EXISTS(SELECT FROM repair_native_inputs i
               WHERE (SELECT count(*) FROM laplace.physicalities p
                       WHERE p.entity_id=i.entity_id AND p.type=1)<>1
                  OR (SELECT count(*) FROM laplace.entities e WHERE e.id=i.entity_id)<>1) THEN
    RAISE EXCEPTION 'Native input changed after its durable receipt';
  END IF;
  IF EXISTS(SELECT FROM repair_candidates c
            LEFT JOIN laplace.entities e ON e.id=c.entity_id
            LEFT JOIN laplace.physicalities p ON p.id=c.projection_id
            WHERE to_jsonb(e) IS DISTINCT FROM c.entity_evidence
               OR pg_temp.repair_snapshot(p) IS DISTINCT FROM c.projection_evidence
               OR (SELECT count(*) FROM laplace.entities other WHERE other.id=c.entity_id)<>1) THEN
    RAISE EXCEPTION 'Owner or position evidence changed after its durable receipt';
  END IF;
  IF EXISTS(SELECT FROM repair_candidates c WHERE
      (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM laplace.attestations a
        WHERE a.subject_id=c.entity_id AND a.type_id=laplace.relation_type_id('HAS_NAME_ALIAS')
          AND a.object_id=c.child_ids[1]) IS DISTINCT FROM c.name_witnesses
      OR (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM laplace.attestations a
        WHERE a.subject_id=c.entity_id AND a.type_id=laplace.relation_type_id('HAS_SETUP'))
           IS DISTINCT FROM c.setup_witnesses
      OR (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM laplace.attestations a
        WHERE a.subject_id=ANY(c.child_ids) AND a.object_id=c.entity_id
          AND a.context_id=c.entity_id AND a.type_id=laplace.relation_type_id('APPEARS_IN'))
           IS DISTINCT FROM c.membership_witnesses) THEN
    RAISE EXCEPTION 'Applicable testimony changed after its durable receipt';
  END IF;
  IF EXISTS(SELECT FROM repair_candidates c WHERE
      (SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id) FROM laplace.physicalities p
        WHERE (p.entity_id=c.entity_id AND p.type=3)
           OR p.id=public.laplace_hash128_blake3(c.entity_id||decode('0300','hex')))
        IS DISTINCT FROM c.occupied_projection_evidence)
     OR (SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id)
         FROM laplace.physicalities p WHERE p.type=1
           AND public.laplace_trajectory_constituent_ids(p.trajectory)
               && ARRAY(SELECT entity_id FROM repair_owners))
        IS DISTINCT FROM (SELECT jsonb_agg(original ORDER BY id) FROM repair_incoming) THEN
    RAISE EXCEPTION 'Projection or incoming Content changed after its durable receipt';
  END IF;
  IF expected>0 THEN
    INSERT INTO repair_applied_epoch SELECT nextval('laplace.apply_write_epoch');
    UPDATE laplace.physicalities p SET id=r.new_id,type=r.new_type,coord=r.new_coord,
      hilbert_index=r.new_hilbert,trajectory=r.new_trajectory,n_constituents=r.new_count
    FROM repair_plan r WHERE p.id=r.old_id;
    GET DIAGNOSTICS changed=ROW_COUNT;
    IF changed<>expected THEN
      RAISE EXCEPTION 'Repair updated % rows, expected %; rolling back',changed,expected;
    END IF;
    IF EXISTS(SELECT FROM repair_plan r LEFT JOIN laplace.physicalities p ON p.id=r.new_id
              WHERE pg_temp.repair_snapshot(p) IS DISTINCT FROM r.proposed)
       OR EXISTS(SELECT FROM repair_plan r JOIN laplace.physicalities p ON p.id=r.old_id
                  WHERE r.old_id<>r.new_id) THEN
      RAISE EXCEPTION 'Repair exact row readback failed; rolling back';
    END IF;
    IF EXISTS(
      SELECT FROM repair_plan r
      CROSS JOIN LATERAL (
        SELECT count(*) AS n,array_agg(v.entity_id ORDER BY v.ordinal) AS ids,
               bool_and(e.id IS NOT NULL AND e.tier<parent.tier) AS descending
        FROM public.laplace_trajectory_expanded_constituents(r.new_trajectory) v
        LEFT JOIN laplace.entities e ON e.id=v.entity_id
        JOIN laplace.entities parent ON parent.id=r.entity_id
      ) proof
      WHERE r.repair_kind='chess-line' AND (
        proof.n<>r.new_count OR NOT proof.descending
        OR public.laplace_hash128_merkle(0::smallint,proof.ids)<>r.entity_id
        OR public.laplace_radius_origin(r.new_coord)>1.0+1e-12)) THEN
      RAISE EXCEPTION 'Repair native content identity/descent/bounds proof failed; rolling back';
    END IF;
  END IF;
END $apply$;
SELECT jsonb_build_object('kind','applied','count',(SELECT count(*) FROM repair_plan),
  'epoch',(SELECT epoch FROM repair_applied_epoch),'postconditions','exact-row-readback-and-native-content-proof');
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', default=os.environ.get('PGDATABASE', 'laplace'))
    parser.add_argument('--receipt-root', type=Path, default=Path('/build/laplace/recovery/legacy-content-repair'))
    parser.add_argument('--max-rows', type=int, default=MAX_ROWS)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("repair_quiescence", ROOT / "scripts/quiesce-managed-database.py")
    quiescence = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(quiescence)
    producer_generation = None
    if args.database == "laplace":
        # Require the concrete installed-service exclusion receipt produced by
        # the lifecycle wrapper. It identifies the still-held root transaction;
        # a copied environment flag alone is not a quiescence claim.
        receipt = Path(os.environ.get("LAPLACE_DATABASE_QUIESCENCE_RECEIPT", "/missing"))
        if receipt.resolve().parent != Path("/build/laplace/recovery/legacy-content-service-quiescence"):
            raise ValueError("canonical repair requires the active managed service quiescence receipt")
        retained = json.loads((receipt / "quiescence-confirmed.json").read_text())
        active_transaction = quiescence.transaction_identity(quiescence.STATE)
        if not isinstance(retained.get("transaction_identity"), dict) or active_transaction is None \
                or (receipt / "commit-submission.json").exists() \
                or retained["transaction_identity"] != active_transaction:
            raise ValueError("canonical repair's managed service exclusion is no longer active")
        producer_generation = json.loads((receipt / "prior-services.json").read_text()).get("producer_generation")
        if not isinstance(producer_generation, dict) or producer_generation != quiescence.published_application_generation():
            raise ValueError("canonical repair requires the same verified published managed generation")
        for name in ("api", "mcp", "lichess"):
            status = quiescence.service_status(name)
            if status["active_state"] not in ("inactive", "failed") or status["main_pid"] != 0:
                raise ValueError("canonical repair found a managed writer still running: " + name)
    # measure-lane starts this process directly while its authoritative advisory
    # lock connection remains alive. Inspect that caller, rather than accepting
    # an inherited Boolean claiming that some unrelated process holds the lane.
    parent_argv = Path(f"/proc/{os.getppid()}/cmdline").read_bytes().split(b"\0")
    if b"measure-lane" not in parent_argv or quiescence.laplace_component(parent_argv) not in (
            "laplace", "Laplace.Cli", "Laplace.Cli.dll"):
        raise ValueError("repair must execute directly inside the installed measurement lane")
    source_sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    prefix = Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18"))
    command = [str(prefix / "bin/psql"), "-XqAt", "-w", "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
               "-p", os.environ.get("PGPORT", "5432"), "-U", os.environ.get("PGUSER", "laplace_admin"),
               "-d", args.database, "-v", "ON_ERROR_STOP=1"]
    args.receipt_root.mkdir(parents=True, mode=0o770, exist_ok=True)
    pending = unresolved_submissions(args.receipt_root)
    directory = args.receipt_root / (str(time.time_ns()) + "-" + uuid.uuid4().hex)
    outcome = preserve_and_apply(command, plan_sql(args.max_rows, pending, producer_generation), apply_sql(), directory,
        source_sha=source_sha, max_rows=args.max_rows, max_bytes=MAX_BYTES)
    close_reconciled_submissions(args.receipt_root, directory)
    print(json.dumps({"receipt_directory": str(directory), "outcome": outcome}, sort_keys=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
