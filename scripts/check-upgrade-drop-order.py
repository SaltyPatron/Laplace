#!/usr/bin/env python3
"""Upgrade drop-order gate.

For every live function-on-function dependency (pg_depend: BEGIN ATOMIC bodies
pin their callees' OIDs), if manifest.upgrade drops the base, a drop of the
dependent must appear EARLIER in concatenation order — otherwise the installed
dependent RESTRICTs the drop and ALTER EXTENSION UPDATE fails on a live DB
while passing every fresh install (metric_ladder d1fa0245; circuit_coord
2026-08-14). Fresh installs never catch this; only the live catalog knows the
pins, so this gate asks it."""
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SQL_ROOT = ROOT / "extension/laplace_substrate/sql"

PAIRS_SQL = """
SELECT DISTINCT nd.nspname || '.' || dep.proname, nb.nspname || '.' || base.proname
FROM pg_depend d
JOIN pg_proc dep ON dep.oid = d.objid AND d.classid = 'pg_proc'::regclass
JOIN pg_proc base ON base.oid = d.refobjid AND d.refclassid = 'pg_proc'::regclass
JOIN pg_namespace nd ON nd.oid = dep.pronamespace
JOIN pg_namespace nb ON nb.oid = base.pronamespace
WHERE nb.nspname IN ('laplace','ops','consensus','generation','realize','converse','structural','chess')
  AND dep.oid <> base.oid
"""


def live_pairs() -> list[tuple[str, str]]:
    out = subprocess.run(
        ["psql", "-h", "/var/run/postgresql", "-U", "laplace_admin",
         "-d", "laplace", "-tAc", PAIRS_SQL],
        capture_output=True, text=True)
    if out.returncode != 0:
        # No live DB (fresh checkout, dev box): nothing is pinned, nothing to gate.
        print(f"drop-order gate: no live catalog ({out.stderr.strip().splitlines()[-1] if out.stderr else 'psql failed'}) — skipping")
        return []
    return [tuple(l.split("|")) for l in out.stdout.splitlines() if "|" in l]


def main() -> int:
    manifest = (SQL_ROOT / "manifest.upgrade").read_text().split()
    drop_re = re.compile(r"^DROP FUNCTION IF EXISTS\s+([a-z_]+\.[a-z_0-9]+)\s*\(", re.M)
    drop_pos: dict[str, int] = {}
    offset = 0
    for name in manifest:
        if not name.endswith(".sql.in"):
            continue
        path = SQL_ROOT / name
        if not path.exists():
            continue
        text = path.read_text(errors="replace")
        for m in drop_re.finditer(text):
            drop_pos.setdefault(m.group(1), offset + m.start())
        offset += len(text)

    bad = 0
    pairs = live_pairs()
    for dep, base in pairs:
        if base not in drop_pos:
            continue
        if dep not in drop_pos or drop_pos[dep] > drop_pos[base]:
            print(f"ERROR: upgrade drops {base} while live dependent {dep} "
                  f"{'drops later' if dep in drop_pos else 'is never dropped'} — "
                  f"the installed dependent pins the base (RESTRICT); drop the dependent first")
            bad += 1
    print(f"drop-order gate: {bad} hazard(s) across {len(pairs)} live pair(s), "
          f"{len(drop_pos)} dropped function(s)")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
