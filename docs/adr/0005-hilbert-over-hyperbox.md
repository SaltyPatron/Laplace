# ADR 0005: Hilbert curve over bounding hyperbox, not the sphere

## Status

**Accepted** — 2026-05-21

## Context

The substrate places codepoint atoms on the surface of S³ (a 3-sphere in R⁴) and entities at higher Merkle strata have centroids in the 4-ball interior. A Hilbert curve is needed for a 1D locality-preserving **candidate-seeding access vertical** — a range-scannable projection of the geometry.

This is access plumbing, not retrieval. Per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth 3/4, retrieval is **not** nearest-neighbor: the geometry only *seeds candidates*; what pulls back and how hard is decided by Glicko-2 effective-μ across typed arenas (RD, volatility, source trust, lineage, context, arena policy), traversed by indexed A\*. The Hilbert curve narrows candidates; it does not rank or decide them by distance.

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
