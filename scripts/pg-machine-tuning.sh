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
  mwm=$(( mem_kb / 32 / 1024 )); (( mwm < 256 )) && mwm=256; (( mwm > 4096 )) && mwm=4096
  wm=$(( mem_kb / 256 / 1024 )); (( wm < 32 )) && wm=32; (( wm > 512 )) && wm=512
  wb=$(( mem_kb / 512 / 1024 )); (( wb < 16 )) && wb=16; (( wb > 1024 )) && wb=1024

  PG_TUNE_SB=$(( mem_kb / 4 / 1024 ))MB
  PG_TUNE_ECS=$(( mem_kb * 65 / 100 / 1024 ))MB
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
}

pg_tune_psql() {
  if [ "${#PG_TUNE_PSQL[@]}" -gt 0 ]; then
    "${PG_TUNE_PSQL[@]}" "$@"
  else
    psql -d "${PGDATABASE:-postgres}" -U laplace_admin "$@"
  fi
}

pg_apply_machine_tuning() {
  pg_compute_machine_tuning
  pg_tune_psql -v ON_ERROR_STOP=1 \
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
