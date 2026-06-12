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
