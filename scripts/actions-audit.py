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


def enabled(value) -> bool:
    """BaseLoader retains scalar strings; expressions must also fail closed."""
    return value is not None and str(value).lower() != "false"


def unique_step(steps: list, key: str, value: str, context: str):
    matches = [(i, step) for i, step in enumerate(steps) if step.get(key) == value]
    if len(matches) != 1:
        fail(f"{context}: requires exactly one {key}={value}, found {len(matches)}")
        return None
    return matches[0]


def result_authority(name: str, workflow: dict) -> None:
    """Optional diagnostics never acquire proof authority; readiness is deferred."""
    if name == "benchmark-evidence.yml":
        if set(workflow.get("jobs") or {}) != {"benchmark"}:
            fail("benchmark-evidence.yml: the reusable workflow may expose only its measurement job")
        for job in (workflow.get("jobs") or {}).values():
            for token in ("product-ci.sh", "pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh", "systemctl ", "sudo "):
                if token in runs(job):
                    fail(f"benchmark-evidence.yml: measurement may not acquire product mutation authority: {token}")
    for job_name, job in (workflow.get("jobs") or {}).items():
        context = f"{name}:{job_name}"
        if enabled(job.get("continue-on-error")):
            fail(f"{context}: hidden red job result")
        steps = job.get("steps") or []
        allowed = set()
        if (name, job_name) == ("benchmark-evidence.yml", "benchmark"):
            readiness = unique_step(steps, "id", "chess_readiness", context)
            upload = unique_step(steps, "name", "Upload complete benchmark evidence", context)
            gate = unique_step(steps, "name", "Require installed chess tools and verified release checks", context)
            if readiness and upload and gate:
                read_index, read_step = readiness
                upload_index, upload_step = upload
                gate_index, gate_step = gate
                allowed.add(read_index)
                if not read_index < upload_index < gate_index == len(steps) - 1:
                    fail(f"{context}: deferred readiness must be enforced after evidence upload as the final step")
                if read_step.get("if") != "${{ !cancelled() && inputs.suite == 'chess' && steps.chess_paths.outcome != 'skipped' }}":
                    fail(f"{context}: readiness must inspect every attempted chess path resolution")
                if read_step.get("timeout-minutes") != "5":
                    fail(f"{context}: readiness must retain its bounded timeout")
                read_command = read_step.get("run", "")
                for token in ("scripts/check-chess-dependencies.py", "--check-latest", "chess-dependency-readiness.json", "chess-readiness-execution.json", "raise SystemExit(1 if failed else 0)"):
                    if token not in read_command:
                        fail(f"{context}: readiness no longer exposes required checks: {token}")
                if upload_step.get("if") != "always()" or not upload_step.get("uses", "").startswith("actions/upload-artifact@"):
                    fail(f"{context}: benchmark evidence must upload on failure")
                if (upload_step.get("with") or {}).get("if-no-files-found") != "error":
                    fail(f"{context}: missing benchmark evidence must fail")
                expected_gate = "${{ !cancelled() && inputs.suite == 'chess' && steps.chess_readiness.outcome == 'failure' }}"
                if gate_step.get("if") != expected_gate:
                    fail(f"{context}: deferred readiness gate must propagate the raw failed outcome")
                gate_lines = gate_step.get("run", "").strip().splitlines()
                if len(gate_lines) != 2 or not gate_lines[0].startswith("echo '::error::") or gate_lines[1] != "exit 1":
                    fail(f"{context}: deferred readiness gate must fail unconditionally when selected")
        elif (name, job_name) == ("pr-validation.yml", "prove"):
            baseline = unique_step(steps, "id", "baseline_diagnostic", context)
            upload = unique_step(steps, "name", "Upload retained baseline before full proof", context)
            proof = unique_step(steps, "id", "proof", context)
            if baseline and upload and proof:
                baseline_index, baseline_step = baseline
                upload_index, upload_step = upload
                proof_index, proof_step = proof
                allowed.update((baseline_index, upload_index))
                if not baseline_index < upload_index < proof_index:
                    fail(f"{context}: optional baseline must precede the authoritative proof")
                if baseline_step.get("if") != "env.LAPLACE_PR_FULL_PROOF == '1'":
                    fail(f"{context}: baseline must be limited to full proof scope")
                expected_upload = "always() && steps.baseline_diagnostic.outcome != 'skipped' && env.LAPLACE_PR_FULL_PROOF == '1'"
                if upload_step.get("if") != expected_upload or not upload_step.get("uses", "").startswith("actions/upload-artifact@") or "run" in upload_step:
                    fail(f"{context}: optional baseline upload may only retain the attempted diagnostic")
                baseline_command = baseline_step.get("run", "")
                for token in ("--proof-outcome baseline-before-proof", "scripts/collect-recursive-proof-evidence.py", "scripts/inspect-recursive-proof-counterexamples.py", "flock --exclusive --close /build/laplace/work/host-resource.lock"):
                    if token not in baseline_command:
                        fail(f"{context}: baseline lacks bounded read-only diagnostic contract: {token}")
                if "if" in proof_step or "baseline_diagnostic" in str(proof_step):
                    fail(f"{context}: actual PR proof must remain independent of optional baseline success")
                for token in ("scripts/pr-proof.sh", "test-parallel.sh --policy", "set -euo pipefail"):
                    if token not in proof_step.get("run", ""):
                        fail(f"{context}: actual PR proof lost authority: {token}")
        for index in allowed:
            step = steps[index]
            if step.get("continue-on-error") != "true":
                fail(f"{context}: diagnostic failure deferral must be explicit")
            for token in ("pr-proof.sh", "test-parallel.sh", "product-ci.sh", "pipeline.sh", "publish-applications.sh", "systemctl ", "sudo ", "prove-live-recursive-substrate.py"):
                if token in step.get("run", ""):
                    fail(f"{context}: optional diagnostic contains an authoritative or mutating operation: {token}")
        for index, step in enumerate(steps):
            if enabled(step.get("continue-on-error")) and index not in allowed:
                fail(f"{context}: hidden red step result: {step.get('id', step.get('name', index))}")


def product_topology(main: dict) -> None:
    jobs = main.get("jobs") or {}
    if set(jobs) != {"product", "chess_environment"}:
        fail(f"laplace.yml must expose one product job and its chess measurement, found {sorted(jobs)}")
    calibration = jobs.get("chess_environment") or {}
    allowed_keys = {"name", "needs", "if", "uses", "with"}
    if set(calibration) - allowed_keys:
        fail("laplace.yml: chess measurement may only call the bounded reusable workflow")
    if calibration.get("needs") != "product":
        fail("laplace.yml: chess measurement must depend on the single product authority")
    if calibration.get("uses") != "./.github/workflows/benchmark-evidence.yml":
        fail("laplace.yml: chess measurement must use the canonical benchmark workflow")
    if calibration.get("if") != "needs.product.result == 'success' && needs.product.outputs.chess_benchmark_ready == 'true'":
        fail("laplace.yml: chess measurement requires successful product activation")
    options = calibration.get("with") or {}
    if options.get("suite") != "chess" or options.get("target_ref") != "${{ needs.product.outputs.activated_ref }}":
        fail("laplace.yml: post-activation measurement must bind chess to the activated source")
    if set(options) - {"suite", "target_ref", "repeats", "reserve_logical_cpus", "allow_saturation"}:
        fail("laplace.yml: post-activation measurement accepts only bounded chess inputs")
    product_job = jobs.get("product") or {}
    expected_outputs = {
        "chess_benchmark_ready": "${{ steps.chess_benchmark_gate.outputs.ready }}",
        "activated_ref": "${{ steps.chess_benchmark_gate.outputs.source_sha }}",
    }
    if product_job.get("outputs") != expected_outputs:
        fail("laplace.yml: measurement outputs must come from the successful activation gate")
    steps = product_job.get("steps") or []
    lifecycle = unique_step(steps, "name", "Run full product lifecycle", "laplace.yml:product")
    gate = unique_step(steps, "id", "chess_benchmark_gate", "laplace.yml:product")
    if lifecycle and gate:
        gate_if = "env.LAPLACE_FAST_ONLY != '1' && (env.LAPLACE_STAGE == 'all' || env.LAPLACE_STAGE == 'deploy' || env.LAPLACE_STAGE == 'applications')"
        if gate[0] <= lifecycle[0] or gate[1].get("if") != gate_if:
            fail("laplace.yml: successful activation must precede measurement authorization")
        command = gate[1].get("run", "")
        if "ready=true" not in command or '"$(git rev-parse HEAD)"' not in command or "source_sha=%s" not in command:
            fail("laplace.yml: measurement gate must expose the exact activated checkout")


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
    result_authority(path.name, workflow)
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
product_topology(main)
main_concurrency = main.get("concurrency") or {}
if main_concurrency.get("group") != "laplace-substrate-lifecycle":
    fail("main product lifecycle does not own the shared substrate lifecycle lock")
if main_concurrency.get("cancel-in-progress") != "false":
    fail("main product lifecycle may be cancelled mid-activation")
main_inputs = ((main.get("on") or {}).get("workflow_dispatch") or {}).get("inputs") or {}
restore = main_inputs.get("restore_foundation") or {}
if restore.get("default") != "false":
    fail("main restore_foundation must be explicit opt-in (default false)")
if "product" in main_jobs:
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
        "run_install_and_db", "run_publish", "run_repair_installed_corpus", "run_integration", "run_live_if_expected",
    ]
    positions = [product.rfind(f"\n{name}\n") for name in required_order]
    if any(pos < 0 for pos in positions) or positions != sorted(positions):
        fail(f"product lifecycle order drifted: {list(zip(required_order, positions))}")
    for token in (
        "managed-publish.sh preflight",
        "pipeline.sh install",
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
    maintenance_path = ROOT / "scripts" / "maintain-installed-database.sh"
    repair_path = ROOT / "scripts" / "repair-legacy-content-lifecycle.sh"
    delegation = r'python3 scripts/quiesce-managed-database\.py --database "\$\{PGDATABASE:-laplace\}" --\s+\\\s+bash scripts/maintain-installed-database\.sh'
    if len(re.findall(delegation, product)) != 1 or product.count("maintain-installed-database.sh") != 1:
        fail("installed database maintenance must have one managed-quiescence owner")
    post_publish_repair = r'LAPLACE_REPAIR_PUBLISHED_SOURCE="\$\(git rev-parse HEAD\)" \\\s+python3 scripts/quiesce-managed-database\.py --database "\$\{PGDATABASE:-laplace\}" --\s+\\\s+bash scripts/repair-legacy-content-lifecycle\.sh "\$\{PGDATABASE:-laplace\}"(?:\n|$)'
    resume_repair = r'python3 scripts/quiesce-managed-database\.py --database "\$\{PGDATABASE:-laplace\}" --resume-if-needed --\s+\\\s+bash scripts/repair-legacy-content-lifecycle\.sh "\$\{PGDATABASE:-laplace\}"(?:\n|$)'
    if len(re.findall(post_publish_repair, product)) != 1 or product.count("repair-legacy-content-lifecycle.sh") != 2:
        fail("post-publication corpus repair must retain quiescence, source evidence, and failure propagation")
    resume_call = "  reconcile|deploy|integrate|all|applications) resume_held_repair_if_needed ;;"
    if len(re.findall(resume_repair, product)) != 1 or product.splitlines().count(resume_call) != 1 \
            or not 0 <= product.find("\nrun_policy\n") < product.find(resume_call) < product.find("\nrun_deps\n"):
        fail("owned repair resume must run unsuppressed before native install or application publication")
    if product.splitlines().count("run_repair_installed_corpus") != 1:
        fail("post-publication corpus repair must have one unsuppressed lifecycle invocation")
    published_tail = product.rsplit("\nrun_publish\n", 1)[-1]
    if not published_tail.startswith("trap - EXIT\n") or "recover_publish" in published_tail or "ensure_api_running" in published_tail:
        fail("publication recovery must end before repair owns service restoration")
    if not maintenance_path.exists() or not repair_path.exists():
        fail("installed database maintenance or measurement-lane wrapper is missing")
    else:
        maintenance = maintenance_path.read_text(encoding="utf-8")
        repair = repair_path.read_text(encoding="utf-8")
        commands = (
            'bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env',
            'bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"',
            'bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"',
        )
        lines = [line.strip() for line in maintenance.splitlines()]
        positions = [lines.index(command) if lines.count(command) == 1 else -1 for command in commands]
        if "set -euo pipefail" not in lines or any(pos < 0 for pos in positions) or positions != sorted(positions):
            fail("installed database maintenance sequence must migrate, reconcile, and verify once")
        measured = r'bash scripts/measure-lane\.sh --\s+\\\s+python3 scripts/repair-legacy-content\.py --database "\$database"'
        if len(re.findall(measured, repair)) != 1 or "set -euo pipefail" not in repair:
            fail("legacy content repair must execute inside the authoritative measurement lane")
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
