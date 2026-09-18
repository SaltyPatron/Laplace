#!/usr/bin/env python3
"""Plan the minimum safe mainline qualification work for one source revision."""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import subprocess
from pathlib import Path

DEV_SUITES = ("native-dev", "managed-dev", "uci-dev", "browser-dev")

# Conservative dependency map. The planner may select extra work, but must never
# omit work for a production surface it does not understand.
RULES = (
    (("engine/", "extension/", "cmake/", "CMakeLists.txt"), ("native", "managed", "uci"), ("native-dev", "managed-dev", "uci-dev")),
    (("app/",), ("managed",), ("managed-dev",)),
    (("web/",), ("web",), ("browser-dev",)),
    (("db/",), ("database",), ()),
    (("deploy/",), ("deployment",), ()),
)

ROOT_FILES_FULL = {
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "NuGet.Config",
    ".gitmodules",
}

ALL_DEV_COMPONENTS = ("native", "managed", "uci", "web")


def _starts(path: str, prefix: str) -> bool:
    return path == prefix.rstrip("/") or path.startswith(prefix)


def classify_paths(paths: list[str]) -> dict:
    components: set[str] = set()
    suites: set[str] = set()
    reasons: dict[str, list[str]] = {suite: [] for suite in DEV_SUITES}
    unknown: list[str] = []
    ignored: list[str] = []

    for path in paths:
        if path.startswith("docs/") or path.endswith(".md") or path.startswith(".github/"):
            ignored.append(path)
            continue

        matched = False
        if path in ROOT_FILES_FULL:
            matched = True
            components.update(ALL_DEV_COMPONENTS)
            suites.update(DEV_SUITES)
            for suite in DEV_SUITES:
                reasons[suite].append(path)

        if path.startswith("scripts/"):
            matched = True
            # Scripts are orchestration/test/build inputs. Until a script-specific
            # dependency manifest exists, invalidate the complete dev qualification.
            components.update(ALL_DEV_COMPONENTS)
            suites.update(DEV_SUITES)
            for suite in DEV_SUITES:
                reasons[suite].append(path)

        for prefixes, comps, rule_suites in RULES:
            if any(_starts(path, prefix) for prefix in prefixes):
                matched = True
                components.update(comps)
                suites.update(rule_suites)
                for suite in rule_suites:
                    reasons[suite].append(path)

        # Chess/UCI is intentionally narrower than all managed code.
        if path.startswith("app/Laplace.Chess") or path.startswith("app/Laplace.Chess.Uci"):
            matched = True
            components.add("uci")
            suites.add("uci-dev")
            reasons["uci-dev"].append(path)

        # Browser contracts depend on the web tree plus the HTTP/OpenAI contract surfaces.
        if (
            path.startswith("app/Laplace.Api.Contracts")
            or path.startswith("app/Laplace.Endpoints.OpenAICompat")
        ):
            matched = True
            components.add("web")
            suites.add("browser-dev")
            reasons["browser-dev"].append(path)

        if not matched:
            unknown.append(path)

    full = bool(unknown)
    if full:
        components.update(ALL_DEV_COMPONENTS)
        suites.update(DEV_SUITES)
        for suite in DEV_SUITES:
            reasons[suite].extend(unknown)

    return {
        "components": sorted(components),
        "dev_suites": [suite for suite in DEV_SUITES if suite in suites],
        "full_qualification": full,
        "unknown_paths": sorted(unknown),
        "ignored_paths": sorted(ignored),
        "reasons": {suite: sorted(set(values)) for suite, values in reasons.items() if values},
    }


def git_changed_files(root: Path, base: str, head: str) -> tuple[list[str], bool]:
    if not base or set(base) == {"0"}:
        return [], True
    try:
        result = subprocess.run(
            ["git", "diff", "--name-only", "--diff-filter=ACMRT", f"{base}..{head}"],
            cwd=root,
            check=True,
            text=True,
            capture_output=True,
        )
    except subprocess.CalledProcessError:
        return [], True
    return [line.strip() for line in result.stdout.splitlines() if line.strip()], False


def write_github_outputs(path: Path, plan: dict) -> None:
    with path.open("a", encoding="utf-8") as stream:
        stream.write(f"dev_suites={','.join(plan['dev_suites'])}\n")
        stream.write(f"components={','.join(plan['components'])}\n")
        stream.write(f"full_qualification={'true' if plan['full_qualification'] else 'false'}\n")
        stream.write(f"changed_count={len(plan['changed_files'])}\n")


def write_summary(path: Path, plan: dict) -> None:
    suites = ", ".join(plan["dev_suites"]) or "none"
    components = ", ".join(plan["components"]) or "none"
    with path.open("a", encoding="utf-8") as stream:
        stream.write("## Product impact plan\n\n")
        stream.write(f"- Changed files: {len(plan['changed_files'])}\n")
        stream.write(f"- Affected components: {components}\n")
        stream.write(f"- Development suites to qualify: {suites}\n")
        stream.write(f"- Conservative full qualification: {'yes' if plan['full_qualification'] else 'no'}\n")
        if plan["unknown_paths"]:
            stream.write("- Unknown production paths forcing full qualification:\n")
            for item in plan["unknown_paths"]:
                stream.write(f"  - \`{item}\`\n")
        if plan["reasons"]:
            stream.write("\n### Why these suites are invalidated\n")
            for suite in DEV_SUITES:
                values = plan["reasons"].get(suite)
                if not values:
                    continue
                stream.write(f"- **{suite}**: " + ", ".join(f"\`{p}\`" for p in values[:12]))
                if len(values) > 12:
                    stream.write(f" (+{len(values) - 12} more)")
                stream.write("\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default=".")
    parser.add_argument("--base", default="")
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--changed-file", action="append", default=[])
    parser.add_argument("--github-output")
    parser.add_argument("--summary")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    changed = list(args.changed_file)
    forced_full = False
    if not changed:
        changed, forced_full = git_changed_files(root, args.base, args.head)

    plan = classify_paths(changed)
    if forced_full:
        plan["full_qualification"] = True
        plan["components"] = list(ALL_DEV_COMPONENTS)
        plan["dev_suites"] = list(DEV_SUITES)
        plan["unknown_paths"] = ["<unable-to-resolve-base>"]
        plan["reasons"] = {suite: ["<unable-to-resolve-base>"] for suite in DEV_SUITES}
    plan["base"] = args.base
    plan["head"] = args.head
    plan["changed_files"] = changed

    if args.github_output:
        write_github_outputs(Path(args.github_output), plan)
    if args.summary:
        write_summary(Path(args.summary), plan)

    print(json.dumps(plan, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
