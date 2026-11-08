#!/usr/bin/env python3
"""Constants, native/managed test filters, git diff helpers, and output writers for ci-impact-plan.py."""
from __future__ import annotations

from pathlib import Path
import re
import subprocess

DEV_SUITES = ("native-dev", "managed-dev", "uci-dev", "browser-dev")
BROWSER_TEST_SUITES = ("typecheck", "read-resource", "workspace-ui", "data-ui", "chess-ui")
DB_SUITES = ("db-health", "native-db", "managed-db")
STANDARD_LIVE_SUITES = ("live-floor", "live-api", "managed-live", "generation-eval")
CHESS_PROVIDER_LIVE_SUITE = "chess-provider-live"
LIVE_SUITES = (*STANDARD_LIVE_SUITES, CHESS_PROVIDER_LIVE_SUITE)
FULL_DELIVERY_ACTIONS = ("install", "database", "publish", "live")
DELIVERY_ACTIONS = ("install", "extension-sql", "ingest-runtime", "database", "publish", "live")
BASE_LIVE_SUITES = ("live-floor", "live-api")
ALL_DEV_COMPONENTS = ("native", "managed", "uci", "web")
ALL_COMPONENTS = ("native", "managed", "uci", "web", "database", "deployment")
API_PUBLISH_PROJECT = "app/Laplace.Endpoints.OpenAICompat/Laplace.Endpoints.OpenAICompat.csproj"
UCI_PUBLISH_PROJECT = "app/Laplace.Chess.Uci/Laplace.Chess.Uci.csproj"
MCP_PUBLISH_PROJECT = "app/Laplace.Endpoints.Mcp/Laplace.Endpoints.Mcp.csproj"
LICHESS_PUBLISH_PROJECT = "app/Laplace.Endpoints.Lichess/Laplace.Endpoints.Lichess.csproj"
MIGRATIONS_PUBLISH_PROJECT = "app/Laplace.Migrations/Laplace.Migrations.csproj"
CLI_INGEST_PROJECT = "app/Laplace.Cli/Laplace.Cli.csproj"
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
        action for action in FULL_DELIVERY_ACTIONS if action != "live"
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
