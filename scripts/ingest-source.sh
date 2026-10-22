#!/bin/bash
set -euo pipefail

source="${1:-}"
path="${2:-}"
corpus(){ printf '%s' "${LAPLACE_DATA:?the corpus directory is LAPLACE_DATA (the runner environment: Laplace-Operations agents.sh; /etc/laplace/machine.env)}"; }    # asked only where a path under the corpus is needed

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# The canonical estate may be explicitly declared, published beside the CLI,
# installed as the Native SDK, or present as the workspace Engine checkout.
if [[ -n "${LAPLACE_RECIPES:-}" ]]; then
    RECIPE_ROOT="$LAPLACE_RECIPES"
else
    prefix="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
    packaged="${LAPLACE_INGEST_RUNTIME:-${LAPLACE_APP_DIR:-$prefix/app}/cli}/native/recipe-engine/share/laplace/recipes"
    if [[ -f "$packaged/order" ]]; then RECIPE_ROOT="$packaged"
    elif [[ -f "$prefix/share/laplace/recipes/order" ]]; then RECIPE_ROOT="$prefix/share/laplace/recipes"
    else RECIPE_ROOT="${LAPLACE_SRC:-$ROOT/..}/Laplace-Engine/recipes"
    fi
fi
[[ -f "$RECIPE_ROOT/order" ]] || { echo "Native recipe estate has no order declaration: $RECIPE_ROOT" >&2; exit 2; }
export LAPLACE_RECIPES="$RECIPE_ROOT"
catalog_names="$(python3 "$ROOT/scripts/ingest_catalog.py" --format names)"
mapfile -t SOURCE_ORDER <<< "$catalog_names"
if [[ "$source" == --list-sources ]]; then printf '%s\n' "${SOURCE_ORDER[@]}"; exit 0; fi
if [[ "$source" == --catalog ]]; then exec python3 "$ROOT/scripts/ingest_catalog.py"; fi
# Normalize once for dispatch, completion and log paths; aliases remain accepted.
if [[ -n "$source" ]]; then source="$(python3 "$ROOT/scripts/ingest_catalog.py" --resolve "$source")"; fi
LADDER=" ${SOURCE_ORDER[*]} "
# Sources outside the ladder; each has its own route below.
OTHER_SOURCES="chess chess-books chess-analyze chess-transitions chess-trajectory chess-eval chess-syzygy chess-opening-match chess-move-outcomes chess-tactic-outcomes chess-position-outcomes chess-player-context-outcomes code repo stack tiny-codes tabular recipe agents rgba-image track-audio frame-video all chain safetensors"

usage_sources() {
    echo "Sources: ${SOURCE_ORDER[*]}" >&2
    echo "         $OTHER_SOURCES" >&2
}

if [[ -z "$source" ]]; then
    echo "Usage: $0 <source> [path] | all | safetensors <snapshot-dir>" >&2
    usage_sources
    exit 2
fi

source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init
LOGDIR="${INGEST_LOGDIR:-$TMPDIR/laplace-ingest}"
LOGDIR="$(realpath -m -- "$LOGDIR")"
case "$LOGDIR" in
    /tmp|/tmp/*|/var/tmp|/var/tmp/*|/dev/shm|/dev/shm/*)
        echo "Ingest logs require permanent storage: $LOGDIR" >&2; exit 2 ;;
esac
mkdir -p -- "$LOGDIR"
laplace_share_directory "$LOGDIR"

PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
INGEST_ROOT="$PREFIX/ingest"
if [[ -n "${LAPLACE_INGEST_RUNTIME:-}" ]]; then
    INGEST_RUNTIME="$LAPLACE_INGEST_RUNTIME"
elif [[ -d "${LAPLACE_APP_DIR:-$PREFIX/app}/cli" ]]; then
    INGEST_RUNTIME="${LAPLACE_APP_DIR:-$PREFIX/app}/cli"
elif [[ -f "$INGEST_ROOT/current/Laplace.Cli.dll" && -f "$INGEST_ROOT/current/liblaplace_core.so" ]]; then
    INGEST_RUNTIME="$INGEST_ROOT/current"
else
    # Compatibility only for a host that has not completed one versioned
    # publication yet. New installs never overwrite this legacy flat payload.
    INGEST_RUNTIME="$INGEST_ROOT"
fi
if [[ "$INGEST_RUNTIME" == "${LAPLACE_APP_DIR:-$PREFIX/app}/cli" ]]; then
    # Site publication replaces this package under the host deployment lock.
    ingest_host_lock="${LAPLACE_HOST_LOCK:-${LAPLACE_LOCKS:-/run/lock/laplace}/host-resource.lock}"
    if ! [[ /proc/self/fd/8 -ef "$ingest_host_lock" ]]; then
        exec {ingest_host_lease}>>"$ingest_host_lock"
        flock -s "$ingest_host_lease"
    fi
fi
if [[ -n "${LAPLACE_INGEST_RUNTIME:-}" || -d "${LAPLACE_APP_DIR:-$PREFIX/app}/cli" || -f "$INGEST_RUNTIME/Laplace.Cli.dll" ]]; then
    DLL="$INGEST_RUNTIME/Laplace.Cli.dll"
    CLI_NATIVE="$INGEST_RUNTIME/liblaplace_core.so"
    ENGINE_NATIVE="$PREFIX/lib/liblaplace_core.so"
    export LD_LIBRARY_PATH="$INGEST_RUNTIME:$PREFIX/lib:${LD_LIBRARY_PATH:-}"
    if [[ -f "$INGEST_RUNTIME/.runtime-lease" ]]; then
        exec {ingest_runtime_lease}<"$INGEST_RUNTIME/.runtime-lease"
        flock -s "$ingest_runtime_lease"
    fi
    INGEST_USES_PREFIX=1
else
    INGEST_USES_PREFIX=0
    export LD_LIBRARY_PATH="$ROOT/build/engine/synthesis:$ROOT/build/engine/core:$ROOT/build/engine/dynamics:${LD_LIBRARY_PATH:-}"
    if [[ -n "${LAPLACE_BUILD_ROOT:-}" ]]; then
        DLL="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Cli/Release/net10.0/Laplace.Cli.dll"
        CLI_NATIVE="$LAPLACE_BUILD_ROOT/app/bin/Laplace.Cli/Release/net10.0/liblaplace_core.so"
        ENGINE_NATIVE="$ROOT/build/engine/core/liblaplace_core.so"
    else
        DLL="$ROOT/app/Laplace.Cli/bin/Release/net10.0/Laplace.Cli.dll"
        CLI_NATIVE="$ROOT/app/Laplace.Cli/bin/Release/net10.0/liblaplace_core.so"
        ENGINE_NATIVE="$ROOT/build/engine/core/liblaplace_core.so"
    fi
fi

if [[ -z "${LAPLACE_OPS_LOG_DIR:-}" ]]; then
    if [[ "$INGEST_USES_PREFIX" == 1 ]]; then
        export LAPLACE_OPS_LOG_DIR="$INGEST_ROOT/logs"
    else
        export LAPLACE_OPS_LOG_DIR="$LOGDIR/ops"
    fi
fi
mkdir -p -- "$LAPLACE_OPS_LOG_DIR" || {
    echo "::error::cannot create ingest operations log directory: $LAPLACE_OPS_LOG_DIR" >&2
    exit 1
}
laplace_share_directory "$LAPLACE_OPS_LOG_DIR"
[[ -w "$LAPLACE_OPS_LOG_DIR" ]] || {
    echo "::error::ingest operations log directory is not writable: $LAPLACE_OPS_LOG_DIR" >&2
    exit 1
}

if [[ -n "${GITHUB_ACTIONS:-}${CI:-}" && -z "${LAPLACE_INGEST_CONSOLE:-}" ]]; then
    export LAPLACE_INGEST_CONSOLE=ci
fi

require_cli() {
    [[ -f "$DLL" ]] || {
        echo "::error::ingest runtime is not installed: $DLL" >&2
        echo "::error::product install must publish a complete CLI at $INGEST_RUNTIME; ingest does not compile" >&2
        return 1
    }
    [[ -f "$ENGINE_NATIVE" && -f "$CLI_NATIVE" ]] || {
        echo "::error::ingest native runtime is incomplete (engine=$ENGINE_NATIVE cli=$CLI_NATIVE)" >&2
        return 1
    }
    cmp -s "$ENGINE_NATIVE" "$CLI_NATIVE" || {
        echo "::error::ingest CLI native closure differs from the installed prefix" >&2
        echo "::error::rebuild/install the product; ingest never repairs or compiles runtime artifacts" >&2
        return 1
    }
    [[ "${INGEST_USES_PREFIX:-0}" != 1 ]] || {
        echo "::notice::ingest runtime $INGEST_RUNTIME (installed prefix)"
        # Changing this file is install+live, not ninja ALL / web / Chess.Tests.
        return 0
    }

    # The self-hosted runner keeps bin/ and build/ between checkouts. Existence and
    # native-byte parity therefore prove only that one coherent runtime was built,
    # not that it implements this checkout. Accept an older receipt only when every
    # production input in the CLI's managed/native dependency closure is unchanged.
    # Docs, tests, workflows and the web application are deliberately outside this
    # closure, so an operator ingest does not pay for an unrelated product rebuild.
    local prepared current
    prepared="$(cat "$ROOT/build/.laplace-source-revision" 2>/dev/null || true)"
    current="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || true)"
    [[ "$prepared" =~ ^[0-9a-fA-F]{40}$ && "$current" =~ ^[0-9a-fA-F]{40}$ ]] || {
        echo "::error::ingest runtime has no valid prepared source revision" >&2
        return 1
    }
    if [[ "$prepared" != "$current" ]]; then
        git -C "$ROOT" cat-file -e "$prepared^{commit}" 2>/dev/null || {
            echo "::error::prepared ingest revision $prepared is unavailable in this checkout" >&2
            return 1
        }
        git -C "$ROOT" diff --quiet "$prepared" "$current" -- \
            CMakeLists.txt cmake Directory.Packages.props global.json \
            engine/core \
            app/Directory.Build.props \
            app/Laplace.Cli \
            app/Laplace.Core \
            app/Laplace.Decomposers \
            app/Laplace.Substrate \
            app/Laplace.Ops \
            app/Laplace.Chess || {
                echo "::error::prepared ingest runtime is stale for this checkout (built $prepared, selected $current)" >&2
                echo "::error::build/deploy the changed CLI dependency closure before ingest" >&2
                return 1
            }
    fi
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

route="$source"
if [[ "$LADDER" == *" $source "* ]]; then route=@ladder; fi
case "$route" in
    all)
        require_cli
        STAGES=( "${SOURCE_ORDER[@]}" )
        from="${INGEST_FROM:-}"
        skip=0; [[ -n "$from" ]] && skip=1
        for src in "${STAGES[@]}"; do
            if [[ "$skip" == 1 ]]; then
                if [[ "$src" == "$from" ]]; then skip=0; else echo ">>> skip $src (before INGEST_FROM=$from)"; continue; fi
            fi
            echo ">>> stage $src — start $(date -u +%H:%M:%S)"
            t0=$SECONDS
            if [[ "$src" == document ]]; then
                doc_path="${INGEST_DOCUMENT_PATH:-$(corpus)/test-data/text}"
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
    @ladder|stack|tiny-codes|rgba-image|track-audio|frame-video)
        require_cli
        if [[ "$source" == document && -z "$path" ]]; then
            path="${INGEST_DOCUMENT_PATH:-$(corpus)/test-data/text}"
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
    chess-move-outcomes|chess-tactic-outcomes|chess-position-outcomes|chess-player-context-outcomes|chess-eval|chess-analyze|chess-transitions|chess-trajectory|chess-opening-match)
        require_cli
        ingest "$source"
        ;;
    chess-syzygy)
        require_cli
        if [[ -n "$path" ]]; then ingest chess-syzygy "$path"; else ingest chess-syzygy; fi
        ;;
    *)
        # The CLI owns historical input normalization and unknown-name errors;
        # this wrapper declares no second alias table or corpus roster.
        require_cli
        if [[ -n "$path" ]]; then ingest "$source" "$path"; else ingest "$source"; fi
        ;;
esac
