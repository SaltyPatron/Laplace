#!/usr/bin/env bash
# Execute the exact pull-request native extension build against an isolated
# throwaway PostgreSQL cluster. The proof must not reuse the production
# postmaster: production preloads the installed laplace_substrate image, while
# PR proof deliberately loads the branch image from the build tree. Loading both
# copies in one postmaster re-registers custom GUCs and makes CREATE EXTENSION
# fail before branch SQL is exercised.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# shellcheck source=scripts/lib/storage.sh
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init

BUILD="$ROOT/build"
PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
REGRESS_DB="${LAPLACE_REGRESS_DB:-}"

[[ -n "$REGRESS_DB" ]] || {
  echo "pr-db-proof: LAPLACE_REGRESS_DB is required" >&2
  exit 2
}
[[ "$REGRESS_DB" =~ ^laplace_[A-Za-z0-9_]+$ ]] || {
  echo "pr-db-proof: refusing non-isolated database name: $REGRESS_DB" >&2
  exit 2
}
[[ "$REGRESS_DB" != "${PGDATABASE:-laplace}" && "$REGRESS_DB" != "laplace" ]] || {
  echo "pr-db-proof: refusing canonical database: $REGRESS_DB" >&2
  exit 2
}
[[ -f "$BUILD/cmake_install.cmake" ]] || {
  echo "pr-db-proof: build/cmake_install.cmake is missing; build the exact revision first" >&2
  exit 2
}

for tool in initdb pg_ctl psql createdb dropdb; do
  [[ -x "$PG_PREFIX/bin/$tool" ]] || {
    echo "pr-db-proof: PostgreSQL tool missing: $PG_PREFIX/bin/$tool" >&2
    exit 2
  }
done

stage="$(mktemp -d -t laplace-pr-db-proof.XXXXXXXX)"
pgdata="$stage/pgdata"
socket_dir="$stage/socket"
mkdir -p "$socket_dir"
postmaster_started=0
cleanup() {
  set +e
  if (( postmaster_started )); then
    "$PG_PREFIX/bin/pg_ctl" -D "$pgdata" -m immediate -w stop >/dev/null 2>&1 || true
  fi
  rm -rf "$stage"
}
trap cleanup EXIT INT TERM

# Materialize the branch's generated extension SQL/control files without writing
# the live prefix. The installed-form SQL is the same artifact main delivery
# would publish, while the C modules themselves remain the exact build-tree ELFs.
DESTDIR="$stage" cmake --install "$BUILD" >/dev/null
staged_prefix="$stage${INSTALL_PREFIX}"
control_root="$staged_prefix/share/postgresql/18"
control_dir="$control_root/extension"

[[ -f "$control_dir/laplace_geom.control" ]] || {
  echo "pr-db-proof: staged laplace_geom.control missing: $control_dir" >&2
  exit 2
}
[[ -f "$control_dir/laplace_substrate.control" ]] || {
  echo "pr-db-proof: staged laplace_substrate.control missing: $control_dir" >&2
  exit 2
}

build_library_path="$BUILD/extension/laplace_substrate:$BUILD/extension/laplace_geom:$BUILD/engine/core:$BUILD/engine/dynamics:$BUILD/engine/synthesis"
t0_perfcache="$BUILD/engine/core/perfcache/laplace_t0_perfcache.bin"
highway_perfcache="$BUILD/engine/core/perfcache/laplace_highway_perfcache.bin"
chess_position_perfcache="$BUILD/engine/core/perfcache/laplace_chess_position_perfcache.bin"
for blob in "$t0_perfcache" "$highway_perfcache" "$chess_position_perfcache"; do
  [[ -f "$blob" ]] || {
    echo "pr-db-proof: branch perfcache blob missing: $blob" >&2
    exit 2
  }
done

# Use a private postmaster for branch-native regression. The production host
# intentionally preloads its installed laplace_substrate image, so using that
# postmaster while dynamic_library_path points at a branch build can load two
# different copies of the extension into one process. Besides invalidating the
# proof, that redefines custom GUCs such as laplace_substrate.perfcache_path.
# A private socket directory makes concurrent proofs independent; listen_addresses
# is empty, so the arbitrary fixed port never opens a TCP listener or conflicts
# with production.
"$PG_PREFIX/bin/initdb" -D "$pgdata" -A trust -U laplace_admin --no-sync >/dev/null
cat >>"$pgdata/postgresql.conf" <<EOF
listen_addresses = ''
port = 55432
unix_socket_directories = '$socket_dir'
shared_preload_libraries = ''
extension_control_path = '$control_root:\$system'
dynamic_library_path = '$build_library_path:\$libdir'
laplace_substrate.perfcache_path = '$t0_perfcache'
laplace_substrate.highway_perfcache_path = '$highway_perfcache'
laplace_substrate.chess_position_perfcache_path = '$chess_position_perfcache'
EOF

"$PG_PREFIX/bin/pg_ctl" -D "$pgdata" -w start >/dev/null
postmaster_started=1
export PGHOST="$socket_dir"
export PGPORT=55432
export PGUSER=laplace_admin
export PGDATABASE=postgres
# Keep the branch resolution settings explicit on every client as well. This
# prevents a caller-provided PGOPTIONS from redirecting extension discovery.
export PGOPTIONS="-c extension_control_path=${control_root}:\$system -c dynamic_library_path=${build_library_path}:\$libdir"

# Fail before pg_regress if the isolated server cannot discover the staged branch
# extensions through the same settings the regress clients inherit.
available="$($PG_PREFIX/bin/psql -X -A -t -d postgres -v ON_ERROR_STOP=1 -c \
  "SELECT string_agg(name, ',' ORDER BY name) FROM pg_available_extensions WHERE name IN ('laplace_geom','laplace_substrate');")"
if [[ "$available" != "laplace_geom,laplace_substrate" ]]; then
  echo "pr-db-proof: staged extensions are not discoverable (found: ${available:-<none>})" >&2
  "$PG_PREFIX/bin/psql" -X -d postgres -v ON_ERROR_STOP=1 -c "SHOW extension_control_path" >&2 || true
  exit 2
fi

# Prove the branch image, not a preloaded installed image, owns the process.
preload="$($PG_PREFIX/bin/psql -X -A -t -d postgres -v ON_ERROR_STOP=1 -c \
  "SHOW shared_preload_libraries;")"
if [[ -n "$preload" ]]; then
  echo "pr-db-proof: isolated postmaster unexpectedly preloaded libraries: $preload" >&2
  exit 2
fi

# Every SQL regression executes CREATE EXTENSION against staged branch control/SQL
# and loads branch-native modules through dynamic_library_path. Preserve the
# pg_regress diffs in the job log on failure so a red gate names the actual SQL or
# native defect rather than collapsing back into an opaque CI failure.
set +e
ctest --test-dir "$BUILD" --output-on-failure -L regress
ctest_rc=$?
set -e

if (( ctest_rc != 0 )); then
  for f in \
    "$BUILD/extension/laplace_geom/tests/regress_output/regression.diffs" \
    "$BUILD/extension/laplace_substrate/tests/regress_output/regression.diffs" \
    "$BUILD/extension/laplace_geom/tests/regress_output/regression.out" \
    "$BUILD/extension/laplace_substrate/tests/regress_output/regression.out"; do
    if [[ -f "$f" ]]; then
      echo "===== $f ====="
      cat "$f"
    fi
  done
  exit "$ctest_rc"
fi

echo "PR_DB_PROOF_OK database_stem=$REGRESS_DB postgres=isolated controls=staged modules=build-tree canonical_mutations=0"
