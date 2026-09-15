#!/usr/bin/env python3
"""Retain bounded repair and owned service metadata for one source generation."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import stat

METADATA_BYTES = 64 * 1024
MAX_DIRECTORIES = 1024
MAX_DIRECTORY_ENTRIES = 1024
MAX_COLLECTION_BYTES = 128 * 1024 * 1024
NAMES = ("resources.json", "measurement.json", "manifest.json", "submission.json",
         "failure.json", "outcome.json", "reconciliation.json",
         "resource-admission.json", "completion-timing.json")
SOURCE_RECORDS = ("measurement.json", "manifest.json", "failure.json", "outcome.json")
QUIESCENCE_NAMES = frozenset((
    "maintenance-command.json", "prior-services.json", "producer-application-verification.json",
    "begin-submission.json", "begin-confirmed.json", "quiescence-confirmed.json",
    "repair-estate.json", "commit-submission.json", "commit-confirmed.json", "restored.json",
    *(f"stop-{service}-{phase}.json" for service in ("api", "mcp", "lichess")
      for phase in ("submission", "confirmed")),
    *(f"restore-{service}-confirmed.json" for service in ("api", "mcp", "lichess"))))
QUIESCENCE_DYNAMIC = re.compile(
    r"(?:repair-attempt|database-status|database-quiescence-held|resume-confirmed)-[0-9a-f]{32}\.json"
    r"|maintenance-resources-[0-9a-f]{32}(?:-usage|-current-readback-admission)?\.json")


@contextmanager
def directory_descriptor(path: Path):
    """Pin every directory component; no ancestor or receipt symlink is followed."""
    absolute = path.absolute()
    if ".." in absolute.parts:
        raise ValueError("metadata estate path cannot contain parent traversal")
    descriptor = os.open(absolute.anchor, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        for part in absolute.parts[1:]:
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                            dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        yield descriptor
    finally:
        os.close(descriptor)


def read_metadata(path: Path, *, dir_fd: int | None = None) -> tuple[bytes, dict] | None:
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=dir_fd)
    except FileNotFoundError:
        return None
    with os.fdopen(descriptor, "rb") as source:
        before = os.fstat(source.fileno())
        if not stat.S_ISREG(before.st_mode) or before.st_size > METADATA_BYTES:
            raise ValueError("repair metadata is not a bounded regular file")
        raw = source.read(METADATA_BYTES + 1)
        after = os.fstat(source.fileno())
    identity = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_size,
                              value.st_mtime_ns, value.st_ctime_ns)
    if len(raw) != before.st_size or len(raw) > METADATA_BYTES or identity(before) != identity(after):
        raise ValueError("repair metadata changed during collection")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("repair metadata must be an object")
    return raw, value


class MetadataReader:
    def __init__(self):
        self.bytes_read = 0

    def read(self, descriptor: int, name: str) -> tuple[bytes, dict] | None:
        if self.bytes_read + METADATA_BYTES > MAX_COLLECTION_BYTES:
            raise ValueError("repair metadata exceeds aggregate collection byte bound")
        item = read_metadata(Path(name), dir_fd=descriptor)
        if item is not None:
            self.bytes_read += len(item[0])
        return item


def entries(descriptor: int, limit: int) -> list[os.DirEntry]:
    observed = []
    with os.scandir(descriptor) as iterator:
        for entry in iterator:
            if len(observed) == limit:
                raise ValueError("repair metadata directory exceeds entry bound")
            observed.append(entry)
    return sorted(observed, key=lambda item: item.name)


def receipt_directories(root: Path, report: dict):
    try:
        context = directory_descriptor(root)
        descriptor = context.__enter__()
    except FileNotFoundError:
        return
    try:
        for entry in entries(descriptor, MAX_DIRECTORIES + MAX_DIRECTORY_ENTRIES):
            if entry.is_symlink():
                raise ValueError("repair metadata estate contains a symlink")
            if not entry.is_dir(follow_symlinks=False):
                continue
            report["directories_inspected"] += 1
            if report["directories_inspected"] > MAX_DIRECTORIES:
                raise ValueError("repair metadata estate exceeds collection bound")
            child = os.open(entry.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                            dir_fd=descriptor)
            try:
                yield entry.name, child
            finally:
                os.close(child)
    finally:
        context.__exit__(None, None, None)


def retain(destination: Path, records: dict, receipt: dict) -> None:
    destination.mkdir(parents=True, exist_ok=False)
    for name, item in sorted(records.items()):
        if item is None:
            continue
        raw, _ = item
        with (destination / name).open("xb") as target:
            target.write(raw)
        receipt["files"].append({"name": name, "bytes": len(raw),
                                 "sha256": hashlib.sha256(raw).hexdigest()})


def collect_quiescence(root: Path, output: Path, source_sha: str, repair_root: Path,
                       selected_repairs: set[str], report: dict, reader: MetadataReader) -> None:
    for name, descriptor in receipt_directories(root, report):
        records = {}
        for entry in entries(descriptor, MAX_DIRECTORY_ENTRIES):
            if entry.name in QUIESCENCE_NAMES or QUIESCENCE_DYNAMIC.fullmatch(entry.name):
                records[entry.name] = reader.read(descriptor, entry.name)
        matched_by = set()
        prior = records.get("prior-services.json")
        producer = prior[1].get("producer_generation") if prior is not None else None
        if isinstance(producer, dict) and producer.get("source_sha") == source_sha:
            matched_by.add("producer_generation")
        attempts = []
        for filename, item in records.items():
            if item is None:
                continue
            value = item[1]
            if filename.startswith("repair-attempt-") and value.get("repair_source_sha") == source_sha:
                if value.get("schema") != "laplace.quiescence-repair-attempt/v1":
                    raise ValueError("repair attempt has an unknown ownership schema")
                target = value.get("repair_directory")
                if not isinstance(target, str) or Path(target).parent != repair_root.absolute() \
                        or ".." in Path(target).parts:
                    raise ValueError("repair attempt names another receipt estate")
                matched_by.add("repair_attempt")
                attempts.append({"file": filename, "repair_directory": target,
                                 "repair_source_sha": source_sha})
            if filename.startswith("maintenance-resources-") \
                    and isinstance(value.get("current_receipt"), str) \
                    and value["current_receipt"] in selected_repairs:
                matched_by.add("current_receipt")
        if not matched_by:
            continue
        receipt = {"directory": name, "matched_by": sorted(matched_by),
                   "scope": "owned-service-transaction",
                   "producer_source_sha": producer.get("source_sha") if isinstance(producer, dict) else None,
                   "matching_attempts": attempts, "files": []}
        report["receipts"].append(receipt)
        retain(output / "quiescence" / name, records, receipt)


def collect(root: Path, output: Path, source_sha: str, *, quiescence_root: Path | None = None) -> dict:
    if not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise ValueError("expected source must be one full commit identity")
    quiescence_root = quiescence_root or root.parent / "legacy-content-service-quiescence"
    output.mkdir(parents=True, exist_ok=False)
    result = {"schema": "laplace.legacy-content-repair-metadata-collection/v1",
              "expected_source_sha": source_sha, "receipt_root": str(root),
              "metadata_bytes_per_file": METADATA_BYTES, "maximum_directories": MAX_DIRECTORIES,
              "maximum_directory_entries": MAX_DIRECTORY_ENTRIES,
              "maximum_collection_bytes": MAX_COLLECTION_BYTES,
              "directories_inspected": 0, "receipts": [], "status": "complete",
              "quiescence": {"receipt_root": str(quiescence_root),
                             "directories_inspected": 0, "receipts": []}}
    reader = MetadataReader()
    selected_repairs = set()
    # Collect both estates even if one is incomplete, preserving available failure evidence.
    for estate in ("repair", "quiescence"):
        try:
            if estate == "quiescence":
                collect_quiescence(quiescence_root, output, source_sha, root,
                                   selected_repairs, result["quiescence"], reader)
                continue
            for name, descriptor in receipt_directories(root, result):
                records = {name: reader.read(descriptor, name) for name in SOURCE_RECORDS}
                identities = [item[1].get("source_sha") for item in records.values() if item is not None]
                if source_sha not in identities:
                    continue
                if any(identity != source_sha for identity in identities):
                    raise ValueError("repair metadata contains conflicting source identities")
                selected_repairs.add(str(root.absolute() / name))
                receipt = {"directory": name, "source_sha": source_sha, "files": []}
                result["receipts"].append(receipt)
                records.update({name: reader.read(descriptor, name) for name in NAMES if name not in records})
                retain(output / name, records, receipt)
        except (OSError, ValueError, RecursionError) as error:
            result.update(status="incomplete", error_type=type(error).__name__)
            result.setdefault("errors", []).append({"estate": estate, "error_type": type(error).__name__})
    result["metadata_bytes_read"] = reader.bytes_read
    (output / "collection.json").write_text(json.dumps(result, sort_keys=True) + "\n", encoding="utf-8")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt-root", type=Path, default=Path("/build/laplace/recovery/legacy-content-repair"))
    parser.add_argument("--quiescence-root", type=Path,
                        help="owned service metadata estate (default: repair-root sibling)")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--expected-source", required=True)
    args = parser.parse_args()
    result = collect(args.receipt_root, args.output, args.expected_source,
                     quiescence_root=args.quiescence_root)
    print(json.dumps({"status": result["status"], "source_sha": args.expected_source,
                      "retained_receipts": len(result["receipts"]),
                      "retained_quiescence_receipts": len(result["quiescence"]["receipts"]),
                      "collection": str(args.output)}, sort_keys=True))
    return 0 if result["status"] == "complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
