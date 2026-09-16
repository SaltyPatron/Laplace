#!/bin/bash
set -euo pipefail

source="${1:-}"
path="${2:-}"
DATA_ROOT="${LAPLACE_DATA_ROOT:-/vault/Data}"

FLOOR=(unicode iso639 operational cili)
KNOWLEDGE=(wordnet omw verbnet propbank framenet mapnet wordframenet semlink conceptnet atomic2020 ud wiktionary)
USAGE=(tatoeba opensubtitles)

if [[ -z "$source" ]]; then
    echo "Usage: $0 <source> [path] | all | safetensors <snapshot-dir>" >&2
    echo "Sources: ${FLOOR[*]} document ${KNOWLEDGE[*]} ${USAGE[*]} \\" >&2
    echo "         code repo stack tiny-codes tabular recipe agents chess openings chess-books chess-eval chess-move-outcomes chess-tactic-outcomes chess-transitions safetensors" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init
LOGDIR="${INGEST_LOGDIR:-$TMPDIR/laplace-ingest}"
LOGDIR="$(realpath -m -- "$LOGDIR")"
case "$LOGDIR" in
    /tmp|/tmp/*|/var/tmp|/var/tmp/*|/dev/shm|/dev/shm/*)
        echo "Ingest logs require permanent storage: $LOGDIR" >&2; exit 2 ;;
esac
mkdir -p -- "$LOGDIR"
export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"

if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
    DLL="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Cli/Release/net10.0/Laplace.Cli.dll"
    CLI_NATIVE="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Cli/Release/net10.0/liblaplace_core.so"
else
    DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"
    CLI_NATIVE="$ROOT/app/Laplace.Cli/bin/Release/net10.0/liblaplace_core.so"
fi

if [[ -n "${GITHUB_ACTIONS:-}${CI:-}" && -z "${LAPLACE_INGEST_CONSOLE:-}" ]]; then
    export LAPLACE_INGEST_CONSOLE=ci
fi

require_cli() {
    [[ -f "$DLL" ]] || {
        echo "::error::ingest runtime is not built: $DLL" >&2
        echo "::error::build/deploy owns compilation; run the product build before ingest" >&2
        return 1
    }
    [[ -f "$ROOT/build/engine/core/liblaplace_core.so" && -f "$CLI_NATIVE" ]] || {
        echo "::error::ingest native runtime is incomplete" >&2
        return 1
    }
    cmp -s "$ROOT/build/engine/core/liblaplace_core.so" "$CLI_NATIVE" || {
        echo "::error::ingest CLI native closure differs from the prepared engine build" >&2
        echo "::error::rebuild the product; ingest never repairs or compiles runtime artifacts" >&2
        return 1
    }
}

ingest() {
    local t0=$SECONDS rc=0 t0_epoch preempted=0
    local -a ingest_args=("$@")
    [[ "${LAPLACE_INGEST_FORCE:-0}" != 1 ]] || ingest_args+=(--force)
    t0_epoch=$(date +%s)
    local detail="$LOGDIR/laplace-ingest-${source}.log"
    if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
        ( cd "$ROOT/app" && dotnet "$DLL" ingest "${ingest_args[@]}" ) >"$detail" 2>&1 || rc=$?
        if [[ "$rc" -ne 0 ]]; then
            if [[ "$(bash "$ROOT/scripts/classify-ingest-exit.sh" "$detail" "$t0_epoch")" == preempted ]]; then
                preempted=1
                echo "::error::ingest ${source} was interrupted because the database disappeared mid-run (rc=$rc)"
                tail -20 "$detail" >&2 || true
            else
                echo "::error::ingest ${source} failed rc=$rc — last 80 lines of $detail"
                tail -80 "$detail" >&2 || true
            fi
        fi
    else
        ( cd "$ROOT/app" && dotnet "$DLL" ingest "${ingest_args[@]}" ) || rc=$?
    fi
    local elapsed=$((SECONDS - t0))
    echo "INGEST_TIMING ${TIMING_LABEL:-source=$source} elapsed_s=$elapsed rc=$rc"
    if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
        printf 'INGEST_TIMING %s elapsed_s=%s rc=%s\n' "${TIMING_LABEL:-source=$source}" "$elapsed" "$rc" >> "$detail" || {
            echo "::error::cannot persist ingest timing to $detail" >&2
            [[ "$rc" -ne 0 ]] || rc=1
        }
    fi
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
        printf 'elapsed_s=%s\npreempted=%s\n' "$elapsed" \
            "$([[ "$preempted" -eq 1 ]] && echo true || echo false)" >> "$GITHUB_OUTPUT" || {
            echo "::error::cannot persist ingest outputs to $GITHUB_OUTPUT" >&2
            [[ "$rc" -ne 0 ]] || rc=1
        }
    fi
    return "$rc"
}

case "$source" in
    all)
        require_cli
        STAGES=( "${FLOOR[@]}" document "${KNOWLEDGE[@]}" "${USAGE[@]}" )
        from="${INGEST_FROM:-}"
        skip=0; [[ -n "$from" ]] && skip=1
        for src in "${STAGES[@]}"; do
            if [[ "$skip" == 1 ]]; then
                if [[ "$src" == "$from" ]]; then skip=0; else echo ">>> skip $src (before INGEST_FROM=$from)"; continue; fi
            fi
            echo ">>> stage $src — start $(date -u +%H:%M:%S)"
            t0=$SECONDS
            if [[ "$src" == document ]]; then
                doc_path="${INGEST_DOCUMENT_PATH:-$DATA_ROOT/test-data/text}"
                ingest "$src" "$doc_path" 2>&1 | tee "$LOGDIR/laplace-ingest-$src.log"
            else
                ingest "$src" 2>&1 | tee "$LOGDIR/laplace-ingest-$src.log"
            fi
            echo ">>> stage $src — done in $((SECONDS - t0))s"
        done
        ;;
    chain)
        require_cli
        shift
        [[ $# -gt 0 ]] || { echo "Usage: $0 chain \"<source [path]>\" ..." >&2; exit 2; }
        source=chain
        TIMING_LABEL="chain_sources=$#"
        ingest chain "$@"
        ;;
    safetensors|model)
        [[ -n "$path" ]] || { echo "Usage: $0 safetensors <snapshot-dir>" >&2; exit 2; }
        require_cli
        ingest safetensors "$path"
        ;;
    unicode|iso639|operational|cili|document|omw|wordnet|ud|tatoeba|atomic2020|conceptnet|wiktionary|opensubtitles|verbnet|propbank|framenet|mapnet|wordframenet|semlink|stack|tiny-codes|rgba-image|track-audio|frame-video)
        require_cli
        if [[ "$source" == document && -z "$path" ]]; then
            path="${INGEST_DOCUMENT_PATH:-$DATA_ROOT/test-data/text}"
        fi
        if [[ -n "$path" ]]; then ingest "$source" "$path"; else ingest "$source"; fi
        ;;
    agents)
        require_cli
        if [[ -n "$path" ]]; then ingest agents "$path"; else ingest agents; fi
        ;;
    code|repo|tabular|recipe)
        require_cli
        [[ -n "$path" ]] || { echo "Usage: $0 $source <file-or-directory>" >&2; exit 2; }
        ingest "$source" "$path"
        ;;
    chess|openings|chess-books)
        require_cli
        [[ -n "$path" ]] || { echo "Usage: $0 $source <corpus-dir>" >&2; exit 2; }
        ingest "$source" "$path"
        ;;
    chess-move-outcomes|chess-tactic-outcomes|chess-eval|chess-analyze|chess-transitions|chess-trajectory|chess-opening-match)
        require_cli
        ingest "$source"
        ;;
    chess-syzygy)
        require_cli
        if [[ -n "$path" ]]; then ingest chess-syzygy "$path"; else ingest chess-syzygy; fi
        ;;
    *)
        echo "Unknown source: $source" >&2
        echo "Sources: ${FLOOR[*]} document ${KNOWLEDGE[*]} ${USAGE[*]} \\" >&2
        echo "         chess openings chess-books chess-analyze chess-transitions chess-trajectory chess-eval chess-syzygy \\" >&2
        echo "         chess-opening-match chess-move-outcomes chess-tactic-outcomes \\" >&2
        echo "         code repo stack tiny-codes tabular recipe agents all safetensors" >&2
        exit 2
        ;;
esac
