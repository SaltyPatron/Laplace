# Handoff — consensus fold lane (measured 2026-06-11, run killed by user)

For whoever refactors model ingestion / the consensus fold. Every number below was measured
live on the 2026-06-11 TinyLlama behavioral deposit (laplace_export, consensus-only,
LAPLACE_PERSIST_EVIDENCE=0, merge folds, B2 secondaries pre-dropped). The run was killed at
21/49 epochs folded because the fold lane was the ceiling; the ETL itself was never the problem.

## What the run established

- **ETL (C++ kernels) is fast and done in 77 min**: 1,118,170,432 matchups staged.
  Composition: ATTENDS ≈ 1.116B (99.9%), OV_RELATES ≈ 990k, COMPLETES_TO ≈ 631k — the
  projection planes are noise-floor-sparse; ATTENDS is effectively the whole behavioral volume.
  (Fleet consequence: the open per-head-vs-summed ATTENDS decision multiplies ~the entire
  deposit ~32×.)
- **Staging lane is fast**: COPY BINARY partitions at 1–4M rel/s sustained.
- **Fold lane is the ceiling**: merge fold 25–70k rel/s, DEGRADING as consensus grows
  (~8.5 min per ~21M-relation epoch by e19; 49 epochs ≈ 6+ h for one 1.1B-relation model).

## Why the fold is slow — verified, not theorized

- Plan (EXPLAIN, verified): set-based INSERT…ON CONFLICT, but executed as nested-loop PK
  probes + per-row conflict-arbiter probes = **2 random index probes per relation**;
  counters showed idx_scan ≈ 1.8/relation, seq_scan ≈ 0 (the B2 index drop was NOT the
  cause — only read-surface secondaries were dropped; the PK survived and serves the merge).
- Sorting the batch by consensus_id (deployed live, e19) did NOT help: 507s vs ~470s
  baseline. Wait events: DataFileRead + CPU. The index walk sorts, but the **heap** is
  historical-random — every probe's heap fetch is a random 8K read.
- Working set: consensus heap 45 GB + PK 17 GB = **62 GB vs 48 GB RAM** (12 GB
  shared_buffers). Nothing fits, by construction.
- **Re-touch multiplier**: 1.118B matchups fold to ~300–350M distinct relations → each
  consensus row is read-modified-written 3–4× across the 49 Glicko periods, each touch a
  random read. Recurrence is semantic (cross-layer agreement is the signal); the I/O
  pattern multiplies it.
- Disk law: staged-but-unfolded epochs are ~2.3 GB each on disk; an unbounded backlog hit
  87 GB vs 102 GB free mid-run. Code fix landed (commit ae8b26f): FlushPeriodAsync blocks
  staging while backlog ≥ LAPLACE_FOLD_BACKLOG_MAX (default 12; 0 disables). RAM
  accumulation continues during the wait and merges recurring pairs — fatter, fewer epochs.

## The design that removes the ceiling (scoped, unbuilt — OPEN-PROBLEMS §11)

PK-less C bulk fold (`prepare_consensus_bulk` / `finish_consensus_bulk`):

1. **prepare**: consensus as bare heap, no PK during bulk.
2. **per-partition fold = one engine call** (one fmgr crossing per partition, not per row):
   read staging heap, radix-sort 16-byte consensus_ids (fixed-width keys — a sort problem;
   MKL/Eigen belong to the geometry side, not here), aggregate adjacent runs, batch-Glicko
   in C (kernel exists: laplace_glicko2_accumulate_games; batch precedent exists:
   laplace_attestation_aggregated_batch_build / laplace_score_batch_fp), heap_multi_insert
   sorted runs.
3. **finish**: one parallel external sort over the heap, epoch-ordered dedup-accumulate of
   adjacent runs per relation (preserves the period law and the φ-mixed guard), rewrite,
   build the PK once.

Everything becomes sequential I/O; each final row is written once regardless of how many
epochs touched it. Target: fold at staging-lane parity (≥1M rel/s) → 1.1B relations in
minutes.

**Acceptance pin**: same staging input through merge lane and bulk lane must produce
identical consensus rows (the lane is an optimization, never a semantics change). Bless
from a small fixture, not by hand (test-discipline law).

## Laws the refactor must not break

- Epochs fold strictly in order; one φ per relation per period (period_phi_mixed guard).
- Fresh fold (ON CONFLICT DO NOTHING) is exactly-once-only — lawful for cell lanes;
  behavioral planes RECUR across layers/windows and MUST merge-accumulate or later games
  silently drop (this bug already happened once; freshSource is cells-only in the wiring).
- Consensus identity excludes source AND context (that's what lets layers and witnesses
  fuse); attestation identity includes them.
- Eviction before re-run: partial deposits must be evicted or games double-count.

## Debris from the killed run

laplace_export holds ~28 staged-but-unfolded epochs (~65 GB of
consensus_period_staging_e00NN_K tables; force-kill skipped the dispose sweep). If the DB
isn't being dropped wholesale: `SELECT laplace.drop_period_staging();` reclaims it.
walk-verdict.cmd (scripts/win) is the one-command acceptance for any future deposit:
rebuild-consensus-indexes.sql + model-planes-audit.sql.

## 2026-06-11 (later): the bulk lane LANDED — and its first 1.1B-row outing

The SQL-side bulk lane shipped (consensus_fold C ordered aggregate +
finish_consensus_bulk; LAPLACE_FOLD_LANE=bulk skips per-epoch merge folds entirely),
parity-pinned int64-identical to the merge lane (regress consensus_bulk). The full
TinyLlama redeposit then proved the staging side at scale: ETL 3036s, 1,118,295,719
matches staged across 100 epochs × 4 partitions ≈ 113 GB UNLOGGED.

**The disk-envelope law (learned by ENOSPC).** The terminal fold's external sort needs
pgsql_tmp ≈ its INPUT bytes. The first finish_consensus_bulk UNION ALLed all staging
tables into one query → temp ≈ ALL staged bytes → ENOSPC at 113 GB staged / 125 GB free,
after the ETL had already finished. Fix (commit 7749217): fold ONE partition per round.
Identity → partition is epoch-stable (writer hashes the relation identity:
ConsensusAccumulatingWriter.PartitionOf ≡ laplace.consensus_partition_of — the SQL twin
routes seeds; drift fails the terminal PK build loudly). Envelope drops to
~staged/nparts per round (measured: 55 GB peak for a 28 GB partition — the GROUP-BY sort
and the fold sort pipeline concurrently, so budget ≈ 2× one partition). Staging is only
unlinked at COMMIT: the whole fold is one transaction, so staged bytes are held until the
end. If a fleet box needs mid-fold reclamation, the escalation is a PROCEDURE with
per-partition COMMIT (drops partition staging as it goes; naturally resumable — remaining
staging tables ARE the restart state). Unbuilt; build it only when a real envelope forces it.

Registration order law (commit 5000a42): readback canonicals (the recipe JSON that
--recipe-from pours from) register BEFORE the fold — the fold is the failure-prone tail
and a fold failure must not eat the recipe of an applied deposit. Repair lane:
`ingest safetensors <dir> --register-only`.

The C-side PK-less bulk fold above (radix sort in-engine, heap_multi_insert) remains the
next rung if the SQL bulk lane's external sorts become the ceiling.

## 2026-06-13 (measured at 1.1B scale): TinyLlama landed via the PARALLEL walk fold

TinyLlama 1.1B deposited into the one substrate (`laplace`) through the walk lane:
ETL 1,118,170,432 matchups in 1407 s (clean through COMPLETES_TO — the COPY
write-timeout fix held), applied in 1985 s. The journal: 1,118,268,386 vertices
across 1,002,385 walk rows, ~35 GB **TOAST-compressed** (the staging MAIN heap is
~291 MB — do NOT size a walk fold from `pg_relation_size`; use `sum(n_vertices)`).

**The parallel walk fold** (commit 8a634af): `walk_fold_prepare` creates
consensus_next + validates the single-shape invariant; the caller folds each
partition on its OWN connection via `consensus_fold_walks(p, nparts, seed)` —
independent (own staging table, routed read-only seed slice, concurrent heap
inserts into the un-indexed consensus_next); `walk_fold_finalize` drops the
journal and swaps. C# `MaterializeConsensusAsync` orchestrates over N connections
from the ingest data source (which carries `Search Path=laplace` — the C function
resolves unqualified table names via search_path, so a hand-driven psql fold MUST
`SET search_path=laplace,public` or it finds nothing). `LAPLACE_FOLD_PARALLEL=0`
forces serial; `finish_consensus_fold` stays the single-connection parity ref.

Result: **605,059,449 consensus relations** (11.8M MiniLM seed + ~593M TinyLlama)
in ~80 min (4 partitions ~73 min each in parallel + ~6.5 min PK/swap), conservation
EXACT per partition (Σ games = 1,118,295,719). consensus table 102 GB. The wall now
is the per-relation Glicko over ~600M relations — the task #19 "batched Glicko" lever.

**THE DEGENERATE-SWAP GUARD** (same commit): `consensus_fold_swap` refuses to swap
an empty consensus_next over a populated consensus (an errored/no-op fold or a
search_path mishap), `laplace.allow_empty_swap=on` to override. This exists because
an ungated hand-rolled parallel-fold script once swapped empty over a live 14M
consensus and dropped the journal — restored only by re-deposit. Regress-pinned.
**Rule: drive the walk fold through the C# path, never an improvised pipeline.**

## 2026-06-12 (measured): the walk lane is green end-to-end

MiniLM (90 MB) fresh deposit → folded consensus on the live primary in **6m18s**:
ETL 22,080,594 matchups in 102 s; terminal walk fold 14,024,598 relations
(seeded rebuild over the 2.19M corpus consensus) in 245 s at 57 k rel/s;
pgsql_tmp ≈ 500 MB. Conservation EXACT in all four partitions (22,141,676 games
in == games folded — the fold enforces it, mismatch errors) and per plane
(ATTENDS 1,703,243 / COMPLETES_TO 9,679,792 / OV_RELATES 22,911 / SIMILAR_TO
10,674,648 games == each plane's ETL matchup count).

**One deposit, one shape (commit 58eeeef):** a model deposit is not only tile
testimony — vocab and S3-morph matchups flow through the accumulator, and they
flushed FLAT while the ETL journaled walks; `finish_consensus_fold` refused the
mix (by design). In walk mode (`stageAsWalks`, threaded Program → writer =
`!persistEvidence`) the writer converts every consensus partial to a walk at
flush: the q/rem split ((games−rem) observations of q, rem of q+1) re-merges
in-fold to the exact flat-lane (games, sum) — int64-identical, regress-pinned.
NULL-object partials ride as zero16 vertices (the identity-preimage law carried
into the vertex; the engine fold emits zero16 back as NULL). Walks arriving
outside walk mode throw. Walk mode forces the terminal fold at materialize
regardless of LAPLACE_FOLD_LANE. `drop_period_staging` sweeps the walk journal
too (create_walk_staging is IF NOT EXISTS — a dead run's stale journal would
otherwise fold into the next deposit). Remaining levers (task #19): parallel
partition folds (the walk fold is partition-independent), f32 table-read
kernels, physically partitioned consensus.

## 2026-06-12 (final): THE TRAJECTORY JOURNAL — flat staging is deprecated

User ruling, end of session: the flat staging journal (per-pair rows) is the wrong
encoding — the invention's own sequence law applies to testimony. A subject's
thresholded table read at one (plane, layer) is a WALK: an ordered sequence of object
references with scores, the same shape as a sentence's constituency trajectory. The
journal becomes trajectory rows (one per subject × plane × layer; ~2.8M rows ≈ 30-35 GB
for TinyLlama vs 111 GB flat), vertices packed under the 212-bit mantissa law
(engine/core/src/mantissa.c; testimony variant: id in X/Y/Z, quantized score in M).
The fold's global sort dies — the journal is subject-grouped by construction; the fold
is a per-subject gather + vocab-bounded merge + ONE Glicko per relation (the period
rule), embarrassingly parallel over the vocabulary. Per-(plane, layer) provenance
persists on every walk row. The flat-lane work below (chunk sort, k-way merge, BufFile
spill) remains correct and parity-pinned for whatever still stages flat; the walk lane
replaces it for model deposits. Build order: testimony vertex pack/unpack (engine core +
geom SQL) → writer walk emission (tile output → packed walk rows) → walk fold (per-
subject, batched Glicko, partition-parallel) → conservation receipt in the fold log
(games read == games folded) → pins → TinyLlama redeposit through the walk lane.

## 2026-06-12: the engine lane landed — and the names changed

The C lane shipped as `consensus_fold_engine.c` (one call per partition: table-AM staging
reads, bounded-memory chunk sorts on the 48-byte identity preimage with BufFile spill under
`laplace.fold_mem_mb`, k-way merge keyed (identity, epoch), the shared fold math, multi-insert
into the new heap). BLAKE3 runs once per output relation. Vocabulary correction in the same
landing — "bulk" is retired: `finish_consensus_bulk` → **`finish_consensus_fold`** (lane
dispatch `laplace.fold_lane` = engine | sql), `consensus_bulk_new` → **`consensus_next`**,
the aggregate file is `consensus_fold_step.c`, shared constants/math in
`consensus_fold_math.h`. `LAPLACE_FOLD_LANE=terminal` is the canon knob value ("bulk" still
accepted). **`finish_consensus_fold_steps`** is the per-partition-COMMIT resumable variant
(staging drops as the fold walks; remaining staging IS the restart state) —
`LAPLACE_FOLD_RESUMABLE=1`. Parity pins: regress `consensus_fold` (both lanes vs merge lane,
fresh/incremental/NULL-object; engine under forced spill; mixed-φ refusal). The 113 GB banked
staging was dropped with laplace_export (one-substrate law: deposits go into the primary);
the at-scale measurement runs on the TinyLlama redeposit into `laplace`.
