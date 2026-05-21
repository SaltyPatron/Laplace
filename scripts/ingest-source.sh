#!/bin/bash
# scripts/ingest-source.sh
# Dispatch ingestion to the right ISource plugin.
# Usage: scripts/ingest-source.sh <source-name> [path]
# Real source plugins land in Chunks 4–6.

set -e
source="${1:-}"
path="${2:-}"

if [ -z "$source" ]; then
    echo "Usage: $0 <source-name> [path]" >&2
    echo "Available sources (planned): wordnet, ud, wiktionary, tatoeba, conceptnet, atomic2020, text-corpus, model" >&2
    exit 2
fi

case "$source" in
    wordnet|ud|wiktionary|tatoeba|conceptnet|atomic2020)
        echo "Source '$source' plugin not yet implemented (lands in Chunk 4–5: linguistic sources)" >&2
        exit 1
        ;;
    text-corpus)
        echo "TextCorpusSource not yet implemented (lands in Chunk 5+ alongside ConceptNet)" >&2
        exit 1
        ;;
    model)
        echo "TransformerModelSource not yet implemented (lands in Chunk 6: probe + Procrustes pipeline)" >&2
        exit 1
        ;;
    *)
        echo "Unknown source: $source" >&2
        exit 2
        ;;
esac
