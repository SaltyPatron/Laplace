# ADR 0016: Reusable helpers — DRY at every layer

## Status

**Accepted** — 2026-05-21

## Context

Recurring operations (hash, coord arithmetic, Hilbert encode/decode, mantissa pack, Glicko-2 update, marshalling) can be inlined ad-hoc or centralized as named, tested, single-source-of-truth helpers. Inlining leads to drift (bugs fixed in one place but not others), performance inconsistency, and bug-hunting overhead.

## Decision

Every operation used more than once **must** be a named, tested, single-source-of-truth helper.

Applies to:
- Hash operations (`hash128_from_bytes`, `hash128_merkle`, ...)
- Coord arithmetic (`coord4d_distance`, `coord4d_centroid`, ...)
- Hilbert encode/decode
- Mantissa pack/unpack
- Geometry serialization (bounds-checked)
- Glicko-2 update
- Trajectory enumeration
- PG ↔ engine marshalling (one wrapper per arg pattern)
- C# ↔ engine marshalling (one P/Invoke per engine function)

Cross-language consistency: ONE canonical engine implementation; PG wrappers + C# bindings are thin glue around it.

## Consequences

- Correctness solved once, performance optimized once, behavior consistent across callers.
- Bugs have ONE place to fix, not 17 inlined copies.
- New contributors find the canonical helper instead of inventing local variants.

## References

- [STANDARDS.md](../../STANDARDS.md) — "Reusable helpers — DRY at every layer" section
