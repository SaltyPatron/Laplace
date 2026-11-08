#!/usr/bin/env python3
"""Regexes, comment strippers, and rule scanners for isa-gate-check.py."""

from __future__ import annotations

from collections import Counter
from pathlib import Path
import re
from typing import Iterable


ROOT = Path(__file__).resolve().parents[1]
BASELINE = ROOT / "scripts" / "isa-gate-baseline.json"
MANIFEST = ROOT / "engine" / "manifest" / "relation_types.toml"

# The schema prefix is OPTIONAL AND ARBITRARY. It used to accept only
# `@extschema@.`, which meant that once the purpose-schema migration qualified
# every declaration (ops.entity_facets, consensus.salient_facts, …) this pattern
# matched NOTHING: it consumed the schema as the name and then demanded '(' where
# a '.' sat. MEASURED 2026-08-10: 0 of 377 declared functions were visible, so
# g4_dead_canonical reported 0 while scanning an empty set — a green gate over no
# data, not a clean tree. scan_g12 carries its own copy of this pattern that was
# updated for the migration; this shared one was not.
CREATE_FUNCTION = re.compile(
    r"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+"
    r"(?:(?:[A-Za-z_][A-Za-z0-9_]*|@extschema@)\.)?"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*\(",
    re.IGNORECASE,
)
CALL_TOKEN = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")
# `WITH x AS (`, `, x AS (`, and the RECURSIVE / column-list / [NOT] MATERIALIZED
# spellings. Used by G11 to treat a CTE alias as a local binding.
CTE_ALIAS = re.compile(
    r"(?:WITH|,)\s*(?:RECURSIVE\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*"
    r"(?:\([^)]*\)\s*)?AS\s*(?:NOT\s+)?(?:MATERIALIZED\s*)?\(",
    re.IGNORECASE,
)
# CREATE AGGREGATE ... SFUNC = foo / FINALFUNC = bar and CREATE FUNCTION ... SUPPORT foo — real callers without '('.
AGGREGATE_FUNC_REF = re.compile(
    r"\b(?:(?:SFUNC|FINALFUNC|MSFUNC|MINVFUNC|COMBINEFUNC|SERIALFUNC|DESERIALFUNC)\s*=\s*|SUPPORT\s+)"
    r"(?:(?:[A-Za-z_][A-Za-z0-9_]*|@extschema@)\.)?([A-Za-z_][A-Za-z0-9_]*)\b",
    re.IGNORECASE,
)

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
# G13 — a string operation applied to a realized surface.
#
# The law (spec: hash-space until render): `realize`/`render`/`render_text`/
# `label` ARE the output projection. From prompt decomposition to the final
# projection it is ids only — indexing, cost, pathing, fanout, mask routing,
# Glicko weight. A string operation wrapped around a realizer means the surface
# is about to be compared, grouped, joined, sorted or measured on, i.e. text is
# standing in for identity in the middle of the pipeline. That is simultaneously
# three defects: the language dependence that breaks omni-glottal behaviour, the
# identity contamination (the substrate already carries the case bridge as rated
# tier-0 evidence, K --HAS_CASE_MAPPING {lower}--> k, read by word_case_variants();
# locale folding fabricates that link unrated and unprovenanced, and is
# locale-dependent besides — Turkish dotless i), and a per-row STABLE call.
#
# Case folds are the common form but not the only one. `char_length(surface)`
# as a sort key was one of the three costs measured taking chat() down, beside
# `lower(realize(syn)) = surface` — same defect class, different function. The
# check covers the fold family AND the length/trim/rewrite family for that
# reason; scoping it to lower() alone would have left the sibling live and
# called the job done.
G13_STRING_OP_ON_SURFACE = re.compile(
    r"\b(?P<op>char_length|length|octet_length|bit_length"
    r"|btrim|ltrim|rtrim|trim"
    r"|replace|translate|substr|substring|left|right"
    r"|md5|starts_with|strpos|position)\s*\(\s*"
    r"(?:[A-Za-z_][A-Za-z0-9_]*\.)?"
    r"(?P<realizer>realize|render|render_text|render_text_fast|label|label_or_hex"
    r"|realize_batch|render_text_batch|synset_gloss|type_label)\s*\(",
    re.IGNORECASE,
)

# G14 — case folding, anywhere in the substrate.
#
# G13 is the render/label rule: do not operate on a surface you just realized.
# This is a different and larger law, and scoping the check to realizer
# arguments misses it entirely — `lower(p_phrase)` wraps no realizer and is the
# worse defect.
#
# CASE IS NOT A PROPERTY OF TEXT — IT IS PACKAGING, AND ITS OWN NAME SAYS SO.
# "Uppercase" and "lowercase" are letterpress furniture. A compositor's type
# lived in a case that folded into two halves; opened, the majuscules sat in the
# upper half and the minuscules in the lower. The terms name WHICH HALF OF THE
# BOX a typesetter's hand reached into. That is a storage layout — precisely the
# class of thing this substrate strips at the witness boundary and never admits
# into identity, the same rule that keeps a tokenizer's vocabulary or a
# checkpoint's tensor layout out of the hash. Folding on it is letting a
# 16th-century box's hinge decide what two pieces of content mean.
#
# It is also a property of BICAMERAL scripts only, which are a minority of the
# writing systems this substrate claims to serve. There is no lowercase 犬 and no
# uppercase 犬; Han is unicameral, as are Arabic, Hebrew, Devanagari, Thai and
# Hangul. `lower()` returns those inputs unchanged, so any branch whose behaviour
# depends on the fold is silently inert for most of the world's scripts and
# silently active for Latin/Greek/Cyrillic. That is not a rounding error in an
# omni-glottal system; it is a pipeline that works in one script family and
# quietly does nothing in the others, which is worse than failing, because it
# never reports.
#
# Where it DOES fire it is locale-dependent and therefore not deterministic
# identity: Turkish I/ı vs I/i, German ß, Greek final sigma, Lithuanian dot-above
# — the fold's result depends on the server's collation, which is environment,
# not content. A content-addressed substrate cannot key, compare, group, join or
# route on a value that changes with the locale of the machine reading it.
#
# The substrate already holds the correct primitive: case is witnessed at tier 0
# as rated evidence (K --HAS_CASE_MAPPING {lower}--> k, 1,488 lc / 1,505 uc /
# 1,509 tc cells) and `word_case_variants()` reads it. That path is provenanced,
# language-correct, and returns the empty set for 犬 — which is the truth.
# `lower()` fabricates the same link unrated, unprovenanced, and Latin-only.
#
# Exempt by construction: DDL identifier generation (partition names are ASCII
# by definition and never content) and prose assembly in the final output
# projection, which is where string work is supposed to live.
G14_CASE_FOLD = re.compile(r"\b(?P<fold>lower|upper|initcap)\s*\(", re.IGNORECASE)

# G15 — a CREATE/DROP FUNCTION whose name carries no schema. Anchored at line
# start because that is how every declaration in sql/functions is written, and
# it keeps the pattern off nested references inside bodies (which is G11's job).
# The name group deliberately excludes '.', so a qualified name never matches.
G15_UNQUALIFIED_DDL = re.compile(
    r"^(?P<ddl>CREATE\s+OR\s+REPLACE\s+FUNCTION|DROP\s+FUNCTION\s+IF\s+EXISTS)"
    r"\s+(?P<name>[a-z_][a-z0-9_]*)\s*\(",
    re.MULTILINE | re.IGNORECASE,
)

G14_EXEMPT_PREFIXES = (
    # Generated DDL: `tbl || '_r_' || lower(h)` builds a partition identifier.
    "extension/laplace_substrate/sql/generated/",
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

# Declaration rosters are EXEMPT from the C# literalism scan. Two gates were
# in direct conflict: DecomposerArchitectureGateTests REQUIRES every source to
# declare its emitted relations by name (the `Relations` roster is the only
# API for it), while this ratchet banned the name literal — so every new
# source failed one gate or the other (measured 2026-08-06: the media-ladder
# sources' declaration rosters were this scan's only findings). The ratchet's
# target is AD-HOC CALL-SITE literals — a relation name spelled at an emit or
# query site instead of resolved through the registry — and those remain
# ratcheted everywhere outside the declaration span.
CSHARP_DECLARATION_ROSTER = re.compile(
    r"(?:Relations|DeclaredRelations)\s*(?:\{\s*get;\s*\}\s*=|=>|=)\s*\[[^\]]*\]",
    re.DOTALL,
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


def mask_sql_single_quoted_literals(text: str) -> str:
    """Blank SQL string literals so identifier scans only inspect executable SQL."""
    out: list[str] = []
    i = 0
    in_string = False
    while i < len(text):
        char = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if not in_string:
            if char == "'":
                in_string = True
                out.append(" ")
            else:
                out.append(char)
        else:
            out.append(char if char in "\r\n" else " ")
            if char == "'" and nxt == "'":
                out.append(" ")
                i += 1
            elif char == "'":
                in_string = False
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
        # Rendered from engine/manifest/relation_types.toml by codegen. The manifest
        # IS the vocabulary this gate protects, so its rendering cannot be vocabulary
        # drift — every name there is one the manifest already declares, and it moves
        # when the manifest moves. Baselining the names individually would instead
        # require editing the baseline on every manifest change, which is the
        # "hiding policy in data" this file's header rejects. Same directory G14
        # already exempts.
        if relative(path).startswith(G14_EXEMPT_PREFIXES):
            continue
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
        text = CSHARP_DECLARATION_ROSTER.sub("", text)
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


def scan_g13_string_op_on_surface() -> Counter[str]:
    """A string operation applied to a realized surface, anywhere in the read path.

    Scans the whole substrate SQL tree plus the extension C sources, because the
    defect has appeared in both (`spi_common.h` publishes per-id realizers that C
    callers have folded the same way).
    """
    found: Counter[str] = Counter()
    roots = [
        (ROOT / "extension" / "laplace_substrate" / "sql", (".sql.in",)),
        (ROOT / "extension" / "laplace_substrate" / "src", (".c", ".h")),
    ]
    for root, suffixes in roots:
        for path in production_files(root, suffixes):
            raw = path.read_text(encoding="utf-8", errors="replace")
            text = (
                strip_c_comments(raw)
                if path.name.endswith((".c", ".h"))
                else strip_sql_comments(raw)
            )
            add_matches(
                found,
                path,
                text,
                G13_STRING_OP_ON_SURFACE,
                lambda match: (
                    f"{match.group('op').lower()}({match.group('realizer').lower()}"
                ),
            )
    return found


def scan_g14_case_fold() -> Counter[str]:
    """lower()/upper()/initcap() anywhere in the substrate read path."""
    found: Counter[str] = Counter()
    roots = [
        (ROOT / "extension" / "laplace_substrate" / "sql", (".sql.in",)),
        (ROOT / "extension" / "laplace_substrate" / "src", (".c", ".h")),
    ]
    for root, suffixes in roots:
        for path in production_files(root, suffixes):
            rel = relative(path)
            if rel.startswith(G14_EXEMPT_PREFIXES):
                continue
            raw = path.read_text(encoding="utf-8", errors="replace")
            text = (
                strip_c_comments(raw)
                if path.name.endswith((".c", ".h"))
                else strip_sql_comments(raw)
            )
            add_matches(
                found,
                path,
                text,
                G14_CASE_FOLD,
                lambda match: match.group("fold").lower(),
            )
    return found


def scan_g15_unqualified_ddl() -> Counter[str]:
    """A CREATE/DROP FUNCTION whose name carries no schema.

    Unqualified DDL resolves through search_path at install time, so the object
    lands somewhere the author did not choose and callers cannot predict. It
    fails in both directions: an unqualified CREATE installs into the wrong
    schema and every qualified caller raises 42883, while an unqualified DROP
    silently misses and leaves retired surface installed.

    Bodies are not scanned — an unqualified reference inside a body is G11.
    """
    found: Counter[str] = Counter()
    root = ROOT / "extension" / "laplace_substrate" / "sql"
    for path in production_files(root, (".sql.in",)):
        text = strip_sql_comments(path.read_text(encoding="utf-8", errors="replace"))
        add_matches(
            found,
            path,
            text,
            G15_UNQUALIFIED_DDL,
            lambda match: match.group("name"),
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
        text = mask_sql_single_quoted_literals(strip_sql_comments(raw))
        if SET_SEARCH_PATH.search(text):
            continue                      # still gated by SET; not this gate's business
        rel = relative(path)
        own = {m.group(1).lower() for m in CREATE_FUNCTION.finditer(text)}
        own |= {m.group(1).lower() for m in DROP_FUNCTION.finditer(text)}
        # A CTE alias is a LOCAL binding, not a substrate reference — `WITH ranked
        # AS (...)` then `FROM ranked` resolves to the CTE, never to a function,
        # so search_path cannot change what it means and the gate has nothing to
        # enforce. Names collide constantly because the natural CTE vocabulary is
        # also the natural function vocabulary: batch/ranked/edges/facts are all
        # both. MEASURED 2026-08-10, the first run after CREATE_FUNCTION was
        # repaired: 57 of 57 G11 hits were CTE aliases — a 100% false-positive
        # rate that would have been baselined as real had it not been checked.
        own |= {m.group(1).lower() for m in CTE_ALIAS.finditer(text)}

        for match in UNQUALIFIED_REF.finditer(text):
            raw_name = match.group("name") or match.group("t")
            if raw_name is None:
                continue
            name = raw_name.lower()
            if name not in defined or name in own:
                continue
            found[f"{rel}::{name}"] += 1
    return found
