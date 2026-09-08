#!/usr/bin/env python3
"""Ratchet hand-written runtime SQL toward the native typed catalog.

Existing statement fingerprints are migration debt, not approved new templates.
Changing or duplicating a legacy statement requires moving it into the catalog.
Test fixtures, migration DDL, and generated ORM SQL are outside runtime literals.
"""
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
BASELINE = ROOT / "scripts/sql-catalog-baseline.json"
CATALOG = ROOT / "engine/core/src/sql_catalog.def"
# Comments must be consumed before strings so examples in comments are not code.
TOKEN = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|"""[\s\S]*?"""|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"')
SQL = re.compile(r'\b(?:SELECT\s|INSERT\s+INTO\s|UPDATE\s+[\w.]+\s+SET\s|DELETE\s+FROM\s|WITH\s+[\w]+\s+AS\s*\(|COPY\s+[\w.(]|CREATE\s+(?:TEMP\s+)?(?:TABLE|FUNCTION|INDEX)|ALTER\s+TABLE|DROP\s+(?:TABLE|FUNCTION))', re.I)


def statements(source):
    """Join adjacent C strings; retain C# raw/verbatim/interpolated SQL text."""
    current, end = [], -1
    for token in TOKEN.finditer(source):
        value = token.group()
        if value.startswith(("//", "/*")):
            continue
        between = source[end:token.start()] if end >= 0 else ""
        between = re.sub(r'/\*[\s\S]*?\*/|//[^\n]*', '', between).strip()
        if between and current:
            joined = "".join(current)
            if SQL.search(joined):
                yield re.sub(r'\s+', ' ', joined).strip()
            current = []
        if value.startswith('"""'):
            current.append(value[3:-3])
        elif value.startswith('@"'):
            current.append(value[2:-1].replace('""', '"'))
        else:
            current.append(value[1:-1])
        end = token.end()
    if current and SQL.search("".join(current)):
        yield re.sub(r'\s+', ' ', "".join(current)).strip()


def fingerprints(source):
    return Counter(hashlib.sha256(s.encode()).hexdigest() for s in statements(source))


def runtime_paths(root=ROOT):
    for base in ("app", "engine", "extension"):
        for path in (root / base).rglob("*"):
            if path.suffix not in (".cs", ".c", ".h", ".cpp"):
                continue
            rel = path.relative_to(root)
            if any(p in ("bin", "obj", "tests", "test", "Migrations") or p.endswith(".Tests") for p in rel.parts):
                continue
            yield path


def excess(current, allowed):
    return current - Counter(allowed)


def main():
    allowed = json.loads(BASELINE.read_text())["files"]
    errors, debt = [], 0
    for path in runtime_paths():
        rel = path.relative_to(ROOT).as_posix()
        actual = fingerprints(path.read_text())
        debt += sum(actual.values())
        for digest, count in excess(actual, allowed.get(rel, {})).items():
            errors.append(f"{rel}: {count} new/changed inline SQL statement(s), {digest[:12]}; use the native catalog")
    catalog = CATALOG.read_text()
    entries = re.findall(r'SQL_QUERY\("([^"]+)",\s*"([^"]*)",([\s\S]*?)\)\s*(?=SQL_QUERY|$)', catalog)
    keys = set()
    for key, types, literal in entries:
        if key in keys:
            errors.append(f"duplicate native query key: {key}")
        keys.add(key)
        sql = "".join(t.group()[1:-1] for t in TOKEN.finditer(literal) if t.group().startswith('"'))
        bound = {int(n) for n in re.findall(r'\$(\d+)', sql)}
        arity = len(types.split(',')) if types else 0
        if bound != set(range(1, arity + 1)):
            errors.append(f"{key}: positional parameters disagree with declared types")
        if re.search(r'\b(?:WITH\s+RECURSIVE|LATERAL|EXECUTE|generate_series)\b', sql, re.I):
            errors.append(f"{key}: computation must use the canonical native operation")
    if not entries:
        errors.append("native SQL catalog is empty or invalid")
    if errors:
        print('\n'.join(errors), file=sys.stderr)
        return 1
    print(f"SQL_CATALOG_OK queries={len(keys)} legacy_runtime_literals={debt}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
