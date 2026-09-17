# Complete recorded chess throughput

The `recorded` benchmark suite measures ordinary Chess Lab jobs from game generation through synchronous PostgreSQL writer completion and exact native game readback. The API, native parser/composer, shared writer and native readback remain the production implementations. The collector checks their receipts and aggregates measured work.


## Installed recording attempt — 2026-09-17

[Run 35184890182, attempt 1](https://github.com/SaltyPatron/Laplace/actions/runs/35184890182)
installed source `3a161d0f8390d4d5c3f9309d5a08746418d13b7d`
(tree `f9db5544b0f14cb2eece60a1312706a77f0392ba`). Its native installation
completed all 14 phases, and managed application publication completed all 19
phases. The canonical consensus repair completed for all five chess sources
without changing their evidence counts. The recording pilot was then cancelled
while processing its seventy-ninth chunk.

| Retained measurement | Actual result |
| --- | ---: |
| Requested and parsed complete games | 2,000 |
| Newly recorded games with sealed exact readback | 594 |
| Ordered plies in those sealed games | 47,162 |
| Sealed chunks | 78 |
| Composed candidate games at the last checkpoint | 601 |
| Acknowledged writer calls | 78 |
| Committed COPY transactions | 1,003 |
| Logical writer round trips | 1,403 |
| Inserted entity rows | 1,971,190 |
| Inserted physicality rows | 1,967,407 |
| Inserted attestation rows | 284,295 |
| Journal replay hits | 0 |

The selection excluded the earlier 140 sealed games and reselected the seven
previously incomplete candidates. Composed candidates and the open seventy-ninth
chunk are not added to the 594 sealed-game count. Each of the 78 current chunk
bodies was checked against its manifest hash. Their writer counters are
cumulative: use the last cumulative values above, not the sum of 78 snapshots.
Similar entity and physicality totals do not establish a one-to-one identity law.

The final recording receipt, full-selection replay and after-measurement runtime
guard were not produced. This attempt therefore supplies committed chunk
evidence, not a completed recorded-game rate or the 2,500-games/second result.
Its last heartbeat was at 774.065 seconds; that diagnostic timestamp is not used
as a successful benchmark denominator. The interrupted artifact retained no
exclusive writer/readback timing totals, so those costs cannot be reconstructed
from its sampled phase names.

Evidence is retained in artifact `10482117875`,
`original-native-install-35184890182-1`, with ZIP SHA256
`d97b27055b3a34b6383f6d79d2f11d3dbb07a98e5c7fd6195a99e4701428d2e9`.
The completed managed publication receipt SHA256 is
`5e363d10cd2b8b3309bb343a0ea99840961cd1bf4959d1ed6ba12e008c2c8db3`.
These identify the retained attempt; a later machine check still reads the
currently installed files and PostgreSQL generation.

## Exact native consensus calculation — 2026-09-17

[Qualification 35188643764](https://github.com/SaltyPatron/Laplace/actions/runs/35188643764)
compared the qualified native implementation
`ac5f844627d7597279ae70f07ab084b09ba9b532` with
`ec39c1f3bf57ec534a33dfdf769ffda77a222775`. The change reuses only the
opponent-dependent calculation for adjacent identical rating/RD pairs within
one canonical period. Every retained row keeps its count, score quotient and
remainder, rounding, validation, accumulation order and final solve.

The actual controls passed 759 arithmetic reference cases, 256 whole-period
reference cases, 505 exact old/new grouped comparisons and 46 native tests,
with no UBSan finding. All 30 timed output states and checksums matched.

Each table entry is median CPU milliseconds for 25,000 folds across three
trials on the GitHub-hosted qualifier, using identical compiler settings.
The instrumentation that counts calculations was run separately.

| Input pattern | Previous CPU ms | Cached CPU ms | Change |
| --- | ---: | ---: | ---: |
| One group | 35.930 | 35.736 | 0.54% lower |
| Actual retained 60 rows, identical opponent | 260.703 | 59.739 | 77.09% lower; 4.36× faster |
| Alternating four opponent pairs | 251.667 | 254.199 | 1.01% higher |
| Four-row runs of repeated pairs | 246.804 | 102.221 | 58.58% lower; 2.41× faster |
| Sixty distinct opponent pairs | 258.396 | 259.781 | 0.54% higher |

Measured opponent calculations fell from 60 to one for the retained uniform
case, and from 60 to 15 for repeated four-row runs. All-miss cases retained 60
calculations. This is native calculation time: the measurement excludes SQL
scans, evidence sorting, COPY, complete-game composition and readback, and
does not imply a corresponding recorded-game speedup.

Artifact `10483035849` has ZIP SHA256
`ebdcf39d354757f72622c2483777c27f8c95feb6e549e406e5839128240fb457`.

## Earlier retained corpus evidence — 2026-09-17

The earlier native installation and attempted retained-corpus run is
[35174504991, attempt 1](https://github.com/SaltyPatron/Laplace/actions/runs/35174504991),
using source `ee2995b65e0f0182ae0776a9524443a6240d72b5`. The corpus measurement
**failed**; it does not establish recorded-game capacity.

| Measurement | Actual result |
| --- | --- |
| Requested complete games | 2,000 |
| Complete games committed and exactly read back before failure | 140 |
| Ordered plies in those games | 10,546 |
| Completed admission chunks | 20 |
| Attempted admission chunks | 21 |
| Elapsed time through failure | 60.071 seconds |
| Completed-run recorded games/second | `null` |
| Completed full-selection replay | No |
| 2,500 recorded games/second established | No |

The failed twenty-first chunk contained seven additional candidates. They are
excluded from the 140 complete-game count. Their attempted work remains in the
failure evidence; the retained games and original failed receipt are preserved.
Dividing 140 by the time through failure would not produce a successful complete
benchmark.

Whole-attempt diagnostics retained 21 provider calls totaling 22.155 seconds,
21 COPY/commit calls totaling 8.927 seconds, and 21 consensus calls totaling
4.279 seconds, of which one was interrupted. The enclosing writer interval was
41.557 seconds; exact game readback took another 14.915 seconds. The nested
provider, COPY and consensus intervals must not be added to their enclosing
writer interval. These are workload measurements, not isolated CPU measurements.

The 20 successful applies reported 557,199 inserted entities, 556,281 inserted
physicalities and 66,989 inserted attestations, across 260 COPY transactions.
Those counters exclude the failed apply and are separate from complete games.
Native descriptor and selected-view structures contribute additional canonical
entities and typed physicalities; the similar E/P totals do not establish a
general one-to-one entity/physicality law.

The failure exposed storage-flush-dependent consensus periods. The canonical
recording correction is committed in
[the source ending at dd31256](https://github.com/SaltyPatron/Laplace/commit/dd31256d3e8deca265a9b7861109e20273de52cb):
fully replayable retained testimony is folded as one canonical period per cell,
and source repair uses the same target locking and fresh evidence read as
admission. Mixed transient continuous-score cells retain their exact delta
semantics. A separate checked-arithmetic correction removes saturated
intermediates. Both corrections were subsequently installed by run 35184890182,
whose five-source canonical refold is recorded above. The repaired 60-row cell
retains 2,474 observations and reads rating `1798219672794`, RD `9058987541`
and volatility `59999089` on the native fixed-point scale.

The private host evidence directory for the attempted run is
`/build/laplace/recovery/native-install/35174504991-1/`. The corresponding
`original-native-install-35174504991-1` workflow artifact has ID
`10477948015` and SHA256
`667837b6c1f6dad4b703ab3669f41c8085a6e450579a67cc593d2d1bf40efeda`.
Use the original corpus receipt, completed chunk descriptors, exact readback
scope and writer aggregates together. Generated Stockfish self-play rates,
position-floor row counts, kernel timings and geometry COPY rates are separate
workloads.

## First establish correctness

The direct collector keeps the existing 24-game, depth-4 sweep over concurrency 1, 2 and 4. Each case must finish normal games, preserve its exact executable and experiment identities, acknowledge synchronous local WAL flush, and read back every newly recorded playing, ordered move identity and experiment witness.

```bash
python3 scripts/benchmark-recorded-chess.py \
  --api-base http://127.0.0.1:5187 \
  --output-dir /build/laplace/work/recorded-chess-correctness \
  --games 24 --repeats 1 --concurrency 1,2,4
```

Each concurrency point runs once in this example. The ordinary collector default remains three repeats. Existing API authentication settings (`LAPLACE_API_KEY`, `LAPLACE_QUOTE_ID`, `LAPLACE_PROOF_TENANT`) stay in the environment and are not copied into the evidence.

The schema `laplace.recorded-chess-benchmark/v2` distinguishes a finite sample rate from a sustained capacity result. `sampleTargetRateReached` reports whether a server-workflow sample median reaches the target. Correctness-only runs always have `durationQualified=false` and `targetMet=false`, even when a short sample is fast.

## Measure a sustained window

After inspecting the initial workload and artifact sizes, run the explicit existing-registry suite:

```bash
python3 scripts/benchmark_suite.py run --suite recorded --repeats 1 \
  --receipt-dir /build/laplace/work/recorded-chess-duration \
  --recorded-api-base http://127.0.0.1:5187 \
  --recorded-games 24 --recorded-concurrency 1,2,4 \
  --recorded-duration-seconds 30 --recorded-total-timeout 900 \
  --recorded-max-sustained-cases 4096 \
  --recorded-max-total-artifact-bytes 1073741824
```

The explicit `recorded` suite defaults to a 30-second duration window. It is excluded from `all`, because it writes real game and experiment evidence through the installed API. It requires the installed service and its dependencies; building a separate collector-side native library would not identify the service being measured. The workflow's existing explicit benchmark selector also exposes this suite under its shared host lock.

The collector first runs the correctness sweep, then selects the concurrency with the highest median complete collector throughput. A tie selects the lower concurrency. That selection is only a configuration candidate. A separate sequence of new complete jobs establishes its duration result.

Every job keeps the configured bounded game batch. No enormous single request is substituted to fill the duration. The collector continues until the requested elapsed duration and at least two complete jobs have finished, or an explicit resource/deadline bound fails. A whole job can extend beyond the requested minimum duration; the complete measured interval remains the denominator. The total execution deadline still limits that job.

### Rate boundaries

| Field | Numerator and denominator |
|---|---|
| `gamesPerSecondServerWorkflow` | New verified games / server entry through full play, normal writer completion and exact readback. Excludes API queue, final receipt serialization and artifact download. |
| `gamesPerSecondEndToEnd` | New verified games / one case's start request through observed completion and artifact download. |
| `gamesPerSecondSustainedEndToEnd` | Total distinct new verified games / one enclosing wall interval across repeated complete cases, including queueing, polling, transfer and inter-case bookkeeping. |
| `pliesPerSecondSustainedEndToEnd` | Exact retained ordered move count / that same enclosing interval. |
| `gamesPerDayExtrapolated` | Measured sustained-window rate multiplied by 86,400. This is an arithmetic extrapolation, not a day-long observation. |

Stage times remain available per case. The aggregate denominator is never a sum of stage durations or overlapping job durations. Current jobs finish their entire match before PGN ingestion; the measured sustained boundary is repeated complete workflows. It does not assert a continuous online game-to-writer stream.

`durationQualified` requires a successful window of at least the explicitly requested 30..3600 seconds and at least two complete, distinct jobs. `targetMet` additionally requires at least 2,500 newly recorded and exactly verified games per second over that complete interval. The target is a requirement, not a claimed measured result. A below-target successful measurement still exits successfully and reports `targetMet=false`.

## Bounds, failures and evidence ownership

Game batches are even and bounded to 2..512 games. Initial sweeps allow at most eight concurrency points in 1..64, each repeated 1..10 times. The sustained case ceiling is 2..4096, with a default of 4096. At the default 24 games per case and a 30-second minimum window, this permits 98,304 games and a count-only ceiling of 3,276.8 games/second. The receipt's `targetResourceEnvelope` reports this ceiling, the minimum target game count, and whether an explicitly smaller case limit admits the target. Actual game runtime, polling, transfer, byte limits and the total deadline still apply; this arithmetic does not establish achieved capacity. The total execution deadline is at most two hours; failed owned-job cancellation has a separate maximum ten-second allowance. No other job is cancelled.

Individual original artifacts are limited to 256 MiB, and their cumulative retained bytes are limited to the configured budget, at most 4 GiB. Partial local artifact writes count against that budget and are retained as `.partial`. Job/catalog API metadata is separately limited to 1 MiB per response; local checkpoints are bounded by the admitted job/game counts. These are explicit payload and workload bounds, not a claim to account for the server's total RSS or disk growth.

HTTP deadlines cover a whole response even if bytes keep arriving. Case deadlines also cover artifact downloads and collector verification. The output directory must be new or empty; an old receipt is never replaced by a new run. Within the owned run, JSON checkpoints are atomically replaced, and every case retains the original request, job state, experiment, recording receipt, PGN and transcript where available.

An incomplete or failed case stays in the aggregate evidence. Only completed, validated, distinct cases contribute to the partial-work counter, and any failed window withholds duration qualification and the capacity claim. The original native receipts retain actual inserted/skipped entities, physicalities, attestations, COPY transaction counts and logical writer accounting; rows and plies remain separate denominators.

## Retained input and replay are separate workloads

The ordinary retained-PGN ingestion route measures generation-free admission of an exact retained PGN and experiment. Its first/mixed/repair disposition and exact replay have separate receipts. Replaying an existing playing may demonstrate identity reuse and avoided writes, but it cannot be counted as newly generated-and-recorded throughput. A second generated match is also not a replay of the first experiment.

Use the [PostgreSQL geometry baseline](POSTGRES_GEOMETRY_BASELINE.md) to compare exact storage rows, vertices and byte payloads under acknowledged synchronous commits. That baseline helps locate write cost; it does not replace complete-game throughput.

## Cache reuse and reader lifetime

The chess position map remains native. Its readers retain access through the complete geometry copy; publication blocks new entries, drains the fixed reader shards and then releases the old mapping. Readers search concurrently without the global managed native gate. The exported pointer lookup returns a thread-local copy that survives another thread's remap/unload, but the next lookup on the same thread reuses that slot. New consumers that need independent values should use the caller-owned geometry-copy API.

The transition map publishes an immutable, fully validated replacement. Each reader holds a SafeHandle reference until its lookup completes, so retiring a mapping cannot invalidate a pointer in use. A rejected replacement preserves the previous valid map. Successful load/unload starts a fresh process-local derived-cache generation.

That derived cache has exactly 65,536 slots. Hash collisions evict acceleration entries; complete keys are checked before reuse, and misses follow the existing canonical composition path. Repeating an existing key with a conflicting result, or conflicting with a mapped result, is rejected. Observations expose capacity, occupancy and collision evictions separately from persistent-map hits. These counters describe reuse, not new testimony or admitted games.

Process-local novel transitions are not exported as a learned corpus. The transition v1 file has no source-generation or recipe identity, and its loader has no canonical rebuild/verification binding. The position header contains an emitter source hash, but the current load API does not take an expected source/recipe identity. Durable derived-cache reuse must establish those compatibility and rebuild links before accepting persisted computed entries; a dictionary dump would not establish them. PostgreSQL remains the system of record. File generation alone also does not establish installed lookup hits or cold-boot performance.
