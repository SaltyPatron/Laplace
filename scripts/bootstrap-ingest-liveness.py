#!/usr/bin/env python3
"""Install the canonical SQL-only ingest liveness surface before native upgrade.

The extension version is unchanged. New functions become members of the existing
extension so its ordinary future upgrade can replace them safely.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SOURCE = "extension/laplace_substrate/sql/functions/ops/ingest_runs.sql.in"
FUNCTIONS = (
    "ops.ingest_run_live(uuid,interval)",
    "ops.ingest_run_touch()",
    "ops.ingest_file_touch_run()",
    "ops.ingest_reconcile_orphans(interval)",
    "ops.ingest_runs(integer)",
)
PROBE = r"""SELECT jsonb_build_object(
'database',current_database(),'current_user',current_user,'server_version_num',current_setting('server_version_num'),
'extension',(SELECT jsonb_build_object('oid',e.oid,'version',e.extversion,'schema',n.nspname,'owner',r.rolname,'caller_superuser',(SELECT rolsuper FROM pg_roles WHERE rolname=current_user)) FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace JOIN pg_roles r ON r.oid=e.extowner WHERE e.extname='laplace_substrate'),
'schemas',(SELECT jsonb_agg(jsonb_build_object('name',n.nspname,'owner',pg_get_userbyid(n.nspowner),'extension',e.extname)) FROM pg_namespace n LEFT JOIN pg_depend d ON d.classid='pg_namespace'::regclass AND d.objid=n.oid AND d.refclassid='pg_extension'::regclass AND d.deptype='e' LEFT JOIN pg_extension e ON e.oid=d.refobjid WHERE n.nspname IN ('laplace','ops')),
'tables',(SELECT jsonb_agg(jsonb_build_object('name',c.oid::regclass::text,'owner',pg_get_userbyid(c.relowner),'kind',c.relkind,'extension',e.extname,'columns',(SELECT jsonb_agg(jsonb_build_object('name',a.attname,'type',format_type(a.atttypid,a.atttypmod),'notnull',a.attnotnull)) FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped))) FROM pg_class c LEFT JOIN pg_depend d ON d.classid='pg_class'::regclass AND d.objid=c.oid AND d.refclassid='pg_extension'::regclass AND d.deptype='e' LEFT JOIN pg_extension e ON e.oid=d.refobjid WHERE c.oid IN (to_regclass('laplace.ingest_run_journal'),to_regclass('laplace.ingest_file_journal'))),
'functions',(SELECT jsonb_agg(jsonb_build_object('name',s.signature,'present',p.oid IS NOT NULL,'owner',pg_get_userbyid(p.proowner),'extension',e.extname,'result',pg_get_function_result(p.oid))) FROM (VALUES ('ops.ingest_run_live(uuid,interval)'),('ops.ingest_run_touch()'),('ops.ingest_file_touch_run()'),('ops.ingest_reconcile_orphans(interval)'),('ops.ingest_runs(integer)'),('laplace.ingest_runs(integer)')) s(signature) LEFT JOIN pg_proc p ON p.oid=to_regprocedure(s.signature) LEFT JOIN pg_depend d ON d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.refclassid='pg_extension'::regclass AND d.deptype='e' LEFT JOIN pg_extension e ON e.oid=d.refobjid),
'triggers',(SELECT jsonb_agg(jsonb_build_object('table',t.tgrelid::regclass::text,'name',t.tgname,'function',t.tgfoid::regprocedure::text)) FROM pg_trigger t WHERE NOT t.tgisinternal AND t.tgrelid IN (to_regclass('laplace.ingest_run_journal'),to_regclass('laplace.ingest_file_journal'))));"""

PREFLIGHT = r"""
SET LOCAL lock_timeout = '20s';
SET LOCAL statement_timeout = '60s';
SET LOCAL search_path = pg_catalog, laplace, ops;
DO $preflight$
DECLARE
    ext pg_extension%ROWTYPE;
    item record;
    object_owner oid;
    member oid;
    identifier oid;
BEGIN
    SELECT * INTO STRICT ext FROM pg_extension WHERE extname = 'laplace_substrate';
    IF ext.extnamespace <> to_regnamespace('laplace') THEN
        RAISE EXCEPTION 'substrate extension must use the canonical laplace schema';
    END IF;
    IF NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user)
       AND ext.extowner <> (SELECT oid FROM pg_roles WHERE rolname = current_user) THEN
        RAISE EXCEPTION 'caller must own laplace_substrate or be a superuser';
    END IF;
    FOR item IN SELECT * FROM (VALUES ('laplace'), ('ops')) AS s(name)
    LOOP
        SELECT n.oid, n.nspowner INTO identifier, object_owner
          FROM pg_namespace n WHERE n.nspname = item.name;
        IF identifier IS NULL OR object_owner <> ext.extowner THEN
            RAISE EXCEPTION 'unexpected schema owner: %', item.name;
        END IF;
        SELECT d.refobjid INTO member FROM pg_depend d
         WHERE d.classid = 'pg_namespace'::regclass AND d.objid = identifier
           AND d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e';
        -- The main extension namespace itself is not necessarily a member.
        IF (item.name = 'ops' AND member IS DISTINCT FROM ext.oid)
           OR (member IS NOT NULL AND member <> ext.oid) THEN
            RAISE EXCEPTION 'unexpected schema extension membership: %', item.name;
        END IF;
    END LOOP;
    FOR item IN SELECT * FROM (VALUES
        ('laplace.ingest_run_journal'), ('laplace.ingest_file_journal')) AS t(name)
    LOOP
        identifier := to_regclass(item.name);
        SELECT c.relowner INTO object_owner FROM pg_class c
         WHERE c.oid = identifier AND c.relkind = 'r';
        SELECT d.refobjid INTO member FROM pg_depend d
         WHERE d.classid = 'pg_class'::regclass AND d.objid = identifier
           AND d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e';
        IF object_owner IS DISTINCT FROM ext.extowner OR member IS DISTINCT FROM ext.oid THEN
            RAISE EXCEPTION 'unexpected journal table owner/membership: %', item.name;
        END IF;
    END LOOP;
    IF to_regprocedure('laplace.ingest_runs(integer)') IS NOT NULL THEN
        RAISE EXCEPTION 'legacy laplace.ingest_runs requires the ordinary full extension upgrade';
    END IF;
    FOR item IN SELECT * FROM (VALUES
        ('laplace.ingest_run_journal','run_id','uuid'),
        ('laplace.ingest_run_journal','source_name','text'),
        ('laplace.ingest_run_journal','status','text'),
        ('laplace.ingest_run_journal','phase','text'),
        ('laplace.ingest_run_journal','started_at','timestamp with time zone'),
        ('laplace.ingest_run_journal','ended_at','timestamp with time zone'),
        ('laplace.ingest_run_journal','files_done','bigint'),
        ('laplace.ingest_run_journal','files_total','bigint'),
        ('laplace.ingest_run_journal','input_units_done','bigint'),
        ('laplace.ingest_run_journal','input_units_total','bigint'),
        ('laplace.ingest_run_journal','error','text'),
        ('laplace.ingest_file_journal','run_id','uuid'),
        ('laplace.ingest_file_journal','status','text'),
        ('laplace.ingest_file_journal','ended_at','timestamp with time zone'),
        ('laplace.ingest_file_journal','error','text')
    ) AS c(relation_name, column_name, type_name)
    LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_attribute a
            WHERE a.attrelid = to_regclass(item.relation_name)
              AND a.attname = item.column_name AND NOT a.attisdropped
              AND a.atttypid = item.type_name::regtype) THEN
            RAISE EXCEPTION 'missing or incompatible journal column: %.%',
                item.relation_name, item.column_name;
        END IF;
    END LOOP;
    IF EXISTS (SELECT 1 FROM pg_attribute a
        WHERE a.attrelid = 'laplace.ingest_run_journal'::regclass
          AND a.attname = 'heartbeat_at' AND NOT a.attisdropped
          AND (a.atttypid <> 'timestamptz'::regtype OR NOT a.attnotnull)) THEN
        RAISE EXCEPTION 'incompatible heartbeat column';
    END IF;
    FOR item IN SELECT * FROM (VALUES
        ('ops.ingest_run_live(uuid,interval)'), ('ops.ingest_run_touch()'),
        ('ops.ingest_file_touch_run()'), ('ops.ingest_reconcile_orphans(interval)'),
        ('ops.ingest_runs(integer)')) AS f(signature)
    LOOP
        identifier := to_regprocedure(item.signature);
        IF identifier IS NULL THEN CONTINUE; END IF;
        SELECT p.proowner INTO object_owner FROM pg_proc p WHERE p.oid = identifier;
        SELECT d.refobjid INTO member FROM pg_depend d
         WHERE d.classid = 'pg_proc'::regclass AND d.objid = identifier
           AND d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e';
        IF object_owner <> ext.extowner OR (member IS NOT NULL AND member <> ext.oid) THEN
            RAISE EXCEPTION 'unexpected existing function owner/membership: %', item.signature;
        END IF;
    END LOOP;
    FOR item IN SELECT * FROM (VALUES
        ('laplace.ingest_run_journal','ingest_run_touch','ops.ingest_run_touch()'),
        ('laplace.ingest_file_journal','ingest_file_touch_run','ops.ingest_file_touch_run()')
    ) AS t(relation_name, trigger_name, function_name)
    LOOP
        IF EXISTS (SELECT 1 FROM pg_trigger t
            WHERE t.tgrelid = to_regclass(item.relation_name)
              AND t.tgname = item.trigger_name
              AND (t.tgisinternal OR t.tgfoid IS DISTINCT FROM to_regprocedure(item.function_name))) THEN
            RAISE EXCEPTION 'unexpected existing trigger: %', item.trigger_name;
        END IF;
    END LOOP;
    -- Newly created SECURITY DEFINER functions retain the extension's owner.
    EXECUTE format('SET LOCAL ROLE %I', pg_get_userbyid(ext.extowner));
END
$preflight$;
LOCK TABLE laplace.ingest_run_journal, laplace.ingest_file_journal IN ACCESS EXCLUSIVE MODE;
"""

ADOPT = r"""
DO $membership$
DECLARE
    signature text;
    ext oid := (SELECT oid FROM pg_extension WHERE extname = 'laplace_substrate');
    member oid;
BEGIN
    FOREACH signature IN ARRAY ARRAY[
        'ops.ingest_run_live(uuid,interval)', 'ops.ingest_run_touch()',
        'ops.ingest_file_touch_run()', 'ops.ingest_reconcile_orphans(interval)',
        'ops.ingest_runs(integer)']
    LOOP
        SELECT d.refobjid INTO member FROM pg_depend d
         WHERE d.classid = 'pg_proc'::regclass AND d.objid = to_regprocedure(signature)
           AND d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e';
        IF member IS NULL THEN
            EXECUTE 'ALTER EXTENSION laplace_substrate ADD FUNCTION ' || signature;
        ELSIF member <> ext THEN
            RAISE EXCEPTION 'unexpected canonical function extension membership: %', signature;
        END IF;
    END LOOP;
END
$membership$;
"""

def render(root=ROOT):
    path = Path(root) / SOURCE
    source = path.read_bytes()
    template = source.decode("utf-8")
    if "@extschema@" not in template:
        raise ValueError("canonical source has no extension schema substitution")
    body = template.replace("@extschema@", "laplace")
    if "@" in body or "MODULE_PATHNAME" in body or "EXECUTION_LIBRARY" in body:
        raise ValueError("canonical liveness source requires unsupported preprocessing")
    return PREFLIGHT + "\n" + body + "\n" + ADOPT, hashlib.sha256(source).hexdigest()

def psql(args, sql, *, transaction=False):
    command = [args.psql, "-X", "--no-password", "-v", "ON_ERROR_STOP=1",
               "-h", args.host, "-U", args.user, "-d", args.database, "-tAX"]
    if transaction:
        command.append("--single-transaction")
    command.extend(["-f", "-"])
    result = subprocess.run(command, input=sql, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=120, check=False)
    if result.stderr:
        print(result.stderr, file=sys.stderr, end="")
    if result.returncode:
        raise RuntimeError(f"psql exited {result.returncode}")
    return result.stdout

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("probe", "apply"))
    parser.add_argument("--database", required=True)
    parser.add_argument("--host", default=os.environ.get("PGHOST", "/var/run/postgresql"))
    parser.add_argument("--user", default=os.environ.get("PGUSER", "laplace_admin"))
    parser.add_argument("--psql", default="psql")
    args = parser.parse_args(argv)
    before = json.loads(psql(args, PROBE))
    print(json.dumps({"stage": "before", "observation": before}), flush=True)
    if args.action == "probe":
        return 0
    if before["extension"] is None and before["tables"] is None:
        print(json.dumps({"stage": "not-installed", "applied": False}), flush=True)
        return 0
    sql, source_sha = render()
    psql(args, sql, transaction=True)
    after = json.loads(psql(args, PROBE))
    if after["extension"] != before["extension"]:
        raise RuntimeError("extension identity/version changed during SQL-only bootstrap")
    for function in after["functions"]:
        if function["name"] in FUNCTIONS and (
                not function["present"] or function["extension"] != "laplace_substrate"
                or function["owner"] != after["extension"]["owner"]):
            raise RuntimeError("canonical function ownership verification failed")
    print(json.dumps({"stage": "after", "source": SOURCE, "source_sha256": source_sha,
                      "applied": True, "observation": after}), flush=True)
    return 0

if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"ingest liveness bootstrap refused: {error}", file=sys.stderr)
        raise SystemExit(1)
