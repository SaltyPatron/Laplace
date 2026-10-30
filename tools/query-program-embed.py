#!/usr/bin/env python3
"""Embed the canonical Engine's compiled producer declarations for PostgreSQL."""
import argparse
import json
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("engine", "recipes", "tier0", "firmware", "output"):
        parser.add_argument("--" + key, type=Path, required=True)
    args = parser.parse_args()
    env = dict(os.environ, LAPLACE_RECIPES=str(args.recipes), LAPLACE_TIER0=str(args.tier0))
    result = subprocess.run([str(args.engine), "firmware", "--operation", "pull",
                             "--firmware", str(args.firmware)], env=env,
                            capture_output=True, text=True, encoding="utf-8", check=True)
    program = json.loads(result.stdout)
    if not program.get("chains") or not program.get("up"):
        raise ValueError("The compiled firmware has no declared query paths")
    payload = json.dumps(program, ensure_ascii=True, separators=(",", ":"))
    content = "/* Compiled by Laplace-Engine from its firmware and producer recipes. */\n"
    content += "static const char laplace_compiled_query_program[] = " + json.dumps(payload) + ";\n"
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if not args.output.exists() or args.output.read_text(encoding="utf-8") != content:
        args.output.write_text(content, encoding="utf-8")


if __name__ == "__main__":
    main()
