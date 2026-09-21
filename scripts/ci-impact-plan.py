#!/usr/bin/env python3
"""Plan the minimum safe mainline qualification and delivery work for one revision."""
from __future__ import annotations

import argparse
import json
import re
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
BROWSER_TEST_SUITES = ("typecheck", "read-resource", "workspace-ui", "data-ui", "chess-ui")
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
MIGRATIONS_PUBLISH_PROJECT = "app/Laplace.Migrations/Laplace.Migrations.csproj"
FULL_PUBLISH_PROJECTS = (
    API_PUBLISH_PROJECT,
    UCI_PUBLISH_PROJECT,
    MCP_PUBLISH_PROJECT,
    LICHESS_PUBLISH_PROJECT,
    MIGRATIONS_PUBLISH_PROJECT,
)

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


def native_test_filter_for_paths(paths: list[str], root: Path) -> str:
    """Select CTest cases owned by affected native components/test files."""
    sources: set[Path] = set()
    explicit: set[str] = set()
    probe_names = {
        "generated_stage_sink_native_probe.c": "generated_stage_sink_pg_native_helpers",
        "physicality_descriptor_native_probe.c": "physicality_descriptor_pg_native_helpers",
        "physicality_readback_native_probe.c": "physicality_readback_pg_native_helpers",
        "test_ud_parse.c": "laplace_ud_parse_tests",
        "test_task_shape.c": "laplace_task_shape_tests",
    }
    for raw in paths:
        path = raw.replace("\\", "/")
        candidate = root / path
        if native_test_path(path):
            if candidate.suffix == ".cpp":
                sources.add(candidate)
            elif candidate.name in probe_names:
                explicit.add(probe_names[candidate.name])
            else:
                return ""
        elif path.startswith("engine/core/"):
            sources.update((root / "engine/core/tests").glob("*.cpp"))
            explicit.update(("laplace_ud_parse_tests", "laplace_task_shape_tests"))
        elif path.startswith("engine/dynamics/"):
            sources.update((root / "engine/dynamics/tests").glob("*.cpp"))
        elif path.startswith("engine/synthesis/"):
            sources.update((root / "engine/synthesis/tests").glob("*.cpp"))
        elif path.startswith("extension/laplace_substrate/"):
            explicit.update((
                "generated_stage_sink_pg_native_helpers",
                "physicality_descriptor_pg_native_helpers",
                "physicality_readback_pg_native_helpers",
            ))
        elif path.startswith(("engine/", "extension/")) or path in ROOT_FILES_FULL:
            return ""

    cases: set[str] = set(explicit)
    macro = re.compile(
        r"(?m)^\s*TEST(?:_F)?\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)"
    )
    for source in sources:
        if not source.is_file():
            return ""
        cases.update(".".join(match) for match in macro.findall(source.read_text(encoding="utf-8")))
    return "|".join(sorted(re.escape(case) for case in cases))


def native_db_test_filter_for_paths(paths: list[str]) -> str:
    owners = set()
    for raw in paths:
        path = raw.replace("\\", "/")
        if path.startswith("extension/laplace_geom/"):
            owners.add("laplace_geom")
        elif path.startswith("extension/laplace_substrate/"):
            owners.add("laplace_substrate")
        elif path.startswith("engine/core/"):
            owners.update(("laplace_geom", "laplace_substrate"))
        elif path.startswith("engine/dynamics/"):
            owners.add("laplace_substrate")
        elif path.startswith("extension/"):
            return ""
    if not owners:
        return ""
    return "^(?:" + "|".join(f"regress_{re.escape(owner)}" for owner in sorted(owners)) + ")$"


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
    browser_test_suites: set[str] = set()
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
    publish_required = True
    skip_default_live_floor = False
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
        browser_test_suites.update(BROWSER_TEST_SUITES)
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

        if path == "scripts/reconcile-highway-masks.sh":
            # This is a database-maintenance delivery owner, not product source.
            # Changing its orchestration must not classify as unknown and drag
            # browser/UCI/all-managed qualification into an otherwise bounded fix.
            matched = product_change = True
            publish_required = False
            components.update(("database", "deployment"))
            db_suites.add("db-health")
            delivery_actions.add("reconcile")
            invalidate(("db-health",), path)
            continue

        if path in (
            "scripts/pipeline.sh",
            "scripts/check-deployed-revision.sh",
            "scripts/product-ci.sh",
            "scripts/ingest-source.sh",
            "scripts/check-substrate-floor.sh",
            "scripts/ensure-foundation.sh",
        ):
            # Activation owner (systemd bounce of mapped native .so). Does not
            # change API/MCP/UCI/Lichess/UI/chess-lab bytes — do not rebuild
            # or republish them.
            matched = product_change = True
            publish_required = False
            components.add("native")
            build_components.add("native")
            delivery_actions.add("install")
            live_suites.add("live-api")
            skip_default_live_floor = True
            continue

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

        if path.startswith(("engine/core/", "engine/dynamics/", "engine/synthesis/")):
            matched = product_change = True
            managed_build_required.update(FULL_PUBLISH_PROJECTS)
            native_family = path.split("/", 2)[1]
            managed_changed_paths.append(
                "app/Laplace.Core.Tests/Laplace.Core.Tests.csproj"
            )
            publish_scope = "full"
            components.update(("native", "managed"))
            build_components.update(("native", "managed"))
            dev_suites.update(("native-dev", "managed-dev"))
            delivery_actions.update(("install", "publish"))
            invalidate(("native-dev", "managed-dev"), path)
            if native_family in ("core", "dynamics"):
                managed_changed_paths.append(
                    "app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj"
                )
                components.add("database")
                db_suites.update(DB_SUITES)
                delivery_actions.update(("database", "reconcile"))
                invalidate(DB_SUITES, path)
            if native_family == "core":
                managed_changed_paths.append(
                    "app/Laplace.Chess.Tests/Laplace.Chess.Tests.csproj"
                )
                components.add("uci")
                dev_suites.add("uci-dev")
                invalidate(("uci-dev",), path)
            continue

        if path == "CMakeLists.txt" or path.startswith("cmake/") or path.startswith("engine/"):
            full(path)
            continue

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
                if "db" in tiers and "live" not in tiers:
                    db_suites.add("managed-db")
                    invalidate(("managed-db",), path)
                elif "live" in tiers:
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

            if path.startswith("app/Laplace.Substrate"):
                # Managed substrate orchestration/CRUD code changes the shipped managed
                # binaries, not the installed PostgreSQL schema or native extension.
                # Keep DB-facing managed qualification available to explicit audits,
                # but do not run migrations, restart/reconcile the database, or scan
                # historical consensus on every C# edit.
                components.add("database")
                db_suites.add("managed-db")
                invalidate(("managed-db",), path)
            elif path.startswith("app/Laplace.Migrations"):
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
                browser_test_suites.add("typecheck")
                if ("Billing" in path or "Account" in path or "/Auth/" in path):
                    browser_test_suites.add("workspace-ui")
                elif not path.endswith("/AppComposition.cs"):
                    browser_test_suites.update(BROWSER_TEST_SUITES)
                invalidate(("browser-dev",), path)

        if path.startswith("web/"):
            matched = product_change = True
            # The SPA has an independent sealed artifact and publication transaction.
            # A web-only change therefore builds/qualifies/publishes only the web
            # component; API/UCI/MCP/Lichess binaries remain byte-identical.
            publish_scope = "full" if publish_scope in ("full", "uci") else "web"
            components.add("web")
            build_components.add("web")
            dev_suites.add("browser-dev")
            browser_test_suites.add("typecheck")
            if path.startswith(("web/src/billing/", "web/src/auth/")):
                browser_test_suites.add("workspace-ui")
            elif path.startswith("web/src/data/") or path == "web/scripts/test-data-workspace.mjs":
                browser_test_suites.update(("read-resource", "data-ui"))
            elif path.startswith("web/src/chess/"):
                browser_test_suites.add("chess-ui")
            elif path.startswith("web/src/home/"):
                pass
            elif path == "web/src/api/client.ts":
                browser_test_suites.update(("read-resource", "workspace-ui", "data-ui"))
            else:
                browser_test_suites.update(("read-resource", "workspace-ui", "data-ui"))
            live_suites.update(BASE_LIVE_SUITES)
            delivery_actions.update(("publish", "live"))
            invalidate(("browser-dev",), path)
            invalidate(BASE_LIVE_SUITES, path)

        if path.startswith("db/"):
            matched = product_change = True
            managed_build_required.add(API_PUBLISH_PROJECT)
            components.add("database")
            build_components.add("managed")
            # Versioned migrations are applied by the database delivery action
            # and then checked by the bounded health suite. They do not change
            # managed test binaries and must not expand into every Tier=db test.
            # Non-migration database assets retain the conservative managed DB
            # qualification until they have a narrower owner.
            if path.startswith("db/migrations/"):
                db_suites.add("db-health")
                invalidate(("db-health",), path)
            else:
                managed_db_force_all = True
                db_suites.update(("db-health", "managed-db"))
                invalidate(("db-health", "managed-db"), path)
            live_suites.update(STANDARD_LIVE_SUITES)
            if path.startswith("db/migrations/"):
                # Versioned migrations apply their own bounded schema/data change
                # and db-health validates the installed result. Highway-mask estate
                # reconciliation belongs to native/extension invalidations; running
                # it after an account, auth, or billing migration turns a small
                # deployment into a historical consensus scan.
                delivery_actions.update(("database", "publish", "live"))
            else:
                delivery_actions.update(("database", "reconcile", "publish", "live"))
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
    managed_db_test_filter = ""
    managed_live_test_filter = ""
    if managed_changed_paths and all(managed_test_path(path) for path in managed_changed_paths):
        # Exact class filters are a managed-dev optimization only. An explicitly
        # DB/live/perf-tier class would be intersected with Tier!=db/live/perf by
        # managed-dev and match nothing, so leave that tier to its own suite.
        explicit_tiers = set().union(
            *(managed_test_tiers(root, path) for path in managed_changed_paths)
        )
        exact_filter = managed_test_filter_for_paths(root, managed_changed_paths)
        if "db" in explicit_tiers and "live" not in explicit_tiers:
            managed_db_test_filter = exact_filter
        elif "live" in explicit_tiers:
            managed_live_test_filter = exact_filter
        elif not (explicit_tiers & {"db", "live", "perf"}):
            managed_test_filter = exact_filter
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
        managed_delivery_build_projects: list[str] = []
    elif managed_build_force_all or managed_impact["full"]:
        managed_build_projects = ["all"]
        # Automatic delivery never executes the broad development matrix. Build
        # production projects only; full qualification keeps the complete solution.
        managed_delivery_build_projects = sorted(
            path for path, project in managed_projects.items() if not project.is_test
        )
    else:
        build_roots = set(managed_impact["build_projects"])
        build_roots.update(managed_build_required)
        all_tests = {
            path for path, project in managed_projects.items() if project.is_test
        }
        # The main delivery lane compiles only shippable/referenced production roots.
        # Test projects remain in the qualification closure below, where --no-build
        # actually requires their binaries.
        managed_delivery_build_projects = sorted(build_roots - all_tests)
        selected_tests = set(managed_test_projects)
        selected_tests.update(managed_db_test_projects)
        selected_tests.update(managed_live_test_projects)
        build_roots.update(selected_tests)
        build_roots.difference_update(all_tests - selected_tests)
        managed_build_projects = sorted(build_roots)

    # Publication performs bounded activation/readiness checks. Full live suites
    # are explicit operator/qualification work and never ride every main deploy.
    if product_change:
        if publish_required:
            delivery_actions.add("publish")
    delivery_actions.discard("live")
    live_suites.clear()
    managed_live_test_projects = []

    # API and SPA artifacts have independent build and publication paths. A
    # revision touching both must publish both; whichever path happened to be
    # visited last must never erase the other component from the delivery plan.
    changed_api = any(path.startswith("app/Laplace.Endpoints.OpenAICompat")
                      or path.startswith("app/Laplace.Api.Contracts") for path in delivery_paths)
    changed_web = any(path.startswith("web/") for path in delivery_paths)
    if publish_scope != "full" and changed_api and changed_web:
        publish_scope = "api-web"

    return {
        "components": sorted(components),
        "build_components": sorted(build_components),
        "managed_build_projects": managed_build_projects,
        "managed_delivery_build_projects": managed_delivery_build_projects,
        "managed_test_projects": managed_test_projects,
        "browser_test_suites": [suite for suite in BROWSER_TEST_SUITES if suite in browser_test_suites],
        "managed_test_filter": managed_test_filter,
        "managed_db_test_filter": managed_db_test_filter,
        "managed_live_test_filter": managed_live_test_filter,
        "native_test_filter": native_test_filter_for_paths(paths, root) if "native-dev" in dev_suites else "",
        "native_db_test_filter": native_db_test_filter_for_paths(paths) if "native-db" in db_suites else "",
        "managed_db_test_projects": managed_db_test_projects,
        "managed_live_test_projects": managed_live_test_projects,
        "dev_suites": [suite for suite in DEV_SUITES if suite in dev_suites],
        "db_suites": [suite for suite in DB_SUITES if suite in db_suites],
        "live_suites": [suite for suite in LIVE_SUITES if suite in live_suites],
        "delivery_actions": [
            action for action in DELIVERY_ACTIONS if action in delivery_actions
        ],
        "publish_scope": publish_scope if publish_scope in ("web", "api", "api-web", "uci", "full") else "full",
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


def sql_comment_only_change(root: Path, base: str, head: str, path: str) -> bool:
    """Return true when an extension SQL change alters comments only."""
    if (not path.startswith("extension/")
            or not path.endswith((".sql", ".sql.in"))):
        return False
    result = subprocess.run(
        ["git", "diff", "--quiet", "--ignore-matching-lines=^[[:space:]]*--",
         f"{base}..{head}", "--", path],
        cwd=root,
        text=True,
        capture_output=True,
    )
    return result.returncode == 0


def force_full_plan(plan: dict) -> None:
    plan["full_qualification"] = True
    plan["components"] = list(ALL_COMPONENTS)
    plan["build_components"] = ["native", "managed", "web"]
    plan["managed_build_projects"] = ["all"]
    plan["managed_delivery_build_projects"] = ["all"]
    plan["managed_test_projects"] = ["all"]
    plan["browser_test_suites"] = list(BROWSER_TEST_SUITES)
    plan["managed_test_filter"] = ""
    plan["managed_db_test_filter"] = ""
    plan["managed_live_test_filter"] = ""
    plan["native_test_filter"] = ""
    plan["native_db_test_filter"] = ""
    plan["managed_db_test_projects"] = ["all"]
    plan["managed_live_test_projects"] = []
    plan["dev_suites"] = list(DEV_SUITES)
    plan["db_suites"] = list(DB_SUITES)
    plan["live_suites"] = []
    plan["delivery_actions"] = [
        action for action in DELIVERY_ACTIONS if action != "live"
    ]
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
            "managed_delivery_build_projects",
            "managed_test_projects",
            "browser_test_suites",
            "managed_db_test_projects",
            "managed_live_test_projects",
        ):
            stream.write(f"{name}={','.join(plan[name])}\n")
        stream.write(f"managed_test_filter={plan.get('managed_test_filter', '')}\n")
        stream.write(f"managed_db_test_filter={plan.get('managed_db_test_filter', '')}\n")
        stream.write(f"managed_live_test_filter={plan.get('managed_live_test_filter', '')}\n")
        stream.write(f"native_test_filter={plan.get('native_test_filter', '')}\n")
        stream.write(f"native_db_test_filter={plan.get('native_db_test_filter', '')}\n")
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
        stream.write(f"- Managed qualification build projects: {joined('managed_build_projects')}\n")
        stream.write(f"- Managed delivery build projects: {joined('managed_delivery_build_projects')}\n")
        stream.write(f"- Managed unit-test projects: {joined('managed_test_projects')}\n")
        stream.write(f"- Browser test suites: {joined('browser_test_suites')}\n")
        stream.write(f"- Managed test filter: {plan.get('managed_test_filter') or 'none'}\n")
        stream.write(f"- Managed DB test filter: {plan.get('managed_db_test_filter') or 'none'}\n")
        stream.write(f"- Managed live test filter: {plan.get('managed_live_test_filter') or 'none'}\n")
        stream.write(f"- Native test filter: {plan.get('native_test_filter') or 'none'}\n")
        stream.write(f"- Native DB test filter: {plan.get('native_db_test_filter') or 'none'}\n")
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

    comment_only_sql: list[str] = []
    effective = changed
    if not forced_full and not args.changed_file:
        comment_only_sql = [
            path for path in changed
            if sql_comment_only_change(root, args.base, args.head, path)
        ]
        effective = [path for path in changed if path not in comment_only_sql]

    plan = classify_paths(effective, root)
    plan["ignored_paths"] = sorted(set(plan["ignored_paths"] + comment_only_sql))
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
