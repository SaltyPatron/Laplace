#!/usr/bin/env bash
# Source before invoking tools so their scratch uses the dedicated build volume.
laplace_storage_init() {
    local base scratch resolved checkout group
    group=laplace-runner
    umask 0002
    checkout=$(realpath -m -- "${ROOT:-$PWD}") || return
    case "$checkout" in
        /tmp|/tmp/*|/var/tmp|/var/tmp/*|/dev/shm|/dev/shm/*)
            echo "Laplace checkouts must use permanent storage: $checkout" >&2; return 1 ;;
    esac
    base=/build/laplace/work
    mountpoint -q /build || { echo 'Laplace requires the /build volume' >&2; return 1; }
    scratch="${LAPLACE_SCRATCH_ROOT:-$base/legacy-scratch}"
    resolved=$(realpath -m -- "$scratch") || return
    case "$resolved" in
        /build/laplace/*) ;;
        *) echo "Laplace scratch must resolve under /build/laplace: $scratch" >&2; return 1 ;;
    esac
    mkdir -p -- "$resolved" || return
    if [[ -O "$resolved" ]]; then
        chgrp "$group" "$resolved" || return
        chmod 2770 "$resolved" || return
    fi
    [[ $(stat -c %G "$resolved") == "$group" && -g "$resolved" && -w "$resolved" ]] || {
        echo "Laplace scratch requires writable setgid $group access: $resolved" >&2
        return 1
    }
    export TMPDIR="$resolved" TMP="$resolved" TEMP="$resolved"
}
