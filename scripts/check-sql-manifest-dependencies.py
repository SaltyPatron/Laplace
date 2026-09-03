#!/usr/bin/env python3
"""Reject extension manifests that bind a view after a literal SQL consumer.

Fresh CREATE EXTENSION starts with no pre-existing views, while upgrade/dev databases
can retain old views and accidentally hide a broken manifest order.  This check keeps
that distinction visible without mutating a database in pull-request validation.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SQL_ROOT = ROOT / "extension" / "laplace_substrate" / "sql"
MANIFESTS = (SQL_ROOT / "manifest.install", SQL_ROOT / "manifest.upgrade")

CREATE_VIEW_RE = re.compile(
    r"\bCREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+laplace\.(v_[a-z0-9_]+)\b",
    re.IGNORECASE,
)
VIEW_REF_RE = re.compile(r"\blaplace\.(v_[a-z0-9_]+)\b", re.IGNORECASE)
DROP_VIEW_RE = re.compile(r"^\s*DROP\s+VIEW\b", re.IGNORECASE)


def manifest_modules(path: Path) -> list[str]:
    modules: list[str] = []
    seen: set[str] = set()
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line in seen:
            raise ValueError(f"{path.name}: duplicate module: {line}")
        module_path = SQL_ROOT / line
        if not module_path.is_file():
            raise ValueError(f"{path.name}: missing module: {line}")
        seen.add(line)
        modules.append(line)
    return modules


def sql_without_line_comments(text: str) -> list[str]:
    cleaned: list[str] = []
    for raw in text.splitlines():
        # Manifest dependency references in comments are documentation, not bindings.
        line = raw.split("--", 1)[0]
        if line.strip():
            cleaned.append(line)
    return cleaned


def declared_views(modules: list[str], manifest: Path) -> dict[str, str]:
    owners: dict[str, str] = {}
    for module in modules:
        text = (SQL_ROOT / module).read_text(encoding="utf-8")
        for match in CREATE_VIEW_RE.finditer(text):
            view = match.group(1).lower()
            previous = owners.get(view)
            if previous is not None and previous != module:
                raise ValueError(
                    f"{manifest.name}: laplace.{view} is declared by both "
                    f"{previous} and {module}"
                )
            owners[view] = module
    return owners


def validate_manifest(path: Path) -> list[str]:
    modules = manifest_modules(path)
    positions = {module: index for index, module in enumerate(modules)}
    owners = declared_views(modules, path)
    errors: list[str] = []

    for module in modules:
        module_index = positions[module]
        text = (SQL_ROOT / module).read_text(encoding="utf-8")
        declared_here = {
            match.group(1).lower() for match in CREATE_VIEW_RE.finditer(text)
        }

        for line_number, line in enumerate(sql_without_line_comments(text), start=1):
            # DROP VIEW is intentionally allowed before the replacement definition;
            # several compatibility shims use it to sever an old function binding.
            if DROP_VIEW_RE.match(line):
                continue
            for match in VIEW_REF_RE.finditer(line):
                view = match.group(1).lower()
                if view in declared_here and CREATE_VIEW_RE.search(line):
                    continue
                owner = owners.get(view)
                if owner is None:
                    continue
                owner_index = positions[owner]
                if module_index < owner_index:
                    errors.append(
                        f"{path.name}: {module}:{line_number} references "
                        f"laplace.{view} before {owner}"
                    )
    return errors


def main() -> int:
    errors: list[str] = []
    try:
        for manifest in MANIFESTS:
            errors.extend(validate_manifest(manifest))
    except ValueError as exc:
        errors.append(str(exc))

    if errors:
        print("SQL manifest dependency order is invalid:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    print("SQL_MANIFEST_DEPENDENCY_ORDER_OK manifests=install,upgrade")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
