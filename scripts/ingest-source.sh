#!/bin/bash

set -euo pipefail

source="${1:-}"
path="${2:-}"
LOGDIR="${INGEST_LOGDIR:-/tmp}"
DATA_ROOT="${LAPLACE_DATA_ROOT:-/vault/Data}"

# Canonical order from scripts/win/witness-manifest.json (do not reorder ad hoc).
FLOOR=(unicode iso639)
KNOWLEDGE=(wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary)
USAGE=(tatoeba opensubtitles)

if [[ -z "$source" ]]; then
    echo "Usage: $0 <source> [path] | all | safetensors <snapshot-dir>" >&2
    echo "Sources: ${FLOOR[*]} document ${KNOWLEDGE[*]} ${USAGE[*]} safetensors" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"

build_cli() { ( cd "$ROOT/app" && dotnet build Laplace.Cli/Laplace.Cli.csproj -c Release -v q -clp:NoSummary >/dev/null ); }
ingest()    { ( cd "$ROOT/app" && dotnet "$DLL" ingest "$@" ); }

case "$source" in
    all)
        build_cli
        STAGES=( "${FLOOR[@]}" document "${KNOWLEDGE[@]}" "${USAGE[@]}" )
        from="${INGEST_FROM:-}"
        skip=0; [[ -n "$from" ]] && skip=1
        for src in "${STAGES[@]}"; do
            if [[ "$skip" == 1 ]]; then
                if [[ "$src" == "$from" ]]; then skip=0; else echo ">>> skip $src (before INGEST_FROM=$from)"; continue; fi
            fi
            echo ">>> stage $src — start $(date -u +%H:%M:%S)"
            t0=$SECONDS
            if [[ "$src" == "document" ]]; then
                doc_path="${INGEST_DOCUMENT_PATH:-$DATA_ROOT/test-data/text}"
                ingest "$src" "$doc_path" 2>&1 | tee "$LOGDIR/laplace-ingest-$src.log"
            else
                ingest "$src" 2>&1 | tee "$LOGDIR/laplace-ingest-$src.log"
            fi
            echo ">>> stage $src — done in $((SECONDS - t0))s"
        done
        ;;
    safetensors|model)
        [[ -n "$path" ]] || { echo "Usage: $0 safetensors <snapshot-dir>" >&2; exit 2; }
        build_cli
        ingest safetensors "$path"
        ;;
    unicode|iso639|document|omw|wordnet|ud|tatoeba|atomic2020|conceptnet|wiktionary|opensubtitles|verbnet|propbank|framenet|mapnet|wordframenet|semlink)
        build_cli
        if [[ "$source" == "document" && -z "$path" ]]; then
            path="${INGEST_DOCUMENT_PATH:-$DATA_ROOT/test-data/text}"
        fi
        if [[ -n "$path" ]]; then
            ingest "$source" "$path"
        else
            ingest "$source"
        fi
        ;;
    *)
        echo "Unknown source: $source" >&2
        echo "Sources: ${FLOOR[*]} document ${KNOWLEDGE[*]} ${USAGE[*]} all safetensors" >&2
        exit 2
        ;;
esac
