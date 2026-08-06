#!/usr/bin/env python3
"""W5 generation-quality runner (#755) — measurement half.

Runs the smoke prompts through BOTH generation lanes via the installed
surface (laplace.generation_probe: converse_walk incumbent, converse_compose
challenger) and scores each lane with the R6 detector set the plan names
(modeled on verify-model-behavioral.py):

  seed_variance   distinct replies across seeds (1 == the replay failure
                  converse_compose's header gates wiring on)
  echo_rate       fraction of reply tokens equal to the prompt token
                  (converse_tiered's "dog. dog. dog." class, GH #878)
  flatness        1 - distinct/total tokens across a lane's replies —
                  HIGHER means flatter (more internal repetition)
  content_rate    fraction of reply content words found in the topic's own
                  expected-continuation set (consensus objects by eff_mu,
                  probe word and short tokens excluded, per the behavioral
                  harness) — sentence-shaped smoke prompts skip this metric

Exit codes: 0 measured (report printed), 2 harness/setup error. This half
does NOT gate: thresholds and the CI seeded-fixture job are the second half
of #755 and land with it. A measurement that exists beats a gate that
doesn't; word salad is at least VISIBLE from tonight.

Usage:
  verify-generation.py [--db "host=localhost user=postgres dbname=laplace"]
      [--prompts scripts/prompts_smoke.txt] [--probes dog,water,king]
      [--seeds 7,991,12345] [--steps 30] [--expected-per-probe 40]
      [--report report.json]
"""

import argparse
import json
import os
import re
import subprocess
import sys

GLUE_WORDS = frozenset("""
the a an of to in on at by for with and or but nor so yet as if that this these
those his her its their our your my is are was were be been being am do does did
have has had will would shall should can could may might must not no he she it
they we you i who whom whose which what when where why how there here then than
seem seems from into onto over under up down out off about after before between
""".split())

# \w+ with UNICODE: generation currently emits whatever language the witnesses
# spoke (no p_lang threading — R1), so an ASCII-only tokenizer scores a fully
# Bulgarian reply as EMPTY. That mistake burned a false "statement interference"
# theory on 2026-08-06 before the debug run showed 130 chars of live reply.
WORD_RE = re.compile(r"\w+", re.UNICODE)


def psql_rows(db, sql):
    cmd = ["psql", "-X", "-q", "-t", "-A", "-F", "\t"]
    for part in db.split():
        k, _, v = part.partition("=")
        flag = {"host": "-h", "user": "-U", "dbname": "-d", "port": "-p"}.get(k)
        if flag:
            cmd += [flag, v]
    r = subprocess.run(cmd + ["-c", sql], capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if r.returncode != 0:
        sys.stderr.write(r.stderr)
        sys.exit(2)
    if os.environ.get("LAPLACE_VG_DEBUG"):
        sys.stderr.write(f"[vg-debug] rc={r.returncode} stdout_len={len(r.stdout)} "
                         f"stderr={r.stderr.strip()[:200]!r}\n")
    return [line for line in r.stdout.splitlines() if line.strip()]


def q(s: str) -> str:
    return s.replace("'", "''")


def expected_set(db, word, limit):
    rows = psql_rows(db, f"""
        SET search_path = laplace, public;
        SELECT lower(render(c.object_id))
        FROM v_consensus_unrefuted c
        WHERE c.subject_id = word_id('{q(word)}')
          AND c.object_id IS NOT NULL
        ORDER BY (c.rating - 2 * c.rd) DESC
        LIMIT {int(limit)};""")
    out = set()
    for r in rows:
        out.update(w.casefold() for w in WORD_RE.findall(r))
    # Match verify-model-behavioral: the probe word itself and short tokens
    # are not evidence of content transfer.
    return {w for w in out if w != word.casefold() and len(w) > 2} - GLUE_WORDS


def probe(db, word, seeds, steps):
    # Through the installed surface (laplace.generation_probe): one row per
    # (lane, seed), one psql spawn per prompt. An earlier per-call-isolation
    # version here defended against a "statement interference" theory that
    # turned out to be an ASCII-tokenizer artifact (see WORD_RE) — the
    # installed op is the product surface and the measurement uses it.
    # JSON aggregation because replies contain newlines.
    seed_arr = ",".join(str(s) for s in seeds)
    rows = psql_rows(db, f"""
        SET search_path = laplace, public;
        SET statement_timeout = '300s';
        SELECT json_agg(json_build_object('lane', lane, 'seed', seed,
                                          'reply', COALESCE(reply, '')))
        FROM generation_probe('{q(word)}', ARRAY[{seed_arr}]::bigint[], {int(steps)});""")
    out = {}
    for rec in json.loads("".join(rows) or "[]") or []:
        out.setdefault(rec["lane"], []).append(rec["reply"])
    return out


def score(prompt, replies, expected):
    toks = [t.lower() for r in replies for t in WORD_RE.findall(r)]
    if not toks:
        return {"empty": True, "seed_variance": len(set(replies))}
    content = [t for t in toks if t not in GLUE_WORDS]
    # Echo = reply tokens that are prompt tokens; for a single-word probe this
    # reduces to repeating the topic word, for sentence prompts it catches
    # prompt-echo loops.
    prompt_toks = {t.casefold() for t in WORD_RE.findall(prompt)}
    return {
        "empty": False,
        "seed_variance": len(set(replies)),
        "echo_rate": round(sum(1 for t in toks if t in prompt_toks) / len(toks), 3),
        "flatness": round(1.0 - len(set(toks)) / len(toks), 3),
        "content_rate": round(
            (sum(1 for t in content if t in expected) / len(content)) if content else 0.0, 3),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default="host=localhost user=postgres dbname=laplace")
    ap.add_argument("--prompts", default="scripts/prompts_smoke.txt")
    ap.add_argument("--probes", default="dog,water,king")
    ap.add_argument("--seeds", default="7,991,12345")
    ap.add_argument("--steps", type=int, default=30)
    ap.add_argument("--expected-per-probe", type=int, default=40)
    ap.add_argument("--report", default=None)
    ap.add_argument("--enforce", action="store_true",
                    help="exit 1 on the unambiguous regression classes: replay "
                         "(variance==1 across >1 seeds — the GH #751/#884 class) "
                         "and echo (echo_rate>0.5 — the GH #878 class). Content "
                         "and flatness stay advisory: the concept-stream work "
                         "(#751 R1) is still moving those numbers.")
    args = ap.parse_args()

    # Probe words get the full detector set (content_rate needs a topic's
    # expected-continuation set); sentence-shaped smoke prompts run with an
    # empty expected set — variance/echo/flatness only.
    probes = [(w.strip(), True) for w in args.probes.split(",") if w.strip()]
    try:
        with open(args.prompts, encoding="utf-8") as f:
            probes += [(line.strip(), False) for line in f if line.strip()]
    except OSError as e:
        print(f"note: smoke prompts skipped ({e})", file=sys.stderr)
    seeds = [int(s) for s in args.seeds.split(",")]

    report = {}
    for word, is_topic in probes:
        expected = expected_set(args.db, word, args.expected_per_probe) if is_topic else set()
        lanes = probe(args.db, word, seeds, args.steps)
        report[word] = {}
        for lane, replies in sorted(lanes.items()):
            s = score(word, replies, expected)
            s["expected_n"] = len(expected)
            report[word][lane] = s
            flags = []
            if s["seed_variance"] == 1 and len(seeds) > 1:
                flags.append("REPLAY")
            if not s["empty"] and s["echo_rate"] > 0.5:
                flags.append("ECHO")
            if s["empty"]:
                flags.append("EMPTY")
            label = word if len(word) <= 24 else word[:21] + "..."
            core = (f" echo={s['echo_rate']} flat={s['flatness']}"
                    + (f" content={s['content_rate']}" if is_topic else "")
                    if not s["empty"] else "")
            print(f"{label:>24} {lane:>8}: variance={s['seed_variance']}/{len(seeds)}"
                  + core + (f"  [{' '.join(flags)}]" if flags else ""))

    if args.report:
        with open(args.report, "w", encoding="utf-8") as f:
            json.dump(report, f, indent=2, sort_keys=True)
        print(f"report -> {args.report}")

    if args.enforce:
        failures = []
        for word, lanes in report.items():
            for lane, s in lanes.items():
                if s["seed_variance"] == 1 and len(seeds) > 1:
                    failures.append(f"{word}/{lane}: REPLAY (variance 1/{len(seeds)})")
                if not s.get("empty") and s.get("echo_rate", 0) > 0.5:
                    failures.append(f"{word}/{lane}: ECHO (echo_rate {s['echo_rate']})")
        if failures:
            print("ENFORCED REGRESSION CLASSES FAILED:", file=sys.stderr)
            for f_ in failures:
                print(f"  {f_}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
