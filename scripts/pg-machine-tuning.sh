#!/usr/bin/env bash
# Machine-sized Postgres GUCs — single formula set for bootstrap + pipeline.
#
# Source only:
#   source "$ROOT/scripts/pg-machine-tuning.sh"
#   pg_compute_machine_tuning          # sets PG_TUNE_* 
#   PG_TUNE_PSQL=(psql ...)            # optional; default below
#   pg_apply_machine_tuning            # ALTER SYSTEM + reload
#   pg_validate_machine_tuning         # live == computed (bytes-equal for mem)
#
# Formulas match MemoryTopology.cs / pipeline phase_tune_pg / cpu-topology --pg-tuning.
# NO hardcoded GB literals for RAM-derived knobs.

pg_compute_machine_tuning() {
  local mem_kb cores pcores pdeg mwp avw mwm wm wb
  mem_kb=$(awk '/MemTotal/ {print $2}' /proc/meminfo)
  cores=$(nproc)
  pcores=$cores
  if compgen -G "/sys/devices/system/cpu/cpu*/cpu_capacity" >/dev/null 2>&1; then
    local maxcap
    maxcap=$(cat /sys/devices/system/cpu/cpu*/cpu_capacity 2>/dev/null | sort -n | tail -1)
    pcores=$(grep -lxF "$maxcap" /sys/devices/system/cpu/cpu*/cpu_capacity 2>/dev/null | wc -l)
    (( pcores < 1 )) && pcores=$cores
  fi
  pdeg=$(( (pcores + 1) / 2 ))
  mwp=$(( pcores + pdeg + 8 ))
  avw=$(( cores / 4 )); (( avw < 3 )) && avw=3; (( avw > 6 )) && avw=6
  # These MUST stay bytes-equal with MemoryTopology.cs (SharedBuffersBytes,
  # EffectiveCacheSizeBytes, MaintenanceWorkMemBytes, WorkMemBytes, WalBuffersBytes) —
  # that file carries the 2026-07-15 / doc-28 incident hardening and this script is what
  # actually issues the ALTER SYSTEM. The two drifted: C# was fixed to RAM/1536-cap-64MB
  # work_mem, RAM/48-cap-1GB maintenance_work_mem and a 16GB shared_buffers cap, while
  # this file kept the pre-incident RAM/256-cap-512MB, RAM/32-cap-4GB and an UNCAPPED
  # shared_buffers. On the 125GB seed host that applied work_mem=502MB,
  # maintenance_work_mem=3.9GB, shared_buffers=31.4GB and drove 12.5GB into swap during
  # the wiktionary ingest (26 live backends x ~work_mem of anonymous memory sitting on
  # top of pinned huge-page shared_buffers). Change both sides together or not at all.
  mwm=$(( mem_kb / 48 / 1024 )); (( mwm < 256 )) && mwm=256; (( mwm > 1024 )) && mwm=1024
  wm=$(( mem_kb / 1536 / 1024 )); (( wm < 16 )) && wm=16; (( wm > 64 )) && wm=64
  wb=$(( mem_kb / 512 / 1024 )); (( wb < 16 )) && wb=16; (( wb > 1024 )) && wb=1024

  local sb ecs
  sb=$(( mem_kb / 4 / 1024 )); (( sb < 128 )) && sb=128; (( sb > 16384 )) && sb=16384
  ecs=$(( mem_kb * 65 / 100 / 1024 )); (( ecs < 512 )) && ecs=512; (( ecs > 98304 )) && ecs=98304

  PG_TUNE_SB=${sb}MB
  PG_TUNE_ECS=${ecs}MB
  PG_TUNE_MWM=${mwm}MB
  PG_TUNE_WM=${wm}MB
  PG_TUNE_WB=${wb}MB
  PG_TUNE_CORES=$cores
  PG_TUNE_PCORES=$pcores
  PG_TUNE_PDEG=$pdeg
  PG_TUNE_MWP=$mwp
  PG_TUNE_AVW=$avw
  PG_TUNE_MAX_WAL=32GB
  PG_TUNE_MIN_WAL=4GB
  PG_TUNE_IO_CONC=256
  PG_TUNE_CHECKPOINT=30min
  # Policy knobs the shell used to omit entirely, letting the cluster keep PG
  # defaults that silently multiply the memory budget: hash_mem_multiplier 2.0
  # doubles work_mem on every hash node, and autovacuum_work_mem = -1 makes each
  # autovacuum worker inherit maintenance_work_mem. Values mirror EmitPgTuning.
  PG_TUNE_MAXCONN=60
  PG_TUNE_AVWM=256MB
  PG_TUNE_TEMPB=32MB
}

pg_tune_psql() {
  if [ "${#PG_TUNE_PSQL[@]}" -gt 0 ]; then
    "${PG_TUNE_PSQL[@]}" "$@"
  else
    psql -d "${PGDATABASE:-postgres}" -U laplace_admin "$@"
  fi
}

# Locate the CLI that owns the GUC set (CpuTopologyCommands.EmitPgTuning). Honour an
# explicit LAPLACE_CLI_DLL, else the standard Release publish under the repo root.
pg_tune_cli_dll() {
  if [ -n "${LAPLACE_CLI_DLL:-}" ] && [ -f "$LAPLACE_CLI_DLL" ]; then
    printf '%s' "$LAPLACE_CLI_DLL"; return 0
  fi
  local root dll
  root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
  dll="$root/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"
  [ -f "$dll" ] || return 1
  command -v dotnet >/dev/null 2>&1 || return 1
  printf '%s' "$dll"
}

# ONE implementation of the GUC set: CpuTopologyCommands.EmitPgTuning, which
# tune-pg.cmd already pipes to psql on Windows. Linux used to re-derive it in bash,
# and the two silently diverged on TEN settings — the shell never emitted
# max_connections, hash_mem_multiplier, autovacuum_work_mem or temp_buffers at all,
# so the seed host ran hash nodes at 2x work_mem and let every autovacuum worker
# inherit maintenance_work_mem (3.9GB) instead of the intended 256MB. Prefer the
# emitter; the bash formulas below survive ONLY as the bare-host bootstrap fallback
# (setup-host tunes the cluster before the app is ever built) and are pinned
# bytes-equal to MemoryTopology by PgTuningParityTests.
pg_apply_machine_tuning() {
  local dll
  if dll="$(pg_tune_cli_dll)"; then
    if dotnet "$dll" cpu-topology --pg-tuning | pg_tune_psql -v ON_ERROR_STOP=1 -f -; then
      pg_compute_machine_tuning
      echo "pg-machine-tuning: applied from cpu-topology --pg-tuning (authoritative emitter)"
      return 0
    fi
    echo "pg-machine-tuning: emitter failed — falling back to bootstrap formulas" >&2
  else
    echo "pg-machine-tuning: CLI not built yet — using bootstrap fallback formulas" >&2
  fi

  pg_apply_machine_tuning_fallback
}

pg_apply_machine_tuning_fallback() {
  pg_compute_machine_tuning
  pg_tune_psql -v ON_ERROR_STOP=1 \
    -c "ALTER SYSTEM SET max_connections = $PG_TUNE_MAXCONN" \
    -c "ALTER SYSTEM SET hash_mem_multiplier = 1.0" \
    -c "ALTER SYSTEM SET autovacuum_work_mem = '$PG_TUNE_AVWM'" \
    -c "ALTER SYSTEM SET temp_buffers = '$PG_TUNE_TEMPB'" \
    -c "ALTER SYSTEM SET shared_buffers = '$PG_TUNE_SB'" \
    -c "ALTER SYSTEM SET effective_cache_size = '$PG_TUNE_ECS'" \
    -c "ALTER SYSTEM SET maintenance_work_mem = '$PG_TUNE_MWM'" \
    -c "ALTER SYSTEM SET work_mem = '$PG_TUNE_WM'" \
    -c "ALTER SYSTEM SET max_wal_size = '$PG_TUNE_MAX_WAL'" \
    -c "ALTER SYSTEM SET min_wal_size = '$PG_TUNE_MIN_WAL'" \
    -c "ALTER SYSTEM SET wal_compression = on" \
    -c "ALTER SYSTEM SET wal_buffers = '$PG_TUNE_WB'" \
    -c "ALTER SYSTEM SET wal_level = minimal" \
    -c "ALTER SYSTEM SET max_wal_senders = 0" \
    -c "ALTER SYSTEM SET checkpoint_timeout = '$PG_TUNE_CHECKPOINT'" \
    -c "ALTER SYSTEM SET checkpoint_completion_target = 0.9" \
    -c "ALTER SYSTEM SET max_worker_processes = $PG_TUNE_MWP" \
    -c "ALTER SYSTEM SET autovacuum_max_workers = $PG_TUNE_AVW" \
    -c "ALTER SYSTEM SET jit = off" \
    -c "ALTER SYSTEM SET max_parallel_workers = $PG_TUNE_PCORES" \
    -c "ALTER SYSTEM SET max_parallel_workers_per_gather = $PG_TUNE_PDEG" \
    -c "ALTER SYSTEM SET max_parallel_maintenance_workers = $PG_TUNE_PDEG" \
    -c "ALTER SYSTEM SET effective_io_concurrency = $PG_TUNE_IO_CONC" \
    -c "ALTER SYSTEM SET maintenance_io_concurrency = $PG_TUNE_IO_CONC" \
    -c "ALTER SYSTEM SET random_page_cost = 1.1" \
    -c "ALTER SYSTEM SET autovacuum_vacuum_cost_delay = 0" \
    -c "ALTER SYSTEM SET huge_pages = try" \
    -c "ALTER SYSTEM SET synchronous_commit = off" \
    -c "ALTER SYSTEM SET io_workers = $PG_TUNE_PDEG" \
    -c "ALTER SYSTEM SET max_locks_per_transaction = 1024" \
    -c "SELECT pg_reload_conf()"

  local io
  io=$(pg_tune_psql -tAc \
    "SELECT CASE WHEN 'io_uring' = ANY(enumvals) THEN 'io_uring' ELSE 'worker' END FROM pg_settings WHERE name = 'io_method'")
  pg_tune_psql -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET io_method = $io"
  echo "pg-machine-tuning: io_method=$io shared_buffers=$PG_TUNE_SB effective_cache_size=$PG_TUNE_ECS maintenance_work_mem=$PG_TUNE_MWM work_mem=$PG_TUNE_WM wal_buffers=$PG_TUNE_WB pcores=$PG_TUNE_PCORES pdeg=$PG_TUNE_PDEG"
}

# Returns 0 if live settings match computed machine tuning and nothing pending_restart.
# Optional: PG_TUNE_OK / PG_TUNE_BAD callbacks (default: echo).
pg_validate_machine_tuning() {
  pg_compute_machine_tuning
  local vbad=0 nm live ok pend
  local _ok="${PG_TUNE_OK:-echo}"
  local _bad="${PG_TUNE_BAD:-echo}"

  while IFS='|' read -r nm live ok pend; do
    [ -z "$nm" ] && continue
    if [ "$ok" != "t" ]; then
      $_bad "  ✗ $nm = '$live' (want machine-sized; not pending alone)"
      vbad=1
    elif [ "$pend" = "t" ]; then
      $_bad "  ✗ $nm pending_restart — cluster not fully restarted"
      vbad=1
    else
      $_ok "  ✓ $nm = $live"
    fi
  done < <(pg_tune_psql -tAF'|' <<PG_EOF
WITH want(name, expected, mode) AS (VALUES
  ('shared_buffers','${PG_TUNE_SB}','mem'),
  ('effective_cache_size','${PG_TUNE_ECS}','mem'),
  ('maintenance_work_mem','${PG_TUNE_MWM}','mem'),
  ('work_mem','${PG_TUNE_WM}','mem'),
  ('max_wal_size','${PG_TUNE_MAX_WAL}','mem'),
  ('min_wal_size','${PG_TUNE_MIN_WAL}','mem'),
  ('wal_buffers','${PG_TUNE_WB}','mem'),
  ('max_connections','${PG_TUNE_MAXCONN}','eq'),
  ('hash_mem_multiplier','1','eq'),
  ('autovacuum_work_mem','${PG_TUNE_AVWM}','mem'),
  ('temp_buffers','${PG_TUNE_TEMPB}','mem'),
  ('synchronous_commit','off','eq'),
  ('checkpoint_timeout','${PG_TUNE_CHECKPOINT}','eq'),
  ('wal_compression','on','enabled'),
  ('max_parallel_maintenance_workers','${PG_TUNE_PDEG}','eq'),
  ('effective_io_concurrency','${PG_TUNE_IO_CONC}','eq'),
  ('max_locks_per_transaction','1024','eq'),
  ('huge_pages','try','eq'))
SELECT w.name, current_setting(w.name),
       CASE w.mode
         WHEN 'mem'     THEN pg_size_bytes(current_setting(w.name)) = pg_size_bytes(w.expected)
         WHEN 'enabled' THEN current_setting(w.name) <> 'off'
         ELSE current_setting(w.name) = w.expected END,
       s.pending_restart
FROM want w JOIN pg_settings s ON s.name = w.name
ORDER BY w.name;
PG_EOF
)

  local npend
  npend=$(pg_tune_psql -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart" 2>/dev/null || echo 1)
  if [ "${npend:-1}" != "0" ]; then
    $_bad "  ✗ ${npend:-?} setting(s) pending_restart — cluster not fully restarted"
    vbad=1
  fi
  return "$vbad"
}
