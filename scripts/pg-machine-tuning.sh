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
  local mem_kb cores pcores pdeg mwp avw mwm_kb wm_kb iow
  local ingest_conns observability_conns serving_conns maintenance_conns reserved_conns
  local backend_kb backend_processes per_backend_kb temp_kb
  mem_kb=$(awk '/MemTotal/ {print $2}' /proc/meminfo)
  cores=$(nproc)
  pcores=$cores
  if [[ -r /sys/devices/cpu_core/cpus ]]; then
    local spec token lo hi cpu core_ids
    local -a cpu_tokens
    spec=$(tr -d '[:space:]' </sys/devices/cpu_core/cpus)
    core_ids=""
    IFS=',' read -ra cpu_tokens <<<"$spec"
    for token in "${cpu_tokens[@]}"; do
      if [[ "$token" == *-* ]]; then
        lo=${token%-*}; hi=${token#*-}
      else
        lo=$token; hi=$token
      fi
      for ((cpu=lo; cpu<=hi; cpu++)); do
        if [[ -r "/sys/devices/system/cpu/cpu${cpu}/topology/core_id" ]]; then
          core_ids+="$(<"/sys/devices/system/cpu/cpu${cpu}/topology/core_id")"$'\n'
        fi
      done
    done
    pcores=$(printf '%s' "$core_ids" | sed '/^$/d' | sort -nu | wc -l)
    (( pcores < 1 )) && pcores=$cores
  elif compgen -G "/sys/devices/system/cpu/cpu*/cpu_capacity" >/dev/null 2>&1; then
    local maxcap
    maxcap=$(cat /sys/devices/system/cpu/cpu*/cpu_capacity 2>/dev/null | sort -n | tail -1)
    pcores=$(grep -lxF "$maxcap" /sys/devices/system/cpu/cpu*/cpu_capacity 2>/dev/null | wc -l)
    (( pcores < 1 )) && pcores=$cores
  fi
  pdeg=$pcores
  # I/O WORKERS ARE NOT PARALLEL-QUERY WORKERS. Under io_method=worker every asynchronous
  # read for the whole cluster is dispatched through this pool, so it bounds the achievable
  # queue depth no matter what effective_io_concurrency claims. It used to be set to $pdeg
  # -- a CPU parallel-degree heuristic, where each worker allocates work_mem and which was
  # deliberately kept small for memory pressure. io_workers allocate no work_mem and never
  # compute; they block on the device.
  #
  # MEASURED 2026-08-03, pgdata on a Samsung 970 EVO Plus, mid chess ingest:
  #   effective_io_concurrency=256 (a promise), io_workers=3 (the real ceiling)
  #   r/s 30,589   r_await 0.29ms   aqu-sz 11.92
  # Little's law: 11.92/0.29ms ~= 41k IOPS -- the drive delivered exactly what the queue
  # depth allowed, at roughly 6% of what it can do, while %util read 100%. On a device with
  # hardware queues %util means "at least one request in flight", not saturation.
  #
  # One blocking I/O worker per logical issuer. The old [8,32] clamp made a
  # 64-thread host indistinguishable from a 32-thread host and invented eight
  # workers on a two-thread host without observing either workload.
  iow=$cores
  # max_worker_processes is the SHARED pool parallel query AND the io_worker pool draw from.
  # Maintenance is a subset of the parallel-worker pool. max_worker_processes
  # therefore owns the compute pool plus the blocking I/O pool, with no mystery +8.
  mwp=$(( pcores + iow ))
  avw=$pdeg
  # These MUST stay bytes-equal with PostgresResourcePlan. The former shell and C#
  # implementations independently clamped each GUC and oversubscribed RAM when their
  # products were combined. The replacement is one four-domain resource equation:
  # shared cache / backend private / ingest client / OS page cache.
  # Connection owners are the actual simultaneous ingest COPY+fold fan, the ingest
  # observability owners (run-liveness lock held for the whole run, file-journal
  # pump, run-journal writer), serving logical concurrency, maintenance pool, and
  # one recovery connection. Without the observability term 1 + 2*pcores equalled
  # the fan population exactly, leaving the ingest pool zero slack; the three
  # observability owners then waited the full 15s Timeout and threw
  # "connection pool has been exhausted".
  observability_conns=3
  ingest_conns=$(( 1 + 2 * pcores + observability_conns ))
  serving_conns=$cores
  maintenance_conns=$pdeg
  reserved_conns=1
  PG_TUNE_MAXCONN=$(( ingest_conns + serving_conns + maintenance_conns + reserved_conns ))
  backend_kb=$(( mem_kb / 4 ))
  backend_processes=$(( PG_TUNE_MAXCONN + pcores + avw ))
  per_backend_kb=$(( backend_kb / backend_processes ))
  wm_kb=$(( per_backend_kb / 2 ))
  temp_kb=$(( per_backend_kb - wm_kb ))
  mwm_kb=$per_backend_kb

  local sb ecs
  sb=$(( mem_kb / 4 ))
  ecs=$(( mem_kb / 2 ))

  PG_TUNE_SB=${sb}kB
  PG_TUNE_ECS=${ecs}kB
  PG_TUNE_MWM=${mwm_kb}kB
  PG_TUNE_WM=${wm_kb}kB
  PG_TUNE_WB=auto
  # shellcheck disable=SC2034  # no consumer yet; kept so this block mirrors EmitPgTuning's
  # full surface — deleting one member of a mirrored contract is worse than an unused var.
  PG_TUNE_CORES=$cores
  PG_TUNE_PCORES=$pcores
  PG_TUNE_PDEG=$pdeg
  PG_TUNE_MWP=$mwp
  PG_TUNE_IOW=$iow
  PG_TUNE_AVW=$avw
  PG_TUNE_RESERVED=$reserved_conns
  # Device/WAL policy below is measured host configuration rather than a machine-size
  # throughput cap. The parity gate keeps the bootstrap fallback and emitter identical.
  #
  # Measured 2026-07-31 on the 125GB host: 202 volume-forced checkpoints against 24 timed,
  # i.e. one roughly every 9 minutes against a 30-minute target, each re-arming full-page
  # writes. The controlled evidence arrived 2026-08-12: at 32GB one seed day produced 60
  # forced vs 33 timed checkpoints and 478GB of WAL for a ~15GB substrate — 72% of it
  # full-page images (42.3M), i.e. the cap itself was the write amplifier. Raised to 96GB
  # (the WAL volume is a dedicated 128GB NVMe LV; 25% headroom). MUST match
  # CpuTopologyCommands.EmitPgTuning; PgTuningParityTests pins the pair.
  PG_TUNE_MAX_WAL=96GB
  PG_TUNE_MIN_WAL=4GB
  # MEASURED 2026-08-16 ON THIS CLUSTER, replacing a hardcoded 256 that was never true.
  #
  # THE CEILING MOVED WHEN io_method DID. effective_io_concurrency is a REQUEST for queue
  # depth; something else bounds what a backend may actually hold in flight. Under
  # io_method=worker that bound is the io_workers pool, which is what the measurement
  # above reasons about. pg_apply_io_method now probes and selects io_uring whenever the
  # build supports it, and under io_uring the bound is io_max_concurrency -- a per-backend
  # cap this script never emitted, so it sat at its default of 64 while this line asked
  # for 256.
  #
  # THE CAP BINDS, sampled from pg_aios while one backend seq-scanned a 2,573 MB
  # attestations leaf: MAX in-flight AIO ops held by that backend = 64, exactly
  # io_max_concurrency. The other 192 of the request were notional.
  #
  # AND THE REQUEST BUYS NOTHING EVEN SO. Three cold 2.5 GB leaves, one each, identical
  # shape, taken under scripts/measure-lane.sh on a quiet substrate:
  #     effective_io_concurrency=256  attestations_rdefault_h0  3175.480 ms
  #     effective_io_concurrency=64   attestations_rdefault_h1  3175.204 ms
  #     effective_io_concurrency=32   attestations_rdefault_h2  3167.613 ms
  # 8 ms of spread across a 3.17 s scan, with the LOWEST setting marginally fastest.
  #
  # So the value is pinned to the cap: the config now states what the backend can actually
  # do. io_max_concurrency is postmaster-context, so RAISING it would cost a cluster
  # restart, and nothing above justifies one.
  #
  # RANDOM ACCESS, measured too, because a sequential scan is the friendliest case for
  # prefetch and the rule is conditional (tasklist SS A). 30,000 sampled entity ids split
  # into 3 disjoint groups, probed against laplace.attestations by subject_id, with the
  # eic-to-group assignment ROTATED so each setting sees each group once and set-size
  # effects cancel:
  #     round 1 (first touch)   eic 256: 5811 ms   64: 5395 ms   32: 5154 ms
  #     round 2 (warm)          eic 256:  677 ms   64:  660 ms   32:  664 ms
  #     round 3 (warm)          eic 256:  664 ms   64:  675 ms   32:  670 ms
  # Warm, the spread is 660-677 ms with NO ordering by eic -- noise. The descending look in
  # round 1 is progressive cache warming, not queue depth: the rotation is what separates
  # the two, and a single cold run per setting would have read as a 13%/row win for 32.
  # That is the SS L trap, avoided by repeating rather than by reasoning.
  #
  # The request now follows the number of live I/O issuers; no unrelated 64/256 literal
  # limits a different machine.
  PG_TUNE_IO_CONC=$iow
  PG_TUNE_CHECKPOINT=30min
  # Policy knobs the shell used to omit entirely, letting the cluster keep PG
  # defaults that silently multiply the memory budget: hash_mem_multiplier 2.0
  # doubles work_mem on every hash node, and autovacuum_work_mem = -1 makes each
  # autovacuum worker inherit maintenance_work_mem. Values mirror EmitPgTuning.
  PG_TUNE_AVWM=${mwm_kb}kB
  PG_TUNE_TEMPB=${temp_kb}kB
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
# io_method is probed, never assumed: io_uring only appears in enumvals when PG was built
# with liburing. This USED TO LIVE IN THE FALLBACK ONLY, so on every host where the CLI is
# built -- i.e. normal operation -- the emitter's hardcoded `io_method = worker` stood and
# the probe never ran. io_uring lets each backend submit directly to the kernel with no
# worker pool and therefore no io_workers ceiling at all, which is the whole point on NVMe.
# wal_compression is probed for the same reason io_method is: PostgreSQL's configure does
# NOT auto-detect lz4/zstd, so a build without them silently offers only pglz -- the SLOWEST
# codec (~100-200 MB/s vs lz4 ~500+). `wal_compression = on` RESOLVES TO pglz, so the old
# unconditional `SET wal_compression = on` looked like a tuning decision and was really a
# default. MEASURED 2026-08-03: enumvals {pglz,on,off} on a cluster ingesting to NVMe with
# ~20x WAL amplification. Prefer lz4, then zstd, then pglz only if that is all there is.
# Single reload point. io_method and wal_compression are probed from a LIVE connection, so
# they are necessarily ALTER SYSTEMed after the bulk settings; a reload issued before that
# leaves both sitting in postgresql.auto.conf unapplied until some unrelated restart. Every
# apply path ends here, after the last ALTER SYSTEM it will issue.
pg_tune_reload() {
  pg_tune_psql -v ON_ERROR_STOP=1 -c "SELECT pg_reload_conf()" >/dev/null
}

# TOAST compression, probed the same way and for the same reason as WAL: the
# emitter writes SQL with no connection, so it cannot read enumvals, and a host
# built without --with-lz4 would ERROR on 'lz4' under ON_ERROR_STOP=1 and take
# tune-pg down with it. default_toast_compression only ever offers {pglz,lz4}.
#
# MEASURED 2026-08-11, before the database was recreated: 86 GB of the 143 GB
# database was TOAST, all pglz -- pg_settings reported source='default', so it
# had never been set anywhere. It concentrates in the physicalities_h*
# trajectory columns a SQL forward pass decompresses on every read, and pglz is
# several times slower than lz4 in both directions.
#
# Safe to change in place: all 2716 compressible columns carry the unbaked
# sentinel (pg_attribute.attcompression = \0), so this resolves per value at
# INSERT rather than being frozen at CREATE TABLE. No ALTER TABLE, no rewrite,
# and mixed pglz/lz4 data stays readable because the codec is recorded in each
# TOAST pointer.
pg_apply_toast_compression() {
  local tc
  tc=$(pg_tune_psql -tAc \
    "SELECT CASE WHEN 'lz4' = ANY(enumvals) THEN 'lz4' ELSE 'pglz' END FROM pg_settings WHERE name = 'default_toast_compression'")
  pg_tune_psql -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET default_toast_compression = $tc"
  echo "pg-machine-tuning: default_toast_compression=$tc"
  if [[ "$tc" == "pglz" ]]; then
    echo "pg-machine-tuning: NOTE pg built without lz4 -- TOAST uses the slowest codec." >&2
    echo "  external/CMakeLists.txt passes --with-lz4; rebuild pg to pick it up." >&2
  fi
}

# huge_pages, probed for the same reason as the two above: the choice between
# 'on' and 'try' depends on /proc/meminfo, which the emitter cannot read.
#
# 'try' fails SILENTLY -- it is why a ~31 GiB buffer pool ran on 4 KiB pages for
# months while the setting read as configured. 'on' makes the postmaster refuse
# to start when the pages are missing, which is the loud behaviour we want, but
# only once the reservation is PROVEN. Reserving the pages themselves is a host
# concern and belongs to bootstrap_pg_hugepages (sysctl vm.nr_hugepages, applied
# at boot because late allocation fails on a fragmented host). This function only
# decides which GUC that reservation justifies.
pg_apply_huge_pages() {
  local need got hp
  need=$(pg_tune_psql -tAc "SHOW shared_memory_size_in_huge_pages" 2>/dev/null | tr -dc '0-9')
  got=$(awk '/HugePages_Total/{print $2}' /proc/meminfo 2>/dev/null)
  if [[ -z "$need" || -z "$got" ]]; then
    echo "pg-machine-tuning: huge_pages unprobeable — leaving as-is" >&2
    return 0
  fi
  if (( got >= need )); then hp=on; else hp=try; fi
  pg_tune_psql -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET huge_pages = $hp"
  echo "pg-machine-tuning: huge_pages=$hp (reserved $got, need $need)"
  if [[ "$hp" == "try" ]]; then
    echo "pg-machine-tuning: NOTE only $got of $need huge pages reserved -- shared memory" >&2
    echo "  will silently fall back to 4 KiB pages. /etc/sysctl.d/60-laplace-hugepages.conf" >&2
    echo "  persists the reservation; REBOOT so it lands before memory fragments." >&2
  fi
}

# temp_tablespaces, probed because it depends on a tablespace the HOST provides.
#
# THE THIRD I/O STREAM. Sort/hash spill lands in $PGDATA/base/pgsql_tmp unless a
# temp tablespace exists — i.e. on the same device as the heap, competing with the
# very reads the spilling query is doing. That is the 2026-07-26 wiktionary
# contention (vg-data at 100% util, aqu-sz ~36) in a second costume: WAL was moved
# off the heap device, temp spill never was. With work_mem=502MB across ~26 live
# backends the seed host spills hard and drove 12.5GB into swap.
#
# Left unset when the tablespace is absent: pointing temp_tablespaces at a missing
# tablespace makes every spilling query ERROR, which is far worse than sharing a
# spindle. bootstrap_pg_tempspace creates it; this only decides whether to use it.
pg_apply_temp_tablespace() {
  local ts="${LAPLACE_PG_TEMP_TS:-pgtemp}" found
  found=$(pg_tune_psql -tAc \
    "SELECT 1 FROM pg_tablespace WHERE spcname = '$ts'" 2>/dev/null | tr -dc '0-9')
  if [[ -z "$found" ]]; then
    echo "pg-machine-tuning: tablespace '$ts' absent — temp_tablespaces left unset" >&2
    echo "  (spill stays in \$PGDATA/base/pgsql_tmp, sharing the heap device)" >&2
    return 0
  fi
  pg_tune_psql -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET temp_tablespaces = '$ts'"
  echo "pg-machine-tuning: temp_tablespaces=$ts (spill off the heap device)"
}

pg_apply_wal_compression() {
  local wc
  wc=$(pg_tune_psql -tAc \
    "SELECT CASE WHEN 'lz4' = ANY(enumvals) THEN 'lz4' WHEN 'zstd' = ANY(enumvals) THEN 'zstd' ELSE 'pglz' END FROM pg_settings WHERE name = 'wal_compression'")
  pg_tune_psql -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET wal_compression = $wc"
  echo "pg-machine-tuning: wal_compression=$wc"
  if [[ "$wc" == "pglz" ]]; then
    echo "pg-machine-tuning: NOTE pg built without lz4/zstd -- WAL uses the slowest codec." >&2
    echo "  external/CMakeLists.txt passes --with-lz4/--with-zstd; rebuild pg to pick them up" >&2
    echo "  (scripts/check-prereqs.sh reports the gap)." >&2
  fi
}

pg_apply_io_method() {
  local io
  io=$(pg_tune_psql -tAc \
    "SELECT CASE WHEN 'io_uring' = ANY(enumvals) THEN 'io_uring' ELSE 'worker' END FROM pg_settings WHERE name = 'io_method'")
  pg_tune_psql -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET io_method = $io"
  echo "pg-machine-tuning: io_method=$io"
  if [[ "$io" != "io_uring" ]]; then
    echo "pg-machine-tuning: NOTE this PostgreSQL was built without liburing, so async I/O" >&2
    echo "  is capped by the io_workers pool. Rebuilding with --with-liburing removes that" >&2
    echo "  ceiling entirely (scripts/win/build-pg.cmd / the pg build recipe)." >&2
  fi
}

pg_apply_machine_tuning() {
  local dll
  if dll="$(pg_tune_cli_dll)"; then
    if dotnet "$dll" cpu-topology --pg-tuning | pg_tune_psql -v ON_ERROR_STOP=1 -f -; then
      pg_compute_machine_tuning
      # The emitter cannot probe -- it writes SQL with no connection -- so it hardcodes
      # io_method=worker. Correct it here, on the path that actually runs.
      pg_apply_io_method
      pg_apply_wal_compression
      pg_apply_toast_compression
      pg_apply_huge_pages
      pg_apply_temp_tablespace
      pg_tune_reload
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
    -c "ALTER SYSTEM SET superuser_reserved_connections = $PG_TUNE_RESERVED" \
    -c "ALTER SYSTEM SET hash_mem_multiplier = 1.0" \
    -c "ALTER SYSTEM SET autovacuum_work_mem = '$PG_TUNE_AVWM'" \
    -c "ALTER SYSTEM SET temp_buffers = '$PG_TUNE_TEMPB'" \
    -c "ALTER SYSTEM SET shared_buffers = '$PG_TUNE_SB'" \
    -c "ALTER SYSTEM SET effective_cache_size = '$PG_TUNE_ECS'" \
    -c "ALTER SYSTEM SET maintenance_work_mem = '$PG_TUNE_MWM'" \
    -c "ALTER SYSTEM SET work_mem = '$PG_TUNE_WM'" \
    -c "ALTER SYSTEM SET max_wal_size = '$PG_TUNE_MAX_WAL'" \
    -c "ALTER SYSTEM SET min_wal_size = '$PG_TUNE_MIN_WAL'" \
    -c "ALTER SYSTEM RESET wal_buffers" \
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
    -c "ALTER SYSTEM SET synchronous_commit = off" \
    -c "ALTER SYSTEM SET io_workers = $PG_TUNE_IOW" \
    -c "ALTER SYSTEM SET max_locks_per_transaction = 1024"

  pg_apply_io_method
  pg_apply_wal_compression
  pg_apply_toast_compression
  pg_apply_huge_pages
  pg_apply_temp_tablespace
  pg_tune_reload
  echo "pg-machine-tuning: shared_buffers=$PG_TUNE_SB effective_cache_size=$PG_TUNE_ECS maintenance_work_mem=$PG_TUNE_MWM work_mem=$PG_TUNE_WM wal_buffers=$PG_TUNE_WB pcores=$PG_TUNE_PCORES pdeg=$PG_TUNE_PDEG"
}

# Returns 0 if live settings match computed machine tuning and nothing pending_restart.
# Optional: PG_TUNE_OK / PG_TUNE_BAD callbacks (default: echo).
# VALIDATE WHAT WAS APPLIED, NOT A SECOND DERIVATION OF IT.
#
# pg_apply_machine_tuning applies `cpu-topology --pg-tuning` -- the C# emitter this file's
# own header calls "the authoritative emitter", noting that the bash formulas below "survive
# ONLY as the bare-host bootstrap fallback" after the two "silently diverged on TEN settings".
#
# This function then validated against pg_compute_machine_tuning -- those same bash formulas.
# Apply from one implementation, check against another. It cannot pass whenever they differ,
# and they differ by construction: measured 2026-08-24 on hart-server, the emitter produced
# max_connections=37, work_mem=336222kB, autovacuum_work_mem=672444kB and
# temp_buffers=336216kB while the bash side wanted 53, 213959kB, 427919kB and 213960kB. Every
# one of those was reported "want machine-sized" against a cluster tuned exactly as the
# authoritative emitter asked, so setup-host.sh ended in "Tuning NOT fully live" on a
# correctly configured host.
#
# The expectations now come from the emitter when it is available, by the same rule the apply
# path uses, and fall back to the bash formulas only on a bare host where no CLI exists.
pg_load_expected_tuning() {
  local dll line name value
  if ! dll="$(pg_tune_cli_dll)"; then
    pg_compute_machine_tuning
    return 0
  fi
  # Seed from the bash formulas so any GUC the emitter does not carry keeps a value, then
  # override with what the emitter actually emitted.
  pg_compute_machine_tuning
  while IFS= read -r line; do
    [[ "$line" =~ ^ALTER[[:space:]]+SYSTEM[[:space:]]+SET[[:space:]]+([a-z_]+)[[:space:]]*=[[:space:]]*\'?([^\';]+)\'?\; ]] || continue
    name="${BASH_REMATCH[1]}"; value="${BASH_REMATCH[2]}"
    case "$name" in
      shared_buffers)                   PG_TUNE_SB="$value" ;;
      effective_cache_size)             PG_TUNE_ECS="$value" ;;
      maintenance_work_mem)             PG_TUNE_MWM="$value" ;;
      work_mem)                         PG_TUNE_WM="$value" ;;
      max_connections)                  PG_TUNE_MAXCONN="$value" ;;
      superuser_reserved_connections)   PG_TUNE_RESERVED="$value" ;;
      autovacuum_work_mem)              PG_TUNE_AVWM="$value" ;;
      temp_buffers)                     PG_TUNE_TEMPB="$value" ;;
      checkpoint_timeout)               PG_TUNE_CHECKPOINT="$value" ;;
      max_parallel_maintenance_workers) PG_TUNE_PDEG="$value" ;;
      effective_io_concurrency)         PG_TUNE_IO_CONC="$value" ;;
      max_wal_size)                     PG_TUNE_MAX_WAL="$value" ;;
      min_wal_size)                     PG_TUNE_MIN_WAL="$value" ;;
    esac
  done < <(dotnet "$dll" cpu-topology --pg-tuning 2>/dev/null)
}

pg_validate_machine_tuning() {
  pg_load_expected_tuning
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
  ('max_connections','${PG_TUNE_MAXCONN}','eq'),
  ('superuser_reserved_connections','${PG_TUNE_RESERVED}','eq'),
  ('hash_mem_multiplier','1','eq'),
  ('autovacuum_work_mem','${PG_TUNE_AVWM}','mem'),
  ('temp_buffers','${PG_TUNE_TEMPB}','mem'),
  ('synchronous_commit','off','eq'),
  ('checkpoint_timeout','${PG_TUNE_CHECKPOINT}','eq'),
  ('wal_compression','on','enabled'),
  ('max_parallel_maintenance_workers','${PG_TUNE_PDEG}','eq'),
  ('effective_io_concurrency','${PG_TUNE_IO_CONC}','eq'),
  ('max_locks_per_transaction','1024','eq'),
  -- 'enabled' (<> off), NOT eq 'try': pg_apply_huge_pages promotes this to 'on'
  -- once the reservation is proven to cover shared_memory_size_in_huge_pages.
  -- Pinning it to 'try' made a successful promotion read as a validation FAILURE.
  ('huge_pages','on','enabled'))
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
