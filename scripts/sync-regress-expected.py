#!/usr/bin/env python3
"""Sync pg_regress expected/*.out with current test SQL (schema drift cleanup)."""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXPECTED_DIRS = (
    ROOT / "extension/laplace_substrate/tests/expected",
    ROOT / "extension/laplace_geom/tests/expected",
)


def scrub(text: str) -> str:
    text = text.replace(
        "INSERT INTO physicalities (id, entity_id, source_id, type, coord, hilbert_index,",
        "INSERT INTO physicalities (id, entity_id, type, coord, hilbert_index,",
    )
    text = re.sub(
        r"(VALUES \(laplace_hash128_blake3\('test/[^']+'\)), ([a-z_0-9]+), src, 1,",
        r"\1, \2, 1,",
        text,
    )
    # Drop standalone documentation comment lines not echoed by psql.
    text = re.sub(r"^-- [^\n]+\n(?=SELECT |INSERT |DO |CREATE |SET |BEGIN|ROLLBACK)", "", text, flags=re.M)
    return text


def main() -> int:
    changed = 0
    for directory in EXPECTED_DIRS:
        if not directory.is_dir():
            continue
        for path in sorted(directory.glob("*.out")):
            old = path.read_text(encoding="utf-8")
            new = scrub(old)
            if new != old:
                path.write_text(new, encoding="utf-8", newline="\n")
                print(f"updated {path.relative_to(ROOT)}")
                changed += 1
    print(f"done: {changed} file(s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
