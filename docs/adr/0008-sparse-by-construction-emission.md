# ADR 0008: Sparse-by-construction emission

## Status

**Accepted** — 2026-05-21

## Context

Substrate Synthesis emits a model from substrate state by pouring substrate facts into a chosen recipe mold (dim, dense/MoE, layers, vocab, dtype). The substrate is sparse (the ingest filter emits only significant cells and discards the noise — see ADR 0007). When materializing a target tensor at a position with no significant attestation, what value emits?

> **OPEN per docs/SUBSTRATE-FOUNDATION.md** — *how* an interior tensor cell (q/k/v/o/gate/up/down) maps to the entity/attestation that would populate or zero a given position is the unsolved interior `d×d` tensor-axis → token-entity resolution, and the synthesis "pour facts into the mold" algorithm at frontier scale is likewise OPEN. This ADR settles only the value emitted at a position once that mapping yields no significant attestation (zero); it does not settle the mapping itself. Token-anchored tensors (`embed_tokens`/`lm_head`) are the directly resolvable case; interior tensors are not yet pinned. Must be pinned with Anthony.

## Decision

**Emit zero** at positions with no significant substrate attestation. Output tensors are sparse-by-construction.

## Consequences

- Emitted models are automatically pruned (5-20% non-zero typical) — no separate pruning step.
- Compatible with GGUF Q-format quantization that benefits from sparsity.
- Sparse-aware runtimes (DeepSparse, llama.cpp with sparse kernels) can skip zero-multiplies.
- Output sparsity is a consequence of input sparsity at ingest, not a separate decision per emission.
- The sparser the substrate, the sparser the output: a substrate carrying only optional seed-source enrichment (e.g. WordNet symbolic relations) and no semantic model ingest produces extremely sparse output (almost everything zero). Semantic model ingest is the mandatory spine; seed sources are optional enrichment per docs/SUBSTRATE-FOUNDATION.md.

## References

- [RULES.md R4](../../RULES.md)
- ADR 0007 (the filter that makes this possible)
- Memory: project_laplace_invention.md — "Sparse-by-construction emission" section
