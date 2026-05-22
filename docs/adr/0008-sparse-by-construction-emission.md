# ADR 0008: Sparse-by-construction emission

## Status

**Accepted** — 2026-05-21

## Context

Substrate Synthesis emits a model from substrate state. The substrate is sparse (lottery-ticket-aware filter at ingest discarded the noise — see ADR 0007). When materializing a target tensor at a position with no significant attestation, what value emits?

## Decision

**Emit zero** at positions with no significant substrate attestation. Output tensors are sparse-by-construction.

## Consequences

- Emitted models are automatically pruned (5-20% non-zero typical) — no separate pruning step.
- Compatible with GGUF Q-format quantization that benefits from sparsity.
- Sparse-aware runtimes (DeepSparse, llama.cpp with sparse kernels) can skip zero-multiplies.
- Output sparsity is a consequence of input sparsity at ingest, not a separate decision per emission.
- Synthesis from a substrate where only WordNet has been ingested produces extremely sparse output (almost everything zero, just symbolic relations encoded).

## References

- [RULES.md R4](../../RULES.md)
- ADR 0007 (the filter that makes this possible)
- Memory: project_laplace_invention.md — "Sparse-by-construction emission" section
