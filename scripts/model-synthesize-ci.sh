#!/usr/bin/env bash
# scripts/model-synthesize-ci.sh
# Substrate model CI: ingest a model → evidence + consensus → synthesize from substrate.
#
# Pipeline under test:
#   1. Migrations up + seed T0 (idempotent — NO nuke: re-ingest converges by
#      content-addressing + ON CONFLICT; per-source eviction is the only reset)
#   2. Ingest the model — the cell ETL: every non-zero weight cell = one
#      adjudicated match under its tensor-role kind (EMBEDS … OUTPUT_PROJECTS),
#      positions aggregating as witnesses — + S³ placements
#   3. Idempotent re-ingest must short-circuit (re-ingest guard)
#   4. Verify evidence + consensus row gates for the tensor-role kinds
#   5. Synthesize from substrate (re-export = fill the mold) → GGUF
#   6. Verify output GGUF is valid and non-trivial in size

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Model dir by CONVENTION only: $1 or $LAPLACE_TINYLLAMA_DIR — never a hardcoded
# snapshot SHA.
MODEL_DIR="${1:-${LAPLACE_TINYLLAMA_DIR:-}}"
[ -n "$MODEL_DIR" ] || { echo "[substrate-ci] ERROR: pass a model dir or set LAPLACE_TINYLLAMA_DIR" >&2; exit 2; }
GGUF_OUT="${LAPLACE_GGUF_OUT:-${TMPDIR:-/tmp}/tinyllama-substrate-ci.gguf}"
export LAPLACE_DB="${LAPLACE_DB:-Host=/var/run/postgresql;Username=laplace_admin;Database=laplace}"

CLI=(dotnet run --project "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-build --)
export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"

log() { echo "[substrate-ci] $*"; }
die() { echo "[substrate-ci] ERROR: $*" >&2; exit 1; }

# --- prereqs ---
for f in "$MODEL_DIR/config.json" "$MODEL_DIR/tokenizer.json"; do
  [ -e "$f" ] || die "missing: $f"
done
ls "$MODEL_DIR"/*.safetensors >/dev/null 2>&1 || die "no *.safetensors under $MODEL_DIR"

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

# --- migrations + T0 seed (idempotent; never nuke-to-reingest) ---
log "migrations up + ingest unicode (idempotent; consensus folds, layer-0 marker set)"
(cd "$ROOT/app" && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- up) \
  || die "db-up failed"
(cd "$ROOT/app" && "${CLI[@]}" ingest unicode) \
  || die "ingest unicode failed"

# --- ingest ---
log "ingest model (pass 1)"
(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR") \
  || die "model ingest pass 1 failed"

log "ingest model (pass 2 — must short-circuit via the re-ingest guard)"
pass2_out="$(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR" 2>&1)"
echo "$pass2_out"
echo "$pass2_out" | grep -qi "already ingested" \
  || die "pass 2 did not short-circuit — idempotency broken"

# --- evidence + consensus gates (the LIVE tensor-role arenas) ---
# ATTENDS / OV_RELATES / COMPLETES_TO are the query-time bilinear reads
# composed across these arenas — never ingest-written, never gated here.
log "evidence/consensus gates"
# Substrate operating surface only — kind names resolve in-DB (kind_id),
# counts come from evidence_count; no hand-built hash, no hex round-trip.
check_kind_evidence() {
  local kind="$1" min="$2"
  local count
  count=$(psql -d laplace -U laplace_admin -tAc \
    "SELECT laplace.evidence_count(p_type => laplace.relation_type_id('$kind'))")
  [ "${count:-0}" -ge "$min" ] || die "kind $kind has $count evidence rows (need >= $min) — ingest broken"
  log "  $kind: $count evidence rows OK"
}
check_kind_evidence EMBEDS          1000
check_kind_evidence Q_PROJECTS      1000
check_kind_evidence K_PROJECTS      1000
check_kind_evidence V_PROJECTS      1000
check_kind_evidence O_PROJECTS      1000
check_kind_evidence GATES           1000
check_kind_evidence UP_PROJECTS     1000
check_kind_evidence DOWN_PROJECTS   1000
check_kind_evidence NORMALIZES      1000
check_kind_evidence OUTPUT_PROJECTS 1000

cn=$(psql -d laplace -U laplace_admin -tAc "SELECT count(*) FROM laplace.consensus")
[ "${cn:-0}" -gt 0 ] || die "consensus empty after ingest (accumulate-at-ingest produced no rows)"
log "  consensus: $cn rows OK"

# --- substrate synthesis (re-export = fill the mold) ---
log "synthesize substrate → $GGUF_OUT"
rm -f "$GGUF_OUT"
syn_out="$(cd "$ROOT/app" && "${CLI[@]}" synthesize substrate "$MODEL_DIR/config.json" "$GGUF_OUT" 2>&1)"
echo "$syn_out"
echo "$syn_out" | grep -qiE 'synthesis complete' \
  || die "synthesize substrate did not complete"

[ -f "$GGUF_OUT" ] || die "GGUF missing: $GGUF_OUT"
size=$(stat -c%s "$GGUF_OUT")
[ "$size" -gt 50000000 ] || die "GGUF too small ($size bytes) — synthesis produced empty/trivial output"

log "GGUF: $GGUF_OUT ($((size / 1048576)) MB)"
log "PASS — substrate pipeline: ingest → evidence/consensus → re-export GGUF"
