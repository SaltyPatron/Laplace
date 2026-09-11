#!/usr/bin/env bash
# Execute the exact pull-request native extension build against throwaway
# PostgreSQL databases without installing into /opt/laplace or touching the
# canonical Laplace database. SQL/control files are staged under DESTDIR and
# PostgreSQL 18 resolves them through session-scoped extension_control_path;
# native modules are loaded from the build tree so their branch build RPATHs and
# exact bytes are exercised.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

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

stage="$(mktemp -d -t laplace-pr-db-proof.XXXXXXXX)"
cleanup() {
  set +e
  "$PG_PREFIX/bin/dropdb" -U laplace_admin --if-exists "$REGRESS_DB" >/dev/null 2>&1
  "$PG_PREFIX/bin/dropdb" -U laplace_admin --if-exists "${REGRESS_DB}_geom" >/dev/null 2>&1
  "$PG_PREFIX/bin/dropdb" -U laplace_admin --if-exists "${REGRESS_DB}_substrate" >/dev/null 2>&1
  rm -rf "$stage"
}
trap cleanup EXIT INT TERM

# Materialize the branch's generated extension SQL/control files without writing
# the live prefix. The installed-form SQL is the same artifact main delivery
# would publish, while the C modules themselves remain the exact build-tree ELFs.
DESTDIR="$stage" cmake --install "$BUILD" >/dev/null
staged_prefix="$stage${INSTALL_PREFIX}"
control_path="$staged_prefix/share/postgresql/18/extension"

[[ -f "$control_path/laplace_geom.control" ]] || {
  echo "pr-db-proof: staged laplace_geom.control missing: $control_path" >&2
  exit 2
}
[[ -f "$control_path/laplace_substrate.control" ]] || {
  echo "pr-db-proof: staged laplace_substrate.control missing: $control_path" >&2
  exit 2
}

# PostgreSQL 18 accepts both paths as session GUCs. $system keeps third-party
# controls (PostGIS) visible; $libdir remains the final fallback only after the
# exact branch build directories.
build_library_path="$BUILD/extension/laplace_substrate:$BUILD/extension/laplace_geom:$BUILD/engine/core:$BUILD/engine/dynamics:$BUILD/engine/synthesis"
export PGOPTIONS="-c extension_control_path=${control_path}:\$system -c dynamic_library_path=${build_library_path}:\$libdir"

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

echo "PR_DB_PROOF_OK database_stem=$REGRESS_DB controls=staged modules=build-tree canonical_mutations=0"
