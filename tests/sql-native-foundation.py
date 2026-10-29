#!/usr/bin/env python3
"""Exercise installed SQL/native owners in an owned database, then remove it.

Runs the real SQL fixtures with ON_ERROR_STOP, independently of pg_regress's
presentation snapshots. Uses libpq's declared host identity and TLS environment.
"""
from pathlib import Path
import argparse
import json
import os
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
CASES = ("bootstrap", "render_depth", "prompt_segments", "native_consensus_endpoints", "session_identity", "generation_corpus", "chess_read",
         "chess_fold_debt_repair", "highway_mask_deposit", "highway_mask_recovery")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pgbin", type=Path, required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--source-database", default=os.environ.get("PGDATABASE", "postgres"))
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=True)
    database = "laplace_sql_native_" + uuid.uuid4().hex[:16]
    suffix = ".exe" if os.name == "nt" else ""
    receipt = {"database": database, "source": subprocess.check_output(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(), "cases": []}
    created = False

    def run(tool, options, log, env=None, sql=None):
        with log.open("w", encoding="utf-8") as output:
            return subprocess.run([str(args.pgbin / (tool + suffix)), *options],
                cwd=ROOT, env=env, input=sql, text=True,
                stdout=output, stderr=subprocess.STDOUT, timeout=600).returncode

    try:
        if run("createdb", ["--no-password", "--maintenance-db=postgres", database],
               args.results / "create.log"):
            raise RuntimeError("Could not create the isolated SQL verification database")
        created = True
        # Installed extension/cache paths may be database-scoped on Linux.
        # Carry their actual values into this database without changing schema
        # resolution, planner switches, roles, or the installed source database.
        configure = """WITH installed AS (
 SELECT name,setting,3 AS priority FROM pg_settings
 UNION ALL
 SELECT split_part(value,'=',1),substr(value,strpos(value,'=')+1),
        CASE WHEN s.setrole=0 THEN 1 ELSE 2 END
 FROM pg_db_role_setting s CROSS JOIN LATERAL unnest(s.setconfig) value
 WHERE s.setdatabase=(SELECT oid FROM pg_database WHERE datname=current_database())
   AND s.setrole IN (0,(SELECT oid FROM pg_roles WHERE rolname=current_user))
), selected AS (
 SELECT DISTINCT ON (name) name,setting FROM installed
 WHERE name IN ('extension_control_path','dynamic_library_path') OR name LIKE 'laplace.%'
 ORDER BY name,priority DESC
)
SELECT format('ALTER DATABASE %I SET %I TO %L', :'target',name,setting) FROM selected
\\gexec
"""
        if run("psql", ["-X", "--no-password", "--dbname=" + args.source_database,
                "--set=ON_ERROR_STOP=1", "--set=target=" + database],
                args.results / "configuration.log", sql=configure):
            raise RuntimeError("Could not carry installed extension/cache configuration into the verification database")
        for name in CASES:
            started = time.monotonic()
            sql = ROOT / "extension/laplace_substrate/tests/sql" / (name + ".sql")
            result = run("psql", ["-X", "--no-password", "--dbname=" + database,
                "--set=ON_ERROR_STOP=1", "--file=" + str(sql)], args.results / (name + ".log"))
            receipt["cases"].append({"name": name, "exit_code": result,
                "elapsed_ms": round((time.monotonic() - started) * 1000)})
            print(f"{name}: {'passed' if result == 0 else 'FAILED'}", flush=True)
            if name == "bootstrap" and result:
                break
        if receipt["cases"][0]["exit_code"] == 0:
            native = Path(os.environ.get("LAPLACE_POSTGRES_SOURCE", ROOT.parent / "Laplace-postgres"))
            started = time.monotonic()
            with (args.results / "native_load_lifecycle.log").open("w", encoding="utf-8") as output:
                result = subprocess.run([sys.executable, str(native / "tests/stage-lifecycle.py"),
                    "--psql", str(args.pgbin / ("psql" + suffix)), "--connection", "dbname=" + database],
                    cwd=ROOT, text=True, stdout=output, stderr=subprocess.STDOUT, timeout=120).returncode
            receipt["cases"].append({"name": "native_load_lifecycle", "exit_code": result,
                "elapsed_ms": round((time.monotonic() - started) * 1000)})
            print(f"native_load_lifecycle: {'passed' if result == 0 else 'FAILED'}", flush=True)
            grants = ROOT / "db/migrations/20261015000000_substrate_runtime_grants.sql"
            options = ["-X", "--no-password", "--dbname=" + database, "--set=ON_ERROR_STOP=1"]
            result = run("psql", [*options, "--file=" + str(grants)], args.results / "runtime_grants.log")
            receipt["cases"].append({"name": "runtime_grants", "exit_code": result})
            # A satisfied ACL must reconcile without locking the target table.
            # This holder and the database both belong exclusively to this run.
            if result == 0:
                with (args.results / "acl_holder.log").open("w", encoding="utf-8") as errors:
                    holder = subprocess.Popen([str(args.pgbin / ("psql" + suffix)), *options, "-Atq"],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors, text=True)
                    try:
                        holder.stdin.write("BEGIN; LOCK TABLE public.entity IN ACCESS EXCLUSIVE MODE; SELECT 'locked';\n")
                        holder.stdin.flush()
                        if holder.stdout.readline().strip() != "locked":
                            raise RuntimeError("ACL replay lock fixture did not acquire its table")
                        env = dict(os.environ)
                        env["PGOPTIONS"] = env.get("PGOPTIONS", "") + " -c lock_timeout=2s -c statement_timeout=10s"
                        result = run("psql", [*options, "--file=" + str(grants)],
                                     args.results / "runtime_grants_locked_replay.log", env=env)
                        receipt["cases"].append({"name": "runtime_grants_locked_replay", "exit_code": result})
                    finally:
                        try:
                            holder.communicate("ROLLBACK;\n\\q\n", timeout=10)
                        except subprocess.TimeoutExpired:
                            holder.kill()
                            holder.communicate()
        receipt["passed"] = len(receipt["cases"]) == len(CASES) + 3 and all(
            case["exit_code"] == 0 for case in receipt["cases"])
    except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
        receipt.update(passed=False, error=str(error))
        print(str(error), file=sys.stderr)
    finally:
        try:
            receipt["database_removed"] = not created or run("dropdb",
                ["--no-password", "--maintenance-db=postgres", "--force", database],
                args.results / "cleanup.log") == 0
        except (OSError, subprocess.TimeoutExpired) as error:
            receipt.update(database_removed=False, cleanup_error=str(error))
        (args.results / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return 0 if receipt.get("passed") and receipt["database_removed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
