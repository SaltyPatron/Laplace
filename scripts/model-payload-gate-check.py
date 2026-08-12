#!/usr/bin/env python3
"""Shrink-only payload gate for the model lane.

A model enters the substrate as TESTIMONY: its weights are vote significances on
edges that already exist, folded by Glicko. Under that law a checkpoint costs
attestations, and attestation cost is bounded by the edges its vocabulary already
touches -- measured 2026-08-11 at 55,971 consensus edges between the 26,622
entities TinyLlama's vocabulary resolves to, i.e. ~9.5 MB. It does not cost
geometry payload proportional to vocab x dim x heads x layers.

This gate exists because that distinction was violated silently for months and
nothing failed. `LAPLACE_MODEL_PLANES=factors` deposits a [V x dim] float field
per circuit slice; TinyLlama's 22 layers projected to 210 GB and filled the
cluster volume before anything complained. The projection across the local model
library is 23.1 TB from 0.24 TB of checkpoints, and Qwen3-Coder-480B alone
projects to 57.8 TB. See docs/MODEL_LANE_AUDIT_2026-08-11.md for the derivation
and every verification command.

WHY A BASELINE RATHER THAN ZERO: the correct steady state is ~0 payload bytes per
model, but 81 GB is already deposited. A gate asserting 0 today fails on arrival
and gets disabled, which is how the last policy died. So the deposited bytes are
baselined and the ceiling may only DECREASE -- the same shrink-only contract
scripts/isa-gate-check.py uses. Re-baselining upward is a deliberate, visible
edit to this file, not a silent data change.

WHAT THIS DELIBERATELY DOES NOT GATE: constant `witnessWeight: 1.0` in the model
lane. Seven call sites pass a literal 1.0, and for CONTAINS / PRECEDES / recipe
edges that is CORRECT -- a tensor either is contained in a checkpoint or is not,
and there is no significance to scale. Flagging the constant would flag the
declared-structure scrape, which is the half of this lane that works (177,959
attestations, 82% dedup on apply, 38,190 consensus cells folded, 252 s). The
violation is payload volume, not the constant.

Exit codes: 0 pass or skip, 1 violation, 2 usage/connection error with --strict.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASELINE = ROOT / "scripts" / "model-payload-gate-baseline.json"

# Measured 2026-08-11 on hart-server. MAY ONLY DECREASE.
#
# Total geometry payload the model lane has deposited, in bytes. The number is
# the sum of length(trajectory::bytea) over physicalities whose entity was first
# observed by a model source. 81 GB across two checkpoints -- one 90 MB BERT
# encoder and one 2.2 GB Llama -- which is the fact the gate is here to keep
# visible.
# Measured 87.4 GB across 13,324 rows from two checkpoints. Set just above that
# so the gate passes at baseline and only a REGRESSION trips it -- a ceiling set
# below the deposited total fails on arrival and gets disabled, which is the
# failure mode this file's header warns about and which the first draft of this
# constant walked straight into.
CEILING_TOTAL_BYTES = 88_000_000_000

# Per-source ceiling: no single checkpoint may deposit more than the worst one
# already has. TinyLlama is 81.8 GB across 8,164 rows.
CEILING_PER_SOURCE_BYTES = 82_500_000_000

# Vertex count is the axis that actually blows up -- 13,324 rows is nothing, but
# each spans the whole vocabulary at full hidden dim. 2.73 bn vertices measured.
CEILING_TOTAL_VERTICES = 2_800_000_000

QUERY = """
SELECT coalesce(encode(e.first_observed_by, 'hex'), 'unattributed') AS src,
       count(*)                                        AS rows,
       coalesce(sum(length(p.trajectory::bytea)), 0)   AS payload_bytes,
       coalesce(sum(ST_NPoints(p.trajectory)), 0)      AS vertices
FROM laplace.physicalities p
JOIN laplace.entities e ON e.id = p.entity_id
WHERE p.trajectory IS NOT NULL
  AND e.first_observed_by IN (
        SELECT source_id FROM laplace.attestations
        WHERE type_id = laplace.relation_type_id('TOKEN_MAPS_TO')
        GROUP BY source_id)
GROUP BY 1
ORDER BY 3 DESC
"""


def measure(dsn_args: list[str], timeout: int) -> list[dict] | None:
    """Returns per-source payload rows, or None when the cluster is unreachable.

    Unreachable is a SKIP, not a failure: this gate ran during a session where the
    cluster was intentionally down for a volume migration, and a gate that fails
    on planned maintenance is a gate that gets commented out.
    """
    cmd = ["psql", *dsn_args, "-tA", "-F", "\x1f", "-v", "ON_ERROR_STOP=1", "-c", QUERY]
    try:
        out = subprocess.run(
            cmd, capture_output=True, text=True, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        print(f"model-payload-gate: SKIP — psql unavailable ({exc})")
        return None
    if out.returncode != 0:
        err = (out.stderr or "").strip().splitlines()
        print(f"model-payload-gate: SKIP — substrate unreachable: "
              f"{err[0] if err else 'unknown error'}")
        return None
    rows = []
    for line in out.stdout.strip().splitlines():
        if not line:
            continue
        src, n, payload, verts = line.split("\x1f")
        rows.append({"source": src, "rows": int(n),
                     "payload_bytes": int(payload), "vertices": int(verts)})
    return rows


def gb(n: int) -> str:
    return f"{n / 1e9:,.1f} GB"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--write-baseline", action="store_true",
                    help="record current measurement as the baseline")
    ap.add_argument("--timeout", type=int, default=900)
    ap.add_argument("--strict", action="store_true",
                    help="treat an unreachable substrate as failure (exit 2)")
    ap.add_argument("psql_args", nargs="*",
                    help="passed through to psql, e.g. -h /var/run/postgresql -d laplace")
    args = ap.parse_args(argv)

    dsn = args.psql_args or [
        "-U", os.environ.get("PGUSER", "laplace_admin"),
        "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
        "-d", os.environ.get("PGDATABASE", "laplace"),
    ]

    rows = measure(dsn, args.timeout)
    if rows is None:
        return 2 if args.strict else 0

    total_bytes = sum(r["payload_bytes"] for r in rows)
    total_verts = sum(r["vertices"] for r in rows)

    if args.write_baseline:
        BASELINE.write_text(json.dumps(
            {"note": "Measured payload per model source. Shrink-only; see "
                     "docs/MODEL_LANE_AUDIT_2026-08-11.md",
             "total_payload_bytes": total_bytes,
             "total_vertices": total_verts,
             "per_source": {r["source"]: r["payload_bytes"] for r in rows}},
            indent=2) + "\n")
        print(f"model-payload-gate: baseline written — {gb(total_bytes)}, "
              f"{total_verts:,} vertices, {len(rows)} source(s)")
        return 0

    print(f"{'source':34} {'rows':>8} {'payload':>12} {'vertices':>16}")
    for r in rows:
        print(f"{r['source']:34} {r['rows']:8,} {gb(r['payload_bytes']):>12} "
              f"{r['vertices']:16,}")
    print(f"{'TOTAL':34} {sum(r['rows'] for r in rows):8,} "
          f"{gb(total_bytes):>12} {total_verts:16,}")

    violations = []
    if total_bytes > CEILING_TOTAL_BYTES:
        violations.append(
            f"total payload {gb(total_bytes)} exceeds ceiling "
            f"{gb(CEILING_TOTAL_BYTES)} — a model lane depositing geometry "
            f"proportional to vocab x dim x heads x layers does not scale: the "
            f"local library projects to 23.1 TB, Qwen3-Coder-480B to 57.8 TB")
    if total_verts > CEILING_TOTAL_VERTICES:
        violations.append(
            f"total vertices {total_verts:,} exceeds ceiling "
            f"{CEILING_TOTAL_VERTICES:,}")
    for r in rows:
        if r["payload_bytes"] > CEILING_PER_SOURCE_BYTES:
            violations.append(
                f"source {r['source']} deposited {gb(r['payload_bytes'])}, "
                f"over the per-source ceiling {gb(CEILING_PER_SOURCE_BYTES)}")

    if violations:
        print()
        for v in violations:
            print(f"model-payload-gate: FAIL — {v}")
        print("\nThe bounded form: a weight is a vote's significance on an edge "
              "that already exists. Cost is attestations over the edges the "
              "vocabulary touches (55,971 measured for TinyLlama, ~9.5 MB), not "
              "geometry payload. NativeAttestation.CategoricalResolved already "
              "accepts witnessWeight; the magnitude->score conversion "
              "(KindRegistry.AttestWeighted / AttestationFactory.CreateWeighted) "
              "was deleted in 7022bbca and is recoverable from 7022bbca^.")
        return 1

    print(f"\nmodel-payload-gate: PASS — {gb(total_bytes)} within "
          f"{gb(CEILING_TOTAL_BYTES)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
