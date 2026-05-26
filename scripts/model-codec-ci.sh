#!/usr/bin/env bash
# scripts/model-codec-ci.sh
# Chunk 8 (partial): CI/local entry — idempotent ingest, substrate synthesize, gates.
# Chat / llama.cpp verification is a separate milestone.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MODEL_DIR="${1:-${LAPLACE_TINYLLAMA_DIR:-/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0/snapshots/fe8a4ea1ffedaf415f4da2f062534de366a451e6}}"
GGUF_OUT="${LAPLACE_GGUF_OUT:-/tmp/tinyllama-substrate-ci.gguf}"
export LAPLACE_DB="${LAPLACE_DB:-Host=/var/run/postgresql;Username=laplace_admin;Database=laplace}"

CLI=(dotnet run --project "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-build --)
export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"

log() { echo "[model-codec-ci] $*"; }
die() { echo "[model-codec-ci] ERROR: $*" >&2; exit 1; }

for f in "$MODEL_DIR/config.json" "$MODEL_DIR/tokenizer.json" "$MODEL_DIR/model.safetensors"; do
  [ -e "$f" ] || die "missing: $f"
done

psql -d laplace -U laplace_admin -tAc "SELECT 1" >/dev/null \
  || die "laplace DB unreachable (just db-up / integration db-deploy)"

psql -d laplace -U laplace_admin -tAc \
  "SELECT 1 FROM pg_extension WHERE extname='laplace_substrate'" | grep -q 1 \
  || die "laplace_substrate not installed"

[ -f "$ROOT/build/engine/synthesis/liblaplace_synthesis.so" ] \
  || die "engine not built (just build)"

if [ ! -f "$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll" ]; then
  log "building Laplace.Cli"
  (cd "$ROOT/app" && dotnet build Laplace.Cli/Laplace.Cli.csproj -c Release -v q)
fi

# --- ingest (idempotent: second run must short-circuit) ---
log "ingest model (pass 1)"
(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR")

log "ingest model (pass 2 — must report already ingested)"
out="$(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR" 2>&1)" || true
echo "$out"
echo "$out" | grep -q "already ingested" \
  || die "second ingest did not short-circuit (not idempotent)"

# --- substrate row gates (interior kinds must exist after ingest) ---
log "stats"
(cd "$ROOT/app" && "${CLI[@]}" stats) | tee /tmp/laplace-stats-ci.txt

grep -q "model attestations" /tmp/laplace-stats-ci.txt || die "stats missing model block"
for kind in EMBEDS Q_PROJECTS V_PROJECTS O_PROJECTS GATES UP_PROJECTS DOWN_PROJECTS OUTPUT_PROJECTS; do
  line=$(grep "└ $kind" /tmp/laplace-stats-ci.txt || true)
  [ -n "$line" ] || die "stats missing kind $kind"
  echo "$line" | grep -qE ':\s+[1-9][0-9,]*\s*$' \
    || die "zero attestations for $kind — interior ingest did not land"
done

# --- synthesize (substrate → GGUF) ---
rm -f "$GGUF_OUT"
log "synthesize tinyllama → $GGUF_OUT"
syn_out="$(cd "$ROOT/app" && "${CLI[@]}" synthesize tinyllama "$GGUF_OUT" 2>&1)"
echo "$syn_out"
echo "$syn_out" | grep -qi "synthesis complete" || die "synthesize did not complete"

[ -f "$GGUF_OUT" ] || die "GGUF missing: $GGUF_OUT"
size=$(stat -c%s "$GGUF_OUT")
[ "$size" -gt 50000000 ] || die "GGUF too small ($size bytes)"

echo "$syn_out" | grep -q "embed_tokens:" || die "synthesize missing embed_tokens summary"
echo "$syn_out" | grep -E "embed_tokens: [1-9][0-9]*/[0-9]+ non-zero" \
  || die "embed_tokens has zero non-zero cells from substrate"

log "PASS — model codec CI (ingest idempotent, interior kinds present, GGUF emitted)"
