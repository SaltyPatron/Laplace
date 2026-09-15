"""Read-only observations of the configured native corpus build inputs.

Current checkouts and a matching core file are observations, not a build receipt
proving those checkouts produced that binary. No raw cache or credential-bearing
remote configuration is returned.
"""
import hashlib
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import urlsplit


PIN_PATHS = {
    "runtime": "external/tree-sitter",
    "cpp": "external/tree-sitter-grammars/tree-sitter-cpp",
}


def _public_origin(value):
    value = value.strip()
    if value.startswith("git@github.com:"):
        value = "https://github.com/" + value[len("git@github.com:"):]
    try:
        parsed = urlsplit(value)
    except ValueError:
        return {"status": "redacted"}
    if (parsed.scheme != "https" or parsed.netloc != "github.com"
            or parsed.username or parsed.password or parsed.query or parsed.fragment
            or not re.fullmatch(r"/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?", parsed.path)):
        return {"status": "redacted"}
    path = parsed.path.rstrip("/").removesuffix(".git")
    return {"status": "public-url", "url": "https://github.com" + path}


def _file(path):
    path = Path(path).absolute()
    result = {"path": str(path), "resolved_path": str(path.resolve())}
    try:
        with path.open("rb") as stream:
            before = os.fstat(stream.fileno())
            hasher = hashlib.sha256()
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                hasher.update(block)
            digest = hasher.hexdigest()
            after = os.fstat(stream.fileno())
        current = path.stat()
        stable = lambda info: (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)
        if stable(before) != stable(after) or stable(after) != stable(current):
            return {**result, "status": "changed-during-observation"}
        return {**result, "status": "present", "bytes": after.st_size, "sha256": digest}
    except FileNotFoundError:
        return {**result, "status": "absent"}
    except OSError as error:
        return {**result, "status": "unreadable", "error_type": type(error).__name__}


def _text(path, limit=16 * 1024 * 1024):
    observation = _file(path)
    if observation["status"] != "present":
        return observation, None
    if observation["bytes"] > limit:
        return {**observation, "status": "exceeds-inspection-limit", "inspection_limit_bytes": limit}, None
    try:
        data = Path(path).read_bytes()
        if hashlib.sha256(data).hexdigest() != observation["sha256"]:
            return {**observation, "status": "changed-during-observation"}, None
        return observation, data.decode("utf-8")
    except (OSError, UnicodeError) as error:
        return {**observation, "status": "unreadable", "error_type": type(error).__name__}, None


def _git(path):
    path = Path(path).resolve()
    result = {"path": str(path)}
    if not path.is_dir():
        return {**result, "status": "absent"}

    def run(*args):
        return subprocess.run(
            ["git", "--no-optional-locks", "-c", "safe.directory=" + str(path),
             "-c", "core.fsmonitor=false", "-C", str(path), *args],
            check=True, capture_output=True, text=True, timeout=20).stdout.strip()

    try:
        if Path(run("rev-parse", "--show-toplevel")).resolve() != path:
            return {**result, "status": "not-repository-root"}
        head = run("rev-parse", "HEAD")
        if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", head):
            return {**result, "status": "invalid-head"}
        result.update(status="observed", head=head,
                      tracked_worktree_clean=not run("status", "--porcelain", "--untracked-files=no"))
        try:
            result["origin"] = _public_origin(run("remote", "get-url", "origin"))
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            result["origin"] = {"status": "unavailable", "error_type": type(error).__name__}
        return result
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        return {**result, "status": "unavailable", "error_type": type(error).__name__}


def observe(repository_root, loaded_core_path, loaded_core_sha256):
    """Observe actual paths/bytes without inferring a source-to-binary build claim."""
    if not re.fullmatch(r"[0-9a-f]{64}", loaded_core_sha256):
        raise ValueError("Loaded core SHA256 must be lowercase hexadecimal")
    repository = Path(repository_root).resolve()
    explicit_engine = os.environ.get("LAPLACE_ENGINE_BUILD")
    engine = Path(explicit_engine).resolve() if explicit_engine else repository / "build/engine"
    cache_candidates = ([engine / "CMakeCache.txt", engine.parent / "CMakeCache.txt"]
                        if explicit_engine else [repository / "build/CMakeCache.txt"])
    cache_path = next((path for path in cache_candidates if path.is_file()), cache_candidates[0])
    cache, text = _text(cache_path)
    external_values = []
    if text is not None:
        for line in text.splitlines():
            if line.startswith("LAPLACE_EXTERNAL:") and "=" in line:
                external_values.append(line.split("=", 1)[1])
    cache["external_entry_count"] = len(external_values)
    external = None
    if len(external_values) == 1 and Path(external_values[0]).is_absolute():
        external = Path(external_values[0]).resolve()
        cache["selected_external_path"] = str(external)
    else:
        cache["external_selection_status"] = "absent-or-ambiguous"

    loaded = _file(loaded_core_path)
    loaded["receipt_sha256"] = loaded_core_sha256
    loaded["matches_receipt_bytes"] = loaded.get("sha256") == loaded_core_sha256
    # The managed output closure copies the CMake-built library with this name.
    core = _file(engine / "core" / Path(loaded_core_path).name)
    core["matches_loaded_core_bytes"] = (core.get("sha256") == loaded_core_sha256
                                         and loaded["matches_receipt_bytes"])
    result = {
        "schema": "laplace.chess-corpus-native-inventory.v1",
        "scope": "Current configured build/cache/checkouts and actual library bytes; source-to-binary compilation lineage is not established without a build receipt.",
        "repository_root": str(repository), "engine_build_directory": str(engine),
        "engine_build_selection": "LAPLACE_ENGINE_BUILD" if explicit_engine else "repository-build-default",
        "cmake_cache": cache, "loaded_core": loaded, "configured_build_core": core,
        "current_checkouts_proven_inputs_of_loaded_core": False,
    }
    if external is None:
        result["external"] = {"status": "not-selected-by-observed-cache"}
        return result
    pins, pin_text = _text(external / "PINS.tsv")
    entries = []
    if pin_text is not None:
        for line in pin_text.splitlines():
            fields = line.split("\t")
            if fields[0] not in PIN_PATHS.values():
                continue
            entry = {"path": fields[0], "status": "malformed"}
            if len(fields) == 3 and re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", fields[2]):
                entry.update(status="observed", commit=fields[2], origin=_public_origin(fields[1]))
            entries.append(entry)
    pins["selected_entries"] = entries
    checkouts = {}
    for name, pin_path in PIN_PATHS.items():
        checkout = _git(external / pin_path.removeprefix("external/"))
        matching = [row for row in entries if row["path"] == pin_path]
        checkout["pin_entry_count"] = len(matching)
        checkout["head_matches_pin"] = (checkout.get("head") == matching[0].get("commit")
                                        if checkout.get("head") and len(matching) == 1
                                        and matching[0]["status"] == "observed" else None)
        checkouts[name] = checkout
    result["external"] = {"status": "observed", "root": str(external),
                          "selected_by": "CMakeCache LAPLACE_EXTERNAL", "pins": pins,
                          "checkouts": checkouts}
    return result
