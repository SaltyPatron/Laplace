#!/usr/bin/env python3
"""Retain bounded, existing recursive-proof JSON without querying or changing the substrate."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import socket
import stat
import time


PROOF_SCHEMA = "laplace.proof.live-recursive-substrate/v1"
RECEIPT_NAME = "live-recursive-substrate.json"
MAX_BYTES = 2 * 1024 * 1024
OBSERVED_MAIN_RECEIPT = Path(
    "/build/laplace/build/legacy-d32928234be8f570/test-receipts/" + RECEIPT_NAME
)


def collect(path: Path, output: Path, expected_source: str, index: int) -> dict:
    result = {"requested_path": str(path), "max_bytes": MAX_BYTES}
    try:
        # A checkout's build directory may be the normal permanent-build symlink.
        # The receipt itself must be a regular file, never a link or a pipe.
        result["resolved_path"] = str(path.resolve())
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        with os.fdopen(descriptor, "rb") as stream:
            before = os.fstat(stream.fileno())
            if not stat.S_ISREG(before.st_mode):
                result["disposition"] = "not-a-regular-file"
                return result
            if before.st_size > MAX_BYTES:
                result["disposition"] = "exceeds-byte-bound"
                result["size_bytes"] = before.st_size
                return result
            data = stream.read(MAX_BYTES + 1)
            after = os.fstat(stream.fileno())
        if len(data) > MAX_BYTES:
            result["disposition"] = "exceeds-byte-bound"
            return result
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            result["disposition"] = "changed-during-read"
            return result
        value = json.loads(data)
        if not isinstance(value, dict) or value.get("schema") != PROOF_SCHEMA:
            result["disposition"] = "not-a-recursive-proof-receipt"
            return result
        name = f"receipt-{index:02d}.json"
        (output / name).write_bytes(data)
        result.update({
            "disposition": "retained",
            "artifact_file": name,
            "sha256": hashlib.sha256(data).hexdigest(),
            "size_bytes": len(data),
            "mtime_unix_nanoseconds": before.st_mtime_ns,
            "device": before.st_dev,
            "inode": before.st_ino,
            "receipt_source_sha": value.get("source_sha"),
            "receipt_source_matches_expected": value.get("source_sha") == expected_source,
            "proof_ok": value.get("ok"),
        })
    except FileNotFoundError:
        result["disposition"] = "absent"
    except (OSError, ValueError, RecursionError) as error:
        # Invalid payloads and arbitrary exception text are not copied into logs.
        result.update({"disposition": "unreadable-or-invalid", "error_type": type(error).__name__})
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkout", type=Path, action="append", default=[])
    parser.add_argument("--build-directory", type=Path, action="append", default=[])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--proof-outcome", default="unknown")
    args = parser.parse_args()
    os.umask(0o002)
    args.output.mkdir(parents=True, exist_ok=False)
    candidates = [checkout.absolute() / "build/test-receipts" / RECEIPT_NAME
                  for checkout in args.checkout]
    candidates.extend(directory.absolute() / "test-receipts" / RECEIPT_NAME
                      for directory in args.build_directory)
    candidates.append(OBSERVED_MAIN_RECEIPT)
    candidates = list(dict.fromkeys(candidates))
    if len(candidates) > 12:
        raise ValueError("at most twelve explicitly selected receipt paths are allowed")
    manifest = {
        "schema": "laplace.recursive-proof-diagnostic-collection/v1",
        "collected_unix_nanoseconds": time.time_ns(),
        "expected_source_sha": args.expected_source,
        "preceding_proof_outcome": args.proof_outcome,
        "scope": "Existing proof receipts only; collection does not execute a proof or establish candidate/live correctness.",
        "hostname": socket.gethostname(),
        "github": {key: os.environ.get(key) for key in (
            "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB", "RUNNER_NAME"
        )},
        "collector_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "receipts": [collect(path, args.output, args.expected_source, index)
                     for index, path in enumerate(candidates)],
    }
    manifest_path = args.output / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    retained = sum(item["disposition"] == "retained" for item in manifest["receipts"])
    print(f"RECURSIVE_PROOF_EVIDENCE candidates={len(candidates)} retained={retained} manifest={manifest_path}")


if __name__ == "__main__":
    main()
