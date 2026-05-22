# ADR 0009: Recipe extraction at model ingest; user JSON override for variants

## Status

**Accepted** — 2026-05-21

## Context

Substrate Synthesis emits a model in a specific architecture (Llama, Qwen, Mamba, etc.). The architecture template, dimensions, layer counts, etc. must come from somewhere. Two cases: (1) round-trip emission of an ingested model (use the source model's own architecture spec), (2) custom emission (user wants different shape).

## Decision

At model ingest, **auto-extract the source's config.json into a Recipe entity** with typed attestations (`HAS_HIDDEN_SIZE`, `HAS_NUM_LAYERS`, `HAS_NUM_HEADS`, `HAS_INTERMEDIATE_SIZE`, `HAS_VOCAB_SIZE`, `HAS_DTYPE`, etc.).

For emission:
- **Default round-trip**: use the source model's own Recipe entity as template.
- **Custom variant**: user provides a recipe JSON that overrides any Recipe field.

Recipe JSON is itself a content record in the substrate (idempotent, hash-addressable).

## Consequences

- Round-trip is the default mode — proves the codec.
- Custom variants are reproducible: same recipe JSON + same substrate state → identical emission.
- Recipe schema is the bridge between ingested-model config and emission target.

## References

- Memory: project_laplace_invention.md — "Recipe extraction" / "Custom recipe JSON" sections
- Issue #6 (TransformerModelSource), Issue #7 (Synthesis pipeline)
