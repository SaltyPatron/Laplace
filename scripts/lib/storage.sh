#!/usr/bin/env bash
# Source before invoking tools so scratch follows the host's configured work root.
source "$(dirname "${BASH_SOURCE[0]}")/installation.sh"
laplace_storage_init() {
    local scratch resolved checkout group
    group="${LAPLACE_BUILD_GROUP:-laplace-runner}"
    umask 0002
    checkout=$(realpath -m -- "${ROOT:-$PWD}") || return
    case "$checkout" in
        /tmp|/tmp/*|/var/tmp|/var/tmp/*|/dev/shm|/dev/shm/*)
            echo "Laplace checkouts must use permanent storage: $checkout" >&2; return 1 ;;
    esac
    scratch="${LAPLACE_SCRATCH_ROOT:?Declare LAPLACE_WORK or LAPLACE_SCRATCH_ROOT}"
    resolved=$(realpath -m -- "$scratch") || return
    case "$resolved" in
        /tmp|/tmp/*|/var/tmp|/var/tmp/*|/dev/shm|/dev/shm/*)
            echo "Laplace scratch must be permanent storage: $scratch" >&2; return 1 ;;
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

# umask 022 clears group write on mkdir. A setgid parent still passes
# laplace-runner and the setgid bit, so the directory is mode 2755 and the
# other writer cannot unlink it. setup-host and setup-storage repair shared
# trees with chmod g+rws. Apply that to a directory this process owns.
laplace_share_directory() {
    local path="$1"
    [[ -d "$path" && ! -L "$path" && -O "$path" ]] || return 0
    [[ "$(stat -c '%G' "$path")" == "${LAPLACE_BUILD_GROUP:-laplace-runner}" ]] || return 0
    chmod g+rws -- "$path"
}

laplace_share_tree() {
    local path="$1" dir
    [[ -d "$path" && ! -L "$path" ]] || return 0
    while IFS= read -r -d '' dir; do
        laplace_share_directory "$dir" || true
    done < <(find "$path" -xdev -type d -print0)
}
