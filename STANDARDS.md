# STANDARDS.md — Datatype, Naming, Coding Standards

These are the binding standards for all code in this project. Inconsistency here causes drift; drift causes bugs; bugs at the foundation cascade. **Lock these in once; do not deviate without explicit authorization.**

---

## Datatype standards

| Concern | Type | Notes |
|---|---|---|
| 4D coordinate component | `float64` (per component) | Room for mantissa packing; sufficient precision |
| Entity hash | `uint128` stored as `bytea(16)` in PG (with `hash128` typed wrapper from `laplace_geom`); `hash128_t = {uint64_t hi, lo}` in C/C++ | BLAKE3 truncated to 128 bits (per ADR 0015); collision-safe for ~10¹⁸ entities; **raw bytes only — never hex/text** |
| Hilbert curve index | `uint128` stored as `bytea(16)` in PG; `hilbert128_t = {uint64_t hi, lo}` in C/C++ | 4D × 32-bit-per-dim |
| Glicko-2 rating / RD / volatility | `int64` fixed-point, scale = 10⁹ | Deterministic, vectorizable, no FP drift |
| Tier ID | `uint8` (range 0–255) | 256 tiers max — wildly sufficient |
| Constituent count / RLE length | `uint32` | Generous; rarely a hot field |
| Entity identity | The hash itself | NO separate ID column; content-addressable PK |
| Source ID | An entity hash (sources are entities) | NO separate sources table |
| Geometry (PG-side) | `geometry` with Z+M flags = 4D | Standard PostGIS; checked via `ST_HasZ AND ST_HasM` constraint |

### Cast minimization

**One type through hot loops. No mixing.**

- All coordinate math: `float64` (`double` in C/C++; `double precision` in PG; `double` in C#). Never introduce `float32`/`single` in the same kernel.
- All Glicko-2 math: `int64` fixed-point. Never convert to float for arithmetic.
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
- Constraint names: `<table>_<column>_<check>` (e.g., `entities_canonical_coord_is_4d_point`).

### C (PG extension wrappers + engine C-ABI)

- **`snake_case`** throughout.
- Function names: `pg_<name>` for PG_FUNCTION wrappers; `<module>_<name>` for engine functions (e.g., `coord4d_distance`, `hilbert4d_encode`).
- Struct/typedef: `<module>_<name>_t` (e.g., `coord4d_t`, `hash128_t`).
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
- **No TODO comments that ship.** TODOs go in `.agent/status/blockers.md`.

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
├── .agent/
│   ├── README.md
│   └── status/
│       ├── STATE.md
│       ├── decisions.md
│       └── blockers.md
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
- Geometry serialization (`geometry4d_serialize`, `geometry4d_deserialize` — both bounds-checked)
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

## Hash discipline (no casts, no hex)

The hash representation MUST be **raw bytes** end to end. No exceptions.

| Layer | Representation |
|---|---|
| Engine C | `hash128_t = { uint64_t hi, lo }` (16 B, naturally aligned, POD) |
| Postgres storage | `bytea` with `CHECK (octet_length(VALUE) = 16)` constraint |
| Postgres comparison | btree on `bytea` — native byte compare, identical to memcmp |
| Postgres FK references | `bytea` to `bytea` — no casts |
| C# interop | `byte[16]` or POD struct via `[StructLayout(LayoutKind.Sequential)]` |
| WKB / mantissa-pack | Raw bytes embedded |
| Wire/disk format | Raw bytes |

**Banned:**

- `bytea ↔ text` casts in hot paths (`encode(h, 'hex')` is debugging-only)
- `varchar`, `text`, or any character-encoded type for hashes
- C# `string` for hash values; no `BitConverter.ToString`, no `Encoding.*`
- `tolower` / `toupper` / normalization of hash representations
- Hex-string parsing of incoming hashes
- Any conversion ceremony at marshalling boundaries

Add a debug-only `laplace_hash_to_hex(bytea) → text` if it helps inspection, but it MUST NOT appear in any data-flow path.

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
