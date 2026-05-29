# ADR 0054: Selective deployment profiles — embedded / read-only-server / full-server

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

## Context

Per the 2026-05-24 conversation: *"The unicode perf-cache generation is a one-time build and then compile (because we modularize and make it selectively deployable (think embedded devices like a raspberry pi with limited ROM))."*

The substrate's three-library engine split per [ADR 0024](0024-engine-modularization.md) (`liblaplace_core` / `liblaplace_dynamics` / `liblaplace_synthesis`) was framed as a build-modularity decision — keep PG-backend lean by not loading oneMKL into every backend; keep ingestion-time heavy linalg out of the read path. But the deeper consequence is **deployment-profile factoring**: different deployment targets need different subsets of the engine + extensions + DB.

Three concrete deployment shapes:

| Profile | Hardware | Ships | Use case |
|---|---|---|---|
| **Embedded** | Raspberry Pi / Jetson Nano / industrial ARM / edge IoT — constrained RAM + ROM | `liblaplace_core.so` + perfcache (embedded in binary) | Read-only cascade inference on T0 + small T≥1 substrates; offline edge AI; no DB; no model ingest; no synthesis |
| **Read-only server** | Cloud VM / container / consumer desktop | `liblaplace_core.so` + perfcache + DB seed + `laplace_geom` + `laplace_substrate` extensions on a populated PG cluster | Substrate query + cascade inference at scale; no model ingest at runtime; no synthesis |
| **Full server** | The hart-server dev box + production substrate instances | all 3 engine libraries + perfcache + DB seed + both PG extensions + `Laplace.Ingestion` + `Laplace.SubstrateCRUD` + `Laplace.Decomposers.*` + `Laplace.Cli` + endpoint extensions | Develop, ingest, synthesize, serve — the substrate's full surface |

Without explicit profile factoring, every deployment ships everything — a Raspberry Pi target carrying ~hundreds of MB of oneMKL runtime + synthesis writers + DbUp + Testcontainers + everything it doesn't need.

The substrate's whole edge-inference promise depends on the embedded profile actually being shipable.

## Decision

**Introduce three deployment profiles selected via the `LAPLACE_DEPLOYMENT_PROFILE` CMake option, each shipping a curated subset of artifacts.**

```cmake
set(LAPLACE_DEPLOYMENT_PROFILE "FULL_SERVER"
    CACHE STRING "Deployment profile: EMBEDDED | READ_ONLY_SERVER | FULL_SERVER")
set_property(CACHE LAPLACE_DEPLOYMENT_PROFILE PROPERTY STRINGS
    EMBEDDED READ_ONLY_SERVER FULL_SERVER)
```

### Profile matrix

| Component | EMBEDDED | READ_ONLY_SERVER | FULL_SERVER |
|---|---|---|---|
| `liblaplace_core.so` | ✓ (perfcache embedded via objcopy `.rodata` per [ADR 0053](0053-perfcache-compile-time-build-pipeline.md)) | ✓ | ✓ |
| `liblaplace_dynamics.so` (oneMKL + Spectra + oneTBB) | ✗ skip | ✗ skip | ✓ |
| `liblaplace_synthesis.so` (recipe, arch_template, feature_extractor, format writers) | ✗ skip | ✗ skip | ✓ |
| Perfcache binary (side-by-side at `share/laplace/perfcache.bin`) | ✗ (embedded in binary instead per `LAPLACE_PERFCACHE_DEPLOYMENT=EMBEDDED_IN_BINARY`) | ✓ | ✓ |
| `laplace_geom` PG extension | ✗ skip (no PG) | ✓ | ✓ |
| `laplace_substrate` PG extension | ✗ skip (no PG) | ✓ | ✓ |
| Custom-built PG 18 + PostGIS 3.6.3 at `/opt/laplace/pgsql-18/` | ✗ skip | ✓ | ✓ |
| DB seed (1.1M T0 codepoint entities + physicalities + attestation cloud) | ✗ skip (no DB) | ✓ (loaded once at install via `Laplace.Migrations` + UnicodeDecomposer + `IngestRunner`) | ✓ |
| `Laplace.Migrations` (DbUp) | ✗ | ✓ | ✓ |
| `Laplace.SubstrateCRUD` (write surface) | ✗ (no DB to write to) | ✗ (read-only) | ✓ |
| `Laplace.Decomposers.*` (per-source decomposers) | ✗ | ✗ | ✓ |
| `Laplace.Ingestion` (IngestRunner) | ✗ | ✗ | ✓ |
| `Laplace.Cli` (cascade / synthesize / ingest subcommands) | ✓ (cascade only) | ✓ (cascade + query) | ✓ (full) |
| `Laplace.Endpoints.*` (OpenAI-compat etc.) | optional | ✓ | ✓ |
| Cascade SRF (the read engine — `laplace_astar_path` / `laplace_cascade`) | ✓ (in-process, mmap'd perfcache only — degraded mode without DB-side attestation graph) | ✓ (full — perfcache + DB) | ✓ |

### Cascade behavior per profile

- **EMBEDDED**: The perfcache carries the T0 codepoint entities plus the typed attestations baked into it at compile time (script / block / general_category membership, UCA-collation, Hilbert-locality). Geometry (Hilbert-locality, script/block co-membership) only *seeds candidates*; what the cascade actually pulls back is still decided by Glicko-2 effective-μ over the perfcache-resident attestations — not by spatial nearness (per docs/SUBSTRATE-FOUNDATION.md truths 3–4; retrieval is NOT nearest-neighbor). What EMBEDDED lacks is the *live DB-stored attestation graph and any T≥1 entities*, so the cascade is bounded to whatever facts the perfcache embeds at T0. Degraded but functional for character-level inference (input method engines, smart-completion at the codepoint stratum, language identification, etc.) where the embedded T0 attestation set suffices.
- **READ_ONLY_SERVER**: Cascade walks the full substrate (T0 from perfcache, T≥1 from PG indexes + DB-stored attestations) but cannot ingest new content. Useful for serving substrate at scale where ingest happens elsewhere.
- **FULL_SERVER**: Everything. Ingest + read + synthesize.

### Build invocation

```sh
# Embedded build (cross-compile to ARM)
cmake -B build-embedded \
      -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/arm64-cross.cmake \
      -DLAPLACE_DEPLOYMENT_PROFILE=EMBEDDED \
      -DLAPLACE_PERFCACHE_DEPLOYMENT=EMBEDDED_IN_BINARY
cmake --build build-embedded
cmake --install build-embedded --prefix /target/embedded-rootfs/opt/laplace

# Read-only server build
cmake -B build-readonly \
      -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
      -DLAPLACE_DEPLOYMENT_PROFILE=READ_ONLY_SERVER \
      -DLAPLACE_PERFCACHE_DEPLOYMENT=ALONGSIDE_BINARY
cmake --build build-readonly

# Full server (the default; what `just build` does)
cmake -B build \
      -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/intel-oneapi.cmake \
      -DLAPLACE_DEPLOYMENT_PROFILE=FULL_SERVER \
      -DLAPLACE_PERFCACHE_DEPLOYMENT=BOTH
cmake --build build
```

`Justfile` gets profile-aware targets:

```just
build-embedded:
    cmake -B build-embedded \
          -DCMAKE_TOOLCHAIN_FILE=cmake/toolchains/${LAPLACE_EMBEDDED_TOOLCHAIN:-arm64-cross.cmake} \
          -DLAPLACE_DEPLOYMENT_PROFILE=EMBEDDED \
          -DLAPLACE_PERFCACHE_DEPLOYMENT=EMBEDDED_IN_BINARY
    cmake --build build-embedded

build-readonly:
    cmake -B build-readonly \
          -DLAPLACE_DEPLOYMENT_PROFILE=READ_ONLY_SERVER \
          -DLAPLACE_PERFCACHE_DEPLOYMENT=ALONGSIDE_BINARY
    cmake --build build-readonly
```

### What the artifact sizes look like (estimated)

- **EMBEDDED**: `liblaplace_core.so` + embedded perfcache ≈ 67 MiB (perfcache) + ~200 KB (engine code) = **~67 MiB binary**. Plus tiny `Laplace.Cli` (cascade subcommand only) ≈ ~few MB if .NET native-AOT compiled.
- **READ_ONLY_SERVER**: ~67 MiB perfcache + ~200 KB engine + ~few MB extension .so + custom PG cluster (~hundreds of MB) + populated DB (~GB once seeded). Order of GB total.
- **FULL_SERVER**: above + oneMKL runtime (~hundreds of MB) + Spectra + TBB + synthesis libraries + `.NET 10 SDK + NuGet deps + Laplace.* projects ≈ several GB.

The embedded profile is **two orders of magnitude smaller than the full server** at deployment. That's the value of the factoring.

### CMake gating

```cmake
# engine/CMakeLists.txt
add_subdirectory(core)  # always

if(LAPLACE_DEPLOYMENT_PROFILE STREQUAL "FULL_SERVER")
    add_subdirectory(dynamics)
    add_subdirectory(synthesis)
endif()

# extension/CMakeLists.txt
if(LAPLACE_DEPLOYMENT_PROFILE STREQUAL "EMBEDDED")
    # No PG, no extensions
elseif(LAPLACE_DEPLOYMENT_PROFILE MATCHES "(READ_ONLY|FULL)_SERVER")
    add_subdirectory(laplace_geom)
    add_subdirectory(laplace_substrate)
endif()

# app/CMakeLists.txt
if(LAPLACE_DEPLOYMENT_PROFILE STREQUAL "FULL_SERVER")
    # Build all C# projects: Migrations, SubstrateCRUD, Decomposers.*, Ingestion, Cli, Endpoints.*
elseif(LAPLACE_DEPLOYMENT_PROFILE STREQUAL "READ_ONLY_SERVER")
    # Migrations (for install-time seed), Cli (cascade + query), Endpoints.*
elseif(LAPLACE_DEPLOYMENT_PROFILE STREQUAL "EMBEDDED")
    # Cli only (cascade subcommand; native-AOT compiled)
endif()
```

## Consequences

- **Edge deployment becomes shippable.** The Raspberry Pi target is a ~67 MiB binary, not several GB.
- **Read-only server deployments save MKL + synthesis overhead** — useful for substrate-query services that don't ingest.
- **`liblaplace_core` becomes the actually-portable core** per its name. Self-contained read primitive that runs anywhere C runs.
- **Profile choice is one CMake option**, not a fork of the codebase.
- **`codepoint_table_load_perfcache` API works both ways**: `(NULL)` to use embedded `.rodata` section; `(path)` to mmap from disk. Per ADR 0053.
- **CI gains a profile-matrix gate**: full server (the existing main build), read-only server (subset build), and embedded (cross-compile target) all must compile cleanly. Catches profile-skipping bugs at PR time.
- **The C# project tree splits by profile**: `Laplace.Engine.Core` ships everywhere; `Laplace.Engine.Dynamics` + `Laplace.Engine.Synthesis` ship only on FULL_SERVER. C# `csproj` `<Condition>` predicates per profile.

## Alternatives considered

- **Ship everything everywhere.** Rejected — defeats the edge-inference promise. A 5GB Raspberry Pi binary is not deployable.
- **Three forks of the codebase per profile.** Rejected — drift / maintenance nightmare. One codebase + profile gating.
- **Runtime profile detection (one binary that checks at startup what's available).** Rejected — wastes ROM on embedded. The whole point is to *not ship* the unused components.
- **Two profiles only (embedded vs server).** Rejected — read-only server is a distinct shape (no ingest, no synthesis, but full DB read). Real customers want this without paying the full-server cost.
- **More than three profiles** (ingest-only, synthesis-only, etc.). Deferred — three covers the conversation's named use cases; finer-grained profiles can be added later if real deployments demand them.

## References

- [STANDARDS.md Build standards](../../STANDARDS.md)
- [STANDARDS.md File and directory layout](../../STANDARDS.md)
- [ADR 0024](0024-engine-modularization.md) — engine modularization (the three-library split this profile axis builds on)
- [ADR 0025](0025-pg-extension-modularization.md) — PG extension modularization
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure
- [ADR 0028](0028-custom-built-pg-postgis-intel.md) — custom-built PG (ships with READ_ONLY / FULL profiles)
- [ADR 0032](0032-unified-cmake-build-pipeline.md) — unified CMake (profile gating fits this tree)
- [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md) — cascade (works in all three profiles, degraded on EMBEDDED)
- [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md) — FULL_SERVER only
- [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md) — FULL_SERVER only
- [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md) — FULL_SERVER only
- [ADR 0053 perfcache compile-time build pipeline](0053-perfcache-compile-time-build-pipeline.md) — drives `LAPLACE_PERFCACHE_DEPLOYMENT`
- Conversation 2026-05-24: "selectively deployable (think embedded devices like a raspberry pi with limited ROM)"
