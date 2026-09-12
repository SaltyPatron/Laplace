#!/usr/bin/env python3
"""Digest the live files named by this build's CMake install manifest."""
import hashlib
import json
from pathlib import Path
import sys


def installed_digest(manifest):
    paths = sorted(set(manifest.read_text().splitlines()))
    if not paths:
        raise ValueError("empty install manifest")
    records = []
    for name in paths:
        path = Path(name)
        if not path.is_absolute():
            raise ValueError("install manifest contains a relative path")
        content = hashlib.sha256()
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                content.update(block)
        records.append((name, str(path.readlink()) if path.is_symlink() else None,
                        content.hexdigest()))
    return hashlib.sha256(json.dumps(records, separators=(",", ":")).encode()).hexdigest()


if __name__ == "__main__":
    try:
        print(installed_digest(Path(sys.argv[1])))
    except (OSError, ValueError) as error:
        print(f"installed artifact verification: {error}", file=sys.stderr)
        sys.exit(1)
