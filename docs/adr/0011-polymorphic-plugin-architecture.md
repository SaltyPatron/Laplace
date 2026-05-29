# ADR 0011: Polymorphic plugin architecture — six interfaces

## Status

**Accepted** — 2026-05-21

## Context

Adding a new source type / modality / target architecture / output format / endpoint protocol should touch ONE place, not the whole codebase. Naive designs require touching schema + query layer + synthesis + endpoint for each new modality. That's untenable.

## Decision

Six plugin interfaces; each new capability is one new implementation:

- `ISource` — adding a new source type (linguistic resource, AI model, corpus, ...)
- `IDecomposer` — adding a new modality (text, code, image, audio, video, ...)
- `IArchitectureTemplate` — adding a new target model architecture
- `IFormatWriter` — adding a new emission format (safetensors, GGUF, ONNX, ...)
- `IFeatureExtractor` — adding a new source-attested contribution to the morph onto the canonical S³ embedding frame (NOT a new orthogonal per-model embedding axis; the S³ glome is the single shared frame every source is morphed into per docs/SUBSTRATE-FOUNDATION.md truth 3)
- `IProtocolEndpoint` — adding a new served-API protocol (OpenAI-compat, Anthropic, ...)

Adding a new entity in any category = one new class implementing the interface; no schema changes, no query-layer changes.

## Consequences

- Codebase stays maintainable as capability surface grows.
- New modalities, architectures, sources are bounded engineering tasks.
- Substrate's storage layer never has modality- or architecture-specific fields.

## References

- [RULES.md R10](../../RULES.md)
- Memory: project_laplace_performance.md — "Reusability + plugin architecture" section
