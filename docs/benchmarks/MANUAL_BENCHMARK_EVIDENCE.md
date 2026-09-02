# Manual benchmark evidence lane

Tracking: `SaltyPatron/Laplace-Refactor#146`, `#165`

The workflow `.github/workflows/benchmark-evidence.yml` is deliberately `workflow_dispatch`-only. Performance measurements are evidence and calibration inputs; they are not source-push or pull-request gates and must not consume the measured host because code changed.

## Current benchmark surfaces

### Core composition

`scripts/bench-compose.py` measures the native core boundary only:

- UTF-8 input;
- Unicode/NFC handling;
- UAX #29 segmentation;
- content/Merkle identity construction;
- tier-tree composition;
- physical/glome placement performed by the core path;
- no PostgreSQL, COPY, network, or API serving work.

The harness is intentionally single-threaded and reports codepoints/s, a 4-characters-per-token BPE-equivalent comparison, and tier-tree nodes/s. This is a per-thread/core floor rather than a claim about whole-machine aggregate throughput.

Historical committed evidence from `0f8405938daf3ab2aa6c1b745823be9e991ce6e6` recorded 875 real documents / 67.9 MB / 67,899,577 codepoints with three runs within 0.4% at approximately 1,859,000 codepoints/s, 464,800 BPE-equivalent tokens/s, and 4,555,400 tier-tree nodes/s, single-threaded.

### Moby Dick bit-perfect roundtrip

`laplace roundtrip <file> [out]` measures the engine text decomposition/reconstruction path and proves the exported bytes with an independent `cmp` and SHA-256 check in the workflow.

Historical committed evidence from `adc161ef86676cc2d078146dc160a25e116092ea` recorded `/vault/Data/test-data/text/moby_dick.txt` at 1,240,979 codepoints, approximately 377 ms ingest and 64 ms export, byte-for-byte identical. This is distinct from the heavier historical database-backed record/reconstruct benchmark in `062d48db8cea97380ffb4ebf7d4a81945763e223` (1.8 s record + 1.3 s reconstruction on 1,256,545 bytes).

## Evidence artifact

Each dispatch uploads a run/attempt-specific artifact containing, when available:

- exact requested and resolved Git revision;
- build log;
- host/runner identity;
- `lscpu`, memory, block-device and filesystem inventory;
- `inxi -Fxz` inventory;
- CPU governor state;
- core shared-library digest and `ldd` linkage;
- NVIDIA state before/after so a physically installed accelerator is not confused with a benchmark that used it;
- Intel RAPL energy counters scoped immediately before/after each measured core/Moby command when exposed by the kernel;
- raw benchmark output;
- Moby input/output byte counts and SHA-256 values;
- an artifact manifest and file hashes.

A 1200 W PSU nameplate is **not** a measured power draw and must never be used as one. Whole-system wall power requires a wall/PDU/UPS meter or another independently calibrated source. RAPL is useful package/domain energy evidence but is not a substitute for whole-system wall energy.

## Isolation law

The workflow shares the `laplace-shared-workspace` concurrency group with the main delivery workflow. It therefore cannot overlap another workflow using that same group on the measured repository workspace. The core benchmark additionally contains its own live-ingest refusal logic because a busy substrate can consume CPU even when the benchmark itself performs no database work.

The workflow builds the exact selected revision but does not install/deploy it, migrate the database, seed data, or publish services.

## Forward path

The refactor must replace legacy log-shaped evidence with typed immutable execution/resource receipts while preserving these useful historical benchmarks. Planned work includes:

- whole-machine scaling curves (1, 2, 3, 4, 6 physical-core workers, then SMT points where justified);
- exact CPU affinity/topology receipts;
- full query/cognition plan-vs-actual telemetry;
- whole-system wall-energy integration when a meter is available;
- storage/data/index/perfcache footprint receipts;
- accepted-work/quality-normalized competitor-equivalent cost reports;
- billing estimator calibration from the same measurements.

Do not infer an aggregate 12-thread result by multiplying a single-thread number. Aggregate throughput must be measured under an explicit workload and topology receipt.
