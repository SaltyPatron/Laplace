#!/usr/bin/env bash
# The laplace_substrate regression suite against the installed extensions: pg_regress over every test
# extension/laplace_substrate/tests/CMakeLists.txt lists (REGRESS_TESTS), then its closed-world psql cases, in a scratch
# database that takes the delivered database's extension settings (dynamic_library_path, extension_control_path, the
# perf-cache paths, session_preload_libraries), so CREATE EXTENSION there resolves exactly what was installed. The
# installed extensions are this revision's after `site.sh install`, so a green run is evidence about this tree.
#
#   tests/substrate-regress.sh TEMPLATE_DB    the delivered database whose settings the scratch database takes
#
# Connection: the libpq environment (PGHOST, PGPORT, PGUSER); PG_CONFIG names the server's pg_config (default on PATH); the role creates and drops the scratch database.
# LAPLACE_REGRESS_DB names it (default laplace_regress_substrate); it is dropped before and after. Exit 1 on any
# difference, with the differences printed.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
template=${1:?usage: tests/substrate-regress.sh TEMPLATE_DB}
db=${LAPLACE_REGRESS_DB:-laplace_regress_substrate}
[[ $db =~ ^laplace_regress_[A-Za-z0-9_]+$ ]] || { echo "substrate-regress: $db is not a laplace_regress_* name" >&2; exit 2; }
pg_config=${PG_CONFIG:-pg_config}
bindir=$("$pg_config" --bindir)
regress=$("$pg_config" --pkglibdir)/pgxs/src/test/regress/pg_regress
[[ -x $regress ]] || { echo "substrate-regress: no pg_regress at $regress" >&2; exit 2; }
tests_dir=$ROOT/extension/laplace_substrate/tests
cmake_list=$tests_dir/CMakeLists.txt
# REGRESS_TESTS: the set(...) line and every list(APPEND REGRESS_TESTS ...) after it, in order
cmake_text=$(tr -d '\r' <"$cmake_list")
read -r -a tests <<<"$(sed -n -e 's/^set(REGRESS_TESTS \(.*\))$/\1/p' -e 's/^list(APPEND REGRESS_TESTS \(.*\))$/\1/p' <<<"$cmake_text" | tr '\n' ' ')"
read -r -a cases <<<"$(sed -n 's/^foreach(case \(.*\))$/\1/p' <<<"$cmake_text")"
(( ${#tests[@]} > 0 )) || { echo "substrate-regress: no REGRESS_TESTS in $cmake_list" >&2; exit 2; }
out=$(mktemp -d "${TMPDIR:-/tmp}/substrate-regress.XXXXXX")
q() { psql -X -q -v ON_ERROR_STOP=1 -d postgres "$@"; }
cleanup() { "$bindir/dropdb" --if-exists "$db" >/dev/null 2>&1 || true; }
trap cleanup EXIT
cleanup
"$bindir/createdb" "$db"
# the template's own database settings, each set again on the scratch database
settings=$(q -At -c "SELECT unnest(s.setconfig) FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase
                     WHERE d.datname = '$template' AND s.setrole = 0")
[[ -n $settings ]] || { echo "substrate-regress: $template has no database settings to take (is it deployed?)" >&2; exit 2; }
while IFS= read -r setting; do
    name=${setting%%=*} value=${setting#*=}
    q -c "ALTER DATABASE \"$db\" SET $name = \$v\$$value\$v\$"
done <<<"$settings"
# LAPLACE_REGRESS_PREFIX: another installation of this tree (a pull request's, from site.sh build install with that
# LAPLACE_INSTALL_PREFIX) is what the scratch database loads: its modules, control files and laplace.* caches. The
# laplace_substrate perf-cache paths are server settings (PGC_SIGHUP), so those stay the delivered ones.
if [[ -n ${LAPLACE_REGRESS_PREFIX:-} ]]; then
    prefix=$LAPLACE_REGRESS_PREFIX
    major=$("$pg_config" --version | sed -E 's/^PostgreSQL ([0-9]+).*/\1/')
    [[ -d $prefix/lib/postgresql/$major && -d $prefix/share/postgresql/$major/extension ]] \
        || { echo "substrate-regress: $prefix holds no PostgreSQL $major installation" >&2; exit 2; }
    q -c "ALTER DATABASE \"$db\" SET dynamic_library_path = \$v\$$prefix/lib/postgresql/$major:\$libdir\$v\$" \
      -c "ALTER DATABASE \"$db\" SET extension_control_path = \$v\$$prefix/share/postgresql/$major:\$system\$v\$"
    for g in tier0:tier0.bin flags:tier0.flags highway:highway.bin; do
        [[ -s $prefix/share/laplace/${g#*:} ]] || { echo "substrate-regress: $prefix lacks share/laplace/${g#*:}" >&2; exit 2; }
        q -c "ALTER DATABASE \"$db\" SET laplace.${g%%:*} = \$v\$$prefix/share/laplace/${g#*:}\$v\$"
    done
fi
status=0
(cd "$out" && "$regress" --bindir="$bindir" --inputdir="$tests_dir" --outputdir="$out" --dbname="$db" \
    --user="${PGUSER:-$(id -un)}" --use-existing "${tests[@]}") || status=$?
if (( status != 0 )); then
    cat "$out/regression.diffs" >&2 || true
    echo "substrate-regress: pg_regress failed ($(grep -c '^not ok' "$out/regression.out" 2>/dev/null || echo '?') of ${#tests[@]})" >&2
fi
for c in "${cases[@]}"; do
    psql -X -q --no-password -d "$db" -v ON_ERROR_STOP=1 -f "$tests_dir/sql/$c.sql" >/dev/null \
        || { echo "substrate-regress: psql case $c failed" >&2; status=1; }
done
(( status == 0 )) && echo "substrate-regress: ${#tests[@]} pg_regress tests and ${#cases[@]} psql cases passed against the installed extensions"
rm -rf "$out"
exit "$status"
