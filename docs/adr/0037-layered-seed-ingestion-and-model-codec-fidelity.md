# ADR 0037: Layered seed ingestion and model-codec fidelity

## Status

**Accepted** — 2026-05-22

## Context

Laplace is not seeded by an ontology plus one model. The seed plan is layered: Unicode and language standards establish atoms and language identity; lexical resources establish senses/POS; multilingual resources cross-link languages; treebanks and dictionaries add usage/grammar; sentence/audio corpora add parallel utterances and speech; commonsense resources add event/causal structure; tree-sitter/code corpora add code modality; AI models arrive as later evidence sources whose computations are recorded as physicalities and attestations.

This changes what "fidelity" means. For a source-scoped model round-trip, fidelity means the AI-model ingestion codec captures the model's load-bearing computation. For broader substrate synthesis, fidelity means the substrate preserves and materializes cross-source consensus structure.

## Decision

Canonical early ingestion order:

1. Unicode / UCD / UCA / UAX — T0 atoms, collation, normalization, scripts, categories, segmentation
2. ISO / CLDR / Glottolog-style language registries — language identity, script/region mappings, names/aliases
3. WordNet — POS, lemmas, synsets, senses, lexical relations, hypernyms
4. OMW — cross-lingual WordNet mappings and omniglottal sense bridges
5. UD Treebanks — observed sentences with POS, morphology, dependency relations, lemmas
6. Wiktionary — definitions, forms, pronunciations, etymology, POS, senses, examples
7. Tatoeba — multilingual aligned sentences and audio samples
8. ConceptNet / Atomic2020 — commonsense, causal, social, and event relations
9. Tree-sitter grammars / code corpora — parseable programming-language structure
10. Text/audio/image/model sources — high-volume observations, model recipes, physicalities, and behavioral attestations

AI model ingestion is a codec, not a conventional distillation/training step. It records recipe metadata, tokenizer content, physicalities, probe observations, architecture-specific attestation arenas, and lottery-ticket sparse load-bearing structure. If `TransformerModelSource` captures the source model faithfully and synthesis uses the source recipe/scope, the emitted model should land in the source model's behavioral basin. Differences should come from intentional sparsity, sampler settings, or broader substrate consensus scope — not accidental missingness.

The v0.1 proof can be narrow and still decisive: Unicode-derived T0 + one Qwen-family source model + recipe extraction + sparse attestations + GGUF emission + chat verification.

## Consequences

- Seed resources supply explicit fidelity channels that models normally carry implicitly: character/script fidelity, lexical fidelity, cross-lingual fidelity, syntactic fidelity, usage fidelity, audio fidelity, code fidelity, commonsense fidelity, and model-behavior fidelity.
- Later model-derived claims are measured inside an already constrained substrate instead of seeding meaning into an empty database.
- Source-scoped round-trip tests should compare stock source model, native substrate traversal, and synthesized export under fixed prompt/sampler settings.
- Broader synthesis can intentionally improve or alter behavior by changing source scope, trust policy, recipe, feature extractors, and sparsity.

## Alternatives considered

- **Model-first seeding.** Rejected — leaves language/script/sense structure implicit inside model behavior.
- **Ontology-only seeding.** Rejected — lacks sequence pressure, usage, audio, code structure, and model behavioral evidence.
- **Treating model weights as authoritative artifacts.** Rejected — models are sources; substrate state is the artifact.

## References

- [RULES.md R3](../../RULES.md) — lottery-ticket-aware sparsity
- [RULES.md R4](../../RULES.md) — sparse-by-construction emission
- [RULES.md R21](../../RULES.md) — layered seed ingestion and model-codec fidelity
- [DESIGN.md](../../DESIGN.md) — seed source order and model codec fidelity
- [OPERATIONS.md](../../OPERATIONS.md) — round-trip and comparison workflow
