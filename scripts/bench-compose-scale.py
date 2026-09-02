#!/usr/bin/env python3
"""Measure whole-machine scaling of the native content composition core.

The historical `bench-compose.py` intentionally measures one thread. This harness
measures the aggregate instead of multiplying that result by a core count.

Boundary:
  * real bounded corpus is read into memory before timing;
  * every worker loads the exact requested liblaplace_core + T0 perfcache before timing;
  * each document is composed exactly once per scaling point/repeat;
  * workers are pinned to explicit logical CPUs;
  * physical cores are populated before SMT siblings;
  * no database/COPY/network work occurs inside the measured interval.

The output is a scaling receipt, not a claim that every Laplace operation has this
complexity or throughput.
"""
from __future__ import annotations

import argparse
import ctypes
import glob
import json
import math
import multiprocessing as mp
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CORE = ROOT / "build/engine/core/liblaplace_core.so"
DEFAULT_T0 = ROOT / "build/engine/core/perfcache/laplace_t0_perfcache.bin"
CORPUS_CAP_BYTES = 48 << 20
SKIP_DIRS = ("/build/", "/.git/", "/external/", "/node_modules/", "/bin/", "/obj/")

_DOCS: list[bytes] = []
_CORE = ""
_T0 = ""


def _load_native(core: str, t0: str):
    lib = ctypes.CDLL(core)
    lib.codepoint_table_load_perfcache.argtypes = [ctypes.c_char_p]
    lib.codepoint_table_load_perfcache.restype = ctypes.c_int
    lib.codepoint_table_is_loaded.restype = ctypes.c_int
    lib.content_witness_tree_build.argtypes = [
        ctypes.c_char_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_void_p)
    ]
    lib.content_witness_tree_build.restype = ctypes.c_int
    lib.tier_tree_free.argtypes = [ctypes.c_void_p]
    lib.tier_tree_node_count.argtypes = [ctypes.c_void_p]
    lib.tier_tree_node_count.restype = ctypes.c_size_t
    if lib.codepoint_table_load_perfcache(t0.encode()) != 0 or not lib.codepoint_table_is_loaded():
        raise RuntimeError(f"could not load T0 perfcache at {t0}")
    return lib


def load_corpus(root: str, cap: int = CORPUS_CAP_BYTES) -> list[bytes]:
    paths: list[str] = []
    for pattern in ("**/*.md", "**/*.txt", "**/*.cs", "**/*.c", "**/*.h"):
        for path in glob.glob(os.path.join(root, pattern), recursive=True):
            normalized = path.replace("\\", "/")
            if any(part in normalized for part in SKIP_DIRS):
                continue
            paths.append(path)

    docs: list[bytes] = []
    total = 0
    for path in sorted(set(paths)):
        try:
            with open(path, "rb") as handle:
                payload = handle.read()
        except OSError:
            continue
        if not payload:
            continue
        docs.append(payload)
        total += len(payload)
        if total >= cap:
            break
    return docs


def _sample_ingest_progress() -> int | None:
    try:
        completed = subprocess.run(
            [
                "psql", "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
                "-U", os.environ.get("PGUSER", "laplace_admin"),
                "-d", os.environ.get("PGDATABASE", "laplace"), "-tAc",
                "SELECT coalesce(sum(input_units_done),0) FROM laplace.ingest_run_journal "
                "WHERE status = 'running'",
            ],
            capture_output=True,
            text=True,
            timeout=15,
        )
        text = completed.stdout.strip()
        return int(text) if completed.returncode == 0 and text.isdigit() else None
    except Exception:
        return None


def refuse_if_ingest_advancing() -> None:
    if os.environ.get("LAPLACE_BENCH_ALLOW_BUSY") == "1":
        return
    first = _sample_ingest_progress()
    if first is None or first == 0:
        return
    try:
        active = subprocess.run(
            [
                "psql", "-h", os.environ.get("PGHOST", "/var/run/postgresql"),
                "-U", os.environ.get("PGUSER", "laplace_admin"),
                "-d", os.environ.get("PGDATABASE", "laplace"), "-tAc",
                "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() "
                "AND state='active' AND pid<>pg_backend_pid() AND query ~* "
                "'consensus\\.upsert|COPY laplace\\.|highway_mask_deposit|"
                "attestations_exist|physicalities_exist'",
            ],
            capture_output=True,
            text=True,
            timeout=15,
        )
        if active.returncode == 0 and active.stdout.strip().isdigit() and int(active.stdout.strip()) > 0:
            raise SystemExit(
                f"bench-compose-scale: refusing — {active.stdout.strip()} ingest backend(s) active; "
                "set LAPLACE_BENCH_ALLOW_BUSY=1 to override deliberately"
            )
    except subprocess.TimeoutExpired:
        pass
    time.sleep(6)
    second = _sample_ingest_progress()
    if second is not None and second > first:
        raise SystemExit(
            f"bench-compose-scale: refusing — ingest advanced {first} -> {second}; "
            "set LAPLACE_BENCH_ALLOW_BUSY=1 to override deliberately"
        )


def _read_int(path: Path, fallback: int) -> int:
    try:
        return int(path.read_text(encoding="utf-8").strip())
    except (OSError, ValueError):
        return fallback


def cpu_order() -> tuple[list[int], list[dict[str, object]]]:
    allowed = sorted(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else list(range(os.cpu_count() or 1))
    groups: dict[tuple[int, int], list[int]] = {}
    for cpu in allowed:
        topo = Path(f"/sys/devices/system/cpu/cpu{cpu}/topology")
        package = _read_int(topo / "physical_package_id", 0)
        core = _read_int(topo / "core_id", cpu)
        groups.setdefault((package, core), []).append(cpu)

    ordered_groups = [(key, sorted(value)) for key, value in sorted(groups.items())]
    primary = [cpus[0] for _, cpus in ordered_groups]
    siblings: list[int] = []
    lane = 1
    while True:
        added = False
        for _, cpus in ordered_groups:
            if lane < len(cpus):
                siblings.append(cpus[lane])
                added = True
        if not added:
            break
        lane += 1
    order = primary + siblings
    topology = [
        {"package": key[0], "core": key[1], "logical_cpus": cpus, "primary_cpu": cpus[0]}
        for key, cpus in ordered_groups
    ]
    return order, topology


def default_worker_counts(physical: int, logical: int) -> list[int]:
    values = {1, min(2, logical), min(3, logical), min(4, logical), physical, logical}
    if logical > physical:
        extra = logical - physical
        values.add(min(logical, physical + math.ceil(extra / 3)))
        values.add(min(logical, physical + math.ceil(2 * extra / 3)))
    return sorted(value for value in values if value > 0)


def parse_worker_counts(text: str | None, physical: int, logical: int) -> list[int]:
    if not text:
        return default_worker_counts(physical, logical)
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


def partition_docs(worker_count: int) -> tuple[list[list[int]], list[int]]:
    shards: list[list[int]] = [[] for _ in range(worker_count)]
    loads = [0] * worker_count
    for index in sorted(range(len(_DOCS)), key=lambda item: len(_DOCS[item]), reverse=True):
        slot = min(range(worker_count), key=lambda candidate: loads[candidate])
        shards[slot].append(index)
        loads[slot] += len(_DOCS[index])
    return shards, loads


def _worker(cpu: int, indices: list[int], start_event, ready_queue, result_queue) -> None:
    try:
        if hasattr(os, "sched_setaffinity"):
            os.sched_setaffinity(0, {cpu})
        lib = _load_native(_CORE, _T0)
        ready_queue.put({"cpu": cpu, "pid": os.getpid(), "documents": len(indices)})
        start_event.wait()
        nodes = 0
        failures = 0
        for index in indices:
            payload = _DOCS[index]
            tree = ctypes.c_void_p()
            if lib.content_witness_tree_build(payload, len(payload), ctypes.byref(tree)) != 0:
                failures += 1
                continue
            nodes += int(lib.tier_tree_node_count(tree))
            lib.tier_tree_free(tree)
        result_queue.put({"ok": True, "cpu": cpu, "nodes": nodes, "failures": failures})
    except BaseException as exc:  # child must report the reason before dying
        result_queue.put({
            "ok": False,
            "cpu": cpu,
            "error": f"{type(exc).__name__}: {exc}",
            "traceback": traceback.format_exc(),
        })


def run_point(worker_count: int, cpus: list[int], repeats: int) -> dict[str, object]:
    shards, byte_loads = partition_docs(worker_count)
    runs: list[dict[str, object]] = []
    context = mp.get_context("fork")

    for repeat in range(repeats):
        start_event = context.Event()
        ready_queue = context.Queue()
        result_queue = context.Queue()
        processes = [
            context.Process(target=_worker, args=(cpus[index], shards[index], start_event, ready_queue, result_queue))
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
        runs.append({
            "repeat": repeat + 1,
            "wall_nanoseconds": wall_ns,
            "nodes": sum(int(result["nodes"]) for result in results),
            "workers": ready,
        })

    best = min(runs, key=lambda run: int(run["wall_nanoseconds"]))
    return {
        "workers": worker_count,
        "cpus": cpus[:worker_count],
        "shard_bytes": byte_loads,
        "runs": runs,
        "best_wall_nanoseconds": int(best["wall_nanoseconds"]),
        "nodes": int(best["nodes"]),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("corpus_dir", nargs="?", default=str(ROOT))
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--workers", help="comma-separated worker counts; default derives physical-core/SMT points")
    parser.add_argument("--json", dest="json_path")
    parser.add_argument("--core", default=os.environ.get("LAPLACE_CORE", str(DEFAULT_CORE)))
    parser.add_argument("--t0", default=os.environ.get("LAPLACE_T0", str(DEFAULT_T0)))
    args = parser.parse_args()

    if args.repeats < 1 or args.repeats > 100:
        parser.error("--repeats must be in 1..100")
    core = Path(args.core).resolve()
    t0 = Path(args.t0).resolve()
    if not core.is_file():
        parser.error(f"core library not found: {core}")
    if not t0.is_file():
        parser.error(f"T0 perfcache not found: {t0}")

    refuse_if_ingest_advancing()
    global _DOCS, _CORE, _T0
    _DOCS = load_corpus(str(Path(args.corpus_dir).resolve()))
    _CORE = str(core)
    _T0 = str(t0)
    if not _DOCS:
        raise SystemExit(f"no benchmark corpus found under {args.corpus_dir}")

    total_bytes = sum(len(document) for document in _DOCS)
    total_codepoints = sum(len(document.decode("utf-8", "replace")) for document in _DOCS)
    ordered_cpus, topology = cpu_order()
    physical = len(topology)
    logical = len(ordered_cpus)
    counts = parse_worker_counts(args.workers, physical, logical)

    print(f"corpus      : {len(_DOCS):,} documents, {total_bytes/1e6:.1f} MB, {total_codepoints:,} codepoints")
    print(f"core        : {core}")
    print(f"t0          : {t0}")
    print(f"topology    : {physical} physical core(s), {logical} allowed logical CPU(s)")
    print(f"worker pts  : {','.join(map(str, counts))}")

    points = [run_point(count, ordered_cpus, args.repeats) for count in counts]
    baseline = next((point for point in points if int(point["workers"]) == 1), None)
    baseline_rate = None
    if baseline is not None:
        baseline_rate = total_codepoints * 1_000_000_000 / int(baseline["best_wall_nanoseconds"])

    expected_nodes = None
    for point in points:
        wall_ns = int(point["best_wall_nanoseconds"])
        cps = total_codepoints * 1_000_000_000 / wall_ns
        nps = int(point["nodes"]) * 1_000_000_000 / wall_ns
        speedup = cps / baseline_rate if baseline_rate else None
        efficiency = speedup / int(point["workers"]) if speedup is not None else None
        point.update({
            "codepoints_per_second": cps,
            "bpe_equivalent_tokens_per_second_4chars": cps / 4.0,
            "tier_tree_nodes_per_second": nps,
            "speedup_vs_1_worker": speedup,
            "parallel_efficiency": efficiency,
        })
        if expected_nodes is None:
            expected_nodes = int(point["nodes"])
        elif int(point["nodes"]) != expected_nodes:
            raise RuntimeError(
                f"node-count drift across scaling points: {expected_nodes} vs {point['nodes']}"
            )

    print()
    print("workers  best_s    Mcp/s   Mtok4/s  Mnodes/s  speedup  efficiency  cpus")
    for point in points:
        wall_s = int(point["best_wall_nanoseconds"]) / 1e9
        speedup = point["speedup_vs_1_worker"]
        efficiency = point["parallel_efficiency"]
        print(
            f"{int(point['workers']):7d}  {wall_s:6.3f}  "
            f"{float(point['codepoints_per_second'])/1e6:7.3f}  "
            f"{float(point['bpe_equivalent_tokens_per_second_4chars'])/1e6:8.3f}  "
            f"{float(point['tier_tree_nodes_per_second'])/1e6:8.3f}  "
            f"{speedup if speedup is not None else float('nan'):7.3f}  "
            f"{efficiency if efficiency is not None else float('nan'):10.3f}  "
            f"{','.join(map(str, point['cpus']))}"
        )

    receipt = {
        "schema": "laplace.benchmark.core-scale/v1",
        "timing_boundary": "in-memory corpus; workers/perfcache ready; native content_witness_tree_build only",
        "corpus_root": str(Path(args.corpus_dir).resolve()),
        "documents": len(_DOCS),
        "corpus_bytes": total_bytes,
        "codepoints": total_codepoints,
        "bpe_equivalence_chars_per_token": 4,
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
        destination.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
