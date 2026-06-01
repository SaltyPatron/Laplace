#!/usr/bin/env bash
# Run a model ingest with a clean, normal environment. Exists because the agent
# shell's TMPDIR is a >108-char path that breaks the .NET diagnostics socket and
# wedges the run; a normal TMPDIR (=/tmp) avoids it. Usage:
#   scripts/run_model_ingest.sh [model-dir]
set -o pipefail   # NOT -u: sourcing oneAPI setvars.sh trips unset-var and dies silently
cd "$(dirname "$0")/.."
ROOT="$PWD"
exec > /tmp/laplace-ingest-run.log 2>&1   # known log path regardless of launcher

export TMPDIR=/tmp
export DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
source /opt/intel/oneapi/setvars.sh --force >/dev/null 2>&1 || true
export LAPLACE_DB="${LAPLACE_DB:-Host=/var/run/postgresql;Username=laplace_admin;Database=laplace}"
export LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}"
export LAPLACE_PERFCACHE_BIN="$ROOT/build/engine/core/perfcache/laplace_t0_perfcache.bin"
# bench: count circuit pairs, skip DB emit (no disk blowup while calibrating the floor)
export LAPLACE_QK_BENCH="${LAPLACE_QK_BENCH:-1}"
export LAPLACE_OVFFN_FLOOR="${LAPLACE_OVFFN_FLOOR:-5e-4}"

MODEL="${1:-/vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0/snapshots/fe8a4ea1ffedaf415f4da2f062534de366a451e6}"
rm -rf /tmp/laplace-ingest

echo "[run] TMPDIR=$TMPDIR  model=$MODEL  bench=$LAPLACE_QK_BENCH floor=$LAPLACE_OVFFN_FLOOR"
exec dotnet run --project app/Laplace.Cli/Laplace.Cli.csproj -c Release --no-build -- ingest model "$MODEL"
