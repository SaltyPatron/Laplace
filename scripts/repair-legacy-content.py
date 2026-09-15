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

from lib.legacy_content_snapshot import snapshot_expression
from lib.legacy_content_receipt import frozen_receipt_sql, receipt_stream_sql
from lib.repair_transaction import preserve_and_apply, sync_directory, write_new_json


ROOT = Path(__file__).resolve().parents[1]
MAX_ROWS = 25000
MAX_CONSTITUENTS = 4096
MAX_BYTES = 512 * 1024 * 1024
MAX_LINE_BYTES = 2 * 1024 * 1024
MAX_NATIVE_INPUTS = 100000
PHASE_TIMEOUT_SECONDS = 1800
PERSISTENCE_TIMEOUT_SECONDS = 60
# Policy allowance for auxiliary receipts and logs, separate from measured JSONL.
AUXILIARY_RESERVE_BYTES = 64 * 1024 * 1024
MAX_METADATA_BYTES = 64 * 1024
LEGACY_MAX_BYTES = 512 * 1024 * 1024
LEGACY_MAX_LINE_BYTES = 2 * 1024 * 1024


def positive_integer(value: object) -> int:
    if type(value) is not int or value <= 0:
        raise ValueError("resource limits must be positive integers")
    return value


def positive_argument(value: str) -> int:
    try:
        return positive_integer(int(value))
    except ValueError as error:
        raise argparse.ArgumentTypeError(str(error)) from error


def nonnegative_argument(value: str) -> int:
    try:
        number = int(value)
        if number < 0:
            raise ValueError("resource reserve must be a nonnegative integer")
        return number
    except ValueError as error:
        raise argparse.ArgumentTypeError(str(error)) from error


class PriorReceiptBudget:
    """Bound historical plan reads across discovery, native status, replay and closure."""

    def __init__(self, max_bytes: int):
        self.max_bytes = positive_integer(max_bytes)
        self.bytes_read = 0

    def reserve(self, size: int, *, directory: Path | None = None) -> None:
        positive_integer(size)
        if size > self.max_bytes - self.bytes_read:
            raise ValueError("prior repair receipts exceed aggregate byte bound")
        self.bytes_read += size


def read_metadata(path: Path) -> dict:
    with path.open("rb") as source:
        raw = source.read(MAX_METADATA_BYTES + 1)
    if len(raw) > MAX_METADATA_BYTES:
        raise ValueError("repair receipt metadata exceeds byte bound")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("repair receipt metadata must be an object")
    return value


def check_deadline(deadline: float | None) -> None:
    if deadline is not None and time.monotonic() >= deadline:
        raise TimeoutError("legacy repair maintenance deadline exceeded during receipt reconciliation")


def normalized_prior_physicality(value: dict) -> dict:
    """Map published operation spelling to the explicit retained-row contract.

    The locked SQL contract independently verifies every reuse field. Legacy
    evidence supplies the actual target; the original supplies the migration.
    No alternate alias or altered semantic field is accepted by normalization.
    """
    operation = value.get("operation")
    legacy_actions = {"rewrite-physicality": "rewrite", "retire-redundant-content": "reuse-existing-projection"}
    if operation is not None and operation not in legacy_actions:
        raise ValueError("prior repair operation is unknown")
    action = value.get("action", legacy_actions.get(operation, "rewrite"))
    if action not in ("rewrite", "reuse-existing-projection", "retain-witnessed-player-alias-projection") or (operation is not None and legacy_actions[operation] != action):
        raise ValueError("prior repair action is unknown or inconsistent")
    if "action" in value:
        return value
    normalized = value | {"action": action}
    if action == "reuse-existing-projection":
        evidence, original, proposed = value.get("evidence"), value.get("original"), value.get("proposed")
        targets = evidence.get("occupied_projections") if isinstance(evidence, dict) else None
        if not isinstance(original, dict) or not isinstance(proposed, dict) or not isinstance(targets, list) or len(targets) != 1:
            raise ValueError("prior retirement receipt has no exact retained target")
        normalized |= {"existing_target": targets[0],
                       "migration_proposal": original | {"id": proposed.get("id"), "type": 3}}
    return normalized


def verified_plan(directory: Path, *, replay_target=None, deadline: float | None = None,
                  max_bytes: int = MAX_BYTES, max_line_bytes: int = MAX_LINE_BYTES,
                  budget: PriorReceiptBudget | None = None) -> tuple[dict, dict]:
    check_deadline(deadline)
    max_bytes = positive_integer(max_bytes)
    max_line_bytes = positive_integer(max_line_bytes)
    manifest = read_metadata(directory / "manifest.json")
    if manifest.get("schema") != "laplace.legacy-content-repair-plan/v1":
        raise ValueError("unknown prior repair receipt schema")
    declared = positive_integer(manifest.get("plan_bytes"))
    # Published 272 replaces optional input ceilings with measured finite
    # admission before writing the manifest. A durable null ceiling is invalid.
    recorded_bytes = positive_integer(manifest.get("max_bytes", LEGACY_MAX_BYTES))
    admitted_bytes = min(max_bytes, recorded_bytes)
    recorded_line = positive_integer(manifest.get("max_line_bytes", LEGACY_MAX_LINE_BYTES))
    admitted_line = min(max_line_bytes, recorded_line, admitted_bytes, declared)
    for field in ("max_rows", "max_native_inputs", "timeout_seconds", "persistence_timeout_seconds",
                  "idle_timeout_seconds", "psql_fetch_rows"):
        if field in manifest:
            positive_integer(manifest[field])
    if declared > admitted_bytes:
        raise ValueError("prior repair receipt exceeds recorded or current byte bound")
    size = rows = native_inputs = rewrites = reuses = alias_retentions = 0
    context = summary = None
    digest = hashlib.sha256()
    with (directory / "plan.jsonl").open("rb") as source:
        if os.fstat(source.fileno()).st_size != declared:
            raise ValueError("prior repair evidence byte count changed")
        if budget is not None:
            budget.reserve(declared, directory=directory)
        while raw := source.readline(admitted_line + 2):
            check_deadline(deadline)
            if not raw.endswith(b"\n") or len(raw) - 1 > admitted_line:
                raise ValueError("prior repair record exceeds line bound")
            size += len(raw)
            if size > declared:
                raise ValueError("prior repair evidence byte count changed")
            digest.update(raw)
            value = json.loads(raw)
            if not isinstance(value, dict) or summary is not None:
                raise ValueError("prior repair record contract is invalid")
            kind = value.get("kind")
            if context is None:
                if kind != "context":
                    raise ValueError("prior repair context is missing")
                context = value
            elif kind == "native-input":
                native_inputs += 1
            elif kind == "physicality":
                value = normalized_prior_physicality(value)
                rows += 1
                rewrites += value["action"] == "rewrite"
                reuses += value["action"] == "reuse-existing-projection"
                alias_retentions += value["action"] == "retain-witnessed-player-alias-projection"
            elif kind == "plan":
                summary = value
            else:
                raise ValueError("prior repair record kind is unknown")
            if replay_target is not None and kind != "native-input":
                # Copy only authenticated row states/metadata. Legacy records
                # are explicitly normalized; native inputs stay in the original.
                replay_target.write((json.dumps(value, sort_keys=True) + "\n").encode("utf-8"))
    if size != declared or digest.hexdigest() != manifest.get("plan_sha256"):
        raise ValueError("prior repair evidence changed")
    if context is None or summary is None or rows != manifest.get("planned_rows") \
            or native_inputs != manifest.get("native_input_rows", 0) \
            or rows != summary.get("count") or native_inputs != summary.get("native_input_count", 0):
        raise ValueError("prior repair receipt count changed")
    for field, count in (("rewrites", rewrites), ("retirements", reuses),
                         ("rewritten_rows", rewrites), ("reused_projection_rows", reuses),
                         ("retained_player_alias_rows", alias_retentions)):
        if field in summary and (type(summary[field]) is not int or summary[field] != count):
            raise ValueError("prior repair action count changed")
    check_deadline(deadline)
    return manifest, context


def confirmed_outcome(directory: Path, manifest: dict) -> bool:
    try:
        outcome = read_metadata(directory / "outcome.json")
    except (OSError, ValueError):
        return False
    return isinstance(outcome, dict) and outcome.get("disposition") == "commit-confirmed" \
        and outcome.get("plan_sha256") == manifest["plan_sha256"] \
        and outcome.get("planned_rows") == manifest["planned_rows"] \
        and isinstance(outcome.get("applied"), dict) \
        and outcome["applied"].get("count") == manifest["planned_rows"]


def verified_reconciliation(directory: Path, manifest: dict, root: Path, *,
                            max_bytes: int = MAX_BYTES, max_line_bytes: int = MAX_LINE_BYTES,
                            budget: PriorReceiptBudget | None = None,
                            deadline: float | None = None) -> bool:
    path = directory / "reconciliation.json"
    if not path.exists():
        return False
    reference = read_metadata(path)
    target = Path(reference["reconciliation_directory"])
    if target.resolve().parent != root.resolve() or target.resolve() == directory.resolve() \
            or reference.get("original_plan_sha256") != manifest["plan_sha256"]:
        raise ValueError("prior reconciliation reference does not identify this receipt estate")
    current_manifest, current_context = verified_plan(target, max_bytes=max_bytes,
        max_line_bytes=max_line_bytes, budget=budget, deadline=deadline)
    if not confirmed_outcome(target, current_manifest) \
            or current_manifest["plan_sha256"] != reference.get("reconciliation_plan_sha256"):
        raise ValueError("prior reconciliation has no matching successful durable outcome")
    return any(item.get("receipt") == str((directory / "plan.jsonl").resolve()) and
               item.get("disposition") in ("originals-confirmed", "prior-commit-confirmed", "zero-row-no-mutation")
               for item in current_context.get("prior_submission_reconciliation", []))


def unresolved_submissions(root: Path, *, max_bytes: int = MAX_BYTES,
                           max_line_bytes: int = MAX_LINE_BYTES,
                           budget: PriorReceiptBudget | None = None,
                           deadline: float | None = None) -> list[Path]:
    positive_integer(max_bytes)
    positive_integer(max_line_bytes)
    if budget is None:
        budget = PriorReceiptBudget(max_bytes)
    pending = []
    for directory in sorted(root.iterdir()):
        if not directory.is_dir() or not (directory / "submission.json").exists():
            continue
        if directory.is_symlink():
            raise ValueError("repair receipt directories cannot be symlinks")
        manifest, _ = verified_plan(directory, max_bytes=max_bytes,
            max_line_bytes=max_line_bytes, budget=budget, deadline=deadline)
        if confirmed_outcome(directory, manifest) or verified_reconciliation(directory, manifest, root,
                max_bytes=max_bytes, max_line_bytes=max_line_bytes, budget=budget, deadline=deadline):
            continue
        pending.append(directory / "plan.jsonl")
    if len(pending) > 32:
        raise ValueError("too many unresolved submissions for one repair transaction")
    return pending


def close_reconciled_submissions(root: Path, current: Path, *, max_bytes: int = MAX_BYTES,
                                 max_line_bytes: int = MAX_LINE_BYTES,
                                 budget: PriorReceiptBudget | None = None,
                                 current_budget: PriorReceiptBudget | None = None,
                                 deadline: float | None = None) -> None:
    # The newly written plan has its own admitted write envelope. The separate
    # prior budget covers retained earlier plans read while closing references.
    manifest, context = verified_plan(current, max_bytes=max_bytes, max_line_bytes=max_line_bytes,
        budget=current_budget, deadline=deadline)
    if budget is None:
        budget = PriorReceiptBudget(max_bytes)
    if not confirmed_outcome(current, manifest):
        raise ValueError("cannot close prior submissions without a successful durable reconciliation")
    for item in context.get("prior_submission_reconciliation", []):
        directory = Path(item["receipt"]).parent
        if directory.resolve().parent != root.resolve() or directory.resolve() == current.resolve():
            raise ValueError("reconciliation refers outside its receipt estate")
        old_manifest, _ = verified_plan(directory, max_bytes=max_bytes,
            max_line_bytes=max_line_bytes, budget=budget, deadline=deadline)
        reference = {"schema": "laplace.legacy-content-repair-reconciliation/v1",
                     "original_plan_sha256": old_manifest["plan_sha256"],
                     "reconciliation_directory": str(current.resolve()),
                     "reconciliation_plan_sha256": manifest["plan_sha256"],
                     "disposition": item["disposition"]}
        check_deadline(deadline)
        write_new_json(directory / "reconciliation.json", reference)
        check_deadline(deadline)


def reconciliation_input(path: Path, *, deadline: float | None = None,
                         max_bytes: int = MAX_BYTES, max_line_bytes: int = MAX_LINE_BYTES,
                         budget: PriorReceiptBudget | None = None) -> Path:
    """Keep the full journal immutable; project only authenticated row states.

    Native inputs are required before mutation. Unknown-outcome reconciliation
    compares the complete original/proposed rows, so it need not COPY the entire
    dependency estate into PostgreSQL again. The count comes from streaming and
    hashing every source record, never from an unverified summary alone.
    """
    if path.name != "plan.jsonl":
        raise ValueError("prior repair must identify its complete plan journal")
    projected = path.parent / ("reconciliation-input-" + uuid.uuid4().hex + ".jsonl")
    with projected.open("xb") as target:
        manifest, _ = verified_plan(path.parent, replay_target=target, deadline=deadline,
            max_bytes=max_bytes, max_line_bytes=max_line_bytes, budget=budget)
        target.write((json.dumps({"kind": "verified-native-input-count",
            "count": manifest.get("native_input_rows", 0),
            "source_plan_sha256": manifest["plan_sha256"],
            "source_plan_bytes": manifest["plan_bytes"]}, sort_keys=True) + "\n").encode())
        target.flush()
        os.fsync(target.fileno())
    sync_directory(path.parent)
    check_deadline(deadline)
    return projected


def prior_sql(paths: list[Path], *, deadline: float | None = None,
              max_bytes: int = MAX_BYTES, max_line_bytes: int = MAX_LINE_BYTES,
              budget: PriorReceiptBudget | None = None) -> str:
    statements = ["""CREATE TEMP TABLE repair_prior(document jsonb, receipt text) ON COMMIT DROP;
CREATE TEMP TABLE repair_prior_input(document jsonb) ON COMMIT DROP;"""]
    for path in paths:
        # psql's COPY path is a local filesystem operand, not SQL from a user.
        # Keep its accepted alphabet narrower than either parser's quoting rules.
        name = str(path.resolve())
        if not re.fullmatch(r"/[A-Za-z0-9/_.-]+", name):
            raise ValueError("prior receipt path is not a supported absolute path")
        replay = str(reconciliation_input(path, deadline=deadline, max_bytes=max_bytes,
            max_line_bytes=max_line_bytes, budget=budget).resolve())
        statements += [f"\\copy repair_prior_input(document) FROM '{replay}' WITH (FORMAT csv, DELIMITER E'\\x01', QUOTE E'\\x02')",
                       f"INSERT INTO repair_prior SELECT document,'{name}' FROM repair_prior_input;",
                       "TRUNCATE repair_prior_input;"]
    statements.append("""
CREATE TEMP TABLE repair_prior_summary ON COMMIT DROP AS
SELECT receipt,count(*) FILTER(WHERE document->>'kind'='context') AS contexts,
  count(*) FILTER(WHERE document->>'kind'='plan') AS plans,
  count(*) FILTER(WHERE document->>'kind'='physicality') AS physicalities,
  count(*) FILTER(WHERE document->>'kind'='verified-native-input-count') AS count_receipts,
  sum((document->>'count')::bigint) FILTER(WHERE document->>'kind'='verified-native-input-count') AS native_inputs,
  (jsonb_agg(document) FILTER(WHERE document->>'kind'='context'))->0 AS context,
  (jsonb_agg(document) FILTER(WHERE document->>'kind'='plan'))->0 AS plan
FROM repair_prior GROUP BY receipt;
CREATE TEMP TABLE repair_prior_player_alias_evidence ON COMMIT DROP AS
SELECT proof.* FROM pg_temp.repair_player_alias_proofs(COALESCE((
  SELECT jsonb_agg(jsonb_build_object('original',document->'original','existing_target',document->'existing_target'))
  FROM repair_prior WHERE document->>'kind'='physicality'
    AND document->>'action'='retain-witnessed-player-alias-projection'),'[]'::jsonb)) proof;
DO $prior_contract$
BEGIN
  IF EXISTS(SELECT FROM repair_prior_summary s WHERE s.contexts<>1 OR s.plans<>1 OR s.count_receipts<>1
      OR (s.plan->>'count')::bigint IS DISTINCT FROM s.physicalities
      OR (s.plan->>'native_input_count')::bigint IS DISTINCT FROM s.native_inputs
      OR (s.plan->>'unresolved')::bigint IS DISTINCT FROM 0
      OR s.context->>'database' IS DISTINCT FROM current_database()
      OR s.context->>'database_oid' IS DISTINCT FROM (SELECT oid::text FROM pg_database WHERE datname=current_database())
      OR s.context->>'system_identifier' IS DISTINCT FROM (SELECT system_identifier::text FROM pg_control_system())) THEN
    RAISE EXCEPTION 'Prior repair receipt contract or database identity does not match';
  END IF;
  IF EXISTS(SELECT FROM repair_prior r WHERE r.document->>'kind'='physicality'
      AND (COALESCE(r.document->>'action','rewrite') NOT IN ('rewrite','reuse-existing-projection','retain-witnessed-player-alias-projection')
        OR (r.document->>'action'='reuse-existing-projection' AND (
          COALESCE(r.document->>'repair_kind','') NOT IN ('player-projection','session-projection')
          OR jsonb_typeof(r.document->'existing_target') IS DISTINCT FROM 'object'
          OR jsonb_typeof(r.document->'migration_proposal') IS DISTINCT FROM 'object'
          OR r.document->'original'->>'type' IS DISTINCT FROM '1'
          OR r.document->'proposed'->>'type' IS DISTINCT FROM '3'
          OR r.document->'original'->>'id' IS NOT DISTINCT FROM r.document->'proposed'->>'id'
          OR r.document->'original'->>'id' IS DISTINCT FROM encode(public.laplace_hash128_blake3(
               decode(r.document->'original'->>'entity_id','hex')||decode('0100','hex')),'hex')
          OR r.document->'proposed' IS DISTINCT FROM r.document->'existing_target'
          OR r.document->'proposed'->>'id' IS DISTINCT FROM encode(public.laplace_hash128_blake3(
               decode(r.document->'original'->>'entity_id','hex')||decode('0300','hex')),'hex')
          OR ((r.document->'migration_proposal')-ARRAY['observed_at','observed_at_binary'])
               IS DISTINCT FROM ((r.document->'existing_target')-ARRAY['observed_at','observed_at_binary'])
          OR (((r.document->'original')||jsonb_build_object(
                 'id',r.document->'proposed'->>'id','type',3))-ARRAY['observed_at','observed_at_binary'])
               IS DISTINCT FROM ((r.document->'migration_proposal')-ARRAY['observed_at','observed_at_binary'])
        )))) THEN
    RAISE EXCEPTION 'Prior Projection reuse receipt has an invalid action or semantic contract';
  END IF;
  IF EXISTS(SELECT FROM repair_prior r
      LEFT JOIN repair_prior_player_alias_evidence proof
        ON proof.old_id=decode(r.document->'original'->>'id','hex')
      WHERE r.document->>'kind'='physicality' AND r.document->>'action'='retain-witnessed-player-alias-projection'
        AND (r.document->>'repair_kind' IS DISTINCT FROM 'player-projection'
          OR NOT COALESCE(proof.valid,false)
          OR r.document->'evidence'->'player_alias_retention' IS DISTINCT FROM proof.evidence
          OR r.document->'proposed' IS DISTINCT FROM r.document->'existing_target'
          OR r.document->'migration_proposal' IS DISTINCT FROM
            ((r.document->'original')||jsonb_build_object('id',r.document->'existing_target'->>'id','type',3))
          OR COALESCE((SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id)
                FROM laplace.physicalities p
                WHERE (p.entity_id=decode(r.document->'original'->>'entity_id','hex') AND p.type=3)
                   OR p.id=decode(r.document->'existing_target'->>'id','hex')),'[]'::jsonb)
               IS DISTINCT FROM jsonb_build_array(r.document->'existing_target')
          OR COALESCE((SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id)
                FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
                  AND public.laplace_trajectory_constituent_ids(p.trajectory)
                    && ARRAY[decode(r.document->'original'->>'entity_id','hex')]),'null'::jsonb)
               IS DISTINCT FROM r.document->'evidence'->'incoming_content')) THEN
    RAISE EXCEPTION 'Prior witnessed player alias receipt has an invalid proof or retained-row contract';
  END IF;
END $prior_contract$;
CREATE TEMP TABLE repair_reconciliation ON COMMIT DROP AS
WITH compared AS (
  SELECT r.receipt,r.document,
         pg_temp.repair_snapshot(oldrow)=r.document->'original'
           AND CASE WHEN r.document->>'action' IN ('reuse-existing-projection','retain-witnessed-player-alias-projection')
                THEN oldrow.id<>newrow.id
                  AND pg_temp.repair_snapshot(newrow)=r.document->'existing_target'
                ELSE oldrow.id=newrow.id OR newrow.id IS NULL END AS matches_original,
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


def player_alias_proof_sql() -> str:
    return rf"""
CREATE FUNCTION pg_temp.repair_player_alias_proofs(pairs jsonb)
RETURNS TABLE(old_id bytea,valid boolean,evidence jsonb)
LANGUAGE sql VOLATILE AS $player_alias_proofs$
WITH distinct_pairs AS MATERIALIZED (
  SELECT DISTINCT value AS pair FROM jsonb_array_elements(pairs)
), pair_ids AS MATERIALIZED (
  SELECT pair,decode(pair->'original'->>'id','hex') AS old_id,
    decode(pair->'original'->>'entity_id','hex') AS owner_id,
    count(*) OVER(PARTITION BY pair->'original'->>'id') AS owner_pair_count
  FROM distinct_pairs
), sides AS MATERIALIZED (
  SELECT p.*,side.role,side.carrier,
    ST_GeomFromEWKB(decode(side.carrier->>'trajectory_ewkb','hex')) AS trajectory
  FROM pair_ids p CROSS JOIN LATERAL (VALUES
    ('source_alias',p.pair->'original'),
    ('target_alias',p.pair->'existing_target')) side(role,carrier)
), decoded AS MATERIALIZED (
  SELECT s.*,packed.child_id,packed.occurrences,
    COALESCE(s.carrier->>'n_constituents'='1'
      AND ST_NPoints(s.trajectory)=1 AND packed.n=1
      AND packed.ordinal=1 AND packed.run_length=1 AND packed.flags=0,false) AS singleton
  FROM sides s CROSS JOIN LATERAL (
    SELECT count(*) AS n,(array_agg(v.entity_id ORDER BY v.ordinal))[1] AS child_id,
      min(v.ordinal) AS ordinal,min(v.run_length) AS run_length,min(v.flags) AS flags,
      COALESCE(jsonb_agg(jsonb_build_object('ordinal',v.ordinal,
        'child_id',encode(v.entity_id,'hex'),'run_length',v.run_length,'flags',v.flags)
        ORDER BY v.ordinal,v.entity_id,v.run_length,v.flags),'[]'::jsonb) AS occurrences
    FROM public.laplace_trajectory_constituents(CASE
      WHEN ST_NPoints(s.trajectory)=1 THEN s.trajectory ELSE NULL END) v
  ) packed
), owners AS MATERIALIZED (
  SELECT requested.owner_id,count(e.id) AS n,
    COALESCE(bool_and(e.type_id=realize.canonical_id('Chess_Player')),false) AS is_player,
    COALESCE(jsonb_agg(to_jsonb(e) ORDER BY e.id,to_jsonb(e))
      FILTER(WHERE e.id IS NOT NULL),'[]'::jsonb) AS entities
  FROM (SELECT DISTINCT owner_id FROM pair_ids) requested
  LEFT JOIN laplace.entities e ON e.id=requested.owner_id GROUP BY requested.owner_id
), child_ids AS MATERIALIZED (
  SELECT DISTINCT child_id FROM decoded WHERE child_id IS NOT NULL
), child_entities AS MATERIALIZED (
  SELECT requested.child_id,count(e.id) AS n,min(e.tier) AS tier,
    COALESCE(jsonb_agg(to_jsonb(e) ORDER BY e.id,to_jsonb(e))
      FILTER(WHERE e.id IS NOT NULL),'[]'::jsonb) AS entities
  FROM child_ids requested LEFT JOIN laplace.entities e ON e.id=requested.child_id
  GROUP BY requested.child_id
), content_structure AS MATERIALIZED (
  SELECT p.id,requested.child_id,pg_temp.repair_snapshot(p) AS content,
    COALESCE(CASE WHEN p.trajectory IS NULL THEN p.n_constituents=0 AND ce.tier=0
      ELSE bounds.valid AND logical.n=p.n_constituents AND logical.ordinals=p.n_constituents
        AND CASE WHEN logical.n=1 THEN logical.ids[1]
                 WHEN logical.n>1 THEN public.laplace_hash128_merkle(0::smallint,logical.ids)
                 ELSE NULL::bytea END=requested.child_id END,false) AS valid_content
  FROM child_ids requested JOIN child_entities ce USING(child_id)
  JOIN laplace.physicalities p ON p.entity_id=requested.child_id AND p.type=1
  CROSS JOIN LATERAL (
    SELECT COALESCE(p.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
      AND ST_NPoints(p.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS}
      AND count(*)=ST_NPoints(p.trajectory)
      AND sum(GREATEST(v.run_length,1))=p.n_constituents
      AND min(v.ordinal)=1
      AND max(v.ordinal+GREATEST(v.run_length,1)-1)=p.n_constituents,false) AS valid
    FROM public.laplace_trajectory_constituents(CASE
      WHEN p.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
        AND ST_NPoints(p.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS} THEN p.trajectory ELSE NULL END) v
  ) bounds
  CROSS JOIN LATERAL (
    SELECT count(*) AS n,count(DISTINCT v.ordinal) AS ordinals,
      array_agg(v.entity_id ORDER BY v.ordinal) AS ids
    FROM public.laplace_trajectory_expanded_constituents(
      CASE WHEN bounds.valid THEN p.trajectory ELSE NULL END) v
  ) logical
), child_contents AS MATERIALIZED (
  SELECT requested.child_id,count(p.id) AS n,
    COALESCE(bool_and(p.valid_content),false) AS valid_content,
    COALESCE(jsonb_agg(p.content ORDER BY p.id,p.content)
      FILTER(WHERE p.id IS NOT NULL),'[]'::jsonb) AS contents
  FROM child_ids requested LEFT JOIN content_structure p USING(child_id)
  GROUP BY requested.child_id
), rendered AS MATERIALIZED (
  -- Render each distinct exact child identity once. This existing native
  -- reconstruction surface returns NULL unless its complete bytes reproduce
  -- the requested canonical identity. No label or alias election participates.
  SELECT requested.child_id,realize.reconstruct_content(requested.child_id) AS canonical_utf8
  FROM child_ids requested
), texts AS MATERIALIZED (
  SELECT canonical_utf8,row_number() OVER(ORDER BY canonical_utf8)::integer AS ord
  FROM (SELECT DISTINCT canonical_utf8 FROM rendered
    WHERE canonical_utf8 IS NOT NULL AND octet_length(canonical_utf8)>0
      AND position(decode('00','hex') IN canonical_utf8)=0) unique_texts
), placements AS MATERIALIZED (
  -- Exactly one coarse composition call for all distinct complete byte inputs.
  -- Its occurrence ordinal is only a join key, never retained semantic evidence.
  SELECT t.canonical_utf8,p.root_id,p.tier,
    jsonb_build_object('root_id',encode(p.root_id,'hex'),'tier',p.tier,
      'coord_ewkb',encode(ST_AsEWKB(ST_MakePoint(p.coord[1],p.coord[2],p.coord[3],p.coord[4])),'hex'),
      'hilbert_index',encode(p.hilbert_index,'hex'),
      'radius_origin_bits',encode(float8send(p.radius_origin),'hex')) AS placement
  FROM converse.text_root_placements(COALESCE((
    SELECT array_agg(convert_from(t.canonical_utf8,'UTF8') ORDER BY t.ord) FROM texts t),
    ARRAY[]::text[])) p JOIN texts t USING(ord)
), testimony AS MATERIALIZED (
  SELECT requested.owner_id,requested.child_id,
    count(a.id) AS n,
    COALESCE(bool_or(COALESCE(a.context_id IS NULL AND a.outcome=2 AND a.observation_count>0
      AND a.sum_score_fp1e9::numeric>a.observation_count::numeric*500000000,false)),false) AS global_positive,
    COALESCE(bool_and(COALESCE(a.outcome=2 AND a.observation_count>0
      AND a.sum_score_fp1e9::numeric>a.observation_count::numeric*500000000,false)),false) AS all_positive,
    COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.id,to_jsonb(a))
      FILTER(WHERE a.id IS NOT NULL),'[]'::jsonb) AS witnesses
  FROM (SELECT DISTINCT owner_id,child_id FROM decoded WHERE child_id IS NOT NULL) requested
  LEFT JOIN laplace.attestations a ON a.subject_id=requested.owner_id
    AND a.object_id=requested.child_id AND a.type_id=laplace.relation_type_id('HAS_NAME_ALIAS')
  GROUP BY requested.owner_id,requested.child_id
), checked AS MATERIALIZED (
  SELECT d.pair,d.old_id,d.owner_id,d.owner_pair_count,d.role,d.child_id,
    o.entities AS owner_entities,
    COALESCE(d.singleton AND o.n=1 AND o.is_player AND ce.n=1 AND cc.n=1 AND cc.valid_content
      AND d.carrier->>'trajectory_ewkb'=CASE WHEN d.child_id IS NOT NULL
        THEN encode(ST_AsEWKB(public.laplace_mantissa_pack(d.child_id,1,1,0::bigint)),'hex') END
      AND d.carrier->>'entity_id'=encode(d.owner_id,'hex')
      AND d.carrier->>'id'=encode(public.laplace_hash128_blake3(d.owner_id||
        CASE WHEN d.role='source_alias' THEN decode('0100','hex') ELSE decode('0300','hex') END),'hex')
      AND d.carrier->>'type'=CASE WHEN d.role='source_alias' THEN '1' ELSE '3' END
      AND d.carrier->'alignment_residual_bits'='null'::jsonb
      AND d.carrier->'source_dim'='null'::jsonb
      AND cc.contents->0->>'id'=encode(public.laplace_hash128_blake3(d.child_id||decode('0100','hex')),'hex')
      AND cc.contents->0->>'entity_id'=encode(d.child_id,'hex')
      AND cc.contents->0->>'type'='1'
      AND cc.contents->0->'alignment_residual_bits'='null'::jsonb
      AND cc.contents->0->'source_dim'='null'::jsonb
      AND r.canonical_utf8 IS NOT NULL AND octet_length(r.canonical_utf8)>0
      AND p.root_id=d.child_id AND p.tier=ce.tier
      AND p.placement->>'coord_ewkb'=d.carrier->>'coord_ewkb'
      AND p.placement->>'hilbert_index'=d.carrier->>'hilbert_index'
      AND p.placement->>'radius_origin_bits'=d.carrier->>'radius_origin_bits'
      AND p.placement->>'coord_ewkb'=cc.contents->0->>'coord_ewkb'
      AND p.placement->>'hilbert_index'=cc.contents->0->>'hilbert_index'
      AND p.placement->>'radius_origin_bits'=cc.contents->0->>'radius_origin_bits'
      AND t.n>0 AND t.global_positive AND t.all_positive,false) AS side_valid,
    jsonb_build_object('child_id',encode(d.child_id,'hex'),
      'logical_occurrences',d.occurrences,'singleton',d.singleton,
      'entity',CASE WHEN ce.n=1 THEN ce.entities->0 ELSE NULL END,
      'entity_rows',ce.n,'entities',COALESCE(ce.entities,'[]'::jsonb),
      'content',CASE WHEN cc.n=1 THEN cc.contents->0 ELSE NULL END,
      'content_rows',cc.n,'content_structure_valid',cc.valid_content,
      'contents',COALESCE(cc.contents,'[]'::jsonb),
      'canonical_utf8_hex',encode(r.canonical_utf8,'hex'),
      'native_placement',p.placement,
      'has_name_alias_testimony',COALESCE(t.witnesses,'[]'::jsonb)) AS alias_evidence
  FROM decoded d JOIN owners o USING(owner_id)
  LEFT JOIN child_entities ce ON ce.child_id=d.child_id
  LEFT JOIN child_contents cc ON cc.child_id=d.child_id
  LEFT JOIN rendered r ON r.child_id=d.child_id
  LEFT JOIN placements p ON p.canonical_utf8=r.canonical_utf8
  LEFT JOIN testimony t ON t.owner_id=d.owner_id AND t.child_id=d.child_id
)
SELECT c.old_id,COALESCE(count(*)=2 AND bool_and(c.side_valid)
    AND max(c.owner_pair_count)=1 AND count(DISTINCT c.child_id)=2
    AND bool_and(c.child_id<>c.owner_id),false) AS valid,
  jsonb_build_object('schema','laplace.witnessed-player-alias-proof/v1',
    'original',c.pair->'original','existing_target',c.pair->'existing_target',
    'owner_entities',(jsonb_agg(c.owner_entities ORDER BY c.role))->0,
    'source_alias',(jsonb_agg(c.alias_evidence) FILTER(WHERE c.role='source_alias'))->0,
    'target_alias',(jsonb_agg(c.alias_evidence) FILTER(WHERE c.role='target_alias'))->0) AS evidence
FROM checked c GROUP BY c.pair,c.old_id ORDER BY c.old_id,c.pair
$player_alias_proofs$;
"""


def plan_sql(max_rows: int, prior_paths: list[Path], producer_generation: dict | None = None, *,
             resource_preamble: bool = False, deadline: float | None = None,
             max_prior_bytes: int = MAX_BYTES, max_line_bytes: int = MAX_LINE_BYTES,
             prior_budget: PriorReceiptBudget | None = None) -> str:
    if type(max_rows) is not int or not 1 <= max_rows <= 100000:
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
LANGUAGE sql IMMUTABLE STRICT AS $snapshot$
  SELECT CASE WHEN p.id IS NULL THEN NULL::jsonb ELSE {snapshot_expression('p')} END
$snapshot$;
{player_alias_proof_sql()}
{prior_sql(prior_paths, deadline=deadline, max_bytes=max_prior_bytes, max_line_bytes=max_line_bytes, budget=prior_budget)}

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
ANALYZE repair_owner_inventory;
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
ANALYZE repair_owner_bounds;
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
ANALYZE repair_owner_identity;
CREATE TEMP TABLE repair_owners ON COMMIT DROP AS
SELECT o.* FROM repair_owner_inventory o JOIN repair_owner_identity i ON i.id=o.id
WHERE NOT COALESCE(i.well_formed,false) OR i.content_id IS DISTINCT FROM o.entity_id
ORDER BY o.id LIMIT {max_rows + 1};
ANALYZE repair_owners;
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
ANALYZE repair_carriers;
CREATE TEMP TABLE repair_packed ON COMMIT DROP AS
SELECT p.id AS physicality_id,c.ordinal,c.entity_id,c.run_length,c.flags
FROM repair_carriers p CROSS JOIN LATERAL public.laplace_trajectory_constituents(
  CASE WHEN p.n_constituents BETWEEN 1 AND {MAX_CONSTITUENTS}
         AND ST_NPoints(p.trajectory) BETWEEN 1 AND {MAX_CONSTITUENTS}
       THEN p.trajectory ELSE NULL END) c;
ANALYZE repair_packed;
CREATE TEMP TABLE repair_bounded ON COMMIT DROP AS
SELECT p.id
FROM repair_carriers p JOIN repair_packed v ON v.physicality_id=p.id
GROUP BY p.id,p.n_constituents,p.trajectory
HAVING count(*)=ST_NPoints(p.trajectory)
   AND sum(GREATEST(v.run_length,1))=p.n_constituents
   AND min(v.ordinal)=1
   AND max(v.ordinal+GREATEST(v.run_length,1)-1)=p.n_constituents;
ANALYZE repair_bounded;
CREATE TEMP TABLE repair_logical ON COMMIT DROP AS
SELECT p.id AS physicality_id,v.ordinal,v.entity_id,v.flags
FROM repair_bounded b JOIN repair_carriers p ON p.id=b.id
CROSS JOIN LATERAL public.laplace_trajectory_expanded_constituents(p.trajectory) v;
ANALYZE repair_logical;
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
ANALYZE repair_manifests;

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
ANALYZE repair_candidates;

-- Different player aliases have one independent native proof per exact pair.
-- The same routine rechecks captured bytes/testimony before/after mutation and
-- when reconciling a retained prior action. It elects no display-name winner.
CREATE TEMP TABLE repair_player_alias_evidence ON COMMIT DROP AS
SELECT proof.* FROM pg_temp.repair_player_alias_proofs(COALESCE((
  SELECT jsonb_agg(jsonb_build_object('original',pg_temp.repair_snapshot(source),
                                    'existing_target',pg_temp.repair_snapshot(target)))
  FROM repair_candidates c
  JOIN laplace.physicalities source ON source.id=c.id
  JOIN laplace.physicalities target
    ON target.id=public.laplace_hash128_blake3(c.entity_id||decode('0300','hex'))
  WHERE c.repair_kind='player-projection' AND c.projection_rows=1
    AND jsonb_array_length(c.occupied_projection_evidence)=1
    AND target.entity_id=c.entity_id AND target.type=3
    AND (pg_temp.repair_snapshot(target)-ARRAY['observed_at','observed_at_binary']) IS DISTINCT FROM
      ((pg_temp.repair_snapshot(source)||jsonb_build_object('id',encode(target.id,'hex'),'type',3))
        -ARRAY['observed_at','observed_at_binary'])),'[]'::jsonb)) proof;
CREATE UNIQUE INDEX repair_player_alias_old_identity ON repair_player_alias_evidence(old_id);
ANALYZE repair_player_alias_evidence;

-- Membership uses the existing indexed native trajectory ID extraction. Keep
-- complete incoming row images: changing/removing one realization must not
-- silently invalidate a containing Content realization.
CREATE TEMP TABLE repair_incoming ON COMMIT DROP AS
SELECT p.id,p.entity_id,public.laplace_trajectory_constituent_ids(p.trajectory) AS members,
       pg_temp.repair_snapshot(p) AS original
FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
  AND public.laplace_trajectory_constituent_ids(p.trajectory)
      && ARRAY(SELECT entity_id FROM repair_owners);
ANALYZE repair_incoming;

-- One retained set of native coordinate/identity inputs, reused across all
-- affected parents. Occurrence order and duplicates remain in each manifest.
CREATE TEMP TABLE repair_native_input_ids ON COMMIT DROP AS
SELECT v.entity_id FROM repair_carriers carrier
  JOIN repair_logical v ON v.physicality_id=carrier.id
UNION SELECT start_id FROM repair_candidates WHERE repair_kind='chess-line' AND start_id IS NOT NULL
UNION SELECT decode(proof.evidence#>>'{{target_alias,child_id}}','hex')
  FROM repair_player_alias_evidence proof WHERE proof.evidence#>>'{{target_alias,child_id}}' IS NOT NULL;
CREATE UNIQUE INDEX repair_native_input_ids_identity ON repair_native_input_ids(entity_id);
ANALYZE repair_native_input_ids;
CREATE TEMP TABLE repair_native_inputs ON COMMIT DROP AS
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
FROM repair_native_input_ids n JOIN laplace.entities e ON e.id=n.entity_id
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

-- Resolve invalid dependencies once for the complete carrier set. Repeated
-- per-owner scans over all retained native snapshots do not add evidence.
ANALYZE repair_native_inputs;
CREATE TEMP TABLE repair_invalid_input_owners ON COMMIT DROP AS
SELECT DISTINCT carrier.entity_id
FROM repair_carriers carrier JOIN repair_logical occurrence ON occurrence.physicality_id=carrier.id
JOIN repair_native_inputs input ON input.entity_id=occurrence.entity_id
WHERE NOT COALESCE(input.valid_content,false);
CREATE UNIQUE INDEX repair_invalid_input_owner_identity ON repair_invalid_input_owners(entity_id);
ANALYZE repair_invalid_input_owners;

CREATE TEMP TABLE repair_eligibility ON COMMIT DROP AS
SELECT c.*,
  CASE WHEN c.entity_rows<>1 THEN 'duplicate-owner-identity'
       WHEN NOT COALESCE(c.resolved,false) THEN 'unresolved-or-malformed-original-manifest'
       WHEN c.id<>public.laplace_hash128_blake3(c.entity_id||decode('0100','hex')) THEN 'noncanonical-physicality-id'
       WHEN EXISTS(SELECT FROM repair_incoming parent WHERE c.entity_id=ANY(parent.members))
         THEN 'incoming-content-requires-coupled-recovery'
       WHEN EXISTS(SELECT FROM repair_invalid_input_owners invalid
                    WHERE invalid.entity_id=c.entity_id) THEN 'invalid-native-input-content'
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
              ELSE 'eligible' END
       WHEN c.repair_kind='session-projection' THEN
         CASE WHEN NOT c.all_messages OR c.witnessed_messages<>(SELECT count(DISTINCT id) FROM unnest(c.child_ids) id)
                THEN 'missing-exact-confirmed-session-membership'
              WHEN EXISTS(SELECT FROM jsonb_array_elements(c.membership_witnesses) a
                WHERE (a->>'outcome')::integer<>2 OR (a->>'observation_count')::bigint<=0
                   OR (a->>'sum_score_fp1e9')::numeric<=(a->>'observation_count')::numeric*500000000)
                THEN 'opposing-session-membership'
              ELSE 'eligible' END
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
       'rewrite'::text AS action,NULL::jsonb AS existing_target,NULL::jsonb AS migration_proposal,
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
DO $proposed_rows$
DECLARE expected bigint; captured bigint;
BEGIN
  SELECT count(*) INTO expected FROM repair_plan;
  UPDATE repair_plan p SET proposed=p.original || jsonb_build_object(
  'id',encode(p.new_id,'hex'),'type',p.new_type,
  'coord_ewkb',encode(ST_AsEWKB(p.new_coord),'hex'),
  'hilbert_index',encode(p.new_hilbert,'hex'),
  'trajectory_ewkb',encode(ST_AsEWKB(p.new_trajectory),'hex'),
  'radius_origin_bits',CASE WHEN p.repair_kind='chess-line'
    THEN encode(float8send(public.laplace_radius_origin(p.new_coord)),'hex')
    ELSE p.original->>'radius_origin_bits' END,
  'n_constituents',p.new_count)
  FROM repair_eligibility admitted WHERE admitted.id=p.old_id AND p.proposed IS NULL;
  GET DIAGNOSTICS captured=ROW_COUNT;
  IF captured<>expected OR EXISTS(SELECT FROM repair_plan WHERE proposed IS NULL) THEN
    RAISE EXCEPTION 'Proposed row snapshots covered % planned rows, expected %',captured,expected;
  END IF;
END $proposed_rows$;

-- Reuse is a distinct witnessed operation. Every semantic field must match
-- the source-derived migration; only observation timestamps may differ. Keep
-- the existing row unchanged and retain both exact prestate records.
UPDATE repair_plan SET migration_proposal=proposed WHERE disposition='eligible';
UPDATE repair_plan p SET existing_target=c.occupied_projection_evidence->0
FROM repair_eligibility c
WHERE c.id=p.old_id AND p.disposition='eligible'
  AND p.repair_kind IN ('player-projection','session-projection')
  AND c.projection_rows=1 AND jsonb_array_length(c.occupied_projection_evidence)=1
  AND c.occupied_projection_evidence->0->>'id'=encode(p.new_id,'hex')
  AND c.occupied_projection_evidence->0->>'entity_id'=encode(p.entity_id,'hex')
  AND c.occupied_projection_evidence->0->>'type'='3';
UPDATE repair_plan p SET disposition='occupied-projection-target'
FROM repair_eligibility c
WHERE c.id=p.old_id AND p.disposition='eligible'
  AND p.repair_kind IN ('player-projection','session-projection')
  AND c.projection_target_exists AND p.existing_target IS NULL;
UPDATE repair_plan SET disposition='projection-semantic-mismatch'
WHERE disposition='eligible' AND existing_target IS NOT NULL
  AND (migration_proposal-ARRAY['observed_at','observed_at_binary'])
    IS DISTINCT FROM (existing_target-ARRAY['observed_at','observed_at_binary']);
UPDATE repair_plan SET action='reuse-existing-projection',proposed=existing_target
WHERE disposition='eligible' AND existing_target IS NOT NULL;
UPDATE repair_plan p SET evidence=p.evidence||jsonb_build_object('player_alias_retention',proof.evidence)
FROM repair_player_alias_evidence proof WHERE proof.old_id=p.old_id;
UPDATE repair_plan p SET disposition='eligible',action='retain-witnessed-player-alias-projection',proposed=p.existing_target
FROM repair_player_alias_evidence proof WHERE proof.old_id=p.old_id AND proof.valid
  AND p.repair_kind='player-projection' AND p.disposition='projection-semantic-mismatch'
  AND p.existing_target IS NOT NULL;

""" + frozen_receipt_sql(f"""
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
""", """
SELECT jsonb_build_object('kind','plan','count',count(*),
  'native_input_count',(SELECT count(*) FROM repair_native_inputs),
  'rewritten_rows',count(*) FILTER(WHERE action='rewrite'),
  'reused_projection_rows',count(*) FILTER(WHERE action='reuse-existing-projection'),
  'retained_player_alias_rows',count(*) FILTER(WHERE action='retain-witnessed-player-alias-projection'),
  'unresolved',count(*) FILTER(WHERE disposition<>'eligible'),
  'classifications',COALESCE((SELECT jsonb_agg(to_jsonb(g)) FROM (
    SELECT repair_kind,disposition,count(*) AS rows FROM repair_plan GROUP BY repair_kind,disposition) g),'[]'::jsonb))
FROM repair_plan;
""", resource_preamble=resource_preamble)


def apply_sql() -> str:
    return """
CREATE FUNCTION pg_temp.repair_assert_immutable_evidence() RETURNS void
LANGUAGE plpgsql AS $immutable$
BEGIN
  IF EXISTS(SELECT FROM repair_native_inputs i
            LEFT JOIN laplace.entities e ON e.id=i.entity_id
            LEFT JOIN laplace.physicalities p ON p.id=i.physicality_id
            WHERE to_jsonb(e) IS DISTINCT FROM i.entity
               OR pg_temp.repair_snapshot(p) IS DISTINCT FROM i.content)
     OR EXISTS(SELECT ids.entity_id FROM repair_native_input_ids ids
               LEFT JOIN laplace.physicalities p ON p.entity_id=ids.entity_id AND p.type=1
               GROUP BY ids.entity_id HAVING count(p.id)<>1)
     OR EXISTS(SELECT ids.entity_id FROM repair_native_input_ids ids
               LEFT JOIN laplace.entities e ON e.id=ids.entity_id
               GROUP BY ids.entity_id HAVING count(e.id)<>1) THEN
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
  IF EXISTS(
    SELECT FROM repair_player_alias_evidence retained
    FULL JOIN pg_temp.repair_player_alias_proofs(COALESCE((
      SELECT jsonb_agg(jsonb_build_object('original',evidence->'original','existing_target',evidence->'existing_target'))
      FROM repair_player_alias_evidence),'[]'::jsonb)) current_proof USING(old_id)
    WHERE retained.valid IS DISTINCT FROM current_proof.valid
       OR retained.evidence IS DISTINCT FROM current_proof.evidence) THEN
    RAISE EXCEPTION 'Witnessed player alias evidence changed after its durable receipt';
  END IF;
END $immutable$;
CREATE TEMP TABLE repair_applied_epoch(epoch bigint) ON COMMIT DROP;
DO $apply$
DECLARE expected bigint; changed bigint; retired bigint;
BEGIN
  SELECT count(*) INTO expected FROM repair_plan;
  IF EXISTS(SELECT FROM repair_plan WHERE disposition<>'eligible') THEN
    RAISE EXCEPTION 'Repair eligibility changed before mutation';
  END IF;
  IF EXISTS(SELECT FROM repair_plan r LEFT JOIN laplace.physicalities p ON p.id=r.old_id
            WHERE pg_temp.repair_snapshot(p) IS DISTINCT FROM r.original) THEN
    RAISE EXCEPTION 'Original physicality changed after its durable receipt';
  END IF;
  IF EXISTS(SELECT FROM repair_plan r
            LEFT JOIN laplace.physicalities p ON p.id=r.new_id
            WHERE r.existing_target IS NOT NULL
              AND pg_temp.repair_snapshot(p) IS DISTINCT FROM r.existing_target) THEN
    RAISE EXCEPTION 'Existing projection changed after its durable receipt';
  END IF;
  IF EXISTS(SELECT FROM repair_plan r WHERE r.action NOT IN ('rewrite','reuse-existing-projection','retain-witnessed-player-alias-projection')
      OR (r.action='reuse-existing-projection' AND (
        r.repair_kind NOT IN ('player-projection','session-projection') OR r.old_id=r.new_id
        OR r.existing_target IS NULL OR r.proposed IS DISTINCT FROM r.existing_target
        OR (r.migration_proposal-ARRAY['observed_at','observed_at_binary'])
             IS DISTINCT FROM (r.existing_target-ARRAY['observed_at','observed_at_binary'])))) THEN
    RAISE EXCEPTION 'Projection reuse action changed after its durable receipt';
  END IF;
  IF EXISTS(SELECT FROM repair_plan r LEFT JOIN repair_player_alias_evidence proof ON proof.old_id=r.old_id
      WHERE r.action='retain-witnessed-player-alias-projection' AND (
        r.repair_kind<>'player-projection' OR r.old_id=r.new_id OR r.existing_target IS NULL
        OR NOT COALESCE(proof.valid,false) OR r.evidence->'player_alias_retention' IS DISTINCT FROM proof.evidence
        OR r.proposed IS DISTINCT FROM r.existing_target
        OR r.migration_proposal IS DISTINCT FROM (r.original||jsonb_build_object('id',encode(r.new_id,'hex'),'type',3)))) THEN
    RAISE EXCEPTION 'Witnessed player alias action changed after its durable receipt';
  END IF;
  PERFORM pg_temp.repair_assert_immutable_evidence();
  IF EXISTS(SELECT FROM repair_candidates c WHERE
      (SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id) FROM laplace.physicalities p
        WHERE (p.entity_id=c.entity_id AND p.type=3)
           OR p.id=public.laplace_hash128_blake3(c.entity_id||decode('0300','hex')))
        IS DISTINCT FROM c.occupied_projection_evidence)
     OR (SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id)
         FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
           AND public.laplace_trajectory_constituent_ids(p.trajectory)
               && ARRAY(SELECT entity_id FROM repair_owners))
        IS DISTINCT FROM (SELECT jsonb_agg(original ORDER BY id) FROM repair_incoming) THEN
    RAISE EXCEPTION 'Projection or incoming Content changed after its durable receipt';
  END IF;
  IF expected>0 THEN
    INSERT INTO repair_applied_epoch SELECT nextval('laplace.apply_write_epoch');
    UPDATE laplace.physicalities p SET id=r.new_id,type=r.new_type,coord=r.new_coord,
      hilbert_index=r.new_hilbert,trajectory=r.new_trajectory,n_constituents=r.new_count
    FROM repair_plan r WHERE p.id=r.old_id AND r.action='rewrite';
    GET DIAGNOSTICS changed=ROW_COUNT;
    DELETE FROM laplace.physicalities p USING repair_plan r
      WHERE p.id=r.old_id AND r.action IN ('reuse-existing-projection','retain-witnessed-player-alias-projection');
    GET DIAGNOSTICS retired=ROW_COUNT;
    changed:=changed+retired;
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
    PERFORM pg_temp.repair_assert_immutable_evidence();
    IF EXISTS(SELECT FROM repair_plan r JOIN repair_candidates c ON c.id=r.old_id WHERE
      (SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id) FROM laplace.physicalities p
        WHERE (p.entity_id=r.entity_id AND p.type=3)
           OR p.id=public.laplace_hash128_blake3(r.entity_id||decode('0300','hex')))
        IS DISTINCT FROM CASE WHEN r.repair_kind='chess-line' THEN c.occupied_projection_evidence
                              ELSE jsonb_build_array(r.proposed) END)
       OR (SELECT jsonb_agg(pg_temp.repair_snapshot(p) ORDER BY p.id)
           FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
             AND public.laplace_trajectory_constituent_ids(p.trajectory)
                 && ARRAY(SELECT entity_id FROM repair_owners))
          IS DISTINCT FROM (SELECT jsonb_agg(original ORDER BY id) FROM repair_incoming) THEN
      RAISE EXCEPTION 'Repair changed Projection occupation or incoming Content; rolling back';
    END IF;
  END IF;
END $apply$;
SELECT jsonb_build_object('kind','applied','count',(SELECT count(*) FROM repair_plan),
  'rewritten_rows',(SELECT count(*) FROM repair_plan WHERE action='rewrite'),
  'reused_projection_rows',(SELECT count(*) FROM repair_plan WHERE action='reuse-existing-projection'),
  'retained_player_alias_rows',(SELECT count(*) FROM repair_plan WHERE action='retain-witnessed-player-alias-projection'),
  'epoch',(SELECT epoch FROM repair_applied_epoch),'postconditions','exact-row-readback-and-native-content-proof');
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', default=os.environ.get('PGDATABASE', 'laplace'))
    parser.add_argument('--receipt-root', type=Path, default=Path('/build/laplace/recovery/legacy-content-repair'))
    parser.add_argument('--max-rows', type=positive_argument, default=MAX_ROWS)
    parser.add_argument('--max-native-inputs', type=positive_argument, default=MAX_NATIVE_INPUTS,
                        help='maximum complete child entity/Content snapshots in this receipt')
    parser.add_argument('--max-bytes', type=positive_argument, default=MAX_BYTES,
                        help='maximum UTF-8 JSONL bytes for one new or retained receipt')
    parser.add_argument('--max-line-bytes', type=positive_argument, default=MAX_LINE_BYTES,
                        help='maximum UTF-8 bytes per JSON record, excluding the newline')
    parser.add_argument('--max-prior-bytes', type=positive_argument, default=MAX_BYTES,
                        help='aggregate earlier plan bytes verified across discovery, native status, replay and closure; repeated reads count')
    parser.add_argument('--timeout-seconds', type=positive_argument, default=PHASE_TIMEOUT_SECONDS,
                        help='maintenance-wide deadline including historical receipt verification and database work')
    parser.add_argument('--persistence-timeout-seconds', type=positive_argument, default=PERSISTENCE_TIMEOUT_SECONDS,
                        help='maximum final evidence durability interval before APPLY')
    parser.add_argument('--auxiliary-reserve-bytes', type=nonnegative_argument, default=AUXILIARY_RESERVE_BYTES,
                        help='additional free receipt-filesystem allowance for receipts/logs beyond measured JSONL; default 64 MiB')
    parser.add_argument('--measurement-only', action='store_true',
                        help='retain complete resource measurement and confirm rollback without streaming or applying the plan')
    args = parser.parse_args()
    deadline = time.monotonic() + args.timeout_seconds
    prior_budget = PriorReceiptBudget(args.max_prior_bytes)
    spec = importlib.util.spec_from_file_location("repair_quiescence", ROOT / "scripts/quiesce-managed-database.py")
    quiescence = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(quiescence)
    resources = None
    producer_generation = None
    if args.database == "laplace":
        # Require the concrete installed-service exclusion receipt produced by
        # the lifecycle wrapper. It identifies the still-held root transaction;
        # a copied environment flag alone is not a quiescence claim.
        receipt = Path(os.environ.get("LAPLACE_DATABASE_QUIESCENCE_RECEIPT", "/missing"))
        if receipt.resolve().parent != Path("/build/laplace/recovery/legacy-content-service-quiescence"):
            raise ValueError("canonical repair requires the active managed service quiescence receipt")
        resources = quiescence.inherited_repair_resources(receipt, receipt_root=args.receipt_root,
            max_bytes=args.max_bytes, max_line_bytes=args.max_line_bytes,
            max_prior_bytes=args.max_prior_bytes, timeout_seconds=args.timeout_seconds)
        deadline, prior_budget = resources.deadline, resources
        retained = json.loads((receipt / "quiescence-confirmed.json").read_text())
        active_transaction = quiescence.transaction_identity(quiescence.STATE)
        if not isinstance(retained.get("transaction_identity"), dict) or active_transaction is None \
                or (receipt / "commit-submission.json").exists() \
                or retained["transaction_identity"] != active_transaction:
            raise ValueError("canonical repair's managed service exclusion is no longer active")
        producer_generation = json.loads((receipt / "prior-services.json").read_text()).get("producer_generation")
        resumed = any(receipt.glob('resume-confirmed-*.json'))
        if not isinstance(producer_generation, dict) or producer_generation != quiescence.published_application_generation(
                retained=producer_generation if resumed else None,
                verification_receipt=receipt / 'producer-application-verification.json'):
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
    directory = args.receipt_root / (str(time.time_ns()) + "-" + uuid.uuid4().hex)
    if args.database == 'laplace':
        quiescence.bind_repair_attempt(receipt, directory, source_sha=source_sha)
    pending = unresolved_submissions(args.receipt_root, max_bytes=args.max_bytes,
        max_line_bytes=args.max_line_bytes, budget=prior_budget, deadline=deadline)
    statuses = quiescence.database_submission_statuses(pending, command=command, deadline=deadline,
        max_bytes=args.max_bytes, max_line_bytes=args.max_line_bytes, budget=prior_budget)
    quiescence.require_finished_database_submissions(statuses)
    outcome = preserve_and_apply(command,
        plan_sql(args.max_rows, pending, producer_generation, resource_preamble=True,
            deadline=deadline, max_prior_bytes=args.max_bytes, max_line_bytes=args.max_line_bytes, prior_budget=prior_budget), apply_sql(), directory,
        source_sha=source_sha, max_rows=args.max_rows, max_native_inputs=args.max_native_inputs,
        max_bytes=args.max_bytes, max_line_bytes=args.max_line_bytes,
        timeout=args.timeout_seconds, persistence_timeout=args.persistence_timeout_seconds,
        receipt_sql=receipt_stream_sql(), measurement_only=args.measurement_only,
        auxiliary_reserve_bytes=args.auxiliary_reserve_bytes, deadline_monotonic=deadline,
        resource_validator=(lambda measured: resources.readback_rejections(
            directory, measured['plan_bytes'], reads=len(pending)+2)) if resources is not None else None)
    if not args.measurement_only:
        try:
            close_reconciled_submissions(args.receipt_root, directory, max_bytes=args.max_bytes,
                max_line_bytes=args.max_line_bytes, budget=prior_budget,
                current_budget=resources, deadline=deadline)
        except BaseException as error:
            write_new_json(directory / "postcommit-reconciliation-failure.json", {
                "disposition": "commit-confirmed-reconciliation-bookkeeping-failed",
                "plan_sha256": outcome["plan_sha256"], "error_type": type(error).__name__,
                "at_unix_nanoseconds": time.time_ns()})
            raise
    print(json.dumps({"receipt_directory": str(directory), "outcome": outcome}, sort_keys=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
