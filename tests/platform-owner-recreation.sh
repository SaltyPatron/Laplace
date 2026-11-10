#!/usr/bin/env bash
set -euo pipefail
runtime=${1:?Pass the compiled Laplace.Migrations.dll}
# This test may recreate only its named fixture on the isolated CI PostgreSQL service.
[[ ${PGHOST:-} == 127.0.0.1 && ${PGUSER:-} == postgres && ${PGDATABASE:-} == postgres ]]
export LAPLACE_DB="Host=127.0.0.1;Port=${PGPORT:-5432};Username=postgres;Password=${PGPASSWORD:?};Database=postgres"
export LAPLACE_APP_DB="Host=127.0.0.1;Port=${PGPORT:-5432};Username=postgres;Password=$PGPASSWORD;Database=platform_owner_fixture"
export LAPLACE_AUTH_GOOGLE_CLIENT_ID=fixture-google-client
export LAPLACE_AUTH_MICROSOFT_CLIENT_ID=fixture-microsoft-client
export LAPLACE_PLATFORM_OWNER_POLICY='{"version":1,"owners":[{"provider":"google","issuer":"https://accounts.google.com","subject":"fixture-owner-google","client_id":"fixture-google-client","verified_at":"2026-01-01T00:00:00Z"},{"provider":"microsoft","issuer":"https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0","subject":"fixture-owner-microsoft","client_id":"fixture-microsoft-client","verified_at":"2026-01-02T00:00:00Z"}]}'
query() { psql -X -v ON_ERROR_STOP=1 -d platform_owner_fixture -Atc "$1"; }
owners() {
  query "SELECT count(*)=2 AND bool_and(r.role='administrator') AND bool_and(e.last_login_at=CASE e.provider WHEN 'google' THEN timestamptz '2026-01-01T00:00:00Z' ELSE timestamptz '2026-01-02T00:00:00Z' END) FROM app.external_identities e JOIN app.platform_roles r USING(user_id) WHERE e.subject IN ('fixture-owner-google','fixture-owner-microsoft')" | grep -qx t
  query "SELECT count(*)=2 FROM app.platform_role_events e JOIN app.external_identities i USING(user_id) WHERE i.subject IN ('fixture-owner-google','fixture-owner-microsoft') AND e.actor='installation-owner' AND e.role='administrator'" | grep -qx t
  query 'SELECT count(*)=0 FROM app.web_sessions' | grep -qx t
}
dotnet "$runtime" app-up
dotnet "$runtime" app-check
owners
before=$(query "SELECT string_agg(user_id::text,',' ORDER BY provider) FROM app.external_identities WHERE subject IN ('fixture-owner-google','fixture-owner-microsoft')")
dotnet "$runtime" app-up
owners
[[ $before == "$(query "SELECT string_agg(user_id::text,',' ORDER BY provider) FROM app.external_identities WHERE subject IN ('fixture-owner-google','fixture-owner-microsoft')")" ]]

# Invalid authority must be rejected before recreation can erase any application data.
query 'CREATE TABLE public.owner_recreation_sentinel(value integer); INSERT INTO public.owner_recreation_sentinel VALUES(1)' >/dev/null
if LAPLACE_AUTH_GOOGLE_CLIENT_ID=wrong-client dotnet "$runtime" app-nuke --yes; then
  echo 'FAIL: mismatched owner client was accepted' >&2; exit 1
fi
query 'SELECT count(*)=1 FROM public.owner_recreation_sentinel' | grep -qx t
owners

# A recreated database restores authority, not authentication tickets or arbitrary account rows.
query "INSERT INTO app.web_sessions(session_id,user_id,tenant_id,ticket,expires_at) SELECT 'fixture-ticket',m.user_id,m.tenant_id,decode('00','hex'),now()+interval '1 day' FROM app.tenant_memberships m LIMIT 1" >/dev/null
dotnet "$runtime" app-nuke --yes
dotnet "$runtime" app-check
owners
[[ $before != "$(query "SELECT string_agg(user_id::text,',' ORDER BY provider) FROM app.external_identities WHERE subject IN ('fixture-owner-google','fixture-owner-microsoft')")" ]]
query "SELECT to_regclass('public.owner_recreation_sentinel') IS NULL" | grep -qx t
dotnet "$runtime" app-up
owners

# Co-located application/substrate topology (where LAPLACE_DB and LAPLACE_APP_DB name the same database)
# recreates only SCHEMA app while preserving public/substrate tables and app schema grants. There is no migration
# journal: no verb creates public.schemaversions, and re-applying the declaration is the only way to repair.
export LAPLACE_DB="$LAPLACE_APP_DB"
query "CREATE ROLE platform_owner_app_runtime NOLOGIN; GRANT USAGE ON SCHEMA app TO platform_owner_app_runtime; GRANT SELECT ON ALL TABLES IN SCHEMA app TO platform_owner_app_runtime; ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO platform_owner_app_runtime" >/dev/null
query "CREATE TABLE public.colocated_substrate_sentinel(value integer); INSERT INTO public.colocated_substrate_sentinel VALUES(42)" >/dev/null
colocated_before=$(query "SELECT string_agg(user_id::text,',' ORDER BY provider) FROM app.external_identities WHERE subject IN ('fixture-owner-google','fixture-owner-microsoft')")
query "INSERT INTO app.web_sessions(session_id,user_id,tenant_id,ticket,expires_at) SELECT 'colocated-ticket',m.user_id,m.tenant_id,decode('00','hex'),now()+interval '1 day' FROM app.tenant_memberships m LIMIT 1" >/dev/null
dotnet "$runtime" app-nuke --yes
dotnet "$runtime" app-check
owners
[[ $colocated_before != "$(query "SELECT string_agg(user_id::text,',' ORDER BY provider) FROM app.external_identities WHERE subject IN ('fixture-owner-google','fixture-owner-microsoft')")" ]]
query 'SELECT count(*)=1 AND max(value)=42 FROM public.colocated_substrate_sentinel' | grep -qx t
query "SELECT to_regclass('public.schemaversions') IS NULL" | grep -qx t
query "SELECT has_schema_privilege('platform_owner_app_runtime','app','USAGE') AND has_table_privilege('platform_owner_app_runtime','app.users','SELECT')" | grep -qx t
# app-reset keeps no journal to clear: it re-applies the declaration, restoring a dropped grant and keeping data.
# laplace_readonly is a declared role (db/schema/40_app_grants.sql): a grant lost on it is drift, and applying repairs it.
query "CREATE ROLE laplace_readonly NOLOGIN" >/dev/null
dotnet "$runtime" app-up
query "SELECT has_schema_privilege('laplace_readonly','app','USAGE') AND has_table_privilege('laplace_readonly','app.users','SELECT')" | grep -qx t
dotnet "$runtime" app-status
query "REVOKE SELECT ON app.users FROM laplace_readonly" >/dev/null
query "SELECT NOT has_table_privilege('laplace_readonly','app.users','SELECT')" | grep -qx t
if dotnet "$runtime" app-status; then echo 'FAIL status did not report a stale grant as drift' >&2; exit 1; fi
if dotnet "$runtime" app-check; then echo 'FAIL app-check accepted a stale grant' >&2; exit 1; fi
dotnet "$runtime" app-reset --yes
query "SELECT has_table_privilege('laplace_readonly','app.users','SELECT')" | grep -qx t
query "SELECT to_regclass('public.schemaversions') IS NULL" | grep -qx t
query 'SELECT count(*)=1 AND max(value)=42 FROM public.colocated_substrate_sentinel' | grep -qx t
dotnet "$runtime" app-up
dotnet "$runtime" app-check
dotnet "$runtime" app-status
owners
query "DROP OWNED BY platform_owner_app_runtime; DROP ROLE platform_owner_app_runtime; DROP OWNED BY laplace_readonly; DROP ROLE laplace_readonly" >/dev/null
echo 'PASS real app-up, app-check and app-nuke restore both verified owners without sessions, duplicate grants or historical local UUIDs'
