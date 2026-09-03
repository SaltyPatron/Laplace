# Dataset estate refresh operator

The release/provenance authority remains
[`DATASET_ESTATE_MODERNIZATION.md`](DATASET_ESTATE_MODERNIZATION.md). The operator here is
only its acquisition/staging helper; it is intentionally unable to promote or delete an
active dataset.

## Quick start

```bash
cd ~/Projects/Laplace-Legacy
git switch main
git pull --ff-only

# Record the bytes already staged by the 2026-09-03 audit/download session.
scripts/dataset-estate-refresh.sh adopt-existing

# Show what the executable manifest will acquire.
scripts/dataset-estate-refresh.sh plan all

# Optional transitional step: update only known CLEAN Git worktrees in place.
# Dirty, missing, or diverged trees are printed as SKIP and are never reset.
scripts/dataset-estate-refresh.sh refresh-clean-repos

# Start normal-size selected lanes. These survive the terminal through nohup.
scripts/dataset-estate-refresh.sh start semantic
scripts/dataset-estate-refresh.sh start mutable
scripts/dataset-estate-refresh.sh start geo
scripts/dataset-estate-refresh.sh start safety

scripts/dataset-estate-refresh.sh status
scripts/dataset-estate-refresh.sh wait
scripts/dataset-estate-refresh.sh verify all
```

`start all` is shorthand for all four normal lanes. Because it starts one background job
per artifact, use lane-by-lane starts when bandwidth or upstream rate limits matter.

Full six-men Syzygy is deliberately separate:

```bash
scripts/dataset-estate-refresh.sh disk
scripts/dataset-estate-refresh.sh start syzygy6
scripts/dataset-estate-refresh.sh status
```

The Syzygy worker downloads one file at a time, resumes partial files, and verifies every
`.rtbw`/`.rtbz` against the upstream `syzygy1/tb` six-men MD5 rosters. It expects 730
verified files before reporting success. Seven-men is not part of the operator because the
vault capacity decision explicitly excludes it.

## Files written under the refresh root

Default refresh root: `/vault/Data/.refresh-20260903`. Override it with
`LAPLACE_DATA_REFRESH_ROOT`.

- `.jobs/<id>.pid`, `.log`, `.rc` — durable background-job state.
- `.git-cache/` — acquisition-only Git caches; never an admitted source tree.
- `REFRESH_RECEIPT.tsv` — completed artifact observations.
- `DOWNLOADS.local.sha256` — local byte-identity snapshot from `adopt-existing`.
- `STAGING_LOCAL.tsv` — relative path, byte size and SHA-256 inventory.
- dataset-specific downloaded archives/snapshots from
  `scripts/dataset-estate-refresh.sources.psv`.

A Git source is resolved to an exact commit before it is staged. Normal Git repositories
become immutable `tar.gz` snapshots. Git-LFS dataset repositories become detached copied
snapshot trees with `.git` excluded and a per-file SHA-256 manifest.

## Safety boundary

This script has no `promote`, `replace`, or `delete` subcommand. A completed background
job means only that an artifact was staged and passed the checks encoded for that source.
Promotion still requires the release/license/schema/archive/tree reconciliation and removal
receipt specified in `DATASET_ESTATE_MODERNIZATION.md` and the owning GitHub issue.
