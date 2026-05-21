# STANDARDS.md — Datatype, Naming, Coding Standards

These are the binding standards for all code in this project. Inconsistency here causes drift; drift causes bugs; bugs at the foundation cascade. **Lock these in once; do not deviate without explicit authorization.**

---

## Datatype standards

| Concern | Type | Notes |
|---|---|---|
| 4D coordinate component | `float64` (per component) | Room for mantissa packing; sufficient precision |
| Entity hash | `uint128` stored as `bytea(16)` in PG; `hash128_t = {uint64_t hi, lo}` in C/C++ | BLAKE3 truncated to 128 bits; collision-safe for ~10¹⁸ entities; **raw bytes only — never hex/text** |
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

## Dependency sources (locked)

Adding a new dep requires (a) listing it here, (b) declaring install path/method, (c) verifying RULES.md doesn't ban it, (d) user authorization.

| Library | Source | Notes |
|---|---|---|
| **Eigen 3.4** | `apt install libeigen3-dev` | Found via `pkg-config eigen3` (→ `/usr/include/eigen3`) |
| **Intel oneMKL** | Intel oneAPI 2026 (`/opt/intel/oneapi/mkl/latest`) | Comes with oneAPI; needs `setvars.sh` sourced for compiler tooling, but lib path is discoverable via `MKLROOT` |
| **Intel oneTBB** | Intel oneAPI 2026 | Same as oneMKL |
| **Intel IPP / DPL** | Intel oneAPI 2026 | Available as needed |
| **Spectra** | CMake `FetchContent`, pinned `v1.2.0` | Header-only; no apt; FetchContent is self-contained in CMakeLists.txt |
| **BLAKE3** | CMake `FetchContent` (official `c/` subdir), pinned `1.5.4` | SIMD-accelerated cryptographic hash; 128-bit truncated → comfortable collision space for ~10¹⁸ entities; raw 16-byte output (no hex/text conversions ever) |
| **tree-sitter runtime** | `/usr/local/lib/libtree-sitter.so` | Manual install (pre-existing); link directly |
| **tree-sitter grammars** | `/vault/Data/TreeSitter` | 303 grammars curated; decomposer selects per modality |
| **PostgreSQL 18** | apt (pgdg repository) | + PostGIS 3.6.3 (system-installed; provides `gist_geometry_ops_nd`) |
| **ICU 70.1** | apt (`libicu-dev`) | UCA collation support; `pkg-config icu-uc icu-i18n` |
| **Boost 1.74** | apt (`libboost-dev`) | Minimal use |
| **BLAKE3** | NOT INSTALLED | XXH3-128 used instead; revisit only if cryptographic strength becomes a requirement |
| **.NET 10 SDK** | Microsoft package | Already installed at `/usr/lib/dotnet/` |

**vcpkg** is present at `/home/ahart/vcpkg` but **NOT in use**. Reserved for if/when we accumulate a second C++ dependency that's neither in apt nor trivially fetchable. For now, the dep set is small enough that vcpkg's toolchain overhead isn't justified.

## Build standards

- **C/C++:** primary compiler `icx`/`icpx` (Intel 2026); fallback `g++` 11.4 / `clang++` 14.
- **C++ standard:** C++23 where available; C++20 minimum.
- **CMake** as the engine build system; **PGXS** for the extension; **dotnet** CLI for C# projects.
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
