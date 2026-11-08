#!/usr/bin/env python3
"""Helper functions for eval-generation.py."""

from __future__ import annotations

import importlib.util
import re
import time
from pathlib import Path

from laplace_api import chat_completion, op_rows

ROOT = Path(__file__).resolve().parent.parent


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
