#!/usr/bin/env python3
"""Decide whether a newer main revision invalidates an exact product candidate.

The law matches Product — main delivery's trigger exclusions: documentation,
workflow/policy files, and explicit diagnostics-only sources do not invalidate an
already qualified product candidate. Any other repository change does.
"""
from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path

EXACT_IGNORES = {
    "scripts/api-diagnostics.py",
    "web/scripts/capture-ui-diagnostics.mjs",
}


def ignored(path: str) -> bool:
    return (
        path.startswith(".github/")
        or path.startswith("docs/")
        or path.endswith(".md")
        or path in EXACT_IGNORES
    )


def changed_files(root: Path, base: str, head: str) -> list[str]:
    result = subprocess.run(
        [
            "git",
            "diff",
            "--name-only",
            "--diff-filter=ACMRTD",
            f"{base}..{head}",
        ],
        cwd=root,
        check=True,
        text=True,
        capture_output=True,
    )
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def product_delta(paths: list[str]) -> dict:
    relevant = [path for path in paths if not ignored(path)]
    ignored_paths = [path for path in paths if ignored(path)]
    return {
        "product_equivalent": not relevant,
        "product_paths": relevant,
        "ignored_paths": ignored_paths,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", required=True)
    parser.add_argument("--json-output")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    try:
        paths = changed_files(root, args.base, args.head)
    except subprocess.CalledProcessError as error:
        print(
            json.dumps(
                {
                    "product_equivalent": False,
                    "error": f"git diff failed with exit {error.returncode}",
                    "base": args.base,
                    "head": args.head,
                },
                sort_keys=True,
            )
        )
        return 2

    result = {
        "base": args.base,
        "head": args.head,
        "changed_files": paths,
        **product_delta(paths),
    }
    encoded = json.dumps(result, sort_keys=True)
    print(encoded)
    if args.json_output:
        Path(args.json_output).write_text(encoded + "\n", encoding="utf-8")
    return 0 if result["product_equivalent"] else 3


if __name__ == "__main__":
    raise SystemExit(main())
