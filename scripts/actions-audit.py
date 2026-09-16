#!/usr/bin/env python3
"""Structural audit for the GitHub Actions surface."""
from __future__ import annotations

from pathlib import Path
import re
import sys
import yaml

ROOT = Path(__file__).resolve().parents[1]
WF = ROOT / ".github" / "workflows"
PRODUCT = ROOT / "scripts" / "product-ci.sh"
failures: list[str] = []


def fail(message: str) -> None:
    failures.append(message)


def load(path: Path):
    return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)


def triggers(workflow: dict) -> set[str]:
    value = workflow.get("on", {})
    return {value} if isinstance(value, str) else set(value or {})


def uses(value):
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "uses" and isinstance(child, str):
                yield child
            yield from uses(child)
    elif isinstance(value, list):
        for child in value:
            yield from uses(child)


def runs(job: dict) -> str:
    return "\n".join(
        step.get("run", "") for step in job.get("steps", []) if isinstance(step, dict)
    )


paths = sorted([*WF.glob("*.yml"), *WF.glob("*.yaml")])
workflows = {path.name: load(path) for path in paths}

# Universal inexpensive invariants.
for path in paths:
    source = path.read_text(encoding="utf-8")
    workflow = workflows[path.name]
    if "workflow_run:" in source:
        fail(f"{path.name}: post-run workflow chaining is forbidden")
    if "pull_request_target:" in source:
        fail(f"{path.name}: privileged PR trigger is forbidden")
    permissions = workflow.get("permissions")
    if not isinstance(permissions, dict) or permissions.get("contents") != "read":
        fail(f"{path.name}: top-level contents permission must be read-only")
    for use in uses(workflow):
        if not use.startswith("./") and not re.fullmatch(r"[^@\s]+@[0-9a-f]{40}", use):
            fail(f"{path.name}: external action is not commit pinned: {use}")
    for name, job in (workflow.get("jobs") or {}).items():
        if "runs-on" in job and "timeout-minutes" not in job:
            fail(f"{path.name}:{name}: no timeout")

# Ingestion is explicit data work, never a source-triggered side effect.
for name, workflow in workflows.items():
    if name.startswith("seed-"):
        event = triggers(workflow)
        if event & {"push", "pull_request"}:
            fail(f"{name}: seed mutation is source-triggered")
        if not event & {"workflow_dispatch", "workflow_call"}:
            fail(f"{name}: seed mutation lacks explicit invocation")

# Main workflow: normal pushes only build/test; explicit operator dispatch owns
# activation. The two paths are deliberately different jobs so skipped deploy,
# ingest, diagnostics and evidence-upload steps do not clutter every commit.
main = workflows.get("laplace.yml", {})
main_jobs = main.get("jobs") or {}
if set(main_jobs) != {"development", "operator"}:
    fail(f"laplace.yml: expected development/operator jobs, found {sorted(main_jobs)}")
else:
    development = main_jobs["development"]
    operator = main_jobs["operator"]
    if development.get("if") != "github.event_name == 'push'":
        fail("laplace.yml: development job must be push-only")
    if operator.get("if") != "github.event_name == 'workflow_dispatch'":
        fail("laplace.yml: operator job must be dispatch-only")
    dev_concurrency = development.get("concurrency") or {}
    if dev_concurrency.get("cancel-in-progress") != "true":
        fail("laplace.yml: superseded development pushes must cancel")
    operator_concurrency = operator.get("concurrency") or {}
    if operator_concurrency.get("group") != "laplace-substrate-lifecycle" or operator_concurrency.get("cancel-in-progress") != "false":
        fail("laplace.yml: operator activation must retain the substrate lifecycle owner")

    dev = runs(development)
    for required in (
        "bash scripts/product-ci.sh check",
        "bash scripts/ci-deps.sh --check-only",
        "bash scripts/pipeline.sh build",
        "--profile dev-native --suite native-dev",
        "--profile dev-managed --suite managed-dev",
        "--profile dev-managed --suite uci-dev",
        "--profile dev-managed --suite browser-dev",
    ):
        if required not in dev:
            fail(f"laplace.yml: development path missing {required}")
    for forbidden in (
        "pipeline.sh install", "pipeline.sh migrate", "ensure-foundation", "ingest-source",
        "publish-applications.sh", "upload-artifact@", "ci-session.py", "quiesce-managed-database.py",
        "install-stockfish.py", "capture-native-regression.py", "collect-recursive-proof-evidence.py",
    ):
        if forbidden in dev:
            fail(f"laplace.yml: ordinary push performs non-development work: {forbidden}")

    operator_commands = runs(operator)
    for required in ("ci-session.py start", "ci-session.py run", "ci-session.py stop", "product-ci.sh \"$LAPLACE_STAGE\" --list-phases"):
        if required not in operator_commands:
            fail(f"laplace.yml: operator lifecycle missing {required}")
    if any(str(use).startswith("actions/upload-artifact@") for use in uses(main)):
        fail("laplace.yml: product lifecycle uploads artifacts instead of using normal job logs")

inputs = ((main.get("on") or {}).get("workflow_dispatch") or {}).get("inputs") or {}
for removed in ("fresh_db", "restore_foundation"):
    if removed in inputs:
        fail(f"laplace.yml: {removed} belongs to an independent database/seed operation")

# Product orchestration must not silently regain ingestion or destructive DB work.
product = PRODUCT.read_text(encoding="utf-8") if PRODUCT.exists() else ""
for forbidden in (
    "ensure-foundation.sh", "verify-operational-seed.py --ingest", "lexical-foundation",
    "operational-seed", "foundation) ", "--fresh-db",
):
    if forbidden in product:
        fail(f"product-ci.sh: delivery contains data/destructive work: {forbidden}")
for required in (
    '"${GITHUB_EVENT_NAME:-}" == "push" && "$stage" == "all"',
    "bash scripts/ci-deps.sh --check-only",
    "bash scripts/pipeline.sh install",
    "database-maintenance",
):
    if required not in product:
        fail(f"product-ci.sh: lifecycle boundary missing {required}")

# Database recreation must produce a usable product from the selected revision.
manual_db = workflows.get("db-ops.yml", {})
if triggers(manual_db) != {"workflow_dispatch"}:
    fail("db-ops.yml: database lifecycle must be dispatch-only")
if (manual_db.get("concurrency") or {}).get("group") != "laplace-substrate-lifecycle":
    fail("db-ops.yml: database lifecycle must share product mutation ownership")
steps = ((manual_db.get("jobs") or {}).get("db") or {}).get("steps") or []
recreate = next((s for s in steps if s.get("name") == "Recreate database structure and runtime"), None)
if not recreate:
    fail("db-ops.yml: missing structural recreate step")
else:
    command = recreate.get("run", "")
    for required in (
        "--fresh-db migrate sync-extension tune-pg tune-laplace perfcache-guc api-env",
        "check-database-health.sh", "verify-application-release.py",
    ):
        if required not in command:
            fail(f"db-ops.yml: recreate lost required operation {required}")
all_db_commands = runs((manual_db.get("jobs") or {}).get("db") or {})
for required in (
    "pipeline.sh build install", "ensure-foundation.sh --required-lexical",
    "ensure-foundation.sh", "check-substrate-floor.sh",
):
    if required not in all_db_commands:
        fail(f"db-ops.yml: recreation cannot produce a usable product without {required}")
if not ((manual_db.get("on") or {}).get("workflow_dispatch") or {}).get("inputs", {}).get("restore_foundation"):
    fail("db-ops.yml: recreation lost complete-foundation selection")

# Branch consolidation is exceptional maintenance, not per-commit work.
hygiene = workflows.get("repo-hygiene.yml", {})
hygiene_job = (hygiene.get("jobs") or {}).get("integrated-branches") or {}
if hygiene_job.get("if") != "github.event_name == 'workflow_dispatch'":
    fail("repo-hygiene.yml: branch consolidation must be manual")
if any(str(use).startswith("actions/upload-artifact@") for use in uses(hygiene_job)):
    fail("repo-hygiene.yml: branch consolidation must not upload recovery bundles")

if failures:
    print("ACTIONS_AUDIT_FAILED", file=sys.stderr)
    for failure in failures:
        print(f"  - {failure}", file=sys.stderr)
    raise SystemExit(1)

print(
    f"ACTIONS_AUDIT_OK workflows={len(workflows)} "
    "push=build-test operator=explicit db=usable-recreate seeds=explicit artifacts=none"
)
