#!/bin/bash

set -euo pipefail

source="${1:-}"
path="${2:-}"
LOGDIR="${INGEST_LOGDIR:-/tmp}"
DATA_ROOT="${LAPLACE_DATA_ROOT:-/vault/Data}"

FLOOR=(unicode iso639 cili)
KNOWLEDGE=(wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary)
USAGE=(tatoeba opensubtitles)

if [[ -z "$source" ]]; then
    echo "Usage: $0 <source> [path] | all | safetensors <snapshot-dir>" >&2
    echo "Sources: ${FLOOR[*]} document ${KNOWLEDGE[*]} ${USAGE[*]} \\" >&2
    echo "         code repo stack tiny-codes tabular recipe chess openings chess-books safetensors" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"

# Content-fingerprint gate for the CLI build (scripts/lib/fp.sh, stamp cli-build):
# ensure-foundation's 10-rung ladder invokes this script once per rung, which was
# up to 10 identical `dotnet build`s per foundation run. Skip only when app/
# content is unchanged since the last SUCCESSFUL build AND the DLL actually
# exists — stamps attest sources, artifacts must be checked too.
# shellcheck source=scripts/lib/fp.sh
source "$ROOT/scripts/lib/fp.sh"

build_cli() {
    local fp
    fp=$(fp_compute app)
    if fp_check cli-build "$fp" && [[ -f "$DLL" ]]; then
        echo ">>> CLI build skipped — app/ unchanged since last successful build (fp ${fp:0:12})"
        return 0
    fi
    ( cd "$ROOT/app" && dotnet build Laplace.Cli/Laplace.Cli.csproj -c Release -v q -clp:NoSummary >/dev/null )
    fp_record cli-build "$fp"
}
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
    unicode|iso639|cili|document|omw|wordnet|ud|tatoeba|atomic2020|conceptnet|wiktionary|opensubtitles|verbnet|propbank|framenet|mapnet|wordframenet|semlink|stack|tiny-codes)
        # Default-path sources: IngestDataPaths resolves a DATA_ROOT-relative default
        # when no <path> is given (stack=stack-v2, tiny-codes=tiny-codes, document=text…).
        # An explicit <path> (single file, bare dir, or ecosystem root) always wins via
        # IngestInput.ResolveFiles — `ingest ud <one.conllu>` validates in seconds.
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
    code|repo|tabular|recipe)
        # Witness-unit code/data sources: the <path> IS the witness boundary (a file,
        # a repository root, a table), so it is REQUIRED — no DATA_ROOT default. Same
        # table-driven CLI dispatch as everything else (IngestCodeAsync / IngestRepoAsync
        # / IngestTabularAsync / IngestRecipeAsync).
        build_cli
        [[ -n "$path" ]] || { echo "Usage: $0 $source <file-or-directory>" >&2; exit 2; }
        ingest "$source" "$path"
        ;;
    chess|openings|chess-books)
        # Chess corpora are plain .NET decomposers (ChessPgn / ChessOpenings / ChessBook)
        # like every other source — cross-platform, not a Windows-only thing. They just
        # take an explicit corpus dir (no fixed default under DATA_ROOT).
        build_cli
        [[ -n "$path" ]] || { echo "Usage: $0 $source <corpus-dir>" >&2; exit 2; }
        ingest "$source" "$path"
        ;;
    *)
        echo "Unknown source: $source" >&2
        echo "Sources: ${FLOOR[*]} document ${KNOWLEDGE[*]} ${USAGE[*]} chess openings all safetensors" >&2
        exit 2
        ;;
esac
