#!/usr/bin/env python3
"""Build the Windows application from independently reusable component artifacts.

MSBuild owns compilation; this module owns publication inputs, immutable outputs
and composition. Native compilation remains one invocation of its CMake graph.
No database or service is changed here.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

from ci_managed_projects import load_projects

# Operations owns local artifact storage for both implementation stacks.
_operations = Path(os.environ.get("LAPLACE_OPERATIONS") or Path(__file__).resolve().parents[2] / "Laplace-Operations")
_spec = importlib.util.spec_from_file_location("artifact_store", _operations / "installation/artifact_store.py")
_store = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_store)
digest, files, atomic = _store.digest, _store.files, _store.atomic
materialize, store_lock = _store.materialize, _store.store_lock

PROJECTS = {
    "api": "Laplace.Endpoints.OpenAICompat",
    "cli": "Laplace.Cli",
    "uci": "Laplace.Chess.Uci",
    "migrations": "Laplace.Migrations",
}
NATIVE_TARGETS = (
    "laplace_core", "laplace_syzygy", "laplace_dynamics", "laplace_synthesis",
    "laplace_t0_perfcache", "laplace_highway_perfcache",
    "laplace_chess_position_perfcache", "laplace_chess_transition_perfcache",
)
SCHEMA = "laplace.component-artifact/v1"
PUBLISH_OPTIONS = ("-c", "Release", "--no-self-contained", "--nologo", "-p:DefaultItemExcludes=wwwroot/**")


def identity(value) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def command(arguments, **kwargs):
    print("component: " + " ".join(map(str, arguments)), flush=True)
    return subprocess.run(list(map(str, arguments)), check=True, **kwargs)


def closure(root: Path, name: str) -> set[str]:
    projects = load_projects(root)
    pending = [f"app/{PROJECTS[name]}/{PROJECTS[name]}.csproj"]
    result = set()
    while pending:
        project = pending.pop()
        if project in result:
            continue
        result.add(project)
        pending.extend(projects[project].refs)
    return result


def documentation_root(root: Path) -> Path:
    declared = os.environ.get("LaplaceDocumentationRoot") or os.environ.get("LAPLACE_DOCUMENTATION_ROOT")
    if declared:
        return Path(declared)
    candidate = root / ".ci-dependencies/Laplace-Wiki/Repositories/Laplace"
    return candidate if candidate.is_dir() else root.parent / "Laplace-Wiki/Repositories/Laplace"


def managed_inputs(root: Path, name: str, native: Path, sdk: str) -> dict:
    projects = closure(root, name)
    tracked = subprocess.run(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "app"],
        cwd=root, check=True, capture_output=True,
    ).stdout
    directories = tuple(project.rsplit("/", 1)[0] + "/" for project in projects)
    inputs = {root / os.fsdecode(path) for path in tracked.split(b"\0") if path
              and os.fsdecode(path).replace("\\", "/").startswith(directories)}
    external = os.environ.get("LAPLACE_EXTERNAL", "")
    configured = native.parent / "CMakeCache.txt"
    if configured.is_file():
        match = re.search(r"(?m)^LAPLACE_EXTERNAL:(?:PATH|STRING)=([^\r\n]*)", configured.read_text())
        if match:
            external = match[1]
    runtime = Path(os.environ.get("LAPLACE_RECIPE_RUNTIME") or native / "core/recipe_engine/runtime")
    properties = {"LAPLACE_ENGINE_BUILD": str(native), "LaplaceRecipeRuntime": str(runtime),
                  "LaplaceDocumentationRoot": str(documentation_root(root)),
                  "LAPLACE_RECIPE_RUNTIME": str(runtime), "LAPLACE_EXTERNAL": external,
                  "LaplaceGrammarRoot": str(Path(external) / "tree-sitter-grammars") if external else ""}
    for project in projects:
        for item in ET.parse(root / project).getroot().iter():
            if item.tag.rsplit("}", 1)[-1] not in ("Content", "None", "Compile", "EmbeddedResource"):
                continue
            include = item.attrib.get("Include", "").replace("\\", "/")
            for pattern in include.split(";"):
                if not pattern:
                    continue
                pattern = re.sub(r"\$\(([^)]+)\)", lambda match: properties.get(match[1], match[0]), pattern)
                if "$" in pattern or "@" in pattern:
                    raise ValueError(f"Unresolved publication resource in {project}: {pattern}")
                location = root / project.rsplit("/", 1)[0]
                inputs.update(Path(path).resolve() for path in __import__("glob").glob(str(location / pattern), recursive=True)
                              if Path(path).is_file())
    inputs.update(root / path for path in (
        "global.json", "NuGet.Config", "app/Directory.Build.props", "app/Directory.Build.targets",
        "app/Directory.Packages.props", "scripts/win/engine-runtime.cmd", "scripts/win/publish-zstd.ps1",
        "scripts/win/machine-env.props",
        "scripts/verify-zstd-runtime.py", "deploy/zstd-release.json",
    ))
    payload = []
    for path in sorted(inputs):
        try:
            relative = os.path.relpath(path, root).replace("\\", "/")
        except ValueError:  # installed resources may be on another Windows volume
            relative = path.as_posix()
        parts = relative.split("/")
        project_output = len(parts) > 2 and parts[0] == "app" and parts[2] in ("bin", "obj")
        if "/wwwroot/" in relative or project_output:
            continue
        payload.append((relative, digest(path) if path.is_file() else None))
    libraries = {}
    for relative in ("core/laplace_core.dll", "core/laplace_syzygy.dll",
                     "dynamics/laplace_dynamics.dll", "synthesis/laplace_synthesis.dll"):
        libraries[relative] = digest(native / relative) if (native / relative).is_file() else None
    libraries.update({"perfcache/" + key: value for key, value in files(native / "core/perfcache").items()
                      if key.endswith((".bin", ".flags", ".layout"))})
    dependency_roots = []
    runtime_patterns = {
        "LAPLACE_ONEAPI": ("mkl/latest/bin/mkl_*.dll", "compiler/latest/bin/libmmd.dll",
                           "compiler/latest/bin/libiomp5md.dll", "compiler/latest/bin/svml_dispmd.dll",
                           "tbb/latest/bin/tbb12.dll", "tbb/latest/bin/libhwloc-15.dll"),
        "LAPLACE_DEPENDENCY_ZLIB_PREFIX": ("bin/z.dll",),
        "LAPLACE_DEPENDENCY_LIBXML2_PREFIX": ("bin/libxml2.dll",),
        "ICU_ROOT": ("bin64/icu*78.dll",),
        "LAPLACE_DEPENDENCY_ZSTD_PREFIX": ("bin/zstd.dll",),
    }
    for variable, patterns in runtime_patterns.items():
        directory = os.environ.get(variable)
        if directory:
            for pattern in patterns:
                dependency_roots.extend((variable + "/" + str(path.relative_to(directory)), digest(path))
                                        for path in sorted(Path(directory).glob(pattern)))
    return {"schema": SCHEMA, "component": name, "projects": sorted(projects), "sources": payload,
            "native": libraries, "runtime_dependencies": dependency_roots, "sdk": sdk,
            "publication": PUBLISH_OPTIONS, "platform": sys.platform,
            "properties": {"Configuration": "Release", "PublishReadyToRun": name == "cli"}}


def read_artifact(path: Path, inputs: dict) -> dict | None:
    try:
        receipt = json.loads((path / "artifact.json").read_text(encoding="utf-8"))
        if (receipt["schema"] == SCHEMA and identity(receipt["inputs"]) == identity(inputs) and receipt["files"]
                and receipt["files"] == files(path / "payload")
                and (inputs["component"] != "api" or receipt.get("openapi") == digest(path / "openapi.json"))):
            return receipt
    except (OSError, KeyError, ValueError):
        pass
    return None


def managed(root: Path, name: str, build: Path, native: Path, store: Path,
            sdk: str, *, plan=False, force=False) -> dict:
    inputs = managed_inputs(root, name, native, sdk)
    key = identity(inputs)
    target = store / name / key
    receipt = read_artifact(target, inputs)
    action = "reuse" if receipt and not force else "build"
    result = {"component": name, "action": action, "identity": key,
              "directory": str(target / "payload"), "projects": inputs["projects"]}
    if plan or action == "reuse":
        return result
    # All four leaves share the same MSBuild output tree. MSBuild compiles shared
    # ProjectReferences once; each leaf publication is recorded independently.
    temporary = Path(tempfile.mkdtemp(prefix=".publish-", dir=store))
    try:
        published = temporary / "published"
        command(["dotnet", "publish", root / f"app/{PROJECTS[name]}/{PROJECTS[name]}.csproj",
                 *PUBLISH_OPTIONS, "-o", published,
                 f"-p:LAPLACE_BUILD_ROOT={build}", f"-p:LAPLACE_ENGINE_BUILD={native}",
                 f"-p:PublishReadyToRun={'true' if name == 'cli' else 'false'}"], cwd=root)
        if managed_inputs(root, name, native, sdk) != inputs:
            raise ValueError("Component inputs changed during publication: " + name)
        inventory = materialize(published, temporary / "payload", store)
        record = {"schema": SCHEMA, "inputs": inputs, "files": inventory}
        if name == "api":
            contract = root / "web/openapi/openapi.json"
            shutil.copy2(contract, temporary / "openapi.json")
            record["openapi"] = digest(contract)
        atomic(temporary / "artifact.json", record)
        shutil.rmtree(published)
        target.parent.mkdir(parents=True, exist_ok=True)
        # Never mutate a previously published artifact or one of its hardlinks.
        # A damaged artifact is replaced by renaming its directory under the lock.
        previous = None
        if target.exists():
            previous = target.with_name(key + ".replaced")
            if previous.exists():
                raise ValueError("Unresolved component replacement: " + str(previous))
            target.rename(previous)
        temporary.rename(target)
        if previous:
            shutil.rmtree(previous)
        return result
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)


def run(root: Path, build: Path, native: Path, store: Path, components: list[str],
        *, plan=False, force=False, force_web=False) -> dict:
    selected = list(dict.fromkeys(components))
    sdk = (subprocess.run(["dotnet", "--version"], cwd=root, check=True, text=True, capture_output=True).stdout.strip()
           if any(name in PROJECTS for name in selected) else "")
    results = []
    if any(name in PROJECTS or name == "native" for name in selected):
        results.append({"component": "native", "action": "inspect", "targets": list(NATIVE_TARGETS)})
        if not plan:
            arguments = ["cmd.exe", "/d", "/c", str(root / "scripts/win/build-engine.cmd")]
            if force:
                arguments.append("--clean-first")
            command([*arguments, *NATIVE_TARGETS], cwd=root)
    for name in PROJECTS:
        if name in selected:
            result = managed(root, name, build, native, store, sdk, plan=plan, force=force)
            results.append(result)
            print(f"component {name}: {result['action']} {result['identity']}", flush=True)
            if name == "api" and not plan:
                contract = Path(result["directory"]).parent / "openapi.json"
                destination = root / "web/openapi/openapi.json"
                destination.parent.mkdir(parents=True, exist_ok=True)
                if not destination.is_file() or digest(destination) != digest(contract):
                    shutil.copy2(contract, destination)
    if "web" in selected:
        script = root / "scripts/web-artifact.py"
        spec = importlib.util.spec_from_file_location("web_artifact", script)
        web = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(web)
        if not plan:
            result = web.build_web(root, build / "web-build.json", force=force or force_web)
            results.append({"component": "web", **result, "directory": str(root / "web/dist")})
        else:
            results.append({"component": "web", "action": "inspect", "directory": str(root / "web/dist")})
    return {"schema": "laplace.windows-components/v1", "selected": selected,
            "plan": plan, "components": results, "store": str(store)}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--build", type=Path, required=True)
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--store", type=Path, required=True)
    parser.add_argument("--components", default="api,cli,uci,migrations,web")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--plan", action="store_true")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--force-web", action="store_true")
    args = parser.parse_args()
    selected = args.components.split(",")
    if set(selected) - {*PROJECTS, "native", "web"}:
        parser.error("Unknown component selection")
    try:
        if args.plan:
            result = run(args.root.resolve(), args.build.resolve(), args.native.resolve(), args.store.resolve(), selected, plan=True, force=args.force, force_web=args.force_web)
        else:
            with store_lock(args.store.resolve()):
                result = run(args.root.resolve(), args.build.resolve(), args.native.resolve(), args.store.resolve(), selected, force=args.force, force_web=args.force_web)
        atomic(args.output.resolve(), result)
        print(json.dumps(result))
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print("windows-components: " + str(error), file=sys.stderr)
        return 1
    finally:
        if not args.plan:
            _store.cleanup(args.store.resolve())


if __name__ == "__main__":
    raise SystemExit(main())
