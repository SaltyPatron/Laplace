#!/usr/bin/env python3
"""Resolve safe default worker points for managed-host benchmark scaling.

The benchmark harnesses can accept explicit worker counts. This planner owns the
workflow default so a managed host does not silently benchmark by consuming every
logical CPU and starving PostgreSQL, the runner, monitoring, or the product itself.

A full-logical-CPU point is still available, but only when saturation is explicitly
allowed. The resolved plan is emitted as machine-readable evidence.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
from typing import Any


def _read_int(path: Path, fallback: int) -> int:
    try:
        return int(path.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return fallback


def topology() -> tuple[int, int, list[int], list[dict[str, Any]]]:
    allowed = (
        sorted(os.sched_getaffinity(0))
        if hasattr(os, "sched_getaffinity")
        else list(range(os.cpu_count() or 1))
    )
    groups: dict[tuple[int, int], list[int]] = {}
    for cpu in allowed:
        root = Path(f"/sys/devices/system/cpu/cpu{cpu}/topology")
        package = _read_int(root / "physical_package_id", 0)
        core = _read_int(root / "core_id", cpu)
        groups.setdefault((package, core), []).append(cpu)

    ordered = [
        {
            "package": package,
            "core": core,
            "logical_cpus": sorted(cpus),
        }
        for (package, core), cpus in sorted(groups.items())
    ]
    return len(ordered), len(allowed), allowed, ordered


def serviceable_cap(logical: int, reserve_logical: int) -> int:
    if logical < 1:
        raise ValueError("logical CPU count must be positive")
    if reserve_logical < 0:
        raise ValueError("reserved logical CPU count cannot be negative")
    if logical == 1:
        return 1
    return max(1, logical - min(reserve_logical, logical - 1))


def default_points(physical: int, logical: int, reserve_logical: int, allow_saturation: bool) -> list[int]:
    if physical < 1 or logical < 1 or physical > logical:
        raise ValueError(f"invalid topology physical={physical} logical={logical}")

    cap = logical if allow_saturation else serviceable_cap(logical, reserve_logical)
    values = {
        1,
        min(2, cap),
        min(3, cap),
        min(4, cap),
        min(physical, cap),
        cap,
    }
    if cap > physical:
        # One SMT midpoint plus the serviceable ceiling gives a useful curve without
        # assuming a fixed 6C/12T host. 6C/12T with reserve=2 -> 1,2,3,4,6,8,10.
        extra = cap - physical
        values.add(min(cap, physical + math.ceil(extra / 2)))
    return sorted(value for value in values if value > 0)


def parse_points(text: str, logical: int) -> list[int]:
    values: list[int] = []
    for raw in text.split(","):
        raw = raw.strip()
        if not raw:
            continue
        value = int(raw)
        if value < 1 or value > logical:
            raise ValueError(f"worker count {value} is outside 1..{logical}")
        values.append(value)
    if not values:
        raise ValueError("worker list is empty")
    return sorted(set(values))


def resolve_points(
    physical: int,
    logical: int,
    requested: str | None,
    reserve_logical: int,
    allow_saturation: bool,
) -> tuple[list[int], int, str]:
    cap = logical if allow_saturation else serviceable_cap(logical, reserve_logical)
    if requested and requested.strip():
        points = parse_points(requested, logical)
        if not allow_saturation and max(points) > cap:
            raise ValueError(
                f"requested worker point {max(points)} exceeds serviceable ceiling {cap}; "
                "reduce --workers or pass --allow-saturation explicitly"
            )
        source = "explicit"
    else:
        points = default_points(physical, logical, reserve_logical, allow_saturation)
        source = "derived"
    return points, cap, source


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workers", help="optional comma-separated worker counts")
    parser.add_argument("--reserve-logical", type=int, default=2)
    parser.add_argument("--allow-saturation", action="store_true")
    parser.add_argument("--github-env", help="append LAPLACE_BENCH_SCALE_WORKERS to this file")
    parser.add_argument("--json", dest="json_path", help="write machine-readable scale plan")
    args = parser.parse_args()

    physical, logical, allowed, topo = topology()
    try:
        points, cap, source = resolve_points(
            physical,
            logical,
            args.workers,
            args.reserve_logical,
            args.allow_saturation,
        )
    except ValueError as exc:
        parser.error(str(exc))

    mode = "saturation-allowed" if args.allow_saturation else "serviceable-headroom"
    resolved = ",".join(map(str, points))
    receipt = {
        "schema": "laplace.benchmark.scale-plan/v1",
        "mode": mode,
        "source": source,
        "requested_workers": args.workers or "",
        "resolved_workers": points,
        "resolved_workers_csv": resolved,
        "physical_cores": physical,
        "allowed_logical_cpus": logical,
        "allowed_cpu_ids": allowed,
        "topology": topo,
        "reserve_logical_cpus": 0 if args.allow_saturation else args.reserve_logical,
        "serviceable_worker_ceiling": cap,
        "full_saturation_worker_count": logical,
    }

    if args.json_path:
        path = Path(args.json_path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.github_env:
        with Path(args.github_env).open("a", encoding="utf-8") as handle:
            handle.write(f"LAPLACE_BENCH_SCALE_WORKERS={resolved}\n")
            handle.write(f"LAPLACE_BENCH_SCALE_MODE={mode}\n")
            handle.write(f"LAPLACE_BENCH_SCALE_CEILING={cap}\n")

    print(
        f"BENCHMARK_SCALE_PLAN mode={mode} physical={physical} logical={logical} "
        f"reserve={receipt['reserve_logical_cpus']} ceiling={cap} workers={resolved}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
