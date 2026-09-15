# Complete recorded chess throughput

The `recorded` benchmark suite measures ordinary Chess Lab jobs from game generation through synchronous PostgreSQL writer completion and exact native game readback. The API, native parser/composer, shared writer and native readback remain the production implementations. The collector checks their receipts and aggregates measured work.

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
