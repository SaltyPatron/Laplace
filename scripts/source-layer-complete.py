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

GATES = Path(__file__).resolve().parent / "decomposer-gates.json"


def quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def main(argv: list[str]) -> int:
    if not 2 <= len(argv) <= 4:
        print(__doc__, file=sys.stderr)
        return 2
    name = argv[1]
    src = json.loads(GATES.read_text(encoding="utf-8"))["sources"].get(name, {})
    decomposer = src.get("decomposer") or (argv[2] if len(argv) > 2 else None)
    layer = src.get("layer", int(argv[3]) if len(argv) > 3 else None)
    if layer is None or (not src.get("witness") and not decomposer):
        print(f"source '{name}' has no gate entry and no decomposer/layer given", file=sys.stderr)
        return 2
    if src.get("witness"):
        authority, release = src["witness"]
        witness = f"laplace.witness_id({quote(authority)}, {quote(release)})"
        label = f"witness={authority}@{release} layer={layer}"
    else:
        witness = f"laplace.source_id({quote(decomposer)})"
        label = f"source={decomposer} layer={layer}"
    print(f"ops.layer_completed({witness}, {int(layer)})\t{label}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
