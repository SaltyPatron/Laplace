#!/usr/bin/env python3
"""Repository-level CI/CD architecture invariants."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"


def shell_function(source: str, name: str) -> str:
    start = source.index(f"{name}() {{\n")
    return source[start:source.index("\n}\n", start) + 3]


def code(body: str) -> str:
    """The executable lines of a shell body; comments name owners they do not call."""
    return "\n".join(line for line in body.splitlines() if not line.lstrip().startswith("#"))


class WorkflowArchitecture(unittest.TestCase):
    def test_manual_deployment_passes_the_selected_promotion_boundary(self):
        maintenance = (WORKFLOWS / "product-operator.yml").read_text(encoding="utf-8")
        delivery = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        deploy = maintenance.split("\n  deploy:\n", 1)[1].split("\n  verify:\n", 1)[0]
        self.assertIn("inputs.target != 'cloud'", deploy)
        self.assertIn("promote: ${{ inputs.target == 'production' }}", deploy)
        start_cloud = delivery.split("\n  cloud-host:\n", 1)[1].split("\n  production-build:\n", 1)[0]
        self.assertIn("needs.deliver.result == 'success' && inputs.qualify_cloud == true", start_cloud)
        self.assertNotIn("github.event_name == 'push' || inputs.promote", start_cloud)
        production = delivery.split("\n  production:\n", 1)[1]
        self.assertIn("needs: deliver", production)
        self.assertNotIn("needs: production-build", production)
        staging = delivery.split("\n  cloud-host:\n", 1)[0]
        self.assertIn("run: deploy/linux/site.sh bundle", staging)
        self.assertIn("qualify_cloud: true", maintenance)
        self.assertIn("promote:\n", delivery)
        self.assertIn("type: boolean\n        default: true", delivery)

    def test_access_observer_runs_reviewed_reads_without_deployment_or_cloud(self):
        maintenance = (WORKFLOWS / "product-operator.yml").read_text(encoding="utf-8")
        validation = maintenance.split("\n  access-contract:\n", 1)[1].split("\n  access-audit:\n", 1)[0]
        observer = maintenance.split("\n  access-audit:\n", 1)[1].split("\n  unsupported-deployment:\n", 1)[0]
        self.assertIn("runs-on: ubuntu-24.04", validation)
        self.assertIn("python3 scripts/test-platform-access-audit.py", validation)
        self.assertIn("needs: [access-contract, access-enroll, owner-enroll]", observer)
        self.assertIn("inputs.target != 'cloud'", observer)
        self.assertIn('host_lock_hold shared 8 "$lock" 120', observer)
        self.assertIn('contents: read', observer)
        self.assertNotIn('id-token:', observer)
        self.assertNotIn('site.sh', observer)
        self.assertIn('collector_revision=', observer)
        self.assertIn('installed_revision=', observer)
        self.assertIn("inputs.operation == 'access-audit' && inputs.target == 'cloud'", maintenance)
        self.assertIn("workflow_dispatch:", maintenance)
        self.assertNotIn("\n  push:\n", maintenance)
        import importlib.util
        scope_spec = importlib.util.spec_from_file_location('access_audit_scope', ROOT / 'scripts/ci_product_scope.py')
        scope = importlib.util.module_from_spec(scope_spec)
        scope_spec.loader.exec_module(scope)
        self.assertTrue(scope.ignored('scripts/platform-access-audit.py'))
        self.assertIn('- "scripts/platform-access-audit.py"', (WORKFLOWS / 'laplace.yml').read_text())

    def test_initial_access_enrollment_selects_a_reviewed_request_and_reserves_the_host(self):
        maintenance = (WORKFLOWS / "product-operator.yml").read_text(encoding="utf-8")
        enrollment = maintenance.split("\n  access-enroll:\n", 1)[1].split("\n  access-audit:\n", 1)[0]
        self.assertIn("needs.access-contract.outputs.request != ''", enrollment)
        self.assertIn("runs-on: [self-hosted, laplace, staging]", enrollment)
        self.assertIn("group: laplace-site-hart-server", enrollment)
        self.assertIn('host_lock_hold exclusive 8 "$lock" 120', enrollment)
        self.assertLess(enrollment.index("host_lock_hold exclusive"), enrollment.index("python3 scripts/platform-access-enroll.py"))
        self.assertIn('PGDATABASE="$LAPLACE_APP_DBNAME"', enrollment)
        self.assertIn("LAPLACE_WORK_ROOT:?Declare persistent host work", enrollment)
        self.assertIn("Request must select the declared API listener", enrollment)
        self.assertIn("contents: read", enrollment)
        self.assertNotIn("id-token:", enrollment)
        self.assertNotIn("site.sh", enrollment)
        self.assertNotIn("sudo", enrollment)
        self.assertIn("'--diff-filter=AM'", maintenance)
        self.assertIn("Review one enrollment request per operation", maintenance)
        self.assertIn("Initial enrollment supports staging only", maintenance)
        self.assertIn('scripts/platform-access-enroll.py', (ROOT / 'scripts/ci_product_scope.py').read_text())
        self.assertIn('- "scripts/platform-access-enroll.py"', (WORKFLOWS / 'laplace.yml').read_text())

    def test_no_ephemeral_repair_workflows_remain(self):
        names = {path.name for path in WORKFLOWS.glob("*.yml")}
        repairs = sorted(name for name in names if "repair" in name)
        self.assertEqual([], repairs)

    def test_redundant_chess_micro_workflows_are_retired(self):
        for name in (
            "seed-chess-books.yml",
            "seed-chess-eval.yml",
            "seed-chess-games.yml",
            "seed-chess-openings.yml",
        ):
            self.assertFalse((WORKFLOWS / name).exists())

    def test_surviving_workflows_use_operator_categories_not_laplace_prefix_clutter(self):
        expected = {
            "api-observability.yml": "name: Observe — API",
            "ui-observability.yml": "name: Observe — UI",
            "benchmark-evidence.yml": "name: Measure — benchmark",
            "competitive-proof.yml": "name: Product — competitive proof",
            "db-ops.yml": "name: Database — maintenance",
            "ci-contract.yml": "name: Policy — CI contract",
            "dev-tests.yml": "name: Policy — dev tests",
            "installed-database-health.yml": "name: Runtime — installed database health",
            "full-qualification.yml": "name: Audit — full product qualification",
            "laplace.yml": "name: Product — main delivery",
            "operator-workflows.yml": "name: Operator workflow validation",
            "product-operator.yml": "name: Product — maintenance",
            "product-stage.yml": "name: Internal — product stage",
            "seed.yml": "name: Internal — substrate ingest",
            "seed-foundation.yml": "name: Data — foundation ingest",
            "seed-relations.yml": "name: Data — relations ingest",
            "seed-documents.yml": "name: Data — documents ingest",
            "seed-code.yml": "name: Data — code ingest",
            "seed-chess.yml": "name: Data — chess ingest",
            "seed-models.yml": "name: Data — models ingest",
        }
        actual = {path.name: path.read_text(encoding="utf-8").splitlines()[0]
                  for path in WORKFLOWS.glob("*.yml")}
        self.assertEqual(expected, actual)

    def test_hosted_policy_lane_owns_writable_external_fixture_cache(self):
        product = (ROOT / "scripts" / "product-ci.sh").read_text(encoding="utf-8")
        checks = product.split("run_ci_contract_checks() {", 1)[1].split("\n}", 1)[0]
        self.assertIn('LAPLACE_WORK_ROOT="$ci_tmp/laplace-work"', checks)
        self.assertIn('LAPLACE_CHESS_PGN_CACHE="$LAPLACE_WORK_ROOT/chess-pgn-validation"', checks)
        self.assertIn('mkdir -p "$ci_tmp" "$LAPLACE_WORK_ROOT" "$LAPLACE_CHESS_PGN_CACHE"', checks)

    def test_ci_contract_is_an_explicit_lightweight_hosted_lane(self):
        text = (WORKFLOWS / "ci-contract.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_dispatch:", text)
        self.assertNotIn("\n  push:\n", text)
        self.assertIn("runs-on: ubuntu-24.04", text)
        self.assertNotIn("self-hosted", text)
        self.assertIn("bash scripts/product-ci.sh check", text)
        self.assertIn("bash tests/identity-law.sh", text)   # the identity law gates every pull request
        self.assertIn("laplace-actions-history-{0}", text)
        self.assertIn("[actions-history-cleanup]", text)
        self.assertIn("[actions-evidence-archive]", text)
        self.assertIn("cancel-in-progress:", text)

    def test_dev_tests_run_every_pull_request_on_the_pinned_intel_host(self):
        # The build pins MKL's CNR branch (MKL_CBWR_AVX2), which MKL takes only on Intel CPUs; the hosted runners are
        # AMD, so the dev profile runs on hart-server's runner, and never for a fork's pull request.
        text = (WORKFLOWS / "dev-tests.yml").read_text(encoding="utf-8")
        self.assertIn("pull_request:\n    branches: [main]", text)
        self.assertIn("runs-on: [self-hosted, laplace, staging]", text)
        self.assertIn("github.event.pull_request.head.repo.full_name == github.repository", text)
        self.assertIn("GenuineIntel", text)
        for phase in ("deps", "native", "managed", "uci", "browser"):
            self.assertIn(f"bash scripts/ci/hosted-dev-tests.sh {phase}", text)
        hosted = (ROOT / "scripts/ci/hosted-dev-tests.sh").read_text(encoding="utf-8")
        self.assertNotIn("FullyQualifiedName!=", hosted)   # every test runs: none is excluded by name
        self.assertNotIn(" -E ", hosted)

    def test_ci_policy_scripts_do_not_trigger_or_invalidate_product_delivery(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        impact = (ROOT / "scripts" / "ci-impact-plan.py").read_text(encoding="utf-8")
        freshness = (ROOT / "scripts" / "ci-product-freshness.py").read_text(encoding="utf-8")
        self.assertIn('from ci_product_scope import ignored as product_ignored', impact)
        self.assertIn('from ci_product_scope import candidate_equivalent, ignored', freshness)
        for path in (
            "scripts/ci-impact-plan.py",
            "scripts/ci-qualification-cache.py",
            "scripts/ci-product-freshness.py",
            "scripts/ci_product_scope.py",
            "scripts/test-parallel.sh",
            "deploy/linux/deploy.sh",
            "scripts/publish-applications.sh",
            "scripts/test-suites/**",
            "scripts/ci_managed_projects.py",
            "scripts/test-*.py",
            "scripts/test-*.sh",
            "scripts/tests/**",
            "web/scripts/test-*.mjs",
            "web/e2e/**",
            "scripts/test-workflow-architecture.py",
            "scripts/test-seed-workflow-ownership.py",
            "scripts/test-product-ci-artifact-ownership.py",
            "scripts/test-benchmark-suite.py",
            "scripts/validate-pipeline.py",
        ):
            self.assertIn(f'- "{path}"', lifecycle)

    def test_managed_project_impact_drives_build_and_test_selection(self):
        pipeline = (ROOT / "scripts" / "pipeline.sh").read_text(encoding="utf-8")
        dispatcher = (ROOT / "scripts" / "test-parallel.sh").read_text(encoding="utf-8")
        common = (ROOT / "scripts" / "test-suites" / "common.sh").read_text(encoding="utf-8")
        managed = (ROOT / "scripts" / "test-suites" / "managed-dev.sh").read_text(encoding="utf-8")
        managed_db = (ROOT / "scripts" / "test-suites" / "managed-db.sh").read_text(encoding="utf-8")
        managed_live = (ROOT / "scripts" / "test-suites" / "managed-live.sh").read_text(encoding="utf-8")
        product = (ROOT / "scripts" / "product-ci.sh").read_text(encoding="utf-8")

        self.assertIn("LAPLACE_MANAGED_BUILD_PROJECTS", pipeline)
        self.assertIn("ci_managed_projects.py", pipeline)
        self.assertIn('driver="$ROOT/scripts/test-suites/$1.sh"', dispatcher)
        self.assertNotIn("run_managed_dev() {", dispatcher)
        self.assertNotIn("run_live_api() {", dispatcher)
        self.assertIn("run_managed_dotnet_tests", common)
        self.assertIn("LAPLACE_MANAGED_TEST_TIMEOUT", common)
        self.assertIn("No test matches", common)
        self.assertIn("filter matched zero tests", common)
        self.assertIn('tee "$test_log"', common)
        self.assertIn("LAPLACE_MANAGED_TEST_PROJECTS", managed)
        self.assertIn("LAPLACE_MANAGED_TEST_FILTER", common)
        self.assertIn("LAPLACE_MANAGED_DB_TEST_PROJECTS", managed_db)
        self.assertIn("LAPLACE_MANAGED_LIVE_TEST_PROJECTS", managed_live)
        for suite in (
            "native-dev", "managed-dev", "uci-dev", "browser-dev",
            "db-health", "native-db", "managed-db",
            "live-floor", "live-api", "managed-live", "generation-eval",
            "chess-provider-live",
        ):
            with self.subTest(suite=suite):
                self.assertTrue((ROOT / "scripts" / "test-suites" / f"{suite}.sh").is_file())

        checks = product.split("run_ci_contract_checks() {", 1)[1].split("\n}", 1)[0]
        self.assertIn("scripts/test-suites/*.sh", checks)
        self.assertIn("test-managed-policy.py", checks)
        self.assertIn("test-ci-managed-projects.py", checks)


    def test_pure_uci_change_uses_isolated_atomic_publication(self):
        impact = (ROOT / "scripts" / "ci-impact-plan.py").read_text(encoding="utf-8")
        product = (ROOT / "scripts" / "product-ci.sh").read_text(encoding="utf-8")
        publish = (ROOT / "scripts" / "publish-applications.sh").read_text(encoding="utf-8")
        deploy = (ROOT / "deploy" / "linux" / "deploy.sh").read_text(encoding="utf-8")

        self.assertIn("pure_uci", impact)
        self.assertIn('publish_scope = "uci"', impact)
        self.assertIn('uci) bash scripts/publish-applications.sh uci-recover', product)
        self.assertIn('uci) bash scripts/publish-applications.sh uci-deploy', product)
        self.assertIn("verify_isolated_uci_delivery", product)
        self.assertIn("uci-deploy|uci-recover", publish)
        self.assertIn("LAPLACE_UCI_REVISION_RECEIPT=1", publish)
        self.assertIn("--uci-only", deploy)
        self.assertIn("uci_revision_snapshot", deploy)
        self.assertIn("uci_revision_restore", deploy)

        checks = product.split("run_ci_contract_checks() {", 1)[1].split("\n}", 1)[0]
        self.assertIn("test-application-publish.py", checks)

    def test_build_executor_obeys_planned_managed_and_web_components(self):
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        run_build = product.split("run_build() {", 1)[1].split("\n}\n\nrun_dev_test_matrix", 1)[0]
        self.assertIn("need_managed", run_build)
        self.assertIn("need_web", run_build)
        self.assertIn('phases+=(build-app)', run_build)
        self.assertIn('phases+=(build-web)', run_build)
        # Publication closure belongs to ci-impact-plan.py; the executor must not
        # independently infer managed work from need_web and drift from the plan.
        self.assertNotIn("need_web == 0 )) || need_managed=1", run_build)
    def test_planned_web_component_is_built_before_delivery(self):
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        pipeline = (ROOT / "scripts/pipeline.sh").read_text(encoding="utf-8")
        run_build = product.split("run_build() {", 1)[1].split("\n}\n\nrun_dev_test_matrix", 1)[0]
        self.assertIn("need_web", run_build)
        self.assertIn('phases+=(build-web)', run_build)
        self.assertIn("phase_build_web()", pipeline)
        self.assertIn("npm run build", pipeline)
        self.assertIn("build-web) phase_build_web", pipeline)

    def test_automatic_publication_consumes_qualified_or_installed_web_without_rebuild(self):
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        pipeline = (ROOT / "scripts/pipeline.sh").read_text(encoding="utf-8")
        deploy = (ROOT / "deploy/linux/deploy.sh").read_text(encoding="utf-8")

        self.assertIn("LAPLACE_REQUIRE_QUALIFIED_WEB", product)
        self.assertIn("LAPLACE_REUSE_INSTALLED_WEB", product)
        self.assertIn("web-artifact.py", pipeline)
        self.assertIn(" seal ", pipeline)
        self.assertIn("web-artifact.py", deploy)
        self.assertIn(" verify ", deploy)
        self.assertIn("use exact qualified front-end artifact", deploy)
        self.assertIn("preserve installed front-end artifact", deploy)
        self.assertIn('cp -r "$APP_DIR/wwwroot/." "$STAGE/wwwroot/"', deploy)
    def test_test_only_successor_can_carry_undelivered_product_work(self):
        product = (ROOT / "scripts" / "product-ci.sh").read_text(encoding="utf-8")
        carry = shell_function(product, "carry_forward_undelivered_impact")
        self.assertNotIn('[[ -n "${LAPLACE_DELIVERY_ACTIONS:-}" ]]', carry)
        self.assertNotIn("deployed product carry-forward skipped", carry)
        # Each installed component diffs its own receipt against the target, so a
        # test-only successor still carries every undelivered component forward.
        for name in ("carry_forward_installed_native_impact",
                     "carry_forward_installed_ingest_runtime_impact",
                     "carry_forward_installed_application_impact"):
            with self.subTest(owner=name):
                self.assertIn(name, carry)
                self.assertIn('--base "$deployed" --head "$target"',
                              shell_function(product, name))
        self.assertIn("carry_forward_installed_extension_impact", carry)
        self.assertIn("carry_forward_installed_web_impact", carry)
        fallback = shell_function(product, "force_full_carry_forward_impact")
        self.assertIn("append_csv_env LAPLACE_BUILD_COMPONENTS managed", fallback)
        self.assertIn("Laplace.Endpoints.OpenAICompat.csproj", fallback)
        self.assertIn("append_csv_env LAPLACE_DELIVERY_ACTIONS publish", fallback)
        self.assertIn("merge_publish_scope full", fallback)
        self.assertNotIn("LAPLACE_DB_SUITES", fallback)
        self.assertNotIn("LAPLACE_LIVE_SUITES", fallback)
        self.assertNotIn("LAPLACE_DEV_SUITES", fallback)
        lifecycle = (WORKFLOWS / "laplace.yml").read_text(encoding="utf-8")
        self.assertIn('- "scripts/product-ci.sh"', lifecycle)

    def test_main_delivery_crosses_mutation_boundary_once_and_executes_impact_plan(self):
        product = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        qualification = product.split("run_release_qualification() {", 1)[1].split("\n}", 1)[0]
        mutation = product.split("run_release_mutation_window() (", 1)[1].split(
            "\n)\n\nrun_release_candidate() {", 1)[0]
        automatic = product.split("run_release_delivery() {", 1)[1].split("\n}", 1)[0]

        self.assertIn("run_build", qualification)
        self.assertIn("run_dev_test_matrix 1", qualification)
        self.assertNotIn("run_build", automatic)
        self.assertIn("release_candidate_current_before_mutation", automatic)
        self.assertIn("export LAPLACE_SKIP_IF_SUPERSEDED=0", automatic)
        self.assertIn("LAPLACE_DELIVERY_ACTIONS", automatic)
        self.assertIn('run_release_mutation_window "$actions"', automatic)
        for selector in ("install", "database"):
            self.assertIn(f'csv_selected "$actions" {selector}', mutation)
        for selector in ("reconcile", "publish"):
            self.assertIn(f'csv_selected "$actions" {selector}', automatic)
        self.assertIn("run_install", mutation)
        self.assertIn("run_database_maintenance --prepare", mutation)
        self.assertNotIn("run_db_tests", mutation)
        self.assertNotIn('run_release_mutation_window "$actions"', mutation)
        # Integration suites are explicit test-db/audit work, not delivery steps.
        self.assertNotIn("run_db_tests", code(automatic))
        self.assertLess(
            automatic.index('run_release_mutation_window "$actions"'),
            automatic.index("run_publish"),
        )
        self.assertIn("verify_installed_product", automatic)
        self.assertNotIn("run_live_tests", automatic)
        self.assertNotIn("run_release_activation", automatic)

    def test_competitive_proof_remains_explicit_and_composed(self):
        proof = (WORKFLOWS / "competitive-proof.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_dispatch:", proof)
        self.assertNotIn("\n  push:\n", proof)
        for stage in ("competitive",):
            self.assertIn(f"operation: {stage}", proof)
        self.assertNotIn("scripts/product-ci.sh", proof)
        self.assertNotIn("runs-on: [self-hosted, laplace]", proof)

    def test_observability_is_explicit_evidence_not_push_queue_load(self):
        for name in ("ui-observability.yml", "api-observability.yml"):
            text = (WORKFLOWS / name).read_text(encoding="utf-8")
            self.assertIn("workflow_dispatch:", text)
            self.assertNotIn("\n  push:\n", text)

    def test_benchmark_uses_immutable_driver_under_real_host_lock(self):
        text = (WORKFLOWS / "benchmark-evidence.yml").read_text(encoding="utf-8")
        driver = (ROOT / "scripts/benchmark-evidence-ci.sh").read_text(encoding="utf-8")
        self.assertNotIn("\nconcurrency:\n", text)
        self.assertEqual(1, text.count("host-resource.lock"))
        self.assertIn('git fetch --no-tags --depth=1 origin "$DISPATCH_SHA"', text)
        self.assertIn('git show "$workflow_sha:scripts/benchmark-evidence-ci.sh"', text)
        self.assertIn('git fetch --no-tags --prune origin "$target"', text)
        self.assertIn('exec bash "$driver"', text)
        self.assertIn("scripts/benchmark_scale_plan.py", driver)
        self.assertNotIn('echo "- Commit: `$sha`"', text)
        self.assertIn('echo "- Commit: \\`$sha\\`"', text)

    def test_observability_uploads_failure_evidence_then_fails_truthfully(self):
        for name in ("ui-observability.yml", "api-observability.yml"):
            text = (WORKFLOWS / name).read_text(encoding="utf-8")
            self.assertEqual(1, text.count("host-resource.lock"))
            self.assertIn("collector-exit-code.txt", text)
            self.assertIn("Enforce collector result", text)
            self.assertIn("if-no-files-found: error", text)
            self.assertNotIn("exit 0", text)
        ui = (WORKFLOWS / "ui-observability.yml").read_text(encoding="utf-8")
        self.assertNotIn("build/.stamps/npm-lock", ui)
        self.assertIn("ui-observability", ui)

    def test_seed_preflight_is_not_coupled_to_cli_help_rendering(self):
        text = (WORKFLOWS / "seed.yml").read_text(encoding="utf-8")
        self.assertNotIn("scripts/laplace --help", text)
        self.assertIn('exec bash scripts/ensure-foundation.sh', text)

    def test_operator_runbook_documents_every_operator_and_internal_surface(self):
        runbook = (ROOT / "docs" / "guides" / "CI_OPERATOR_RUNBOOK.md").read_text(encoding="utf-8")
        for name in (
            "Product — main delivery",
            "Product — maintenance",
            "Product — competitive proof",
            "Audit — full product qualification",
            "Database — maintenance",
            "Data — foundation ingest",
            "Data — content ingest",
            "Data — documents ingest",
            "Data — code ingest",
            "Data — chess ingest",
            "Data — models ingest",
            "Observe — API",
            "Observe — UI",
            "Measure — benchmark",
            "Policy — CI contract",
            "Internal — product stage",
            "Internal — substrate ingest",
        ):
            self.assertIn(name, runbook)
        self.assertIn("Resource / concurrency contract", runbook)
        self.assertIn("Outputs", runbook)

    def test_internal_workflows_are_not_manually_dispatchable(self):
        for name in ("product-stage.yml", "seed.yml"):
            text = (WORKFLOWS / name).read_text(encoding="utf-8")
            self.assertIn("workflow_call:", text)
            self.assertNotIn("workflow_dispatch:", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
