#!/usr/bin/env bash
# scripts/model-codec-ci.sh
# Codec CI entry — under Stream A of /home/ahart/.claude/plans/replicated-hatching-stream.md.
#
# Stream A removed the pseudoinverse synthesis pipeline + restored the full
# ADR 0056 T9 kind vocabulary + universalized the CLI synthesize surface.
# Stream B (codec rebuild correctly via WeightTensorETL +
# IArchitectureTemplate::materialize_tensor) is pending Anthony's grounding.
#
# Until Stream B lands:
#   * LlamaWeightExtractor.ExtractAsync is a no-op stub → no model-codec
#     attestations emit. The interior-kind gates are RELAXED (warn, don't die).
#   * `synthesize substrate <recipe.json>` returns the documented Stream B
#     pending stub. The synthesize gates are SKIPPED.
#   * What this script DOES verify post-Stream-A: recipe ingest succeeds,
#     tokenizer entity + vocab tokens insert cleanly (the LINESTRING + FK
#     fixes in commits 24f9b60 + 97b5f2f), the idempotent re-ingest short-
#     circuits, the stats output displays without errors.
#
# When Stream B lands: re-add the interior-kind gates + the synthesize check.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
MODEL_DIR="${1:-${LAPLACE_TINYLLAMA_DIR:-/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0/snapshots/fe8a4ea1ffedaf415f4da2f062534de366a451e6}}"
GGUF_OUT="${LAPLACE_GGUF_OUT:-/tmp/tinyllama-substrate-ci.gguf}"
export LAPLACE_DB="${LAPLACE_DB:-Host=/var/run/postgresql;Username=laplace_admin;Database=laplace}"

CLI=(dotnet run --project "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-build --)
export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"

log() { echo "[model-codec-ci] $*"; }
warn() { echo "[model-codec-ci] WARN: $*" >&2; }
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

# Stream A defensive: clear any prior-job substrate state so the ingest runs
# against a known-clean substrate. Commit fcfa982 dropped the in-run nuke in
# favor of a single Unicode T0 seed at db-deploy; under Stream A's relaxed
# gate posture we need a clean slate per model-codec invocation so the
# idempotency check + tokenizer FK behavior are deterministic regardless of
# prior dotnet-test / regress mutation of substrate state.
#
# This nuke is local to the model-codec CI invocation. It does NOT affect
# dotnet-test / regress (those ran first per workflow dependencies).
log "db-nuke + reseed + clear-checkpoint (Stream A: ensure clean substrate AND fresh ingest checkpoint)"
(cd "$ROOT/app" && echo NUKE | dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- nuke) \
  || die "db-nuke failed"
(cd "$ROOT/app" && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- up) \
  || die "db-up after nuke failed"
(cd "$ROOT/app" && "${CLI[@]}" seed-unicode) \
  || die "seed-unicode after nuke failed"

# CRITICAL — wipe the IngestRunner checkpoint dir. The checkpoint at
# /tmp/laplace-ingest/<source-name>/checkpoint.bin is content-addressed by
# intent id and persists across CI runs. After db-nuke the DB has no
# entities, but the checkpoint claims certain intents were already applied —
# IngestRunner.ProcessOneIntentAsync checks `checkpoint.WasApplied(intent_id)`
# at line 199 and SKIPS those intents. Skipped intents = entities never
# re-inserted = subsequent intents' attestations fail
# `attestations_subject_id_fkey` because their subject entities don't exist.
#
# This was the bug behind the persistent CI failure on 72ac17c8276fd296d37c6c556251d0e3
# = BLAKE3(TinyLlama tokenizer.json) — the tokenizer intent was checkpointed
# from a prior CI run, db-nuke wiped its entity row, the model-codec re-run
# skipped re-emitting it, and the vocab batch's TOKEN_MAPS_TO attestations
# (subject = tokenizerEntityId) failed FK.
#
# rm -rf the per-decomposer checkpoint dir. /tmp/laplace-ingest itself may
# be laplace-runner-owned (created by the IngestRunner); use rm with -f to
# tolerate "no such file" on first run.
log "clear stale ingest checkpoint at /tmp/laplace-ingest"
rm -rf /tmp/laplace-ingest 2>/dev/null || true
mkdir -p /tmp/laplace-ingest

# --- ingest (idempotent: second run must short-circuit on a SUBSEQUENT pass,
# but Stream A's no-op extractor means the Q_PROJECTS-presence check that
# IngestModelAsync uses to detect "already ingested" will always return false
# (no Q_PROJECTS ever emit). So pass 2 will RE-RUN, not short-circuit. The
# idempotency-via-Q_PROJECTS-presence check itself is wrong-shape pending
# Stream B; for now we accept that pass 2 re-runs and merely verify both
# passes succeed without error.) ---
log "ingest model (pass 1)"
(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR")

log "ingest model (pass 2 — under Stream A: re-runs, must not crash)"
(cd "$ROOT/app" && "${CLI[@]}" ingest model "$MODEL_DIR")

# --- substrate row gates (RELAXED under Stream A; warn-don't-die) ---
log "stats"
(cd "$ROOT/app" && "${CLI[@]}" stats) | tee /tmp/laplace-stats-ci.txt

grep -q "model attestations" /tmp/laplace-stats-ci.txt \
  || die "stats output missing 'model attestations' line — stats command itself broken"

# Pre-Stream-B: every interior kind is expected to be zero (no-op extractor).
# Warn but don't die. When Stream B lands and WeightTensorETL emits real
# attestations, change `warn` back to `die` (and verify counts are >> 0).
for kind in EMBEDS Q_PROJECTS V_PROJECTS O_PROJECTS GATES UP_PROJECTS DOWN_PROJECTS OUTPUT_PROJECTS NORMALIZES K_PROJECTS; do
  line=$(grep "└ $kind" /tmp/laplace-stats-ci.txt || true)
  if [ -z "$line" ]; then
    warn "stats missing kind $kind in printed output (line not found)"
  elif echo "$line" | grep -qE ':\s+0\s*$'; then
    warn "kind $kind has 0 attestations (Stream A no-op extractor — Stream B will populate)"
  fi
done

# --- synthesize: substrate-mediated synthesis is the Stream B pending stub. ---
# The substrate subcommand returns Fail() with the documented message; the
# passthrough subcommand still works (model-dir direct transcode, no
# substrate). For Stream A CI we verify the passthrough path produces a
# valid GGUF since that path is unaffected by the codec revert.
log "synthesize passthrough → $GGUF_OUT (Stream A: substrate path stubbed pending Stream B)"
rm -f "$GGUF_OUT"
syn_out="$(cd "$ROOT/app" && "${CLI[@]}" synthesize passthrough "$MODEL_DIR" "$GGUF_OUT" 2>&1)"
echo "$syn_out"
echo "$syn_out" | grep -qiE 'passthrough.*complete|complete:' \
  || die "synthesize passthrough did not complete"

[ -f "$GGUF_OUT" ] || die "GGUF missing: $GGUF_OUT"
size=$(stat -c%s "$GGUF_OUT")
[ "$size" -gt 50000000 ] || die "GGUF too small ($size bytes)"

# Substrate-mediated synthesize: confirm Stream B stub returns documented error
log "synthesize substrate (Stream A: must return documented Stream B pending stub)"
sub_out="$(cd "$ROOT/app" && "${CLI[@]}" synthesize substrate "$MODEL_DIR/config.json" /tmp/stream-b-pending.gguf 2>&1)" || true
echo "$sub_out"
echo "$sub_out" | grep -qi "pending stream b" \
  || die "synthesize substrate did not return the documented Stream B pending stub"

log "PASS — model codec CI (Stream A: ingest + passthrough emit + substrate stub all OK)"
log "      Interior-kind gates RELAXED + synthesize-substrate gates SKIPPED pending Stream B."
log "      See /home/ahart/.claude/plans/replicated-hatching-stream.md."
