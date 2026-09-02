#!/usr/bin/env python3
"""Measure aggregate independent-stream scaling of the native composition core.

This is deliberately different from ``bench-compose-scale.py``.

``bench-compose-scale.py`` measures the makespan for ONE finite corpus where each
source document is an indivisible work item. That is the right scheduler/batch
measurement, but a single very large document can become the critical path and make
additional idle workers look like native-core serialization.

This harness measures the other legitimate question: how much independent composition
traffic can the host execute concurrently? Every pinned worker receives one complete,
identical real-corpus stream per repeat. No document is split and no single-thread rate
is multiplied after the fact: every reported codepoint and node is actually processed
by ``content_witness_tree_build`` inside the measured interval.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import multiprocessing as mp
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


def run_stream_point(worker_count: int, cpus: list[int], repeats: int) -> dict[str, Any]:
    """Run one full corpus independently on every worker.

    Total measured work therefore scales with worker_count. This is intentional: the
    benchmark measures concurrent service capacity, not time-to-finish one finite batch.
    """
    indices = list(range(len(scale._DOCS)))
    runs: list[dict[str, Any]] = []
    context = mp.get_context("fork")

    for repeat in range(repeats):
        start_event = context.Event()
        ready_queue = context.Queue()
        result_queue = context.Queue()
        processes = [
            context.Process(
                target=scale._worker,
                args=(cpus[index], indices, start_event, ready_queue, result_queue),
            )
            for index in range(worker_count)
        ]
        for process in processes:
            process.start()

        ready = [ready_queue.get(timeout=120) for _ in processes]
        wall_start = time.perf_counter_ns()
        start_event.set()
        results = [result_queue.get(timeout=1800) for _ in processes]
        wall_ns = time.perf_counter_ns() - wall_start

        for process in processes:
            process.join(timeout=30)
            if process.is_alive():
                process.kill()
                process.join()

        errors = [result for result in results if not result.get("ok")]
        if errors:
            raise RuntimeError(f"worker failure(s): {errors}")
        failures = sum(int(result["failures"]) for result in results)
        if failures:
            raise RuntimeError(f"native composition rejected {failures} document(s)")

        per_worker_nodes = sorted(int(result["nodes"]) for result in results)
        if len(set(per_worker_nodes)) != 1:
            raise RuntimeError(f"worker node-count drift at {worker_count} workers: {per_worker_nodes}")

        runs.append({
            "repeat": repeat + 1,
            "wall_nanoseconds": wall_ns,
            "nodes": sum(per_worker_nodes),
            "nodes_per_worker": per_worker_nodes[0],
            "ready_workers": sorted(ready, key=lambda row: int(row["cpu"])),
            "worker_results": sorted(results, key=lambda row: int(row["cpu"])),
        })

    best = min(runs, key=lambda run: int(run["wall_nanoseconds"]))
    return {
        "workers": worker_count,
        "cpus": cpus[:worker_count],
        "runs": runs,
        "best_wall_nanoseconds": int(best["wall_nanoseconds"]),
        "nodes": int(best["nodes"]),
        "nodes_per_worker": int(best["nodes_per_worker"]),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus_dir", nargs="?", default=str(ROOT))
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--workers", help="comma-separated worker counts; default derives physical-core/SMT points")
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

    scale.refuse_if_ingest_advancing()
    scale._DOCS = scale.load_corpus(str(Path(args.corpus_dir).resolve()))
    scale._CORE = str(core)
    scale._T0 = str(t0)
    if not scale._DOCS:
        raise SystemExit(f"no benchmark corpus found under {args.corpus_dir}")

    corpus_bytes = sum(len(document) for document in scale._DOCS)
    corpus_codepoints = sum(len(document.decode("utf-8", "replace")) for document in scale._DOCS)
    largest_document_bytes = max(len(document) for document in scale._DOCS)
    top_document_bytes = sorted((len(document) for document in scale._DOCS), reverse=True)[:10]

    ordered_cpus, topology = scale.cpu_order()
    physical = len(topology)
    logical = len(ordered_cpus)
    counts = scale.parse_worker_counts(args.workers, physical, logical)

    print(f"corpus      : {len(scale._DOCS):,} documents, {corpus_bytes/1e6:.1f} MB, {corpus_codepoints:,} codepoints / worker")
    print(f"largest doc : {largest_document_bytes/1e6:.1f} MB ({largest_document_bytes/corpus_bytes:.1%} of one corpus stream)")
    print(f"core        : {core}")
    print(f"t0          : {t0}")
    print(f"topology    : {physical} physical core(s), {logical} allowed logical CPU(s)")
    print(f"mode        : replicated-independent-streams")
    print(f"worker pts  : {','.join(map(str, counts))}")

    points = [run_stream_point(count, ordered_cpus, args.repeats) for count in counts]
    baseline = next((point for point in points if int(point["workers"]) == 1), None)
    if baseline is None:
        raise RuntimeError("stream scaling requires a measured 1-worker baseline")

    baseline_rate = corpus_codepoints * 1_000_000_000 / int(baseline["best_wall_nanoseconds"])
    baseline_nodes = int(baseline["nodes_per_worker"])

    for point in points:
        workers = int(point["workers"])
        wall_ns = int(point["best_wall_nanoseconds"])
        total_codepoints = corpus_codepoints * workers
        expected_nodes = baseline_nodes * workers
        if int(point["nodes"]) != expected_nodes:
            raise RuntimeError(
                f"stream node-count drift: expected {expected_nodes} at {workers} workers, got {point['nodes']}"
            )
        cps = total_codepoints * 1_000_000_000 / wall_ns
        nps = expected_nodes * 1_000_000_000 / wall_ns
        speedup = cps / baseline_rate
        point.update({
            "corpus_streams_executed": workers,
            "total_documents_executed": len(scale._DOCS) * workers,
            "total_bytes_executed": corpus_bytes * workers,
            "total_codepoints_executed": total_codepoints,
            "codepoints_per_second": cps,
            "bpe_equivalent_tokens_per_second_4chars": cps / 4.0,
            "tier_tree_nodes_per_second": nps,
            "speedup_vs_1_worker": speedup,
            "parallel_efficiency": speedup / workers,
        })

    print()
    print("workers  best_s    Mcp/s   Mtok4/s  Mnodes/s  speedup  efficiency  cpus")
    for point in points:
        wall_s = int(point["best_wall_nanoseconds"]) / 1e9
        print(
            f"{int(point['workers']):7d}  {wall_s:6.3f}  "
            f"{float(point['codepoints_per_second'])/1e6:7.3f}  "
            f"{float(point['bpe_equivalent_tokens_per_second_4chars'])/1e6:8.3f}  "
            f"{float(point['tier_tree_nodes_per_second'])/1e6:8.3f}  "
            f"{float(point['speedup_vs_1_worker']):7.3f}  "
            f"{float(point['parallel_efficiency']):10.3f}  "
            f"{','.join(map(str, point['cpus']))}"
        )

    receipt = {
        "schema": "laplace.benchmark.core-scale-streams/v1",
        "scaling_mode": "replicated-independent-streams",
        "timing_boundary": "in-memory corpus; workers/perfcache ready; each worker executes one complete corpus through native content_witness_tree_build",
        "documents_per_worker": len(scale._DOCS),
        "corpus_root": str(Path(args.corpus_dir).resolve()),
        "corpus_bytes_per_worker": corpus_bytes,
        "codepoints_per_worker": corpus_codepoints,
        "largest_document_bytes": largest_document_bytes,
        "largest_document_fraction_of_corpus": largest_document_bytes / corpus_bytes,
        "top_document_bytes": top_document_bytes,
        "bpe_equivalence_chars_per_token": 4,
        "core_library": str(core),
        "t0_perfcache": str(t0),
        "repeats": args.repeats,
        "physical_cores": physical,
        "allowed_logical_cpus": logical,
        "cpu_order_physical_first": ordered_cpus,
        "topology": topology,
        "points": points,
    }

    if args.json_path:
        path = Path(args.json_path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
