# ADR 0030: MKL / Eigen / Spectra / TBB integration + determinism via MKL_CBWR

## Status

**Accepted** — 2026-05-21
**Amended** — 2026-05-22: Eigen acquisition switched from `apt install libeigen3-dev` to git submodule at `external/eigen/` (3.4.0 release tag). Spectra acquisition switched from CMake `FetchContent` to git submodule at `external/spectra/` (v1.2.0 release tag). Both per [ADR 0033](0033-all-deps-as-submodules.md). The MKL+TBB integration story is unchanged — both are oneAPI vendor components and remain at `/opt/intel/oneapi/`.

## Context

[ADR 0024](0024-engine-modularization.md) splits the engine into three libraries with deliberately different dependency footprints — `liblaplace_core` (no heavy linalg), `liblaplace_dynamics` (oneMKL + Spectra + TBB), `liblaplace_synthesis` (inherits dynamics). The integration story across Eigen, oneMKL, Spectra, and oneTBB needs to be made explicit so:

1. The PG backend (linking `liblaplace_core`) doesn't accidentally pull in oneMKL.
2. `liblaplace_dynamics` actually gets the speedup oneMKL is supposed to provide (Eigen dispatching its dense ops to MKL; Spectra's iterative methods running on MKL-backed dense kernels).
3. Threading is unified — MKL doesn't spawn its own OpenMP threads competing with TBB worker arenas.
4. Substrate determinism ([RULES.md R7](../../RULES.md)) holds despite parallel reductions in MKL (which are non-deterministic across thread counts by default).

These libraries are designed to compose, but only if you wire them up explicitly with the right compile-time defines, runtime initialization calls, and CMake `find_package` invocations. Defaults give you "they all work" but not "they all work *together*."

## Decision

### Compile-flag matrix per engine library

| Library | Eigen flags | Links | Threading layer | ISA |
|---|---|---|---|---|
| `liblaplace_core` | `EIGEN_NO_DEBUG`, `EIGEN_DONT_PARALLELIZE` | Eigen (submodule, INTERFACE); BLAKE3 (submodule via `add_subdirectory`) | None (caller-driven) | `-march=${LAPLACE_TARGET_ISA}` |
| `liblaplace_dynamics` | `EIGEN_USE_MKL_ALL`, `EIGEN_USE_BLAS`, `EIGEN_USE_LAPACKE`, `MKL_LP64`, `MKL_THREADING_TBB` | `liblaplace_core`; Spectra; `MKL::MKL`; `TBB::tbb` | TBB (via `mkl_set_threading_layer(MKL_THREADING_TBB)`) | Same `-march` as core |
| `liblaplace_synthesis` | Inherits dynamics | `liblaplace_core`; `liblaplace_dynamics` | Inherits | Same |

Concrete CMake (in `engine/dynamics/CMakeLists.txt`):

```cmake
add_library(laplace_dynamics SHARED ${LAPLACE_DYNAMICS_SOURCES})

target_link_libraries(laplace_dynamics
    PUBLIC  laplace_core Spectra
    PRIVATE MKL::MKL TBB::tbb)

target_compile_definitions(laplace_dynamics PRIVATE
    EIGEN_USE_MKL_ALL
    EIGEN_USE_BLAS
    EIGEN_USE_LAPACKE
    MKL_LP64
    MKL_THREADING_TBB)
```

Top-level `engine/CMakeLists.txt`:

```cmake
find_package(MKL CONFIG REQUIRED)  # provided by oneAPI's MKLConfig.cmake
find_package(TBB CONFIG REQUIRED)  # provided by oneAPI

# Eigen, Spectra, BLAKE3 are git submodules per ADR 0033.
# Eigen is INTERFACE-only (header-only); Spectra is INTERFACE-only and
# depends on Eigen; BLAKE3 has its own CMakeLists.txt under c/.
add_library(laplace_eigen INTERFACE)
target_include_directories(laplace_eigen INTERFACE
    "${LAPLACE_EXTERNAL}/eigen")

add_library(laplace_spectra INTERFACE)
target_include_directories(laplace_spectra INTERFACE
    "${LAPLACE_EXTERNAL}/spectra/include")
target_link_libraries(laplace_spectra INTERFACE laplace_eigen)

add_subdirectory("${LAPLACE_EXTERNAL}/blake3/c"
    "${CMAKE_BINARY_DIR}/_deps/blake3" EXCLUDE_FROM_ALL)
```

### Runtime initialization

`liblaplace_dynamics` exposes a `laplace_dynamics_init(void)` C function that runs once at process startup (called by the C# binding's static constructor):

```c
extern "C" int laplace_dynamics_init(void) {
    // Force MKL to use TBB for threading (unifies with our scheduler).
    mkl_set_threading_layer(MKL_THREADING_TBB);
    // MKL Conditional Bitwise Reproducibility — locks reduction order
    // regardless of thread count. ~5-10% perf cost; required for substrate
    // determinism per RULES.md R7.
    int rc = mkl_cbwr_set(LAPLACE_MKL_CBWR_MODE);
    return rc == MKL_CBWR_SUCCESS ? 0 : -1;
}
```

Where `LAPLACE_MKL_CBWR_MODE` is a compile-time define selected by the `LAPLACE_TARGET_ISA` CMake option:

| `LAPLACE_TARGET_ISA` | `LAPLACE_MKL_CBWR_MODE` | Target hardware |
|---|---|---|
| `AVX2` (default; dev workstation) | `MKL_CBWR_AVX2` | Haswell, Broadwell-E, Skylake-X, Cascade Lake |
| `AVX512` (deployment target) | `MKL_CBWR_AVX512` | Sapphire Rapids, Granite Rapids, Genoa |
| `AUTO` (let MKL pick — non-deterministic) | `MKL_CBWR_AUTO` | Only for non-determinism-required workflows |

Default is `AVX2` for hart-server compatibility; production deployment builds set `AVX512`.

### Determinism verification

A dedicated ctest case runs Procrustes alignment on a deterministic input matrix with varying thread counts (`TBB_NUM_THREADS={1,2,4,8}`) and asserts byte-identical output. Failure here means MKL_CBWR isn't applied correctly somewhere — a regression caught at CI.

### What `liblaplace_core` must NOT link

To preserve PG-backend leanness, `liblaplace_core` MUST NOT transitively pull in MKL or Spectra. Build-time check: `ldd ${BUILD_DIR}/core/liblaplace_core.so` must show NO `libmkl_*.so` references. If a future PR adds MKL to core, the check fails CI before merge.

## Consequences

- **PG backend stays lean.** `laplace_geom` and `laplace_substrate` PG extensions link only `liblaplace_core`. PG worker processes don't mmap MKL's hundreds-of-MB runtime.
- **C# ingest pipeline gets the full speedup.** `Laplace.Sources.Transformer` (Procrustes-time) calls into `liblaplace_dynamics`; Eigen's dense ops dispatch to MKL automatically. SVD on 4096×4096 hidden-dim matrices goes from minutes to seconds.
- **One scheduler.** TBB powers MKL's parallelism AND our own task graphs. No nested-parallelism oversubscription. No thread-count tuning required per-call.
- **Substrate determinism preserved across thread counts.** MKL_CBWR locks the reduction order; same input → byte-identical output regardless of `nproc`. ~5-10% perf cost on heavy GEMM workloads, accepted.
- **Spectra inherits MKL acceleration for free.** Spectra is built on Eigen; when Eigen has MKL backend, Spectra's iterative-method internal dense ops flow through MKL automatically. No separate "Spectra+MKL" wiring needed.
- **Compile-time ISA selection** decouples the build product. AVX2 build runs on the dev box; AVX512 build deploys to Sapphire Rapids. Same source, different `LAPLACE_TARGET_ISA`.

## Alternatives considered

- **Don't use MKL; stay on Eigen-only.** Rejected — for 4096×4096+ SVD (Procrustes on real Transformer hidden dims), pure Eigen is minutes; MKL is seconds. Substrate ingest time matters.
- **Use MKL via direct calls; skip the Eigen integration.** Rejected — Eigen's `EIGEN_USE_MKL_ALL` is one line and handles BLAS+LAPACK+Sparse uniformly; direct MKL calls add boilerplate without benefit.
- **Use OpenMP as MKL's threading layer.** Rejected — OpenMP would oversubscribe alongside our TBB task arenas (MKL spawns its own OpenMP team); single TBB scheduler is cleaner.
- **Skip `MKL_CBWR` (accept non-deterministic reductions).** Rejected — substrate determinism is non-negotiable per RULES.md R7. 5-10% perf cost is a fair price for reproducibility.

## References

- [Intel oneMKL — Conditional Numerical Reproducibility (CBWR)](https://www.intel.com/content/www/us/en/docs/onemkl/developer-guide-linux/2024-0/conditional-numerical-reproducibility.html)
- [Intel oneMKL — Threading Layers](https://www.intel.com/content/www/us/en/docs/onemkl/developer-guide-linux/2024-0/threading.html)
- [Eigen — Using BLAS/LAPACK from MKL](https://eigen.tuxfamily.org/dox/TopicUsingIntelMKL.html)
- [Spectra — Built on Eigen, inherits backend choices](https://spectralib.org/)
- [Intel oneAPI — `MKLConfig.cmake`, `TBBConfig.cmake`](https://www.intel.com/content/www/us/en/developer/articles/technical/onemkl-cmake-config-files.html)
- ADR 0024 (engine modularization — this ADR realizes the dynamics-specific linkage)
- ADR 0028 (custom-built PG with Intel toolchain — same compiler regime)
- RULES.md R7 (determinism by construction) — driving the MKL_CBWR requirement
- RULES.md R8 (no GPU at runtime) — MKL is CPU-only here
- `engine/core/CMakeLists.txt`, `engine/dynamics/CMakeLists.txt`, `engine/synthesis/CMakeLists.txt`
