#!/usr/bin/env python3
"""Helper functions for benchmark-chess-environment.py."""
from __future__ import annotations

import csv
import datetime as dt
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import statistics
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

from lib import chess_pgn

ROOT = Path(__file__).resolve().parents[1]
MIB = 1024 * 1024
SCHEMA = "laplace.benchmark.chess-environment/v1"
SERVICE_PROPERTIES = ("Id", "LoadState", "UnitFileState", "ActiveState", "SubState", "MainPID",
    "Type", "Result", "ExecMainCode", "ExecMainStatus", "ConditionResult",
    "ExecMainStartTimestamp", "ExecMainStartTimestampMonotonic", "ExecMainExitTimestamp",
    "ExecMainExitTimestampMonotonic", "ActiveEnterTimestamp", "ActiveEnterTimestampMonotonic",
    "InactiveExitTimestamp", "InactiveExitTimestampMonotonic", "StateChangeTimestampMonotonic")
SERVICE_UNITS = ("laplace-api.service", "laplace-lichess.service")


def service_observations(text):
    """Retain only explicitly requested non-secret systemd properties."""
    records = {}
    for block in re.split(r"\n\s*\n", text.strip()):
        fields = dict(line.split("=", 1) for line in block.splitlines()
                      if "=" in line and line.split("=", 1)[0] in SERVICE_PROPERTIES)
        unit = fields.get("Id")
        if unit not in SERVICE_UNITS:
            continue
        pid = int(fields["MainPID"]) if fields.get("MainPID", "").isdigit() else None
        clocks = {name: int(value) if value.isdigit() else None for name, value in fields.items()
                  if name.endswith("Monotonic")}
        start, active = clocks.get("ExecMainStartTimestampMonotonic"), clocks.get("ActiveEnterTimestampMonotonic")
        records[unit] = {"properties": fields, "main_pid": pid,
            "installed": fields.get("LoadState") in ("loaded", "masked"),
            "enabled": fields.get("UnitFileState") in ("enabled", "enabled-runtime"),
            "running": fields.get("ActiveState") == "active" and pid is not None and pid > 0,
            "systemd_monotonic_microseconds": clocks,
            "manager_start_to_active_seconds": (active - start) / 1e6 if start and active and active >= start else None,
            "startup_scope": "Service-manager timestamps only; Type=simple activation does not establish application readiness or application startup duration."}
    return records


def validate_chess_observation_timestamp(stamp):
    """Validate DateTimeOffset JSON on Python 3.10 without changing its receipt bytes."""
    if not isinstance(stamp, str):
        raise ValueError("invalid chess observation timestamp")
    match = re.fullmatch(r"([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})"
                         r"(?:\.([0-9]{1,7}))?(Z|[+-][0-9]{2}:[0-9]{2})", stamp)
    if match is None:
        raise ValueError("invalid chess observation timestamp")
    calendar, fraction, offset = match.groups()
    if offset == "Z":
        offset = "+00:00"
    hours, minutes = int(offset[1:3]), int(offset[4:6])
    if hours > 14 or minutes > 59 or (hours == 14 and minutes != 0):
        raise ValueError("invalid chess observation timestamp")
    # System.Text.Json emits up to seven tick digits, trimming trailing zeroes.
    # Python 3.10 accepts only three or six fractional digits. Normalize a local
    # validation copy; the caller retains the exact original timestamp string.
    validation_fraction = "." + fraction.ljust(6, "0")[:6] if fraction else ""
    dt.datetime.fromisoformat(calendar + validation_fraction + offset)


def chess_perfcache_observation(value):
    """Retain only typed process/map evidence; never retain arbitrary health detail."""
    if not isinstance(value, dict) or type(value.get("process_id")) is not int or value["process_id"] <= 0:
        raise ValueError("invalid chess process identity")
    if type(value.get("initialization_completed")) is not bool:
        raise ValueError("invalid chess initialization status")
    if type(value.get("ready")) is not bool:
        raise ValueError("invalid chess readiness status")
    stamp = value.get("observed_utc")
    validate_chess_observation_timestamp(stamp)
    scope = "process-lifetime completed managed lookups; counters include earlier mappings"
    if value.get("counter_scope") != scope:
        raise ValueError("unknown chess counter scope")
    result = {name: value[name] for name in ("process_id", "observed_utc", "counter_scope", "initialization_completed", "ready")}
    for name, counters in (("position", ("record_count", "lookup_hits", "lookup_misses")),
                           ("transition", ("record_count", "novel_count", "persistent_hits", "novel_hits", "lookup_misses"))):
        part = value.get(name)
        if part is None:
            result[name] = None
            continue
        if not isinstance(part, dict) or type(part.get("is_loaded")) is not bool:
            raise ValueError("invalid chess map state")
        if any(type(part.get(key)) is not int or part[key] < 0 for key in counters):
            raise ValueError("invalid chess lookup counters")
        if not part["is_loaded"] and part["record_count"] != 0:
            raise ValueError("unloaded chess map claims records")
        result[name] = {key: part[key] for key in ("is_loaded", *counters)}
    failure = value.get("failure_type")
    if failure is not None:
        if not isinstance(failure, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9]{0,80}Exception", failure):
            raise ValueError("invalid chess failure type")
        result["failure_type"] = failure
    expected_ready = result["initialization_completed"] and failure is None and all(
        result[name] is not None and result[name]["is_loaded"] and result[name]["record_count"] > 0
        for name in ("position", "transition"))
    if result["ready"] != expected_ready:
        raise ValueError("inconsistent chess readiness status")
    return result


def local_http_opener():
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, message, headers, newurl):
            return None
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())


def api_base(value):
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username \
            or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("API base must be an HTTP(S) URL without credentials or query")
    return value.rstrip("/")


def normal_api_headers():
    headers = {"Content-Type": "application/json", "X-Laplace-Tenant": os.environ.get("LAPLACE_PROOF_TENANT", "ci")}
    for variable, header in (("LAPLACE_API_KEY", "Authorization"), ("LAPLACE_QUOTE_ID", "X-Laplace-Quote-Id")):
        if os.environ.get(variable):
            headers[header] = ("Bearer " if header == "Authorization" else "") + os.environ[variable]
    return headers


def http_readiness(timeout, opener=None, base="http://127.0.0.1:5187"):
    url = api_base(base) + "/health/ready"
    started = time.monotonic()
    result = {"url": url, "ready": False, "http_status": None,
              "scope": "One current readiness request; elapsed time is HTTP response latency, not application startup time."}
    opener = opener or local_http_opener()
    try:
        try:
            response = opener.open(urllib.request.Request(url, headers=normal_api_headers()), timeout=timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            result["http_status"] = response.code
            raw = response.read(65537)
        if len(raw) > 65536:
            raise ValueError("readiness response exceeds bound")
        body = json.loads(raw)
        names = ("ready", "substrate_reachable", "perfcache_ready")
        if not isinstance(body, dict) or any(type(body.get(name)) is not bool for name in names):
            raise ValueError("readiness booleans unavailable")
        result["observed"] = {name: body[name] for name in names}
        for name in ("entities", "consensus_relations"):
            if type(body.get(name)) is int and body[name] >= 0:
                result["observed"][name] = body[name]
        result["response_sha256"] = hashlib.sha256(raw).hexdigest()
        result["ready"] = result["http_status"] == 200 and all(body[name] for name in names)
        result["status"] = "ready" if result["ready"] else "not-ready"
        if body.get("chess_perfcache") is not None:
            try:
                result["observed"]["chess_perfcache"] = chess_perfcache_observation(body["chess_perfcache"])
            except (ValueError, TypeError, OverflowError):
                result["chess_perfcache_status"] = "invalid-observation"
    except (OSError, ValueError, urllib.error.URLError) as error:
        # Response detail and exception messages can contain database settings.
        result.update(status="unavailable", error_type=type(error).__name__)
    result["request_wall_seconds"] = time.monotonic() - started
    return result


def chess_lookup_deltas(before, after):
    first = before.get("observed", {}).get("chess_perfcache")
    last = after.get("observed", {}).get("chess_perfcache")
    if first is None or last is None:
        return {"status": "missing-chess-observation"}
    if first["process_id"] != last["process_id"]:
        return {"status": "process-changed", "before_process_id": first["process_id"], "after_process_id": last["process_id"]}
    delta = {}
    for name, counters in (("position", ("lookup_hits", "lookup_misses")),
                           ("transition", ("persistent_hits", "novel_hits", "lookup_misses"))):
        if first[name] is None or last[name] is None:
            return {"status": "missing-map-observation"}
        delta[name] = {key: last[name][key] - first[name][key] for key in counters}
        if any(value < 0 for value in delta[name].values()):
            return {"status": "counter-regressed"}
    return {"status": "observed", "process_id": first["process_id"], "deltas": delta,
            "scope": "Process-wide changes during one ordinary read-only chess request; concurrent requests may contribute. Zero is retained and does not prove catalog use."}


def chess_read_request(timeout, opener=None, base="http://127.0.0.1:5187"):
    """One ordinary depth-one substrate search through the API's read-only host."""
    url = api_base(base) + "/chess/eval"
    payload = {"fen": "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "depth": 1, "substrate": True}
    result = {"url": url, "method": "POST", "request": payload, "http_status": None}
    started = time.monotonic()
    try:
        request = urllib.request.Request(url, data=json.dumps(payload).encode(),
                                         headers=normal_api_headers(), method="POST")
        try:
            response = (opener or local_http_opener()).open(request, timeout=timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            result["http_status"] = response.code
            raw = response.read(65537)
        if len(raw) > 65536:
            raise ValueError("chess response exceeds bound")
        result["response_sha256"] = hashlib.sha256(raw).hexdigest()
        body = json.loads(raw)
        if not isinstance(body, dict) or type(body.get("depth")) is not int or body["depth"] != 1 \
                or type(body.get("nodes")) is not int or body["nodes"] <= 0 \
                or body.get("substrate") is not True:
            raise ValueError("chess search receipt unavailable")
        result["observed"] = {key: body[key] for key in ("depth", "nodes", "substrate")}
        result["status"] = "completed" if result["http_status"] == 200 else "failed"
    except (OSError, ValueError, urllib.error.URLError) as error:
        result.update(status="unavailable", error_type=type(error).__name__)
    result["request_wall_seconds"] = time.monotonic() - started
    return result


def gpu_observations(text, with_compute=True):
    devices = []
    for row in csv.reader(text.splitlines(), skipinitialspace=True):
        if not row:
            continue
        if len(row) != (7 if with_compute else 6):
            raise ValueError("NVIDIA query column count differs")
        index, identity, name, driver = [value.strip() for value in row[:4]]
        compute = row[4].strip() if with_compute else None
        total, free = [value.strip() for value in row[-2:]]
        devices.append({"index": int(index), "uuid": identity, "name": name, "driver_version": driver,
            "compute_capability": compute if compute and re.fullmatch(r"\d+\.\d+", compute) else None,
            "memory_total_bytes": int(total) * MIB if total.isdigit() else None,
            "memory_free_bytes": int(free) * MIB if free.isdigit() else None})
    return devices


def lichess_readiness(timeout, opener=None, base="http://127.0.0.1:5187"):
    """Observe the existing managed bot's verified account and event-stream state."""
    url = api_base(base) + "/chess/lichess/status"
    started = time.monotonic()
    result = {"url": url, "ready": False, "http_status": None,
              "scope": "One managed service observation of account prerequisites and event-stream connectivity; no account changes, challenge or game."}
    opener = opener or local_http_opener()
    try:
        try:
            response = opener.open(urllib.request.Request(url, headers=normal_api_headers()), timeout=timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            result["http_status"] = response.code
            raw = response.read(65537)
        if len(raw) > 65536:
            raise ValueError("Lichess status response exceeds bound")
        body = json.loads(raw)
        flags = ("configured", "running", "connected", "substrate")
        if not isinstance(body, dict) or any(type(body.get(name)) is not bool for name in flags):
            raise ValueError("Lichess service status booleans unavailable")
        observed = {name: body[name] for name in flags}
        for name in ("depth", "maxConcurrent", "gamesRecorded"):
            if type(body.get(name)) is not int or body[name] < 0:
                raise ValueError("Lichess service counts unavailable")
            observed[name] = body[name]
        observed["error_present"] = body.get("error") is not None
        account = body.get("account")
        observed["account"] = None
        if account is not None:
            account_flags = ("tokenValid", "botAccount", "botPlayScope")
            if not isinstance(account, dict) or any(
                    account.get(name) is not None and type(account[name]) is not bool for name in account_flags):
                raise ValueError("Lichess account status booleans invalid")
            selected = {name: account.get(name) for name in account_flags}
            selected["username_present"] = isinstance(account.get("username"), str) and bool(account["username"].strip())
            selected["error_present"] = account.get("error") is not None
            ready = all(selected[name] is True for name in account_flags) and selected["username_present"] and not selected["error_present"]
            if type(account.get("ready")) is not bool or account["ready"] != ready:
                raise ValueError("Lichess account readiness contradicts its prerequisites")
            selected["ready"] = ready
            observed["account"] = selected
        result["observed"] = observed
        result["response_sha256"] = hashlib.sha256(raw).hexdigest()
        result["ready"] = result["http_status"] == 200 and all(body[name] for name in ("configured", "running", "connected")) \
            and not observed["error_present"] and observed["account"] is not None and observed["account"]["ready"]
        result["status"] = "ready" if result["ready"] else "not-ready"
    except (OSError, ValueError, urllib.error.URLError) as error:
        result.update(status="unavailable", error_type=type(error).__name__)
    result["elapsed_seconds"] = time.monotonic() - started
    return result


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
            result["source_networks"] = [{"name": name, "path": str(candidate / "src" / name),
                                           "present": (candidate / "src" / name).is_file(),
                                           "size_bytes": (candidate / "src" / name).stat().st_size if (candidate / "src" / name).is_file() else None,
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


def parse_pgn(text, expected, *, allow_adjudication=False, max_moves=0, provider=None):
    if allow_adjudication != (max_moves > 0):
        raise ValueError("PGN adjudication requires the explicit diagnostic move cap")
    return chess_pgn.validate_games(text, expected, provider=provider, max_moves=max_moves)


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
    command = [str(cutechess), "-engine", *first, "-engine", "name=Stockfish-B", "cmd=" + str(stockfish), *settings,
            "-each", "tc=inf", "depth=" + str(args.match_depth), "-rounds", str(games), "-concurrency", str(concurrency),
            "-pgnout", str(pgn), "-debug", "all"]
    if args.max_moves:
        command.extend(["-maxmoves", str(args.max_moves)])
    return command


def recommendations(report):
    sf = [case for case in report["stockfish_bench"] if case.get("status") == "complete"]
    cc = [case for case in report["cutechess_matches"] if case.get("status") == "complete"
          and report["parameters"].get("max_moves", 0) == 0]
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
            "scope": "Measured complete Stockfish self-play games with pondering off; Laplace capacity requires its own paired measurement."}
    if report["parameters"].get("max_moves", 0):
        result["tournament_diagnostic_only"] = "Move-limited runs establish no complete-game capacity or tournament configuration recommendation."
    return result
