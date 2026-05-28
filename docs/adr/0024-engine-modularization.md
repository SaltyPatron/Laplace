# ADR 0024: Engine modularization — `liblaplace_core` / `liblaplace_dynamics` / `liblaplace_synthesis`

## Status

**Accepted** — 2026-05-21

## Context

The original engine plan (implicit in [STANDARDS.md](../../STANDARDS.md) and the chunk plan) was a single shared library `liblaplace_engine.so` housing every C/C++ kernel — coord4d math, hash128 (BLAKE3), Hilbert curves, mantissa packing, geometry serde, Glicko-2 fixed-point arithmetic, A* cascade primitives, Procrustes alignment, Laplacian eigenmaps, Gram-Schmidt, lottery-ticket sparsity, recipe extraction, architecture templates, feature extractors, GGUF writing.

That single library would have grown to thousands of files with a dependency footprint spanning BLAKE3, Eigen, Spectra, Intel oneMKL, oneTBB, and substrate-specific I/O codecs. Loading it into a Postgres backend process via the laplace extension would mmap *everything* — including hundreds of MB of MKL runtime — into every PG worker that touched any substrate function, regardless of whether that backend ever ran a Procrustes alignment or wrote a GGUF.

Three natural fault lines exist in the engine work:

1. **Dependency footprint**: small fixed-size kernels (4×4 matmul, 4-vector math) need only Eigen headers + BLAKE3. Heavy linalg (SVD, sparse eigendecomp) needs oneMKL + Spectra + TBB. File-format writers (GGUF) need their own I/O stacks.
2. **Loaded-by**: PG backend (laplace_geom / laplace_substrate extensions) needs only the foundational primitives. C# ingest pipelines (Procrustes-time, eigenmap-time) need the heavy linalg. C# export pipelines (Synthesis) need linalg + format writers.
3. **Version cadence**: foundational primitives are stable once correct; heavy compute primitives evolve as we tune; emission pipelines evolve as we add architectures.

## Decision

Split the engine into **three shared libraries**:

| Library | Contents | Links | Loaded by |
|---|---|---|---|
| **`liblaplace_core.so`** | coord4d arithmetic; hash128 (BLAKE3) wrapper; hilbert4d encode/decode; mantissa pack/unpack; geometry4d serde (WKB-compatible); Glicko-2 fixed-point math; A* cascade primitives; trajectory construction helpers | Eigen 3.4 (headers only); BLAKE3 (FetchContent); optionally oneTBB for parallel kernels | PG backend (laplace_geom, laplace_substrate); C# bindings (Laplace.Engine.Core) |
| **`liblaplace_dynamics.so`** | Procrustes alignment (SVD); Laplacian eigenmaps (Spectra Arnoldi); Gram-Schmidt orthonormalization (Eigen HouseholderQR); lottery-ticket sparsity passes; physicality projection | `liblaplace_core`; oneMKL (BLAS + LAPACK + Sparse BLAS); Spectra; oneTBB | C# only (Laplace.Engine.Dynamics) — **never** PG backend |
| **`liblaplace_synthesis.so`** | Recipe extraction (parse `config.json`, `tokenizer.json`); architecture templates (LlamaTemplate, future MambaTemplate, etc.); feature extractors (canonical_coord, POS, WordNet synset, ConceptNet relation, co-occurrence, physicality projection, random projection pad); native package writers; proof/compatibility writers such as GGUF | `liblaplace_core`; `liblaplace_dynamics` | C# only (Laplace.Engine.Synthesis) — **never** PG backend |

The split is enforced at the build system level — `engine/core/CMakeLists.txt` has no oneMKL dependency; `engine/dynamics/CMakeLists.txt` and `engine/synthesis/CMakeLists.txt` do. Verification: `ldd liblaplace_core.so` must show NO oneMKL symbols; if a future PR adds an oneMKL call to `engine/core/`, the build fails before merge.

Each library exposes a strict C ABI surface (per [RULES.md R14](../../RULES.md)) — POD structs, no name-mangled symbols, no exceptions across the boundary. Same `.so` files are linked by the PG extensions (via the unified CMake pipeline per [ADR 0032](0032-unified-cmake-build-pipeline.md); PGXS retired) AND loaded by .NET via P/Invoke.

## Consequences

- **PG backend stays lean.** The two PG extensions (`laplace_geom`, `laplace_substrate` per [ADR 0025](0025-pg-extension-modularization.md)) link only `liblaplace_core`. PG worker processes don't pay the oneMKL footprint.
- **C# ingest pipeline gets the full heavy stack** — `Laplace.Sources.Transformer` (and similar) link `liblaplace_dynamics` via P/Invoke; calls to Procrustes / eigenmaps / sparsity all dispatch through oneMKL + Spectra.
- **C# export pipeline (Substrate Synthesis) gets the full stack** — `Laplace.Engine.Synthesis` and `Laplace.Cli` (synthesize subcommand) link `liblaplace_synthesis`, which transitively pulls dynamics + core.
- **Independent version cadence.** `liblaplace_core` evolves rarely (the foundational primitives are stable once correct). `liblaplace_dynamics` evolves as we tune heavy compute. `liblaplace_synthesis` evolves as we add architectures and feature extractors. Each library carries its own SemVer.
- **Per-library testing.** Each library has its own `tests/` directory with its own ctest entries. Bugs are isolated to their layer.
- **Required folder restructure** — `engine/` becomes `engine/{core,dynamics,synthesis}/`, each with `include/laplace/<module>/`, `src/`, `tests/`, `CMakeLists.txt`. A top-level `engine/CMakeLists.txt` orchestrates all three in dependency order. See [ADR 0027](0027-separation-of-concerns-invariants.md) for the parallel folder structure for `extension/` and `app/`.

## Alternatives considered

- **Single engine library (status-quo plan).** Rejected — uncontrolled dependency footprint loaded into PG backend; opaque to whom-needs-what; harder per-layer testing.
- **More granular split (one library per kernel — coord, hash, hilbert, mantissa, geom, etc.).** Rejected — these kernels always travel together at runtime (every Tier 1+ entity uses all of them); splitting them would create N artifacts to version with no payoff.
- **Two libraries (core + everything-else).** Rejected — synthesis has different cadence and consumer set than dynamics; bundling them couples export evolution to ingest evolution.

## References

- [PostgreSQL — Loadable Module Conventions](https://www.postgresql.org/docs/current/xfunc-c.html)
- [Intel oneAPI — oneMKL CMake integration](https://www.intel.com/content/www/us/en/developer/articles/technical/onemkl-cmake-config-files.html)
- [Spectra — sparse eigenvalue solver built on Eigen](https://spectralib.org/)
- ADR 0014 → ADR 0019 (`laplace-runner` system account, where dynamics is loaded)
- ADR 0021 + 0023 (DbUp orchestrates extension lifecycle; extension owns schema)
- [ADR 0025](0025-pg-extension-modularization.md) — parallel modularization of the PG extension
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure mirroring this split
- [ADR 0027](0027-separation-of-concerns-invariants.md) — invariants this modularization enforces
- [ADR 0030](0030-mkl-eigen-spectra-tbb-integration.md) — how `liblaplace_dynamics` integrates MKL + Spectra + TBB
- RULES.md R6 (DB as dumb columnar store; entity math in C/C++); R14 (C ABI at engine boundaries)
- `engine/`, `engine/core/`, `engine/dynamics/`, `engine/synthesis/`
