#!/usr/bin/env python3
"""Select and bind X11 prerequisites; only the separate real GUI session proves readiness."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import time

spec = importlib.util.spec_from_file_location(
    "chess_x11_shared", Path(__file__).with_name("lib") / "chess_x11_runtime.py")
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)

SCHEMA = "laplace.x11-runtime-selection/v1"
TOOLS = ("xvfb-run", *core.TOOLS)
PACKAGES = ("xvfb", "xdotool", "xauth", "x11-utils", "x11-xkb-utils")
require = core.require
digest = core.digest


def save(path, value):
    pending = path.with_name(path.name + ".pending")
    pending.write_text(json.dumps(value, indent=2, sort_keys=True, allow_nan=False) + "\n", encoding="utf-8")
    pending.replace(path)


def snapshot(environment, deadline):
    """Hash ELF tools and their resolved dependencies, plus the official shell wrapper."""
    tools, files = core.loaded_files(environment, deadline)
    wrapper = shutil.which("xvfb-run", path=environment.get("PATH"))
    require(wrapper is not None, "selected runtime is missing xvfb-run")
    wrapper = str(Path(wrapper).resolve(strict=True))
    require(Path(wrapper).is_file() and os.access(wrapper, os.X_OK),
            "selected xvfb-run is not an executable file")
    tools = {**tools, "xvfb-run": wrapper}
    files = {**files, wrapper: digest(wrapper)}
    return {"tools": tools, "loaded_files": files}


def selected_environment(selection, base=None, *, qt_prefix=None):
    environment = dict(os.environ if base is None else base)
    if selection["mode"] == "private":
        return core.selected_environment(
            {"root": selection["private_runtime"]["root"]}, environment, qt_prefix=qt_prefix)
    require(selection["mode"] == "host", "unknown X11 runtime selection")
    # Bind the recorded host tool directories even when the caller's PATH differs.
    prefixes = list(dict.fromkeys(str(Path(path).parent) for path in selection["tools"].values()))
    if qt_prefix is not None:
        prefixes.insert(0, str(Path(qt_prefix) / "bin"))
        libraries = [str(Path(qt_prefix) / "lib"),
                     *filter(None, environment.get("LD_LIBRARY_PATH", "").split(os.pathsep))]
        environment["LD_LIBRARY_PATH"] = os.pathsep.join(dict.fromkeys(libraries))
    environment["PATH"] = os.pathsep.join(dict.fromkeys(
        [*prefixes, *filter(None, environment.get("PATH", "").split(os.pathsep))]))
    return environment


def check_tool_selection(selection, observation):
    require(observation["tools"] == selection["tools"], "resolved X11 tools differ from selected paths")
    for path in selection["tools"].values():
        require(observation["loaded_files"].get(path) == selection["loaded_files"].get(path),
                "selected X11 executable or wrapper bytes changed")
    # Qt-first loader resolution can legitimately select a different compatible
    # library than the prerequisite probe; retain and compare that actual map per session.


def load_selection(path):
    path = Path(path)
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 4 * 1024 * 1024,
            "X11 selection receipt is not a bounded regular file")
    selection = json.loads(path.read_text(encoding="utf-8"))
    identity = selection.pop("selection_sha256", None)
    require(selection.get("schema") == SCHEMA and selection.get("status") == "tools-selected"
            and core.HEX.fullmatch(str(identity))
            and hashlib.sha256(core.canonical(selection)).hexdigest() == identity,
            "X11 selection receipt identity or status differs")
    require(set(selection["tools"]) == set(TOOLS), "X11 selection tool inventory is incomplete")
    if selection["mode"] == "private":
        expected = selection["private_runtime"]
        current = core.load(Path(expected["manifest_root"]))
        require(current is not None and current["runtime_id"] == expected["runtime_id"]
                and current["root"] == expected["root"]
                and current["manifest_sha256"] == expected["manifest_sha256"],
                "private X11 runtime differs from the selected manifest")
        require({name: selection["tools"][name] for name in core.TOOLS} == current["tools"],
                "selected X11 tool paths differ from the private manifest")
    else:
        require(selection["mode"] == "host", "unknown X11 runtime selection")
    require(0 < len(selection["loaded_files"]) <= 257, "X11 file inventory exceeds its bound")
    for item, expected in selection["loaded_files"].items():
        require(Path(item).is_absolute() and core.HEX.fullmatch(str(expected))
                and Path(item).is_file() and digest(item) == expected,
                "selected X11 executable/library/resource changed: " + item)
    for item in selection["tools"].values():
        require(item in selection["loaded_files"] and os.access(item, os.X_OK),
                "selected X11 tool has no authenticated executable body")
    selection["selection_sha256"] = identity
    return selection


def package_versions(deadline):
    """Installed package metadata is separately labeled, never inferred from a tool name."""
    path = "/usr/bin/dpkg-query"
    if not Path(path).is_file():
        return {"available": False, "scope": "installed dpkg metadata"}
    result = core.run_owned(
        [path, "-W", "-f=${binary:Package}\t${Version}\t${Status}\n", *PACKAGES],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        timeout=min(10, max(.001, deadline - time.monotonic())))
    require(result.returncode in (0, 1) and len(result.stdout) <= 65536,
            "host X11 package inventory failed")
    return {"available": True, "scope": "installed dpkg metadata; loaded file hashes own tool identity",
            "rows": result.stdout.splitlines()}


def retain_acquisition(document, evidence):
    """Copy only the selected generation's already retained, bounded acquisition evidence."""
    evidence.mkdir(parents=True, exist_ok=True)
    acquisition = Path(document["root"]).parent / "acquisition"
    core.physical_directory(acquisition)
    source = acquisition / "sources.list"
    require(source.is_file() and not source.is_symlink() and source.stat().st_size <= 16384
            and source.read_text(encoding="utf-8") == document["sources"],
            "retained X11 source configuration differs from the manifest")
    shutil.copy2(source, evidence / "sources.list")
    indexes = document["apt_index_sha256"]
    require(len(indexes) == 3, "retained signed archive index set is incomplete")
    for name, expected in indexes.items():
        require(Path(name).name == name and name.endswith("_InRelease"),
                "retained signed archive index name differs")
        path = acquisition / name
        require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 4 * 1024 * 1024
                and digest(path) == expected, "retained signed archive index bytes differ")
        shutil.copy2(path, evidence / name)
    logs = evidence / "logs"
    logs.mkdir(exist_ok=True)
    names = ["update.log", "selection.txt", *[item["name"] + ".download.log" for item in document["packages"]]]
    for name in names:
        require(Path(name).name == name, "retained X11 log filename differs")
        path = acquisition / "logs" / name
        if not path.exists() and name.endswith(".download.log"):
            continue  # An authenticated cached .deb requires no new download command.
        require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 1024 * 1024,
                "retained X11 acquisition log is absent or exceeds its bound")
        shutil.copy2(path, logs / name)
    save(evidence / "current.json", document)


def ensure(root, deadline, evidence=None):
    found = {name: shutil.which(name) for name in TOOLS}
    missing = [name for name, path in found.items() if path is None]
    private_on_path = any(Path(path).resolve().is_relative_to(Path(root).absolute())
                          for path in found.values() if path is not None)
    document = None
    if missing or private_on_path:
        document = core.load(root)
        if document is None:
            document = core.provision(root, deadline, evidence)
        environment = core.selected_environment(document)
    else:
        environment = dict(os.environ)
    observation = snapshot(environment, deadline)
    selection = {"schema": SCHEMA, "status": "tools-selected",
                 "mode": "private" if document is not None else "host",
                 "scope": "X11 prerequisites; actual Qt window interaction remains separately required",
                 "gui_ready": False, "host_packages_installed": False,
                 "initially_missing_tools": missing, **observation,
                 "host_package_versions": package_versions(deadline)}
    if document is not None:
        require({name: observation["tools"][name] for name in core.TOOLS} == document["tools"],
                "selected X11 tool paths differ from the private manifest")
        selection["private_runtime"] = {
            "manifest_root": str(Path(root).absolute()), "root": document["root"],
            "runtime_id": document["runtime_id"], "manifest_sha256": document["manifest_sha256"],
            "packages": document["packages"]}
        # The package inventory includes scripts; do not apply ldd to xvfb-run.
        relative = Path(observation["tools"]["xvfb-run"]).relative_to(Path(document["root"])).as_posix()
        require(relative in document["files"], "private xvfb-run is outside the authenticated package inventory")
        if evidence is not None:
            retain_acquisition(document, evidence)
    selection["selection_sha256"] = hashlib.sha256(core.canonical(selection)).hexdigest()
    return selection


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=core.runtime_root())
    parser.add_argument("--deadline-seconds", type=int, default=280)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--evidence-output", type=Path)
    args = parser.parse_args(argv)
    require(30 <= args.deadline_seconds <= 300, "provisioning deadline must be 30..300 seconds")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    value = {"schema": SCHEMA, "status": "failed", "gui_ready": False}
    previous_term = signal.getsignal(signal.SIGTERM)
    def interrupted(_signum, _frame):
        raise InterruptedError("X11 selection interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    try:
        value = ensure(args.root.absolute(), started + args.deadline_seconds, args.evidence_output)
    except (Exception, KeyboardInterrupt) as error:
        value.update(status="failed", error_type=type(error).__name__, error=str(error)[-2000:])
    finally:
        signal.signal(signal.SIGTERM, previous_term)
        save(args.output, value)
    print(json.dumps({"schema": SCHEMA, "status": value["status"], "output": str(args.output),
                      "mode": value.get("mode"), "gui_ready": False}), flush=True)
    return 0 if value["status"] == "tools-selected" else 1


if __name__ == "__main__":
    raise SystemExit(main())
