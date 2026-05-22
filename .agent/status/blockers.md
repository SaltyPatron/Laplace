# Laplace — Open Blockers

Format: open blockers first (most recent at top). Resolved blockers move to a "Resolved" section at the bottom (chronological).

```
## YYYY-MM-DD — <blocker title>
**Severity:** blocking | high | medium | low
**Reported by:** <agent or user>
**Context:** <what we were trying to do>
**Diagnostic:** <what we found>
**Proposed resolution:** <or "open — needs investigation">
```

---

## Open

## 2026-05-21 — Spectra library not vendored yet
**Severity:** medium (needed for Laplacian eigenmaps in physicality pipeline, Chunk 6/7)
**Context:** Preparing for AI model ingestion pipeline implementation.
**Proposed resolution:** Per STANDARDS.md, Spectra ships via CMake `FetchContent` pinned to v1.2.0; integration happens during Chunk 6's `engine/dynamics/CMakeLists.txt` setup. Story D.3 (#161) tracks the linkage.

## 2026-05-21 — tree-sitter library not installed
**Severity:** medium (needed for code decomposition in `CodeDecomposer`, post-Chunk-7)
**Context:** Preparing for code ingestion via `IDecomposer` interface.
**Diagnostic:** `dpkg -l libtree-sitter-dev` returns no result.
**Proposed resolution:** `sudo apt install libtree-sitter-dev`. Defer until first code-ingestion source is implemented.

## 2026-05-21 — AVX-512 not available on dev machine
**Severity:** informational (not blocking; deployment-target consideration)
**Context:** Dev machine is i7-6850K (Broadwell-E), AVX2 only.
**Diagnostic:** `lscpu` shows `avx avx2`, no `avx512*`.
**Proposed resolution:** CMake option `LAPLACE_TARGET_ISA={AVX2,AVX512}` per ADR 0030 controls both `-march=` and the MKL_CBWR mode. Default AVX2 (dev workstation); AVX512 selectable for Sapphire Rapids deployment.

---

## Resolved

### 2026-05-21 — BLAKE3 standalone library not installed
**Resolved by:** ADR 0015 (BLAKE3-128 for entity hashing) — landed via `FetchContent` in the engine's `CMakeLists.txt`. Pinned to v1.5.4. No system install needed.

### 2026-05-21 — Layer-0 bootstrap not yet executed on hart-server
**Resolved by:** Anthony ran `sudo scripts/bootstrap-laplace-runner.sh bootstrap` on hart-server. All Layer-0 state (system account, runner, PG roles, peer auth, sudoers, postgis installation) is correctly in place.

### 2026-05-22 — Mismatched postgis package state on hart-server (`ST_MMin` not in `.so`)
**Resolved by:** `sudo apt --reinstall install postgresql-18-postgis-3 postgresql-18-postgis-3-scripts` followed by `sudo update-alternatives --auto postgresql-18-postgis.control` — restored the symlink chain so `postgis.control` points to the packaged 3.6.3 version instead of the orphan `3.7.0dev` file. Future-proof solution: ADR 0028 (custom-built PG + PostGIS with submodules) eliminates this failure class entirely.

### 2026-05-22 — Orphan `laplace` schema owned by `postgres` on hart-server
**Context:** Transitional state from commit `a689478` that briefly pre-created the schema (later reverted in `a9088a3`).
**Resolved by:** Anthony ran `sudo -u postgres psql -d laplace -c "DROP SCHEMA laplace CASCADE"`. Future installs of the `laplace` extension run via the `trusted = true` path (post-`1af5890`) which creates the schema with `laplace_admin` as owner.

### 2026-05-22 — DbUp can't `CREATE EXTENSION postgis` (requires SUPERUSER)
**Resolved by:** `laplace_priv.install_extension` SECURITY DEFINER wrapper (per `bootstrap_pg_database_and_postgis`) — runs as `postgres`, lets `laplace_admin` trigger postgis install through an allowlist-bounded gateway. Also: bootstrap now installs postgis DIRECTLY as `postgres` at first install (the wrapper is for laplace_admin's recovery path via db-nuke).

### 2026-05-22 — `laplace_priv` wrapper search_path placed `pg_catalog` first
**Context:** PostGIS install creates `geometry_dump` type without schema qualifier; the wrapper's `SET search_path = pg_catalog, public` made it resolve to `pg_catalog`, failing with `permission denied to create "pg_catalog.geometry_dump"`.
**Resolved by:** Reordered to `SET search_path = public, pg_catalog` — extension objects land in `public` as designed.

### 2026-05-22 — `permission denied for language c` during laplace extension install
**Context:** Even with `superuser = false` + `trusted = true` in `laplace.control`, the install hit a privilege error on `CREATE FUNCTION laplace_version() ... LANGUAGE C`.
**Resolved by:** Removed `superuser = false` from `laplace.control`. The two settings are conflicting — `superuser = false` tells PG "no elevation needed; install as calling user" which makes PG IGNORE the `trusted = true` hint. With the default `superuser = true` (implicit) + `trusted = true`, PG's trusted-elevation actually fires and the install script runs as bootstrap superuser (which has language-c USAGE). Verified the same setup works for stock `pgcrypto`.

### 2026-05-22 — Smoke-test `psql -tA -d laplace` defaulted to OS user as PG role
**Context:** My earlier global replace of `psql -d laplace` → `psql -d laplace -U laplace_admin` didn't match the `-tA` flag prefix in the smoke-test step.
**Resolved by:** Added `-U laplace_admin` to the smoke-test psql invocation. CI green end-to-end as of commit `ab8f62b`.
