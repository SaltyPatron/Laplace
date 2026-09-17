#!/usr/bin/env bash
set -euo pipefail

ROOT="${GITHUB_WORKSPACE:?GITHUB_WORKSPACE is required}"
cd "$ROOT"

: "${LAPLACE_BENCH_RECEIPT:?LAPLACE_BENCH_RECEIPT is required}"
: "${LAPLACE_BENCH_SHA:?LAPLACE_BENCH_SHA is required}"
: "${SUITE:?SUITE is required}"
: "${REPEATS:?REPEATS is required}"
: "${MOBY_PATH:?MOBY_PATH is required}"

record_accelerator_after() {
  local out="$LAPLACE_BENCH_RECEIPT"
  if command -v nvidia-smi >/dev/null 2>&1; then
    nvidia-smi -q > "$out/nvidia-smi-after.txt" 2>&1 || true
    nvidia-smi --query-gpu=name,uuid,pstate,power.draw,power.limit,memory.used,utilization.gpu,utilization.memory \
      --format=csv,noheader,nounits > "$out/gpu-after.csv" 2>&1 || true
  fi
}
trap record_accelerator_after EXIT

source scripts/ci-environment.sh

python3 scripts/benchmark_suite.py validate
python3 scripts/benchmark_suite.py list | tee "$LAPLACE_BENCH_RECEIPT/suite-list.txt"
[[ "$REPEATS" =~ ^[1-9][0-9]*$ ]] || {
  echo "::error::repeats must be a positive integer" >&2
  exit 2
}
(( REPEATS <= 100 )) || {
  echo "::error::repeats must be <= 100" >&2
  exit 2
}

out="$LAPLACE_BENCH_RECEIPT"
{
  printf 'schema=laplace.benchmark-host/v3\n'
  printf 'repository=%s\n' "$GITHUB_REPOSITORY"
  printf 'commit=%s\n' "$LAPLACE_BENCH_SHA"
  printf 'run_id=%s\n' "$GITHUB_RUN_ID"
  printf 'run_attempt=%s\n' "$GITHUB_RUN_ATTEMPT"
  printf 'suite=%s\n' "$SUITE"
  printf 'observed_utc=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf 'runner_name=%s\n' "$RUNNER_NAME"
  printf 'runner_os=%s\n' "$RUNNER_OS"
  printf 'runner_arch=%s\n' "$RUNNER_ARCH"
} > "$out/identity.env"

git status --short > "$out/git-status.txt"
git show -s --format=fuller "$LAPLACE_BENCH_SHA" > "$out/commit.txt"
uname -a > "$out/uname.txt"
lscpu > "$out/lscpu.txt"
free -b > "$out/memory.txt"
lsblk -b -o NAME,TYPE,SIZE,ROTA,FSTYPE,MOUNTPOINTS,MODEL > "$out/storage.txt"
df -B1 > "$out/filesystems.txt"
if command -v inxi >/dev/null 2>&1; then
  inxi -Fxz > "$out/inxi.txt" 2>&1 || true
else
  printf 'inxi unavailable\n' > "$out/inxi.txt"
fi
for governor in /sys/devices/system/cpu/cpu*/cpufreq/scaling_governor; do
  [[ -r "$governor" ]] || continue
  printf '%s\t%s\n' "$governor" "$(cat "$governor")"
done > "$out/cpu-governors.tsv"
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi -q > "$out/nvidia-smi-before.txt" 2>&1 || true
  nvidia-smi --query-gpu=name,uuid,pstate,power.draw,power.limit,memory.used,utilization.gpu,utilization.memory \
    --format=csv,noheader,nounits > "$out/gpu-before.csv" 2>&1 || true
else
  printf 'nvidia-smi unavailable\n' > "$out/nvidia-smi-before.txt"
fi

build_args=()
[[ "${FORCE_REBUILD:-false}" != true ]] || build_args+=(--force-rebuild)
bash scripts/pipeline.sh "${build_args[@]}" build 2>&1 | tee "$out/build.log"

core="$GITHUB_WORKSPACE/build/engine/core/liblaplace_core.so"
t0="$GITHUB_WORKSPACE/build/engine/core/perfcache/laplace_t0_perfcache.bin"
[[ -f "$core" ]] || { echo "::error::built core missing: $core" >&2; exit 1; }
[[ -f "$t0" ]] || { echo "::error::built T0 perfcache missing: $t0" >&2; exit 1; }
export LAPLACE_CORE="$core"
export LAPLACE_T0="$t0"
export LAPLACE_PERFCACHE_BIN="$t0"
{
  sha256sum "$core" "$t0"
  printf '\n== core linkage ==\n'
  ldd "$core" || true
} > "$out/built-artifacts.txt"

case "$SUITE" in
  quick|moby|all)
    dotnet build app/Laplace.Cli/Laplace.Cli.csproj -c Release --no-restore 2>&1 \
      | tee "$out/cli-release-build.log"
    ;;
esac

corpus="${CORPUS_DIR:-$GITHUB_WORKSPACE}"
args=(
  run
  --suite "$SUITE"
  --receipt-dir "$out"
  --repeats "$REPEATS"
  --corpus-dir "$corpus"
  --moby-path "$MOBY_PATH"
  --core "$core"
  --t0 "$t0"
)
[[ -z "${SCALE_WORKERS:-}" ]] || args+=(--scale-workers "$SCALE_WORKERS")
python3 scripts/benchmark_suite.py "${args[@]}" 2>&1 | tee "$out/suite.log"
