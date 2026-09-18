#!/usr/bin/env python3
"""Derive affected managed build/test projects from ProjectReference dependencies."""
from __future__ import annotations

import argparse
import json
import os
import xml.etree.ElementTree as ET
from collections import defaultdict, deque
from pathlib import Path, PurePosixPath

DEPLOYABLE_PROJECTS = frozenset({
    "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
    "app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj",
    "app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj",
    "app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj",
    "app/Laplace.Migrations/Laplace.Migrations.csproj",
})
ROOT_MANAGED_FILES = frozenset({
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "NuGet.Config",
    "app/Laplace.slnx",
})


def rel(root: Path, path: Path) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


def discover(root: Path) -> dict[str, dict]:
    projects: dict[str, dict] = {}
    for project in sorted((root / "app").glob("**/*.csproj")):
        key = rel(root, project)
        try:
            tree = ET.parse(project)
        except ET.ParseError as error:
            raise ValueError(f"invalid project XML: {key}: {error}") from error
        refs: list[str] = []
        for node in tree.getroot().iter():
            if node.tag.split("}")[-1] != "ProjectReference":
                continue
            include = node.attrib.get("Include")
            if not include:
                continue
            referenced = (project.parent / include.replace("\\", os.sep)).resolve()
            try:
                refs.append(rel(root, referenced))
            except ValueError:
                raise ValueError(f"{key} references project outside repository: {include}")
        is_test = (
            ".Tests" in project.parent.name
            or project.parent.name.endswith("Tests")
            or any(
                node.tag.split("}")[-1] == "IsTestProject"
                and (node.text or "").strip().lower() == "true"
                for node in tree.getroot().iter()
            )
        )
        projects[key] = {
            "path": key,
            "directory": project.parent.resolve(),
            "references": sorted(set(refs)),
            "is_test": is_test,
        }
    return projects


def owner_for_path(root: Path, projects: dict[str, dict], path: str) -> str | None:
    candidate = (root / path).resolve()
    owners = [
        key for key, meta in projects.items()
        if candidate == meta["directory"] or meta["directory"] in candidate.parents
    ]
    if not owners:
        return None
    return max(owners, key=lambda key: len(projects[key]["directory"].parts))


def reverse_graph(projects: dict[str, dict]) -> dict[str, set[str]]:
    reverse: dict[str, set[str]] = defaultdict(set)
    for key, meta in projects.items():
        for dependency in meta["references"]:
            if dependency in projects:
                reverse[dependency].add(key)
    return reverse


def closure(starts: set[str], reverse: dict[str, set[str]]) -> set[str]:
    seen = set(starts)
    queue = deque(starts)
    while queue:
        current = queue.popleft()
        for dependent in reverse.get(current, ()):
            if dependent in seen:
                continue
            seen.add(dependent)
            queue.append(dependent)
    return seen


def plan(root: Path, changed_paths: list[str]) -> dict:
    projects = discover(root)
    reverse = reverse_graph(projects)

    force_all = any(path in ROOT_MANAGED_FILES for path in changed_paths)
    changed_projects: set[str] = set()
    unknown_app_paths: list[str] = []
    for path in changed_paths:
        if not path.startswith("app/"):
            continue
        if path == "app/Laplace.slnx":
            force_all = True
            continue
        owner = owner_for_path(root, projects, path)
        if owner:
            changed_projects.add(owner)
        else:
            unknown_app_paths.append(path)

    if unknown_app_paths:
        force_all = True

    if force_all:
        affected = set(projects)
    else:
        affected = closure(changed_projects, reverse)

    test_projects = sorted(
        key for key in affected if projects[key]["is_test"]
    )

    changed_production = {
        key for key in changed_projects if not projects[key]["is_test"]
    }
    deployable = DEPLOYABLE_PROJECTS & affected
    build_projects = sorted(changed_production | deployable | set(test_projects))

    return {
        "changed_projects": sorted(changed_projects),
        "affected_projects": sorted(affected),
        "test_projects": test_projects,
        "build_projects": build_projects,
        "deployable_projects": sorted(deployable),
        "force_all": force_all,
        "unknown_app_paths": sorted(unknown_app_paths),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--changed-file", action="append", default=[])
    parser.add_argument("--json-output")
    args = parser.parse_args()
    result = plan(Path(args.root).resolve(), args.changed_file)
    encoded = json.dumps(result, sort_keys=True)
    print(encoded)
    if args.json_output:
        Path(args.json_output).write_text(encoded + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
