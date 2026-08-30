#!/usr/bin/env python3
"""Shrink-only payload measurement and enforcement for the model lane.

A model enters the substrate as TESTIMONY: its weights are vote significances on
edges that already exist, folded by Glicko. Under that law a checkpoint costs
attestations, and attestation cost is bounded by the edges its vocabulary already
touches -- measured 2026-08-11 at 55,971 consensus edges between the 26,622
entities TinyLlama's vocabulary resolves to, i.e. ~9.5 MB. It does not cost
geometry payload proportional to vocab x dim x heads x layers.

This check exists because that distinction was violated silently for months and
nothing failed. `LAPLACE_MODEL_PLANES=factors` deposits a [V x dim] float field
per circuit slice; TinyLlama's 22 layers projected to 210 GB and filled the
cluster volume before anything complained. The projection across the local model
library is 23.1 TB from 0.24 TB of checkpoints, and Qwen3-Coder-480B alone
projects to 57.8 TB. See docs/archive/reports/MODEL_LANE_AUDIT_2026-08-11.md for the derivation
and every verification command.

THE BASELINE IS THE CONTRACT: the recorded measurement is the effective ceiling
and may only DECREASE -- the same shrink-only contract scripts/isa-gate-check.py
uses. Re-baselining upward is a deliberate, visible edit to the JSON, not a
silent data change. When this gate landed, 87.4 GB was already deposited and the
baseline existed to avoid a gate that fails on arrival and gets disabled (how
the last policy died). MEASURED 2026-08-13 after the storage remediation
re-seed: 0 bytes, 0 sources -- the deposit is gone, so the baseline now arms at
the correct steady state. NOTE the consequence: the first legitimate model
ingest that composes new content entities (subword vocabulary and the like,
~MBs of trajectory) will trip the gate and require a reviewed --write-baseline.
That friction is the design, not a defect: every payload byte a model source
deposits gets seen and blessed in a diff.

ENFORCEMENT BELONGS AT THE MUTATION BOUNDARY. A standing substrate may contain
historical bad payload while unrelated code needs to deploy the repair. Therefore
the default invocation measures and reports violations but does not make mutable
production data a prerequisite for every code-policy run. Model-ingest workflows
must pass --enforce (and --strict) before AND after the ingest. That preserves the
zero/shrink-only law without letting old bad data deadlock all future repair code.

WHAT THIS DELIBERATELY DOES NOT GATE: constant `witnessWeight: 1.0` in the model
lane. Seven call sites pass a literal 1.0, and for CONTAINS / PRECEDES / recipe
edges that is CORRECT -- a tensor either is contained in a checkpoint or is not,
and there is no significance to scale. Flagging the constant would flag the
declared-structure scrape, which is the half of this lane that works (177,959
attestations, 82% dedup on apply, 38,190 consensus cells folded, 252 s). The
violation is payload volume, not the constant.

Exit codes: 0 pass/advisory/skip, 1 enforced violation, 2 usage/connection error
with --strict.
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

# MAY ONLY DECREASE. Outer bound on any future deliberate re-baseline: the
# baseline JSON is the day-to-day contract, these constants are what a
# re-baseline itself may not silently cross.
#
# History: first measured 2026-08-11 on hart-server at 87.4 GB across 13,324
# rows from two checkpoints (a 90 MB BERT encoder and a 2.2 GB Llama), and the
# ceilings sat just above that so the gate passed at baseline. MEASURED
# 2026-08-13 after the storage remediation re-seed: 0 bytes. Shrink-only means
# the ceilings follow the deposit down. 2 GB is headroom for legitimately
# composed content trajectories from model sources (subword vocabulary is ~MBs,
# not GBs) while the violation class -- [V x dim] float fields at 210 GB per
# checkpoint -- stays two orders of magnitude beyond it.
CEILING_TOTAL_BYTES = 2_000_000_000

# Per-source ceiling: was 82.5 GB when TinyLlama's deposit (81.8 GB across
# 8,164 rows) was the standing worst case; that deposit is purged.
CEILING_PER_SOURCE_BYTES = 1_000_000_000

# Vertex count is the axis that actually blows up -- 13,324 rows was nothing,
# but each spanned the whole vocabulary at full hidden dim: 2.73 bn vertices
# measured before the purge. Composed content trajectories are a few points per
# entity; 100 M is generous headroom.
CEILING_TOTAL_VERTICES = 100_000_000

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

    Unreachable is a SKIP, not a failure unless --strict is requested: this check
    ran during a session where the cluster was intentionally down for a volume
    migration, and a global observer that fails on planned maintenance is a check
    that gets commented out. Mutation boundaries use --strict.
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
    ap.add_argument("--enforce", action="store_true",
                    help="return exit 1 when payload violates the shrink-only contract")
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

    violations = []
    baseline = None
    if BASELINE.is_file():
        try:
            baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            violations.append(f"baseline {BASELINE.name} unreadable: {exc} — "
                              f"fix or regenerate it with --write-baseline")

    if args.write_baseline:
        BASELINE.write_text(json.dumps(
            {"note": "Measured payload per model source. Shrink-only; see "
                     "docs/archive/reports/MODEL_LANE_AUDIT_2026-08-11.md",
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

    # The shrink-only contract proper: the recorded baseline is the effective
    # ceiling, and it may only be moved by an explicit --write-baseline (a
    # visible, reviewable edit to the JSON), never by data growth. The
    # CEILING_* constants stay as the outer bound so a re-baseline upward past
    # them is itself a violation — the isa-gate-check.py arrangement.
    if baseline is None:
        if not violations:
            print(f"\nmodel-payload-gate: NOTE — no baseline recorded "
                  f"({BASELINE.name}); enforcing hard ceilings only. Run "
                  f"--write-baseline against the substrate to arm the "
                  f"shrink-only contract.")
    else:
        base_bytes = int(baseline.get("total_payload_bytes", 0))
        base_verts = int(baseline.get("total_vertices", 0))
        per_source = baseline.get("per_source", {})
        if base_bytes > CEILING_TOTAL_BYTES:
            violations.append(
                f"baseline total {gb(base_bytes)} exceeds the hard ceiling "
                f"{gb(CEILING_TOTAL_BYTES)} — re-baselining upward requires "
                f"editing this file's ceiling, not just the JSON")
        if total_bytes > base_bytes:
            violations.append(
                f"total payload {gb(total_bytes)} grew past the recorded "
                f"baseline {gb(base_bytes)} — the model lane deposited new "
                f"geometry payload; shrink-only")
        if total_verts > base_verts:
            violations.append(
                f"total vertices {total_verts:,} grew past the recorded "
                f"baseline {base_verts:,}")
        for r in rows:
            allowed = int(per_source.get(r["source"], 0))
            if r["payload_bytes"] > allowed:
                violations.append(
                    f"source {r['source']} deposited {gb(r['payload_bytes'])} "
                    f"against a recorded baseline of {gb(allowed)} — "
                    + ("a NEW source may deposit zero payload bytes"
                       if r["source"] not in per_source else "shrink-only"))

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
        if args.enforce:
            return 1
        print("\nmodel-payload-gate: ADVISORY — standing substrate violates the contract; "
              "code policy remains deployable. Mutation lanes must use --enforce.")
        return 0

    print(f"\nmodel-payload-gate: PASS — {gb(total_bytes)} within "
          f"{gb(CEILING_TOTAL_BYTES)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))