#!/bin/bash
# scripts/ingest-source.sh
# Dispatch ingestion to the right IDecomposer via Laplace.Cli + IngestRunner (ADR 0052).
# Usage: scripts/ingest-source.sh <source-name> [path]
#
# path is reserved for sources that read from a filesystem root (wordnet, model, …).

set -euo pipefail
source="${1:-}"
path="${2:-}"

if [[ -z "$source" ]]; then
    echo "Usage: $0 <source-name> [path]" >&2
    echo "Implemented: unicode" >&2
    echo "Planned: wordnet, ud, wiktionary, tatoeba, conceptnet, atomic2020, text-corpus, model" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

case "$source" in
    unicode)
        cd "$ROOT/app"
        dotnet run --project Laplace.Cli/Laplace.Cli.csproj -c Release -- ingest unicode
        ;;
    wordnet|ud|wiktionary|tatoeba|conceptnet|atomic2020)
        echo "Source '$source' plugin not yet implemented." >&2
        exit 1
        ;;
    text-corpus)
        echo "TextCorpusSource not yet implemented." >&2
        exit 1
        ;;
    model)
        echo "TransformerModelSource not yet implemented." >&2
        if [[ -n "$path" ]]; then
            echo "  (model path argument ignored until implemented: $path)" >&2
        fi
        exit 1
        ;;
    *)
        echo "Unknown source: $source" >&2
        exit 2
        ;;
esac
