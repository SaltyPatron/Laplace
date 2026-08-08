#!/usr/bin/env bash
# Laplace pipeline orchestrator — one idempotent phase per invocation.
#
# CI (.github/workflows/laplace.yml) runs one phase per job so the Actions
# graph maps 1:1 onto these phases. Locally, run any phase directly.
#
# Usage: pipeline.sh <phase> [<phase> ...] [options]
#
# Change-aware: build/install/test are gated on content fingerprints
# (scripts/lib/fp.sh, stamps under build/.stamps/) — a phase whose input domain
# is unchanged since its last SUCCESS no-ops in seconds. dotnet build/test runs
# only the affected ProjectReference closure (scripts/affected-app.py). Stamps
# advance on success only, so failed/cancelled runs never cause a skip.
# Bypass: --force-all / LAPLACE_FORCE_ALL=1; `clean` wipes the stamps.
#
# Phases (in canonical order):
#   clean           rm -rf build/
#   codegen         attestation-law codegen (stamp-skipped unless --force-codegen)
#   build           codegen + cmake configure/build + dotnet build
#   install         cmake --install to $LAPLACE_INSTALL_PREFIX
#   migrate         Laplace.Migrations up (idempotent; --fresh-db nukes first)
#   sync-extension  CREATE/ALTER EXTENSION laplace_substrate to built version
#   tune-pg         machine-sized ALTER SYSTEM tuning (restarts if pending)
#   tune-laplace    db/table-scoped ALTER TABLE tuning (run after migrate; skips empty DB)
#   perfcache-guc   point laplace_substrate.perfcache_path at installed blob
#   api-env         ensure laplace-api.env has current perfcache path + DB
#   publish         FULL publish-target contract: chess-lab binaries/paths,
#                   secrets drop refresh, API + SPA + laplace-uci + laplace-mcp deploy
#   foundation      scripts/ensure-foundation.sh (no-ops on present layers; --force)
#   test            scripts/test-parallel.sh (ctest ∥ regress, then dotnet)
#
# (chess-lab is not a separate human/CI step — publish owns it.)
#
# Options:
#   --fresh-db        nuke DB before migrate
#   --force           pass --force to ensure-foundation.sh
#   --force-codegen   ignore attestation-law stamp; always run Python codegen
#   --skip-codegen    skip codegen in build (CMake custom_command remains SoT)
#   --clean-first     cmake --build --clean-first (rebuild objects, keep configure)
#   --force-rebuild   wipe build/ then build (same as: clean build)
#   --serial-tests    set LAPLACE_TEST_SERIAL=1 for the test phase
#   --force-all       ignore all content-fingerprint stamps (LAPLACE_FORCE_ALL=1)

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# GROUP-WRITABLE ARTIFACTS, EVERY PHASE. The checkout, build/, and
# $LAPLACE_INSTALL_PREFIX are SHARED between the CI runner (laplace-runner) and
# whichever operator is at the console — both members of the laplace-runner
# group. With the default 0022 umask, whoever builds first writes 0644/0755
# artifacts they alone own, and the next member of the group cannot overwrite
# them: dotnet fails MSB3021 "Access to the path ... is denied" on a bin/
# output a previous run produced, and cmake --install fails the same way under
# /opt/laplace. Observed 2026-07-27 on app/Laplace.Migrations/bin/Release,
# owned laplace-runner:laplace-runner mode -rwxr--r-- after a CI publish; every
# other project's tree was operator-owned, so only the one project CI had
# rebuilt was poisoned.
#
# This was already known and fixed LOCALLY in install_native (a bare
# `umask 0002` before cmake --install) — which covers the install phase and
# nothing else. Hoisting it to the top makes the invariant hold for codegen,
# build, install, publish and test alike, which is the only way it can actually
# hold: the failure lands in whichever phase happens to write first.
#
# Never repair this with sudo/chown. The permission class is fixed at the
# source of the write (see .scratchpad memory: /opt/laplace install permission
# class); a sudo repair silently re-poisons on the next run by the other user.
umask 0002

# Content-fingerprint gates (build/.stamps): build/install/test phases no-op
# when their input domain hasn't changed. LAPLACE_FORCE_ALL=1 (or --force-all)
# bypasses every gate; `pipeline.sh clean` wipes the stamps with build/.
# shellcheck source=scripts/lib/fp.sh
source "$ROOT/scripts/lib/fp.sh"

LAPLACE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
LAPLACE_EXTERNAL="${LAPLACE_EXTERNAL:-/opt/laplace/external}"
# Peer auth over the runner-owned unix socket (laplace_admin). Bare psql without
# these defaults looks for OS-user role "ahart" / a missing system socket.
export PGHOST="${PGHOST:-/var/run/postgresql}"
export PGUSER="${PGUSER:-laplace_admin}"
PGDATABASE="${PGDATABASE:-laplace}"
export PGDATABASE

FRESH_DB=0
FORCE_FOUNDATION=0
FORCE_CODEGEN=0
SKIP_CODEGEN=0
CLEAN_FIRST=0
FORCE_REBUILD=0
SERIAL_TESTS=0

# Parallelism defaults (parity with scripts/win/env.cmd).
nproc_n="$(nproc 2>/dev/null || echo 1)"
export CMAKE_BUILD_PARALLEL_LEVEL="${CMAKE_BUILD_PARALLEL_LEVEL:-$nproc_n}"
if [[ -z "${CTEST_PARALLEL_LEVEL:-}" ]]; then
  if [[ "${LAPLACE_TEST_SERIAL:-}" == "1" ]]; then
    export CTEST_PARALLEL_LEVEL=1
  else
    export CTEST_PARALLEL_LEVEL="$nproc_n"
  fi
fi

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
  cat <<'EOF'
Usage: pipeline.sh <phase> [<phase> ...] [options]

Phases: clean codegen build install migrate sync-extension tune-pg tune-laplace
        perfcache-guc api-env publish publish-stamp foundation test

Options:
  --fresh-db        nuke DB before migrate
  --force           pass --force to ensure-foundation.sh
  --force-codegen   ignore attestation-law stamp; always run Python codegen
  --skip-codegen    skip codegen in build (CMake custom_command remains SoT)
  --clean-first     cmake --build --clean-first (rebuild objects, keep configure)
  --force-rebuild   wipe build/ then build (same as: clean build)
  --serial-tests    set LAPLACE_TEST_SERIAL=1 for the test phase
  --force-all       ignore all content-fingerprint stamps (LAPLACE_FORCE_ALL=1)
EOF
  exit 2
}

# Content digest of the libraries the postmaster preloads. Empty string when
# neither is installed yet (first install — nothing is pinned, nothing to bounce).
preloaded_so_digest() {
  local d="$LAPLACE_INSTALL_PREFIX/lib/postgresql/18"
  cat "$d/laplace_substrate.so" "$d/laplace_geom.so" 2>/dev/null | sha256sum | cut -d' ' -f1
}

restart_postgres() {
  # $1 = reason. ROOTLESS self-bounce: on this host the postmaster runs AS the
  # runner user (laplace-postgresql.service, User=laplace-runner), so the
  # runner controls its own postgres — it signals the postmaster it owns with
  # SIGINT (fast shutdown) and systemd's Restart=always resurrects it with the
  # new config. No sudo anywhere on the hot path. A sudo -n (never-prompt)
  # systemctl restart exists only as a fallback for hosts where postgres runs
  # under a different user. Either way this PROVES nothing is left pending or
  # fails the phase loudly right here — a pending restart is never downgraded
  # to a warning four jobs upstream of the failure it causes.
  local reason="$1" datadir pidfile oldpid=""
  datadir=$(psql -d postgres -U laplace_admin -tAc "SHOW data_directory")
  pidfile="$datadir/postmaster.pid"
  oldpid=$(head -1 "$pidfile" 2>/dev/null || true)
  # data_directory is often 0700 owner=laplace-runner; fall back to unit MainPID.
  if [[ -z "$oldpid" ]]; then
    oldpid=$(systemctl show -p MainPID --value laplace-postgresql.service 2>/dev/null || true)
    [[ "$oldpid" == "0" ]] && oldpid=""
  fi

  if [[ -n "$oldpid" ]] && kill -0 "$oldpid" 2>/dev/null; then
    echo "restart_postgres ($reason): fast-shutdown SIGINT to owned postmaster pid $oldpid (systemd resurrects it)"
    kill -INT "$oldpid"
  else
    local unit="laplace-postgresql.service"
    if ! sudo -n systemctl restart "$unit" 2>/dev/null; then
      unit=$(systemctl list-units --type=service --state=running --plain --no-legend \
               'postgres*' '*postgres*' 2>/dev/null | awk '{print $1}' | head -1)
      if [[ -z "$unit" ]] || ! sudo -n systemctl restart "$unit" 2>/dev/null; then
        echo "::error::restart_postgres ($reason): postmaster pid ${oldpid:-unknown} is not signalable by $(id -un) and no rootless path exists — bounce PostgreSQL manually, then rerun this phase" >&2
        return 1
      fi
    fi
    echo "restart_postgres ($reason): restarted $unit via passwordless systemctl fallback"
  fi

  local tries=0 newpid=""
  until { newpid=$(head -1 "$pidfile" 2>/dev/null || true)
          [[ -z "$newpid" || "$newpid" == "0" ]] \
            && newpid=$(systemctl show -p MainPID --value laplace-postgresql.service 2>/dev/null || true)
          [[ -n "$newpid" && "$newpid" != "0" && "$newpid" != "$oldpid" ]]
        } && psql -d postgres -U laplace_admin -tAc "SELECT 1" >/dev/null 2>&1; do
    tries=$((tries + 1))
    if (( tries > 120 )); then
      echo "::error::restart_postgres ($reason): PostgreSQL did not come back within ${tries}s (old pid ${oldpid:-unknown}) — if the unit lacks Restart=always, apply the drop-in from bootstrap-laplace-runner.sh and start it manually" >&2
      return 1
    fi
    sleep 1
  done

  local still
  still=$(psql -d postgres -U laplace_admin -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart")
  if [[ "$still" != "0" ]]; then
    echo "::error::restart_postgres ($reason): $still setting(s) STILL pending after restart:" >&2
    psql -d postgres -U laplace_admin -c "SELECT name, setting FROM pg_settings WHERE pending_restart" >&2
    return 1
  fi
  echo "restart_postgres ($reason): clean — postmaster ${oldpid:-?} -> $newpid, no settings pending"
}

phase_clean() {
  echo "===== PHASE — CLEAN ====="
  rm -rf "$ROOT/build"
  # Stale generated SQL fragments trip the manifest-completeness gate on reconfigure.
  find "$ROOT/extension/laplace_substrate/sql/generated" -name '[0-9]*_*.sql.in' -delete 2>/dev/null || true
}

# Stamp path matches scripts/codegen-attestation-law.ps1 (Windows).
_codegen_stamp_path() {
  echo "$ROOT/engine/core/src/generated/.attestation-law-stamp"
}

_codegen_manifest_key() {
  # ticks-equivalent: mtimes of inputs the PS1 stamps on
  local a b c
  a=$(stat -c %Y "$ROOT/engine/manifest/relation_types.toml" 2>/dev/null || echo 0)
  b=$(stat -c %Y "$ROOT/engine/manifest/pos_tags.toml" 2>/dev/null || echo 0)
  c=$(stat -c %Y "$ROOT/scripts/codegen-attestation-law.py" 2>/dev/null || echo 0)
  echo "${a}:${b}:${c}"
}

phase_codegen() {
  echo "===== PHASE — CODEGEN ====="
  if [[ "$SKIP_CODEGEN" -eq 1 ]]; then
    echo "codegen skipped (--skip-codegen)"
    return 0
  fi
  local stamp key prev
  stamp="$(_codegen_stamp_path)"
  key="$(_codegen_manifest_key)"
  if [[ "$FORCE_CODEGEN" -eq 0 && -f "$stamp" ]]; then
    prev=$(cat "$stamp" 2>/dev/null || true)
    if [[ "$prev" == "$key" ]]; then
      echo "attestation law codegen skipped (stamp fresh)"
      return 0
    fi
  fi
  "$PYTHON" "$ROOT/scripts/codegen-attestation-law.py"
  mkdir -p "$(dirname "$stamp")"
  printf '%s' "$key" > "$stamp"
  echo "attestation law codegen complete"
}

phase_build() {
  if [[ "$FORCE_REBUILD" -eq 1 ]]; then
    phase_clean
  fi
  if [[ "$SKIP_CODEGEN" -eq 0 ]]; then
    phase_codegen
  else
    echo "===== PHASE — CODEGEN [skipped] ====="
  fi
  echo "===== PHASE — BUILD ENGINE + EXTENSIONS ====="
  local native_fp
  native_fp=$(fp_native)
  # Corpus paths are declared OUTSIDE the configure branch: the perfcache existence gates
  # below read $chess_openings unconditionally, and under `set -u` a cached-fingerprint run
  # (configure skipped) would abort on an unbound variable.
  local data_root="${LAPLACE_DATA_ROOT:-/vault/Data}"
  local ucd="${LAPLACE_UCD_PATH:-$data_root/UCD/Public/UCD/latest}"
  # Chess openings corpus, declared exactly like the UCD corpus above. engine/core
  # deliberately carries NO default path (a hardcoded /vault in CMake was removed), so if
  # nothing passes this the chess position perfcache target silently disappears — its guard
  # is an `if(LAPLACE_CHESS_OPENINGS ...)` whose else branch is only a message(STATUS).
  # That is why the blob in share/laplace was a hand copy (owner ahart:ahart) instead of an
  # install product (laplace-runner group, install perms) like t0 and highway.
  local chess_openings="${LAPLACE_CHESS_OPENINGS:-$data_root/Games/Chess/openings}"
  if [[ "$CLEAN_FIRST" -eq 0 && -d "$ROOT/build" ]] && fp_check build-native "$native_fp"; then
    echo "engine up-to-date — cmake configure/build skipped (fp ${native_fp:0:12})"
  else
    local build_flags=()
    [[ "$CLEAN_FIRST" -eq 1 ]] && build_flags+=(--clean-first)
    cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
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
      -DLAPLACE_CHESS_OPENINGS="$chess_openings"
    LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}" \
      cmake --build build "${build_flags[@]}"
    fp_record build-native "$native_fp"
  fi
  # Perfcache targets are ALL — existence check only (parity with rebuild-all.cmd).
  local t0 hw
  t0=$(find "$ROOT/build" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | head -1 || true)
  hw=$(find "$ROOT/build" -name 'laplace_highway_perfcache*.bin' 2>/dev/null | head -1 || true)
  if [[ -z "$t0" || -z "$hw" ]]; then
    echo "::error::perfcache blobs missing after ALL build — expected under build/"
    exit 1
  fi
  echo "T0 perfcache ready: $t0"
  echo "highway perfcache ready: $hw"
  # Chess position blob (tier-2 geometry, spec 33 / GH #822) gets the SAME existence gate,
  # but it SELF-ARMS off the PRODUCING TARGET, not off a variable.
  #
  # The first version of this armed on `LAPLACE_CHESS_OPENINGS:` appearing in CMakeCache.txt.
  # That was wrong and red-lit every build: THIS SCRIPT passes -DLAPLACE_CHESS_OPENINGS on
  # every configure, so the cache always contains it whether or not engine/core declares a
  # target that consumes it. The variable proves the input was offered, never that a producer
  # exists to accept it.
  #
  # `add_custom_target(laplace_chess_position_perfcache ...)` in engine/core/CMakeLists.txt IS
  # the producer. Grepping for it is exact: absent -> nothing can emit the blob and the gate
  # must stay quiet; present -> a missing blob is a real skipped target. It arms itself the
  # commit that target lands and needs no second edit here.
  local chess_bin chess_target_declared=0
  if grep -q 'add_custom_target(laplace_chess_position_perfcache' "$ROOT/engine/core/CMakeLists.txt" 2>/dev/null; then
    chess_target_declared=1
  fi
  if [[ "$chess_target_declared" -eq 0 ]]; then
    echo "chess position perfcache: no laplace_chess_position_perfcache target in engine/core/CMakeLists.txt — gate inactive"
  elif [[ -d "$chess_openings" ]]; then
    chess_bin=$(find "$ROOT/build" -name 'laplace_chess_position_perfcache*.bin' 2>/dev/null | head -1 || true)
    if [[ -z "$chess_bin" ]]; then
      echo "::error::chess position perfcache missing after ALL build, but the openings corpus EXISTS at $chess_openings — the CMake target was skipped (LAPLACE_CHESS_OPENINGS not reaching engine/core, or ChessCatalogSurfaces missing)"
      exit 1
    fi
    echo "chess position perfcache ready: $chess_bin"
  else
    echo "chess position perfcache: openings corpus absent at $chess_openings — target not expected"
  fi
  echo "===== PHASE — BUILD APP ====="
  phase_build_app
}

phase_build_app() {
  # Affected-only dotnet build: the planner walks the ProjectReference graph
  # with per-project Merkle fingerprints, so building the printed roots builds
  # every affected project. Empty plan = nothing changed. Any planner failure
  # falls back to the full solution — never trade correctness for speed.
  local plan_out plan_rc=0
  plan_out=$("$PYTHON" "$ROOT/scripts/affected-app.py" plan --ns build) || plan_rc=$?
  if [[ "$plan_rc" -ne 0 ]]; then
    echo "::warning::affected-app plan failed (rc=$plan_rc) — full solution build"
    ( cd "$ROOT/app" && dotnet build Laplace.slnx -c Release )
    return 0
  fi
  if [[ -z "$plan_out" ]]; then
    # Stamps can outlive artifacts (e.g. a manual bin/ wipe): trust them only
    # while EVERY solution project has a Release output tree — one surviving
    # bin/Release must not vouch for the others (stale-artifact class).
    local slnx proj rel missing=""
    slnx=$(<"$ROOT/app/Laplace.slnx")
    for proj in "$ROOT"/app/*/*.csproj; do
      [[ -e "$proj" ]] || continue
      rel="${proj#"$ROOT/app/"}"
      [[ "$slnx" == *"\"$rel\""* ]] || continue   # not part of the solution build
      if [[ ! -d "${proj%/*}/bin/Release" ]]; then
        missing="$rel"
        break
      fi
    done
    if [[ -z "$missing" ]]; then
      echo "app up-to-date — dotnet build skipped (fingerprints unchanged, all Release trees present)"
      return 0
    fi
    echo "app stamps present but $missing lacks bin/Release — full solution build"
    ( cd "$ROOT/app" && dotnet build Laplace.slnx -c Release )
    "$PYTHON" "$ROOT/scripts/affected-app.py" record --ns build
    return 0
  fi
  local -a roots=()
  mapfile -t roots <<<"$plan_out"
  if (( ${#roots[@]} > 4 )); then
    echo "app: ${#roots[@]} affected roots — building full solution"
    ( cd "$ROOT/app" && dotnet build Laplace.slnx -c Release )
  else
    local r
    for r in "${roots[@]}"; do
      echo "app: dotnet build $r"
      ( cd "$ROOT/app" && dotnet build "$r" -c Release )
    done
  fi
  "$PYTHON" "$ROOT/scripts/affected-app.py" record --ns build
}

phase_test() {
  echo "===== PHASE — TEST ====="
  local args=()
  [[ "$SERIAL_TESTS" -eq 1 || "${LAPLACE_TEST_SERIAL:-}" == "1" ]] && args+=(--serial)
  bash "$ROOT/scripts/test-parallel.sh" "${args[@]}"
}

phase_install() {
  echo "===== PHASE — INSTALL ====="
  test -d build || { echo "::error::build/ missing — run 'pipeline.sh build' first"; exit 1; }
  local native_fp
  native_fp=$(fp_native)
  if fp_check install-native "$native_fp" \
     && [[ -f "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so" ]]; then
    echo "install up-to-date — skipped (engine/extension unchanged since last install; no API stop, no PG bounce)"
    return 0
  fi
  # Stop the API for the install window (previously the CI deploy job's step —
  # owning it here means a skipped install never bounces the service at all).
  local api_was_active=0
  if systemctl is-active --quiet laplace-api 2>/dev/null; then
    api_was_active=1
    sudo -n systemctl stop laplace-api 2>/dev/null || true
  fi
  local so_before so_after
  so_before=$(preloaded_so_digest)
  umask 0002
  cmake --install build
  test -f "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so"
  so_after=$(preloaded_so_digest)
  # shared_preload_libraries pins the extension image in the postmaster, so a
  # replaced .so needs a bounce before CREATE FUNCTION / ALTER EXTENSION can
  # dlsym a newly-exported symbol (the "could not find function ... in file"
  # class; terminate-backends is not enough when the library is preloaded).
  #
  # BUT ONLY WHEN THE BINARY ACTUALLY CHANGED. This used to bounce whenever the
  # extension was merely preloaded — which it always is — so every SQL-only
  # change paid a full cluster restart for an image that is byte-identical.
  # fp_native (the install gate above) covers the whole engine+extension domain
  # INCLUDING .sql.in, so editing one function body invalidates it, reaches here,
  # and forced a bounce that nothing needed. Digest the preloaded libraries
  # across the install instead: same bytes, same image, no restart.
  if [[ "$so_before" != "$so_after" ]]; then
    local preload
    preload=$(psql -d postgres -U laplace_admin -tAc "SHOW shared_preload_libraries" 2>/dev/null || true)
    if [[ ",${preload// /}," == *",laplace_substrate,"* ]] \
       || [[ ",${preload// /}," == *",laplace_geom,"* ]]; then
      restart_postgres "install: preloaded extension .so changed"
    fi
  else
    echo "install: preloaded .so unchanged — no PG bounce needed (SQL-only change)"
  fi
  fp_record install-native "$native_fp"
  if [[ "$api_was_active" -eq 1 ]]; then
    sudo -n systemctl start laplace-api 2>/dev/null || true
  fi
}

phase_migrate() {
  echo "===== PHASE — MIGRATE ($PGDATABASE) ====="
  local mig="$ROOT/app/Laplace.Migrations/bin/Release/net10.0/Laplace.Migrations.dll"
  if [[ ! -f "$mig" || "$FORCE_REBUILD" -eq 1 || "$CLEAN_FIRST" -eq 1 ]]; then
    dotnet build "$ROOT/app/Laplace.Migrations/Laplace.Migrations.csproj" -c Release
  else
    echo "migrate: using existing $mig"
  fi
  if [[ "$FRESH_DB" -eq 1 ]]; then
    dotnet "$mig" nuke --yes
  fi
  dotnet "$mig" up
}

# Bridge one extension from its installed version to the built one. The install SQL
# is a fresh-CREATE script; ALTER EXTENSION UPDATE needs a --<old>--<new>.sql file,
# which is just the upgrade body under the name PG looks for.
sync_one_extension() {
  local ext="$1" avail installed share bridge
  avail=$(psql -d "$PGDATABASE" -U laplace_admin -tAX \
    -c "SELECT default_version FROM pg_available_extensions WHERE name='$ext'" | tr -d '[:space:]')
  installed=$(psql -d "$PGDATABASE" -U laplace_admin -tAX \
    -c "SELECT extversion FROM pg_extension WHERE extname='$ext'" | tr -d '[:space:]')
  echo "$ext: installed='$installed' available='$avail'"
  test -n "$avail" || { echo "::error::$ext missing from pg_available_extensions"; exit 1; }

  if [[ -z "$installed" ]]; then
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "CREATE EXTENSION IF NOT EXISTS $ext CASCADE"
    return 0
  fi
  [[ "$installed" == "$avail" ]] && { echo "OK $ext already at $avail"; return 0; }

  share=$(dirname "$(find "$LAPLACE_INSTALL_PREFIX" -name "$ext.control" -not -path '*/build*' 2>/dev/null | head -1)")
  test -n "$share" || { echo "::error::could not locate $ext.control under $LAPLACE_INSTALL_PREFIX"; exit 1; }
  bridge="$share/$ext--${installed}--${avail}.sql"
  install -m 664 "$share/${ext}_upgrade.sql" "$bridge"
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "ALTER EXTENSION $ext UPDATE TO '$avail'"
  echo "OK upgraded $ext $installed -> $avail in place"
}

phase_sync_extension() {
  echo "===== PHASE — SYNC EXTENSION SQL ====="
  local avail installed share

  # laplace_geom FIRST. Substrate SQL — including index expressions, which are
  # evaluated immediately at CREATE INDEX and hard-fail rather than degrading —
  # can reference geom functions, so geom must already carry them. This ordering
  # is the fix for the class of failure where a substrate upgrade referenced a
  # geom function that existed in the build tree but not in the database.
  sync_one_extension laplace_geom

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
    # install -m 664 (not cp): group-writable so a leftover bridge script can be
    # refreshed; cmake install already ships extension SQL as 0664.
    local bridge="$share/laplace_substrate--${installed}--${avail}.sql"
    install -m 664 "$share/laplace_substrate_upgrade.sql" "$bridge"
    # Fail fast if on-disk .so is missing any C symbol the upgrade SQL binds.
    # Usual cause: install wrote a new .so but shared_preload still holds the
    # old image (or build tree was never reinstalled).
    local so="$LAPLACE_INSTALL_PREFIX/lib/postgresql/18/laplace_substrate.so"
    if [[ -f "$so" ]] && command -v nm >/dev/null 2>&1; then
      # ONE nm, buffered. The per-symbol `nm | grep -q` form under pipefail
      # was a coin flip: grep -q exits on first match, nm takes SIGPIPE (141),
      # and pipefail turns the successful match into a spurious "lacks
      # <symbol>" — a DIFFERENT phantom symbol each run, observed twice on
      # 2026-08-01 (consensus_fold_final, then highway_ready; both present in
      # the image by hand-check). Buffering also spawns 2 processes instead
      # of 2 per required symbol.
      local sym nm_out
      nm_out="$(nm -D "$so" 2>/dev/null || true)"
      while IFS= read -r sym; do
        [[ -z "$sym" ]] && continue
        if ! grep -q "T ${sym}\$" <<< "$nm_out"; then
          echo "::error::installed $so lacks $sym but $bridge requires it — rebuild+install (preload bounce) before sync-extension" >&2
          exit 1
        fi
      done < <(grep -oE "'pg_laplace_[A-Za-z0-9_]+'" "$bridge" | tr -d "'" | sort -u)
    fi
    # BOUNCE ON DEMAND, NOT ON PRINCIPLE.
    #
    # This used to restart PostgreSQL unconditionally whenever laplace_substrate
    # was in shared_preload_libraries — which it always is. So every SQL-only
    # change (a rewritten function body, a new .sql.in, a regress fixture) paid a
    # full cluster bounce, and on any host where the postmaster is not signalable
    # by the invoking user the phase simply could not complete. That is an
    # arbitrary restriction: a preloaded image only goes stale when the SQL binds
    # a C symbol the RUNNING image does not export. Adding no symbols needs no
    # bounce.
    #
    # The exact condition is already tested above (the nm -D loop over the
    # bridge's pg_laplace_* symbols) — but that inspects the ON-DISK .so, which
    # install may have just replaced under a postmaster still mapping the old
    # inode. So probe the LIVE image instead: ALTER EXTENSION UPDATE runs in one
    # transaction and rolls back whole on failure, so attempting it is free. If it
    # fails specifically because a symbol is unresolvable in the loaded library,
    # THAT is the bounce signal — restart and retry once. Any other failure is a
    # real error and must not be masked by a restart.
    local upd_log rc=0
    upd_log=$(mktemp)
    if ! psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
           -c "ALTER EXTENSION laplace_substrate UPDATE TO '$avail'" >"$upd_log" 2>&1; then
      if grep -q 'could not find function\|could not load library\|undefined symbol' "$upd_log"; then
        echo "sync-extension: loaded image lacks a symbol this SQL binds — bounce required"
        cat "$upd_log"
        restart_postgres "sync-extension: preloaded .so is stale for the new SQL"
        psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
          -c "ALTER EXTENSION laplace_substrate UPDATE TO '$avail'" || rc=$?
      else
        cat "$upd_log" >&2
        rc=1
      fi
    fi
    rm -f "$upd_log"
    [[ "$rc" -eq 0 ]] || { echo "::error::laplace_substrate UPDATE to $avail failed" >&2; exit "$rc"; }
    echo "OK upgraded laplace_substrate $installed -> $avail in place"
  else
    echo "OK laplace_substrate already at $avail"
  fi
  verify_c_symbols
}

# Orphan gate. The install/upgrade SQL is CREATE-OR-REPLACE over the manifest:
# it adds and replaces, it never drops. A C function removed from the manifest
# (or renamed) without a matching drop_retired_*.sql.in stays in the catalog
# bound to a symbol the freshly installed .so no longer exports — it errors only
# when someone calls it. sync-extension's existing nm check covers symbols the
# UPGRADE bridge binds; it cannot see rows already in the catalog. This closes
# that gap: EVERY laplace C-bound function must resolve its symbol in the loaded
# image, or the phase fails loudly here instead of at some user's first call.
#
# Scoped by probin, not by schema. The schema is where a function is NAMED; the
# probin is which image it is supposed to resolve in, and that is what this gate
# is actually asserting. Another extension installed into the laplace schema —
# file_fdw, which the ops log surface needs (#601) — has its own .so and its own
# symbols, and checking its functions against laplace_substrate.so declared them
# orphaned when nothing was wrong: two false failures that took the whole
# deploy down and blocked publish. Any extension we host in this schema hits the
# same wall, so the fix belongs here rather than in a per-extension exclusion.
verify_c_symbols() {
  local so sym missing=0
  so="$LAPLACE_INSTALL_PREFIX/lib/postgresql/18/laplace_substrate.so"
  [[ -f "$so" ]] || { echo "::error::verify_c_symbols: $so not found"; return 1; }
  command -v nm >/dev/null 2>&1 || { echo "verify_c_symbols: nm unavailable — skipped"; return 0; }
  echo "===== GATE — C symbol integrity ====="

  # READ THE SYMBOL TABLE ONCE. This used to run `nm -D "$so" | grep` INSIDE the
  # loop — 74 nm invocations against the same unchanged file — and treated a
  # non-match as proof the symbol was absent. Those are not the same thing: any
  # single transient nm failure (fork pressure during a parallel build, a signal,
  # an interrupted read) produces empty output, the grep fails, and the gate
  # reports a healthy function as an "orphaned C function" telling the operator to
  # write a drop_retired_*.sql.in for something that is present.
  #
  # Observed exactly that 2026-07-28: two consecutive runs against a byte-identical
  # .so accused two DIFFERENT symbols (pg_laplace_substrate_version, then
  # pg_laplace_attestations_exist_bitmap), both verifiably exported. A gate whose
  # verdict changes run to run on identical inputs is worse than no gate — it
  # trains you to ignore it.
  local symtab
  symtab=$(mktemp)
  if ! nm -D --defined-only "$so" 2>/dev/null | awk '$2=="T" {print $3}' | sort -u >"$symtab" \
     || [[ ! -s "$symtab" ]]; then
    rm -f "$symtab"
    echo "::error::verify_c_symbols: could not read the symbol table of $so — nm failed or exported nothing" >&2
    return 1
  fi

  while IFS= read -r sym; do
    [[ -z "$sym" ]] && continue
    if ! grep -qxF "$sym" "$symtab"; then
      echo "::error::orphaned C function: laplace catalog binds '${sym}' but the installed .so does not export it — a manifest removal is missing its drop_retired_*.sql.in" >&2
      missing=$((missing+1))
    fi
  done < <(psql -d "$PGDATABASE" -U laplace_admin -tAX -c \
      "SELECT p.prosrc FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace \
       WHERE n.nspname='laplace' AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='c') \
         AND p.probin = '\$libdir/laplace_substrate'")
  rm -f "$symtab"
  if [[ "$missing" -gt 0 ]]; then
    echo "::error::verify_c_symbols: $missing orphaned C function(s) — catalog and .so disagree" >&2
    return 1
  fi
  echo "OK all laplace C functions resolve in the loaded image"
}

phase_tune_pg() {
  echo "===== PHASE — TUNE PG ====="
  # shellcheck source=scripts/pg-machine-tuning.sh
  source "$ROOT/scripts/pg-machine-tuning.sh"
  PG_TUNE_PSQL=(psql -d "${PGDATABASE:-laplace}" -U laplace_admin)
  pg_apply_machine_tuning
  local pending
  pending=$(pg_tune_psql -tAc "SELECT count(*) FROM pg_settings WHERE pending_restart")
  if [[ "$pending" != "0" ]]; then
    restart_postgres "tune-pg: $pending setting(s) pending"
  fi
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
  # The substrate tables are partitioned: storage parameters are illegal on a
  # partitioned parent, and autoanalyze reads LEAF-level settings — so both
  # tunings walk pg_partition_tree and hit every leaf. Idempotent; re-running
  # after new partitions appear tunes them too. (pg_partition_tree on a plain
  # table returns the table itself, so this stays correct either way.)
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
  echo "tune-laplace: applied stat + autoanalyze tuning across all leaf partitions."
}

phase_perfcache_guc() {
  echo "===== PHASE — PERFCACHE GUC ====="
  local bin hwbin chessbin
  bin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$bin" || { echo "::error::t0 perfcache blob not installed under $LAPLACE_INSTALL_PREFIX/share/laplace"; exit 1; }
  # The highway perfcache is built + installed (engine/core/CMakeLists.txt:206) and required
  # by the highway/band SQL, but this Linux phase historically only wired the T0 GUC — Windows
  # install-extensions.cmd sets BOTH. So highway_perfcache_path stayed empty (default) on
  # hart-server and the band-mask path never used its perfcache. Wire it here too.
  hwbin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_highway_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$hwbin" || { echo "::error::highway perfcache blob not installed under $LAPLACE_INSTALL_PREFIX/share/laplace"; exit 1; }
  # GH #822 — chess position_id → coord floor. Same class of omission as highway:
  # the blob was built/installed and chess.position_ready() stayed false because
  # the GUC was never pointed at it.
  chessbin=$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_chess_position_perfcache*.bin' 2>/dev/null | sort -V | tail -1)
  test -n "$chessbin" || { echo "::error::chess position perfcache blob not installed under $LAPLACE_INSTALL_PREFIX/share/laplace"; exit 1; }
  psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
    -c "LOAD 'laplace_substrate'" \
    -c "ALTER SYSTEM SET laplace_substrate.perfcache_path = '$bin'" \
    -c "ALTER SYSTEM SET laplace_substrate.highway_perfcache_path = '$hwbin'" \
    -c "ALTER SYSTEM SET laplace_substrate.chess_position_perfcache_path = '$chessbin'" \
    -c "SELECT pg_reload_conf()"
  echo "perfcache_path -> $bin"
  echo "highway_perfcache_path -> $hwbin"
  echo "chess_position_perfcache_path -> $chessbin"
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

  # GH #657: shared ops CSV dir + file_fdw repoint so ops.app_log() is live.
  local ops_log_dir="${LAPLACE_OPS_LOG_DIR:-$LAPLACE_INSTALL_PREFIX/app/logs}"
  mkdir -p "$ops_log_dir"
  chmod 2775 "$ops_log_dir" 2>/dev/null || true
  if grep -q '^LAPLACE_OPS_LOG_DIR=' "$env_file"; then
    sed -i "s|^LAPLACE_OPS_LOG_DIR=.*|LAPLACE_OPS_LOG_DIR=$ops_log_dir|" "$env_file"
  else
    printf '\nLAPLACE_OPS_LOG_DIR=%s\n' "$ops_log_dir" >> "$env_file"
  fi
  # Drop the misnamed LAPLACE_LOG_DIR if present — the code never reads it.
  if grep -q '^LAPLACE_LOG_DIR=' "$env_file"; then
    sed -i '/^LAPLACE_LOG_DIR=/d' "$env_file"
  fi
  echo "LAPLACE_OPS_LOG_DIR -> $ops_log_dir"
  if command -v psql >/dev/null 2>&1; then
    PGPASSWORD="${PGPASSWORD:-postgres}" psql -h "${PGHOST:-localhost}" -U "${PGUSER:-postgres}" \
      -d "${PGDATABASE:-laplace}" -v ON_ERROR_STOP=1 \
      -c "SELECT ops.repoint_app_log('$ops_log_dir'); SELECT ops.repoint_sql_gap('$ops_log_dir'); SELECT ops.repoint_chess_drops('$ops_log_dir');" \
      || echo "::warning::ops.repoint_* failed — ops.app_log/sql_gap/chess_drops stay unpointed until next successful api-env"
  fi
}

phase_chess_lab() {
  echo "===== PHASE — CHESS LAB (stockfish / Qt / cutechess / path env) ====="
  # Change-aware: the cutechess pin and this bootstrap are the only inputs, and
  # the cmake configure (Qt feature checks) dominates the cost. Skip only when
  # the fingerprint matches AND the installed binary actually exists — stamps
  # attest sources, never artifacts (the stale-.so lesson).
  local fp bin="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/bin/cutechess-cli"
  fp=$(fp_compute external/cutechess scripts/bootstrap-chess-lab.sh)
  if fp_check chess-lab "$fp" && [[ -x "$bin" ]]; then
    echo "chess-lab unchanged (pin + bootstrap fingerprint) and $bin present — skipping"
    return 0
  fi
  bash "$ROOT/scripts/bootstrap-chess-lab.sh"
  fp_record chess-lab "$fp"
}

# Materialize /opt/laplace/secrets from the job environment.
# CI source of truth: GitHub repository Secrets injected by laplace.yml publish
# (LICHESS_API, STRIPE_API_SECRET, STRIPE_WEBHOOK_SECRET) + optional var
# STRIPE_API_PUBLISHABLE. Machine ~/.config/shell/secrets.env is NOT a deploy path.
phase_runtime_secrets() {
  echo "===== PHASE — RUNTIME SECRETS DROP ====="
  local dst_dir="$LAPLACE_INSTALL_PREFIX/secrets"
  mkdir -p "$dst_dir"
  chmod 2770 "$dst_dir" 2>/dev/null || true
  local in_ci=0
  [ -n "${GITHUB_ACTIONS:-}" ] && in_ci=1

  local dst tok stripe_secret stripe_whsec missing=0
  dst="$dst_dir/lichess.env"
  # Canonical name matches operator .env: LICHESS_API. LICHESS_TOKEN accepted as alias.
  tok="${LICHESS_API:-${LICHESS_TOKEN:-}}"
  if [ -n "$tok" ]; then
    {
      printf 'LICHESS_API=%s\n' "$tok"
      printf 'LICHESS_TOKEN=%s\n' "$tok"
    } >"$dst"
    chmod 640 "$dst"
    echo "lichess.env written from job env"
  elif [ "$in_ci" -eq 1 ]; then
    echo "::error::LICHESS_API secret missing — set with: gh secret set LICHESS_API"
    missing=1
  elif [ -f "$dst" ]; then
    echo "lichess.env kept (local drop; not refreshed)"
  else
    echo "::warning::no lichess.env — set GitHub secret LICHESS_API for CI publish"
  fi

  dst="$dst_dir/stripe.env"
  stripe_secret="${STRIPE_API_SECRET:-${LAPLACE_STRIPE_API_KEY:-}}"
  stripe_whsec="${STRIPE_WEBHOOK_SECRET:-${LAPLACE_STRIPE_WEBHOOK_SECRET:-}}"
  if [ -n "$stripe_secret" ]; then
    {
      printf 'STRIPE_API_SECRET=%s\n' "$stripe_secret"
      if [ -n "${STRIPE_API_Publishable:-${STRIPE_API_PUBLISHED:-${STRIPE_API_PUBLISHABLE:-}}}" ]; then
        printf 'STRIPE_API_Publishable=%s\n' "${STRIPE_API_Publishable:-${STRIPE_API_PUBLISHED:-$STRIPE_API_PUBLISHABLE}}"
      fi
      if [ -n "$stripe_whsec" ]; then
        printf 'STRIPE_WEBHOOK_SECRET=%s\n' "$stripe_whsec"
      fi
    } >"$dst"
    chmod 640 "$dst"
    echo "stripe.env written from job env (webhook_secret=$([ -n "$stripe_whsec" ] && echo set || echo missing))"
  elif [ "$in_ci" -eq 1 ]; then
    echo "::error::STRIPE_API_SECRET secret missing — set with: gh secret set STRIPE_API_SECRET"
    missing=1
  elif [ -f "$dst" ]; then
    echo "stripe.env kept (local drop; not refreshed)"
  else
    echo "::warning::no stripe.env — set GitHub secret STRIPE_API_SECRET for CI publish"
  fi

  if [ "$in_ci" -eq 1 ] && [ -z "$stripe_whsec" ] && [ -n "$stripe_secret" ]; then
    echo "::warning::STRIPE_WEBHOOK_SECRET unset — Checkout works; signed webhooks will fail until set"
  fi

  if [ "$missing" -eq 1 ]; then
    echo "::error::runtime secrets incomplete — push from Windows: cmd /c scripts\\win\\sync-github-secrets.cmd"
    return 1
  fi
}

# The publish input domain: everything deploy.sh reads. app/ covers both
# dotnet publish closures, web/ the SPA (openapi.json is generated FROM app/
# content, so app/ subsumes it), deploy/ the script + unit + nginx material.
fp_publish() {
  fp_compute app web deploy
}

phase_publish() {
  local fp app_dir="${LAPLACE_APP_DIR:-/opt/laplace/app}"
  source "$ROOT/deploy/linux/app-dir-contract.sh"
  laplace_require_app_dir_contract "$app_dir"
  echo "===== PHASE — PUBLISH (full runtime contract) ====="
  # Publish owns the whole target: chess binaries, secrets, API+SPA+uci.
  phase_chess_lab
  phase_runtime_secrets

  # Change-aware: skip the SPA build + dotnet publishes + rsync when the
  # publish domain is unchanged AND the deployed tree is intact. The stamp is
  # NOT written here — success for publish means "deployed, restarted, ready",
  # and the restart+readiness gate lives in the workflow, which records it via
  # `pipeline.sh publish-stamp` only after /health/ready passes. A deploy that
  # never went ready therefore re-deploys on the next run.
  fp=$(fp_publish)
  if fp_check publish "$fp" && [[ -x "$app_dir/laplace-uci" && -x "$app_dir/laplace-mcp" && -d "$app_dir/wwwroot" ]]; then
    echo "publish domain unchanged (app/ web/ deploy/) and $app_dir intact — skipping deploy"
    mkdir -p "$ROOT/build"
    printf 'skipped' >"$ROOT/build/.publish-action"
    return 0
  fi
  local deploy_args=()
  [[ "${LAPLACE_FORCE_NPM:-}" == "1" ]] && deploy_args+=(--force-npm)
  [[ "${LAPLACE_PUBLISH_SERIAL:-}" == "1" ]] && deploy_args+=(--serial)
  bash "$ROOT/deploy/linux/deploy.sh" "${deploy_args[@]}"
  mkdir -p "$ROOT/build"
  printf 'deployed' >"$ROOT/build/.publish-action"

  # Drift tripwire: publish restarts the unit but cannot reinstall it (rootless
  # runner, no sudo — by design). A stale unit ran the API without loading
  # stripe.env for weeks. Warn loudly; setup-host.sh owns the fix.
  local installed_unit=/etc/systemd/system/laplace-api.service
  local repo_unit="$ROOT/deploy/linux/laplace-api.service"
  if [[ -r "$installed_unit" && -f "$repo_unit" ]] && ! diff -q "$installed_unit" "$repo_unit" >/dev/null 2>&1; then
    echo "::warning title=laplace-api unit drift::installed unit differs from deploy/linux/laplace-api.service — run: sudo bash scripts/setup-host.sh"
    diff "$installed_unit" "$repo_unit" || true
  fi
}

phase_publish_stamp() {
  # Record the publish stamp — call ONLY after the restart + readiness gate
  # passed (the workflow does). Success-only stamping, end to end.
  fp_record publish "$(fp_publish)"
  echo "publish stamp recorded"
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
    --fresh-db)       FRESH_DB=1; shift ;;
    --force)          FORCE_FOUNDATION=1; shift ;;
    --force-codegen)  FORCE_CODEGEN=1; shift ;;
    --skip-codegen)   SKIP_CODEGEN=1; shift ;;
    --clean-first)    CLEAN_FIRST=1; shift ;;
    --force-rebuild)  FORCE_REBUILD=1; shift ;;
    --serial-tests)   SERIAL_TESTS=1; export LAPLACE_TEST_SERIAL=1; shift ;;
    --force-all)      export LAPLACE_FORCE_ALL=1; shift ;;
    -h|--help) usage ;;
    clean|codegen|build|install|migrate|sync-extension|tune-pg|tune-laplace|perfcache-guc|api-env|publish|publish-stamp|foundation|test)
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
    publish-stamp)  phase_publish_stamp ;;
    foundation)     phase_foundation ;;
    test)           phase_test ;;
  esac
done

echo "===== PIPELINE PHASES COMPLETE: ${PHASES[*]} ====="
