#!/usr/bin/env python3
"""Replace legacy substrate/type/X/v1 canonical names with bare X in seed SQL."""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SEED = ROOT / "extension/laplace_substrate/sql/seed/canonical_names_seed.sql.in"

def main() -> int:
    text = SEED.read_text(encoding="utf-8")
    new = re.sub(r"\('substrate/type/([^']+)/v1'\)", r"('\1')", text)
    if new == text:
        print("no changes needed")
        return 0
    before = text.count("substrate/type/")
    after = new.count("substrate/type/")
    SEED.write_text(new, encoding="utf-8", newline="\n")
    print(f"migrated {before - after} entries ({before} -> {after} remaining)")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
