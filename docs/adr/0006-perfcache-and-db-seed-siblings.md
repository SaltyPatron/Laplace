# ADR 0006: Perf-cache and DB seed as sibling artifacts of UCD

## Status

**Accepted** — 2026-05-21

## Context

The substrate needs T0 (codepoint) data in two places: (1) a memory-mapped perf-cache binary for hot-path lookups, (2) seeded rows in the `entities` table for FK integrity and DB-side queries.

These could be related: build perf-cache from UCD, then seed DB from perf-cache. Or: derive both independently from UCD.

## Decision

**Both perf-cache and DB seed are derived independently from Unicode UCD.** Sibling artifacts. Neither feeds the other.

The deterministic derivation (UCA + super-Fibonacci + Hopf + Hilbert + BLAKE3) is run twice — once emitting the perf-cache binary, once emitting the DB-seed COPY data — both consuming the same UCD source.

## Consequences

- Independent regeneration: either artifact can be rebuilt from UCD without dependency on the other.
- Cross-verification possible: load both, compare row-for-row; any mismatch indicates a build problem.
- No single point of failure: changing perf-cache doesn't require reseeding DB.
- Versioning is by Unicode version, not by perf-cache or DB-seed version.

## References

- [RULES.md R7](../../RULES.md) — determinism by construction
- Memory: project_laplace_invention.md — "Three-phase architecture" / "Build pipeline" section
