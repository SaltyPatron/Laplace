#!/usr/bin/env bash
# The monorepo's site on a machine Laplace-Operations declares (hart-server): the engine, its perf-caches and the
# PostgreSQL extensions built against the Operations cluster, installed under $LAPLACE_INSTALL_PREFIX, the database
# laplace made and migrated, the API + SPA published to $LAPLACE_APP_DIR and restarted, and read back.
#
# Installation, publication, maintenance and qualification share the declared
# PostgreSQL installation, storage roots and setup-owned restart triggers.
#
#   deploy/linux/site.sh                  every phase below, in order
#   deploy/linux/site.sh build install    one phase, or several
#   deploy/linux/site.sh seed             the foundation (scripts/ensure-foundation.sh) into the database
#   deploy/linux/site.sh reset seed       the database dropped and made again, then the foundation
#
# Phases: externals build install database extensions migrate app publish restart smoke   (and: configure migrate_app reset seed serve)
#         bundle: what the hosting host receives, after app; receive: on the hosting host, before publish
#         reconcile: drain installed highway work; chess-lab: update the declared external chess tools
#         qualification: build all components and run development suites without changing installed services
#         competitive: qualification, installation, database suites, model synthesis and authenticated live suites
#
# The machine is set up once by root with deploy/linux/site-host.sh (directories, the unit, nginx, the restart
# trigger); the runner that runs this is Laplace-Operations' agents.sh's (labels laplace,Laplace). Every value below is
# an environment variable with this machine's value as its default.
set -euo pipefail
umask 0002

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=deploy/linux/host-lock.sh
source "$ROOT/deploy/linux/host-lock.sh"
source "$ROOT/deploy/linux/native-runtime.sh"
# the Operations environment, when the runner did not already give it (runner/.env carries the build and work roots)
if [[ -z "${LAPLACE_PG_DIR:-}" && -r "${LAPLACE_OPERATIONS:-/repos/src/Laplace-Operations}/laplace.env" ]]; then
  # shellcheck disable=SC1090,SC1091
  set +u; . "${LAPLACE_OPERATIONS:-/repos/src/Laplace-Operations}/laplace.env"; set -u
fi
# the machine's own declaration (Laplace-Operations: /etc/laplace/machine.env), whichever way the rest arrived: where
# its databases are (LAPLACE_PGHOST, LAPLACE_APP_DBNAME, LAPLACE_APP_PGHOST), what the runner's environment does not carry
machine_values=$(python3 "$ROOT/scripts/machine-env.py")
eval "$machine_values"
unset machine_values

LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-${LAPLACE_PG_DIR:-/usr/local/pgsql}}"
LAPLACE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
LAPLACE_APP_DIR="${LAPLACE_APP_DIR:-$LAPLACE_INSTALL_PREFIX/app}"
LAPLACE_DEPSRC="${LAPLACE_DEPSRC:-/repos/src}"
# LAPLACE_DATA_ROOT is persistent installation state (also on Windows), not
# the corpus estate. Keep source material on its independently declared root.
export LAPLACE_DATA="${LAPLACE_DATA:-/vault/Data}"
SITE_WORK="${LAPLACE_SITE_WORK:-${LAPLACE_WORK:-/repos/work}/mono-site}"
SITE_BUILD="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:-/repos/build}/Laplace/release}"
# the PostGIS the cluster runs is already installed; laplace_geom has been removed from this substrate
LAPLACE_LOCKS="${LAPLACE_LOCKS:-/run/lock/laplace}"
LAPLACE_API_URL="${LAPLACE_API_URL:-http://127.0.0.1:5187}"
LAPLACE_SITE_URL="${LAPLACE_SITE_URL:-http://127.0.0.1:8080}"
# the MCP endpoint (laplace-mcp.service): its loopback port, and the one browser origin it answers (a caller that
# sends no Origin, as MCP clients do, is judged by its bearer token alone)
LAPLACE_MCP_HTTP_PORT="${LAPLACE_MCP_HTTP_PORT:-5190}"   # 5188 is Laplace-MCP's lpm serve where a host runs it, 5189 the Lichess service
LAPLACE_MCP_ORIGIN="${LAPLACE_MCP_ORIGIN:-https://mcp.hartonomous.com}"
LAPLACE_MCP_URL="http://127.0.0.1:$LAPLACE_MCP_HTTP_PORT"

export PGHOST="${LAPLACE_PGHOST:-/tmp}" PGPORT="${LAPLACE_PGPORT:-5432}" PGUSER="${LAPLACE_DATABASE_ROLE:-${LAPLACE_ROLE:-laplace}}"
_runtime_db="$(printf '%s' "${LAPLACE_DATABASE_RUNTIME_DB:-}" | sed -nE 's/.*[Dd]atabase=([^;]+).*/\1/p')"
export LAPLACE_DBNAME="${LAPLACE_DBNAME:-${_runtime_db:-laplace}}"
unset _runtime_db
export PGDATABASE="$LAPLACE_DBNAME"
export PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-10}"
export LAPLACE_DB="Host=$PGHOST;Port=$PGPORT;Username=$PGUSER;Database=$LAPLACE_DBNAME${LAPLACE_DB_SSLMODE:+;SSL Mode=$LAPLACE_DB_SSLMODE}"
# The application's own database (the app schema: accounts, sessions, keys, billing) when the host declares one:
# LAPLACE_APP_DBNAME names it, LAPLACE_APP_PGHOST/PGPORT/ROLE say where (default: the same server). The migrator
# makes it and routes the app schema's scripts to it; the API reads it as LAPLACE_APP_DB. Unset: one database.
if [[ -n "${LAPLACE_APP_DBNAME:-}" ]]; then
  export LAPLACE_APP_DB="Host=${LAPLACE_APP_PGHOST:-$PGHOST};Port=${LAPLACE_APP_PGPORT:-$PGPORT};Username=${LAPLACE_APP_ROLE:-${LAPLACE_ROLE:-laplace}};Database=$LAPLACE_APP_DBNAME"
fi
export LAPLACE_EXTERNAL="${LAPLACE_EXTERNAL:-$SITE_WORK/external}"
export LAPLACE_INSTALL_PREFIX LAPLACE_ENGINE_BUILD="$SITE_BUILD/engine"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$LAPLACE_PG_PREFIX/bin:$PATH"

# The PostgreSQL cluster's own build, where there is one (the database host): the extensions are built against it and
# installed under this prefix for its major. A hosting host has no such build and runs only receive, publish, restart
# and smoke, which need none of this; a phase that does need it says so when it runs.
if [[ -x "$LAPLACE_PG_PREFIX/bin/pg_config" ]]; then
  PG_MAJOR="$("$LAPLACE_PG_PREFIX/bin/pg_config" --version | sed -E 's/^PostgreSQL ([0-9]+).*/\1/')"
else
  PG_MAJOR=""
fi
EXT_LIBDIR="$LAPLACE_INSTALL_PREFIX/lib/postgresql/${PG_MAJOR:-none}"
EXT_SHARE="$LAPLACE_INSTALL_PREFIX/share/postgresql/${PG_MAJOR:-none}"
need_pg() { [[ -n "$PG_MAJOR" ]] || fail "no PostgreSQL build at $LAPLACE_PG_PREFIX: this phase runs on the database host"; }
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"

say()  { echo; echo "=== $*   $(date -u +%H:%M:%S)"; }
fail() { echo "::error::$*" >&2; exit 1; }
host_lock() {
  local path="${LAPLACE_HOST_LOCK:-$LAPLACE_LOCKS/host-resource.lock}" mode="${1:-exclusive}"
  local wait="${LAPLACE_HOST_LOCK_WAIT_SECONDS:-0}" rc=0
  [[ "$wait" == wait || "$wait" =~ ^[0-9]+$ ]] || fail "LAPLACE_HOST_LOCK_WAIT_SECONDS must be a nonnegative integer or wait"
  case "$mode" in shared|exclusive) ;; *) fail "invalid host lock mode: $mode" ;; esac
  [[ -d "$(dirname "$path")" ]] || fail "missing shared host lock directory: $path"
  host_lock_hold "$mode" 8 "$path" "$wait" || rc=$?      # through the turnstile: a waiting deployment is not overtaken
  case $rc in
    0) ;;
    3) fail "host lock deadline expired: $path; no deployment phase ran" ;;
    *) fail "host lock could not be acquired (status $rc): $path; no deployment phase ran" ;;
  esac
}
revision() {
  local app="$1" rev web
  [[ -r "$app/.laplace-source-revision" && -r "$app/wwwroot/.laplace-web-source-revision" ]] || fail "missing application or web revision receipt in $app"
  rev=$(cat "$app/.laplace-source-revision"); web=$(cat "$app/wwwroot/.laplace-web-source-revision")
  [[ "$rev" =~ ^[0-9a-f]{40}$ && "$rev" == "$web" ]] || fail "invalid or mismatched application/web revisions in $app"
  [[ -z "${GITHUB_SHA:-}" || "$rev" == "$GITHUB_SHA" ]] || fail "bundle revision $rev differs from workflow revision $GITHUB_SHA"
  printf '%s\n' "$rev"
}
sql()  { psql -X -v ON_ERROR_STOP=1 -At "$@"; }
oneapi() {
  [[ -n "${SETVARS_COMPLETED:-}" ]] && return 0
  [[ -r /opt/intel/oneapi/setvars.sh ]] || fail "oneAPI is not at /opt/intel/oneapi"
  set +u; . /opt/intel/oneapi/setvars.sh >/dev/null 2>&1 || true; set -u
}

# ------------------------------------------------------------------------------------------------ externals
# The pinned sources the build reads under one root (LAPLACE_EXTERNAL), as links to what the machine already has:
# the Operations dependency sources, the distribution's Eigen, the grammars under the data. liblwgeom and GEOS/PROJ
# are found under one deps prefix the way laplace_geom looks for them (<prefix>/{geos,proj,pgsql-18}).
phase_externals() {
  say externals
  local e="$LAPLACE_EXTERNAL" name target
  mkdir -p "$e"
  while read -r name target; do
    # A configured dependency root can hold the source itself. Never create a
    # nested link inside that checkout or replace it with the default mapping.
    if [[ -d "$e/$name" && ! -L "$e/$name" ]]; then
      printf '  %-22s %s\n' "$name" "$e/$name"
      continue
    fi
    [[ -e "$target" ]] || fail "external $name: $target is not on this machine"
    ln -sfnT "$target" "$e/$name"; printf '  %-22s %s\n' "$name" "$target"
  done <<EOF
blake3                $LAPLACE_DEPSRC/blake3
laplace-native        ${LAPLACE_NATIVE:-$LAPLACE_DEPSRC/Laplace-Native}
tree-sitter           $LAPLACE_DEPSRC/tree-sitter
fathom                $LAPLACE_DEPSRC/Fathom
spectra               $LAPLACE_DEPSRC/spectra
eigen                 ${LAPLACE_EIGEN:-/usr/include/eigen3}
tree-sitter-grammars  $LAPLACE_DATA/TreeSitter
EOF
}

# ------------------------------------------------------------------------------------------------ build
# configure_native_build_tree's configuration (scripts/pipeline.sh), against the Operations cluster, staged install.
phase_build() {
  need_pg
  say "build: engine, perf-caches, extensions"
  oneapi
  local ucd="${LAPLACE_UCD_PATH:-$LAPLACE_DATA/UCD/Public/UCD/latest}"
  local recipe_engine="${LAPLACE_RECIPE_ENGINE_SOURCE:-$LAPLACE_DEPSRC/Laplace-Engine}"
  local grammars="${LAPLACE_GRAMMARS:-/repos/build/grammars}"
  local operations_source="${LAPLACE_OPERATIONS:-$LAPLACE_DEPSRC/Laplace-Operations}"
  local operations_contract="$SITE_BUILD/operations-build-contract"
  local native_source="${LAPLACE_NATIVE:-$LAPLACE_EXTERNAL/laplace-native}"
  local eigen_dir="$LAPLACE_EXTERNAL/eigen"
  [[ -f "$eigen_dir/Eigen/Core" ]] || eigen_dir=/usr/include/eigen3
  bash "$ROOT/scripts/ci/materialize-operations-contract.sh" "$operations_source" "$operations_contract"
  export LAPLACE_OPERATIONS="$operations_contract"
  local grammar_source="${LAPLACE_GRAMMAR_SRC:-$LAPLACE_EXTERNAL/tree-sitter-grammars}"
  grammar_source=$(cd "$grammar_source" && pwd -P)
  if [[ ! -f "$grammars/libtree-sitter-json.so" ]]; then
    LAPLACE_GRAMMAR_SRC="$grammar_source" \
    LAPLACE_TREESITTER="${LAPLACE_TREESITTER:-$LAPLACE_DEPSRC/tree-sitter}" \
      bash "$operations_contract/grammars.sh" "$grammars"
  fi
  LAPLACE_GRAMMAR_SRC="$grammar_source" \
  LAPLACE_TREESITTER="${LAPLACE_TREESITTER:-$LAPLACE_DEPSRC/tree-sitter}" \
    bash "$recipe_engine/tools/build_grammars.sh" "$recipe_engine/recipes/formats/grammars" "$grammars"

  local native_pkg
  native_pkg=$(LAPLACE_SRC="$LAPLACE_DEPSRC" \
    LAPLACE_BUILD="${LAPLACE_BUILD:-/repos/build}" \
    LAPLACE_ICU_DIR="${LAPLACE_ICU_DIR:-${LAPLACE_DEPS:-/repos/deps}/icu78}" \
    LAPLACE_COREMATH="${LAPLACE_COREMATH:-$LAPLACE_DEPSRC/core-math}" \
    LAPLACE_BLAKE3_DIR="${LAPLACE_BLAKE3_DIR:-$LAPLACE_DEPSRC/blake3/c}" \
    LAPLACE_EIGEN_DIR="$eigen_dir" \
    LAPLACE_SPECTRA_DIR="$LAPLACE_EXTERNAL/spectra" \
    bash "$operations_source/native-package.sh" "$native_source") 8>&-

  mkdir -p "$SITE_BUILD/csharp"
  if [[ -s "${LAPLACE_BUILD:-/repos/build}/Laplace-Native/package/csharp/LaplaceNative.g.cs" && -s "${LAPLACE_BUILD:-/repos/build}/Laplace-Native/package/csharp/Abi.g.cs" ]]; then
    cp -f "${LAPLACE_BUILD:-/repos/build}/Laplace-Native/package/csharp/"*.g.cs "$SITE_BUILD/csharp/"
  else
    cmake -DCC=icx -DNODE="$(command -v node)" -DSOURCE="$native_source" -DOUT="$SITE_BUILD/csharp" -P "$native_source/tools/csharp/generate.cmake" 8>&-
  fi

  mkdir -p "$SITE_BUILD/share/laplace"
  local native_sha prepared_inputs
  native_sha=$(git -c "safe.directory=$native_source" -C "$native_source" rev-parse HEAD)
  prepared_inputs="${LAPLACE_BUILD%/agents/*}/native-prepared-inputs/$native_sha"
  if [[ -s "$prepared_inputs/tier0.bin" && -s "$prepared_inputs/tier0.flags" && ( -s "$prepared_inputs/tier0.highway" || -s "$prepared_inputs/highway.bin" ) ]]; then
    cp -f "$prepared_inputs/tier0.bin" "$SITE_BUILD/share/laplace/tier0.bin"
    cp -f "$prepared_inputs/tier0.flags" "$SITE_BUILD/share/laplace/tier0.flags"
    [[ ! -s "$prepared_inputs/tier0.flags.layout" ]] || cp -f "$prepared_inputs/tier0.flags.layout" "$SITE_BUILD/share/laplace/tier0.flags.layout"
    if [[ -s "$prepared_inputs/highway.bin" ]]; then
      cp -f "$prepared_inputs/highway.bin" "$SITE_BUILD/share/laplace/highway.bin"
    else
      cp -f "$prepared_inputs/tier0.highway" "$SITE_BUILD/share/laplace/highway.bin"
    fi
  else
    LAPLACE_TIER0="$SITE_BUILD/share/laplace/tier0.bin" \
    LAPLACE_FLAGS="$SITE_BUILD/share/laplace/tier0.flags" \
    LAPLACE_HIGHWAY="$SITE_BUILD/share/laplace/highway.bin" \
      bash "$operations_source/native-inputs.sh" prepare "$native_source" 8>&-
    [[ -s "$SITE_BUILD/share/laplace/highway.bin" || ! -s "$SITE_BUILD/share/laplace/tier0.highway" ]] \
      || cp -f "$SITE_BUILD/share/laplace/tier0.highway" "$SITE_BUILD/share/laplace/highway.bin"
  fi

  local engine_pkg
  engine_pkg=$(LAPLACE_SRC="$LAPLACE_DEPSRC" \
    LAPLACE_BUILD="${LAPLACE_BUILD:-/repos/build}" \
    LAPLACE_ICU_DIR="${LAPLACE_ICU_DIR:-${LAPLACE_DEPS:-/repos/deps}/icu78}" \
    LAPLACE_PG_DIR="$LAPLACE_PG_PREFIX" \
    LAPLACE_TREESITTER="${LAPLACE_TREESITTER:-$LAPLACE_DEPSRC/tree-sitter}" \
    LAPLACE_GRAMMARS="$grammars" \
    bash "$operations_source/package.sh" laplace-engine "$recipe_engine" \
      "LaplaceNative_DIR=$native_pkg/lib/cmake/LaplaceNative" \
      "LAPLACE_TIER0=$SITE_BUILD/share/laplace/tier0.bin" \
      "LAPLACE_RECIPES=$recipe_engine/recipes" \
      "LAPLACE_INSTALL_RECIPE_RESOURCES=ON" \
      "LAPLACE_CONNINFO=") 8>&-
  printf '%s\n' "$native_pkg" > "$SITE_BUILD/.native-package"
  printf '%s\n' "$engine_pkg" > "$SITE_BUILD/.engine-package"

  if [[ ! -s "$SITE_BUILD/share/laplace/tier0.chess" ]] || ! env -u LAPLACE_CHESS \
    LD_LIBRARY_PATH="$native_pkg/lib:${LD_LIBRARY_PATH:-}" \
    LAPLACE_TIER0="$SITE_BUILD/share/laplace/tier0.bin" \
    LAPLACE_FIRMWARE="$recipe_engine/firmware/program.firmware" \
    "$engine_pkg/bin/laplace" chess -o "$SITE_BUILD/share/laplace/tier0.chess" -t "$SITE_BUILD/share/laplace/tier0.bin" --fingerprint >/dev/null 2>&1; then
    if [[ -s /hosting/laplace/modular/current/share/prepared/tier0.chess ]] && env -u LAPLACE_CHESS \
      LD_LIBRARY_PATH="$native_pkg/lib:${LD_LIBRARY_PATH:-}" \
      LAPLACE_TIER0="$SITE_BUILD/share/laplace/tier0.bin" \
      LAPLACE_FIRMWARE="$recipe_engine/firmware/program.firmware" \
      "$engine_pkg/bin/laplace" chess -o /hosting/laplace/modular/current/share/prepared/tier0.chess -t "$SITE_BUILD/share/laplace/tier0.bin" --verify >/dev/null 2>&1; then
      cp -f /hosting/laplace/modular/current/share/prepared/tier0.chess "$SITE_BUILD/share/laplace/tier0.chess"
    else
      env -u LAPLACE_CHESS \
        LD_LIBRARY_PATH="$native_pkg/lib:${LD_LIBRARY_PATH:-}" \
        LAPLACE_TIER0="$SITE_BUILD/share/laplace/tier0.bin" \
        LAPLACE_FLAGS="$SITE_BUILD/share/laplace/tier0.flags" \
        LAPLACE_HIGHWAY="$SITE_BUILD/share/laplace/highway.bin" \
        LAPLACE_RECIPES="$recipe_engine/recipes" \
        LAPLACE_GRAMMARS="$grammars" \
        LAPLACE_MANIFEST="$native_source/manifest" \
        LAPLACE_FIRMWARE="$recipe_engine/firmware/program.firmware" \
        LAPLACE_DATA="$LAPLACE_DATA" \
        LAPLACE_CONNINFO="host=127.0.0.1 port=1 connect_timeout=1 dbname=unused" \
        "$engine_pkg/bin/laplace" chess -o "$SITE_BUILD/share/laplace/tier0.chess" -t "$SITE_BUILD/share/laplace/tier0.bin" -r "$recipe_engine/recipes" openings 8>&-
      env -u LAPLACE_CHESS \
        LD_LIBRARY_PATH="$native_pkg/lib:${LD_LIBRARY_PATH:-}" \
        LAPLACE_TIER0="$SITE_BUILD/share/laplace/tier0.bin" \
        LAPLACE_FIRMWARE="$recipe_engine/firmware/program.firmware" \
        "$engine_pkg/bin/laplace" chess -o "$SITE_BUILD/share/laplace/tier0.chess" -t "$SITE_BUILD/share/laplace/tier0.bin" --verify 8>&-
    fi
  fi

  cmake -U 'ICU_*' -S "$ROOT" -B "$SITE_BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$ROOT/cmake/toolchains/intel-oneapi.cmake" \
    -DLAPLACE_REQUIRE_MKL=ON -DBUILD_TESTING="${LAPLACE_SITE_BUILD_TESTING:-OFF}" \
    -DCMAKE_INSTALL_PREFIX="$LAPLACE_INSTALL_PREFIX" \
    -DLAPLACE_PG_PREFIX="$LAPLACE_PG_PREFIX" \
    -DLaplaceNative_DIR="$native_pkg/lib/cmake/LaplaceNative" \
    -DLAPLACE_ICU_DIR="${LAPLACE_ICU_DIR:-${LAPLACE_DEPS:-/repos/deps}/icu78}" \
    -DICU_ROOT="${LAPLACE_ICU_DIR:-${LAPLACE_DEPS:-/repos/deps}/icu78}" \
    -DLAPLACE_EXTERNAL="$LAPLACE_EXTERNAL" \
    -DLAPLACE_NATIVE="$native_source" \
    -DLAPLACE_OPERATIONS="$operations_contract" \
    -DLAPLACE_RECIPE_ENGINE_SOURCE="$recipe_engine" \
    -DLAPLACE_GRAMMARS="$grammars" \
    -DLAPLACE_EIGEN_DIR="$eigen_dir" \
    -DLAPLACE_SPECTRA_DIR="$LAPLACE_EXTERNAL/spectra" \
    -DLAPLACE_INSTALL_STAGED=ON \
    -DLAPLACE_UCD_PATH="$ucd" \
    -DLAPLACE_TIER0="${LAPLACE_INSTALL_PREFIX}/share/laplace/tier0.bin" \
    -DLAPLACE_UCDXML_ZIP="$ucd/ucdxml/ucd.all.grouped.zip" \
    -DLAPLACE_DUCET_FILE="$ucd/uca/allkeys.txt" \
    -DLAPLACE_UCD_CONFORMANCE_DIR="$ucd/ucd" 8>&-
  LD_LIBRARY_PATH="$SITE_BUILD:$native_pkg/lib:$LAPLACE_INSTALL_PREFIX/lib:${LD_LIBRARY_PATH:-}" \
    cmake --build "$SITE_BUILD" --target all 8>&-
  [[ -s "$SITE_BUILD/share/laplace/tier0.bin" || -s "$LAPLACE_INSTALL_PREFIX/share/laplace/tier0.bin" ]] || fail "tier-0 perf-cache not produced"
  [[ -s "$SITE_BUILD/share/laplace/highway.bin" || -s "$LAPLACE_INSTALL_PREFIX/share/laplace/highway.bin" ]] || fail "highway perf-cache not produced"
}

# ------------------------------------------------------------------------------------------------ install
# Staged with DESTDIR, then moved into the prefix file by file (rename: a backend or the API that has the old library
# mapped keeps its inode). The oneAPI runtime the libraries load (MKL core, its CPU kernels, the threading layer, the
# compiler runtime) is copied beside them: PostgreSQL loads the modules with plain dlopen and no oneAPI environment,
# and MKL dlopens its kernels by name (the HART-DESKTOP lesson: the runtime beside what loads it).
phase_install() {
  need_pg
  say "install into $LAPLACE_INSTALL_PREFIX"
  oneapi
  [[ -d "$LAPLACE_INSTALL_PREFIX" && -w "$LAPLACE_INSTALL_PREFIX" ]] || fail "$LAPLACE_INSTALL_PREFIX is missing or not writable: sudo deploy/linux/site-host.sh"
  local stage f rel n=0
  local native_pkg engine_pkg recipe_engine native_source grammars
  native_pkg=$(<"$SITE_BUILD/.native-package")
  engine_pkg=$(<"$SITE_BUILD/.engine-package")
  recipe_engine="${LAPLACE_RECIPE_ENGINE_SOURCE:-$LAPLACE_DEPSRC/Laplace-Engine}"
  native_source="${LAPLACE_NATIVE:-$LAPLACE_EXTERNAL/laplace-native}"
  grammars="${LAPLACE_GRAMMARS:-/repos/build/grammars}"
  stage="$(mktemp -d "$SITE_WORK/.install.XXXXXX")"
  DESTDIR="$stage" cmake --install "$SITE_BUILD" >/dev/null 8>&-
  install -D -m 0775 "$engine_pkg/bin/laplace" "$stage$LAPLACE_INSTALL_PREFIX/bin/laplace"
  mkdir -p "$stage$LAPLACE_INSTALL_PREFIX/lib" "$stage$LAPLACE_INSTALL_PREFIX/lib/grammars" "$stage$LAPLACE_INSTALL_PREFIX/bin/grammars" "$stage$LAPLACE_INSTALL_PREFIX/share/laplace"
  cp -a "$native_pkg"/lib/liblaplace.so* "$stage$LAPLACE_INSTALL_PREFIX/lib/"
  cp -a "$engine_pkg"/lib/liblaplace_engine_abi.so* "$stage$LAPLACE_INSTALL_PREFIX/lib/"
  local grammar_dir="$engine_pkg/bin/grammars"
  compgen -G "$grammar_dir/*.so*" >/dev/null || grammar_dir="$engine_pkg/lib/grammars"
  compgen -G "$grammar_dir/*.so*" >/dev/null || grammar_dir="$grammars"
  cp -fL "$grammar_dir"/*.so* "$stage$LAPLACE_INSTALL_PREFIX/lib/grammars/"
  cp -fL "$grammar_dir"/*.so* "$stage$LAPLACE_INSTALL_PREFIX/bin/grammars/"
  chmod 0775 "$stage$LAPLACE_INSTALL_PREFIX/lib/grammars"/*.so* "$stage$LAPLACE_INSTALL_PREFIX/bin/grammars"/*.so*
  for f in tier0.bin tier0.flags tier0.flags.layout highway.bin tier0.chess; do
    [[ ! -s "$SITE_BUILD/share/laplace/$f" ]] || install -D -m 0664 "$SITE_BUILD/share/laplace/$f" "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/$f"
  done
  local recipes_src="$engine_pkg/share/laplace/recipes"
  [[ -f "$recipes_src/order" ]] || recipes_src="$recipe_engine/recipes"
  mkdir -p "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/recipes"
  cp -a "$recipes_src/." "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/recipes/"
  local firmware_src="$engine_pkg/share/laplace/firmware"
  [[ -d "$firmware_src" ]] || firmware_src="$recipe_engine/firmware"
  mkdir -p "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/firmware"
  cp -a "$firmware_src/." "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/firmware/"
  mkdir -p "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/manifest"
  cp -a "$native_source/manifest/." "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/manifest/"
  rm -rf "$LAPLACE_INSTALL_PREFIX/lib/cmake/LaplaceNative"
  # The recipe tree is data no process maps, and the Engine loads every .recipe in a source's directory: it is
  # replaced whole, so a recipe removed from Laplace-Engine is gone from the installation too.
  [[ -d "$stage$LAPLACE_INSTALL_PREFIX/share/laplace/recipes" ]] && rm -rf "$LAPLACE_INSTALL_PREFIX/share/laplace/recipes"
  while IFS= read -r -d '' f; do
    rel="${f#"$stage$LAPLACE_INSTALL_PREFIX"/}"
    mkdir -p "$LAPLACE_INSTALL_PREFIX/$(dirname "$rel")"
    if [[ -L "$f" ]]; then ln -sfn "$(readlink "$f")" "$LAPLACE_INSTALL_PREFIX/$rel"; else mv -f "$f" "$LAPLACE_INSTALL_PREFIX/$rel"; fi
    n=$((n + 1))
  done < <(find "$stage$LAPLACE_INSTALL_PREFIX" \( -type f -o -type l \) -print0)
  rm -rf "$stage"
  echo "  $n files"
  # ldd resolves the transitive native dependencies too. Carry every dependency
  # except the host's C/C++ platform runtime; the receiving host has no build
  # prefixes from which to load ICU, libxml2, or another declared dependency.
  # MKL's dynamically selected kernels are added explicitly below.
  local lib resolved; local -A want=()
  local -a native_inputs=()
  while IFS= read -r -d '' lib; do native_inputs+=("$lib"); done < <(
    find "$LAPLACE_INSTALL_PREFIX/lib" -maxdepth 3 -name '*.so*' -type f -print0)
  [[ -x "$LAPLACE_INSTALL_PREFIX/bin/laplace" ]] || fail "Native recipe executable was not installed"
  native_inputs+=("$LAPLACE_INSTALL_PREFIX/bin/laplace")
  resolved=$(native_runtime_libraries "${native_inputs[@]}") || fail "Native release dependency discovery failed"
  while IFS= read -r lib; do [[ -z "$lib" ]] || want[$lib]=1; done <<< "$resolved"
  for lib in "${MKLROOT:-/opt/intel/oneapi/mkl/latest}"/lib/libmkl_{core,def,mc3,avx2,avx512,vml_def,vml_mc3,vml_avx2,vml_avx512,vml_cmpt,intel_thread,sequential,tbb_thread,intel_lp64,intel_ilp64}.so.[0-9]; do
    [[ -e "$lib" ]] && want[$lib]=1; done
  # SDL opens this runtime dynamically, so it may have no DT_NEEDED edge for
  # ldd to discover. Resolve it from the same compiler used for this release.
  local openmp_runtime
  openmp_runtime="$(icx -print-file-name=libiomp5.so)"
  [[ "$openmp_runtime" == /* && -s "$openmp_runtime" ]] || fail "the selected Intel compiler cannot resolve libiomp5.so"
  want[$openmp_runtime]=1
  for lib in "${!want[@]}"; do cp -fL "$lib" "$LAPLACE_INSTALL_PREFIX/lib/.$(basename "$lib").new" && mv -f "$LAPLACE_INSTALL_PREFIX/lib/.$(basename "$lib").new" "$LAPLACE_INSTALL_PREFIX/lib/$(basename "$lib")"; done
  echo "  native dependencies beside the libraries: ${#want[@]} files"
  LD_LIBRARY_PATH="$LAPLACE_INSTALL_PREFIX/lib" native_runtime_libraries "${native_inputs[@]}" >/dev/null \
    || fail "Native release dependencies do not resolve"
  # The opening catalog is a.tsv through e.tsv. A declared or partially present catalog installs whole or fails.
  local openings_src="${LAPLACE_CHESS_OPENINGS:-}" candidate letter
  if [[ -z "$openings_src" ]]; then
    for candidate in "$LAPLACE_DATA/Games/Chess/lichess-openings" "$LAPLACE_DATA/Games/Chess/openings"; do
      for letter in a b c d e; do [[ -e "$candidate/$letter.tsv" ]] && { openings_src="$candidate"; break 2; }; done
    done
  fi
  if [[ -n "$openings_src" ]]; then
    for letter in a b c d e; do
      [[ -s "$openings_src/$letter.tsv" ]] || fail "opening catalog $openings_src lacks $letter.tsv"
    done
    mkdir -p "$LAPLACE_INSTALL_PREFIX/share/laplace/chess-openings"
    cp -f "$openings_src"/{a,b,c,d,e}.tsv "$LAPLACE_INSTALL_PREFIX/share/laplace/chess-openings/" \
      || fail "cannot install the opening catalog from $openings_src"
    echo "  chess openings: $openings_src"
  else
    echo "  chess openings: no catalog under LAPLACE_CHESS_OPENINGS or $LAPLACE_DATA/Games/Chess; none installed"
  fi
  git -C "$ROOT" rev-parse HEAD > "$LAPLACE_INSTALL_PREFIX/lib/.laplace-source-revision"
}

# ------------------------------------------------------------------------------------------------ database
# The database on the Operations cluster. Its modules and control files are this prefix's, reached by the database's
# own dynamic_library_path and extension_control_path, so nothing of the monorepo goes into the cluster's directories
# or reaches another database. The perf-cache paths are server settings (PGC_SIGHUP) the extension alone reads.
phase_database() {
  need_pg
  say "database $LAPLACE_DBNAME"
  local q
  if [[ "$(sql -d postgres -c "SELECT 1 FROM pg_database WHERE datname = '$LAPLACE_DBNAME'")" != 1 ]]; then
    sql -d postgres -c "CREATE DATABASE \"$LAPLACE_DBNAME\"" >/dev/null; echo "  made"
  fi
  sql -d postgres \
    -c "ALTER DATABASE \"$LAPLACE_DBNAME\" SET dynamic_library_path = '$EXT_LIBDIR:\$libdir'" \
    -c "ALTER DATABASE \"$LAPLACE_DBNAME\" SET extension_control_path = '$EXT_SHARE:\$system'" \
    -c "ALTER DATABASE \"$LAPLACE_DBNAME\" RESET session_preload_libraries" >/dev/null
  # Clear any stale laplace_substrate.* settings an earlier site wrote so sessions never warn on reserved prefixes.
  local dir="$LAPLACE_INSTALL_PREFIX/share/laplace" g f q
  while IFS= read -r q; do
    [[ -n "$q" ]] || continue
    sql -d postgres -c "ALTER SYSTEM RESET \"$q\"" >/dev/null
    echo "  $q reset: no longer declared"
  done < <(sql -d postgres -c "SELECT DISTINCT name FROM pg_file_settings WHERE name LIKE 'laplace\\_substrate.%' AND sourcefile LIKE '%postgresql.auto.conf'")
  for g in tier0:tier0.bin flags:tier0.flags highway:highway.bin chess:tier0.chess; do
    f="$dir/${g#*:}"
    [[ -s "$f" ]] || fail "laplace.${g%%:*}: missing installed cache $f"
    sql -d postgres -c "ALTER DATABASE \"$LAPLACE_DBNAME\" SET laplace.${g%%:*} = '$f'" >/dev/null
    echo "  laplace.${g%%:*} = $f"
  done
  sql -d postgres -c "SELECT pg_reload_conf()" >/dev/null
}

phase_reset() {
  say "reset: $LAPLACE_DBNAME dropped"
  [[ "$LAPLACE_DBNAME" == laplace || "$LAPLACE_DBNAME" == laplace-mono* ]] || fail "reset drops only a laplace database, not '$LAPLACE_DBNAME'"
  sql -d postgres -c "DROP DATABASE IF EXISTS \"$LAPLACE_DBNAME\" WITH (FORCE)" >/dev/null
  phase_database
}

# ------------------------------------------------------------------------------------------------ migrate + extensions
# Compiler servers may outlive a build. The supervising shell owns the host lock;
# those servers must not retain its file descriptor after the phase completes.
dotnet_app() {
  local -a env_args=(LAPLACE_REUSE_INSTALLED_NATIVE=0)
  [[ ! -r "$SITE_BUILD/.native-package" ]] || env_args+=("LAPLACE_NATIVE_PACKAGE=$(<"$SITE_BUILD/.native-package")")
  [[ ! -r "$SITE_BUILD/.engine-package" ]] || env_args+=("LAPLACE_ENGINE_PACKAGE=$(<"$SITE_BUILD/.engine-package")")
  [[ ! -s "$SITE_BUILD/csharp/LaplaceNative.g.cs" ]] || env_args+=("LaplaceNativeGenerated=$SITE_BUILD/csharp")
  env "${env_args[@]}" dotnet "$@" 8>&-
}
# This checkout's migrator: the declared schema (db/schema) is read from the checkout, never from the installed release.
migrations() {
  local out="$SITE_WORK/migrations"
  [[ -n "${migrations_published:-}" ]] || {
    dotnet_app publish "$ROOT/app/Laplace.Migrations/Laplace.Migrations.csproj" -c Release --no-self-contained -o "$out" -v minimal --nologo
    migrations_published=1
  }
  ( cd "$ROOT" && LD_LIBRARY_PATH="$LAPLACE_INSTALL_PREFIX/lib" dotnet "$out/Laplace.Migrations.dll" "$@" )
}
phase_migrate() {
  need_pg
  say "migrate $LAPLACE_DBNAME"
  migrations up   # the declared schema is applied in full on every deploy
  [[ "$(sql -c "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind = 'p' AND ((n.nspname = 'public' AND c.relname = 'entity') OR (n.nspname = 'laplace' AND c.relname = 'entities'))")" -ge 1 ]] \
    || fail "partitioned entity storage is missing: the extension installed predates the schema"
}
# Compatible extension updates reconcile replaceable SQL/C bindings in place via
# storage-stamped reconcile scripts; only incompatible storage DDL invokes
# recreate_substrate.
phase_extensions() {
  need_pg
  say "extensions in $LAPLACE_DBNAME"
  local pg_sharedir ext_dir transition candidate
  pg_sharedir="$("$LAPLACE_PG_PREFIX/bin/pg_config" --sharedir)/extension"
  rm -f "$EXT_SHARE/extension"/laplace*--*--*.sql
  if [[ -w "$pg_sharedir" ]]; then rm -f "$pg_sharedir"/laplace*--*--*.sql; fi
  local ext avail installed module stale=()
  for ext in laplace; do
    avail="$(sql -c "SELECT default_version FROM pg_available_extensions WHERE name = '$ext'")"
    installed="$(sql -c "SELECT extversion FROM pg_extension WHERE extname = '$ext'")"
    [[ -n "$avail" ]] || fail "$ext is not available to $LAPLACE_DBNAME (extension_control_path)"
    if [[ -z "$installed" ]]; then installed="none yet: the migrations make it at $avail"
    else
      if [[ "$installed" != "$avail" ]]; then
        ext_dir='' transition=''
        for candidate in "$EXT_SHARE/extension" "$pg_sharedir"; do
          if [[ -f "$candidate/$ext.reconcile.sql" && -f "$candidate/$ext.verify-storage.sql" ]]; then
            ext_dir="$candidate"; break
          fi
        done
        if [[ -n "$ext_dir" ]] && [[ "$(sql -f "$ext_dir/$ext.verify-storage.sql" 2>/dev/null || true)" == ok ]]; then
          transition="$ext_dir/${ext}--${installed}--${avail}.sql"
          cp -f "$ext_dir/$ext.reconcile.sql" "$transition"
        fi
        if [[ "$(sql -c "SELECT count(*) FROM pg_extension_update_paths('$ext') WHERE source = '$installed' AND target = '$avail' AND path IS NOT NULL")" != 1 ]]; then
          [[ -z "$transition" ]] || rm -f "$transition"
          stale+=("$ext $installed -> $avail"); continue
        fi
        if ! PGOPTIONS="-c lock_timeout=${LAPLACE_DDL_LOCK_TIMEOUT:-20s}" sql -c "ALTER EXTENSION $ext UPDATE TO '$avail'" >/dev/null; then
          [[ -z "$transition" ]] || rm -f "$transition"
          return 1
        fi
        [[ -z "$transition" ]] || rm -f "$transition"
        installed="$installed -> $avail (in place)"
      fi
      if [[ "$ext" == laplace ]]; then
        module=''
        for candidate in "$EXT_SHARE/extension/laplace.control" "$pg_sharedir/laplace.control"; do
          if [[ -f "$candidate" ]]; then
            module="$(sed -nE "s/^module_pathname[[:space:]]*=[[:space:]]*'([^']+)'.*/\1/p" "$candidate" | head -1)"
            [[ -z "$module" ]] || break
          fi
        done
        [[ -n "$module" ]] || fail "laplace.control has no module_pathname"
        PGOPTIONS="-c lock_timeout=${LAPLACE_DDL_LOCK_TIMEOUT:-20s}" sql \
          -c "CREATE OR REPLACE FUNCTION public.laplace_restore_function_bindings() RETURNS void LANGUAGE c AS '$module', 'laplace_restore_function_bindings'" \
          -c "SELECT public.laplace_restore_function_bindings()" >/dev/null
      fi
    fi
    echo "  $ext $installed"
  done
  [[ ${#stale[@]} -eq 0 ]] || recreate_substrate "${stale[@]}"
}

# The substrate database recreated from this checkout's declaration (Laplace.Migrations nuke): a replacement made with
# a fresh CREATE EXTENSION and the declared substrate schema, carrying the database's settings and grants, swapped in
# by name; the old database is dropped. The deployment declares whether a version change may recreate its substrate
# (LAPLACE_SUBSTRATE_RECREATE, machine.env or the delivery's environment):
#   version                          the substrate only; a database that also holds the application schema
#                                    (LAPLACE_APP_DBNAME unset or naming it) is refused
#   version-including-application    also a database that still holds the application schema, whose accounts, keys
#                                    and billing are then made again from the declaration (a host whose application
#                                    data is disposable, until it has an application database of its own)
#   unset                            refuse; recreate deliberately (Database maintenance workflow, operation=recreate)
recreate_substrate() {
  local why
  why="$(printf '%s, ' "$@")"; why="${why%, }"
  case "${LAPLACE_SUBSTRATE_RECREATE:-}" in
    version|version-including-application) ;;
    *) fail "$LAPLACE_DBNAME carries $why and extensions have no upgrade path; this host does not declare LAPLACE_SUBSTRATE_RECREATE=version: recreate it deliberately (Database maintenance workflow, database=substrate, operation=recreate)" ;;
  esac
  if [[ -z "${LAPLACE_APP_DBNAME:-}" || "$LAPLACE_APP_DBNAME" == "$LAPLACE_DBNAME" ]]; then
    [[ "$LAPLACE_SUBSTRATE_RECREATE" == version-including-application ]] \
      || fail "$LAPLACE_DBNAME carries $why and extensions have no upgrade path, but it also holds the application schema (LAPLACE_APP_DBNAME=${LAPLACE_APP_DBNAME:-unset}): give the application a database of its own before the substrate is recreated"
    say "recreate $LAPLACE_DBNAME ($why): substrate and the application schema it still holds"
  else
    say "recreate $LAPLACE_DBNAME ($why): substrate only, the application database $LAPLACE_APP_DBNAME is kept"
  fi
  migrations nuke --yes
}

# ------------------------------------------------------------------------------------------------ app + publish
# Everything is built and staged before the serving copy is touched (publish building before copy): the API with its
# native closure, the SPA, the migrations; then one sync into the app directory and a restart.
phase_app() {
  say "app: API, SPA, migrations, staged"
  local stage="$SITE_WORK/stage"
  rm -rf "$stage"; mkdir -p "$stage"
  dotnet_app publish "$ROOT/app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj" -c Release --no-self-contained -o "$stage/app" -v minimal --nologo
  dotnet_app publish "$ROOT/app/Laplace.Migrations/Laplace.Migrations.csproj" -c Release --no-self-contained -o "$stage/app/migrations" -v minimal --nologo
  mkdir -p "$stage/app/migrations/db/schema"
  cp -a "$ROOT/db/schema/." "$stage/app/migrations/db/schema/"
  # Runtime verification travels with the release; an installed host needs no
  # source checkout or build workspace to prove its authenticated native path.
  mkdir -p "$stage/app/operations/scripts/lib" "$stage/app/operations/deploy/linux"
  cp "$ROOT/scripts/verify-application-release.py" "$ROOT/scripts/install-stockfish.py" \
    "$ROOT/scripts/installation_paths.py" "$ROOT/scripts/machine-env.py" "$stage/app/operations/scripts/"
  cp "$ROOT/scripts/lib/chess_source_integrity.py" "$stage/app/operations/scripts/lib/"
  cp "$ROOT/deploy/linux/stockfish-release.json" "$stage/app/operations/deploy/linux/"
  [[ -f "$ROOT/web/openapi/openapi.json" ]] || fail "the endpoint build did not write web/openapi/openapi.json"
  python3 "$ROOT/scripts/web-artifact.py" build \
    --root "$ROOT" --manifest "$SITE_BUILD/web-build.json" 8>&-
  [[ -f "$ROOT/web/dist/index.html" ]] || fail "the web build wrote no dist/index.html"
  mkdir -p "$stage/app/wwwroot"; cp -a "$ROOT/web/dist/." "$stage/app/wwwroot/"
  git -C "$ROOT" rev-parse HEAD | tee "$stage/app/.laplace-source-revision" > "$stage/app/wwwroot/.laplace-web-source-revision"
  # The install phase collected the complete non-platform native closure. Keep
  # it together in every API, CLI, MCP and chess payload sent to another host.
  cp -fL "$LAPLACE_INSTALL_PREFIX"/lib/*.so* "$stage/app/"
  local f
  for f in liblaplace.so liblaplace_engine_abi.so; do
    [[ -f "$stage/app/$f" ]] || fail "native runtime not staged: $f"
  done
  local zstd_library
  zstd_library=$(python3 "$ROOT/scripts/install-zstd.py" --build-dir "$SITE_BUILD/zstd")
  cp -fL "$zstd_library" "$stage/app/libzstd.so.1"
  mkdir -p "$stage/app/third-party-licenses/zstd"
  cp "$(dirname "$zstd_library")/LICENSE" "$(dirname "$zstd_library")/COPYING" "$stage/app/third-party-licenses/zstd/"
  python3 "$ROOT/scripts/check-zstd-runtime.py" --library "$stage/app/libzstd.so.1" \
    --require-version "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$ROOT/deploy/zstd-release.json")"
  [[ -f "$stage/app/Laplace.Endpoints.OpenAICompat.dll" ]] || fail "API not staged"
  # the MCP server (laplace-mcp.service) in its own directory of the app, with the same native closure beside it
  dotnet_app publish "$ROOT/app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj" -c Release --no-self-contained -o "$stage/app/mcp-runtime" -v minimal --nologo
  for f in "$stage/app"/lib*.so*; do [[ -e "$f" ]] && cp -fL "$f" "$stage/app/mcp-runtime/"; done
  [[ -f "$stage/app/mcp-runtime/Laplace.Endpoints.Mcp.dll" ]] || fail "MCP server not staged"
  # Chess entry points belong to the same monorepo release and native closure.
  dotnet_app publish "$ROOT/app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj" -c Release --no-self-contained -o "$stage/app/uci" -v minimal --nologo
  dotnet_app publish "$ROOT/app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj" -c Release --no-self-contained -o "$stage/app/lichess-runtime" -v minimal --nologo
  for f in "$stage/app"/lib*.so*; do
    [[ -e "$f" ]] || continue
    cp -fL "$f" "$stage/app/uci/"
    cp -fL "$f" "$stage/app/lichess-runtime/"
  done
  [[ -x "$stage/app/uci/laplace-uci" && -f "$stage/app/lichess-runtime/Laplace.Endpoints.Lichess.dll" ]] || fail "chess runtimes not staged"
  # Both API and MCP launch the same installed CLI. A serving host must not
  # depend on a runner checkout or an old in-repository build directory.
  dotnet_app publish "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-self-contained -o "$stage/app/cli" -v minimal --nologo
  for f in "$stage/app"/lib*.so*; do [[ -e "$f" ]] && cp -fL "$f" "$stage/app/cli/"; done
  # Each entry point can compose identities during assembly initialization,
  # before its host reads environment files. Ship its canonical floor alongside
  # the native libraries; the promotion bundle deduplicates identical files.
  local component cache cache_src
  for component in . cli mcp-runtime uci lichess-runtime; do
    mkdir -p "$stage/app/$component/share/laplace"
    for cache in tier0.bin tier0.flags tier0.flags.layout highway.bin tier0.chess; do
      cache_src="$SITE_BUILD/share/laplace/$cache"
      [[ -f "$cache_src" ]] || cache_src="$LAPLACE_INSTALL_PREFIX/share/laplace/$cache"
      cp "$cache_src" "$stage/app/$component/share/laplace/"
    done
  done
  install -m 0775 "$LAPLACE_INSTALL_PREFIX/bin/laplace" "$stage/app/cli/laplace"
  mkdir -p "$stage/app/cli/grammars"
  cp -fL "$LAPLACE_INSTALL_PREFIX/lib/grammars/"*.so* "$stage/app/cli/grammars/"
  for component in . cli mcp-runtime; do
    for cache in recipes firmware manifest; do
      if [[ -d "$LAPLACE_INSTALL_PREFIX/share/laplace/$cache" ]]; then
        mkdir -p "$stage/app/$component/share/laplace/$cache"
        cp -a "$LAPLACE_INSTALL_PREFIX/share/laplace/$cache/." "$stage/app/$component/share/laplace/$cache/"
      fi
    done
  done
  mkdir -p "$stage/app/cli/third-party-licenses"
  cp -a "$stage/app/third-party-licenses/zstd" "$stage/app/cli/third-party-licenses/"
  [[ -x "$stage/app/cli/Laplace.Cli" ]] || fail "CLI not staged"
  verify_cli_package "$stage/app/cli" || fail "staged CLI runtime verification failed"
  echo "  staged $(du -sh "$stage/app" | cut -f1) at $stage/app"
}

api_env() {
  local env_file="$LAPLACE_APP_DIR/laplace-api.env" key val t0
  # Migrations keep their schema-owning identity; only the serving environment
  # receives the explicitly declared application runtime connection.
  local runtime_app_db="${LAPLACE_APP_RUNTIME_DB:-${LAPLACE_APP_DB:-}}"
  local runtime_substrate_db="${LAPLACE_DATABASE_RUNTIME_DB:-$LAPLACE_DB}"
  [[ -z "${LAPLACE_APP_RUNTIME_DB:-}" || -n "${LAPLACE_APP_DB:-}" ]] || fail "application runtime selection requires the migration database declaration"
  [[ -f "$env_file" ]] || install -m 0664 "$ROOT/deploy/linux/laplace-api.env.example" "$env_file"
  t0="$LAPLACE_INSTALL_PREFIX/share/laplace/tier0.bin"
  [[ -s "$t0" ]] || fail "installed canonical Tier-0 cache is missing: $t0"
  while IFS='=' read -r key val; do
    [[ -n "$key" ]] || continue
    if grep -q "^$key=" "$env_file"; then sed -i "s|^$key=.*|$key=$val|" "$env_file"; else printf '%s=%s\n' "$key" "$val" >> "$env_file"; fi
  done <<EOF
LAPLACE_DB=$runtime_substrate_db
${runtime_app_db:+LAPLACE_APP_DB=$runtime_app_db}
LD_LIBRARY_PATH=$LAPLACE_APP_DIR:$LAPLACE_INSTALL_PREFIX/lib
LAPLACE_PERFCACHE_BIN=$t0
LAPLACE_TIER0=$t0
LAPLACE_HIGHWAY=$LAPLACE_INSTALL_PREFIX/share/laplace/highway.bin
LAPLACE_CHESS=$LAPLACE_INSTALL_PREFIX/share/laplace/tier0.chess
ASPNETCORE_URLS=$LAPLACE_API_URL
LAPLACE_OPS_LOG_DIR=${LAPLACE_API_LOG_DIR:-$LAPLACE_APP_DIR/logs}
LAPLACE_EXTERNAL=$LAPLACE_EXTERNAL
LAPLACE_DATA=$LAPLACE_DATA
LAPLACE_INSTALL_PREFIX=$LAPLACE_INSTALL_PREFIX
LAPLACE_CLI_EXE=$LAPLACE_APP_DIR/cli/Laplace.Cli
LAPLACE_UCI=$LAPLACE_APP_DIR/uci/laplace-uci
LAPLACE_DATA_PROTECTION_KEYS=${LAPLACE_DATA_PROTECTION_KEYS:-$LAPLACE_INSTALL_PREFIX/secrets/data-protection}
${LAPLACE_API_STATE_DIR:+LAPLACE_AGENTS_CONFIG=$LAPLACE_API_STATE_DIR/agents.json}
${LAPLACE_API_STATE_DIR:+LAPLACE_OUT=$LAPLACE_API_STATE_DIR/out}
${LAPLACE_PUBLIC_BASE_URL:+LAPLACE_PUBLIC_BASE_URL=$LAPLACE_PUBLIC_BASE_URL}
EOF
  python3 "$ROOT/deploy/linux/platform-owner-policy.py" --apply-api-env "$env_file"
  chmod 0664 "$env_file"
}

# The MCP server's non-secret environment, beside the API's: the same database and perf-cache, its own port and
# origin. Its token is the host's ($LAPLACE_INSTALL_PREFIX/secrets/mcp.env, made by site-host.sh), never written here.
mcp_env() {
  local env_file="$LAPLACE_APP_DIR/laplace-mcp.env" t0
  t0="$LAPLACE_INSTALL_PREFIX/share/laplace/tier0.bin"
  [[ -s "$t0" ]] || fail "installed canonical Tier-0 cache is missing: $t0"
  cat > "$env_file.new" <<EOF
LAPLACE_DB=${LAPLACE_MCP_RUNTIME_DB:-$LAPLACE_DB}
LAPLACE_PGPORT=$PGPORT
LAPLACE_CLI_EXE=$LAPLACE_APP_DIR/cli/Laplace.Cli
LD_LIBRARY_PATH=$LAPLACE_APP_DIR/mcp-runtime:$LAPLACE_INSTALL_PREFIX/lib
LAPLACE_PERFCACHE_BIN=$t0
LAPLACE_TIER0=$t0
LAPLACE_HIGHWAY=$LAPLACE_INSTALL_PREFIX/share/laplace/highway.bin
LAPLACE_CHESS=$LAPLACE_INSTALL_PREFIX/share/laplace/tier0.chess
LAPLACE_MCP_HTTP_PORT=$LAPLACE_MCP_HTTP_PORT
LAPLACE_MCP_ORIGIN=$LAPLACE_MCP_ORIGIN
LAPLACE_OPS_LOG_DIR=${LAPLACE_MCP_LOG_DIR:-$LAPLACE_APP_DIR/logs}
${LAPLACE_MCP_STATE_DIR:+LAPLACE_AGENTS_CONFIG=$LAPLACE_MCP_STATE_DIR/agents.json}
${LAPLACE_MCP_STATE_DIR:+LAPLACE_OUT=$LAPLACE_MCP_STATE_DIR/out}
LAPLACE_EXTERNAL=$LAPLACE_EXTERNAL
LAPLACE_DATA=$LAPLACE_DATA
LAPLACE_INSTALL_PREFIX=$LAPLACE_INSTALL_PREFIX
EOF
  chmod 0664 "$env_file.new"; mv -f "$env_file.new" "$env_file"
}

phase_publish() {
  say "publish into $LAPLACE_APP_DIR"
  [[ -d "$SITE_WORK/stage/app" ]] || fail "nothing staged: run the app phase first"
  revision "$SITE_WORK/stage/app" >/dev/null
  [[ -d "$LAPLACE_APP_DIR" && -w "$LAPLACE_APP_DIR" ]] || fail "$LAPLACE_APP_DIR is missing or not writable: sudo deploy/linux/site-host.sh"
  if [[ -d "$SITE_WORK/stage/share/laplace" ]]; then
    [[ -d "$LAPLACE_INSTALL_PREFIX/share" && -w "$LAPLACE_INSTALL_PREFIX/share" ]] || fail "$LAPLACE_INSTALL_PREFIX/share is missing or not writable"
    mkdir -p "$LAPLACE_INSTALL_PREFIX/share/laplace"
    rsync -a --delete "$SITE_WORK/stage/share/laplace/" "$LAPLACE_INSTALL_PREFIX/share/laplace/"
  fi
  rsync -a --delete --delay-updates \
    --exclude 'laplace-api.env' --exclude 'laplace-mcp.env' --exclude 'agents.json' --exclude 'logs/' --exclude 'releases/' \
    "$SITE_WORK/stage/app/" "$LAPLACE_APP_DIR/"
  mkdir -p "$LAPLACE_APP_DIR/logs"
  api_env
  mcp_env
  echo "  $(cat "$LAPLACE_APP_DIR/.laplace-source-revision")"
}

# Regenerate runtime configuration without rebuilding or replacing application files.
phase_configure() {
  say "configure runtime environment"
  [[ -d "$LAPLACE_APP_DIR" && -w "$LAPLACE_APP_DIR" ]] || fail "$LAPLACE_APP_DIR is not writable"
  api_env
  mcp_env
}

# The runner has no sudo: a root-owned path unit (site-host.sh) restarts laplace-api when this file changes.
phase_restart() {
  say "restart laplace-api"
  local trigger="$LAPLACE_LOCKS/restart-api" i previous current
  previous=$(systemctl show -p InvocationID --value laplace-api)
  [[ -w "$LAPLACE_LOCKS" ]] || fail "$LAPLACE_LOCKS is not writable (Operations setup.sh)"
  date -u +%FT%TZ > "$trigger"
  for i in $(seq 90); do
    sleep 2
    current=$(systemctl show -p InvocationID --value laplace-api)
    [[ -n "$current" && "$current" != "$previous" && "$(systemctl show -p ActiveState --value laplace-api 2>/dev/null)" == active ]] &&
      curl -fsS -m 5 "$LAPLACE_API_URL/health" >/dev/null 2>&1 && { echo "  up after $((i * 2)) s"; restart_mcp; restart_chess_controller; refresh_uci_connector; return 0; }
  done
  systemctl status laplace-api --no-pager 2>&1 | tail -20 || true
  fail "laplace-api did not answer $LAPLACE_API_URL/health within 180 s"
}

# The MCP endpoint restarts the same way. A host where root has not declared it yet (no unit: site-host.sh) is said
# and passed over: the API's delivery does not wait on a host's root.
mcp_declared() { systemctl cat laplace-mcp.service >/dev/null 2>&1; }
restart_mcp() {
  local trigger="$LAPLACE_LOCKS/restart-mcp" i previous current
  mcp_declared || { echo "  laplace-mcp.service is not declared on this host: sudo bash deploy/linux/site-host.sh"; return 0; }
  previous=$(systemctl show -p InvocationID --value laplace-mcp)
  date -u +%FT%TZ > "$trigger"
  for i in $(seq 60); do
    sleep 2
    current=$(systemctl show -p InvocationID --value laplace-mcp)
    [[ -n "$current" && "$current" != "$previous" && "$(systemctl show -p ActiveState --value laplace-mcp 2>/dev/null)" == active ]] &&
      curl -fsS -m 5 "$LAPLACE_MCP_URL/health/live" >/dev/null 2>&1 && { echo "  laplace-mcp up after $((i * 2)) s"; return 0; }
  done
  systemctl status laplace-mcp --no-pager 2>&1 | tail -20 || true
  fail "laplace-mcp did not answer $LAPLACE_MCP_URL/health/live within 120 s"
}

restart_chess_controller() {
  local declaration="${LAPLACE_CHESS_DEPLOYMENT_CONFIG:-/etc/laplace-mono/chess/deployment.json}"
  if [[ ! -f "$declaration" ]]; then
    if systemctl cat laplace-mono-lichess.service >/dev/null 2>&1; then
      fail "The installed chess controller has no delivery declaration: run monorepo chess host setup"
    fi
    return 0
  fi
  local values service trigger previous current i
  values=$(python3 - "$declaration" <<'PY'
import json, re, sys
c = json.load(open(sys.argv[1]))
if not re.fullmatch(r'[a-zA-Z0-9_-]+\.service', c['service']) or not re.fullmatch(r'/[a-zA-Z0-9_./-]+', c['trigger']):
    raise ValueError('Invalid installed chess delivery declaration')
print(c['service'])
print(c['trigger'])
PY
  ) || fail "Cannot read the installed chess delivery declaration"
  service=${values%%$'\n'*}; trigger=${values#*$'\n'}
  systemctl is-active --quiet "${service%.service}-restart.path" || fail "Chess restart path is not installed or active"
  previous=$(systemctl show -p InvocationID --value "$service")
  date -u +%FT%TZ > "$trigger"
  for i in $(seq 90); do
    sleep 2
    current=$(systemctl show -p InvocationID --value "$service")
    if [[ -n "$current" && "$current" != "$previous" && "$(systemctl show -p ActiveState --value "$service")" == active ]]; then
      echo "  $service restarted for the delivered revision"; return 0
    fi
  done
  fail "The installed chess controller did not restart within 180 s"
}

# The runner has no sudo. Chess host setup installs a root path unit that runs
# setup.py --refresh when this trigger changes. A host with no delivery.json has
# not armed that unit yet; API delivery still proceeds.
refresh_uci_connector() {
  local declaration="/etc/laplace-mono/uci/delivery.json"
  [[ -f "$declaration" ]] || { echo "  UCI connector render is not armed on this host"; return 0; }
  local values service trigger unit rest previous current i
  values=$(python3 - "$declaration" <<'PY'
import json, re, sys
c = json.load(open(sys.argv[1]))
if (not re.fullmatch(r'[a-zA-Z0-9_-]+\.service', c['service'])
        or not re.fullmatch(r'/[a-zA-Z0-9_./-]+', c['trigger'])
        or not re.fullmatch(r'[a-z0-9-]+\.path', c['unit'])):
    raise ValueError('Invalid installed UCI delivery declaration')
print(c['service'])
print(c['trigger'])
print(c['unit'])
PY
  ) || fail "Cannot read the installed UCI delivery declaration"
  service=${values%%$'\n'*}
  rest=${values#*$'\n'}
  trigger=${rest%%$'\n'*}
  unit=${rest#*$'\n'}
  systemctl is-active --quiet "$unit" || fail "UCI connector render path is not installed or active"
  previous=$(systemctl show -p InvocationID --value "$service")
  date -u +%FT%TZ > "$trigger"
  for i in $(seq 45); do
    sleep 2
    current=$(systemctl show -p InvocationID --value "$service")
    if [[ -n "$current" && "$current" != "$previous" && "$(systemctl show -p ActiveState --value "$service")" == active ]]; then
      echo "  $service re-rendered for the delivered revision"; return 0
    fi
  done
  fail "The installed UCI connector did not re-render within 90 s"
}

# ------------------------------------------------------------------------------------------------ bundle, receive
# The verified build host supplies the serving host. One directory travels through the pipeline
# as an artifact: the staged app with its native closure, and the perf-caches the API reads. Nothing else of the
# build host's goes along, and the hosting host builds nothing: it needs dotnet, nginx and the directories
# site-host.sh makes.
phase_bundle() {
  say "bundle for the hosting host"
  local b="$SITE_WORK/bundle"
  [[ -d "$SITE_WORK/stage/app" ]] || fail "nothing staged: run the app phase first"
  revision "$SITE_WORK/stage/app" >/dev/null
  rm -rf "$b"; mkdir -p "$b/share"
  cp -a "$SITE_WORK/stage/app" "$b/app"
  cp -a "$LAPLACE_INSTALL_PREFIX/share/laplace" "$b/share/laplace"
  # The entry points share identical managed/native dependencies. Tar preserves
  # hardlinks, so the network carries each byte-identical file once.
  python3 "$ROOT/deploy/linux/deduplicate-release.py" "$b"
  # Artifact upload normalizes file modes; tar preserves executable CLI permissions.
  tar -cf "$b/laplace-site.tar" -C "$b" app share
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then printf 'path=%s\n' "$b/laplace-site.tar" >> "$GITHUB_OUTPUT"; fi
  echo "  $(du -sh "$b" | cut -f1) at $b: $(cat "$b/app/.laplace-source-revision")"
}

verify_cli_package() {
  local cli="$1" cache
  for cache in tier0 highway; do
    [[ -s "$cli/share/laplace/$cache.bin" ]] || { echo "CLI package is missing $cache.bin" >&2; return 1; }
  done
  for cache in tier0.flags tier0.flags.layout tier0.chess; do
    [[ -s "$cli/share/laplace/$cache" ]] || { echo "CLI package is missing $cache" >&2; return 1; }
  done
  (cd "$SITE_WORK"; env -u LAPLACE_ENGINE_BUILD -u LAPLACE_INSTALL_PREFIX \
    LD_LIBRARY_PATH="$cli" \
    LAPLACE_TIER0="$cli/share/laplace/tier0.bin" \
    LAPLACE_PERFCACHE_BIN="$cli/share/laplace/tier0.bin" \
    LAPLACE_HIGHWAY="$cli/share/laplace/highway.bin" \
    "$cli/Laplace.Cli" decompose Laplace) || return $?
  local engine="$cli/laplace" estate="$cli/share/laplace" gdir="$cli/grammars" program
  if [[ ! -x "$engine" ]]; then
    engine="$cli/native/recipe-engine/bin/laplace"
    estate="$cli/native/recipe-engine/share/laplace"
    gdir="$(dirname "$engine")/grammars"
  fi
  [[ -x "$engine" && -s "$estate/firmware/program.firmware" ]] || { echo "CLI Native query compiler is missing" >&2; return 1; }
  program=$(env -u LAPLACE_ENGINE_BUILD -u LAPLACE_INSTALL_PREFIX \
    LD_LIBRARY_PATH="$cli" LAPLACE_RECIPES="$estate/recipes" LAPLACE_GRAMMARS="$gdir" \
    LAPLACE_TIER0="$cli/share/laplace/tier0.bin" LAPLACE_HIGHWAY="$cli/share/laplace/highway.bin" \
    "$engine" firmware --operation pull --firmware "$estate/firmware/program.firmware") || return 1
  [[ -n "$program" ]] || { echo "Native query compiler returned no declarations" >&2; return 1; }
}

phase_receive() {
  say "receive the bundle"
  local b="$SITE_WORK/bundle"
  [[ -f "$b/laplace-site.tar" ]] || fail "no archive at $b/laplace-site.tar: the verified build's bundle phase makes it"
  rm -rf "$SITE_WORK/stage"; mkdir -p "$SITE_WORK/stage/share"
  tar -xf "$b/laplace-site.tar" -C "$SITE_WORK/stage" --no-same-owner
  [[ -f "$SITE_WORK/stage/app/Laplace.Endpoints.OpenAICompat.dll" ]] || fail "bundle API is missing"
  revision "$SITE_WORK/stage/app" >/dev/null
  [[ -d "$SITE_WORK/stage/share/laplace" ]] || fail "bundle perf-caches are missing"
  [[ -x "$SITE_WORK/stage/app/cli/Laplace.Cli" ]] || fail "bundle CLI is missing or not executable"
  verify_cli_package "$SITE_WORK/stage/app/cli" || fail "received CLI runtime verification failed"
  echo "  $(cat "$SITE_WORK/stage/app/.laplace-source-revision")"
}

# ------------------------------------------------------------------------------------------------ smoke
phase_smoke() {
  say smoke
  local rc=0 url code installed_revision client_origin
  if ! installed_revision=$(revision "$LAPLACE_APP_DIR"); then installed_revision=unknown; rc=1; fi
  if verify_cli_package "$LAPLACE_APP_DIR/cli"; then
    echo '| Installed monorepo CLI caches and native decomposition | passed |' >> "$SUMMARY"
  else
    echo '| Installed monorepo CLI caches and native decomposition | FAILED |' >> "$SUMMARY"
    rc=1
  fi
  {
    echo "### Laplace monorepo site on $(hostname)"
    echo
    echo "| read | status |"
    echo "|---|---|"
  } >> "$SUMMARY"
  for url in "$LAPLACE_API_URL/health" "$LAPLACE_API_URL/" "$LAPLACE_API_URL/openapi/v1.json" "$LAPLACE_API_URL/v1/models" \
             "$LAPLACE_SITE_URL/health" "$LAPLACE_SITE_URL/"; do
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 20 "$url" || true)"
    printf '  %-44s %s\n' "$url" "$code"; echo "| \`$url\` | $code |" >> "$SUMMARY"
    [[ "$code" == 200 ]] || rc=1
  done
  # The MCP endpoint, where the host declares it: alive on its port; through the site it refuses a caller without the
  # token (401) and answers one that has it (initialize: 200).
  if mcp_declared || [[ -n "${LAPLACE_MCP_UPSTREAM:-}" ]]; then
    local token init='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"site.sh smoke","version":"1"}}}'
    if mcp_declared; then
      code="$(curl -s -o /dev/null -w '%{http_code}' -m 20 "$LAPLACE_MCP_URL/health/live" || true)"
      printf '  %-44s %s\n' "$LAPLACE_MCP_URL/health/live" "$code"; echo "| \`$LAPLACE_MCP_URL/health/live\` | $code |" >> "$SUMMARY"; [[ "$code" == 200 ]] || rc=1
    fi
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 20 -X POST -H 'Content-Type: application/json' -d "$init" "$LAPLACE_SITE_URL/mcp" || true)"
    printf '  %-44s %s\n' "$LAPLACE_SITE_URL/mcp, no token" "$code"; echo "| \`$LAPLACE_SITE_URL/mcp\` without a token (401) | $code |" >> "$SUMMARY"; [[ "$code" == 401 ]] || rc=1
    token="$(python3 -c 'import pathlib,shlex,sys; lines=pathlib.Path(sys.argv[1]).read_text().splitlines(); print(next(shlex.split(line.partition("=")[2])[0] for line in lines if line.startswith("LAPLACE_MCP_TOKEN=")))' "$LAPLACE_INSTALL_PREFIX/secrets/mcp.env")"
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 20 -X POST -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -H "Authorization: Bearer $token" -d "$init" "$LAPLACE_SITE_URL/mcp" || true)"
    printf '  %-44s %s\n' "$LAPLACE_SITE_URL/mcp, initialize" "$code"; echo "| \`$LAPLACE_SITE_URL/mcp\` initialize, with the token | $code |" >> "$SUMMARY"; [[ "$code" == 200 ]] || rc=1
  else
    echo "  laplace-mcp.service is not declared on this host: sudo bash deploy/linux/site-host.sh"; echo "| MCP endpoint | not declared on this host |" >> "$SUMMARY"
  fi
  # An empty substrate database is valid; unavailable SQL, authentication, native execution or
  # incompatible loaded artifacts are deployment failures and block promotion.
  local controller_checks=()
  if [[ -f "${LAPLACE_CHESS_DEPLOYMENT_CONFIG:-/etc/laplace-mono/chess/deployment.json}" ]]; then
    controller_checks+=(--chess-controller)
  fi
  if python3 "$ROOT/scripts/verify-application-release.py" --base "${LAPLACE_PUBLIC_BASE_URL:-$LAPLACE_API_URL}" \
      --state-file "${LAPLACE_VERIFICATION_STATE:-$LAPLACE_APP_DIR/logs/application-verification.json}" "${controller_checks[@]}"; then
    echo '| Authenticated SQL, native storage proof and installed dependencies | passed |' >> "$SUMMARY"
  else
    echo '| Authenticated installed runtime | FAILED |' >> "$SUMMARY"
    rc=1
  fi
  client_origin="${LAPLACE_PUBLIC_BASE_URL:-}"
  if [[ -z "$client_origin" && "$LAPLACE_SITE_URL" == https://* ]]; then
    client_origin="$LAPLACE_SITE_URL"
  fi
  if [[ -z "$client_origin" ]]; then
    client_origin=$(python3 "$ROOT/deploy/linux/configure-nginx.py" \
      --configuration "${LAPLACE_PRIVATE_ORIGINS:-$ROOT/deploy/private-origins.json}" --print-origin) || return 1
  fi
  # Routine verification reads the installed service without admitting synthetic
  # corpus content or creating ingestion runs. Write/round-trip qualification uses
  # --document-roundtrip explicitly against an isolated test installation.
  if python3 "$ROOT/scripts/run-authenticated-verification.py" --base "$client_origin" -- \
      python3 "$ROOT/scripts/verify-installed-api-client.py" --cli "$LAPLACE_APP_DIR/cli/Laplace.Cli" \
      --base "$client_origin" --work "$SITE_WORK"; then
    echo '| Installed authenticated CLI catalog and substrate read | passed |' >> "$SUMMARY"
  else
    echo '| Installed authenticated CLI catalog and substrate read | FAILED |' >> "$SUMMARY"
    rc=1
  fi
  {
    echo
    echo "- revision: \`$installed_revision\`"
    echo "- database: \`$LAPLACE_DBNAME\` on \`$PGHOST:$PGPORT\`; $(sql -c "SELECT string_agg(extname || ' ' || extversion, ', ' ORDER BY extname) FROM pg_extension WHERE extname LIKE 'laplace%'" 2>/dev/null)"
    echo "- size: $(sql -c "SELECT pg_size_pretty(pg_database_size(current_database()))" 2>/dev/null)"
  } >> "$SUMMARY"
  return "$rc"
}

phase_status() {
  python3 "$ROOT/scripts/site-status.py" --app-dir "$LAPLACE_APP_DIR" \
    --machine-file "${LAPLACE_MACHINE_ENV:-/etc/laplace/machine.env}"
}

phase_reconcile() {
  say "reconcile highway masks in $PGDATABASE on $PGHOST:$PGPORT as $PGUSER"
  bash "$ROOT/scripts/reconcile-highway-masks.sh" "$PGDATABASE"
  bash "$ROOT/scripts/check-database-health.sh" --installed-runtime "$PGDATABASE"
}

phase_chess_lab() {
  local uci="${LAPLACE_UCI:-$LAPLACE_APP_DIR/uci/laplace-uci}"
  [[ -x "$uci" ]] || fail "installed UCI command is missing: $uci"
  say "update declared chess tools for $LAPLACE_APP_DIR"
  LAPLACE_APP_DIR="$LAPLACE_APP_DIR" bash "$ROOT/scripts/bootstrap-chess-lab.sh"
  phase_restart
  LAPLACE_APP_DIR="$LAPLACE_APP_DIR" python3 "$ROOT/scripts/chess-runtime-env.py" --prefix "$LAPLACE_INSTALL_PREFIX" -- "$uci" check
}

# ------------------------------------------------------------------------------------------------ seed, serve
# The foundation through the monorepo's own seed path (ensure-foundation.sh: the CLI's layer-checked ingest).
phase_seed() {
  say "seed $LAPLACE_DBNAME: the foundation"
  local cli="$SITE_WORK/cli"
  dotnet_app publish "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-self-contained -o "$cli" -v minimal --nologo
  # the CLI runs on the installed native closure (ingest-source.sh compares the two byte for byte)
  cp -fL "$LAPLACE_INSTALL_PREFIX"/lib/liblaplace*.so* "$cli/"
  mkdir -p "$SITE_WORK/scratch" "$SITE_WORK/ingest-logs/ops"
  # shellcheck disable=SC2086
  INGEST="$LAPLACE_DATA" \
    LAPLACE_INGEST_RUNTIME="$cli" LAPLACE_SCRATCH_ROOT="$SITE_WORK/scratch" INGEST_LOGDIR="$SITE_WORK/ingest-logs" \
    LAPLACE_OPS_LOG_DIR="$SITE_WORK/ingest-logs/ops" \
    bash "$ROOT/scripts/ensure-foundation.sh" ${LAPLACE_SEED_ARGS:-}
}

# The API by hand, in the foreground, from the staged copy (no unit, no app directory): a proof before root has run.
phase_serve() {
  say "serve the staged API on $LAPLACE_API_URL"
  local app="$SITE_WORK/stage/app" t0
  t0="$LAPLACE_INSTALL_PREFIX/share/laplace/tier0.bin"
  [[ -s "$t0" ]] || fail "installed canonical Tier-0 cache is missing: $t0"
  cd "$app"
  exec 8>&- # The foreground runtime is not a deployment transaction.
  LD_LIBRARY_PATH="$app:$LAPLACE_INSTALL_PREFIX/lib" ASPNETCORE_URLS="$LAPLACE_API_URL" ASPNETCORE_ENVIRONMENT=Production \
    LAPLACE_TIER0="$t0" LAPLACE_PERFCACHE_BIN="$t0" LAPLACE_HIGHWAY="$LAPLACE_INSTALL_PREFIX/share/laplace/highway.bin" \
    LAPLACE_OPS_LOG_DIR="$SITE_WORK/logs" LAPLACE_AUTH_MODE=header LAPLACE_BILLING_BYPASS=true \
    exec dotnet "$app/Laplace.Endpoints.OpenAICompat.dll"
}

# Database checks of the deployed installation, run against what delivery just installed and migrated, never a
# database of their own: the platform authority tests touch only the users and owners they create and remove, and the
# band-health contract runs inside a transaction it rolls back.
phase_checks() {
  say "checks against the deployed $LAPLACE_DBNAME${LAPLACE_APP_DBNAME:+ and $LAPLACE_APP_DBNAME}"
  # The substrate's regression suite, against the extensions this delivery installed, in a scratch database that
  # takes $LAPLACE_DBNAME's extension settings.
  PG_CONFIG="$LAPLACE_PG_PREFIX/bin/pg_config" \
    LAPLACE_POSTGRES_TESTS="${LAPLACE_POSTGRES_SOURCE:-$(dirname "$ROOT")/Laplace-postgres}/tests" \
    LAPLACE_REGRESS_DB="laplace_regress_substrate_${GITHUB_RUN_ID:-$$}" \
    bash "$ROOT/tests/substrate-regress.sh" "$LAPLACE_DBNAME" || fail "laplace_substrate regression suite failed"
  local results="${RUNNER_TEMP:-$SITE_WORK}/authority-results"
  LAPLACE_TEST_APP_DB="${LAPLACE_APP_DB:-$LAPLACE_DB}" dotnet_app test     "$ROOT/app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj" -c Release --nologo     -p:LAPLACE_BUILD_ROOT="$SITE_BUILD/authority" -p:OpenApiGenerateDocuments=false     --filter 'FullyQualifiedName~PlatformInitialRoleDatabaseTests|FullyQualifiedName~PlatformOwner|FullyQualifiedName~BrowserDataProtectionTests'     --logger 'trx;LogFileName=authority.trx' --results-directory "$results" || fail "platform authority checks failed"
  python3 - "$results/authority.trx" <<'PY2'
import sys, xml.etree.ElementTree as ET
c = ET.parse(sys.argv[1]).getroot().find('.//{*}Counters').attrib
if c.get('total') != c.get('passed') or int(c.get('total', '0')) == 0:
    raise SystemExit('Every platform authority check must run and pass without skips: %s' % c)
PY2
  bash "$ROOT/tests/relation-band-health.sh"
}
phase_migrate_app() {
  [[ -n "${LAPLACE_APP_DB:-}" ]] || fail "migrate_app requires the declared application database"
  local app="$SITE_WORK/stage/app"
  revision "$app" >/dev/null
  [[ -d "$app/migrations/db/schema" ]] || fail "staged migration scripts are missing"
  LAPLACE_OPS_LOG_DIR="$SITE_WORK/logs" python3 "$ROOT/deploy/linux/platform-owner-policy.py" -- dotnet "$app/migrations/Laplace.Migrations.dll" app-up
}

source "$ROOT/deploy/linux/qualification.sh"
phases=("$@"); [[ ${#phases[@]} -gt 0 ]] || phases=(externals build install database extensions migrate app publish restart smoke)
lock_needed=; lock_mode=shared
for p in "${phases[@]}"; do
  [[ "$p" == serve ]] && continue
  lock_needed=1
  # Bundle reads the installed runtime and writes only this delivery's staging directory.
  [[ "$p" == smoke || "$p" == status || "$p" == bundle ]] || lock_mode=exclusive
done
[[ -z "$lock_needed" ]] || host_lock "$lock_mode"
for p in "${phases[@]}"; do
  case "$p" in
    chess-lab) phase_chess_lab ;;
    externals|build|install|database|reset|migrate|migrate_app|extensions|app|bundle|receive|publish|configure|restart|smoke|checks|status|reconcile|seed|serve|qualification|competitive) "phase_$p" ;;
    *) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
  esac
done
