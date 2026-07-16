#!/usr/bin/env python3
"""Change-aware .NET project selection over the app/ ProjectReference graph.

Every project gets a Merkle fingerprint: sha256 of its own content (git index
blobs + live hashes of dirty/untracked files, no mtimes) folded with the
fingerprints of its transitive ProjectReferences and the app-global files
(Directory.*.props/targets, *.slnx, loose files under app/). A project is
"affected" iff its effective fingerprint differs from the stamp recorded at the
last successful build/test — so the affected set is dependent-closed by
construction: touching Laplace.Core marks every project that can see it.

Commands (stdout is the plan; diagnostics go to stderr):
  plan   --ns build            csproj paths (relative to app/) to `dotnet build`;
                               minimal roots — building them builds every
                               affected project. Empty output = up to date.
  plan   --ns test [--salt S]  affected *.Tests csproj paths. --salt folds an
                               external fingerprint (native engine, migrations)
                               into the staleness key.
  record --ns X [--salt S] [--projects NAME...]
                               stamp fingerprints (all projects, or only the
                               named ones) after the guarded action succeeded.

Stamps: build/.stamps/dotnet-<ns>.json. Exit 3 on any structural failure —
callers fall back to a full solution build/test.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP = ROOT / "app"
STAMP_DIR = ROOT / "build" / ".stamps"

PROJECT_REF = re.compile(r'ProjectReference\s+Include="([^"]+)"')


def sh(*args: str) -> str:
    return subprocess.run(
        args, cwd=ROOT, check=True, capture_output=True, text=True
    ).stdout


def sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def file_hash(path: Path) -> str:
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError:
        return "unreadable"


def discover_projects() -> dict[str, Path]:
    """name -> csproj path; one project per directory directly under app/."""
    projects: dict[str, Path] = {}
    for csproj in sorted(APP.glob("*/*.csproj")):
        projects[csproj.stem] = csproj
    if not projects:
        raise RuntimeError("no csproj files found under app/*/")
    return projects


def parse_deps(projects: dict[str, Path]) -> dict[str, list[str]]:
    """ProjectReference edges, resolved to project names; unknown refs ignored."""
    by_dir = {p.parent.name: name for name, p in projects.items()}
    deps: dict[str, list[str]] = {}
    for name, csproj in projects.items():
        edges: set[str] = set()
        for include in PROJECT_REF.findall(csproj.read_text(encoding="utf-8")):
            ref = (csproj.parent / include.replace("\\", "/")).resolve()
            target = by_dir.get(ref.parent.name)
            if target and target != name:
                edges.add(target)
        deps[name] = sorted(edges)
    return deps


def content_buckets(projects: dict[str, Path]) -> dict[str, list[str]]:
    """Per-project content lines from git, dirty files overlaid with live hashes."""
    dir_to_name = {p.parent.name: name for name, p in projects.items()}
    buckets: dict[str, list[str]] = {name: [] for name in projects}
    buckets["__global__"] = [f"tool {file_hash(Path(__file__).resolve())}"]

    def bucket_of(repo_path: str) -> str | None:
        parts = Path(repo_path).parts
        if len(parts) < 2 or parts[0] != "app":
            return None
        if len(parts) >= 3 and parts[1] in dir_to_name:
            return dir_to_name[parts[1]]
        return "__global__"

    for line in sh("git", "ls-files", "-s", "--", "app").splitlines():
        # "<mode> <blob> <stage>\t<path>"
        meta, _, path = line.partition("\t")
        target = bucket_of(path)
        if target:
            buckets[target].append(f"index {path} {meta.split()[1]}")

    dirty = sh("git", "diff", "--name-only", "--", "app").splitlines()
    untracked = sh(
        "git", "ls-files", "--others", "--exclude-standard", "--", "app"
    ).splitlines()
    for path in dirty + untracked:
        if not path:
            continue
        target = bucket_of(path)
        if not target:
            continue
        live = ROOT / path
        if live.is_file():
            buckets[target].append(f"dirty {path} {file_hash(live)}")
        else:
            buckets[target].append(f"gone {path}")
    return buckets


def effective_fps(
    projects: dict[str, Path],
    deps: dict[str, list[str]],
    buckets: dict[str, list[str]],
) -> dict[str, str]:
    global_fp = sha256("\n".join(sorted(buckets["__global__"])))
    raw = {
        name: sha256(global_fp + "\n" + "\n".join(sorted(buckets[name])))
        for name in projects
    }
    eff: dict[str, str] = {}
    in_flight: set[str] = set()

    def walk(name: str) -> str:
        if name in eff:
            return eff[name]
        if name in in_flight:
            raise RuntimeError(f"ProjectReference cycle through {name}")
        in_flight.add(name)
        folded = raw[name] + "".join(walk(d) for d in deps[name])
        in_flight.discard(name)
        eff[name] = sha256(folded)
        return eff[name]

    for name in projects:
        walk(name)
    return eff


def stamp_path(ns: str) -> Path:
    return STAMP_DIR / f"dotnet-{ns}.json"


def load_stamps(ns: str) -> dict[str, str]:
    try:
        return json.loads(stamp_path(ns).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def salted(eff: dict[str, str], salt: str) -> dict[str, str]:
    if not salt:
        return dict(eff)
    return {name: sha256(fp + salt) for name, fp in eff.items()}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("command", choices=["plan", "record"])
    ap.add_argument("--ns", required=True, choices=["build", "test"])
    ap.add_argument("--salt", default="")
    ap.add_argument("--projects", nargs="*", default=None,
                    help="record only these project names (default: all)")
    args = ap.parse_args()

    projects = discover_projects()
    deps = parse_deps(projects)
    keys = salted(effective_fps(projects, deps, content_buckets(projects)), args.salt)

    if args.command == "record":
        stamps = load_stamps(args.ns)
        names = args.projects if args.projects is not None else list(projects)
        for name in names:
            if name not in keys:
                raise RuntimeError(f"unknown project: {name}")
            stamps[name] = keys[name]
        STAMP_DIR.mkdir(parents=True, exist_ok=True)
        stamp_path(args.ns).write_text(
            json.dumps(stamps, indent=0, sort_keys=True), encoding="utf-8"
        )
        print(f"recorded {len(names)} project stamp(s) -> {stamp_path(args.ns)}",
              file=sys.stderr)
        return 0

    stamps = {} if os_force() else load_stamps(args.ns)
    changed = {name for name in projects if stamps.get(name) != keys[name]}

    if args.ns == "test":
        picked = sorted(n for n in changed if n.endswith(".Tests"))
    else:
        # Roots: changed projects no other CHANGED project depends on. The
        # affected set is dependent-closed, so building the roots builds all.
        changed_dep_targets = {d for n in changed for d in deps[n]}
        picked = sorted(changed - changed_dep_targets)

    rel = {name: projects[name].relative_to(APP) for name in picked}
    print(
        f"affected: {len(changed)}/{len(projects)} projects; "
        f"plan({args.ns}): {len(picked)}",
        file=sys.stderr,
    )
    for name in picked:
        print(rel[name])
    return 0


def os_force() -> bool:
    import os
    return os.environ.get("LAPLACE_FORCE_ALL") == "1"


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:  # structural failure -> caller does a full pass
        print(f"affected-app: {exc}", file=sys.stderr)
        sys.exit(3)
