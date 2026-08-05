#!/usr/bin/env python3
"""Shrink-only architecture gates for substrate-operation literals.

The baseline records the exact violations present when each gate landed. A new
path/literal/count fails, and removing a violation without shrinking the
baseline also fails. The hard-coded ceilings make an allowlist increase visible
in executable policy rather than hiding it in generated data.
"""

from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys
from typing import Iterable


ROOT = Path(__file__).resolve().parents[1]
BASELINE = ROOT / "scripts" / "isa-gate-baseline.json"
MANIFEST = ROOT / "engine" / "manifest" / "relation_types.toml"

# Measured 2026-08-02. These constants may only decrease as literals migrate to
# their canonical surfaces.
#
# ONE EXCEPTION, TAKEN DELIBERATELY AND VISIBLY (2026-08-03), which is what this
# ceiling is for — "make an allowlist increase visible in executable policy rather
# than hiding it in generated data."
#
# g3_sql 243 -> 250. The chess read surface gained four functions: the first join
# from openings to games, and the first read of the syzygy lane at all (it had
# written HAS_WDL/HAS_DTZ since campaign PR-8 with no way to read them). A read
# function must NAME the relation it reads; there is no non-literal surface for
# vocabulary in SQL, so every new one costs literals. The prior instance of this
# choice (51843f46) refused the raise and parameterized instead — correct there,
# because the relation was incidental to an audit. It is not incidental here: the
# relation IS the subject of the query, and a chess_syzygy_line that takes HAS_WDL
# as an argument is a worse function.
#
# The cost was minimized first, not after the fact — 22 new sites reduced to 7:
#   -4  C#: named constants on ChessSeedManifest, which already owned the literals
#   -5  chess_opening_games returns ids; chess_game(event) already reads the headers
#   -2  chess_opening_endgames composes its two neighbours and names nothing
#   -2  chess_syzygy_line binds both relation ids once in a CTE (also stops a
#       STABLE function running per row)
# The remaining 7 are one name per relation per function, which is the floor.
CEILINGS = {
    "g1_weight_literalism": 25,
    "g3_sql_vocabulary_literalism": 250,
    "g3_c_vocabulary_literalism": 17,
    "g3_csharp_vocabulary_literalism": 700,
    "g8_band_literalism": 8,
    # G4 scaffolding (W6 D3): grep for CREATE FUNCTION with zero callers outside
    # its own CREATE line. Destination form is substrate CALLS in-degree after W3
    # (#765); this allowlist is shrink-only until that replace lands.
    "g4_dead_canonical": 72,
    # Measured 2026-08-05, landing with its violations enumerated per W6's trap
    # note ("a gate that goes red on merge-day teaches people to ignore it").
    # 29 occurrences across 10 sites, all pre-existing: model_factor (6 names),
    # entities_has_highway, and the three canonical_names writers.
    "g11_unqualified_in_setless_body": 29,
}

CREATE_FUNCTION = re.compile(
    r"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+(?:@extschema@\.)?([A-Za-z_][A-Za-z0-9_]*)\s*\(",
    re.IGNORECASE,
)
CALL_TOKEN = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")

G1_FORMULA = re.compile(
    r"\b(?P<rating>(?:[A-Za-z_][A-Za-z0-9_]*\.)?(?:p_)?rating)"
    r"\s*-\s*2(?:\.0)?\s*\*\s*"
    r"(?P<rd>(?:[A-Za-z_][A-Za-z0-9_]*\.)?(?:p_)?rd)\b",
    re.IGNORECASE,
)
G3_SQL_LITERAL = re.compile(
    r"\brelation_type_id\s*\(\s*'(?P<literal>[A-Z][A-Z0-9_]*)'\s*\)",
    re.IGNORECASE,
)
G3_C_LITERAL = re.compile(
    r'\brel_type_id\s*\(\s*"(?P<literal>[A-Z][A-Z0-9_]*)"\s*\)',
)
CSHARP_STRING_LITERAL = re.compile(r'"(?P<literal>[A-Z][A-Z0-9_]*)"')
G8_BAND_LITERAL = re.compile(
    r"\brelation_highway_band\s*\([^)]*\)\s*"
    r"(?:=\s*\d+|IN\s*\(\s*\d+(?:\s*,\s*\d+)*\s*\))",
    re.IGNORECASE,
)

CREATE_RELATION = re.compile(
    r"CREATE\s+(?:OR\s+REPLACE\s+)?(?:TABLE|VIEW|MATERIALIZED\s+VIEW)\s+"
    r"(?:IF\s+NOT\s+EXISTS\s+)?(?:@extschema@\.)?([A-Za-z_][A-Za-z0-9_]*)",
    re.IGNORECASE,
)
DROP_FUNCTION = re.compile(
    r"DROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?(?:@extschema@\.)?([A-Za-z_][A-Za-z0-9_]*)",
    re.IGNORECASE,
)
SET_SEARCH_PATH = re.compile(r"\bSET\s+search_path\b", re.IGNORECASE)
# A substrate name used as a call or as a FROM/JOIN target, with no qualifier in
# front of it. `(?<![.\w@])` rejects `@extschema@.x`, `a.x` and `xy` alike.
UNQUALIFIED_REF = re.compile(
    r"(?<![.\w@])(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\("
    r"|(?:\bFROM|\bJOIN)\s+(?![@\w]*\.)(?P<t>[A-Za-z_][A-Za-z0-9_]*)",
    re.IGNORECASE,
)

G1_EXEMPT_FILES = {
    "engine/core/src/glicko2.c",
    "extension/laplace_substrate/sql/functions/mu/eff_mu.sql.in",
}
G1_EXEMPT_PREFIXES = (
    "extension/laplace_substrate/sql/indexes/",
)


def relative(path: Path) -> str:
    return path.relative_to(ROOT).as_posix()


def production_files(root: Path, suffixes: tuple[str, ...]) -> Iterable[Path]:
    if not root.is_dir():
        return
    for path in sorted(root.rglob("*")):
        if not path.is_file() or not path.name.endswith(suffixes):
            continue
        if any(part in {"bin", "obj", "node_modules", ".git"} for part in path.parts):
            continue
        yield path


def strip_c_comments(text: str) -> str:
    """Remove // and /* */ comments while preserving strings and newlines."""
    out: list[str] = []
    i = 0
    state = "code"
    while i < len(text):
        char = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if state == "code":
            if char == "/" and nxt == "/":
                out.extend((" ", " "))
                i += 2
                state = "line"
                continue
            if char == "/" and nxt == "*":
                out.extend((" ", " "))
                i += 2
                state = "block"
                continue
            if char == '"':
                state = "string"
            elif char == "'":
                state = "char"
            out.append(char)
        elif state == "line":
            if char in "\r\n":
                out.append(char)
                state = "code"
            else:
                out.append(" ")
        elif state == "block":
            if char == "*" and nxt == "/":
                out.extend((" ", " "))
                i += 2
                state = "code"
                continue
            out.append(char if char in "\r\n" else " ")
        else:
            out.append(char)
            if char == "\\" and nxt:
                out.append(nxt)
                i += 2
                continue
            if state == "string" and char == '"':
                state = "code"
            elif state == "char" and char == "'":
                state = "code"
        i += 1
    return "".join(out)


def strip_sql_comments(text: str) -> str:
    """Remove SQL comments while preserving quoted relation-name literals."""
    out: list[str] = []
    i = 0
    state = "code"
    while i < len(text):
        char = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if state == "code":
            if char == "-" and nxt == "-":
                out.extend((" ", " "))
                i += 2
                state = "line"
                continue
            if char == "/" and nxt == "*":
                out.extend((" ", " "))
                i += 2
                state = "block"
                continue
            if char == "'":
                state = "string"
            out.append(char)
        elif state == "line":
            if char in "\r\n":
                out.append(char)
                state = "code"
            else:
                out.append(" ")
        elif state == "block":
            if char == "*" and nxt == "/":
                out.extend((" ", " "))
                i += 2
                state = "code"
                continue
            out.append(char if char in "\r\n" else " ")
        else:
            out.append(char)
            if char == "'" and nxt == "'":
                out.append(nxt)
                i += 2
                continue
            if char == "'":
                state = "code"
        i += 1
    return "".join(out)


def governed_relation_names() -> set[str]:
    text = MANIFEST.read_text(encoding="utf-8")
    return set(
        re.findall(
            r'^(?:canonical|surface)\s*=\s*"([A-Z][A-Z0-9_]*)"\s*$',
            text,
            re.MULTILINE,
        )
    )


def add_matches(
    counter: Counter[str],
    path: Path,
    text: str,
    pattern: re.Pattern[str],
    token,
) -> None:
    rel = relative(path)
    for match in pattern.finditer(text):
        counter[f"{rel}::{token(match)}"] += 1


def scan_g1() -> Counter[str]:
    found: Counter[str] = Counter()
    roots = [
        (ROOT / "extension" / "laplace_substrate" / "sql", (".sql.in",)),
        (ROOT / "extension" / "laplace_substrate" / "src", (".c", ".h")),
        (ROOT / "scripts" / "sql", (".sql",)),
    ]
    for source_root in sorted((ROOT / "engine").glob("*/src")):
        roots.append((source_root, (".c", ".cpp", ".h", ".hpp")))
    for include_root in sorted((ROOT / "engine").glob("*/include")):
        roots.append((include_root, (".h", ".hpp")))
    for root, suffixes in roots:
        for path in production_files(root, suffixes):
            rel = relative(path)
            if rel in G1_EXEMPT_FILES or rel.startswith(G1_EXEMPT_PREFIXES):
                continue
            raw = path.read_text(encoding="utf-8", errors="replace")
            text = strip_sql_comments(raw) if path.name.endswith((".sql", ".sql.in")) else strip_c_comments(raw)
            add_matches(
                found,
                path,
                text,
                G1_FORMULA,
                lambda match: re.sub(r"\s+", "", match.group(0).lower()),
            )
    return found


def scan_g3_sql() -> Counter[str]:
    found: Counter[str] = Counter()
    sql_root = ROOT / "extension" / "laplace_substrate" / "sql"
    for path in production_files(sql_root, (".sql.in",)):
        text = strip_sql_comments(path.read_text(encoding="utf-8", errors="replace"))
        add_matches(
            found,
            path,
            text,
            G3_SQL_LITERAL,
            lambda match: match.group("literal").upper(),
        )
    return found


def scan_g3_c() -> Counter[str]:
    found: Counter[str] = Counter()
    c_root = ROOT / "extension" / "laplace_substrate" / "src"
    for path in production_files(c_root, (".c", ".h")):
        text = strip_c_comments(path.read_text(encoding="utf-8", errors="replace"))
        add_matches(
            found,
            path,
            text,
            G3_C_LITERAL,
            lambda match: match.group("literal"),
        )
    return found


def scan_g3_csharp() -> Counter[str]:
    found: Counter[str] = Counter()
    governed = governed_relation_names()
    app_root = ROOT / "app"
    for path in production_files(app_root, (".cs",)):
        if any(".Tests" in part for part in path.parts):
            continue
        text = strip_c_comments(path.read_text(encoding="utf-8", errors="replace"))
        rel = relative(path)
        for match in CSHARP_STRING_LITERAL.finditer(text):
            literal = match.group("literal")
            if literal in governed:
                found[f"{rel}::{literal}"] += 1
    return found


def scan_g8() -> Counter[str]:
    found: Counter[str] = Counter()
    functions_root = ROOT / "extension" / "laplace_substrate" / "sql" / "functions"
    for path in production_files(functions_root, (".sql.in",)):
        text = strip_sql_comments(path.read_text(encoding="utf-8", errors="replace"))
        add_matches(
            found,
            path,
            text,
            G8_BAND_LITERAL,
            lambda match: re.sub(r"\s+", "", match.group(0).upper()),
        )
    return found


def scan_g11_unqualified_in_setless_body() -> Counter[str]:
    """A body without SET search_path must qualify every substrate reference.

    Removing ``SET search_path`` is what makes a SQL function inlinable —
    ``inline_set_returning_function`` refuses when ``proconfig IS NOT NULL``
    (clauses.c:5168), and the scalar inliner refuses on any SET clause. But the
    removal is only correct if EVERY substrate name in the body carries the
    ``@extschema@.`` prefix, and a miss is caught by nothing else in the
    pipeline:

      * the build never parses SQL function bodies — they are strings;
      * ``CREATE FUNCTION`` does parse-check under ``check_function_bodies``,
        but during extension install the extension schema is ON the
        search_path, so an unqualified name resolves cleanly right there;
      * it fails only at RUNTIME, for a caller whose search_path excludes the
        extension schema.

    Grounded: 22c3d98b removed SET from ``salient_facts`` and qualified only its
    first CTE, leaving eleven bare references. It built clean, installed clean,
    and merged. Found afterwards by hand; this gate is that inspection made
    mechanical.

    Only names the substrate actually defines are considered, so CTE aliases and
    column names cannot false-positive unless they shadow a real object.
    """
    sql_root = ROOT / "extension" / "laplace_substrate" / "sql"
    functions_root = sql_root / "functions"

    defined: set[str] = set()
    for path in production_files(functions_root, (".sql.in",)):
        text = strip_sql_comments(path.read_text(encoding="utf-8", errors="replace"))
        for match in CREATE_FUNCTION.finditer(text):
            defined.add(match.group(1).lower())
    for path in production_files(sql_root, (".sql.in",)):
        text = strip_sql_comments(path.read_text(encoding="utf-8", errors="replace"))
        for match in CREATE_RELATION.finditer(text):
            defined.add(match.group(1).lower())

    found: Counter[str] = Counter()
    for path in production_files(functions_root, (".sql.in",)):
        raw = path.read_text(encoding="utf-8", errors="replace")
        text = strip_sql_comments(raw)
        if SET_SEARCH_PATH.search(text):
            continue                      # still gated by SET; not this gate's business
        rel = relative(path)
        own = {m.group(1).lower() for m in CREATE_FUNCTION.finditer(text)}
        own |= {m.group(1).lower() for m in DROP_FUNCTION.finditer(text)}

        for match in UNQUALIFIED_REF.finditer(text):
            raw_name = match.group("name") or match.group("t")
            if raw_name is None:
                continue
            name = raw_name.lower()
            if name not in defined or name in own:
                continue
            found[f"{rel}::{name}"] += 1
    return found


def scan_g4_dead_canonical() -> Counter[str]:
    """Scaffolding for ISA G4 — zero-caller installed functions (W6 D3).

    A function is dead when its only mention is its own CREATE OR REPLACE
    FUNCTION line after comment strip. Call sites in SQL / extension C /
    production C# count. MCP ``op`` / HTTP ``/v1/op`` are not textual callers
    — the destination gate is substrate CALLS after W3 (#765).
    """
    functions_root = ROOT / "extension" / "laplace_substrate" / "sql" / "functions"
    defined: dict[str, str] = {}
    corpus: list[tuple[str, str]] = []

    for path in production_files(functions_root, (".sql.in",)):
        rel = relative(path)
        text = strip_sql_comments(path.read_text(encoding="utf-8", errors="replace"))
        corpus.append((rel, text))
        for match in CREATE_FUNCTION.finditer(text):
            defined[match.group(1).lower()] = rel

    for path in production_files(ROOT / "extension" / "laplace_substrate" / "src", (".c", ".h")):
        corpus.append(
            (relative(path), strip_c_comments(path.read_text(encoding="utf-8", errors="replace")))
        )
    for path in production_files(ROOT / "app", (".cs",)):
        if any(".Tests" in part for part in path.parts):
            continue
        corpus.append(
            (relative(path), strip_c_comments(path.read_text(encoding="utf-8", errors="replace")))
        )

    calls: Counter[str] = Counter()
    for rel, text in corpus:
        for match in CALL_TOKEN.finditer(text):
            name = match.group(1).lower()
            if name not in defined:
                continue
            if rel == defined[name]:
                window = text[max(0, match.start() - 50) : match.start()].lower()
                if "function" in window and "create" in window:
                    continue
            calls[name] += 1

    found: Counter[str] = Counter()
    for name, def_path in defined.items():
        if calls[name] == 0:
            found[f"{def_path}::{name}"] = 1
    return found


def current_violations() -> dict[str, dict[str, int]]:
    scans = {
        "g1_weight_literalism": scan_g1(),
        "g3_sql_vocabulary_literalism": scan_g3_sql(),
        "g3_c_vocabulary_literalism": scan_g3_c(),
        "g3_csharp_vocabulary_literalism": scan_g3_csharp(),
        "g11_unqualified_in_setless_body": scan_g11_unqualified_in_setless_body(),
        "g8_band_literalism": scan_g8(),
        "g4_dead_canonical": scan_g4_dead_canonical(),
    }
    return {
        gate: dict(sorted(counter.items()))
        for gate, counter in scans.items()
    }


def baseline_document(violations: dict[str, dict[str, int]]) -> dict:
    return {
        "measured": "2026-08-03",
        "law": "W6 G1/G3/G4(scaffolding)/G8; entries and ceilings may only shrink",
        "violations": violations,
    }


def compare(
    actual: dict[str, dict[str, int]],
    allowed: dict[str, dict[str, int]],
) -> list[str]:
    errors: list[str] = []
    for gate, ceiling in CEILINGS.items():
        current = Counter(actual.get(gate, {}))
        baseline = Counter(allowed.get(gate, {}))
        total = sum(baseline.values())
        if total > ceiling:
            errors.append(
                f"{gate}: baseline has {total} violations; shrink-only ceiling is {ceiling}"
            )

        newcomers = current - baseline
        stale = baseline - current
        for key, count in sorted(newcomers.items()):
            errors.append(f"{gate}: new violation x{count}: {key}")
        for key, count in sorted(stale.items()):
            errors.append(
                f"{gate}: stale baseline x{count}: {key} "
                "(remove it and lower the ceiling when the code is clean)"
            )
    return errors


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--print-current",
        action="store_true",
        help="print the measured baseline document without changing files",
    )
    args = parser.parse_args()

    if not MANIFEST.is_file():
        print(f"ERROR: missing relation manifest: {MANIFEST}", file=sys.stderr)
        return 2
    if not args.print_current and not BASELINE.is_file():
        print(f"ERROR: missing ISA gate baseline: {BASELINE}", file=sys.stderr)
        return 2
    try:
        actual = current_violations()
    except OSError as exc:
        print(f"ERROR: ISA gate scan failed: {exc}", file=sys.stderr)
        return 2

    if args.print_current:
        print(json.dumps(baseline_document(actual), indent=2, sort_keys=True))
        return 0

    try:
        document = json.loads(BASELINE.read_text(encoding="utf-8"))
        allowed = document["violations"]
    except (OSError, json.JSONDecodeError, KeyError, TypeError) as exc:
        print(f"ERROR: invalid ISA gate baseline: {exc}", file=sys.stderr)
        return 2

    errors = compare(actual, allowed)
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    totals = ", ".join(
        f"{gate}={sum(entries.values())}"
        for gate, entries in actual.items()
    )
    print(f"ISA literalism gates green ({totals})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
