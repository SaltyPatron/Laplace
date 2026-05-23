# STANDARDS.md — Datatype, Naming, Coding Standards

These are the binding standards for all code in this project. Inconsistency here causes drift; drift causes bugs; bugs at the foundation cascade. **Lock these in once; do not deviate without explicit authorization.**

---

## Datatype standards

| Concern | Type | Notes |
|---|---|---|
| 4D coordinate component | `float64` (per component) | Room for mantissa packing; sufficient precision |
| Entity ID | Column-role `id` (PK) or `<role>_id` (FK) of type `bytea(16)` in PG with `CHECK (octet_length(VALUE) = 16)` + custom `laplace_btree_hash128_ops` opclass (no wrapper PG TYPE per R22 + ADR 0034); `hash128_t = {uint64_t hi, lo}` POD struct in C/C++ ({hi, lo} layout justified by mantissa-pack read pattern) | The column-role is `id`; the value IS a BLAKE3-128 hash of the entity's canonical (type-canonicalized) content bytes (per ADR 0015). Collision-safe for ~10¹⁸ entities. **Raw bytes only — never hex/text.** The fact that the ID is a hash is in the CHECK constraint + ID discipline (see below), not the column name. |
| Physicality ID | `bytea(16)` PK on `physicalities`, BLAKE3 of canonical `(entity_id, source_id, kind, coord, trajectory)` bytes | Lets attestations or higher-tier physicalities reference a specific physicality if needed |
| Attestation ID | `bytea(16)` PK on `attestations`, BLAKE3 of the 5-tuple `(subject_id, kind_id, object_id, source_id, context_id)` | Stable across re-observation — same tuple → same id |
| Hilbert curve index | `uint128` stored as `bytea(16)` in PG; `hilbert128_t = {uint64_t hi, lo}` in C/C++ | 4D × 32-bit-per-dim |
| Glicko-2 rating / RD / volatility | `int64` fixed-point, scale = 10⁹ | Deterministic, vectorizable, no FP drift |
| Effective mu | `int64` fixed-point, scale = 10⁹ | Derived from rating/RD/volatility/source credibility/context compatibility; never stored as float |
| Tier ID | `uint8` (range 0–255) | 256 tiers max — wildly sufficient |
| Constituent count / RLE length | `uint32` | Generous; rarely a hot field |
| Entity identity | The hash itself | NO separate ID column; content-addressable PK |
| Source ID | An entity hash (sources are entities) | NO separate sources table |
| Geometry (PG-side) | `geometry` with Z+M flags = 4D | Standard PostGIS; checked via `ST_HasZ AND ST_HasM` constraint |

### Cast minimization

**One type through hot loops. No mixing.**

- All coordinate math: `float64` (`double` in C/C++; `double precision` in PG; `double` in C#). Never introduce `float32`/`single` in the same kernel.
- All Glicko-2 math: `int64` fixed-point. Never convert to float for arithmetic.
- All effective-score math used by traversal/synthesis: `int64` fixed-point. If a formula needs normalization, do it with deterministic integer scaling.
- Eigen vector type: `Eigen::Matrix<double, 4, 1>` everywhere. **No `Vector4f` for "memory savings"** — the cast cost beats the storage cost.
- Postgres-side: native types (`float8`, `int8`, `bytea`) matching C type sizes exactly. Zero marshaling overhead.
- C#-side: `double` for coords, `long` for ratings, `byte[]` for hashes/Hilbert. `[StructLayout(LayoutKind.Sequential)]` so .NET layouts match C structs byte-for-byte.

### Memory layout

- **SoA (struct-of-arrays)** for batch operations on coordinates — maximizes SIMD throughput.
- **AoS (array-of-structs)** for single-entity access patterns.
- Decision per data structure: which access pattern dominates → choose layout accordingly.

---

## Naming conventions

### SQL (PG schema, functions, types)

- **`snake_case`** throughout.
- Table names: plural (`entities`, `physicalities`, `attestations`).
- Column names: singular (`hash`, `tier`, `canonical_coord`, `hilbert_index`, `trajectory`).
- Function names: `snake_case`. Custom 4D functions: `laplace_*_4d` (e.g., `laplace_distance_4d`, `laplace_centroid_4d`, `laplace_frechet_4d`).
- Index names: `<table>_<column>_<type>` (e.g., `entities_hilbert_btree`, `entities_coord_gist_nd`).
- Constraint names: `<table>_<column>_<check>` (e.g., `physicalities_coord_is_4d_point`).

### C (PG extension wrappers + engine C-ABI)

- **`snake_case`** throughout.
- Function names: `pg_<name>` for PG_FUNCTION wrappers; `<module>_<name>` for engine functions (e.g., `coord4d_distance`, `hilbert4d_encode`).
- Struct/typedef: `<module>_<name>_t` (e.g., `hash128_t`, `hilbert128_t`, `mantissa_payload_t`, `glicko2_state_t`, `astar_query_t`).
- Macros: `UPPER_SNAKE_CASE` (e.g., `LAPLACE_TIER_MAX`).
- File extension: `.c` (PG extension wrappers); headers `.h` (C ABI).

### C++ (engine implementation)

- **`PascalCase`** for class/struct names: `SubstrateService`, `IngestionPipeline`, `LlamaTemplate`.
- **`camelCase`** for member functions, methods: `computeCentroid`, `extractAttestations`.
- **`snake_case`** for free functions exposed via C ABI (`extern "C"`).
- **`m_` prefix** for non-public class members (optional but consistent).
- **`I` prefix** for plugin interface base classes: `ISource`, `IDecomposer`, `IArchitectureTemplate`, `IFormatWriter`, `IFeatureExtractor`, `IProtocolEndpoint`.
- Header extension: `.hpp` (C++ headers); `.h` (C ABI headers shared with the extension wrappers).
- Implementation extension: `.cpp`.
- Header guards via `#pragma once`.

### C# (app layer)

- **`PascalCase`** for type names, public members.
- **`camelCase`** for parameters, locals.
- **`_camelCase`** for private fields.
- **`I` prefix** for interfaces (`ISource`, etc. — mirroring C++).
- Project structure: `Laplace.Engine` (P/Invoke bindings), `Laplace.Synthesis` (Substrate Synthesis UI/API), `Laplace.Endpoints.OpenAI` (OpenAI-compat plugin), etc.
- File-scoped namespaces.
- Nullable reference types ON.

---

## Coding standards

### Error handling

- **PG extension wrappers:** `PG_TRY` / `PG_CATCH` around any code that might raise. `ereport(ERROR, ...)` for malformed input. **Never** return garbage; never silently ignore.
- **C/C++ engine:** return-code based (`int` or enum) for fallible operations. Errors populate a thread-local error context inspectable via `engine_last_error()`. Engine functions DO NOT throw C++ exceptions across the C ABI boundary.
- **C# app:** standard exception-based error handling. P/Invoke wrappers translate engine return codes into managed exceptions.

### Hot-path boundaries

- **Cascade traversal:** one SQL-call SRF/operator enters C/C++; the engine owns frontier queues, visited sets, A*, tier transitions, effective-mu ordering, and abstention. Do not implement hot-path traversal with recursive CTEs, cursors, or app-layer SELECT loops.
- **Prompt handling:** prompt bytes are decomposed to substrate entities/context before traversal. Do not pass prompt text around as a transformer-style context buffer.
- **SPI usage:** only batched, prepared, indexed lookups requested by the engine. No per-edge executor bounce.
- **Sparse synthesis:** unsupported tensor positions are exact zero. Do not add tiny nonzero noise to avoid zeros.

### Memory management

- **PG extension wrappers:** `palloc` / `pfree` exclusively. `palloc` longjmps on OOM (no NULL check needed); never use `malloc` for PG-lifetime data.
- **C/C++ engine:** RAII in C++ code; explicit ownership documented at the C ABI boundary (caller-allocates OR callee-allocates per function; never ambiguous).
- **C# app:** managed memory; `unsafe` blocks only where P/Invoke requires; explicit `Marshal.AllocHGlobal`/`Marshal.FreeHGlobal` pairing.

### Memory contexts (PG-specific)

- Use the **appropriate MemoryContext** for the lifetime needed: `CurrentMemoryContext` for short-lived per-call, `TopMemoryContext` only when truly required.
- Switch contexts via `MemoryContextSwitchTo()` and document the lifetime expectation.

### Determinism

- **FP determinism:** pin FP regime in the engine (no `-ffast-math` on hot paths; specific oneMKL CBWR settings for deterministic SVD; deterministic reduction order for parallel sums).
- **Glicko-2 fixed-point:** all math in `int64`; no `double` intermediates.
- **Hashing:** BLAKE3 is deterministic by spec; use the official C implementation (git submodule at `external/blake3/` pinned to 1.5.4 per ADR 0033); truncate to 128 bits via the `hash128_t` helper.
- **Hilbert encoding:** pure integer bit-twiddling; no FP involved.

### Concurrency

- **PG-side:** standard Postgres MVCC; rely on it.
- **Engine-side parallelism:** oneTBB for parallel work; pin thread count via env var; avoid thread-local globals.
- **No data races.** Use Eigen's thread-safe usage; no lock-free hand-coded structures unless explicitly verified.

### Logging

- **Engine:** stderr at standardized levels (DEBUG / INFO / WARN / ERROR); structured (key=value or JSON lines).
- **PG extension:** `ereport(DEBUG/NOTICE/WARNING)` for in-context; `elog` for low-level.
- **C# app:** `Microsoft.Extensions.Logging`; structured via Serilog or built-in.

### Testing

Three test surfaces; each layer's tests run from CMake's CTest harness or `dotnet test`. The framework picks are locked:

- **C/C++ engine unit tests** — **GoogleTest** via `external/googletest/` submodule. Per-module test sources at `engine/{core,dynamics,synthesis}/tests/test_*.cpp`. CMake's `gtest_discover_tests` registers each test case with CTest automatically. Run via `cd build && ctest --output-on-failure`.
- **PG extension SQL tests** — **`pg_regress`** with the conventional `sql/`+`expected/` directory pattern per extension. Each `extension/{laplace_geom,laplace_substrate}/tests/sql/*.sql` has a matching `tests/expected/*.out`. CMake adds a CTest target invoking `pg_regress --temp-instance` against the custom-built PG. Most SQL-layer regressions are caught at the C++ layer first (SQL is thin offload per RULES.md R6); pg_regress catches the marshalling layer + opclass behavior.
- **C# unit + integration tests** — **xUnit** + **Testcontainers.PostgreSql** via NuGet. Project structure: `app/Laplace.Engine.Core.Tests/`, `app/Laplace.Engine.Dynamics.Tests/`, `app/Laplace.Migrations.Tests/`. P/Invoke smoke tests link against the built engine `.so`. DbUp tests use Testcontainers to spin up a `postgis/postgis:18` container per fixture; `IAsyncLifetime` handles container lifecycle. Run via `dotnet test`.
- **Full-stack integration** — `test/integration/` — end-to-end scenarios crossing engine + extension + app boundaries, exercised in `integration.yml` against the deployed custom-PG cluster.
- **Round-trip determinism tests** — cross-machine reproducibility verified per release; ctest case asserts byte-identical output across `TBB_NUM_THREADS={1,2,4,8}` for any Procrustes/eigenmaps computation (per ADR 0030).
- **Cross-language consistency** — the same engine function called via SQL (extension wrapper) and via C# P/Invoke must produce byte-identical results. Verified by a dedicated ctest case that calls both paths with the same input and asserts equality.

### Documentation

- **Function docstrings:** required for all public engine functions, PG functions, C# public APIs.
- **No inline narrative comments** explaining what the code does — names and types should be self-evident. Comments ONLY for:
  - Non-obvious WHY (hidden constraint, subtle invariant, workaround for a specific bug)
  - References to external standards (UCA, JL lemma, Skilling 2004, etc.)

---

## File and directory layout

```
laplace/                              ← project root (= /home/ahart/Projects/Laplace)
├── CLAUDE.md AGENTS.md README.md
├── GLOSSARY.md RULES.md STANDARDS.md
├── DESIGN.md OPERATIONS.md Justfile
├── .github/copilot-instructions.md
├── .claude/
│   ├── settings.json
│   └── agents/
│       ├── substrate-architect.md
│       ├── postgres-extension.md
│       ├── cpp-performance.md
│       ├── type-taxonomy.md
│       ├── ingestion-pipeline.md
│       ├── verification.md
│       └── conventional-ai-skeptic.md
├── CMakeLists.txt                    ← top-level orchestrator (ADR 0032 Path B)
├── engine/                           ← C/C++ engine (3 shared libs per ADR 0024)
│   ├── CMakeLists.txt                ← engine-level orchestration
│   ├── core/                         ← liblaplace_core.so (no MKL)
│   │   ├── include/laplace/core/     ← coord4d, hash128, hilbert4d, mantissa, etc.
│   │   ├── src/
│   │   ├── tests/                    ← GoogleTest; ctest-discoverable
│   │   └── CMakeLists.txt
│   ├── dynamics/                     ← liblaplace_dynamics.so (MKL+Spectra+TBB)
│   │   ├── include/laplace/dynamics/ ← Procrustes, eigenmaps, Gram-Schmidt, sparsity
│   │   ├── src/, tests/, CMakeLists.txt
│   └── synthesis/                    ← liblaplace_synthesis.so
│       ├── include/laplace/synthesis/← recipe, arch_template, gguf_writer
│       └── src/, tests/, CMakeLists.txt
├── external/                         ← git submodules per ADR 0033
│   ├── postgresql/                   ← REL_18_0
│   ├── postgis/                      ← 3.6.3
│   ├── proj/                         ← 9.4.1
│   ├── geos/                         ← 3.12.2
│   ├── gdal/                         ← v3.9.3
│   ├── eigen/                        ← 3.4.0 (header-only via INTERFACE library)
│   ├── spectra/                      ← v1.2.0 (header-only, on Eigen)
│   ├── blake3/                       ← 1.5.4 (add_subdirectory c/)
│   └── googletest/                   ← test framework for engine ctest
├── extension/                        ← PostgreSQL extensions (2 per ADR 0025)
│   ├── laplace_geom/                 ← general-purpose 4D PostGIS additions
│   │   ├── CMakeLists.txt            ← extension build via CMake (no PGXS, per ADR 0032)
│   │   ├── src/                      ← C wrapper functions (PG_FUNCTION_INFO_V1)
│   │   ├── sql/                      ← .sql.in source modules (per ADR 0034)
│   │   │   ├── sqldefines.h.in
│   │   │   ├── laplace_geom.sql.in   ← entry — #includes the modules
│   │   │   ├── 01_meta.sql.in
│   │   │   ├── 02_hash128_type.sql.in
│   │   │   ├── 03_hash128_ops.sql.in
│   │   │   ├── 04_hilbert.sql.in
│   │   │   ├── 05_mantissa.sql.in
│   │   │   ├── 06_st_4d.sql.in
│   │   │   ├── 07_s3_opclass.sql.in
│   │   │   └── uninstall_laplace_geom.sql.in
│   │   ├── tests/                    ← pg_regress sql/+expected/ pairs
│   │   └── laplace_geom.control.in
│   └── laplace_substrate/            ← substrate schema; requires laplace_geom
│       ├── CMakeLists.txt
│       ├── src/
│       ├── sql/                      ← .sql.in source modules
│       │   ├── sqldefines.h.in
│       │   ├── laplace_substrate.sql.in
│       │   ├── 01_schema.sql.in
│       │   ├── 02_entities.sql.in
│       │   ├── 03_physicalities.sql.in
│       │   ├── 04_attestations.sql.in
│       │   ├── 05_indexes.sql.in
│       │   ├── 06_glicko2.sql.in
│       │   ├── 07_cascade.sql.in
│       │   ├── 08_sp_trajectory_ops.sql.in
│       │   ├── 09_brin_tier_ops.sql.in
│       │   └── uninstall_laplace_substrate.sql.in
│       ├── tests/                    ← pg_regress sql/+expected/ pairs
│       └── laplace_substrate.control.in
├── app/                              ← C# .NET 10 projects (per ADR 0026)
│   ├── Laplace.slnx
│   ├── Laplace.Engine.{Core,Dynamics,Synthesis}/    ← P/Invoke per engine lib
│   ├── Laplace.Migrations/                          ← DbUp runner
│   ├── Laplace.Cli/                                 ← cascade / synthesize subcommands
│   ├── Laplace.Endpoints[.*]/                       ← protocol endpoint host + plugins
│   ├── Laplace.Sources.*/                           ← ISource plugins (WordNet, Transformer, ...)
│   └── Laplace.Decomposers.*/                       ← IDecomposer plugins (Safetensors, Text, ...)
├── db/                               ← DbUp migrations (Layer 1 per ADR 0023)
│   └── migrations/
├── scripts/                          ← operational scripts
│   ├── build-perfcache.sh
│   ├── seed-t0.sh
│   ├── ingest-source.sh
│   └── verify-determinism.sh
└── test/integration/                 ← end-to-end tests
```

### File-naming conventions

- C/C++: `snake_case.{c,cpp,h,hpp}`
- C#: `PascalCase.cs`
- SQL modules (source): `NN_module_name.sql.in` (numeric prefix locks load order; per ADR 0034)
- SQL entry: `<extension_name>.sql.in` (`#include`s the modules)
- SQL shared header: `sqldefines.h.in` (configured via CMake `configure_file`)
- SQL built artifact: `<extension_name>--<version>.sql` (generated; never hand-edited per RULES.md R17)
- SQL upgrade scripts: `<extension_name>--<from>--<to>.sql` (also generated from `.sql.in` sources)
- Scripts: `kebab-case.sh`
- Markdown: `UPPERCASE.md` for top-level project docs; `kebab-case.md` for sub-docs.

---

## Dependency sources (locked — submodule policy per ADR 0033)

Every direct C/C++ dependency is a git submodule under `external/` pinned to a release tag (per [ADR 0033](docs/adr/0033-all-deps-as-submodules.md)). The only exceptions are (a) Intel oneAPI (vendor compiler + runtime; no source-build path), (b) build-time tooling (cmake, ninja, autoconf, perl), and (c) supporting system libraries oneAPI doesn't provide (libxml2, libicu, libsqlite3).

Adding a new dep requires (a) listing it here, (b) adding the submodule under `external/` pinned to a release tag, (c) writing a `scripts/build-<dep>.sh` if it's not header-only, (d) wiring it into `scripts/build-all-deps.sh`, (e) verifying RULES.md doesn't ban it, (f) user authorization.

### Direct C/C++ dependencies (all submodules under `external/`)

| Library | Submodule path | Pinned to | Build step | Install prefix |
|---|---|---|---|---|
| **PostgreSQL 18** | `external/postgresql/` | `REL_18_0` | `scripts/build-pg.sh` (icx/icpx, VPATH build) | `/opt/laplace/pgsql-18/` |
| **PostGIS 3.6.3** | `external/postgis/` | `3.6.3` | `scripts/build-postgis.sh` (against custom PG + custom GEOS/PROJ/GDAL) | under PG prefix |
| **GEOS 3.12.2** | `external/geos/` | `3.12.2` | `scripts/build-geos.sh` (CMake) | `/opt/laplace/geos/` |
| **PROJ 9.4.1** | `external/proj/` | `9.4.1` | `scripts/build-proj.sh` (CMake) | `/opt/laplace/proj/` |
| **GDAL 3.9.3** | `external/gdal/` | `v3.9.3` | `scripts/build-gdal.sh` (CMake, links PROJ) | `/opt/laplace/gdal/` |
| **Eigen 3.4.0** | `external/eigen/` | `3.4.0` | header-only; `add_library(laplace_eigen INTERFACE)` in engine CMake | n/a |
| **Spectra v1.2.0** | `external/spectra/` | `v1.2.0` | header-only; `add_library(laplace_spectra INTERFACE)` on Eigen | n/a |
| **BLAKE3 1.5.4** | `external/blake3/` | `1.5.4` | `add_subdirectory(external/blake3/c)` in engine CMake | n/a (linked statically into engine libs) |
| **GoogleTest** | `external/googletest/` | `1.15+` (TBD on Epic A) | `add_subdirectory(external/googletest)` for test targets | n/a |
| **tree-sitter** | `external/tree-sitter/` | `v0.22.6` (or current stable) | `scripts/build-tree-sitter.sh` (small Makefile/CMake build) | `/opt/laplace/tree-sitter/` |

**tree-sitter grammars** (303 `tree-sitter-<lang>` parser source repos, ~1.9 GB unpacked) live at `external/tree-sitter-grammars/<lang>/` — one git submodule per grammar:

- The 303 submodules are added in bulk via `scripts/import-tree-sitter-grammars.sh`, which reads `/vault/Data/TreeSitter/<lang>/.git/HEAD` to determine each grammar's pinned SHA, then issues `git submodule add <upstream-url> external/tree-sitter-grammars/<lang>` for each.
- `.gitmodules` grows by 303 entries (~50KB). Acceptable one-time cost for full reproducibility.
- **Init is opt-in per grammar.** Most contributors won't need all 303 — only the language they're working on. `git submodule update --init external/tree-sitter-grammars/tree-sitter-python` pulls just that one.
- `/vault/Data/TreeSitter` is the *warm-cache fallback* on hart-server (laplace-runner can reuse the already-fetched .git objects via `git clone --reference`), but submodule URLs point at upstream GitHub so any machine can rebuild from scratch.
- Per-grammar build (`grammar.js → src/parser.c → loadable .so`) is performed lazily by the future `CodeDecomposer` at first use — not at submodule-init time.

### Vendor toolchain (installer, not submodule)

| Library | Path | Notes |
|---|---|---|
| **Intel oneAPI 2026** | `/opt/intel/oneapi/` | Includes `icx`, `icpx`, oneMKL, oneTBB, IPP. Sourced via `source /opt/intel/oneapi/setvars.sh` for compiler tooling. `find_package(MKL CONFIG REQUIRED)` + `find_package(TBB CONFIG REQUIRED)` for CMake integration. |

### System libraries (apt — supporting only)

| Library | Source | Used by | Why apt is OK |
|---|---|---|---|
| **libxml2-dev** | `apt install libxml2-dev` | PROJ, PostGIS configure | Build-time XML parsing helper; stable interface |
| **libicu-dev** | `apt install libicu-dev` | PostgreSQL configure (--with-icu); UCA collation runtime | Stable ABI; UCA collation is the contract, not a build-time fragility surface |
| **libsqlite3-dev** | `apt install libsqlite3-dev` | PROJ (datum grid storage) | Stable ABI; PROJ uses it only for grid metadata |
| **.NET 10 SDK** | Microsoft package at `/usr/lib/dotnet/` | C# app layer | Microsoft's distribution is the supported channel |

### Build-time tooling (apt — bootstrap only)

The `bootstrap_build_environment` step in `scripts/bootstrap-laplace-runner.sh` installs:

`build-essential, cmake, ninja-build, autoconf, automake, libtool, pkg-config, perl, bison, flex, gettext`

These are pure build infrastructure — they don't end up linked into the substrate or its extensions.

### NuGet dependencies (C# app layer)

Managed via project `.csproj` files in `app/`. Pinned via `<PackageReference Version=...>`:

- **Npgsql** — PostgreSQL ADO.NET provider
- **DbUp** — migration runner (per ADR 0021)
- **xUnit** + **xunit.runner.visualstudio** — unit test framework
- **Testcontainers.PostgreSql** — containerized PG for `Laplace.Migrations.Tests`
- **Microsoft.Extensions.Logging** — structured logging

### Banned / removed

- **vcpkg** — previously at `/home/ahart/vcpkg`; not in use. ADR 0033 makes it irrelevant (all C++ deps are direct submodules).
- **apt for direct C/C++ deps** — explicitly forbidden by ADR 0033. The bootstrap script's `bootstrap_build_environment` step is the only place apt is invoked for libraries, and only for build-time helpers and supporting libs (above).
- **CMake `FetchContent`** for direct deps — replaced by submodules per ADR 0033. The exception is build-time-only fetches with no end-user impact (none currently).

## Build standards

- **C/C++:** primary compiler `icx`/`icpx` (Intel 2026); fallback `g++` 11.4 / `clang++` 14.
- **C++ standard:** C++23 where available; C++20 minimum.
- **CMake** as the engine + extension + top-level build system (Path B per ADR 0032; PGXS retired); **dotnet** CLI for C# projects.
- **Build modes:** `Debug` (no opt, full symbols), `Release` (O3 / `/O2`, LTO), `RelWithDebInfo` (O2 + symbols).
- **Sanitizers in CI:** ASan, UBSan on Debug builds.
- **No `-ffast-math`** on hot paths (breaks FP determinism).
- **Vectorization target:** `-march=haswell` minimum (AVX2 on this dev box); `-march=sapphirerapids` or `-mavx512f` for AVX-512 deployment targets.

---

## Reusable helpers — DRY at every layer

Every operation used more than once **must** be a named, tested, single-source-of-truth helper. Helpers exist to ensure:

1. **Correctness** is solved once.
2. **Performance** is optimized in one place (SIMD, caching, alignment).
3. **Behavior** is consistent across all callers.
4. **Bugs** have one place to be fixed, not 17 inlined copies.

### Applies to (non-exhaustive)

- Hash operations (`hash128_from_bytes`, `hash128_merkle`, `hash128_equals`, `hash128_zero`, `hash128_compare`, ...)
- Coord arithmetic (`coord4d_distance`, `coord4d_centroid`, `coord4d_norm`, ...)
- Hilbert encode/decode (`hilbert4d_encode`, `hilbert4d_decode`, `hilbert128_compare`)
- Mantissa pack/unpack (`mantissa_pack`, `mantissa_unpack`)
- Geometry round-trip via liblwgeom (`lwgeom_from_gserialized`, `lwgeom_as_lwline`, `getPoint4d`, etc.) — engine kernels then operate on raw XYZM-packed `double*` buffers (no parallel datatype per R22)
- Glicko-2 update (`glicko2_update`, `glicko2_decay_rd_in_place`)
- Trajectory enumeration (`trajectory_constituents`, `trajectory_build`)
- PG ↔ engine marshalling (one wrapper macro per arg pattern)
- C# ↔ engine marshalling (one P/Invoke binding per engine function — never call native API ad-hoc)
- Determinism shims (fixed-point arithmetic on rating/RD/volatility)

### Don't

- Inline a hash invocation in multiple `.c`/`.cpp`/`.cs` files
- Duplicate marshalling code across PG_FUNCTION wrappers
- Re-implement a geometric operation across languages (engine is the canonical source)
- Skip the helper because "it's just one line" — that one line still gets multiplied N times
- Define ad-hoc inline conversions at marshalling boundaries

### Do

- Define each helper **in a single header**, implement once
- Test exhaustively at the helper level (not at every caller)
- Call the helper from all callers — no exceptions
- Centralize optimization within the helper
- Document the helper's contract (preconditions, postconditions, ownership rules)

### Cross-language consistency

For any operation exposed in BOTH PG and C# (i.e., callable via SQL and via P/Invoke), there is exactly **one canonical implementation in the C/C++ engine**. PG wrappers (PG_FUNCTION_INFO_V1) and C# wrappers (LibraryImport) are thin glue around the engine helper. Behavior across SQL/C#/engine direct is byte-identical, verified by cross-language consistency tests.

## Storage-class discipline

The substrate distinguishes five storage classes by *purpose*. Code that mixes them is a smell.

| Class | Storage surface | Role | Indexed how |
|---|---|---|---|
| **Content** | `entities.id` rows + `physicalities.trajectory` (mantissa-packed LINESTRING) | The actual digital bytes being recorded; reconstructible by trajectory walk to T0 | `entities.id` PK (`laplace_btree_hash128_ops`); structural opclass on `physicalities.trajectory` per ADR 0029 |
| **Metadata** | structural columns: `entities.{tier,type_id,first_observed_by,created_at}`; `physicalities.{kind,alignment_residual,source_dim,observed_at}`; `attestations.{last_observed_at,observation_count}` | HOW or WHEN a row exists; not knowledge | stock B-tree / BRIN; never participates in [Effective Mu](GLOSSARY.md#effective-mu) |
| **Attestation** | `attestations` table rows | Typed knowledge edges with Glicko-2 ratings; the substrate's "weights" | btree on `subject_id` / `kind_id` / `object_id` / `source_id` / `context_id` + `(rating DESC, rd ASC)` |
| **Lookup** | PK + FK columns: `*.id`, `*_id` | Identity resolution; same `bytea(16)` content-addressed type everywhere | custom `laplace_btree_hash128_ops` opclass |
| **Index** | btree / GIST / BRIN / SP-GiST + the 4 substrate-specific opclasses (ADR 0029) | Acceleration; not data | n/a (indexes index data) |

`physicalities.coord`, `hilbert_index`, `radius_origin`, and source PROJECTION rows form the projection/access layer. They may support fuzzy candidate discovery and embedding-shaped exports, but they are not the knowledge layer and never participate directly in Effective Mu. Semantic response is computed from typed attestations under arena/source policy.

**Rules:**

- Putting knowledge in a metadata column → wrong. Metadata is structural; knowledge belongs in attestations.
- Putting metadata in an attestation → wrong. `last_observed_at` doesn't need a Glicko-2 rating; it's a timestamp.
- Putting content in an attestation → wrong. Content is bytes (trajectory of constituent IDs); attestations are typed edges between entities.
- Building a lookup index on a metadata column → fine. Building a lookup index on a content column → forbidden (content is identified by hash, looked up by `id`).
- Adding columns to the three core tables to satisfy a domain — wrong. The substrate's schema is fixed at three tables (per RULES R2 + ADR 0002); domain-specific knowledge goes in attestations, period.

Decomposers emit content + attestations; metadata is set automatically by ingestion (tier, type_id, observed_at); lookup + index are schema-time concerns, not per-row concerns.

## ID discipline (no casts, no hex)

The substrate's ID values MUST be **raw bytes** end to end. No exceptions. Column-role is `id` (PK) or `<role>_id` (FK); the value IS a BLAKE3-128 hash of canonical content. Column-name reflects role; CHECK constraint + this discipline reflect the hash-value mechanism.

| Layer | Representation |
|---|---|
| Engine C | `hash128_t = { uint64_t hi, lo }` (16 B, naturally aligned, POD) |
| Postgres storage | `bytea` with `CHECK (octet_length(VALUE) = 16)` constraint, column-named `id` (PK) or `<role>_id` (FK) |
| Postgres comparison | btree on `bytea` — native byte compare, identical to memcmp |
| Postgres FK references | `bytea` to `bytea` — no casts |
| C# interop | `byte[16]` or POD struct via `[StructLayout(LayoutKind.Sequential)]` |
| WKB / mantissa-pack | Raw bytes embedded (XYZ of a trajectory vertex carries the full 128-bit entity_id) |
| Wire/disk format | Raw bytes |

**Banned:**

- `bytea ↔ text` casts in hot paths (`encode(h, 'hex')` is debugging-only)
- `varchar`, `text`, or any character-encoded type for IDs
- C# `string` for ID values; no `BitConverter.ToString`, no `Encoding.*`
- `tolower` / `toupper` / normalization of ID representations
- Hex-string parsing of incoming IDs
- Any conversion ceremony at marshalling boundaries
- Naming the PK column `hash` — the column-role is `id`. The value-mechanism is hashing, recorded in the CHECK constraint and in this discipline.
- Truncating an ID to fewer than 128 bits anywhere (no truncated-hash storage; mantissa-packing carries the full 128-bit value)

Add a debug-only `laplace_id_to_hex(bytea) → text` if it helps inspection, but it MUST NOT appear in any data-flow path.

## Canonicalization discipline (lossless, per type)

Entity IDs are BLAKE3-128 of **canonical content bytes**, where canonical is defined per entity type and is **lossless** — every input that decodes to the same canonical bytes hashes to the same ID. Examples:

- Text: NFC-normalized Unicode codepoint sequence (UTF-8 vs UTF-16 of identical codepoints → same ID).
- Pixel (RGB type): canonical `(r, g, b)` triple bytes (per-channel uint8). RGBA, CMYK, HSV are *different types*, related by attestations, not by ID equivalence.
- Lossless image container: canonical pixel grid bytes (PNG vs lossless WebP of identical pixels → same ID).
- Audio frame (PCM): canonical PCM sample bytes at canonical sample rate/bit depth.
- Model recipe: canonical config + tokenizer JSON normalized form (sorted keys, no whitespace variation).

**Lossy conversions are NOT equivalent under canonicalization.** A JPEG of an image has different canonical bytes from the lossless original (different pixel values after decode) — they are different entities, cross-linked by `IS_LOSSY_ENCODING_OF` attestations. An MP3 of a FLAC is a different entity. A quantized GGUF is a different artifact from the safetensors it was derived from (and per Vampire mode, model files are not preserved as entities at all).

The canonicalization rule for each type lives with that type's IDecomposer plugin. The substrate-canonical source emits the canonical-form attestation; cross-type / cross-format equivalence is captured as attestations, not as ID collapse.

## Attestation kind discipline (typed transform vocabulary)

Attestation kinds are not arbitrary labels — they are the substrate's typed-computation vocabulary inside one generic attestation observation envelope. Each kind is itself an entity (content-addressed by its canonical name, not by hashing arbitrary parameter tuples) with arena semantics (compatibility, cardinality, context policy, observation update scope, conflict policy, source-trust policy, lineage policy, and structural support inputs) recorded as meta-attestations. Cascade A* composes kind-typed walks, but the substrate's vocabulary is **usage- and structure-shaped**, not transformer-position-shaped.

Naming convention for attestation kinds:

- UPPER_SNAKE_CASE for the kind name.
- Each kind is drawn from a **small fixed vocabulary** per modality / per architecture family. Vocabularies are documented; new kinds require the type-taxonomy agent to extend the documented vocabulary.
- The generic operation is `OBSERVE_ATTESTATION(kind_id, subject_id, object_id, source_id, context_id, qualifiers)`. Kind names such as `HAS_POS` are semantic entities inside that envelope, not bespoke APIs.
- **Forbidden**: synthesizing per-(layer, head, position) kind entities via hash-concatenation of architectural metadata. Layer/head/position are not part of kind identity. For transformer-family tensor-calculation kinds, they live in recipe content, never as kind-name parameters or routine per-attestation metadata.
- **Forbidden**: opaque `params[]` as a storage or hot-path escape hatch. Qualifiers must be modeled as context entities, object/value entities, source metadata, recipe content, or meta-attestations so arena resolution can inspect them.
- Modality-specific kinds carry a modality prefix or unambiguous semantic (`EXTRACTS_R_CHANNEL` rather than `EXTRACTS_R` to avoid collision with non-pixel modalities).
- Cross-modal kinds are first-class: `DEPICTS`, `CAPTIONS`, `TRANSCRIBES_AS`.

Tensor-calculation kinds for transformer-family AI models are a fixed ~10-element architecture-family vocabulary: `EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`. Per-position attribution (layer, head, per-tensor vocabulary) is **recipe content** — text/JSON on the model recipe entity, not per-attestation metadata. Attestations aggregate across positions; the architecture template (substrate code, per `IArchitectureTemplate`) distributes the aggregated typed attestations across recipe-shaped tensor slots at emit time. Storing position attribution on attestation rows would be redundant with the recipe. This transformer-family list is not universal; other model architectures and modalities must define their own small fixed role vocabularies under the same generic observation envelope.

### Kind value tiers + Glicko-2 priors (ADR 0044)

Every attestation kind belongs to one of 11 **value tiers** that determine its Glicko-2 prior (initial rating / RD / volatility) and its cascade-weight multiplier. Tier assignment is a meta-attestation on the kind entity (`HAS_KIND_VALUE_TIER`). Tiers + their priors are bootstrapped at install per [ADR 0042](docs/adr/0042-bootstrap-order-and-substrate-canonical-seeding.md) Stage 3.

Summary (full per-tier values in [ADR 0044](docs/adr/0044-attestation-kind-priors-and-source-trust-taxonomy.md)):

- **T1 Mandate** — substrate invariants (highest prior + 1.0× weight)
- **T2 Standards Structural** — codified standards-derived (rating ≈ 2300, weight 1.0×)
- **T3 Taxonomic** — `IS_A`, `IS_HYPERNYM_OF`, ... (rating ≈ 1900, weight 0.9×)
- **T4 Partitive** — `IS_PART_OF`, `IS_MERONYM_OF`, ... (rating ≈ 1800, weight 0.85×)
- **T5 Causal** — `CAUSES`, `BECAUSE`, `ENTAILS`, ... (rating ≈ 1700, weight 0.8×)
- **T6 Equivalence** — `IS_TRANSLATION_OF`, `IS_SIMILAR_TO`, ... (rating ≈ 1600, weight 0.7×)
- **T7 Oppositional** — `IS_ANTONYM_OF`, `EXCLUDES`, ... (rating ≈ 1550, weight 0.6×)
- **T8 Associative** — `CO_OCCURS_WITH`, `FOLLOWS`, `USED_FOR`, ... (rating ≈ 1500, weight 0.5×)
- **T9 Tensor-Calculation** — `Q_PROJECTS`, ... (rating ≈ 1400, weight 0.4×; single-probe trust; cluster across many models for higher confidence)
- **T10 Scalar-Valued** — `HAS_NUMERIC_VALUE`, `HAS_FREQUENCY`, ... (rating IS the value; RD captures measurement uncertainty)
- **T11 Probationary** — user-prompt-emitted attestations (rating ≈ 1300, weight 0.3×; session-scoped)

A kind entity MAY carry per-kind overrides (meta-attestations) to deviate from tier defaults when justified.

### Source trust class discipline (ADR 0044)

Sources MUST register a `HAS_TRUST_CLASS` meta-attestation pointing at one of the 10 bootstrapped trust-class entities. Glicko-2 attestation initialization combines the kind's prior tier + the source's trust class weight to compute initial (rating, RD, volatility) — NOT a global default. Trust-class weight + arena admittance policy lives on the trust-class entity (not in plugin code).

Adding a new attestation kind requires (a) defining its arena semantics as meta-attestations, (b) declaring its source-trust policy, (c) registering it as an entity with the substrate-canonical source attesting its kind-properties. The type-taxonomy agent is the canonical owner.

## Versioning

- **Semantic versioning** for engine library and PG extension: `MAJOR.MINOR.PATCH`.
- **Unicode version** pinned per release (e.g., 15.1, 16.0). Build artifacts (perf-cache + DB seed) tagged with Unicode version.
- **Schema migrations** are additive only (new columns nullable with defaults; new tables independent). Schema version tracked in `extension/laplace--<from>--<to>.sql` files.
- **Per-recipe versioning** for Synthesis outputs: recipe JSON includes substrate-state-hash + recipe-content-hash for reproducibility.

---

## Determinism / reproducibility checklist

For every change touching a hot-path function:

- [ ] Same input → same output across runs (single machine)
- [ ] Same input → same output across machines (cross-machine determinism)
- [ ] No `-ffast-math`, no `fma` non-deterministic fusion
- [ ] Parallel reductions use deterministic ordering
- [ ] FP regime documented (rounding mode, denormals handling)
- [ ] Round-trip test: serialize → deserialize → match byte-for-byte

---

## What's NOT standardized (intentionally)

- **Specific A* heuristic h() formula** — being designed; tunable per use case.
- **Specific Glicko-2 adaptation formula** — being designed; tunable per attestation kind.
- **Specific feature-extractor dim assignments** — recipe-driven, user-configurable.

These are **tuning decisions** explicitly left open. They are NOT standards violations to leave undecided; they're decisions to make at execution time with the user.
