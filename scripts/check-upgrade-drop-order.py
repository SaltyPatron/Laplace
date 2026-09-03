#!/usr/bin/env python3
"""Upgrade dependency-order gate.

BEGIN ATOMIC SQL bodies record pg_depend on called pg_proc OIDs. When an upgrade
retires an exact base signature, the installed dependent must release that OID
before the base DROP or PostgreSQL RESTRICTs the ALTER EXTENSION UPDATE.

A dependency can be released in two legitimate ways before the base drop:

* DROP the exact dependent signature; or
* CREATE OR REPLACE the exact dependent signature with a BEGIN ATOMIC body that
  no longer references the base. PostgreSQL replaces the function's dependency
  records when it replaces the parsed SQL body.

The previous gate modeled only the first form. That produced a false hazard for a
safe in-place rebind and encouraged needless drop/recreate cascades. This gate now
models the ordered state of each live dependent up to the first base DROP.

Function name alone is never enough: pg_depend is on pg_proc OIDs, so overloads
remain independent throughout this check.
"""
from __future__ import annotations

from collections import defaultdict
import pathlib
import re
import subprocess
import sys
from typing import NamedTuple

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

DROP_RE = re.compile(
    r"^\s*DROP\s+FUNCTION\s+IF\s+EXISTS\s+"
    r"([a-z_][a-z_0-9]*\.[a-z_][a-z_0-9]*)\s*\(([^)]*)\)",
    re.MULTILINE | re.IGNORECASE,
)
CREATE_HEAD_RE = re.compile(
    r"^\s*CREATE\s+OR\s+REPLACE\s+FUNCTION\s+"
    r"([a-z_][a-z_0-9]*\.[a-z_][a-z_0-9]*)\s*\(",
    re.MULTILINE | re.IGNORECASE,
)
TOP_LEVEL_EVENT_RE = re.compile(
    r"^\s*(?:CREATE\s+OR\s+REPLACE\s+FUNCTION|DROP\s+FUNCTION\s+IF\s+EXISTS)\b",
    re.MULTILINE | re.IGNORECASE,
)


class Rebind(NamedTuple):
    position: int
    body: str


def canonical_type(raw: str) -> str:
    value = re.sub(r"\s+", " ", raw.strip().lower())
    value = re.sub(r"^(?:in|inout|variadic)\s+", "", value)

    named = re.match(r"^p_[a-z0-9_]+\s+(.+)$", value)
    if named:
        value = named.group(1)

    suffix = ""
    while value.endswith("[]"):
        suffix += "[]"
        value = value[:-2].rstrip()
    return TYPE_ALIASES.get(value, value) + suffix


def split_top_level_args(args: str) -> list[str]:
    """Split a function declaration on commas outside (), [], and SQL strings."""
    if not args.strip():
        return []
    out: list[str] = []
    start = 0
    paren = 0
    bracket = 0
    quote = False
    i = 0
    while i < len(args):
        ch = args[i]
        nxt = args[i + 1] if i + 1 < len(args) else ""
        if quote:
            if ch == "'" and nxt == "'":
                i += 2
                continue
            if ch == "'":
                quote = False
            i += 1
            continue
        if ch == "'":
            quote = True
        elif ch == "(":
            paren += 1
        elif ch == ")":
            paren -= 1
        elif ch == "[":
            bracket += 1
        elif ch == "]":
            bracket -= 1
        elif ch == "," and paren == 0 and bracket == 0:
            out.append(args[start:i])
            start = i + 1
        i += 1
    out.append(args[start:])
    return out


def strip_default(arg: str) -> str:
    """Remove a top-level DEFAULT/= expression from one declared argument."""
    # DEFAULT is the repository's normal spelling. `=` support is intentionally
    # conservative and only strips a token surrounded by whitespace.
    match = re.search(r"\s+DEFAULT\s+|\s+=\s+", arg, re.IGNORECASE)
    return arg[: match.start()] if match else arg


def canonical_signature(name: str, args: str, *, declaration: bool = False) -> str:
    name = name.strip().lower()
    parts = split_top_level_args(args)
    if declaration:
        parts = [strip_default(part) for part in parts]
    types = [canonical_type(arg) for arg in parts if arg.strip()]
    return f"{name}({','.join(types)})"


def canonical_live_signature(signature: str) -> str:
    match = re.fullmatch(
        r"\s*([a-z_][a-z_0-9]*\.[a-z_][a-z_0-9]*)\((.*)\)\s*",
        signature,
        re.IGNORECASE,
    )
    if not match:
        raise ValueError(f"unexpected live function signature: {signature!r}")
    return canonical_signature(match.group(1), match.group(2))


def find_decl_close(text: str, open_pos: int) -> int:
    """Return the ')' closing a CREATE FUNCTION declaration argument list."""
    depth = 0
    quote = False
    i = open_pos
    while i < len(text):
        ch = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if quote:
            if ch == "'" and nxt == "'":
                i += 2
                continue
            if ch == "'":
                quote = False
            i += 1
            continue
        if ch == "'":
            quote = True
        elif ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    raise ValueError("unterminated CREATE FUNCTION argument list")


def mask_comments(text: str) -> str:
    """Blank SQL comments while preserving positions/newlines."""
    chars = list(text)
    i = 0
    state = "code"
    while i < len(chars):
        ch = chars[i]
        nxt = chars[i + 1] if i + 1 < len(chars) else ""
        if state == "code":
            if ch == "-" and nxt == "-":
                chars[i] = chars[i + 1] = " "
                i += 2
                state = "line"
                continue
            if ch == "/" and nxt == "*":
                chars[i] = chars[i + 1] = " "
                i += 2
                state = "block"
                continue
        elif state == "line":
            if ch in "\r\n":
                state = "code"
            else:
                chars[i] = " "
        else:
            if ch == "*" and nxt == "/":
                chars[i] = chars[i + 1] = " "
                i += 2
                state = "code"
                continue
            if ch not in "\r\n":
                chars[i] = " "
        i += 1
    return "".join(chars)


def mask_single_quoted_literals(text: str) -> str:
    """Blank SQL string literals; dynamic SQL does not create pg_depend edges."""
    chars = list(text)
    i = 0
    quote = False
    while i < len(chars):
        ch = chars[i]
        nxt = chars[i + 1] if i + 1 < len(chars) else ""
        if not quote:
            if ch == "'":
                quote = True
                chars[i] = " "
        else:
            if ch == "'" and nxt == "'":
                chars[i] = chars[i + 1] = " "
                i += 2
                continue
            if ch == "'":
                quote = False
            if ch not in "\r\n":
                chars[i] = " "
        i += 1
    return "".join(chars)


def parse_upgrade_events() -> tuple[dict[str, list[int]], dict[str, list[Rebind]], int]:
    """Return ordered DROP and dependency-bearing CREATE OR REPLACE events."""
    manifest = (SQL_ROOT / "manifest.upgrade").read_text().split()
    drops: dict[str, list[int]] = defaultdict(list)
    rebinds: dict[str, list[Rebind]] = defaultdict(list)
    offset = 0

    for entry in manifest:
        if not entry.endswith(".sql.in"):
            continue
        path = SQL_ROOT / entry
        if not path.exists():
            continue
        text = path.read_text(errors="replace")
        masked = mask_comments(text)
        events = list(TOP_LEVEL_EVENT_RE.finditer(masked))

        for match in DROP_RE.finditer(masked):
            signature = canonical_signature(match.group(1), match.group(2))
            drops[signature].append(offset + match.start())

        for match in CREATE_HEAD_RE.finditer(masked):
            open_pos = masked.find("(", match.start(), match.end() + 1)
            if open_pos < 0:
                raise ValueError(f"cannot locate argument list for {match.group(1)} in {entry}")
            close_pos = find_decl_close(masked, open_pos)
            signature = canonical_signature(
                match.group(1), text[open_pos + 1 : close_pos], declaration=True
            )

            next_pos = len(text)
            for event in events:
                if event.start() > match.start():
                    next_pos = event.start()
                    break
            body = text[close_pos + 1 : next_pos]
            # Only parsed SQL-standard bodies can carry the dependency state this
            # gate models. Opaque AS $$ replacements stay conservative.
            if re.search(r"\bBEGIN\s+ATOMIC\b", mask_comments(body), re.IGNORECASE):
                rebinds[signature].append(Rebind(offset + match.start(), body))

        offset += len(text)

    return dict(drops), dict(rebinds), offset


def body_references_base(body: str, base_signature: str) -> bool:
    base_name = base_signature.split("(", 1)[0].lower()
    short = base_name.rsplit(".", 1)[-1]
    code = mask_single_quoted_literals(mask_comments(body))
    pattern = re.compile(
        rf"(?<![A-Za-z0-9_])(?:{re.escape(base_name)}|{re.escape(short)})\s*\(",
        re.IGNORECASE,
    )
    return pattern.search(code) is not None


def dependency_released_before(
    dependent: str,
    base: str,
    base_drop: int,
    drops: dict[str, list[int]],
    rebinds: dict[str, list[Rebind]],
) -> tuple[bool, str]:
    """Model the dependent's last relevant upgrade event before base DROP."""
    events: list[tuple[int, str, str]] = []
    for position in drops.get(dependent, []):
        if position < base_drop:
            events.append((position, "drop", ""))
    for rebind in rebinds.get(dependent, []):
        if rebind.position < base_drop:
            events.append((rebind.position, "rebind", rebind.body))
    if not events:
        return False, "has no earlier drop or dependency-releasing rebind"

    position, kind, body = max(events, key=lambda item: item[0])
    if kind == "drop":
        return True, f"dropped at {position}"
    if body_references_base(body, base):
        return False, "last earlier rebind still references the base"
    return True, f"rebound without base dependency at {position}"


def live_pairs() -> list[tuple[str, str]]:
    out = subprocess.run(
        [
            "psql", "-h", "/var/run/postgresql", "-U", "laplace_admin",
            "-d", "laplace", "-tAc", PAIRS_SQL,
        ],
        capture_output=True,
        text=True,
    )
    if out.returncode != 0:
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
    try:
        drops, rebinds, _ = parse_upgrade_events()
    except (OSError, ValueError) as exc:
        print(f"ERROR: cannot parse upgrade dependency events: {exc}", file=sys.stderr)
        return 2

    first_drop = {signature: min(positions) for signature, positions in drops.items()}
    bad = 0
    pairs = live_pairs()
    for dependent, base in pairs:
        base_drop = first_drop.get(base)
        if base_drop is None:
            continue
        released, detail = dependency_released_before(
            dependent, base, base_drop, drops, rebinds
        )
        if released:
            continue
        print(
            f"ERROR: upgrade drops {base} while live dependent {dependent} "
            f"still pins it (RESTRICT): {detail}; drop the dependent first or "
            "CREATE OR REPLACE its exact BEGIN ATOMIC signature before the base "
            "drop with a body that no longer references the base"
        )
        bad += 1

    print(
        f"drop-order gate: {bad} hazard(s) across {len(pairs)} live signature pair(s), "
        f"{len(first_drop)} dropped signature(s), "
        f"{sum(len(v) for v in rebinds.values())} parsed rebind(s)"
    )
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
