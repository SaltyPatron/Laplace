# Ingestion Write-Path — Architecture & Decisions (ground truth)

> Authoritative reference for the ingestion write path. Verified June 2026 against the repo
> (`03_physicalities.sql.in`, `05_indexes.sql.in`, `hilbert4d.c`, `apply_batch.c`,
> `NpgsqlSubstrateWriter.cs`, `ContentBatch.cs`) and external authority (PostgreSQL/PostGIS docs +
> B-tree/GiST literature). Any sub-agent touching ingest perf reads this first and does not
> re-derive or re-guess. Every perf claim here is either code-cited or web-cited; nothing is from
> model training.

## 1. The thesis

The write path runs at ~47k rows/s (~5 MB/s) **not** because of an "index-maintenance floor," but
because it feeds the indexes **uniformly-random BLAKE3 ids, one row at a time, single-threaded**.
Random keys are the cliff. NVMe-class throughput (the 2M rows/s target, indexes intact) comes from
feeding the same indexes **sequential, Hilbert-ordered, bulk** writes across **parallel disjoint
Hilbert ranges**, with the rowset **reduced first** by the O(tier) containment descent.

"Indexes are expensive" is the random-order half of the truth stated as the whole. Sequential-order
index maintenance is cheap; random-order is the page-split cliff.

## 2. Verified facts

### Identity / dedup key
- physicalities dedup key is the **BLAKE3 `id` (PK) ONLY**. `03_physicalities.sql.in` explicitly
  forbids an `(entity_id,type)` unique: *"a physicality is keyed by its content-addressed id
  (BLAKE3 of entity_id|type|coord|trajectory) … Dedup is the hash."* A divergent geometry for the
  same content is a determinism **bug to catch in a test**, never a relational collapse.
  → Any `(entity_id,type)` anti-join in the apply path is a **phantom** against a non-existent
    constraint and must be removed.
- `id = BLAKE3(entity_id | type | coord | trajectory)` — uniformly random.

### Hilbert index (`engine/core/src/hilbert4d.c`)
- 128-bit (`bytea(16)`), 4 dims (PointZM) × 32 bits/dim. Coord quantized `[-1,1] → uint32`
  (2³² cells/dim), i.e. on the 4D unit-hypersphere surface.
- **Locality-preserving** (tests assert consecutive-index→adjacent-cell and NN-recall).
- **Collides by design.** Two distinct contents (distinct ids) inside the same ~2⁻³¹ cell get the
  same hilbert. Same hilbert = structural equivalence / anagram; the id distinguishes content
  inside the bucket. Already exploited: `23_structural_surface.sql.in` joins
  `p2.hilbert_index = p1.hilbert_index` to group neighbors.
- ⇒ **hilbert is the sequencing/bucket key; the blake3 id is identity.** Collisions are *allowed*
  precisely so hilbert never has to be unique — it only sequences access and shrinks the exact
  check to a small local bucket.

### The random-order cliff (external authority)
- UUID/random primary keys → "write performance falls off a cliff … random seeks for every insert;
  the B-tree cannot handle massive random writes." Page splits + fragmentation + **3–5× write
  amplification**. ([use-the-index-luke](https://use-the-index-luke.com/sql/dml/insert),
  [PlanetScale](https://planetscale.com/blog/btrees-and-database-indexes))
- Sequential keys → right-most path, no splits, revisits the same pages, fewer I/Os.
  ([Tiger Data](https://www.tigerdata.com/blog/13-tips-to-improve-postgresql-insert-performance))
- **PostGIS's native pattern** is Hilbert-sort then bulk load; GiST bulk-packs (Hilbert/STR) far
  faster than one-by-one. ([Crunchy](https://www.crunchydata.com/blog/the-many-spatial-indexes-of-postgis),
  [Alibaba](https://www.alibabacloud.com/blog/postgresql-best-practices-selection-and-optimization-of-postgis-spatial-indexes-gist-brin-and-r-tree_597034))
- PG16 **parallel COPY ≈ +300%**. ([Rapydo](https://www.rapydo.io/blog/postgresqls-surging-popularity-andinnovation))

### Indexes (physicalities, `05_indexes.sql.in` — ~11, all load-bearing)
id PK, `(entity_id)` btree, `(type)` btree, **coord GiST (`gist_geometry_ops_nd`)**,
**`hilbert_index` btree**, `radius_origin` btree, `alignment_residual` btree, `observed_at` BRIN,
`traj_probe` btree, **constituents GIN**. Not physically `CLUSTER`ed — only `apply_batch`'s
`ORDER BY hilbert_index` at insert, which holds for a fresh load and drifts incremental.
**Indexes are needed for the dedup probe and every read — do NOT drop them.**

## 3. The architecture — dedup ONCE (descent), then a dumb sorted append

Dedup and insert are **separate steps**. The descent is the *only* existence check; the insert
looks up **nothing**. This is the enterprise pattern (dedup in staging → bulk append/partition
attach), and it is why the insert never pays the random-id cost.

1. **Skip T0/T1 entirely — no DB check, ever.** Codepoints (T0) and graphemes (T1) are
   deterministic, lossless, and perfcache-resident: same content ⇒ same hash ⇒ present by
   construction. Most nodes in any content tree are T0/T1, so this drops the bulk of the candidates
   before a single round-trip.
2. **Descent = the ONLY existence check.** Native O(tier) `merkle_dedup_trunk_shortcircuit` /
   `content_descent_bitmap` probes **T2+ trunks** top-down; a present trunk ⟹ its whole subtree is
   present ⟹ skipped. Output: a **known-novel set** + a **re-observed (fold) set**. Dedup happens
   here — once, on a handful of trunks, never per row, never at insert time.
3. **Stage the known-novel set** in an unlogged staging table (no WAL), **ordered by
   `hilbert_index`**. Transform/dedup is finished; the snapshot is clean.
4. **Load the target as a pure bulk append — no lookup, no anti-join, no `ON CONFLICT`** (the set is
   already novel; checking again is the mistake). Sorted by hilbert ⇒ index maintenance is a
   sequential append (B-tree right-most, GiST/GIN nearby pages) ⇒ no page-split cliff, indexes kept.
   Partition the target by **contiguous hilbert range**; bulk-append to the owning range, or build
   the range as a partition and **`ATTACH`** it — the attach validates *structure*, not rows, so it
   does **zero per-row validation**.
5. **Fold the re-observed set** via Glicko-2 consensus (native C, `glicko2_*`) — the one inherent
   DB-side merge.
6. **Concurrency is free:** disjoint hilbert ranges ⇒ writers never share a key ⇒ no per-row
   arbiter, no speculative-insert stalls.

The existence lookups are the descent's job, done once on the reduced T2+ trunk set. The insert does
index **maintenance only**, in sorted order. That is the whole reason indexed bulk hits NVMe speed —
not dropping indexes, not "the floor."

### Enterprise bulk-load pattern this maps to (verified)
- Dedup/transform in **unlogged staging**, set-based, before the target — the production table never
  does per-row dedup. (no-WAL staging is 10–100× the load step.)
- **`ATTACH`/`EXCHANGE`/`SWITCH` partition**: build a sorted, indexed partition offline, attach it
  atomically; the switch validates *structure*, not individual rows → zero RBAR validation.
- Sorted/clustered load order for sequential index build; `COPY` for the staging fill.

## 4. What must change (code)

| Change | Where | Why |
|---|---|---|
| Remove phantom `(entity_id,type)` anti-join | `apply_batch.c` physicalities insert | no such constraint exists; contradicts schema |
| Hilbert-range partitioning (replace `id.lo % N`) | `NpgsqlSubstrateWriter` partition + `intent_stage_partition` | random-hash scatters every index write; range = sequential + disjoint |
| Bulk Hilbert merge dedup (replace per-row anti-join) | `apply_batch.c` | per-row anti-join over millions is the RBAR wall |
| Bulk insert ordered by hilbert (ensure contiguous ranges) | `apply_batch.c` / writer | sequential append, no page splits |
| Descent feeds reduced rowset (done) | `ContentBatch` / `content_descent_bitmap` | fewer rows reach the indexed insert |
| Shared-machine-aware worker counts + headroom; revert greedy 7 + whole-process P-core pin | `StructuredGrammarIngest.ResolveComposeWorkers`, `IngestCommands` affinity | machine is in use (user + 2 GPUs + hypervisor + WSL + other pools); greedy grab starves it → freezes |

## 5. Anti-patterns — never again

- Per-row anti-join / RBAR dedup over millions of rows.
- Partitioning / probing by random hash (the blake3 id) — scatters every index write.
- Treating "indexes are expensive" as a fixed floor (it is the **random-order** half-truth).
- `func()` calls on anything but the final projection — SQL is an orchestration layer.
- Phantom constraints — checking a unique/key that does not exist in the schema.
- **Fabricated / non-reproducible "measurements."** Every perf claim ships with a committed,
  re-runnable script + exact numbers + hardware. (The prior "it's the floor / hilbert-sort is a
  non-lever / anti-join is fine" memory had no repro steps, tested the same floor two ways to look
  better, and was wrong — deleted.)
- Greedy core-grabbing on a shared, in-use machine.
- One-thing-at-a-time reasoning (e.g. "drop the GiST") that ignores the indexes are needed for the
  dedup probe and every read. The levers compound; reason about the whole.

## 6. Concurrency

Disjoint Hilbert-range partitions give non-overlapping key spaces → concurrent writers never touch
the same key → no per-row arbiter, no speculative-insert stalls. (Historical incremental-mode entity
key-sharing races were band-aided with `ON CONFLICT(id) DO NOTHING` / `FOR UPDATE SKIP LOCKED`;
Hilbert-range partitioning removes the race at the source.)

## 7. Measurement discipline

No perf number enters this doc or memory without a committed, re-runnable script, the exact output,
and the hardware it ran on. Hardware of record: i9-14900KS (8 P-cores / 16 E-cores), 48 GB RAM,
2× Samsung 990 EVO NVMe RAID-0, PostgreSQL 18 native Windows.
