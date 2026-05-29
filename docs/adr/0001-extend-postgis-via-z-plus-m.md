# ADR 0001: Extend PostGIS via Z+M, do not create parallel geometry type

## Status

**Accepted** — 2026-05-21

## Context

The substrate needs 4D coordinates (codepoints on S³ surface; centroids in the 4-ball interior). PostGIS provides the standard `geometry` type with optional Z (third spatial dim) and M (measure). We need to either (a) create a parallel `geometry4d` type with its own opclasses, I/O, casts, etc., or (b) use the standard `geometry` with both Z and M flags set, treating M as our fourth spatial dimension.

## Decision

Use **standard PostGIS `geometry`** with the Z and M flags set (`ST_HasZ AND ST_HasM`), interpreted as 4D (X, Y, Z spatial + M as fourth spatial dim).

**Do NOT** create a parallel `geometry4d` type.

> **Indexing refined by [ADR 0029](0029-custom-indexing-strategy.md).** This ADR originally specified indexing via PostGIS's existing `gist_geometry_ops_nd` N-dimensional GIST opclass. ADR 0029 superseded that: the substrate ships a **custom S³-aware GIST opclass** (`laplace_gist_s3_ops`) on the *same standard `geometry` type*, because entity coords lie on S³ / in the structured 4-ball interior where stock `gist_geometry_ops_nd`'s axis-aligned MBRs are wildly loose. The Z+M-on-standard-`geometry` decision below stands; only the opclass changed. Note also that the GIST index only *seeds candidates* — per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) (truth 3) the geometry carries meaning and what actually pulls back is Glicko-2 effective-μ across typed arenas, not nearest-neighbor distance.

Custom functions exist only where standard PostGIS is 2D/3D-only (Frechet, centroid, distance, etc.) — namespaced as `laplace_*_4d`.

## Consequences

- We inherit decades of PostGIS work: WKB I/O, indexing, predicates, standard ops on Z+M points.
- Cross-modality unification is automatic: same geometry type for text trajectories, image regions, audio segments.
- The 4D-aware function set is small (~15-20 custom functions) instead of a whole parallel type system.
- M is overloaded — semantically it's a "measure" in PostGIS, but we use it as a spatial dim. CHECK constraints enforce the 4D-Point/LineString shape.

## Alternatives considered

- **Parallel `geometry4d` type**: would have required custom WKB-style serialization, GIST opclass, all operators, all casts, all PG_FUNCTION wrappers. Rejected as massive duplication of PostGIS.

## References

- [RULES.md R1](../../RULES.md)
- [STANDARDS.md](../../STANDARDS.md)
- DESIGN.md Section I (schema)
