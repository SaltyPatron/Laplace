#!/usr/bin/env python3
"""Measure this machine's chess tools under explicit CPU, memory and time budgets.

This calibrates the installed executables and the admitted environment. It does
not estimate Elo or extrapolate single-worker rates into whole-machine capacity.
Raw commands, transcripts, PGNs and a versioned report remain in --output-dir.
"""
from __future__ import annotations

import argparse
import contextlib
import ctypes
import datetime as dt
import hashlib  # noqa: F401
import importlib.util  # noqa: F401
import json
import math  # noqa: F401
import os
from pathlib import Path
import platform
import queue
import random
import re
import shutil
import signal
import socket
import statistics  # noqa: F401
import subprocess
import sys
import threading
import time
import urllib.error  # noqa: F401
import urllib.parse  # noqa: F401
import urllib.request  # noqa: F401

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from lib import chess_pgn
from benchmark_chess_helpers import (  # noqa: F401
    MIB,
    ROOT,
    SCHEMA,
    SERVICE_PROPERTIES,
    SERVICE_UNITS,
    api_base,
    cgroup_limits,
    chess_lookup_deltas,
    chess_perfcache_observation,
    chess_read_request,
    gpu_observations,
    http_readiness,
    key_numbers,
    lichess_readiness,
    local_http_opener,
    match_command,
    module,
    normal_api_headers,
    parse_benches,
    parse_options,
    parse_pgn,
    plan,
    points,
    proc_children,
    read,
    recommendations,
    service_observations,
    sha256,
    source_identity,
    summary,
    validate_chess_observation_timestamp,
    verify_tournament,
    visible_cgroups,
)


def runtime_capabilities(deadline, base="http://127.0.0.1:5187"):
    def remaining():
        value = deadline - time.monotonic()
        if value <= 0:
            raise TimeoutError("runtime capability observation exhausted the benchmark deadline")
        return min(8, value)
    result = {"observed_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
              "service_mutations_performed": False, "application_startup_measured": False}
    command = ["systemctl", "show", "--no-pager", "--property=" + ",".join(SERVICE_PROPERTIES), *SERVICE_UNITS]
    try:
        observed = subprocess.run(command, capture_output=True, text=True, timeout=remaining())
        units = service_observations(observed.stdout)
        result["systemd"] = {"command": command, "returncode": observed.returncode,
            "units": {unit: units.get(unit, {"installed": None, "running": None, "enabled": None,
                "status": "unavailable"}) for unit in SERVICE_UNITS}}
    except (OSError, subprocess.SubprocessError) as error:
        result["systemd"] = {"status": "unavailable", "error_type": type(error).__name__}
    result["api_readiness"] = http_readiness(remaining(), base=base)
    if result["api_readiness"].get("ready") is True:
        result["chess_read_request"] = chess_read_request(remaining(), base=base)
        result["api_readiness_after_chess_request"] = http_readiness(remaining(), base=base)
        result["chess_lookup_observation"] = chess_lookup_deltas(
            result["api_readiness"], result["api_readiness_after_chess_request"])
    else:
        result["chess_read_request"] = {"status": "not-run-api-not-ready"}
    result["lichess_readiness"] = lichess_readiness(remaining(), base=base)
    executable = shutil.which("nvidia-smi")
    result["nvidia"] = {"utility_installed": executable is not None, "executable": executable,
                        "compute_execution_measured": False, "devices": [], "queries": []}
    if executable:
        try:
            for with_compute in (True, False):
                fields = "index,uuid,name,driver_version," + ("compute_cap," if with_compute else "") + "memory.total,memory.free"
                command = [executable, "--query-gpu=" + fields, "--format=csv,noheader,nounits"]
                observed = subprocess.run(command, capture_output=True, text=True, timeout=remaining())
                result["nvidia"]["queries"].append({"command": command, "returncode": observed.returncode})
                if observed.returncode == 0:
                    result["nvidia"]["devices"] = gpu_observations(observed.stdout, with_compute)
                    result["nvidia"]["driver_query_succeeded"] = True
                    result["nvidia"]["compute_capability_query_succeeded"] = with_compute
                    break
            else:
                result["nvidia"]["driver_query_succeeded"] = False
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            result["nvidia"].update(driver_query_succeeded=False, error_type=type(error).__name__)
    return result


def uci_bootstrap(executable, log, budget, timeout, repeats=3):
    """Time a new owned process and sequential ready probes in that same process."""
    started = time.monotonic()
    deadline = started + timeout
    events = queue.Queue(maxsize=1024)
    timings = {"clock": "time.monotonic", "reused_process_isready_seconds": [],
        "scope": "New process spawn to uciok, then command-write to readyok; filesystem cache is uncontrolled. These probes do not perform a search or prove NNUE evaluation."}
    kwargs = {"start_new_session": True} if os.name != "nt" else {}
    if hasattr(os, "sched_setaffinity"):
        kwargs["preexec_fn"] = lambda: os.sched_setaffinity(0, budget["cpu_affinity"])
    process = None
    reader = None
    failure = None
    peak_rss = 0
    next_sample = started
    can_sample = platform.system() == "Linux" and proc_namespace_matches()
    with Path(log).open("w", encoding="utf-8") as transcript:
        try:
            process = subprocess.Popen([str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT, text=True, bufsize=1, **kwargs)
            def receive():
                total = 0
                try:
                    while line := process.stdout.readline(65537):
                        arrived = time.monotonic()
                        total += len(line.encode("utf-8"))
                        if len(line) > 65536 or total > 4 * MIB:
                            events.put_nowait((None, arrived))
                            return
                        transcript.write(line)
                        transcript.flush()
                        events.put_nowait((line.rstrip("\r\n"), arrived))
                    events.put_nowait((None, time.monotonic()))
                except (OSError, ValueError, queue.Full):
                    return
            reader = threading.Thread(target=receive, daemon=True)
            reader.start()
            def send(command):
                process.stdin.write(command + "\n")
                process.stdin.flush()
            def wait_for(expected, sent=started):
                nonlocal peak_rss, next_sample
                while time.monotonic() < deadline:
                    if can_sample and time.monotonic() >= next_sample:
                        peak_rss = max(peak_rss, sum(item["rss_bytes"] for item in proc_sample(process.pid).values()))
                        next_sample = time.monotonic() + 0.05
                        if peak_rss > budget["memory_budget_bytes"]:
                            raise ValueError("UCI bootstrap exceeded sampled memory budget")
                    try:
                        line, arrived = events.get(timeout=min(0.05, max(0.001, deadline - time.monotonic())))
                    except queue.Empty:
                        continue
                    if line is None:
                        raise ValueError("UCI stream ended before " + expected)
                    if line == expected:
                        if arrived < sent:
                            raise ValueError("UCI acknowledgement preceded its command: " + expected)
                        return arrived
                raise TimeoutError("UCI bootstrap deadline exhausted waiting for " + expected)
            send("uci")
            timings["process_spawn_to_uciok_seconds"] = wait_for("uciok") - started
            for index in range(repeats + 1):
                sent = time.monotonic()
                send("isready")
                elapsed = wait_for("readyok", sent) - sent
                if index == 0:
                    timings["initial_isready_seconds"] = elapsed
                else:
                    timings["reused_process_isready_seconds"].append(elapsed)
            send("compiler")
            send("isready")
            wait_for("readyok")
            send("quit")
            process.wait(timeout=max(0.001, deadline - time.monotonic()))
            if process.returncode != 0:
                raise ValueError("Stockfish exited unsuccessfully after UCI bootstrap")
        except (OSError, ValueError, TimeoutError, subprocess.SubprocessError) as error:
            failure = type(error).__name__ + ": " + str(error)
        finally:
            if process is not None:
                if os.name != "nt":
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                elif process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
                if reader is not None:
                    reader.join(timeout=5)
                if process.stdin:
                    try:
                        process.stdin.close()
                    except OSError:
                        pass
                if process.stdout:
                    process.stdout.close()
            transcript.flush()
            os.fsync(transcript.fileno())
    return {"command": [str(executable)], "pid": process.pid if process else None,
        "returncode": process.returncode if process else None, "log": str(log),
        "wall_seconds": time.monotonic() - started, "success": failure is None, "failure": failure,
        "process_sampling_available": can_sample, "process_tree_peak_rss_bytes_sampled": peak_rss or None,
        "bootstrap": timings}


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


@contextlib.contextmanager
def interruption_scope():
    """Let existing owned-process finally blocks finish on cancellation."""
    previous = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
    def interrupted(signum, frame):
        for selected in previous:
            signal.signal(selected, signal.SIG_IGN)
        raise KeyboardInterrupt("chess calibration interrupted")
    for sig in previous:
        signal.signal(sig, interrupted)
    try:
        yield
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)


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
    parser.add_argument("--max-moves", type=int, default=0,
                        help="0 plays complete games; a positive cap requests only a move-limited diagnostic")
    parser.add_argument("--bench-limit", type=int, default=12)
    parser.add_argument("--bench-limit-type", choices=("depth", "nodes"), default="depth")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--max-seconds", type=float, default=180)
    parser.add_argument("--case-timeout", type=float, default=60)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--api-base", type=api_base,
                        default=os.environ.get("LAPLACE_API_BASE", "http://127.0.0.1:" + os.environ.get("LAPLACE_API_PORT", "5187")))
    parser.add_argument("--plan-only", action="store_true", help="Inspect capability/resource admission without launching benchmark tools")
    parser.add_argument("--pgn-validator-cache", type=Path, default=chess_pgn.default_cache(),
                        help="Cache for the exact pinned external rules provider; no global Python installation")
    parser.add_argument("--pgn-validator-offline", action="store_true",
                        help="Require the verified PGN provider archive to be present in its cache")
    parser.add_argument("--runtime-only", action="store_true", help="Observe services, chess readiness before/after one depth-one Laplace evaluation, GPU, NNUE files and Stockfish UCI bootstrap; skip Stockfish search calibration and tournaments")
    args = parser.parse_args()
    if args.plan_only and args.runtime_only:
        parser.error("--plan-only and --runtime-only describe different execution scopes")
    if min(args.repeats, args.match_threads, args.match_hash_mb, args.match_depth, args.bench_limit,
           args.engine_overhead_mb, args.max_seconds, args.case_timeout) <= 0 or args.max_moves < 0 or args.reserve_cpus < 0 or not 0 < args.memory_fraction <= 1:
        parser.error("counts/timeouts must be positive, move cap/reserve nonnegative, and memory fraction in (0,1]")
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
              "tournament_scope": "complete-games" if args.max_moves == 0 else "move-limited-diagnostic",
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
        report["runtime_capabilities"] = runtime_capabilities(deadline, args.api_base)
        save()
        report["stockfish_identity"] = source_identity(sf)
        if not args.runtime_only:
            report["cutechess_identity"] = source_identity(cc)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("overall benchmark wall budget exhausted before Stockfish bootstrap")
        probe = uci_bootstrap(sf, output / "stockfish-capabilities.log", budget,
                              min(args.case_timeout, remaining), args.repeats)
        transcript = Path(probe["log"]).read_text()
        if not probe["success"] or "uciok" not in transcript:
            raise ValueError("Stockfish capability handshake failed")
        report["stockfish_identity"].update({"capability_process": probe, "uci_options": parse_options(transcript), "compiler_transcript": transcript})
        options = report["stockfish_identity"]["uci_options"]
        report["stockfish_identity"]["advertised_nnue_files"] = {
            name: value.get("default") for name, value in options.items() if name in ("EvalFile", "EvalFileSmall")}
        if args.runtime_only:
            if report["stockfish_identity"]["sha256"] != sha256(sf):
                report["evidence_invalid"] = True
                raise ValueError("Stockfish executable changed during bootstrap")
            report["status"] = "complete"
            print(f"CHESS_ENVIRONMENT_BENCHMARK status=complete scope=runtime_only report={report_path}")
            return 0
        provider = chess_pgn.load_provider(args.pgn_validator_cache, offline=args.pgn_validator_offline)
        report["pgn_validation_provider"] = provider[2]
        save()
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
                games = parse_pgn(pgn.read_text(), budget["games_per_match_sample"], allow_adjudication=args.max_moves > 0,
                                  max_moves=args.max_moves, provider=provider)
                verify_tournament(Path(result["log"]).read_text(), games)
                sample.update({"games": games, "pgn_sha256": sha256(pgn),
                               "normal_completed_games": sum(game["normal_completion"] for game in games),
                               "legal_moves_validated": True, "total_plies": sum(game["plies"] for game in games),
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
            paired = {"identity": source_identity(laplace),
                      "scope": "Two complete paired games; no Elo inference." if args.max_moves == 0 else "Two move-limited diagnostic games; no complete-game or Elo inference.",
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
            games = parse_pgn(pgn.read_text(), 2, allow_adjudication=args.max_moves > 0,
                                  max_moves=args.max_moves, provider=provider)
            verify_tournament(Path(result["log"]).read_text(), games)
            if sorted(game["white"] for game in games) != ["Laplace", "Stockfish-B"]:
                raise ValueError("paired acceptance did not exercise Laplace with both colors")
            paired.update({"games": games, "pgn": str(pgn), "pgn_sha256": sha256(pgn),
                           "normal_completed_games": sum(game["normal_completion"] for game in games),
                           "legal_moves_validated": True})
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
    except (OSError, ValueError, TimeoutError, subprocess.SubprocessError, KeyboardInterrupt) as error:
        report["status"] = ("interrupted" if isinstance(error, KeyboardInterrupt) else
                            "budget_exhausted" if isinstance(error, TimeoutError) else "failed")
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
    with interruption_scope():
        raise SystemExit(main())
