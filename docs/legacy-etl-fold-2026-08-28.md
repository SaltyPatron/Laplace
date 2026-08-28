# Legacy ETL investigation, 2026-08-28

Status: native batch-math repair under verification, **not deployed**. No index
was dropped/disabled, no durability setting changed on production, no ingest
restarted, and no refactor checkout modified. This does not resolve all ETL
performance, write amplification, or post-ingest maintenance issues.

## Live evidence

Host hart-server: Intel i7-6850K, six physical/twelve logical CPUs, approximately
126 GiB RAM, PostgreSQL 18.3. Legacy origin is SaltyPatron/Laplace. Work starts
from main a9667a7e in the separate legacy ETL worktree.

The user's comparison is supported by the journal:

| Run | Input units | Ingest wall seconds | Terminal consensus drain seconds |
| --- | ---: | ---: | ---: |
| WordNet, 45e3bf02-43ca-4349-b647-974b7900faa5 | 333,972 | 1,133.718 | 1,054.113 |
| ChessPgn, 4d70db67-2aa8-4ed2-b45c-9c4f7c774059 | 68,545 | 424.581 | 32.315 |

Chess input `/vault/Data/Games/Chess/Hikaru_chesscom.pgn` is 204,041,082 bytes.
The journal reports 369,249 entities, 224,998 physicalities and 1,678,708
attestations for that run. These are journal receipts, not independent counts
of source quality. The WordNet run predates the earlier fold-plan correction;
it is not a before/after replay of the new change.

`laplace.pg_stat_statements` is installed and available. During a 30.08-second
read-only sample (16 activity snapshots, two seconds apart):

- 30 completed consensus calls: 206,099.8 cumulative backend milliseconds.
- Nested MERGE: 1,006,192 rows, 187,124.7 backend milliseconds, 2,450,500,399 WAL
  bytes, 194,171 shared blocks read and 375,690 dirtied.
- Top-level fold WAL: 2,452,204,126 bytes. Do **not** add nested and outer
  statistics: they account for the same work.
- 12 highway-mask deposits: 27,469.2 backend milliseconds, 307,103,389 WAL bytes.
- Cluster WAL delta: 2,748,545,052 bytes; 181,535 WAL-buffer-full events.
- Consensus activity samples: 29 waiting on WALWrite, 27 executing without a
  reported wait, nine DataFileRead, four WalSync, one WalWrite and one
  DataFileExtend. These are backend-sample counts, not percentages of wall time.

The activity rows marked idle/ClientRead show completed statements, not ongoing
execution. `entities_exist_bitmap` calls native C, which performs a batch SPI
lookup. `consensus.upsert_type` also calls C, but persists through MERGE. Neither
fact alone establishes efficient bulk execution.

After the ingest journal marked complete, the CLI continued serial post-ingest
ANALYZE and GIN maintenance. Recorded ANALYZE times for the four column-scoped
commands: attestations 39,955.7 ms, physicalities 31,632.4 ms, entities 8,846.6 ms,
consensus 20,962.3 ms. That time is outside the 424.581-second receipt. No claim
is made that the journal is a complete CLI elapsed-time measurement.

A read-only WordNet evidence grouping, with a 15-second statement timeout,
found 1,058,325 rows sharing one opponent/phi/count/score tuple, then 455,426,
413,955 and 343,569 rows sharing three others. Evidence grouping does not
reconstruct original rating-period boundaries or prove identical stored priors.

## Selected repair

`fold_run_states` previously ran the identical pure Glicko transition once for
every cell, including fresh cells with identical evidence inputs. The native
batch now computes each distinct transition once. Its key includes **all seven**
fixed-point inputs: prior rating, RD, volatility, opponent rating, opponent RD,
game count and score sum. Tau and time are the existing fixed arguments.

The cache lives for one type batch only. Every row still receives its exact
result and witness count. No rating periods are combined; no cross-call cache,
SQL change, index change, new staging, altered lock ordering or retry semantics
is introduced. The existing canonical core math remains the only calculator.

A native COPY prototype was measured separately and excluded from this repair.
It saved only about 10–14% on the fixture by itself, did not materially reduce
WAL, and would expand the concurrency/permission/trigger verification surface.

## Verification and boundaries

`scripts/test-consensus-fold-transitions.py` compiles the actual C implementation
into private diagnostic modules, clones a scratch template database, and runs
the real SQL regression and pg_regress expected output. It refuses a server
whose data directory does not match an explicit /tmp path or has TCP enabled.

Fresh and existing-cell tests vary each mathematical input independently and
repeat each case. Exact scalar parity, counts, NULL objects and the existing
mixed-type/dedup/partition tests pass. Seven separately compiled mutations,
each omitting one key field, all fail with the intended batch/scalar mismatch;
the restored implementation passes again. Existing production code is never
replaced by these modules.

```sh
python3 scripts/test-consensus-fold-transitions.py \
  --socket /tmp/laplace-election-pg.suDcqI \
  --data /tmp/laplace-election-pg.suDcqI/data \
  --user election_test --template election_test --mutations

python3 scripts/bench-consensus-persistence.py \
  --socket /tmp/laplace-election-pg.suDcqI \
  --data /tmp/laplace-election-pg.suDcqI/data \
  --user election_test --template election_test --rows 30000 --samples 3 \
  --candidate-module /tmp/laplace-etl-memo-only.so
# Repeat with --distinct-inputs for the no-reuse control.
```

The benchmark's candidate module was compiled from fold_route.c with a
PG_MODULE_MAGIC wrapper, GCC, -O3 -march=haswell -fno-fast-math -ffp-contract=off,
PG18 server headers and the unchanged installed liblaplace_core. The mutation
harness generates the equivalent wrapper directly from that source contract.

Scratch PG: 64 MB shared buffers, fsync=on, synchronous_commit=on, all nine
consensus indexes present. Each timed operation starts with an empty table and
ends after statement completion/commit. Fixture generation and truncation are
outside the boundary. Every sample verifies all 30,000 stored rows and a digest
of all persisted fields against the original implementation. COPY receives
precomputed states and is diagnostic only, not an equivalent end-to-end fold.

| Input shape | Original wall seconds (3 samples) | Repaired wall seconds (3 samples) |
| --- | --- | --- |
| Identical transition inputs | 1.377583, 1.346294, 1.393522 | 0.714411, 0.734753, 0.730180 |
| 30,000 distinct transition inputs | 1.384642, 1.412656, 1.424028 | 1.462713, 1.465140, 1.440944 |

Repeated-input backend CPU: original 1.31/1.29/1.31 seconds, repaired
0.64/0.66/0.65. Repaired RSS after each sample: 104,060/104,060/104,656 KiB;
process-lifetime peak 120,060/120,060/120,488 KiB (not a per-operation peak).
Backend physical reads were zero in these warm-cache fixtures. Repaired
backend physical-write counters: 62,808,064 / 60,350,464 / 60,915,712 bytes.
WAL remained approximately 41.36 MB per sample. One top-level fold statement
runs per native sample; internal storage operations are unchanged.

The distinct-input control shows overhead, approximately 1–6% in these samples,
not a universal speedup. A full WordNet replay and production-scale concurrency
measurement remain outstanding. This repair does not claim lower WAL volume or
resolution of the 1,054-second historical drain.
