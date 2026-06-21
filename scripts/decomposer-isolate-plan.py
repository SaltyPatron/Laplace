#!/usr/bin/env python3
"""Resolve isolated DB name and ordered prerequisite sources for a decomposer test run."""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GATES = ROOT / "scripts" / "decomposer-gates.json"


def load_spec() -> dict:
    with GATES.open(encoding="utf-8") as f:
        return json.load(f)


def isolated_name(prefix: str, source: str) -> str:
    return f"{prefix}_{source}"


def ordered_prerequisites(source: str, spec: dict) -> list[str]:
    """Transitive prerequisites in manifest_order (unicode before iso639 before wordnet, …)."""
    order = spec.get("manifest_order", [])
    order_index = {s: i for i, s in enumerate(order)}
    seen: set[str] = set()
    result: list[str] = []

    def walk(s: str) -> None:
        for p in spec["sources"].get(s, {}).get("prerequisites", []):
            walk(p)
            if p not in seen:
                seen.add(p)
                result.append(p)

    walk(source)
    result.sort(key=lambda s: order_index.get(s, 999))
    return result


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--source", required=True)
    p.add_argument("--prefix", default=os.environ.get("LAPLACE_ISOLATE_PREFIX", "laplace_d"))
    p.add_argument("--format", choices=("env", "json"), default="env")
    args = p.parse_args()

    spec = load_spec()
    if args.source not in spec.get("sources", {}):
        raise SystemExit(f"unknown source: {args.source}")

    target = isolated_name(args.prefix, args.source)
    prereqs = ordered_prerequisites(args.source, spec)

    out = {
        "source": args.source,
        "target_db": target,
        "prerequisite_sources": prereqs,
    }

    if args.format == "json":
        print(json.dumps(out))
    else:
        print(f"TARGET_DB={target}")
        print(f"PREREQ_SOURCES={' '.join(prereqs)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
