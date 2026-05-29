#!/usr/bin/env bash
# v0.1 proof (ADR 0037 / #8): model → substrate → synthesize → GGUF.
# llama.cpp behavioral chat is not in this script yet.
set -euo pipefail
exec "$(cd "$(dirname "$0")" && pwd)/model-roundtrip-ci.sh" "${1:-}"
