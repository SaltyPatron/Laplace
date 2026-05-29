#!/bin/bash
# scripts/ingest-source.sh
# Dispatch ingestion to the right IDecomposer via Laplace.Cli + IngestRunner (ADR 0052).
# Usage: scripts/ingest-source.sh <source-name> [path]
#
# path is required only for: model <model-dir>

set -euo pipefail
source="${1:-}"
path="${2:-}"

if [[ -z "$source" ]]; then
    echo "Usage: $0 <source-name> [path]" >&2
    echo "Sources: unicode, iso639, wordnet, omw, ud, model" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Resolve the engine native libs (laplace_synthesis + its core/dynamics deps) from
# the build tree — same LD_LIBRARY_PATH discipline as the `build`/`test` recipes, so
# ingestion works in a clean shell without relying on an ambient setvars env.
export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
CLI=(dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release --)

case "$source" in
    unicode)
        cd "$ROOT/app"
        "${CLI[@]}" ingest unicode
        ;;
    iso639)
        cd "$ROOT/app"
        "${CLI[@]}" ingest iso639
        ;;
    wordnet)
        cd "$ROOT/app"
        "${CLI[@]}" ingest wordnet
        ;;
    omw)
        cd "$ROOT/app"
        "${CLI[@]}" ingest omw
        ;;
    ud)
        cd "$ROOT/app"
        "${CLI[@]}" ingest ud
        ;;
    model)
        if [[ -z "$path" ]]; then
            echo "Usage: $0 model <model-dir-path>" >&2
            exit 2
        fi
        cd "$ROOT/app"
        "${CLI[@]}" ingest model "$path"
        ;;
    wiktionary|tatoeba|conceptnet|atomic2020|text-corpus)
        echo "Source '$source' has no decomposer implementation yet." >&2
        exit 1
        ;;
    *)
        echo "Unknown source: $source" >&2
        exit 2
        ;;
esac
