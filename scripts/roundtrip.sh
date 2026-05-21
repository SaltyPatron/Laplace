#!/bin/bash
# scripts/roundtrip.sh
# End-to-end round-trip: ingest model → synthesize matching architecture → load in llama.cpp.
# Real implementation lands in Chunk 8 (Round-trip + chat verification).

set -e
model_path="${1:-}"

if [ -z "$model_path" ]; then
    echo "Usage: $0 <model-path>" >&2
    exit 2
fi

echo "Round-trip not yet implemented (lands in Chunk 8: Round-trip + chat verification)" >&2
echo "Will: just ingest model $model_path && just synthesize ... && llama-cli -m ..." >&2
exit 1
