#!/usr/bin/env python3
"""Run every existing live-product suite and report the complete failure vector.

This is deliberately not another test definition. It imports the canonical test-profile
registry, executes the suites already classified as `profile=live`, and does not stop at
the first failure. The resulting receipt answers which installed/seeded product surfaces
are red in the same run instead of hiding later failures behind the first broken lane.
"""
from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import shlex
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
REGISTRY_MODULE = ROOT / "scripts" / "test-profile-registry.py"
REGISTRY_PATH = ROOT / "scripts" / "test-profiles.json"
RECEIPT_PATH = ROOT / "build" / "test-receipts" / "live.json"

spec = importlib.util.spec_from_file_location("laplace_test_profile_registry", REGISTRY_MODULE)
if spec is None or spec.loader is None:
    raise SystemExit("cannot load canonical test-profile registry")
registry = importlib.util.module_from_spec(spec)
spec.loader.exec_module(registry)


def main() -> int:
    suites = registry.load_validated(REGISTRY_PATH)
    chosen = registry.suites_for_request(suites, "live")
    started_wall = time.time()
    records: list[dict[str, object]] = []
    runnable: list[tuple[dict[str, object], dict[str, object]]] = []
    failed = False

    # Discovery is part of the result, not a reason to hide every later suite.
    for suite in chosen:
        record: dict[str, object] = {
            "id": suite["id"],
            "profile": suite["profile"],
            "runner": suite["runner"],
            "discovered": 0,
            "selected": 0,
            "executed": 0,
            "skipped": 0,
            "status": "selected",
            "elapsed_ms": 0,
        }
        records.append(record)
        try:
            discovered, _ = registry.discovered_count(suite)
        except registry.RegistryError as exc:
            record["status"] = "failed-discovery"
            record["error"] = str(exc)
            failed = True
            print(f"live-profile: discovery failed for {suite['id']}: {exc}", file=sys.stderr)
            continue

        record["discovered"] = discovered
        record["selected"] = 0 if suite["runner"] == "dotnet" else discovered
        if suite["required"] and discovered == 0:
            record["status"] = "failed-zero-discovery"
            failed = True
            print(
                f"live-profile: required suite {suite['id']} discovered=0",
                file=sys.stderr,
            )
            continue
        runnable.append((suite, record))

    # Execute every runnable live suite even after another live suite is red.
    for suite, record in runnable:
        command = registry.command_for_suite(suite)
        print(f"==== {suite['id']}: {shlex.join(command)} ====")
        rc, output, elapsed_ms = registry._run(
            command,
            registry._env_for_suite(suite),
            registry.TIMEOUT_SECONDS[suite["timeout_class"]],
        )
        sys.stdout.write(output)
        record["elapsed_ms"] = elapsed_ms
        zero_selected = False
        try:
            executed, skipped = registry._result_counts(suite, int(record["selected"]), output)
            if suite["runner"] == "dotnet":
                record["selected"] = executed + skipped
                zero_selected = suite["required"] and record["selected"] == 0
                if zero_selected:
                    rc = 1
                    print(
                        f"live-profile: required suite {suite['id']} filtered selection=0",
                        file=sys.stderr,
                    )
        except registry.RegistryError as exc:
            executed, skipped, rc = 0, 0, 1
            record["error"] = str(exc)
            print(f"live-profile: {suite['id']}: {exc}", file=sys.stderr)

        record["executed"] = executed
        record["skipped"] = skipped
        if zero_selected:
            record["status"] = "failed-zero-selection"
        else:
            record["status"] = "success" if rc == 0 else "failed"
        if rc != 0:
            failed = True

    status = "failed" if failed else "success"
    receipt = registry._finish_receipt("live", started_wall, records, status)
    # Expose the whole vector explicitly so consumers do not infer success from counts.
    receipt["failed_suites"] = [
        str(item["id"]) for item in records if item["status"] != "success"
    ]
    registry._write_receipt(RECEIPT_PATH, receipt)
    registry._append_summary(receipt)
    print(
        "LIVE_PRODUCT_VECTOR "
        f"status={status} failed={json.dumps(receipt['failed_suites'])} "
        f"executed={receipt['executed']} skipped={receipt['skipped']} "
        f"receipt={RECEIPT_PATH}"
    )
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
