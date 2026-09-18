#!/usr/bin/env python3
"""Plan the minimum safe mainline qualification and delivery work for one revision."""
from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path

from ci_product_scope import ignored as product_ignored
from ci_managed_graph import plan as plan_managed_graph

DEV_SUITES = ("native-dev", "managed-dev", "uci-dev", "browser-dev")
DB_SUITES = ("db-health", "native-db", "managed-db")
LIVE_SUITES = ("live-floor", "live-api", "managed-live", "generation-eval")
DELIVERY_ACTIONS = ("install", "database", "reconcile", "publish", "live")
BASE_LIVE_SUITES = ("live-floor", "live-api")
ALL_DEV_COMPONENTS = ("native", "managed", "uci", "web")
ALL_COMPONENTS = ("native", "managed", "uci", "web", "database", "deployment")

ROOT_FILES_FULL = {
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "global.json",
    "NuGet.Config",
    ".gitmodules",
}


def _starts(path: str, prefix: str) -> bool:
    return path == prefix.rstrip("/") or path.startswith(prefix)


def classify_paths(paths: list[str]) -> dict:
    components: set[str] = set()
    build_components: set[str] = set()
    dev_suites: set[str] = set()
    db_suites: set[str] = set()
    live_suites: set[str] = set()
    delivery_actions: set[str] = set()
    reasons: dict[str, list[str]] = {
        suite: [] for suite in (*DEV_SUITES, *DB_SUITES, *LIVE_SUITES)
    }
    unknown: list[str] = []
    ignored: list[str] = []
    publish_scope = "api"
    product_change = False
    force_full = False

    def invalidate(suites: tuple[str, ...], path: str) -> None:
        for suite in suites:
            reasons[suite].append(path)

    def full(path: str) -> None:
        nonlocal force_full, publish_scope, product_change
        force_full = True
        product_change = True
        publish_scope = "full"
        components.update(ALL_COMPONENTS)
        build_components.update(("native", "managed", "web"))
        dev_suites.update(DEV_SUITES)
        db_suites.update(DB_SUITES)
        live_suites.update(LIVE_SUITES)
        delivery_actions.update(DELIVERY_ACTIONS)
        invalidate(DEV_SUITES, path)
        invalidate(DB_SUITES, path)
        invalidate(LIVE_SUITES, path)

    for path in paths:
        if product_ignored(path):
            ignored.append(path)
            continue

        if path in ROOT_FILES_FULL:
            full(path)
            continue

        matched = False

        if (
            path == "CMakeLists.txt"
            or path.startswith("cmake/")
            or path.startswith("engine/")
            or path.startswith("extension/")
        ):
            matched = product_change = True
            publish_scope = "full"
            components.update(("native", "managed", "uci", "database"))
            build_components.update(("native", "managed"))
            dev_suites.update(("native-dev", "managed-dev", "uci-dev"))
            db_suites.update(DB_SUITES)
            live_suites.update(LIVE_SUITES)
            delivery_actions.update(DELIVERY_ACTIONS)
            invalidate(("native-dev", "managed-dev", "uci-dev"), path)
            invalidate(DB_SUITES, path)
            invalidate(LIVE_SUITES, path)

        if path.startswith("app/"):
            matched = product_change = True
            api_scoped = (
                path.startswith("app/Laplace.Api.Contracts")
                or path.startswith("app/Laplace.Endpoints.OpenAICompat")
            )
            if not api_scoped:
                publish_scope = "full"
            components.add("managed")
            build_components.add("managed")
            dev_suites.add("managed-dev")
            live_suites.update(LIVE_SUITES)
            delivery_actions.update(("publish", "live"))
            invalidate(("managed-dev",), path)
            invalidate(LIVE_SUITES, path)

            if path.startswith("app/Laplace.Chess") or path.startswith("app/Laplace.Chess.Uci"):
                publish_scope = "full"
                components.add("uci")
                dev_suites.add("uci-dev")
                invalidate(("uci-dev",), path)

            if (
                path.startswith("app/Laplace.Substrate")
                or path.startswith("app/Laplace.Migrations")
            ):
                components.add("database")
                db_suites.update(("db-health", "managed-db"))
                delivery_actions.update(("database", "reconcile"))
                invalidate(("db-health", "managed-db"), path)

            if (
                path.startswith("app/Laplace.Endpoints.Mcp")
                or path.startswith("app/Laplace.Endpoints.Lichess")
            ):
                publish_scope = "full"

            if (
                path.startswith("app/Laplace.Api.Contracts")
                or path.startswith("app/Laplace.Endpoints.OpenAICompat")
            ):
                components.add("web")
                dev_suites.add("browser-dev")
                invalidate(("browser-dev",), path)

        if path.startswith("web/"):
            matched = product_change = True
            components.add("web")
            build_components.add("web")
            dev_suites.add("browser-dev")
            live_suites.update(BASE_LIVE_SUITES)
            delivery_actions.update(("publish", "live"))
            invalidate(("browser-dev",), path)
            invalidate(BASE_LIVE_SUITES, path)

        if path.startswith("db/"):
            matched = product_change = True
            components.add("database")
            build_components.add("managed")
            db_suites.update(("db-health", "managed-db"))
            live_suites.update(LIVE_SUITES)
            delivery_actions.update(("database", "reconcile", "publish", "live"))
            invalidate(("db-health", "managed-db"), path)
            invalidate(LIVE_SUITES, path)

        if path.startswith("deploy/"):
            matched = product_change = True
            publish_scope = "full"
            components.add("deployment")
            build_components.add("managed")
            live_suites.update(LIVE_SUITES)
            delivery_actions.update(("publish", "live"))
            invalidate(LIVE_SUITES, path)

        if not matched:
            unknown.append(path)
            full(path)

    # Every delivered revision has an exact application revision receipt and a
    # universal live floor. This is deliberately much smaller than full live
    # qualification and does not imply native/DB mutation.
    if product_change:
        delivery_actions.update(("publish", "live"))
        live_suites.update(BASE_LIVE_SUITES)

    return {
        "components": sorted(components),
        "build_components": sorted(build_components),
        "dev_suites": [suite for suite in DEV_SUITES if suite in dev_suites],
        "db_suites": [suite for suite in DB_SUITES if suite in db_suites],
        "live_suites": [suite for suite in LIVE_SUITES if suite in live_suites],
        "delivery_actions": [
            action for action in DELIVERY_ACTIONS if action in delivery_actions
        ],
        "publish_scope": "full" if publish_scope == "full" else "api",
        "full_qualification": force_full or bool(unknown),
        "unknown_paths": sorted(set(unknown)),
        "ignored_paths": sorted(ignored),
        "reasons": {
            suite: sorted(set(values))
            for suite, values in reasons.items()
            if values
        },
    }


def git_changed_files(root: Path, base: str, head: str) -> tuple[list[str], bool]:
    if not base or set(base) == {"0"}:
        return [], True
    try:
        result = subprocess.run(
            ["git", "diff", "--name-only", "--diff-filter=ACMRTD", f"{base}..{head}"],
            cwd=root,
            check=True,
            text=True,
            capture_output=True,
        )
    except subprocess.CalledProcessError:
        return [], True
    return [line.strip() for line in result.stdout.splitlines() if line.strip()], False


def force_full_plan(plan: dict) -> None:
    plan["full_qualification"] = True
    plan["components"] = list(ALL_COMPONENTS)
    plan["build_components"] = ["native", "managed", "web"]
    plan["dev_suites"] = list(DEV_SUITES)
    plan["db_suites"] = list(DB_SUITES)
    plan["live_suites"] = list(LIVE_SUITES)
    plan["delivery_actions"] = list(DELIVERY_ACTIONS)
    plan["publish_scope"] = "full"
    plan["unknown_paths"] = ["<unable-to-resolve-base>"]
    plan["reasons"] = {
        suite: ["<unable-to-resolve-base>"]
        for suite in (*DEV_SUITES, *DB_SUITES, *LIVE_SUITES)
    }


def write_github_outputs(path: Path, plan: dict) -> None:
    with path.open("a", encoding="utf-8") as stream:
        for name in (
            "dev_suites",
            "db_suites",
            "live_suites",
            "delivery_actions",
            "components",
            "build_components",
            "managed_test_projects",
            "managed_build_projects",
        ):
            stream.write(f"{name}={','.join(plan[name])}\n")
        stream.write(f"publish_scope={plan['publish_scope']}\n")
        stream.write(
            f"full_qualification={'true' if plan['full_qualification'] else 'false'}\n"
        )
        stream.write(f"changed_count={len(plan['changed_files'])}\n")


def write_summary(path: Path, plan: dict) -> None:
    def joined(name: str) -> str:
        return ", ".join(plan[name]) or "none"

    with path.open("a", encoding="utf-8") as stream:
        stream.write("## Product impact plan\n\n")
        stream.write(f"- Changed files: {len(plan['changed_files'])}\n")
        stream.write(f"- Affected components: {joined('components')}\n")
        stream.write(f"- Candidate build components: {joined('build_components')}\n")
        stream.write(f"- Managed build projects: {joined('managed_build_projects')}\n")
        stream.write(f"- Development suites: {joined('dev_suites')}\n")
        stream.write(f"- Managed test projects: {joined('managed_test_projects')}\n")
        stream.write(f"- Database suites: {joined('db_suites')}\n")
        stream.write(f"- Delivery actions: {joined('delivery_actions')}\n")
        stream.write(f"- Publication scope: {plan['publish_scope']}\n")
        stream.write(f"- Live suites: {joined('live_suites')}\n")
        stream.write(
            f"- Conservative full qualification: "
            f"{'yes' if plan['full_qualification'] else 'no'}\n"
        )
        if plan["unknown_paths"]:
            stream.write("- Unknown paths forcing full qualification:\n")
            for item in plan["unknown_paths"]:
                stream.write(f"  - `{item}`\n")
        if plan["reasons"]:
            stream.write("\n### Invalidated verification\n")
            for suite in (*DEV_SUITES, *DB_SUITES, *LIVE_SUITES):
                values = plan["reasons"].get(suite)
                if not values:
                    continue
                stream.write(
                    f"- **{suite}**: "
                    + ", ".join(f"`{p}`" for p in values[:12])
                )
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
        force_full_plan(plan)

    managed = plan_managed_graph(root, changed)
    managed_dev_selected = "managed-dev" in plan["dev_suites"]
    app_product_change = any(
        path.startswith("app/") and not product_ignored(path)
        for path in changed
    )
    native_or_global_managed_change = any(
        path in ROOT_FILES_FULL
        or path == "CMakeLists.txt"
        or path.startswith("cmake/")
        or path.startswith("engine/")
        or path.startswith("extension/")
        for path in changed
        if not product_ignored(path)
    )

    if managed_dev_selected:
        if native_or_global_managed_change or forced_full:
            all_managed = plan_managed_graph(root, ["Directory.Packages.props"])
            plan["managed_test_projects"] = ["all"]
            managed = all_managed
        elif app_product_change:
            plan["managed_test_projects"] = managed["test_projects"]
            if not plan["managed_test_projects"]:
                plan["dev_suites"] = [
                    suite for suite in plan["dev_suites"] if suite != "managed-dev"
                ]
        else:
            plan["managed_test_projects"] = ["all"]
    else:
        plan["managed_test_projects"] = []

    if "managed" in plan["build_components"]:
        if (
            native_or_global_managed_change
            or forced_full
            or plan["db_suites"]
        ):
            plan["managed_build_projects"] = ["all"]
        else:
            build_projects = set(managed["build_projects"])
            if plan["publish_scope"] == "full":
                build_projects.update({
                    "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj",
                    "app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj",
                    "app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj",
                    "app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj",
                })
            elif "publish" in plan["delivery_actions"]:
                build_projects.add(
                    "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj"
                )
            plan["managed_build_projects"] = sorted(build_projects) or ["all"]
    else:
        plan["managed_build_projects"] = []

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
