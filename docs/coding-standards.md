# Coding Standards

These rules govern all code in the Laplace repository. They are concrete (this is what to do) rather than aspirational governance. They are non-negotiable; violations cause cross-source dedup, content-addressing, or substrate integrity to break.

The architecture lives in `docs/substrate-synthesis.md`. The plan lives at `~/.claude/plans/time-for-you-to-scalable-wind.md`. The session memory lives at `~/.claude/projects/D--Repositories-Laplace/memory/`. Read these at the start of every working session.

---

## Cardinal rules

1. **Services-first.** Every reusable operation is a shared service with a single canonical implementation. Same operation in two places = bug. Before adding a function, search the codebase for existing implementations.

2. **One type per file. No exceptions.** File name = type name. Comparers, helpers, options classes, DTOs, records, enums each get their own file. Service interfaces and concrete implementations live in separate files (and typically in separate projects: `*.Abstractions` for interfaces, `*` for implementations).

3. **Files are as small as cohesion allows.** Service implementations are 50-200 lines. Decomposers are 100-300 lines. If a file exceeds 500 lines, suspect cohesion drift; split into focused services.

4. **DRY ruthlessly across all layers.** Native + managed share via P/Invoke through ONE facade (`Laplace.Core.Native`). SQL + native share via PostgreSQL extension functions wrapping the same C symbols managed code uses. Never reimplement BLAKE3, Glicko-2, geometry math, etc. — call the canonical native service.

5. **No stubs. No HARTONOMOUS_NOT_IMPLEMENTED-style macros.** Either a function does the work or it doesn't exist as a callable. Empty signatures with milestone tags are bugs that ship as features.

6. **No agent-written architecture documentation.** The synthesis is the source. If something needs to be remembered, write it as a memory entry, a failing test, or a call-site comment — not as a separate architecture document.

---

## Layering and separation of concerns

| Layer | Owns | Does NOT |
|---|---|---|
| Native (C, in `ext/laplace_pg/`) | All compute primitives | I/O orchestration; pipeline batching; CLI |
| SQL (in `ext/laplace_pg/sql/` + `sql/migrations/`) | Schema, indexes, materialized views, type definitions, GiST/SP-GiST 4D operator classes, partition routing, set-based templates | Compute (delegate to native via PG extension functions) |
| Managed (.NET, in `src/Laplace.*`) | Orchestration, I/O, parsing, pipeline batching, CLI, Gödel Engine cognition | Compute (delegate to native via P/Invoke); inline SQL string concatenation (use templates as embedded resources) |

**Native interop is centralized in `Laplace.Core/Native/`.** No other project imports `System.Runtime.InteropServices` directly. Every native function has exactly one managed wrapper, generated via `[LibraryImport]` source generators (AOT-friendly).

**SQL templates live as embedded resources in `Laplace.Core/Sql/Templates/*.sql`.** No inline SQL string concatenation in business logic. Set-based bulk patterns (`COPY ... FROM STDIN BINARY`, `INSERT ... SELECT FROM unnest(...)`) are the only acceptable inline SQL forms.

---

## C# conventions

### Project structure

- One project per concern. Abstractions in `*.Abstractions` projects. Concrete implementations in `*` projects. Per-modality decomposers each get their own project (e.g. `Laplace.Decomposers.WordNet`).
- Project name = root namespace = assembly name.
- Namespace = directory path. Sub-namespaces match sub-folders.

### Naming

| Element | Convention |
|---|---|
| Namespace | `Laplace.{Project}` (e.g., `Laplace.Core.Abstractions`) |
| Interface | `I` + PascalCase (`IIdentityHashing`) |
| Async method | `...Async` suffix accepts `CancellationToken` |
| Private field | `_camelCase` |
| Constants | `PascalCase` |
| Enum values | `PascalCase` |

### Async and cancellation

- All I/O methods are `async` and accept `CancellationToken`.
- The token originates from the phase orchestrator (CLI), the request pipeline (eventual API), or the Gödel Engine task host (long-horizon goals).
- Pure computation (hashing, geometry math via P/Invoke) is synchronous.

### Error handling

- Fail loud. No `catch (Exception) { log; continue; }`. If it fails, the batch fails, the phase halts.
- `Result<T>` for expected failure modes (entity already exists, parse error).
- Exceptions for bugs and infrastructure failures — propagate up.
- Every `catch` block either rethrows with context or sits at a documented substrate boundary.

### Logging

- `Microsoft.Extensions.Logging` only. No `Console.WriteLine` in library code (CLI console output is fine).
- Structured properties (`{EntityCount}`), not string interpolation.
- Levels: Trace (per-entity), Debug (per-batch), Information (phase start/end), Warning (recoverable), Error (halt), Critical (process halt).

### Testing

- xUnit v3 + coverlet. No Moq — use hand-written fakes.
- Tests must not depend on external files or databases unless explicitly marked as integration tests.
- Synthetic data over file fixtures. Generate inputs in-test.
- Integration tests live in `tests/managed/Laplace.Integration.Tests` and require a running Postgres + the laplace_pg extension.

---

## C / C++ conventions

### File organization

- One operation per `.c`/`.h` pair under `ext/laplace_pg/src/<service-name>/`.
- Public headers (consumed by other services or by managed P/Invoke) live in `ext/laplace_pg/include/laplace_pg/<service-name>/`.
- Service-private headers stay alongside the .c files.

### Style

- C17 standard for plain C. C++20 standard for any C++ glue (e.g., Spectra wrappers, CGAL bindings).
- 4-space indent, LF line endings (per `.editorconfig`).
- 120-char soft line limit.
- Strict warnings as errors (`/W4 /WX /permissive-` on MSVC; `-Wall -Wextra -Wpedantic -Werror` elsewhere).

### Memory and safety

- AddressSanitizer-clean. Test builds run under ASan via `scripts/build.ps1 -Asan` and `scripts/test.ps1 -Suite native`.
- Defensive bounds checking at all FFI boundaries (P/Invoke and PG extension function entries).
- No undefined behavior. UBSan-clean for sanitizer-instrumented test builds.

### CPU dispatch

- AVX2 baseline. AVX-512 paths runtime-dispatched via the `CpuidService` (the user's 14900KS has AVX-512 fused off; AMD Zen 4+ and Intel Xeon receive the wider paths automatically).
- VNNI / SHA-NI dispatch the same way.

---

## SQL conventions

- All schema and SQL services live in `ext/laplace_pg/sql/laplace_pg--*.sql` (extension-owned) or `sql/migrations/<NNNN>_<name>.up.sql` / `<NNNN>_<name>.down.sql` (substrate-content schema). Numbering is monotonic; no gaps.
- One concern per migration file; small focused changes.
- All identifiers (table, column, function) are `lower_snake_case`.
- Schema-qualified everywhere (`laplace.entity`, never bare `entity`).
- Edge type and entity type vocabularies come from seed decomposer ingestion (Phase 3+); the schema does NOT enumerate them as enums.
- pgTAP tests for every SQL service in `tests/sql/<service>.sql`.

---

## Substrate-specific rules

These derive from the architecture but are reproduced here for visibility:

- **Tier 0 atoms are Unicode codepoints.** Full 1,114,112 (17 planes × 65,536), not just currently-assigned (~155K). Tier 1+ are compositions of lower-tier entities.
- **Identity = content.** BLAKE3 of canonical content for atoms; Merkle of ordered child hashes (with RLE counts) for compositions; `(edge_type_hash + role-ordered participant hashes)` for edges.
- **Maximum dedup + RLE everywhere.** Same content = ONE entity row, referenced as FEW times as physically possible. The same sky-blue pixel that appears in every outdoor image worldwide on a sunny day = one entity.
- **Knowledge IS edges and intersections.** Intersection / co-occurrence queries (`how many things intersect with X?`) are first-class.
- **Edge types and entity types are themselves substrate entities.** Compositions of their codepoint LINESTRINGs. Resolved via `IConceptEntityResolver` — never hardcoded English string codes in schema.
- **Three-layer Glicko-2 (rated-source attestation).** Sources, entities, edges each carry their own (μ, σ, volatility, games) state. Trusted-source observation = weighted win for the asserted edge. Absence of observation = high RD, not low rating.
- **Fireflies are AI model extraction artifacts.** Per-token-per-model 4D positions stored in `firefly_s3_extracted` physicality partition — separate from the `codepoint_s3_substrate` partition that holds substrate atom positions. Voronoi consensus over firefly clouds emerges from cumulative ingestion.
- **GEOMETRY4D is an independent custom PostgreSQL type family.** Full subtype family (POINT4D / LINESTRING4D / POLYGON4D / MULTI*4D / GEOMETRYCOLLECTION4D / TRIANGLE4D / TIN4D / POLYHEDRALSURFACE4D / CIRCULARSTRING4D / COMPOUNDCURVE4D / CURVEPOLYGON4D / MULTICURVE4D / MULTISURFACE4D / BOX4D), own ST_4D_* operators. Additive to existing PostGIS — never reuses GEOMETRYZM with M repurposed.
- **The Gödel Engine is THE behavioral engine.** Surface where ALL reasoning patterns live (CoT, ToT, ReAct, Reflexion, Self-Consistency, Graph-of-Thought, hypothesis-driven, self-questioning, goal decomposition, honest abstention, long-horizon churning, analogy, abduction, meta-cognition). Composed of OODA cycles at three scales (micro / meso / macro). The AGI/ASI enabler.

---

## Pre-rejected substitutions (the conventional ML pulls AI agents fall into)

Reject before discussion:

- Glicko-2 → ELO ❌
- Exact KNN → HNSW / approximate ❌
- GEOMETRY4D parallel family → GEOMETRYZM with M repurposed ❌
- Edges reference entities → hardcoded enum of English string codes in schema ❌
- AI model semantic edge extraction → tensor-as-entity weight storage ❌
- Universal codepoint atoms → per-modality atom pools ❌
- Numbers as digit-codepoint LINESTRINGs → integer atom types ❌
- Real algorithm → stub with milestone tag ❌
- Content-addressed identity → opaque metadata hash with English prefix ❌
- Rated-source attestation → competitive negative sampling ❌
- Decomposer implementing primitives inline → decomposer not consuming a shared service ❌
- Same operation in two files → two implementations instead of one shared service ❌
- Large file with multiple types → multiple files, one type per file ❌
- ~155K assigned codepoints → full 1,114,112 codepoint space ❌
- Row-count gates → semantic-fidelity gates ❌
- Atom + Composition + Relation as separate tables → ONE tiered `entity` table + `edge` table ❌
- Entity referenced N times redundantly → entity referenced as FEW times as physically possible (max dedup + RLE) ❌
- Gödel Engine as just self-questioning → Gödel Engine as the behavioral engine for ALL reasoning patterns ❌
- Phases-in-isolation → phases with explicit cross-phase integration verification (convergence gates G1-G10) ❌

---

## Verification methodology

Each work unit:
1. **Gate written first** as runnable code (semantic-fidelity, not row count). SQL query, PowerShell, native test, or managed test.
2. **Implementation** until the gate passes. No "mostly works." No "compile = done."
3. **Inline evidence** when claiming done. Paste actual output.
4. **Cross-session audit** before merge. A fresh session reads the diff against `docs/substrate-synthesis.md` and reports drift.

Convergence gates G1–G10 fire continuously as their dependencies are met, NOT deferred to "the end." See the build plan for the gate definitions.
