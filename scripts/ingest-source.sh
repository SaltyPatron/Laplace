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
# The layer-2 corpora are independent (each needs only unicode+iso), so `all` runs them with
# bounded concurrency (INGEST_JOBS, default 3); omw runs last because it needs WordNet synsets.
# Concurrency is safe + fast because ContentEmitter skips the universally-seeded T0 codepoints,
# so parallel writers don't contend on them.

set -euo pipefail

source="${1:-}"
path="${2:-}"
JOBS="${INGEST_JOBS:-3}"
LOGDIR="${INGEST_LOGDIR:-/tmp}"

# Layer-2 independent corpora (parallelizable); unicode/iso639 are serial prerequisites and
# omw (layer 3) runs after wordnet.
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
        # Serial prerequisites (fast; idempotent skip if already done).
        ingest unicode
        ingest iso639
        # Independent layer-2 group — bounded-concurrency parallel. A failure in one does not
        # stop the others (xargs continues); each source's exit code is echoed.
        printf '%s\n' "${LAYER2[@]}" \
          | xargs -P "$JOBS" -I{} bash -c \
              'cd "$0/app" && dotnet "$1" ingest "$2" > "$3/laplace-ingest-$2.log" 2>&1; echo "$(date -u +%H:%M:%S) $2 exit=$?"' \
              "$ROOT" "$DLL" {} "$LOGDIR"
        # omw needs WordNet synsets → runs after the layer-2 group (which includes wordnet).
        ingest omw
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
