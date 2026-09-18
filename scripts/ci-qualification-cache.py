#!/usr/bin/env python3
"""Persistent, content-addressed qualification receipts for mainline test suites."""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

SCHEMA_VERSION = 1

SUITE_SCOPES = {
    "native-dev": (
        "CMakeLists.txt",
        "cmake/",
        "engine/",
        "extension/",
        "scripts/",
    ),
    "managed-dev": (
        "CMakeLists.txt",
        "Directory.Build.*",
        "Directory.Packages.props",
        "global.json",
        "app/",
        "engine/",
        "extension/",
        "scripts/",
    ),
    "uci-dev": (
        "CMakeLists.txt",
        "Directory.Build.*",
        "global.json",
        "app/Laplace.Chess",
        "app/Laplace.Chess.Uci",
        "engine/",
        "scripts/",
    ),
    "browser-dev": (
        "web/",
        "app/Laplace.Api.Contracts",
        "app/Laplace.Endpoints.OpenAICompat",
        "scripts/",
    ),
}

TOOL_COMMANDS = {
    "native-dev": (("cmake", "--version"), ("ninja", "--version"), ("icpx", "--version")),
    "managed-dev": (("dotnet", "--version"),),
    "uci-dev": (("dotnet", "--version"),),
    "browser-dev": (("node", "--version"), ("npm", "--version")),
}

ENV_KEYS = (
    "LAPLACE_EXTERNAL",
    "LAPLACE_INSTALL_PREFIX",
    "LAPLACE_PG_PREFIX",
    "LAPLACE_UCD_PATH",
    "LAPLACE_DATA_ROOT",
)


def scope_matches(path: str, selector: str) -> bool:
    if selector.endswith("/"):
        return path.startswith(selector)
    if "*" in selector or "?" in selector or "[" in selector:
        return fnmatch.fnmatch(path, selector)
    return path == selector or path.startswith(selector + "/")


def tracked_index_entries(root: Path) -> list[tuple[str, str, str]]:
    result = subprocess.run(
        ["git", "ls-files", "-s", "-z"],
        cwd=root,
        check=True,
        capture_output=True,
    )
    entries: list[tuple[str, str, str]] = []
    for raw in result.stdout.split(b"\0"):
        if not raw:
            continue
        meta, path_bytes = raw.split(b"\t", 1)
        mode, sha, _stage = meta.decode("utf-8").split()
        entries.append((path_bytes.decode("utf-8"), mode, sha))
    return entries


def command_version(command: tuple[str, ...]) -> str:
    try:
        result = subprocess.run(
            list(command),
            check=False,
            text=True,
            capture_output=True,
            timeout=8,
        )
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return "<unavailable>"
    text = (result.stdout or result.stderr).strip().splitlines()
    return text[0] if text else f"<exit-{result.returncode}>"


def fingerprint(root: Path, suite: str) -> tuple[str, dict]:
    selectors = SUITE_SCOPES.get(suite)
    if not selectors:
        raise ValueError(f"unknown qualification suite: {suite}")

    files = [
        {"path": path, "mode": mode, "blob": sha}
        for path, mode, sha in tracked_index_entries(root)
        if any(scope_matches(path, selector) for selector in selectors)
    ]
    tools = {
        " ".join(command): command_version(command)
        for command in TOOL_COMMANDS.get(suite, ())
    }
    environment = {key: os.environ.get(key, "") for key in ENV_KEYS}
    document = {
        "schema": SCHEMA_VERSION,
        "suite": suite,
        "selectors": list(selectors),
        "files": files,
        "tools": tools,
        "environment": environment,
    }
    raw = json.dumps(document, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(raw).hexdigest(), document


def cache_root(value: str | None) -> Path:
    if value:
        return Path(value)
    return Path(os.environ.get("LAPLACE_QUALIFICATION_ROOT", "/build/laplace/work/qualification"))


def receipt_path(root: Path, suite: str, digest: str) -> Path:
    return root / suite / f"{digest}.json"


def read_receipt(path: Path, suite: str, digest: str) -> dict | None:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    if payload.get("schema") != SCHEMA_VERSION:
        return None
    if payload.get("suite") != suite or payload.get("fingerprint") != digest:
        return None
    if payload.get("result") != "passed":
        return None
    return payload


def record(root: Path, suite: str, digest: str, source_sha: str, inputs: dict) -> Path:
    path = receipt_path(root, suite, digest)
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "schema": SCHEMA_VERSION,
        "suite": suite,
        "fingerprint": digest,
        "source_sha": source_sha,
        "result": "passed",
        "qualified_at": datetime.now(timezone.utc).isoformat(),
        "inputs": inputs,
    }
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, sort_keys=True, indent=2)
            stream.write("\n")
        os.replace(temp_name, path)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass
    return path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("fingerprint", "check", "source", "record"))
    parser.add_argument("--suite", required=True, choices=tuple(SUITE_SCOPES))
    parser.add_argument("--root", default=".")
    parser.add_argument("--cache-root")
    parser.add_argument("--source-sha", default="")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    digest, inputs = fingerprint(root, args.suite)
    if args.operation == "fingerprint":
        print(digest)
        return 0

    receipts = cache_root(args.cache_root)
    path = receipt_path(receipts, args.suite, digest)

    if args.operation in ("check", "source"):
        payload = read_receipt(path, args.suite, digest)
        if payload is None:
            if args.operation == "check":
                print(f"QUALIFICATION_MISS suite={args.suite} fingerprint={digest}")
            return 1
        if args.operation == "source":
            print(payload.get("source_sha", ""))
            return 0
        print(
            f"QUALIFICATION_HIT suite={args.suite} fingerprint={digest} "
            f"qualified_source={payload.get('source_sha', '')}"
        )
        return 0

    source_sha = args.source_sha
    if not source_sha:
        source_sha = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=root,
            check=True,
            text=True,
            capture_output=True,
        ).stdout.strip()
    path = record(receipts, args.suite, digest, source_sha, inputs)
    print(f"QUALIFICATION_RECORDED suite={args.suite} fingerprint={digest} receipt={path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
