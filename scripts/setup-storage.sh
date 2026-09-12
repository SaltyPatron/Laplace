#!/usr/bin/env bash
# Reconcile shared parent directories without replacing users or database files.
# sudo bash scripts/setup-storage.sh [--check]
set -euo pipefail
group=laplace-runner
mode="${1:---repair}"
[[ "$mode" == --repair || "$mode" == --check ]] || exit 2
getent group "$group" >/dev/null
umask 0002
for volume in /build /opt/laplace/pgdata /var/lib/pgwal /pgtemp; do
    mountpoint -q "$volume" || { echo "Missing storage mount: $volume" >&2; exit 1; }
done
paths=(/build/laplace /build/laplace/build /build/laplace/work
       /build/laplace/work/legacy-scratch /build/laplace/worktrees
       /build/laplace/work/refactor-scratch
       /build/laplace/recovery /build/laplace/runner
       /opt/laplace/pgdata /var/lib/pgwal /pgtemp)
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
    if [[ ! -d "$path" || $(stat -c %G "$path") != "$group" || $(stat -c %a "$path") != 2770 ]]; then
        echo "Shared-directory drift: $path (requires $group 2770)" >&2
        failed=1
    fi
done
[[ "$failed" == 0 ]] || exit "$failed"
if [[ "$mode" == --repair ]]; then
    # Only workspace trees are recursive. Never apply build permissions to the
    # data/WAL/tablespace contents or follow a workspace symlink into them.
    immutable_inputs=$(readlink -m /opt/laplace/package-inputs/postgresql)
    immutable_releases=$(readlink -m /opt/laplace/releases)
    for path in /build/laplace/build /build/laplace/work /build/laplace/worktrees; do
        find "$path" -xdev \( -path "$immutable_inputs" -o -path "$immutable_releases" \) -prune -o ! -type l -exec chgrp "$group" {} +
        find "$path" -xdev \( -path "$immutable_inputs" -o -path "$immutable_releases" \) -prune -o -type d -exec chmod g+rws {} +
        find "$path" -xdev \( -path "$immutable_inputs" -o -path "$immutable_releases" \) -prune -o -type f -exec chmod g+rwX {} +
    done
    while read -r unit _; do
        [[ "$unit" == actions.runner.SaltyPatron-Laplace.hart-server.service ]] || continue
        [[ $(systemctl show "$unit" -p User --value) == laplace-runner ]] || continue
        scratch=/build/laplace/work/legacy-scratch
        dropin="/etc/systemd/system/$unit.d"
        install -d -m 0755 "$dropin"
        cat > "$dropin/50-laplace-storage.conf" <<EOF
[Unit]
RequiresMountsFor=/build /var/lib/agents

[Service]
Group=laplace-runner
UMask=0002
Environment=TMPDIR=$scratch
Environment=TMP=$scratch
Environment=TEMP=$scratch
EOF
    done < <(systemctl list-unit-files 'actions.runner.SaltyPatron-Laplace.hart-server.service' --no-legend)
    systemctl daemon-reload
    operator="${LAPLACE_OPERATOR:-${SUDO_USER:-}}"
    if [[ -n "$operator" && "$operator" != root && "$operator" != laplace-runner ]]; then
        probe=$(mktemp -d /build/laplace/work/group-writer-proof.XXXXXXXX)
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
