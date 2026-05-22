# ADR 0038: Unified deps CMake pipeline; gcc toolchain for system deps

## Status

**Accepted** — 2026-05-23

Amends:
- [ADR 0028](0028-custom-built-pg-postgis-intel.md) — narrows the "Intel toolchain" scope from "everything" to "the engine only."
- [ADR 0032](0032-unified-cmake-build-pipeline.md) — extends Path B with a sibling deps-only CMake project.
- [ADR 0033](0033-all-deps-as-submodules.md) — keeps submodule policy; replaces per-dep shell scripts with one CMakeLists.

## Context

Two problems with the prior per-dep shell-script approach (`scripts/build-{proj,geos,gdal,pg,postgis,tree-sitter}.sh` + `scripts/build-all-deps.sh`):

**1. Each upstream CMakeLists ran in our outer CMake context, so upstream bugs leaked in.**

PROJ's `CMakeLists.txt:118-123`:

```cmake
if (CMAKE_CXX_COMPILER_ID STREQUAL "IntelLLVM")
  # Intel CXX compiler based on clang defaults to -ffast-math, which
  # breaks std::isinf(), std::isnan(), etc.
  set(CMAKE_C_FLAGS ${CMAKE_C_FLAGS} -fno-fast-math)
  set(CMAKE_CXX_FLAGS ${CMAKE_CXX_FLAGS} -fno-fast-math)
endif ()
```

The unquoted `${CMAKE_C_FLAGS}` expansion turns the variable into a CMake list (each whitespace-separated token becomes a list element). CMake's "Unix Makefiles" generator then emits that list with `;` as the separator into `flags.make`:

```
CXX_FLAGS = -O3 -march=haswell -fno-fast-math -ffp-contract=off;-fno-fast-math -O3 -DNDEBUG -fPIC ...
```

Shell parses the makefile compile rule as two commands separated by `;`: first runs `icpx [flags]` without a source file (`no input files`), second tries to execute `-fno-fast-math` as a command (`/bin/sh: -fno-fast-math: not found`). The bug branch is gated on `CMAKE_CXX_COMPILER_ID STREQUAL "IntelLLVM"` — gcc/clang go through a different code path with no bug.

**2. The Intel toolchain (icpx) was being used for the entire dep chain, but only earns its slot in `liblaplace_dynamics.so`.**

icpx is Intel's clang-based compiler. Its value lives in:

- oneMKL integration (Procrustes SVD, Spectra eigensolvers, BLAS-heavy paths)
- AVX-512 vectorization where the compiler can hoist intrinsics
- Cross-thread-count determinism with `mkl_cbwr`

What it does *not* meaningfully accelerate:

- PostgreSQL storage + query routing (we don't hot-path through PG internals; the substrate's hot path is in our SRFs)
- PostGIS / GEOS / GDAL geometry ops (2D-only; we dispatch 4D to liblwgeom inside the substrate)
- PROJ coordinate transforms (we don't use them — SRID=0; transitive cruft inherited via PostGIS)
- tree-sitter parser runtime (loaded lazily; not hot)

The original "Intel toolchain for everything" framing in ADR 0028 was a yak-shave-on-day-1 call. Standard practice splits "system deps via system compiler" from "performance-critical inner library via vendor optimized compiler" — e.g., PostgreSQL is built with the system compiler in every production deployment; oneMKL is shipped as Intel-pre-built `.so`s linked into whichever app needs them.

## Decision

### 1. Unified deps CMake pipeline via ExternalProject_Add

`external/CMakeLists.txt` is a new `project(laplace-deps LANGUAGES NONE)` driving every Layer-0.5 system dep via `ExternalProject_Add`:

- PROJ → GEOS → GDAL → tree-sitter → PostgreSQL → PostGIS
- Each dep's build runs as a fully isolated subprocess in its own CMake context. Upstream bugs cannot leak into our outer build.
- Mixed upstream build systems (CMake / autoconf / Make) normalized via explicit `CONFIGURE_COMMAND` / `BUILD_COMMAND` / `INSTALL_COMMAND` overrides.
- Stamp-based caching: `cmake --build build/deps` is a no-op when no submodule SHA changed.
- `DEPENDS` clauses encode ordering (gdal → proj; postgis → pg + geos + proj + gdal).

`scripts/build-{proj,geos,gdal,pg,postgis,tree-sitter}.sh` + `scripts/build-all-deps.sh` + `scripts/lib/oneapi-env.sh` are deleted. The `just build-deps` recipe becomes a two-line CMake invocation.

### 2. gcc for system deps; icpx for the engine

Two toolchain files now coexist:

- `cmake/toolchains/gcc-deterministic.cmake` — used by `external/CMakeLists.txt` for every system dep. System gcc/g++ with the same `-O3 -march=... -fno-fast-math -ffp-contract=off` determinism flags as before.
- `cmake/toolchains/intel-oneapi.cmake` — used by the engine top-level CMake for `liblaplace_core / dynamics / synthesis`. icx/icpx + oneMKL/TBB CMAKE_PREFIX_PATH.

The split is reflected in:

- Justfile: `just build-deps` uses gcc (via `external/CMakeLists.txt`); `just build` uses icpx (via `-DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake`).
- `.github/workflows/integration.yml`: same split, same invocations.

### 3. ABI compatibility is preserved

Every cross-component boundary is the C ABI (per RULES.md R14 + PG's `PG_FUNCTION_INFO_V1`). gcc-built PG loads icpx-built `laplace_geom.so` / `laplace_substrate.so`, which call into icpx-built `liblaplace_core/dynamics/synthesis.so`, which dlopens Intel-pre-built MKL. No C++ symbol mangling crosses any boundary; no ABI drift.

## Consequences

- **PROJ patch not needed.** The IntelLLVM-gated bug branch never fires under gcc. Submodule stays unmodified.
- **Build time shrinks ~2-3× on the dep chain.** gcc compiles faster than icpx for the same source.
- **Toolchain story simplifies.** One toolchain file per concern, one file per compiler. No `oneapi-env.sh` shim, no `setvars.sh` sourcing dance, no `set +u` workarounds for setvars's unbound-var quirks under `set -u`.
- **Engine performance unchanged.** icpx still applies where it earns its slot (MKL/Spectra/AVX-512 in dynamics).
- **CI semantics unchanged.** Same `just build-deps` runs in CI as locally; stamp-based caching keeps re-runs cheap.
- **Standard practice alignment.** Matches how PostgreSQL / PostGIS / GEOS / PROJ are built in every production deployment elsewhere.

## Alternatives considered

- **Patch PROJ's CMakeLists locally to quote the unsafe `set()` lines.** Rejected — adds maintenance burden (track upstream PROJ for fix; manage patch over submodule version bumps) and doesn't address the root question of whether icpx earns its slot for the system deps at all (it doesn't).
- **Keep per-dep shell scripts.** Rejected — duplication, no upstream-bug isolation, no stamp-based caching, no DEPENDS-encoded ordering.
- **Use `FetchContent` or `add_subdirectory` for the deps.** Rejected — these run the dep's CMakeLists in our outer CMake context, so the PROJ-style bug class would still leak in. `ExternalProject_Add` is what CMake designed for this exact case.
- **icpx for everything, including the deps.** Rejected per the analysis above — no measurable gain on the system deps; only adds attack surface for upstream Intel-gated bugs.

## References

- [ADR 0028](0028-custom-built-pg-postgis-intel.md) — amended
- [ADR 0030](0030-mkl-eigen-spectra-tbb-integration.md) — determinism flags pattern preserved
- [ADR 0032](0032-unified-cmake-build-pipeline.md) — Path B extended
- [ADR 0033](0033-all-deps-as-submodules.md) — submodule policy intact
- [RULES.md R7](../../RULES.md) — determinism by construction
- [RULES.md R14](../../RULES.md) — C ABI at engine boundaries (justifies safe gcc/icpx mixing)
- [`external/CMakeLists.txt`](../../external/CMakeLists.txt)
- [`cmake/toolchains/gcc-deterministic.cmake`](../../cmake/toolchains/gcc-deterministic.cmake)
- [`cmake/toolchains/intel-oneapi.cmake`](../../cmake/toolchains/intel-oneapi.cmake)
- PROJ upstream bug context: `external/proj/CMakeLists.txt:118-123` (commit 875a485f)
