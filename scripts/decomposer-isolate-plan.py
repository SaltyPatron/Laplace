#!/usr/bin/env python3
"""Resolve isolated DB name and ordered prerequisite sources for a decomposer test run."""

from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GATES = ROOT / "scripts" / "decomposer-gates.json"


def recipe_root() -> Path:
    root = Path(os.environ.get("LAPLACE_RECIPES", ROOT.parent / "Laplace-Engine" / "recipes"))
    if not (root / "order").is_file():
        raise ValueError(f"Native recipe estate has no order declaration: {root}; declare LAPLACE_RECIPES")
    return root


def source_order() -> list[str]:
    root = recipe_root()
    names = [line.split("#", 1)[0].strip() for line in (root / "order").read_text(encoding="utf-8").splitlines()]
    names = [name for name in names if name]
    if not names or len(names) != len(set(names)):
        raise ValueError("recipes/order is empty or contains duplicate sources")
    for name in names:
        if not re.fullmatch(r"[a-z0-9-]+", name) or not (root / name / "source").is_file():
            raise ValueError(f"Invalid or undeclared recipes/order source: {name}")
    return names


def load_spec() -> dict:
    with GATES.open(encoding="utf-8") as f:
        spec = json.load(f)
    spec["manifest_order"] = source_order()
    return spec


def isolated_name(prefix: str, source: str) -> str:
    return f"{prefix}_{source}"


def ordered_prerequisites(source: str, spec: dict) -> list[str]:
    """Transitive prerequisites in the canonical native source order."""
    order = source_order()
    order_index = {s: i for i, s in enumerate(order)}
    seen: set[str] = set()
    active: set[str] = set()
    result: list[str] = []

    def walk(s: str) -> None:
        if s in active:
            raise ValueError(f"Cyclic recipe dependency: {s}")
        active.add(s)
        if s in order_index:
            dependencies = []
            for line in (recipe_root() / s / "source").read_text(encoding="utf-8").splitlines():
                fields = line.split("#", 1)[0].split()
                if fields and fields[0] == "after":
                    dependencies.extend(fields[1:])
            unknown = set(dependencies) - order_index.keys()
            if unknown:
                raise ValueError(f"Unknown recipe dependencies for {s}: {sorted(unknown)}")
        else:
            dependencies = spec["sources"].get(s, {}).get("prerequisites", [])
        for p in dependencies:
            if p in seen:
                continue
            walk(p)
            seen.add(p)
            result.append(p)
        active.remove(s)

    walk(source)
    result.sort(key=lambda s: order_index.get(s, 999))
    return result


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--source")
    p.add_argument("--list-sources", action="store_true")
    p.add_argument("--prefix", default=os.environ.get("LAPLACE_ISOLATE_PREFIX", "laplace_d"))
    p.add_argument("--format", choices=("env", "json"), default="env")
    args = p.parse_args()
    if args.list_sources:
        print("\n".join(source_order()))
        return 0
    if not args.source:
        p.error("--source or --list-sources is required")

    spec = load_spec()
    if args.source not in spec.get("sources", {}) and args.source not in spec["manifest_order"]:
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
        # Shell-quoted: the caller eval's these lines. Unquoted, a multi-prerequisite
        # source emitted `PREREQ_SOURCES=unicode iso639 cili`, which eval reads as an
        # assignment followed by the COMMAND `iso639 cili` -- so decomposer-test.sh
        # died with "iso639: command not found" for every source with more than one
        # prerequisite, i.e. every source above the floor.
        print(f"TARGET_DB={shlex.quote(target)}")
        print(f"PREREQ_SOURCES={shlex.quote(' '.join(prereqs))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
