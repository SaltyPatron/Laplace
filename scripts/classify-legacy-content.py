#!/usr/bin/env python3
"""Exhaustively classify Content identity failures without changing the database.

The native expanded trajectory reader and canonical Merkle implementation own
decoding/hashing. This cold operator audit issues one set-sized snapshot query.
Examples are bounded within each entity-type/source stratum; counts are not.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]


def load_proof():
    spec = importlib.util.spec_from_file_location(
        "laplace_live_recursive_proof", ROOT / "scripts/prove-live-recursive-substrate.py")
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def failure_ctes() -> str:
    """Complete ordered expansion, including malformed empty trajectories."""
    return """
parents AS MATERIALIZED (
  SELECT p.id AS physicality_id,p.entity_id AS parent_id,p.n_constituents,p.trajectory
  FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
),
expanded AS MATERIALIZED (
  SELECT p.physicality_id,p.parent_id,p.n_constituents,u.ordinal,u.entity_id AS child_id
  FROM parents p
  LEFT JOIN LATERAL public.laplace_trajectory_expanded_constituents(p.trajectory) u ON true
),
per_parent AS MATERIALIZED (
  SELECT physicality_id,parent_id,max(n_constituents) AS n_constituents,
         count(child_id)::bigint AS expanded_count,
         array_agg(child_id ORDER BY ordinal) FILTER (WHERE child_id IS NOT NULL) AS child_ids
  FROM expanded GROUP BY physicality_id,parent_id
),
checked AS MATERIALIZED (
  SELECT *,CASE WHEN expanded_count=1 THEN child_ids[1]
                WHEN expanded_count>1 THEN public.laplace_hash128_merkle(0::smallint,child_ids)
                ELSE NULL::bytea END AS recomputed_id
  FROM per_parent
),
failed AS MATERIALIZED (
  SELECT * FROM checked
  WHERE expanded_count<>n_constituents OR recomputed_id IS DISTINCT FROM parent_id
)
"""


def recovery_dependency_ctes() -> str:
    """Complete dependency counts before repair eligibility/resource filters.

    Packed membership supplies distinct dependency IDs; logical occurrences stay
    in the existing exhaustive identity classification. Counts do not assert
    that a recipe is eligible or estimate the bytes of its retained receipt.
    """
    return """
recovery_types AS MATERIALIZED (
  SELECT public.laplace_hash128_blake3('Chess_Game'::bytea) AS game,
         public.laplace_hash128_blake3('Chess_Player'::bytea) AS player,
         public.laplace_hash128_blake3('Conversation_Session'::bytea) AS session
),
recovery_failed_parents AS MATERIALIZED (
  SELECT f.physicality_id,f.parent_id,
         bool_or(e.type_id=t.game) AS is_game,
         bool_or(e.type_id=t.player) AS is_player,
         bool_or(e.type_id=t.session) AS is_session
  FROM failed f JOIN laplace.entities e ON e.id=f.parent_id
  CROSS JOIN recovery_types t WHERE e.type_id IN (t.game,t.player,t.session)
  GROUP BY f.physicality_id,f.parent_id
),
recovery_parent_ids AS MATERIALIZED (
  SELECT parent_id,bool_or(is_game) AS is_game,bool_or(is_player) AS is_player,
         bool_or(is_session) AS is_session
  FROM recovery_failed_parents GROUP BY parent_id
),
recovery_carriers AS MATERIALIZED (
  SELECT p.id,p.entity_id,p.type,p.trajectory
  FROM recovery_parent_ids owner JOIN laplace.physicalities p ON p.entity_id=owner.parent_id
  WHERE p.type=1 OR (owner.is_game AND p.type=3)
),
recovery_carrier_members AS MATERIALIZED (
  SELECT p.id AS carrier_id,member.child_id
  FROM recovery_carriers p CROSS JOIN LATERAL
    unnest(public.laplace_trajectory_constituent_ids(p.trajectory)) member(child_id)
),
recovery_needed_ids AS MATERIALIZED (
  SELECT DISTINCT child_id FROM recovery_carrier_members
),
recovery_needed_entities AS MATERIALIZED (
  SELECT n.child_id,count(e.id)::bigint AS entity_rows
  FROM recovery_needed_ids n LEFT JOIN laplace.entities e ON e.id=n.child_id
  GROUP BY n.child_id
),
recovery_needed_content AS MATERIALIZED (
  SELECT n.child_id,count(p.id)::bigint AS content_rows,
         count(p.id) FILTER(WHERE p.id<>public.laplace_hash128_blake3(
           n.child_id||decode('0100','hex')))::bigint AS noncanonical_content_rows
  FROM recovery_needed_ids n LEFT JOIN laplace.physicalities p ON p.entity_id=n.child_id AND p.type=1
  GROUP BY n.child_id
),
recovery_needed_rows AS MATERIALIZED (
  SELECT e.child_id,e.entity_rows,p.content_rows,p.noncanonical_content_rows
  FROM recovery_needed_entities e JOIN recovery_needed_content p USING(child_id)
),
recovery_game_projections AS MATERIALIZED (
  SELECT owner.parent_id,count(p.id)::bigint AS projection_rows,
    count(p.id) FILTER(WHERE p.id<>public.laplace_hash128_blake3(
      owner.parent_id||decode('0300','hex')))::bigint AS noncanonical_projection_rows
  FROM recovery_parent_ids owner LEFT JOIN laplace.physicalities p
    ON p.entity_id=owner.parent_id AND p.type=3
  WHERE owner.is_game GROUP BY owner.parent_id
),
recovery_projection_destinations AS MATERIALIZED (
  SELECT owner.parent_id,p.id,p.entity_id,p.type,
    p.id=public.laplace_hash128_blake3(owner.parent_id||decode('0300','hex')) AS canonical_target
  FROM recovery_parent_ids owner JOIN laplace.physicalities p
    ON (p.entity_id=owner.parent_id AND p.type=3)
       OR p.id=public.laplace_hash128_blake3(owner.parent_id||decode('0300','hex'))
  WHERE owner.is_player OR owner.is_session
),
recovery_incoming_content AS MATERIALIZED (
  SELECT p.id,p.entity_id,public.laplace_trajectory_constituent_ids(p.trajectory) AS member_ids
  FROM laplace.physicalities p WHERE p.type=1 AND p.trajectory IS NOT NULL
    AND public.laplace_trajectory_constituent_ids(p.trajectory)
        && ARRAY(SELECT parent_id FROM recovery_parent_ids)
),
recovery_incoming_matches AS MATERIALIZED (
  SELECT p.id AS container_id,owner.parent_id
  FROM recovery_incoming_content p JOIN recovery_parent_ids owner
    ON owner.parent_id=ANY(p.member_ids)
)
"""


def recovery_dependency_json() -> str:
    return """jsonb_build_object(
    'schema','laplace.legacy-content-recovery-dependencies/v1',
    'scope','Complete failed typed parent dependency set before repair eligibility and resource filters; counts do not establish admissibility or receipt byte size.',
    'failed_typed_parent_physicalities',(SELECT count(*) FROM recovery_failed_parents),
    'failed_typed_parent_ids',(SELECT count(*) FROM recovery_parent_ids),
    'carriers',jsonb_build_object(
      'physicality_rows',(SELECT count(*) FROM recovery_carriers),
      'content_rows',(SELECT count(*) FROM recovery_carriers WHERE type=1),
      'game_position_projection_rows',(SELECT count(*) FROM recovery_carriers WHERE type=3),
      'null_trajectory_rows',(SELECT count(*) FROM recovery_carriers WHERE trajectory IS NULL),
      'empty_trajectory_rows',(SELECT count(*) FROM recovery_carriers WHERE ST_IsEmpty(trajectory)),
      'distinct_carrier_child_memberships',(SELECT count(*) FROM recovery_carrier_members)),
    'native_inputs',jsonb_build_object(
      'distinct_needed_child_ids',(SELECT count(*) FROM recovery_needed_ids),
      'joined_entity_content_snapshot_rows',(SELECT COALESCE(sum(entity_rows::numeric*content_rows::numeric),0) FROM recovery_needed_rows),
      'entity_rows',(SELECT COALESCE(sum(entity_rows),0) FROM recovery_needed_rows),
      'content_rows',(SELECT COALESCE(sum(content_rows),0) FROM recovery_needed_rows),
      'missing_entity_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE entity_rows=0),
      'missing_content_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE content_rows=0),
      'missing_either_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE entity_rows=0 OR content_rows=0),
      'multiple_entity_row_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE entity_rows>1),
      'multiple_content_row_id_count',(SELECT count(*) FROM recovery_needed_rows WHERE content_rows>1),
      'noncanonical_content_rows',(SELECT COALESCE(sum(noncanonical_content_rows),0) FROM recovery_needed_rows)),
    'game_position_projections',jsonb_build_object(
      'game_ids',(SELECT count(*) FROM recovery_game_projections),
      'missing_projection_game_ids',(SELECT count(*) FROM recovery_game_projections WHERE projection_rows=0),
      'multiple_projection_game_ids',(SELECT count(*) FROM recovery_game_projections WHERE projection_rows>1),
      'noncanonical_projection_rows',(SELECT COALESCE(sum(noncanonical_projection_rows),0) FROM recovery_game_projections)),
    'player_session_destinations',jsonb_build_object(
      'parent_ids',(SELECT count(*) FROM recovery_parent_ids WHERE is_player OR is_session),
      'occupied_parent_ids',(SELECT count(DISTINCT parent_id) FROM recovery_projection_destinations),
      'occupied_parent_row_pairs',(SELECT count(*) FROM recovery_projection_destinations),
      'distinct_occupied_physicalities',(SELECT count(DISTINCT id) FROM recovery_projection_destinations),
      'canonical_target_rows',(SELECT count(*) FROM recovery_projection_destinations WHERE canonical_target),
      'noncanonical_projection_rows',(SELECT count(*) FROM recovery_projection_destinations WHERE NOT canonical_target),
      'canonical_target_identity_or_type_conflicts',(SELECT count(*) FROM recovery_projection_destinations
        WHERE canonical_target AND (entity_id<>parent_id OR type<>3))),
    'incoming_content',jsonb_build_object(
      'container_physicalities',(SELECT count(*) FROM recovery_incoming_content),
      'container_entity_ids',(SELECT count(DISTINCT entity_id) FROM recovery_incoming_content),
      'affected_failed_parent_ids',(SELECT count(DISTINCT parent_id) FROM recovery_incoming_matches),
      'container_parent_pairs',(SELECT count(*) FROM recovery_incoming_matches))
  )"""


def classification_sql(sample_limit: int) -> str:
    if not 1 <= sample_limit <= 100:
        raise ValueError("sample_limit must be between 1 and 100")
    return "WITH " + failure_ctes() + f""",
entity_metadata AS MATERIALIZED (
  SELECT f.parent_id,count(e.id)::int AS entity_rows,
         (array_agg(e.type_id ORDER BY e.tier))[1] AS type_id,
         (array_agg(e.first_observed_by ORDER BY e.tier))[1] AS source_id,
         min(e.created_at) AS first_created_at,max(e.created_at) AS last_created_at,
         COALESCE(jsonb_agg(to_jsonb(e) ORDER BY e.tier)
             FILTER (WHERE e.id IS NOT NULL),'[]'::jsonb) AS entity_records
  FROM (SELECT DISTINCT parent_id FROM failed) f
  LEFT JOIN laplace.entities e ON e.id=f.parent_id
  GROUP BY f.parent_id
),
classified AS MATERIALIZED (
  SELECT f.*,m.entity_rows,m.type_id,m.source_id,m.first_created_at,
         m.last_created_at,m.entity_records,CASE
    WHEN m.entity_rows<>1 THEN 'missing_or_duplicate_entity'
    WHEN f.expanded_count<>f.n_constituents THEN 'expansion_count_mismatch'
    WHEN f.expanded_count=0 THEN 'empty_content_trajectory'
    WHEN m.type_id=public.laplace_hash128_blake3('Chess_Game'::bytea)
      THEN 'chess_line_identity_mismatch'
    WHEN m.type_id=public.laplace_hash128_blake3('Chess_Player'::bytea)
      AND f.expanded_count=1 THEN 'player_singleton_content'
    WHEN m.type_id=public.laplace_hash128_blake3('Conversation_Session'::bytea)
      THEN 'stable_session_content'
    ELSE 'unclassified_identity_mismatch' END AS failure_class
  FROM failed f JOIN entity_metadata m USING (parent_id)
),
numbered AS MATERIALIZED (
  SELECT *,row_number() OVER (
    PARTITION BY type_id,source_id,failure_class ORDER BY physicality_id) AS example_number
  FROM classified
),
examples AS MATERIALIZED (
  SELECT n.type_id,n.source_id,n.failure_class,n.example_number,
    jsonb_build_object(
      'parent_id',encode(n.parent_id,'hex'),
      'physicality_id',encode(n.physicality_id,'hex'),
      'recomputed_id',encode(n.recomputed_id,'hex'),
      'n_constituents',n.n_constituents,'expanded_count',n.expanded_count,
      'entity_records',n.entity_records,
      'physicality_fingerprint',encode(public.laplace_hash128_blake3(
        convert_to(to_jsonb(p)::text,'UTF8')),'hex')) AS example
  FROM numbered n JOIN laplace.physicalities p ON p.id=n.physicality_id
  WHERE n.example_number<={sample_limit}
),
strata AS MATERIALIZED (
  SELECT type_id,source_id,failure_class,count(*)::bigint AS failures,
    min(first_created_at) AS earliest_entity_created_at,
    max(last_created_at) AS latest_entity_created_at,
    min(expanded_count) AS minimum_expanded_count,
    max(expanded_count) AS maximum_expanded_count
  FROM classified GROUP BY type_id,source_id,failure_class
),
{recovery_dependency_ctes()}
SELECT jsonb_build_object(
  'schema','laplace.legacy-content-classification/v1',
  'database',current_database(),'observed_at',clock_timestamp(),
  'transaction_read_only',current_setting('transaction_read_only'),
  'transaction_isolation',current_setting('transaction_isolation'),
  'server_version',current_setting('server_version'),
  'substrate_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'),
  'geometry_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_geom'),
  'parents_checked',(SELECT count(*) FROM checked),
  'identity_or_expansion_failures',(SELECT count(*) FROM failed),
  'examples_per_stratum',{sample_limit},
  'recovery_dependency_envelope',{recovery_dependency_json()},
  'strata',COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'entity_type_id',encode(s.type_id,'hex'),'source_id',encode(s.source_id,'hex'),
    'failure_class',s.failure_class,'failures',s.failures,
    'earliest_entity_created_at',s.earliest_entity_created_at,
    'latest_entity_created_at',s.latest_entity_created_at,
    'minimum_expanded_count',s.minimum_expanded_count,
    'maximum_expanded_count',s.maximum_expanded_count,
    'examples',(SELECT jsonb_agg(e.example ORDER BY e.example_number) FROM examples e
       WHERE e.type_id IS NOT DISTINCT FROM s.type_id
         AND e.source_id IS NOT DISTINCT FROM s.source_id
         AND e.failure_class=s.failure_class))
    ORDER BY s.type_id,s.source_id,s.failure_class) FROM strata s),'[]'::jsonb)
)::text;
"""


def retained_json(path: Path, value: dict) -> None:
    """Create an immutable receipt, flushing its bytes before reporting success."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", nargs="?", default=os.environ.get("PGDATABASE", "laplace"))
    parser.add_argument("--examples-per-stratum", type=int, default=10)
    parser.add_argument("--timeout-seconds", type=int, default=1800)
    parser.add_argument("--receipt", type=Path)
    parser.add_argument("--emit-sql", action="store_true", help="Print the read-only query without connecting")
    args = parser.parse_args(argv)
    if args.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    try:
        sql = classification_sql(args.examples_per_stratum)
    except ValueError as exc:
        parser.error(str(exc))
    sql = ("BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;\n"
           "SET LOCAL timezone='UTC';\nSET LOCAL extra_float_digits=3;\n"
           f"SET LOCAL statement_timeout='{args.timeout_seconds}s';\n" + sql + "\nCOMMIT;\n")
    if args.emit_sql:
        print(sql)
        return 0
    proof = load_proof()
    path = args.receipt or ROOT / "build/test-receipts" / f"legacy-content-classification-{time.time_ns()}.json"
    if path.exists():
        parser.error(f"receipt already exists: {path}")
    report = {
        "schema": "laplace.legacy-content-classification-run/v1",
        "source_sha": proof.git_sha(),
        "implementation_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "sql_sha256": hashlib.sha256(sql.encode()).hexdigest(),
        "started_unix_nanoseconds": time.time_ns(),
        "scope": "Read-only exhaustive Content identity classification; classes are diagnostic hypotheses, not repair authorization.",
    }
    exit_code = 1
    try:
        proc = subprocess.run(proof.psql_argv(args.database) + ["-w"], input=sql,
            cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=args.timeout_seconds + 30)
        report["psql_exit_code"] = proc.returncode
        if proc.returncode:
            raise RuntimeError(proc.stderr.strip() or "classification query failed")
        result = proof.parse_single_json_document(proc.stdout)
        if result.get("transaction_read_only") != "on":
            raise RuntimeError("classification did not attest a read-only transaction")
        if sum(row["failures"] for row in result["strata"]) != result["identity_or_expansion_failures"]:
            raise RuntimeError("classification stratum counts do not cover the exhaustive failure count")
        report.update(status="classified", live=result)
        exit_code = 0
    except (OSError, RuntimeError, subprocess.TimeoutExpired, ValueError, KeyError) as exc:
        report.update(status="failed", error=str(exc))
    report["finished_unix_nanoseconds"] = time.time_ns()
    retained_json(path, report)
    print(f"legacy Content classification: {report['status']}; receipt={path}")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
