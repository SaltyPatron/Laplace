#!/usr/bin/env python3
"""Print the layer-completion predicate for one ingest source.

Completion is operational state (laplace.ingest_layer_completion), keyed by the
source witness and the layer. A recipe source generation completes under its
witness [authority, release]; a legacy decomposer under its named source until it
moves onto a recipe. Both, and the layer, come from scripts/decomposer-gates.json,
the same resolution scripts/decomposer-gate-check.py uses.

Usage: source-layer-complete.py NAME [DECOMPOSER [LAYER]]
Prints one line: SQL<TAB>LABEL, where SQL is a boolean expression and LABEL names
the witness and layer for diagnostics. DECOMPOSER and LAYER are the fallback for a
name the gates file does not list.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from ingest_catalog import canonical_source, load_catalog

GATES = Path(__file__).resolve().parent / "decomposer-gates.json"


def quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def catalog_witness(name: str) -> str | None:
    for entry in load_catalog()["sources"]:
        if entry["name"] == name:
            return entry.get("witness") or None
    return None


def main(argv: list[str]) -> int:
    if not 2 <= len(argv) <= 4:
        print(__doc__, file=sys.stderr)
        return 2
    name = canonical_source(argv[1])
    src = json.loads(GATES.read_text(encoding="utf-8"))["sources"].get(name, {})
    witness_name = catalog_witness(name)
    witness_trunk_check = (
        f"EXISTS (SELECT 1 FROM public.physicality p WHERE p.path ^@ public.laplace_text_id({quote(witness_name)}))"
        if witness_name else "false"
    )
    if src.get("native"):
        # A Native recipe source with no managed decomposer: check ops.native_source_completed if present, else witness trunk.
        print(
            f"(CASE WHEN to_regprocedure('ops.native_source_completed(text)') IS NOT NULL "
            f"THEN ops.native_source_completed({quote(name)}) "
            f"ELSE {witness_trunk_check} END)\tnative={name}"
        )
        return 0
    decomposer = src.get("decomposer") or (argv[2] if len(argv) > 2 else None)
    layer = src.get("layer", int(argv[3]) if len(argv) > 3 else None)
    if layer is None or (not src.get("witness") and not decomposer):
        print(f"source '{name}' has no gate entry and no decomposer/layer given", file=sys.stderr)
        return 2
    if src.get("witness"):
        authority, release = src["witness"]
        witness = f"public.blake3_send(public.laplace_compose(ARRAY[public.laplace_text_id({quote(authority)}), public.laplace_text_id({quote(release)})]))"
        label = f"witness={authority}@{release} layer={layer}"
        legacy_trunk = f"EXISTS (SELECT 1 FROM public.attestation a WHERE a.witness = public.blake3_from_bytea({witness}))"
    else:
        witness = f"public.blake3_send(public.laplace_text_id('substrate/source/' || {quote(decomposer)} || '/v1'))"
        label = f"source={decomposer} layer={layer}"
        legacy_trunk = "false"
    fallback = f"({witness_trunk_check} OR {legacy_trunk})" if witness_name else legacy_trunk
    print(
        f"(CASE WHEN to_regprocedure('ops.native_source_completed(text)') IS NOT NULL "
        f"AND to_regprocedure('ops.layer_completed(bytea,integer)') IS NOT NULL "
        f"THEN (ops.native_source_completed({quote(name)}) OR ops.layer_completed({witness}, {int(layer)})) "
        f"ELSE {fallback} END)\t{label}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
