#!/usr/bin/env python3
"""Generation / election quality harness (W5 / GH #755).

Exit codes (same contract as verify-model-behavioral.py):
  0 pass
  1 content-gate failure (or missing baseline without --record)
  2 harness/setup error (unseeded / unreachable / bad inputs)

election_correctness remains a separately reported diagnostic. Production forward
acceptance is blocking: every forward probe must emit something, must not leak
substrate bookkeeping, and every probe with an explicit expected answer must reach
that answer. The deployed OpenAI-compatible chat endpoint is exercised against the
same probes and must independently satisfy the same output/answer requirements. A
green elector, a valid JSON envelope, or hygienic-but-empty/incorrect output is not
a product success.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import re
import sys
import time
from pathlib import Path

from laplace_api import LaplaceApiError, chat_completion, op_rows

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
    rows = op_rows(api, "ops.source_status", max_rows=2000)
    return sorted(
        str(row["source"]).strip()
        for row in rows
        if row.get("ingested") and str(row.get("source", "")).strip()
    )


def canonical_source_name(source: str) -> str:
    """Compare legacy display names and governed source references as one source."""
    name = str(source).strip()
    prefix = "substrate/source/"
    if name.startswith(prefix):
        name = name[len(prefix):]
    if name.endswith("/v1"):
        name = name[:-3]
    return name


def fingerprint_drift(baseline: dict, fp: dict, sources: list[str]) -> str | None:
    """Return a human reason the baseline is incomparable, or None.

    The previous rule was `baseline["fingerprint"] != fp` — exact dictionary
    equality against substrate_counts(), whose rows are `(ESTIMATE)` values
    read from pg_class.reltuples. Those are SAMPLED PLANNER STATISTICS: they
    move on autovacuum with zero data change, so the check failed on
    background maintenance and demanded a manual re-record every time. A gate
    that fires on noise teaches people to ignore it (ingest-baseline.py:34-37
    says exactly this in the repo's own words).

    Baseline sources are a required floor, not an exact database snapshot. CI
    shares one live substrate with independent seed workflows, so new sources
    can lawfully arrive between code pushes. Missing baseline sources still
    make the run incomparable.

    Row estimates are evidence in the report, not comparability gates. They are
    pg_class.reltuples samples rather than exact counts, and an intentional
    normalization/reseed is specifically expected to make them shrink. A fixed
    percentage threshold therefore rejects the desired outcome while saying
    nothing about whether the corpus required by these probes is present. The
    source journal and the probes themselves carry those two responsibilities.
    """
    base_sources = baseline.get("sources")
    if base_sources is not None:
        required = {canonical_source_name(s) for s in base_sources}
        current_sources = {canonical_source_name(s) for s in sources}
        missing = sorted(required - current_sources)
        if missing:
            return f"required seeded sources missing: {missing}"

    if not fp:
        return "substrate fingerprint is empty"
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


def prompt_coherence_rank1(
    api: str, prompt: str
) -> tuple[str | None, str | None, float | None, float]:
    """Return (topic surface, sense surface, specificity, latency) for rank one.

    Production orientation returns the elected token (`y.tok`). The selected
    synset explains which sense won, but its preferred label is not the topic
    surface: after OMW, token `hot` can lawfully select a synset realized as
    `beautiful`. Conflating those two made a correct topic look like a miss.
    """
    t0 = time.perf_counter()
    rows = op_rows(
        api,
        "converse.prompt_coherence",
        {"p_prompt": prompt},
        max_rows=200,
    )
    latency = time.perf_counter() - t0
    if not rows:
        return None, None, None, latency
    elected = min(rows, key=_elector_key)
    specificity = elected.get("specificity")
    return label(api, elected.get("tok")), label(api, elected.get("synset_id")), (
        float(specificity) if specificity is not None else None
    ), latency


# Rendering hygiene for production inference/chat. Hygiene is necessary but is
# not a substitute for answer correctness or nonempty output.
OFFSET_KEY_RE = re.compile(r"^\d{6,10}-[nvasr]$")
ILI_KEY_RE = re.compile(r"^i\d+$")
HEX_ID_RE = re.compile(r"^[0-9a-f]{16,32}\W*$")


def entity_type_names(api: str) -> set[str]:
    """The substrate's own entity-type roster, so leak checks cannot drift."""
    rows = op_rows(api, "ops.entity_type_counts_approx", max_rows=1000)
    return {str(row["type"]).strip() for row in rows if str(row.get("type", "")).strip()}


def prediction_leaks(predictions: list[str], type_names: set[str]) -> list[str]:
    leaks: list[str] = []
    for value in predictions:
        if value in type_names:
            leaks.append(f"entity-type:{value}")
        elif OFFSET_KEY_RE.match(value) or ILI_KEY_RE.match(value):
            leaks.append(f"internal-key:{value}")
        elif HEX_ID_RE.match(value):
            leaks.append(f"rendered-id:{value}")
    return leaks


def text_leaks(content: str, type_names: set[str]) -> list[str]:
    leaks: list[str] = []
    for type_name in sorted(type_names):
        if type_name and re.search(rf"(?<!\w){re.escape(type_name)}(?!\w)", content):
            leaks.append(f"entity-type:{type_name}")
    for token in re.findall(r"\S+", content):
        normalized = token.strip(".,;:!?()[]{}<>\"'`")
        if not normalized:
            continue
        if OFFSET_KEY_RE.match(normalized) or ILI_KEY_RE.match(normalized):
            leaks.append(f"internal-key:{normalized}")
        elif HEX_ID_RE.match(normalized):
            leaks.append(f"rendered-id:{normalized}")
    return sorted(set(leaks))


def contains_expected_surface(content: str, expected: str) -> bool:
    return bool(re.search(rf"(?<!\w){re.escape(expected)}(?!\w)", content, re.IGNORECASE))


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
    """Exercise the deployed forward operation path."""
    prompt = probe["prompt"]
    expected = probe.get("expected_answer_surface")
    preds, latency = infer_predictions(api, prompt, probe.get("limit", 8))
    nonempty = bool(preds)
    leaks = prediction_leaks(preds, type_names)

    answered = None
    if expected is not None:
        answered = any(p.lower() == expected.lower() for p in preds)

    ok = nonempty and not leaks and (answered is not False)
    return {
        "id": probe.get("id"),
        "surface": "forward",
        "class": "forward",
        "held_out": bool(probe.get("held_out", False)),
        "prompt": prompt,
        "expected_answer_surface": expected,
        "predictions": preds,
        "nonempty": nonempty,
        "leaks": leaks,
        "answer_reached": answered,
        "latency_s": round(latency, 4),
        "forward_clean": ok,
        "miss": not ok,
    }


def run_chat(api: str, probe: dict, type_names: set[str]) -> dict:
    """Exercise the actual non-streaming OpenAI-compatible product endpoint."""
    prompt = probe["prompt"]
    expected = probe.get("expected_answer_surface")
    t0 = time.perf_counter()
    content, session, response = chat_completion(api, prompt)
    latency = time.perf_counter() - t0
    content = content.strip()
    nonempty = bool(content)
    leaks = text_leaks(content, type_names)
    answered = None
    if expected is not None:
        answered = contains_expected_surface(content, str(expected))
    metadata = response.get("metadata") if isinstance(response.get("metadata"), dict) else {}
    reply_rows = metadata.get("reply_rows") if isinstance(metadata, dict) else None
    if reply_rows is None and isinstance(metadata, dict):
        reply_rows = metadata.get("replyRows")
    if reply_rows is not None and not isinstance(reply_rows, int):
        reply_rows = None
    session_present = bool(session)
    ok = nonempty and not leaks and session_present and (answered is not False)
    return {
        "id": probe.get("id"),
        "surface": "chat",
        "class": "forward",
        "held_out": bool(probe.get("held_out", False)),
        "prompt": prompt,
        "expected_answer_surface": expected,
        "content": content,
        "nonempty": nonempty,
        "leaks": leaks,
        "answer_reached": answered,
        "session_present": session_present,
        "reply_rows": reply_rows,
        "latency_s": round(latency, 4),
        "chat_clean": ok,
        "miss": not ok,
    }


def run_op_election(api: str, probe: dict) -> dict:
    prompt = probe["prompt"]
    expected = probe.get("expected_topic_surface")
    mode = probe.get("election_via", "prompt_coherence")
    t0 = time.perf_counter()
    if mode == "resolve_topic":
        got = resolve_topic_surface(api, prompt)
        sense = None
        latency = time.perf_counter() - t0
        specificity = None
    else:
        got, sense, specificity, latency = prompt_coherence_rank1(api, prompt)
    ok = expected is not None and got is not None and got.lower() == expected.lower()
    return {
        "id": probe.get("id"),
        "surface": "op",
        "class": probe.get("class", "election"),
        "held_out": bool(probe.get("held_out", False)),
        "prompt": prompt,
        "expected_topic_surface": expected,
        "got_topic_surface": got,
        "got_sense_surface": sense,
        "specificity": specificity,
        "latency_s": round(latency, 4),
        "election_correctness": ok,
        "miss": not ok,
    }


def summarize_surface(verdicts: dict, name: str, rows: list[dict]) -> None:
    if not rows:
        return
    empty = [r for r in rows if not r.get("nonempty")]
    leaked = [r for r in rows if r.get("leaks")]
    answerable = [r for r in rows if r.get("answer_reached") is not None]
    unreached = [r for r in answerable if r.get("answer_reached") is False]
    verdicts[f"{name}_output"] = {
        "passed": len(rows) - len(empty),
        "total": len(rows),
        "all_nonempty": len(empty) == 0,
        "empty": [r["id"] for r in empty],
    }
    verdicts[f"{name}_hygiene"] = {
        "passed": len(rows) - len(leaked),
        "total": len(rows),
        "clean": len(leaked) == 0,
        "leaks": sorted({leak for r in leaked for leak in r["leaks"]}),
    }
    verdicts[f"{name}_answer"] = {
        "passed": len(answerable) - len(unreached),
        "total": len(answerable),
        "exact": len(answerable) > 0 and len(unreached) == 0,
        "unreached": [r["id"] for r in unreached],
    }
    if name == "chat":
        missing_session = [r for r in rows if not r.get("session_present")]
        verdicts["chat_session"] = {
            "passed": len(rows) - len(missing_session),
            "total": len(rows),
            "all_present": len(missing_session) == 0,
            "missing": [r["id"] for r in missing_session],
        }


def surface_passes(verdicts: dict, name: str) -> bool:
    result = (
        verdicts.get(f"{name}_output", {}).get("all_nonempty") is True
        and verdicts.get(f"{name}_hygiene", {}).get("clean") is True
        and verdicts.get(f"{name}_answer", {}).get("exact") is True
    )
    if name == "chat":
        result = result and verdicts.get("chat_session", {}).get("all_present") is True
    return result


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--api", default="http://127.0.0.1:8080", help="deployed HTTP base")
    ap.add_argument("--probes", type=Path, default=DEFAULT_PROBES)
    ap.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    ap.add_argument("--report", type=Path, default=None)
    ap.add_argument("--record", action="store_true", help="write baseline from this run")
    ap.add_argument(
        "--surfaces",
        default="op,forward,chat",
        help="comma list: op,forward,chat",
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
    unknown_surfaces = surfaces - {"op", "forward", "chat"}
    if unknown_surfaces:
        sys.stderr.write(f"unknown evaluation surfaces: {sorted(unknown_surfaces)}\n")
        sys.exit(2)

    results: list[dict] = []
    type_names: set[str] = set()
    if any(p.get("class") == "forward" for p in probes) and ({"forward", "chat"} & surfaces):
        type_names = entity_type_names(args.api)

    try:
        for probe in probes:
            if probe.get("class") == "forward":
                if "forward" in surfaces:
                    results.append(run_forward(args.api, probe, type_names))
                if "chat" in surfaces:
                    results.append(run_chat(args.api, probe, type_names))
                continue
            if "op" in surfaces and probe.get("surface", "op") in ("op", "both"):
                if probe.get("class") == "election" or probe.get("expected_topic_surface"):
                    results.append(run_op_election(args.api, probe))
    except (LaplaceApiError, KeyError, TypeError, ValueError) as ex:
        sys.stderr.write(f"product evaluation failed: {ex}\n")
        sys.exit(1)

    # Misses before hits (plan standard of evidence).
    results.sort(key=lambda r: (0 if r.get("miss") else 1, r.get("surface") or "", r.get("id") or ""))

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

    forward = [r for r in results if r.get("surface") == "forward"]
    chat = [r for r in results if r.get("surface") == "chat"]
    summarize_surface(verdicts, "forward", forward)
    summarize_surface(verdicts, "chat", chat)
    if "op" in surfaces and not election:
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
            "sources": sources,
            "fingerprint": fp,
            "election": {
                "passed": len(election_ok),
                "total": len(election),
                "require_exact": True,
            },
            "latency_ceiling_s": latency_ceiling,
            "notes": (
                "election correctness and each enabled production surface independently "
                "block on nonempty output, hygiene, and explicit expected-answer probes; "
                "chat additionally requires a returned substrate session key. Baseline "
                "sources are a required floor; row estimates are diagnostic only."
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

    enabled_product_surfaces = [name for name in ("forward", "chat") if name in surfaces]
    ok = (
        ("op" not in surfaces or (
            len(election) > 0
            and verdicts["election_correctness"]["exact"]
            and latency_budget_ok
            and "no_scorable_probes" not in verdicts
        ))
        and "fingerprint_drift" not in verdicts
        and bool(enabled_product_surfaces)
        and all(surface_passes(verdicts, name) for name in enabled_product_surfaces)
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
        f"forward_output {verdicts.get('forward_output', {}).get('passed', 0)}/"
        f"{verdicts.get('forward_output', {}).get('total', 0)} nonempty; "
        f"forward_answer {verdicts.get('forward_answer', {}).get('passed', 0)}/"
        f"{verdicts.get('forward_answer', {}).get('total', 0)} exact; "
        f"chat_output {verdicts.get('chat_output', {}).get('passed', 0)}/"
        f"{verdicts.get('chat_output', {}).get('total', 0)} nonempty; "
        f"chat_answer {verdicts.get('chat_answer', {}).get('passed', 0)}/"
        f"{verdicts.get('chat_answer', {}).get('total', 0)} exact; "
        f"chat_session {verdicts.get('chat_session', {}).get('passed', 0)}/"
        f"{verdicts.get('chat_session', {}).get('total', 0)} present; "
        f"p50_latency={p50}s ceiling={latency_ceiling}s"
    )
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
