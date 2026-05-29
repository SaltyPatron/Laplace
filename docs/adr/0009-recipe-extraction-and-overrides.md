# ADR 0009: Recipe extraction at model ingest; user JSON override for variants

## Status

**Accepted** — 2026-05-21

## Context

Substrate Synthesis emits a model in a specific architecture (Llama, Qwen, Mamba, etc.). The recipe is a **fillable mold** (per docs/SUBSTRATE-FOUNDATION.md, truth 6): synthesis pours substrate facts into a chosen shape (dim, dense/MoE, layers, vocab, dtype). The architecture template, dimensions, layer counts, etc. must come from somewhere. Two cases: (1) emission into the source model's own mold — same machinery refilling the original shape (use the source model's own architecture spec), (2) retarget emission into a different mold (user wants different shape).

## Decision

At model ingest, **auto-extract the source's config.json into a Recipe entity** with typed attestations (`HAS_HIDDEN_SIZE`, `HAS_NUM_LAYERS`, `HAS_NUM_HEADS`, `HAS_INTERMEDIATE_SIZE`, `HAS_VOCAB_SIZE`, `HAS_DTYPE`, etc.).

For emission:
- **Refill source mold (default)**: use the source model's own Recipe entity as the mold; synthesis pours the consensus of substrate facts into the original shape. This is not bit-perfect blob preservation (banned per SUBSTRATE-FOUNDATION.md truth 6) — the stored weights were dissolved to rated attestations at ingest; emission re-materializes their consensus into the recipe's tokens (truth 8).
- **Retarget mold**: user provides a recipe JSON that overrides any Recipe field, pouring the same substrate facts into a different shape.

Recipe JSON is itself a content record in the substrate (idempotent, hash-addressable).

## Consequences

- Refilling the source mold is the default mode — it exercises the full dissolve-and-re-materialize path (ingest → rated attestations → synthesis) on a known shape, not a blob round-trip (the term "codec" is banned per SUBSTRATE-FOUNDATION.md truth 10 — it implies round-trip preservation, which is worthless per truth 6).
- Retargeted variants are reproducible: same recipe JSON + same substrate state → identical emission.
- Recipe schema is the bridge between ingested-model config and emission mold.
- **OPEN per docs/SUBSTRATE-FOUNDATION.md:** the synthesis "pour facts into the mold" algorithm at frontier scale is unsolved, as is interior `d×d` tensor-axis → token-entity resolution. The recipe captures the *shape* to fill; how interior-tensor facts are materialized back into that shape without re-running the GEMM is not settled here and must be pinned with Anthony.

## References

- Memory: project_laplace_invention.md — "Recipe extraction" / "Custom recipe JSON" sections
- Issue #6 (TransformerModelSource), Issue #7 (Synthesis pipeline)
