#!/usr/bin/env bash
# Reconcile shared parent directories without replacing users or database files.
# sudo bash scripts/setup-storage.sh [--check]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/lib/installation.sh"
group="${LAPLACE_BUILD_GROUP:-laplace-runner}"
mode="${1:---repair}"
[[ "$mode" == --repair || "$mode" == --check ]] || exit 2
getent group "$group" >/dev/null
umask 0002
# This owner repairs compilation/work directories. PostgreSQL data, WAL and
# tablespaces retain their database-owner modes and are managed by cluster setup.
paths=("$LAPLACE_BUILD_ROOT" "$LAPLACE_WORK_ROOT" "$LAPLACE_SCRATCH_ROOT"
       "$LAPLACE_DEPS_BUILD" "$LAPLACE_LOCK_ROOT" "$LAPLACE_WORK_ROOT/runner" "$LAPLACE_WORK_ROOT/recovery")
# Validate the complete selection before changing any directory permissions.
for path in "${paths[@]}"; do
    resolved=$(realpath -m -- "$path")
    [[ "$resolved" != / && ! -L "$path" && ! -e "$path/PG_VERSION" ]] || {
        echo "Expected a workspace directory, not a filesystem root, cluster or symlink: $path" >&2; exit 1;
    }
    for protected in "${LAPLACE_PGDATA:-}" "${LAPLACE_PG_DATA:-}" "${LAPLACE_PGWAL:-}" "${LAPLACE_PG_WAL:-}" \
        "${LAPLACE_PGTEMP:-}" "${LAPLACE_PG_TEMP:-}" "${LAPLACE_APP_DIR:-${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/app}"; do
        [[ -n "$protected" ]] || continue
        protected=$(realpath -m -- "$protected")
        if [[ "$resolved/" == "$protected/"* || "$protected/" == "$resolved/"* ]]; then
            echo "Workspace overlaps database or serving state: $path; no permissions changed" >&2; exit 1
        fi
    done
done
failed=0
for path in "${paths[@]}"; do
    [[ ! -L "$path" && ! -e "$path/PG_VERSION" ]] || {
        echo "Expected physical shared parent, not a cluster or symlink: $path" >&2; exit 1;
    }
    if [[ "$mode" == --repair ]]; then
        mkdir -p -- "$path"
        chgrp "$group" "$path"
        chmod 2770 "$path"
    fi
    if [[ ! -d "$path" || $(stat -c %G "$path") != "$group" || ! -g "$path" || ! -w "$path" ]]; then
        echo "Shared-directory drift: $path (requires $group 2770)" >&2
        failed=1
    fi
done
[[ "$failed" == 0 ]] || exit "$failed"
if [[ "$mode" == --repair ]]; then
    # Only workspace trees are recursive. Never apply build permissions to the
    # data/WAL/tablespace contents or follow a workspace symlink into them.
    immutable_inputs=$(readlink -m "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/package-inputs/postgresql")
    immutable_releases=$(readlink -m "${LAPLACE_INSTALL_PREFIX:-/opt/laplace}/releases")
    for path in "$LAPLACE_BUILD_ROOT" "$LAPLACE_WORK_ROOT"; do
        find "$path" -xdev \( -path "$immutable_inputs" -o -path "$immutable_releases" \) -prune -o ! -type l -exec chgrp "$group" {} +
        find "$path" -xdev \( -path "$immutable_inputs" -o -path "$immutable_releases" \) -prune -o -type d -exec chmod g+rws {} +
        find "$path" -xdev \( -path "$immutable_inputs" -o -path "$immutable_releases" \) -prune -o -type f -exec chmod g+rwX {} +
    done
    while read -r unit _; do
        [[ "$unit" == "${LAPLACE_RUNNER_UNIT:-$unit}" ]] || continue
        [[ $(systemctl show "$unit" -p User --value) == laplace-runner ]] || continue
        scratch="$LAPLACE_SCRATCH_ROOT"
        dropin="/etc/systemd/system/$unit.d"
        install -d -m 0755 "$dropin"
        python3 - "$LAPLACE_WORK_ROOT" "$scratch" "$group" > "$dropin/50-laplace-storage.conf" <<'PY'
import sys
def quote(value):
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%') + '"'
work, scratch, group = sys.argv[1:]
print('[Unit]\nRequiresMountsFor=' + quote(work))
print('\n[Service]\nGroup=' + group + '\nUMask=0002')
for name in ('TMPDIR', 'TMP', 'TEMP'):
    print('Environment=' + quote(name + '=' + scratch))
PY
    done < <(systemctl list-unit-files 'actions.runner.SaltyPatron-Laplace.*.service' --no-legend)
    systemctl daemon-reload
    operator="${LAPLACE_OPERATOR:-${SUDO_USER:-}}"
    if [[ -n "$operator" && "$operator" != root && "$operator" != laplace-runner ]]; then
        probe=$(mktemp -d "$LAPLACE_WORK_ROOT/group-writer-proof.XXXXXXXX")
        trap 'rm -rf -- "$probe"' EXIT
        chgrp "$group" "$probe"
        chmod 2770 "$probe"
        for writer in "$operator" laplace-runner; do
            runuser -u "$writer" -- bash -c 'umask 0002; mkdir "$1/$2"; printf "created\n" > "$1/$2/file"' bash "$probe" "$writer"
        done
        for writer in "$operator" laplace-runner; do
            other=laplace-runner
            [[ "$writer" != laplace-runner ]] || other="$operator"
            runuser -u "$writer" -- bash -c 'printf "modified\n" >> "$1/$2/file"; mv "$1/$2/file" "$1/$2/renamed"; rm "$1/$2/renamed"; rmdir "$1/$2"' bash "$probe" "$other"
        done
        echo "PASS: $operator and laplace-runner created, modified, renamed and removed each other's output"
    fi
    echo 'Shared directory and runner configuration repaired; runner restarts load the new environment.'
fi
exit "$failed"
