#!/usr/bin/env bash
# DEPRECATED — isolated tests use fresh db-isolate + prerequisite ingests, not pg clone.
set -euo pipefail
echo "ERROR: decomposer-clone.sh is deprecated." >&2
echo "  Use decomposer-test.sh <source> — fresh laplace_d_<source> + prerequisite ingests." >&2
echo "  Promote with decomposer-promote.sh <source> (re-ingest into laplace)." >&2
exit 2
