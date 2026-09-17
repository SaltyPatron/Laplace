#!/usr/bin/env bash
set -euo pipefail

ROOT="${GITHUB_WORKSPACE:?GITHUB_WORKSPACE is required}"
cd "$ROOT"

: "${LAPLACE_BENCH_RECEIPT:?LAPLACE_BENCH_RECEIPT is required}"
: "${LAPLACE_BENCH_SHA:?LAPLACE_BENCH_SHA is required}"
: "${SUITE:?SUITE is required}"
: "${REPEATS:?REPEATS is required}"
: "${MOBY_PATH:?MOBY_PATH is required}"

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
resolved_scale=""
if [[ "$SUITE" == throughput || "$SUITE" == scale || "$SUITE" == dag || "$SUITE" == all ]]; then
  scale_plan_args=(
    --reserve-logical "$RESERVE_LOGICAL_CPUS"
    --github-env "$GITHUB_ENV"
    --json "$out/scale-plan.json"
  )
  [[ -z "${SCALE_WORKERS:-}" ]] || scale_plan_args+=(--workers "$SCALE_WORKERS")
  [[ "$ALLOW_SATURATION" != true ]] || scale_plan_args+=(--allow-saturation)
  python3 scripts/benchmark_scale_plan.py "${scale_plan_args[@]}"
  resolved_scale="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["resolved_workers_csv"])' "$out/scale-plan.json")"
fi

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
for f in /sys/devices/system/cpu/cpu*/cpufreq/scaling_governor; do
  [[ -r "$f" ]] || continue
  printf '%s\t%s\n' "$f" "$(cat "$f")"
done > "$out/cpu-governors.tsv"
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi -q > "$out/nvidia-smi-before.txt" 2>&1 || true
  nvidia-smi --query-gpu=name,uuid,pstate,power.draw,power.limit,memory.used,utilization.gpu,utilization.memory \
    --format=csv,noheader,nounits > "$out/gpu-before.csv" 2>&1 || true
else
  printf 'nvidia-smi unavailable\n' > "$out/nvidia-smi-before.txt"
fi

record_after() {
  if command -v nvidia-smi >/dev/null 2>&1; then
    nvidia-smi -q > "$out/nvidia-smi-after.txt" 2>&1 || true
    nvidia-smi --query-gpu=name,uuid,pstate,power.draw,power.limit,memory.used,utilization.gpu,utilization.memory \
      --format=csv,noheader,nounits > "$out/gpu-after.csv" 2>&1 || true
  fi
}
trap record_after EXIT

core=""
t0=""
if [[ "$SUITE" != chess && "$SUITE" != geometry && "$SUITE" != recorded ]]; then
  build_args=()
  [[ "${FORCE_REBUILD:-false}" != true ]] || build_args+=(--force-rebuild)
  bash scripts/pipeline.sh "${build_args[@]}" build 2>&1 | tee "$out/build.log"

  core="$GITHUB_WORKSPACE/build/engine/core/liblaplace_core.so"
  t0="$GITHUB_WORKSPACE/build/engine/core/perfcache/laplace_t0_perfcache.bin"
  control="$GITHUB_WORKSPACE/build/extension/laplace_substrate/laplace_substrate.control"
  execution="$GITHUB_WORKSPACE/build/extension/laplace_substrate/laplace_execution_module.txt"
  [[ -f "$core" ]] || { echo "::error::built core missing: $core" >&2; exit 1; }
  [[ -f "$t0" ]] || { echo "::error::built T0 perfcache missing: $t0" >&2; exit 1; }
  [[ -f "$control" ]] || { echo "::error::built substrate control missing: $control" >&2; exit 1; }
  [[ -f "$execution" ]] || { echo "::error::built execution identity missing: $execution" >&2; exit 1; }
  {
    sha256sum "$core" "$t0" "$control" "$execution"
    printf '\n== core linkage ==\n'
    ldd "$core" || true
  } > "$out/built-artifacts.txt"
fi

if [[ "$SUITE" == quick || "$SUITE" == moby || "$SUITE" == all ]]; then
  dotnet build app/Laplace.Cli/Laplace.Cli.csproj -c Release --no-restore 2>&1 \
    | tee "$out/cli-release-build.log"
fi

corpus="${CORPUS_DIR:-$GITHUB_WORKSPACE}"
run_args=(
  run
  --suite "$SUITE"
  --receipt-dir "$out"
  --repeats "$REPEATS"
  --corpus-dir "$corpus"
  --moby-path "$MOBY_PATH"
  --database "$PGDATABASE"
)
if [[ -n "$core" ]]; then
  run_args+=(--core "$core" --t0 "$t0")
fi
[[ -z "$resolved_scale" ]] || run_args+=(--scale-workers "$resolved_scale")
python3 scripts/benchmark_suite.py "${run_args[@]}" 2>&1 | tee "$out/suite.log"
