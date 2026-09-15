#!/usr/bin/env python3
"""Retain bounded repair metadata for one exact deployed source generation."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat

METADATA_BYTES = 64 * 1024
MAX_DIRECTORIES = 1024
NAMES = ("resources.json", "measurement.json", "manifest.json", "submission.json",
         "failure.json", "outcome.json", "reconciliation.json")
SOURCE_RECORDS = ("measurement.json", "manifest.json", "failure.json", "outcome.json")


def read_metadata(path: Path) -> tuple[bytes, dict] | None:
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    except FileNotFoundError:
        return None
    with os.fdopen(descriptor, "rb") as source:
        before = os.fstat(source.fileno())
        if not stat.S_ISREG(before.st_mode) or before.st_size > METADATA_BYTES:
            raise ValueError("repair metadata is not a bounded regular file")
        raw = source.read(METADATA_BYTES + 1)
        after = os.fstat(source.fileno())
    if len(raw) > METADATA_BYTES or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError("repair metadata changed during collection")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("repair metadata must be an object")
    return raw, value


def collect(root: Path, output: Path, source_sha: str) -> dict:
    if not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise ValueError("expected source must be one full commit identity")
    output.mkdir(parents=True, exist_ok=False)
    result = {"schema": "laplace.legacy-content-repair-metadata-collection/v1",
              "expected_source_sha": source_sha, "receipt_root": str(root),
              "metadata_bytes_per_file": METADATA_BYTES, "maximum_directories": MAX_DIRECTORIES,
              "directories_inspected": 0, "receipts": [], "status": "complete"}
    try:
        if root.exists():
            with os.scandir(root) as entries:
                for entry in entries:
                    if not entry.is_dir(follow_symlinks=False):
                        continue
                    result["directories_inspected"] += 1
                    if result["directories_inspected"] > MAX_DIRECTORIES:
                        raise ValueError("repair metadata estate exceeds collection bound")
                    directory = Path(entry.path)
                    records = {name: read_metadata(directory / name) for name in SOURCE_RECORDS}
                    identities = {item[1].get("source_sha") for item in records.values() if item is not None}
                    if source_sha not in identities:
                        continue
                    if identities != {source_sha}:
                        raise ValueError("repair metadata contains conflicting source identities")
                    destination = output / entry.name
                    destination.mkdir()
                    receipt = {"directory": entry.name, "source_sha": source_sha, "files": []}
                    result["receipts"].append(receipt)
                    for name in NAMES:
                        item = records[name] if name in records else read_metadata(directory / name)
                        if item is None:
                            continue
                        raw, _ = item
                        (destination / name).write_bytes(raw)
                        receipt["files"].append({"name": name, "bytes": len(raw),
                                                 "sha256": hashlib.sha256(raw).hexdigest()})
    except (OSError, ValueError, RecursionError) as error:
        result.update(status="incomplete", error_type=type(error).__name__)
    (output / "collection.json").write_text(json.dumps(result, sort_keys=True) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt-root", type=Path, default=Path("/build/laplace/recovery/legacy-content-repair"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-source", required=True)
    args = parser.parse_args()
    result = collect(args.receipt_root, args.output, args.expected_source)
    print(json.dumps({"status": result["status"], "source_sha": args.expected_source,
                      "retained_receipts": len(result["receipts"]), "collection": str(args.output)}, sort_keys=True))
    return 0 if result["status"] == "complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
