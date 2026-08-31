#!/usr/bin/env python3
"""Fail closed when the executable Actions graph violates delivery/test authority."""
from pathlib import Path
import re
import sys
import yaml

ROOT = Path(__file__).resolve().parents[1]
WF = ROOT / ".github" / "workflows"
failures = []

def fail(message): failures.append(message)
def load(path): return yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
def needs(job):
    value = job.get("needs", [])
    return {value} if isinstance(value, str) else set(value or [])
def runs(job):
    return "\n".join(step.get("run", "") for step in job.get("steps", []) if isinstance(step, dict))
def triggers(workflow):
    value = workflow.get("on", {})
    return {value} if isinstance(value, str) else set(value or {})

def uses(value):
    if isinstance(value, dict):
        for key, child in value.items():
            if key == "uses" and isinstance(child, str): yield child
            yield from uses(child)
    elif isinstance(value, list):
        for child in value: yield from uses(child)

paths = sorted([*WF.glob("*.yml"), *WF.glob("*.yaml")])
workflows = {path.name: load(path) for path in paths}
for obsolete in (WF / "application-delivery.yml", ROOT / "scripts" / "application-delivery-source.py"):
    if obsolete.exists(): fail(f"obsolete delivery workaround remains: {obsolete.relative_to(ROOT)}")

for path in paths:
    source = path.read_text(encoding="utf-8")
    workflow = workflows[path.name]
    if "workflow_run:" in source: fail(f"{path.name}: post-run delivery is forbidden")
    if "pull_request_target:" in source: fail(f"{path.name}: privileged PR trigger is forbidden")
    if "continue-on-error: true" in source: fail(f"{path.name}: hidden red result")
    permissions = workflow.get("permissions")
    if not isinstance(permissions, dict) or permissions.get("contents") != "read":
        fail(f"{path.name}: top-level contents permission must be read-only")
    for use in uses(workflow):
        if not use.startswith("./") and not re.fullmatch(r"[^@\s]+@[0-9a-f]{40}", use):
            fail(f"{path.name}: external action is not commit pinned: {use}")
    for name, job in (workflow.get("jobs") or {}).items():
        if "runs-on" in job and "timeout-minutes" not in job: fail(f"{path.name}:{name}: no timeout")
        concurrency = job.get("concurrency")
        if isinstance(concurrency, dict) and concurrency.get("cancel-in-progress") == "true":
            fail(f"{path.name}:{name}: shared/runtime job may be cancelled")

main = workflows.get("laplace.yml", {})
jobs = main.get("jobs") or {}
required = {"policy","deps","build","unit-test","deploy","db-ops","publish","integration-test","smoke","eval","restore-api"}
for name in sorted(required - set(jobs)): fail(f"laplace.yml: missing job {name}")
if jobs:
    publish = jobs["publish"]; integration = jobs["integration-test"]; smoke = jobs["smoke"]; restore = jobs["restore-api"]
    if needs(publish) != {"db-ops"}: fail("publish must follow db-ops directly")
    if "integration-test" in str(publish.get("if", "")): fail("integration still controls publication")
    if "publish-applications.sh deploy" not in runs(publish): fail("publish bypasses application transaction")
    if "check-database-health.sh" not in runs(jobs["db-ops"]): fail("db lifecycle lacks structural health gate")
    if needs(integration) != {"db-ops", "publish"}: fail("integration must execute after publication")
    if "test-parallel.sh --integration" not in runs(integration): fail("integration bypasses DB QA profile")
    smoke_if = str(smoke.get("if", ""))
    if needs(smoke) != {"publish", "integration-test"}: fail("product smoke must execute after post-delivery DB QA")
    if "needs.integration-test.result == 'success'" in smoke_if: fail("DB QA still suppresses product evidence")
    if "needs.publish.outputs.has_data" in smoke_if: fail("empty/thin substrate silently skips product-floor evidence")
    if "check-substrate-floor.sh" not in runs(smoke): fail("product smoke lacks substrate-floor gate")
    if needs(restore) != {"deploy", "publish"}: fail("recovery owns more than failed application publication")
    if "needs.publish.result != 'success'" not in str(restore.get("if", "")): fail("recovery is not limited to failed publication")
    if needs(jobs["deps"]) != {"policy"} or needs(jobs["build"]) != {"deps"}: fail("single-runner policy/deps/build order drifted")
    if runs(jobs["unit-test"]).count("test-parallel.sh --engine") != 1: fail("DEV/BAT is not one canonical profile invocation")

proof = (ROOT / "scripts" / "pr-proof.sh").read_text(encoding="utf-8")
if proof.count("bash scripts/ci-policy.sh") != 1: fail("PR proof must execute policy exactly once")
for forbidden in ("publish-applications.sh check", "check-application-runtime.py", "pipeline.sh install", "pipeline.sh migrate", "sync-extension", "publish-applications.sh deploy", "systemctl ", "sudo ", "--fresh-db"):
    if forbidden in proof: fail(f"PR proof assumes/mutates installed runtime via {forbidden}")

pr = workflows.get("pr-validation.yml", {})
prove = (pr.get("jobs") or {}).get("prove", {})
pr_source = (WF / "pr-validation.yml").read_text(encoding="utf-8") if (WF / "pr-validation.yml").exists() else ""
pr_commands = runs(prove)
if "pull_request" not in triggers(pr): fail("PR proof workflow has no pull_request trigger")
if "head.repo.full_name == github.repository" not in str(prove.get("if", "")): fail("PR proof may execute fork code on self-hosted runner")
if "${{ secrets." in pr_source: fail("PR proof workflow reads repository secrets")
if "scripts/pr-proof.sh" not in pr_commands: fail("PR workflow bypasses canonical PR proof")
if "git checkout --force" in pr_commands: fail("PR proof mutates the persistent main workspace checkout")
for token in ("git worktree add --detach", "LAPLACE_PR_WORKTREE", "git worktree remove --force"):
    if token not in pr_commands: fail(f"PR proof lacks isolated-worktree contract: {token}")
pr_concurrency = pr.get("concurrency") or {}
if pr_concurrency.get("group") != "laplace-pr-${{ github.event.pull_request.number }}" or pr_concurrency.get("cancel-in-progress") != "true":
    fail("PR proof must supersede only the same PR and must not share main's pending-run queue")
if (main.get("concurrency") or {}).get("group") == pr_concurrency.get("group"):
    fail("PR proof and main share a concurrency group; PR pushes can cancel pending delivery")

for name, workflow in workflows.items():
    if name == "db-ops.yml" or name.startswith("seed-"):
        event = triggers(workflow)
        if event & {"push", "pull_request"}: fail(f"{name}: data/lifecycle mutation is source-triggered")
        if not event & {"workflow_dispatch", "workflow_call"}: fail(f"{name}: data/lifecycle mutation lacks explicit dispatch")

push = {name for name, workflow in workflows.items() if "push" in triggers(workflow)}
if push != {"laplace.yml", "repo-hygiene.yml"}: fail(f"automatic push workflows drifted: {sorted(push)}")
manual_db = workflows.get("db-ops.yml", {})
if "check-database-health.sh" not in "\n".join(runs(job) for job in (manual_db.get("jobs") or {}).values()):
    fail("db-ops workflow lacks canonical structural health verification")

all_workflow_source = "\n".join(path.read_text(encoding="utf-8") for path in paths)
if "Tier!=db&Tier!=perf" in all_workflow_source: fail("workflow filter still accidentally includes Tier=live")

runner = (ROOT / "scripts" / "bootstrap-laplace-runner.sh").read_text(encoding="utf-8")
for token in ('RUNNER_SERVICE="actions.runner.SaltyPatron-Laplace.hart-server.service"', "--name hart-server", "--work _work"):
    if token not in runner: fail(f"runner contract missing {token}")

test_runner = (ROOT / "scripts" / "test-parallel.sh").read_text(encoding="utf-8")
for token in ("DOTNET_DEV_FILTER='Tier!=db&Tier!=live&Tier!=perf'", "DOTNET_DB_FILTER='Tier=db'", "DOTNET_LIVE_FILTER='Tier=live'"):
    if token not in test_runner: fail(f"test profile contract missing {token}")

if failures:
    print("ACTIONS_AUDIT_FAILED", file=sys.stderr)
    for failure in failures: print(f"  - {failure}", file=sys.stderr)
    raise SystemExit(1)
print(f"ACTIONS_AUDIT_OK workflows={len(workflows)} delivery=before_qa pr_proof=isolated-worktree")
