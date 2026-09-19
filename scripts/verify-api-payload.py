#!/usr/bin/env python3
"""Seal and verify the API publication owned by publish-applications.sh.

This checks the app-local build form. Installed PostgreSQL/native compatibility
remains owned by check-application-runtime.py. It never reads service secrets.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
MAX_FILES = 20000
MAX_MANIFEST = 16 * 1024 * 1024
REQUIRED = ("Laplace.Endpoints.OpenAICompat.dll", "Laplace.Core.dll",
            "Laplace.Chess.dll", "liblaplace_core.so", "liblaplace_dynamics.so",
            "liblaplace_synthesis.so", "liblaplace_syzygy.so")
MAPPED = REQUIRED[:4]
UCI_REQUIRED = ("laplace-uci", "laplace-uci.dll", "laplace-uci.deps.json",
                "laplace-uci.runtimeconfig.json", "Laplace.Core.dll", "Laplace.Chess.dll",
                *REQUIRED[3:])


def owner(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


floor = owner("api_payload_floor", "verify-chess-floor-serving.py")
release = owner("api_payload_release", "verify-application-release.py")


def save(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(value, stream, sort_keys=True, allow_nan=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def files(directory, required=REQUIRED, spa=True, wrapped_uci=False):
    directory = Path(directory).resolve(strict=True)
    result = {}
    for root, dirs, names in os.walk(directory, followlinks=False):
        for name in dirs + names:
            path = Path(root) / name
            entry = path.lstat()
            if wrapped_uci and path == directory / "logs":
                if not stat.S_ISLNK(entry.st_mode) or os.readlink(path) != "../../../logs":
                    raise ValueError("UCI runtime log link differs from its deployment owner")
                continue
            if stat.S_ISLNK(entry.st_mode):
                raise ValueError("API payload contains a symbolic link: " + str(path))
            if name in names:
                if not stat.S_ISREG(entry.st_mode):
                    raise ValueError("API payload contains a non-regular file")
                relative = path.relative_to(directory).as_posix()
                result[relative] = floor.fact(path)
                if len(result) > MAX_FILES:
                    raise ValueError("API payload exceeds declared file bound")
    for name in required:
        if name not in result:
            raise ValueError("API payload omitted " + name)
    if spa and "wwwroot/index.html" not in result:
        raise ValueError("API payload omitted the SPA document")
    return result


def native_sources(rows, native_build, native_installed=None, repo_root=ROOT):
    native = {}
    if native_build is not None and native_installed is not None:
        raise ValueError("select a build or installed native source, not both")
    if native_build is not None:
        repo_root = Path(repo_root).resolve(strict=True)
        build = (repo_root / "build").resolve(strict=True)
        native_build = Path(native_build).resolve(strict=True)
        if native_build != build / "engine":
            raise ValueError("API native source must be the selected repository build/engine")
        cache = (build / "CMakeCache.txt").read_text(encoding="utf-8")
        matches = re.findall(r"^CMAKE_HOME_DIRECTORY:INTERNAL=(.+)$", cache, re.MULTILINE)
        if len(matches) != 1 or Path(matches[0]).resolve(strict=True) != repo_root:
            raise ValueError("API native build belongs to another source checkout")
        # This is the exact Directory.Build.props Linux publication set, including
        # SONAME aliases and Syzygy. Compare dereferenced build bytes, not install RPATHs.
        for component in ("core", "dynamics", "synthesis"):
            for path in sorted((native_build / component).glob("*.so*")):
                if path.name in native:
                    raise ValueError("duplicate native publication filename")
                expected = floor.fact(path)
                actual = rows.get(path.name)
                if actual is None or (actual["sha256"], actual["bytes"]) != (
                        expected["sha256"], expected["bytes"]):
                    raise ValueError("published native bytes differ from build: " + path.name)
                native[path.name] = expected
        for name in REQUIRED[3:]:
            if name not in native:
                raise ValueError("selected native build omitted " + name)
        engine_name = re.compile(r"liblaplace_(?:core|dynamics|synthesis|syzygy)\.so(?:\..+)?$")
        published = {name for name in rows if engine_name.fullmatch(name)}
        expected_engine = {name for name in native if engine_name.fullmatch(name)}
        if published != expected_engine:
            raise ValueError("API native publication set differs from build")
    elif native_installed is not None:
        native_installed = Path(native_installed).resolve(strict=True)
        for path in sorted(native_installed.glob("liblaplace_*.so*")):
            if path.name in native:
                raise ValueError("duplicate installed native publication filename")
            expected = floor.fact(path)
            actual = rows.get(path.name)
            if actual is None or (actual["sha256"], actual["bytes"]) != (
                    expected["sha256"], expected["bytes"]):
                raise ValueError("published native bytes differ from installed closure: " + path.name)
            native[path.name] = expected
        for name in REQUIRED[3:]:
            if name not in native:
                raise ValueError("installed native closure omitted " + name)
        engine_name = re.compile(r"liblaplace_(?:core|dynamics|synthesis|syzygy)\.so(?:\..+)?$")
        published = {name for name in rows if engine_name.fullmatch(name)}
        expected_engine = {name for name in native if engine_name.fullmatch(name)}
        if published != expected_engine:
            raise ValueError("API native publication set differs from installed closure")
    return native, native_build, native_installed, repo_root


def seal(directory, native_build=None, repo_root=ROOT, *, native_installed=None):
    rows = files(directory)
    native, native_build, native_installed, repo_root = native_sources(
        rows, native_build, native_installed, repo_root)
    provenance = "selected-build" if native_build is not None else (
        "preserved-installed-native" if native_installed is not None else "rollback-snapshot")
    return {"schema": "laplace.api-payload/v1",
            "provenance": provenance,
            "native_build": str(native_build) if native_build is not None else None,
            "native_installed": str(native_installed) if native_installed is not None else None,
            "source_checkout": str(repo_root) if native_build is not None else None,
            "native_sources": native,
            "files": {name: {"sha256": item["sha256"], "bytes": item["bytes"]}
                      for name, item in rows.items()}}


def seal_uci(directory, native_build=None, repo_root=ROOT, wrapped=False):
    rows = files(directory, UCI_REQUIRED, spa=False, wrapped_uci=wrapped)
    if wrapped and "laplace-uci.native" not in rows:
        raise ValueError("installed UCI lease wrapper omitted its native apphost")
    native, native_build, _, repo_root = native_sources(rows, native_build, None, repo_root)
    return {"schema": "laplace.uci-payload/v1", "wrapped": wrapped,
            "provenance": "selected-build" if native_build is not None else "rollback-snapshot",
            "native_build": str(native_build) if native_build is not None else None,
            "source_checkout": str(repo_root) if native_build is not None else None,
            "native_sources": native,
            "files": {name: {"sha256": item["sha256"], "bytes": item["bytes"]}
                      for name, item in rows.items()}}


def read_manifest(path, uci=False):
    with Path(path).open("rb") as stream:
        raw = stream.read(MAX_MANIFEST + 1)
    if len(raw) > MAX_MANIFEST:
        raise ValueError("API payload manifest exceeds bound")
    value = json.loads(raw)
    rows = value.get("files")
    schema = "laplace.uci-payload/v1" if uci else "laplace.api-payload/v1"
    if value.get("schema") != schema or not isinstance(rows, dict):
        raise ValueError("invalid API payload manifest")
    if not 0 < len(rows) <= MAX_FILES:
        raise ValueError("invalid API payload file count")
    for name, item in rows.items():
        parts = Path(name).parts
        if (not parts or Path(name).is_absolute() or any(p in (".", "..") for p in parts)
                or Path(name).as_posix() != name or not isinstance(item, dict)
                or not re.fullmatch("[0-9a-f]{64}", str(item.get("sha256")))
                or type(item.get("bytes")) is not int or item["bytes"] < 0):
            raise ValueError("invalid API payload file identity")
    required = UCI_REQUIRED if uci else (*REQUIRED, "wwwroot/index.html")
    if uci and type(value.get("wrapped")) is not bool:
        raise ValueError("invalid UCI wrapper disposition")
    if uci and value["wrapped"]:
        required = (*required, "laplace-uci.native")
    if any(name not in rows for name in required):
        raise ValueError("API payload manifest is incomplete")
    return value


def installed(directory, manifest, remap=None):
    directory = Path(directory).resolve(strict=True)
    result = {}
    for name, expected in manifest["files"].items():
        path = directory / (remap or {}).get(name, name)
        if path.is_symlink() or not path.resolve(strict=True).is_relative_to(directory):
            raise ValueError("installed API file escapes its payload")
        actual = floor.fact(path)
        if (actual["sha256"], actual["bytes"]) != (expected["sha256"], expected["bytes"]):
            raise ValueError("installed API bytes differ from sealed payload: " + name)
        result[name] = actual
    return result


def service():
    properties = ("Id", "LoadState", "ActiveState", "SubState", "MainPID",
                  "User", "Group", "UnitFileState", "ActiveEnterTimestamp",
                  "ExecMainStartTimestamp")
    output = subprocess.run(
        ["systemctl", "show", "laplace-api.service", "--no-pager",
         "--property=" + ",".join(properties)],
        check=True, capture_output=True, text=True, timeout=10).stdout
    if len(output) > 65536:
        raise ValueError("API service observation exceeds bound")
    value = dict(line.split("=", 1) for line in output.splitlines() if "=" in line)
    if (value.get("Id") != "laplace-api.service" or value.get("LoadState") != "loaded"
            or value.get("ActiveState") != "active" or value.get("SubState") != "running"
            or not value.get("MainPID", "").isdigit() or int(value["MainPID"]) <= 0):
        raise ValueError("API service is not running")
    return value


def health(base, pid):
    status, content_type, body = release.request("GET", base + "/health/ready")
    if status not in (200, 503) or "json" not in content_type.lower():
        raise ValueError("API readiness response is invalid")
    value = release.json_object(body, "readiness")
    release.classify_readiness(value)
    chess = value.get("chess_perfcache")
    if (not isinstance(chess, dict) or type(chess.get("process_id")) is not int
            or chess["process_id"] != pid):
        raise ValueError("API readiness process differs from systemd MainPID")
    return value


def verify(directory, manifest, base, timeout_seconds):
    base = floor.local_base(base)
    first = installed(directory, manifest)
    has_data, product_ready = release.wait_for_readiness(base, timeout_seconds)
    release.verify_spa(base)
    release.verify_typed_operation(base)
    before = service()
    pid = int(before["MainPID"])
    health_before = health(base, pid)
    mapped = {name: first[name] for name in MAPPED}
    process_before = floor.process(pid, mapped)
    # An actual typed operation is bracketed by the same serving identity.
    release.verify_typed_operation(base)
    health_after = health(base, pid)
    process_after = floor.process(pid, mapped)
    after = service()
    if before["MainPID"] != after["MainPID"] or process_before != process_after:
        raise ValueError("API serving process changed during verification")
    last = installed(directory, manifest)
    if first != last:
        raise ValueError("API payload changed during verification")
    # Readiness/typed operation need not touch dynamics/synthesis/Syzygy. They
    # are present and byte-verified; do not claim execution of lazy libraries.
    return {"schema": "laplace.api-payload-verification/v1", "status": "passed",
            "has_data": has_data, "product_ready": product_ready,
            "boot_id": floor.acceptance.boot_id(),
            "service": after, "process": process_after,
            "health_before": health_before, "health_after": health_after,
            "payload": last, "provenance": manifest["provenance"],
            "native_sources": manifest["native_sources"],
            "unexercised_native": [name for name in REQUIRED[4:]]}


def verify_uci(executable, manifest):
    """Verify the existing lease launcher and exact closure, then run pure UCI search."""
    executable = Path(executable)
    selected = executable.resolve(strict=True)
    if selected.name != "laplace-uci" or selected.is_symlink():
        raise ValueError("selected UCI is not the installed lease launcher")
    remap = {} if manifest["wrapped"] else {"laplace-uci": "laplace-uci.native"}
    before = installed(selected.parent, manifest, remap)
    wrapper = floor.fact(selected)
    lease = selected.parent.parent / ".runtime-lease"
    if not lease.is_file() or lease.is_symlink():
        raise ValueError("UCI immutable runtime lease is unavailable")
    runtime = owner("uci_payload_protocol", "check-uci-runtime.py")
    move = runtime.check_runtime(executable)
    after = installed(selected.parent, manifest, remap)
    if (before != after or wrapper != floor.fact(selected)
            or executable.resolve(strict=True) != selected):
        raise ValueError("UCI selection changed during runtime verification")
    return {"schema": "laplace.uci-payload-verification/v1", "status": "passed",
            "scope": "published closure and pure-search UCI packaging; substrate acceptance is separate",
            "selected_executable": str(selected), "launcher": wrapper,
            "payload": after, "bestmove": move, "native_sources": manifest["native_sources"],
            "provenance": manifest["provenance"], "substrate_access_verified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seal-payload", type=Path)
    parser.add_argument("--seal-uci", type=Path)
    parser.add_argument("--wrapped-uci", action="store_true")
    parser.add_argument("--verify-uci", type=Path)
    parser.add_argument("--native-build", type=Path)
    parser.add_argument("--native-installed", type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--app-dir", type=Path)
    parser.add_argument("--receipt", type=Path)
    parser.add_argument("--base", default="http://127.0.0.1:5187")
    parser.add_argument("--timeout-seconds", type=float, default=60)
    args = parser.parse_args()
    if args.seal_uci or args.verify_uci:
        if args.seal_payload or args.app_dir or (args.seal_uci and args.verify_uci):
            parser.error("select one API or UCI payload operation")
        if args.seal_uci:
            if args.receipt:
                parser.error("UCI sealing requires a manifest, not a runtime receipt")
            save(args.manifest, seal_uci(args.seal_uci, args.native_build, wrapped=args.wrapped_uci))
            return 0
        if args.native_build or args.native_installed or args.wrapped_uci or not args.receipt:
            parser.error("UCI runtime verification requires only --manifest and --receipt")
        started = time.monotonic()
        receipt = {"schema": "laplace.uci-payload-verification/v1", "status": "failed"}
        try:
            with floor.acceptance.deadline(args.timeout_seconds + 30):
                receipt = verify_uci(args.verify_uci, read_manifest(args.manifest, uci=True))
            return 0
        except (OSError, ValueError, RuntimeError, KeyError, TypeError,
                subprocess.SubprocessError) as error:
            receipt["error"] = {"type": type(error).__name__, "message": str(error)}
            print("UCI payload verification failed: " + str(error), file=sys.stderr)
            return 1
        finally:
            receipt["elapsed_seconds"] = time.monotonic() - started
            save(args.receipt, receipt)
    if args.wrapped_uci:
        parser.error("--wrapped-uci requires --seal-uci")
    if args.seal_payload:
        if args.app_dir or args.receipt:
            parser.error("sealing and runtime verification are distinct operations")
        save(args.manifest, seal(
            args.seal_payload, args.native_build, native_installed=args.native_installed))
        return 0
    if not args.app_dir or not args.receipt or args.native_build or args.native_installed:
        parser.error("runtime verification requires --app-dir and --receipt")
    started = time.monotonic()
    receipt = {"schema": "laplace.api-payload-verification/v1", "status": "failed"}
    try:
        with floor.acceptance.deadline(args.timeout_seconds + 30):
            receipt = verify(args.app_dir, read_manifest(args.manifest),
                             args.base, args.timeout_seconds)
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError,
            subprocess.SubprocessError) as error:
        receipt["error"] = {"type": type(error).__name__, "message": str(error)}
        print("API payload verification failed: " + str(error), file=sys.stderr)
        return 1
    finally:
        receipt["elapsed_seconds"] = time.monotonic() - started
        save(args.receipt, receipt)


if __name__ == "__main__":
    raise SystemExit(main())
