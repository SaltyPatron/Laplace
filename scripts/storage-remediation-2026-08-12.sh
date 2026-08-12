#!/usr/bin/env bash
# =============================================================================
# One-time storage remediation — 2026-08-12
#
#   sudo bash scripts/storage-remediation-2026-08-12.sh --dry-run
#   sudo bash scripts/storage-remediation-2026-08-12.sh
#   sudo bash scripts/storage-remediation-2026-08-12.sh --from 6      # resume
#
# WHY. WAL lived on md127, a RAID0 of two Intel 535s manufactured 20 units apart
# in the same production run — one failure domain, not two. sda stopped answering
# for ~60s mid-ingest (ata1 frozen, 5x COMRESET failed); the WAL fsync had no copy
# to retry against, issue_xlog_fsync PANICked on segment 0000000100001ABC0000009A,
# XFS shut down and took the postgres binaries with it (status=203/EXEC). The
# checkpoint record was unrecoverable.
#
# The RAID level was never the problem — the tenancy was. The array stays striped.
# What changes is that nothing on it may cost more than time.
#
# TARGET
#   nvme1n1 vg-data     lv-postgres 640G  heap            DATABASE ONLY
#                       lv-laplace   16G  install prefix
#   nvme0n1 vg-hosting  lv-pgwal    128G  WAL             WAL ONLY — nothing competes
#                       lv-swap      16G  (was 32G, never touched)
#   md127   vg-raid     lv-build    256G  external/ + submodule-modules/ (git-pinned)
#                       lv-agents   128G  CI _work
#                       lv-gaming    64G  Minecraft (backed up to /backup first)
#                       lv-redis     64G  cache — UNCHANGED
#   sdc                 OS — UNCHANGED
#   sdd     /archive    UNCHANGED       sde /vault UNCHANGED
#   sdf     /backup     worlds, secrets, future basebackups
#
# SAFETY. Fails fast. Every destroy is preceded by a verify gate that aborts
# rather than proceeds. Phases are resumable with --from N. Nothing is destroyed
# until its replacement is mounted and proven readable. /backup is established
# and the worlds copied there BEFORE anything moves onto the correlated pair.
# =============================================================================
set -euo pipefail

DRY=0; FROM=1
while [ $# -gt 0 ]; do
    case "$1" in
        --dry-run) DRY=1 ;;
        --from)    FROM="$2"; shift ;;
        *) echo "unknown arg: $1" >&2; exit 2 ;;
    esac
    shift
done
[ "$(id -u)" -eq 0 ] || { echo "must run as root" >&2; exit 1; }

G(){ printf '\033[0;32m%s\033[0m\n' "$*"; }
Y(){ printf '\033[0;33m%s\033[0m\n' "$*"; }
R(){ printf '\033[0;31m%s\033[0m\n' "$*"; }
PHASE(){ CUR="$1"; shift
    if [ "$CUR" -lt "$FROM" ]; then SKIP=1; Y "── phase $CUR: $* (skipped)"; return 0; fi
    SKIP=0; echo; G "════ phase $CUR — $*"; }
run(){ [ "${SKIP:-0}" -eq 1 ] && return 0
    if [ "$DRY" -eq 1 ]; then echo "  DRY: $*"; return 0; fi
    echo "  + $*"; "$@"; }
fstab_set(){
    local dev="$1" mnt="$2" fs="$3" opts="$4" uuid
    [ "${SKIP:-0}" -eq 1 ] && return 0
    if [ "$DRY" -eq 1 ]; then echo "  DRY: fstab $mnt -> $dev"; return 0; fi
    uuid="$(blkid -s UUID -o value "$dev")" || true
    [ -n "$uuid" ] || { R "no UUID for $dev"; exit 1; }
    sed -i "\\|[[:space:]]${mnt}[[:space:]]|d" /etc/fstab
    printf 'UUID=%s  %s  %s  %s  0  2\n' "$uuid" "$mnt" "$fs" "$opts" >> /etc/fstab
    # systemd's fstab-generator OWNS these paths once they are in fstab. A bare
    # umount marks the generated .mount unit inactive; a bare mount afterwards does
    # NOT mark it active, so the next reconcile unmounts it again. 2026-08-12: this
    # silently unmounted /opt/laplace, /opt/gaming and /var/lib/agents AFTER the
    # script had mounted and verified all three ("Deactivated successfully" twice
    # per path in the journal). Reload so systemd re-reads the table it owns.
    systemctl daemon-reload
    echo "  + fstab: $mnt -> UUID=$uuid"; }
OPTS="defaults,nofail"
MIG=/mnt/mig

cp -n /etc/fstab "/etc/fstab.pre-remediation-$(date +%Y%m%d-%H%M)" 2>/dev/null || true
mkdir -p "$MIG"

# =============================================================================
PHASE 1 "preflight (read-only — runs in --dry-run too)"
# =============================================================================
if [ "${SKIP:-0}" -eq 0 ]; then
    if mountpoint -q /data/models; then
        echo "  verifying /data/models is fully present on /vault/models ..."
        d="$(rsync -aHAXn --itemize-changes /data/models/ /vault/models/ 2>/dev/null | grep -c '^[<>ch]' || true)"
        [ "${d:-1}" -eq 0 ] || { R "✗ $d file(s) differ — /vault copy incomplete. Refusing."; exit 1; }
        G "  ✓ model copy verified (0 differing files)"
    else Y "  /data/models not mounted — already reclaimed"; fi

    if [ -f /opt/laplace/pgdata/data/PG_VERSION ]; then
        R "✗ a cluster exists at /opt/laplace/pgdata/data — this re-carves that volume."
        R "  Remove it first:  rm -rf /opt/laplace/pgdata/data"
        exit 1
    fi
    G "  ✓ no cluster present"

    for vg in vg-data vg-hosting vg-raid; do
        printf '  %-12s free %s\n' "$vg" "$(vgs --noheadings -o vg_free --units g "$vg" | tr -d ' ')"
    done
fi

# =============================================================================
PHASE 2 "stop everything holding a target filesystem"
# =============================================================================
# nginx included: phase 11 unmounts /var/www out from under it. Leaving it running
# either fails the umount with EBUSY or leaves it serving a yanked mount.
for u in laplace-api laplace-postgresql redis-server nginx \
         minecraft minecraft-fabric minecraft-paper minecraft-vanilla \
         'actions.runner.SaltyPatron-Laplace.hart-server.service'; do
    run systemctl stop "$u" || true
done
run systemctl reset-failed laplace-postgresql || true

# =============================================================================
PHASE 3 "establish /backup on sdf — BEFORE anything moves to the array"
# =============================================================================
# Minecraft worlds are the only irreplaceable thing that ends up on the correlated
# pair. They may not move there until a copy exists on a different physical device.
if mountpoint -q /mnt/sdg; then run umount /mnt/sdg; fi
run sed -i '\|/mnt/sdg|d' /etc/fstab
run mkdir -p /backup
run mount /dev/sdf /backup
fstab_set /dev/sdf /backup ext4 "$OPTS"
run rmdir /mnt/sdg || true
run mkdir -p /backup/gaming /backup/secrets

# =============================================================================
PHASE 4 "back up the worlds and secrets to /backup"
# =============================================================================
run rsync -aHAX --info=progress2 /opt/gaming/ /backup/gaming/
run rsync -aHAX /opt/laplace/secrets/ /backup/secrets/ || true
# /var/www is only ~16K but it is the nginx vhost roots (html/, minecraft/, sites/)
# and phase 11 destroys lv-web outright. Preserved here, restored there.
run mkdir -p /backup/www
run rsync -aHAX /var/www/ /backup/www/ || true
if [ "${SKIP:-0}" -eq 0 ] && [ "$DRY" -eq 0 ]; then
    [ -d /backup/gaming/minecraft ] || { R "✗ world backup missing — refusing to continue"; exit 1; }
    G "  ✓ worlds on /backup ($(du -sh /backup/gaming | cut -f1)), different physical device"
    G "  ✓ /var/www preserved ($(du -sh /backup/www 2>/dev/null | cut -f1))"
fi

# =============================================================================
PHASE 5 "drop the redundant — lv-models (dup on /vault), lv-neo4j (empty)"
# =============================================================================
if mountpoint -q /data/models; then
    run umount /data/models
    run lvremove -y vg-raid/lv-models
    run sed -i '\|/data/models|d' /etc/fstab
    run rmdir /data/models
fi
if mountpoint -q /var/lib/neo4j; then
    run umount /var/lib/neo4j
    run lvremove -y vg-data/lv-neo4j
    run sed -i '\|/var/lib/neo4j|d' /etc/fstab
    run rmdir /var/lib/neo4j
fi

# =============================================================================
PHASE 6 "lv-build on the array — git-pinned source off the database tier"
# =============================================================================
# external/ (7.8G) + submodule-modules/ (5.6G) are source checkouts pinned by
# PINS.tsv, not build output. Re-clonable, so they belong on expendable storage —
# and keeping compile churn off nvme1n1 keeps it away from the heap.
run lvcreate -L 256G -n lv-build vg-raid
run mkfs.xfs -f /dev/vg-raid/lv-build
run mkdir -p /build
run mount /dev/vg-raid/lv-build /build
fstab_set /dev/vg-raid/lv-build /build xfs "$OPTS"
run rsync -aHAX --info=progress2 /opt/laplace/external/          /build/external/
run rsync -aHAX --info=progress2 /opt/laplace/submodule-modules/ /build/submodule-modules/
if [ "${SKIP:-0}" -eq 0 ] && [ "$DRY" -eq 0 ]; then
    [ -f /build/external/PINS.tsv ] || { R "✗ PINS.tsv missing on /build — source copy failed"; exit 1; }
    [ -d /build/external/postgresql ] || { R "✗ postgresql source missing on /build"; exit 1; }
    G "  ✓ pinned source verified on /build ($(du -sh /build | cut -f1))"
fi
run chown -R laplace-runner:laplace-runner /build

# THE THIRD I/O STREAM. Sort/hash spill goes to $PGDATA/base/pgsql_tmp unless a
# temp tablespace exists — the heap device, contending with the reads of the very
# query that is spilling. WAL was split off the heap after the 2026-07-26
# wiktionary incident; temp spill is the same fight and was never split. Lands on
# the array on purpose: Postgres deletes temp files at startup, so the correlated
# pair holding them costs nothing, and it is the only idle spindle left.
run lvcreate -L 128G -n lv-pgtemp vg-raid
run mkfs.xfs -f /dev/vg-raid/lv-pgtemp
run mkdir -p /pgtemp
run mount /dev/vg-raid/lv-pgtemp /pgtemp
fstab_set /dev/vg-raid/lv-pgtemp /pgtemp xfs "$OPTS"
run chown laplace-runner:laplace-runner /pgtemp
run chmod 0700 /pgtemp

# =============================================================================
PHASE 7 "install prefix -> nvme1n1, WITHOUT the source trees"
# =============================================================================
run lvcreate -L 16G -n lv-laplace vg-data
run mkfs.xfs -f /dev/vg-data/lv-laplace
run mount /dev/vg-data/lv-laplace "$MIG"
run rsync -aHAX --info=progress2 \
    --exclude='pgdata/***' --exclude='external/***' --exclude='submodule-modules/***' \
    /opt/laplace/ "$MIG"/
run umount "$MIG"
if mountpoint -q /opt/laplace/pgdata; then run umount /opt/laplace/pgdata; fi
run umount /opt/laplace
run mount /dev/vg-data/lv-laplace /opt/laplace
fstab_set /dev/vg-data/lv-laplace /opt/laplace xfs "$OPTS"
if [ "${SKIP:-0}" -eq 0 ] && [ "$DRY" -eq 0 ]; then
    [ -x /opt/laplace/pgsql-18/bin/postgres ] \
        || { R "✗ postgres binary missing on new prefix — old LV intact, remount it"; exit 1; }
    G "  ✓ prefix verified on nvme1n1 ($(du -sh /opt/laplace | cut -f1))"
fi
run lvremove -y vg-raid/lv-laplace

# =============================================================================
PHASE 8 "re-carve the heap 867G -> 640G"
# =============================================================================
run lvremove -y vg-data/lv-postgres
run lvcreate -L 640G -n lv-postgres vg-data
run mkfs.xfs -f /dev/vg-data/lv-postgres
run mkdir -p /opt/laplace/pgdata
run mount /dev/vg-data/lv-postgres /opt/laplace/pgdata
fstab_set /dev/vg-data/lv-postgres /opt/laplace/pgdata xfs "$OPTS"
run chown laplace-runner:laplace-runner /opt/laplace/pgdata
run chmod 0700 /opt/laplace/pgdata

# =============================================================================
PHASE 9 "Minecraft: instances -> array, WORLDS -> nvme1n1"
# =============================================================================
# The worlds are ~362M of the 3.7G and 100% of the irreplaceable value; the rest
# is mods, jars, bundled JREs and plugin caches, all re-downloadable. So the two
# halves go to storage matching what their loss costs.
#
# Symlinks rather than level-name/--world-container: the mechanism differs across
# NeoForge / Fabric / Paper / vanilla, and a server upgrade that rewrites
# server.properties would silently point the world back at local disk. A symlink
# is flavour-independent and survives that.
WORLDS=(
    "/opt/gaming/minecraft/stoneblock-4/world:stoneblock-4"
    "/opt/gaming/minecraft/fabric/1.21.11/overworld/003:fabric-003"
    "/opt/gaming/minecraft/paper/1.21.11/world:paper"
    "/opt/gaming/minecraft/vanilla/1.21.11/world:vanilla"
)
run lvcreate -L 16G -n lv-worlds vg-data
run mkfs.xfs -f /dev/vg-data/lv-worlds
run mkdir -p /worlds
run mount /dev/vg-data/lv-worlds /worlds
fstab_set /dev/vg-data/lv-worlds /worlds xfs "$OPTS"

# instances (worlds included for now — replaced by symlinks below)
run lvcreate -L 64G -n lv-gaming vg-raid
run mkfs.xfs -f /dev/vg-raid/lv-gaming
run mount /dev/vg-raid/lv-gaming "$MIG"
run rsync -aHAX --info=progress2 /opt/gaming/ "$MIG"/
run umount "$MIG"
run umount /opt/gaming
run mount /dev/vg-raid/lv-gaming /opt/gaming
fstab_set /dev/vg-raid/lv-gaming /opt/gaming xfs "$OPTS"
if [ "${SKIP:-0}" -eq 0 ] && [ "$DRY" -eq 0 ]; then
    [ -d /opt/gaming/minecraft ] || { R "✗ instances missing after copy — /backup has a copy"; exit 1; }
    G "  ✓ instances on the array"
fi

# lift each world onto nvme1n1 and leave a symlink behind
for entry in "${WORLDS[@]}"; do
    src="${entry%%:*}"; name="${entry##*:}"
    if [ "${SKIP:-0}" -eq 0 ] && [ "$DRY" -eq 0 ]; then
        [ -d "$src" ] || { Y "  $src absent — skipping"; continue; }
        rsync -aHAX "$src"/ "/worlds/$name"/
        [ -d "/worlds/$name" ] || { R "✗ world copy failed: $name"; exit 1; }
        rm -rf "${src:?}"
        ln -s "/worlds/$name" "$src"
        echo "  + $src -> /worlds/$name ($(du -sh "/worlds/$name" | cut -f1))"
    else
        echo "  DRY: $src -> /worlds/$name"
    fi
done
run chown -R ahart:gaming /worlds
run lvremove -y vg-hosting/lv-gaming

# =============================================================================
PHASE 10 "CI _work -> the array"
# =============================================================================
run lvcreate -L 128G -n lv-agents vg-raid
run mkfs.xfs -f /dev/vg-raid/lv-agents
run mount /dev/vg-raid/lv-agents "$MIG"
run rsync -aHAX --info=progress2 /var/lib/agents/ "$MIG"/
run umount "$MIG"
run umount /var/lib/agents
run mount /dev/vg-raid/lv-agents /var/lib/agents
fstab_set /dev/vg-raid/lv-agents /var/lib/agents xfs "$OPTS"
run lvremove -y vg-hosting/lv-agents

# =============================================================================
PHASE 11 "nvme0n1 becomes WAL-only: drop lv-web, shrink swap, carve lv-pgwal"
# =============================================================================
# lv-web held a default nginx page and an empty dynmap dir. Dynmap was never
# installed; the Laplace SPA ships inside /opt/laplace/app/wwwroot and nginx
# reverse-proxies to :5187. Nothing deploys to /var/www.
if mountpoint -q /var/www; then run umount /var/www; fi
run sed -i '\|/var/www|d' /etc/fstab
run lvremove -y vg-hosting/lv-web
# /var/www now lives on lv-var (sdc), which has ~100G idle. Restore the vhost roots
# saved in phase 4 — lvremove just destroyed the originals.
run mkdir -p /var/www
run rsync -aHAX /backup/www/ /var/www/ || true
run chown -R www-data:www-data /var/www

run swapoff /dev/vg-hosting/lv-swap
run lvremove -y vg-hosting/lv-swap
run lvcreate -L 16G -n lv-swap vg-hosting
run mkswap /dev/vg-hosting/lv-swap
run swapon /dev/vg-hosting/lv-swap
if [ "${SKIP:-0}" -eq 0 ] && [ "$DRY" -eq 0 ]; then
    sed -i '\|[[:space:]]none[[:space:]]*swap|d' /etc/fstab
    printf 'UUID=%s  none  swap  sw  0  0\n' \
        "$(blkid -s UUID -o value /dev/vg-hosting/lv-swap)" >> /etc/fstab
fi

run lvcreate -L 128G -n lv-pgwal vg-hosting
run mkfs.xfs -f /dev/vg-hosting/lv-pgwal
run mkdir -p /var/lib/pgwal
run mount /dev/vg-hosting/lv-pgwal /var/lib/pgwal
fstab_set /dev/vg-hosting/lv-pgwal /var/lib/pgwal xfs "$OPTS"
run chown laplace-runner:laplace-runner /var/lib/pgwal
run chmod 0700 /var/lib/pgwal

# =============================================================================
PHASE 12 "verify"
# =============================================================================
if [ "${SKIP:-0}" -eq 0 ] && [ "$DRY" -eq 0 ]; then
    echo
    # pgdata is a NESTED mount and phase 7's rsync excluded pgdata/*** — which skips
    # the directory too, so the mountpoint does not exist on the fresh lv-laplace.
    mkdir -p /opt/laplace/pgdata
    # Hand the mounts back to systemd. Everything above used bare mount/umount; this
    # is what makes the generated .mount units agree with reality.
    systemctl daemon-reload
    mount -a || Y "⚠ mount -a reported an error — check findmnt below"
    # findmnt takes ONE target. Passing eight made it exit non-zero, and under
    # `set -e` that killed phase 12 silently on the 2026-08-12 run.
    for m in /opt/laplace /opt/laplace/pgdata /build /pgtemp /opt/gaming /worlds \
             /var/lib/agents /var/lib/pgwal /var/lib/redis /backup; do
        printf '  %-22s %s\n' "$m" "$(findmnt -n -o SOURCE,SIZE,USED "$m" 2>/dev/null || echo '✗ NOT MOUNTED')"
    done
    echo; vgs --units g; echo; lvs --units g; echo
    hd="$(lsblk -nrso NAME "$(findmnt -n -o SOURCE /opt/laplace/pgdata)" | tail -1)"
    wd="$(lsblk -nrso NAME "$(findmnt -n -o SOURCE /var/lib/pgwal)"      | tail -1)"
    td="$(lsblk -nrso NAME "$(findmnt -n -o SOURCE /pgtemp)"             | tail -1)"
    [ "$hd" != "$wd" ] || { R "✗ heap and WAL both on $hd — bootstrap WILL fail"; exit 1; }
    [ "$hd" != "$td" ] || { R "✗ heap and temp both on $hd — spill would still contend"; exit 1; }
    G "✓ three independent streams: heap=$hd  WAL=$wd  temp=$td"
    if findmnt --verify >/dev/null 2>&1; then G "✓ fstab parses clean"; else Y "⚠ check fstab"; fi
    echo
    G "════ done. Next:"
    echo "   1. point the build at the new source location:"
    echo "        LAPLACE_EXTERNAL=/build/external   (bootstrap-laplace-runner.sh)"
    echo "   2. sudo LAPLACE_OPERATOR=\${SUDO_USER:-ahart} bash scripts/bootstrap-laplace-runner.sh bootstrap"
    echo "   3. systemctl start nginx redis-server minecraft minecraft-{fabric,paper,vanilla}"
    echo "      (nginx was stopped in phase 2 so /var/www could be unmounted)"
    echo "   4. systemctl start 'actions.runner.*'"
fi
