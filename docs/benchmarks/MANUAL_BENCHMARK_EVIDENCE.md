# Laplace benchmark suite

Tracking: `#1432`, `SaltyPatron/Laplace-Refactor#146`, `#165`, `#166`

`.github/workflows/benchmark-evidence.yml` is deliberately `workflow_dispatch`-only. Long performance measurements are evidence, calibration inputs, billing inputs, and optimization evidence; they are not automatic source-push or pull-request gates.

The workflow is only the dispatcher. The benchmark definition now lives in a versioned registry:

- `scripts/benchmark-profiles.json` — named benchmark profiles and suites;
- `scripts/benchmark_suite.py` — registry validator, suite runner, artifact binder, and suite-receipt writer;
- `scripts/bench-compose.py` — existing conservative single-thread core benchmark;
- `scripts/bench-compose-scale.py` — measured physical-core/SMT scaling benchmark.

This separation is intentional. Adding another benchmark should normally add a versioned profile to the registry and a reusable harness/provider, not another pile of workflow-specific shell logic.

## Current suites

| Suite | Profiles | Purpose |
|---|---|---|
| `quick` | `core-single`, `moby-roundtrip` | conservative core floor + bit-perfect realization proof |
| `throughput` | `core-single`, `core-scale` | single-thread floor and measured machine scaling |
| `core` | `core-single` | single-thread native composition |
| `scale` | `core-scale` | physical-core/SMT scaling only |
| `moby` | `moby-roundtrip` | bit-perfect Moby Dick engine roundtrip |
| `all` | all implemented profiles | complete currently implemented source/core benchmark set |

Future query/cognition, PostgreSQL, storage, UD/corpus, GPU-provider, model export, and complete accepted-work profiles belong in the same registry but require their own declared execution/state boundaries. Do not hide a live-database mutation behind a source/core profile.

## Exact-artifact law

A benchmark of source revision `X` must execute the artifact built from revision `X`.

This repository has an installed `/opt/laplace/lib/liblaplace_core.so` on the system loader path. The historical `bench-compose.py` defaults to that installed library unless `LAPLACE_CORE` is set. Merely checking out and building revision `X` while timing the installed library would create a perfectly formatted lie.

The suite therefore binds explicitly to:

```text
build/engine/core/liblaplace_core.so
build/engine/core/perfcache/laplace_t0_perfcache.bin
```

and exports the exact paths through `LAPLACE_CORE`, `LAPLACE_T0`, `LAPLACE_PERFCACHE_BIN`, `LAPLACE_ENGINE_BUILD`, and build-tree `LD_LIBRARY_PATH`. SHA-256 digests of the exact core library and T0 artifact are part of `suite-receipt.json`.

The same rule applies to future PostgreSQL extensions, model exporters, GPU kernels, perfcaches, generated code, and installed product benchmarks: **source identity is not execution identity until the receipt proves the binding**.

## Profile: `core-single`

`scripts/bench-compose.py` measures the native core boundary only:

- UTF-8 input;
- Unicode/NFC handling;
- UAX #29 segmentation;
- content/Merkle identity construction;
- tier-tree composition;
- physical/glome placement performed by the core path;
- no PostgreSQL, COPY, network, API serving, or GPU work.

The harness is intentionally single-threaded and reports codepoints/s, a 4-characters-per-token BPE-equivalent comparison, and tier-tree nodes/s. It is a one-worker floor rather than a whole-machine claim.

Historical committed evidence from `0f8405938daf3ab2aa6c1b745823be9e991ce6e6` recorded 875 real documents / 67.9 MB / 67,899,577 codepoints with three runs within 0.4% at approximately:

```text
1,859,000 codepoints/s
464,800 4-char BPE-equivalent tokens/s
4,555,400 tier-tree nodes/s
```

single-threaded.

## Profile: `core-scale`

`scripts/bench-compose-scale.py` exists specifically so whole-machine throughput is measured rather than inferred from the single-thread result.

For every scaling point it:

1. loads one fixed bounded real corpus before measurement;
2. identifies CPUs allowed to the process from the host affinity mask;
3. groups logical CPUs by physical package/core from sysfs topology;
4. assigns one hardware thread per physical core first;
5. adds SMT siblings only after physical cores are populated;
6. partitions the corpus once so each document is composed exactly once per point/repeat;
7. forks one worker per selected CPU and pins each worker to that logical CPU;
8. has every worker load the exact core library and T0 perfcache before the timer starts;
9. starts all workers from one synchronization event;
10. records all repeats, retaining the fastest measured repeat as the headline for that point;
11. rejects any scaling point whose constructed tier-tree node count differs from the others.

Default worker points are derived from the actual topology and include the useful physical-core and SMT boundaries rather than assuming a six-core host forever. On the known i7-6850K 6C/12T host the expected default shape is approximately:

```text
1, 2, 3, 4, 6, selected SMT points, 12
```

The receipt retains:

- exact CPU IDs used at every point;
- package/core/sibling topology;
- per-worker byte shard;
- every repeat wall time;
- aggregate codepoints/s;
- aggregate 4-character BPE-equivalent tokens/s;
- aggregate tier-tree nodes/s;
- speedup relative to the measured one-worker point;
- parallel efficiency.

The historical ~3.4M/s aggregate recollection is therefore treated as a result to recover/supersede with this measured scaling curve, **not** as `464.8k × N` arithmetic.

## Profile: `moby-roundtrip`

`laplace roundtrip <file> [out]` measures the engine text decomposition/reconstruction path. The suite independently verifies the output bytes and SHA-256 rather than trusting the CLI's own success message.

Historical committed evidence from `adc161ef86676cc2d078146dc160a25e116092ea` recorded `/vault/Data/test-data/text/moby_dick.txt` at 1,240,979 codepoints, approximately 377 ms ingest and 64 ms export, byte-for-byte identical.

This remains distinct from the heavier historical database-backed record/reconstruct benchmark in `062d48db8cea97380ffb4ebf7d4a81945763e223` (1.8 s record + 1.3 s reconstruction on 1,256,545 bytes). The database-backed path belongs in a separate future profile because persistence is additional work and must not be silently blended into the core number.

## Sparse-compute law

The computational thesis being tested by future complete-operation profiles is not "make dense math slightly faster." It is to avoid dense all-pairs work when the program can address the relevant state sparsely:

```text
naive/dense candidate work:       O(N^2)
indexed candidate location:       O(log N)     (provider/index dependent)
selected useful work:             O(K)

intended sparse shape:            O(log N) + O(K)
```

For direct content/perfcache addressing, an admitted physical provider may make the address term effectively `O(1)` for that operation.

This is a **workload/physical-plan contract**, not a universal theorem about every Laplace operation. A benchmark making this claim must declare:

- what `N` counts;
- what `K` counts;
- the exact index/perfcache/provider used for the address term;
- candidate counts before and after each filter;
- whether `K` is bounded, estimated, or actual;
- fallback behavior when the accelerator is unavailable or incomplete;
- actual rows/IDs/edges/physicalities touched;
- CPU/memory/I/O/database crossings;
- result parity against the non-accelerated logical operation.

If a selected algorithm performs sorting, pairwise comparison, numerical solve, or another superlinear operation over `K`, its real complexity is recorded instead of being mislabeled `O(K)`.

## Optional accelerator / GPU law

Laplace's GPU contract is **optional sparse physical acceleration**, not GPU-resident world/model authority.

A conforming CPU/GPU benchmark holds the logical program, world/evidence epoch, selected IDs, result contract, and semantic output constant, then compares physical providers. It records at least:

```text
N world/candidate population
K selected workset
CPU-only wall/resource receipt
GPU-assisted wall/resource receipt
host -> device bytes
device -> host bytes
kernel count / GPU-active interval when measurable
peak/additional device memory
GPU/provider identity
speedup
result/semantic parity
```

A 7x GPU-assisted result is meaningful even when only a tiny selected mathematical workset reaches the accelerator. It must not be described as evidence that the entire substrate was loaded into VRAM or brute-forced by the GPU.

Historical GEMM measurements in commit `f3e5425d8367cb12557f8338a9b3ddca69a783fa` are provider/precision evidence, not a substitute for the end-to-end sparse-offload benchmark.

## Energy and power boundaries

The suite captures Intel RAPL domains when the kernel exposes them. The receipt labels that boundary explicitly.

- PSU nameplate/rated wattage is a hardware capacity bound, **not** measured wall draw.
- RAPL is CPU/package/domain energy, **not** automatically whole-system wall energy.
- summed RAPL domains may overlap and therefore are not blindly promoted to wall joules.
- whole-system energy requires a compatible wall meter, UPS/PDU/BMC, or another calibrated admitted provider.

The suite runner records RAPL at the profile process boundary. Inner benchmark timers may intentionally exclude setup such as worker/perfcache preparation. Any work-per-joule headline must use an energy boundary whose numerator covers the same execution interval.

## Evidence artifact

Each dispatch uploads a run/attempt-specific artifact containing, when available:

- exact requested and resolved Git revision;
- benchmark registry and selected suite/profile identities;
- build log;
- exact built core/perfcache SHA-256 identities;
- host/runner identity;
- `lscpu`, memory, block-device and filesystem inventory;
- `inxi -Fxz` inventory;
- CPU governor state;
- core shared-library linkage;
- NVIDIA inventory/state before and after the suite so installed hardware is not confused with the selected execution provider;
- per-profile RAPL readings/deltas where available;
- raw profile output;
- machine-readable `core-scale.json` where selected;
- machine-readable `suite-receipt.json`;
- Moby reconstructed bytes and bit-perfect digests where selected;
- artifact manifest and file hashes.

## Isolation law

The workflow shares `laplace-shared-workspace` with the main delivery workflow, so those jobs cannot overlap on the persistent measured checkout. Core benchmarks additionally refuse a demonstrably advancing ingest unless the operator explicitly overrides the guard, because a benchmark with zero DB calls can still be corrupted by CPU contention from a live ingest.

The source/core suite builds the exact selected revision but does not install/deploy it, migrate the database, seed data, or publish services.

## Forward suite

The common registry should grow into separate profiles for:

- database-backed bit-perfect deposit/reconstruct;
- exact query `EXPLAIN` preflight + actual execution receipt;
- `O(log N)+O(K)` sparse addressability across representative `N`/`K` populations;
- CPU-only vs optional GPU sparse-provider parity and transfer economics;
- whole UD Treebanks and other corpus-scale admission;
- cold vs warm vs canonical/perfcache reuse;
- data/index/perfcache/storage amplification and maintenance cost;
- complete cognition/generation accepted-work throughput;
- model decomposition and bit-reproducible export;
- competitor-equivalent accepted-work cost;
- actual whole-system energy when a compatible meter is available.

Those profiles should feed the same plan/execution receipts used by billing, estimator calibration, capacity planning, and the Gödel optimization/refactoring loop.
