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
#
# g1 25 -> 23 (2026-08-04). related_objects stopped hand-rolling the consensus
# scan and now wraps edges_raw(), which computes the fold value through eff_mu();
# its two open-coded `rating - 2*rd` sites went with the body. Shrink, not an
# exception.
CEILINGS = {
    "g1_weight_literalism": 11,
    "g3_sql_vocabulary_literalism": 240,
    "g3_c_vocabulary_literalism": 17,
    # 700 -> 701 (2026-08-05): the language-scope declaration. Nine monolingual
    # sources emitted no HAS_LANGUAGE at all, so every English sense read back as
    # language-UNATTESTED and word_language() inferred at read time a fact the
    # source states for free.
    #
    # Cost minimized first, per the note above — 3 literals across 2 files reduced
    # to 1:
    #   -2  WordNetDecomposer's two emit sites now read EtlSource.LanguageScopeRelation
    #   -1  EtlSource.LanguageScopeRelations is built from that same constant
    # The remaining 1 is the floor: the name has to exist once in C#, and this is
    # the single place the feature owns it. A scoped source that spelled it again
    # locally would be the per-source hand-roll that caused the defect.
    "g3_csharp_vocabulary_literalism": 504,
    "g8_band_literalism": 3,
    # G4 scaffolding (W6 D3): grep for CREATE FUNCTION with zero callers outside
    # its own CREATE line. Destination form is substrate CALLS in-degree after W3
    # (#765); this allowlist is shrink-only until that replace lands.
    #
    # 0 -> 16, and this is the ONE ceiling raise in this file. It is not a
    # regression: the previous 0 was never a measurement. CREATE_FUNCTION matched
    # only `@extschema@.`-prefixed names, so once the purpose-schema migration
    # qualified every declaration the detector saw 0 of 377 functions and this
    # gate reported green over an empty set. Same blindness silently zeroed G11.
    # 16 is the first true count this gate has ever produced.
    #
    # The 16 are NOT uniformly deletable, which is why they are baselined rather
    # than removed here:
    #   8  chess/*  — Phase D reads (distance_to_syzygy, missed_finish,
    #      opening_shape_peers, opening_record, opening_preference,
    #      opening_endgames, time_pressure_outcome, position_ready). Written and
    #      installed, awaiting the extension rebuild that puts them in api()
    #      (.scratchpad/43, unchecked box). Not dead — not yet wired.
    #   4  inference/* — decay, prune, distill, forward_step. prune DELETEs from
    #      consensus and decay hand-UPDATEs it, both against the "consensus
    #      accumulates at ingest, no backfill path" law, and both reachable from
    #      the MCP op tool via api()'s unfiltered pg_proc scan. These are
    #      deletions, tracked in #989 — a write-path hazard, not scaffolding.
    #   4  converse/geometry — tiered, label_or_hex_batch, locale, cluster_batch.
    #      Audited one by one 2026-08-10. NONE are safe deletions; "zero textual
    #      caller" turned out to mean "written ahead of its caller" in every case:
    #        converse.tiered            deliberately off the hot path, and chat.sql.in
    #                                   says so in 14 lines at :441. Hangs are fixed
    #                                   (ceef97d); it stays off because content
    #                                   regressed to topic echo. Open as #878.
    #        converse.label_or_hex_batch the BATCH replacement for converse.label_or_hex,
    #                                   which production C# calls PER ROW from 20 sites
    #                                   across NpgsqlSubstrateReads and SubstrateTools.
    #                                   Added 2026-08-09 by "standardize deployed MCP and
    #                                   batch realization" and never wired. Deleting it
    #                                   would delete the fix and keep the N+1 — the same
    #                                   shape evidence_receipt's header records as
    #                                   ~3,129 renders to return 3 rows.
    #        structural.locale          2026-06-30 import, touched since only by schema
    #        structural.cluster_batch   migrations. Operator-diagnostic shaped, and
    #                                   api() is an unfiltered pg_proc scan (#989), so
    #                                   "no textual caller" cannot prove unused while
    #                                   any MCP op call reaches them. Not provably dead.
    "g4_dead_canonical": 16,
    # Measured 2026-08-05, landing with its violations enumerated per W6's trap
    # note ("a gate that goes red on merge-day teaches people to ignore it").
    # 29 occurrences across 10 sites, all pre-existing: model_factor (6 names),
    # entities_has_highway, and the three canonical_names writers.
    "g11_unqualified_in_setless_body": 0,
    # GH #764 step 3: LANGUAGE sql with quoted-string bodies (AS $$) — PostgreSQL
    # records no pg_depend. Shrink-only allowlist; new SQL must use BEGIN ATOMIC.
    "g12_string_sql_bodies": 216,
    # G13 — case-folding a realized surface. Measured 2026-08-10 with the check
    # that introduced it, so it lands enumerated rather than red on merge day.
    # Both survivors are in translate_to's language-reference matcher, where the
    # fold is bounded (once per DISTINCT candidate language, inside a
    # MATERIALIZED fence) and compares against a user-supplied language NAME —
    # the input boundary the read law does permit. They are baselined, not
    # blessed: the durable fix is resolving the language reference to an id once
    # and comparing ids, which retires the fold entirely.
    #
    # This ceiling exists because the defect kept coming back. converse(),
    # converse_walk() and chat() each carried `lower(realize(syn)) = surface` as
    # a sort key, each was fixed separately, and each fix left only a comment
    # behind — nothing stopped the next author from writing the fourth. A
    # comment is not enforcement.
    "g13_string_op_on_surface": 1,
    # G14 — case folding. Measured 2026-08-10, landing enumerated: 24 occurrences
    # across 9 sites, all pre-existing.
    #
    #   14  initcap() in chat_scaffold/converse_facts prose assembly. Output
    #       projection, so the least wrong — but note the shape: these templates
    #       already ship an English and a Bulgarian arm, both bicameral. The same
    #       initcap() on a Japanese or Arabic arm silently returns its input, so
    #       the substrate would capitalize its replies in some languages and not
    #       others with nothing reporting the difference.
    #    7  input-boundary normalization of a caller-supplied string before id
    #       resolution (translate_to's language reference, chess tc-class and
    #       player name). Permitted by the read law, still Latin-only: a language
    #       named in Han or Arabic gets no normalization at all, so the matcher
    #       is case-forgiving for "English" and case-strict for "日本語".
    #    2  type_label/type_label_batch display formatting.
    #    1  resolve_topic — THE REAL ONE, and it is not a formatting call. It case-
    #       folds a raw prompt to match a hardcoded English pronoun list
    #       (it|its|that|this|they|...) and returns the session context on a hit.
    #       Two defects stacked: an English function-word stoplist, which the
    #       conversational design says to replace with frame evocation, and a fold
    #       that does nothing for a Japanese or Arabic pronoun — so anaphora
    #       resolution is a feature English speakers get and nobody else does.
    #       It is also on the elector path this session is measuring.
    "g14_case_fold": 24,
    # G15 — unqualified CREATE/DROP FUNCTION. Ceiling 0: measured 2026-08-10,
    # all 32 occurrences fixed in the same change that added this gate, so there
    # is no landing to grandfather.
    #
    # `CREATE OR REPLACE FUNCTION evidence_receipt(...)` with no schema installs
    # wherever search_path points. It went to laplace.evidence_receipt while
    # every caller — and all three siblings in its own directory — used ops.
    # NpgsqlSubstrateReads asked for ops.evidence_receipt, got 42883, and because
    # SubstrateClient.Explore fans nine reads through one Task.WhenAll, that one
    # missing function failed the WHOLE Explore entity page. Four more functions
    # carried the identical defect and were dead read-path surface the whole time
    # (top_relations_readable, consensus_out_readable, attestations_out,
    # attestations_in).
    #
    # The DROP side fails the opposite way and is quieter: an unqualified
    # `DROP FUNCTION IF EXISTS substrate_health(boolean)` silently no-ops when
    # search_path does not reach the schema holding it, so the retired function
    # SURVIVES — which is the one thing the retirement files exist to prevent.
    #
    # Not flagged: `DROP laplace.X` followed by `CREATE <purpose>.X` is the
    # purpose-schema migration idiom (generation/variant_walk.sql.in), used by
    # 250 files. Both halves are qualified, so both are correct and neither
    # matches this pattern. This gate only rejects a name with NO schema at all.
    "g15_unqualified_ddl": 0,
}

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
# CREATE AGGREGATE ... SFUNC = foo / FINALFUNC = bar — real callers without '('.
AGGREGATE_FUNC_REF = re.compile(
    r"\b(?:SFUNC|FINALFUNC|MSFUNC|MINVFUNC|COMBINEFUNC|SERIALFUNC|DESERIALFUNC)\s*=\s*"
    r"(?:[A-Za-z_][A-Za-z0-9_]*\.)?([A-Za-z_][A-Za-z0-9_]*)\b",
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
# tier-0 evidence, K --HAS_LOWERCASE_MAPPING--> k, read by word_case_variants();
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
# as rated evidence (K --HAS_LOWERCASE_MAPPING--> k, 1,488 lc / 1,505 uc /
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
        for match in AGGREGATE_FUNC_REF.finditer(text):
            name = match.group(1).lower()
            if name in defined and rel != defined[name]:
                calls[name] += 1

    found: Counter[str] = Counter()
    for name, def_path in defined.items():
        if calls[name] == 0:
            found[f"{def_path}::{name}"] = 1
    return found


def scan_g12_string_sql_bodies() -> Counter[str]:
    """LANGUAGE sql functions still using opaque AS $$ bodies (GH #764).

    BEGIN ATOMIC bodies parse at CREATE time and record pg_depend. String bodies
    do not. Count shrinks as families convert; new string-bodied SQL fails CI.
    """
    functions_root = ROOT / "extension" / "laplace_substrate" / "sql" / "functions"
    create_head = re.compile(
        r"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+"
        r"(?:(?:[A-Za-z_][A-Za-z0-9_]*|@extschema@)\.)?"
        r"([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        re.IGNORECASE,
    )
    found: Counter[str] = Counter()
    for path in production_files(functions_root, (".sql.in",)):
        rel = relative(path)
        text = strip_sql_comments(path.read_text(encoding="utf-8", errors="replace"))
        parts = re.split(r"(?=CREATE\s+OR\s+REPLACE\s+FUNCTION\b)", text, flags=re.IGNORECASE)
        for part in parts:
            head = create_head.match(part)
            if head is None:
                continue
            lang = re.search(r"\bLANGUAGE\s+sql\b", part, flags=re.IGNORECASE)
            if lang is None:
                continue
            after = part[lang.end() :]
            # Options may sit between LANGUAGE sql and the body (IMMUTABLE, PARALLEL, …).
            body = re.search(r"\bBEGIN\s+ATOMIC\b|\bAS\s+\$", after, flags=re.IGNORECASE)
            if body is None or body.group(0).upper().startswith("BEGIN"):
                continue
            found[f"{rel}::{head.group(1).lower()}"] += 1
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
        "g12_string_sql_bodies": scan_g12_string_sql_bodies(),
        "g13_string_op_on_surface": scan_g13_string_op_on_surface(),
        "g14_case_fold": scan_g14_case_fold(),
        "g15_unqualified_ddl": scan_g15_unqualified_ddl(),
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
