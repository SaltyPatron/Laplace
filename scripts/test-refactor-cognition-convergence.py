#!/usr/bin/env python3
"""Source-only contract for the Laplace -> Laplace-Refactor cognition boundary."""

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ADAPTER = ROOT / "extension/laplace_substrate/src/refactor_cognition.c"
CMAKE = ROOT / "extension/laplace_substrate/CMakeLists.txt"
SQL = ROOT / "extension/laplace_substrate/sql/functions/converse/chat_scaffold.sql.in"
BUILD = ROOT / "scripts/build-refactor-engine.sh"
CATALOG = ROOT / "engine/core/src/sql_catalog_refactor.def"


def require(text: str, needle: str, label: str) -> None:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")


def forbid(text: str, needle: str, label: str) -> None:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")


def main() -> None:
    adapter = ADAPTER.read_text(encoding="utf-8")
    cmake = CMAKE.read_text(encoding="utf-8")
    sql = SQL.read_text(encoding="utf-8")
    build = BUILD.read_text(encoding="utf-8")
    catalog = CATALOG.read_text(encoding="utf-8")

    # Legacy may retrieve durable physicality manifests and adapt them to the
    # candidate ABI. It may not carry a second search/cognition implementation or
    # invoke lower semantic stages.
    require(
        adapter,
        "laplace_cognition_observation_request_execute_with_candidate_provider(",
        "canonical candidate-provider execution",
    )
    for forbidden in (
        "laplace_query_search_execute(",
        "laplace_cognition_forward_pass_execute(",
        "laplace_cognition_guidance_select_operation(",
        "laplace_cognition_observation_request_compile(",
    ):
        forbid(adapter, forbidden, "legacy semantic-owner call")

    # SQL is storage retrieval only. Ordered relation crossings are derived from
    # the exact expanded manifest in native code and all five canonical structural
    # families remain available.
    require(adapter, "decode_manifest_runs", "native ordered-manifest decoding")
    require(adapter, "source_logical_ordinal", "source logical ordinal")
    require(adapter, "target_logical_ordinal", "target logical ordinal")
    require(adapter, "run_length", "packed-run multiplicity")
    for relation in (
        "LAPLACE_OBSERVATION_QUERY_CONTAINER",
        "LAPLACE_OBSERVATION_QUERY_CONSTITUENT",
        "LAPLACE_OBSERVATION_QUERY_PREDECESSOR",
        "LAPLACE_OBSERVATION_QUERY_SUCCESSOR",
        "LAPLACE_OBSERVATION_QUERY_COOCCUR",
    ):
        require(adapter, relation, f"relation family {relation}")
    require(
        adapter,
        "LAPLACE_OBSERVATION_QUERY_SOURCE_PHYSICALITY",
        "explicit physicality source layer",
    )
    require(adapter, 'laplace_sql_query_text("refactor_cognition.physicalities")',
            "typed native storage query")
    require(catalog, "laplace_trajectory_expanded_constituents", "canonical manifest decoder")
    forbid(catalog, "LATERAL", "SQL-owned relation crossing")

    # The normal extension build owns provisioning. The user must not preload a
    # checkout/library or toggle a hidden mode to reach canonical cognition.
    require(cmake, "src/refactor_cognition.c", "legacy adapter source")
    require(cmake, "build-refactor-engine.sh", "automatic engine build dependency")
    require(cmake, "liblaplace_engine.so", "canonical engine link")
    require(cmake, 'INSTALL_RPATH "$ORIGIN"', "installed sibling-library lookup")
    require(
        cmake,
        'PATTERN "liblaplace_engine.so*"',
        "canonical engine installation",
    )
    for forbidden in (
        "LAPLACE_OPERATOR_TOKEN",
        "LAPLACE_REFACTOR_COGNITION_ENABLE",
        "LAPLACE_USE_REFACTOR_COGNITION",
    ):
        forbid(cmake + build + adapter + sql, forbidden, "operator-only convergence switch")

    # Dependency identity is an immutable commit, never main/latest/a branch.
    match = re.search(r'^REF_REVISION="([0-9a-f]{40})"$', build, re.MULTILINE)
    if match is None:
        raise AssertionError("REF_REVISION must be one exact 40-hex commit")
    for moving in ('REF_REVISION="main"', 'REF_REVISION="master"', "git checkout main"):
        forbid(build, moving, "moving Refactor dependency")

    # SQL is transport only: one C binding returning actual selected entity/path
    # records and engine receipts. It must not recreate cognition in PL/pgSQL.
    require(sql, "converse.refactor_cognition(", "public SQL transport")
    require(sql, "pg_laplace_refactor_cognition", "native adapter symbol")
    require(sql, "entity_id", "terminal entity realization input")
    require(sql, "forward_receipt_id", "native forward receipt")
    require(sql, "output_fingerprint", "native output fingerprint")

    print("refactor cognition convergence source contract: ok")


if __name__ == "__main__":
    main()
