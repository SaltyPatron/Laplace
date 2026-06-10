---
applyTo: "{app/**,engine/**,extension/**,docs/**}"
description: "Use when touching C# app code, engine kernels, PG extensions, CLI/API SQL, or architecture docs."
---

# Laplace Layering Law

C# and SQL orchestrate. C/C++ and PG C SRFs compute. One compiled kernel per math truth.

## Authority boundaries

| Layer | Role | Must NOT |
|-------|------|----------|
| `engine/core`, `dynamics`, `synthesis` | Deterministic math, parsing kernels, COPY-binary builders | Host SQL, business rules, HTTP |
| `extension/laplace_*` | Versioned SQL surface + thin C wrappers + SPI neighbor providers | Duplicate logic already in engine |
| `app/Laplace.*` | Orchestration, marshalling, batching, I/O | Inline math (Glicko, eigenmaps, scoring, SIMD) |
| `app/Laplace.Cli`, `Endpoints.*` | Call `laplace.*` functions; parameterized queries only | `WITH RECURSIVE` CTEs, inline business SQL |

## C# rules

- Use `LibraryImport` / pinned spans for native calls. No `Vector128`/`Simd` in `app/`.
- Glicko2, score law, trajectories, text decomposition, grammar compose → `laplace_core` bindings.
- Laplacian eigenmaps, Procrustes, bilinear/FFN tiles → `laplace_dynamics`.
- QK scoring, SVD truncate, BF16 decode, GGUF → `laplace_synthesis`.
- Do not add `Parallel.For` for float→double conversion or matrix math — add or call a native kernel.
- `CalibratedInverse` and similar calibration must use `Glicko2.UpdatePeriod` in C#, not SQL round-trips.

## SQL rules

- Business logic lives in `extension/laplace_substrate/sql/*.sql.in` and `extension/laplace_geom/sql/*.sql.in`.
- CLI/API/SubstrateCRUD call `laplace.*` functions or COPY — never duplicate extension CTEs.
- Direct `SELECT ... FROM laplace.consensus` in app code is forbidden; use `laplace.*` read surfaces or COPY export.
- Period fold, converse, generate, cascade → extension only.

## Production toolchain

- Production builds require Intel oneAPI: `MKLROOT`, `TBBROOT`, `CMPLR_ROOT`.
- MKL-hard paths (`bilinear_edges`, `ffn_edges`, `tensor_svd_truncate`) must fail fast at startup if unavailable — not silent `-2` at runtime.

## Forbidden patterns

- Inline `WITH RECURSIVE` generation walks in `Laplace.Cli/Program.cs`
- C# Glicko2 via `laplace_glicko2_accumulate_games` SQL when `Glicko2.UpdatePeriod` exists
- `Hash128.OfCanonical("substrate/type/...")` in decomposers (use registries — see type-id-law)
- Per-line `new byte[]` in fast-ingest line readers
- `ContentEmitter.Emit` inside inner loops of corpus-scale decomposers
