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
import re
import sys
import time
from pathlib import Path

from laplace_api import LaplaceApiError, op_rows

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


def substrate_fingerprint(api: str) -> dict:
    rows = op_rows(api, "ops.substrate_counts", max_rows=50)
    fp: dict[str, int] = {}
    for row in rows:
        try:
            fp[str(row["metric"])] = int(row["value"])
        except (KeyError, TypeError, ValueError):
            continue
    return fp


def seeded_sources(api: str) -> list[str]:
    """Which sources are ingested. THIS is what decides comparability."""
    rows = op_rows(api, "source_status", max_rows=2000)
    return sorted(
        str(row["source"]).strip()
        for row in rows
        if row.get("ingested") and str(row.get("source", "")).strip()
    )


# How far the row estimates may move before a baseline is considered
# incomparable. Nothing about a 5% drift in a planner estimate invalidates a
# hand-written expected-topic list.
FINGERPRINT_TOLERANCE = 0.25


def fingerprint_drift(baseline: dict, fp: dict, sources: list[str]) -> str | None:
    """Return a human reason the baseline is incomparable, or None.

    The previous rule was `baseline["fingerprint"] != fp` — exact dictionary
    equality against substrate_counts(), whose rows are `(ESTIMATE)` values
    read from pg_class.reltuples. Those are SAMPLED PLANNER STATISTICS: they
    move on autovacuum with zero data change, so the check failed on
    background maintenance and demanded a manual re-record every time. A gate
    that fires on noise teaches people to ignore it (ingest-baseline.py:34-37
    says exactly this in the repo's own words).

    What actually decides whether two runs are comparable is WHICH SOURCES ARE
    SEEDED — a baseline recorded on foundation-only cannot judge a run that
    also has OMW, and no row count expresses that. Counts stay, as a wide
    tolerance band, to catch a truncated or half-loaded substrate.
    """
    base_sources = baseline.get("sources")
    if base_sources is not None and sorted(base_sources) != sorted(sources):
        added = sorted(set(sources) - set(base_sources))
        removed = sorted(set(base_sources) - set(sources))
        return f"seeded sources changed (added={added}, removed={removed})"

    # Purpose-schema migration removed the historical `laplace.` prefix from
    # metric labels. The measured relation is the same; compare canonical
    # metric names so a naming cleanup cannot masquerade as lost data.
    canonical = lambda metric: str(metric).split(".", 1)[-1]
    current = {canonical(metric): value for metric, value in fp.items()}
    base_fp = baseline.get("fingerprint") or {}
    for metric, base_val in base_fp.items():
        cur = current.get(canonical(metric))
        if cur is None:
            return f"metric {metric!r} no longer reported"
        if base_val <= 0:
            continue
        if abs(cur - base_val) / base_val > FINGERPRINT_TOLERANCE:
            pct = 100.0 * (cur - base_val) / base_val
            return f"{metric} moved {pct:+.1f}% ({base_val} -> {cur})"
    return None


def entities_estimate(fp: dict) -> int:
    # substrate_counts() metrics are schema-qualified, e.g.
    # `laplace.entities(ESTIMATE)` — not a bare `entities` prefix.
    for k, v in fp.items():
        if "entities" in k:
            return v
    return 0


def label(api: str, entity_id: str | None) -> str | None:
    if not entity_id:
        return None
    rows = op_rows(api, "realize.label", {"p_id": entity_id}, max_rows=1)
    return str(rows[0]["label"]) if rows and rows[0].get("label") is not None else None


def resolve_topic_surface(api: str, phrase: str) -> str | None:
    rows = op_rows(
        api,
        "converse.resolve_topic",
        {"p_phrase": phrase, "p_context": None},
        max_rows=1,
    )
    topic_id = rows[0].get("resolve_topic") if rows else None
    return label(api, topic_id)


# The six-key elector invariant, verbatim from the five production sites
# (converse/chat, converse, converse_walk, resolve_topic, infer) and pinned by
# ElectorArchitectureGateTests. This harness MUST rank the way the system ranks;
# anything else measures a fiction.
#
# It previously used `ORDER BY ord LIMIT 1`, which takes the EARLIEST token
# rather than the best-ranked candidate — the exact inverse of the invariant's
# `ord DESC` tiebreak, which exists so the later, more specific token wins when
# the discriminating keys tie. Measured 2026-08-04 on "What is a glacier":
# specificity/rel_mass/peers all tie at 0, ord 2 = "a", ord 3 = "glacier", so
# ord ASC elected the article and scored the elector wrong. Two of the six
# probes ("glacier", "pawn") were failing on that alone.
def _descending_nulls_last(value) -> tuple[bool, float]:
    return value is None, -float(value or 0)


def _elector_key(row: dict) -> tuple:
    return (
        _descending_nulls_last(row.get("specificity")),
        _descending_nulls_last(row.get("rel_mass")),
        -int(row.get("peers") or 0),
        -int(row.get("ord") or 0),
        _descending_nulls_last(row.get("denote_mu")),
        str(row.get("synset_id") or ""),
    )


def prompt_coherence_rank1(api: str, prompt: str) -> tuple[str | None, float | None, float]:
    """Return (synset_surface, specificity, latency_s) for the elected candidate."""
    t0 = time.perf_counter()
    rows = op_rows(
        api,
        "converse.prompt_coherence",
        {"p_prompt": prompt},
        max_rows=200,
    )
    latency = time.perf_counter() - t0
    if not rows:
        return None, None, latency
    elected = min(rows, key=_elector_key)
    specificity = elected.get("specificity")
    return label(api, elected.get("synset_id")), (
        float(specificity) if specificity is not None else None
    ), latency


# Rendering hygiene for the FORWARD PASS. These need no hand-written expected
# answer, which is the point: they fail on output that is structurally wrong
# regardless of whether the topic was right.
#
# GROUNDED 2026-08-05, infer('The opposite of hot is') in production:
#     WordNet_Synset   2264.2   <- rank 1, an entity TYPE as a prediction
#     buz              1501.9
#     jaa              1499.8
#     lod              1455.2
#     14915184-n       1373.3   <- a raw WordNet offset key as a prediction
# The harness scored the run GREEN, because it only ever asked
# prompt_coherence for a topic and never called the forward pass at all.
#
# A type is not an answer and an internal address is not an answer. Both are
# leaks of the substrate's own bookkeeping into the reply.
OFFSET_KEY_RE = re.compile(r"^\d{6,10}-[nvasr]$")
ILI_KEY_RE = re.compile(r"^i\d+$")
# The content hash itself, rendered as a word. realize()'s last arm is
# _realize_canonical, which prints the id when every naming arm abstains --
# measured 2026-08-05 on the ice synset's IS_SYNONYM_OF neighbours:
#     b6b080e5de7a4654728bb8519930859c...
#     b9e2f3c9ceacc91f94d8ba386ff7fba0...
# "Hubs are ADDRESSES, not names." A reply that cannot name a thing must say so,
# not print where the thing lives. Trailing ellipsis because render_text
# truncates.
HEX_ID_RE = re.compile(r"^[0-9a-f]{16,32}\W*$")


def entity_type_names(api: str) -> set[str]:
    """The substrate's own entity-type roster, so the leak check is not a
    hardcoded list that drifts the moment a type is added."""
    rows = op_rows(api, "entity_type_counts_approx", max_rows=1000)
    return {str(row["type"]).strip() for row in rows if str(row.get("type", "")).strip()}


def infer_predictions(api: str, prompt: str, limit: int = 8) -> tuple[list[str], float]:
    t0 = time.perf_counter()
    rows = op_rows(
        api,
        "converse.infer",
        {"p_prompt": prompt, "p_limit": int(limit)},
        max_rows=limit,
    )
    predictions = [
        str(row["prediction"]).strip()
        for row in rows
        if str(row.get("prediction", "")).strip()
    ]
    return predictions, time.perf_counter() - t0


def run_forward(api: str, probe: dict, type_names: set[str]) -> dict:
    """The PRODUCTION entry point, not the elector behind it. A probe passes only
    if the forward pass emits no leaked bookkeeping AND, when an answer is
    specified, actually reaches it."""
    prompt = probe["prompt"]
    expected = probe.get("expected_answer_surface")
    preds, latency = infer_predictions(api, prompt, probe.get("limit", 8))

    leaks = []
    for p in preds:
        if p in type_names:
            leaks.append(f"entity-type:{p}")
        elif OFFSET_KEY_RE.match(p) or ILI_KEY_RE.match(p):
            leaks.append(f"internal-key:{p}")
        elif HEX_ID_RE.match(p):
            leaks.append(f"rendered-id:{p}")

    answered = None
    if expected is not None:
        answered = any(p.lower() == expected.lower() for p in preds)

    ok = not leaks and (answered is not False)
    return {
        "id": probe.get("id"),
        "surface": "forward",
        "class": "forward",
        "held_out": bool(probe.get("held_out", False)),
        "prompt": prompt,
        "expected_answer_surface": expected,
        "predictions": preds,
        "leaks": leaks,
        "answer_reached": answered,
        "latency_s": round(latency, 4),
        "forward_clean": ok,
        "miss": not ok,
    }


def run_op_election(api: str, probe: dict) -> dict:
    prompt = probe["prompt"]
    expected = probe.get("expected_topic_surface")
    mode = probe.get("election_via", "prompt_coherence")
    t0 = time.perf_counter()
    if mode == "resolve_topic":
        got = resolve_topic_surface(api, prompt)
        latency = time.perf_counter() - t0
        specificity = None
    else:
        got, specificity, latency = prompt_coherence_rank1(api, prompt)
    ok = expected is not None and got is not None and got.lower() == expected.lower()
    return {
        "id": probe.get("id"),
        "surface": "op",
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
    ap.add_argument("--api", default="http://127.0.0.1:8080", help="deployed HTTP base")
    ap.add_argument("--probes", type=Path, default=DEFAULT_PROBES)
    ap.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    ap.add_argument("--report", type=Path, default=None)
    ap.add_argument("--record", action="store_true", help="write baseline from this run")
    ap.add_argument(
        "--surfaces",
        # `forward` is ON by default deliberately. It is the production entry
        # point; leaving it opt-in is how a green board coexisted with a forward
        # pass emitting an entity type as its rank-1 answer.
        default="op,forward",
        help="comma list: op,forward",
    )
    args = ap.parse_args()

    if not args.probes.is_file():
        sys.stderr.write(f"probes file missing: {args.probes}\n")
        sys.exit(2)

    try:
        fp = substrate_fingerprint(args.api)
        sources = seeded_sources(args.api)
    except (LaplaceApiError, KeyError, TypeError, ValueError) as ex:
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
    # Fetched once, not per probe: the roster is the substrate's own, so a new
    # entity type is covered the moment it exists.
    type_names: set[str] = set()
    if any(p.get("class") == "forward" for p in probes) and "forward" in surfaces:
        type_names = entity_type_names(args.api)

    for probe in probes:
        if probe.get("class") == "forward":
            if "forward" in surfaces:
                results.append(run_forward(args.api, probe, type_names))
            continue
        if "op" in surfaces and probe.get("surface", "op") in ("op", "both"):
            if probe.get("class") == "election" or probe.get("expected_topic_surface"):
                results.append(run_op_election(args.api, probe))

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

    # The forward pass, scored separately from the elector behind it. An election
    # verdict says the right topic was CHOSEN; it says nothing about what the
    # system then emits, and the two came apart measurably on 2026-08-05.
    forward = [r for r in results if r.get("surface") == "forward"]
    if forward:
        leaked = [r for r in forward if r.get("leaks")]
        unreached = [r for r in forward if r.get("answer_reached") is False]
        verdicts["forward_hygiene"] = {
            "passed": len(forward) - len(leaked),
            "total": len(forward),
            "clean": len(leaked) == 0,
            "leaks": sorted({leak for r in leaked for leak in r["leaks"]}),
        }
        verdicts["forward_answer"] = {
            "passed": len([r for r in forward if r.get("answer_reached") is True]),
            "total": len([r for r in forward if r.get("answer_reached") is not None]),
            "unreached": [r["id"] for r in unreached],
        }
    if not election:
        verdicts["no_scorable_probes"] = True

    report = {
        "fingerprint": fp,
        "sources": sources,
        "verdicts": verdicts,
        "probes": results,
        "misses_first": True,
    }

    if args.record:
        baseline = {
            "recorded_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "advisory_until": probes_doc.get("advisory_until", "2026-08-10"),
            "blocking_flip_date": probes_doc.get("blocking_flip_date"),
            # WHICH SOURCES ARE SEEDED is what decides comparability; the row
            # estimates below are a tolerance band, not an equality check.
            "sources": sources,
            "fingerprint": fp,
            "election": {
                "passed": len(election_ok),
                "total": len(election),
                # Hand-written expected surfaces are the truth; rates are informational.
                "require_exact": True,
            },
            "latency_ceiling_s": latency_ceiling,
            "notes": (
                "election_correctness is exact. Comparability is decided by the seeded "
                "source set; row estimates are a "
                f"{int(FINGERPRINT_TOLERANCE * 100)}% tolerance band, not an equality "
                "check — substrate_counts() reports sampled planner estimates that move "
                "on autovacuum with no data change."
            ),
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
        # Incomparable substrate → re-record required (not a silent pass).
        drift = fingerprint_drift(baseline, fp, sources)
        if drift is not None:
            verdicts["fingerprint_drift"] = drift
            sys.stderr.write(
                f"substrate incomparable to baseline: {drift} — re-record required (exit 1)\n"
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
        # Forward hygiene is BLOCKING. A leaked entity type or internal key is a
        # structural defect in the reply, not a ranking preference, so it fails
        # the run outright rather than reporting alongside a PASS.
        and verdicts.get("forward_hygiene", {"clean": True})["clean"]
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
        f"forward_hygiene "
        f"{verdicts.get('forward_hygiene', {}).get('passed', 0)}/"
        f"{verdicts.get('forward_hygiene', {}).get('total', 0)} clean; "
        f"p50_latency={p50}s ceiling={latency_ceiling}s"
    )
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
