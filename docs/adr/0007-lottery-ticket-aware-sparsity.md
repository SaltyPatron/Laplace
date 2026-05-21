# ADR 0007: Lottery-ticket-aware sparsity, NEVER flat thresholds

## Status

**Accepted** — 2026-05-21

## Context

AI model ingestion extracts attestations from observed weight + activation patterns. Most weights are noise (gradient jitter, init residue, training artifacts) — only the lottery-ticket subnetwork is load-bearing.

We must filter the noise. A flat numeric threshold (e.g., `|w| < 0.001`) is the naive approach. But: different tensors have different magnitude regimes; small weights are sometimes load-bearing; large weights are sometimes noise. Flat thresholds destroy content.

## Decision

Multi-pass relative filter — never a flat threshold:

1. **Per-tensor relative top-k%** — rank within each tensor; keep top k% by importance.
2. **Per-row top-k** for attention/MLP — preserve load-bearing IO connectivity, not just absolute magnitude.
3. **Probe-validated retention** — synthesize candidate sparse subgraph; verify behavior preserved on probe set.

Combined gate; all three must pass.

## Consequences

- Substrate captures the lottery-ticket subnetwork — the actual semantic content of an ingested model.
- No flat numeric cutoff lives anywhere in the ingestion code path.
- Per-architecture tuning is via parameters of the multi-pass filter (k%, per-row k, probe set), never via swapping the algorithm for a simpler one.
- **Linguistic resources are exempt** — every WordNet entry is curated; the filter does NOT apply to pre-structured sources.

## References

- [RULES.md R3](../../RULES.md)
- Memory: project_laplace_invention.md — "Lottery ticket + sparse recording" section
