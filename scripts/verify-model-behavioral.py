#!/usr/bin/env python3
"""Behavioral truth-harness for foundry-synthesized GGUFs.

The project's defining failure mode has been SIMULATED success: models that
load, emit tokens, and pass exit-code smoke tests while producing nothing
real (see .scratchpad/02 and the FAITHFUL/LOOKUP retirement). This harness
gates on CONTENT: the expected continuations for each probe word are pulled
from the substrate's own consensus — the same evidence the model was synthesized
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
import os
import re
import shutil
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


# SCHEMA-QUALIFIED, no SET search_path. The nine purpose schemas (ops, consensus, converse,
# lexical, taxonomy, generation, structural, chess, realize) are deliberately kept OFF
# search_path (purpose_schemas.sql.in, #862/#957), so the bare `render(...)` this used to
# call has not resolved since that migration -- another reason this harness could not run.
EXPECTED_SQL = """
SELECT lower(realize.render(c.object_id))
FROM laplace.v_consensus_unrefuted c
WHERE c.subject_id = laplace.word_id(%(word)s)
  AND c.object_id IS NOT NULL
ORDER BY consensus.eff_mu(c.rating, c.rd) DESC
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


# OLLAMA BACKEND. The harness assumed a llama-completion binary, which is not vendored in
# external/ and is not on the runner, so it could never run and was never wired into CI --
# leaving "the file exists and is over 50 MB" as the only synthesis gate, which is the
# SIMULATED success this file's own header names as the project's defining failure mode.
#
# ollama IS present (/usr/local/bin/ollama, with the ggml runtime under
# /usr/local/lib/ollama) and serves an HTTP API. It runs a bare GGUF through a one-line
# Modelfile, so the artifact under test needs no conversion and no tokenizer directory.
#
# Same decode contract as the llama path: greedy (temperature 0) and repeat-penalised, so
# a model is judged on content rather than on sampling luck, and the hub-collapse detector
# still sees cross-probe repetition.
LLAMA_BIN_CANDIDATES = (
    "/data/archive/llama-workspace/llama.cpp/build/bin/llama-completion",
    "/data/archive/llama-workspace/llama.cpp/build-cpu/bin/llama-completion",
)


def resolve_llama_bin():
    """Find a llama-completion binary, or None.

    The default used to be the literal string "D:/LlamaCPP/llama-completion.exe", so the
    harness could not run on any machine that was not one particular Windows box -- which
    is most of why it was invoked by no workflow. Resolution order: explicit env, PATH,
    then known build locations.
    """
    def usable(path):
        # Executable is not runnable: one of the checked-in builds is present and
        # +x but dies with "libllama.so.0: cannot open shared object file". Selecting
        # it would fail every probe and report 0/0, which reads as a model verdict
        # rather than a broken runner. Invoke it before trusting it.
        if not path or not os.path.isfile(path) or not os.access(path, os.X_OK):
            return False
        try:
            r = subprocess.run([path, "--help"], capture_output=True, timeout=30)
        except Exception:
            return False
        return r.returncode == 0

    env = os.environ.get("LAPLACE_LLAMA_BIN")
    if env:
        return env if usable(env) else None
    for cand in (shutil.which("llama-completion"), *LLAMA_BIN_CANDIDATES):
        if usable(cand):
            return cand
    return None


def ollama_ensure_model(url, tag, gguf_path):
    """Register the artifact under test.

    Creation goes through the CLI, not POST /api/create: since 0.13 that endpoint takes
    pre-uploaded blob digests rather than a local path, so handing it a file returns 400.
    `ollama create -f <Modelfile>` takes the path directly, which is what a bare
    just-synthesized GGUF is. Generation still uses the HTTP API.
    """
    import tempfile
    ollama_bin = os.environ.get("LAPLACE_OLLAMA_BIN", "ollama")
    with tempfile.TemporaryDirectory() as td:
        mf = os.path.join(td, "Modelfile")
        with open(mf, "w", encoding="utf-8") as fh:
            fh.write(f"FROM {os.path.abspath(gguf_path)}\n")
        try:
            r = subprocess.run([ollama_bin, "create", tag, "-f", mf],
                               capture_output=True, text=True, timeout=900)
        except Exception as exc:
            return f"ollama create failed to launch ({ollama_bin}): {exc}"
    if r.returncode != 0:
        return f"ollama create failed: {(r.stderr or r.stdout or '')[-400:]}"
    return None


def run_completion_ollama(url, tag, prompt, n_tokens):
    import urllib.request
    body = json.dumps({
        "model": tag, "prompt": prompt, "stream": False, "raw": True,
        "options": {"temperature": 0, "num_predict": n_tokens, "repeat_penalty": 1.4},
    }).encode()
    try:
        req = urllib.request.Request(f"{url}/api/generate", data=body,
                                     headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=300) as fh:
            payload = json.loads(fh.read().decode("utf-8", "replace"))
    except Exception as exc:
        return None, f"ollama generate failed: {exc}"
    if payload.get("error"):
        return None, str(payload["error"])
    return (payload.get("response") or "").strip(), None


def run_completion(llama, model, prompt, n_tokens):
    # Greedy + repeat-penalty: deterministic AND loop-suppressed — the decode any
    # real consumer would use. Pure greedy (no penalty) loops even trained models
    # at this length; the hub-collapse detector still fires on cross-probe hubs.
    # -no-cnv and stdin=DEVNULL are both load-bearing. Without them the binary enters
    # conversation mode, wraps the probe in the GGUF's chat template (so the scored text
    # begins "<|user|> king<|assistant|>" rather than the continuation), and then BLOCKS on
    # inherited stdin until the 180s timeout -- which surfaces as a probe failure and reads
    # as a verdict on the model rather than on the harness.
    r = subprocess.run(
        [llama, "-m", model, "-p", prompt, "-n", str(n_tokens), "--temp", "0",
         "--repeat-penalty", "1.4", "-no-cnv", "--no-display-prompt"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
        stdin=subprocess.DEVNULL, timeout=180)
    if r.returncode != 0:
        return None, (r.stderr or "")[-400:]
    # llama-completion prints diagnostics before and perf counters after the
    # generation; the generation itself is the line block that starts with the
    # echoed prompt. Collect from that line until the perf block begins, so
    # words like "sampling"/"tokens" never leak into the scored output.
    # With --no-display-prompt the generation is the whole of stdout; keep the perf-block
    # cutoff so counters never leak into the scored text, and keep tolerating a build that
    # echoes the prompt anyway.
    gen_lines = []
    for ln in r.stdout.splitlines():
        if ln.startswith(("common_perf_print", "llama_", "generate:", "sampler")):
            break
        gen_lines.append(ln[len(prompt):] if ln.startswith(prompt) else ln)
    return "\n".join(gen_lines).strip(), None


def likelihood_mode(args, probes):
    """Finish-line Phase 3 gate: score the model's next-token DISTRIBUTION, not a
    greedy string (argmax of true English bigram conditionals IS glue — a correct
    floor 'fails' greedy evals by design). Reports mean reciprocal rank + mean
    rank of substrate-attested continuations via the numpy oracle, plus the
    math-proof check: Spearman correlation between the model's logits and the
    substrate's own log-conditional over the attested pairs."""
    import importlib.util
    here = os.path.dirname(os.path.abspath(__file__))
    spec = importlib.util.spec_from_file_location("oracle", os.path.join(here, "model-forward-oracle.py"))
    oracle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(oracle)
    import numpy as np

    kv, T = oracle._read_gguf(args.model)
    vocab = oracle.gguf_vocab(args.tokenizer)

    expected = {}
    for word in probes:
        wid = vocab.get(word) or vocab.get("▁" + word)
        if wid is None:
            continue
        tids = []
        for tok in expected_set(args.db, word, args.expected_per_probe):
            tid = vocab.get("▁" + tok) or vocab.get(tok)
            if tid is not None:
                tids.append(int(tid))
        expected[word] = (wid, tids)

    def depth_metrics(layers):
        ranks, per_probe = [], []
        for word, (wid, tids) in expected.items():
            logits = oracle.gguf_forward(kv, T, [wid], layers=layers).astype("float64")
            order = np.argsort(logits)[::-1]
            rank_of = {int(i): r for r, i in enumerate(order, 1)}
            probe_ranks = [rank_of[t] for t in tids]
            ranks.extend(probe_ranks)
            if probe_ranks:
                per_probe.append({"word": word, "n_expected_in_vocab": len(probe_ranks),
                                  "best_rank": min(probe_ranks),
                                  "mean_rank": round(sum(probe_ranks) / len(probe_ranks), 1)})
        verdicts = {}
        if ranks:
            verdicts["mean_rank"] = round(sum(ranks) / len(ranks), 1)
            verdicts["mrr"] = round(sum(1.0 / r for r in ranks) / len(ranks), 4)
            verdicts["hits_at_50"] = round(sum(1 for r in ranks if r <= 50) / len(ranks), 3)
            verdicts["vocab_size"] = len(vocab)
        return verdicts, per_probe

    if args.floor_gate:
        # Attenuation gate (doc 14 §6b 2026-07-09): the correction planes are
        # tail-only, so strict monotone improvement is unachievable — the full
        # forward must improve mean rank while holding hits@50 exactly and
        # keeping MRR within 3% of the calibrated floor (layers=0).
        floor, _ = depth_metrics(0)
        full, per_probe = depth_metrics(None)
        ok = (bool(floor) and bool(full)
              and full["mean_rank"] <= floor["mean_rank"]
              and full["hits_at_50"] >= floor["hits_at_50"]
              and full["mrr"] >= 0.97 * floor["mrr"])
        verdicts = {"floor": floor, "full": full}
    else:
        verdicts, per_probe = depth_metrics(args.layers)
        ok = bool(verdicts) and verdicts.get("mean_rank", 1e9) <= max(50, len(vocab) * 0.02)
    report = {"model": args.model, "mode": "likelihood", "verdicts": verdicts,
              "ok": ok, "probes": per_probe}
    text = json.dumps(report, indent=2, ensure_ascii=False)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    if args.floor_gate:
        f0, ff = verdicts.get("floor", {}), verdicts.get("full", {})
        print(f"\nFLOOR-GATE {'PASS' if ok else 'FAIL'}: mean rank "
              f"{f0.get('mean_rank')}→{ff.get('mean_rank')}, "
              f"mrr {f0.get('mrr')}→{ff.get('mrr')}, "
              f"hits@50 {f0.get('hits_at_50')}→{ff.get('hits_at_50')}")
    else:
        print(f"\nLIKELIHOOD {'PASS' if ok else 'FAIL'}: mean rank "
              f"{verdicts.get('mean_rank')} of {verdicts.get('vocab_size')} "
              f"(mrr {verdicts.get('mrr')}, hits@50 {verdicts.get('hits_at_50')})")
    sys.exit(0 if ok else 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--llama", default=None,
                    help="llama-completion binary; default resolves LAPLACE_LLAMA_BIN, then PATH, "
                         "then known build locations")
    # `auto` is the default because a hardcoded D:/LlamaCPP path meant this harness could
    # never run anywhere it was actually checked out, which is most of why it was wired
    # into no workflow. Prefer llama-completion (the decode this was written against);
    # fall back to ollama, which serves the same GGUF through its HTTP API.
    ap.add_argument("--runner", choices=["auto", "llama", "ollama"], default="auto")
    ap.add_argument("--ollama-url", default="http://127.0.0.1:11434")
    ap.add_argument("--ollama-tag", default="laplace-verify")
    ap.add_argument("--db", default="host=localhost user=postgres dbname=laplace")
    ap.add_argument("--probes", default=",".join(DEFAULT_PROBES))
    ap.add_argument("--gen-tokens", type=int, default=6)
    ap.add_argument("--expected-per-probe", type=int, default=40)
    ap.add_argument("--min-pass", type=float, default=0.5)
    ap.add_argument("--report", default=None)
    ap.add_argument("--mode", choices=["generate", "likelihood"], default="generate")
    ap.add_argument("--tokenizer", default=None,
                    help="tokenizer dir (required for --mode likelihood)")
    ap.add_argument("--layers", type=int, default=None,
                    help="likelihood mode: truncate the forward to N layers (0 = floor only)")
    ap.add_argument("--floor-gate", action="store_true",
                    help="likelihood mode: gate full forward against the layers=0 floor "
                         "(mean rank improves, hits@50 holds, mrr within 3%%)")
    args = ap.parse_args()

    if args.llama is None:
        args.llama = resolve_llama_bin()
    if args.runner == "auto":
        if args.llama:
            args.runner = "llama"
        elif shutil.which(os.environ.get("LAPLACE_OLLAMA_BIN", "ollama")):
            args.runner = "ollama"
        else:
            print("::error::no model runner found: set LAPLACE_LLAMA_BIN to a "
                  "llama-completion binary, or install ollama")
            return 2
    if args.runner == "llama" and not args.llama:
        print("::error::--runner llama but no llama-completion binary resolved "
              "(set LAPLACE_LLAMA_BIN)")
        return 2

    if args.runner == "ollama":
        err = ollama_ensure_model(args.ollama_url, args.ollama_tag, args.model)
        if err:
            print(f"::error::{err}")
            return 2

    if args.mode == "likelihood":
        if not args.tokenizer:
            sys.exit("--mode likelihood requires --tokenizer <dir>")
        likelihood_mode(args, [p.strip().lower() for p in args.probes.split(",") if p.strip()])
        return

    probes = [p.strip().lower() for p in args.probes.split(",") if p.strip()]
    results = []
    first_tokens = []
    load_failure = None

    for word in probes:
        exp = expected_set(args.db, word, args.expected_per_probe)
        if not exp:
            results.append({"word": word, "skipped": "no consensus edges"})
            continue
        gen, err = (
            run_completion_ollama(args.ollama_url, args.ollama_tag, " " + word, args.gen_tokens)
            if args.runner == "ollama"
            else run_completion(args.llama, args.model, " " + word, args.gen_tokens))
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
