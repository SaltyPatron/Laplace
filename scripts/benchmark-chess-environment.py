#!/usr/bin/env python3
"""Measure this machine's chess tools under explicit CPU, memory and time budgets.

This calibrates the installed executables and the admitted environment. It does
not estimate Elo or extrapolate single-worker rates into whole-machine capacity.
Raw commands, transcripts, PGNs and a versioned report remain in --output-dir.
"""
from __future__ import annotations

import argparse
import ctypes
import datetime as dt
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import platform
import random
import re
import signal
import socket
import statistics
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
MIB = 1024 * 1024
SCHEMA = "laplace.benchmark.chess-environment/v1"


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / (name + ".py"))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def read(path):
    try:
        return Path(path).read_text().strip()
    except OSError:
        return None


def sha256(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(MIB), b""):
            value.update(block)
    return value.hexdigest()


def key_numbers(text):
    return {line.split()[0].rstrip(":"): int(line.split()[1])
            for line in (text or "").splitlines()
            if len(line.split()) >= 2 and line.split()[1].isdigit()}


def visible_cgroups(proc=Path("/proc")):
    """Map this process's v1/v2 memberships through its visible mount namespace."""
    memberships = {}
    for line in (read(proc / "self/cgroup") or "").splitlines():
        _, controllers, path = line.split(":", 2)
        for controller in controllers.split(","):
            memberships[controller] = path
    found = []
    for line in (read(proc / "self/mountinfo") or "").splitlines():
        left, _, right = line.partition(" - ")
        before, after = left.split(), right.split()
        if len(before) < 5 or not after or after[0] not in ("cgroup", "cgroup2"):
            continue
        root, mount = before[3], Path(before[4].replace("\\040", " "))
        controllers = [""] if after[0] == "cgroup2" else after[-1].split(",")
        for controller in controllers:
            if controller not in memberships or (controller and controller not in ("cpu", "memory", "cpuset")):
                continue
            membership = memberships[controller]
            relative = os.path.relpath(membership, root)
            if relative.startswith(".."):
                continue
            current = mount / relative
            while current.is_relative_to(mount):
                entry = {"version": 2 if not controller else 1, "controller": controller,
                         "path": str(current)}
                if entry not in found:
                    found.append(entry)
                if current == mount:
                    break
                current = current.parent
    return found


def cgroup_limits(groups):
    quotas, remaining, maximums, evidence = [], [], [], []
    for group in groups:
        path = Path(group["path"])
        item = dict(group)
        if group["version"] == 2:
            cpu, maximum, used = read(path / "cpu.max"), read(path / "memory.max"), read(path / "memory.current")
            if cpu:
                item["cpu.max"] = cpu
                quota, period = cpu.split()
                if quota != "max":
                    quotas.append(int(quota) / int(period))
        else:
            quota, period = read(path / "cpu.cfs_quota_us"), read(path / "cpu.cfs_period_us")
            maximum, used = read(path / "memory.limit_in_bytes"), read(path / "memory.usage_in_bytes")
            if quota and period and int(quota) > 0:
                quotas.append(int(quota) / int(period))
                item["cpu_quota_us"], item["cpu_period_us"] = int(quota), int(period)
        if maximum and maximum != "max" and int(maximum) < (1 << 60):
            maximums.append(int(maximum))
            remaining.append(max(0, int(maximum) - int(used or 0)))
            item["memory_limit_bytes"], item["memory_current_bytes"] = int(maximum), int(used or 0)
        for name in ("cpu.stat", "memory.events", "cpu.pressure", "memory.pressure", "cpuset.cpus.effective"):
            text = read(path / name)
            if text is not None:
                item[name] = text
        evidence.append(item)
    return {"cpu_quota": min(quotas) if quotas else None,
            "memory_limit_bytes": min(maximums) if maximums else None,
            "memory_available_bytes": min(remaining) if remaining else None,
            "ancestors": evidence}


def proc_namespace_matches():
    stat = read("/proc/self/stat")
    return bool(stat and stat.split()[0] == str(os.getpid()))


def machine():
    limitations = []
    cpu_text = read("/proc/cpuinfo") or ""
    models = sorted(set(re.findall(r"^model name\s*:\s*(.+)$", cpu_text, re.M)))
    memory = key_numbers(read("/proc/meminfo"))
    affinity = sorted(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None
    if os.name == "nt":
        class Memory(ctypes.Structure):
            _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong)] + [
                (name, ctypes.c_ulonglong) for name in
                ("total", "available", "page_total", "page_available", "virtual_total", "virtual_available", "extended")]
        status = Memory()
        status.length = ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            memory = {"MemTotal": status.total // 1024, "MemAvailable": status.available // 1024}
        mask, system_mask = ctypes.c_size_t(), ctypes.c_size_t()
        if ctypes.windll.kernel32.GetProcessAffinityMask(ctypes.windll.kernel32.GetCurrentProcess(), ctypes.byref(mask), ctypes.byref(system_mask)):
            affinity = [i for i in range(ctypes.sizeof(mask) * 8) if mask.value & (1 << i)]
        limitations.append("Windows processor-group and Job Object CPU/memory restrictions are not fully enumerated; supply explicit budgets for such hosts.")
        limitations.append("Windows process-tree RSS/CPU sampling and per-run affinity placement are unavailable; resource fields remain null and inherited affinity applies.")
    if affinity is None:
        affinity = list(range(os.cpu_count() or 1))
        limitations.append("Process affinity unavailable; logical CPU count is a fallback, not a measured affinity mask.")
    if platform.system() == "Linux" and not proc_namespace_matches():
        limitations.append("The visible /proc PID namespace differs from process API IDs; per-process /proc sampling is disabled to avoid attributing unrelated host processes.")
    groups = visible_cgroups() if platform.system() == "Linux" else []
    limits = cgroup_limits(groups)
    available = [memory["MemAvailable"] * 1024] if "MemAvailable" in memory else []
    if limits["memory_available_bytes"] is not None:
        available.append(limits["memory_available_bytes"])
    capacity = min(len(affinity), limits["cpu_quota"]) if limits["cpu_quota"] is not None else len(affinity)
    topology = {}
    for cpu in affinity:
        base = Path(f"/sys/devices/system/cpu/cpu{cpu}/topology")
        package, core = read(base / "physical_package_id"), read(base / "core_id")
        topology.setdefault((package or "unknown", core or str(cpu)), []).append(cpu)
    ordered = [cpus[index] for index in range(max(map(len, topology.values()), default=0))
               for cpus in topology.values() if index < len(cpus)]
    return {"hostname": socket.gethostname(), "platform": platform.platform(), "machine": platform.machine(),
            "virtualization_product": read("/sys/class/dmi/id/product_name"),
            "cpu_models": models or [platform.processor() or "unknown"], "logical_cpus_reported": os.cpu_count(),
            "affinity_cpu_ids": affinity, "physical_core_groups_within_affinity": list(topology.values()),
            "physical_first_cpu_order": ordered, "effective_cpu_capacity": capacity,
            "physical_memory_bytes": memory.get("MemTotal", 0) * 1024 or None,
            "proc_process_namespace_matches": proc_namespace_matches(),
            "available_memory_bytes": min(available) if available else None, "cgroup": limits,
            "load_average": os.getloadavg() if hasattr(os, "getloadavg") else None,
            "limitations": limitations}


def points(value, maximum, physical_boundary=None):
    if value:
        result = sorted(set(int(part) for part in value.split(",")))
    else:
        result, point = [1, maximum], 2
        while point < maximum:
            result.append(point)
            point *= 2
        if physical_boundary is not None and 1 <= physical_boundary <= maximum:
            result.append(physical_boundary)
        result = sorted(set(result))
    if not result or min(result) < 1 or max(result) > maximum:
        raise ValueError(f"requested points must lie in 1..{maximum}: {result}")
    return result


def plan(args, host):
    capacity = host["effective_cpu_capacity"]
    grant = capacity - args.reserve_cpus
    cpus = args.cpu_budget if args.cpu_budget is not None else grant
    if cpus < 1:
        raise ValueError(f"Observed CPU capacity {capacity} minus reserve {args.reserve_cpus} leaves {grant}; cannot admit a full search thread without consuming the declared reserve")
    if cpus > grant:
        raise ValueError(f"CPU budget {cpus} exceeds observed affinity/quota capacity {capacity} minus reserve {args.reserve_cpus} ({grant})")
    logical_budget = math.floor(cpus)
    available = host["available_memory_bytes"]
    if available is None and args.memory_mb is None:
        raise ValueError("available memory could not be measured; provide --memory-mb")
    memory = args.memory_mb * MIB if args.memory_mb is not None else int(available * args.memory_fraction)
    if memory <= 0 or (available is not None and memory > available):
        raise ValueError("memory budget exceeds the observed currently available memory")
    overhead = args.engine_overhead_mb * MIB
    affinity = host["physical_first_cpu_order"][:logical_budget]
    selected_cpus = set(affinity)
    physical_cores = sum(bool(selected_cpus.intersection(group))
                         for group in host.get("physical_core_groups_within_affinity", []))
    threads = points(args.threads, logical_budget, physical_cores)
    hashes = sorted(set(int(value) for value in args.hash_mb.split(",")))
    if not hashes or min(hashes) < 1:
        raise ValueError("hash sizes must be positive MiB values")
    excluded_hashes = [value for value in hashes if value * MIB + overhead > memory]
    hashes = [value for value in hashes if value not in excluded_hashes]
    if not hashes:
        raise ValueError("no hash setting fits the memory budget plus declared per-engine overhead")
    concurrency_cap = min(cpus // args.match_threads, memory // (2 * (args.match_hash_mb * MIB + overhead)))
    concurrency = points(args.concurrency, int(concurrency_cap), physical_cores // args.match_threads) if concurrency_cap >= 1 else []
    if args.concurrency and not concurrency:
        raise ValueError("no requested tournament concurrency fits CPU and two-resident-engines-per-game memory budgets")
    games = args.games if args.games else 2 * max(concurrency, default=1)
    if games < max(concurrency, default=1) or games % 2:
        raise ValueError("--games must be even and at least the largest tournament concurrency")
    return {"cpu_budget": cpus, "whole_search_thread_budget": logical_budget,
            "fractional_cpu_capacity_not_used": cpus - logical_budget,
            "cpu_affinity": affinity,
            "reserved_cpu_capacity": max(0, capacity - cpus), "memory_budget_bytes": memory,
            "engine_overhead_estimate_bytes": overhead, "threads": threads, "hash_mib": hashes,
            "excluded_hash_mib": excluded_hashes, "concurrency": concurrency, "games_per_match_sample": games,
            "match_threads_per_engine": args.match_threads, "match_hash_mib_per_engine": args.match_hash_mb,
            "memory_admission_formula": "2 * concurrency * (Hash MiB + declared engine overhead)",
            "cpu_admission_formula": "concurrency * Threads; pondering disabled; engine startup/clearing can overlap",
            "sampled_memory_enforcement_interval_seconds": 0.05,
            "memory_enforcement": ("admission estimate plus sampled aggregate process-tree RSS; not a private kernel cgroup hard limit"
                                   if host.get("proc_process_namespace_matches") else
                                   "admission estimate only; process RSS monitoring unavailable in this environment; existing host/cgroup limits still apply"),
            "max_wall_seconds": args.max_seconds, "per_case_timeout_seconds": args.case_timeout}


def proc_children(pid, proc=Path("/proc")):
    children = set()
    # Linux attributes children to the creating task. CuteChess launches engines
    # from worker threads, so reading only task/<main-pid>/children omits them.
    for path in (proc / str(pid) / "task").glob("*/children"):
        children.update(int(value) for value in (read(path) or "").split())
    return sorted(children)


def proc_sample(root):
    queue, samples = [root], {}
    visited = set()
    while queue:
        pid = queue.pop()
        if pid in visited:
            continue
        visited.add(pid)
        stat = read(f"/proc/{pid}/stat")
        if not stat:
            continue
        fields = stat[stat.rfind(")") + 2:].split()
        if len(fields) < 22:
            continue
        try:
            executable = os.readlink(f"/proc/{pid}/exe")
        except OSError:
            executable = None
        samples[str(pid) + ":" + fields[19]] = {
            "pid": pid, "start_ticks": int(fields[19]), "executable": executable,
            "cpu_ticks": int(fields[11]) + int(fields[12]),
            "rss_bytes": int(fields[21]) * os.sysconf("SC_PAGE_SIZE"),
            "io": key_numbers(read(f"/proc/{pid}/io"))}
        queue.extend(proc_children(pid))
    return samples


def run_process(command, log, budget, timeout, stdin=None, env=None):
    started = time.monotonic()
    kwargs = {"start_new_session": True} if os.name != "nt" else {}
    if hasattr(os, "sched_setaffinity"):
        kwargs["preexec_fn"] = lambda: os.sched_setaffinity(0, budget["cpu_affinity"])
    before = cgroup_limits(visible_cgroups()) if platform.system() == "Linux" else None
    snapshots, peak, peak_processes = {}, 0, 0
    reason = None
    can_sample = platform.system() == "Linux" and proc_namespace_matches()
    with Path(log).open("w", encoding="utf-8") as output:
        child = subprocess.Popen(command, stdin=subprocess.PIPE if stdin is not None else subprocess.DEVNULL,
                                 stdout=output, stderr=subprocess.STDOUT, text=True, env=env, **kwargs)
        try:
            if stdin is not None:
                child.stdin.write(stdin)
                child.stdin.close()
            while child.poll() is None:
                if can_sample:
                    current = proc_sample(child.pid)
                    snapshots.update(current)
                    peak = max(peak, sum(item["rss_bytes"] for item in current.values()))
                    peak_processes = max(peak_processes, len(current))
                    if peak > budget["memory_budget_bytes"]:
                        reason = "sampled_memory_budget_exceeded"
                        break
                if time.monotonic() - started >= timeout:
                    reason = "wall_timeout"
                    break
                try:
                    child.wait(timeout=0.05)
                except subprocess.TimeoutExpired:
                    pass
        finally:
            if os.name != "nt":
                try:
                    os.killpg(child.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            elif child.poll() is None:
                subprocess.run(["taskkill", "/PID", str(child.pid), "/T", "/F"], capture_output=True, timeout=10)
            child.wait(timeout=10)
    elapsed = time.monotonic() - started
    after = cgroup_limits(visible_cgroups()) if platform.system() == "Linux" else None
    deltas = []
    if before and after:
        previous = {item["path"]: item for item in before["ancestors"]}
        for current in after["ancestors"]:
            old = previous.get(current["path"], {})
            deltas.append({"path": current["path"], **{
                name: {key: value - key_numbers(old.get(name)).get(key, value)
                       for key, value in key_numbers(current.get(name)).items()}
                for name in ("cpu.stat", "memory.events")}})
    return {"command": command, "stdin": stdin, "pid": child.pid, "returncode": child.returncode,
            "wall_seconds": elapsed, "failure": reason, "log": str(log),
            "process_tree_peak_rss_bytes_sampled": peak or None, "process_tree_peak_processes_sampled": peak_processes or None,
            "process_tree_cpu_seconds_sampled_lower_bound": sum(item["cpu_ticks"] for item in snapshots.values()) / os.sysconf("SC_CLK_TCK") if snapshots else None,
            "observed_processes": list(snapshots.values()), "cgroup_before": before, "cgroup_after": after,
            "cgroup_counter_deltas": deltas,
            "process_sampling_available": can_sample,
            "resource_sampling_note": "50 ms snapshots can miss short-lived processes and resource peaks; CPU includes only observed processes.",
            "success": child.returncode == 0 and reason is None}


def parse_options(text):
    result = {}
    for line in text.splitlines():
        match = re.match(r"option name (.*?) type (\S+)(.*)", line)
        if match:
            name, kind, rest = match.groups()
            item = {"type": kind, "raw": line}
            for key in ("default", "min", "max"):
                value = re.search(r"(?:^| )" + key + r" (.*?)(?= (?:default|min|max|var) |$)", rest)
                if value:
                    item[key] = value[1]
            result[name] = item
    return result


def source_identity(binary):
    candidate = binary.parent.parent if binary.parent.name == "src" else None
    cache = binary.parent / "CMakeCache.txt"
    if candidate is None and cache.is_file():
        match = re.search(r"^CMAKE_HOME_DIRECTORY:INTERNAL=(.+)$", cache.read_text(), re.M)
        if match:
            candidate = Path(match[1])
    result = {"path": str(binary), "resolved_path": str(binary.resolve()), "sha256": sha256(binary),
              "size_bytes": binary.stat().st_size, "source": str(candidate) if candidate else None}
    managed = list(binary.parent.glob("*.runtimeconfig.json"))
    if managed:
        paths = sorted({path for pattern in ("*.dll", "*.deps.json", "*.runtimeconfig.json", "*.so", "*.dylib")
                        for path in binary.parent.glob(pattern) if path.is_file()})
        result["managed_runtime_files"] = [{"path": str(path), "sha256": sha256(path)} for path in paths]
    if candidate is not None:
        def git(*args):
            return subprocess.run(["git", "-c", "safe.directory=" + str(candidate), "-C", str(candidate), *args],
                                  capture_output=True, text=True, timeout=10, check=True).stdout.strip()
        try:
            result["source_commit"] = git("rev-parse", "HEAD")
            result["source_status"] = git("status", "--porcelain", "--untracked-files=all")
            state = Path(git("rev-parse", "--git-path", "laplace-stockfish-build.json"))
            if not state.is_absolute():
                state = candidate / state
            if state.is_file():
                receipt = json.loads(state.read_text())
                result["build_receipt"] = receipt
                result["build_receipt_matches_binary_and_source"] = (
                    receipt.get("binary_sha256") == result["sha256"] and
                    receipt.get("recipe", {}).get("commit") == result["source_commit"])
            header = read(candidate / "src/evaluate.h") or ""
            nets = sorted(set(re.findall(r"nn-[a-f0-9]{12}\.nnue", header)))
            result["source_networks"] = [{"name": name, "present": (candidate / "src" / name).is_file(),
                                           "sha256": sha256(candidate / "src" / name) if (candidate / "src" / name).is_file() else None}
                                          for name in nets]
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            result["source_binding_error"] = str(error)
    if cache.is_file():
        result["cmake_build"] = {line.split("=", 1)[0]: line.split("=", 1)[1]
                                 for line in cache.read_text().splitlines()
                                 if re.match(r"^(CMAKE_CXX_COMPILER:|CMAKE_BUILD_TYPE:|Qt6[^=]*_DIR:)", line)}
    return result


def parse_benches(text):
    summaries = re.findall(r"Total time \(ms\)\s*:\s*(\d+)\s+Nodes searched\s*:\s*(\d+)\s+Nodes/second\s*:\s*(\d+)", text)
    if not summaries:
        raise ValueError("Stockfish emitted no complete bench summary")
    positions = re.findall(r"Position:\s*\d+/(\d+)", text)
    return [{"engine_seconds": int(ms) / 1000, "nodes": int(nodes), "nodes_per_second": int(nps),
             "positions": int(positions[-1]) if positions else None}
            for ms, nodes, nps in summaries]


def parse_pgn(text, expected):
    records = [part for part in re.split(r'(?=^\[Event ")', text, flags=re.M) if part.strip()]
    if len(records) != expected:
        raise ValueError(f"expected {expected} PGN games, found {len(records)}")
    games = []
    for record in records:
        tags = dict(re.findall(r'^\[(\w+) "(.*)"\]$', record, re.M))
        result = tags.get("Result")
        if result not in ("1-0", "0-1", "1/2-1/2"):
            raise ValueError("PGN includes an unscored or incomplete game")
        if tags.get("Termination", "").lower() not in ("normal", "adjudication"):
            raise ValueError("PGN records a failed engine/game termination")
        if not tags.get("PlyCount", "").isdigit() or int(tags["PlyCount"]) == 0:
            raise ValueError("PGN lacks a numeric PlyCount")
        if not record.rstrip().endswith(result):
            raise ValueError("PGN movetext is truncated or disagrees with its Result tag")
        games.append({"white": tags.get("White"), "black": tags.get("Black"), "result": result,
                      "plies": int(tags["PlyCount"]), "termination": tags.get("Termination", "unspecified")})
    return games


def verify_tournament(transcript, games):
    finished = re.findall(r"^Finished game \d+ .*", transcript, re.M)
    moves = re.findall(r"<.*?: bestmove ([a-h][1-8][a-h][1-8][qrbn]?)", transcript)
    if len(finished) != len(games) or len(moves) != sum(game["plies"] for game in games):
        raise ValueError("CuteChess finished-game/engine-move transcript does not reconcile with the PGN")
    if re.search(r"(?:CRITICAL ERROR|disconnects|illegal move|connection stalls|Unrecognized option|Unknown option)", transcript, re.I):
        raise ValueError("CuteChess transcript records an engine/protocol failure")


def summary(values):
    return {"count": len(values), "median": statistics.median(values), "min": min(values), "max": max(values),
            "mean": statistics.mean(values), "stdev": statistics.stdev(values) if len(values) > 1 else 0,
            "relative_range": (max(values) - min(values)) / statistics.median(values) if statistics.median(values) else None}


def match_command(cutechess, stockfish, pgn, concurrency, games, args, laplace=None):
    settings = ["proto=uci", f"option.Threads={args.match_threads}", f"option.Hash={args.match_hash_mb}",
                "option.Ponder=false", "option.UCI_LimitStrength=false", "option.Skill Level=20", "option.MultiPV=1"]
    first = ["name=Stockfish-A", "cmd=" + str(stockfish), *settings]
    if laplace:
        first = ["name=Laplace", "cmd=" + str(laplace), "proto=uci"]
        if args.laplace_substrate != "inherit":
            first.append("option.Substrate=" + args.laplace_substrate)
    return [str(cutechess), "-engine", *first, "-engine", "name=Stockfish-B", "cmd=" + str(stockfish), *settings,
            "-each", "tc=inf", "depth=" + str(args.match_depth), "-rounds", str(games), "-concurrency", str(concurrency),
            "-maxmoves", str(args.max_moves), "-pgnout", str(pgn), "-debug", "all"]


def recommendations(report):
    sf = [case for case in report["stockfish_bench"] if case.get("status") == "complete"]
    cc = [case for case in report["cutechess_matches"] if case.get("status") == "complete"]
    result = {"scope": "Only measured configurations, exact executable identities and this host's admitted resource envelope.",
              "strength": "No Elo or Laplace-versus-Stockfish strength conclusion is supported by these calibration workloads.",
              "application": "Recommendations are proposals; this command changes no application configuration.",
              "evaluator_throughput": "CuteChess self-play is a process/concurrency proxy; independently benchmark the corpus evaluator before applying its worker count.",
              "numa": "UCI defaults and compiler capabilities are recorded; actual NUMA placement and huge-page backing are not measured, and no unmeasured policy is recommended."}
    if report.get("evidence_invalid"):
        result["invalid_evidence"] = "Executable/runtime identity or the resource envelope changed; no configuration recommendations are valid."
        return result
    result["all_planned_measurements_complete"] = report.get("status") == "complete"
    if report["parameters"]["repeats"] < 3:
        result["insufficient_repeats"] = "Fewer than three steady samples were requested; retain this as a smoke measurement, not a configuration recommendation."
        return result
    if sf:
        fastest = min(sf, key=lambda item: item["steady_engine_seconds"]["median"])
        throughput = max(sf, key=lambda item: item["steady_nodes_per_second"]["median"])
        result["bench_suite_latency"] = {"threads": fastest["threads"], "hash_mib": fastest["hash_mib"],
                                           "median_seconds": fastest["steady_engine_seconds"]["median"],
                                           "scope": "Time to complete the declared built-in bench workload; search work can differ across Threads/Hash."}
        result["search_node_throughput"] = {"threads": throughput["threads"], "hash_mib": throughput["hash_mib"],
                                             "median_nodes_per_second": throughput["steady_nodes_per_second"]["median"],
                                             "observed_range_overlaps_another_configuration": any(
                                                 item is not throughput and item["steady_nodes_per_second"]["max"] >= throughput["steady_nodes_per_second"]["min"]
                                                 for item in sf)}
    if cc:
        winner = max(cc, key=lambda item: item["steady_plies_per_second"]["median"])
        result["bounded_tournament_throughput"] = {"concurrency": winner["concurrency"],
            "threads_per_engine": report["plan"]["match_threads_per_engine"],
            "hash_mib_per_engine": report["plan"]["match_hash_mib_per_engine"],
            "median_plies_per_second": winner["steady_plies_per_second"]["median"],
            "observed_range_overlaps_another_configuration": any(
                item is not winner and item["steady_plies_per_second"]["max"] >= winner["steady_plies_per_second"]["min"] for item in cc),
            "scope": "Measured move-limited Stockfish self-play with pondering off; Laplace gauntlet capacity requires its own paired measurement."}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--stockfish", type=Path)
    parser.add_argument("--cutechess", type=Path)
    parser.add_argument("--laplace-uci", type=Path, help="Optional separate two-game Laplace/SF acceptance using its configured substrate mode")
    parser.add_argument("--laplace-substrate", choices=("inherit", "substrate", "off"), default="inherit",
                        help="Preserve Laplace's configured mode by default; disabling the substrate is explicit")
    parser.add_argument("--cpu-budget", type=float)
    parser.add_argument("--reserve-cpus", type=float, default=2)
    parser.add_argument("--memory-mb", "--memory-mib", type=int)
    parser.add_argument("--memory-fraction", type=float, default=0.5)
    parser.add_argument("--engine-overhead-mb", type=int, default=256)
    parser.add_argument("--threads", help="Comma-separated points; default powers of two plus the observed CPU-budget endpoint and physical-core boundary within selected affinity")
    parser.add_argument("--hash-mb", "--hash-mib", default="16,64,256")
    parser.add_argument("--concurrency", help="Comma-separated games-in-flight points; derived from CPU and two-engine memory budgets")
    parser.add_argument("--match-threads", type=int, default=1)
    parser.add_argument("--match-hash-mb", type=int, default=16)
    parser.add_argument("--games", type=int)
    parser.add_argument("--match-depth", type=int, default=4)
    parser.add_argument("--max-moves", type=int, default=8)
    parser.add_argument("--bench-limit", type=int, default=12)
    parser.add_argument("--bench-limit-type", choices=("depth", "nodes"), default="depth")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--max-seconds", type=float, default=180)
    parser.add_argument("--case-timeout", type=float, default=60)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--plan-only", action="store_true", help="Inspect capability/resource admission without launching benchmark tools")
    args = parser.parse_args()
    if min(args.repeats, args.match_threads, args.match_hash_mb, args.match_depth, args.max_moves, args.bench_limit,
           args.engine_overhead_mb, args.max_seconds, args.case_timeout) <= 0 or args.reserve_cpus < 0 or not 0 < args.memory_fraction <= 1:
        parser.error("counts/timeouts must be positive, reserve nonnegative, and memory fraction in (0,1]")
    output = args.output_dir.absolute()
    output.mkdir(parents=True, exist_ok=True)
    if (output / "report.json").exists():
        parser.error("output directory already has a report; use a new directory to preserve prior evidence")
    host = machine()
    try:
        budget = plan(args, host)
    except ValueError as error:
        (output / "report.json").write_text(json.dumps({"schema": SCHEMA, "status": "preflight_failed", "host": host,
                                                       "failures": [str(error)]}, indent=2) + "\n")
        print(f"CHESS_ENVIRONMENT_BENCHMARK status=preflight_failed report={output / 'report.json'}")
        return 1
    if args.plan_only:
        (output / "report.json").write_text(json.dumps({"schema": SCHEMA, "status": "plan_only", "host": host,
                                                       "plan": budget, "measurements": "not run"}, indent=2) + "\n")
        print(f"CHESS_ENVIRONMENT_BENCHMARK status=plan_only report={output / 'report.json'}")
        return 0
    sf_helper = module("install-stockfish")
    sf = (args.stockfish or sf_helper.configured_binary()).absolute()
    cc = (args.cutechess or Path(os.environ.get("LAPLACE_CUTECHESS", str(Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace")) / "bin/cutechess-cli")))).absolute()
    report = {"schema": SCHEMA, "started_utc": dt.datetime.now(dt.timezone.utc).isoformat(), "status": "running",
              "purpose": "Machine-specific installed chess-tool environment calibration; this hostname identifies the measured machine.",
              "host": host, "plan": budget, "parameters": {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()},
              "stockfish_bench": [], "cutechess_matches": [], "paired_acceptance": None, "failures": [],
              "cache_contract": {"machine_cold": "Not measured; OS filesystem cache is neither flushed nor controlled.",
                  "stockfish_first_sample": "First bench in a new process after capability discovery; process state cold, OS cache uncontrolled.",
                  "stockfish_steady_samples": "Subsequent bench commands in the same process; upstream bench issues ucinewgame and resets TT each bench.",
                  "cutechess_samples": "Every sample starts a new tournament process; sample zero warms filesystem/runtime paths and is excluded from steady aggregates.",
                  "sample_variation": "All samples retained; mean/median/min/max/stdev/range reported. No full-host idle guarantee."}}
    report_path = output / "report.json"
    def save():
        report_path.write_text(json.dumps(report, indent=2) + "\n")
    started, deadline = time.monotonic(), time.monotonic() + args.max_seconds
    def execute(command, name, stdin=None, env=None):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("overall benchmark wall budget exhausted")
        return run_process(command, output / (name + ".log"), budget, min(args.case_timeout, remaining), stdin, env)
    try:
        report["stockfish_identity"], report["cutechess_identity"] = source_identity(sf), source_identity(cc)
        probe = execute([str(sf)], "stockfish-capabilities", "uci\ncompiler\nquit\n")
        transcript = Path(probe["log"]).read_text()
        if not probe["success"] or "uciok" not in transcript:
            raise ValueError("Stockfish capability handshake failed")
        report["stockfish_identity"].update({"capability_process": probe, "uci_options": parse_options(transcript), "compiler_transcript": transcript})
        options = report["stockfish_identity"]["uci_options"]
        for name, values in (("Threads", budget["threads"] + [args.match_threads]), ("Hash", budget["hash_mib"] + [args.match_hash_mb])):
            option = options.get(name)
            if not option or any(not int(option["min"]) <= value <= int(option["max"]) for value in values):
                raise ValueError(f"requested {name} settings are unsupported by the actual UCI options")
        version = execute([str(cc), "--version"], "cutechess-capabilities")
        if not version["success"]:
            raise ValueError("CuteChess/Qt capability check failed")
        report["cutechess_identity"]["version_process"] = version
        report["cutechess_identity"]["version_text"] = Path(version["log"]).read_text()
        configs = [(threads, size) for threads in budget["threads"] for size in budget["hash_mib"]]
        random.Random(args.seed).shuffle(configs)
        for threads, size in configs:
            case = {"threads": threads, "hash_mib": size, "status": "running"}
            report["stockfish_bench"].append(case)
            bench = f"bench {size} {threads} {args.bench_limit} default {args.bench_limit_type}\n"
            result = execute([str(sf)], f"stockfish-t{threads}-h{size}", bench * (args.repeats + 1) + "quit\n")
            case["process"] = result
            if not result["success"]:
                case["status"] = "failed"
                raise ValueError(f"Stockfish bench failed for Threads={threads}, Hash={size}")
            samples = parse_benches(Path(result["log"]).read_text())
            if len(samples) != args.repeats + 1:
                raise ValueError("Stockfish bench repeat count did not reconcile")
            case.update({"status": "complete", "first_process_sample": samples[0], "steady_samples": samples[1:],
                         "steady_engine_seconds": summary([sample["engine_seconds"] for sample in samples[1:]]),
                         "steady_nodes_per_second": summary([sample["nodes_per_second"] for sample in samples[1:]]),
                         "steady_nodes": summary([sample["nodes"] for sample in samples[1:]])})
            save()
        if not budget["concurrency"]:
            report["failures"].append("No CuteChess concurrency fits the admitted two-engine memory/CPU budget")
        for concurrency in budget["concurrency"]:
            case = {"concurrency": concurrency, "status": "running", "samples": []}
            report["cutechess_matches"].append(case)
            for repeat in range(args.repeats + 1):
                pgn = output / f"cutechess-c{concurrency}-r{repeat}.pgn"
                result = execute(match_command(cc, sf, pgn, concurrency, budget["games_per_match_sample"], args), pgn.stem)
                sample = {"process": result, "pgn": str(pgn)}
                case["samples"].append(sample)
                if not result["success"]:
                    raise ValueError(f"CuteChess tournament failed at concurrency {concurrency}")
                games = parse_pgn(pgn.read_text(), budget["games_per_match_sample"])
                verify_tournament(Path(result["log"]).read_text(), games)
                sample.update({"games": games, "pgn_sha256": sha256(pgn), "total_plies": sum(game["plies"] for game in games),
                               "games_per_second": len(games) / result["wall_seconds"],
                               "plies_per_second": sum(game["plies"] for game in games) / result["wall_seconds"]})
                save()
            case.update({"status": "complete", "first_process_sample": 0,
                         "steady_plies_per_second": summary([sample["plies_per_second"] for sample in case["samples"][1:]]),
                         "steady_games_per_second": summary([sample["games_per_second"] for sample in case["samples"][1:]])})
            save()
        if args.laplace_uci:
            laplace = args.laplace_uci.absolute()
            pgn = output / "laplace-stockfish-paired.pgn"
            paired = {"identity": source_identity(laplace), "scope": "Two move-limited paired search acceptance games; no Elo inference.",
                      "substrate_requested": args.laplace_substrate,
                      "fairness": "Same per-move depth and process affinity; Stockfish Threads/Hash explicit, Laplace has no equivalent UCI knobs. No equal-work claim."}
            report["paired_acceptance"] = paired
            capability = execute([str(laplace)], "laplace-capabilities", "uci\nquit\n")
            capability_text = Path(capability["log"]).read_text()
            if not capability["success"] or "uciok" not in capability_text:
                raise ValueError("Laplace UCI capability discovery failed")
            paired["identity"]["uci_options"] = parse_options(capability_text)
            paired["identity"]["capability_process"] = capability
            substrate_option = paired["identity"]["uci_options"].get("Substrate", {})
            paired["substrate_effective"] = substrate_option.get("default", "unadvertised") if args.laplace_substrate == "inherit" else args.laplace_substrate
            result = execute(match_command(cc, sf, pgn, 1, 2, args, laplace), pgn.stem,
                             env=dict(os.environ, LAPLACE_OPS_LOG_DIR=str(output)))
            paired["process"] = result
            if not result["success"]:
                raise ValueError("Laplace/Stockfish paired acceptance failed")
            games = parse_pgn(pgn.read_text(), 2)
            verify_tournament(Path(result["log"]).read_text(), games)
            if sorted(game["white"] for game in games) != ["Laplace", "Stockfish-B"]:
                raise ValueError("paired acceptance did not exercise Laplace with both colors")
            paired.update({"games": games, "pgn": str(pgn), "pgn_sha256": sha256(pgn)})
        for key, binary in (("stockfish_identity", sf), ("cutechess_identity", cc)):
            if report[key]["sha256"] != sha256(binary):
                report["evidence_invalid"] = True
                raise ValueError("an executable changed during the benchmark")
        if report["paired_acceptance"]:
            identity = report["paired_acceptance"]["identity"]
            if identity["sha256"] != sha256(args.laplace_uci) or any(
                    item["sha256"] != sha256(item["path"]) for item in identity.get("managed_runtime_files", [])):
                report["evidence_invalid"] = True
                raise ValueError("the Laplace executable/runtime changed during paired acceptance")
        after_host = machine()
        if (after_host["affinity_cpu_ids"] != host["affinity_cpu_ids"] or
                after_host["cgroup"]["cpu_quota"] != host["cgroup"]["cpu_quota"] or
                after_host["cgroup"]["memory_limit_bytes"] != host["cgroup"]["memory_limit_bytes"]):
            report["evidence_invalid"] = True
            raise ValueError("machine affinity or cgroup CPU/memory limits changed during the benchmark")
        report["status"] = "complete" if not report["failures"] else "incomplete"
    except (OSError, ValueError, TimeoutError, subprocess.SubprocessError) as error:
        report["status"] = "budget_exhausted" if isinstance(error, TimeoutError) else "failed"
        report["failures"].append(str(error))
    finally:
        report["elapsed_wall_seconds"] = time.monotonic() - started
        report["finished_utc"] = dt.datetime.now(dt.timezone.utc).isoformat()
        report["host_after"] = machine()
        report["recommendations"] = recommendations(report)
        save()
    print(f"CHESS_ENVIRONMENT_BENCHMARK status={report['status']} report={report_path}")
    return 0 if report["status"] == "complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
