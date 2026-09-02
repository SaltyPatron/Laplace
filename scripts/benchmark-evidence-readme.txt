This file intentionally contains no executable benchmark logic.

The dispatch-only benchmark evidence lane is defined by:
  .github/workflows/benchmark-evidence.yml
  docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md

Do not invent an aggregate throughput number by multiplying the single-thread
bench-compose result. Whole-machine/core-scaling must be implemented as a measured
benchmark with explicit topology/affinity and preserved receipts.
