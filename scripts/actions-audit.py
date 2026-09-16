#!/usr/bin/env python3
"""Small structural audit for the GitHub Actions surface.

This intentionally checks architecture, not exact step-by-step choreography. CI
must stay understandable: normal pushes validate development work, database
lifecycle operations stay structural, ingestion stays explicitly invoked, and
operator/measurement workflows do not silently become main-push work.
"""
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

# Universal safety/maintenance invariants. These are cheap and stable.
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

# Ingestion is data work. It must never be a side effect of source pushes/PRs.
for name, workflow in workflows.items():
    if name.startswith("seed-"):
        event = triggers(workflow)
        if event & {"push", "pull_request"}:
            fail(f"{name}: seed mutation is source-triggered")
        if not event & {"workflow_dispatch", "workflow_call"}:
            fail(f"{name}: seed mutation lacks explicit invocation")

# Main pushes are development validation. Installation/database mutation,
# ingestion, publication and benchmarks remain explicit operator work.
main = workflows.get("laplace.yml", {})
if (main.get("concurrency") or {}).get("group") != "laplace-substrate-lifecycle":
    fail("laplace.yml: shared substrate lifecycle lock changed")
product_jobs = main.get("jobs") or {}
if set(product_jobs) != {"product"}:
    fail(f"laplace.yml: expected one product job, found {sorted(product_jobs)}")
else:
    command = runs(product_jobs["product"])
    if "bash scripts/product-ci.sh check" not in command:
        fail("laplace.yml: source-only pushes must stay on the lightweight check path")
    if "LAPLACE_FAST_ONLY" not in command:
        fail("laplace.yml: source-only classifier is missing")

product = PRODUCT.read_text(encoding="utf-8") if PRODUCT.exists() else ""
for token in (
    '"${GITHUB_EVENT_NAME:-}" == "push" && "$stage" == "all"',
    'stage="test"',
    '"${GITHUB_EVENT_NAME:-}" == "push" && "$stage" == "check"',
    "source-only syntax check passed",
):
    if token not in product:
        fail(f"product-ci.sh: normal-push boundary missing {token}")

# DB lifecycle is structural only. Corpus/foundation admission has dedicated
# seed workflows and must not make database creation succeed or fail.
manual_db = workflows.get("db-ops.yml", {})
if triggers(manual_db) != {"workflow_dispatch"}:
    fail("db-ops.yml: database lifecycle must be dispatch-only")
if (manual_db.get("concurrency") or {}).get("group") != "laplace-substrate-lifecycle":
    fail("db-ops.yml: database lifecycle must share product mutation ownership")
steps = ((manual_db.get("jobs") or {}).get("db") or {}).get("steps") or []
recreate = next((s for s in steps if s.get("name") == "Recreate database structure"), None)
if not recreate:
    fail("db-ops.yml: missing structural recreate step")
else:
    command = recreate.get("run", "")
    for required in ("--fresh-db migrate sync-extension", "check-database-health.sh"):
        if required not in command:
            fail(f"db-ops.yml: recreate lost structural operation {required}")
    for forbidden in (
        "ensure-foundation", "ingest", "pipeline.sh build", "pipeline.sh install",
        "tune-pg", "perfcache-guc", "api-env", "verify-application-release.py",
    ):
        if forbidden in command:
            fail(f"db-ops.yml: recreate contains non-structural work: {forbidden}")
for step in steps:
    if step.get("if") == "inputs.operation == 'recreate'":
        text = step.get("run", "")
        if "ensure-foundation" in text or "ingest" in text:
            fail("db-ops.yml: recreate still depends on ingestion")
if ((manual_db.get("on") or {}).get("workflow_dispatch") or {}).get("inputs", {}).get("restore_foundation"):
    fail("db-ops.yml: foundation restore belongs to seed-foundation.yml, not database lifecycle")

# Branch consolidation is exceptional maintenance, not an every-commit job and
# not a reason to upload another recovery bundle on every invocation.
hygiene = workflows.get("repo-hygiene.yml", {})
hygiene_job = (hygiene.get("jobs") or {}).get("integrated-branches") or {}
if hygiene_job.get("if") != "github.event_name == 'workflow_dispatch'":
    fail("repo-hygiene.yml: branch consolidation must be manual")
if "upload-artifact@" in runs(hygiene_job) or any(
    str(use).startswith("actions/upload-artifact@") for use in uses(hygiene_job)
):
    fail("repo-hygiene.yml: branch consolidation should preserve history in Git, not upload bundles")

if failures:
    print("ACTIONS_AUDIT_FAILED", file=sys.stderr)
    for failure in failures:
        print(f"  - {failure}", file=sys.stderr)
    raise SystemExit(1)

print(
    f"ACTIONS_AUDIT_OK workflows={len(workflows)} "
    "push=development db=structural seeds=explicit consolidation=manual"
)
