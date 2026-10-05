#!/usr/bin/env python3
"""Print the source name a seed step's run journals under.

The CLI selects a recipe (recipes/**/*.source.json, "selected": true) by its aliases, by its sourceName, and by the
built-in registry key whose decomposer class the recipe replaces (SourceGenerationCatalog); otherwise the registry
decomposer runs and journals under its class name. The seed step's legacy map names that class (second argument), so
a recipe whose sourceName is that class name is the one the step runs.
    recipe-source-name.py <step> [<legacy class name>]"""
import json, pathlib, sys
step = sys.argv[1].lower(); fallback = sys.argv[2] if len(sys.argv) > 2 else ""
root = pathlib.Path(__file__).resolve().parents[1] / "recipes"
recipes = []
for manifest in sorted(root.rglob("*.source.json")):
    try: recipe = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, ValueError): continue
    if recipe.get("selected", False): recipes.append(recipe)
for recipe in recipes:
    if step in {a.lower() for a in recipe.get("aliases") or []} or step == recipe["sourceName"].lower():
        print(recipe["sourceName"]); sys.exit(0)
for recipe in recipes:
    if fallback and recipe["sourceName"].lower() == fallback.lower():
        print(recipe["sourceName"]); sys.exit(0)
print(fallback)
