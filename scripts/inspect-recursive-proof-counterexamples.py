#!/usr/bin/env python3
"""Read structural details for a bounded set of retained live-proof counterexamples."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
MAX_BYTES = 2 * 1024 * 1024
MAX_PARENTS = 24
MAX_VERTICES = 256


def read_json(path: Path) -> tuple[dict, bytes]:
    with path.open("rb") as source:
        raw = source.read(MAX_BYTES + 1)
    if len(raw) > MAX_BYTES:
        raise ValueError("input exceeds byte limit")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("input must be a JSON object")
    return value, raw


def selected_parents(directory: Path) -> tuple[list[str], dict]:
    manifest, _ = read_json(directory / "manifest.json")
    if manifest.get("schema") != "laplace.recursive-proof-diagnostic-collection/v1":
        raise ValueError("unexpected collection schema")
    candidates = []
    for item in manifest.get("receipts", [])[:12]:
        name = item.get("artifact_file", "")
        if item.get("disposition") != "retained" or not re.fullmatch(r"receipt-\d{2}\.json", name):
            continue
        value, raw = read_json(directory / name)
        if hashlib.sha256(raw).hexdigest() != item.get("sha256"):
            raise ValueError("retained receipt hash changed")
        if value.get("schema") != "laplace.proof.live-recursive-substrate/v1":
            raise ValueError("unexpected proof schema")
        examples = [*value.get("trajectory", {}).get("non_descending_examples", []),
                    *value.get("identity", {}).get("failure_examples", [])]
        ids = list(dict.fromkeys(row["parent_id"] for row in examples
                                if re.fullmatch(r"[0-9a-f]{32}", str(row.get("parent_id", "")))))
        if ids:
            candidates.append((value.get("finished_unix_nanoseconds", 0), ids, item))
    if not candidates:
        return [], {"disposition": "no-retained-parent-counterexamples"}
    _, ids, item = max(candidates, key=lambda candidate: candidate[0])
    return ids[:MAX_PARENTS], {
        "retained_receipt_sha256": item["sha256"],
        "retained_receipt_source_sha": item.get("receipt_source_sha"),
        "retained_receipt_path": item["requested_path"],
        "available_sample_parent_count": len(ids),
        "parent_limit": MAX_PARENTS,
    }


def structural_sql(ids: list[str]) -> str:
    if not ids or len(ids) > MAX_PARENTS or any(not re.fullmatch(r"[0-9a-f]{32}", value) for value in ids):
        raise ValueError("invalid bounded parent identities")
    values = ",".join(f"(decode('{value}','hex'))" for value in ids)
    return f"""BEGIN READ ONLY;
SET LOCAL statement_timeout='30s';
SET LOCAL lock_timeout='5s';
SET LOCAL max_parallel_workers_per_gather=0;
WITH targets(id) AS (VALUES {values}),
parents AS MATERIALIZED (
  SELECT p.id,p.entity_id,p.type,p.n_constituents,p.observed_at,p.trajectory,
         ST_NPoints(p.trajectory) AS packed_vertices
  FROM targets t JOIN laplace.physicalities p ON p.entity_id=t.id
  WHERE p.type=1
),
vertices AS MATERIALIZED (
  SELECT p.id AS physicality_id,c.ordinal,c.entity_id,c.run_length,c.flags
  FROM parents p CROSS JOIN LATERAL (
    SELECT c.* FROM public.laplace_trajectory_constituents(
      CASE WHEN p.packed_vertices <= 4096 THEN p.trajectory ELSE NULL END) c
    ORDER BY c.ordinal LIMIT {MAX_VERTICES}
  ) c
),
entity_ids AS MATERIALIZED (
  SELECT id FROM targets UNION SELECT entity_id FROM vertices
),
entities AS MATERIALIZED (
  SELECT e.id,e.tier,e.type_id,e.first_observed_by,e.created_at
  FROM entity_ids i JOIN laplace.entities e ON e.id=i.id
)
SELECT json_build_object(
  'database',current_database(),'observed_at',clock_timestamp(),
  'server_version',current_setting('server_version'),
  'transaction_read_only',current_setting('transaction_read_only'),
  'substrate_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'),
  'geometry_extension_version',(SELECT extversion FROM pg_extension WHERE extname='laplace_geom'),
  'parents',COALESCE((SELECT json_agg(json_build_object(
    'physicality_id',encode(p.id,'hex'),'entity_id',encode(p.entity_id,'hex'),
    'type',p.type,'n_constituents',p.n_constituents,'packed_vertices',p.packed_vertices,
    'observed_at',p.observed_at,'vertices_limited',p.packed_vertices>{MAX_VERTICES},
    'decoding_skipped',p.packed_vertices>4096)) FROM parents p),'[]'::json),
  'vertices',COALESCE((SELECT json_agg(json_build_object(
    'physicality_id',encode(v.physicality_id,'hex'),'ordinal',v.ordinal,
    'entity_id',encode(v.entity_id,'hex'),'run_length',v.run_length,'flags',v.flags,
    'contextual_tier',realize.vertex_tier(v.flags)) ORDER BY v.physicality_id,v.ordinal)
    FROM vertices v),'[]'::json),
  'entities',COALESCE((SELECT json_agg(json_build_object(
    'id',encode(e.id,'hex'),'tier',e.tier,'type_id',encode(e.type_id,'hex'),
    'first_observed_by',encode(e.first_observed_by,'hex'),'created_at',e.created_at))
    FROM entities e),'[]'::json)
)::text;
ROLLBACK;
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--collection", type=Path, required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--database", default=os.environ.get("PGDATABASE", "laplace"))
    args = parser.parse_args()
    report = {"schema": "laplace.recursive-proof-counterexample-inspection/v1",
              "source_sha": args.expected_source,
              "implementation_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "scope": "Bounded live structural readback of retained counterexample IDs; no corpus text, repair, or invariant pass claim.",
              "started_unix_nanoseconds": time.time_ns()}
    path = args.collection / "counterexample-structure.json"
    if path.exists():
        raise ValueError("preserve the existing structural inspection")
    try:
        ids, selection = selected_parents(args.collection)
        report.update({"selection": selection, "selected_parent_ids": ids})
        if not ids:
            report["status"] = "no-counterexamples-selected"
        else:
            sql = structural_sql(ids)
            report["sql_sha256"] = hashlib.sha256(sql.encode()).hexdigest()
            command = ["psql", "-X", "-w", "-A", "-t", "-q", "-v", "ON_ERROR_STOP=1",
                       "-v", "VERBOSITY=sqlstate",
                       "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
                       "-p", os.environ.get("PGPORT", "5432"),
                       "-U", os.environ.get("PGUSER", "laplace_admin"), "-d", args.database]
            with tempfile.TemporaryFile(dir=args.collection) as output, tempfile.TemporaryFile(dir=args.collection) as errors:
                result = subprocess.run(command, input=sql.encode(), stdout=output, stderr=errors,
                                        timeout=40, cwd=ROOT)
                output.seek(0)
                raw = output.read(MAX_BYTES + 1)
                report["psql_exit_code"] = result.returncode
                if result.returncode != 0:
                    errors.seek(0)
                    error_bytes = errors.read(MAX_BYTES + 1)
                    report["stderr_sha256"] = hashlib.sha256(error_bytes).hexdigest()
                    code = re.search(rb"(?:ERROR|FATAL):\s+([0-9A-Z]{5})\b", error_bytes)
                    report["sqlstate"] = code[1].decode("ascii") if code else None
                    raise ValueError("read-only structural query failed")
                if len(raw) > MAX_BYTES:
                    raise ValueError("structural query exceeds byte bound")
            report["live"] = json.loads(raw)
            if report["live"].get("transaction_read_only") != "on":
                raise ValueError("live transaction was not read-only")
            report["status"] = "observed"
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        report.update({"status": "failed", "error_type": type(error).__name__})
    report["finished_unix_nanoseconds"] = time.time_ns()
    serialized = (json.dumps(report, indent=2, sort_keys=True) + "\n").encode()
    if len(serialized) > MAX_BYTES:
        report.pop("live", None)
        report.update({"status": "failed", "error_type": "SerializedOutputByteLimit"})
        serialized = (json.dumps(report, indent=2, sort_keys=True) + "\n").encode()
    path.write_bytes(serialized)
    print(f"RECURSIVE_COUNTEREXAMPLE_INSPECTION status={report['status']} receipt={path}")
    return 1 if report["status"] == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main())
