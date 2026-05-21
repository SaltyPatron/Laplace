# ADR 0012: Mantissa-packing format: 8 tier + 12 position + 60 truncated hash per vertex

## Status

**Accepted** — 2026-05-21

## Context

Trajectory entities store a LineString through their constituents. Each vertex needs to carry the constituent's identity. Several encodings considered: parallel `bytea[]` column with full hashes, side table, mantissa-packing into the coord doubles.

## Decision

**Mantissa-pack into 4D vertex coord components.** Per vertex (4 × float64):

- **8 bits** of tier (low bits of W's mantissa)
- **12 bits** of position-within-trajectory (low bits of W's mantissa)
- **60 bits** of truncated constituent hash (spread across low 20 mantissa bits of X, Y, Z)

High mantissa bits preserve approximate spatial position (for GIST bounding-box filtering); low bits carry payload.

## Consequences

- Trajectory is self-contained: no side table, no extra column.
- 60-bit truncated hash gives collision probability ~10⁻⁹ per pair within a trajectory; for trajectories <10⁶ vertices, expected collisions <10⁻¹⁸ — safe.
- Full 128-bit hash recovery is via an entity-table lookup, not from mantissa bits.
- Coord precision is partially sacrificed (~20 bits per dim), but spatial index uses MBR which is unaffected.

## Alternatives considered

- **Parallel `bytea[]` column** with full 128-bit hashes per vertex. Cleaner but breaks "linestring is self-contained" property.
- **Side table** keyed (trajectory_hash, position). More joins per query.

## References

- Memory: project_laplace_invention.md — "Mantissa-packing mechanic" section
- [STANDARDS.md](../../STANDARDS.md) — datatype standards
