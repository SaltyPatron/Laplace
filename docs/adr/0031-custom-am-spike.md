# ADR 0031: Custom Access Method backed by perf-cache — research spike (post-v0.1.0)

## Status

**Accepted (as a tracked research spike)** — 2026-05-21

**Not a v0.1.0 commitment.** Scheduled for execution after the v0.1 milestone lands (chattable model round-trip synthesis — same synthesis machinery filling the source's own mold). Tracked here so the option isn't forgotten.

> Note (2026-05-28): the original "Chunk 8" scheduling reference predates [ADR 0060](0060-retire-chunk-sequence-v0.1-milestone-cadence.md), which retired the chunk sequence in favor of the v0.1 milestone. This spike still gates on v0.1.0 shipping; the chunk label is gone.

## Context

The perf-cache ([memory: laplace-performance](../../memory/project_laplace_performance.md), [GLOSSARY.md](../../GLOSSARY.md)) is a memory-mapped binary file containing precomputed T0 codepoint data: hashes, 4D coordinates, Hilbert indices, UCA orders, flags. ~67 MiB total; fits in CPU L2/L3 cache. Currently planned as a *separate* read-side optimization queried alongside PG (perf-cache lookup ↔ DB fallthrough on miss).

PostgreSQL has supported pluggable Access Methods since PG 9.6. An AM provides the storage + retrieval interface for a relation — `amhandler`, `ambeginscan`, `amgettuple`, `amgetbitmap`, `aminsert`, `ambuild`, etc. Stock AMs include `heap` (default), `btree`, `gist`, `spgist`, `gin`, `brin`. Custom AMs let you implement entirely new storage strategies.

The substrate has a specific shape that makes a custom AM compelling:

1. **Tier 0 is finite, fixed, and known at compile time.** 1,114,112 Unicode codepoints, ordered by UCA. Never changes per Unicode version. Could live in a read-only mmap.
2. **Lookup pattern is dominated by hash-keyed point queries.** "Does entity X exist? What's its canonical coord?" → direct mmap offset is faster than heap+B-tree traversal.
3. **PG's buffer cache adds overhead** for entity reads that go through a single hot indirection. If the perf-cache IS the storage, the buffer cache layer becomes redundant.
4. **Cascade-time access patterns** (walks at Tier-1+ that resolve Tier-0 constituents) hit this access shape repeatedly per query — millions of times.

A custom AM `laplace_perfcache_am` could wrap the perf-cache mmap such that the PG planner CHOOSES between perf-cache-AM and heap+index per query, based on the substrate's natural cold-vs-hot structure.

## Decision

**Schedule a research spike** to prototype `laplace_perfcache_am` after v0.1.0 ships. The spike is **time-boxed to 2 weeks**. Success criteria measured against the heap+index baseline as it exists at v0.1.0.

### Spike scope

1. Implement the minimum AM hooks required for read-only sequential + indexed scans against an mmap-backed table:
   - `amhandler` — registration
   - `ambeginscan` — scan setup
   - `amgettuple` — sequential and index scans
   - `amgetbitmap` — bitmap scan
   - `amendscan` — teardown
2. Map the perf-cache binary into the PG backend's address space.
3. Expose Tier-0 codepoint entities through this AM.
4. Benchmark cascade-walk queries against the heap+index baseline.

### Go / no-go criteria

| Metric | Threshold for go |
|---|---|
| Cascade-walk speedup (90th percentile) | ≥ 2× vs heap+B-tree on Tier-0 lookups |
| Cold-cache substrate query speedup | ≥ 3× (where mmap-direct beats buffer-cache miss + heap fetch) |
| Memory overhead | ≤ same as current perf-cache (no doubling) |
| PG version stability risk | AM API has been stable since PG 9.6; risk acceptable |
| Implementation complexity | ≤ 3000 LOC C in the AM module; if much more, the AM is doing too much |

### What's explicitly out of scope for the spike

- Writes (`aminsert`). T0 is read-only by definition.
- Inheritance support, parallel scans, ANALYZE estimates beyond a fixed table-stats stub.
- Tier 1+ entities. The spike targets Tier 0 only.
- A full AM-vs-heap migration plan. Spike results inform that plan, but the spike itself doesn't ship to production.

### If go: a follow-up ADR

A successful spike spawns a new ADR (`0032+`) committing to the AM as a v0.2.0+ feature, with its own implementation Epic and acceptance criteria. The spike's prototype code becomes the starting point for the production implementation.

### If no-go: ADR amendment

A failed spike (didn't hit the thresholds) gets documented here as an addendum: what we tried, what the numbers were, what we learned. The perf-cache remains a read-side optimization separate from PG storage.

## Consequences

- **No v0.1.0 commitment.** This ADR doesn't add scope to the milestone; it just reserves a thought for after.
- **Spike outcome is binary.** Either we get a 2-3× cascade speedup (huge), or we get marginal numbers (no point). Time-boxed to 2 weeks so we don't burn months chasing it.
- **AM API depth is real but bounded.** PG's AM interface has been stable since 9.6; documentation is good; precedents exist (`zheap`, `cstore_fdw`'s columnar AM, `pg_columnar`). Not a research-into-the-void scope.
- **The perf-cache concept persists either way.** If the spike succeeds, perf-cache becomes the AM's backing store. If it fails, perf-cache stays as the current read-side optimization. The artifact and its build pipeline don't change.

## Alternatives considered

- **Build the AM as part of v0.1.0.** Rejected — too much risk for the milestone scope. Custom AMs are deep work; cascade-time wins are speculative until measured.
- **Skip the spike entirely; perf-cache stays as a side-cache forever.** Rejected — leaves a potentially large win unexamined. Time-boxed spike with go/no-go is the cheap way to find out.
- **Implement a Foreign Data Wrapper (FDW) instead of an AM.** Considered. FDW is the wrong interface — substrates aren't foreign; they're local mmap. AM is the right fit.

## References

- [PostgreSQL — Defining Custom Access Methods](https://www.postgresql.org/docs/current/indexam.html)
- [PostgreSQL — Table Access Method API](https://www.postgresql.org/docs/current/tableam.html)
- [pg_columnar (Citus columnar AM)](https://github.com/citusdata/citus/tree/main/src/backend/columnar) — prior art for a custom table AM
- [zheap (Postgres Pro)](https://github.com/postgrespro/zheap) — prior art for a custom storage manager
- ADR 0006 (perf-cache + DB seed siblings) — the perf-cache concept
- ADR 0024 (engine modularization) — the perf-cache lives in `liblaplace_core`
- ADR 0029 (custom indexing strategy) — orthogonal axis; opclasses ride on stock storage, AM replaces storage
- RULES.md R8 (no GPU at runtime) — the AM is CPU-native; consistent with the rest of the substrate
- `engine/core/include/laplace/core/codepoint_table.h`, `engine/core/src/codepoint_table.c` — perf-cache implementation, which the AM would wrap
