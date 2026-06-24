# Empirical ingestion learning log

Fresh DB (`db-reset`). Measuring the real pipeline source-by-source instead of assuming.
**Scope:** unicode → iso639 → cili → wordnet.
**Discipline:** show/EXPLAIN every SQL before running; checks before AND after each source; timing on.
**Hardware:** i9-14900KS (8P/16E), 48 GB, 2× 990 EVO RAID-0, PG18 native Windows.

---

## Step 0 — before baseline (fresh DB)

**SQL:** `docs/bench/checks.sql` — row counts, physicalities indexes, constraints, table/index sizes.

**Result:**
- `entities=41` (extension-bootstrap types), `physicalities=0`, `attestations=1`.
- physicalities **10 indexes**: constituents_gin, coord_gist, entity_btree, hilbert_btree,
  observed_brin, pkey, radius_btree, residual_btree, traj_probe, type_btree.
- **13 constraints** incl `physicalities_entity_id_fkey` (FK → entities = the per-row RI trigger),
  the id/hilbert octet_length CHECKs, n_constituents CHECK.
- sizes all near-empty (index overhead only).

**Thoughts:** every physicality seeded from here forward pays index maintenance; whether the FK
trigger fires depends on the load path (see Step 1 — the fold disables triggers via
`session_replication_role=replica`; the entity/phys load path is still TBD).

---

## Step 1 — unicode (floor / T0 codepoints)

**Operation (verified LIVE via pg_stat_activity, not assumed):** bulk-fresh (`LAPLACE_BULK_FRESH=1`).
**It does NOT use `apply_batch`.** Two DB phases:
1. **Entity/phys/attestation load** — COPY native tuples → merge. 1,173,965 ent + 1,173,964 phys +
   1,631,769 att in **32.3 s = ~123k rows/s**, 455 round-trips. (Exact merge SQL not captured —
   finished before I sampled; sample earlier next source.)
2. **Consensus fold** — `materialize_period_partition_fresh` (`14_period_fold.sql.in:175`):
   **7 parallel backends**, each takes an UNLOGGED `consensus_period_staging_eNNNN_k`, does
   `GROUP BY (subject,type,object)` → `glicko2_accumulate_games` → `INSERT INTO consensus … ORDER BY
   cid ON CONFLICT (id) DO NOTHING`, with **`session_replication_role=replica` (triggers/FK OFF)** and
   `work_mem=2GB`. ~40 s for 1.63M consensus relations. Waits: **LWLock:BufferContent + LWLock:WALWrite
   + IO:WalWrite** (buffer/WAL contention across the 7 backends).

**Totals:** 72.8 s wall (≈32 s load + ≈40 s fold). After: entities 1.18M, phys 1.17M
(487 MB, **286 MB = 59% indexes**), attestations 1.63M (515 MB), consensus 1.63M.

**Learnings (these overturn my earlier assumptions):**
- The seed does **not** touch the `apply_batch` anti-join I kept editing. Wrong target the whole time.
- The **consensus fold already IS the enterprise pattern**: unlogged staging, set-based `GROUP BY`,
  sorted insert, **triggers OFF**, partition-parallel, `ON CONFLICT`. Well-built.
- The fold is **~55% of wall-clock**; its ceiling is **buffer/WAL LWLock contention at 7 backends**,
  not seeks. More backends won't linearly help (contention).
- Indexes ≈ 59% of physicalities size; GIN + GiST dominate.
- **OPEN:** capture the entity/phys LOAD SQL (123k/s) by sampling during the ingest phase — does it
  also disable the FK trigger, or pay it per row?
