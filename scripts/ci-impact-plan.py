#!/usr/bin/env python3
"""Plan the minimum safe mainline qualification and delivery work for one revision."""
from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path

from ci_product_scope import ignored as product_ignored, managed_test_path
from ci_managed_projects import (
    load_projects as load_managed_projects,
    plan_changed as plan_managed_projects,
    project_for_path as managed_project_for_path,
    test_filter_for_paths as managed_test_filter_for_paths,
)

DEV_SUITES = ("native-dev", "managed-dev", "uci-dev", "browser-dev")
DB_SUITES = ("db-health", "native-db", "managed-db")
STANDARD_LIVE_SUITES = ("live-floor", "live-api", "managed-live", "generation-eval")
CHESS_PROVIDER_LIVE_SUITE = "chess-provider-live"
LIVE_SUITES = (*STANDARD_LIVE_SUITES, CHESS_PROVIDER_LIVE_SUITE)
DELIVERY_ACTIONS = ("install", "database", "reconcile", "publish", "live")
BASE_LIVE_SUITES = ("live-floor", "live-api")
ALL_DEV_COMPONENTS = ("native", "managed", "uci", "web")
ALL_COMPONENTS = ("native", "managed", "uci", "web", "database", "deployment")
API_PUBLISH_PROJECT = "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj"
UCI_PUBLISH_PROJECT = "app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj"
MCP_PUBLISH_PROJECT = "app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj"
LICHESS_PUBLISH_PROJECT = "app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj"
FULL_PUBLISH_PROJECTS = (API_PUBLISH_PROJECT, UCI_PUBLISH_PROJECT, MCP_PUBLISH_PROJECT, LICHESS_PUBLISH_PROJECT)

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


def native_test_path(path: str) -> bool:
    normalized = path.replace("\\", "/")
    return (
        normalized.startswith("engine/") and "/tests/" in normalized
    ) or (
        normalized.startswith("extension/") and "/tests/" in normalized
    )


def managed_test_tiers(root: Path, path: str) -> set[str]:
    """Read explicit xUnit Tier traits from one changed managed test source."""
    if not path.endswith(".cs"):
        return set()
    source = root / path
    if not source.is_file():
        return set()
    text = source.read_text(encoding="utf-8")
    tiers: set[str] = set()
    marker = 'Trait("Tier", "'
    start = 0
    while True:
        start = text.find(marker, start)
        if start < 0:
            break
        value_start = start + len(marker)
        value_end = text.find('"', value_start)
        if value_end < 0:
            break
        tiers.add(text[value_start:value_end].strip().lower())
        start = value_end + 1
    return tiers


def classify_paths(paths: list[str], root: Path | None = None) -> dict:
    root = (root or Path(".")).resolve()
    managed_projects = load_managed_projects(root)
    managed_changed_paths: list[str] = []
    managed_build_force_all = False
    managed_test_force_all = False
    managed_db_force_all = False
    managed_live_force_all = False
    managed_build_required: set[str] = set()
    delivery_paths = [
        path for path in paths
        if not product_ignored(path) and not managed_test_path(path)
    ]
    pure_uci = bool(delivery_paths) and all(
        path.startswith("app/Laplace.Chess.Uci/") for path in delivery_paths
    )

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
        nonlocal managed_build_force_all, managed_test_force_all
        nonlocal managed_db_force_all, managed_live_force_all
        force_full = True
        managed_build_force_all = True
        managed_test_force_all = True
        managed_db_force_all = True
        managed_live_force_all = True
        product_change = True
        publish_scope = "full"
        components.update(ALL_COMPONENTS)
        build_components.update(("native", "managed", "web"))
        dev_suites.update(DEV_SUITES)
        db_suites.update(DB_SUITES)
        live_suites.update(STANDARD_LIVE_SUITES)
        delivery_actions.update(DELIVERY_ACTIONS)
        invalidate(DEV_SUITES, path)
        invalidate(DB_SUITES, path)
        invalidate(STANDARD_LIVE_SUITES, path)

    for path in paths:
        if product_ignored(path):
            ignored.append(path)
            continue

        if path in ROOT_FILES_FULL:
            full(path)
            continue

        matched = False

        if native_test_path(path):
            matched = True
            build_components.add("native")
            dev_suites.add("native-dev")
            invalidate(("native-dev",), path)
            continue

        if path.startswith("extension/"):
            # Native/SQL is proved by native-dev and native-db. Forcing every
            # managed test project here is the "ocean, one drop at a time"
            # qualification that dies at the 15-minute packed deadline.
            matched = product_change = True
            publish_scope = "full"
            components.update(("native", "database"))
            build_components.add("native")
            dev_suites.add("native-dev")
            db_suites.update(("db-health", "native-db"))
            delivery_actions.update(("install", "database", "reconcile", "publish", "live"))
            invalidate(("native-dev",), path)
            invalidate(("db-health", "native-db"), path)
            continue

        if (
            path == "CMakeLists.txt"
            or path.startswith("cmake/")
            or path.startswith("engine/")
        ):
            matched = product_change = True
            managed_build_required.update(FULL_PUBLISH_PROJECTS)
            # Rebuild/publish native-bound binaries. Do not schedule Chess,
            # Decomposers, Agents, … as managed-dev for a core .cpp edit.
            managed_changed_paths.append(
                "app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj"
            )
            managed_db_force_all = True
            managed_live_force_all = True
            publish_scope = "full"
            components.update(("native", "managed", "uci", "database"))
            build_components.update(("native", "managed"))
            dev_suites.update(("native-dev", "managed-dev", "uci-dev"))
            db_suites.update(DB_SUITES)
            live_suites.update(STANDARD_LIVE_SUITES)
            delivery_actions.update(DELIVERY_ACTIONS)
            invalidate(("native-dev", "managed-dev", "uci-dev"), path)
            invalidate(DB_SUITES, path)
            invalidate(STANDARD_LIVE_SUITES, path)

        if path.startswith("app/"):
            matched = True
            managed_changed_paths.append(path)
            managed_project = managed_project_for_path(managed_projects, path)
            if managed_project is not None and managed_projects[managed_project].is_test:
                build_components.add("managed")
                tiers = managed_test_tiers(root, path)
                # A DB/live-only test edit belongs to its actual execution tier.
                # Sending it through managed-dev adds Tier!=db/live and can produce
                # a false "zero tests matched" failure before the relevant suite runs.
                if tiers == {"db"}:
                    db_suites.add("managed-db")
                    invalidate(("managed-db",), path)
                elif tiers == {"live"}:
                    live_suites.add("managed-live")
                    invalidate(("managed-live",), path)
                else:
                    dev_suites.add("managed-dev")
                    invalidate(("managed-dev",), path)
                continue

            product_change = True
            isolated_uci = pure_uci and path.startswith("app/Laplace.Chess.Uci/")
            api_scoped = (
                path.startswith("app/Laplace.Api.Contracts")
                or path.startswith("app/Laplace.Endpoints.OpenAICompat")
            )
            if isolated_uci:
                publish_scope = "uci"
                managed_build_required.add(UCI_PUBLISH_PROJECT)
            elif api_scoped:
                managed_build_required.add(API_PUBLISH_PROJECT)
            else:
                publish_scope = "full"
                managed_build_required.update(FULL_PUBLISH_PROJECTS)
            components.add("managed")
            build_components.add("managed")
            if not isolated_uci:
                dev_suites.add("managed-dev")
                live_suites.update(STANDARD_LIVE_SUITES)
                delivery_actions.update(("publish", "live"))
                invalidate(("managed-dev",), path)
                invalidate(STANDARD_LIVE_SUITES, path)
                if path.startswith("app/Laplace.Chess/"):
                    live_suites.add(CHESS_PROVIDER_LIVE_SUITE)
                    invalidate((CHESS_PROVIDER_LIVE_SUITE,), path)
            else:
                delivery_actions.add("publish")

            if path.startswith("app/Laplace.Chess") or path.startswith("app/Laplace.Chess.Uci"):
                publish_scope = "uci" if isolated_uci else "full"
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
            # The SPA has an independent sealed artifact and publication transaction.
            # A web-only change therefore builds/qualifies/publishes only the web
            # component; API/UCI/MCP/Lichess binaries remain byte-identical.
            publish_scope = "web"
            components.add("web")
            build_components.add("web")
            dev_suites.add("browser-dev")
            live_suites.update(BASE_LIVE_SUITES)
            delivery_actions.update(("publish", "live"))
            invalidate(("browser-dev",), path)
            invalidate(BASE_LIVE_SUITES, path)

        if path.startswith("db/"):
            matched = product_change = True
            managed_build_required.add(API_PUBLISH_PROJECT)
            managed_db_force_all = True
            managed_live_force_all = True
            components.add("database")
            build_components.add("managed")
            db_suites.update(("db-health", "managed-db"))
            live_suites.update(STANDARD_LIVE_SUITES)
            delivery_actions.update(("database", "reconcile", "publish", "live"))
            invalidate(("db-health", "managed-db"), path)
            invalidate(STANDARD_LIVE_SUITES, path)

        if path.startswith("deploy/"):
            matched = product_change = True
            managed_build_required.update(FULL_PUBLISH_PROJECTS)
            managed_live_force_all = True
            publish_scope = "full"
            components.add("deployment")
            build_components.add("managed")
            live_suites.update(STANDARD_LIVE_SUITES)
            delivery_actions.update(("publish", "live"))
            invalidate(STANDARD_LIVE_SUITES, path)

        if not matched:
            unknown.append(path)
            full(path)

    managed_impact = plan_managed_projects(root, managed_changed_paths)
    managed_test_filter = ""
    if managed_changed_paths and all(managed_test_path(path) for path in managed_changed_paths):
        # Exact class filters are a managed-dev optimization only. An explicitly
        # DB/live/perf-tier class would be intersected with Tier!=db/live/perf by
        # managed-dev and match nothing, so leave that tier to its own suite.
        explicit_tiers = set().union(
            *(managed_test_tiers(root, path) for path in managed_changed_paths)
        )
        if not (explicit_tiers & {"db", "live", "perf"}):
            managed_test_filter = managed_test_filter_for_paths(root, managed_changed_paths)
    if pure_uci and managed_impact["test_projects"]:
        dev_suites.add("managed-dev")

    def managed_test_selection(selected: bool, force_all: bool) -> list[str]:
        if not selected:
            return []
        if force_all or managed_impact["full"]:
            return ["all"]
        return list(managed_impact["test_projects"])

    managed_test_projects = managed_test_selection(
        "managed-dev" in dev_suites, managed_test_force_all)
    managed_db_test_projects = managed_test_selection(
        "managed-db" in db_suites, managed_db_force_all)
    managed_live_test_projects = managed_test_selection(
        "managed-live" in live_suites, managed_live_force_all)

    if "managed" not in build_components:
        managed_build_projects: list[str] = []
    elif managed_build_force_all or managed_impact["full"]:
        managed_build_projects = ["all"]
    else:
        build_roots = set(managed_impact["build_projects"])
        build_roots.update(managed_build_required)
        all_tests = {
            path for path, project in managed_projects.items() if project.is_test
        }
        for selection in (
            managed_test_projects,
            managed_db_test_projects,
            managed_live_test_projects,
        ):
            if selection == ["all"]:
                build_roots.update(all_tests)
            else:
                build_roots.update(selection)
        managed_build_projects = sorted(build_roots)

    # Every delivered revision has an exact application revision receipt and a
    # universal live floor. This is deliberately much smaller than full live
    # qualification and does not imply native/DB mutation.
    if product_change:
        delivery_actions.add("publish")
        if not pure_uci:
            delivery_actions.add("live")
            live_suites.update(BASE_LIVE_SUITES)

    return {
        "components": sorted(components),
        "build_components": sorted(build_components),
        "managed_build_projects": managed_build_projects,
        "managed_test_projects": managed_test_projects,
        "managed_test_filter": managed_test_filter,
        "managed_db_test_projects": managed_db_test_projects,
        "managed_live_test_projects": managed_live_test_projects,
        "dev_suites": [suite for suite in DEV_SUITES if suite in dev_suites],
        "db_suites": [suite for suite in DB_SUITES if suite in db_suites],
        "live_suites": [suite for suite in LIVE_SUITES if suite in live_suites],
        "delivery_actions": [
            action for action in DELIVERY_ACTIONS if action in delivery_actions
        ],
        "publish_scope": publish_scope if publish_scope in ("web", "api", "uci", "full") else "full",
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
    plan["managed_build_projects"] = ["all"]
    plan["managed_test_projects"] = ["all"]
    plan["managed_test_filter"] = ""
    plan["managed_db_test_projects"] = ["all"]
    plan["managed_live_test_projects"] = ["all"]
    plan["dev_suites"] = list(DEV_SUITES)
    plan["db_suites"] = list(DB_SUITES)
    plan["live_suites"] = list(STANDARD_LIVE_SUITES)
    plan["delivery_actions"] = list(DELIVERY_ACTIONS)
    plan["publish_scope"] = "full"
    plan["unknown_paths"] = ["<unable-to-resolve-base>"]
    plan["reasons"] = {
        suite: ["<unable-to-resolve-base>"]
        for suite in (*DEV_SUITES, *DB_SUITES, *STANDARD_LIVE_SUITES)
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
            "managed_build_projects",
            "managed_test_projects",
            "managed_db_test_projects",
            "managed_live_test_projects",
        ):
            stream.write(f"{name}={','.join(plan[name])}\n")
        stream.write(f"managed_test_filter={plan.get('managed_test_filter', '')}\n")
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
        stream.write(f"- Managed unit-test projects: {joined('managed_test_projects')}\n")
        stream.write(f"- Managed test filter: {plan.get('managed_test_filter') or 'none'}\n")
        stream.write(f"- Managed DB-test projects: {joined('managed_db_test_projects')}\n")
        stream.write(f"- Managed live-test projects: {joined('managed_live_test_projects')}\n")
        stream.write(f"- Development suites: {joined('dev_suites')}\n")
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

    plan = classify_paths(changed, root)
    if forced_full:
        force_full_plan(plan)
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
