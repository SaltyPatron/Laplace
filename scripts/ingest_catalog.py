#!/usr/bin/env python3
"""Inventory the installed Native recipe estate without loading data or a database.

Names, aliases, content locations, dependencies and decomposers belong to the
recipe/source declarations. Operational schedules and completion thresholds may
select these sources, but must resolve their names through this catalog.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent


def recipe_root() -> Path:
    explicit = os.environ.get("LAPLACE_RECIPES")
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    runtime = Path(os.environ.get("LAPLACE_INGEST_RUNTIME", str(prefix / "app" / "cli")))
    candidates = [Path(explicit)] if explicit else [
        runtime / "native" / "recipe-engine" / "share" / "laplace" / "recipes",
        prefix / "share" / "laplace" / "recipes",
        Path(os.environ.get("LAPLACE_SRC", ROOT.parent)) / "Laplace-Engine" / "recipes",
    ]
    for candidate in candidates:
        if (candidate / "order").is_file():
            return candidate
    raise ValueError(f"Native recipe estate has no order declaration: {candidates}; declare LAPLACE_RECIPES")


def declarations(path: Path) -> dict[str, list[str]]:
    result: dict[str, list[str]] = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = re.split(r"(?:^|\s)#", line, maxsplit=1)[0].strip()
        if not line:
            continue
        key, _, value = line.partition(" ")
        if "\t" in key:
            key, value = line.split(None, 1)
        result.setdefault(key, []).append(value.strip())
    return result


def words(fields: dict[str, list[str]], key: str) -> list[str]:
    return [word for line in fields.get(key, []) for word in line.split()]


def recipe(path: Path, root: Path) -> dict:
    fields = declarations(path)
    grammar = fields.get("grammar", [""])[0]
    if "format" in fields:
        name = fields["format"][0]
        if not re.fullmatch(r"[a-zA-Z0-9_-]+", name):
            raise ValueError(f"Invalid format name: {name}")
        grammar = declarations(root / "formats" / f"{name}.format").get("grammar", [grammar])[0]
    return {"name": fields.get("name", [path.stem])[0],
            "grammar": grammar,
            "like": fields.get("like", [""])[0], "match": words(fields, "match")}


def load_catalog(root: Path | None = None) -> dict:
    root = root or recipe_root()
    sources = {}
    accepted = {}
    formats = [recipe(path, root) for path in sorted(root.glob("*.recipe"))]
    for path in sorted(root.glob("*/source")):
        fields = declarations(path)
        name = fields.get("name", [""])[0]
        if name != path.parent.name or not re.fullmatch(r"[a-z0-9][a-z0-9-]*", name):
            raise ValueError(f"Invalid source name: {path}")
        aliases = words(fields, "aliases")
        for key in [name, *aliases]:
            if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", key) or key in accepted:
                raise ValueError(f"Invalid or duplicate source name or alias: {key}")
            accepted[key] = name
        sources[name] = {
            "name": name, "aliases": aliases,
            "witness": fields.get("witness", [""])[0],
            "lineage": fields.get("lineage", [""])[0],
            "held": fields.get("held", [""])[0],
            "roots": fields.get("root", []), "files": fields.get("files", []),
            "after": words(fields, "after"), "reads": words(fields, "reads"),
            "recipes": [recipe(p, root) for p in sorted(path.parent.glob("*.recipe"))],
        }
    order = [line.split("#", 1)[0].strip() for line in (root / "order").read_text(encoding="utf-8-sig").splitlines()]
    order = [name for name in order if name]
    if not order or len(order) != len(set(order)) or set(order) - sources.keys():
        raise ValueError("recipes/order is empty, duplicated or names an undeclared source")
    earlier = set()
    for name in order:
        if set(sources[name]["after"]) - earlier:
            raise ValueError(f"Source '{name}' has dependencies missing or later in recipes/order")
        earlier.add(name)
    # The Engine also admits declared sources not yet selected by the default
    # campaign. Append them in dependency order instead of hiding them from CLI.
    while len(order) < len(sources):
        ready = [name for name, source in sources.items()
                 if name not in earlier and set(source["after"]) <= earlier]
        if not ready:
            raise ValueError("Sources have missing or cyclic dependencies")
        name = ready[0]
        order.append(name)
        earlier.add(name)
    all_recipes = {r["name"]: r for s in sources.values() for r in s["recipes"]}
    all_recipes.update({r["name"]: r for r in formats})

    def inherited(r: dict, active: set[str]) -> None:
        if not r["like"]:
            return
        parent = r["like"]
        if parent not in all_recipes or parent in active:
            raise ValueError(f"Missing or cyclic recipe inheritance: {r['name']} -> {parent}")
        inherited(all_recipes[parent], active | {parent})
        r["grammar"] = all_recipes[parent]["grammar"]
        r["like"] = ""

    for r in all_recipes.values():
        inherited(r, {r["name"]})
    return {"sources": [sources[name] for name in order], "formats": formats}


def canonical_source(name: str, catalog: dict | None = None) -> str:
    key = name.lower()
    for source in (catalog or load_catalog())["sources"]:
        if key == source["name"] or key in source["aliases"]:
            return source["name"]
    return key  # explicit managed/operator commands are resolved by their owner


def markdown(catalog: dict) -> str:
    lines = ["## Ingestion catalog", "", "| Command | Source / witness | Content | Decomposers | After |",
             "|---|---|---|---|---|"]
    for s in catalog["sources"]:
        command = s["aliases"][0] if s["aliases"] else s["name"]
        names = ", ".join([s["name"], *s["aliases"]])
        formats = ", ".join(sorted({r["grammar"] or r["name"] for r in s["recipes"]} | set(s["reads"])))
        content = "; ".join(s["files"] or s["roots"]) or s["held"]
        cells = [f"`{command}`", f"{names}; {s['witness']}", content, formats, ", ".join(s["after"])]
        lines.append("| " + " | ".join(c.replace("|", "&#124;") for c in cells) + " |")
    lines.extend(["", "## File decomposers", "", "| Format | Grammar | Matches |", "|---|---|---|"])
    for r in catalog["formats"]:
        lines.append(f"| {r['name']} | {r['grammar']} | {', '.join(r['match'])} |")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recipes", type=Path)
    parser.add_argument("--format", choices=("json", "markdown", "names"), default="json")
    parser.add_argument("--resolve")
    args = parser.parse_args()
    catalog = load_catalog(args.recipes)
    if args.resolve:
        print(canonical_source(args.resolve, catalog))
    elif args.format == "names":
        print("\n".join(s["name"] for s in catalog["sources"]))
    elif args.format == "markdown":
        print(markdown(catalog))
    else:
        print(json.dumps(catalog, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
