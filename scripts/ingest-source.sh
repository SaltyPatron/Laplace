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
    echo "         code repo stack tiny-codes tabular recipe chess openings chess-books chess-eval safetensors" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"

# Durable progress lives in laplace.ingest_run_journal (+ ops CSV). Actions is not
# a log warehouse — default CI/console to quiet unless the operator overrides.
if [[ -n "${GITHUB_ACTIONS:-}${CI:-}" && -z "${LAPLACE_INGEST_CONSOLE:-}" ]]; then
    export LAPLACE_INGEST_CONSOLE=ci
fi
# Detail log on disk when CI (journal still validates); keep job log short.
if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
    LOGDIR="${INGEST_LOGDIR:-${RUNNER_TEMP:-/tmp}/laplace-ingest}"
    mkdir -p "$LOGDIR"
fi

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
# Every branch below routes through here, so timing is recorded once for all of them.
# Only the `all` path used to print any timing at all; the single-source path -- the one
# _ingest.yml and ensure-foundation.sh actually call -- printed none, so no seed run in CI
# history has a recorded duration. A timeout is a ceiling, not a measurement.
# INGEST_TIMING is machine-readable on purpose: it is what a throughput baseline parses.
ingest() {
    local t0=$SECONDS rc=0
    local detail="${LOGDIR:-}/laplace-ingest-${source}.log"
    if [[ -n "${GITHUB_ACTIONS:-}" && -n "${LOGDIR:-}" ]]; then
        # Job log: timing + journal. Full stderr → file on the runner.
        ( cd "$ROOT/app" && dotnet "$DLL" ingest "$@" ) >"$detail" 2>&1 || rc=$?
        if [[ "$rc" -ne 0 ]]; then
            echo "::error::ingest ${source} failed rc=${rc} — last 80 lines of ${detail}"
            tail -80 "$detail" >&2 || true
        fi
    else
        ( cd "$ROOT/app" && dotnet "$DLL" ingest "$@" ) || rc=$?
    fi
    local elapsed=$((SECONDS - t0))
    echo "INGEST_TIMING source=$source elapsed_s=$elapsed rc=$rc"
    if [[ -n "${GITHUB_ACTIONS:-}" && -n "${LOGDIR:-}" ]]; then
        # The throughput gate parses the detail log; under LAPLACE_INGEST_CONSOLE=ci
        # nothing else machine-readable lands there.
        echo "INGEST_TIMING source=$source elapsed_s=$elapsed rc=$rc" >> "$detail"
    fi
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        echo "elapsed_s=$elapsed" >> "$GITHUB_OUTPUT"
    fi
    if [[ "$rc" -eq 0 ]]; then
        # Pass/fail is the journal row when this source is in decomposer-gates.json.
        if python3 -c "import json,sys; json.load(open('${ROOT}/scripts/decomposer-gates.json'))['sources'].get(sys.argv[1]) or sys.exit(1)" "$source" 2>/dev/null; then
            bash "$ROOT/scripts/verify-ingest-journal.sh" --cli-key "$source" || return 1
        fi
    fi
    return "$rc"
}

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
    unicode|iso639|cili|document|omw|wordnet|ud|tatoeba|atomic2020|conceptnet|wiktionary|opensubtitles|verbnet|propbank|framenet|mapnet|wordframenet|semlink|stack|tiny-codes|rgba-image|track-audio|frame-video)
        # Default-path sources: IngestDataPaths resolves a DATA_ROOT-relative default
        # when no <path> is given (stack=stack-v2, tiny-codes=tiny-codes, document=text…).
        # An explicit <path> (single file, bare dir, or ecosystem root) always wins via
        # IngestInput.ResolveFiles — `ingest ud <one.conllu>` validates in seconds.
        # Media lanes (generic): rgba-image, track-audio, frame-video — not corpus keys.
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
    chess-eval)
        # Stockfish eval pass over recorded games (calculated layer, GH #573). No path —
        # the substrate is the source. Part of the seed ladder so a db-reset + reseed
        # re-derives the census like every other calculated layer; per-game markers make
        # re-runs skip-complete. Needs a stockfish binary (env, chess-lab bootstrap, PATH).
        build_cli
        if [[ -z "${LAPLACE_STOCKFISH:-}" && -x /usr/games/stockfish ]]; then
            export LAPLACE_STOCKFISH=/usr/games/stockfish
        fi
        ingest chess-eval
        ;;
    chess-syzygy)
        # Tablebase packaging dir → position-grain WDL/DTZ records (Fathom = unpack codec).
        # Path optional: falls back to LAPLACE_SYZYGY / data-root Games/Chess/syzygy/….
        build_cli
        if [[ -n "$path" ]]; then
            ingest chess-syzygy "$path"
        else
            ingest chess-syzygy
        fi
        ;;
    chess-analyze|chess-trajectory|chess-opening-match)
        # Substrate-sourced calculated passes: no path, marker-gated, safe to re-run.
        build_cli
        ingest "$source"
        ;;
    *)
        echo "Unknown source: $source" >&2
        echo "Sources: ${FLOOR[*]} document ${KNOWLEDGE[*]} ${USAGE[*]} \\" >&2
        echo "         chess openings chess-books chess-analyze chess-trajectory chess-eval chess-syzygy \\" >&2
    echo "         chess-opening-match \\" >&2
        echo "         code repo stack tiny-codes tabular recipe all safetensors" >&2
        exit 2
        ;;
esac
