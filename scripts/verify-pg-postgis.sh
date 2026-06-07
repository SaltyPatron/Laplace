#!/bin/bash

set -euo pipefail

PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
GEOS_PREFIX="${LAPLACE_GEOS_PREFIX:-/opt/laplace/geos}"
PG_MAJOR="${PG_MAJOR:-18}"
SOCKET_DIR="${LAPLACE_PG_SOCKET_DIR:-/tmp}"
PGPORT="${PGPORT:-5432}"
PGUSER="${PGUSER:-laplace_admin}"

reset='\033[0m'
green='\033[0;32m'
red='\033[0;31m'
yellow='\033[0;33m'

errors=0
warnings=0

fail() {
    printf "${red}✗${reset} %s\n" "$1"
    errors=$((errors + 1))
}

warn() {
    printf "${yellow}⚠${reset} %s\n" "$1"
    warnings=$((warnings + 1))
}

ok() {
    printf "${green}✓${reset} %s\n" "$1"
}

require_file() {
    if [ -f "$1" ]; then
        ok "$2"
    else
        fail "$2 (missing: $1)"
    fi
}

echo "=== PostgreSQL + PostGIS install (${PREFIX}) ==="

require_file "${PREFIX}/bin/postgres" "postgres binary"
require_file "${PREFIX}/bin/pg_config" "pg_config"
require_file "${PREFIX}/bin/psql" "psql"
require_file "${PREFIX}/lib/postgis-3.so" "postgis-3.so"
require_file "${PREFIX}/lib/postgis_raster-3.so" "postgis_raster-3.so"
require_file "${PREFIX}/lib/postgis_topology-3.so" "postgis_topology-3.so"
require_file "${PREFIX}/share/extension/postgis.control" "postgis.control"
require_file "${PREFIX}/share/extension/postgis_topology.control" "postgis_topology.control"
require_file "${PREFIX}/share/extension/postgis_raster.control" "postgis_raster.control"
require_file "${PREFIX}/share/extension/postgis_tiger_geocoder.control" "postgis_tiger_geocoder.control"
require_file "${PREFIX}/share/extension/fuzzystrmatch.control" "fuzzystrmatch.control (contrib)"
require_file "${PREFIX}/include/liblwgeom.h" "liblwgeom.h (laplace_geom compile)"
require_file "${PREFIX}/lib/liblwgeom.a" "liblwgeom.a"

if [ -f /etc/ld.so.conf.d/laplace.conf ] && grep -q "${GEOS_PREFIX}/lib" /etc/ld.so.conf.d/laplace.conf; then
    ok "ld.so.conf.d prefers ${GEOS_PREFIX}/lib"
else
    warn "laplace.conf missing ${GEOS_PREFIX}/lib — run sudo just bootstrap (ldconfig)"
fi

if readelf -d "${PREFIX}/lib/postgis-3.so" 2>/dev/null | grep -q "${GEOS_PREFIX}/lib"; then
    ok "postgis-3.so RUNPATH includes ${GEOS_PREFIX}/lib"
else
    warn "postgis-3.so RUNPATH does not list ${GEOS_PREFIX}/lib"
fi

echo
echo "=== pg_config ==="
"${PREFIX}/bin/pg_config" --version
echo "sharedir: $("${PREFIX}/bin/pg_config" --sharedir)"
echo "pkglibdir: $("${PREFIX}/bin/pg_config" --pkglibdir)"

echo
echo "=== Cluster smoke (optional) ==="
export PGHOST="${SOCKET_DIR}"
export PGPORT
if ! "${PREFIX}/bin/pg_isready" -q 2>/dev/null; then
    warn "cluster not accepting connections on ${SOCKET_DIR}:${PGPORT} — skipping SQL smoke"
else
    ok "cluster accepting connections (${SOCKET_DIR}:${PGPORT})"
    DB="laplace_verify_pg_$$"
    cleanup_db() {
        "${PREFIX}/bin/psql" -d postgres -U "${PGUSER}" -q \
            -c "DROP DATABASE IF EXISTS ${DB};" 2>/dev/null || true
    }
    trap cleanup_db EXIT
    "${PREFIX}/bin/psql" -d postgres -U "${PGUSER}" -v ON_ERROR_STOP=1 -q <<SQL
CREATE DATABASE ${DB};
\\connect ${DB}
CREATE EXTENSION postgis;
CREATE EXTENSION postgis_topology;
CREATE EXTENSION postgis_raster;
CREATE EXTENSION fuzzystrmatch;
CREATE EXTENSION postgis_tiger_geocoder;
SELECT PostGIS_Full_Version() IS NOT NULL AS postgis_ok;
SELECT COUNT(*) > 0 FROM spatial_ref_sys;
SQL
    ok "CREATE EXTENSION postgis + topology + raster + tiger_geocoder"
    cleanup_db
    trap - EXIT
fi

echo
if [ "${errors}" -gt 0 ]; then
    printf "${red}%d error(s)${reset}" "${errors}"
    [ "${warnings}" -gt 0 ] && printf ", ${yellow}%d warning(s)${reset}" "${warnings}"
    echo
    echo "Fix: just build-deps  (then sudo just bootstrap if cluster/ldconfig missing)"
    exit 1
fi

printf "${green}PostgreSQL + PostGIS install OK${reset}"
[ "${warnings}" -gt 0 ] && printf " (${warnings} warning(s))"
echo
