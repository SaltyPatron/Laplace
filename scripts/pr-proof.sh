#!/usr/bin/env bash
# Same-repository pull-request proof.  The live install, services, canonical
# database, migrations and publication remain untouched.  In addition to the
# source/build DEV/BAT gates, the exact branch-native PostgreSQL extensions run
# their registered regression suite in a run-scoped throwaway database.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# Use a run-scoped database name at CMake configure time so concurrent/retried PR
# proofs cannot share a regression database.  Local invocation gets a process-
# scoped fallback and is still forbidden from naming the canonical database.
run_id="${GITHUB_RUN_ID:-$$}"
run_attempt="${GITHUB_RUN_ATTEMPT:-1}"
export LAPLACE_REGRESS_DB="${LAPLACE_REGRESS_DB:-laplace_pr_${run_id}_${run_attempt}}"

# Test selection belongs to the executable profile registry. Policy and DEV/BAT
# therefore use the same authority as main; UCI/browser coverage is part of DEV/BAT
# and must not be repeated here with independent commands.
python3 scripts/check-sql-manifest-dependencies.py
bash scripts/test-parallel.sh --policy

bash scripts/pipeline.sh build
bash scripts/test-parallel.sh --engine --all

# Compile success is not database/product execution.  Exercise the exact branch
# extension SQL and native modules against PostgreSQL before this revision can be
# represented as a valid merge candidate.
bash scripts/pr-db-proof.sh

echo "PR_PROOF_OK policy=green build=green dev_bat=green native_db=green production_mutations=0"
