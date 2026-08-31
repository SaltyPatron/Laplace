#!/usr/bin/env python3
"""Single executable authority for Laplace test profiles.

Profiles are selected before execution from scripts/test-profiles.json. Required
suites are enumerated first; selected=0 is a hard failure. Every run writes one
machine-readable receipt and, under Actions, the same facts to GITHUB_STEP_SUMMARY.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import tempfile
import time
from typing import Any

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_REGISTRY = ROOT / "scripts" / "test-profiles.json"
DEFAULT_RECEIPT_DIR = ROOT / "build" / "test-receipts"

ALLOWED_PROFILES = {"policy", "dev-native", "dev-managed", "db", "live", "perf"}
ALLOWED_RUNNERS = {"ctest", "dotnet", "script", "browser"}
ALLOWED_MUTABILITY = {"none", "disposable-db", "shared-read", "measurement"}
ALLOWED_SHARED = {"forbidden", "required", "not-applicable"}
ALLOWED_AUTHORITIES = {
    "policy", "delivery", "database-qa", "product-evidence",
    "performance-evidence", "advisory",
}
ALLOWED_TIMEOUTS = {"fast", "qa", "long", "perf"}
TIMEOUT_SECONDS = {"fast": 900, "qa": 3600, "long": 7200, "perf": 3600}
REQUIRED_FIELDS = {
    "id", "profile", "owner", "runner", "environment", "mutability",
    "shared_substrate", "blocking_authority", "timeout_class", "required",
}

PROFILE_GROUPS = {
    "policy": ("policy",),
    "dev": ("dev-native", "dev-managed"),
    "dev-native": ("dev-native",),
    "dev-managed": ("dev-managed",),
    "db": ("db",),
    "live": ("live",),
    "perf": ("perf",),
    "all": ("dev-native", "dev-managed", "db"),
}


class RegistryError(ValueError):
    pass


def _string(value: Any, where: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise RegistryError(f"{where} must be a non-empty string")
    return value


def load_document(path: Path = DEFAULT_REGISTRY) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RegistryError(f"cannot load {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise RegistryError("registry root must be an object")
    return value


def validate_document(doc: dict[str, Any]) -> dict[str, dict[str, Any]]:
    if doc.get("schema_version") != 1:
        raise RegistryError("schema_version must be 1")
    suites = doc.get("suites")
    if not isinstance(suites, list) or not suites:
        raise RegistryError("suites must be a non-empty array")

    by_id: dict[str, dict[str, Any]] = {}
    profiles: set[str] = set()
    for index, suite in enumerate(suites):
        where = f"suites[{index}]"
        if not isinstance(suite, dict):
            raise RegistryError(f"{where} must be an object")
        missing = sorted(REQUIRED_FIELDS - suite.keys())
        if missing:
            raise RegistryError(f"{where} missing fields: {', '.join(missing)}")
        suite_id = _string(suite["id"], f"{where}.id")
        if suite_id in by_id:
            raise RegistryError(f"duplicate suite id: {suite_id}")
        profile = _string(suite["profile"], f"{where}.profile")
        runner = _string(suite["runner"], f"{where}.runner")
        _string(suite["owner"], f"{where}.owner")
        _string(suite["environment"], f"{where}.environment")
        mutability = _string(suite["mutability"], f"{where}.mutability")
        shared = _string(suite["shared_substrate"], f"{where}.shared_substrate")
        authority = _string(suite["blocking_authority"], f"{where}.blocking_authority")
        timeout_class = _string(suite["timeout_class"], f"{where}.timeout_class")
        if type(suite["required"]) is not bool:
            raise RegistryError(f"{where}.required must be boolean")
        if profile not in ALLOWED_PROFILES:
            raise RegistryError(f"{where}.profile has unsupported value: {profile}")
        if runner not in ALLOWED_RUNNERS:
            raise RegistryError(f"{where}.runner has unsupported value: {runner}")
        if mutability not in ALLOWED_MUTABILITY:
            raise RegistryError(f"{where}.mutability has unsupported value: {mutability}")
        if shared not in ALLOWED_SHARED:
            raise RegistryError(f"{where}.shared_substrate has unsupported value: {shared}")
        if authority not in ALLOWED_AUTHORITIES:
            raise RegistryError(f"{where}.blocking_authority has unsupported value: {authority}")
        if timeout_class not in ALLOWED_TIMEOUTS:
            raise RegistryError(f"{where}.timeout_class has unsupported value: {timeout_class}")

        if runner == "ctest":
            selector = suite.get("selector")
            if not isinstance(selector, dict) or len(selector) != 1:
                raise RegistryError(f"{where}.selector must contain exactly one CTest label selector")
            key = next(iter(selector))
            if key not in {"include_label", "exclude_label"}:
                raise RegistryError(f"{where}.selector must use include_label or exclude_label")
            _string(selector[key], f"{where}.selector.{key}")
        elif runner == "dotnet":
            _string(suite.get("project"), f"{where}.project")
            selector = suite.get("selector")
            if not isinstance(selector, dict) or set(selector) != {"filter"}:
                raise RegistryError(f"{where}.selector for dotnet must contain exactly filter")
            _string(selector["filter"], f"{where}.selector.filter")
        else:
            command = suite.get("command")
            if not isinstance(command, list) or not command:
                raise RegistryError(f"{where}.command must be a non-empty argv array")
            for part in command:
                _string(part, f"{where}.command")

        if profile in {"dev-native", "dev-managed"} and (
            mutability != "none" or shared != "forbidden"
        ):
            raise RegistryError(
                f"{where}: DEV suites must be non-mutating and forbid shared substrate"
            )
        if profile == "db" and shared != "forbidden":
            raise RegistryError(f"{where}: DB suites must forbid the standing shared substrate")
        if profile == "live" and shared != "required":
            raise RegistryError(f"{where}: live suites must require the standing substrate")
        if profile == "perf" and mutability != "measurement":
            raise RegistryError(f"{where}: perf suites must be measurement-only")

        by_id[suite_id] = suite
        profiles.add(profile)

    missing_profiles = ALLOWED_PROFILES - profiles
    if missing_profiles:
        raise RegistryError(
            "registry missing executable profiles: " + ", ".join(sorted(missing_profiles))
        )
    return by_id


def load_validated(path: Path = DEFAULT_REGISTRY) -> dict[str, dict[str, Any]]:
    return validate_document(load_document(path))


def suites_for_request(
    suites: dict[str, dict[str, Any]], request: str
) -> list[dict[str, Any]]:
    groups = PROFILE_GROUPS.get(request)
    if groups is None:
        raise RegistryError(f"unknown profile: {request}")
    result = [suite for suite in suites.values() if suite["profile"] in groups]
    if not result:
        raise RegistryError(f"profile {request} selected zero suites")
    return result


def _env_for_suite(suite: dict[str, Any]) -> dict[str, str]:
    env = dict(os.environ)
    if suite["profile"] == "dev-managed":
        candidates = sorted(ROOT.glob("build/**/laplace_t0_perfcache*.bin"))
        if candidates:
            env["LAPLACE_PERFCACHE_BIN"] = str(candidates[-1])
    elif suite["profile"] in {"db", "live", "perf"}:
        prefix = Path(env.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
        candidates = sorted((prefix / "share/laplace").glob("laplace_t0_perfcache*.bin"))
        if not candidates:
            candidates = sorted(ROOT.glob("build/**/laplace_t0_perfcache*.bin"))
        if candidates:
            env["LAPLACE_PERFCACHE_BIN"] = str(candidates[-1])
    return env


def command_for_suite(suite: dict[str, Any], *, list_only: bool = False) -> list[str]:
    runner = suite["runner"]
    if runner == "ctest":
        command = ["ctest", "--test-dir", "build"]
        if list_only:
            command.append("-N")
        else:
            command += [
                "--output-on-failure", "-j",
                os.environ.get("CTEST_PARALLEL_LEVEL", str(os.cpu_count() or 1)),
            ]
        selector = suite["selector"]
        if "include_label" in selector:
            command += ["-L", selector["include_label"]]
        else:
            command += ["-LE", selector["exclude_label"]]
        return command
    if runner == "dotnet":
        command = ["dotnet", "test", suite["project"]]
        if list_only:
            command.append("--list-tests")
        command += [
            "-c", "Release", "--no-build", "--nologo", "--verbosity", "minimal",
            "--filter", suite["selector"]["filter"],
        ]
        return command
    return list(suite["command"])


def _run(
    command: list[str], env: dict[str, str], timeout_seconds: int
) -> tuple[int, str, int]:
    started = time.monotonic_ns()
    try:
        proc = subprocess.run(
            command, cwd=ROOT, env=env, text=True,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=timeout_seconds,
        )
        returncode, output = proc.returncode, proc.stdout
    except subprocess.TimeoutExpired as exc:
        returncode = 124
        raw = exc.stdout or ""
        output = raw.decode() if isinstance(raw, bytes) else raw
        output += f"\nTIMEOUT after {timeout_seconds}s\n"
    elapsed_ms = (time.monotonic_ns() - started) // 1_000_000
    return returncode, output, int(elapsed_ms)


def _count_dotnet_list(output: str) -> int:
    count = 0
    active = False
    for line in output.splitlines():
        if "The following Tests are available:" in line:
            active = True
            continue
        if active:
            if line.startswith("    ") and line.strip():
                count += 1
            elif line.strip() and not line.startswith(" "):
                active = False
    return count


def selected_count(suite: dict[str, Any]) -> tuple[int, str]:
    if suite["runner"] in {"script", "browser"}:
        return 1, "declared executable suite"
    rc, output, _ = _run(
        command_for_suite(suite, list_only=True), _env_for_suite(suite),
        TIMEOUT_SECONDS[suite["timeout_class"]],
    )
    if rc != 0:
        raise RegistryError(f"selection failed for {suite['id']}:\n{output}")
    if suite["runner"] == "ctest":
        match = re.findall(r"Total Tests:\s*(\d+)", output)
        count = int(match[-1]) if match else 0
    else:
        count = _count_dotnet_list(output)
    return count, output


def _result_counts(
    suite: dict[str, Any], selected: int, output: str
) -> tuple[int, int]:
    if suite["runner"] in {"script", "browser"}:
        return 1, 0
    if suite["runner"] == "ctest":
        skipped = sum(1 for line in output.splitlines() if "***Skipped" in line)
        return max(0, selected - skipped), skipped
    totals = [int(value) for value in re.findall(r"Total:\s*(\d+)", output)]
    skips = [int(value) for value in re.findall(r"Skipped:\s*(\d+)", output)]
    observed = sum(totals)
    skipped = sum(skips)
    if observed < selected:
        raise RegistryError(
            f"{suite['id']} selection/result drift: selected={selected} result_total={observed}"
        )
    return max(0, observed - skipped), skipped


def _sha256(path: Path) -> str | None:
    if not path.is_file():
        return None
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _git_sha() -> str:
    proc = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
    )
    return proc.stdout.strip() if proc.returncode == 0 else "unknown"


def _write_receipt(path: Path, receipt: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(receipt, stream, indent=2, sort_keys=True)
            stream.write("\n")
        os.replace(temp_name, path)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass


def _append_summary(receipt: dict[str, Any]) -> None:
    destination = os.environ.get("GITHUB_STEP_SUMMARY")
    if not destination:
        return
    with open(destination, "a", encoding="utf-8") as out:
        out.write(f"\n### Test profile `{receipt['profile']}` — {receipt['status']}\n\n")
        out.write("| suite | selected | executed | skipped | status | ms |\n")
        out.write("|---|---:|---:|---:|---|---:|\n")
        for suite in receipt["suites"]:
            out.write(
                f"| `{suite['id']}` | {suite['selected']} | {suite['executed']} | "
                f"{suite['skipped']} | {suite['status']} | {suite['elapsed_ms']} |\n"
            )
        out.write(
            f"\nsource `{receipt['source_sha']}` · selected={receipt['selected']} · "
            f"executed={receipt['executed']} · skipped={receipt['skipped']}\n"
        )


def _finish_receipt(
    request: str, started_wall: float, records: list[dict[str, Any]], status: str
) -> dict[str, Any]:
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    ended_wall = time.time()
    return {
        "schema_version": 1,
        "profile": request,
        "source_sha": _git_sha(),
        "built_native_sha256": _sha256(ROOT / "build/engine/core/liblaplace_core.so"),
        "installed_native_sha256": _sha256(prefix / "lib/liblaplace_core.so"),
        "started_at_unix_ms": int(started_wall * 1000),
        "ended_at_unix_ms": int(ended_wall * 1000),
        "elapsed_ms": int((ended_wall - started_wall) * 1000),
        "selected": sum(item["selected"] for item in records),
        "executed": sum(item["executed"] for item in records),
        "skipped": sum(item["skipped"] for item in records),
        "status": status,
        "suites": records,
    }


def run_profile(request: str, registry_path: Path, receipt_path: Path | None) -> int:
    suites = load_validated(registry_path)
    chosen = suites_for_request(suites, request)
    started_wall = time.time()
    records: list[dict[str, Any]] = []

    for suite in chosen:
        selected, _ = selected_count(suite)
        record = {
            "id": suite["id"],
            "profile": suite["profile"],
            "runner": suite["runner"],
            "discovered": selected,
            "selected": selected,
            "executed": 0,
            "skipped": 0,
            "status": "selected",
            "elapsed_ms": 0,
        }
        records.append(record)
        if suite["required"] and selected == 0:
            record["status"] = "failed-zero-selection"
            receipt = _finish_receipt(request, started_wall, records, "failed")
            target = receipt_path or DEFAULT_RECEIPT_DIR / f"{request}.json"
            _write_receipt(target, receipt)
            _append_summary(receipt)
            print(
                f"test-profile: ERROR required suite {suite['id']} selected=0",
                file=sys.stderr,
            )
            return 1

    status = "success"
    for suite, record in zip(chosen, records):
        command = command_for_suite(suite)
        print(f"==== {suite['id']}: {shlex.join(command)} ====")
        rc, output, elapsed_ms = _run(
            command, _env_for_suite(suite), TIMEOUT_SECONDS[suite["timeout_class"]]
        )
        sys.stdout.write(output)
        record["elapsed_ms"] = elapsed_ms
        try:
            executed, skipped = _result_counts(suite, record["selected"], output)
            if suite["runner"] == "dotnet":
                record["selected"] = executed + skipped
        except RegistryError as exc:
            print(f"test-profile: ERROR {exc}", file=sys.stderr)
            executed, skipped, rc = 0, 0, 1
        record["executed"] = executed
        record["skipped"] = skipped
        record["status"] = "success" if rc == 0 else "failed"
        if rc != 0:
            status = "failed"
            break

    receipt = _finish_receipt(request, started_wall, records, status)
    target = receipt_path or DEFAULT_RECEIPT_DIR / f"{request}.json"
    _write_receipt(target, receipt)
    _append_summary(receipt)
    print(
        f"TEST_PROFILE profile={request} status={status} selected={receipt['selected']} "
        f"executed={receipt['executed']} skipped={receipt['skipped']} receipt={target}"
    )
    return 0 if status == "success" else 1


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--registry", type=Path, default=DEFAULT_REGISTRY)
    sub = p.add_subparsers(dest="command", required=True)
    sub.add_parser("validate")
    listing = sub.add_parser("list")
    listing.add_argument("--profile")
    run = sub.add_parser("run")
    run.add_argument("--profile", required=True, choices=sorted(PROFILE_GROUPS))
    run.add_argument("--receipt", type=Path)
    return p


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        suites = load_validated(args.registry)
        if args.command == "validate":
            print(f"test-profile-registry: OK suites={len(suites)}")
            return 0
        if args.command == "list":
            values = (
                suites.values()
                if not args.profile
                else suites_for_request(suites, args.profile)
            )
            for suite in values:
                print(suite["id"])
            return 0
        return run_profile(args.profile, args.registry, args.receipt)
    except RegistryError as exc:
        print(f"test-profile-registry: ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
