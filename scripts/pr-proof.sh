#!/usr/bin/env bash
# Same-repository pull-request proof. The live install, services, canonical
# database, migrations and publication remain untouched. The exact branch-native
# PostgreSQL extensions execute before the broader DEV/BAT matrix so a broken
# database/product build fails fast instead of burning minutes on unrelated tests.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Use a run-scoped database name at CMake configure time so concurrent/retried PR
# proofs cannot share a regression database. Local invocation gets a process-
# scoped fallback and is still forbidden from naming the canonical database.
run_id="${GITHUB_RUN_ID:-$$}"
run_attempt="${GITHUB_RUN_ATTEMPT:-1}"
export LAPLACE_REGRESS_DB="${LAPLACE_REGRESS_DB:-laplace_pr_${run_id}_${run_attempt}}"

python3 scripts/check-sql-manifest-dependencies.py
bash scripts/test-parallel.sh --policy
bash scripts/pipeline.sh build

# Compile success is not database/product execution. Exercise the exact branch
# extension SQL and native modules immediately after build; this is the shortest
# path to a real failure signal for the cognition work.
bash scripts/pr-db-proof.sh

# Only a database-valid branch spends time on the full DEV/BAT matrix.
bash scripts/test-parallel.sh --engine --all

echo "PR_PROOF_OK policy=green build=green native_db=green dev_bat=green production_mutations=0"
