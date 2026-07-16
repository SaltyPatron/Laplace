# 28 — Model-Lane Performance Ledger

Standing law: every model-lane stage gets a measured baseline BEFORE optimization
(CLAUDE.md: profile before optimizing — VTune is installed), and every gate run
records wall-clock. Perf gates are Tier 1 invariants like exactness: a regression
is a failure, not a footnote. Machine context for all numbers unless stated:
hart-desktop, i9-14900KS (8 P-cores pinned, 16 E), 48 GiB, PG18 local, NVMe.
(Caveat: 14900KS flaky-AV history — long runs are dice-rolls until RMA resolved;
timing outliers on crashed runs don't count.)

## Baselines (measured)

| Stage | Model | Wall | Detail | Date |
|---|---|---|---|---|
| **Full factor witness** (`cli.cmd ingest model`, planes=factors, ALL planes: emb+q/k+ov+mlp, layout v2 self-describing) | MiniLM-L6 (90 MB) | **123.7 s** | 223 trajectories × 27,852 tokens (~1e9 values, 5.2 GB); compute+pack 13.4 s (2.2 s/layer); remainder = COPY. Readback gate: qk 16k values bit-exact + 500 header ids exact + pairs 1.66e-8 CS; emb/mlp/ov bit-exact. | 2026-07-15 |
| (superseded first cut: q/k only, anonymous layout) | MiniLM-L6 | 19.2 s | 144 trajectories, evicted for layout v2 | 2026-07-15 |
| Readback gate (`ModelGateFactorReadbackTests`) | MiniLM-L6 | **1.7 s** | DB fetch 2 slices + 16k bit-exact asserts + 2k pair scores + full kernel-direct reference recompute | 2026-07-15 |
| Lens measurement (`FactorLens4dTests`) | MiniLM embedding | ~5 s in suite | SVD 2000×384→32 + 5 k-ladders × 200-probe tiles, native | 2026-07-15 |
| Recorder (structure phases: vocab+merges+maps, prior session) | MiniLM-L6 | seconds-class | 30,522 tokens parsed in 113–131 ms; maps/merges dominated by COPY | 2026-07-15 |
| Legacy `planes=all` V² path (DELETED architecture) | TinyLlama | hours-class, 83% CPU in per-row managed sort (VTune, ledger) | the anti-baseline the factor design killed | 2026-07-09 |

Reference seed-lane throughputs from today's reseed (the spine the factors ride):
COPY 121–294k rows/s per table across 8 id-range connections; client Glicko fold
3.2–13.2k rel/s (mask refresh dominates: 60–90% of fold wall); OMW 25.5M rows in
1,862 s. Factor deposits are few-large-rows — COPY cost is negligible next to
compute; the 19.2 s MiniLM run is compute+pack dominated (no per-phase split yet).

## Standing targets (campaign doc 26)

| Gate | Target | Ceiling | Status |
|---|---|---|---|
| MiniLM full factors (q/k + OV + MLP + CONTINUES + arena) | seconds-class | 60 s | q/k-only at 19.2 s — headroom exists but OV/MLP double the GEMM volume; re-measure at full deposit |
| TinyLlama full factors (1.1B, RoPE family) | ≤ 60 s | 120 s | unmeasured; O(V·d²) projection GEMMs ≈ 30 TFLOP ⇒ 15–30 s pure MKL compute on 8P — target is credible, pack/COPY overlap must not serialize |
| Readback gate per model | ≤ 5 s | 30 s | 1.7 s |
| Tier-2 behavioral gates (KL/top-k vs llama.cpp) | per-model minutes-class | TBD at first implementation | not built |

## Owed measurements (in order)

1. **Per-phase split of the factors ingest** — load / LN+project (GEMM) / head
   slice / FactorWalk.Pack / COPY, logged per layer at INFO. The 19.2 s is one
   opaque number; the TinyLlama projection needs the split to be steerable.
   Cheap: stopwatch fields in EmitFactorTrajectories, log at phase summary.
2. **Pack throughput microbench** — FactorWalk.Pack is per-token managed loop
   over 27,852 tokens × 144 slices ≈ 4M small packs; if the split shows it hot,
   the fix is one native `laplace_factor_pack_rows` call per slice (batch pack),
   NOT managed micro-tuning.
3. **TinyLlama first timed run** once RoPE-family factors deposit (item A
   completion) — THE perf gate of record.
4. **VTune pass** only after (1) shows where the wall is (law: profile first).
5. **Readback at scale** — model_pair_score SPI latency distribution once item B
   exists (target: sub-ms per pair warm, perfcache-class).

## Incident + fix (2026-07-16): the machine-killer query

Cause stack: C SRFs without ROWS estimates (planner assumed 1000 rows, chose
hash-side plans over 18M-row partitioned consensus) x work_mem 190MB x
hash_mem_multiplier 2.0 x 4 workers x partition-wise nodes = RAM detonation,
cold power boot. Second crash from an unguarded post-incident EXPLAIN.
Fixes, all live: ROWS 20/1 on model SRFs (extension 7b2ef17f71b0ea03);
ALTER SYSTEM work_mem 190MB->64MB, hash_mem_multiplier 2.0->1.0 (OVERRIDES
tune-laplace values — revert with ALTER SYSTEM RESET if fold/seed perf
regresses); guard prolog law (timeout 30s / work_mem 32MB / no parallel
gather) + MATERIALIZED fences for all exploration sessions.
PROOF: the exact crash query now returns in **217.6 ms** under full guards.

FORENSICS (PG logs + Windows event log, 2026-07-16):
- CRASH 1 = CONFIRMED query-induced starvation: "autovacuum worker took too
  long to start; canceled" EVERY MINUTE 20:03->20:13 (postmaster too starved
  to fork for ten minutes) — window matches explore_math2 section E exactly.
  PG never OOM'd; the OS swapped to death around it (why no
  Resource-Exhaustion 2004 event exists).
- CRASH 2 (~20:41) = UNATTRIBUTED: PG log quiet (connection prewarms only),
  the EXPLAIN had already returned, no query in flight; Defender intelligence
  update landed 20:26 mid-window; box has documented instability history.
  Guards close the query-induced class regardless.
- TinyLlama never ran; all ingests were MiniLM, all completed cleanly by
  19:44 — ingest exonerated for both crashes.
- DISCOVERY: "perfcache prewarmed in postmaster" logs ONCE PER CONNECTION on
  Windows — EXEC_BACKEND means every backend re-processes
  shared_preload_libraries, so the fork-inheritance assumption in perfcache.c
  does NOT hold here: every new connection re-maps AND re-BLAKE3-CRCs the
  85MB t0 blob. Per-connection tax today; the FACTOR blob must not repeat
  this (skip CRC on re-map, or validate once per boot via a stamp).

## Storage scaling law (operator-prompted, 2026-07-15)

The full factor witness IS "the scale of the model at token grain":
V × d × circuit-units ≈ 1e9 values for MiniLM (5.5 GB through the mantissa
channel) vs the checkpoint's 22.7M-weight compressed generator. Materialized
once (one-time scrape, substrate self-sufficient) vs recomputed per query
(the model's way). Still 477× under the dead V² design.

CLIFF: OV at full d-grain is rank-hd content stored at rank-d cost — 79% of
MiniLM's payload, and ~18 TB for TinyLlama (non-viable). MANDATORY before the
TinyLlama gate: rank-aware OV storage — deposit v_h(t) factors (n×hd) + the
head's O-basis (d×hd, once, itself a factor trajectory). Exact, (n+d)×hd vs
n×d ≈ 30× smaller; TinyLlama lands ~40 GB. Same lesson as the pair-tile kill:
store the factorization, never the evaluated product, at every grain it recurs.

Rule for future entries: every new baseline lands in THIS file with date,
machine, and the exact command; targets move only with operator sign-off.
