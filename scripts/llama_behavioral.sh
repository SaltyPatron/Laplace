#!/bin/bash
# scripts/llama_behavioral.sh
# CPU-only behavioral-validation harness for Laplace model exports.
#
# Runs a GGUF model under llama.cpp (CPU, greedy decode) over a list of prompts
# and records, per prompt: the prompt, the generated continuation, the model's
# reported generation tokens/sec, and the wall-clock time — as a JSON array.
#
# This establishes the behavioral baseline that a Laplace-exported GGUF (the
# substrate poured back into the target architecture's mold) is later compared
# against: same prompts, same harness, diff the continuations.
#
# Usage:
#   scripts/llama_behavioral.sh <model.gguf> [prompts.txt] [out.json]
#
#   <model.gguf>   Path to the GGUF to evaluate (required).
#   [prompts.txt]  One prompt per line (default: scripts/prompts_smoke.txt).
#                  Blank lines and lines beginning with '#' are ignored.
#   [out.json]     Output JSON array path (default: /tmp/llama_behavioral.json).
#
# Environment (override without editing the script):
#   LLAMA_BIN       Path to the non-interactive completion binary
#                   (llama-completion; older trees: a non-interactive llama-cli/main).
#                   Default: the CPU-only build under the llama.cpp workspace.
#                   NOTE: modern llama.cpp split the tooling — `llama-cli` is
#                   interactive chat only and BLOCKS on stdin, so it is the wrong
#                   binary for a batch harness. Use `llama-completion`.
#   LLAMA_LIB_DIR   Directory holding the llama.cpp shared libs (libllama.so, ...).
#                   Default: the same bin/ dir as LLAMA_BIN.
#   N_PREDICT       Tokens to generate per prompt (default: 64).
#   TEMP            Sampling temperature; 0 = greedy/deterministic (default: 0).
#   THREADS         CPU threads (default: nproc).
#   CTX_SIZE        Context size (default: 2048).
#   EXTRA_ARGS      Extra args appended verbatim to every invocation.
#
# Fail-loud: any missing binary/model/prompt-file, or any non-zero exit from the
# completion binary, aborts the whole run with a non-zero status. No partial JSON
# is emitted. stdin is fed from /dev/null so a binary can never hang the harness.

set -euo pipefail

# ---- args -------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

MODEL="${1:-}"
PROMPTS="${2:-$SCRIPT_DIR/prompts_smoke.txt}"
OUT="${3:-/tmp/llama_behavioral.json}"

if [[ -z "$MODEL" ]]; then
    echo "Usage: $0 <model.gguf> [prompts.txt] [out.json]" >&2
    exit 2
fi

# ---- completion-binary discovery --------------------------------------------
# Default to the CPU-only llama.cpp build. Overridable via LLAMA_BIN for any
# other build/host. We resolve a binary; we never silently fall back to a GPU one.
DEFAULT_LLAMA_BIN="/data/archive/llama-workspace/llama.cpp/build-cpu/bin/llama-completion"
LLAMA_BIN="${LLAMA_BIN:-$DEFAULT_LLAMA_BIN}"

if [[ ! -x "$LLAMA_BIN" ]]; then
    # Fall back to anything appropriate on PATH (prefer the non-interactive ones).
    if command -v llama-completion >/dev/null 2>&1; then
        LLAMA_BIN="$(command -v llama-completion)"
    elif command -v llama-cli >/dev/null 2>&1; then
        LLAMA_BIN="$(command -v llama-cli)"
    elif command -v main >/dev/null 2>&1; then
        LLAMA_BIN="$(command -v main)"
    else
        echo "FATAL: completion binary not found. Set LLAMA_BIN to the path." >&2
        echo "       (looked for: $DEFAULT_LLAMA_BIN, llama-completion/llama-cli/main on PATH)" >&2
        exit 1
    fi
fi

# llama.cpp ships its libs (libllama.so, libggml*.so, libmtmd.so) next to the
# binary; the binary won't load without them on the loader path.
LLAMA_LIB_DIR="${LLAMA_LIB_DIR:-$(dirname "$LLAMA_BIN")}"
export LD_LIBRARY_PATH="$LLAMA_LIB_DIR:${LD_LIBRARY_PATH:-}"

# ---- input validation -------------------------------------------------------
if [[ ! -f "$MODEL" ]]; then
    echo "FATAL: model GGUF not found: $MODEL" >&2
    exit 1
fi
if [[ ! -f "$PROMPTS" ]]; then
    echo "FATAL: prompts file not found: $PROMPTS" >&2
    exit 1
fi

# ---- knobs ------------------------------------------------------------------
N_PREDICT="${N_PREDICT:-64}"
TEMP="${TEMP:-0}"
THREADS="${THREADS:-$(nproc)}"
CTX_SIZE="${CTX_SIZE:-2048}"
EXTRA_ARGS="${EXTRA_ARGS:-}"

echo "llama_behavioral: CPU behavioral harness" >&2
echo "  binary    : $LLAMA_BIN" >&2
echo "  lib dir   : $LLAMA_LIB_DIR" >&2
echo "  model     : $MODEL" >&2
echo "  prompts   : $PROMPTS" >&2
echo "  output    : $OUT" >&2
echo "  n_predict=$N_PREDICT temp=$TEMP threads=$THREADS ctx=$CTX_SIZE" >&2
"$LLAMA_BIN" --version >&2 2>&1 || true
echo >&2

# ---- run --------------------------------------------------------------------
# Build the JSON array incrementally; jq does the escaping so arbitrary prompt /
# completion text is safe.
results="[]"
n=0

while IFS= read -r prompt || [[ -n "$prompt" ]]; do
    # Skip blank lines and comments.
    [[ -z "${prompt//[[:space:]]/}" ]] && continue
    [[ "$prompt" =~ ^[[:space:]]*# ]] && continue

    n=$((n + 1))
    echo "[$n] prompt: $prompt" >&2

    raw_stderr="$(mktemp)"

    start_ns="$(date +%s%N)"
    # --no-display-prompt : stdout = generated continuation only (prompt excluded)
    # --temp 0            : greedy/deterministic by default
    # </dev/null          : never let the binary block waiting on stdin
    set +e
    gen="$("$LLAMA_BIN" \
        -m "$MODEL" \
        -p "$prompt" \
        -n "$N_PREDICT" \
        --temp "$TEMP" \
        -t "$THREADS" \
        -c "$CTX_SIZE" \
        --no-display-prompt \
        $EXTRA_ARGS \
        </dev/null 2>"$raw_stderr")"
    rc=$?
    set -e
    end_ns="$(date +%s%N)"

    if [[ $rc -ne 0 ]]; then
        echo "FATAL: $LLAMA_BIN exited $rc on prompt: $prompt" >&2
        echo "----- completion-binary stderr -----" >&2
        cat "$raw_stderr" >&2
        rm -f "$raw_stderr"
        exit "$rc"
    fi

    wall_ms="$(( (end_ns - start_ns) / 1000000 ))"

    # Pull generation tokens/sec from the "eval time = ... N runs (... tokens per second)"
    # line (the decode phase, not the prompt-eval phase). Empty if not reported.
    tps="$(grep -E 'eval time =.*runs' "$raw_stderr" \
            | grep -oE '[0-9]+\.[0-9]+ tokens per second' \
            | grep -oE '^[0-9]+\.[0-9]+' \
            | tail -n1 || true)"
    [[ -z "$tps" ]] && tps="null"

    rm -f "$raw_stderr"

    # llama-completion ends the stream with an "> EOF by user" marker line; drop it
    # (and any trailing prompt-marker lines) so `generated` is the model text only.
    gen="$(printf '%s' "$gen" | sed -e '/^> EOF by user$/d' -e '/^>[[:space:]]*$/d')"
    # Trim leading/trailing whitespace left by --no-display-prompt and the strip above.
    gen="${gen#"${gen%%[![:space:]]*}"}"
    gen="${gen%"${gen##*[![:space:]]}"}"

    results="$(jq \
        --arg prompt "$prompt" \
        --arg generated "$gen" \
        --argjson tps "$tps" \
        --argjson wall_ms "$wall_ms" \
        '. += [{
            prompt: $prompt,
            generated: $generated,
            tokens_per_second: $tps,
            wall_ms: $wall_ms
        }]' <<<"$results")"

    echo "    -> ${tps} tok/s, ${wall_ms} ms wall" >&2
done <"$PROMPTS"

if [[ "$n" -eq 0 ]]; then
    echo "FATAL: no usable prompts in $PROMPTS" >&2
    exit 1
fi

# ---- emit -------------------------------------------------------------------
printf '%s\n' "$results" | jq '.' >"$OUT"
echo >&2
echo "llama_behavioral: wrote $n result(s) to $OUT" >&2
