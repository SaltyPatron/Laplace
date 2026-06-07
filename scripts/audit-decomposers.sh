#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LOG="${AUDIT_LOG:-/tmp/laplace-decomposer-audit.log}"
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
      p_type => laplace.canonical_id('substrate/kind/HasLayerCompleted/${n}/v1')) > 0;
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
  section "VAULT"
  local ok=0
  for f in \
    "/vault/Data/Unicode/Public/17.0.0/uca/allkeys.txt" \
    "/vault/Data/Unicode/Public/17.0.0/ucdxml/ucd.nounihan.flat.zip" \
    "/vault/Data/ISO639/iso-639-3.tab" \
    "/vault/Data/Wordnet/WordNet-3.0/dict/data.noun" \
    "/vault/Data/Wordnet/WordNet-3.0/dict/index.sense" \
    "/vault/Data/omw/wns" \
    "/vault/Data/UD-Treebanks/ud-treebanks-v2.17" \
    "/vault/Data/Tatoeba/sentences.csv" \
    "/vault/Data/Atomic2020/train.tsv" \
    "/vault/Data/ConceptNet/assertions.csv" \
    "/vault/Data/Wiktionary" \
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
declare -A LAYER=( [iso639]=1 [wordnet]=2 [omw]=3 [ud]=4
                   [tatoeba]=4 [atomic2020]=4 [conceptnet]=4 [wiktionary]=4 )
LADDER=(iso639 wordnet omw ud)
[[ $FULL -eq 1 ]] && LADDER+=(tatoeba atomic2020 conceptnet wiktionary)
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
    run_just "ingest model" just ingest model "$TINYLLAMA" || AUDIT_FAIL=1
    if [[ -x "$ROOT/scripts/model-synthesize.sh" ]]; then
      run_just "model-synthesize" "$ROOT/scripts/model-synthesize.sh" "$TINYLLAMA" || AUDIT_FAIL=1
    fi
  else
    skip "ingest model (TINYLLAMA_DIR missing)"
  fi
fi

section "DOTNET TESTS (no full PG unicode integration unless configured)"
(cd "$ROOT/app" && dotnet test Laplace.Decomposers.Abstractions.Tests -c Release --no-restore 2>/dev/null || dotnet test Laplace.Decomposers.Abstractions.Tests -c Release) || AUDIT_FAIL=1

section "AUDIT END exit=$AUDIT_FAIL"
exit "$AUDIT_FAIL"
