# STANDARDS.md — Datatype, Naming, Coding Standards

These are the binding standards for all code in this project. Inconsistency here causes drift; drift causes bugs; bugs at the foundation cascade. **Lock these in once; do not deviate without explicit authorization.**

---

## Datatype standards

| Concern | Type | Notes |
|---|---|---|
| 4D coordinate component | `float64` (per component) | Room for mantissa packing; sufficient precision |
| Entity hash | `uint128` stored as `bytea(16)` in PG; `hash128_t = {uint64_t hi, lo}` in C/C++ | XXH3-128; collision-safe for ~10¹⁸ entities |
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
- **Hashing:** XXH3-128 is deterministic by spec; use standard variant.
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

- **Unit tests:** `engine/test/` for engine code (C++ test framework — Catch2 or doctest); `extension/test/` for PG via `pg_regress`; `app/test/` for C# (xUnit or NUnit).
- **Integration tests:** `test/integration/` — end-to-end scenarios crossing the engine/extension/app boundary.
- **Round-trip determinism tests:** cross-machine reproducibility verified per release.
- **Cross-language consistency:** the same engine function called via SQL and via C# P/Invoke must produce byte-identical results.

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
├── engine/                           ← C/C++ engine library
│   ├── CMakeLists.txt
│   ├── include/laplace/              ← public headers (C ABI)
│   ├── src/                          ← implementation (C++)
│   ├── test/                         ← unit tests
│   └── third_party/                  ← Spectra, etc.
├── extension/                        ← PostgreSQL extension
│   ├── Makefile                      ← PGXS-based
│   ├── laplace.control
│   ├── laplace--1.0.0.sql
│   ├── src/                          ← PG_FUNCTION wrappers
│   └── test/                         ← pg_regress tests
├── app/                              ← C# .NET 10 projects
│   ├── Laplace.Engine/               ← P/Invoke bindings
│   ├── Laplace.Synthesis/            ← Substrate Synthesis API
│   └── Laplace.Endpoints.OpenAI/     ← OpenAI-compat plugin
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
- SQL: `snake_case.sql` (with extension version: `laplace--<version>.sql`, `laplace--<from>--<to>.sql` for migrations)
- Scripts: `kebab-case.sh`
- Markdown: `UPPERCASE.md` for top-level project docs; `kebab-case.md` for sub-docs.

---

## Build standards

- **C/C++:** primary compiler `icx`/`icpx` (Intel 2026); fallback `g++` 11.4 / `clang++` 14.
- **C++ standard:** C++23 where available; C++20 minimum.
- **CMake** as the engine build system; **PGXS** for the extension; **dotnet** CLI for C# projects.
- **Build modes:** `Debug` (no opt, full symbols), `Release` (O3 / `/O2`, LTO), `RelWithDebInfo` (O2 + symbols).
- **Sanitizers in CI:** ASan, UBSan on Debug builds.
- **No `-ffast-math`** on hot paths (breaks FP determinism).
- **Vectorization target:** `-march=haswell` minimum (AVX2 on this dev box); `-march=sapphirerapids` or `-mavx512f` for AVX-512 deployment targets.

---

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
