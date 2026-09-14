# Laplace benchmark suite

Tracking: `#1432`, `#1436`, `#1451`, `SaltyPatron/Laplace-Refactor#146`, `#165`, `#166`

`.github/workflows/benchmark-evidence.yml` is deliberately `workflow_dispatch`-only. Long performance measurements are evidence, calibration inputs, billing inputs and optimization evidence; they are not automatic source-push or pull-request gates.

The workflow is only the dispatcher. Benchmark definitions live in versioned repository state:

- `scripts/benchmark-profiles.json` — named profiles and suites;
- `scripts/benchmark_suite.py` — registry validator, exact-artifact binder, suite runner and receipt writer;
- `scripts/bench-compose.py` — conservative single-thread native composition floor;
- `scripts/bench-compose-scale.py` — one finite unique-corpus/file-grain diagnostic;
- `scripts/bench-compose-stream-scale.py` — replicated independent-stream aggregate scaling;
- `docs/benchmarks/SCALING_MODES.md` — what each scaling mode does and does not prove.

This separation is intentional. Adding another benchmark should normally add a versioned profile and reusable harness/provider, not workflow-specific shell logic.

## Current suites

The registry is authoritative for exact membership. At the time of this document revision:

| Suite | Profiles | Purpose |
|---|---|---|
| `quick` | `core-single`, `moby-roundtrip` | conservative native floor + bit-perfect realization proof |
| `throughput` | `core-single`, `core-scale-streams` | single-thread floor + aggregate independent-stream scaling |
| `core` | `core-single` | single-thread native composition only |
| `scale` | `core-scale-streams` | aggregate independent-stream scaling only |
| `moby` | `moby-roundtrip` | bit-perfect Moby Dick engine roundtrip |
| `all` | `core-single`, `core-scale-streams`, `core-scale`, `moby-roundtrip` | all currently implemented source/core profiles |

`core-scale` remains useful as a diagnostic for coarse whole-file scheduling. It is not the current whole-machine aggregate throughput profile. Future query/cognition, PostgreSQL, storage, UD/corpus, GPU-provider, model export and complete accepted-work profiles belong in the same registry but require their own declared execution/state boundaries. Do not hide a live-database mutation behind a source/core profile.

## Exact-artifact law

A benchmark of source revision `X` must execute artifacts built from revision `X`.

This repository has an installed `/opt/laplace/lib/liblaplace_core.so` on the system loader path. Historical benchmark code can default to installed state unless explicit environment overrides are supplied. Checking out/building revision `X` while timing an older installed `.so` would create a perfectly formatted lie.

The suite therefore binds explicitly to the build-tree native core and T0 perfcache and records their SHA-256 digests in its receipt. The same law applies to future PostgreSQL extensions, model exporters, GPU kernels, perfcaches, generated code and installed product benchmarks:

> **Source identity is not execution identity until the receipt proves the binding.**

## Two different capacity questions

A benchmark must distinguish **serviceable throughput** from **absolute saturation**.

They answer different questions:

```text
serviceable throughput:
  how much Laplace work can this host sustain while the database,
  product, runner, monitoring and operator control plane remain healthy?

absolute saturation:
  how much work can the selected kernel push when the experiment is
  allowed to consume essentially all schedulable machine capacity?
```

Both numbers are useful. They are not interchangeable.

### Managed-host default: reserve headroom

A live managed host must not schedule one benchmark worker for every schedulable logical CPU merely because those CPUs exist.

The normal capacity profile reserves explicit CPU and host headroom for PostgreSQL, the Actions runner, SSH/operator control, monitoring and normal product operation. It fills physical cores before SMT siblings, but stops before the host loses its control/service reserve.

For the current 6-core/12-thread i7-6850K host, a sensible normal series is approximately:

```text
1, 2, 3, 4, 6, 8, 10
```

rather than ending at 12 by default. The exact reserve should ultimately be topology/configuration driven rather than hard-coded to this machine.

A serviceable-capacity receipt should include enough host evidence to reject a misleading peak point, including where available:

- selected and reserved CPU IDs / affinity policy;
- physical-core and SMT topology;
- CPU/load state;
- memory pressure / swap state;
- I/O pressure relevant to the profile;
- database/product/runner liveness where those services are expected to remain available;
- per-point wall time, useful work, speedup and efficiency.

If a point makes the managed host, database, runner or required product health boundary unavailable, that point is **not valid evidence of serviceable capacity**, even if it printed a larger throughput number before the machine became unavailable.

### Saturation is explicit

A full physical/SMT experiment may intentionally include every allowed logical CPU, but that belongs to an explicitly selected saturation/isolated profile. It must be labeled accordingly and must not be the default meaning of `all` on a live managed host.

A saturation result is still useful for kernel/provider ceiling work. It simply cannot be marketed as the amount of compute safely available to normal operations on the same machine.

### Evidence from Actions run `34823625126`

The 2026-09-14 manual benchmark run entered `core-scale-streams` with:

```text
6 physical cores
12 allowed logical CPUs
worker points: 1,2,3,4,6,8,10,12
mode: replicated-independent-streams
```

GitHub records the run as `failure`, and the final sealed evidence artifact was not available through the Actions interface when inspected. The exact terminal process/cause therefore must not be invented from incomplete evidence.

What the run does establish for benchmark design is simpler: **12 available logical CPUs must not automatically mean 12 safe workers on the live managed host.** The benchmark contract now treats serviceable capacity and saturation as separate evidence classes. See `#1436`.

## Profile: `core-single`

`scripts/bench-compose.py` measures the native core boundary only:

- UTF-8 input;
- Unicode/NFC handling;
- UAX #29 segmentation;
- content/Merkle identity construction;
- tier-tree composition;
- physical/glome placement performed by the native core path;
- no PostgreSQL, COPY, network, API serving or GPU work.

The harness is intentionally single-threaded and reports codepoints/s, a four-characters-per-token BPE-equivalent comparison and tier-tree nodes/s. It is a one-worker floor rather than a whole-machine claim.

Historical committed evidence from `0f8405938daf3ab2aa6c1b745823be9e991ce6e6` recorded 875 real documents / 67.9 MB / 67,899,577 codepoints with three runs within 0.4% at approximately:

```text
1,859,000 codepoints/s
464,800 4-char BPE-equivalent input units/s
4,555,400 tier-tree nodes/s
```

single-threaded.

Actions run `34823625126` later measured a different 1,158-document / 51.2 MB corpus on the same class of host at:

```text
best: 1,761,500 codepoints/s
      440,400 4-char BPE-equivalent input units/s
    4,325,900 tier-tree nodes/s
```

with all three repeats constructing exactly `125,793,955` nodes. The different corpus/revision means the ~5% headline difference is not by itself a controlled regression result. Both receipts establish the same order-of-magnitude native floor.

The BPE-equivalent value is only a familiar normalization. The benchmark is building an exact recursive structure; in the latter run approximately 4.326 million tier-tree nodes/s were built while ingesting approximately 440 thousand four-character input-token equivalents/s. Do not erase that structural work by describing the benchmark as a flat tokenizer measurement.

## Profile: `core-scale-streams`

`scripts/bench-compose-stream-scale.py` answers a different question from the older partitioned finite-corpus benchmark: how much aggregate native composition can the host sustain when multiple independent streams exist simultaneously?

Every pinned worker executes one complete real corpus stream. Measured useful work therefore grows with worker count instead of partitioning one fixed input among workers.

The profile:

1. discovers CPUs allowed by the process affinity mask;
2. groups logical CPUs by physical package/core from sysfs topology;
3. fills physical cores before adding SMT siblings;
4. pins workers to explicit logical CPUs;
5. has each worker load the exact native core and T0 perfcache before the timing boundary;
6. starts workers from a synchronization barrier;
7. retains repeats and machine-readable point receipts;
8. verifies structural work does not drift unexpectedly.

This profile proves aggregate independent-stream headroom. It does **not** prove that one semantic DAG internally parallelizes across the same workers; that is a separate benchmark/implementation question tracked by `#1451`.

On a managed host, the profile's normal worker set must obey the serviceable-headroom law above. A separate explicit saturation mode may extend through all logical CPUs.

## Profile: `core-scale`

`scripts/bench-compose-scale.py` partitions one finite unique corpus across workers at whole-document granularity. It is valuable precisely because it exposes scheduling/granularity effects, but it is not a clean aggregate host-ceiling measurement when one indivisible document dominates the corpus.

The corpus used in run `34823625126` reported a largest document of approximately 41.6 MB, about 81.2% of one 51.2 MB stream. In a partitioned fixed-corpus run, that one document can dominate makespan regardless of available native concurrency. A plateau under that workload must not be mislabeled a native serialization ceiling.

This is why `core-scale-streams` exists and why single-DAG/frontier scaling remains a separate acceptance target.

## Profile: `moby-roundtrip`

`laplace roundtrip <file> [out]` measures the engine text decomposition/reconstruction path. The suite independently verifies output bytes and SHA-256 rather than trusting the CLI's own success message.

Historical committed evidence from `adc161ef86676cc2d078146dc160a25e116092ea` recorded `/vault/Data/test-data/text/moby_dick.txt` at 1,240,979 codepoints, approximately 377 ms ingest and 64 ms export, byte-for-byte identical.

This remains distinct from the heavier historical database-backed record/reconstruct benchmark in `062d48db8cea97380ffb4ebf7d4a81945763e223` (1.8 s record + 1.3 s reconstruction on 1,256,545 bytes). Persistence is additional work and belongs in its own profile.

## Sparse-compute law

The computational thesis for future complete-operation profiles is not “make dense math slightly faster.” It is to avoid all-world work when the program can address the responding state sparsely.

A useful high-level shape is:

```text
world / eligible population: N
actually responding/admitted frontier: K, with K << N when sparsity holds

address/index work: provider dependent (often O(log N), sometimes direct/O(1))
selected work:       algorithm dependent over K
```

Do not mechanically write every operation as `O(log N)+O(K)`. A benchmark making a sparse-compute claim must declare:

- what `N` and `K` count;
- the exact index/perfcache/provider used;
- candidate counts before/after filters;
- hop/fanout/frontier budgets;
- whether `K` is bounded, estimated or actual;
- fallback behavior when an accelerator is unavailable/incomplete;
- actual rows/IDs/edges/physicalities touched;
- CPU/memory/I/O/database and managed/native boundary crossings;
- result parity against the corresponding logical operation.

If the selected algorithm sorts, performs pairwise work, solves numerically or is otherwise superlinear over `K`, record its real complexity.

## Execution-grain evidence

Performance profiles should expose not only how much work was completed but **where repeated work executed**.

Laplace's intended hot-path split is:

```text
PostgreSQL / indexes: selective durable set access
SPI:                  prepared, set-sized bridge
native C/C++:          repeated loops, recursion, composition,
                       trajectory work, fanout/search, reductions,
                       parsing/encoding/materialization kernels
C#/SQL:                orchestration, contracts, transport
```

The same law applies to decomposition, ingestion, query/cognition, analysis/domain engines, reconstruction, synthesis and export.

A benchmark can look slow for reasons that have nothing to do with the underlying semantic operation if the implementation pays a SQL/SPI/PInvoke/parser/transaction/materialization boundary per row, token, node, candidate or output value. Future receipts should therefore include boundary-crossing counts and batch/set sizes wherever practical.

The optimization question is two-dimensional:

```text
1. how much work was avoided through addressing/reuse/hops/fanout?
2. how much per-unit orchestration overhead was avoided by coarse native/set execution?
```

## Optional accelerator / GPU law

Laplace's GPU contract is optional sparse physical acceleration, not GPU-resident world/model authority.

A conforming CPU/GPU benchmark holds the logical program, world/evidence epoch, selected IDs, result contract and semantic output constant, then compares physical providers. It records at least selected workset, host/device bytes, kernel/provider identity, memory, active interval when measurable, speedup and semantic parity.

Installed GPU != selected GPU != GPU-resident world.

Historical GEMM measurements are provider/precision evidence, not substitutes for end-to-end sparse-offload evidence.

## Energy and power boundaries

The suite captures Intel RAPL domains when the kernel exposes them and labels the boundary explicitly.

- PSU rated wattage is capacity, not measured wall draw.
- RAPL is CPU/package/domain energy, not automatically whole-system wall energy.
- RAPL domains may overlap and must not be blindly summed into wall joules.
- whole-system energy requires a compatible wall meter, UPS/PDU/BMC or other calibrated provider.

Any work-per-joule headline must use an energy interval covering the same work as its numerator.

## Evidence artifact

Each dispatch should preserve, when available:

- exact requested and resolved Git revision;
- benchmark registry and selected suite/profile identities;
- build log;
- exact built native/perfcache hashes;
- host/runner identity;
- CPU topology, affinity and reserved-headroom policy;
- memory/block/filesystem inventory relevant to the run;
- CPU governor state;
- accelerator inventory/state and selected-provider identity;
- per-profile energy evidence where available;
- raw profile output;
- machine-readable profile and suite receipts;
- reconstruction outputs/digests where selected;
- artifact manifest and hashes;
- service-health evidence for profiles claiming serviceable capacity.

A benchmark failure should preserve the completed point receipts/logs when the runner remains alive. A missing artifact must be reported as missing evidence rather than reconstructed from memory.

## Isolation law

The benchmark workflow shares the managed Laplace host with real services. Mutual exclusion with another Actions job is necessary but not sufficient isolation: PostgreSQL, monitoring, SSH/operator control and the product can still need host capacity.

Source/core benchmarks must not deploy, migrate, seed or mutate the live substrate merely to collect throughput evidence.

Profiles claiming serviceable capacity reserve headroom and keep required services alive. Profiles intentionally claiming saturation must say so and run only where consuming the reserve is acceptable.

## Forward suite

The common registry should grow into separate profiles for:

- database-backed bit-perfect deposit/reconstruct;
- exact query `EXPLAIN` preflight + actual execution receipt;
- hop/fanout/query-relative coupling and sparse-addressability cost across representative populations;
- rich web-response latency/throughput (constituents, evidence cells, consensus/relations, paths/frontier work), not only token-normalized rates;
- CPU-only vs optional GPU sparse-provider parity and transfer economics;
- whole-corpus/source admission;
- cold vs warm vs canonical/perfcache reuse;
- data/index/perfcache/storage amplification and maintenance cost;
- complete cognition/generation accepted-work throughput;
- model decomposition and bit-reproducible export;
- competitor-equivalent accepted-work cost/quality/latency/energy;
- actual whole-system energy when a compatible meter is available.

Those profiles should feed the same plan/execution receipts used by billing, estimator calibration, capacity planning and the optimization/refactoring loop.
