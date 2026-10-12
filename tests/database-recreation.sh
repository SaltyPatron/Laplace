#!/usr/bin/env bash
# Exercise the installed migrator against PostgreSQL, including failed preparation.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source "$root/scripts/lib/database.sh"
runtime="${1:-${LAPLACE_APP_DIR:-/opt/laplace/app}/migrations/Laplace.Migrations.dll}"
[[ -f "$runtime" ]] || { echo "Migration runtime not found: $runtime" >&2; exit 1; }
name="laplace_recreate_check_$$"
owner="${name}_owner"
reader="${name}_reader"
P(){ psql -X -v ON_ERROR_STOP=1 -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$1" -Atq; }
cleanup(){
  printf 'DROP DATABASE IF EXISTS "%s" WITH (FORCE);\nDROP ROLE IF EXISTS "%s";\nDROP ROLE IF EXISTS "%s";\n' "$name" "$reader" "$owner" | P postgres
}
trap cleanup EXIT
printf 'CREATE ROLE "%s";\nCREATE ROLE "%s";\nCREATE DATABASE "%s" OWNER "%s" TEMPLATE template0 LOCALE_PROVIDER icu ICU_LOCALE '\''und'\'' ENCODING '\''UTF8'\'' LC_COLLATE '\''C'\'' LC_CTYPE '\''C'\'' CONNECTION LIMIT 23;\n' "$owner" "$reader" "$name" "$owner" | P postgres
copy_settings(){
  psql -X -v ON_ERROR_STOP=1 -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d postgres -Atq \
    -v source="$PGDATABASE" -v target="$name" <<'SQL'
SELECT format('ALTER DATABASE %I SET %I TO %L', :'target',split_part(v,'=',1),substr(v,strpos(v,'=')+1))
FROM pg_db_role_setting s JOIN pg_database d ON d.oid=s.setdatabase,LATERAL unnest(s.setconfig) v
WHERE d.datname=:'source' AND s.setrole=0
\gexec
SQL
}
copy_settings
connection="Host=$PGHOST;Port=$PGPORT;Username=$PGUSER;Database=$name;Search Path=laplace,public"
migrate(){ LAPLACE_DB="$connection" LAPLACE_APP_DB="$connection" dotnet "$runtime" "$@"; }
migrate up
psql -X -v ON_ERROR_STOP=1 -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$name" \
  -v target="$name" -v reader="$reader" -v owner="$PGUSER" <<'SQL'
CREATE TABLE public.recreate_sentinel(value integer);
INSERT INTO public.recreate_sentinel VALUES(41);
GRANT SELECT ON public.recreate_sentinel TO :"reader";
REVOKE ALL ON DATABASE :"target" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"target" TO :"reader";
GRANT USAGE ON SCHEMA app TO :"reader";
GRANT SELECT ON ALL TABLES IN SCHEMA app TO :"reader";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA app GRANT SELECT ON TABLES TO :"reader";
ALTER DATABASE :"target" SET work_mem='7MB';
ALTER ROLE :"reader" IN DATABASE :"target" SET work_mem='9MB';
ALTER DATABASE :"target" SET extension_control_path='/nonexistent/laplace-recreation-check';
SQL
if migrate nuke --yes; then echo 'FAIL accepted missing extension installation'; exit 1; fi
[[ "$(printf 'SELECT value FROM public.recreate_sentinel;' | P "$name")" == 41 ]]
echo 'PASS preparation failure leaves original database and data reachable'
copy_settings
for iteration in 1 2; do
  PGAPPNAME="$name-active" psql -X -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$name" -c 'SELECT pg_sleep(120)' &
  active=$!
  for attempt in {1..50}; do
    [[ "$(printf "SELECT count(*) FROM pg_stat_activity WHERE datname='%s' AND application_name='%s-active';" "$name" "$name" | P postgres)" == 1 ]] && break
    sleep 0.1
  done
  migrate nuke --yes
  wait "$active" || true # The live session must be disconnected by cutover.
  psql -X -v ON_ERROR_STOP=1 -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$name" \
    -v target="$name" -v reader="$reader" -v owner="$owner" <<'SQL'
SELECT (pg_get_userbyid(datdba)=:'owner' AND datconnlimit=23 AND datlocprovider='i' AND datlocale='und') AS preserved
FROM pg_database WHERE datname=current_database() \gset
\if :preserved
\else
\quit 1
\endif
SELECT current_setting('work_mem')='7MB' AND to_regclass('public.recreate_sentinel') IS NULL
 AND has_database_privilege(:'reader',current_database(),'CONNECT')
 AND has_schema_privilege(:'reader','app','USAGE')
 AND NOT has_schema_privilege(:'reader','app','CREATE')
 AND has_table_privilege(:'reader','app.tenants','SELECT')
 AND NOT has_table_privilege(:'reader','app.tenants','INSERT')
 AND EXISTS(SELECT FROM pg_db_role_setting s JOIN pg_roles r ON r.oid=s.setrole
   WHERE s.setdatabase=(SELECT oid FROM pg_database WHERE datname=current_database())
   AND r.rolname=:'reader' AND 'work_mem=9MB'=ANY(s.setconfig)) AS preserved \gset
\if :preserved
\else
\quit 1
\endif
CREATE TABLE app.recreate_default_check(value integer);
SELECT has_table_privilege(:'reader','app.recreate_default_check','SELECT') AS preserved \gset
\if :preserved
\else
\quit 1
\endif
DROP TABLE app.recreate_default_check;
SET ROLE :"reader";
SELECT count(*) FROM app.tenants;
RESET ROLE;
SQL
  echo "PASS recreation $iteration: owner, ICU locale, connection limit, module paths, database/role settings and restricted access retained"
done
