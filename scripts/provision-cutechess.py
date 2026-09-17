#!/usr/bin/env python3
"""Provision the locked CuteChess source and verify the executable's Qt closure."""
import argparse
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
        run(["git", "clone", "--branch", lock["tag"], "--single-branch", lock["repository"], target])
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


def probe(binary, lock, qt_version=None):
    # Executing the process checks the dynamic loader, Core and Core5Compat;
    # directory existence cannot establish a working Qt runtime.
    output = run([binary, "--version"])
    if not re.search(r"^cutechess-cli " + re.escape(lock["version"]) + r"\s*$", output, re.M):
        raise RuntimeError(f"{binary} is not locked CuteChess {lock['version']}: {output[:800]}")
    match = re.search(r"Using Qt version (\d+\.\d+\.\d+)", output)
    if not match or tuple(map(int, match[1].split('.'))) < (6, 8, 0):
        raise RuntimeError(f"{binary} requires a working Qt >=6.8 runtime: {output[:800]}")
    qt_version = qt_version or lock.get("qt_version")
    if qt_version and match[1] != qt_version:
        raise RuntimeError(f"{binary} uses Qt {match[1]}; expected {qt_version}")
    return {"component": "cutechess", "version": lock["version"],
            "qt_version": match[1], "path": str(binary), "ready": True}


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def verify_build(source, binary, lock, qt_version=None, receipt_path=None):
    """Verify post-build source and the tested binary before publication."""
    source = source.resolve(strict=True)
    binary = binary.resolve(strict=True)
    verify_source(source, lock)
    before = digest(binary)
    runtime = probe(binary, lock, qt_version)
    integrity = verify_source(source, lock)
    if digest(binary) != before:
        raise RuntimeError("CuteChess executable changed during the runtime probe")
    cache = binary.parent / "CMakeCache.txt"
    receipt = {"schema": "laplace.cutechess-source-build.v1", "repository": lock["repository"],
               "commit": lock["commit"], "source": str(source), "source_integrity": integrity,
               "binary": str(binary), "binary_sha256": before, "runtime": runtime,
               "cmake_cache_sha256": digest(cache) if cache.is_file() else None,
               "scope": "post-build committed source bytes/modes and probed executable identity"}
    if receipt_path is not None:
        receipt_path = receipt_path.absolute()
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".cutechess-receipt-", dir=receipt_path.parent) as temporary:
            pending = Path(temporary) / "receipt.json"
            pending.write_text(json.dumps(receipt, sort_keys=True) + "\n")
            os.replace(pending, receipt_path)
    return receipt


GUI_MODULES = ("Core", "Gui", "Widgets", "Concurrent", "Svg", "PrintSupport", "Core5Compat")


def gui_inventory(qt_prefix, lock):
    """Identify selected SDK inputs; runtime loading is established by probe_gui."""
    qt_prefix = qt_prefix.resolve(strict=True)
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
    environment = {"QT_PLUGIN_PATH": str(qt_prefix / "plugins"),
                   "QT_QPA_PLATFORM_PLUGIN_PATH": str(qt_prefix / "plugins/platforms")}
    if sys.platform.startswith("linux"):
        # GNU DT_RUNPATH is searched after LD_LIBRARY_PATH. Select this SDK's
        # runtime first for both the probe and the reported direct launch.
        selected = str(qt_prefix / "lib")
        inherited = [path for path in os.environ.get("LD_LIBRARY_PATH", "").split(os.pathsep)
                     if path and path != selected]
        environment["LD_LIBRARY_PATH"] = os.pathsep.join([selected, *inherited])
    return environment


def probe_gui(binary, lock, qt_prefix, work=None):
    """Run upstream QApplication initialization without opening a desktop window."""
    if binary.is_symlink() or not binary.is_file():
        raise RuntimeError(f"GUI executable must be a regular direct build/install file: {binary}")
    binary = binary.resolve(strict=True)
    inventory = gui_inventory(qt_prefix, lock)
    before = digest(binary)
    scratch = Path(work) if work else Path(os.environ.get("LAPLACE_WORK_ROOT", "/build/laplace/work")) / "chess-tools"
    scratch.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env.update(gui_environment(qt_prefix))
    env.update({"QT_QPA_PLATFORM": "offscreen", "QT_DEBUG_PLUGINS": "1",
                "QT_LOGGING_RULES": "qt.core.library.debug=true;qt.core.plugin.*.debug=true",
                "QT_MESSAGE_PATTERN": "%{category}: %{message}"})
    for key in ("DISPLAY", "WAYLAND_DISPLAY", "QT_QPA_PLATFORMTHEME", "QT_QPA_GENERIC_PLUGINS"):
        env.pop(key, None)
    with tempfile.TemporaryDirectory(prefix="cutechess-gui-probe-", dir=scratch) as temporary:
        root = Path(temporary)
        for key, name in (("XDG_CONFIG_HOME", "config"), ("XDG_CONFIG_DIRS", "config-dirs"),
                          ("XDG_DATA_HOME", "data"), ("XDG_DATA_DIRS", "data-dirs"),
                          ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime")):
            path = root / name
            path.mkdir(mode=0o700)
            env[key] = str(path)
        # Upstream constructs CuteChessApplication/QApplication before --version.
        # This proves the offscreen QApplication/platform loader, not an event loop.
        result = subprocess.run([str(binary), "-platform", "offscreen", "--version"],
                                stdin=subprocess.DEVNULL, capture_output=True, text=True,
                                timeout=30, env=env, cwd=root)
    if result.returncode:
        raise RuntimeError(f"CuteChess GUI initialization failed ({result.returncode}): {result.stderr[-2000:]}")
    if not re.search(r"^Cute Chess " + re.escape(lock["version"]) + r"\s*$", result.stdout, re.M):
        raise RuntimeError(f"GUI is not locked Cute Chess {lock['version']}: {result.stdout[:800]}")
    if not re.search(r"^Using Qt version " + re.escape(lock["qt_version"]) + r"\s*$", result.stdout, re.M):
        raise RuntimeError(f"GUI does not use selected Qt {lock['qt_version']}: {result.stdout[:800]}")
    loaded = set()
    for match in re.finditer(r'^qt\.core\.library:\s+("(?:[^"\\]|\\.)*")\s+loaded library\s*$', result.stderr, re.M):
        path = Path(json.loads(match[1]))
        if path.name in ("libqoffscreen.so", "qoffscreen.dll"):
            loaded.add(str(path.resolve(strict=True)))
    expected = str(Path(inventory["offscreen"]["path"]).resolve(strict=True))
    if loaded != {expected}:
        raise RuntimeError(f"GUI did not load the selected offscreen plugin: expected {expected}, observed {sorted(loaded)}")
    if digest(binary) != before or gui_inventory(qt_prefix, lock) != inventory:
        raise RuntimeError("GUI executable or selected Qt inputs changed during the runtime probe")
    return {"component": "cutechess-gui", "version": lock["version"], "qt_version": lock["qt_version"],
            "path": str(binary), "binary_sha256": before, "ready": True, "status": "ready-headless",
            "qapplication_initialized": True, "qt": inventory, "loaded_platform_plugin": expected,
            "plugin_diagnostics_sha256": hashlib.sha256(result.stderr.encode()).hexdigest(),
            "scope": "official GUI QApplication initialization and offscreen platform loading through --version",
            "settings_scope": "isolated XDG directories" if sys.platform.startswith("linux") else
                              "temporary XDG directories; platform native settings are not isolated",
            "interactive_desktop_tested": False, "interactive_desktop_ready": None,
            "direct_launch": {"argv": [str(binary)], "environment": gui_environment(qt_prefix)}}


def verify_gui_build(source, binary, lock, qt_prefix, receipt_path, work=None):
    source = source.resolve(strict=True)
    verify_source(source, lock)
    runtime = probe_gui(binary, lock, qt_prefix, work)
    integrity = verify_source(source, lock)
    if digest(binary) != runtime["binary_sha256"]:
        raise RuntimeError("CuteChess GUI executable changed after its runtime probe")
    cache = binary.parent / "CMakeCache.txt"
    receipt = {"schema": "laplace.cutechess-gui-source-build.v1", "repository": lock["repository"],
               "commit": lock["commit"], "source": str(source), "source_integrity": integrity,
               "build_target": "gui", "binary": str(binary.resolve(strict=True)),
               "binary_sha256": runtime["binary_sha256"], "runtime": runtime,
               "cmake_cache_sha256": digest(cache) if cache.is_file() else None}
    if receipt_path:
        receipt_path = receipt_path.absolute()
        receipt_path.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".cutechess-gui-receipt-", dir=receipt_path.parent) as temporary:
            pending = Path(temporary) / "receipt.json"
            pending.write_text(json.dumps(receipt, sort_keys=True) + "\n")
            os.replace(pending, receipt_path)
    return receipt


def verify_gui_install(binary, receipt_path, lock, work=None):
    """Bind the direct installed executable and selected SDK to a verified build."""
    receipt_bytes = receipt_path.read_bytes()
    receipt = json.loads(receipt_bytes)
    if (receipt.get("schema") != "laplace.cutechess-gui-source-build.v1" or
            receipt.get("repository") != lock["repository"] or receipt.get("commit") != lock["commit"] or
            receipt.get("build_target") != "gui"):
        raise RuntimeError("GUI build receipt does not match the selected official source")
    source = Path(receipt["source"])
    integrity = verify_source(source, lock)
    if integrity != receipt["source_integrity"]:
        raise RuntimeError("GUI source identity differs from the retained build")
    if binary.is_symlink() or not binary.is_file() or digest(binary) != receipt.get("binary_sha256"):
        raise RuntimeError("GUI installed executable differs from the retained official build")
    qt = receipt["runtime"]["qt"]
    qt_prefix = Path(qt["prefix"])
    if gui_inventory(qt_prefix, lock) != qt:
        raise RuntimeError("GUI selected Qt inputs differ from the retained build")
    runtime = probe_gui(binary, lock, qt_prefix, work)
    if runtime["binary_sha256"] != receipt["binary_sha256"] or verify_source(source, lock) != integrity:
        raise RuntimeError("GUI installed executable or source changed during verification")
    if receipt_path.read_bytes() != receipt_bytes:
        raise RuntimeError("GUI retained build receipt changed during verification")
    return {"build_receipt": str(receipt_path), "build_receipt_sha256": hashlib.sha256(receipt_bytes).hexdigest(),
            "repository": receipt["repository"], "commit": receipt["commit"], "source": str(source),
            "build_target": "gui", "runtime": runtime}


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
        "cutechess_runtime_configuration", Path(__file__).with_name("check-chess-dependencies.py"))
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


def desktop_calibration(path, expected_sha256, stockfish):
    if not re.fullmatch(r"[0-9a-f]{64}", expected_sha256 or ""):
        raise ValueError("select the exact completed calibration SHA256")
    path = Path(path)
    with path.open("rb") as stream:
        raw = stream.read((32 << 20) + 1)
    if len(raw) > 32 << 20 or hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise ValueError("selected calibration report differs")
    report = json.loads(raw)
    spec = importlib.util.spec_from_file_location(
        "cutechess_machine_calibration", Path(__file__).with_name("benchmark-chess-environment.py"))
    benchmark = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(benchmark)
    options, receipt = calibration_configuration(report, benchmark.source_identity(stockfish),
                                                 benchmark.machine(), benchmark.recommendations(report))
    receipt.update(reportSha256=expected_sha256, producerSchema=report["schema"],
                   startedUtc=report["started_utc"], finishedUtc=report["finished_utc"])
    return options, receipt, raw

def install_desktop(prefix, verified, stockfish, work_root=None, calibration_report=None, calibration_sha256=None):
    """Install public user-session launch files from the installed GUI verifier."""
    if not sys.platform.startswith("linux"):
        raise RuntimeError("desktop launcher installation requires Linux")
    prefix = prefix.resolve()
    runtime = verified["runtime"]
    direct = runtime["direct_launch"]
    binary = Path(runtime["path"])
    sdk = Path(runtime["qt"]["prefix"])
    library = str(sdk / "lib")
    expected = {"QT_PLUGIN_PATH": str(sdk / "plugins"),
                "QT_QPA_PLATFORM_PLUGIN_PATH": str(sdk / "plugins/platforms")}
    if (not runtime.get("ready") or direct.get("argv") != [str(binary)]
            or binary.is_symlink() or not binary.is_file()
            or digest(binary) != runtime["binary_sha256"]
            or any(direct["environment"].get(key) != value for key, value in expected.items())
            or direct["environment"].get("LD_LIBRARY_PATH", "").split(os.pathsep)[0] != library):
        raise RuntimeError("desktop launch selection differs from installed GUI verification")
    if not Path("/usr/bin/python3").is_file() or not os.access("/usr/bin/python3", os.X_OK):
        raise RuntimeError("system desktop launcher requires /usr/bin/python3")
    launcher = prefix / "bin/laplace-cutechess"
    desktop = prefix / "share/applications/laplace-cutechess.desktop"
    manifest = prefix / "share/laplace/cutechess-desktop.json"
    stockfish = Path(stockfish).resolve(strict=True)
    if not stockfish.is_file() or not os.access(stockfish, os.X_OK):
        raise RuntimeError("selected installed Stockfish executable is unavailable")
    selected_options = []
    calibration = {"status": "not-selected", "scope": "existing explicit user options or engine defaults"}
    explicit_calibration = calibration_report is not None
    if calibration_report is None and manifest.is_file() and not manifest.is_symlink():
        prior = json.loads(manifest.read_text()).get("calibration", {})
        if prior.get("status") == "selected":
            calibration_report, calibration_sha256 = prior["report"], prior["reportSha256"]
    if calibration_report is not None:
        try:
            selected_options, calibration, raw = desktop_calibration(calibration_report, calibration_sha256, stockfish)
        except (OSError, ValueError, KeyError, TypeError):
            if explicit_calibration:
                raise
            calibration = {"status": "stale", "reportSha256": calibration_sha256,
                           "scope": "existing user options or engine defaults; remeasure this installed engine"}
        else:
            retained = prefix / "share/laplace/chess-calibrations" / calibration_sha256 / "report.json"
            if retained.exists() and (retained.is_symlink() or retained.read_bytes() != raw):
                raise ValueError("retained calibration identity changed")
            _publish_desktop_text(retained, raw.decode("utf-8"), 0o644)
            calibration["report"] = str(retained)
    perfcache = desktop_perfcache(prefix)
    work_root = Path(work_root or os.environ.get("LAPLACE_WORK_ROOT", "/build/laplace/work")).absolute()
    if not work_root.is_dir():
        raise RuntimeError("existing permanent desktop work root is unavailable")
    engine_catalog = prefix / "share/laplace/cutechess-engines.json"
    session_helper = prefix / "share/laplace/cutechess-user-engines.py"
    helper_source = Path(__file__).with_name("cutechess-user-engines.py").read_text(encoding="utf-8")
    stockfish_selection = {"binary": str(stockfish), "sha256": digest(stockfish)}
    catalog = [{"name": "Stockfish (official)", "command": str(stockfish), "protocol": "uci"},
               {"name": "Laplace (substrate)", "command": str(prefix / "app/laplace-uci"), "protocol": "uci"}]
    if selected_options:
        catalog[0]["options"] = selected_options
    _publish_desktop_text(engine_catalog, json.dumps(catalog, indent=2) + "\n", 0o644)
    _publish_desktop_text(session_helper, helper_source, 0o644)
    selection = {"argv": [str(binary)], "binary_sha256": runtime["binary_sha256"],
                 "stockfish": stockfish_selection,
                 "previous_stockfish_command": str(prefix / "bin/laplace-cutechess-stockfish"),
                 "environment": expected, "qt_library_path": library,
                 "session_helper": str(session_helper), "session_helper_sha256": digest(session_helper),
                 "engine_catalog": str(engine_catalog), "engine_catalog_sha256": digest(engine_catalog),
                 "work_root": str(work_root), "t0_perfcache": str(perfcache),
                 "chess_floor_root": str(prefix / "share/laplace/chess-floor")}
    # Preserve the invoking user's display, settings and extra search paths.
    # Do not publish provisioning-process environment or read the API's secrets.
    program = '''#!/usr/bin/env python3
"""Launch the installed CuteChess GUI in the invoking user's desktop session."""
import hashlib
import importlib.util
import os
from pathlib import Path
import sys

SELECTION = ''' + repr(selection) + '''

def main():
    binary = Path(SELECTION["argv"][0])
    digest = hashlib.sha256()
    if binary.is_symlink() or not binary.is_file():
        raise RuntimeError("installed CuteChess executable is unavailable")
    with binary.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    if digest.hexdigest() != SELECTION["binary_sha256"]:
        raise RuntimeError("installed CuteChess changed; rerun chess provisioning")
    stockfish = Path(SELECTION["stockfish"]["binary"])
    digest = hashlib.sha256()
    with stockfish.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    if digest.hexdigest() != SELECTION["stockfish"]["sha256"]:
        raise RuntimeError("selected official Stockfish changed; rerun chess provisioning")
    for role in ("session_helper", "engine_catalog"):
        path = Path(SELECTION[role])
        if path.is_symlink() or not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != SELECTION[role + "_sha256"]:
            raise RuntimeError("installed CuteChess engine configuration changed; rerun chess provisioning")
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("laplace_cutechess_user_engines", SELECTION["session_helper"])
    session = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(session)
    # The inheritable lock remains held by the GUI for its whole lifetime.
    session_lock, session_environment, _ = session.prepare(
        SELECTION["engine_catalog"], binary, SELECTION["work_root"],
        previous_stockfish=SELECTION["previous_stockfish_command"])
    environment = session.launch_environment(SELECTION, session_environment)
    os.execve(str(binary), [*SELECTION["argv"], *sys.argv[1:]], environment)

if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError) as error:
        print("CuteChess: " + str(error), file=sys.stderr)
        raise SystemExit(1)
'''
    entry = ("[Desktop Entry]\nType=Application\nName=Cute Chess (Laplace)\n"
             "Comment=Play and analyze chess with the installed Cute Chess GUI\n"
             "Exec=/usr/bin/python3 " + _desktop_exec(launcher) + "\nTerminal=false\n"
             "Categories=Game;BoardGame;\nStartupNotify=true\n")
    _publish_desktop_text(launcher, program, 0o755)
    _publish_desktop_text(desktop, entry, 0o644)
    system = Path("/usr/local/share/applications/laplace-cutechess.desktop")
    result = {"schema": "laplace.cutechess-desktop-install/v1",
              "binary": str(binary), "binary_sha256": runtime["binary_sha256"],
              "qt_prefix": str(sdk), "build_receipt_sha256": verified["build_receipt_sha256"],
              "launcher": {"path": str(launcher), "sha256": digest(launcher)},
              "desktop": {"path": str(desktop), "sha256": digest(desktop)},
              "engine_catalog": {"path": str(engine_catalog), "sha256": digest(engine_catalog)},
              "session_helper": {"path": str(session_helper), "sha256": digest(session_helper)},
              "stockfish": stockfish_selection, "calibration": calibration,
              "session_work_root": str(work_root),
              "launch": selection,
              "t0_perfcache": str(perfcache),
              "engine_execution_verified": False, "desktop_substrate_access_verified": False,
              "system_desktop_file": str(system) if system.is_symlink() and
                                     os.readlink(system) == str(desktop) else None,
              "operator_desktop_tested": False, "autostart_installed": False}
    _publish_desktop_text(manifest, json.dumps(result, indent=2) + "\n", 0o644)
    return result



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


def configure_evaluation(prefix, stockfish):
    """Resolve a retained calibration once during ordinary cold deployment."""
    prefix = Path(prefix).resolve(strict=True)
    env_path = prefix / "app/laplace-api.env"
    original = env_path.read_text(encoding="utf-8")
    remaining = without_evaluation_defaults(original)
    manifest = json.loads((prefix / "share/laplace/cutechess-desktop.json").read_text())
    calibration = manifest.get("calibration", {})
    receipt = {"schema": "laplace.stockfish-evaluation-defaults/v1",
               "status": "not-selected", "configurationApplied": False,
               "scope": "Installed cold-launch defaults; caller environment/options take precedence"}
    if calibration.get("status") == "selected":
        try:
            options, identity, raw = desktop_calibration(
                calibration["report"], calibration["reportSha256"], Path(stockfish).resolve(strict=True))
        except (OSError, ValueError, KeyError, TypeError):
            receipt.update(status="stale", reportSha256=calibration.get("reportSha256"))
        else:
            spec = importlib.util.spec_from_file_location(
                "evaluation_runtime_configuration", Path(__file__).with_name("check-chess-dependencies.py"))
            doctor = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(doctor)
            keys = {"LAPLACE_STOCKFISH_EVAL_" + name for name in ("THREADS", "HASH_MB", "PROCESSES")}
            explicit = doctor.configuration(prefix, keys, api_environment=remaining)
            spec = importlib.util.spec_from_file_location(
                "evaluation_machine_configuration", Path(__file__).with_name("benchmark-chess-environment.py"))
            benchmark = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(benchmark)
            report = json.loads(raw)
            host = benchmark.machine()
            if calibration_machine(host) != identity["machine"]:
                raise ValueError("machine limits changed while selecting evaluation defaults")
            effective, resource = evaluation_configuration(options, explicit, report, host)
            # The block only fills absent installed values. Explicit environment values
            # are persisted here only when no corresponding user-owned file value exists.
            installed = doctor.configuration(prefix, keys, api_environment=remaining, include_environment=False)
            entries = {key: value for key, value in effective.items() if key not in installed}
            installed_values = {**installed, **{key: str(value) for key, value in entries.items()}}
            _, installed_resource = evaluation_configuration(options, installed_values, report, host)
            block = (EVALUATION_BEGIN + "\n"
                     + "# report-sha256=" + identity["reportSha256"] + "\n"
                     + "".join(key + "=" + str(value) + "\n" for key, value in entries.items())
                     + EVALUATION_END + "\n")
            remaining = remaining.rstrip("\n") + "\n" + block
            receipt.update(status="selected", configurationApplied=True,
                           reportSha256=identity["reportSha256"], stockfish=identity["stockfish"],
                           machine=identity["machine"],
                           installedConfiguration=installed_resource["configuration"],
                           environmentOverridesPersistedOnlyForMissingFileKeys=True, **resource)
    if remaining != original:
        _publish_desktop_text(env_path, remaining, stat.S_IMODE(env_path.stat().st_mode))
    receipt["environmentFile"] = str(env_path)
    receipt["restartRequiredForRunningServices"] = True
    receipt_path = prefix / "share/laplace/stockfish-evaluation-defaults.json"
    _publish_desktop_text(receipt_path, json.dumps(receipt, indent=2) + "\n", 0o644)
    return receipt


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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=LOCK)
    parser.add_argument("--source-dir", type=Path)
    parser.add_argument("--verify-source", type=Path, help="Read-only exact source verification; never updates the checkout")
    parser.add_argument("--reset-build-cache", type=Path, help="Remove only generated CMakeCache.txt/CMakeFiles for the verified source before configure")
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--receipt", type=Path, help="Retain verified source and binary identity after the build")
    parser.add_argument("--qt-version")
    parser.add_argument("--gui", action="store_true", help="Verify the official GUI through offscreen QApplication initialization")
    parser.add_argument("--qt-prefix", type=Path, help="Selected Qt SDK for GUI build verification")
    parser.add_argument("--verify-receipt", type=Path, help="Read-only GUI installed-binary verification against its retained source build")
    parser.add_argument("--work", type=Path, help="Build-volume directory for isolated GUI probe settings")
    parser.add_argument("--check-latest", action="store_true")
    parser.add_argument("--install-desktop", type=Path, help="Install a public desktop launcher under this prefix after GUI installed verification")
    parser.add_argument("--desktop-stockfish", type=Path, help="Selected direct official Stockfish executable for the public GUI catalog")
    parser.add_argument("--register-desktop", type=Path, help="Register an existing prefix desktop installation in the system application menu; no GUI execution")
    parser.add_argument("--configure-evaluation", type=Path, help="Apply retained measured analysis defaults to this prefix at cold deployment")
    parser.add_argument("--calibration-report", type=Path, help="Completed exact-machine Stockfish calibration to apply as missing GUI option defaults")
    parser.add_argument("--calibration-sha256", help="SHA256 of the explicitly selected calibration report")
    args = parser.parse_args()
    lock = json.loads(args.lock.read_text())
    if not re.fullmatch(r"[0-9a-f]{40}", lock["commit"]):
        parser.error("release lock must contain a complete source commit")
    if args.configure_evaluation:
        if args.desktop_stockfish is None:
            parser.error("--configure-evaluation requires the selected direct --desktop-stockfish")
        print(json.dumps(configure_evaluation(args.configure_evaluation, args.desktop_stockfish)))
        return 0
    if bool(args.calibration_report) != bool(args.calibration_sha256) or (args.calibration_report and not args.install_desktop):
        parser.error("calibration report and SHA256 must be selected together with --install-desktop")
    if args.register_desktop:
        if any((args.source_dir, args.verify_source, args.binary, args.check_latest, args.gui,
                args.qt_prefix, args.receipt, args.verify_receipt, args.install_desktop, args.desktop_stockfish, args.reset_build_cache, args.work)):
            parser.error("--register-desktop is a separate metadata-only operation")
        try:
            print(json.dumps(register_desktop(args.register_desktop)))
            return 0
        except (OSError, ValueError, KeyError, TypeError, RuntimeError) as error:
            print(f"CuteChess: {error}", file=sys.stderr)
            return 1
    if args.install_desktop and not (args.gui and args.verify_receipt and args.desktop_stockfish):
        parser.error("--install-desktop requires --gui installed verification and --desktop-stockfish")
    if args.desktop_stockfish and not args.install_desktop:
        parser.error("--desktop-stockfish requires --install-desktop")
    if not (args.source_dir or args.verify_source or args.binary or args.check_latest):
        parser.error("choose --source-dir, --verify-source, --binary or --check-latest")
    if args.reset_build_cache and (not args.verify_source or args.binary or args.receipt or args.source_dir or args.check_latest):
        parser.error("--reset-build-cache requires --verify-source without another action")
    if args.receipt and not (args.verify_source and args.binary):
        parser.error("--receipt requires --verify-source and --binary")
    if args.gui and (not args.binary or (not args.verify_receipt and not args.qt_prefix)):
        parser.error("--gui requires --binary and either --qt-prefix or --verify-receipt")
    if args.verify_receipt and (not args.gui or args.receipt or args.source_dir or args.verify_source):
        parser.error("--verify-receipt requires --gui and --binary without a source/build mutation")
    if (args.qt_prefix or args.verify_receipt or args.work) and not args.gui:
        parser.error("GUI options require --gui")
    try:
        if args.check_latest:
            print(json.dumps(check_latest(lock)))
        if args.source_dir:
            print(provision(args.source_dir.resolve(), lock))
        if args.reset_build_cache:
            print(json.dumps(reset_build_cache(args.verify_source, args.reset_build_cache, lock)))
        elif args.gui and args.verify_receipt:
            verified = verify_gui_install(args.binary, args.verify_receipt, lock, args.work)
            if args.install_desktop:
                verified["desktop_install"] = install_desktop(args.install_desktop, verified, args.desktop_stockfish, args.work,
                                                              args.calibration_report, args.calibration_sha256)
            print(json.dumps(verified))
        elif args.gui and args.verify_source:
            print(json.dumps(verify_gui_build(args.verify_source, args.binary, lock, args.qt_prefix, args.receipt, args.work)))
        elif args.gui:
            print(json.dumps(probe_gui(args.binary, lock, args.qt_prefix, args.work)))
        elif args.verify_source and args.binary:
            print(json.dumps(verify_build(args.verify_source, args.binary, lock, args.qt_version, args.receipt)))
        elif args.verify_source:
            print(json.dumps(verify_source(args.verify_source, lock)))
        elif args.binary:
            print(json.dumps(probe(args.binary, lock, args.qt_version)))
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"CuteChess: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
