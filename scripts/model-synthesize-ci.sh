#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

newest_weighted_snapshot() {
  local family="$1" root="${LAPLACE_MODEL_HUB:-/vault/models}" snapshot
  [[ -d "$root/$family/snapshots" ]] || return 1
  while IFS= read -r snapshot; do
    [[ -f "$snapshot/config.json" && -f "$snapshot/tokenizer.json" ]] || continue
    compgen -G "$snapshot/*.safetensors" >/dev/null || continue
    printf '%s\n' "$snapshot"
    return 0
  done < <(find "$root/$family/snapshots" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' 2>/dev/null \
      | sort -nr | cut -d' ' -f2-)
  return 1
}

MODEL_DIR="${1:-${LAPLACE_MODEL_PROOF_DIR:-${LAPLACE_QWEN25_CODER_DIR:-${LAPLACE_TINYLLAMA_DIR:-}}}}"
if [[ -z "$MODEL_DIR" ]]; then
  MODEL_DIR="$(newest_weighted_snapshot 'models--Qwen--Qwen2.5-Coder-3B-Instruct' || true)"
fi
if [[ -z "$MODEL_DIR" ]]; then
  MODEL_DIR="$(newest_weighted_snapshot 'models--TinyLlama--TinyLlama-1.1B-Chat-v1.0' || true)"
fi
[[ -n "$MODEL_DIR" ]] || {
  echo "[substrate-ci] ERROR: no weighted proof model resolved; pass a model dir or set LAPLACE_MODEL_PROOF_DIR/LAPLACE_QWEN25_CODER_DIR/LAPLACE_TINYLLAMA_DIR" >&2
  exit 2
}

model_slug="$(basename "$(dirname "$(dirname "$MODEL_DIR")")" 2>/dev/null || basename "$MODEL_DIR")"
GGUF_OUT="${LAPLACE_GGUF_OUT:-${TMPDIR:-/tmp}/${model_slug}-substrate-ci.gguf}"
REPORT_OUT="${LAPLACE_MODEL_BEHAVIOR_REPORT:-${GGUF_OUT%.gguf}.behavior.json}"
export LAPLACE_DB="${LAPLACE_DB:-Host=/var/run/postgresql;Username=laplace_admin;Database=laplace}"

CLI=(dotnet run --project "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-build --)
export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"

log() { echo "[substrate-ci] $*"; }
die() { echo "[substrate-ci] ERROR: $*" >&2; exit 1; }

for f in "$MODEL_DIR/config.json" "$MODEL_DIR/tokenizer.json"; do
  [ -e "$f" ] || die "missing: $f"
done
ls "$MODEL_DIR"/*.safetensors >/dev/null 2>&1 || die "no *.safetensors under $MODEL_DIR"

psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc "SELECT 1" >/dev/null \
  || die "laplace DB unreachable (just db-up)"

psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc \
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

log "proof model: $MODEL_DIR"
log "migrations up + ingest unicode (idempotent; consensus folds, layer-0 marker set)"
(cd "$ROOT/app" && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- up) \
  || die "db-up failed"
(cd "$ROOT/app" && "${CLI[@]}" ingest unicode) \
  || die "ingest unicode failed"

log "deposit safetensors (pass 1)"
(cd "$ROOT/app" && "${CLI[@]}" ingest safetensors "$MODEL_DIR") \
  || die "safetensor deposition pass 1 failed"

log "deposit safetensors (pass 2 — must short-circuit via the re-ingest guard)"
pass2_out="$(cd "$ROOT/app" && "${CLI[@]}" ingest safetensors "$MODEL_DIR" 2>&1)"
echo "$pass2_out"
echo "$pass2_out" | grep -qi "already ingested" \
  || die "pass 2 did not short-circuit — idempotency broken"

log "evidence/consensus gates"
check_type_evidence() {
  local rel_type="$1" min="$2"
  local count
  count=$(psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc \
    "SELECT ops.evidence_count(p_type => laplace.relation_type_id('$rel_type'))")
  [ "${count:-0}" -ge "$min" ] || die "type $rel_type has $count evidence rows (need >= $min) — ingest broken"
  log "  $rel_type: $count evidence rows OK"
}
check_type_evidence SIMILAR_TO      1000
check_type_evidence ATTENDS         1000
check_type_evidence OV_RELATES      1000
check_type_evidence COMPLETES_TO    1000

cn=$(psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc "SELECT count(*) FROM laplace.consensus")
[ "${cn:-0}" -gt 0 ] || die "consensus empty after ingest (accumulate-at-ingest produced no rows)"
log "  consensus: $cn rows OK"

log "SQL model-plane readback"
probe_subject="$(psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc \
  "SELECT encode(subject_id,'hex') FROM laplace.consensus WHERE type_id=laplace.relation_type_id('ATTENDS') ORDER BY witness_count DESC, subject_id LIMIT 1")"
[[ "$probe_subject" =~ ^[0-9a-fA-F]{32}$ ]] || die "no ATTENDS subject available for SQL model-plane proof"
probe_rows="$(psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc \
  "SELECT count(*) FROM generation.adjudicated_row(decode('$probe_subject','hex'), 8, 'ATTENDS')")"
[ "${probe_rows:-0}" -gt 0 ] || die "generation.adjudicated_row returned no SQL-visible model evidence"
log "  generation.adjudicated_row: $probe_rows row(s) for $probe_subject"

log "synthesize substrate → $GGUF_OUT"
rm -f "$GGUF_OUT" "$REPORT_OUT"
syn_out="$(cd "$ROOT/app" && "${CLI[@]}" synthesize substrate "$MODEL_DIR/config.json" "$GGUF_OUT" 2>&1)"
echo "$syn_out"
echo "$syn_out" | grep -qiE 'synthesis complete' \
  || die "synthesize substrate did not complete"

[ -f "$GGUF_OUT" ] || die "GGUF missing: $GGUF_OUT"
size=$(stat -c%s "$GGUF_OUT")
[ "$size" -gt 50000000 ] || die "GGUF too small ($size bytes) — synthesis produced empty/trivial output"
log "GGUF: $GGUF_OUT ($((size / 1048576)) MB)"

LLAMA_BIN="${LAPLACE_LLAMA_BIN:-}"
if [[ -z "$LLAMA_BIN" ]]; then
  for candidate in \
    /data/archive/llama-workspace/llama.cpp/build/bin/llama-completion \
    /data/archive/llama-workspace/llama.cpp/build-cpu/bin/llama-completion \
    "$(command -v llama-completion 2>/dev/null || true)"; do
    [[ -n "$candidate" && -x "$candidate" ]] || continue
    if "$candidate" --help >/dev/null 2>&1; then
      LLAMA_BIN="$candidate"
      break
    fi
  done
fi
[[ -n "$LLAMA_BIN" ]] || die "no runnable llama.cpp llama-completion binary; external-runtime proof is mandatory"

log "external runtime behavioral proof via llama.cpp: $LLAMA_BIN"
python3 "$ROOT/scripts/verify-model-behavioral.py" \
  --model "$GGUF_OUT" \
  --runner llama \
  --llama "$LLAMA_BIN" \
  --db "host=/var/run/postgresql user=laplace_admin dbname=laplace" \
  --min-pass "${LAPLACE_MODEL_MIN_PASS:-0.5}" \
  --report "$REPORT_OUT" \
  || die "export loaded but failed semantic behavioral proof; see $REPORT_OUT"

[ -s "$REPORT_OUT" ] || die "behavioral verifier returned success without a report"
grep -q '"ok": true' "$REPORT_OUT" \
  || die "behavioral report does not record ok=true: $REPORT_OUT"

log "PASS — substrate pipeline: model ingest → SQL model evidence → deterministic export → llama.cpp generation → semantic behavior gate"
