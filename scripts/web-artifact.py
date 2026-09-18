#!/usr/bin/env python3
"""Seal and verify the exact qualified web artifact consumed by publication."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import tempfile
from pathlib import Path

SCHEMA = 1


def sha256_file(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def dist_digest(dist: Path, *, ignore: frozenset[str] = frozenset()) -> str:
    if not dist.is_dir() or dist.is_symlink():
        raise ValueError(f"web dist directory is missing or not a real directory: {dist}")
    files = sorted(
        path for path in dist.rglob("*")
        if path.is_file() and path.relative_to(dist).as_posix() not in ignore
    )
    if not files:
        raise ValueError(f"web dist directory is empty: {dist}")
    value = hashlib.sha256()
    for path in files:
        if path.is_symlink():
            raise ValueError(f"web dist may not contain symlinks: {path}")
        relative = path.relative_to(dist).as_posix().encode("utf-8")
        value.update(len(relative).to_bytes(4, "big"))
        value.update(relative)
        value.update(bytes.fromhex(sha256_file(path)))
    return value.hexdigest()


def git_head(root: Path) -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=root,
        check=True,
        text=True,
        capture_output=True,
    ).stdout.strip()


def document(root: Path) -> dict:
    dist = root / "web" / "dist"
    lock = root / "web" / "package-lock.json"
    openapi = root / "web" / "openapi" / "openapi.json"
    if not lock.is_file():
        raise ValueError("web/package-lock.json is missing")
    if not openapi.is_file():
        raise ValueError("web/openapi/openapi.json is missing")
    return {
        "schema": SCHEMA,
        "source_sha": git_head(root),
        "dist_sha256": dist_digest(dist),
        "package_lock_sha256": sha256_file(lock),
        "openapi_sha256": sha256_file(openapi),
    }


def write_atomic(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
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


def verify(root: Path, manifest: Path) -> dict:
    try:
        expected = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"qualified web manifest is unreadable: {manifest}") from error
    current = document(root)
    if expected.get("schema") != SCHEMA:
        raise ValueError("qualified web manifest schema differs")
    for key in ("source_sha", "dist_sha256", "package_lock_sha256", "openapi_sha256"):
        if expected.get(key) != current[key]:
            raise ValueError(
                f"qualified web artifact differs for {key}: "
                f"expected {expected.get(key)!r}, current {current[key]!r}"
            )
    return current


def installed_verify(root: Path, manifest: Path, directory: Path) -> dict:
    expected = verify(root, manifest)
    directory = directory.resolve(strict=True)
    receipt = directory / ".laplace-web-source-revision"
    if receipt.is_symlink() or not receipt.is_file():
        raise ValueError("installed web artifact has no real source-revision receipt")
    source_sha = receipt.read_text(encoding="utf-8").strip()
    if source_sha != expected["source_sha"]:
        raise ValueError(
            f"installed web source differs: expected {expected['source_sha']}, found {source_sha}"
        )
    installed_digest = dist_digest(
        directory, ignore=frozenset({".laplace-web-source-revision"})
    )
    if installed_digest != expected["dist_sha256"]:
        raise ValueError(
            "installed web artifact differs: "
            f"expected {expected['dist_sha256']}, found {installed_digest}"
        )
    return {
        **expected,
        "installed_directory": str(directory),
        "installed_dist_sha256": installed_digest,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("seal", "verify", "verify-installed", "digest"))
    parser.add_argument("--root", default=".")
    parser.add_argument("--manifest")
    parser.add_argument("--directory")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    manifest = Path(args.manifest).resolve() if args.manifest else None
    directory = Path(args.directory).resolve() if args.directory else None
    try:
        if args.operation == "digest":
            if directory is None or manifest is not None:
                parser.error("digest requires --directory and no --manifest")
            print(dist_digest(directory))
            return 0
        if manifest is None:
            parser.error(f"{args.operation} requires --manifest")
        if args.operation == "seal":
            if directory is not None:
                parser.error("seal does not accept --directory")
            payload = document(root)
            write_atomic(manifest, payload)
            print(
                f"WEB_ARTIFACT_SEALED source={payload['source_sha']} "
                f"dist={payload['dist_sha256']} manifest={manifest}"
            )
        elif args.operation == "verify-installed":
            if directory is None:
                parser.error("verify-installed requires --directory")
            payload = installed_verify(root, manifest, directory)
            print(
                f"WEB_ARTIFACT_INSTALLED source={payload['source_sha']} "
                f"dist={payload['installed_dist_sha256']} directory={directory}"
            )
        else:
            if directory is not None:
                parser.error("verify does not accept --directory")
            payload = verify(root, manifest)
            print(
                f"WEB_ARTIFACT_VERIFIED source={payload['source_sha']} "
                f"dist={payload['dist_sha256']}"
            )
    except (ValueError, subprocess.CalledProcessError) as error:
        print(f"web-artifact: {error}", file=__import__("sys").stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
