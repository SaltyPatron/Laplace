#!/usr/bin/env python3
"""Resolve managed project build/test impact from the real ProjectReference graph."""
from __future__ import annotations

import argparse
import json
import os
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Project:
    path: str
    refs: tuple[str, ...]
    is_test: bool


def _tag_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def load_projects(root: Path) -> dict[str, Project]:
    root = root.resolve()
    projects: dict[str, Project] = {}
    app = root / "app"
    for csproj in sorted(app.glob("*/*.csproj")):
        rel = csproj.relative_to(root).as_posix()
        try:
            tree = ET.parse(csproj)
        except ET.ParseError as error:
            raise ValueError(f"cannot parse {rel}: {error}") from error

        refs: list[str] = []
        is_test = csproj.parent.name.endswith(".Tests")
        for element in tree.getroot().iter():
            name = _tag_name(element.tag)
            if name == "ProjectReference":
                include = (element.attrib.get("Include") or "").replace("\\", "/")
                if not include:
                    continue
                target = (csproj.parent / include).resolve()
                try:
                    target_rel = target.relative_to(root).as_posix()
                except ValueError:
                    continue
                refs.append(target_rel)
            elif name == "IsTestProject" and (element.text or "").strip().lower() == "true":
                is_test = True

        projects[rel] = Project(rel, tuple(sorted(set(refs))), is_test)
    return projects


def project_for_path(projects: dict[str, Project], path: str) -> str | None:
    normalized = path.replace("\\", "/")
    for project in projects.values():
        directory = project.path.rsplit("/", 1)[0] + "/"
        if normalized == project.path or normalized.startswith(directory):
            return project.path
    return None


def test_filter_for_paths(root: Path, paths: list[str]) -> str:
    """Return an exact VSTest class filter only for unambiguous test-class edits."""
    root = root.resolve()
    projects = load_projects(root)
    filters: set[str] = set()
    for raw in paths:
        path = raw.replace("\\", "/")
        project = project_for_path(projects, path)
        if project is None or not projects[project].is_test or not path.endswith(".cs"):
            return ""
        source = root / path
        if not source.is_file():
            return ""
        text = source.read_text(encoding="utf-8")
        namespace = re.search(
            r"(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[;{]",
            text,
        )
        classes = re.findall(
            r"(?m)^\s*(?:public|internal)\s+(?:(?:sealed|partial|abstract)\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)",
            text,
        )
        if namespace is None or not classes:
            return ""
        filters.update(
            f"FullyQualifiedName~{namespace.group(1)}.{name}"
            for name in classes
        )
    return "|".join(sorted(filters))


def plan_changed(root: Path, paths: list[str]) -> dict[str, object]:
    projects = load_projects(root)
    if not projects:
        return {
            "full": True,
            "changed_projects": [],
            "affected_projects": [],
            "build_projects": ["all"],
            "test_projects": ["all"],
        }

    changed: set[str] = set()
    force_all = False
    for raw in paths:
        path = raw.replace("\\", "/")
        if not path.startswith("app/"):
            continue
        project = project_for_path(projects, path)
        if project is not None:
            changed.add(project)
            continue
        # Shared managed build inputs and unclassified tracked app files can affect
        # every project. Failing safe here is about the managed graph only.
        force_all = True

    if force_all:
        all_paths = sorted(projects)
        return {
            "full": True,
            "changed_projects": sorted(changed),
            "affected_projects": all_paths,
            "build_projects": ["all"],
            "test_projects": ["all"],
        }

    if not changed:
        return {
            "full": False,
            "changed_projects": [],
            "affected_projects": [],
            "build_projects": [],
            "test_projects": [],
        }

    reverse: dict[str, set[str]] = {path: set() for path in projects}
    for owner, project in projects.items():
        for referenced in project.refs:
            if referenced in reverse:
                reverse[referenced].add(owner)

    affected = set(changed)
    queue = list(changed)
    while queue:
        current = queue.pop()
        for consumer in reverse.get(current, ()):
            if consumer in affected:
                continue
            affected.add(consumer)
            queue.append(consumer)

    tests = {path for path in affected if projects[path].is_test}
    production = affected - tests
    production_roots = {
        path
        for path in production
        if not (reverse.get(path, set()) & production)
    }
    # Building selected tests also compiles their ProjectReference closure. Add
    # affected production leaves so compile-only executables/tools with no test
    # consumer are still proved without compiling unrelated projects.
    build = sorted(tests | production_roots)

    return {
        "full": False,
        "changed_projects": sorted(changed),
        "affected_projects": sorted(affected),
        "build_projects": build,
        "test_projects": sorted(tests),
    }


def write_solution(root: Path, projects_csv: str, output: Path) -> Path:
    root = root.resolve()
    output = output.resolve()
    projects = load_projects(root)
    selected = [item for item in projects_csv.split(",") if item]
    if projects_csv == "all":
        raise ValueError("'all' uses app/Laplace.slnx directly")
    if not selected:
        raise ValueError("at least one managed project is required")
    unknown = sorted(set(selected) - set(projects))
    if unknown:
        raise ValueError("unknown managed project(s): " + ", ".join(unknown))

    # A project omitted from a solution is built as an out-of-solution
    # ProjectReference. MSBuild then removes the parent solution configuration
    # and the referenced project falls back to its default (Debug), even when
    # the selected leaf is built with `-c Release`. Include the complete forward
    # reference closure so every dependency receives the selected configuration
    # and publish never looks for Release assemblies that were emitted as Debug.
    closure = set(selected)
    pending = list(selected)
    while pending:
        current = pending.pop()
        for referenced in projects[current].refs:
            if referenced in projects and referenced not in closure:
                closure.add(referenced)
                pending.append(referenced)

    output.parent.mkdir(parents=True, exist_ok=True)
    lines = ["<Solution>"]
    for path in sorted(closure):
        relative = os.path.relpath(root / path, output.parent).replace(os.sep, "/")
        lines.append(f'  <Project Path="{relative}" />')
    lines.append("</Solution>")
    output.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return output


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    sub = parser.add_subparsers(dest="command", required=True)

    plan = sub.add_parser("plan")
    plan.add_argument("--changed-file", action="append", default=[])

    solution = sub.add_parser("solution")
    solution.add_argument("--projects", required=True)
    solution.add_argument("--output", required=True)

    args = parser.parse_args()
    root = Path(args.root)
    if args.command == "plan":
        print(json.dumps(plan_changed(root, args.changed_file), sort_keys=True))
        return 0
    write_solution(root, args.projects, Path(args.output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
