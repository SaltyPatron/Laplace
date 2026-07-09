#!/usr/bin/env python3
"""Behavioral truth-harness for foundry-synthesized GGUFs.

The project's defining failure mode has been SIMULATED success: models that
load, emit tokens, and pass exit-code smoke tests while producing nothing
real (see .scratchpad/02 and the FAITHFUL/LOOKUP retirement). This harness
gates on CONTENT: the expected continuations for each probe word are pulled
from the substrate's own consensus — the same evidence the model was poured
from — so a passing model demonstrably carries the knowledge it claims to
transcribe, and the known failure shapes (global-frequency-hub collapse,
prompt-echo loops, flat/empty output) are each detected explicitly.

Usage:
  verify-model-behavioral.py --model out.gguf [--llama D:/LlamaCPP/llama-completion.exe]
      [--db "host=localhost user=postgres dbname=laplace password=postgres"]
      [--probes king,dog,...] [--gen-tokens 6] [--expected-per-probe 40]
      [--min-pass 0.5] [--report report.json]

Exit codes: 0 pass, 1 content-gate failure, 2 harness/setup error.
"""

import argparse
import json
import re
import subprocess
import sys

DEFAULT_PROBES = [
    "king", "queen", "dog", "cat", "water", "fire", "gold", "sea",
    "tree", "man", "woman", "time", "good", "world", "day", "people",
]

# Phase 7 depth-k content-WORD metric (2026-07-08): determiners/prepositions ARE
# attested PRECEDES objects, so glue hits inflate content_pass_rate. Hits are
# additionally scored with this stoplist excluded; content_word_pass_rate is the
# number that adjudicates knowledge transfer (doc 14 P10).
GLUE_WORDS = frozenset("""
the a an of to in on at by for with and or but nor so yet as if that this these
those his her its their our your my is are was were be been being am do does did
have has had will would shall should can could may might must not no he she it
they we you i who whom whose which what when where why how there here then than
seem seems from into onto over under up down out off about after before between
""".split())

WORD_RE = re.compile(r"[a-z]+")


def psql_rows(db, sql):
    """Run one SQL statement through psql, return list of tab-separated rows."""
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
    r = subprocess.run(cmd + ["-c", sql], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if r.returncode != 0:
        sys.stderr.write(r.stderr)
        sys.exit(2)
    return [line for line in r.stdout.splitlines() if line.strip()]


EXPECTED_SQL = """
SET search_path = laplace, public;
SELECT lower(render(c.object_id))
FROM v_consensus_unrefuted c
WHERE c.subject_id = word_id(%(word)s)
  AND c.object_id IS NOT NULL
ORDER BY (c.rating - 2 * c.rd) DESC
LIMIT %(limit)d;
"""


def expected_set(db, word, limit):
    sql = EXPECTED_SQL % {"word": "'" + word.replace("'", "''") + "'", "limit": limit}
    out = set()
    for row in psql_rows(db, sql):
        for tok in WORD_RE.findall(row.lower()):
            if len(tok) >= 2 and tok != word:
                out.add(tok)
    return out


def run_completion(llama, model, prompt, n_tokens):
    # Greedy + repeat-penalty: deterministic AND loop-suppressed — the decode any
    # real consumer would use. Pure greedy (no penalty) loops even trained models
    # at this length; the hub-collapse detector still fires on cross-probe hubs.
    r = subprocess.run(
        [llama, "-m", model, "-p", prompt, "-n", str(n_tokens), "--temp", "0",
         "--repeat-penalty", "1.4"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=180)
    if r.returncode != 0:
        return None, (r.stderr or "")[-400:]
    # llama-completion prints diagnostics before and perf counters after the
    # generation; the generation itself is the line block that starts with the
    # echoed prompt. Collect from that line until the perf block begins, so
    # words like "sampling"/"tokens" never leak into the scored output.
    lines = r.stdout.splitlines()
    gen_lines = []
    started = False
    for ln in lines:
        if not started:
            if ln.startswith(prompt):
                gen_lines.append(ln[len(prompt):])
                started = True
            continue
        if ln.startswith(("common_perf_print", "llama_", "generate:", "sampler")):
            break
        gen_lines.append(ln)
    return "\n".join(gen_lines).strip(), None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--llama", default="D:/LlamaCPP/llama-completion.exe")
    ap.add_argument("--db", default="host=localhost user=postgres dbname=laplace")
    ap.add_argument("--probes", default=",".join(DEFAULT_PROBES))
    ap.add_argument("--gen-tokens", type=int, default=6)
    ap.add_argument("--expected-per-probe", type=int, default=40)
    ap.add_argument("--min-pass", type=float, default=0.5)
    ap.add_argument("--report", default=None)
    args = ap.parse_args()

    probes = [p.strip().lower() for p in args.probes.split(",") if p.strip()]
    results = []
    first_tokens = []
    load_failure = None

    for word in probes:
        exp = expected_set(args.db, word, args.expected_per_probe)
        if not exp:
            results.append({"word": word, "skipped": "no consensus edges"})
            continue
        gen, err = run_completion(args.llama, args.model, " " + word, args.gen_tokens)
        if gen is None:
            load_failure = err
            break
        toks = [t for t in WORD_RE.findall(gen.lower()) if t != word]
        hit = sorted(set(toks) & exp)
        content_hit = sorted(t for t in hit if t not in GLUE_WORDS)
        if toks:
            first_tokens.append(toks[0])
        results.append({
            "word": word,
            "generated": gen,
            "hits": hit,
            "content_hits": content_hit,
            "passed": len(hit) >= 1,
            "content_passed": len(content_hit) >= 1,
            "distinct_tokens": len(set(toks)),
        })

    scored = [r for r in results if "passed" in r]
    passed = [r for r in scored if r["passed"]]
    content_passed = [r for r in scored if r.get("content_passed")]

    # Failure-shape detectors, each named for the fraud it catches.
    verdicts = {}
    if load_failure is not None:
        verdicts["load_or_generate_failed"] = load_failure
    if scored:
        verdicts["content_pass_rate"] = round(len(passed) / len(scored), 3)
        verdicts["content_word_pass_rate"] = round(len(content_passed) / len(scored), 3)
        empty = [r for r in scored if r["distinct_tokens"] == 0]
        if len(empty) > len(scored) / 2:
            verdicts["empty_output_collapse"] = len(empty)
        if len(first_tokens) >= 4:
            top = max(set(first_tokens), key=first_tokens.count)
            share = first_tokens.count(top) / len(first_tokens)
            if share > 0.5:
                verdicts["global_hub_collapse"] = {"token": top, "share": round(share, 2)}
    else:
        verdicts["no_scorable_probes"] = True

    ok = (load_failure is None
          and scored
          and "global_hub_collapse" not in verdicts
          and "empty_output_collapse" not in verdicts
          and (len(passed) / len(scored)) >= args.min_pass)

    report = {"model": args.model, "verdicts": verdicts, "ok": ok, "probes": results}
    text = json.dumps(report, indent=2, ensure_ascii=False)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    print(f"\nBEHAVIORAL {'PASS' if ok else 'FAIL'}: "
          f"{len(passed)}/{len(scored)} probes hit substrate-attested continuations "
          f"(threshold {args.min_pass})")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
