#!/usr/bin/env bash
set -euo pipefail

# One deployed revision: application payload, prefix native libraries,
# PostgreSQL C MODULE bindings, and the T0 perfcache must agree.
# A matching application receipt alone is not delivery.
# Changing this file is install+live, not managed-dev / Chess.Tests.

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

NATIVE_RECEIPT="$PREFIX/lib/.laplace-source-revision"
NATIVE="$(cat "$NATIVE_RECEIPT" 2>/dev/null || true)"
native_matches=0
if [[ "$NATIVE" == "$EXPECTED" ]]; then
  native_matches=1
  printf 'PASS: deployed native prefix revision %s\n' "$NATIVE"
elif [[ -n "$NATIVE" ]]; then
  echo "::notice::native prefix remains at independent revision $NATIVE"
fi

ACTUAL="$(cat "$RECEIPT" 2>/dev/null || true)"
application_matches=0
if [[ "$ACTUAL" == "$EXPECTED" ]]; then
  application_matches=1
  printf 'PASS: deployed application revision %s\n' "$ACTUAL"
elif [[ -z "$ACTUAL" && "$NATIVE" == "$EXPECTED" ]]; then
  echo "::notice::application payload not published for $EXPECTED; native prefix receipt matches"
elif [[ -n "$ACTUAL" ]]; then
  echo "::notice::application payload remains at independent revision $ACTUAL"
fi

if (( native_matches == 0 && application_matches == 0 )); then
  echo "::error::no deployed component belongs to this checkout (expected $EXPECTED, application ${ACTUAL:-missing}, native ${NATIVE:-missing})" >&2
  failed=1
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

vocab="$PREFIX/share/laplace/laplace_vocabulary_perfcache.bin"
if [[ ! -f "$vocab" ]]; then
  echo "::error::vocabulary perfcache blob is missing under $PREFIX/share/laplace" >&2
  failed=1
else
  magic="$(od -An -t x4 -N 8 "$vocab" | awk '{print $1, $2}')"
  # LVCP little-endian 0x5043564c, version 1
  if [[ "$magic" != "5043564c 00000001" ]]; then
    echo "::error::vocabulary perfcache $vocab is not format v1 LVCP (got $magic)" >&2
    failed=1
  elif command -v psql >/dev/null 2>&1; then
    # The serving process maps the blob, and its record for NOUN is the content id the
    # database composes for the same text.
    served="$(psql -d "${PGDATABASE:-laplace}" -Atqc \
      "SELECT laplace.vocabulary_ready() AND EXISTS (SELECT 1 FROM laplace.vocabulary('upos') v
         WHERE v.label = 'NOUN' AND v.id = laplace.content_id('NOUN'))" 2>&1 || true)"
    if [[ "$served" != "t" ]]; then
      echo "::error::PostgreSQL does not serve the vocabulary perfcache ($served)" >&2
      failed=1
    else
      printf 'PASS: vocabulary perfcache is LVCP v1 and served (%s)\n' "$vocab"
    fi
  fi
fi

exit "$failed"
