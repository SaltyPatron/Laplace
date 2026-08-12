# Storage remediation — 2026-08-11

## Why

`sda` stopped answering for ~60s during an OMW ingest (`ata1` frozen, 5× COMRESET
failed). WAL was on `md127`, a two-disk RAID0 with no redundancy, so the fsync
could not be retried against a second copy. `issue_xlog_fsync` PANICked on segment
`0000000100001ABC0000009A`; XFS shut the filesystem down and took the postgres
binaries with it (`status=203/EXEC`). The checkpoint record in that segment was
unrecoverable — cluster rebuilt from scratch.

Root cause is placement, not hardware: WAL and the install prefix were on a stripe.
`pg_prepare_waldir()` approved it because it only asks "different filesystem?" and
"enough free space?" — neither of which a RAID0 fails.

## Decisions

| Question | Decision |
|---|---|
| WAL location | `nvme0n1` — dedicated `lv-pgwal`. Interim: `/var/lib/pgwal` on `sdc` |
| `/opt/laplace` install prefix | Move to `nvme1n1`, reclaiming `lv-neo4j` |
| Models (326 G) | Already on `/vault`, rsync-verified. Drop `lv-models` |
| Redis | **Keep** — for web/other caching. Cache on expendable storage is correct |
| neo4j / milvus / qdrant | Not wanted. Reclaim |
| CI `_work` | Move to the array — write-heavy, re-cloneable, expendable by design |
| RAID0 | Break into two plain LVM PVs. LVM spans PVs; the stripe adds only blast radius |
| `/archive` (223 realloc, 90% full) | Freeze. No new writes. Evacuate later |
| `sdf` (916 G, empty) | Leave unassigned for now |

## End state

```
sdc      850 EVO   466G   /  /var  /boot  /boot/efi          healthiest disk, unchanged
nvme1n1  970 EVO   932G   lv-postgres (heap) + lv-laplace (install prefix)
nvme0n1  600p      239G   lv-pgwal 96G + lv-web (Dynmap) + lv-swap + lv-gaming
sda+sdb  894G      two plain PVs, NO stripe → CI _work + redis + scratch
sde      /vault    3.7T   models + data
sdd      /archive  1.9T   frozen
sdf      916G      free
```

Every device holds what matches its reliability. Nothing that can't be regenerated
sits on the two untrustworthy SSDs.

---

## Phase 0 — Postgres live (~5 min)

```bash
cd /home/ahart/Projects/Laplace
sudo install -d -m 0700 -o laplace-runner -g laplace-runner /var/lib/pgwal
sudo systemctl stop laplace-postgresql; sudo systemctl reset-failed laplace-postgresql
sudo rm -rf /opt/laplace/pgdata/data /opt/laplace/pg_wal/pg_wal
sudo LAPLACE_OPERATOR=ahart bash scripts/bootstrap-laplace-runner.sh bootstrap
```

Expect:
```
✓ WAL volume /var/lib/pgwal — separate spindle, 101GB free
✓ initdb'd /opt/laplace/pgdata/data (WAL on /var/lib/pgwal)
```

`✓ Cluster already initialized` instead means the `rm -rf` didn't take — stop.

Verify:
```bash
systemctl is-active laplace-postgresql
readlink -f /opt/laplace/pgdata/data/pg_wal     # must be /var/lib/pgwal
```

## Phase 1 — Reclaim models, move the install prefix (~20 min)

```bash
# drop lv-models — 512G back, instant (data already verified on /vault)
sudo umount /data/models
sudo sed -i.bak '\|/data/models|d' /etc/fstab
sudo lvremove -y vg-raid/lv-models
sudo rmdir /data/models

# quiesce anything holding /opt/laplace
sudo systemctl stop laplace-api laplace-postgresql
sudo systemctl stop 'actions.runner.*'
sudo fuser -vm /opt/laplace

# reclaim neo4j, carve the prefix volume on nvme1n1
sudo umount /var/lib/neo4j
sudo sed -i.bak '\|/var/lib/neo4j|d' /etc/fstab
sudo lvremove -y vg-data/lv-neo4j
sudo lvcreate -L 64G -n lv-laplace vg-data
sudo mkfs.xfs /dev/vg-data/lv-laplace

# copy (pgdata is a nested mount — exclude it)
sudo mkdir -p /mnt/newlaplace
sudo mount /dev/vg-data/lv-laplace /mnt/newlaplace
sudo rsync -aHAX --info=progress2 --exclude='pgdata/***' /opt/laplace/ /mnt/newlaplace/

# swap
sudo umount /opt/laplace/pgdata /mnt/newlaplace /opt/laplace
sudo mount /dev/vg-data/lv-laplace /opt/laplace
sudo mount /dev/mapper/vg--data-lv--postgres /opt/laplace/pgdata
sudo sed -i 's|/dev/mapper/vg--raid-lv--laplace|/dev/mapper/vg--data-lv--laplace|' /etc/fstab

# VERIFY BEFORE DESTROYING
findmnt /opt/laplace /opt/laplace/pgdata
ls -l /opt/laplace/pgsql-18/bin/postgres
sudo lvremove -y vg-raid/lv-laplace

sudo systemctl start laplace-postgresql laplace-api 'actions.runner.*'
```

Side benefit: the 86 MB `laplace_t0_perfcache` blob now cold-loads from NVMe
instead of the array.

## Phase 2 — WAL onto dedicated NVMe (~20 min)

Frees `nvme0n1` by moving CI `_work` to the array, then carves a real WAL volume.

```bash
# move CI _work to the array (expendable by design)
sudo systemctl stop 'actions.runner.*'
sudo lvcreate -L 64G -n lv-agents vg-raid
sudo mkfs.xfs /dev/vg-raid/lv-agents
sudo mkdir -p /mnt/newagents && sudo mount /dev/vg-raid/lv-agents /mnt/newagents
sudo rsync -aHAX --info=progress2 /var/lib/agents/ /mnt/newagents/
sudo umount /mnt/newagents /var/lib/agents
sudo mount /dev/vg-raid/lv-agents /var/lib/agents
sudo sed -i 's|vg--hosting-lv--agents|vg--raid-lv--agents|' /etc/fstab
sudo lvremove -y vg-hosting/lv-agents          # nvme0n1 now has ~110G free

# carve WAL
sudo lvcreate -L 96G -n lv-pgwal vg-hosting
sudo mkfs.xfs /dev/vg-hosting/lv-pgwal

# relocate WAL with the cluster stopped
sudo systemctl stop laplace-postgresql
sudo install -d -m 0700 -o laplace-runner -g laplace-runner /mnt/pgwal
sudo mount /dev/vg-hosting/lv-pgwal /mnt/pgwal
sudo rsync -aHAX /var/lib/pgwal/ /mnt/pgwal/
sudo umount /mnt/pgwal
echo "UUID=$(sudo blkid -s UUID -o value /dev/mapper/vg--hosting-lv--pgwal)  /var/lib/pgwal  xfs  defaults  0  2" | sudo tee -a /etc/fstab
sudo umount /var/lib/pgwal && sudo mount /var/lib/pgwal
sudo chown laplace-runner:laplace-runner /var/lib/pgwal
sudo systemctl start laplace-postgresql laplace-api 'actions.runner.*'
```

Path stays `/var/lib/pgwal`, so no script change needed — it's a mount now, not a
directory on `/var`.

## Phase 3 — Break the stripe (~10 min)

Only `lv-redis` and `lv-agents` remain on the array at this point.

```bash
# park redis + agents, empty the VG
sudo systemctl stop redis-server 'actions.runner.*'
sudo umount /var/lib/redis /var/lib/agents
sudo lvremove -y vg-raid/lv-redis vg-raid/lv-agents
sudo vgremove -y vg-raid
sudo pvremove -y /dev/md127
sudo mdadm --stop /dev/md127
sudo mdadm --zero-superblock /dev/sda /dev/sdb
sudo sed -i '/md-raid/d' /etc/mdadm/mdadm.conf 2>/dev/null

# two independent PVs, one VG, no stripe
sudo pvcreate /dev/sda /dev/sdb
sudo vgcreate vg-scratch /dev/sda /dev/sdb
sudo lvcreate -L 64G -n lv-agents vg-scratch /dev/sda
sudo lvcreate -L 64G -n lv-redis  vg-scratch /dev/sdb
sudo mkfs.xfs /dev/vg-scratch/lv-agents
sudo mkfs.xfs /dev/vg-scratch/lv-redis
# update /etc/fstab to the vg-scratch paths, remount, restart services
sudo update-initramfs -u
```

`lvcreate ... vg-scratch /dev/sda` pins an LV to one PV — so a single disk failure
costs only the LVs on that disk, not the VG.

## Phase 4 — Later

- **`/archive` evacuation.** 1.7 TB at 90% on `sdd` (223 reallocated sectors).
  `/vault` has room. Highest remaining risk on the box.
- **Harden `pg_prepare_waldir()`** to reject non-redundant devices — walk
  `lsblk -nrso NAME` and fail on `raid0`/`linear` in `/sys/block/*/md/level`.
  Verified working detection; not yet written in.
- **Env-var consolidation.** 53 `:-` defaults, 11 `$ENV{}` reads in CMake, nested
  sudo stripping vars. One generated `laplace.env` sourced by scripts, systemd
  (`EnvironmentFile=`), CMake and CI.
- **`sdf`** (916 G, healthy, empty) — unassigned.

## Already done tonight

- USB enclosure root-caused (VIA VL805 LPM failure) and moved to ASMedia; survived
  two reboots mounted
- `usb-storage.quirks=174c:55aa:u` active — bays on `usb-storage`, `queue_depth=1`
- udev rule pinning enclosure hubs out of autosuspend
- smartd: nightly short + Saturday long tests, `smart-notify` (journal + wall,
  no MTA), postfix purged
- Cruft masked: Azure Arc ×4, Azure Pipelines agent, VMware tools, wpa_supplicant,
  multipathd, open-iscsi, switcheroo, docker, containerd, ollama
- `LAPLACE_PG_WAL` default moved off the RAID0
- `wait-for-quiet-substrate.sh`: a stopped cluster is now proven quiet via systemd
  instead of spinning the full 5 h budget
