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
#                   secrets drop refresh, API + SPA + laplace-uci deploy
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

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

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
        perfcache-guc api-env publish foundation test

Options:
  --fresh-db        nuke DB before migrate
  --force           pass --force to ensure-foundation.sh
  --force-codegen   ignore attestation-law stamp; always run Python codegen
  --skip-codegen    skip codegen in build (CMake custom_command remains SoT)
  --clean-first     cmake --build --clean-first (rebuild objects, keep configure)
  --force-rebuild   wipe build/ then build (same as: clean build)
  --serial-tests    set LAPLACE_TEST_SERIAL=1 for the test phase
EOF
  exit 2
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
  local data_root="${LAPLACE_DATA_ROOT:-/vault/Data}"
  local ucd="${LAPLACE_UCD_PATH:-$data_root/UCD/Public/UCD/latest}"
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
    -DLAPLACE_UCD_CONFORMANCE_DIR="$ucd/ucd"
  LD_LIBRARY_PATH="$ROOT/build/engine/core:$ROOT/build/engine/dynamics:$ROOT/build/engine/synthesis:${LD_LIBRARY_PATH:-}" \
    cmake --build build "${build_flags[@]}"
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
  echo "===== PHASE — BUILD APP ====="
  ( cd "$ROOT/app" && dotnet build Laplace.slnx -c Release )
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
  umask 0002
  cmake --install build
  test -f "$LAPLACE_INSTALL_PREFIX/lib/liblaplace_core.so"
  # shared_preload_libraries pins the extension image in the postmaster. cmake
  # --install replaces the .so on disk, but CREATE FUNCTION / ALTER EXTENSION
  # still dlsym against the preloaded handle — so a newly-exported symbol
  # (e.g. pg_laplace_explore_web) faults as "could not find function ... in
  # file" until bounce. Same class as Windows install-extensions --recycle;
  # terminate-backends is not enough when the library is preloaded.
  local preload
  preload=$(psql -d postgres -U laplace_admin -tAc "SHOW shared_preload_libraries" 2>/dev/null || true)
  if [[ ",${preload// /}," == *",laplace_substrate,"* ]] \
     || [[ ",${preload// /}," == *",laplace_geom,"* ]]; then
    restart_postgres "install: refresh preloaded extension .so"
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
    # Defense in depth: deploy/install should already have bounced when the
    # extension is preloaded, but sync-extension is a separate CI job — if
    # install skipped the bounce (or a human ran sync alone), UPDATE still
    # needs a live image that matches the just-installed SQL.
    local preload
    preload=$(psql -d "$PGDATABASE" -U laplace_admin -tAc "SHOW shared_preload_libraries" 2>/dev/null || true)
    if [[ ",${preload// /}," == *",laplace_substrate,"* ]]; then
      restart_postgres "sync-extension: refresh preloaded laplace_substrate before UPDATE"
    fi
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
      local sym
      while IFS= read -r sym; do
        [[ -z "$sym" ]] && continue
        if ! nm -D "$so" 2>/dev/null | grep -q "T ${sym}\$"; then
          echo "::error::installed $so lacks $sym but $bridge requires it — rebuild+install (preload bounce) before sync-extension" >&2
          exit 1
        fi
      done < <(grep -oE "'pg_laplace_[A-Za-z0-9_]+'" "$bridge" | tr -d "'" | sort -u)
    fi
    psql -d "$PGDATABASE" -U laplace_admin -v ON_ERROR_STOP=1 \
      -c "ALTER EXTENSION laplace_substrate UPDATE TO '$avail'"
    echo "OK upgraded laplace_substrate $installed -> $avail in place"
  else
    echo "OK laplace_substrate already at $avail"
  fi
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

phase_chess_lab() {
  echo "===== PHASE — CHESS LAB (stockfish / Qt / cutechess / path env) ====="
  bash "$ROOT/scripts/bootstrap-chess-lab.sh"
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

phase_publish() {
  echo "===== PHASE — PUBLISH (full runtime contract) ====="
  # Publish owns the whole target: chess binaries, secrets, API+SPA+uci.
  phase_chess_lab
  phase_runtime_secrets
  local deploy_args=()
  [[ "${LAPLACE_FORCE_NPM:-}" == "1" ]] && deploy_args+=(--force-npm)
  [[ "${LAPLACE_PUBLISH_SERIAL:-}" == "1" ]] && deploy_args+=(--serial)
  bash "$ROOT/deploy/linux/deploy.sh" "${deploy_args[@]}"

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
    -h|--help) usage ;;
    clean|codegen|build|install|migrate|sync-extension|tune-pg|tune-laplace|perfcache-guc|api-env|publish|foundation|test)
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
    test)           phase_test ;;
  esac
done

echo "===== PIPELINE PHASES COMPLETE: ${PHASES[*]} ====="
