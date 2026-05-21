# ADR 0005: Hilbert curve over bounding hyperbox, not the sphere

## Status

**Accepted** — 2026-05-21

## Context

The substrate places codepoint atoms on the surface of S³ (a 3-sphere in R⁴) and entities at higher tiers have centroids in the 4-ball interior. A Hilbert curve is needed for 1D locality-preserving indexing.

There are two natural choices:

1. **Sphere-native curve** (HEALPix-like) — only indexes the surface
2. **Hilbert curve over bounding hyperbox `[-1, 1]⁴`** — indexes the entire 4-ball, surface and interior alike

The interior is meaningful (radial abstraction-grading); surface-only indexing would leave centroid entities un-indexable via Hilbert.

## Decision

Use a **4D Hilbert curve over the bounding hyperbox `[-1, 1]⁴`**, 32-bit-per-dimension quantization → 128-bit Hilbert index.

Implementation: Skilling (2004) algorithm.

## Consequences

- One Hilbert curve indexes all entities — surface atoms AND interior centroids.
- B-tree on Hilbert index supports range scans across abstraction levels.
- ~20% of the hyperbox is outside the unit 4-ball (the corners); unused index space is acceptable.

## References

- John Skilling, "Programming the Hilbert curve" (2004)
- [DESIGN.md](../../DESIGN.md) Section I
- Memory: project_laplace_invention.md — "Geometric foundation" section
