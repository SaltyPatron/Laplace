#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOG="${AUDIT_LOG:-/tmp/laplace-decomposer-audit.log}"
DATA_ROOT="${LAPLACE_DATA_ROOT:-/vault/Data}"
PSQL=(psql -h /var/run/postgresql -U laplace_admin -d laplace -v ON_ERROR_STOP=1)
TINYLLAMA="${LAPLACE_TINYLLAMA_DIR:-${TINYLLAMA_DIR:-}}"

FROM=""
FRESH=0
FULL=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fresh) FRESH=1; shift ;;
    --full) FULL=1; shift ;;
    --from) FROM="${2:-}"; shift 2 ;;
    -h|--help)
      echo "Usage: $0 [--full] [--fresh] [--from <source>]"
      exit 0
      ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

exec > >(tee -a "$LOG") 2>&1

section() { echo ""; echo "======== $* ========"; echo "$(date -u +%Y-%m-%dT%H:%M:%SZ)"; }
pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; }
skip() { echo "SKIP  $*"; }

counts() {
  "${PSQL[@]}" -t -A -c \
    "SELECT metric || '=' || value::text FROM laplace.substrate_counts();"
}

layer_done() {
  local n="$1"
  "${PSQL[@]}" -t -A -c "
    SELECT laplace.evidence_count(
      p_type => laplace.canonical_id('substrate/type/HasLayerCompleted/${n}/v1')) > 0;
  " | tr -d '[:space:]'
}

run_just() {
  local label="$1"
  shift
  section "RUN $label"
  local t0=$SECONDS
  set +e
  (cd "$ROOT" && "$@")
  local ec=$?
  set -e
  local dt=$((SECONDS - t0))
  counts
  if [[ $ec -eq 0 ]]; then pass "$label (${dt}s)"; else fail "$label exit=$ec (${dt}s)"; fi
  return $ec
}

vault_check() {
  section "VAULT (LAPLACE_DATA_ROOT=$DATA_ROOT)"
  local ok=0
  for f in \
    "$DATA_ROOT/UCD/Public/UCD/latest/ucd/UnicodeData.txt" \
    "$DATA_ROOT/ISO639/iso-639-3.tab" \
    "$DATA_ROOT/Wordnet/WordNet-3.0/dict/data.noun" \
    "$DATA_ROOT/Wordnet/WordNet-3.0/dict/index.sense" \
    "$DATA_ROOT/OMW/wns" \
    "$DATA_ROOT/omw/wns" \
    "$DATA_ROOT/VerbNet" \
    "$DATA_ROOT/PropBank" \
    "$DATA_ROOT/FrameNet/framenet_v17" \
    "$DATA_ROOT/MapNet-0.1/mapping_frame_synsets.txt" \
    "$DATA_ROOT/MapNet/mapping_frame_synsets.txt" \
    "$DATA_ROOT/SemLink/semlink-master/instances/pb-vn2.json" \
    "$DATA_ROOT/SemLink/semlink-master/instances/vn-fn2.json" \
    "$DATA_ROOT/PredicateMatrix.v1.3/PredicateMatrix.v1.3.txt" \
    "$DATA_ROOT/CILI/ili-map-pwn30.tab" \
    "$DATA_ROOT/CILI/ili-map.ttl" \
    "$DATA_ROOT/ConceptNet/assertions.csv" \
    "$DATA_ROOT/Atomic2020/train.tsv" \
    "$DATA_ROOT/UD-Treebanks/ud-treebanks-v2.17" \
    "$DATA_ROOT/Wiktionary" \
    "$DATA_ROOT/Tatoeba/sentences.csv" \
    "$TINYLLAMA/config.json"
  do
    if [[ -e "$f" ]]; then echo "  ok  $f"; else echo "  MISSING  $f"; ok=1; fi
  done
  return $ok
}

section "AUDIT START log=$LOG"
cd "$ROOT"
just check-prereqs || true
vault_check || true
counts

if [[ $FRESH -eq 1 ]]; then
  run_just "db-fresh" just db-fresh || exit 1
elif [[ -z "$FROM" ]]; then
  run_just "seed-t0 (if needed)" bash -c '
    n=$('"${PSQL[@]}"' -t -A -c "SELECT count(*) FROM laplace.entities" | tr -d "[:space:]")
    if [[ "$n" -lt 1000000 ]]; then just seed-t0; else echo "T0 already present ($n entities)"; fi
  ' || true
fi

should_run() {
  local name="$1"
  [[ -z "$FROM" ]] && return 0
  [[ "$FROM" == "$name" ]] && FROM="" && return 0
  return 1
}

if should_run "unicode-ingest"; then
  if [[ "$(layer_done 0)" == "t" ]]; then
    skip "ingest unicode (layer 0 already marked)"
  else
    run_just "ingest unicode" just ingest unicode || { echo "FATAL: unicode ingest failed"; exit 1; }
  fi
fi

AUDIT_FAIL=0
declare -A LAYER=(
  [iso639]=1
  [wordnet]=2 [omw]=3 [verbnet]=4 [propbank]=5 [framenet]=6
  [mapnet]=7 [wordframenet]=8 [semlink]=9
  [conceptnet]=10 [atomic2020]=11 [ud]=12 [wiktionary]=13
  [tatoeba]=14 [opensubtitles]=15
)
KNOWLEDGE=(wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary)
LADDER=(iso639 "${KNOWLEDGE[@]}")
[[ $FULL -eq 1 ]] && LADDER+=(tatoeba opensubtitles)
for src in "${LADDER[@]}"; do
  should_run "$src" || continue
  prev=$(( ${LAYER[$src]} - 1 ))
  if [[ "$(layer_done "$prev")" != "t" ]]; then
    fail "ingest $src blocked: HasLayerCompleted/$prev missing (run the lower ladder layers first)"
    AUDIT_FAIL=1
    continue
  fi
  run_just "ingest $src" just ingest "$src" || AUDIT_FAIL=1
done

if printf '%s\n' "${LADDER[@]}" | grep -qx wordnet || [[ "$(layer_done 2)" == "t" ]]; then
  section "CONTENT CHECK (seed = content + attestations, not attestations alone)"
  wn_content=$("${PSQL[@]}" -t -A -c \
    "SELECT laplace.content_count(laplace.source_id('WordNetDecomposer'));" | tr -d '[:space:]')
  echo "WordNet CONTENT physicalities = ${wn_content:-0}"
  if [[ "${wn_content:-0}" -gt 0 ]]; then
    pass "WordNet emits CONTENT physicalities (${wn_content}) — content path live"
  else
    fail "WordNet emitted no CONTENT physicalities — regressed to attestations-only / string-keyed entities"
    AUDIT_FAIL=1
  fi
fi

if should_run "model"; then
  if [[ -d "$TINYLLAMA" ]]; then
    run_just "deposit safetensors" just ingest-tinyllama || AUDIT_FAIL=1
    if [[ -x "$ROOT/scripts/model-synthesize-ci.sh" ]]; then
      run_just "model-synthesize" "$ROOT/scripts/model-synthesize-ci.sh" "$TINYLLAMA" || AUDIT_FAIL=1
    fi
  else
    skip "deposit safetensors (LAPLACE_TINYLLAMA_DIR missing)"
  fi
fi

section "DOTNET TESTS (no full PG unicode integration unless configured)"
(cd "$ROOT/app" && dotnet test Laplace.Decomposers.Abstractions.Tests -c Release --no-restore 2>/dev/null || dotnet test Laplace.Decomposers.Abstractions.Tests -c Release) || AUDIT_FAIL=1

section "AUDIT END exit=$AUDIT_FAIL"
exit "$AUDIT_FAIL"
