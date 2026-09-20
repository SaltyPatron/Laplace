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

LAPLACE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
LAPLACE_EXTERNAL="${LAPLACE_EXTERNAL:-/build/external}"

PYTHON="$(command -v python3 || command -v python || true)"
[[ -n "$PYTHON" ]] || { echo "::error::python3 is required by source/code generators" >&2; exit 127; }

cmake_args=(
  --root "$LAPLACE_INSTALL_PREFIX/tools/cmake"
  --work "${LAPLACE_WORK_ROOT:-/build/laplace/work}/cmake"
)
[[ "${LAPLACE_PROVISION_TOOLS:-0}" != 1 ]] || cmake_args+=(--ensure)
cmake_bin=$("$PYTHON" "$ROOT/scripts/provision-cmake.py" "${cmake_args[@]}")
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
CLEAN_FIRST=0
FORCE_REBUILD=0
SERIAL_TESTS=0

nproc_n="$(nproc 2>/dev/null || echo 1)"
export CMAKE_BUILD_PARALLEL_LEVEL="${CMAKE_BUILD_PARALLEL_LEVEL:-$nproc_n}"
if [[ -z "${CTEST_PARALLEL_LEVEL:-}" ]]; then
  if [[ "${LAPLACE_TEST_SERIAL:-0}" == 1 ]]; then
    export CTEST_PARALLEL_LEVEL=1
  else
    export CTEST_PARALLEL_LEVEL="$nproc_n"
  fi
fi

usage() {
  cat <<'EOF'
Usage: pipeline.sh <phase> [<phase> ...] [options]

Phases: clean codegen build build-native build-app build-web install activate-postgres migrate sync-extension tune-pg tune-laplace
        perfcache-guc api-env publish foundation test
Options:
  --fresh-db --force --force-codegen --clean-first --force-rebuild --serial-tests
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

chess_openings_path() {
  if [[ -n "${LAPLACE_CHESS_OPENINGS:-}" ]]; then
    printf '%s\n' "$LAPLACE_CHESS_OPENINGS"
    return
  fi
  local chess_root="${LAPLACE_DATA_ROOT:-/vault/Data}/Games/Chess" candidate
  for candidate in "$chess_root/lichess-openings" "$chess_root/openings"; do
    if [[ -d "$candidate" ]] && [[ -n "$(find -H "$candidate" -type f -name '*.tsv' -print -quit)" ]]; then
      printf '%s\n' "$candidate"
      return
    fi
  done
  printf '%s\n' "$chess_root/lichess-openings"
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
  else
    rc=$?
  fi
  [[ "$rc" -eq 3 ]] && return 0
  return 2
}

restart_postgres() {
  local reason="$1" unit=laplace-postgresql.service oldpid="" newpid="" tries=0 still
  bash "$ROOT/scripts/wait-for-quiet-substrate.sh" "$PGDATABASE"
  # systemd owns the postmaster. SIGINT against the pid is a clean exit, so
  # systemd does not restart it — CI then waits 120s and dies. Every native
  # install that replaces a mapped .so must bounce the unit, not the process.
  # Changing this file plans install + live-floor/live-api only. It does not
  # rebuild or publish API, MCP, UCI, Lichess, UI, or chess lab.
  # Empty planner suite lists must not default back to the managed-dev ocean.
  # A missing /opt/laplace/app receipt must not force BUILD_COMPONENTS=all.
  # A missing SPA receipt must not force build-web on a native-only SHA.
  # Native install delivery must not require application publication.
  # live-floor is engine floor (extension + T0 GUC), not complete CILI/WordNet.
  oldpid=$(systemctl show -p MainPID --value "$unit" 2>/dev/null || true)
  [[ "$oldpid" != 0 ]] || oldpid=""
  echo "restart_postgres ($reason): systemctl restart $unit (was pid ${oldpid:-none})"
  sudo -n systemctl restart "$unit" || {
    echo "::error::cannot restart $unit for $reason" >&2
    return 1
  }
  until {
    systemctl is-active --quiet "$unit" \
      && newpid=$(systemctl show -p MainPID --value "$unit") \
      && [[ -n "$newpid" && "$newpid" != 0 && "$newpid" != "$oldpid" ]] \
      && psql -d postgres -U laplace_admin -tAc "SELECT 1" >/dev/null 2>&1
  }; do
    tries=$((tries + 1))
    (( tries <= 120 )) || { echo "::error::PostgreSQL did not return after restart" >&2; return 1; }
    sleep 1
  done
  still=$(psql -d postgres -U laplace_admin -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart")
  [[ "$still" == 0 ]] || { echo "::error::$still PostgreSQL settings remain pending restart" >&2; return 1; }
}


preloaded_native_restart_required() {
  local observation rc
  mkdir -p "${LAPLACE_WORK_ROOT:-/build/laplace/work}"
  observation=$(mktemp "${LAPLACE_WORK_ROOT:-/build/laplace/work}/postgres-preload.XXXXXX")
  if ! psql -d postgres -U laplace_admin -qtAX -v ON_ERROR_STOP=1 -c \
    "SELECT json_build_object('preload',current_setting('shared_preload_libraries'),
       'maps',pg_read_file('/proc/' || split_part(pg_read_file('postmaster.pid'),chr(10),1) || '/maps'))" \
       >"$observation"; then
    rm -f "$observation"; return 2
  fi
  if "$PYTHON" - "$ROOT" "$LAPLACE_INSTALL_PREFIX" "$LAPLACE_EXT_LIBDIR" "$observation" <<'PY'
import importlib.util, json, pathlib, re, sys
root, prefix, extension, observation = map(pathlib.Path, sys.argv[1:])
spec = importlib.util.spec_from_file_location("laplace_preload_guard", root / "scripts/check-application-runtime.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
state = json.loads(observation.read_text())
library = re.compile(r"/(liblaplace_(core|dynamics|synthesis)[.]so([.][0-9]+)*|laplace_(geom|substrate)[.]so|laplace_execution_[0-9a-f]{16}[.]so)( [(]deleted[)])?$")
rows = [line for line in state["maps"].splitlines() if library.search(line)]
selected = {}
for path in (prefix / "lib/liblaplace_core.so", prefix / "lib/liblaplace_dynamics.so",
             prefix / "lib/liblaplace_synthesis.so", extension / "laplace_geom.so",
             extension / "laplace_substrate.so", *extension.glob("laplace_execution_*.so")):
    if path.is_file():
        selected[str(path.relative_to(prefix))] = None
preload = {item.strip().strip('"').rsplit("/", 1)[-1].removesuffix(".so")
           for item in state["preload"].split(",")}
required = set()
for module in ("laplace_geom", "laplace_substrate"):
    if module in preload:
        required.update(("lib/liblaplace_core.so", str((extension / (module + ".so")).relative_to(prefix))))
try:
    identities = guard.mapped_native_identities(prefix, {"native_mappings": rows}, selected, required=required)
except ValueError as error:
    print("PostgreSQL preload activation required: " + str(error), flush=True)
    sys.exit(3)
print("PostgreSQL preload identities current: " + json.dumps(identities, sort_keys=True), flush=True)
PY
  then
    rc=1
  else
    rc=$?
    if [[ "$rc" == 3 ]]; then rc=0; else rc=2; fi
  fi
  rm -f "$observation"
  return "$rc"
}

phase_activate_postgres() {
  local required="${1:-0}" rc
  if postgresql_restart_required; then required=1; else rc=$?; [[ "$rc" == 1 ]] || return "$rc"; fi
  if preloaded_native_restart_required; then required=1; else rc=$?; [[ "$rc" == 1 ]] || return "$rc"; fi
  if [[ "$required" == 1 ]]; then
    restart_postgres "installed PostgreSQL or preloaded native image changed" || return $?
  fi
  if postgresql_restart_required; then
    echo "::error::running PostgreSQL release still differs after activation" >&2; return 1
  else
    rc=$?; [[ "$rc" == 1 ]] || return "$rc"
  fi
  if preloaded_native_restart_required; then
    echo "::error::running PostgreSQL preload still differs after activation" >&2; return 1
  else
    rc=$?; [[ "$rc" == 1 ]] || return "$rc"
  fi
}

phase_clean() {
  echo "===== PHASE — CLEAN ====="
  rm -rf "$LAPLACE_BUILD_DIRECTORY"
  mkdir -p "$LAPLACE_BUILD_DIRECTORY"
  find "$ROOT/extension/laplace_substrate/sql/generated" -name '[0-9]*_*.sql.in' -delete 2>/dev/null || true
}

phase_codegen() {
  echo "===== PHASE — CODEGEN ====="
  "$PYTHON" "$ROOT/scripts/codegen-attestation-law.py"
}

phase_build_app() {
  local selected="${LAPLACE_MANAGED_BUILD_PROJECTS:-all}"
  local solution="$ROOT/app/Laplace.slnx"
  local generated="" rc=0 work

  if [[ "$selected" != all ]]; then
    if [[ -z "$selected" ]]; then
      echo "::notice::managed impact plan selected no build projects"
      return 0
    fi
    work="${LAPLACE_WORK_ROOT:-/build/laplace/work}/managed-solutions"
    mkdir -p "$work"
    generated="$(mktemp "$work/build.XXXXXX.slnx")"
    "$PYTHON" "$ROOT/scripts/ci_managed_projects.py" --root "$ROOT" solution \
      --projects "$selected" --output "$generated"
    solution="$generated"
  fi

  echo "===== PHASE — BUILD APP (${selected}) ====="
  dotnet build "$solution" -c Release || rc=$?
  [[ -z "$generated" ]] || rm -f "$generated"
  return "$rc"
}

phase_build_web() {
  echo "===== PHASE — BUILD WEB ====="
  local lock_hash stamp previous
  if [[ ! -f "$ROOT/web/openapi/openapi.json" ]]; then
    echo "::notice::web OpenAPI contract absent in clean candidate; generating from endpoint project only"
    LAPLACE_REUSE_INSTALLED_NATIVE=1 dotnet build       "$ROOT/app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj"       -c Release -v minimal --nologo
  fi
  [[ -f "$ROOT/web/openapi/openapi.json" ]] || {
    echo "::error::endpoint project completed without web/openapi/openapi.json" >&2
    return 1
  }

  mkdir -p "$LAPLACE_BUILD_DIRECTORY/.stamps"
  lock_hash="$(sha256sum "$ROOT/web/package-lock.json" | awk '{print $1}')"
  stamp="$LAPLACE_BUILD_DIRECTORY/.stamps/npm-lock.sha256"
  previous="$(cat "$stamp" 2>/dev/null || true)"
  if [[ ! -d "$ROOT/web/node_modules" || "$previous" != "$lock_hash" ]]; then
    ( cd "$ROOT/web" && npm ci --no-audit --no-fund --prefer-offline )
    printf '%s\n' "$lock_hash" > "$stamp"
  fi
  ( cd "$ROOT/web" && npm run build )
  [[ -f "$ROOT/web/dist/index.html" ]] || {
    echo "::error::web build completed without dist/index.html" >&2
    return 1
  }
  python3 "$ROOT/scripts/web-artifact.py" seal \
    --root "$ROOT" --manifest "$ROOT/build/.laplace-web-artifact.json"
}

phase_build_native() {
  "$PYTHON" "$ROOT/scripts/postgresql-release.py" build-inputs --prefix "$LAPLACE_PG_PREFIX" || return $?
  [[ "$FORCE_REBUILD" != 1 ]] || phase_clean
  [[ "$FORCE_CODEGEN" != 1 ]] || phase_codegen
  echo "===== PHASE — BUILD ENGINE + EXTENSIONS ====="
  local data_root="${LAPLACE_DATA_ROOT:-/vault/Data}"
  local ucd="${LAPLACE_UCD_PATH:-$data_root/UCD/Public/UCD/latest}"
  local chess_openings chess_corpus_export
  chess_openings=$(chess_openings_path)
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
    -DLAPLACE_UCDXML_ZIP="$ucd/ucdxml/ucd.all.flat.zip" \
    -DLAPLACE_DUCET_FILE="$ucd/uca/allkeys.txt" \
    -DLAPLACE_UCD_CONFORMANCE_DIR="$ucd/ucd" \
    -DLAPLACE_CHESS_OPENINGS="$chess_openings" \
    -DLAPLACE_CHESS_CORPUS_EXPORT="$chess_corpus_export"
  LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}" \
    cmake --build "$LAPLACE_BUILD_DIRECTORY" "${build_flags[@]}" --target \
      all \
      laplace_t0_perfcache \
      laplace_highway_perfcache \
      laplace_chess_position_perfcache \
      laplace_modality_number_perfcache

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
}

phase_build() {
  phase_build_native
  phase_build_app
  phase_build_web
}

phase_test() {
  echo "===== PHASE — TEST ====="
  local args=()
  [[ "$SERIAL_TESTS" != 1 && "${LAPLACE_TEST_SERIAL:-0}" != 1 ]] || args+=(--serial)
  bash "$ROOT/scripts/test-parallel.sh" "${args[@]}"
}

reclaim_install_headroom() {
  local app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  local prefix="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
  local ingest_root="$prefix/ingest/runtimes"
  local current_ingest="" target_ingest="" candidate="" base=""
  local managed_transaction="${LAPLACE_MANAGED_TRANSACTION_PATH:-/var/lib/laplace-managed/transaction.json}"

  # Installation happens only after wait-for-quiet-substrate. Reclaim the exact
  # deployment-owned debris whose liveness is mechanically knowable before we
  # allocate another native/perfcache generation on the serving filesystem.
  if [[ -r "$ROOT/deploy/linux/payload-sync.sh" ]]; then
    # shellcheck source=deploy/linux/payload-sync.sh
    source "$ROOT/deploy/linux/payload-sync.sh"
    laplace_prune_unreferenced_releases "$app_dir"
    # A managed transaction owns its rollback snapshot. Outside one, managed.*
    # directories are completed/rolled-back debris by the payload-sync contract.
    if [[ ! -e "$managed_transaction" ]]; then
      laplace_prune_managed_backups "$prefix/app-backups"
    fi
  fi

  # A quiet ingest journal does not prove that another CLI operation has exited.
  # Only runtimes whose launchers participate in lifetime leasing can be removed.
  # Keep earlier unleased releases, the active donor, and this candidate.
  [[ -d "$ingest_root" && ! -L "$ingest_root" ]] || return 0
  current_ingest="$(readlink -f "$prefix/ingest/current" 2>/dev/null || true)"
  target_ingest="$ingest_root/$(git -C "$ROOT" rev-parse HEAD)"
  while IFS= read -r -d '' candidate; do
    [[ "$candidate" == "$current_ingest" || "$candidate" == "$target_ingest" ]] && continue
    base="${candidate##*/}"
    [[ "$base" =~ ^[0-9a-f]{40}$ ]] || continue
    [[ -f "$candidate/.runtime-lease" ]] || continue
    if [[ -d "$candidate" && ! -L "$candidate" && -w "$candidate" && -w "$ingest_root" ]]; then
      (
        exec {runtime_lease}<"$candidate/.runtime-lease"
        flock -n -x "$runtime_lease" || exit 0
        find "$candidate" -xdev -depth -delete || exit 1
        echo "::notice::reclaimed stale ingest runtime $candidate"
      ) || return 1
    fi
  done < <(find "$ingest_root" -mindepth 1 -maxdepth 1 -xdev -type d -print0)
}

phase_install() (
  echo "===== PHASE — INSTALL ====="
  [[ -f "$LAPLACE_BUILD_DIRECTORY/build.ninja" ]] || {
    echo "::error::native build tree missing; run pipeline.sh build first" >&2; exit 1;
  }

  # Local CLI ingests do not hold Actions' resource reservation. Observe the
  # canonical run heartbeat/beacon before replacing their database libraries.
  bash "$ROOT/scripts/wait-for-quiet-substrate.sh" "$PGDATABASE"
  reclaim_install_headroom

  local library_path_changed=0 server_release_changed=0 path_rc server_rc
  if postgresql_restart_required; then server_release_changed=1; else server_rc=$?; [[ "$server_rc" == 1 ]] || exit "$server_rc"; fi
  if ensure_extension_library_path; then library_path_changed=1; else path_rc=$?; [[ "$path_rc" == 1 ]] || exit "$path_rc"; fi

  local api_was_active=0 so_before so_after postgres_activation_required rc=0
  local install_stage
  install_stage="$(mktemp -d "$LAPLACE_BUILD_DIRECTORY/.install-XXXXXXXX")"
  systemctl is-active --quiet laplace-api 2>/dev/null && api_was_active=1 || true
  cleanup_install() {
    rc=$?
    trap - EXIT
    rm -rf "$install_stage"
    if [[ "$api_was_active" == 1 ]]; then sudo -n systemctl start laplace-api || rc=1; fi
    exit "$rc"
  }
  trap cleanup_install EXIT
  [[ "$api_was_active" != 1 ]] || sudo -n systemctl stop laplace-api

  so_before=$(preloaded_so_digest)
  # Finish native and managed payload preparation on the build filesystem before
  # changing the serving prefix. A publish failure must not split its runtime.
  DESTDIR="$install_stage" cmake --install "$LAPLACE_BUILD_DIRECTORY"
  [[ -f "$install_stage$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so" ]] || { echo "::error::core library not staged" >&2; exit 1; }
  # Foundation ingest uses an immutable revision-addressed runtime. The legacy
  # flat directory may contain files from an operator-owned install and is never
  # overwritten by the service runner. Build into a fresh owned directory, then
  # atomically switch one symlink to make the exact revision active.
  local ingest_dir="$LAPLACE_INSTALL_PREFIX/ingest"
  local ingest_runtime_root="$ingest_dir/runtimes"
  local ingest_revision ingest_runtime ingest_stage ingest_link_tmp
  ingest_revision="$(git -C "$ROOT" rev-parse HEAD)"
  ingest_runtime="$ingest_runtime_root/$ingest_revision"
  ingest_stage="$ingest_runtime_root/.stage-$ingest_revision-${GITHUB_RUN_ID:-$$}"
  mkdir -p "$ingest_dir" "$ingest_dir/logs" "$ingest_runtime_root"
  if getent group laplace-runner >/dev/null; then
    local ingest_path ingest_owner ingest_group ingest_mode
    for ingest_path in "$ingest_dir" "$ingest_dir/logs" "$ingest_runtime_root"; do
      ingest_owner="$(stat -c '%U' "$ingest_path")"
      ingest_group="$(stat -c '%G' "$ingest_path")"
      ingest_mode="$(stat -c '%a' "$ingest_path")"
      if [[ "$ingest_group" != laplace-runner ]]; then
        echo "::error::$ingest_path permissions drifted: ${ingest_owner}:${ingest_group} mode ${ingest_mode}; expected shared group laplace-runner" >&2
        echo "::error::run scripts/bootstrap-laplace-runner.sh prefix from a privileged operator session" >&2
        exit 1
      fi
      if [[ "$ingest_mode" != 2775 ]]; then
        if [[ -O "$ingest_path" ]]; then
          chmod 2775 "$ingest_path"
        else
          echo "::error::$ingest_path mode drifted: ${ingest_owner}:${ingest_group} mode ${ingest_mode}; runner cannot reconcile a directory it does not own" >&2
          echo "::error::run scripts/bootstrap-laplace-runner.sh prefix from a privileged operator session" >&2
          exit 1
        fi
      fi
    done
  fi
  if [[ ! -d "$ingest_runtime" ]]; then
    rm -rf "$ingest_stage"
    mkdir -p "$ingest_stage"
    local ingest_build="$install_stage/ingest" ingest_reference
    dotnet publish "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release -o "$ingest_build" --no-self-contained -v q
    cp -Pf "$install_stage$LAPLACE_INSTALL_PREFIX/lib"/liblaplace_*.so* "$ingest_build/"
    touch "$ingest_build/.runtime-lease"
    source "$ROOT/deploy/linux/payload-sync.sh"
    laplace_wrap_runtime_lease "$ingest_build/Laplace.Cli"
    # Immutable releases share identical payload files with the active runtime.
    # Build on the build volume, then copy only new bytes onto the install volume.
    ingest_reference="$(readlink -f "$ingest_dir/current" 2>/dev/null || true)"
    local -a ingest_links=()
    [[ ! -d "$ingest_reference" ]] || ingest_links+=("--link-dest=$ingest_reference")
    rsync -rl --checksum --no-times --no-perms --executability "${ingest_links[@]}" \
      "$ingest_build/" "$ingest_stage/"
    printf '%s\n' "$ingest_revision" > "$ingest_stage/.laplace-source-revision"
    [[ -f "$ingest_stage/Laplace.Cli.dll" && -f "$ingest_stage/liblaplace_core.so" ]] || {
      echo "::error::ingest runtime missing after staged install: $ingest_stage" >&2
      exit 1
    }
    mv "$ingest_stage" "$ingest_runtime"
  fi
  [[ "$(cat "$ingest_runtime/.laplace-source-revision" 2>/dev/null || true)" == "$ingest_revision" &&
     -f "$ingest_runtime/Laplace.Cli.dll" && -f "$ingest_runtime/liblaplace_core.so" ]] || {
    echo "::error::immutable ingest runtime is incomplete: $ingest_runtime" >&2
    exit 1
  }
  # Keep changed files temporary until the transfer succeeds. A full destination
  # leaves the serving T0/native files intact. Retain older execution modules:
  # other databases can still reference their content-addressed names.
  rsync -rl --checksum --delay-updates --no-times --no-perms --executability \
    "$install_stage$LAPLACE_INSTALL_PREFIX/" "$LAPLACE_INSTALL_PREFIX/"
  git -C "$ROOT" rev-parse HEAD > "$LAPLACE_INSTALL_PREFIX/lib/.laplace-source-revision"
  ingest_link_tmp="$ingest_dir/.current-$ingest_revision-$$"
  rm -f "$ingest_link_tmp"
  ln -s "runtimes/$ingest_revision" "$ingest_link_tmp"
  mv -Tf "$ingest_link_tmp" "$ingest_dir/current"
  echo "::notice::installed ingest runtime $ingest_runtime; active=$ingest_dir/current"
  so_after=$(preloaded_so_digest)
  postgres_activation_required="$server_release_changed"
  if [[ "$so_before" != "$so_after" || "$library_path_changed" == 1 ]]; then
    local preload
    preload=$(psql -d postgres -U laplace_admin -tAc "SHOW shared_preload_libraries")
    if [[ ",${preload// /}," == *",laplace_substrate,"* || ",${preload// /}," == *",laplace_geom,"* ]]; then
      postgres_activation_required=1
    fi
  fi
  phase_activate_postgres "$postgres_activation_required"
  if [[ "$api_was_active" == 1 ]]; then sudo -n systemctl start laplace-api; api_was_active=0; fi
  rm -rf "$install_stage"
  trap - EXIT
)

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
  mkdir -p "${LAPLACE_WORK_ROOT:-/build/laplace/work}"
  log=$(mktemp "${LAPLACE_WORK_ROOT:-/build/laplace/work}/alter-extension.XXXXXX")
  PGOPTIONS="-c lock_timeout=${LAPLACE_DDL_LOCK_TIMEOUT:-20s}" \
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "ALTER EXTENSION $ext UPDATE TO '$avail'" >"$log" 2>&1 || rc=$?
  if [[ "$rc" != 0 ]]; then
    cat "$log" >&2
    if grep -q 'lock timeout\|canceling statement due to lock timeout\|deadlock detected' "$log"; then
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
  [[ -n "$avail" ]] || { echo "::error::$ext is not available on the server" >&2; return 1; }
  if [[ -z "$installed" ]]; then
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS $ext CASCADE"
    return
  fi
  [[ "$installed" != "$avail" ]] || { echo "$ext already at $avail"; return; }
  bridge="$LAPLACE_EXT_SHAREDIR/$ext--${installed}--${avail}.sql"
  install -m 664 "$LAPLACE_EXT_SHAREDIR/${ext}_upgrade.sql" "$bridge"
  alter_extension_update "$ext" "$avail"
}

phase_sync_extension() (
  echo "===== PHASE — SYNC EXTENSION SQL ====="
  # Extension upgrades replace views/functions and require DDL locks. Do not
  # restart the live API between native install and schema synchronization and
  # then let request transactions deadlock the upgrade. Quiesce the API for the
  # exact DDL window; PostgreSQL remains online for the migration itself.
  local api_was_active=0 sync_rc=0
  systemctl is-active --quiet laplace-api 2>/dev/null && api_was_active=1 || true
  cleanup_extension_sync() {
    sync_rc=$?
    trap - EXIT
    if [[ "$api_was_active" == 1 ]]; then
      sudo -n systemctl start laplace-api || sync_rc=1
    fi
    exit "$sync_rc"
  }
  trap cleanup_extension_sync EXIT
  [[ "$api_was_active" != 1 ]] || sudo -n systemctl stop laplace-api

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
    mkdir -p "${LAPLACE_WORK_ROOT:-/build/laplace/work}"
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
    if [[ "$rc" != 0 ]]; then
      cat "$log" >&2
      if grep -q 'lock timeout\|canceling statement due to lock timeout\|deadlock detected' "$log"; then
        psql -d "$PGDATABASE" -U laplace_admin -P pager=off -c \
          "SELECT pid,state,wait_event_type,wait_event,now()-COALESCE(xact_start,query_start) AS held,left(query,160) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND state<>'idle' ORDER BY COALESCE(xact_start,query_start)" >&2 || true
      fi
      rm -f "$log"
      return "$rc"
    fi
    rm -f "$log"
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 -c \
      "CALL chess.repair_player_ratings(laplace.relation_type_id('OUTCOME'),laplace.relation_type_id('PLAYED_BY'),laplace.relation_type_id('HAS_RATING'))"
  fi

  if [[ "$api_was_active" == 1 ]]; then
    sudo -n systemctl start laplace-api
    api_was_active=0
  fi
  trap - EXIT
)

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
  local env_file="$LAPLACE_INSTALL_PREFIX/app/laplace-api.env" bin example ops_log_dir billing_bypass
  set_api_env() {
    local key="$1" value="$2"
    if grep -q "^${key}=" "$env_file"; then
      sed -i "s|^${key}=.*|${key}=${value}|" "$env_file"
    else
      printf '\n%s=%s\n' "$key" "$value" >>"$env_file"
    fi
  }
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
  if [[ -n "${LAPLACE_PUBLIC_BASE_URL:-}" ]]; then
    [[ "$LAPLACE_PUBLIC_BASE_URL" =~ ^https://[^/?#]+(:[0-9]+)?$ ]] || {
      echo "::error::LAPLACE_PUBLIC_BASE_URL must be an HTTPS origin without a path" >&2
      return 1
    }
    set_api_env LAPLACE_AUTH_MODE identity
    set_api_env LAPLACE_BILLING_STORE postgres
    billing_bypass="${LAPLACE_BILLING_BYPASS:-true}"
    case "${billing_bypass,,}" in
      true|1) billing_bypass=true ;;
      false|0) billing_bypass=false ;;
      *)
        echo "::error::LAPLACE_BILLING_BYPASS must be true, false, 1, or 0" >&2
        return 1
        ;;
    esac
    set_api_env LAPLACE_BILLING_BYPASS "$billing_bypass"
    set_api_env LAPLACE_PUBLIC_BASE_URL "${LAPLACE_PUBLIC_BASE_URL%/}"
    set_api_env LAPLACE_DATA_PROTECTION_KEYS "$LAPLACE_INSTALL_PREFIX/secrets/data-protection"
  fi
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
    { printf 'STRIPE_API_SECRET=%s\nSTRIPE_API_KEY=%s\n' "$stripe" "$stripe"; [[ -z "$whsec" ]] || printf 'STRIPE_WEBHOOK_SECRET=%s\n' "$whsec"; [[ -z "${STRIPE_API_Publishable:-${STRIPE_API_PUBLISHABLE:-}}" ]] || printf 'STRIPE_API_Publishable=%s\n' "${STRIPE_API_Publishable:-$STRIPE_API_PUBLISHABLE}"; } >"$dst.tmp"
    chmod 640 "$dst.tmp"; mv "$dst.tmp" "$dst"
  elif [[ "$in_ci" == 1 ]]; then echo "::error::STRIPE_API_SECRET is required" >&2; missing=1; fi

  # Absent workflow inputs retain each installed OAuth provider.
  python3 "$ROOT/scripts/update-identity-secrets.py" "$dir/identity.env" || missing=1

  # Agent credentials are optional and independently retained. A provider becomes
  # callable when its key appears; missing providers remain visible as uncredentialed.
  python3 "$ROOT/scripts/update-agent-secrets.py" "$dir/agents.env" || missing=1

  # The live operator may edit agents.json through the admin surface. Seed a
  # missing/empty install from the checked-in routing contract, then preserve it.
  local agent_config="$LAPLACE_INSTALL_PREFIX/app/agents.json"
  if [[ ! -s "$agent_config" ]]; then
    install -m 0644 "$ROOT/config/agents.json" "$agent_config" || missing=1
  fi
  [[ "$missing" == 0 ]]
}

phase_publish() {
  echo "===== PHASE — PUBLISH PAYLOAD ====="
  local app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  # shellcheck source=deploy/linux/app-dir-contract.sh
  source "$ROOT/deploy/linux/app-dir-contract.sh"
  laplace_reconcile_app_dir_contract "$app_dir"
  phase_runtime_secrets
  local deploy_args=()
  [[ "${LAPLACE_FORCE_NPM:-0}" != 1 ]] || deploy_args+=(--force-npm)
  [[ "${LAPLACE_PUBLISH_SERIAL:-0}" != 1 ]] || deploy_args+=(--serial)
  LAPLACE_MANAGED_TRANSACTION=1 bash "$ROOT/deploy/linux/deploy.sh" "${deploy_args[@]}"
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
    --clean-first) CLEAN_FIRST=1; shift ;;
    --force-rebuild) FORCE_REBUILD=1; shift ;;
    --serial-tests) SERIAL_TESTS=1; export LAPLACE_TEST_SERIAL=1; shift ;;
    --force-all) shift ;;
    -h|--help) usage ;;
    clean|codegen|build|build-native|build-app|build-web|install|activate-postgres|migrate|sync-extension|tune-pg|tune-laplace|perfcache-guc|api-env|chess-lab|publish|foundation|test)
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
    build-native) phase_build_native ;;
    build-app) phase_build_app ;;
    build-web) phase_build_web ;;
    install) phase_install ;;
    activate-postgres) phase_activate_postgres ;;
    migrate) phase_migrate ;;
    sync-extension) phase_sync_extension ;;
    tune-pg) phase_tune_pg ;;
    tune-laplace) phase_tune_laplace ;;
    perfcache-guc) phase_perfcache_guc ;;
    api-env) phase_api_env ;;
    chess-lab) phase_chess_lab ;;
    publish) phase_publish ;;
    foundation) phase_foundation ;;
    test) phase_test ;;
  esac
done

echo "===== PIPELINE PHASES COMPLETE: ${PHASES[*]} ====="
