# ADR 0028: Custom-built PostgreSQL 18 + PostGIS 3.6.3 with Intel toolchain

## Status

**Accepted** — 2026-05-21

## Context

The substrate currently runs against stock Debian/Ubuntu packages of PostgreSQL 18 and PostGIS 3.6.3 (`postgresql-18`, `postgresql-18-postgis-3`, `postgresql-18-postgis-3-scripts`). This works in principle but has surfaced recurring problems on hart-server:

1. **Mismatched package state.** On hart-server, `/usr/share/postgresql/18/extension/postgis.control` declared `default_version = '3.7.0dev'` (apparently from a prior manual install) while the loaded `.so` was the 3.6.3 package version — missing the `ST_MMin` symbol the 3.7.0dev `.sql` script referenced. `apt --reinstall postgresql-18-postgis-3` did NOT replace `postgis.control` (apt's `update-alternatives` warning: "*not replacing /usr/share/postgresql/18/extension/postgis.control with a link*"), leaving the stale file in place.
2. **Compiler regime.** Stock packages are built with gcc, optimization tuned for distribution-target heterogeneity (typically `-O2`, no aggressive vectorization). The substrate's performance regime ([memory: laplace-performance](../../memory/project_laplace_performance.md)) targets `icx`/`icpx` with `-march={haswell|sapphirerapids}` for AVX2/AVX-512 native dispatch.
3. **Integration with Intel oneMKL.** PostGIS includes geometric algorithms (e.g., GEOS-backed distance, area, KNN comparators) that could benefit from MKL-backed BLAS when shape complexity grows. Stock builds don't enable that.
4. **Reproducibility.** Stock package versions can shift under us via apt upgrades. Custom build pinned to a release tag gives us bit-stable behavior.
5. **Substrate determinism.** Substrate determinism ([RULES.md R7](../../RULES.md)) depends on the entire stack behaving identically across machines. Stock packages can ship different revisions on different hosts.

A custom build from git submodules with the Intel toolchain solves all of the above and aligns the database layer with the substrate's CPU-native performance posture.

## Decision

Build PostgreSQL 18 and PostGIS 3.6.3 from source as git submodules under `external/`, compiled with `icx`/`icpx`, installed to `/opt/laplace/pgsql-18/`. The custom build coexists with stock packages (different prefix, different port) so the migration is non-destructive.

### Submodule pinning

| Submodule | Path | Pinned to |
|---|---|---|
| PostgreSQL | `external/postgresql/` | `REL_18_0` (the GA release tag) — bumped per release with an ADR amendment |
| PostGIS | `external/postgis/` | `3.6.3` (matches what currently works on hart-server) |

GEOS, PROJ, GDAL remain as system packages for now (their build chains are deep and not on our critical performance path). If a need surfaces to control any of them, we'll submodule them then.

### Build configuration

#### PostgreSQL

```sh
cd external/postgresql
./configure \
    CC=icx CXX=icpx \
    CFLAGS="-O3 -march=native" CXXFLAGS="-O3 -march=native" \
    --prefix=/opt/laplace/pgsql-18 \
    --with-icu --with-openssl --with-zlib --with-uuid=e2fs \
    --enable-thread-safety
make -j$(nproc) && sudo make install
```

#### PostGIS

```sh
cd external/postgis
./autogen.sh
./configure \
    CC=icx CXX=icpx \
    CFLAGS="-O3 -march=native" CXXFLAGS="-O3 -march=native" \
    --with-pgconfig=/opt/laplace/pgsql-18/bin/pg_config \
    --with-projdir=/usr --with-geosconfig=/usr/bin/geos-config \
    --with-gdalconfig=/usr/bin/gdal-config
make -j$(nproc) && sudo make install
```

### Cluster + systemd

- Cluster data directory: `/opt/laplace/pgsql-18/data/`
- Initialized via `/opt/laplace/pgsql-18/bin/initdb -D /opt/laplace/pgsql-18/data --locale=en_US.UTF-8 --encoding=UTF8`
- Port: **5433** (so the stock cluster on 5432 keeps working during migration)
- systemd unit: `laplace-postgres.service`, runs as a dedicated `laplace_pg` system user (no home, no shell), `ExecStart=/opt/laplace/pgsql-18/bin/postgres -D /opt/laplace/pgsql-18/data`
- `pg_hba.conf` + `pg_ident.conf` carry the same `laplace_map` peer-auth model as the stock cluster (mapping `laplace-runner` and `ahart` to PG role `laplace_admin`)

### Bootstrap script integration

`scripts/bootstrap-laplace-runner.sh` accepts `--pg-prefix /opt/laplace/pgsql-18` (or an env var) to select between custom and stock PG. Default: whichever path exists; explicit flag overrides. Both cluster paths can coexist on the same host; only one is the "active" target for Layer 1.

### Migration plan (substrate has no real data yet)

The substrate is at Chunk 0 framework completion. No real entity data exists. The migration is a clean start:

1. Build + install the custom PG cluster on hart-server.
2. Stop stock cluster (or leave running on port 5432).
3. Re-run `setup-host.sh` targeting `/opt/laplace/pgsql-18`.
4. Future Chunk-3 perf-cache seed populates the custom cluster's `laplace` DB from scratch.

No `pg_dump` / `pg_restore` ceremony needed at this stage.

## Consequences

- **Recurring package mismatch class of failure eliminated.** No more apt half-upgrades leaving control/SQL/`.so` out of sync.
- **Performance regime aligned at the DB layer.** Postgres + PostGIS compiled with the same `icx`/`icpx` + AVX2/AVX-512 dispatch as the engine.
- **Reproducible builds.** Pin to a tag; rebuild is identical across machines given the same submodule SHAs.
- **Custom build is parallelizable with Chunks 1-3.** Doesn't block engine math, geometry serde, or T0 seed work (which can target either cluster).
- **More build-system surface to maintain.** Two new scripts (`scripts/build-pg.sh`, `scripts/build-postgis.sh`), one systemd unit, port allocation discipline. Documented in OPERATIONS.md.
- **CI matrix expansion.** `integration.yml` gains a (initially opt-in via `workflow_dispatch` input) "custom PG" matrix entry. Becomes the default once stable.
- **GEOS/PROJ/GDAL stay system-packaged.** If we ever need a specific feature of one (e.g., PROJ 9.5 features not yet packaged), we'll submodule then, not preemptively.

## Alternatives considered

- **Stay on stock packages, clean up `postgis.control` manually when it drifts.** Rejected — operational toil; no performance alignment; not reproducible.
- **Submodule GEOS/PROJ/GDAL too.** Deferred — adds build chain complexity (GDAL alone has dozens of optional drivers). Revisit if/when a specific feature requirement appears.
- **Use Docker for PG + PostGIS to control the build.** Rejected — adds container runtime layer between substrate and host; substrate's IPC + shared library model wants direct PG access; CI runner is already host-native.
- **Build with gcc instead of icx/icpx.** Rejected — Intel compilers produce measurably better AVX-512 code on substrate-shape workloads (per Intel published benchmarks for oneMKL+icx vs gcc), and we're already paying for oneAPI for the engine.

## References

- [PostgreSQL 18 build documentation](https://www.postgresql.org/docs/current/install-make.html)
- [PostGIS 3.6 build documentation](https://postgis.net/documentation/getting_started/#installation)
- [Intel oneAPI 2026 — `icx`/`icpx` compilers](https://www.intel.com/content/www/us/en/developer/tools/oneapi/dpc-compiler.html)
- [PostgreSQL — Custom port + alternate clusters](https://www.postgresql.org/docs/current/server-start.html)
- ADR 0001 (extend PostGIS via Z+M)
- ADR 0015 (BLAKE3-128 truncated — also FetchContent-vendored for control)
- ADR 0018 (three-layer architecture — this lands in Layer 0)
- ADR 0019 (`laplace-runner` system account)
- ADR 0030 (MKL/Eigen/Spectra/TBB integration — same Intel toolchain regime)
- `external/postgresql/`, `external/postgis/` (submodule paths)
- `scripts/build-pg.sh`, `scripts/build-postgis.sh`
- `/opt/laplace/pgsql-18/` (install prefix)
- `OPERATIONS.md` — operational docs for the custom-build path
