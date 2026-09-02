#!/usr/bin/env python3
"""Upgrade drop-order gate.

For every live function-on-function dependency (pg_depend: BEGIN ATOMIC bodies
pin their callees' OIDs), if manifest.upgrade drops the exact base signature, a
drop of the exact dependent signature must appear EARLIER in concatenation
order — otherwise the installed dependent RESTRICTs the drop and ALTER EXTENSION
UPDATE fails on a live DB while passing every fresh install.

Function name alone is not enough: PostgreSQL dependencies are on pg_proc OIDs,
so overloads are independent. A retired scalar overload may be dropped while a
live dependent calls an array overload with the same schema/name. Treating those
as one function creates a false hazard and blocks an otherwise legal upgrade.
"""
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SQL_ROOT = ROOT / "extension/laplace_substrate/sql"

PAIRS_SQL = """
SELECT DISTINCT
       nd.nspname || '.' || dep.proname || '(' || pg_catalog.oidvectortypes(dep.proargtypes) || ')',
       nb.nspname || '.' || base.proname || '(' || pg_catalog.oidvectortypes(base.proargtypes) || ')'
FROM pg_depend d
JOIN pg_proc dep ON dep.oid = d.objid AND d.classid = 'pg_proc'::regclass
JOIN pg_proc base ON base.oid = d.refobjid AND d.refclassid = 'pg_proc'::regclass
JOIN pg_namespace nd ON nd.oid = dep.pronamespace
JOIN pg_namespace nb ON nb.oid = base.pronamespace
WHERE nb.nspname IN ('laplace','ops','consensus','generation','realize','converse','structural','chess')
  AND dep.oid <> base.oid
"""

# SQL source commonly uses short aliases while oidvectortypes() emits PostgreSQL's
# canonical spellings. Keep this deliberately small and explicit; new aliases are
# added only when the upgrade manifest actually uses them.
TYPE_ALIASES = {
    "int": "integer",
    "int4": "integer",
    "int8": "bigint",
    "float8": "double precision",
    "float4": "real",
    "bool": "boolean",
    "varchar": "character varying",
    "timestamptz": "timestamp with time zone",
    "timestamp": "timestamp without time zone",
}


def canonical_type(raw: str) -> str:
    value = re.sub(r"\s+", " ", raw.strip().lower())
    value = re.sub(r"^(?:in|inout|variadic)\s+", "", value)

    # DROP FUNCTION may include argument names. The generated repository uses
    # p_* for function parameters, so strip only that unambiguous form rather
    # than guessing whether a multiword SQL type contains a name.
    named = re.match(r"^p_[a-z0-9_]+\s+(.+)$", value)
    if named:
        value = named.group(1)

    suffix = ""
    while value.endswith("[]"):
        suffix += "[]"
        value = value[:-2].rstrip()
    return TYPE_ALIASES.get(value, value) + suffix


def canonical_signature(name: str, args: str) -> str:
    name = name.strip().lower()
    if not args.strip():
        return f"{name}()"
    types = [canonical_type(arg) for arg in args.split(",")]
    return f"{name}({','.join(types)})"


def canonical_live_signature(signature: str) -> str:
    match = re.fullmatch(r"\s*([a-z_][a-z_0-9]*\.[a-z_][a-z_0-9]*)\((.*)\)\s*",
                         signature, re.IGNORECASE)
    if not match:
        raise ValueError(f"unexpected live function signature: {signature!r}")
    return canonical_signature(match.group(1), match.group(2))


def live_pairs() -> list[tuple[str, str]]:
    out = subprocess.run(
        ["psql", "-h", "/var/run/postgresql", "-U", "laplace_admin",
         "-d", "laplace", "-tAc", PAIRS_SQL],
        capture_output=True, text=True)
    if out.returncode != 0:
        # No live DB (fresh checkout, dev box): nothing is pinned, nothing to gate.
        detail = out.stderr.strip().splitlines()[-1] if out.stderr else "psql failed"
        print(f"drop-order gate: no live catalog ({detail}) — skipping")
        return []

    pairs: list[tuple[str, str]] = []
    for line in out.stdout.splitlines():
        if "|" not in line:
            continue
        dep, base = line.split("|", 1)
        pairs.append((canonical_live_signature(dep), canonical_live_signature(base)))
    return pairs


def main() -> int:
    manifest = (SQL_ROOT / "manifest.upgrade").read_text().split()
    drop_re = re.compile(
        r"^\s*DROP\s+FUNCTION\s+IF\s+EXISTS\s+"
        r"([a-z_][a-z_0-9]*\.[a-z_][a-z_0-9]*)\s*\(([^)]*)\)",
        re.MULTILINE | re.IGNORECASE)
    drop_pos: dict[str, int] = {}
    offset = 0
    for entry in manifest:
        if not entry.endswith(".sql.in"):
            continue
        path = SQL_ROOT / entry
        if not path.exists():
            continue
        text = path.read_text(errors="replace")
        for match in drop_re.finditer(text):
            signature = canonical_signature(match.group(1), match.group(2))
            drop_pos.setdefault(signature, offset + match.start())
        offset += len(text)

    bad = 0
    pairs = live_pairs()
    for dep, base in pairs:
        if base not in drop_pos:
            continue
        if dep not in drop_pos or drop_pos[dep] > drop_pos[base]:
            disposition = "drops later" if dep in drop_pos else "is never dropped"
            print(f"ERROR: upgrade drops {base} while live dependent {dep} "
                  f"{disposition} — the installed dependent pins the exact base "
                  f"signature (RESTRICT); drop the dependent first")
            bad += 1

    print(f"drop-order gate: {bad} hazard(s) across {len(pairs)} live signature pair(s), "
          f"{len(drop_pos)} dropped signature(s)")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
