#!/usr/bin/env bash
# The modular Laplace extension regression suite against the installed extension:
# runs ../Laplace-postgres/tests/sql/*.sql via psql -w -X -q -v ON_ERROR_STOP=1 in a
# scratch database that takes the delivered database's settings (dynamic_library_path,
# extension_control_path, laplace.* cache paths), so CREATE EXTENSION laplace resolves
# exactly what was installed.
#
#   tests/substrate-regress.sh TEMPLATE_DB    the delivered database whose settings the scratch database takes
#
# Connection: the libpq environment (PGHOST, PGPORT, PGUSER); PG_CONFIG names the server's pg_config (default on PATH);
# the role creates and drops the scratch database. LAPLACE_REGRESS_DB names it (default laplace_regress_substrate).
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
template=${1:?usage: tests/substrate-regress.sh TEMPLATE_DB}
db=${LAPLACE_REGRESS_DB:-laplace_regress_substrate}
[[ $db =~ ^laplace_regress_[A-Za-z0-9_]+$ ]] || { echo "substrate-regress: $db is not a laplace_regress_* name" >&2; exit 2; }
pg_config=${PG_CONFIG:-pg_config}
bindir=$("$pg_config" --bindir)
tests_dir=${LAPLACE_POSTGRES_TESTS:-$(dirname "$ROOT")/Laplace-postgres/tests}
sql_dir=$tests_dir/sql
[[ -d $sql_dir ]] || { echo "substrate-regress: missing SQL test directory at $sql_dir" >&2; exit 2; }

out=$(mktemp -d "${TMPDIR:-/tmp}/substrate-regress.XXXXXX")
q() { psql -w -X -q -v ON_ERROR_STOP=1 -d postgres "$@"; }
cleanup() { "$bindir/dropdb" --if-exists "$db" >/dev/null 2>&1 || true; rm -rf "$out"; }
trap cleanup EXIT
cleanup
mkdir -p "$out"
"$bindir/createdb" "$db"

# Copy the template database's own settings onto the scratch database.
settings=$(q -At -c "SELECT unnest(s.setconfig) FROM pg_db_role_setting s JOIN pg_database d ON d.oid = s.setdatabase
                     WHERE d.datname = '$template' AND s.setrole = 0")
[[ -n $settings ]] || { echo "substrate-regress: $template has no database settings to take (is it deployed?)" >&2; exit 2; }
while IFS= read -r setting; do
    [[ -n $setting ]] || continue
    name=${setting%%=*} value=${setting#*=}
    [[ $name == session_preload_libraries ]] && continue
    q -c "ALTER DATABASE \"$db\" SET $name = \$v\$$value\$v\$"
done <<<"$settings"

psql -w -X -q -d "$db" -v ON_ERROR_STOP=1 -c "CREATE EXTENSION IF NOT EXISTS laplace CASCADE;" >/dev/null

declare -a cases=()
if [[ -f $tests_dir/regression_cases.txt ]]; then
    while IFS= read -r c; do
        [[ -n $c ]] && cases+=("$sql_dir/$c.sql")
    done < <(tr -d '\r' <"$tests_dir/regression_cases.txt" | grep -v '^[[:space:]]*$')
fi
for f in "$sql_dir"/*.sql; do
    [[ $(basename "$f") == geometry_cases.sql ]] && continue
    already=0
    for existing in "${cases[@]:-}"; do
        [[ $existing == "$f" ]] && { already=1; break; }
    done
    (( already == 0 )) && cases+=("$f")
done
(( ${#cases[@]} > 0 )) || { echo "substrate-regress: no SQL cases found in $sql_dir" >&2; exit 2; }

status=0
for f in "${cases[@]}"; do
    psql -w -X -q -d "$db" -v ON_ERROR_STOP=1 -f "$f" >/dev/null \
        || { echo "substrate-regress: psql case $(basename "$f" .sql) failed" >&2; status=1; }
done
(( status == 0 )) && echo "substrate-regress: ${#cases[@]} SQL cases passed against installed laplace extension"
exit "$status"
