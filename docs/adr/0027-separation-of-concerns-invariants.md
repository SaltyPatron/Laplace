# ADR 0027: Separation of concerns invariants

## Status

**Accepted** — 2026-05-21

## Context

Across the engine ([ADR 0024](0024-engine-modularization.md)), PG extensions ([ADR 0025](0025-pg-extension-modularization.md)), and C# app layer ([ADR 0026](0026-csharp-project-structure.md)), each layer is meant to do exactly one kind of work:

- C/C++ engine does **math, hashing, geometry, linalg, sparsity, codecs**
- PG extensions do **PG-binding glue** (PG_FUNCTION_INFO_V1 wrappers, opclass dispatch, schema DDL)
- C# app does **orchestration** (pipelines, plugin host, protocol adapters, DB connections, CLI)
- SQL migrations do **declarative DDL only** (CREATE EXTENSION, GRANT, ALTER DEFAULT PRIVILEGES)

But code drifts. Without an explicit, codified discipline, math leaks into C# "just for convenience"; pipeline logic creeps into PG via PL/pgSQL; the PG extension grows business logic that should be in the engine; SQL migrations sprout procedural transforms.

The substrate's correctness, performance, and reproducibility depend on each layer staying in its lane. Math implemented twice (once in engine, once in C#) drifts. Pipeline logic implemented in PL/pgSQL can't be SIMD-vectorized. Determinism guarantees depend on a single source of truth for every kernel.

## Decision

Codify a per-layer **may-do / must-not-do** matrix as a project-wide invariant. Violations are caught at code review (and where possible, at build/lint time).

### Per-layer invariant matrix

| Layer | MAY do | MUST NOT do |
|---|---|---|
| **C/C++ engine** (`engine/{core,dynamics,synthesis}/`) | All math, linalg, hashing, geometry, sparsity, codec, SIMD, fixed-point, file-format read/write | Pipeline orchestration, plugin loading, network I/O, DB connection management |
| **PG extension (C wrappers)** (`extension/{laplace_geom,laplace_substrate}/src/`) | Datum↔engine-struct marshalling, PG_TRY/PG_CATCH wrapping engine calls, opclass support functions (consistent/union/penalty/picksplit/etc.), schema DDL via `.sql` files | Re-implementing engine math, non-trivial PL/pgSQL business logic, control flow that isn't dispatching to engine |
| **C# orchestration** (`app/*.csproj`) | Pipeline scheduling, plugin host, protocol endpoint adapters, DB connection (Npgsql), CLI, recipe parsing, source plugin host, network I/O | Math beyond trivial accounting (counts, predicates, comparisons); reimplementing engine functions for "convenience"; any hot-path numerical code |
| **SQL / DbUp migrations** (`db/migrations/`) | Idempotent DDL; role grants; `CREATE EXTENSION` orchestration (direct, since `laplace_admin` is `SUPERUSER` per [ADR 0045](0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md)); `ALTER DEFAULT PRIVILEGES` | Business logic; procedural transforms; anything non-declarative; substrate schema definition (per [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md), schema lives in extension upgrade scripts) |

### Resolution rule for "where does this belong?"

> A piece of work belongs in **the lowest layer that can correctly express it.** Math always lands in C/C++. Orchestration in C# (or SQL if it's declarative DDL). The PG extension is *only* the binding surface.

Counterexamples:

- "I want to compute a simple sum of `int` ratings in C# for a debug log." — Trivial, no SIMD opportunity, no shared-truth concern. Stays in C#. (May-do.)
- "I want to compute coord4d distance in C# because it's just `sqrt(dx²+dy²+dz²+dw²)`." — **Must-not-do.** That's the canonical kernel; C# must P/Invoke into `coord4d_distance` via `Laplace.Engine.Core`. Drift between two implementations is unacceptable.
- "I want to write a PL/pgSQL function that walks an attestation chain." — **Must-not-do.** Cascade traversal is engine work; expose via a C SRF in the extension that calls into engine.
- "I want to add a `CREATE TABLE laplace.foo` to a DbUp migration." — **Must-not-do.** Substrate tables are owned by the extension's upgrade scripts ([ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md)). Migration files in `db/migrations/` orchestrate extension lifecycle only.

### Detection

- **Code review checklist** in CONTRIBUTING.md's Definition of Done includes "Math beyond trivial accounting?  → in engine." and "DDL in db/migrations/ touches substrate schema?  → re-target to extension upgrade script."
- **Build-time lint where possible**: a future CI check greps `app/*.cs` for `Math.Sqrt`/`Math.Pow` on coord types and `Vector4` math, flagging probable drift.
- **Pre-commit hook**: blocks commits that add `CREATE TABLE laplace.` to any `db/migrations/*.sql` file.

## Consequences

- **Single source of truth for every numerical kernel.** PG-side and C#-side both call into engine functions; results are byte-identical because they're the same compiled code path.
- **Determinism is enforceable.** Engine FP regime (no `-ffast-math`, deterministic reductions) governs ALL math everywhere. No drift across layers.
- **PG backend lean.** Heavy compute (oneMKL via `liblaplace_dynamics`) never gets loaded into PG worker processes because the PG extension only links `liblaplace_core` and only dispatches to engine functions.
- **Plugins stay shallow.** A new source plugin is C# orchestration code calling engine kernels. It doesn't reimplement Procrustes; it calls `liblaplace_dynamics`. Per [RULES.md R10](../../RULES.md) (one plugin = one capability).
- **Code reviewers have a clear rule** to point at when reviewing PRs. "This math belongs in `engine/core/`, not `app/Laplace.Sources.Transformer/`."

## Alternatives considered

- **No formal invariant; trust review judgment.** Rejected — judgment drifts; the substrate has invariant-shaped correctness (determinism, reproducibility) that demands invariant-shaped discipline. Codified > implicit.
- **Strict static enforcement (banned-import lists per project).** Considered. Partially adopted (pre-commit hook for the DDL case). Full static enforcement of "no math in C#" is hard to detect statically; deferred to CR + linter heuristics.
- **Looser rule: "math in C++ unless trivial, judgment-call".** Rejected — "judgment-call" is exactly how drift starts. The substrate's value proposition requires no drift.

## References

- [RULES.md R6](../../RULES.md) — original "DB as dumb columnar store; entity math in C/C++" — this ADR extends that principle to all four layers
- [RULES.md R10](../../RULES.md) — polymorphic plugin architecture (one capability = one plugin, never spread across layers)
- [RULES.md R14](../../RULES.md) — C ABI at engine boundaries
- [ADR 0024](0024-engine-modularization.md) — engine layer structure
- [ADR 0025](0025-pg-extension-modularization.md) — PG extension layer structure
- [ADR 0026](0026-csharp-project-structure.md) — C# layer structure
- CONTRIBUTING.md — Definition of Done (CR enforcement)
