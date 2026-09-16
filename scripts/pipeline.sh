#!/usr/bin/env bash
# Laplace build/install/runtime operator. Real tools own incremental execution:
# CMake/Ninja for native code and MSBuild for managed code. This script sequences
# those operations; it does not invent a second build/proof state machine.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
umask 0002

# shellcheck source=scripts/lib/storage.sh
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init
LAPLACE_BUILD_DIRECTORY=$(python3 "$ROOT/scripts/place-build-directory.py" "$ROOT")
export LAPLACE_BUILD_DIRECTORY

# These helpers resolve configured corpus paths and source identity. They are not
# execution gates: fp_check deliberately never authorizes skipping work.
# shellcheck source=scripts/lib/fp.sh
source "$ROOT/scripts/lib/fp.sh"

LAPLACE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
LAPLACE_EXTERNAL="${LAPLACE_EXTERNAL:-/build/external}"

PYTHON="$(command -v python3 || command -v python || true)"
[[ -n "$PYTHON" ]] || { echo "::error::python3 is required by source/code generators" >&2; exit 127; }

cmake_bin=$("$PYTHON" "$ROOT/scripts/provision-cmake.py" \
  --root "$LAPLACE_INSTALL_PREFIX/tools/cmake" \
  --work "${LAPLACE_WORK_ROOT:-/build/laplace/work}/cmake" --ensure)
export PATH="$cmake_bin:$PATH"

[[ -x "$LAPLACE_PG_PREFIX/bin/psql" && -x "$LAPLACE_PG_PREFIX/bin/pg_config" ]] || {
  echo "::error::Laplace PostgreSQL toolchain missing under $LAPLACE_PG_PREFIX/bin" >&2
  exit 127
}
export PATH="$LAPLACE_PG_PREFIX/bin:$PATH"
LAPLACE_PG_MAJOR="${LAPLACE_PG_MAJOR:-$("$LAPLACE_PG_PREFIX/bin/pg_config" --version | sed -E 's/^PostgreSQL ([0-9]+).*/\1/')}"
[[ "$LAPLACE_PG_MAJOR" =~ ^[0-9]+$ ]] || { echo "::error::cannot derive PostgreSQL major" >&2; exit 1; }
LAPLACE_EXT_SHAREDIR="$LAPLACE_INSTALL_PREFIX/share/postgresql/$LAPLACE_PG_MAJOR/extension"
LAPLACE_EXT_LIBDIR="$LAPLACE_INSTALL_PREFIX/lib/postgresql/$LAPLACE_PG_MAJOR"

export PGHOST="${PGHOST:-/var/run/postgresql}"
export PGUSER="${PGUSER:-laplace_admin}"
export PGDATABASE="${PGDATABASE:-laplace}"

FRESH_DB=0
FORCE_FOUNDATION=0
FORCE_CODEGEN=0
SKIP_CODEGEN=0
CLEAN_FIRST=0
FORCE_REBUILD=0
SERIAL_TESTS=0

nproc_n="$(nproc 2>/dev/null || echo 1)"
export CMAKE_BUILD_PARALLEL_LEVEL="${CMAKE_BUILD_PARALLEL_LEVEL:-$nproc_n}"
export CTEST_PARALLEL_LEVEL="${CTEST_PARALLEL_LEVEL:-$([[ "${LAPLACE_TEST_SERIAL:-0}" == 1 ]] && echo 1 || echo "$nproc_n")}"

usage() {
  cat <<'EOF'
Usage: pipeline.sh <phase> [<phase> ...] [options]

Phases: clean codegen build install migrate sync-extension tune-pg tune-laplace
        perfcache-guc api-env publish foundation test
Options:
  --fresh-db --force --force-codegen --skip-codegen --clean-first
  --force-rebuild --serial-tests
EOF
  exit 2
}

psql() {
  if [[ "$(id -u)" == 0 ]]; then
    [[ "$PGHOST" == /* ]] || { echo "::error::root SQL requires local socket" >&2; return 2; }
    runuser -u laplace-runner -- env \
      -u PGPASSWORD -u PGPASSFILE -u PGSERVICE -u PGSERVICEFILE -u PGHOSTADDR \
      "PGHOST=$PGHOST" "PGPORT=${PGPORT:-5432}" "PGUSER=laplace_admin" \
      "PGDATABASE=$PGDATABASE" "PGOPTIONS=${PGOPTIONS:-}" \
      "$LAPLACE_PG_PREFIX/bin/psql" -X -w "$@"
  else
    command "$LAPLACE_PG_PREFIX/bin/psql" -X -w "$@"
  fi
}

preloaded_so_digest() {
  local library
  for library in \
    "$LAPLACE_EXT_LIBDIR/laplace_substrate.so" \
    "$LAPLACE_EXT_LIBDIR/laplace_geom.so" \
    "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so" \
    "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_dynamics.so" \
    "$LAPLACE_INSTALL_PREFIX/share/laplace/laplace_chess_position_perfcache.bin" \
    "$LAPLACE_INSTALL_PREFIX/share/laplace/laplace_chess_transition_perfcache.bin"; do
    [[ ! -f "$library" ]] || sha256sum "$library"
  done | sha256sum | cut -d' ' -f1
}

ensure_extension_library_path() {
  local current desired desired_sql part reloaded
  local -a current_parts desired_parts
  current=$(psql -d postgres -U laplace_admin -tAX -c "SHOW dynamic_library_path") || return 2
  [[ -n "$current" ]] || return 2
  desired_parts=("$LAPLACE_EXT_LIBDIR" '$libdir')
  IFS=: read -r -a current_parts <<<"$current"
  for part in "${current_parts[@]}"; do
    [[ -z "$part" || "$part" == "$LAPLACE_EXT_LIBDIR" || "$part" == '$libdir' ]] && continue
    desired_parts+=("$part")
  done
  desired=$(IFS=:; echo "${desired_parts[*]}")
  [[ "$current" != "$desired" ]] || return 1
  desired_sql=${desired//\'/\'\'}
  reloaded=$(psql -d postgres -U laplace_admin -qtAX -v ON_ERROR_STOP=1 \
    -c "ALTER SYSTEM SET dynamic_library_path = '$desired_sql'" \
    -c "SELECT pg_reload_conf()") || return 2
  [[ "$reloaded" == t ]] || return 2
  echo "dynamic_library_path: '$current' -> '$desired'"
  return 0
}

postgresql_restart_required() {
  local running rc
  running=$(psql -d postgres -U laplace_admin -tAc "SHOW server_version_num") || return 2
  if "$PYTHON" "$ROOT/scripts/postgresql-release.py" restart-needed \
       --prefix "$LAPLACE_PG_PREFIX" --server-version-num "$running"; then
    return 1
  fi
  rc=$?
  [[ "$rc" -eq 3 ]] && return 0
  return 2
}

restart_postgres() {
  local reason="$1" datadir pidfile oldpid="" tries=0 newpid="" still
  datadir=$(psql -d postgres -U laplace_admin -tAc "SHOW data_directory")
  pidfile="$datadir/postmaster.pid"
  oldpid=$(head -1 "$pidfile" 2>/dev/null || true)
  if [[ -z "$oldpid" ]]; then
    oldpid=$(systemctl show -p MainPID --value laplace-postgresql.service 2>/dev/null || true)
    [[ "$oldpid" != 0 ]] || oldpid=""
  fi
  if [[ -n "$oldpid" ]] && kill -0 "$oldpid" 2>/dev/null; then
    echo "restart_postgres ($reason): SIGINT $oldpid"
    kill -INT "$oldpid"
  else
    local unit=laplace-postgresql.service
    if ! sudo -n systemctl restart "$unit" 2>/dev/null; then
      unit=$(systemctl list-units --type=service --state=running --plain --no-legend 'postgres*' '*postgres*' 2>/dev/null | awk '{print $1}' | head -1)
      [[ -n "$unit" ]] && sudo -n systemctl restart "$unit" || {
        echo "::error::cannot restart PostgreSQL for $reason" >&2; return 1;
      }
    fi
  fi
  until {
    newpid=$(head -1 "$pidfile" 2>/dev/null || true)
    [[ -n "$newpid" && "$newpid" != "$oldpid" ]]
  } && psql -d postgres -U laplace_admin -tAc "SELECT 1" >/dev/null 2>&1; do
    tries=$((tries + 1))
    (( tries <= 120 )) || { echo "::error::PostgreSQL did not return after restart" >&2; return 1; }
    sleep 1
  done
  still=$(psql -d postgres -U laplace_admin -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart")
  [[ "$still" == 0 ]] || { echo "::error::$still PostgreSQL settings remain pending restart" >&2; return 1; }
}

phase_clean() {
  echo "===== PHASE — CLEAN ====="
  rm -rf "$LAPLACE_BUILD_DIRECTORY"
  mkdir -p "$LAPLACE_BUILD_DIRECTORY"
  find "$ROOT/extension/laplace_substrate/sql/generated" -name '[0-9]*_*.sql.in' -delete 2>/dev/null || true
}

phase_codegen() {
  echo "===== PHASE — CODEGEN ====="
  [[ "$SKIP_CODEGEN" != 1 ]] || { echo "codegen skipped by explicit request"; return 0; }
  if [[ "$FORCE_CODEGEN" == 1 ]]; then
    "$PYTHON" "$ROOT/scripts/codegen-attestation-law.py"
  else
    # The generator is deterministic and cheap relative to the native build; run
    # it directly instead of maintaining a second mtime/stamp authority.
    "$PYTHON" "$ROOT/scripts/codegen-attestation-law.py"
  fi
}

phase_build_app() {
  echo "===== PHASE — BUILD APP ====="
  ( cd "$ROOT/app" && dotnet build Laplace.slnx -c Release )
}

phase_build() {
  [[ "$FORCE_REBUILD" != 1 ]] || phase_clean
  [[ "$SKIP_CODEGEN" == 1 ]] || phase_codegen
  echo "===== PHASE — BUILD ENGINE + EXTENSIONS ====="
  local data_root="${LAPLACE_DATA_ROOT:-/vault/Data}"
  local ucd="${LAPLACE_UCD_PATH:-$data_root/UCD/Public/UCD/latest}"
  local chess_openings chess_corpus_export
  chess_openings=$(fp_chess_openings_path)
  chess_corpus_export=$("$PYTHON" "$ROOT/scripts/chess-floor-artifacts.py" selected-export \
    --prefix "$LAPLACE_INSTALL_PREFIX" --path-only)
  local build_flags=()
  [[ "$CLEAN_FIRST" != 1 ]] || build_flags+=(--clean-first)
  cmake -S "$ROOT" -B "$LAPLACE_BUILD_DIRECTORY" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
    -DLAPLACE_REQUIRE_MKL=ON \
    -DCMAKE_INSTALL_PREFIX="$LAPLACE_INSTALL_PREFIX" \
    -DLAPLACE_PG_PREFIX="$LAPLACE_PG_PREFIX" \
    -DLAPLACE_EXTERNAL="$LAPLACE_EXTERNAL" \
    -DLAPLACE_INSTALL_STAGED=ON \
    -DLAPLACE_UCD_PATH="$ucd" \
    -DLAPLACE_UCDXML_ZIP="$ucd/ucdxml/ucd.nounihan.flat.zip" \
    -DLAPLACE_DUCET_FILE="$ucd/uca/allkeys.txt" \
    -DLAPLACE_UCD_CONFORMANCE_DIR="$ucd/ucd" \
    -DLAPLACE_CHESS_OPENINGS="$chess_openings" \
    -DLAPLACE_CHESS_CORPUS_EXPORT="$chess_corpus_export"
  LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}" \
    cmake --build "$LAPLACE_BUILD_DIRECTORY" "${build_flags[@]}"

  local t0 hw
  t0=$(find -H "$ROOT/build" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | head -1 || true)
  hw=$(find -H "$ROOT/build" -name 'laplace_highway_perfcache*.bin' 2>/dev/null | head -1 || true)
  [[ -n "$t0" && -n "$hw" ]] || { echo "::error::required perfcache output missing" >&2; return 1; }
  if grep -q 'add_custom_target(laplace_chess_position_perfcache' "$ROOT/engine/core/CMakeLists.txt"; then
    test -n "$(find -H "$ROOT/build" -name 'laplace_chess_position_perfcache*.bin' -print -quit)" || {
      echo "::error::declared chess position perfcache was not produced" >&2; return 1;
    }
    test -n "$(find -H "$ROOT/build" -name 'laplace_chess_transition_perfcache*.bin' -print -quit)" || {
      echo "::error::declared chess transition perfcache was not produced" >&2; return 1;
    }
  fi
  phase_build_app
  fp_record build-native "$(fp_native)"
}

phase_test() {
  echo "===== PHASE — TEST ====="
  local args=()
  [[ "$SERIAL_TESTS" != 1 && "${LAPLACE_TEST_SERIAL:-0}" != 1 ]] || args+=(--serial)
  bash "$ROOT/scripts/test-parallel.sh" "${args[@]}"
}

phase_install() {
  echo "===== PHASE — INSTALL ====="
  [[ -f "$LAPLACE_BUILD_DIRECTORY/build.ninja" ]] || {
    echo "::error::native build tree missing; run pipeline.sh build first" >&2; return 1;
  }
  # Let Ninja prove the native tree is current instead of trusting a custom skip stamp.
  cmake --build "$LAPLACE_BUILD_DIRECTORY"

  local library_path_changed=0 server_release_changed=0 path_rc server_rc
  if postgresql_restart_required; then server_release_changed=1; else server_rc=$?; [[ "$server_rc" == 1 ]] || return "$server_rc"; fi
  if ensure_extension_library_path; then library_path_changed=1; else path_rc=$?; [[ "$path_rc" == 1 ]] || return "$path_rc"; fi

  local api_was_active=0 so_before so_after postgres_activation_required
  systemctl is-active --quiet laplace-api 2>/dev/null && api_was_active=1 || true
  [[ "$api_was_active" != 1 ]] || sudo -n systemctl stop laplace-api
  trap 'rc=$?; trap - RETURN; [[ "$api_was_active" != 1 ]] || sudo -n systemctl start laplace-api || rc=1; return "$rc"' RETURN

  so_before=$(preloaded_so_digest)
  cmake --install "$LAPLACE_BUILD_DIRECTORY"
  [[ -f "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so" ]] || { echo "::error::core library not installed" >&2; return 1; }
  so_after=$(preloaded_so_digest)
  postgres_activation_required="$server_release_changed"
  if [[ "$so_before" != "$so_after" || "$library_path_changed" == 1 ]]; then
    local preload
    preload=$(psql -d postgres -U laplace_admin -tAc "SHOW shared_preload_libraries")
    if [[ ",${preload// /}," == *",laplace_substrate,"* || ",${preload// /}," == *",laplace_geom,"* ]]; then
      postgres_activation_required=1
    fi
  fi
  [[ "$postgres_activation_required" != 1 ]] || restart_postgres "installed native/PostgreSQL image changed"
  if postgresql_restart_required; then
    echo "::error::running PostgreSQL release still differs after install" >&2; return 1
  else
    server_rc=$?; [[ "$server_rc" == 1 ]] || return "$server_rc"
  fi
  [[ "$api_was_active" != 1 ]] || { sudo -n systemctl start laplace-api; api_was_active=0; }
  trap - RETURN
}

phase_migrate() {
  echo "===== PHASE — MIGRATE ($PGDATABASE) ====="
  local mig
  if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
    mig="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Migrations/Release/net10.0/Laplace.Migrations.dll"
  else
    mig="$ROOT/app/Laplace.Migrations/bin/Release/net10.0/Laplace.Migrations.dll"
  fi
  if [[ ! -f "$mig" ]]; then
    [[ "${LAPLACE_REQUIRE_PREBUILT_MIGRATIONS:-0}" != 1 ]] || {
      echo "::error::prebuilt migration runtime missing; build/deploy owns compilation" >&2; return 1;
    }
    dotnet build "$ROOT/app/Laplace.Migrations/Laplace.Migrations.csproj" -c Release
  fi
  [[ -f "$mig" ]] || { echo "::error::migration runtime missing after build" >&2; return 1; }
  [[ "$FRESH_DB" != 1 ]] || dotnet "$mig" nuke --yes
  dotnet "$mig" up
}

alter_extension_update() {
  local ext="$1" avail="$2" rc=0 log
  log=$(mktemp "${LAPLACE_WORK_ROOT:-/build/laplace/work}/alter-extension.XXXXXX")
  PGOPTIONS="-c lock_timeout=${LAPLACE_DDL_LOCK_TIMEOUT:-20s}" \
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "ALTER EXTENSION $ext UPDATE TO '$avail'" >"$log" 2>&1 || rc=$?
  if [[ "$rc" != 0 ]]; then
    cat "$log" >&2
    if grep -q 'lock timeout\|canceling statement due to lock timeout' "$log"; then
      psql -d "$PGDATABASE" -U laplace_admin -P pager=off -c \
        "SELECT pid,state,wait_event_type,wait_event,now()-COALESCE(xact_start,query_start) AS held,left(query,120) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND state<>'idle' ORDER BY COALESCE(xact_start,query_start)" >&2 || true
    fi
  fi
  rm -f "$log"
  return "$rc"
}

sync_one_extension() {
  local ext="$1" avail installed bridge
  avail=$(psql -d "$PGDATABASE" -U laplace_admin -tAX -c "SELECT default_version FROM pg_available_extensions WHERE name='$ext'" | tr -d '[:space:]')
  installed=$(psql -d "$PGDATABASE" -U laplace_admin -tAX -c "SELECT extversion FROM pg_extension WHERE extname='$ext'" | tr -d '[:space:]')
  [[ -n "$avail" ]] || { echo "::error::$ext is not installed on the server" >&2; return 1; }
  if [[ -z "$installed" ]]; then
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS $ext CASCADE"
    return
  fi
  [[ "$installed" != "$avail" ]] || { echo "$ext already at $avail"; return; }
  bridge="$LAPLACE_EXT_SHAREDIR/$ext--${installed}--${avail}.sql"
  install -m 664 "$LAPLACE_EXT_SHAREDIR/${ext}_upgrade.sql" "$bridge"
  alter_extension_update "$ext" "$avail"
}

phase_sync_extension() {
  echo "===== PHASE — SYNC EXTENSION SQL ====="
  sync_one_extension laplace_geom
  local avail installed bridge log rc=0
  avail=$(psql -d "$PGDATABASE" -U laplace_admin -tAX -c "SELECT default_version FROM pg_available_extensions WHERE name='laplace_substrate'" | tr -d '[:space:]')
  installed=$(psql -d "$PGDATABASE" -U laplace_admin -tAX -c "SELECT extversion FROM pg_extension WHERE extname='laplace_substrate'" | tr -d '[:space:]')
  [[ -n "$avail" ]] || { echo "::error::laplace_substrate not available" >&2; return 1; }
  if [[ -z "$installed" ]]; then
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS laplace_substrate"
  elif [[ "$installed" != "$avail" ]]; then
    bridge="$LAPLACE_EXT_SHAREDIR/laplace_substrate--${installed}--${avail}.sql"
    install -m 664 "$LAPLACE_EXT_SHAREDIR/laplace_substrate_upgrade.sql" "$bridge"
    log=$(mktemp "${LAPLACE_WORK_ROOT:-/build/laplace/work}/substrate-update.XXXXXX")
    PGOPTIONS="-c lock_timeout=${LAPLACE_DDL_LOCK_TIMEOUT:-20s}" \
      psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
        -c "ALTER EXTENSION laplace_substrate UPDATE TO '$avail'" >"$log" 2>&1 || rc=$?
    if [[ "$rc" != 0 ]] && grep -q 'could not find function\|could not load library\|undefined symbol' "$log" \
       && ! grep -q 'laplace_execution_[0-9a-f]' "$log"; then
      cat "$log" >&2
      restart_postgres "preloaded substrate image is stale for new SQL"
      rc=0
      PGOPTIONS="-c lock_timeout=${LAPLACE_DDL_LOCK_TIMEOUT:-20s}" \
        psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
          -c "ALTER EXTENSION laplace_substrate UPDATE TO '$avail'" || rc=$?
    fi
    if [[ "$rc" != 0 ]]; then cat "$log" >&2; rm -f "$log"; return "$rc"; fi
    rm -f "$log"
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 -c \
      "CALL chess.repair_player_ratings(laplace.relation_type_id('OUTCOME'),laplace.relation_type_id('PLAYED_BY'),laplace.relation_type_id('HAS_RATING'))"
  fi
}

phase_tune_pg() {
  echo "===== PHASE — TUNE PG ====="
  # shellcheck source=scripts/pg-machine-tuning.sh
  source "$ROOT/scripts/pg-machine-tuning.sh"
  PG_TUNE_PSQL=(psql -d "$PGDATABASE" -U laplace_admin)
  pg_apply_machine_tuning
  local pending
  pending=$(pg_tune_psql -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart")
  [[ "$pending" == 0 ]] || restart_postgres "machine tuning requires restart"
}

phase_tune_laplace() {
  echo "===== PHASE — TUNE LAPLACE ====="
  local have
  have=$(psql -d "$PGDATABASE" -U laplace_admin -tAc "SELECT to_regclass('laplace.physicalities') IS NOT NULL" 2>/dev/null || echo f)
  [[ "$have" == t ]] || { echo "substrate tables absent; tuning skipped"; return 0; }
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 <<'SQL'
ALTER TABLE laplace.physicalities ALTER COLUMN coord SET STATISTICS 0;
ALTER TABLE laplace.physicalities ALTER COLUMN trajectory SET STATISTICS 0;
DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT roots.t AS root, relid AS rel
    FROM (VALUES ('laplace.entities'::regclass), ('laplace.physicalities'),
                 ('laplace.attestations'), ('laplace.consensus')) roots(t),
         LATERAL pg_partition_tree(roots.t)
    WHERE isleaf
  LOOP
    EXECUTE format('ALTER TABLE %s SET (autovacuum_analyze_scale_factor = 0.02, autovacuum_analyze_threshold = 100000)', r.rel);
    IF r.root = 'laplace.physicalities'::regclass THEN
      EXECUTE format('ALTER TABLE %s ALTER COLUMN coord SET STATISTICS 0', r.rel);
      EXECUTE format('ALTER TABLE %s ALTER COLUMN trajectory SET STATISTICS 0', r.rel);
    END IF;
  END LOOP;
END $$;
SQL
}

phase_perfcache_guc() {
  echo "===== PHASE — PERFCACHE GUC ====="
  local dir="$LAPLACE_INSTALL_PREFIX/share/laplace" bin hwbin chessbin transition preload newval
  bin=$(find "$dir" -name 'laplace_t0_perfcache*.bin' | sort -V | tail -1)
  hwbin=$(find "$dir" -name 'laplace_highway_perfcache*.bin' | sort -V | tail -1)
  chessbin=$(find "$dir" -name 'laplace_chess_position_perfcache*.bin' | sort -V | tail -1)
  transition=$(find "$dir" -name 'laplace_chess_transition_perfcache*.bin' | sort -V | tail -1)
  [[ -n "$bin" && -n "$hwbin" && -n "$chessbin" && -n "$transition" ]] || {
    echo "::error::installed perfcache set incomplete" >&2; return 1;
  }
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "LOAD 'laplace_substrate'" \
    -c "ALTER SYSTEM SET laplace_substrate.perfcache_path = '$bin'" \
    -c "ALTER SYSTEM SET laplace_substrate.highway_perfcache_path = '$hwbin'" \
    -c "ALTER SYSTEM SET laplace_substrate.chess_position_perfcache_path = '$chessbin'" \
    -c "SELECT pg_reload_conf()"
  preload=$(psql -d "$PGDATABASE" -U laplace_admin -tAc "SHOW shared_preload_libraries")
  if [[ ",${preload// /}," != *",laplace_substrate,"* ]]; then
    newval=laplace_substrate
    [[ -z "$preload" ]] || newval="$preload,laplace_substrate"
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 -c "ALTER SYSTEM SET shared_preload_libraries = '$newval'"
    restart_postgres "enable substrate preload"
  fi
}

phase_api_env() {
  echo "===== PHASE — API ENV ====="
  local env_file="$LAPLACE_INSTALL_PREFIX/app/laplace-api.env" bin example ops_log_dir
  bin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' | sort -V | tail -1)
  [[ -n "$bin" ]] || { echo "::error::installed t0 perfcache missing" >&2; return 1; }
  example="$ROOT/deploy/linux/laplace-api.env.example"
  if [[ ! -f "$env_file" ]]; then
    install -m 0640 -o laplace-runner -g laplace-runner "$example" "$env_file" 2>/dev/null || cp "$example" "$env_file"
  fi
  if grep -q '^LAPLACE_PERFCACHE_BIN=' "$env_file"; then sed -i "s|^LAPLACE_PERFCACHE_BIN=.*|LAPLACE_PERFCACHE_BIN=$bin|" "$env_file"; else printf '\nLAPLACE_PERFCACHE_BIN=%s\n' "$bin" >>"$env_file"; fi
  local api_db="LAPLACE_DB=Host=/var/run/postgresql;Username=laplace_admin;Database=$PGDATABASE"
  if grep -q '^LAPLACE_DB=' "$env_file"; then sed -i "s|^LAPLACE_DB=.*|$api_db|" "$env_file"; else printf '\n%s\n' "$api_db" >>"$env_file"; fi
  ops_log_dir="${LAPLACE_OPS_LOG_DIR:-$LAPLACE_INSTALL_PREFIX/app/logs}"
  mkdir -p "$ops_log_dir"; chmod 2775 "$ops_log_dir" 2>/dev/null || true
  if grep -q '^LAPLACE_OPS_LOG_DIR=' "$env_file"; then sed -i "s|^LAPLACE_OPS_LOG_DIR=.*|LAPLACE_OPS_LOG_DIR=$ops_log_dir|" "$env_file"; else printf '\nLAPLACE_OPS_LOG_DIR=%s\n' "$ops_log_dir" >>"$env_file"; fi
  sed -i '/^LAPLACE_LOG_DIR=/d' "$env_file"
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "SELECT ops.repoint_app_log('$ops_log_dir'); SELECT ops.repoint_chess_drops('$ops_log_dir');"
}

phase_chess_lab() {
  echo "===== PHASE — CHESS LAB ====="
  bash "$ROOT/scripts/bootstrap-chess-lab.sh" --cutechess-gui
}

phase_runtime_secrets() {
  echo "===== PHASE — RUNTIME SECRETS ====="
  local dir="$LAPLACE_INSTALL_PREFIX/secrets" in_ci=0 missing=0 dst value name secret
  mkdir -p "$dir/data-protection"
  chmod 2770 "$dir" "$dir/data-protection" 2>/dev/null || true
  [[ -z "${GITHUB_ACTIONS:-}" ]] || in_ci=1

  for name in mcp operator; do
    [[ "$name" == mcp ]] && secret=LAPLACE_MCP_TOKEN || secret=LAPLACE_OPERATOR_TOKEN
    value="${!secret:-}"
    dst="$dir/$name.env"
    if [[ -n "$value" ]]; then
      [[ "$value" =~ ^[A-Za-z0-9_=/+-]{32,}$ ]] || { echo "::error::$secret is invalid" >&2; missing=1; continue; }
      printf '%s=%s\n' "$secret" "$value" >"$dst.tmp"; chmod 640 "$dst.tmp"; mv "$dst.tmp" "$dst"
    elif [[ "$in_ci" == 1 || ! -s "$dst" ]]; then
      echo "::error::$secret is required" >&2; missing=1
    fi
  done

  local tok="${LICHESS_API:-${LICHESS_TOKEN:-}}"
  dst="$dir/lichess.env"
  if [[ -n "$tok" ]]; then
    printf 'LICHESS_API=%s\nLICHESS_TOKEN=%s\n' "$tok" "$tok" >"$dst.tmp"; chmod 640 "$dst.tmp"; mv "$dst.tmp" "$dst"
  elif [[ "$in_ci" == 1 ]]; then echo "::error::LICHESS_API is required" >&2; missing=1; fi

  local stripe="${STRIPE_API_SECRET:-${LAPLACE_STRIPE_API_KEY:-}}" whsec="${STRIPE_WEBHOOK_SECRET:-${LAPLACE_STRIPE_WEBHOOK_SECRET:-}}"
  dst="$dir/stripe.env"
  if [[ -n "$stripe" ]]; then
    { printf 'STRIPE_API_SECRET=%s\n' "$stripe"; [[ -z "$whsec" ]] || printf 'STRIPE_WEBHOOK_SECRET=%s\n' "$whsec"; [[ -z "${STRIPE_API_Publishable:-${STRIPE_API_PUBLISHABLE:-}}" ]] || printf 'STRIPE_API_Publishable=%s\n' "${STRIPE_API_Publishable:-$STRIPE_API_PUBLISHABLE}"; } >"$dst.tmp"
    chmod 640 "$dst.tmp"; mv "$dst.tmp" "$dst"
  elif [[ "$in_ci" == 1 ]]; then echo "::error::STRIPE_API_SECRET is required" >&2; missing=1; fi

  local mid="${LAPLACE_AUTH_MICROSOFT_CLIENT_ID:-}" msecret="${LAPLACE_AUTH_MICROSOFT_CLIENT_SECRET:-}"
  local gid="${LAPLACE_AUTH_GOOGLE_CLIENT_ID:-}" gsecret="${LAPLACE_AUTH_GOOGLE_CLIENT_SECRET:-}"
  [[ -z "$mid" == -z "$msecret" ]] || { echo "::error::Microsoft OAuth client id/secret must be paired" >&2; missing=1; }
  [[ -z "$gid" == -z "$gsecret" ]] || { echo "::error::Google OAuth client id/secret must be paired" >&2; missing=1; }
  dst="$dir/identity.env"
  {
    [[ -z "$mid" ]] || printf 'LAPLACE_AUTH_MICROSOFT_CLIENT_ID=%s\nLAPLACE_AUTH_MICROSOFT_CLIENT_SECRET=%s\n' "$mid" "$msecret"
    [[ -z "$gid" ]] || printf 'LAPLACE_AUTH_GOOGLE_CLIENT_ID=%s\nLAPLACE_AUTH_GOOGLE_CLIENT_SECRET=%s\n' "$gid" "$gsecret"
  } >"$dst.tmp"
  chmod 640 "$dst.tmp"; mv "$dst.tmp" "$dst"
  [[ "$missing" == 0 ]]
}

phase_publish() {
  echo "===== PHASE — PUBLISH ====="
  local app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  # shellcheck source=deploy/linux/app-dir-contract.sh
  source "$ROOT/deploy/linux/app-dir-contract.sh"
  laplace_reconcile_app_dir_contract "$app_dir"
  bash "$ROOT/deploy/linux/managed-publish.sh" begin
  phase_chess_lab
  phase_runtime_secrets
  local deploy_args=()
  [[ "${LAPLACE_FORCE_NPM:-0}" != 1 ]] || deploy_args+=(--force-npm)
  [[ "${LAPLACE_PUBLISH_SERIAL:-0}" != 1 ]] || deploy_args+=(--serial)
  LAPLACE_MANAGED_TRANSACTION=1 bash "$ROOT/deploy/linux/deploy.sh" "${deploy_args[@]}"
  bash "$ROOT/deploy/linux/managed-publish.sh" reconcile
}

phase_foundation() {
  echo "===== PHASE — FOUNDATION ====="
  local args=()
  [[ "$FORCE_FOUNDATION" != 1 ]] || args+=(--force)
  LAPLACE_DBNAME="$PGDATABASE" bash "$ROOT/scripts/ensure-foundation.sh" "${args[@]}"
}

PHASES=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fresh-db) FRESH_DB=1; shift ;;
    --force) FORCE_FOUNDATION=1; shift ;;
    --force-codegen) FORCE_CODEGEN=1; shift ;;
    --skip-codegen) SKIP_CODEGEN=1; shift ;;
    --clean-first) CLEAN_FIRST=1; shift ;;
    --force-rebuild) FORCE_REBUILD=1; shift ;;
    --serial-tests) SERIAL_TESTS=1; export LAPLACE_TEST_SERIAL=1; shift ;;
    --force-all) shift ;; # accepted for old operator commands; no skip gates remain
    -h|--help) usage ;;
    clean|codegen|build|install|migrate|sync-extension|tune-pg|tune-laplace|perfcache-guc|api-env|publish|foundation|test)
      PHASES+=("$1"); shift ;;
    *) echo "unknown argument: $1" >&2; usage ;;
  esac
done
[[ ${#PHASES[@]} -gt 0 ]] || usage

for phase in "${PHASES[@]}"; do
  case "$phase" in
    clean) phase_clean ;;
    codegen) phase_codegen ;;
    build) phase_build ;;
    install) phase_install ;;
    migrate) phase_migrate ;;
    sync-extension) phase_sync_extension ;;
    tune-pg) phase_tune_pg ;;
    tune-laplace) phase_tune_laplace ;;
    perfcache-guc) phase_perfcache_guc ;;
    api-env) phase_api_env ;;
    publish) phase_publish ;;
    foundation) phase_foundation ;;
    test) phase_test ;;
  esac
done

echo "===== PIPELINE PHASES COMPLETE: ${PHASES[*]} ====="
