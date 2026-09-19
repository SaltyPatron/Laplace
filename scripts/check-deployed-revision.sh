#!/usr/bin/env bash
set -euo pipefail

# One deployed revision: application payload, prefix native libraries,
# PostgreSQL C MODULE bindings, and the T0 perfcache must agree.
# A matching application receipt alone is not delivery.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_DIR="${LAPLACE_APP_DIR:-/opt/laplace/app}"
PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
PG_LIBDIR="${LAPLACE_PG_LIBDIR:-$PREFIX/lib/postgresql/18}"
EXPECTED="${1:-$(git -C "$ROOT" rev-parse HEAD)}"
RECEIPT="$APP_DIR/.laplace-source-revision"
failed=0

[[ "$EXPECTED" =~ ^[0-9a-f]{40}$ ]] || {
  echo "::error::invalid expected application revision: $EXPECTED" >&2
  exit 2
}

ACTUAL="$(cat "$RECEIPT" 2>/dev/null || true)"
if [[ "$ACTUAL" != "$EXPECTED" ]]; then
  echo "::error::deployed application does not belong to this checkout (expected $EXPECTED, found ${ACTUAL:-missing})" >&2
  failed=1
else
  printf 'PASS: deployed application revision %s\n' "$ACTUAL"
fi

app_core="$APP_DIR/liblaplace_core.so.0.1.0"
prefix_core="$PREFIX/lib/liblaplace_core.so.0.1.0"
# Receipt-only directories are used by publication fixtures. Coherence checks
# run only when this APP_DIR is an actual native payload.
if [[ ! -f "$app_core" ]]; then
  exit "$failed"
fi
if [[ ! -f "$prefix_core" ]]; then
  echo "::error::missing prefix native library $prefix_core" >&2
  failed=1
else
  app_hash="$(sha256sum "$app_core" | awk '{print $1}')"
  prefix_hash="$(sha256sum "$prefix_core" | awk '{print $1}')"
  if [[ "$app_hash" != "$prefix_hash" ]]; then
    echo "::error::API native library $app_core ($app_hash) differs from prefix $prefix_core ($prefix_hash)" >&2
    failed=1
  else
    printf 'PASS: API and prefix liblaplace_core.so.0.1.0 are identical\n'
  fi
fi

manifest="$PREFIX/share/postgresql/18/extension/laplace_execution_module.txt"
if [[ ! -f "$manifest" ]]; then
  echo "::error::missing execution-module manifest $manifest" >&2
  failed=1
else
  module="$(tr -d '[:space:]' < "$manifest")"
  if [[ ! "$module" =~ ^laplace_execution_[0-9a-f]{16}$ ]]; then
    echo "::error::invalid execution-module identity: $module" >&2
    failed=1
  elif [[ ! -f "$PG_LIBDIR/${module}.so" ]]; then
    echo "::error::execution module $module is declared but $PG_LIBDIR/${module}.so is absent" >&2
    failed=1
  else
    printf 'PASS: execution module file %s.so is present\n' "$module"
    if command -v psql >/dev/null 2>&1; then
      bound="$(psql -d "${PGDATABASE:-laplace}" -Atqc \
        "SELECT COALESCE(string_agg(DISTINCT probin, ',' ORDER BY probin), '')
           FROM pg_proc WHERE probin LIKE 'laplace_execution_%';" 2>/dev/null || true)"
      if [[ -n "$bound" && "$bound" != "$module" ]]; then
        echo "::error::PostgreSQL C functions bind [$bound] but installed module is $module" >&2
        failed=1
      elif [[ "$bound" == "$module" ]]; then
        printf 'PASS: PostgreSQL C functions bind %s\n' "$module"
      fi
    fi
  fi
fi

t0="$(readlink -f "$PREFIX/share/laplace/laplace_t0_perfcache_17.0.0.bin" 2>/dev/null || true)"
if [[ -z "$t0" || ! -f "$t0" ]]; then
  echo "::error::T0 perfcache blob is missing under $PREFIX/share/laplace" >&2
  failed=1
else
  magic="$(od -An -t x4 -N 8 "$t0" | awk '{print $1, $2}')"
  # LPRF little-endian 0x4652504c, version 4
  if [[ "$magic" != "4652504c 00000004" ]]; then
    echo "::error::T0 perfcache $t0 is not format v4 LPRF (got $magic)" >&2
    failed=1
  else
    printf 'PASS: T0 perfcache is LPRF v4 (%s)\n' "$t0"
  fi
fi

exit "$failed"
