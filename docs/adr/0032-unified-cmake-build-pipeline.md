# ADR 0032: Unified CMake build pipeline (Path B) — PGXS retired in favor of one tree

## Status

**Accepted** — 2026-05-22

Locks in [ADR 0028](0028-custom-built-pg-postgis-intel.md) as a hard prerequisite (not parallel/deferrable). Retires PGXS for our two extensions once the custom PG build lands.

## Context

The build pipeline today is three systems running in parallel:

1. **CMake + Ninja** for `engine/` → three `.so` files in `engine/build/`
2. **PGXS** (PostgreSQL's bespoke Makefile build) for `extension/` → `laplace.so` + `.control` + `.sql` install
3. **dotnet** CLI for `app/Laplace.slnx`

Plus a top-level Justfile orchestrating them, and DbUp for deploy-time migrations.

This works but creates seams. The extension/Makefile (PGXS) has to know engine/build's output paths via relative-path includes and `SHLIB_LINK` hand-coding. PGXS is Make-based (no Ninja parallelism). Building two extensions (`laplace_geom` + `laplace_substrate` per ADR 0025) means two PGXS Makefiles. Cross-language CI integration is awkward.

The deeper issue: PGXS exists to encapsulate "what does the stock PG cluster expect" — install paths (`/usr/share/postgresql/18/extension/`), compiler flags matching what PG itself was built with, `.control` file conventions. Once we own the PG build (per ADR 0028 — custom-built PG + PostGIS with Intel toolchain), we KNOW what PG expects because we built it. PGXS's encapsulation becomes a layer we don't need.

[ADR 0028](0028-custom-built-pg-postgis-intel.md) was originally framed as parallel to Chunks 1-3 (deferrable). Anthony's 2026-05-22 redirect makes it a hard prerequisite for two reasons:

1. **Performance.** Stock PG + PostGIS are built with gcc + `-O2`, no aggressive vectorization. The substrate's regime ([memory: laplace-performance](../../memory/project_laplace_performance.md)) targets `icx`/`icpx` with `-march=${LAPLACE_TARGET_ISA}` for AVX2/AVX-512 dispatch. Stock packages leave that performance off the table.
2. **Correctness — "code against the repo itself."** When an error fires ("`ST_MMin` not found in postgis-3.so") and the answer is "the package on this host is mismatched with what the .control file says," that's a class of failure caused by apt managing a build we don't own. With `external/postgresql/` + `external/postgis/` as submodules, errors point at actual code we can read. Anthony's verbatim: *"Why did this fail... oh... this code expects this."*

Both reasons compound. Once Epic B lands, the build pipeline is naturally unified under CMake.

## Decision

Adopt the **unified CMake build pipeline (Path B)** with the following layout, contingent on Epic B (ADR 0028) being treated as a prerequisite rather than a parallel track:

```
laplace/
├── CMakeLists.txt                       ← top-level orchestrator
├── external/                            ← Git submodules per ADR 0028
│   ├── postgresql/                      ← PG 18 release tag (autoconf/make,
│   │                                      driven by top-level CMake via
│   │                                      ExternalProject_Add or custom cmd)
│   └── postgis/                         ← PostGIS 3.6.3 (same)
├── engine/                              ← CMake (per ADR 0024)
│   ├── core/
│   ├── dynamics/
│   ├── synthesis/
│   └── CMakeLists.txt                   ← subordinated to top-level
└── extension/                           ← CMake (replaces PGXS per this ADR)
    ├── laplace_geom/
    │   ├── CMakeLists.txt               ← extension build via custom CMake,
    │   │                                  install paths from our PG build
    │   ├── src/laplace_geom.c
    │   ├── laplace_geom.control
    │   └── laplace_geom--0.1.0.sql
    └── laplace_substrate/
        ├── CMakeLists.txt
        ├── src/laplace_substrate.c
        ├── laplace_substrate.control
        └── laplace_substrate--0.1.0.sql
```

### CMake structure

**Top-level `CMakeLists.txt`:**

```cmake
cmake_minimum_required(VERSION 3.20)
project(laplace LANGUAGES C CXX)

# Build/runtime targets
set(LAPLACE_TARGET_ISA "AVX2" CACHE STRING "AVX2 (default dev) | AVX512 (deploy)")
set(LAPLACE_PG_PREFIX  "${CMAKE_INSTALL_PREFIX}/pgsql-18" CACHE PATH
    "Where the custom PG cluster is installed (default: under CMAKE_INSTALL_PREFIX)")

# Step 1: Custom PostgreSQL + PostGIS (Epic B / ADR 0028)
add_subdirectory(external)

# Step 2: Engine (per ADR 0024)
add_subdirectory(engine)

# Step 3: Extensions (replaces PGXS per this ADR)
add_subdirectory(extension)
```

**`external/CMakeLists.txt`** drives autoconf/make for postgresql + postgis via custom commands (NOT `ExternalProject_Add`, which has parallelism quirks — use targeted `add_custom_command` + `add_custom_target` with explicit dependencies on the autoconf output files).

**`extension/laplace_geom/CMakeLists.txt`** (replaces the Makefile):

```cmake
add_library(laplace_geom MODULE src/laplace_geom.c)

target_include_directories(laplace_geom PRIVATE
    ${LAPLACE_PG_PREFIX}/include/postgresql/server
    ${LAPLACE_PG_PREFIX}/include/postgresql/server/internal)

target_link_libraries(laplace_geom PRIVATE laplace_core)
target_link_options(laplace_geom PRIVATE "-Wl,-rpath,${LAPLACE_ENGINE_LIBDIR}")

set_target_properties(laplace_geom PROPERTIES PREFIX "")   # `laplace_geom.so`, not `liblaplace_geom.so`

# Custom install rules that replicate PGXS's behavior (using our known PG prefix):
install(TARGETS laplace_geom DESTINATION ${LAPLACE_PG_PREFIX}/lib/postgresql)
install(FILES laplace_geom.control DESTINATION ${LAPLACE_PG_PREFIX}/share/postgresql/extension)
install(FILES laplace_geom--0.1.0.sql DESTINATION ${LAPLACE_PG_PREFIX}/share/postgresql/extension)

# pg_regress integration — driven by CTest, not by PGXS's installcheck
add_test(NAME laplace_geom_regress
         COMMAND ${LAPLACE_PG_PREFIX}/lib/postgresql/pgxs/src/test/regress/pg_regress
                 --bindir=${LAPLACE_PG_PREFIX}/bin
                 --inputdir=${CMAKE_CURRENT_SOURCE_DIR}/tests
                 --temp-instance=${CMAKE_BINARY_DIR}/regress-temp
                 ${REGRESS_TESTS})
```

### Build invocation

One command does everything:

```sh
cmake -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_INSTALL_PREFIX=/opt/laplace \
      -DLAPLACE_TARGET_ISA=AVX2 \
      -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icpx
cmake --build build -j$(nproc)
cmake --install build
```

Output: `/opt/laplace/pgsql-18/{bin,lib,share}/` plus the substrate extensions installed into the correct paths under that prefix.

### Migration from PGXS

The transition has two phases:

**Phase 1 (bridge, while still on stock PG):** Keep PGXS Makefiles in `extension/{laplace_geom,laplace_substrate}/`. Top-level CMake exists but only orchestrates `engine/`. Justfile runs `cmake --build` + `make install` (PGXS) sequentially. Relative paths from extension Makefile to `engine/build/core/liblaplace_core.so` are generated by a small CMake helper macro to avoid hand-coding.

**Phase 2 (full unification, after Epic B lands):** Top-level CMake takes over. PGXS Makefiles deleted; replaced by `extension/{laplace_geom,laplace_substrate}/CMakeLists.txt`. PGXS no longer in the build graph. Justfile shrinks to thin wrappers over `cmake --build` / `cmake --install`.

The transition itself is a Story in Epic A's refactor work (or a small standalone Epic if it grows).

## Consequences

- **One build system.** One mental model. New contributors don't have to learn PGXS + CMake + dotnet conventions simultaneously.
- **Engine→extension dependency is a CMake target dependency**, not a relative path comment in a Makefile. `target_link_libraries(laplace_geom PRIVATE laplace_core)` is the contract; the build graph enforces it.
- **Ninja parallelism end-to-end.** PGXS's Make-based serial build becomes irrelevant.
- **`pg_regress` integration via CTest** — substrate-test discoverable alongside engine ctest tests.
- **Install paths are explicit.** No reliance on `pg_config --pkglibdir` reading from a system PG we don't control; we set `LAPLACE_PG_PREFIX` and install there.
- **Performance regime aligned.** Same `icx`/`icpx` + `-march` flags for engine AND PG AND extensions AND substrate (when applicable). One compiler + ISA story.
- **PGXS code paths gone from our maintenance surface.** What PGXS does for us we now do explicitly in our CMakeLists — auditable, debuggable, no Makefile-shell-quoting traps.
- **Cost: more CMake code.** ~100 LOC of CMake per extension to replicate what PGXS does in 10 LOC of Makefile. Worth it for the unification.
- **Risk: PG conventions changing.** PG-extension conventions change rarely (PGXS's interface has been stable since ~PG 9), but if they do change, we have to update our CMake helpers instead of getting it for free via `$(pg_config --pgxs)`. Acceptable.

## Alternatives considered

- **Path A: keep PGXS for extensions, CMake for engine (status quo, refined).** Rejected for the post-Epic-B world. Acceptable as the Phase-1 bridge only.
- **Path C: CMake drives PGXS via `ExternalProject_Add`.** Considered. Rejected because it preserves PGXS as a transitive dependency without gaining the unification benefit.
- **Bazel / Buck / Meson.** Considered. Rejected — would require porting everything; CMake is already in use and has the necessary primitives.

## References

- [PostgreSQL — PGXS](https://www.postgresql.org/docs/current/extend-pgxs.html)
- [CMake — Modern CMake recommendations](https://cliutils.gitlab.io/modern-cmake/)
- [PostgreSQL — pg_config](https://www.postgresql.org/docs/current/app-pgconfig.html)
- ADR 0024 (engine modularization)
- ADR 0025 (PG extension modularization)
- [ADR 0028](0028-custom-built-pg-postgis-intel.md) — custom PG + PostGIS; locked in as prerequisite by this ADR
- ADR 0030 (MKL/Eigen/Spectra/TBB integration — same Intel toolchain regime)
- `engine/CMakeLists.txt`, `extension/{laplace_geom,laplace_substrate}/CMakeLists.txt` (after Phase 2)
