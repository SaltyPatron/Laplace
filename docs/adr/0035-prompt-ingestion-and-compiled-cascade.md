# ADR 0035: Prompt ingestion and compiled cascade traversal

## Status

**Accepted** — 2026-05-22

## Context

Conventional serving stacks treat a prompt as an ephemeral token buffer and run a forward pass over that buffer. SQL-native graph approaches often express traversal as recursive CTEs, cursors, or application-side row-by-row loops. Both patterns are wrong for Laplace.

Laplace already has the substrate structure needed to make a prompt part of the substrate itself: Unicode-rooted tiered entities, content-addressed Merkle hashes, trajectories, source/context entities, attestation indexes, T0 perf-cache, and C/C++ engine primitives.

The key runtime question is where the recursive traversal loop lives. If it lives in app code or SQL-level RBAR, every frontier step pays avoidable executor/network/control-flow overhead. If it lives in the C/C++ engine inside the PostgreSQL backend, one SQL call can ingest prompt content, seed a frontier, perform indexed attestation expansion, and stream results.

## Decision

A prompt is ingested as substrate content before inference. It may be ephemeral or durable by policy, but it is always decomposed into tiered entities and represented by a context entity/trajectory before cascade traversal begins.

Cascade inference is exposed as a single SQL-call surface, implemented as a set-returning C function owned by `laplace_substrate`. The C/C++ engine owns the recursive traversal loop: priority queue, visited set, tier transitions, effective-score ranking, context compatibility, and early abstention. PostgreSQL provides storage, MVCC visibility, and indexes. SPI/executor access is permitted only for batched, prepared, indexed lookups; it is not the traversal brain.

The implementation MUST NOT express the cascade frontier as recursive CTEs, cursors, app-layer polling, or row-by-row loops. SQL is the invocation surface for compiled substrate operators.

T0 perf-cache is part of the runtime contract. Clients and ingestion workers can compute codepoint atom hash, canonical coordinate, Hilbert index, UCA order, and flags without DB round trips. T1+ values are then built from child hashes/coords before INSERT.

## Consequences

- Laplace has no context-window primitive. Context is ingested substrate content.
- Long prompts, documents, corpora, and previous conversations are bounded by storage, ingestion cost, and traversal budget, not a transformer positional buffer.
- Duplicate prompt/content spans collapse to existing entity hashes and trajectories. Novel spans create only novel entities.
- One SQL call can recursively walk across tiers and attestation arenas while streaming results.
- The database is not used as a procedural language for graph search; the compiled engine loop avoids RBAR/CTE/cursor overhead.
- Native endpoint responses can expose path evidence: source trace, effective rating, RD, volatility, context, and competing paths.

## Alternatives considered

- **Application-side loop issuing repeated SELECTs.** Rejected — network/executor round trips destroy the substrate runtime model.
- **Recursive CTE graph traversal.** Rejected — useful for inspection and diagnostics, not the hot-path cascade engine.
- **Prompt as transient model buffer.** Rejected — this reintroduces context windows and conventional serving assumptions.

## References

- [RULES.md R6](../../RULES.md) — DB as dumb columnar store; entity math in C/C++
- [RULES.md R8](../../RULES.md) — no GPU at runtime
- [RULES.md R19](../../RULES.md) — prompt is ingestion; cascade is compiled
- [DESIGN.md](../../DESIGN.md) — runtime execution model
- [GLOSSARY.md](../../GLOSSARY.md) — Prompt Ingestion, Compiled Cascade
