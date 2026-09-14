#!/usr/bin/env python3
"""Fail closed when Actions stops describing one Laplace product lifecycle."""
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


def runs(job: dict) -> str:
    return "\n".join(
        step.get("run", "") for step in job.get("steps", []) if isinstance(step, dict)
    )


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


paths = sorted([*WF.glob("*.yml"), *WF.glob("*.yaml")])
workflows = {path.name: load(path) for path in paths}

for obsolete in (
    WF / "application-delivery.yml",
    ROOT / "scripts" / "application-delivery-source.py",
):
    if obsolete.exists():
        fail(f"obsolete delivery workaround remains: {obsolete.relative_to(ROOT)}")

for path in paths:
    source = path.read_text(encoding="utf-8")
    workflow = workflows[path.name]
    if "workflow_run:" in source:
        fail(f"{path.name}: post-run delivery is forbidden")
    if "pull_request_target:" in source:
        fail(f"{path.name}: privileged PR trigger is forbidden")
    if "continue-on-error: true" in source:
        fail(f"{path.name}: hidden red result")
    permissions = workflow.get("permissions")
    if not isinstance(permissions, dict) or permissions.get("contents") != "read":
        fail(f"{path.name}: top-level contents permission must be read-only")
    for use in uses(workflow):
        if not use.startswith("./") and not re.fullmatch(r"[^@\s]+@[0-9a-f]{40}", use):
            fail(f"{path.name}: external action is not commit pinned: {use}")
    for name, job in (workflow.get("jobs") or {}).items():
        if "runs-on" in job and "timeout-minutes" not in job:
            fail(f"{path.name}:{name}: no timeout")
        concurrency = job.get("concurrency")
        if isinstance(concurrency, dict) and concurrency.get("cancel-in-progress") == "true":
            fail(f"{path.name}:{name}: shared/runtime job may be cancelled")

main = workflows.get("laplace.yml", {})
main_jobs = main.get("jobs") or {}
if set(main_jobs) != {"product"}:
    fail(f"laplace.yml must expose exactly one product job, found {sorted(main_jobs)}")
main_concurrency = main.get("concurrency") or {}
if main_concurrency.get("group") != "laplace-substrate-lifecycle":
    fail("main product lifecycle does not own the shared substrate lifecycle lock")
if main_concurrency.get("cancel-in-progress") != "false":
    fail("main product lifecycle may be cancelled mid-activation")
main_inputs = ((main.get("on") or {}).get("workflow_dispatch") or {}).get("inputs") or {}
restore = main_inputs.get("restore_foundation") or {}
if restore.get("default") != "false":
    fail("main restore_foundation must be explicit opt-in (default false)")
if main_jobs:
    command = runs(main_jobs["product"])
    if 'bash scripts/product-ci.sh "$LAPLACE_STAGE"' not in command:
        fail("main product job bypasses scripts/product-ci.sh")
    if "bash scripts/product-ci.sh reconcile" not in command:
        fail("main fast path bypasses installed-product reconciliation")
    if "LAPLACE_FAST_ONLY" not in command:
        fail("main product job has no proportional source/tooling path")
    for forbidden in (
        "pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh deploy",
        "test-parallel.sh --engine", "test-parallel.sh --integration", "test-parallel.sh --app-live",
    ):
        if forbidden in command:
            fail(f"laplace.yml reimplements product lifecycle primitive: {forbidden}")

if not PRODUCT.exists():
    fail("scripts/product-ci.sh is missing")
else:
    product = PRODUCT.read_text(encoding="utf-8")
    required_order = [
        "run_policy", "run_deps", "run_build", "run_dev",
        "run_install_and_db", "run_publish", "run_integration", "run_live_if_expected",
    ]
    positions = [product.rfind(f"\n{name}\n") for name in required_order]
    if any(pos < 0 for pos in positions) or positions != sorted(positions):
        fail(f"product lifecycle order drifted: {list(zip(required_order, positions))}")
    for token in (
        "managed-publish.sh preflight",
        "pipeline.sh install",
        "migrate sync-extension tune-pg tune-laplace perfcache-guc api-env",
        "check-database-health.sh",
        "ensure-foundation.sh --check-only",
        "check-substrate-floor.sh",
        "LAPLACE_RESTORE_FOUNDATION",
        "foundation restore not requested — no ingest",
        "publish-applications.sh deploy",
        "publish-applications.sh recover",
        "local args=(--integration)",
        'bash scripts/test-parallel.sh "${args[@]}"',
        "test-parallel.sh --app-live",
    ):
        if token not in product:
            fail(f"product lifecycle missing {token}")
    reconcile = product.split("reconcile_installed_product() {", 1)[1].split("\n}", 1)[0]
    if "ensure-foundation.sh" in reconcile or "ensure_product_foundation" in reconcile:
        fail("fast installed-product reconciliation may not auto-seed")
    if "pipeline.sh" in reconcile:
        fail("fast installed-product reconciliation may not build/install/migrate")

proof = (ROOT / "scripts" / "pr-proof.sh").read_text(encoding="utf-8")
if proof.count("test-parallel.sh --policy") != 1:
    fail("full PR proof must execute policy profile exactly once")
if proof.count("test-parallel.sh --engine") != 1:
    fail("full PR proof must execute DEV/BAT profile exactly once")
for forbidden in (
    "pipeline.sh install", "pipeline.sh migrate", "sync-extension",
    "publish-applications.sh deploy", "systemctl ", "sudo ", "--fresh-db",
):
    if forbidden in proof:
        fail(f"PR proof mutates installed product via {forbidden}")

pr = workflows.get("pr-validation.yml", {})
prove = (pr.get("jobs") or {}).get("prove", {})
pr_source = (WF / "pr-validation.yml").read_text(encoding="utf-8")
pr_commands = runs(prove)
if "pull_request" not in triggers(pr):
    fail("PR proof workflow has no pull_request trigger")
if "head.repo.full_name == github.repository" not in str(prove.get("if", "")):
    fail("PR proof may execute fork code on self-hosted runner")
if "${{ secrets." in pr_source:
    fail("PR proof reads repository secrets")
for token in (
    "LAPLACE_PR_FULL_PROOF", "test-parallel.sh --policy", "scripts/pr-proof.sh",
    "git worktree add --detach", "LAPLACE_PR_WORKTREE", "git worktree remove --force",
):
    if token not in pr_commands:
        fail(f"PR proof lacks proportional/isolated contract: {token}")
if "git checkout --force" in pr_commands:
    fail("PR proof mutates persistent main checkout")
pr_concurrency = pr.get("concurrency") or {}
if pr_concurrency.get("group") != "laplace-pr-${{ github.event.pull_request.number }}":
    fail("PR proof concurrency is not scoped to one PR")
if pr_concurrency.get("cancel-in-progress") != "true":
    fail("superseded PR proof is not cancelled")
if pr_concurrency.get("group") == main_concurrency.get("group"):
    fail("PR proof shares the product lifecycle lock")

manual_db = workflows.get("db-ops.yml", {})
if (manual_db.get("concurrency") or {}).get("group") != "laplace-substrate-lifecycle":
    fail("manual DB lifecycle does not share product lifecycle ownership")
if triggers(manual_db) != {"workflow_dispatch"}:
    fail("manual DB lifecycle must be dispatch-only")
manual_inputs = ((manual_db.get("on") or {}).get("workflow_dispatch") or {}).get("inputs") or {}
manual_restore = manual_inputs.get("restore_foundation") or {}
if manual_restore.get("default") != "false":
    fail("DB recreate restore_foundation must be explicit opt-in (default false)")
manual_steps = ((manual_db.get("jobs") or {}).get("db") or {}).get("steps") or []
recreate_step = next((s for s in manual_steps if s.get("name") == "Recreate database structure and runtime"), None)
restore_step = next((s for s in manual_steps if s.get("name") == "Restore canonical foundation (explicit opt-in)"), None)
if not recreate_step or "ensure-foundation.sh" in recreate_step.get("run", ""):
    fail("DB recreate structural step must not seed")
if not restore_step or restore_step.get("if") != "inputs.operation == 'recreate' && inputs.restore_foundation":
    fail("DB foundation restore is not an explicit recreate opt-in")
if restore_step and "ensure-foundation.sh --force" not in restore_step.get("run", ""):
    fail("explicit DB foundation restore does not invoke canonical foundation owner")
manual_db_commands = "\n".join(runs(job) for job in (manual_db.get("jobs") or {}).values())
if "check-database-health.sh" not in manual_db_commands:
    fail("manual DB lifecycle lacks canonical structural health verification")

for name, workflow in workflows.items():
    if name.startswith("seed-"):
        event = triggers(workflow)
        if event & {"push", "pull_request"}:
            fail(f"{name}: seed mutation is source-triggered")
        if not event & {"workflow_dispatch", "workflow_call"}:
            fail(f"{name}: seed mutation lacks explicit invocation")

push = {name for name, workflow in workflows.items() if "push" in triggers(workflow)}
if push != {"laplace.yml", "repo-hygiene.yml"}:
    fail(f"automatic push workflows drifted: {sorted(push)}")

runner = (ROOT / "scripts" / "bootstrap-laplace-runner.sh").read_text(encoding="utf-8")
for token in (
    'RUNNER_SERVICE="actions.runner.SaltyPatron-Laplace.hart-server.service"',
    "--name hart-server",
    "--work /build/laplace/work/legacy-runner",
):
    if token not in runner:
        fail(f"runner contract missing {token}")

registry_tool = ROOT / "scripts" / "test-profile-registry.py"
registry_data = ROOT / "scripts" / "test-profiles.json"
if not registry_tool.exists() or not registry_data.exists():
    fail("executable test-profile registry is missing")
test_runner = (ROOT / "scripts" / "test-parallel.sh").read_text(encoding="utf-8")
if "test-profile-registry.py run --profile" not in test_runner:
    fail("test entry point bypasses registry executor")
policy_alias = (ROOT / "scripts" / "ci-policy.sh").read_text(encoding="utf-8")
if "test-profile-registry.py run --profile policy" not in policy_alias:
    fail("ci-policy.sh is not a thin alias to the policy profile")

if failures:
    print("ACTIONS_AUDIT_FAILED", file=sys.stderr)
    for failure in failures:
        print(f"  - {failure}", file=sys.stderr)
    raise SystemExit(1)
print(
    f"ACTIONS_AUDIT_OK workflows={len(workflows)} product=single-lifecycle "
    "foundation=explicit-opt-in pr_proof=proportional-isolated db_ops=shared-owner"
)
