#!/usr/bin/env python3
"""Helper functions for provision-cutechess.py."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.request

_SCRIPTS_DIR = str(Path(__file__).resolve().parent)
if _SCRIPTS_DIR not in sys.path:
    sys.path.insert(0, _SCRIPTS_DIR)

from lib.chess_source_integrity import verify_checkout

LOCK = Path(__file__).resolve().parents[1] / "deploy" / "cutechess-release.json"


def run(argv, **kwargs):
    if len(argv) >= 3 and argv[0:2] == ["git", "-C"]:
        # Operators and the CI runner share this one external checkout. Trust
        # only the explicitly selected path without changing global Git policy.
        argv = ["git", "--no-replace-objects", "-c", "core.fsmonitor=false",
                "-c", f"safe.directory={Path(argv[2]).resolve()}", *argv[1:]]
    elif argv and argv[0] == "git":
        argv = ["git", "--no-replace-objects", *argv[1:]]
    result = subprocess.run([str(arg) for arg in argv], capture_output=True, text=True,
                            timeout=120, **kwargs)
    if result.returncode:
        raise RuntimeError(f"{' '.join(map(str, argv))} failed ({result.returncode}): "
                           f"{result.stderr.strip() or result.stdout.strip()}")
    return result.stdout.strip()


def verify_source(path, lock):
    root = Path(run(["git", "-C", path, "rev-parse", "--show-toplevel"])).resolve()
    if root != path.resolve():
        raise RuntimeError(f"{path} is not a standalone dependency repository")
    origin = run(["git", "-C", path, "remote", "get-url", "origin"])
    normalize = lambda value: value.rstrip("/").removesuffix(".git").replace("git@github.com:", "https://github.com/").replace("ssh://git@github.com/", "https://github.com/")
    if normalize(origin) != normalize(lock["repository"]):
        raise RuntimeError(f"{path} origin does not match {lock['repository']}")
    actual = run(["git", "-C", path, "rev-parse", "HEAD"])
    if actual != lock["commit"]:
        raise RuntimeError(f"{path} is at {actual}; expected {lock['commit']}")
    if run(["git", "-C", path, "status", "--porcelain", "--untracked-files=all"]):
        raise RuntimeError(f"{path} contains local changes; preserving it without building")
    integrity = verify_checkout(path, actual, "CuteChess")
    version = (path / ".version").read_text().strip()
    if version != lock["version"] or not (path / "CMakeLists.txt").is_file():
        raise RuntimeError(f"{path} does not contain the locked CuteChess {lock['version']} source")
    return integrity


def reset_build_cache(source, build, lock):
    """Reset only CMake's generated configure metadata, without --fresh support."""
    source = source.resolve()
    build = build.resolve()
    verify_source(source, lock)
    entries = [(build / "CMakeCache.txt", False), (build / "CMakeFiles", True)]
    present = []
    # Validate both entries before removing either. A declared build path can be
    # nested under the source or reached through a configured symlink; only the
    # actual metadata entries may be removed, never source or tracked content.
    for path, directory in entries:
        if path == source or path in source.parents:
            raise RuntimeError(f"CMake cache metadata would contain the source checkout: {path}")
        if source in path.parents:
            relative = path.relative_to(source).as_posix()
            if run(["git", "-C", source, "ls-tree", "-r", "--name-only", "HEAD", "--", ":(literal)" + relative]):
                raise RuntimeError(f"CMake cache metadata contains committed source: {path}")
        try:
            info = path.lstat()
        except FileNotFoundError:
            continue
        reparse = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & reparse:
            raise RuntimeError(f"CMake cache metadata must not be a link or reparse point: {path}")
        expected_type = stat.S_ISDIR if directory else stat.S_ISREG
        if not expected_type(info.st_mode):
            raise RuntimeError(f"Unexpected CMake cache metadata type: {path}")
        present.append((path, directory))
    build.mkdir(parents=True, exist_ok=True)
    for path, directory in present:
        if directory:
            shutil.rmtree(path)
        else:
            path.unlink()
    integrity = verify_source(source, lock)
    return {"source_integrity": integrity, "build_dir": str(build),
            "removed": [path.name for path, _ in present]}


def provision(target, lock):
    # This is the configured shared external dependency checkout, not a second
    # source cache. Preserve dirty trees and branch tips; only clean detached
    # checkouts are moved to the verified release commit.
    if not target.exists():
        target.parent.mkdir(parents=True, exist_ok=True)
        run(["git", "clone", "--config", "core.autocrlf=false", "--branch", lock["tag"], "--single-branch", lock["repository"], target])
    root = Path(run(["git", "-C", target, "rev-parse", "--show-toplevel"])).resolve()
    if root != target.resolve():
        raise RuntimeError(f"{target} is not a standalone dependency repository")
    origin = run(["git", "-C", target, "remote", "get-url", "origin"])
    normalize = lambda value: value.rstrip("/").removesuffix(".git").replace("git@github.com:", "https://github.com/").replace("ssh://git@github.com/", "https://github.com/")
    if normalize(origin) != normalize(lock["repository"]):
        raise RuntimeError(f"{target} origin does not match {lock['repository']}")
    if run(["git", "-C", target, "status", "--porcelain", "--untracked-files=all"]):
        raise RuntimeError(f"{target} contains local changes; preserving them without updating")
    actual = run(["git", "-C", target, "rev-parse", "HEAD"])
    verify_checkout(target, actual, "CuteChess")
    if actual != lock["commit"]:
        run(["git", "-C", target, "fetch", "origin", f"refs/tags/{lock['tag']}"])
        fetched = run(["git", "-C", target, "rev-parse", "FETCH_HEAD^{commit}"])
        if fetched != lock["commit"]:
            raise RuntimeError(f"upstream {lock['tag']} points to {fetched}; expected {lock['commit']}")
        # Retain even a detached former tip before moving this dependency.
        run(["git", "-C", target, "update-ref", f"refs/laplace/previous/{actual}", actual])
        run(["git", "-C", target, "checkout", "--detach", lock["commit"]])
    verify_source(target, lock)
    # Keep the existing external cache authority in agreement with this upgrade;
    # a later setup-host bootstrap must not reset CuteChess to its previous pin.
    pins = target.parent / "PINS.tsv"
    descriptor = os.open(pins, os.O_RDWR | os.O_CREAT, 0o664)
    with os.fdopen(descriptor, "r+", encoding="utf-8") as output:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(output.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(output, fcntl.LOCK_EX)
        # Read after locking and preserve the inode, owner and mode shared by
        # operators and CI. Stockfish uses this same per-manifest writer lock.
        original = output.read()
        lines = original.splitlines()
        matching = [index for index, line in enumerate(lines)
                    if line.split("\t", 1)[0] == "external/cutechess"]
        row = "\t".join(["external/cutechess", lock["repository"], lock["commit"]])
        if matching:
            for index in matching:
                lines[index] = row
        else:
            lines.append(row)
        text = "\n".join(lines) + "\n"
        if text != original:
            output.seek(0)
            output.write(text)
            output.truncate()
            output.flush()
            os.fsync(output.fileno())
        if os.name == "nt":
            output.seek(0)
            msvcrt.locking(output.fileno(), msvcrt.LK_UNLCK, 1)

    return target


def check_latest(lock):
    request = urllib.request.Request(lock["latest_api"], headers={
        "Accept": "application/vnd.github+json", "User-Agent": "Laplace-chess-dependencies"})
    with urllib.request.urlopen(request, timeout=30) as response:
        release = json.load(response)
    if release.get("draft") or release.get("prerelease") or release.get("tag_name") != lock["tag"]:
        raise RuntimeError(f"CuteChess release lock {lock['tag']} differs from upstream "
                           f"{release.get('tag_name', 'unknown')}; update the verified release lock")
    return {"component": "cutechess", "locked": lock["tag"],
            "latest": release["tag_name"], "current": True}


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


GUI_MODULES = ("Core", "Gui", "Widgets", "Concurrent", "Svg", "PrintSupport", "Core5Compat")


def gui_inventory(qt_prefix, lock):
    """Identify selected SDK inputs; runtime loading is established by probe_gui."""
    qt_prefix = qt_prefix.resolve(strict=True)
    if os.name == "nt" and (qt_prefix / "Qt6Core.dll").is_file():
        required = ("Qt6Core.dll", "Qt6Gui.dll", "Qt6Widgets.dll", "platforms/qoffscreen.dll", "platforms/qwindows.dll")
        for name in required:
            if not (qt_prefix / name).is_file():
                raise RuntimeError(f"Deployed Qt runtime is missing {qt_prefix / name}")
        libraries = {p.relative_to(qt_prefix).as_posix(): digest(p)
                     for p in sorted(qt_prefix.rglob("*.dll"))}
        offscreen = qt_prefix / "platforms/qoffscreen.dll"
        return {"prefix": str(qt_prefix), "version": lock["qt_version"], "layout": "deployed-windows",
                "libraries": libraries, "offscreen": {"path": str(offscreen), "present": True, "sha256": digest(offscreen)}}
    version_file = qt_prefix / "lib/cmake/Qt6/Qt6ConfigVersion.cmake"
    version_text = version_file.read_text()
    version_files = {str(version_file): digest(version_file)}
    # Qt 6.11 wraps CMake's basic version file in its compatibility policy.
    # Follow only the fixed same-directory include emitted by upstream Qt.
    if re.search(r'^\s*include\s*\(\s*"\$\{CMAKE_CURRENT_LIST_DIR\}/Qt6ConfigVersionImpl\.cmake"\s*\)\s*$',
                 version_text, re.M):
        implementation = version_file.with_name("Qt6ConfigVersionImpl.cmake")
        version_text += "\n" + implementation.read_text()
        version_files[str(implementation)] = digest(implementation)
    versions = re.findall(r'^\s*set\s*\(\s*PACKAGE_VERSION\s+"?(\d+\.\d+\.\d+)"?\s*\)\s*$', version_text, re.M)
    if len(versions) != 1 or versions[0] != lock["qt_version"] or tuple(map(int, versions[0].split("."))) < (6, 8, 0):
        raise RuntimeError(f"GUI requires the selected Qt {lock['qt_version']} SDK >=6.8")
    modules = {}
    for name in GUI_MODULES:
        config = qt_prefix / f"lib/cmake/Qt6{name}/Qt6{name}Config.cmake"
        if not config.is_file():
            raise RuntimeError(f"GUI requires Qt module {name}: {config}")
        modules[name] = {"config": str(config), "config_sha256": digest(config)}
    plugin_names = ("platforms/qoffscreen.dll", "platforms/qminimal.dll", "platforms/qwindows.dll",
                    "imageformats/qsvg.dll", "iconengines/qsvgicon.dll") if os.name == "nt" else (
                    "platforms/libqoffscreen.so", "platforms/libqminimal.so", "platforms/libqxcb.so",
                    "platforms/libqwayland.so", "platforms/libqwayland-generic.so", "platforms/libqwayland-egl.so",
                    "imageformats/libqsvg.so", "iconengines/libqsvgicon.so")
    plugins = {}
    for name in plugin_names:
        path = qt_prefix / "plugins" / name
        plugins[name] = {"path": str(path), "present": path.is_file(),
                         "sha256": digest(path) if path.is_file() else None}
    offscreen = plugins[plugin_names[0]]
    if not offscreen["present"]:
        raise RuntimeError(f"GUI offscreen platform plugin missing: {offscreen['path']}")
    return {"prefix": str(qt_prefix), "version": versions[0], "version_files": version_files, "modules": modules,
            "module_identity_scope": "SDK CMake configuration files, not loaded library identities",
            "plugins": plugins, "offscreen": offscreen}


def gui_environment(qt_prefix):
    qt_prefix = qt_prefix.resolve(strict=True)
    plugins = qt_prefix if (qt_prefix / "Qt6Core.dll").is_file() else qt_prefix / "plugins"
    environment = {"QT_PLUGIN_PATH": str(plugins),
                   "QT_QPA_PLATFORM_PLUGIN_PATH": str(plugins / "platforms")}
    if sys.platform.startswith("linux"):
        # GNU DT_RUNPATH is searched after LD_LIBRARY_PATH. Select this SDK's
        # runtime first for both the probe and the reported direct launch.
        selected = str(qt_prefix / "lib")
        inherited = [path for path in os.environ.get("LD_LIBRARY_PATH", "").split(os.pathsep)
                     if path and path != selected]
        environment["LD_LIBRARY_PATH"] = os.pathsep.join([selected, *inherited])
    return environment


def _publish_desktop_text(path, value, mode):
    """Publish owned public launch artifacts without truncating a prior file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    prior = path.lstat() if path.exists() or path.is_symlink() else None
    if prior is not None and not stat.S_ISREG(prior.st_mode):
        raise RuntimeError(f"desktop artifact is not a regular file: {path}")
    with tempfile.TemporaryDirectory(prefix=".cutechess-desktop-", dir=path.parent) as temporary:
        pending = Path(temporary) / path.name
        with pending.open("w", encoding="utf-8", newline="\n") as stream:
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
        if prior is not None:
            os.chown(pending, prior.st_uid, prior.st_gid)
        pending.chmod(mode)
        os.replace(pending, path)


def _desktop_exec(path):
    value = str(path)
    if any(character in value for character in ("\0", "\n", "\r", "=")):
        raise ValueError("desktop launcher path contains unsupported characters")
    # Desktop Entry string escaping precedes Exec argument unquoting.
    quoted = "".join("\\" + char if char in ('"', "$", chr(96), "\\") else char
                     for char in value.replace("%", "%%"))
    return '"' + quoted.replace("\\", "\\\\") + '"'


def desktop_perfcache(prefix):
    # This provisioning-time whitelist reuses the installed configuration owner.
    # Desktop users never read the app environment or its adjacent secret files.
    spec = importlib.util.spec_from_file_location(
        "cutechess_runtime_configuration", Path(__file__).with_name("chess-runtime-env.py"))
    owner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(owner)
    configured = owner.configuration(prefix, keys={"LAPLACE_PERFCACHE_BIN"})
    if configured.get("LAPLACE_PERFCACHE_BIN"):
        selected = Path(configured["LAPLACE_PERFCACHE_BIN"]).resolve(strict=True)
    else:
        candidates = list((prefix / "share/laplace").glob("laplace_t0_perfcache_*.bin"))
        if len(candidates) != 1:
            raise RuntimeError("select the installed T0 perfcache before provisioning desktop engines")
        selected = candidates[0].resolve(strict=True)
    if not selected.is_file() or selected.parent != (prefix / "share/laplace").resolve(strict=True):
        raise RuntimeError("desktop T0 perfcache must be the selected installed public file")
    return selected


def calibration_machine(host):
    """Stable measured machine and process resource limits; exclude transient load."""
    keys = ("hostname", "platform", "machine", "cpu_models", "affinity_cpu_ids",
            "physical_core_groups_within_affinity", "physical_memory_bytes")
    return {**{key: host[key] for key in keys},
            "cpu_quota": host["cgroup"]["cpu_quota"],
            "memory_limit_bytes": host["cgroup"]["memory_limit_bytes"]}


def calibration_configuration(report, current_identity, current_host, recommendations):
    if (report.get("schema") != "laplace.benchmark.chess-environment/v1"
            or report.get("status") != "complete" or report.get("evidence_invalid")
            or report.get("failures") or report["parameters"]["repeats"] < 3
            or report["parameters"].get("max_moves", 0) != 0):
        raise ValueError("desktop defaults require a complete repeated uncapped calibration")
    measured = report["stockfish_identity"]
    for key in ("sha256", "source_commit"):
        if current_identity.get(key) != measured.get(key) or not measured.get(key):
            raise ValueError("calibration Stockfish executable or source differs")
    if not measured.get("build_receipt_matches_binary_and_source") \
            or not current_identity.get("build_receipt_matches_binary_and_source"):
        raise ValueError("calibration must identify the direct source-built Stockfish")
    def networks(identity):
        rows = identity.get("source_networks", [])
        if not rows or any(not row.get("present") or not row.get("sha256") for row in rows):
            raise ValueError("calibration has no complete NNUE source identity")
        return sorted((row["name"], row["sha256"], row["size_bytes"]) for row in rows)
    if networks(measured) != networks(current_identity):
        raise ValueError("calibration NNUE source bytes differ")
    before = calibration_machine(report["host"])
    if before != calibration_machine(report["host_after"]) or before != calibration_machine(current_host):
        raise ValueError("calibration machine or admitted process resource limits differ")
    selection = recommendations["bench_suite_latency"]
    threads, memory = selection["threads"], selection["hash_mib"]
    if type(threads) is not int or type(memory) is not int or min(threads, memory) < 1:
        raise ValueError("calibration selected invalid engine options")
    budget = report["plan"]
    if threads > current_host["effective_cpu_capacity"] - budget["reserved_cpu_capacity"]:
        raise ValueError("calibrated search threads no longer fit the observed CPU allowance")
    demand = memory * (1 << 20) + budget["engine_overhead_estimate_bytes"]
    if current_host.get("available_memory_bytes") is None or demand > current_host["available_memory_bytes"]:
        raise ValueError("calibrated Stockfish no longer fits current memory headroom")
    options = []
    for name, value in (("Threads", threads), ("Hash", memory)):
        option = measured["uci_options"][name]
        minimum, maximum, default = (int(option[key]) for key in ("min", "max", "default"))
        if option["type"] != "spin" or not minimum <= value <= maximum:
            raise ValueError("calibration option is outside the engine's advertised range")
        options.append({"name": name, "type": "spin", "value": value,
                        "default": default, "min": minimum, "max": maximum})
    return options, {"status": "selected", "purpose": "single-engine-built-in-bench-latency",
                     "scope": selection["scope"], "configuration": selection,
                     "stockfish": {key: measured[key] for key in ("sha256", "source_commit", "source_networks")},
                     "machine": before,
                     "selfplayConcurrencyApplied": False,
                     "evaluatorPoolConfigurationApplied": False}


EVALUATION_BEGIN = "# >>> laplace measured Stockfish evaluation defaults >>>"
EVALUATION_END = "# <<< laplace measured Stockfish evaluation defaults <<<"


def without_evaluation_defaults(text):
    """Remove only this owner's prior block; all other settings remain caller-owned."""
    result, inside, seen = [], False, False
    for line in text.splitlines(keepends=True):
        if line.rstrip("\r\n") == EVALUATION_BEGIN:
            if inside or seen:
                raise ValueError("measured evaluation environment block is ambiguous")
            inside, seen = True, True
        elif line.rstrip("\r\n") == EVALUATION_END:
            if not inside:
                raise ValueError("measured evaluation environment block has no beginning")
            inside = False
        elif not inside:
            result.append(line)
    if inside:
        raise ValueError("measured evaluation environment block is incomplete")
    return "".join(result)


def evaluation_configuration(options, explicit, report, host):
    measured = {row["name"]: row["value"] for row in options}
    defaults = {"THREADS": measured["Threads"], "HASH_MB": measured["Hash"], "PROCESSES": 1}
    selected, overrides = {}, []
    for suffix, default in defaults.items():
        name = "LAPLACE_STOCKFISH_EVAL_" + suffix
        value = explicit.get(name)
        if value is not None:
            if re.fullmatch(r"[1-9][0-9]*", value) is None:
                raise ValueError("explicit evaluation option must be a positive integer: " + name)
            selected[name] = int(value)
            overrides.append(name)
        else:
            selected[name] = default
    threads = selected["LAPLACE_STOCKFISH_EVAL_THREADS"]
    memory = selected["LAPLACE_STOCKFISH_EVAL_HASH_MB"]
    processes = selected["LAPLACE_STOCKFISH_EVAL_PROCESSES"]
    for name, value in (("Threads", threads), ("Hash", memory)):
        option = report["stockfish_identity"]["uci_options"][name]
        if not int(option["min"]) <= value <= int(option["max"]):
            raise ValueError("effective evaluation option exceeds the engine's advertised range")
    cpu = host["effective_cpu_capacity"] - report["plan"]["reserved_cpu_capacity"]
    demand = processes * (memory * (1 << 20) + report["plan"]["engine_overhead_estimate_bytes"])
    memory_budget = min(host["available_memory_bytes"], report["plan"]["memory_budget_bytes"])
    if processes * threads > cpu or demand > memory_budget:
        raise ValueError("effective evaluation process/thread/hash configuration exceeds the measured resource envelope")
    return selected, {"configuration": selected, "explicitOverrides": overrides,
        "cpuAllowance": cpu, "requestedThreadSlots": processes * threads,
        "estimatedMemoryBytes": demand, "availableMemoryBudgetBytes": memory_budget,
        "processScope": "One analysis engine by default; an explicit process override is resource-checked, not throughput-calibrated",
        "selfplayConcurrencyApplied": False, "enginePoolThroughputMeasured": False}


def register_desktop(prefix, directory=Path("/usr/local/share/applications")):
    """Register the same prefix-owned entry; this never starts a GUI."""
    prefix = prefix.resolve()
    manifest = prefix / "share/laplace/cutechess-desktop.json"
    receipt = json.loads(manifest.read_text())
    if receipt.get("schema") != "laplace.cutechess-desktop-install/v1":
        raise RuntimeError("desktop installation receipt is unavailable")
    for role, relative in (("launcher", "bin/laplace-cutechess"),
                           ("desktop", "share/applications/laplace-cutechess.desktop"),
                           ("engine_catalog", "share/laplace/cutechess-engines.json"),
                           ("session_helper", "share/laplace/cutechess-user-engines.py")):
        path = prefix / relative
        if (receipt[role]["path"] != str(path) or path.is_symlink() or
                not path.is_file() or digest(path) != receipt[role]["sha256"]):
            raise RuntimeError("desktop installation artifact differs from its receipt")
    desktop = prefix / "share/applications/laplace-cutechess.desktop"
    directory.mkdir(mode=0o755, parents=True, exist_ok=True)
    installed = directory / "laplace-cutechess.desktop"
    if installed.exists() or installed.is_symlink():
        if not installed.is_symlink() or os.readlink(installed) != str(desktop):
            raise RuntimeError("existing desktop registration belongs to another installation")
    else:
        # The conventional path is registered by the existing root bootstrap.
        # Its link keeps following later nonroot CI refreshes of prefix artifacts.
        installed.symlink_to(desktop)
    receipt["system_desktop_file"] = str(installed)
    _publish_desktop_text(manifest, json.dumps(receipt, indent=2) + "\n", 0o644)
    return receipt
