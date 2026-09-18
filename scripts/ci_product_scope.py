"""One source of truth for repository paths that do not change the product.

These paths may change CI policy, tests, diagnostics, or documentation. They are
validated by the Policy workflow, but they do not invalidate a qualified product
candidate and do not trigger Product — main delivery by themselves.
"""
from __future__ import annotations

import fnmatch

PRODUCT_IGNORED_PREFIXES = (
    ".github/",
    "docs/",
)

PRODUCT_IGNORED_SUFFIXES = (
    ".md",
)

PRODUCT_IGNORED_EXACT = frozenset({
    "scripts/api-diagnostics.py",
    "web/scripts/capture-ui-diagnostics.mjs",
    "scripts/ci-impact-plan.py",
    "scripts/ci-qualification-cache.py",
    "scripts/ci-product-freshness.py",
    "scripts/ci_product_scope.py",
    "scripts/test-parallel.sh",
    "scripts/test-workflow-architecture.py",
    "scripts/test-seed-workflow-ownership.py",
    "scripts/test-product-ci-artifact-ownership.py",
    "scripts/test-benchmark-suite.py",
    "scripts/validate-pipeline.py",
})

PRODUCT_IGNORED_GLOBS = (
    "scripts/test-ci-*.py",
)

# These are the equivalent GitHub Actions paths-ignore entries. A contract test
# keeps the workflow trigger synchronized with ignored().
GITHUB_PATH_IGNORES = (
    "docs/**",
    "**/*.md",
    ".github/**",
    "scripts/api-diagnostics.py",
    "web/scripts/capture-ui-diagnostics.mjs",
    "scripts/ci-impact-plan.py",
    "scripts/ci-qualification-cache.py",
    "scripts/ci-product-freshness.py",
    "scripts/ci_product_scope.py",
    "scripts/test-parallel.sh",
    "scripts/test-ci-*.py",
    "scripts/test-workflow-architecture.py",
    "scripts/test-seed-workflow-ownership.py",
    "scripts/test-product-ci-artifact-ownership.py",
    "scripts/test-benchmark-suite.py",
    "scripts/validate-pipeline.py",
)


def ignored(path: str) -> bool:
    return (
        path.startswith(PRODUCT_IGNORED_PREFIXES)
        or path.endswith(PRODUCT_IGNORED_SUFFIXES)
        or path in PRODUCT_IGNORED_EXACT
        or any(fnmatch.fnmatch(path, pattern) for pattern in PRODUCT_IGNORED_GLOBS)
    )
