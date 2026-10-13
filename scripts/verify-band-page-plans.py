#!/usr/bin/env python3
"""Compare generic/custom plans on a populated substrate in one read-only snapshot.

Uses libpq's environment for the selected installation. The SQL comes from the
native catalog; no copied query or fixture replaces the actual admitted data.
This measures planning policy, not activation of a newly built extension.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--psql", default="psql")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--bands", type=int, nargs="+", default=[1, 2, 4, 7])
    parser.add_argument("--limits", type=int, nargs="+", default=[5])
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    catalog = (root / "engine/core/src/sql_catalog.def").read_text()
    entry = re.search(r'SQL_QUERY\("consensus\.band_edges",[^\n]+\n(.*?)\)\s*\n',
                      catalog, re.S).group(1)
    query = "".join(json.loads(value) for value in re.findall(r'"(?:\\.|[^"\\])*"', entry))
    cases = [(band, limit) for band in args.bands for limit in args.limits]
    if any(limit < 1 for _, limit in cases):
        parser.error("Page limits must be positive")
    statements = [
        "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;",
        "SET LOCAL statement_timeout='20s';",
        "SET LOCAL application_name='laplace-band-plan-verification';",
        "SELECT ops.api('relation_band_types');",
        "\\echo __IDENTITY__",
        "SELECT json_build_object('database',current_database(),'user',current_user,"
        "'address',inet_server_addr(),'version',version(),"
        "'cluster',system_identifier::text) FROM pg_control_system();",
        f"PREPARE page(bytea[],bigint,bigint) AS {query};",
    ]
    for band, limit in cases:
        statements.append(f"SELECT laplace.relation_band_types(ARRAY[{band}])::text AS types \\gset")
        for mode in ("generic", "custom"):
            call = f"EXECUTE page(:'types'::bytea[], '-9223372036854775808'::bigint, {limit})"
            statements.extend([
                f"SET LOCAL plan_cache_mode=force_{mode}_plan;",
                f"\\echo __ROWS_{band}_{limit}_{mode}__",
                call + ";",
                f"\\echo __PLAN_{band}_{limit}_{mode}__",
                "EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) " + call + ";",
            ])
    statements.extend(["\\echo __END__", "ROLLBACK;"])
    began = time.time()
    proc = subprocess.run([args.psql, "-X", "-w", "-A", "-t", "-q", "-v", "ON_ERROR_STOP=1"],
                          input="\n".join(statements), text=True, capture_output=True,
                          timeout=240, env=os.environ.copy())
    if proc.returncode:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps({"passed": False, "error": proc.stderr.strip(),
            "completed_output": proc.stdout}, indent=2) + "\n")
        markers = re.findall(r"(?m)^__(\w+)__\r?$", proc.stdout)
        raise RuntimeError(f"{markers[-1] if markers else 'setup'}: {proc.stderr.strip()}")
    parts = re.split(r"(?m)^__(\w+)__\r?$", proc.stdout)
    sections = {parts[i]: parts[i + 1].strip() for i in range(1, len(parts), 2)}
    results = []
    for band, limit in cases:
        rows = {mode: sections[f"ROWS_{band}_{limit}_{mode}"] for mode in ("generic", "custom")}
        plans = {mode: json.loads(sections[f"PLAN_{band}_{limit}_{mode}"])[0]
                 for mode in rows}
        equal = rows["generic"] == rows["custom"]
        result = {"band": band, "limit": limit, "equal": equal,
                  "row_count": len(rows["custom"].splitlines()),
                  "sha256": hashlib.sha256(rows["custom"].encode()).hexdigest(),
                  "plans": plans}
        results.append(result)
        print(json.dumps({"band": band, "limit": limit, "equal": equal,
                          **{mode + "_ms": plans[mode]["Execution Time"] for mode in rows}}))
    receipt = {"checked_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
               "identity": json.loads(sections["IDENTITY"]),
               "query_sha256": hashlib.sha256(query.encode()).hexdigest(),
               "scope": "Planning policy and ordered-row parity; candidate module not activated",
               "duration_seconds": round(time.time() - began, 3), "cases": results,
               "passed": all(result["equal"] for result in results)
                         and any(result["row_count"] for result in results)}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + "\n")
    if not receipt["passed"]:
        raise RuntimeError("Band page parity failed, or no populated band was exercised")


if __name__ == "__main__":
    main()
