#!/bin/bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

MODEL="${1:-}"
PROMPTS="${2:-$SCRIPT_DIR/prompts_smoke.txt}"
OUT="${3:-/tmp/llama_behavioral.json}"

if [[ -z "$MODEL" ]]; then
    echo "Usage: $0 <model.gguf> [prompts.txt] [out.json]" >&2
    exit 2
fi

DEFAULT_LLAMA_BIN="/data/archive/llama-workspace/llama.cpp/build-cpu/bin/llama-completion"
LLAMA_BIN="${LLAMA_BIN:-$DEFAULT_LLAMA_BIN}"

if [[ ! -x "$LLAMA_BIN" ]]; then
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

LLAMA_LIB_DIR="${LLAMA_LIB_DIR:-$(dirname "$LLAMA_BIN")}"
export LD_LIBRARY_PATH="$LLAMA_LIB_DIR:${LD_LIBRARY_PATH:-}"

if [[ ! -f "$MODEL" ]]; then
    echo "FATAL: model GGUF not found: $MODEL" >&2
    exit 1
fi
if [[ ! -f "$PROMPTS" ]]; then
    echo "FATAL: prompts file not found: $PROMPTS" >&2
    exit 1
fi

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

results="[]"
n=0

while IFS= read -r prompt || [[ -n "$prompt" ]]; do
    [[ -z "${prompt//[[:space:]]/}" ]] && continue
    [[ "$prompt" =~ ^[[:space:]]*# ]] && continue

    n=$((n + 1))
    echo "[$n] prompt: $prompt" >&2

    raw_stderr="$(mktemp)"

    start_ns="$(date +%s%N)"
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

    tps="$(grep -E 'eval time =.*runs' "$raw_stderr" \
            | grep -oE '[0-9]+\.[0-9]+ tokens per second' \
            | grep -oE '^[0-9]+\.[0-9]+' \
            | tail -n1 || true)"
    [[ -z "$tps" ]] && tps="null"

    rm -f "$raw_stderr"

    gen="$(printf '%s' "$gen" | sed -e '/^> EOF by user$/d' -e '/^>[[:space:]]*$/d')"
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

printf '%s\n' "$results" | jq '.' >"$OUT"
echo >&2
echo "llama_behavioral: wrote $n result(s) to $OUT" >&2
