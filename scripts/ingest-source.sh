#!/bin/bash
# scripts/ingest-source.sh
# Dispatch ingestion to the right IDecomposer via Laplace.Cli + IngestRunner (ADR 0052).
#
# Usage:
#   scripts/ingest-source.sh <source>            # one source
#   scripts/ingest-source.sh all                 # full ladder; layer-2 sources run in parallel
#   scripts/ingest-source.sh model <model-dir>   # model (path required)
#
# Dependency layers (ADR 0037 — each source's IngestRunner refuses to start until every lower
# layer has a HasLayerCompleted marker):
#   0 unicode → 1 iso639 → 2 { wordnet ud tatoeba atomic2020 conceptnet wiktionary } → 3 omw
# `all` runs the stages STRICTLY ONE AT A TIME, in this order — NEVER in parallel. A single
# source gets the whole machine and finishes as fast as possible; running several at once only
# splits CPU + DB and makes every stage slower. Each stage is idempotent + checkpoint-resumable,
# so a re-run short-circuits completed work; INGEST_FROM=<source> resumes the ladder at a stage.

set -euo pipefail

source="${1:-}"
path="${2:-}"
LOGDIR="${INGEST_LOGDIR:-/tmp}"

# Layer-2 corpora (each needs only unicode+iso639); unicode/iso639 are the prerequisites and
# omw (layer 3) runs after wordnet. The `all` ladder walks these ONE AT A TIME — see below.
LAYER2=(wordnet ud tatoeba atomic2020 conceptnet wiktionary)

if [[ -z "$source" ]]; then
    echo "Usage: $0 <source> [path] | all | model <model-dir>" >&2
    echo "Sources: unicode iso639 ${LAYER2[*]} omw model" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# Engine native libs (laplace_synthesis + core/dynamics) from the build tree — same
# LD_LIBRARY_PATH discipline as the build/test recipes.
export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"

# Build once up front so parallel runs share one fresh binary — concurrent `dotnet run` would
# race the incremental build. Every run then invokes the prebuilt DLL.
build_cli() { ( cd "$ROOT/app" && dotnet build Laplace.Cli/Laplace.Cli.csproj -c Release -v q -clp:NoSummary >/dev/null ); }
ingest()    { ( cd "$ROOT/app" && dotnet "$DLL" ingest "$@" ); }

case "$source" in
    all)
        build_cli
        # STRICTLY ONE AT A TIME, in dependency order (ADR 0037). Each stage runs to completion
        # before the next starts — never in parallel. Fail-fast (set -e aborts the ladder on the
        # first stage that errors). Each stage is idempotent + checkpoint-resumable, so a re-run
        # short-circuits finished work; INGEST_FROM=<source> resumes the ladder at that stage.
        STAGES=(unicode iso639 "${LAYER2[@]}" omw)
        from="${INGEST_FROM:-}"
        skip=0; [[ -n "$from" ]] && skip=1
        for src in "${STAGES[@]}"; do
            if [[ "$skip" == 1 ]]; then
                if [[ "$src" == "$from" ]]; then skip=0; else echo ">>> skip $src (before INGEST_FROM=$from)"; continue; fi
            fi
            echo ">>> stage $src — start $(date -u +%H:%M:%S)"
            t0=$SECONDS
            # tee to a per-stage log; pipefail (set above) propagates the ingest exit, not tee's.
            ingest "$src" 2>&1 | tee "$LOGDIR/laplace-ingest-$src.log"
            echo ">>> stage $src — done in $((SECONDS - t0))s"
        done
        ;;
    model)
        [[ -n "$path" ]] || { echo "Usage: $0 model <model-dir-path>" >&2; exit 2; }
        build_cli
        ingest model "$path"
        ;;
    unicode|iso639|omw|wordnet|ud|tatoeba|atomic2020|conceptnet|wiktionary)
        build_cli
        ingest "$source"
        ;;
    *)
        echo "Unknown source: $source" >&2
        echo "Sources: unicode iso639 ${LAYER2[*]} omw all model" >&2
        exit 2
        ;;
esac
