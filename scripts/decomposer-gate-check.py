#!/usr/bin/env python3
"""Run per-source decomposer gates (substrate_health, layer_complete, attestations, CI consensus)."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GATES = ROOT / "scripts" / "decomposer-gates.json"


def psql(dbname: str, sql: str, *, host: str, user: str) -> str:
    env = os.environ.copy()
    cmd = [
        os.environ.get("PSQL", "psql"),
        "-h", host,
        "-U", user,
        "-d", dbname,
        "-v", "ON_ERROR_STOP=1",
        "-tAc", sql,
    ]
    r = subprocess.run(cmd, capture_output=True, text=True, env=env)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip() or r.stdout.strip() or f"psql failed: {sql[:80]}")
    return r.stdout.strip()


def load_gates() -> dict:
    with GATES.open(encoding="utf-8") as f:
        return json.load(f)


def check_source(
    source: str,
    dbname: str,
    *,
    host: str,
    user: str,
    allow_health_tier: bool,
) -> dict:
    spec = load_gates()
    src = spec["sources"].get(source)
    if not src:
        raise SystemExit(f"unknown source '{source}' — not in decomposer-gates.json")

    decomposer = src["decomposer"]
    layer = src["layer"]
    results: list[dict] = []
    ok = True

    def record(name: str, passed: bool, detail: str, **extra: object) -> None:
        nonlocal ok
        if not passed:
            ok = False
        entry: dict = {"check": name, "passed": passed, "detail": detail}
        entry.update(extra)
        results.append(entry)

    # substrate_health
    try:
        raw = psql(
            dbname,
            "SELECT ok FROM laplace.substrate_health();",
            host=host,
            user=user,
        )
        ok_val = raw.splitlines()[-1].strip().lower()
        health_ok = ok_val in ("t", "true")
        detail = f"ok={ok_val}"
        if not health_ok and allow_health_tier:
            health_ok = True
            detail = f"{detail}; tier violations allowed (LAPLACE_GATE_ALLOW_HEALTH_TIER=1)"
        record("substrate_health", health_ok, detail)
    except Exception as e:
        record("substrate_health", False, str(e))

    # attestations > 0
    try:
        att = int(
            psql(
                dbname,
                f"SELECT laplace.evidence_count(p_source => laplace.source_id('{decomposer}'));",
                host=host,
                user=user,
            )
        )
        record("attestations", att > 0, f"{att:,} attestations", count=att)
    except Exception as e:
        record("attestations", False, str(e), count=0)

    # layer_complete (document uses physicality probe)
    if src.get("skip_layer_complete"):
        try:
            exists = psql(
                dbname,
                f"SELECT EXISTS(SELECT 1 FROM laplace.physicalities p "
                f"WHERE p.source_id = laplace.source_id('{decomposer}') LIMIT 1);",
                host=host,
                user=user,
            ).lower() in ("t", "true")
            record("layer_complete", exists, f"content physicalities present={exists}")
        except Exception as e:
            record("layer_complete", False, str(e))
    else:
        try:
            layer_ok = psql(
                dbname,
                "SELECT laplace.evidence_count("
                f"p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/{layer}/v1'), "
                f"p_source => laplace.source_id('{decomposer}')) > 0;",
                host=host,
                user=user,
            ).lower() in ("t", "true")
            record("layer_complete", layer_ok, f"L{layer} HasLayerCompleted={layer_ok}")
        except Exception as e:
            record("layer_complete", False, str(e))

    # consensus gates (CI pattern; optional source-evidence fallback)
    for gate in src.get("consensus_gates", []):
        rel = gate["relation"]
        minimum = int(gate["min"])
        try:
            n = int(
                psql(
                    dbname,
                    f"SELECT laplace.consensus_count(laplace.relation_type_id('{rel}'));",
                    host=host,
                    user=user,
                )
            )
            passed = n >= minimum
            detail = f"consensus={n:,} (min {minimum:,})"
            if not passed and gate.get("fallback") == "source_evidence":
                alt_rels = gate.get("fallback_relations") or [rel]
                total = 0
                for alt in alt_rels:
                    total += int(
                        psql(
                            dbname,
                            "SELECT laplace.evidence_count("
                            f"p_type => laplace.relation_type_id('{alt}'), "
                            f"p_source => laplace.source_id('{decomposer}'));",
                            host=host,
                            user=user,
                        )
                    )
                alt_min = int(gate.get("fallback_min", minimum))
                if total >= alt_min:
                    passed = True
                    detail = (
                        f"consensus={n:,} (min {minimum:,}); "
                        f"source_evidence fallback {total:,} across {alt_rels} (min {alt_min:,})"
                    )
            record(
                f"consensus:{rel}",
                passed,
                detail,
                relation=rel,
                consensus=n,
                min=minimum,
            )
        except Exception as e:
            record(f"consensus:{rel}", False, str(e), relation=rel, min=minimum)

    report = {
        "source": source,
        "dbname": dbname,
        "decomposer": decomposer,
        "layer": layer,
        "passed": ok,
        "checked_at": datetime.now(timezone.utc).isoformat(),
        "checks": results,
    }
    return report


def main() -> int:
    p = argparse.ArgumentParser(description="Decomposer gate checks")
    p.add_argument("--source", required=True, help="manifest cli name (e.g. wordnet)")
    p.add_argument("--dbname", required=True, help="target PostgreSQL database")
    p.add_argument("--host", default=os.environ.get("PGHOST", "localhost"))
    p.add_argument("--user", default=os.environ.get("PGUSER", "postgres"))
    p.add_argument(
        "--report",
        help="write JSON report path (default .ingest-proof/decomposer-<source>.json)",
    )
    p.add_argument(
        "--allow-health-tier",
        action="store_true",
        default=os.environ.get("LAPLACE_GATE_ALLOW_HEALTH_TIER", "") == "1",
    )
    args = p.parse_args()

    report_path = Path(
        args.report or ROOT / ".ingest-proof" / f"decomposer-{args.source}.json"
    )
    report_path.parent.mkdir(parents=True, exist_ok=True)

    try:
        report = check_source(
            args.source,
            args.dbname,
            host=args.host,
            user=args.user,
            allow_health_tier=args.allow_health_tier,
        )
    except SystemExit as e:
        print(e, file=sys.stderr)
        return 2

    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"gate report: {report_path}")
    for chk in report["checks"]:
        status = "PASS" if chk["passed"] else "FAIL"
        print(f"  [{status}] {chk['check']}: {chk['detail']}")

    if report["passed"]:
        print(f"GATES OK: {args.source} on {args.dbname}")
        return 0
    print(f"GATES FAILED: {args.source} on {args.dbname}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
