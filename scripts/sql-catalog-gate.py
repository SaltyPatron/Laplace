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
# Comments and character literals must be consumed before strings, so their
# quotes cannot start a fictitious string across later source code. A character
# literal cannot start at a C++ numeric separator (for example, 1'000).
TOKEN = re.compile(
    r'//[^\n]*|/\*[\s\S]*?\*/|"""[\s\S]*?"""|@"(?:""|[^"])*"'
    r"|(?P<char>(?<![\w])(?:u8|[uUL])?'(?:\\.|[^'\\\r\n])+')"
    r'|"(?:\\.|[^"\\])*"')
SQL = re.compile(r'\b(?:SELECT\s|INSERT\s+INTO\s|UPDATE\s+[\w.]+\s+SET\s|DELETE\s+FROM\s|WITH\s+[\w]+\s+AS\s*\(|COPY\s+[\w.(]|CREATE\s+(?:TEMP\s+)?(?:TABLE|FUNCTION|INDEX)|ALTER\s+TABLE|DROP\s+(?:TABLE|FUNCTION))', re.I)


def statements(source):
    """Join adjacent C strings; retain C# raw/verbatim/interpolated SQL text."""
    current, end = [], -1
    for token in TOKEN.finditer(source):
        value = token.group()
        if value.startswith(("//", "/*")):
            continue
        if token.lastgroup == "char":
            # Unlike a comment, a character expression ends a string sequence.
            joined = "".join(current)
            if current and SQL.search(joined):
                yield re.sub(r'\s+', ' ', joined).strip()
            current, end = [], token.end()
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


def catalog_entries(source):
    """Parse the complete SQL_QUERY-only file; comments cannot hide an entry.

    Strip comments through the existing literal-aware tokenizer, so comment-like
    text and parentheses inside SQL strings keep their original meaning. Match
    each macro's own adjacent C string literals rather than looking ahead for the
    next macro; reject any unparsed syntax instead of silently skipping it.
    """
    source = TOKEN.sub(lambda token: " " * len(token.group())
                       if token.group().startswith(("//", "/*")) else token.group(), source)
    string = r'"(?:\\.|[^"\\])*"'
    entry = re.compile(r'\s*SQL_QUERY\s*\(\s*(' + string + r')\s*,\s*('
                       + string + r')\s*,\s*((?:' + string + r'\s*)+)\)')
    entries, offset = [], 0
    while source[offset:].strip():
        match = entry.match(source, offset)
        if match is None:
            raise ValueError(f"native SQL catalog has invalid macro syntax at offset {offset}")
        key, types, literal = match.groups()
        sql = "".join(token.group()[1:-1] for token in TOKEN.finditer(literal))
        entries.append((key[1:-1], types[1:-1], sql))
        offset = match.end()
    return entries


def catalog_errors(entries):
    errors, keys = [], set()
    for key, types, sql in entries:
        if not key:
            errors.append("native SQL query key cannot be empty")
        if key in keys:
            errors.append(f"duplicate native query key: {key}")
        keys.add(key)
        bound = {int(n) for n in re.findall(r'\$(\d+)', sql)}
        arity = len(types.split(',')) if types else 0
        if bound != set(range(1, arity + 1)):
            errors.append(f"{key}: positional parameters disagree with declared types")
        if re.search(r'\b(?:WITH\s+RECURSIVE|LATERAL|EXECUTE|generate_series)\b', sql, re.I):
            errors.append(f"{key}: computation must use the canonical native operation")
    if not entries:
        errors.append("native SQL catalog is empty or invalid")
    return errors


def main():
    allowed = json.loads(BASELINE.read_text())["files"]
    errors, debt = [], 0
    for path in runtime_paths():
        rel = path.relative_to(ROOT).as_posix()
        actual = fingerprints(path.read_text())
        debt += sum(actual.values())
        for digest, count in excess(actual, allowed.get(rel, {})).items():
            errors.append(f"{rel}: {count} new/changed inline SQL statement(s), {digest[:12]}; use the native catalog")
    entries = []
    try:
        entries = catalog_entries(CATALOG.read_text())
        errors.extend(catalog_errors(entries))
    except ValueError as error:
        errors.append(str(error))
    if errors:
        print('\n'.join(errors), file=sys.stderr)
        return 1
    print(f"SQL_CATALOG_OK queries={len(entries)} legacy_runtime_literals={debt}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
