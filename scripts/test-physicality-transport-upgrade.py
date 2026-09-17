#!/usr/bin/env python3
"""Exercise the real 22-to-24 materializer extension upgrade in an isolated PG18 DB.

Requires a PostgreSQL 18 superuser test connection, writable selected prefix
extension directory, and a compiled materializer library (or explicit DDL-only symbol fixture). No
production database is opened; uniquely named test roles and DB are removed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

SIGNATURE = "ops.physicality_descriptor_materialize(bytea[],bytea[],bytea[],bytea[],bytea[],bytea[],bytea[],bytea[],float8[],bigint,bigint,integer,bigint)"
ROOT = Path(__file__).resolve().parents[1]


def literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pg-config", required=True, type=Path)
    parser.add_argument("--execution-library", required=True, type=Path)
    parser.add_argument("--maintenance-db", default="postgres")
    parser.add_argument("--ddl-symbol-fixture", action="store_true",
                        help="label this run as extension DDL only, never native materialization proof")
    parser.add_argument("--old-template", type=Path,
                        default=ROOT / "scripts/testdata/physicality-descriptor-materialize-22.sql")
    parser.add_argument("--new-template", type=Path,
                        default=ROOT / "extension/laplace_substrate/sql/functions/ingest/physicality_descriptor_materialize.sql.in")
    parser.add_argument("--receipt", required=True, type=Path)
    args = parser.parse_args()
    token = uuid.uuid4().hex[:16]
    extension = "laplace_transport_acl_" + token
    database = extension
    roles = {kind: extension + "_" + kind for kind in ("owner", "grantor", "reader", "defaults")}
    owned_files: list[Path] = []
    created_roles: list[str] = []
    database_created = False
    checks: list[dict] = []
    receipt = {"schema": "laplace.materializer-output-upgrade-test/v1",
               "status": "failed", "checks": checks, "testDatabase": database,
               "libraryScope": "ddl-symbol-fixture" if args.ddl_symbol_fixture else "actual-execution-library"}
    pg_config = args.pg_config.resolve(strict=True)
    library = args.execution_library.resolve(strict=True)

    def config(flag: str) -> str:
        return subprocess.run([str(pg_config), flag], check=True, text=True,
                              capture_output=True, timeout=30).stdout.strip()

    psql = Path(config("--bindir")) / "psql"
    extension_dir = Path(config("--sharedir")) / "extension"

    def sql(body: str, db: str | None = None) -> str:
        # SQL is sent over stdin, never interpolated into a shell command.
        result = subprocess.run(
            [str(psql), "-X", "--no-password", "--set=ON_ERROR_STOP=1",
             "--tuples-only", "--no-align", "--dbname", db or args.maintenance_db],
            input=body, text=True, capture_output=True, timeout=90)
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or "psql failed")
        return result.stdout.strip()

    def write_owned(path: Path, text: str) -> None:
        with path.open("x", encoding="utf-8") as stream:
            owned_files.append(path)
            stream.write(text)
        path.chmod(0o644)

    def state() -> dict:
        text = sql("""
SELECT jsonb_build_object(
 'oid',p.oid,'owner',pg_get_userbyid(p.proowner),
 'outputCount',cardinality(p.proallargtypes)-p.pronargs,
 'acl',coalesce((SELECT jsonb_agg(jsonb_build_array(
       pg_get_userbyid(a.grantor),
       CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END,
       a.privilege_type,a.is_grantable)
       ORDER BY a.grantor,a.grantee,a.privilege_type,a.is_grantable)
     FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) a),'[]'::jsonb),
 'extensionMembers',(SELECT count(*) FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
     WHERE d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.refclassid='pg_extension'::regclass
       AND d.deptype='e' AND e.extname=""" + literal(extension) + """),
 'extensionVersion',(SELECT extversion FROM pg_extension WHERE extname=""" + literal(extension) + """),
 'role',current_setting('role'),
 'scratchEmpty',coalesce(current_setting('laplace.physicality_transport_upgrade',true),'')='')
FROM pg_proc p WHERE p.oid=to_regprocedure(""" + literal(SIGNATURE) + ");", database)
        return json.loads(text)

    def equivalent(before: dict, after: dict, shape: int, version: str) -> None:
        assert after["owner"] == before["owner"], (before, after)
        assert after["acl"] == before["acl"], (before, after)
        assert after["outputCount"] == shape and after["extensionVersion"] == version, after
        assert after["extensionMembers"] == 1 and after["scratchEmpty"], after
        assert after["role"] == before["role"], after

    try:
        version = int(sql("SHOW server_version_num;"))
        if version // 10000 != 18:
            raise RuntimeError("this control requires an actual PostgreSQL 18 server")
        if sql("SELECT rolsuper FROM pg_roles WHERE rolname=current_user;") != "t":
            raise RuntimeError("isolated extension/role control requires the test cluster superuser")
        old = args.old_template.read_text(encoding="utf-8")
        new = args.new_template.read_text(encoding="utf-8")
        for label, text in (("old", old), ("new", new)):
            if text.count("'EXECUTION_LIBRARY'") != 1:
                raise RuntimeError(label + " template does not select exactly one real execution library")
        old = old.replace("'EXECUTION_LIBRARY'", literal(str(library)))
        new = new.replace("'EXECUTION_LIBRARY'", literal(str(library)))
        fault_anchor = "PERFORM pg_catalog.set_config('role', grantor_name, true);"
        if new.count(fault_anchor) != 1:
            raise RuntimeError("exact grantor restoration fault point changed")
        failing = new.replace(fault_anchor, fault_anchor +
                              "\n                RAISE EXCEPTION 'transport ACL fixture rollback';", 1)
        receipt.update({"postgresVersion": version, "executionLibrary": str(library),
                        "executionLibrarySha256": sha(library),
                        "oldTemplateSha256": sha(args.old_template),
                        "newTemplateSha256": sha(args.new_template)})
        write_owned(extension_dir / (extension + ".control"),
                    "comment = 'isolated materializer output ACL regression'\n"
                    "default_version = '2'\nrelocatable = false\nsuperuser = true\n")
        for suffix, text in (("--1.sql", old), ("--2.sql", new),
                             ("--1--2.sql", new), ("--2--3.sql", new),
                             ("--1--fault.sql", failing)):
            write_owned(extension_dir / (extension + suffix), text)
        for role in roles.values():
            sql("CREATE ROLE " + role + " NOLOGIN;")
            created_roles.append(role)
        sql("CREATE DATABASE " + database + " TEMPLATE template0;")
        database_created = True
        sql("CREATE SCHEMA ops; GRANT USAGE ON SCHEMA ops TO " +
            ",".join(roles.values()) + ";\n"
            "CREATE EXTENSION " + extension + " VERSION '1';\n"
            "ALTER FUNCTION " + SIGNATURE + " OWNER TO " + roles["owner"] + ";\n"
            "SET ROLE " + roles["owner"] + ";\n"
            "REVOKE ALL ON FUNCTION " + SIGNATURE + " FROM PUBLIC;\n"
            "GRANT EXECUTE ON FUNCTION " + SIGNATURE + " TO " + roles["grantor"] +
            " WITH GRANT OPTION;\nRESET ROLE;\n"
            "SET ROLE " + roles["grantor"] + ";\n"
            "GRANT EXECUTE ON FUNCTION " + SIGNATURE + " TO " + roles["reader"] +
            ";\nRESET ROLE;\n"
            "ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO " + roles["defaults"] + ";",
            database)
        before = state()
        assert before["outputCount"] == 22 and before["extensionMembers"] == 1, before
        assert any(row[0] == roles["grantor"] and row[1] == roles["reader"] and not row[3]
                   for row in before["acl"]), before
        assert any(row[1] == roles["grantor"] and row[3] for row in before["acl"]), before
        assert all(row[1] != "PUBLIC" for row in before["acl"]), before
        checks.append({"name": "real-old-member-custom-owner-delegated-acl", "passed": True,
                       "state": before})
        # A real extension update throws after SET ROLE to the saved grantor.
        # Both its handler and the extension transaction must preserve the old
        # member and restore the session role on failure.
        sql("DO $test$ DECLARE caught boolean := false; BEGIN\n"
            "BEGIN ALTER EXTENSION " + extension + " UPDATE TO 'fault';\n"
            "EXCEPTION WHEN raise_exception THEN\n"
            "IF SQLERRM <> 'transport ACL fixture rollback' THEN RAISE; END IF;\n"
            "caught := true; END;\n"
            "IF NOT caught THEN RAISE EXCEPTION 'fault injection did not execute'; END IF;\n"
            "IF current_setting('role') <> 'none' THEN RAISE EXCEPTION 'role was not restored'; END IF;\n"
            "END $test$;", database)
        failed = state()
        equivalent(before, failed, 22, "1")
        assert failed["oid"] == before["oid"], (before, failed)
        checks.append({"name": "grantor-fault-rolls-back-owner-acl-member-and-role",
                       "passed": True, "state": failed})
        role_check = ("DO $role$ BEGIN "
                      "IF current_setting('role') <> 'none' OR "
                      "coalesce(current_setting('laplace.physicality_transport_upgrade',true),'') <> '' "
                      "THEN RAISE EXCEPTION 'upgrade left role or scratch state'; END IF; END $role$;")
        sql("ALTER EXTENSION " + extension + " UPDATE TO '2';\n" + role_check, database)
        upgraded = state()
        equivalent(before, upgraded, 24, "2")
        assert upgraded["oid"] != before["oid"], (before, upgraded)
        checks.append({"name": "actual-extension-update-preserves-exact-grantors-and-options",
                       "passed": True, "state": upgraded})
        sql("ALTER EXTENSION " + extension + " UPDATE TO '3';\n" + role_check, database)
        repeated = state()
        equivalent(upgraded, repeated, 24, "3")
        assert repeated["oid"] == upgraded["oid"], (upgraded, repeated)
        checks.append({"name": "same-shape-repeat-preserves-oid-owner-acl",
                       "passed": True, "state": repeated})
        sql("DROP EXTENSION " + extension + ";\nCREATE EXTENSION " + extension + " VERSION '2';\n" + role_check,
            database)
        fresh = state()
        assert fresh["outputCount"] == 24 and fresh["extensionMembers"] == 1, fresh
        assert fresh["scratchEmpty"] and fresh["role"] == "none", fresh
        assert any(row[1] == roles["defaults"] for row in fresh["acl"]), fresh
        checks.append({"name": "fresh-create-retains-normal-default-privileges",
                       "passed": True, "state": fresh})
        receipt["status"] = "completed"
    except Exception as exc:
        receipt["error"] = str(exc)
        raise
    finally:
        cleanup_errors = []
        if database_created:
            try:
                sql("DROP DATABASE " + database + " WITH (FORCE);")
            except Exception as exc:
                cleanup_errors.append(str(exc))
        for role in reversed(created_roles):
            try:
                sql("DROP ROLE " + role + ";")
            except Exception as exc:
                cleanup_errors.append(str(exc))
        for path in reversed(owned_files):
            try:
                path.unlink()
            except Exception as exc:
                cleanup_errors.append(str(exc))
        receipt["cleanupErrors"] = cleanup_errors
        if cleanup_errors:
            receipt["status"] = "failed"
        args.receipt.parent.mkdir(parents=True, exist_ok=True)
        args.receipt.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    if receipt["status"] != "completed":
        raise RuntimeError("isolated materializer upgrade cleanup failed")
    print("MATERIALIZER_OUTPUT_UPGRADE_PASS checks=" + str(len(checks)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
