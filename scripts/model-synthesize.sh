#!/usr/bin/env bash
# v0.1 proof: model → substrate → synthesize (re-export = fill the mold) → GGUF.
# llama.cpp behavioral chat is not in this script yet.
set -euo pipefail
exec "$(cd "$(dirname "$0")" && pwd)/model-synthesize-ci.sh" "${1:-}"
