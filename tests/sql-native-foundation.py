#!/usr/bin/env python3
"""Exercise the installed SQL/native owners in the deployed database.

Runs the real SQL fixtures with ON_ERROR_STOP against the database delivery just migrated (PGDATABASE). Every case
works inside a transaction it rolls back; the bootstrap case only confirms the installed extensions and their module
binding. No database is created or dropped.
"""
from pathlib import Path
import argparse
import json
import os
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
CASES = ("bootstrap", "render_depth", "prompt_segments", "native_consensus_endpoints", "session_identity", "declared_paths_input", "generation_corpus",
         "highway_mask_deposit", "highway_mask_recovery")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pgbin", type=Path, required=True)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--source-database", default=os.environ.get("PGDATABASE", "postgres"))
    args = parser.parse_args()
    args.results.mkdir(parents=True, exist_ok=True)
    database = args.source_database
    suffix = ".exe" if os.name == "nt" else ""
    receipt = {"database": database, "source": subprocess.check_output(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(), "cases": []}

    def run(tool, options, log, env=None, sql=None):
        with log.open("w", encoding="utf-8") as output:
            return subprocess.run([str(args.pgbin / (tool + suffix)), *options],
                cwd=ROOT, env=env, input=sql, text=True,
                stdout=output, stderr=subprocess.STDOUT, timeout=600).returncode

    try:
        for name in CASES:
            started = time.monotonic()
            sql = ROOT / "extension/laplace_substrate/tests/sql" / (name + ".sql")
            result = run("psql", ["-X", "--no-password", "--dbname=" + database,
                "--set=ON_ERROR_STOP=1", "--file=" + str(sql)], args.results / (name + ".log"))
            receipt["cases"].append({"name": name, "exit_code": result,
                "elapsed_ms": round((time.monotonic() - started) * 1000)})
            print(f"{name}: {'passed' if result == 0 else 'FAILED'}", flush=True)
            if result:   # the runner's temp directory is gone after the job: the failing case's error is told here
                print("".join((args.results / (name + ".log")).read_text(encoding="utf-8", errors="replace")
                              .splitlines(keepends=True)[-25:]), flush=True)
            if name == "bootstrap" and result:
                break
        if receipt["cases"][0]["exit_code"] == 0:
            native = Path(os.environ.get("LAPLACE_POSTGRES_SOURCE", ROOT.parent / "Laplace-postgres"))
            started = time.monotonic()
            # Laplace-postgres's lifecycle test, against the deployed database and its prepared stage.
            stage = run("psql", ["-X", "--no-password", "--dbname=" + database, "--set=ON_ERROR_STOP=1",
                                 "--command=SELECT public.laplace_ensure_stage();"], args.results / "stage.log")
            with (args.results / "native_load_lifecycle.log").open("w", encoding="utf-8") as output:
                result = subprocess.run([sys.executable, str(native / "tests/stage-lifecycle.py"),
                    "--psql", str(args.pgbin / ("psql" + suffix)), "--connection", "dbname=" + database],
                    cwd=ROOT, text=True, stdout=output, stderr=subprocess.STDOUT, timeout=120).returncode if stage == 0 else stage
            receipt["cases"].append({"name": "native_load_lifecycle", "exit_code": result,
                "elapsed_ms": round((time.monotonic() - started) * 1000)})
            print(f"native_load_lifecycle: {'passed' if result == 0 else 'FAILED'}", flush=True)
            if result:
                failed = args.results / ("stage.log" if stage else "native_load_lifecycle.log")
                print("".join(failed.read_text(encoding="utf-8", errors="replace").splitlines(keepends=True)[-25:]), flush=True)
            grants = ROOT / "db/schema/20_substrate_grants.sql"
            options = ["-X", "--no-password", "--dbname=" + database, "--set=ON_ERROR_STOP=1"]
            result = run("psql", [*options, "--file=" + str(grants)], args.results / "runtime_grants.log")
            receipt["cases"].append({"name": "runtime_grants", "exit_code": result})
            # A satisfied ACL must reconcile without locking the target table.
            # The holder rolls back; the declaration it replays is the one every delivery applies.
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
        (args.results / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return 0 if receipt.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
