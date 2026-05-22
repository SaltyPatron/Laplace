# ADR 0029: Custom indexing strategy — five substrate-shaped opclasses

## Status

**Accepted** — 2026-05-21

Refines [RULES.md R1](../../RULES.md): custom GIST opclasses ARE permitted (and built) where they exploit substrate-specific structure that general-purpose opclasses can't.

## Context

The original [RULES.md R1](../../RULES.md) said: "Do NOT write custom GIST opclasses unless PostGIS's `gist_geometry_ops_nd` provably fails for our access pattern (it doesn't)." That framing was correct in spirit (don't speculatively replace mature general-purpose code) but turns out to be wrong on substance: the substrate has *several* structural facts that general-purpose opclasses can't exploit, and writing custom opclasses against those facts produces order-of-magnitude wins, not marginal ones.

Specifically:

1. **BLAKE3-128 entity hashes** are uniform-random `bytea(16)`. The most frequent index probe in the system is "is this hash present?" — and stock B-tree on `bytea` uses generic `memcmp` plus split heuristics that defend against insertion-clustering (irrelevant for uniform data).
2. **Tier-0 codepoint entities** live on S³ (radius = 1). **Tier 1+ composite entities** populate the 4-ball interior with their *radius* encoding abstraction tier ([GLOSSARY.md](../../GLOSSARY.md) — radial abstraction). The geometric distribution is structured (concentric, fibered), not uniform — so `gist_geometry_ops_nd`'s axis-aligned 4D MBR boxes are wildly loose for S³-surface entities (a point on S³ has a box MBR spanning ~radius-1 in every dimension).
3. **Trajectories** (entity paths through their constituents) are tier-ordered linestrings; trajectories sharing a prefix are structurally close in the substrate's compositional model. Cascade descent walks are *prefix-extensions of trajectories* — an access pattern that matches SP-GiST's unbalanced k-way partitioning exactly.
4. **Entities are clustered on disk by tier** (perf-cache build order). Tier is a physically-monotonic column on the heap — a textbook BRIN candidate.
5. **Cascade A*** uses Glicko-2 ratings as edge weights. Internal GIST nodes that carry per-subtree aggregate Glicko-2 stats (`max(rating)`, `min(rd)`) enable branch-and-bound pruning *at the index level*, before any heap access.

## Decision

Land five custom opclasses across Chunks 1–5, distributed across the two PG extensions ([ADR 0025](0025-pg-extension-modularization.md)). Each opclass has its own pg_regress tests + a benchmark against the stock alternative with concrete acceptance thresholds.

### 1. `laplace_btree_hash128_ops` — B-tree for BLAKE3-128 keys

**Home.** `laplace_geom` extension (hash128 is a general-purpose primitive).

**Substrate fact exploited.** BLAKE3-128 outputs are uniform; collision-safe for ~10¹⁸ entities; `bytea(16)`-shaped.

**What it adds over stock B-tree on `bytea`.**
- SIMD-friendly 16-byte compare (`_mm_cmpeq_epi64` + branchless mask) replacing `memcmp`'s loop
- `sort_support` proc telling the planner this type is cheap to sort in-place → enables Sort-then-Merge over hash columns
- Simpler page-split policy because BLAKE3 uniformity makes hotspot-defense overhead pure cost

**Acceptance.** pg_regress proves correctness on 10⁶ random keys; microbench shows ≥ 1.5× speedup vs stock `bytea` ops on equality probes.

**Chunk target.** Chunk 1 (Story 1.14).

### 2. `laplace_gist_s3_ops` — S³-aware GIST keys for 4D geometry

**Home.** `laplace_geom` extension.

**Substrate fact exploited.** Entity coords lie on S³ or in the 4-ball interior with structured radius. Stock `gist_geometry_ops_nd` uses axis-aligned 4D MBRs that are wildly loose for S³ surface points.

**What it adds.**
- Key type: spherical cap (center direction unit-vector + angular radius) for S³-surface clusters; radial slab + angular cone for interior clusters
- `union(keys)` = smallest cap containing all child caps
- `distance(key, query)` = great-circle distance to nearest point in cap, vs Euclidean box-to-point
- KNN traversal touches far fewer pages

**Acceptance.** KNN benchmark on 10⁵ S³-distributed entities vs `gist_geometry_ops_nd` shows ≥ 2× reduction in page reads.

**Chunk target.** Chunk 2 (Story 2.15).

### 3. `laplace_sp_trajectory_ops` — tier-prefix SP-GiST for trajectories

**Home.** `laplace_substrate` extension.

**Substrate fact exploited.** Trajectories are tier-ordered vertex sequences; cascade descent walks are prefix-extensions.

**What it adds.**
- Partitions trajectories at each tree level by their vertex AT THAT DEPTH (level 0: Tier-1 head; level 1: Tier-2 second vertex; etc.)
- "Find all trajectories sharing prefix [v0, v1, v2]" becomes O(depth + matches)
- Index structure mirrors the cascade structure → cascade A* descent is a natural traversal of the index itself

**Acceptance.** pg_regress on synthetic trajectory set proves O(depth + matches) behavior on prefix-match queries.

**Chunk target.** Chunk 2 (Story 2.16).

### 4. `laplace_brin_tier_ops` — BRIN summary on tier

**Home.** `laplace_substrate` extension.

**Substrate fact exploited.** Entities are physically clustered on disk by tier (perf-cache build order writes Tier 0 first, then Tier 1, etc.); within each tier, sub-clustered by Hilbert curve.

**What it adds.**
- Compound key: `(min_tier, max_tier, min_hilbert_index, max_hilbert_index)` per BRIN page range
- "WHERE tier = N" and "WHERE tier = N AND hilbert_index BETWEEN ..." get page-skip behavior without B-tree traversal
- Zero update cost; trivial implementation

**Acceptance.** EXPLAIN ANALYZE demonstrably skips non-matching page ranges on tier-filtered queries.

**Chunk target.** Chunk 3 (Story 3.13).

### 5. Glicko-2-aware GIST internal stats (extension of `laplace_gist_s3_ops`)

**Home.** `laplace_substrate` extension (extends the `laplace_geom` opclass with substrate-domain stats).

**Substrate fact exploited.** Cascade A* uses Glicko-2 rating as edge weight; index-level aggregate stats enable branch-and-bound pruning.

**What it adds.**
- Internal GIST nodes carry `max(rating)`, `min(rd)` aggregates across children
- `union()` updates the aggregates when children merge
- `consistent()` short-circuits when query's rating threshold exceeds node's max — pruning happens at the index level, before any heap access

**Acceptance.** Cascade A* benchmark shows reduced row visits vs the non-aware opclass on rating-filtered queries.

**Chunk target.** Chunk 5 (Story 5.12).

### Cumulative implementation cost

~200-800 LOC C per opclass + 30-100 LOC SQL each. Spread across Chunks 1-5. Each opclass lands with its own ADR section (this ADR is the umbrella; each opclass commit references this ADR's relevant section).

## Consequences

- **Substrate access patterns get substrate-shaped indexes.** Order-of-magnitude wins on the hottest paths (hash probe, KNN, cascade descent) over general-purpose opclasses.
- **Custom opclasses live in PG extensions, not in DbUp migrations.** They're part of the extension's `--A.B.C.sql` file (per [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md)). Versioning is the extension's responsibility.
- **`laplace_geom` becomes substantively valuable on its own.** The hash128 B-tree opclass and S³-aware GIST opclass are useful to any 4D-PostGIS work, not just the substrate. Increases the reusability case for the extension split ([ADR 0025](0025-pg-extension-modularization.md)).
- **Each opclass has measurable acceptance.** Not "feels faster" — concrete numbers from `pg_regress` + `pgbench` against stock opclasses on substrate-shape workloads.
- **[RULES.md R1](../../RULES.md) is amended.** Custom GIST opclasses are permitted when they exploit substrate-specific structure with an ADR justifying the case. Speculative replacement of working general-purpose code still forbidden.

## Alternatives considered

- **Stay on stock opclasses (`gist_geometry_ops_nd` + B-tree on `bytea`).** Rejected — leaves order-of-magnitude wins on the table on the substrate's hottest paths.
- **Implement only the highest-value opclass (B-tree hash128) and skip the rest.** Rejected — each opclass has its own well-defined ROI and they don't interfere; doing them across Chunks 1-5 spreads the cost without blocking forward progress.
- **Implement a full custom Access Method backed by the perf-cache** (instead of/in addition to opclasses). Tracked as a separate research spike — see [ADR 0031](0031-custom-am-spike.md). Much higher implementation cost; deferred post-v0.1.0.

## References

- [PostgreSQL — Generalized Search Tree (GIST) API](https://www.postgresql.org/docs/current/gist-extensibility.html)
- [PostgreSQL — Space-Partitioned GIST (SP-GiST) API](https://www.postgresql.org/docs/current/spgist-extensibility.html)
- [PostgreSQL — Block Range Index (BRIN) API](https://www.postgresql.org/docs/current/brin-extensibility.html)
- [PostgreSQL — B-tree opclass interface (sort_support, etc.)](https://www.postgresql.org/docs/current/btree-support-funcs.html)
- [PostgreSQL — Index Operator Classes and Families](https://www.postgresql.org/docs/current/indexes-opclass.html)
- ADR 0001 (extend PostGIS via Z+M)
- ADR 0015 (BLAKE3-128 truncated, raw `bytea(16)`)
- ADR 0023 (extension owns schema; opclasses land in extension `.sql` files)
- ADR 0024 (engine `liblaplace_core` provides the kernels these opclasses dispatch to)
- ADR 0025 (PG extension layout — `laplace_geom` vs `laplace_substrate` homes)
- [ADR 0031](0031-custom-am-spike.md) — research spike for a fuller perf-cache-backed AM
- RULES.md R1 — amended by this ADR
- `extension/laplace_geom/src/`, `extension/laplace_substrate/src/`
