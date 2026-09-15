#!/usr/bin/env python3
"""Measure scheduled legacy-repair receipt verification from bounded metadata.

This reads manifests, outcomes, references and (only for reconciliation) the first
context record. It never hashes or streams complete plans, opens PostgreSQL, or
authorizes repair. The existing repair verifier remains authoritative.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import stat

METADATA_BYTES = 64 * 1024
MAX_DIRECTORIES = 1024
MAX_REPORT_BYTES = 4 * 1024 * 1024
LEGACY_BYTES = 512 * 1024 * 1024
LEGACY_LINE_BYTES = 2 * 1024 * 1024


def identity(info: os.stat_result) -> tuple:
    return (info.st_dev, info.st_ino, info.st_mode, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def positive(value: object) -> int:
    if type(value) is not int or value <= 0:
        raise ValueError("expected a positive integer bound or plan size")
    return value


def metadata(path: Path) -> dict:
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, "rb") as source:
        before = os.fstat(source.fileno())
        if not stat.S_ISREG(before.st_mode) or before.st_size > METADATA_BYTES:
            raise ValueError("metadata is not a bounded regular file")
        raw = source.read(METADATA_BYTES + 1)
        after = os.fstat(source.fileno())
    if len(raw) > METADATA_BYTES or identity(before) != identity(after):
        raise ValueError("metadata changed during measurement")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("metadata must be an object")
    return value


def confirmed(directory: Path, manifest: dict) -> bool:
    try:
        outcome = metadata(directory / "outcome.json")
    except (OSError, ValueError):
        return False
    return outcome.get("disposition") == "commit-confirmed" \
        and outcome.get("plan_sha256") == manifest["plan_sha256"] \
        and outcome.get("planned_rows") == manifest["planned_rows"] \
        and isinstance(outcome.get("applied"), dict) \
        and outcome["applied"].get("count") == manifest["planned_rows"]


def measure(root: Path, *, max_bytes: int = 4 * 1024**3,
            max_line_bytes: int = LEGACY_LINE_BYTES,
            max_prior_bytes: int = LEGACY_BYTES) -> dict:
    for limit in (max_bytes, max_line_bytes, max_prior_bytes):
        positive(limit)
    root = root.resolve()
    result = {"schema": "laplace.legacy-content-repair-history/v1", "receipt_root": str(root),
        "status": "complete", "scope": "Scheduled full-plan verification bytes across discovery, authenticated replay projection and closure, conditional on the existing verifier accepting retained bytes; no complete payload hashes were checked.",
        "metadata_bytes_per_file": METADATA_BYTES, "maximum_directories": MAX_DIRECTORIES,
        "maximum_report_bytes": MAX_REPORT_BYTES, "directories_inspected": 0,
        "max_bytes": max_bytes, "max_line_bytes": max_line_bytes, "max_prior_bytes": max_prior_bytes,
        "plans": [], "discovery_reads": [], "pending_receipts": [], "errors": []}
    plans: dict[Path, tuple[dict, os.stat_result]] = {}

    def load(directory: Path) -> tuple[dict, os.stat_result]:
        if directory.parent != root or directory.is_symlink():
            raise ValueError("receipt is outside the estate or is a symlink")
        if directory in plans:
            return plans[directory]
        manifest = metadata(directory / "manifest.json")
        if manifest.get("schema") != "laplace.legacy-content-repair-plan/v1":
            raise ValueError("unknown retained plan schema")
        size = positive(manifest.get("plan_bytes"))
        recorded_bytes = positive(manifest.get("max_bytes", LEGACY_BYTES))
        recorded_line = positive(manifest.get("max_line_bytes", LEGACY_LINE_BYTES))
        if not re.fullmatch(r"[0-9a-f]{64}", str(manifest.get("plan_sha256", ""))):
            raise ValueError("retained plan hash is malformed")
        if type(manifest.get("planned_rows")) is not int or manifest["planned_rows"] < 0:
            raise ValueError("retained plan count is malformed")
        for field in ("max_rows", "max_native_inputs", "timeout_seconds", "persistence_timeout_seconds",
                      "idle_timeout_seconds", "psql_fetch_rows"):
            if field in manifest:
                positive(manifest[field])
        info = (directory / "plan.jsonl").lstat()
        if not stat.S_ISREG(info.st_mode) or info.st_size != size:
            raise ValueError("retained plan is not a regular file of its declared size")
        blockers = []
        if size > min(max_bytes, recorded_bytes):
            blockers.append("plan_bytes exceeds current or recorded complete-plan limit")
        plans[directory] = (manifest, info)
        result["plans"].append({"directory": directory.name, "plan_bytes": size,
            "recorded_max_bytes": recorded_bytes, "recorded_max_line_bytes": recorded_line,
            "effective_max_line_bytes": min(max_line_bytes, recorded_line, max_bytes, recorded_bytes, size),
            "plan_sha256": manifest["plan_sha256"], "blockers": blockers})
        return manifest, info

    def context(directory: Path, info: os.stat_result) -> dict:
        fd = os.open(directory / "plan.jsonl", os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        with os.fdopen(fd, "rb") as source:
            before = os.fstat(source.fileno())
            if identity(before) != identity(info):
                raise ValueError("reconciliation plan changed during measurement")
            raw = source.readline(METADATA_BYTES + 1)
            after = os.fstat(source.fileno())
        if len(raw) > METADATA_BYTES or not raw.endswith(b"\n") or identity(before) != identity(after):
            raise ValueError("reconciliation context is unbounded, incomplete or changed")
        value = json.loads(raw)
        if not isinstance(value, dict) or value.get("kind") != "context":
            raise ValueError("reconciliation plan context is missing")
        return value

    def scheduled(directory: Path, reason: str) -> tuple[dict, os.stat_result]:
        manifest, info = load(directory)
        result["discovery_reads"].append({"directory": directory.name,
            "reason": reason, "plan_bytes": manifest["plan_bytes"]})
        return manifest, info

    try:
        directories = []
        if root.exists():
            with os.scandir(root) as entries:
                for entry in entries:
                    if not entry.is_dir() and not entry.is_symlink():
                        continue
                    result["directories_inspected"] += 1
                    if result["directories_inspected"] > MAX_DIRECTORIES:
                        raise ValueError("receipt estate exceeds directory measurement bound")
                    directories.append(Path(entry.path))
        for directory in sorted(directories):
            if not (directory / "submission.json").exists():
                continue
            try:
                manifest, _ = scheduled(directory, "submitted-plan")
                if confirmed(directory, manifest):
                    continue
                reference_path = directory / "reconciliation.json"
                reconciled = False
                if reference_path.exists():
                    reference = metadata(reference_path)
                    target = Path(reference["reconciliation_directory"])
                    if target.resolve().parent != root or target.resolve() == directory \
                            or reference.get("original_plan_sha256") != manifest["plan_sha256"]:
                        raise ValueError("reconciliation reference does not identify this estate and plan")
                    target_manifest, target_info = scheduled(target, "reconciliation-target-for:" + directory.name)
                    if not confirmed(target, target_manifest) \
                            or target_manifest["plan_sha256"] != reference.get("reconciliation_plan_sha256"):
                        raise ValueError("reconciliation target lacks its matching confirmed outcome")
                    target_context = context(target, target_info)
                    reconciled = any(item.get("receipt") == str((directory / "plan.jsonl").resolve())
                        and item.get("disposition") in ("originals-confirmed", "prior-commit-confirmed", "zero-row-no-mutation")
                        for item in target_context.get("prior_submission_reconciliation", []))
                if not reconciled:
                    result["pending_receipts"].append(directory.name)
            except (OSError, ValueError, KeyError, TypeError, AttributeError, RecursionError) as error:
                result["errors"].append({"directory": directory.name, "error": str(error)[:512]})
    except (OSError, ValueError) as error:
        result["errors"].append({"error": str(error)[:512]})
    discovery = sum(item["plan_bytes"] for item in result["discovery_reads"])
    closure = sum(plans[root / name][0]["plan_bytes"] for name in result["pending_receipts"])
    # Each unresolved prior journal is authenticated again while producing its
    # compact replay, then again before its successful reconciliation closes.
    # The canonical executor charges every read to one maintenance-wide budget.
    replay = closure
    maintenance = discovery + replay + closure
    blockers = [item for item in result["plans"] if item["blockers"]]
    if result["errors"]:
        result["status"] = "incomplete"
    result.update(schedule_complete=not result["errors"],
        scheduled_discovery_plan_bytes=discovery, scheduled_closure_prior_plan_bytes=closure,
        scheduled_replay_authentication_plan_bytes=replay,
        required_max_prior_bytes=maintenance,
        prior_budget_scope="One aggregate allowance across discovery, replay authentication and closure; repeated reads are charged repeatedly.",
        discovery_read_count=len(result["discovery_reads"]), distinct_plan_count=len(plans),
        pending_count=len(result["pending_receipts"]),
        pending_limit_exceeded=len(result["pending_receipts"]) > 32,
        declared_prior_budget_fits=not result["errors"] and maintenance <= max_prior_bytes,
        declared_plan_size_bounds_fit=not result["errors"] and not blockers,
        plan_line_validation="Complete payload line validation is deferred to the existing verifier.",
        closure_new_plan_scope="The newly created plan is separately read under its own admitted plan limit and is not charged to the prior-receipt budget.")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt-root", type=Path, default=Path("/build/laplace/recovery/legacy-content-repair"))
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--max-bytes", type=int, default=4 * 1024**3)
    parser.add_argument("--max-line-bytes", type=int, default=LEGACY_LINE_BYTES)
    parser.add_argument("--max-prior-bytes", type=int, default=LEGACY_BYTES)
    args = parser.parse_args()
    report = measure(args.receipt_root, max_bytes=args.max_bytes,
        max_line_bytes=args.max_line_bytes, max_prior_bytes=args.max_prior_bytes)
    raw = (json.dumps(report, sort_keys=True) + "\n").encode("utf-8")
    if len(raw) > MAX_REPORT_BYTES:
        report = {"schema": report["schema"], "status": "incomplete", "schedule_complete": False,
            "error": "history report exceeds its output bound", "maximum_report_bytes": MAX_REPORT_BYTES}
        raw = (json.dumps(report, sort_keys=True) + "\n").encode("utf-8")
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    with args.receipt.open("xb") as output:
        output.write(raw)
        output.flush()
        os.fsync(output.fileno())
    fd = os.open(args.receipt.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
    print(json.dumps({"status": report["status"], "receipt": str(args.receipt)}, sort_keys=True))
    return 0 if report["status"] == "complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
