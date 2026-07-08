#!/usr/bin/env bash
# Laplace pipeline orchestrator — one idempotent phase per invocation.
#
# CI (.github/workflows/laplace.yml) runs one phase per job so the Actions
# graph maps 1:1 onto these phases. Locally, run any phase directly.
#
# Usage: pipeline.sh <phase> [<phase> ...] [options]
#
# Phases (in canonical order):
#   clean           rm -rf build/
#   build           codegen + cmake configure/build + perfcache + dotnet build
#   install         cmake --install to $LAPLACE_INSTALL_PREFIX
#   migrate         Laplace.Migrations up (idempotent; --fresh-db nukes first)
#   sync-extension  CREATE/ALTER EXTENSION laplace_substrate to built version
#   tune-pg         machine-sized ALTER SYSTEM tuning (restarts if pending)
#   tune-laplace    db/table-scoped ALTER TABLE tuning (run after migrate; skips empty DB)
#   perfcache-guc   point laplace_substrate.perfcache_path at installed blob
#   api-env         ensure laplace-api.env has current perfcache path
#   publish         deploy/linux/deploy.sh (API + SPA to $LAPLACE_INSTALL_PREFIX/app)
#   foundation      scripts/ensure-foundation.sh (no-ops on present layers; --force)
#
# Options:
#   --fresh-db   nuke DB before migrate
#   --force      pass --force to ensure-foundation.sh

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

LAPLACE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-/usr/lib/postgresql/18}"
LAPLACE_EXTERNAL="${LAPLACE_EXTERNAL:-/opt/laplace/external}"
PGDATABASE="${PGDATABASE:-laplace}"

FRESH_DB=0
FORCE_FOUNDATION=0

PYTHON=""
if command -v python3 >/dev/null 2>&1; then
  PYTHON=python3
elif command -v python >/dev/null 2>&1; then
  PYTHON=python
else
  echo "::error::python3 not found — install python3 on the runner" >&2
  exit 127
fi

usage() {
  sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'
  exit 2
}

restart_postgres() {
  # $1 = reason. Restart the systemd unit that OWNS the running postmaster —
  # discovered, never guessed. The old `systemctl is-active postgresql` guard
  # silently skipped the restart on any runner whose unit isn't literally named
  # "postgresql", leaving ALTER SYSTEM settings pending_restart forever (the
  # perfcache preload among them, which 503'd every smoke run). This helper
  # either restarts the real unit and PROVES nothing is left pending, or fails
  # the phase loudly right here — a pending restart is never again downgraded
  # to a warning four jobs upstream of the failure it causes.
  local reason="$1" unit=""
  if command -v systemctl >/dev/null 2>&1; then
    unit=$(systemctl list-units --type=service --state=running --plain --no-legend \
             'postgres*' '*postgres*' 2>/dev/null | awk '{print $1}' | head -1)
  fi
  if [[ -z "$unit" ]]; then
    echo "::error::restart_postgres ($reason): no running postgres systemd unit found — restart the cluster manually, then rerun this phase" >&2
    return 1
  fi
  echo "restart_postgres ($reason): restarting $unit"
  sudo systemctl restart "$unit"
  local tries=0
  until psql -d postgres -U laplace_admin -tAc "SELECT 1" >/dev/null 2>&1; do
    tries=$((tries + 1))
    if (( tries > 60 )); then
      echo "::error::restart_postgres ($reason): PostgreSQL not accepting connections ${tries}s after restarting $unit" >&2
      return 1
    fi
    sleep 1
  done
  local still
  still=$(psql -d postgres -U laplace_admin -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart")
  if [[ "$still" != "0" ]]; then
    echo "::error::restart_postgres ($reason): $still setting(s) STILL pending after restarting $unit:" >&2
    psql -d postgres -U laplace_admin -c "SELECT name, setting FROM pg_settings WHERE pending_restart" >&2
    return 1
  fi
  echo "restart_postgres ($reason): clean — no settings pending"
}

phase_clean() {
  echo "===== PHASE — CLEAN ====="
  rm -rf "$ROOT/build"
}

phase_codegen() {
  echo "===== PHASE — CODEGEN ====="
  "$PYTHON" "$ROOT/scripts/codegen-attestation-law.py"
}

phase_build() {
  phase_codegen
  echo "===== PHASE — BUILD ENGINE + EXTENSIONS ====="
  local data_root="${LAPLACE_DATA_ROOT:-/vault/Data}"
  local ucd="${LAPLACE_UCD_PATH:-$data_root/UCD/Public/UCD/latest}"
  cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
    -DCMAKE_INSTALL_PREFIX="$LAPLACE_INSTALL_PREFIX" \
    -DLAPLACE_PG_PREFIX="$LAPLACE_PG_PREFIX" \
    -DLAPLACE_EXTERNAL="$LAPLACE_EXTERNAL" \
    -DLAPLACE_INSTALL_STAGED=ON \
    -DLAPLACE_UCD_PATH="$ucd" \
    -DLAPLACE_UCDXML_ZIP="$ucd/ucdxml/ucd.nounihan.flat.zip" \
    -DLAPLACE_DUCET_FILE="$ucd/uca/allkeys.txt" \
    -DLAPLACE_UCD_CONFORMANCE_DIR="$ucd/ucd"
  LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}" \
    cmake --build build
  cmake --build build --target laplace_t0_perfcache
  echo "===== PHASE — BUILD APP ====="
  ( cd "$ROOT/app" && dotnet build Laplace.slnx -c Release )
}

phase_install() {
  echo "===== PHASE — INSTALL ====="
  test -d build || { echo "::error::build/ missing — run 'pipeline.sh build' first"; exit 1; }
  umask 0002
  cmake --install build
  test -f "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so"
}

phase_migrate() {
  echo "===== PHASE — MIGRATE ($PGDATABASE) ====="
  dotnet build "$ROOT/app/Laplace.Migrations/Laplace.Migrations.csproj" -c Release
  local mig="$ROOT/app/Laplace.Migrations/bin/Release/net10.0/Laplace.Migrations.dll"
  if [[ "$FRESH_DB" -eq 1 ]]; then
    dotnet "$mig" nuke --yes
  fi
  dotnet "$mig" up
}

phase_sync_extension() {
  echo "===== PHASE — SYNC EXTENSION SQL ====="
  local avail installed share
  avail=$(psql -d "$PGDATABASE" -U laplace_admin -tAX \
    -c "SELECT default_version FROM pg_available_extensions WHERE name='laplace_substrate'" \
    | tr -d '[:space:]')
  installed=$(psql -d "$PGDATABASE" -U laplace_admin -tAX \
    -c "SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'" \
    | tr -d '[:space:]')
  echo "laplace_substrate: installed='$installed' available='$avail'"
  test -n "$avail" || { echo "::error::laplace_substrate missing from pg_available_extensions"; exit 1; }
  if [[ -z "$installed" ]]; then
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate"
  elif [[ "$installed" != "$avail" ]]; then
    share=$(dirname "$(find "$LAPLACE_INSTALL_PREFIX" -name laplace_substrate.control -not -path '*/build*' 2>/dev/null | head -1)")
    test -n "$share" || { echo "::error::could not locate laplace_substrate.control under $LAPLACE_INSTALL_PREFIX"; exit 1; }
    cp "$share/laplace_substrate_upgrade.sql" "$share/laplace_substrate--${installed}--${avail}.sql"
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "ALTER EXTENSION laplace_substrate UPDATE TO '$avail'"
    echo "OK upgraded laplace_substrate $installed -> $avail in place"
  else
    echo "OK laplace_substrate already at $avail"
  fi
}

phase_tune_pg() {
  echo "===== PHASE — TUNE PG ====="
  # Sized from the machine, not hardcoded: ~25% RAM shared_buffers, ~65%
  # effective_cache_size, parallel workers from cores. PG18 async I/O via
  # io_uring + huge_pages=try (needs vm.nr_hugepages; try degrades quietly).
  # wal_level=minimal: single-node ingest server, no replicas — bulk COPY
  # WAL volume drops; flip to replica/logical the day a standby exists.
  local mem_kb cores pcores pdeg sb ecs mwp avw
  mem_kb=$(awk '/MemTotal/ {print $2}' /proc/meminfo)
  cores=$(nproc)
  # Hybrid-CPU awareness (Intel P/E since Alder Lake, ARM big.LITTLE). nproc counts P-threads
  # and E-threads as equal — wrong for parallel DEGREE: a worker dragged onto an efficiency
  # core stalls the whole gather at the slowest participant. Size parallelism to the count of
  # max-capacity (performance) threads, read from the kernel's cpu_capacity topology. On a
  # homogeneous CPU (the 6850K exposes no cpu_capacity) every core is max-capacity, so
  # pcores==nproc and this is a no-op — the 14900KS is where it bites (P-threads only).
  pcores=$cores
  if compgen -G "/sys/devices/system/cpu/cpu*/cpu_capacity" >/dev/null 2>&1; then
    local maxcap
    maxcap=$(cat /sys/devices/system/cpu/cpu*/cpu_capacity 2>/dev/null | sort -n | tail -1)
    pcores=$(grep -lxF "$maxcap" /sys/devices/system/cpu/cpu*/cpu_capacity 2>/dev/null | wc -l)
    (( pcores < 1 )) && pcores=$cores
  fi
  pdeg=$(( (pcores + 1) / 2 ))                 # per-gather / per-maintenance degree
  sb=$(( mem_kb / 4 / 1024 ))MB
  ecs=$(( mem_kb * 65 / 100 / 1024 ))MB
  # max_worker_processes is the shared ceiling that parallel query ($pcores), parallel
  # maintenance, PG18 io_workers, and autovacuum ALL draw from. A hardcoded 32 was wrong in
  # both directions — oversized on a 12-thread 6850K, and STARVING on a 32-thread 14900KS
  # (max_parallel_workers==nproc would consume the whole pool, leaving zero slots for
  # io/autovacuum). Derive it from performance-core parallelism plus io/autovacuum headroom.
  mwp=$(( pcores + pdeg + 8 ))
  # autovacuum_max_workers default (3) is thin for 4 bulk-ingest tables under aggressive
  # per-table analyze thresholds (phase_tune_laplace); scale with total cores, clamp to [3,6].
  avw=$(( cores / 4 )); (( avw < 3 )) && avw=3; (( avw > 6 )) && avw=6
  # maintenance_work_mem / work_mem / wal_buffers derived from RAM — the SAME formulas as
  # MemoryTopology.cs (the canonical .NET authority: RAM/32, RAM/256, RAM/512 with the same
  # clamps). Kept in bash here because this phase also needs runtime PG introspection
  # (io_method capability probe below) that the static `cpu-topology --pg-tuning` emitter
  # cannot do. NO hardcoded GB literals.
  local mwm wm wb
  mwm=$(( mem_kb / 32 / 1024 )); (( mwm < 256 )) && mwm=256; (( mwm > 4096 )) && mwm=4096; mwm=${mwm}MB
  wm=$(( mem_kb / 256 / 1024 )); (( wm < 32 )) && wm=32; (( wm > 512 )) && wm=512; wm=${wm}MB
  wb=$(( mem_kb / 512 / 1024 )); (( wb < 16 )) && wb=16; (( wb > 1024 )) && wb=1024; wb=${wb}MB
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1     -c "ALTER SYSTEM SET shared_buffers = '$sb'"     -c "ALTER SYSTEM SET effective_cache_size = '$ecs'"     -c "ALTER SYSTEM SET maintenance_work_mem = '$mwm'"     -c "ALTER SYSTEM SET work_mem = '$wm'"     -c "ALTER SYSTEM SET max_wal_size = '32GB'"     -c "ALTER SYSTEM SET min_wal_size = '4GB'"     -c "ALTER SYSTEM SET wal_compression = on"     -c "ALTER SYSTEM SET wal_buffers = '$wb'"     -c "ALTER SYSTEM SET wal_level = minimal"     -c "ALTER SYSTEM SET max_wal_senders = 0"     -c "ALTER SYSTEM SET checkpoint_timeout = '30min'"     -c "ALTER SYSTEM SET checkpoint_completion_target = 0.9"     -c "ALTER SYSTEM SET max_worker_processes = $mwp"     -c "ALTER SYSTEM SET autovacuum_max_workers = $avw"     -c "ALTER SYSTEM SET jit = off"     -c "ALTER SYSTEM SET max_parallel_workers = $pcores"     -c "ALTER SYSTEM SET max_parallel_workers_per_gather = $pdeg"     -c "ALTER SYSTEM SET max_parallel_maintenance_workers = $pdeg"     -c "ALTER SYSTEM SET effective_io_concurrency = 256"     -c "ALTER SYSTEM SET maintenance_io_concurrency = 256"     -c "ALTER SYSTEM SET random_page_cost = 1.1"     -c "ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0"     -c "ALTER SYSTEM SET huge_pages = try"     -c "ALTER SYSTEM SET io_workers = $pdeg"     -c "SELECT pg_reload_conf()"
  # io_uring only exists when PG was built with liburing; fall back to worker.
  local io
  io=$(psql -d "$PGDATABASE" -U laplace_admin -tAc     "SELECT CASE WHEN 'io_uring' = ANY(enumvals) THEN 'io_uring' ELSE 'worker' END FROM pg_settings WHERE name = 'io_method'")
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET io_method = $io"
  echo "tune-pg: io_method=$io"
  local pending
  pending=$(psql -d "$PGDATABASE" -U laplace_admin -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart")
  if [[ "$pending" != "0" ]]; then
    restart_postgres "tune-pg: $pending setting(s) pending"
  fi
  echo "tune-pg: shared_buffers=$sb effective_cache_size=$ecs maintenance_work_mem=$mwm work_mem=$wm wal_buffers=$wb cores=$cores pcores=$pcores pdeg=$pdeg max_worker_processes=$mwp autovacuum_max_workers=$avw jit=off"
}

phase_tune_laplace() {
  echo "===== PHASE — TUNE LAPLACE ====="
  # db/table-scoped tuning, distinct from tune-pg's cluster-wide ALTER SYSTEM GUCs: these are
  # ALTER TABLE settings that need the substrate tables to exist, so run AFTER migrate/install
  # (skips, not errors, on an empty DB). SET STATISTICS 0 on the geometry columns (read via
  # GiST/KNN, not histograms) makes autoanalyze on physicalities ~160x cheaper; the 2%/100k
  # thresholds fire at 100M-row scale instead of the 10% default that lags on bulk ingest.
  local have
  have=$(psql -d "$PGDATABASE" -U laplace_admin -tAc "SELECT to_regclass('laplace.physicalities') IS NOT NULL" 2>/dev/null || echo f)
  if [[ "$have" != "t" ]]; then
    echo "tune-laplace: substrate tables absent -- skipping (run after migrate)."
    return 0
  fi
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "ALTER TABLE laplace.physicalities ALTER COLUMN coord SET STATISTICS 0" \
    -c "ALTER TABLE laplace.physicalities ALTER COLUMN trajectory SET STATISTICS 0" \
    -c "ALTER TABLE laplace.entities      SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000)" \
    -c "ALTER TABLE laplace.physicalities SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000)" \
    -c "ALTER TABLE laplace.attestations  SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000)" \
    -c "ALTER TABLE laplace.consensus     SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000)"
  echo "tune-laplace: applied per-table stat + autoanalyze tuning."
}

phase_perfcache_guc() {
  echo "===== PHASE — PERFCACHE GUC ====="
  local bin hwbin
  bin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$bin" || { echo "::error::t0 perfcache blob not installed under $LAPLACE_INSTALL_PREFIX/share/laplace"; exit 1; }
  # The highway perfcache is built + installed (engine/core/CMakeLists.txt:206) and required
  # by the highway/band SQL, but this Linux phase historically only wired the T0 GUC — Windows
  # install-extensions.cmd sets BOTH. So highway_perfcache_path stayed empty (default) on
  # hart-server and the band-mask path never used its perfcache. Wire it here too.
  hwbin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_highway_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$hwbin" || { echo "::error::highway perfcache blob not installed under $LAPLACE_INSTALL_PREFIX/share/laplace"; exit 1; }
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "LOAD 'laplace_substrate'" \
    -c "ALTER SYSTEM SET laplace_substrate.perfcache_path = '$bin'" \
    -c "ALTER SYSTEM SET laplace_substrate.highway_perfcache_path = '$hwbin'" \
    -c "SELECT pg_reload_conf()"
  echo "perfcache_path -> $bin"
  echo "highway_perfcache_path -> $hwbin"
  # Preload the extension in the postmaster so every forked backend inherits
  # the mmap'd perfcache + reverse index copy-on-write (_PG_init prewarm)
  # instead of paying a multi-second lazy load on its first substrate call.
  # Requires a postmaster restart; only touched when the value changes.
  local preload
  preload=$(psql -d "$PGDATABASE" -U laplace_admin -tAc "SHOW shared_preload_libraries")
  if [[ ",${preload// /}," != *",laplace_substrate,"* ]]; then
    local newval="laplace_substrate"
    [[ -n "$preload" ]] && newval="$preload,laplace_substrate"
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1       -c "ALTER SYSTEM SET shared_preload_libraries = '$newval'"
    restart_postgres "shared_preload_libraries -> $newval (perfcache prewarm)"
  fi

}

phase_api_env() {
  echo "===== PHASE — API ENV ====="
  local env_file="$LAPLACE_INSTALL_PREFIX/app/laplace-api.env"
  local bin example
  bin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$bin" || { echo "::error::perfcache blob missing for API env"; exit 1; }
  example="$ROOT/deploy/linux/laplace-api.env.example"
  if [[ ! -f "$env_file" ]]; then
    install -m 0640 -o laplace-runner -g laplace-runner "$example" "$env_file" 2>/dev/null \
      || cp "$example" "$env_file"
    echo "created $env_file from example"
  fi
  if grep -q '^LAPLACE_PERFCACHE_BIN=' "$env_file"; then
    sed -i "s|^LAPLACE_PERFCACHE_BIN=.*|LAPLACE_PERFCACHE_BIN=$bin|" "$env_file"
  else
    printf '\nLAPLACE_PERFCACHE_BIN=%s\n' "$bin" >> "$env_file"
  fi
  echo "LAPLACE_PERFCACHE_BIN -> $bin"

  # Reconcile the API's database to the one this pipeline actually seeds ($PGDATABASE),
  # EVERY run. The block above only writes the example (with Database=laplace) when the
  # env file is absent; an existing laplace-api.env keeps whatever DB it had. A stale
  # Database=laplace-dev therefore persisted, so the deployed API served an empty DB while
  # the seed populated laplace — /health/ready reported entities=0 and every smoke failed,
  # a silent config drift, not a seed failure. Pin it like the perfcache path.
  local api_db="LAPLACE_DB=Host=/var/run/postgresql;Username=laplace_admin;Database=${PGDATABASE:-laplace}"
  if grep -q '^LAPLACE_DB=' "$env_file"; then
    sed -i "s|^LAPLACE_DB=.*|${api_db}|" "$env_file"
  else
    printf '\n%s\n' "$api_db" >> "$env_file"
  fi
  echo "LAPLACE_DB -> Database=${PGDATABASE:-laplace}"
}

phase_publish() {
  echo "===== PHASE — PUBLISH API + SPA ====="
  bash "$ROOT/deploy/linux/deploy.sh"
}

phase_foundation() {
  echo "===== PHASE — ENSURE FOUNDATION ====="
  local args=()
  [[ "$FORCE_FOUNDATION" -eq 1 ]] && args+=(--force)
  LAPLACE_DBNAME="$PGDATABASE" \
    bash "$ROOT/scripts/ensure-foundation.sh" "${args[@]}"
}

PHASES=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fresh-db) FRESH_DB=1; shift ;;
    --force) FORCE_FOUNDATION=1; shift ;;
    -h|--help) usage ;;
    clean|codegen|build|install|migrate|sync-extension|tune-pg|tune-laplace|perfcache-guc|api-env|publish|foundation)
      PHASES+=("$1"); shift ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done

[[ ${#PHASES[@]} -gt 0 ]] || usage

for phase in "${PHASES[@]}"; do
  case "$phase" in
    clean)          phase_clean ;;
    codegen)        phase_codegen ;;
    build)          phase_build ;;
    install)        phase_install ;;
    migrate)        phase_migrate ;;
    sync-extension) phase_sync_extension ;;
    tune-pg)        phase_tune_pg ;;
    tune-laplace)   phase_tune_laplace ;;
    perfcache-guc)  phase_perfcache_guc ;;
    api-env)        phase_api_env ;;
    publish)        phase_publish ;;
    foundation)     phase_foundation ;;
  esac
done

echo "===== PIPELINE PHASES COMPLETE: ${PHASES[*]} ====="
