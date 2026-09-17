#!/usr/bin/env python3
"""Measure fixed-work scaling inside one exact semantic content DAG.

Unlike file-grain makespan and replicated independent-stream profiles, this harness
holds semantic work constant: one exact UTF-8 object is built at every worker point
through content_witness_tree_build_workers. Speedup is reported only after the complete
native tree state matches the 1-worker oracle byte-for-byte.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
SCALE_PATH = ROOT / "scripts/bench-compose-scale.py"


def _load_scale_module():
    spec = importlib.util.spec_from_file_location("laplace_bench_compose_scale", SCALE_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot load shared scaling harness: {SCALE_PATH}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


scale = _load_scale_module()


class Hash128(ctypes.Structure):
    _fields_ = [("bytes", ctypes.c_ubyte * 16)]


def load_native(core: str, t0: str):
    lib = scale._load_native(core, t0)
    lib.content_witness_tree_build_workers.argtypes = [
        ctypes.c_char_p, ctypes.c_size_t, ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_void_p),
    ]
    lib.content_witness_tree_build_workers.restype = ctypes.c_int
    lib.content_witness_tree_root_id.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(Hash128)
    ]
    lib.content_witness_tree_root_id.restype = ctypes.c_int

    for name in (
        "tier_tree_tier_array",
        "tier_tree_first_child_idx_array",
        "tier_tree_child_count_array",
        "tier_tree_parent_idx_array",
        "tier_tree_atom_array",
        "tier_tree_text_off_array",
        "tier_tree_text_len_array",
        "tier_tree_id_array",
        "tier_tree_coord_array",
        "tier_tree_hilbert_array",
    ):
        fn = getattr(lib, name)
        fn.argtypes = [ctypes.c_void_p]
        fn.restype = ctypes.c_void_p
    return lib


def exact_tree_receipt(lib, tree: ctypes.c_void_p) -> dict[str, Any]:
    count = int(lib.tier_tree_node_count(tree))
    root = Hash128()
    if lib.content_witness_tree_root_id(tree, ctypes.byref(root)) != 0:
        raise RuntimeError("content_witness_tree_root_id failed")

    digest = hashlib.sha256()
    digest.update(b"laplace.single-dag-tree/v1\0")
    digest.update(count.to_bytes(8, "little", signed=False))

    fields = (
        ("tier", "tier_tree_tier_array", count),
        ("first_child", "tier_tree_first_child_idx_array", count * 4),
        ("child_count", "tier_tree_child_count_array", count * 4),
        ("parent", "tier_tree_parent_idx_array", count * 4),
        ("atom", "tier_tree_atom_array", count * 4),
        ("text_off", "tier_tree_text_off_array", count * 4),
        ("text_len", "tier_tree_text_len_array", count * 4),
        ("id", "tier_tree_id_array", count * 16),
        ("coord", "tier_tree_coord_array", count * 4 * 8),
        ("hilbert", "tier_tree_hilbert_array", count * 16),
    )
    for label, symbol, size in fields:
        digest.update(label.encode("ascii") + b"\0")
        ptr = getattr(lib, symbol)(tree)
        if size and not ptr:
            raise RuntimeError(f"{symbol} returned null for {count} nodes")
        if size:
            digest.update(ctypes.string_at(ptr, size))

    return {
        "node_count": count,
        "root_id": bytes(root.bytes).hex(),
        "tree_fingerprint_sha256": digest.hexdigest(),
        "tree_fingerprint_scope": [
            "tier", "first_child", "child_count", "parent", "atom",
            "text_off", "text_len", "id", "coord_binary64", "hilbert128",
        ],
    }


def choose_input(corpus_dir: Path, explicit: Path | None) -> tuple[bytes, str]:
    if explicit is not None:
        payload = explicit.read_bytes()
        if not payload:
            raise ValueError(f"benchmark input is empty: {explicit}")
        return payload, str(explicit.resolve())

    docs = scale.load_corpus(str(corpus_dir.resolve()))
    if not docs:
        raise ValueError(f"no benchmark corpus found under {corpus_dir}")
    payload = max(docs, key=len)
    return payload, "largest-document-from-bounded-core-corpus"


def build_once(
    lib,
    payload: bytes,
    workers: int,
    cpus: list[int],
    original_affinity: set[int] | None,
) -> tuple[int, dict[str, Any]]:
    if workers < 1:
        raise ValueError("workers must be positive")
    admitted = cpus[:workers]
    if len(admitted) != workers:
        raise ValueError(f"worker grant {workers} exceeds admitted CPU list")
    if original_affinity is not None:
        os.sched_setaffinity(0, set(admitted))

    tree = ctypes.c_void_p()
    try:
        start = time.perf_counter_ns()
        rc = lib.content_witness_tree_build_workers(
            payload, len(payload), workers, ctypes.byref(tree)
        )
        wall_ns = time.perf_counter_ns() - start
        if rc != 0 or not tree.value:
            raise RuntimeError(
                f"content_witness_tree_build_workers(workers={workers}) returned {rc}"
            )
        receipt = exact_tree_receipt(lib, tree)
        return wall_ns, receipt
    finally:
        if tree.value:
            lib.tier_tree_free(tree)
        if original_affinity is not None:
            os.sched_setaffinity(0, original_affinity)


def run_point(
    lib,
    payload: bytes,
    workers: int,
    cpus: list[int],
    repeats: int,
    oracle: dict[str, Any],
    original_affinity: set[int] | None,
) -> dict[str, Any]:
    # One untimed build removes first-touch/library noise from the timing points while
    # still enforcing semantic parity before any measurement is accepted.
    _, warm = build_once(lib, payload, workers, cpus, original_affinity)
    if warm != oracle:
        raise RuntimeError(
            f"semantic drift at {workers} workers during warmup: "
            f"oracle={oracle} actual={warm}"
        )

    runs: list[dict[str, Any]] = []
    for repeat in range(repeats):
        wall_ns, semantic = build_once(
            lib, payload, workers, cpus, original_affinity
        )
        if semantic != oracle:
            raise RuntimeError(
                f"semantic drift at {workers} workers repeat {repeat + 1}: "
                f"oracle={oracle} actual={semantic}"
            )
        runs.append({
            "repeat": repeat + 1,
            "wall_nanoseconds": wall_ns,
            "semantic": semantic,
        })

    best = min(runs, key=lambda item: int(item["wall_nanoseconds"]))
    return {
        "workers": workers,
        "affinity_cpus": cpus[:workers],
        "runs": runs,
        "best_wall_nanoseconds": int(best["wall_nanoseconds"]),
        "semantic": oracle,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus_dir", nargs="?", default=str(ROOT))
    parser.add_argument("--input", type=Path, help="exact UTF-8 object; default selects the largest bounded corpus document")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--workers", help="comma-separated worker grants; default derives physical-core/SMT points")
    parser.add_argument("--json", dest="json_path")
    parser.add_argument("--core", default=os.environ.get("LAPLACE_CORE", str(scale.DEFAULT_CORE)))
    parser.add_argument("--t0", default=os.environ.get("LAPLACE_T0", str(scale.DEFAULT_T0)))
    args = parser.parse_args()

    if args.repeats < 1 or args.repeats > 100:
        parser.error("--repeats must be in 1..100")

    core = Path(args.core).resolve()
    t0 = Path(args.t0).resolve()
    if not core.is_file():
        parser.error(f"core library not found: {core}")
    if not t0.is_file():
        parser.error(f"T0 perfcache not found: {t0}")
    if args.input is not None and not args.input.is_file():
        parser.error(f"input not found: {args.input}")

    scale.refuse_if_ingest_advancing()
    payload, input_label = choose_input(Path(args.corpus_dir), args.input)
    input_sha = hashlib.sha256(payload).hexdigest()
    codepoints = len(payload.decode("utf-8", "replace"))

    ordered_cpus, topology = scale.cpu_order()
    physical = len(topology)
    logical = len(ordered_cpus)
    counts = scale.parse_worker_counts(args.workers, physical, logical)
    if 1 not in counts:
        counts = [1, *counts]

    original_affinity = (
        set(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None
    )
    lib = load_native(str(core), str(t0))

    # Scalar oracle is not timed. Every point, including workers=1, must reproduce it.
    _, oracle = build_once(lib, payload, 1, ordered_cpus, original_affinity)
    points = [
        run_point(
            lib, payload, workers, ordered_cpus, args.repeats,
            oracle, original_affinity,
        )
        for workers in counts
    ]

    baseline = next(point for point in points if int(point["workers"]) == 1)
    baseline_ns = int(baseline["best_wall_nanoseconds"])
    for point in points:
        wall_ns = int(point["best_wall_nanoseconds"])
        speedup = baseline_ns / wall_ns
        point.update({
            "codepoints_per_second": codepoints * 1_000_000_000 / wall_ns,
            "tier_tree_nodes_per_second": int(oracle["node_count"]) * 1_000_000_000 / wall_ns,
            "speedup_vs_1_worker": speedup,
            "parallel_efficiency": speedup / int(point["workers"]),
        })

    print(f"input       : {input_label}")
    print(f"bytes       : {len(payload):,}")
    print(f"sha256      : {input_sha}")
    print(f"codepoints  : {codepoints:,}")
    print(f"nodes       : {oracle['node_count']:,}")
    print(f"root        : {oracle['root_id']}")
    print(f"tree sha256 : {oracle['tree_fingerprint_sha256']}")
    print(f"topology    : {physical} physical core(s), {logical} allowed logical CPU(s)")
    print("mode        : single-semantic-dag-frontier")
    print()
    print("workers  best_s    Mcp/s  Mnodes/s  speedup  efficiency  affinity")
    for point in points:
        print(
            f"{int(point['workers']):7d}  "
            f"{int(point['best_wall_nanoseconds']) / 1e9:6.3f}  "
            f"{float(point['codepoints_per_second']) / 1e6:7.3f}  "
            f"{float(point['tier_tree_nodes_per_second']) / 1e6:8.3f}  "
            f"{float(point['speedup_vs_1_worker']):7.3f}  "
            f"{float(point['parallel_efficiency']):10.3f}  "
            f"{','.join(map(str, point['affinity_cpus']))}"
        )

    receipt = {
        "schema": "laplace.benchmark.core-dag-scale/v1",
        "scaling_mode": "single-semantic-dag-frontier",
        "work_held_fixed": True,
        "semantic_objects_per_repeat": 1,
        "physical_grain": "dependency-frontier nodes inside one canonical tier tree",
        "timing_boundary": "one exact in-memory UTF-8 object through native content_witness_tree_build_workers; library/perfcache loaded; fingerprinting outside timed interval",
        "input_label": input_label,
        "input_sha256": input_sha,
        "input_bytes": len(payload),
        "input_codepoints": codepoints,
        "semantic_oracle": oracle,
        "core_library": str(core),
        "t0_perfcache": str(t0),
        "physical_cores": physical,
        "allowed_logical_cpus": logical,
        "cpu_order_physical_first": ordered_cpus,
        "topology": topology,
        "repeats": args.repeats,
        "points": points,
    }
    if args.json_path:
        destination = Path(args.json_path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(
            json.dumps(receipt, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
