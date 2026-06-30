#!/usr/bin/env python3
"""Refresh pg_regress expected/*.out after content-addressed relation type id migration."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

PATTERNS = [
    (
        re.compile(r"laplace_hash128_blake3\('substrate/type/([^']+)/v1'::bytea\)"),
        r"laplace_hash128_blake3('\1'::bytea)",
    ),
    (
        re.compile(r"laplace_hash128_blake3\('substrate/type/([^']+)/v1'\)"),
        r"laplace_hash128_blake3('\1')",
    ),
    (
        re.compile(r"register_canonical\('substrate/type/([^']+)/v1'\)"),
        r"register_canonical('\1')",
    ),
]


def refresh_dir(directory: Path) -> int:
    changed = 0
    for path in sorted(directory.glob("*.out")):
        text = path.read_text(encoding="utf-8")
        new = text
        for pattern, repl in PATTERNS:
            new = pattern.sub(repl, new)
        if new != text:
            path.write_text(new, encoding="utf-8", newline="\n")
            print(f"updated {path.relative_to(ROOT)}")
            changed += 1
    return changed


def main() -> int:
    total = 0
    for sub in (
        "extension/laplace_substrate/tests/expected",
        "extension/laplace_geom/tests/expected",
    ):
        d = ROOT / sub
        if d.is_dir():
            total += refresh_dir(d)
    print(f"done: {total} file(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
