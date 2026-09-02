#!/usr/bin/env python3
"""Versioned benchmark registry/runner for manual Laplace evidence lanes.

The workflow owns host isolation and source checkout/build. This runner owns which
benchmarks constitute a named suite, binds every benchmark to exact built artifacts,
and emits one machine-readable suite receipt.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
REGISTRY_PATH = ROOT / "scripts/benchmark-profiles.json"
DEFAULT_CORE = ROOT / "build/engine/core/liblaplace_core.so"
DEFAULT_T0 = ROOT / "build/engine/core/perfcache/laplace_t0_perfcache.bin"
VALID_KINDS = {"core-single", "core-scale", "moby-roundtrip"}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def git_sha() -> str:
    completed = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, check=True, capture_output=True, text=True
    )
    return completed.stdout.strip()


def load_registry(path: Path = REGISTRY_PATH) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def validate_registry(registry: dict[str, Any]) -> None:
    if registry.get("schema_version") != 1:
        raise ValueError("benchmark registry schema_version must be 1")
    profiles = registry.get("profiles")
    suites = registry.get("suites")
    if not isinstance(profiles, list) or not profiles:
        raise ValueError("benchmark registry requires non-empty profiles")
    if not isinstance(suites, list) or not suites:
        raise ValueError("benchmark registry requires non-empty suites")

    profile_ids: set[str] = set()
    for profile in profiles:
        if not isinstance(profile, dict):
            raise ValueError("profile must be an object")
        profile_id = profile.get("id")
        if not isinstance(profile_id, str) or not profile_id:
            raise ValueError("profile id must be non-empty")
        if profile_id in profile_ids:
            raise ValueError(f"duplicate benchmark profile {profile_id}")
        profile_ids.add(profile_id)
        if profile.get("kind") not in VALID_KINDS:
            raise ValueError(f"profile {profile_id} has unsupported kind {profile.get('kind')!r}")
        for required in ("description", "execution_boundary", "mutability", "resource_shape", "receipt_schema"):
            if not isinstance(profile.get(required), str) or not profile[required]:
                raise ValueError(f"profile {profile_id} missing {required}")

    suite_ids: set[str] = set()
    for suite in suites:
        if not isinstance(suite, dict):
            raise ValueError("suite must be an object")
        suite_id = suite.get("id")
        if not isinstance(suite_id, str) or not suite_id:
            raise ValueError("suite id must be non-empty")
        if suite_id in suite_ids:
            raise ValueError(f"duplicate benchmark suite {suite_id}")
        suite_ids.add(suite_id)
        selected = suite.get("profiles")
        if not isinstance(selected, list) or not selected:
            raise ValueError(f"suite {suite_id} has no profiles")
        unknown = set(selected) - profile_ids
        if unknown:
            raise ValueError(f"suite {suite_id} references unknown profiles {sorted(unknown)}")

    if not (ROOT / "scripts/bench-compose.py").is_file():
        raise ValueError("single-thread benchmark harness is missing")
    if not (ROOT / "scripts/bench-compose-scale.py").is_file():
        raise ValueError("scaling benchmark harness is missing")


def profile_map(registry: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {profile["id"]: profile for profile in registry["profiles"]}


def suite_map(registry: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {suite["id"]: suite for suite in registry["suites"]}


def run_and_tee(command: list[str], log_path: Path, env: dict[str, str]) -> tuple[int, int]:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter_ns()
    with log_path.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(
            command,
            cwd=ROOT,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
        )
        assert process.stdout is not None
        for line in process.stdout:
            sys.stdout.write(line)
            sys.stdout.flush()
            log.write(line)
        returncode = process.wait()
    return returncode, time.perf_counter_ns() - started


def capture_rapl() -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    root = Path("/sys/class/powercap")
    if not root.is_dir():
        return rows
    for zone in sorted(root.glob("intel-rapl:*")):
        try:
            energy = int((zone / "energy_uj").read_text().strip())
        except (OSError, ValueError):
            continue
        try:
            maximum = int((zone / "max_energy_range_uj").read_text().strip())
        except (OSError, ValueError):
            maximum = 0
        try:
            name = (zone / "name").read_text().strip()
        except OSError:
            name = zone.name
        rows.append({"path": str(zone), "name": name, "energy_uj": energy, "max_energy_range_uj": maximum})
    return rows


def rapl_delta(before: list[dict[str, Any]], after: list[dict[str, Any]]) -> list[dict[str, Any]]:
    indexed = {row["path"]: row for row in before}
    result: list[dict[str, Any]] = []
    for row in after:
        prior = indexed.get(row["path"])
        if prior is None:
            continue
        start = int(prior["energy_uj"])
        end = int(row["energy_uj"])
        maximum = int(row.get("max_energy_range_uj") or prior.get("max_energy_range_uj") or 0)
        if end >= start:
            delta = end - start
        elif maximum > 0:
            delta = (maximum - start) + end
        else:
            continue
        result.append({
            "path": row["path"],
            "name": row["name"],
            "energy_uj": delta,
            "boundary": "Intel RAPL domain; not whole-system wall energy",
        })
    return result


def exact_env(core: Path, t0: Path) -> dict[str, str]:
    env = dict(os.environ)
    env["LAPLACE_CORE"] = str(core)
    env["LAPLACE_T0"] = str(t0)
    env["LAPLACE_PERFCACHE_BIN"] = str(t0)
    env["LAPLACE_ENGINE_BUILD"] = str(ROOT / "build/engine")
    native_paths = [
        str(ROOT / "build/engine/core"),
        str(ROOT / "build/engine/dynamics"),
        str(ROOT / "build/engine/synthesis"),
    ]
    existing = env.get("LD_LIBRARY_PATH")
    if existing:
        native_paths.append(existing)
    env["LD_LIBRARY_PATH"] = ":".join(native_paths)
    return env


def parse_scaled_k(text: str) -> float:
    return float(text.replace(",", "")) * 1000.0


def parse_core_single(log_path: Path) -> dict[str, Any]:
    text = log_path.read_text(encoding="utf-8")
    throughput = list(re.finditer(
        r"([0-9,.]+)k codepoints/s\s+([0-9,.]+)k BPE-equiv tokens/s", text
    ))
    nodes = list(re.finditer(r"([0-9,.]+)k tier-tree nodes/s\s+\(([0-9,]+) nodes built\)", text))
    if not throughput or not nodes:
        raise ValueError("could not parse core-single benchmark output")
    rate = throughput[-1]
    node = nodes[-1]
    return {
        "schema": "laplace.benchmark.core-single/v1",
        "codepoints_per_second": parse_scaled_k(rate.group(1)),
        "bpe_equivalent_tokens_per_second_4chars": parse_scaled_k(rate.group(2)),
        "tier_tree_nodes_per_second": parse_scaled_k(node.group(1)),
        "tier_tree_nodes": int(node.group(2).replace(",", "")),
        "bpe_equivalence_chars_per_token": 4,
    }


def parse_moby(log_path: Path, source: Path, output: Path) -> dict[str, Any]:
    text = log_path.read_text(encoding="utf-8")
    ingest = re.search(
        r"ingest\s*:\s*([0-9,]+) bytes\s*→\s*([0-9,]+) tier-tree nodes \(([0-9,]+) codepoints\)\s*in\s*([0-9.]+) ms",
        text,
    )
    export = re.search(r"export\s*:\s*([0-9,]+) bytes\s*in\s*([0-9.]+) ms", text)
    if ingest is None or export is None:
        raise ValueError("could not parse Moby roundtrip output")
    source_hash = sha256(source)
    output_hash = sha256(output)
    if source_hash != output_hash or source.read_bytes() != output.read_bytes():
        raise ValueError("Moby output is not bit-perfect")
    return {
        "schema": "laplace.benchmark.moby-roundtrip/v1",
        "input_bytes": int(ingest.group(1).replace(",", "")),
        "tier_tree_nodes": int(ingest.group(2).replace(",", "")),
        "codepoints": int(ingest.group(3).replace(",", "")),
        "ingest_milliseconds": float(ingest.group(4)),
        "output_bytes": int(export.group(1).replace(",", "")),
        "export_milliseconds": float(export.group(2)),
        "sha256_input": source_hash,
        "sha256_output": output_hash,
        "bit_perfect": True,
    }


def run_profile(
    profile: dict[str, Any],
    receipt_dir: Path,
    env: dict[str, str],
    repeats: int,
    corpus_dir: Path,
    moby_path: Path,
    scale_workers: str | None,
) -> dict[str, Any]:
    profile_id = profile["id"]
    kind = profile["kind"]
    log_path = receipt_dir / f"{profile_id}.log"
    before = capture_rapl()
    if kind == "core-single":
        command = [sys.executable, "scripts/bench-compose.py", str(corpus_dir), "--repeats", str(repeats)]
    elif kind == "core-scale":
        json_path = receipt_dir / "core-scale.json"
        command = [
            sys.executable, "scripts/bench-compose-scale.py", str(corpus_dir),
            "--repeats", str(repeats), "--json", str(json_path),
        ]
        if scale_workers:
            command.extend(["--workers", scale_workers])
    elif kind == "moby-roundtrip":
        if not moby_path.is_file():
            raise FileNotFoundError(f"Moby fixture not found: {moby_path}")
        output = receipt_dir / "moby-roundtrip.out"
        command = [
            "dotnet", "run", "--project", "app/Laplace.Cli/Laplace.Cli.csproj",
            "-c", "Release", "--no-build", "--", "roundtrip", str(moby_path), str(output),
        ]
    else:
        raise ValueError(f"unsupported benchmark kind {kind}")

    print(f"\n== benchmark {profile_id}: {' '.join(command)} ==")
    returncode, wall_ns = run_and_tee(command, log_path, env)
    after = capture_rapl()
    if returncode != 0:
        raise RuntimeError(f"benchmark {profile_id} exited {returncode}; see {log_path}")

    if kind == "core-single":
        result = parse_core_single(log_path)
    elif kind == "core-scale":
        result = json.loads((receipt_dir / "core-scale.json").read_text(encoding="utf-8"))
    else:
        result = parse_moby(log_path, moby_path, receipt_dir / "moby-roundtrip.out")

    return {
        "profile": profile_id,
        "kind": kind,
        "execution_boundary": profile["execution_boundary"],
        "resource_shape": profile["resource_shape"],
        "command": command,
        "wall_nanoseconds_process_boundary": wall_ns,
        "rapl_energy": rapl_delta(before, after),
        "result": result,
    }


def run_suite(args: argparse.Namespace) -> int:
    registry = load_registry()
    validate_registry(registry)
    suites = suite_map(registry)
    profiles = profile_map(registry)
    if args.suite not in suites:
        raise SystemExit(f"unknown benchmark suite {args.suite!r}; choose from {', '.join(sorted(suites))}")
    if args.repeats < 1 or args.repeats > 100:
        raise SystemExit("--repeats must be in 1..100")

    receipt_dir = Path(args.receipt_dir).resolve()
    receipt_dir.mkdir(parents=True, exist_ok=True)
    core = Path(args.core or os.environ.get("LAPLACE_CORE", DEFAULT_CORE)).resolve()
    t0 = Path(args.t0 or os.environ.get("LAPLACE_T0", DEFAULT_T0)).resolve()
    if not core.is_file():
        raise SystemExit(f"built core library not found: {core}")
    if not t0.is_file():
        raise SystemExit(f"built T0 perfcache not found: {t0}")

    env = exact_env(core, t0)
    selected = suites[args.suite]
    source_sha = git_sha()
    artifact_identity = {
        "repository_sha": source_sha,
        "core_library": str(core),
        "core_sha256": sha256(core),
        "t0_perfcache": str(t0),
        "t0_sha256": sha256(t0),
    }
    (receipt_dir / "artifact-identities.json").write_text(
        json.dumps(artifact_identity, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )

    results = []
    started = time.time_ns()
    for profile_id in selected["profiles"]:
        results.append(run_profile(
            profiles[profile_id], receipt_dir, env, args.repeats,
            Path(args.corpus_dir).resolve(), Path(args.moby_path).resolve(), args.scale_workers,
        ))
    finished = time.time_ns()

    receipt = {
        "schema": "laplace.benchmark.suite/v1",
        "registry_schema_version": registry["schema_version"],
        "suite": args.suite,
        "suite_description": selected["description"],
        "profiles": selected["profiles"],
        "source_sha": source_sha,
        "artifact_identity": artifact_identity,
        "started_unix_nanoseconds": started,
        "finished_unix_nanoseconds": finished,
        "results": results,
    }
    (receipt_dir / "suite-receipt.json").write_text(
        json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    print(f"\nBENCHMARK_SUITE_OK suite={args.suite} profiles={','.join(selected['profiles'])}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("validate")
    sub.add_parser("list")
    run = sub.add_parser("run")
    run.add_argument("--suite", required=True)
    run.add_argument("--receipt-dir", required=True)
    run.add_argument("--repeats", type=int, default=3)
    run.add_argument("--corpus-dir", default=str(ROOT))
    run.add_argument("--moby-path", default="/vault/Data/test-data/text/moby_dick.txt")
    run.add_argument("--core")
    run.add_argument("--t0")
    run.add_argument("--scale-workers", help="optional comma-separated scaling points")
    args = parser.parse_args()

    registry = load_registry()
    if args.command == "validate":
        validate_registry(registry)
        print(f"BENCHMARK_REGISTRY_OK profiles={len(registry['profiles'])} suites={len(registry['suites'])}")
        return 0
    if args.command == "list":
        validate_registry(registry)
        for suite in registry["suites"]:
            print(f"{suite['id']:<12} {','.join(suite['profiles']):<40} {suite['description']}")
        return 0
    return run_suite(args)


if __name__ == "__main__":
    raise SystemExit(main())
