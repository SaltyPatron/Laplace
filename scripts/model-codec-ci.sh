#!/usr/bin/env bash
# scripts/model-codec-ci.sh
# Substrate synthesis CI: ingest a model → attestations → synthesize from substrate.
#
# Pipeline under test:
#   1. db-nuke + reseed T0 (clean slate)
#   2. Ingest TinyLlama → substrate attestations (EMBEDS, Q_PROJECTS, V/O/G/U/D, NORMALIZES)
#   3. Idempotent re-ingest must short-circuit (Q_PROJECTS presence = already done)
#   4. Verify attestation counts above noise floor for every kind
#   5. Synthesize from substrate: eigenmaps → spectral basis → materialize tensors → GGUF
#   6. Verify output GGUF is valid and non-trivial in size
#
# The passthrough path (straight model-file transcode) is a diagnostic tool,
# not the product. It is not tested here.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MODEL_DIR="${1:-${LAPLACE_TINYLLAMA_DIR:-/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0/snapshots/fe8a4ea1ffedaf415f4da2f062534de366a451e6}}"
GGUF_OUT="${LAPLACE_GGUF_OUT:-/tmp/tinyllama-substrate-ci.gguf}"
export LAPLACE_DB="${LAPLACE_DB:-Host=/var/run/postgresql;Username=laplace_admin;Database=laplace}"

CLI=(dotnet run --project "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-build --)
export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"

log() { echo "[substrate-ci] $*"; }
die() { echo "[substrate-ci] ERROR: $*" >&2; exit 1; }

# --- prereqs ---
for f in "$MODEL_DIR/config.json" "$MODEL_DIR/tokenizer.json" "$MODEL_DIR/model.safetensors"; do
  [ -e "$f" ] || die "missing: $f"
done

psql -d laplace -U laplace_admin -tAc "SELECT 1" >/dev/null \
  || die "laplace DB unreachable (just db-up)"

psql -d laplace -U laplace_admin -tAc \
  "SELECT 1 FROM pg_extension WHERE extname='laplace_substrate'" | grep -q 1 \
  || die "laplace_substrate not installed"

[ -f "$ROOT/build/engine/synthesis/liblaplace_synthesis.so" ] \
  || die "engine not built (just build)"
[ -f "$ROOT/build/engine/dynamics/liblaplace_dynamics.so" ] \
  || die "dynamics engine not built (just build)"

if [ ! -f "$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll" ]; then
  log "building Laplace.Cli"
  (cd "$ROOT/app" && dotnet build Laplace.Cli/Laplace.Cli.csproj -c Release -v q)
fi

# --- clean slate ---
log "db-nuke + reseed"
(cd "$ROOT/app" && echo NUKE | dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- nuke) \
  || die "db-nuke failed"
(cd "$ROOT/app" && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- up) \
  || die "db-up after nuke failed"
(cd "$ROOT/app" && "${CLI[@]}" seed-unicode) \
  || die "seed-unicode failed"

# Clear stale ingest checkpoint — after db-nuke the DB has no entities but the
# checkpoint claims intents were applied. Without clearing it, IngestRunner
# skips re-emitting those intents and subsequent attestation FKs fail.
log "clear stale ingest checkpoint"
rm -rf /tmp/laplace-ingest 2>/dev/null || true
mkdir -p /tmp/laplace-ingest

# --- ingest ---
log "ingest model (pass 1)"
(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR") \
  || die "model ingest pass 1 failed"

log "ingest model (pass 2 — must short-circuit via Q_PROJECTS presence check)"
pass2_out="$(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR" 2>&1)"
echo "$pass2_out"
echo "$pass2_out" | grep -qi "already ingested" \
  || die "pass 2 did not short-circuit — idempotency broken"

# --- attestation count gates ---
log "stats"
(cd "$ROOT/app" && "${CLI[@]}" stats) | tee /tmp/laplace-stats-ci.txt

grep -q "model attestations" /tmp/laplace-stats-ci.txt \
  || die "stats output missing 'model attestations' line"

check_kind_nonzero() {
  local kind="$1" min="${2:-1}"
  local line
  line=$(grep "└ $kind" /tmp/laplace-stats-ci.txt || true)
  [ -n "$line" ] || die "kind $kind missing from stats output"
  local count
  count=$(echo "$line" | grep -oE '[0-9,]+$' | tr -d ',')
  [ -n "$count" ] || die "could not parse count for $kind"
  [ "$count" -ge "$min" ] || die "kind $kind has $count attestations (need >= $min) — ingest broken"
  log "  $kind: $count attestations OK"
}

# EMBEDS, V/O/G/U/D, OUTPUT_PROJECTS: expect ~32K rows (one per non-zero token)
check_kind_nonzero EMBEDS          10000
check_kind_nonzero Q_PROJECTS      100000
check_kind_nonzero V_PROJECTS      10000
check_kind_nonzero O_PROJECTS      10000
check_kind_nonzero GATES           10000
check_kind_nonzero UP_PROJECTS     10000
check_kind_nonzero DOWN_PROJECTS   10000
check_kind_nonzero OUTPUT_PROJECTS 10000
check_kind_nonzero NORMALIZES      1

# --- substrate synthesis ---
log "synthesize substrate → $GGUF_OUT"
rm -f "$GGUF_OUT"
syn_out="$(cd "$ROOT/app" && "${CLI[@]}" synthesize substrate "$MODEL_DIR/config.json" "$GGUF_OUT" 2>&1)"
echo "$syn_out"
echo "$syn_out" | grep -qiE 'synthesis complete' \
  || die "synthesize substrate did not complete"

[ -f "$GGUF_OUT" ] || die "GGUF missing: $GGUF_OUT"
size=$(stat -c%s "$GGUF_OUT")
[ "$size" -gt 50000000 ] || die "GGUF too small ($size bytes) — synthesis produced empty/trivial output"

# Verify spectral basis was computed (eigenmaps ran, not fallback)
echo "$syn_out" | grep -qi "spectral token basis" \
  || die "eigenmaps did not run — TokenBasis still null"
echo "$syn_out" | grep -qi "procrustes residual" \
  || die "Procrustes alignment did not run"

log "GGUF: $GGUF_OUT ($((size / 1048576)) MB)"
log "PASS — substrate synthesis pipeline: ingest → attestations → eigenmaps → GGUF"
