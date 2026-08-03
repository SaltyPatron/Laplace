#!/usr/bin/env python3
"""Generation / election quality harness (W5 / GH #755).

Exit codes (same contract as verify-model-behavioral.py):
  0 pass
  1 content-gate failure (or missing baseline without --record)
  2 harness/setup error (unseeded / unreachable / bad inputs)

election_correctness lands first: hand-written expected topic surfaces vs
prompt_coherence / resolve_topic rank-1. Content-rate detectors reuse the
GLUE_WORDS stoplist from verify-model-behavioral (imported, not retyped).
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_PROBES = ROOT / "scripts" / "eval-probes.json"
DEFAULT_BASELINE = ROOT / "scripts" / "eval-baselines.json"


def _load_glue_words():
    """Import GLUE_WORDS from verify-model-behavioral — do not retype the stoplist."""
    path = ROOT / "scripts" / "verify-model-behavioral.py"
    spec = importlib.util.spec_from_file_location("verify_model_behavioral", path)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod.GLUE_WORDS, mod.WORD_RE


GLUE_WORDS, WORD_RE = _load_glue_words()


def psql_rows(db: str, sql: str) -> list[str]:
    cmd = ["psql", "-X", "-q", "-t", "-A", "-F", "\t"]
    for part in db.split():
        k, _, v = part.partition("=")
        if k == "host":
            cmd += ["-h", v]
        elif k == "user":
            cmd += ["-U", v]
        elif k == "dbname":
            cmd += ["-d", v]
        elif k == "port":
            cmd += ["-p", v]
    r = subprocess.run(
        cmd + ["-c", sql],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if r.returncode != 0:
        sys.stderr.write(r.stderr or r.stdout or "psql failed\n")
        sys.exit(2)
    return [line for line in r.stdout.splitlines() if line.strip()]


def substrate_fingerprint(db: str) -> dict:
    rows = psql_rows(
        db,
        "SET search_path=laplace,public; SELECT metric, value FROM substrate_counts();",
    )
    fp: dict[str, int] = {}
    for line in rows:
        metric, _, value = line.partition("\t")
        try:
            fp[metric] = int(value)
        except ValueError:
            continue
    return fp


def entities_estimate(fp: dict) -> int:
    for k, v in fp.items():
        if k.startswith("entities"):
            return v
    return 0


def resolve_topic_surface(db: str, phrase: str) -> str | None:
    q = phrase.replace("'", "''")
    rows = psql_rows(
        db,
        "SET search_path=laplace,public; "
        f"SELECT render(resolve_topic('{q}', NULL));",
    )
    return rows[0] if rows else None


def prompt_coherence_rank1(db: str, prompt: str) -> tuple[str | None, float | None, float]:
    """Return (synset_surface, specificity, latency_s) for ord=1 / first row."""
    q = prompt.replace("'", "''")
    t0 = time.perf_counter()
    rows = psql_rows(
        db,
        "SET search_path=laplace,public; "
        "SELECT render(synset_id), specificity "
        f"FROM prompt_coherence('{q}') ORDER BY ord LIMIT 1;",
    )
    latency = time.perf_counter() - t0
    if not rows:
        return None, None, latency
    parts = rows[0].split("\t")
    syn = parts[0] if parts else None
    spec = float(parts[1]) if len(parts) > 1 and parts[1] else None
    return syn, spec, latency


def run_sql_election(db: str, probe: dict) -> dict:
    prompt = probe["prompt"]
    expected = probe.get("expected_topic_surface")
    mode = probe.get("election_via", "prompt_coherence")
    t0 = time.perf_counter()
    if mode == "resolve_topic":
        got = resolve_topic_surface(db, prompt)
        latency = time.perf_counter() - t0
        specificity = None
    else:
        got, specificity, latency = prompt_coherence_rank1(db, prompt)
    ok = expected is not None and got is not None and got.lower() == expected.lower()
    return {
        "id": probe.get("id"),
        "surface": "sql",
        "class": probe.get("class", "election"),
        "held_out": bool(probe.get("held_out", False)),
        "prompt": prompt,
        "expected_topic_surface": expected,
        "got_topic_surface": got,
        "specificity": specificity,
        "latency_s": round(latency, 4),
        "election_correctness": ok,
        "miss": not ok,
    }


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--db",
        default="host=/var/run/postgresql user=laplace_admin dbname=laplace",
        help="psql connection as space-separated key=value",
    )
    ap.add_argument("--api", default=None, help="HTTP base (optional; not required for SQL election)")
    ap.add_argument("--probes", type=Path, default=DEFAULT_PROBES)
    ap.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    ap.add_argument("--report", type=Path, default=None)
    ap.add_argument("--record", action="store_true", help="write baseline from this run")
    ap.add_argument(
        "--surfaces",
        default="sql",
        help="comma list: sql[,http] — http deferred until API path wired",
    )
    args = ap.parse_args()

    if not args.probes.is_file():
        sys.stderr.write(f"probes file missing: {args.probes}\n")
        sys.exit(2)

    try:
        fp = substrate_fingerprint(args.db)
    except SystemExit:
        raise
    except Exception as ex:  # noqa: BLE001 — harness boundary
        sys.stderr.write(f"fingerprint failed: {ex}\n")
        sys.exit(2)

    n_ent = entities_estimate(fp)
    if n_ent <= 0:
        sys.stderr.write(
            "unseeded / empty substrate (entities estimate <= 0) — refusing to score (exit 2)\n"
        )
        sys.exit(2)

    baseline_path: Path = args.baseline
    # Refuse before expensive election probes when there is nothing to compare to.
    if not args.record:
        baseline_pre = {}
        if baseline_path.is_file():
            baseline_pre = json.loads(baseline_path.read_text(encoding="utf-8"))
        if not baseline_pre.get("fingerprint"):
            sys.stderr.write(
                f"No recorded baseline fingerprint in {baseline_path} — refusing to pass. "
                "Run with --record on a known-good seeded box, then commit the JSON.\n"
            )
            sys.exit(1)

    probes_doc = json.loads(args.probes.read_text(encoding="utf-8"))
    probes = probes_doc.get("probes") or []
    surfaces = {s.strip() for s in args.surfaces.split(",") if s.strip()}

    results: list[dict] = []
    for probe in probes:
        if "sql" in surfaces and probe.get("surface", "sql") in ("sql", "both"):
            if probe.get("class") == "election" or probe.get("expected_topic_surface"):
                results.append(run_sql_election(args.db, probe))

    # Misses before hits (plan standard of evidence).
    results.sort(key=lambda r: (0 if r.get("miss") else 1, r.get("id") or ""))

    election = [r for r in results if "election_correctness" in r]
    election_ok = [r for r in election if r["election_correctness"]]
    election_miss = [r for r in election if not r["election_correctness"]]

    latencies = [r["latency_s"] for r in election if r.get("latency_s") is not None]
    p50 = sorted(latencies)[len(latencies) // 2] if latencies else None
    latency_ceiling = float(probes_doc.get("latency_ceiling_s", 30.0))
    latency_budget_ok = p50 is not None and p50 <= latency_ceiling

    verdicts: dict = {
        "election_correctness": {
            "passed": len(election_ok),
            "total": len(election),
            "exact": len(election_miss) == 0 and len(election) > 0,
        },
        "latency_budget": {
            "p50_s": p50,
            "ceiling_s": latency_ceiling,
            "ok": latency_budget_ok,
        },
        "glue_words_imported": len(GLUE_WORDS),
    }
    if not election:
        verdicts["no_scorable_probes"] = True

    report = {
        "fingerprint": fp,
        "verdicts": verdicts,
        "probes": results,
        "misses_first": True,
    }

    if args.record:
        baseline = {
            "recorded_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "advisory_until": probes_doc.get("advisory_until", "2026-08-10"),
            "blocking_flip_date": probes_doc.get("blocking_flip_date"),
            "fingerprint": fp,
            "election": {
                "passed": len(election_ok),
                "total": len(election),
                # Hand-written expected surfaces are the truth; rates are informational.
                "require_exact": True,
            },
            "latency_ceiling_s": latency_ceiling,
            "notes": "election_correctness is exact; fingerprint change requires re-record",
        }
        baseline_path.write_text(json.dumps(baseline, indent=2) + "\n", encoding="utf-8")
        report["recorded_baseline"] = str(baseline_path)
    else:
        baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
        report["baseline"] = {
            "path": str(baseline_path),
            "recorded_at": baseline.get("recorded_at"),
            "advisory_until": baseline.get("advisory_until"),
        }
        # Fingerprint drift → re-record required (not a silent pass).
        if baseline["fingerprint"] != fp:
            verdicts["fingerprint_drift"] = True
            sys.stderr.write(
                "substrate fingerprint changed vs baseline — re-record required (exit 1)\n"
            )
            if args.report:
                args.report.parent.mkdir(parents=True, exist_ok=True)
                args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps(report, indent=2, ensure_ascii=False))
            sys.exit(1)

    ok = (
        len(election) > 0
        and verdicts["election_correctness"]["exact"]
        and latency_budget_ok
        and "fingerprint_drift" not in verdicts
        and "no_scorable_probes" not in verdicts
    )
    report["ok"] = ok

    text = json.dumps(report, indent=2, ensure_ascii=False)
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(text, encoding="utf-8")
    print(text)
    print(
        f"\nEVAL {'PASS' if ok else 'FAIL'}: election "
        f"{len(election_ok)}/{len(election)} exact; "
        f"p50_latency={p50}s ceiling={latency_ceiling}s"
    )
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
