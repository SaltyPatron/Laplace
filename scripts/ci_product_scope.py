"""Repository path laws for product triggering and candidate freshness.

`ignored` paths are pure policy/verification/diagnostic surfaces: they neither
trigger Product — main delivery nor invalidate an existing candidate.
`candidate_equivalent` additionally admits managed test-project changes. Those
still trigger targeted qualification, but they do not change shipped product bytes.
"""
from __future__ import annotations

import fnmatch

PRODUCT_IGNORED_PREFIXES = (
    ".github/",
    "docs/",
    "scripts/test-suites/",
)

PRODUCT_IGNORED_SUFFIXES = (
    ".md",
)

PRODUCT_IGNORED_EXACT = frozenset({
    "vault-data-inventory.tsv",
    "vault-data-directories.list",
    "scripts/api-diagnostics.py",
    "web/scripts/capture-ui-diagnostics.mjs",
    "scripts/ci-impact-plan.py",
    "scripts/ci-qualification-cache.py",
    "scripts/ci-product-freshness.py",
    "scripts/ci_product_scope.py",
    "scripts/ci_managed_projects.py",
    "scripts/product-ci.sh",
    "scripts/pipeline.sh",
    "scripts/bootstrap-chess-lab.sh",
    "scripts/check-deployed-revision.sh",
    "scripts/test-parallel.sh",
    "deploy/linux/deploy.sh",
    "deploy/linux/payload-sync.sh",
    "deploy/linux/bootstrap-host.sh",
    "deploy/linux/laplace-managed-deploy",
    "deploy/linux/nginx-laplace.conf",
    "scripts/db-migrations.sh",
    "scripts/publish-applications.sh",
    "scripts/verify-api-payload.py",
    "scripts/verify-application-release.py",
    "scripts/check-application-runtime.py",
    "scripts/check-database-health.sh",
    "scripts/configure-laplace-environment.py",
    "scripts/test-workflow-architecture.py",
    "scripts/test-seed-workflow-ownership.py",
    "scripts/test-product-ci-artifact-ownership.py",
    "scripts/test-benchmark-suite.py",
    "scripts/validate-pipeline.py",
    "scripts/setup-host.sh",
})

PRODUCT_IGNORED_GLOBS = (
    "scripts/test-*.py",
    "scripts/test-*.sh",
    "scripts/tests/**",
    "web/scripts/test-*.mjs",
    "web/scripts/verify-*.mjs",
    "web/e2e/**",
)

# These are the equivalent GitHub Actions paths-ignore entries. A contract test
# keeps the workflow trigger synchronized with ignored().
GITHUB_PATH_IGNORES = (
    "vault-data-inventory.tsv",
    "vault-data-directories.list",
    "docs/**",
    "**/*.md",
    ".github/**",
    "scripts/api-diagnostics.py",
    "web/scripts/capture-ui-diagnostics.mjs",
    "scripts/ci-impact-plan.py",
    "scripts/ci-qualification-cache.py",
    "scripts/ci-product-freshness.py",
    "scripts/ci_product_scope.py",
    "scripts/ci_managed_projects.py",
    "scripts/product-ci.sh",
    "scripts/pipeline.sh",
    "scripts/bootstrap-chess-lab.sh",
    "scripts/check-deployed-revision.sh",
    "scripts/test-parallel.sh",
    "scripts/publish-applications.sh",
    "scripts/db-migrations.sh",
    "scripts/verify-api-payload.py",
    "scripts/verify-application-release.py",
    "scripts/check-application-runtime.py",
    "scripts/check-database-health.sh",
    "scripts/configure-laplace-environment.py",
    "deploy/linux/deploy.sh",
    "deploy/linux/payload-sync.sh",
    "deploy/linux/bootstrap-host.sh",
    "deploy/linux/laplace-managed-deploy",
    "deploy/linux/nginx-laplace.conf",
    "scripts/test-*.py",
    "scripts/test-*.sh",
    "scripts/tests/**",
    "web/scripts/test-*.mjs",
    "web/scripts/verify-*.mjs",
    "web/e2e/**",
    "scripts/test-workflow-architecture.py",
    "scripts/test-seed-workflow-ownership.py",
    "scripts/test-product-ci-artifact-ownership.py",
    "scripts/test-benchmark-suite.py",
    "scripts/validate-pipeline.py",
    "scripts/setup-host.sh",
)


def ignored(path: str) -> bool:
    return (
        path.startswith(PRODUCT_IGNORED_PREFIXES)
        or path.endswith(PRODUCT_IGNORED_SUFFIXES)
        or path in PRODUCT_IGNORED_EXACT
        or any(fnmatch.fnmatch(path, pattern) for pattern in PRODUCT_IGNORED_GLOBS)
    )


def managed_test_path(path: str) -> bool:
    parts = path.replace("\\", "/").split("/")
    return len(parts) >= 3 and parts[0] == "app" and parts[1].endswith(".Tests")


def native_test_path(path: str) -> bool:
    normalized = path.replace("\\", "/")
    return (
        normalized.startswith("engine/") and "/tests/" in normalized
    ) or (
        normalized.startswith("extension/") and "/tests/" in normalized
    )


def candidate_equivalent(path: str) -> bool:
    return ignored(path) or managed_test_path(path) or native_test_path(path)
