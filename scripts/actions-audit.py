#!/usr/bin/env python3
"""Fail closed when Actions stops describing one Laplace product lifecycle."""
from pathlib import Path
import os
import re
import subprocess
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


def canonical_phases(kind: str, stage: str) -> list[str]:
    """Read the shared lifecycle's selection without executing a lifecycle phase."""
    script = ROOT / "scripts" / ("product-ci.sh" if kind == "product" else "pr-proof.sh")
    if "--list-phases" not in script.read_text(encoding="utf-8"):
        fail(f"{script.name}: missing independently selectable lifecycle phases")
        return []
    command = ["bash", str(script)]
    if kind == "product":
        command.append(stage)
    else:
        command.extend(["--stage", stage])
    command.append("--list-phases")
    env = dict(os.environ, LAPLACE_GENERATION_BENCHMARK="1", LAPLACE_FRESH_DB="",
               LAPLACE_RESTORE_FOUNDATION="1")
    result = subprocess.run(command, cwd=ROOT, env=env, text=True,
                            capture_output=True, timeout=15)
    phases = result.stdout.splitlines()
    if result.returncode or not phases or len(phases) != len(set(phases)) \
            or any(not re.fullmatch(r"[a-z]+(?:-[a-z]+)*", phase) for phase in phases):
        fail(f"{script.name}: invalid canonical phase plan for {stage}")
        return []
    return phases


def visible_phases(job: dict, kind: str) -> tuple[list[str], int, int]:
    """The workflow exposes each selected shared command as its own result."""
    steps = job.get("steps") or []
    context = f"{kind} lifecycle"
    session = unique_step(steps, "id", f"{kind}_session", context)
    stops = [(i, step) for i, step in enumerate(steps)
             if "ci-session.py" in step.get("run", "")
             and re.search(r"ci-session\.py[\"']?\s+stop\b", step.get("run", ""))]
    if len(stops) != 1:
        fail(f"{context}: requires one always-run session cleanup")
    if not session or len(stops) != 1:
        return [], -1, -1
    start_index, start_step = session
    stop_index, stop_step = stops[0]
    start_command = start_step.get("run", "")
    for token in ("ci-session.py", "start", f"--kind {kind}", "--checkout", "--lock",
                  "/build/laplace/work/host-resource.lock"):
        if token not in start_command:
            fail(f"{context}: session start lacks {token}")
    if stop_step.get("if") != f"always() && steps.{kind}_session.outcome == 'success'":
        fail(f"{context}: cleanup must run after phase failure or cancellation")
    found = []
    for index, step in enumerate(steps):
        command = step.get("run", "")
        matches = re.findall(r"--phase\s+([a-z]+(?:-[a-z]+)*)", command)
        if not matches:
            continue
        if len(matches) != 1 or "ci-session.py" not in command:
            fail(f"{context}: a visible step must execute exactly one shared phase")
            continue
        phase = matches[0]
        found.append(phase)
        if not start_index < index < stop_index:
            fail(f"{context}: phase {phase} escapes session ownership")
        if step.get("if") != f"contains(env.LAPLACE_CI_PHASES, '|{phase}|')":
            fail(f"{context}: phase {phase} must use canonical selection and normal failure propagation")
        if step.get("id") != f"{kind}_{phase.replace('-', '_')}":
            fail(f"{context}: phase {phase} has no stable individual result identity")
    if len(found) != len(set(found)):
        fail(f"{context}: duplicated lifecycle phase")
    return found, start_index, stop_index


def result_authority(name: str, workflow: dict) -> None:
    """Optional diagnostics never acquire proof authority; readiness is deferred."""
    if name == "benchmark-evidence.yml":
        if set(workflow.get("jobs") or {}) != {"benchmark"}:
            fail("benchmark-evidence.yml: the reusable workflow may expose only its measurement job")
        for job in (workflow.get("jobs") or {}).values():
            for token in ("product-ci.sh", "ci-session.py", "pipeline.sh install", "pipeline.sh migrate", "publish-applications.sh", "systemctl ", "sudo "):
                if token in runs(job):
                    fail(f"benchmark-evidence.yml: measurement may not acquire product mutation authority: {token}")
    for job_name, job in (workflow.get("jobs") or {}).items():
        context = f"{name}:{job_name}"
        if enabled(job.get("continue-on-error")):
            fail(f"{context}: hidden red job result")
        steps = job.get("steps") or []
        allowed = set()
        if (name, job_name) in (("laplace.yml", "product"), ("pr-validation.yml", "prove")):
            prefix = "product" if name == "laplace.yml" else "pr"
            baseline = unique_step(steps, "id", f"{prefix}_native_baseline", context)
            upload = unique_step(steps, "name", "Upload retained native baseline before build", context)
            policy = unique_step(steps, "id", f"{prefix}_policy", context)
            if baseline and upload and policy:
                allowed.update((baseline[0], upload[0]))
                if not baseline[0] < upload[0] < policy[0]:
                    fail(f"{context}: retained native diagnostics must precede build execution")
                expected = "env.LAPLACE_FAST_ONLY != '1'" if prefix == "product" else "env.LAPLACE_PR_FULL_PROOF == '1'"
                if baseline[1].get("if") != expected:
                    fail(f"{context}: retained native diagnostics have incorrect selection")
                if "capture-native-regression.py" not in baseline[1].get("run", ""):
                    fail(f"{context}: native baseline must use the shared read-only capture")
                expected_upload = f"always() && steps.{prefix}_native_baseline.outcome != 'skipped'"
                if upload[1].get("if") != expected_upload or "run" in upload[1] \
                        or not upload[1].get("uses", "").startswith("actions/upload-artifact@"):
                    fail(f"{context}: optional native baseline upload may only retain the attempted diagnostic")
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
            proof = unique_step(steps, "id", "pr_session", context)
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
                for token in ("--proof-outcome baseline-before-proof", "scripts/collect-recursive-proof-evidence.py"):
                    if token not in baseline_command:
                        fail(f"{context}: baseline lacks existing-file retention contract: {token}")
                if "if" in proof_step or "baseline_diagnostic" in str(proof_step):
                    fail(f"{context}: actual PR proof must remain independent of optional baseline success")
                for token in ("ci-session.py", "--kind pr", "set -euo pipefail"):
                    if token not in proof_step.get("run", ""):
                        fail(f"{context}: actual PR proof lost authority: {token}")
        for index in allowed:
            step = steps[index]
            if step.get("continue-on-error") != "true":
                fail(f"{context}: diagnostic failure deferral must be explicit")
            for token in ("pr-proof.sh", "ci-session.py", "test-parallel.sh", "product-ci.sh", "pipeline.sh", "publish-applications.sh", "systemctl ", "sudo ", "prove-live-recursive-substrate.py"):
                if token in step.get("run", ""):
                    fail(f"{context}: optional diagnostic contains an authoritative or mutating operation: {token}")
        for index, step in enumerate(steps):
            if enabled(step.get("continue-on-error")) and index not in allowed:
                fail(f"{context}: hidden red step result: {step.get('id', step.get('name', index))}")


def product_topology(main: dict) -> None:
    jobs = main.get("jobs") or {}
    if set(jobs) != {"product"}:
        fail(f"laplace.yml must expose product delivery without domain measurement jobs, found {sorted(jobs)}")
    product_job = jobs.get("product") or {}
    steps = product_job.get("steps") or []
    phases, _, _ = visible_phases(product_job, "product")
    plans = [canonical_phases("product", stage) for stage in (
        "check", "build", "test", "deploy", "integrate", "all",
        "application-check", "applications")]
    required = {phase for plan in plans for phase in plan}
    if set(phases) != required:
        fail(f"laplace.yml: visible phases differ from the complete shared lifecycle: {sorted(required ^ set(phases))}")
    for plan in plans:
        if [phase for phase in phases if phase in plan] != plan:
            fail("laplace.yml: visible phase order differs from the shared lifecycle")
    session = unique_step(steps, "id", "product_session", "laplace.yml:product")
    if session:
        environment = session[1].get("env") or {}
        if environment.get("LAPLACE_OPERATIONAL_PROOF_DIRECTORY") != "/build/laplace/work/operational-proof/${{ github.run_id }}-${{ github.run_attempt }}":
            fail("laplace.yml: operational evidence must identify this exact run and attempt")
    operational = unique_step(steps, "name", "Upload operational seed and execution receipts", "laplace.yml:product")
    execution = unique_step(steps, "id", "product_operational_execution", "laplace.yml:product")
    if operational and execution:
        expected = "always() && steps.product_session.outcome == 'success' && contains(env.LAPLACE_CI_PHASES, '|operational-seed|')"
        if operational[0] <= execution[0] or operational[1].get("if") != expected:
            fail("laplace.yml: retain operational receipts after attempted execution, including seed-only stages")
        if (operational[1].get("with") or {}).get("path") != "/build/laplace/work/operational-proof/${{ github.run_id }}-${{ github.run_attempt }}/":
            fail("laplace.yml: operational upload must retain this exact run and attempt")



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
    if "ci-session.py" not in command or "--kind product" not in command:
        fail("main product job bypasses the shared product phase executor")
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
        "policy", "dependencies", "build", "native-dev", "managed-dev", "uci-dev", "browser-dev",
        "native-install", "database-maintenance", "foundation", "operational-seed", "publish",
        "operational-execution", "db-health", "native-db", "managed-db", "live-floor", "live-api",
        "managed-live", "generation-eval", "performance",
    ]
    if canonical_phases("product", "all") != required_order:
        fail("product lifecycle phase selection/order drifted")
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
        'test-parallel.sh --profile "$1" --suite "$2"',
        'db-health|managed-db) run_suite db "$1"',
        'run_suite db native-db',
        'operational-execution) verify_operational_execution ;;',
    ):
        if token not in product:
            fail(f"product lifecycle missing {token}")
    for forbidden in ("repair-legacy-content", "resume_held_repair", "corpus-repair"):
        if forbidden in product:
            fail("product lifecycle must not automatically repair historical content")
    maintenance_path = ROOT / "scripts" / "maintain-installed-database.sh"
    delegation = r'python3 scripts/quiesce-managed-database\.py --database "\$\{PGDATABASE:-laplace\}" --\s+\\\s+bash scripts/maintain-installed-database\.sh'
    if len(re.findall(delegation, product)) != 1 or product.count("maintain-installed-database.sh") != 1:
        fail("installed database maintenance must have one managed-quiescence owner")
    if not maintenance_path.exists():
        fail("installed database maintenance wrapper is missing")
    else:
        maintenance = maintenance_path.read_text(encoding="utf-8")
        commands = (
            'bash scripts/pipeline.sh "${args[@]}" migrate sync-extension tune-pg tune-laplace perfcache-guc api-env',
            'bash scripts/reconcile-highway-masks.sh "${PGDATABASE:-laplace}"',
            'bash scripts/check-database-health.sh "${PGDATABASE:-laplace}"',
        )
        lines = [line.strip() for line in maintenance.splitlines()]
        positions = [lines.index(command) if lines.count(command) == 1 else -1 for command in commands]
        if "set -euo pipefail" not in lines or any(pos < 0 for pos in positions) or positions != sorted(positions):
            fail("installed database maintenance sequence must migrate, reconcile, and verify once")
    reconcile = product.split("reconcile_installed_product() {", 1)[1].split("\n}", 1)[0]
    if "ensure-foundation.sh" in reconcile or "ensure_product_foundation" in reconcile:
        fail("fast installed-product reconciliation may not auto-seed")
    if "pipeline.sh" in reconcile:
        fail("fast installed-product reconciliation may not build/install/migrate")

proof = (ROOT / "scripts" / "pr-proof.sh").read_text(encoding="utf-8")
if proof.count("test-parallel.sh --policy") != 1:
    fail("full PR proof must execute policy profile exactly once")
pr_expected = ["policy", "build", "private-db-start", "native-db", "operational-db",
               "highway-recovery", "legacy-repair-db", "private-db-stop", "native-dev",
               "managed-dev", "uci-dev", "browser-dev", "proof-complete"]
if canonical_phases("pr", "all") != pr_expected or canonical_phases("pr", "check") != ["policy"]:
    fail("PR proof must run its isolated database phases before the separate DEV/BAT suites")
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
pr_phases, _, _ = visible_phases(prove, "pr")
if pr_phases != pr_expected:
    fail("PR workflow must expose the complete canonical proof as individual ordered steps")
if "pull_request" not in triggers(pr):
    fail("PR proof workflow has no pull_request trigger")
if "head.repo.full_name == github.repository" not in str(prove.get("if", "")):
    fail("PR proof may execute fork code on self-hosted runner")
if "${{ secrets." in pr_source:
    fail("PR proof reads repository secrets")
for token in (
    "LAPLACE_PR_FULL_PROOF", "ci-session.py", "--kind pr",
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
if 'python3 scripts/test-profile-registry.py run --profile "$1" "${args[@]}"' not in test_runner:
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
