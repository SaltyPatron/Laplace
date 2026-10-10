#!/usr/bin/env bash
# The monorepo's site on a machine Laplace-Operations declares (hart-server): the engine, its perf-caches and the
# PostgreSQL extensions built against the Operations cluster, installed under $LAPLACE_INSTALL_PREFIX, the database
# laplace-mono made and migrated, the API + SPA published to $LAPLACE_APP_DIR and restarted, and read back.
#
# This is the counterpart of scripts\win\publish-deploy.cmd for Linux on the Operations layout. scripts/pipeline.sh
# and deploy/linux/deploy.sh stay as they are for the earlier layout (a /build volume, its own cluster under
# /opt/laplace/pgsql-18, laplace_admin over /var/run/postgresql, sudo -n); nothing here needs root or sudo.
#
#   deploy/linux/site.sh                  every phase below, in order
#   deploy/linux/site.sh build install    one phase, or several
#   deploy/linux/site.sh seed             the foundation (scripts/ensure-foundation.sh) into the database
#   deploy/linux/site.sh reset seed       the database dropped and made again, then the foundation
#
# Phases: externals build install database extensions migrate app publish restart smoke   (and: configure migrate_app reset seed serve)
#         bundle: what the hosting host receives, after app; receive: on the hosting host, before publish
#
# The machine is set up once by root with deploy/linux/site-host.sh (directories, the unit, nginx, the restart
# trigger); the runner that runs this is Laplace-Operations' agents.sh's (labels laplace,Laplace). Every value below is
# an environment variable with this machine's value as its default.
set -euo pipefail
umask 0002

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# the Operations environment, when the runner did not already give it (runner/.env carries the build and work roots)
if [[ -z "${LAPLACE_PG_DIR:-}" && -r "${LAPLACE_OPERATIONS:-/repos/src/Laplace-Operations}/laplace.env" ]]; then
  # shellcheck disable=SC1090,SC1091
  set +u; . "${LAPLACE_OPERATIONS:-/repos/src/Laplace-Operations}/laplace.env"; set -u
fi
# the machine's own declaration (Laplace-Operations: /etc/laplace/machine.env), whichever way the rest arrived: where
# its databases are (LAPLACE_PGHOST, LAPLACE_APP_DBNAME, LAPLACE_APP_PGHOST), what the runner's environment does not carry
if [[ -d /etc/laplace && ! -x /etc/laplace ]]; then
  echo '::error::cannot traverse /etc/laplace; refusing to use another machine configuration' >&2
  exit 1
fi
if [[ -r /etc/laplace/machine.env ]]; then
  # shellcheck disable=SC1091
  set -a; set +u; . /etc/laplace/machine.env; set -u; set +a
elif [[ -e /etc/laplace/machine.env ]]; then
  echo '::error::cannot read /etc/laplace/machine.env' >&2
  exit 1
fi

LAPLACE_PG_PREFIX="${LAPLACE_PG_PREFIX:-${LAPLACE_PG_DIR:-/usr/local/pgsql}}"
LAPLACE_INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
LAPLACE_APP_DIR="${LAPLACE_APP_DIR:-$LAPLACE_INSTALL_PREFIX/app}"
LAPLACE_DEPSRC="${LAPLACE_DEPSRC:-/repos/src}"
LAPLACE_DATA_ROOT="${LAPLACE_DATA_ROOT:-${LAPLACE_DATA:-/vault/Data}}"
SITE_WORK="${LAPLACE_SITE_WORK:-${LAPLACE_WORK:-/repos/work}/mono-site}"
SITE_BUILD="${LAPLACE_SITE_BUILD:-${LAPLACE_BUILD:-/repos/build}/Laplace/release}"
# the PostGIS the cluster runs was built by setup.sh deps out of tree: its liblwgeom is the one laplace_geom links
LAPLACE_POSTGIS_BUILD="${LAPLACE_POSTGIS_BUILD:-/repos/work/postgis}"
LAPLACE_GEOS_PROJ_PREFIX="${LAPLACE_GEOS_PROJ_PREFIX:-${LAPLACE_PREFIX:-/usr/local}}"
LAPLACE_LOCKS="${LAPLACE_LOCKS:-/run/lock/laplace}"
LAPLACE_API_URL="${LAPLACE_API_URL:-http://127.0.0.1:5187}"
LAPLACE_SITE_URL="${LAPLACE_SITE_URL:-http://127.0.0.1:8080}"
# the MCP endpoint (laplace-mcp.service): its loopback port, and the one browser origin it answers (a caller that
# sends no Origin, as MCP clients do, is judged by its bearer token alone)
LAPLACE_MCP_HTTP_PORT="${LAPLACE_MCP_HTTP_PORT:-5190}"   # 5188 is Laplace-MCP's lpm serve where a host runs it, 5189 the Lichess service
LAPLACE_MCP_ORIGIN="${LAPLACE_MCP_ORIGIN:-https://mcp.hartonomous.com}"
LAPLACE_MCP_URL="http://127.0.0.1:$LAPLACE_MCP_HTTP_PORT"

export PGHOST="${LAPLACE_PGHOST:-/tmp}" PGPORT="${LAPLACE_PGPORT:-5432}" PGUSER="${LAPLACE_ROLE:-laplace}"
export LAPLACE_DBNAME="${LAPLACE_DBNAME:-laplace-mono}"
export PGDATABASE="$LAPLACE_DBNAME"
export LAPLACE_DB="Host=$PGHOST;Port=$PGPORT;Username=$PGUSER;Database=$LAPLACE_DBNAME"
# The application's own database (the app schema: accounts, sessions, keys, billing) when the host declares one:
# LAPLACE_APP_DBNAME names it, LAPLACE_APP_PGHOST/PGPORT/ROLE say where (default: the same server). The migrator
# makes it and routes the app schema's scripts to it; the API reads it as LAPLACE_APP_DB. Unset: one database.
if [[ -n "${LAPLACE_APP_DBNAME:-}" ]]; then
  export LAPLACE_APP_DB="Host=${LAPLACE_APP_PGHOST:-$PGHOST};Port=${LAPLACE_APP_PGPORT:-$PGPORT};Username=${LAPLACE_APP_ROLE:-$PGUSER};Database=$LAPLACE_APP_DBNAME"
fi
export LAPLACE_EXTERNAL="$SITE_WORK/external"
export LAPLACE_INSTALL_PREFIX LAPLACE_ENGINE_BUILD="$SITE_BUILD/engine"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$LAPLACE_PG_PREFIX/bin:$PATH"

# The knowledge cluster's own build, where there is one (the knowledge host): the extensions are built against it and
# installed under this prefix for its major. A hosting host has no such build and runs only receive, publish, restart
# and smoke, which need none of this; a phase that does need it says so when it runs.
if [[ -x "$LAPLACE_PG_PREFIX/bin/pg_config" ]]; then
  PG_MAJOR="$("$LAPLACE_PG_PREFIX/bin/pg_config" --version | sed -E 's/^PostgreSQL ([0-9]+).*/\1/')"
else
  PG_MAJOR=""
fi
EXT_LIBDIR="$LAPLACE_INSTALL_PREFIX/lib/postgresql/${PG_MAJOR:-none}"
EXT_SHARE="$LAPLACE_INSTALL_PREFIX/share/postgresql/${PG_MAJOR:-none}"
need_pg() { [[ -n "$PG_MAJOR" ]] || fail "no PostgreSQL build at $LAPLACE_PG_PREFIX: this phase runs on the knowledge host"; }
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"

say()  { echo; echo "=== $*   $(date -u +%H:%M:%S)"; }
fail() { echo "::error::$*" >&2; exit 1; }
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
  local e="$LAPLACE_EXTERNAL" d="$SITE_WORK/deps" name target
  mkdir -p "$e" "$d/pgsql-18/include" "$d/pgsql-18/lib"
  while read -r name target; do
    [[ -e "$target" ]] || fail "external $name: $target is not on this machine"
    ln -sfn "$target" "$e/$name"; printf '  %-22s %s\n' "$name" "$target"
  done <<EOF
blake3                $LAPLACE_DEPSRC/blake3
tree-sitter           $LAPLACE_DEPSRC/tree-sitter
fathom                $LAPLACE_DEPSRC/Fathom
spectra               $LAPLACE_DEPSRC/spectra
postgis               $LAPLACE_DEPSRC/postgis
eigen                 ${LAPLACE_EIGEN:-/usr/include/eigen3}
tree-sitter-grammars  $LAPLACE_DATA_ROOT/TreeSitter
EOF
  ln -sfn "$LAPLACE_GEOS_PROJ_PREFIX" "$d/geos"; ln -sfn "$LAPLACE_GEOS_PROJ_PREFIX" "$d/proj"
  [[ -f "$LAPLACE_POSTGIS_BUILD/liblwgeom/.libs/liblwgeom.a" ]] || fail "no liblwgeom.a under $LAPLACE_POSTGIS_BUILD (Operations setup.sh deps builds PostGIS there)"
  # liblwgeom's headers as an installed set (PostGIS 3 installs none): liblwgeom.h reads "../postgis_config.h" and that
  # reads "postgis_revision.h", which an out-of-tree build without git metadata does not write
  local inc="$d/pgsql-18/include" rev
  sed 's|#include "../postgis_config.h"|#include "postgis_config.h"|' "$LAPLACE_POSTGIS_BUILD/liblwgeom/liblwgeom.h" > "$inc/liblwgeom.h"
  cp -f "$LAPLACE_POSTGIS_BUILD/postgis_config.h" "$inc/postgis_config.h"
  ln -sfn "$LAPLACE_DEPSRC/postgis/liblwgeom/lwinline.h" "$inc/lwinline.h"
  if [[ -f "$LAPLACE_POSTGIS_BUILD/postgis_revision.h" ]]; then cp -f "$LAPLACE_POSTGIS_BUILD/postgis_revision.h" "$inc/"
  elif [[ -f "$LAPLACE_DEPSRC/postgis/postgis_revision.h" ]]; then cp -f "$LAPLACE_DEPSRC/postgis/postgis_revision.h" "$inc/"
  else rev="$(git -C "$LAPLACE_DEPSRC/postgis" rev-parse --short=10 HEAD 2>/dev/null || echo unknown)"
       echo "#define POSTGIS_REVISION $rev" > "$inc/postgis_revision.h"; fi
  ln -sfn "$LAPLACE_POSTGIS_BUILD/liblwgeom/.libs/liblwgeom.a" "$d/pgsql-18/lib/liblwgeom.a"
  echo "  deps prefix            $d (geos, proj -> $LAPLACE_GEOS_PROJ_PREFIX; liblwgeom -> $LAPLACE_POSTGIS_BUILD)"
}

# ------------------------------------------------------------------------------------------------ build
# configure_native_build_tree's configuration (scripts/pipeline.sh), against the Operations cluster, staged install.
phase_build() {
  need_pg
  say "build: engine, perf-caches, extensions"
  oneapi
  local ucd="${LAPLACE_UCD_PATH:-$LAPLACE_DATA_ROOT/UCD/Public/UCD/latest}"
  local openings="${LAPLACE_CHESS_OPENINGS:-$LAPLACE_DATA_ROOT/Games/Chess/lichess-openings}"
  cmake -S "$ROOT" -B "$SITE_BUILD" -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$ROOT/cmake/toolchains/intel-oneapi.cmake" \
    -DLAPLACE_REQUIRE_MKL=ON -DBUILD_TESTING=OFF \
    -DCMAKE_INSTALL_PREFIX="$LAPLACE_INSTALL_PREFIX" \
    -DLAPLACE_PG_PREFIX="$LAPLACE_PG_PREFIX" \
    -DLAPLACE_EXTERNAL="$LAPLACE_EXTERNAL" \
    -DLAPLACE_DEPS_PREFIX="$SITE_WORK/deps" \
    -DLAPLACE_DEPS_LWGEOM_PREFIX="$SITE_WORK/deps/pgsql-18" \
    -DLAPLACE_INSTALL_STAGED=ON \
    -DLAPLACE_UCD_PATH="$ucd" \
    -DLAPLACE_UCDXML_ZIP="$ucd/ucdxml/ucd.all.grouped.zip" \
    -DLAPLACE_DUCET_FILE="$ucd/uca/allkeys.txt" \
    -DLAPLACE_UCD_CONFORMANCE_DIR="$ucd/ucd" \
    -DLAPLACE_CHESS_OPENINGS="$openings"
  LD_LIBRARY_PATH="$SITE_BUILD/engine/core:$SITE_BUILD/engine/dynamics:$SITE_BUILD/engine/synthesis:${LD_LIBRARY_PATH:-}" \
    cmake --build "$SITE_BUILD" --target all laplace_t0_perfcache laplace_highway_perfcache laplace_chess_position_perfcache laplace_modality_number_perfcache laplace_vocabulary_perfcache
  find -H "$SITE_BUILD" -name 'laplace_t0_perfcache*.bin' -print -quit | grep -q . || fail "tier-0 perf-cache not produced"
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
  stage="$(mktemp -d "$SITE_WORK/.install.XXXXXX")"
  DESTDIR="$stage" cmake --install "$SITE_BUILD" >/dev/null
  while IFS= read -r -d '' f; do
    rel="${f#"$stage$LAPLACE_INSTALL_PREFIX"/}"
    mkdir -p "$LAPLACE_INSTALL_PREFIX/$(dirname "$rel")"
    if [[ -L "$f" ]]; then ln -sfn "$(readlink "$f")" "$LAPLACE_INSTALL_PREFIX/$rel"; else mv -f "$f" "$LAPLACE_INSTALL_PREFIX/$rel"; fi
    n=$((n + 1))
  done < <(find "$stage$LAPLACE_INSTALL_PREFIX" \( -type f -o -type l \) -print0)
  rm -rf "$stage"
  echo "  $n files"
  # the oneAPI runtime: every library the installed objects resolve from oneAPI, and every MKL kernel library
  local lib; local -A want=()
  while IFS= read -r lib; do want[$lib]=1; done < <(
    find "$LAPLACE_INSTALL_PREFIX/lib" -maxdepth 3 -name '*.so*' -type f -exec ldd {} + 2>/dev/null |
      awk '$3 ~ "^/opt/intel/oneapi/" {print $3}' | sort -u)
  for lib in "${MKLROOT:-/opt/intel/oneapi/mkl/latest}"/lib/libmkl_{core,def,mc3,avx2,avx512,vml_def,vml_mc3,vml_avx2,vml_avx512,vml_cmpt,intel_thread,sequential,tbb_thread,intel_lp64,intel_ilp64}.so.[0-9]; do
    [[ -e "$lib" ]] && want[$lib]=1; done
  for lib in "${!want[@]}"; do cp -fL "$lib" "$LAPLACE_INSTALL_PREFIX/lib/.$(basename "$lib").new" && mv -f "$LAPLACE_INSTALL_PREFIX/lib/.$(basename "$lib").new" "$LAPLACE_INSTALL_PREFIX/lib/$(basename "$lib")"; done
  echo "  oneAPI runtime beside the libraries: ${#want[@]} files"
  local missing
  missing="$(find "$LAPLACE_INSTALL_PREFIX/lib" -maxdepth 3 -name '*.so*' -type f -exec env LD_LIBRARY_PATH="$LAPLACE_INSTALL_PREFIX/lib" ldd {} + 2>/dev/null | grep 'not found' | sort -u || true)"
  [[ -z "$missing" ]] || fail "unresolved libraries under $LAPLACE_INSTALL_PREFIX/lib: $missing"
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
    -c "ALTER DATABASE \"$LAPLACE_DBNAME\" SET session_preload_libraries = 'laplace_substrate'" >/dev/null
  # session_preload_libraries: laplace_execution_<hash>.so resolves laplace_substrate's symbols (dlopen RTLD_GLOBAL),
  # so the substrate is loaded first in every session of this database. The monorepo's own hosts put it in the
  # cluster's shared_preload_libraries; here the cluster is Operations' and the setting stays this database's.
  local dir="$LAPLACE_INSTALL_PREFIX/share/laplace" g f
  for g in perfcache_path:laplace_t0_perfcache highway_perfcache_path:laplace_highway_perfcache \
           vocabulary_perfcache_path:laplace_vocabulary_perfcache chess_position_perfcache_path:laplace_chess_position_perfcache; do
    f="$(find "$dir" -name "${g#*:}*.bin" 2>/dev/null | sort -V | tail -1)"
    [[ -n "$f" ]] || { echo "  laplace_substrate.${g%%:*}: no ${g#*:} installed"; continue; }
    q="$(sql -d postgres -c "SELECT setting FROM pg_file_settings WHERE name = 'laplace_substrate.${g%%:*}' AND applied ORDER BY seqno DESC LIMIT 1")"
    [[ "$q" == "$f" ]] || sql -d postgres -c "ALTER SYSTEM SET laplace_substrate.${g%%:*} = '$f'" >/dev/null
    echo "  laplace_substrate.${g%%:*} = $f"
  done
  sql -d postgres -c "SELECT pg_reload_conf()" >/dev/null
}

phase_reset() {
  say "reset: $LAPLACE_DBNAME dropped"
  [[ "$LAPLACE_DBNAME" == laplace-mono* ]] || fail "reset drops only a laplace-mono database, not '$LAPLACE_DBNAME'"
  sql -d postgres -c "DROP DATABASE IF EXISTS \"$LAPLACE_DBNAME\" WITH (FORCE)" >/dev/null
  phase_database
}

# ------------------------------------------------------------------------------------------------ migrate + extensions
dotnet_app() { LAPLACE_REUSE_INSTALLED_NATIVE=0 dotnet "$@"; }
phase_migrate() {
  need_pg
  say "migrate $LAPLACE_DBNAME"
  local out="$SITE_WORK/migrations"
  dotnet_app publish "$ROOT/app/Laplace.Migrations/Laplace.Migrations.csproj" -c Release --no-self-contained -o "$out" -v minimal --nologo
  ( cd "$ROOT" && LD_LIBRARY_PATH="$LAPLACE_INSTALL_PREFIX/lib" dotnet "$out/Laplace.Migrations.dll" up )   # db/migrations is read from the checkout
  [[ "$(sql -c "SELECT c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'laplace' AND c.relname = 'entities'")" == p ]] \
    || fail "laplace.entities is not the partitioned generation: the extension installed predates the schema"
}
# A newer extension than the database has is updated in place through the bridge its upgrade script makes
# (scripts/pipeline.sh sync_one_extension), never by dropping it.
phase_extensions() {
  need_pg
  say "extensions in $LAPLACE_DBNAME"
  local ext avail installed
  for ext in laplace_geom laplace_substrate; do
    avail="$(sql -c "SELECT default_version FROM pg_available_extensions WHERE name = '$ext'")"
    installed="$(sql -c "SELECT extversion FROM pg_extension WHERE extname = '$ext'")"
    [[ -n "$avail" ]] || fail "$ext is not available to $LAPLACE_DBNAME (extension_control_path)"
    if [[ -z "$installed" ]]; then installed="none yet: the migrations make it at $avail"
    elif [[ "$installed" != "$avail" ]]; then
      install -m 664 "$EXT_SHARE/extension/${ext}_upgrade.sql" "$EXT_SHARE/extension/$ext--$installed--$avail.sql"
      PGOPTIONS="-c lock_timeout=${LAPLACE_DDL_LOCK_TIMEOUT:-20s}" sql -c "ALTER EXTENSION $ext UPDATE TO '$avail'" >/dev/null
      installed="$installed -> $avail"
    fi
    echo "  $ext $installed"
  done
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
  mkdir -p "$stage/app/migrations/db/migrations"
  cp -a "$ROOT/db/migrations/." "$stage/app/migrations/db/migrations/"
  [[ -f "$ROOT/web/openapi/openapi.json" ]] || fail "the endpoint build did not write web/openapi/openapi.json"
  ( cd "$ROOT/web"
    lock="$(sha256sum package-lock.json | cut -d' ' -f1)"
    [[ -d node_modules && "$(cat node_modules/.laplace-npm-ci.stamp 2>/dev/null)" == "$lock" ]] || { npm ci --no-audit --no-fund --prefer-offline; echo "$lock" > node_modules/.laplace-npm-ci.stamp; }
    npm run gen:api; npm run build )
  [[ -f "$ROOT/web/dist/index.html" ]] || fail "the web build wrote no dist/index.html"
  mkdir -p "$stage/app/wwwroot"; cp -a "$ROOT/web/dist/." "$stage/app/wwwroot/"
  git -C "$ROOT" rev-parse HEAD | tee "$stage/app/.laplace-source-revision" > "$stage/app/wwwroot/.laplace-web-source-revision"
  # the native closure beside what loads it: the engine libraries and the oneAPI runtime from the prefix
  cp -fL "$LAPLACE_INSTALL_PREFIX"/lib/liblaplace_*.so* "$LAPLACE_INSTALL_PREFIX"/lib/libmkl_*.so* "$stage/app/" 2>/dev/null || true
  local f; for f in "$LAPLACE_INSTALL_PREFIX"/lib/lib{iomp5,tbb,tbbmalloc,svml,imf,intlc,irng,sycl}*.so*; do [[ -e "$f" ]] && cp -fL "$f" "$stage/app/"; done
  [[ -f "$stage/app/Laplace.Endpoints.OpenAICompat.dll" ]] || fail "API not staged"
  # the MCP server (laplace-mcp.service) in its own directory of the app, with the same native closure beside it
  dotnet_app publish "$ROOT/app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj" -c Release --no-self-contained -o "$stage/app/mcp-runtime" -v minimal --nologo
  for f in "$stage/app"/lib*.so*; do [[ -e "$f" ]] && cp -fL "$f" "$stage/app/mcp-runtime/"; done
  [[ -f "$stage/app/mcp-runtime/Laplace.Endpoints.Mcp.dll" ]] || fail "MCP server not staged"
  echo "  staged $(du -sh "$stage/app" | cut -f1) at $stage/app"
}

api_env() {
  local env_file="$LAPLACE_APP_DIR/laplace-api.env" key val t0
  [[ -f "$env_file" ]] || install -m 0664 "$ROOT/deploy/linux/laplace-api.env.example" "$env_file"
  t0="$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)"
  while IFS='=' read -r key val; do
    [[ -n "$key" ]] || continue
    if grep -q "^$key=" "$env_file"; then sed -i "s|^$key=.*|$key=$val|" "$env_file"; else printf '%s=%s\n' "$key" "$val" >> "$env_file"; fi
  done <<EOF
LAPLACE_DB=$LAPLACE_DB
${LAPLACE_APP_DB:+LAPLACE_APP_DB=$LAPLACE_APP_DB}
LD_LIBRARY_PATH=$LAPLACE_APP_DIR:$LAPLACE_INSTALL_PREFIX/lib
LAPLACE_PERFCACHE_BIN=$t0
ASPNETCORE_URLS=$LAPLACE_API_URL
LAPLACE_OPS_LOG_DIR=$LAPLACE_APP_DIR/logs
LAPLACE_EXTERNAL=$LAPLACE_EXTERNAL
LAPLACE_DATA_PROTECTION_KEYS=$LAPLACE_INSTALL_PREFIX/secrets/data-protection
EOF
}

# The MCP server's non-secret environment, beside the API's: the same database and perf-cache, its own port and
# origin. Its token is the host's ($LAPLACE_INSTALL_PREFIX/secrets/mcp.env, made by site-host.sh), never written here.
mcp_env() {
  local env_file="$LAPLACE_APP_DIR/laplace-mcp.env" t0
  t0="$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)"
  cat > "$env_file.new" <<EOF
LAPLACE_DB=$LAPLACE_DB
LAPLACE_PGPORT=$PGPORT
LD_LIBRARY_PATH=$LAPLACE_APP_DIR/mcp-runtime:$LAPLACE_INSTALL_PREFIX/lib
LAPLACE_PERFCACHE_BIN=$t0
LAPLACE_MCP_HTTP_PORT=$LAPLACE_MCP_HTTP_PORT
LAPLACE_MCP_ORIGIN=$LAPLACE_MCP_ORIGIN
LAPLACE_OPS_LOG_DIR=$LAPLACE_APP_DIR/logs
LAPLACE_EXTERNAL=$LAPLACE_EXTERNAL
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
  local trigger="$LAPLACE_LOCKS/restart-api" i
  [[ -w "$LAPLACE_LOCKS" ]] || fail "$LAPLACE_LOCKS is not writable (Operations setup.sh)"
  date -u +%FT%TZ > "$trigger"
  for i in $(seq 90); do
    sleep 2
    [[ "$(systemctl show -p ActiveState --value laplace-api 2>/dev/null)" == active ]] &&
      curl -fsS -m 5 "$LAPLACE_API_URL/health" >/dev/null 2>&1 && { echo "  up after $((i * 2)) s"; restart_mcp; return 0; }
  done
  systemctl status laplace-api --no-pager 2>&1 | tail -20 || true
  fail "laplace-api did not answer $LAPLACE_API_URL/health within 180 s"
}

# The MCP endpoint restarts the same way. A host where root has not declared it yet (no unit: site-host.sh) is said
# and passed over: the API's delivery does not wait on a host's root.
mcp_declared() { systemctl cat laplace-mcp.service >/dev/null 2>&1; }
restart_mcp() {
  local trigger="$LAPLACE_LOCKS/restart-mcp" i
  mcp_declared || { echo "  laplace-mcp.service is not declared on this host: sudo bash deploy/linux/site-host.sh"; return 0; }
  date -u +%FT%TZ > "$trigger"
  for i in $(seq 60); do
    sleep 2
    [[ "$(systemctl show -p ActiveState --value laplace-mcp 2>/dev/null)" == active ]] &&
      curl -fsS -m 5 "$LAPLACE_MCP_URL/health/live" >/dev/null 2>&1 && { echo "  laplace-mcp up after $((i * 2)) s"; return 0; }
  done
  systemctl status laplace-mcp --no-pager 2>&1 | tail -20 || true
  fail "laplace-mcp did not answer $LAPLACE_MCP_URL/health/live within 120 s"
}

# ------------------------------------------------------------------------------------------------ bundle, receive
# The knowledge host builds, the hosting host runs. What travels between them is one directory the pipeline carries
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
  echo "  $(du -sh "$b" | cut -f1) at $b: $(cat "$b/app/.laplace-source-revision")"
}

phase_receive() {
  say "receive the bundle"
  local b="$SITE_WORK/bundle"
  [[ -f "$b/app/Laplace.Endpoints.OpenAICompat.dll" ]] || fail "no bundle at $b/app: the knowledge host's bundle phase makes it"
  revision "$b/app" >/dev/null
  [[ -d "$b/share/laplace" ]] || fail "bundle perf-caches are missing"
  rm -rf "$SITE_WORK/stage"; mkdir -p "$SITE_WORK/stage/share"
  cp -a "$b/app" "$SITE_WORK/stage/app"
  cp -a "$b/share/laplace" "$SITE_WORK/stage/share/laplace"
  echo "  $(cat "$SITE_WORK/stage/app/.laplace-source-revision")"
}

# ------------------------------------------------------------------------------------------------ smoke
phase_smoke() {
  say smoke
  local rc=0 url code installed_revision
  if ! installed_revision=$(revision "$LAPLACE_APP_DIR"); then installed_revision=unknown; rc=1; fi
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
  if mcp_declared; then
    local token init='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"site.sh smoke","version":"1"}}}'
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 20 "$LAPLACE_MCP_URL/health/live" || true)"
    printf '  %-44s %s\n' "$LAPLACE_MCP_URL/health/live" "$code"; echo "| \`$LAPLACE_MCP_URL/health/live\` | $code |" >> "$SUMMARY"; [[ "$code" == 200 ]] || rc=1
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 20 -X POST -H 'Content-Type: application/json' -d "$init" "$LAPLACE_SITE_URL/mcp" || true)"
    printf '  %-44s %s\n' "$LAPLACE_SITE_URL/mcp, no token" "$code"; echo "| \`$LAPLACE_SITE_URL/mcp\` without a token (401) | $code |" >> "$SUMMARY"; [[ "$code" == 401 ]] || rc=1
    token="$(sed -n 's/^LAPLACE_MCP_TOKEN=//p' "$LAPLACE_INSTALL_PREFIX/secrets/mcp.env" 2>/dev/null | head -1)"
    code="$(curl -s -o /dev/null -w '%{http_code}' -m 20 -X POST -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -H "Authorization: Bearer $token" -d "$init" "$LAPLACE_SITE_URL/mcp" || true)"
    printf '  %-44s %s\n' "$LAPLACE_SITE_URL/mcp, initialize" "$code"; echo "| \`$LAPLACE_SITE_URL/mcp\` initialize, with the token | $code |" >> "$SUMMARY"; [[ "$code" == 200 ]] || rc=1
  else
    echo "  laplace-mcp.service is not declared on this host: sudo bash deploy/linux/site-host.sh"; echo "| MCP endpoint | not declared on this host |" >> "$SUMMARY"
  fi
  # readiness says whether the substrate is seeded: reported, not required (a push does not seed)
  code="$(curl -s -o "$SITE_WORK/ready.json" -w '%{http_code}' -m 20 "$LAPLACE_API_URL/health/ready" || true)"
  printf '  %-44s %s %s\n' "$LAPLACE_API_URL/health/ready" "$code" "$(head -c 160 "$SITE_WORK/ready.json" 2>/dev/null)"
  echo "| \`$LAPLACE_API_URL/health/ready\` (not required) | $code $(head -c 120 "$SITE_WORK/ready.json" 2>/dev/null | tr '|' '/') |" >> "$SUMMARY"
  {
    echo
    echo "- revision: \`$installed_revision\`"
    echo "- database: \`$LAPLACE_DBNAME\` on \`$PGHOST:$PGPORT\`; $(sql -c "SELECT string_agg(extname || ' ' || extversion, ', ' ORDER BY extname) FROM pg_extension WHERE extname LIKE 'laplace%'" 2>/dev/null)"
    echo "- size: $(sql -c "SELECT pg_size_pretty(pg_database_size(current_database()))" 2>/dev/null)"
  } >> "$SUMMARY"
  return "$rc"
}

# ------------------------------------------------------------------------------------------------ seed, serve
# The foundation through the monorepo's own seed path (ensure-foundation.sh: the CLI's layer-checked ingest).
phase_seed() {
  say "seed $LAPLACE_DBNAME: the foundation"
  local cli="$SITE_WORK/cli"
  dotnet_app publish "$ROOT/app/Laplace.Cli/Laplace.Cli.csproj" -c Release --no-self-contained -o "$cli" -v minimal --nologo
  # the CLI runs on the installed native closure (ingest-source.sh compares the two byte for byte)
  cp -fL "$LAPLACE_INSTALL_PREFIX"/lib/liblaplace_*.so* "$cli/"
  mkdir -p "$SITE_WORK/scratch" "$SITE_WORK/ingest-logs/ops"
  # shellcheck disable=SC2086
  INGEST="$LAPLACE_DATA_ROOT" LAPLACE_DATA_ROOT="$LAPLACE_DATA_ROOT" \
    LAPLACE_INGEST_RUNTIME="$cli" LAPLACE_SCRATCH_ROOT="$SITE_WORK/scratch" INGEST_LOGDIR="$SITE_WORK/ingest-logs" \
    LAPLACE_OPS_LOG_DIR="$SITE_WORK/ingest-logs/ops" \
    bash "$ROOT/scripts/ensure-foundation.sh" ${LAPLACE_SEED_ARGS:-}
}

# The API by hand, in the foreground, from the staged copy (no unit, no app directory): a proof before root has run.
phase_serve() {
  say "serve the staged API on $LAPLACE_API_URL"
  local app="$SITE_WORK/stage/app" t0
  t0="$(find "$LAPLACE_INSTALL_PREFIX/share/laplace" -name 'laplace_t0_perfcache*.bin' 2>/dev/null | sort -V | tail -1)"
  cd "$app"
  LD_LIBRARY_PATH="$app:$LAPLACE_INSTALL_PREFIX/lib" ASPNETCORE_URLS="$LAPLACE_API_URL" ASPNETCORE_ENVIRONMENT=Production \
    LAPLACE_PERFCACHE_BIN="$t0" LAPLACE_OPS_LOG_DIR="$SITE_WORK/logs" LAPLACE_AUTH_MODE=header LAPLACE_BILLING_BYPASS=true \
    exec dotnet "$app/Laplace.Endpoints.OpenAICompat.dll"
}

phase_migrate_app() {
  [[ -n "${LAPLACE_APP_DB:-}" ]] || fail "migrate_app requires the declared application database"
  local app="$SITE_WORK/stage/app"
  revision "$app" >/dev/null
  [[ -d "$app/migrations/db/migrations" ]] || fail "staged migration scripts are missing"
  LAPLACE_OPS_LOG_DIR="$SITE_WORK/logs" dotnet "$app/migrations/Laplace.Migrations.dll" app-up
}

phases=("$@"); [[ ${#phases[@]} -gt 0 ]] || phases=(externals build install database extensions migrate app publish restart smoke)
for p in "${phases[@]}"; do
  case "$p" in
    externals|build|install|database|reset|migrate|migrate_app|extensions|app|bundle|receive|publish|configure|restart|smoke|seed|serve) "phase_$p" ;;
    *) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
  esac
done
