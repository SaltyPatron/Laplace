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

resolve_corroboration_snapshot() {
  local primary="$1" candidate family
  if [[ -n "${LAPLACE_MODEL_CORROBORATION_DIR:-}" ]]; then
    printf '%s\n' "$LAPLACE_MODEL_CORROBORATION_DIR"
    return 0
  fi
  for family in \
    models--Qwen--Qwen2.5-Coder-3B-Instruct \
    models--TinyLlama--TinyLlama-1.1B-Chat-v1.0; do
    candidate="$(newest_weighted_snapshot "$family" || true)"
    [[ -n "$candidate" && ! "$primary" -ef "$candidate" ]] || continue
    printf '%s\n' "$candidate"
    return 0
  done
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

# A fresh database has no model-kind claims for a single checkpoint to
# corroborate. The existing joint-analysis owner requires two independent
# complete checkpoints; it verifies their full content identities itself.
CORROBORATION_MODEL_DIR="$(resolve_corroboration_snapshot "$MODEL_DIR")" || {
  echo "[substrate-ci] ERROR: no second weighted proof model resolved; set LAPLACE_MODEL_CORROBORATION_DIR" >&2
  exit 2
}
[[ ! "$MODEL_DIR" -ef "$CORROBORATION_MODEL_DIR" ]] || {
  echo "[substrate-ci] ERROR: corroboration requires a second model directory, not the selected snapshot again" >&2
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

for snapshot in "$MODEL_DIR" "$CORROBORATION_MODEL_DIR"; do
  for f in "$snapshot/config.json" "$snapshot/tokenizer.json"; do
    [ -e "$f" ] || die "missing: $f"
  done
  ls "$snapshot"/*.safetensors >/dev/null 2>&1 || die "no *.safetensors under $snapshot"
done

# Inspect the exact selected inputs before Unicode, corpus admission, or model
# work. Sampled metadata is only an early rejection gate; all full readers and
# the complete evidence/export/behavioral proof below still have to succeed.
preflight_code_corpora=0
[[ "${LAPLACE_MODEL_PROOF_CODE_CORPORA:-1}" != 1 ]] || preflight_code_corpora=1
if ! preflight_out="$(python3 -I "$ROOT/scripts/check-model-proof-prerequisites.py" \
    --repo "$ROOT" --model-dir "$MODEL_DIR" --second-model-dir "$CORROBORATION_MODEL_DIR" \
    --require-proof-ready --code-corpora "$preflight_code_corpora")"; then
  echo "$preflight_out"
  die "preliminary model-proof prerequisites failed before admission"
fi
echo "$preflight_out"
LLAMA_BIN="$(python3 -c '
import json, sys
prefix = "MODEL_PROOF_PREFLIGHT "
lines = [line[len(prefix):] for line in sys.stdin.read().splitlines() if line.startswith(prefix)]
if len(lines) != 1:
    raise SystemExit("expected one preliminary prerequisite report")
report = json.loads(lines[0])
if not report.get("preliminary_prerequisites_passed") or not report.get("selected_llama"):
    raise SystemExit("preliminary prerequisites did not select a runnable llama executable")
print(report["selected_llama"])
' <<< "$preflight_out")" || die "invalid preliminary model-proof prerequisite report"

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
log "corroborating model: $CORROBORATION_MODEL_DIR"
log "migrations up + ingest unicode (idempotent; consensus folds, layer-0 marker set)"
(cd "$ROOT/app" && dotnet run --project Laplace.Migrations/Laplace.Migrations.csproj -- up) \
  || die "db-up failed"
(cd "$ROOT/app" && "${CLI[@]}" ingest unicode) \
  || die "ingest unicode failed"

if [[ "${LAPLACE_MODEL_PROOF_CODE_CORPORA:-1}" == 1 ]]; then
  log "admit code estate: TinyCodes + Stack v2 through the grammar/AST ingest spine"
  bash "$ROOT/scripts/ingest-source.sh" tiny-codes \
    || die "TinyCodes ingest failed"
  bash "$ROOT/scripts/ingest-source.sh" stack \
    || die "Stack v2 ingest failed"

  for source_name in TinyCodesDecomposer StackDecomposer; do
    evidence="$(psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc \
      "SELECT ops.evidence_count(NULL, laplace.source_id('$source_name'))")"
    [ "${evidence:-0}" -gt 0 ] || die "$source_name has no retained evidence after ingest"
    log "  $source_name: $evidence evidence row(s)"
  done

  ast_rows="$(psql -h /var/run/postgresql -d laplace -U laplace_admin -tAc "
    SELECT count(*)
    FROM laplace.attestations a
    WHERE a.source_id IN (laplace.source_id('TinyCodesDecomposer'), laplace.source_id('StackDecomposer'))
      AND a.type_id IN (
        laplace.relation_type_id('DEFINES'),
        laplace.relation_type_id('CALLS'),
        laplace.relation_type_id('REFERENCES'))")"
  [ "${ast_rows:-0}" -gt 0 ] || die "code corpora admitted content but no Tree-sitter structural testimony (DEFINES/CALLS/REFERENCES)"
  log "  Tree-sitter structural testimony: $ast_rows row(s)"
fi

log "deposit safetensors (pass 1)"
(cd "$ROOT/app" && "${CLI[@]}" ingest safetensors "$MODEL_DIR") \
  || die "safetensor deposition pass 1 failed"

log "deposit corroborating safetensors (pass 1)"
(cd "$ROOT/app" && "${CLI[@]}" ingest safetensors "$CORROBORATION_MODEL_DIR") \
  || die "corroborating safetensor deposition pass 1 failed"

log "deposit corroborating safetensors (pass 2 — must short-circuit via the re-ingest guard)"
corroboration_pass2_out="$(cd "$ROOT/app" && "${CLI[@]}" ingest safetensors "$CORROBORATION_MODEL_DIR" 2>&1)"
echo "$corroboration_pass2_out"
echo "$corroboration_pass2_out" | grep -q '^Safetensor snapshot already deposited — source ' \
  || die "corroborating model pass 2 did not short-circuit — idempotency broken"

log "corroborate retained graph nominations through two independent model checkpoints"
(cd "$ROOT/app" && "${CLI[@]}" ingest model-corroborate "$MODEL_DIR" "$CORROBORATION_MODEL_DIR") \
  || die "joint model corroboration failed"

log "deposit safetensors (pass 2 — must short-circuit via the re-ingest guard)"
pass2_out="$(cd "$ROOT/app" && "${CLI[@]}" ingest safetensors "$MODEL_DIR" 2>&1)"
echo "$pass2_out"
# IngestSafetensorSnapshotAsync emits this diagnostic only after finding the
# selected model source's retained completion evidence. Require that exact
# no-op branch; unrelated "already" output must not qualify a repeated ingest.
echo "$pass2_out" | grep -q '^Safetensor snapshot already deposited — source ' \
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

log "PASS — code corpus AST evidence + model ingest + SQL model evidence + deterministic export + llama.cpp generation + semantic behavior gate"
