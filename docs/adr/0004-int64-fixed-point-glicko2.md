# ADR 0004: int64 fixed-point Glicko-2 ratings

## Status

**Accepted** — 2026-05-21

## Context

Glicko-2 ratings, RDs, and volatilities are real-valued. Storing them as `double precision` introduces FP non-determinism in the update path (different reduction orders → different ratings on different machines or different runs).

Determinism is a substrate invariant (RULES.md R7): cross-machine reproducibility is non-negotiable.

## Decision

Store rating / RD / volatility as `int64` fixed-point at scale 10⁹ (one billion).

All Glicko-2 math is integer arithmetic — no `double` intermediates. The fixed-point representation gives ~9 decimal digits of precision, ample for rating distinctions.

## Consequences

- Cross-machine determinism for the Glicko-2 update path.
- Math is vectorizable (SIMD integer ops).
- Numerical accuracy bounded by fixed-point scale; verified against double-precision reference within 10⁻⁶ relative error.
- A small fixed-point math library is needed (multiply, divide, exp, log) — exists in C99 with care.

## References

- [STANDARDS.md](../../STANDARDS.md) — datatype standards
- [RULES.md R7](../../RULES.md) — determinism by construction
