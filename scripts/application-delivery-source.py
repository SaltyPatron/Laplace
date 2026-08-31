#!/usr/bin/env python3
"""Decide whether a completed full pipeline still owes application delivery.

The main pipeline may finish red because environment QA failed after the exact build,
native/managed DEV proof, engine install, and database lifecycle all succeeded. That is
useful evidence, but it is not authority to keep serving an older API/SPA payload.

This selector consumes the producing workflow's job list. It authorizes the separate
application CD transaction only when every prerequisite mutation/proof phase succeeded
and the producing run's publish job was skipped. A failed publish is never retried here;
activation recovery remains owned by the producing run.
"""
from __future__ import annotations

import argparse
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

REQUIRED_JOBS = (
    "Build — engine, extensions, app, perfcache",
    "Unit tests — native engine and managed ABI",
    "Deploy — stage install to /opt/laplace",
    "DB — migrate, extension sync, perfcache GUC",
)
PUBLISH_JOB = "Publish — API + SPA, restart service"


@dataclass(frozen=True)
class Decision:
    deliver: bool
    reason: str


def _conclusions(jobs: Iterable[dict[str, Any]]) -> dict[str, str | None]:
    result: dict[str, str | None] = {}
    for job in jobs:
        name = job.get("name")
        if isinstance(name, str):
            result[name] = job.get("conclusion")
    return result


def decide(payload: dict[str, Any], source_event: str) -> Decision:
    if source_event != "push":
        return Decision(False, f"source event {source_event!r} is not a main push")

    jobs = payload.get("jobs")
    if not isinstance(jobs, list):
        raise ValueError("workflow jobs payload has no jobs array")

    total = payload.get("total_count")
    if isinstance(total, int) and total > len(jobs):
        raise ValueError(
            f"workflow jobs payload is incomplete ({len(jobs)} of {total}); refusing a partial delivery decision"
        )

    conclusions = _conclusions(jobs)
    failed_prerequisites = [
        f"{name}={conclusions.get(name, 'missing')}"
        for name in REQUIRED_JOBS
        if conclusions.get(name) != "success"
    ]
    if failed_prerequisites:
        return Decision(False, "prerequisites not proven: " + ", ".join(failed_prerequisites))

    publish = conclusions.get(PUBLISH_JOB)
    if publish == "success":
        return Decision(False, "producing run already delivered the application payload")
    if publish == "skipped":
        return Decision(
            True,
            "build/unit/install/database lifecycle succeeded; producing publish was skipped by downstream QA",
        )
    if publish is None:
        return Decision(False, "producing run has no publish job result")
    return Decision(False, f"producing publish concluded {publish!r}; activation failure is not auto-retried")


def _write_github_output(path: Path, decision: Decision) -> None:
    reason = decision.reason.replace("\r", " ").replace("\n", " ")
    with path.open("a", encoding="utf-8") as stream:
        stream.write(f"deliver={'true' if decision.deliver else 'false'}\n")
        stream.write(f"reason={reason}\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--jobs", required=True, type=Path)
    parser.add_argument("--event", required=True)
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args(argv)

    payload = json.loads(args.jobs.read_text(encoding="utf-8"))
    decision = decide(payload, args.event)
    print(
        "APPLICATION_DELIVERY "
        f"deliver={'true' if decision.deliver else 'false'} reason={decision.reason}"
    )
    if args.github_output is not None:
        _write_github_output(args.github_output, decision)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
