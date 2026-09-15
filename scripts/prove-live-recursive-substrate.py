#!/usr/bin/env python3
"""Read-only live proof/falsification gate for recursive Laplace physicality.

The bounded-composition theorem is mathematical; this program does not pretend a
finite database scan proves it.  It exhaustively checks the selected installed
finite content estate against the current executable representation contract.

Packed GeometryZM trajectory values are never interpreted as child positions.
Every child geometry check first decodes the child id and resolves the child's
live content physicality.  Repeated work is set-sized SQL/native SRF work; Python
does not issue one query per entity or constituent.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_RECEIPT = ROOT / "build" / "test-receipts" / "live-recursive-substrate.json"


def git_sha() -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, check=True, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    ).stdout.strip()


def psql_argv(database: str) -> list[str]:
    return [
        "psql", "-X",
        "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
        "-p", os.environ.get("PGPORT", "5432"),
        "-U", os.environ.get("PGUSER", "laplace_admin"),
        "-d", database,
        "-v", "ON_ERROR_STOP=1", "-A", "-t", "-q",
    ]


def parse_single_json_document(output: str) -> dict[str, Any]:
    """Parse exactly one JSON document regardless of physical line wrapping.

    PostgreSQL's textual JSON aggregates may span multiple stdout lines while still
    being one SQL value.  Line count is therefore not row count.  Decode one complete
    JSON document and fail closed if any second value or other non-whitespace output
    follows it.
    """
    payload = output.strip()
    if not payload:
        raise RuntimeError("proof SQL returned no JSON")
    decoder = json.JSONDecoder()
    try:
        value, end = decoder.raw_decode(payload)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"proof SQL returned invalid JSON: {payload[:500]!r}") from exc
    trailing = payload[end:].strip()
    if trailing:
        raise RuntimeError(
            f"proof SQL returned trailing output after one JSON document: {trailing[:500]!r}"
        )
    if not isinstance(value, dict):
        raise RuntimeError("proof SQL JSON root is not an object")
    return value


def query_json(database: str, sql: str, timeout_seconds: int) -> dict[str, Any]:
    wrapped = (
        "SET default_transaction_read_only=on;\n"
        f"SET statement_timeout='{timeout_seconds}s';\n"
        + sql.strip()
    )
    started = time.monotonic_ns()
    try:
        proc = subprocess.run(
            psql_argv(database) + ["-c", wrapped], cwd=ROOT, text=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            timeout=timeout_seconds + 30,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"proof SQL timed out after {timeout_seconds}s") from exc
    elapsed_ms = (time.monotonic_ns() - started) // 1_000_000
    if proc.returncode != 0:
        raise RuntimeError(
            f"proof SQL failed\nstdout:\n{proc.stdout}\nstderr:\n{proc.stderr}"
        )
    value = parse_single_json_document(proc.stdout)
    value["wall_milliseconds"] = int(elapsed_ms)
    return value


def metadata_sql() -> str:
    return r"""
SELECT json_build_object(
  'database', current_database(),
  'observed_at', clock_timestamp(),
  'server_version', current_setting('server_version'),
  'substrate_extension_version',
      (SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'),
  'geometry_extension_version',
      (SELECT extversion FROM pg_extension WHERE extname='laplace_geom'),
  'apply_write_epoch',
      CASE WHEN to_regclass('laplace.apply_write_epoch') IS NULL THEN NULL
           ELSE (SELECT last_value FROM laplace.apply_write_epoch) END,
  'entities', (SELECT count(*) FROM laplace.entities),
  'physicalities', (SELECT count(*) FROM laplace.physicalities),
  'content_physicalities',
      (SELECT count(*) FROM laplace.physicalities WHERE type=1),
  'content_trajectory_physicalities',
      (SELECT count(*) FROM laplace.physicalities WHERE type=1 AND trajectory IS NOT NULL)
)::text;
"""


def storage_sql(tolerance: float, sample_limit: int) -> str:
    return f"""
WITH
entity_dupes AS MATERIALIZED (
  SELECT id, count(*) AS rows, min(tier) AS min_tier, max(tier) AS max_tier
  FROM laplace.entities GROUP BY id HAVING count(*) > 1
),
content_dupes AS MATERIALIZED (
  SELECT entity_id, count(*) AS rows
  FROM laplace.physicalities WHERE type=1
  GROUP BY entity_id HAVING count(*) > 1
),
parent_fail AS MATERIALIZED (
  SELECT entity_id, radius_origin, n_constituents,
         trajectory IS NOT NULL AS has_trajectory
  FROM laplace.physicalities
  WHERE type=1 AND (
      radius_origin IS NULL
      OR NOT (radius_origin >= 0.0 AND radius_origin <= 1.0 + {tolerance:.17g})
      OR (trajectory IS NULL AND n_constituents <> 0)
      OR (trajectory IS NOT NULL AND n_constituents = 0)
  )
)
SELECT json_build_object(
  'schema','laplace.proof.recursive-storage/v1',
  'tolerance',{tolerance:.17g},
  'duplicate_entity_ids',(SELECT count(*) FROM entity_dupes),
  'duplicate_content_physicality_entities',(SELECT count(*) FROM content_dupes),
  'parent_contract_failures',(SELECT count(*) FROM parent_fail),
  'max_content_radius',(SELECT max(radius_origin) FROM laplace.physicalities WHERE type=1),
  'entity_duplicate_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(id,'hex') AS id, rows, min_tier, max_tier
      FROM entity_dupes ORDER BY id LIMIT {sample_limit}) x),'[]'::json),
  'content_physicality_duplicate_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(entity_id,'hex') AS entity_id, rows
      FROM content_dupes ORDER BY entity_id LIMIT {sample_limit}) x),'[]'::json),
  'parent_failure_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(entity_id,'hex') AS entity_id, radius_origin,
             n_constituents, has_trajectory
      FROM parent_fail ORDER BY entity_id LIMIT {sample_limit}) x),'[]'::json)
)::text;
"""


def trajectory_sql(tolerance: float, sample_limit: int) -> str:
    return f"""
WITH
parents AS MATERIALIZED (
  SELECT p.id AS physicality_id, p.entity_id AS parent_id,
         p.n_constituents, p.trajectory, e.tier::int AS parent_tier
  FROM laplace.physicalities p
  JOIN laplace.entities e ON e.id=p.entity_id
  WHERE p.type=1 AND p.trajectory IS NOT NULL
),
packed AS MATERIALIZED (
  SELECT p.physicality_id,p.parent_id,p.n_constituents,p.parent_tier,
         c.ordinal,c.entity_id AS child_id,
         GREATEST(c.run_length,1)::bigint AS run_length,c.flags,
         realize.vertex_tier(c.flags)::int AS child_tier
  FROM parents p
  CROSS JOIN LATERAL public.laplace_trajectory_constituents(p.trajectory) c
),
per_parent AS MATERIALIZED (
  SELECT physicality_id,parent_id,n_constituents,
         count(*)::bigint AS packed_vertices,
         COALESCE(sum(run_length),0)::bigint AS logical_constituents,
         min(ordinal) AS min_ordinal,max(ordinal) AS max_ordinal
  FROM packed GROUP BY physicality_id,parent_id,n_constituents
),
count_fail AS MATERIALIZED (
  SELECT * FROM per_parent
  WHERE logical_constituents <> n_constituents OR packed_vertices <= 0
),
non_descending AS MATERIALIZED (
  SELECT physicality_id,parent_id,child_id,parent_tier,child_tier,ordinal
  FROM packed WHERE child_tier >= parent_tier
),
self_edges AS MATERIALIZED (
  SELECT physicality_id,parent_id,child_id,ordinal
  FROM packed WHERE child_id=parent_id
),
child_ids AS MATERIALIZED (SELECT DISTINCT child_id FROM packed),
child_state AS MATERIALIZED (
  SELECT c.child_id,
         EXISTS(SELECT 1 FROM laplace.entities e WHERE e.id=c.child_id) AS has_entity,
         EXISTS(SELECT 1 FROM laplace.physicalities p
                WHERE p.entity_id=c.child_id AND p.type=1) AS has_content_physicality,
         EXISTS(SELECT 1 FROM laplace.physicalities p
                WHERE p.entity_id=c.child_id AND p.type=1
                  AND (p.radius_origin IS NULL OR NOT (
                       p.radius_origin >= 0.0
                       AND p.radius_origin <= 1.0 + {tolerance:.17g}))) AS bad_radius
  FROM child_ids c
),
child_fail AS MATERIALIZED (
  SELECT * FROM child_state
  WHERE NOT has_entity OR NOT has_content_physicality OR bad_radius
)
SELECT json_build_object(
  'schema','laplace.proof.recursive-trajectory/v1',
  'trajectory_parents',(SELECT count(*) FROM parents),
  'packed_vertices',(SELECT count(*) FROM packed),
  'logical_constituents',(SELECT COALESCE(sum(run_length),0) FROM packed),
  'distinct_children',(SELECT count(*) FROM child_ids),
  'count_mismatch_parents',(SELECT count(*) FROM count_fail),
  'self_edges',(SELECT count(*) FROM self_edges),
  'non_descending_edges',(SELECT count(*) FROM non_descending),
  'missing_child_entities',(SELECT count(*) FROM child_state WHERE NOT has_entity),
  'missing_child_content_physicalities',
      (SELECT count(*) FROM child_state WHERE NOT has_content_physicality),
  'child_bound_failures',(SELECT count(*) FROM child_state WHERE bad_radius),
  'acyclicity_certificate',CASE
      WHEN NOT EXISTS(SELECT 1 FROM non_descending)
       AND NOT EXISTS(SELECT 1 FROM self_edges)
      THEN 'strict-contextual-tier-descent' ELSE NULL END,
  'count_failure_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(parent_id,'hex') AS parent_id,n_constituents,
             packed_vertices,logical_constituents,min_ordinal,max_ordinal
      FROM count_fail ORDER BY parent_id LIMIT {sample_limit}) x),'[]'::json),
  'non_descending_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(parent_id,'hex') AS parent_id,encode(child_id,'hex') AS child_id,
             parent_tier,child_tier,ordinal
      FROM non_descending ORDER BY parent_id,ordinal LIMIT {sample_limit}) x),'[]'::json),
  'child_failure_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(child_id,'hex') AS child_id,has_entity,
             has_content_physicality,bad_radius
      FROM child_fail ORDER BY child_id LIMIT {sample_limit}) x),'[]'::json)
)::text;
"""


def identity_sql(sample_limit: int) -> str:
    return f"""
WITH
parents AS MATERIALIZED (
  SELECT p.id AS physicality_id,p.entity_id AS parent_id,
         p.n_constituents,p.trajectory
  FROM laplace.physicalities p
  WHERE p.type=1 AND p.trajectory IS NOT NULL
),
expanded AS MATERIALIZED (
  SELECT p.physicality_id,p.parent_id,p.n_constituents,
         u.ordinal,u.entity_id AS child_id
  FROM parents p
  CROSS JOIN LATERAL public.laplace_trajectory_expanded_constituents(p.trajectory) u
),
per_parent AS MATERIALIZED (
  SELECT physicality_id,parent_id,max(n_constituents) AS n_constituents,
         count(*)::bigint AS expanded_count,
         array_agg(child_id ORDER BY ordinal) AS child_ids
  FROM expanded GROUP BY physicality_id,parent_id
),
checked AS MATERIALIZED (
  SELECT *,CASE
      WHEN expanded_count=1 THEN child_ids[1]
      WHEN expanded_count>1 THEN public.laplace_hash128_merkle(0::smallint,child_ids)
      ELSE NULL::bytea END AS recomputed_id
  FROM per_parent
),
failed AS MATERIALIZED (
  SELECT * FROM checked
  WHERE expanded_count <> n_constituents
     OR recomputed_id IS NULL OR recomputed_id IS DISTINCT FROM parent_id
),
failure_groups AS MATERIALIZED (
  SELECT e.type_id,e.first_observed_by,count(*) AS failed_physicalities
  FROM failed f LEFT JOIN laplace.entities e ON e.id=f.parent_id
  GROUP BY e.type_id,e.first_observed_by
)
SELECT json_build_object(
  'schema','laplace.proof.recursive-identity/v1',
  'parents_checked',(SELECT count(*) FROM checked),
  'expanded_constituents',(SELECT COALESCE(sum(expanded_count),0) FROM checked),
  'identity_or_expansion_failures',(SELECT count(*) FROM failed),
  'failure_group_limit',32,
  'failure_group_count',(SELECT count(*) FROM failure_groups),
  'failure_groups_truncated',(SELECT count(*)>32 FROM failure_groups),
  'failure_classification_rows',
      (SELECT COALESCE(sum(failed_physicalities),0) FROM failure_groups),
  'failure_groups',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(type_id,'hex') AS entity_type_id,
             encode(first_observed_by,'hex') AS first_observed_by,
             failed_physicalities
      FROM failure_groups
      ORDER BY failed_physicalities DESC,type_id,first_observed_by
      LIMIT 32) x),'[]'::json),
  'failure_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(parent_id,'hex') AS parent_id,
             encode(recomputed_id,'hex') AS recomputed_id,
             n_constituents,expanded_count
      FROM failed ORDER BY parent_id LIMIT {sample_limit}) x),'[]'::json)
)::text;
"""


def reconstruction_sql(sample_limit: int, proof_roots: int) -> str:
    # This is a selected exact-decoder proof set.  Direct referential/bounds/
    # identity checks above remain exhaustive over the selected content estate.
    return f"""
WITH
candidates AS MATERIALIZED (
  SELECT DISTINCT e.id
  FROM laplace.entities e
  JOIN laplace.physicalities p ON p.entity_id=e.id
  WHERE p.type=1 AND p.trajectory IS NOT NULL
    AND e.type_id IN (realize.canonical_id('Sentence'),realize.canonical_id('Document'))
  ORDER BY e.id LIMIT {proof_roots}
),
checked AS MATERIALIZED (
  SELECT c.id,realize.reconstruct_content(c.id) AS body FROM candidates c
),
failed AS MATERIALIZED (SELECT id FROM checked WHERE body IS NULL)
SELECT json_build_object(
  'schema','laplace.proof.recursive-reconstruction/v1',
  'selected_roots',(SELECT count(*) FROM candidates),
  'reconstructed_roots',(SELECT count(*) FROM checked WHERE body IS NOT NULL),
  'failed_roots',(SELECT count(*) FROM failed),
  'root_fingerprints',COALESCE((SELECT json_agg(x ORDER BY x.id) FROM (
      SELECT encode(id,'hex') AS id,octet_length(body) AS bytes,md5(body) AS md5
      FROM checked WHERE body IS NOT NULL) x),'[]'::json),
  'failure_examples',COALESCE((SELECT json_agg(x) FROM (
      SELECT encode(id,'hex') AS id FROM failed ORDER BY id LIMIT {sample_limit}) x),'[]'::json)
)::text;
"""


def add_reconstruction_fingerprint(section: dict[str, Any]) -> None:
    h = hashlib.sha256()
    for row in section.get("root_fingerprints") or []:
        h.update(str(row["id"]).encode("ascii"))
        h.update(b":")
        h.update(str(row["bytes"]).encode("ascii"))
        h.update(b":")
        h.update(str(row["md5"]).encode("ascii"))
        h.update(b"\n")
    section["aggregate_sha256_over_id_length_md5"] = h.hexdigest()


def failures(receipt: dict[str, Any]) -> list[tuple[str, int]]:
    out: list[tuple[str, int]] = []
    s,t,i,r = (receipt[k] for k in ("storage","trajectory","identity","reconstruction"))
    for name in ("duplicate_entity_ids","duplicate_content_physicality_entities","parent_contract_failures"):
        out.append((f"storage.{name}",int(s.get(name) or 0)))
    for name in ("count_mismatch_parents","self_edges","non_descending_edges",
                 "missing_child_entities","missing_child_content_physicalities","child_bound_failures"):
        out.append((f"trajectory.{name}",int(t.get(name) or 0)))
    out.append(("identity.identity_or_expansion_failures",int(i.get("identity_or_expansion_failures") or 0)))
    out.append(("reconstruction.failed_roots",int(r.get("failed_roots") or 0)))
    if int(r.get("selected_roots") or 0) <= 0:
        out.append(("reconstruction.zero_selected_roots",1))
    if not t.get("acyclicity_certificate"):
        out.append(("trajectory.acyclicity_certificate_missing",1))
    return [(name,value) for name,value in out if value != 0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("database",nargs="?",default=os.environ.get("LAPLACE_DBNAME") or os.environ.get("PGDATABASE") or "laplace")
    parser.add_argument("--tolerance",type=float,default=1e-12)
    parser.add_argument("--sample-limit",type=int,default=20)
    parser.add_argument("--reconstruction-roots",type=int,default=32)
    parser.add_argument("--statement-timeout",type=int,default=900)
    parser.add_argument("--receipt",default=os.environ.get("LAPLACE_RECURSIVE_PROOF_RECEIPT") or str(DEFAULT_RECEIPT))
    args = parser.parse_args()
    if not 0 <= args.tolerance <= 1e-6:
        raise SystemExit("--tolerance must be in [0,1e-6]")
    if not 1 <= args.sample_limit <= 100:
        raise SystemExit("--sample-limit must be in 1..100")
    if not 1 <= args.reconstruction_roots <= 1000:
        raise SystemExit("--reconstruction-roots must be in 1..1000")
    if args.statement_timeout < 30:
        raise SystemExit("--statement-timeout must be >=30")

    path = Path(args.receipt).resolve()
    path.parent.mkdir(parents=True,exist_ok=True)
    started = time.time_ns()
    try:
        receipt: dict[str,Any] = {
            "schema":"laplace.proof.live-recursive-substrate/v1",
            "source_sha":git_sha(),"database":args.database,"tolerance":args.tolerance,
            "metadata":query_json(args.database,metadata_sql(),args.statement_timeout),
            "storage":query_json(args.database,storage_sql(args.tolerance,args.sample_limit),args.statement_timeout),
            "trajectory":query_json(args.database,trajectory_sql(args.tolerance,args.sample_limit),args.statement_timeout),
            "identity":query_json(args.database,identity_sql(args.sample_limit),args.statement_timeout),
            "reconstruction":query_json(args.database,reconstruction_sql(args.sample_limit,args.reconstruction_roots),args.statement_timeout),
        }
        add_reconstruction_fingerprint(receipt["reconstruction"])
        bad = failures(receipt)
        receipt["failures"]=[{"coordinate":n,"count":v} for n,v in bad]
        receipt["ok"]=not bad
    except Exception as exc:
        receipt={"schema":"laplace.proof.live-recursive-substrate/v1","source_sha":git_sha(),
                 "database":args.database,"ok":False,"error":str(exc)}
    receipt["started_unix_nanoseconds"]=started
    receipt["finished_unix_nanoseconds"]=time.time_ns()
    path.write_text(json.dumps(receipt,indent=2,sort_keys=True,default=str)+"\n",encoding="utf-8")

    if not receipt.get("ok"):
        print("RECURSIVE_SUBSTRATE_PROOF_FAIL",file=sys.stderr)
        if receipt.get("error"):
            print(f"  error={receipt['error']}",file=sys.stderr)
        for item in receipt.get("failures") or []:
            print(f"  {item['coordinate']}={item['count']}",file=sys.stderr)
        print(f"receipt={path}",file=sys.stderr)
        return 1

    t=receipt["trajectory"]
    print("RECURSIVE_SUBSTRATE_PROOF_OK "
          f"parents={t['trajectory_parents']} packed_vertices={t['packed_vertices']} "
          f"logical_constituents={t['logical_constituents']} "
          f"acyclicity={t['acyclicity_certificate']} receipt={path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
