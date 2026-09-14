# Laplace benchmark suite

Tracking: `#1432`, `#1436`, `#1451`.

`.github/workflows/benchmark-evidence.yml` is deliberately `workflow_dispatch`-only. Long performance measurements are evidence, calibration inputs, billing inputs, and optimization evidence; they are not automatic source-push or pull-request gates.

The workflow is only the dispatcher. The benchmark definition lives in a versioned registry and reusable harnesses:

- `scripts/benchmark-profiles.json` — named benchmark profiles and suites;
- `scripts/benchmark_suite.py` — registry validator, suite runner, artifact binder, and suite-receipt writer;
- `scripts/benchmark_scale_plan.py` — managed-host serviceable/saturation worker-plan authority;
- `scripts/bench-compose.py` — conservative single-thread core benchmark;
- `scripts/bench-compose-scale.py` — finite unique-corpus/file-grain scaling diagnostic;
- `scripts/bench-compose-stream-scale.py` — aggregate independent-stream scaling.

## Current suites

| Suite | Profiles | Purpose |
|---|---|---|
| `quick` | `core-single`, `moby-roundtrip` | conservative core floor + bit-perfect realization proof |
| `throughput` | `core-single`, `core-scale-streams` | single-thread floor + aggregate serviceable stream scaling |
| `core` | `core-single` | single-thread native composition |
| `scale` | `core-scale-streams` | aggregate stream scaling only |
| `moby` | `moby-roundtrip` | bit-perfect Moby Dick engine roundtrip |
| `all` | all implemented profiles | source/core evidence set, including file-grain diagnostic |

Future query/cognition, PostgreSQL, storage, UD/corpus, GPU-provider, model export, and complete accepted-work profiles belong in the same registry but require their own declared execution/state boundaries.

## Exact-artifact law

A benchmark of source revision `X` must execute the artifact built from revision `X`.

The suite binds explicitly to:

```text
build/engine/core/liblaplace_core.so
build/engine/core/perfcache/laplace_t0_perfcache.bin
```

and exports those exact paths through `LAPLACE_CORE`, `LAPLACE_T0`, `LAPLACE_PERFCACHE_BIN`, `LAPLACE_ENGINE_BUILD`, and build-tree `LD_LIBRARY_PATH`. SHA-256 digests of the exact core library and T0 artifact are part of the evidence.

Source identity is not execution identity until the receipt proves the binding.

## Profile: `core-single`

`scripts/bench-compose.py` measures the native core boundary only: UTF-8 input, Unicode/NFC handling, UAX #29 segmentation, content/Merkle identity construction, tier-tree composition, and geometric placement performed by the core path. It includes no PostgreSQL, COPY, network, API serving, or GPU work.

It is a one-worker floor rather than a whole-machine claim.

Historical committed evidence from `0f8405938daf3ab2aa6c1b745823be9e991ce6e6` recorded roughly:

```text
1.859M codepoints/s
464.8k 4-char BPE-equivalent units/s
4.555M tier-tree nodes/s
```

single-threaded.

## Scaling profiles

`core-scale` and `core-scale-streams` answer different questions.

`core-scale` measures one finite corpus partitioned at whole-file grain. It is useful for exposing coarse scheduler bottlenecks; it is not a whole-machine ceiling and not proof of single-object parallelism.

`core-scale-streams` runs one complete real-corpus stream per worker, so the amount of measured work grows with worker count. This measures aggregate concurrent composition capacity; it is not proof that one semantic DAG uses several workers internally.

See `docs/benchmarks/SCALING_MODES.md` for the full distinction and #1451 for the missing single-DAG frontier proof.

## Managed-host capacity law

The self-hosted Laplace runner is a managed machine, not a disposable isolated benchmark appliance. PostgreSQL, the Actions runner, monitoring/control processes, and product services need CPU capacity too.

Therefore the workflow resolves a scale plan **before** running either scaling harness.

By default:

```text
allow_saturation = false
reserve_logical_cpus = 2
```

`scripts/benchmark_scale_plan.py` discovers the allowed CPU topology, derives serviceable worker points, writes `scale-plan.json`, and passes the exact resolved list into the suite runner.

On the known 6C/12T i7-6850K host, the default resolves to:

```text
1,2,3,4,6,8,10
```

A requested worker point above the serviceable ceiling is rejected unless the operator explicitly dispatches with `allow_saturation=true`.

With saturation enabled, the full logical-CPU boundary may be included and the receipt labels the plan `saturation-allowed`.

This distinction is required because **serviceable throughput and destructive saturation are different measurements**. A point that makes the runner/database/product/control plane unavailable is not normal service capacity just because it printed a higher partial throughput number.

Run `34823625126` is retained as an incomplete/failing counterexample from the prior design: its scale series admitted all 12 logical CPUs and no sealed evidence artifact was available afterward. The available evidence does not establish the exact terminal process failure, so no stronger causal claim is made.

## Scale receipt

The evidence retains, as applicable:

- physical/logical CPU topology and allowed CPU IDs;
- requested and resolved worker points;
- serviceable headroom reserve or explicit saturation opt-in;
- exact CPU IDs used at every measured point;
- complete corpus bytes/codepoints/documents per worker;
- every repeat wall time;
- aggregate codepoints/s;
- aggregate 4-character BPE-equivalent units/s;
- aggregate tier-tree nodes/s;
- speedup versus measured one-worker point;
- parallel efficiency;
- host inventory and available power/accelerator telemetry.

Serviceable-capacity profiles should continue growing health receipts so database/product/runner liveness is measured, not assumed.

## Profile: `moby-roundtrip`

`laplace roundtrip <file> [out]` measures engine text decomposition/reconstruction. The suite independently verifies output bytes and SHA-256 rather than trusting the CLI's own success message.

This remains distinct from database-backed deposit/reconstruction because persistence is additional work and must not be silently blended into the core number.

## Sparse-compute law

The computational thesis for future complete-operation profiles is not “make dense math slightly faster.” It is to avoid dense all-pairs work when the program can address relevant state sparsely:

```text
naive/dense candidate work: O(N^2)
indexed candidate location: provider/index dependent
selected useful work:       K
```

Any benchmark claiming sparse complexity names what `N` and `K` count, the exact address provider/index, candidate reductions, actual rows/IDs/edges/physicalities touched, boundary crossings, and semantic parity. Superlinear work over `K` is recorded honestly.

## Optional accelerator / GPU law

Laplace's GPU contract is optional sparse physical acceleration, not GPU-resident world/model authority.

CPU/GPU comparisons hold the logical program, world/evidence epoch, selected IDs, result contract, and semantic output constant while recording transfer bytes, kernel/provider identity, device memory, timing, speedup, and parity.

Installed GPU != selected GPU != GPU-resident world.

## Energy and power boundaries

The suite captures Intel RAPL domains when exposed by the kernel.

- PSU nameplate wattage is capacity, not measured wall draw.
- RAPL is CPU/package/domain energy, not automatically whole-system wall energy.
- overlapping RAPL domains are not blindly summed into wall joules.
- whole-system energy requires a calibrated compatible meter/provider.

Any work-per-joule headline must align the energy interval with the work interval.

## Evidence artifact

Each dispatch attempts to upload a run/attempt-specific artifact containing, when available:

- requested/resolved Git revision;
- benchmark registry and selected suite/profile identities;
- `scale-plan.json` and resolved serviceable/saturation policy;
- build log;
- exact built core/perfcache SHA-256 identities;
- host/runner inventory;
- CPU governor state;
- native linkage;
- accelerator state before/after;
- RAPL readings/deltas where available;
- raw profile output;
- machine-readable scaling receipts;
- `suite-receipt.json`;
- Moby reconstructed bytes/digests where selected;
- manifest and file hashes.

The artifact upload uses `if: always()`. If the host/runner itself is made unavailable, no workflow can guarantee a terminal upload; that is another reason unsafe saturation is not the managed-host default.

## Isolation law

The workflow shares `laplace-shared-workspace` with the main delivery workflow, so those jobs cannot overlap on the persistent measured checkout. Source/core benchmarks additionally refuse demonstrably advancing ingest unless explicitly overridden.

The source/core suite builds the exact selected revision but does not install/deploy it, migrate the database, seed data, or publish services.

## Forward suite

The common registry should grow into separate profiles for database-backed bit-perfect deposit/reconstruct, query `EXPLAIN` plus execution receipts, sparse addressability across representative populations, CPU/GPU provider parity, whole-corpus admission, cold/warm/perfcache reuse, storage amplification, complete cognition/generation accepted-work throughput, model decomposition/export, competitor-equivalent accepted-work cost, and calibrated whole-system energy where available.

Those profiles should feed the same plan/execution receipts used by billing, estimator calibration, capacity planning, and the optimization/refactoring loop.
