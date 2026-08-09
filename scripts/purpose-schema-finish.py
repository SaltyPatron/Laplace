#!/usr/bin/env python3
"""Finish GH #862 purpose-schema migration in one pass.

Rules match landed families (taxonomy / lexical / realize / converse):
  DROP FUNCTION IF EXISTS laplace.<old>(...);
  CREATE OR REPLACE FUNCTION <purpose>.<new>(...);
  strip SET search_path (#860 inlining);
  rewrite callers laplace.<old> / bare <old>( → <purpose>.<new>(
  fix contamination: RETURNS/AS laplace.eff_mu, three-part laplace.purpose.x

Identity / μ / relation / readback / ops / inspect stay in laplace.
Run from repo root. Review via git diff.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SQL = ROOT / "extension/laplace_substrate/sql"
SRC = ROOT / "extension/laplace_substrate/src"
APP = ROOT / "app"
TESTS = ROOT / "extension/laplace_substrate/tests"

DIR_SCHEMA: dict[str, str | None] = {
    "consensus": "consensus",
    "converse": "converse",
    "lexical": "lexical",
    "taxonomy": "taxonomy",
    "realize": "realize",
    "chess": "chess",
    "generation": "generation",
    "structural": "structural",
    "geometry": "structural",
    "trajectory": "structural",
    "recall": "converse",
    "cascade": "converse",
    "contrast": "converse",
    "fold": "consensus",
    "highway": "consensus",
    "link": "lexical",
    "corpus": "generation",
    "variant": "generation",
    "model": "generation",
    "inference": "generation",
    "mu": None,
    "glicko2": None,
    "relation": None,
    "readback": None,
    "identity": None,
    "ops": None,
    "inspect": None,
    "ingest": None,
    "analysis": None,
}

NAME_OVERRIDES: dict[str, tuple[str | None, str]] = {
    "word_id": (None, "word_id"),
    "label": ("realize", "label"),
    "realize_path": ("realize", "path"),
    "realize_path_with_dirs": ("realize", "path"),
    "structural_neighbors": ("structural", "neighbors"),
    "structural_neighbors_of": ("structural", "neighbors_of"),
    "structural_locale": ("structural", "locale"),
    "generation_probe": ("generation", "probe"),
    "realize_batch": ("realize", "batch"),
    "prompt_language": ("converse", "prompt_language"),
    "prompt_coherence": ("converse", "prompt_coherence"),
    "chat": ("converse", "chat"),
    "consensus_in": ("consensus", "consensus_in"),
    "consensus_out": ("consensus", "consensus_out"),
    "laplace_chess_position_ready": ("chess", "position_ready"),
}

RESERVED = {
    "in", "out", "on", "off", "user", "order", "group", "table", "select",
    "all", "any", "some", "both", "lead", "new", "old", "to", "as", "is",
    "or", "and", "not",
}

PURPOSE = {
    "consensus", "converse", "lexical", "taxonomy", "generation",
    "structural", "chess", "realize",
}

TABLES = {
    "entities", "physicalities", "attestations", "consensus", "canonical_names",
    "ingest_run_journal", "ingest_flush_journal", "index_cycle_journal",
    "highway_mask_dirty", "consensus_id",
}

# Old flat → already-landed purpose (from prior commits)
LANDED: dict[str, str] = {
    "senses": "lexical.senses",
    "bubble_up": "taxonomy.bubble_up",
    "word_language": "converse.word_language",
    "realize": "realize.realize",
    "realize_path": "realize.path",
    "label": "realize.label",
    "structural_neighbors": "structural.neighbors",
    "structural_neighbors_of": "structural.neighbors_of",
    "structural_locale": "structural.locale",
    "generation_probe": "generation.probe",
    "converse_facts": "converse.facts",
    "converse_about": "converse.about",
    "prompt_state": "converse.prompt_state",
    "prompt_words": "converse.prompt_words",
    "relation_bands": "converse.relation_bands",
    "relation_band_catalog": "converse.relation_band_catalog",
    "non_kin_assoc_types": "converse.non_kin_assoc_types",
    "prompt_language_top": "converse.prompt_language_top",
    "relation_summary": "consensus.relation_summary",
    "relate_path": "consensus.relate_path",
    "gaps": "consensus.gaps",
    "usage_overlap": "consensus.usage_overlap",
    "consensus_cell": "consensus.cell",
    "lexical_peers": "lexical.lexical_peers",
    "synset_gloss": "taxonomy.synset_gloss",
    "top_synset": "taxonomy.top_synset",
    "retrieve_grounded": "taxonomy.retrieve_grounded",
    "translation_sources": "taxonomy.translation_sources",
    "consensus_taxonomy_edges": "taxonomy.consensus_taxonomy_edges",
    "links": "converse.links",
    "correlate": "converse.correlate",
    "epistemic_status": "converse.epistemic_status",
    "first_placed_topic": "converse.first_placed_topic",
    "resolve": "converse.resolve",
    "resolve_audit": "converse.resolve_audit",
    "resolve_last_word": "converse.resolve_last_word",
    "resolve_topic": "converse.resolve_topic",
    "session_trajectory": "converse.session_trajectory",
    "witness_precedes_chain": "converse.witness_precedes_chain",
    "pair_scores": "consensus.pair_scores",
    "entity_physicality_count": "consensus.entity_physicality_count",
}

CREATE_RE = re.compile(
    r"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+"
    r"((?:@extschema@|laplace|[a-z_]+)\.)?"
    r"([A-Za-z_][A-Za-z0-9_]*)\s*\(",
    re.IGNORECASE,
)


def shorten(schema: str, name: str) -> str:
    if name in NAME_OVERRIDES:
        return NAME_OVERRIDES[name][1]
    if name.startswith(f"{schema}_"):
        rest = name[len(schema) + 1 :]
        if rest and rest not in RESERVED and not rest[0].isdigit():
            return rest
    if schema == "chess" and name.startswith("chess_"):
        rest = name[6:]
        if rest and rest not in RESERVED:
            return rest
    if schema == "realize" and name.startswith("realize_"):
        rest = name[8:]
        if rest and rest not in RESERVED:
            return rest
    return name


def target_for(dir_name: str, func_name: str) -> tuple[str | None, str]:
    if func_name in NAME_OVERRIDES:
        return NAME_OVERRIDES[func_name]
    sch = DIR_SCHEMA.get(dir_name)
    if sch is None:
        return None, func_name
    return sch, shorten(sch, func_name)


def collect_moves() -> dict[str, tuple[str, str]]:
    moves: dict[str, tuple[str, str]] = {}

    def consider(dir_name: str, text: str) -> None:
        for m in CREATE_RE.finditer(text):
            qual, name = m.group(1), m.group(2)
            if qual and qual.rstrip(".") in PURPOSE:
                continue
            sch, new = target_for(dir_name, name)
            if sch is None:
                continue
            prev = moves.get(name)
            if prev and prev != (sch, new):
                raise SystemExit(f"conflicting move for {name}: {prev} vs {(sch, new)}")
            moves[name] = (sch, new)

    for path in sorted((SQL / "functions").rglob("*.sql.in")):
        d = path.relative_to(SQL / "functions").parts[0]
        consider(d, path.read_text(encoding="utf-8"))
    for path in sorted((SQL / "inference").glob("*.sql.in")):
        consider("recall", path.read_text(encoding="utf-8"))  # → converse
    return moves


def fix_contamination(text: str) -> str:
    def fix_returns(m: re.Match[str]) -> str:
        body = m.group(1)
        body = body.replace("laplace.eff_mu", "eff_mu")
        body = body.replace("laplace.attention", "attention")
        return f"RETURNS TABLE({body})"

    text = re.sub(r"RETURNS\s+TABLE\(([^)]*)\)", fix_returns, text, flags=re.IGNORECASE)
    text = re.sub(r"\bAS\s+laplace\.eff_mu\b", "AS eff_mu", text)
    text = re.sub(r"\bAS\s+laplace\.attention\b", "AS attention", text)
    text = re.sub(
        r"\blaplace\.(converse|consensus|lexical|realize|taxonomy|structural|generation|chess)\.",
        r"\1.",
        text,
    )
    return text


def strip_set_search_path(text: str) -> str:
    text = re.sub(
        r"\n[ \t]*SET\s+search_path\s*=\s*[^;\n]+(?=\s*AS\b)",
        "",
        text,
        flags=re.IGNORECASE,
    )
    text = re.sub(
        r"(\bLANGUAGE\s+\w+(?:\s+\w+)*)\s+SET\s+search_path\s*=\s*[^;\n]+(?=\s+AS\b)",
        r"\1",
        text,
        flags=re.IGNORECASE,
    )
    return text


def qualify_tables(text: str) -> str:
    for table in TABLES:
        # Do not touch purpose-schema calls: consensus.relate_path(...) —
        # `consensus` is both a table and a purpose schema.
        text = re.sub(
            rf"\b(FROM|JOIN|INTO|UPDATE|TABLE)\s+(?!laplace\.)({table})\b(?!\s*\.)",
            rf"\1 laplace.\2",
            text,
            flags=re.IGNORECASE,
        )
    return text


def fix_table_schema_collision(text: str) -> str:
    """Undo FROM laplace.consensus.fn → FROM consensus.fn (table/schema clash)."""
    text = re.sub(
        r"\blaplace\.(consensus|converse|lexical|realize|taxonomy|structural|generation|chess)\.",
        r"\1.",
        text,
    )
    return text


def scan_args(text: str, open_paren_idx: int) -> str:
    depth = 0
    i = open_paren_idx
    while i < len(text):
        ch = text[i]
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth == 0:
                return text[open_paren_idx + 1 : i]
        i += 1
    return ""


def rewrite_creates(text: str, dir_name: str | None, moves: dict[str, tuple[str, str]]) -> str:
    out: list[str] = []
    pos = 0
    for m in CREATE_RE.finditer(text):
        qual, name = m.group(1), m.group(2)
        out.append(text[pos:m.start()])
        sch_new: tuple[str, str] | None = None
        if qual and qual.rstrip(".") in PURPOSE:
            out.append(m.group(0))
            pos = m.end()
            continue
        if name in moves:
            sch_new = moves[name]
        elif dir_name is not None:
            sch, new = target_for(dir_name, name)
            if sch is not None:
                sch_new = (sch, new)
        if sch_new is None:
            out.append(m.group(0))
            pos = m.end()
            continue
        sch, new = sch_new
        args = scan_args(text, m.end() - 1)
        prefix = text[max(0, m.start() - 240) : m.start()]
        if f"DROP FUNCTION IF EXISTS laplace.{name}(" not in prefix:
            out.append(f"DROP FUNCTION IF EXISTS laplace.{name}({args});\n")
        out.append(f"CREATE OR REPLACE FUNCTION {sch}.{new}(")
        pos = m.end()
    out.append(text[pos:])
    text = "".join(out)
    text = strip_set_search_path(text)
    text = qualify_tables(text)
    return text


def rewrite_calls(text: str, call_map: dict[str, str]) -> str:
    # Preserve DROP FUNCTION IF EXISTS laplace.<old>(...) — those drop the
    # pre-migration name and must not be rewritten to purpose.<new>.
    drops: list[str] = []

    def stash_drop(m: re.Match[str]) -> str:
        drops.append(m.group(0))
        return f"__LAPLACE_DROP_{len(drops) - 1}__"

    # Line-bounded — never DOTALL across the file (catastrophic backtrack on .cs).
    text = re.sub(
        r"DROP\s+FUNCTION\s+IF\s+EXISTS\s+laplace\.[A-Za-z_][A-Za-z0-9_]*\s*\([^;\n]*\);",
        stash_drop,
        text,
        flags=re.IGNORECASE,
    )

    for old in sorted(call_map.keys(), key=len, reverse=True):
        target = call_map[old]
        text = re.sub(rf"\blaplace\.{re.escape(old)}\b", target, text)
        text = re.sub(rf"\b@extschema@\.{re.escape(old)}\b", target, text)
        text = re.sub(rf"(?<![.\w]){re.escape(old)}\s*\(", f"{target}(", text)
    # undo double purpose: converse.converse.chat
    text = re.sub(
        r"\b(consensus|converse|lexical|taxonomy|generation|structural|chess|realize)\.\1\.",
        r"\1.",
        text,
    )
    for i, drop in enumerate(drops):
        text = text.replace(f"__LAPLACE_DROP_{i}__", drop)
    return text


API_SQL = """CREATE OR REPLACE FUNCTION api(p_like text DEFAULT NULL)
    RETURNS TABLE(name text, args text, returns text)
    LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN n.nspname = 'laplace' THEN p.proname::text
                ELSE n.nspname || '.' || p.proname END,
           pg_get_function_arguments(p.oid),
           pg_get_function_result(p.oid)
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = ANY (ARRAY[
            'laplace','consensus','converse','lexical','taxonomy',
            'generation','structural','chess','realize'
        ])
      AND (p_like IS NULL
           OR p.proname ILIKE '%'||p_like||'%'
           OR (n.nspname || '.' || p.proname) ILIKE '%'||p_like||'%')
    ORDER BY n.nspname, p.proname
$$;
"""


def main() -> int:
    moves = collect_moves()
    call_map = dict(LANDED)
    for old, (sch, new) in moves.items():
        call_map[old] = f"{sch}.{new}"

    print(f"moves={len(moves)} call_map={len(call_map)}", file=sys.stderr)

    # Pass 1: SQL CREATE sites
    sql_targets: list[tuple[Path, str | None]] = []
    for path in sorted((SQL / "functions").rglob("*.sql.in")):
        d = path.relative_to(SQL / "functions").parts[0]
        sql_targets.append((path, d))
    for path in sorted((SQL / "inference").glob("*.sql.in")):
        sql_targets.append((path, "recall"))
    for path in sorted((SQL / "views").glob("*.sql.in")):
        sql_targets.append((path, None))

    for path, dir_name in sql_targets:
        orig = path.read_text(encoding="utf-8")
        text = fix_contamination(orig)
        text = rewrite_creates(text, dir_name, moves)
        text = rewrite_calls(text, call_map)
        text = fix_table_schema_collision(text)
        if text != orig:
            path.write_text(text, encoding="utf-8")
            print(f"sql {path.relative_to(ROOT)}", file=sys.stderr)

    # api()
    api_path = SQL / "functions/ops/api.sql.in"
    api_path.write_text(API_SQL, encoding="utf-8")
    print("sql functions/ops/api.sql.in (rewrote)", file=sys.stderr)

    # Pass 2: C / tests / app / remaining sql
    extras: list[Path] = []
    extras += list(SRC.glob("*.c"))
    extras += list(SRC.glob("*.h"))
    extras += list(TESTS.rglob("*.sql"))
    extras += list(APP.rglob("*.cs"))
    for base in (ROOT / "scripts/queries", ROOT / "scripts/sql"):
        if base.exists():
            extras += list(base.rglob("*.sql"))
            extras += list(base.rglob("*.sql.in"))

    for path in sorted(set(extras)):
        try:
            orig = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        text = fix_contamination(orig) if path.suffix in {".sql", ".in"} or str(path).endswith(".sql.in") else orig
        text = rewrite_calls(text, call_map)
        text = fix_table_schema_collision(text)
        if text != orig:
            path.write_text(text, encoding="utf-8")
            print(f"call {path.relative_to(ROOT)}", file=sys.stderr)

    map_path = ROOT / "build-logs" / "purpose-schema-rename-map.txt"
    map_path.parent.mkdir(parents=True, exist_ok=True)
    map_path.write_text(
        "\n".join(f"{o}\t{s}.{n}" for o, (s, n) in sorted(moves.items())) + "\n",
        encoding="utf-8",
    )
    print(f"wrote {map_path.relative_to(ROOT)}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
