# ADR 0040: Multi-modal entity types, universal T0, semantic Merkle DAG with lossless canonicalization

## Status

**Accepted** — 2026-05-23

## Context

The substrate must accommodate every digital modality — text, code, pixels, patches, regions, images, audio frames/tracks, AI model knowledge, structured linguistic data (WordNet, OMW, UD, Wiktionary, Tatoeba, ConceptNet, Atomic2020) — through the same three core tables (`entities`, `physicalities`, `attestations`) without modality-flavored schema, without per-modality storage partitions, without inventing private hash-mechanisms.

Key design observations from the working sessions:

1. **Every entity is content-addressed by canonical content bytes.** The hash is BLAKE3-128 of *canonical* content, not of raw input bytes. Two different encodings that decode to the same canonical content yield the same entity ID. Two lossy encodings of "the same source" are *different entities* — they have different canonical bytes after lossy decode.
2. **Universal T0**: every modality's tier ladder bottoms at Unicode codepoints. Pixel color values, audio sample magnitudes, model-weight scalars, syntactic dependency labels — every constituent eventually decomposes to substrate entities whose bottom-tier is the universal codepoint alphabet (1,114,112 atoms pre-seeded from UCD).
3. **Tiers are type-specific above T0**: text has UAX#29 graphemes / word-forms / sentences / paragraphs / sections / documents; visual has pixel / patch / region / image / collection; audio has sample / frame / track; structured-data has source-specific schemas (synset, sentence, treebank, etc.).
4. **Semantic Merkle DAG, not bit-wise**: content-addressing is meaningful at the *semantic* layer per type. The Merkle property holds: a parent entity's hash uniquely identifies its content, and that content is reproducible by walking the parent's CONTENT physicality trajectory.
5. **AI models are not special**: tokens are text entities (deduping across all models that tokenize the same text); model knowledge is typed attestations between substrate entities, of fixed-vocabulary kinds. No model-flavored tables; no per-(layer, head) synthetic entity IDs.

## Decision

### Entities carry a `type_id`

`entities.type_id` (a FK to another entity declaring the type) names what *kind* of thing the entity is. Type drives:

- **Canonicalization rule** — how raw input becomes the canonical bytes that get hashed. Different per type. Must be **lossless**.
- **Decomposition rule** — which IDecomposer plugin breaks this entity into lower-tier constituents and produces the CONTENT physicality trajectory.
- **Reconstruction rule** — how to emit bytes back from a trajectory walk.
- **Modality-applicable attestation kinds** — which typed transforms apply (e.g., `EXTRACTS_R_CHANNEL` for pixel-type entities; `HAS_POS` for text-type entities).

Type entities are themselves rows in `entities`, bootstrapped at install. Initial type vocabulary:

- **Universal**: `Codepoint` (T0).
- **Text**: `Text`, `Grapheme`, `Word_Form`, `Sentence`, `Paragraph`, `Section`, `Document`, `Corpus`.
- **Code**: `Code_Token`, `Code_Span`, `Code_File`, `Code_Repository` (tree-sitter-driven).
- **Visual**: `Pixel`, `Patch`, `Region`, `Image`, `Image_Collection`.
- **Audio**: `Audio_Sample`, `Audio_Frame`, `Audio_Track`.
- **AI model**: `Model_Recipe`, `Model_Tokenizer`, `Model_Architecture` (and the substrate-canonical tensor-calculation-kind entities — see below).
- **Structured linguistic**: `WordNet_Synset`, `UD_Sentence`, `UD_Token`, `Wiktionary_Entry`, `Tatoeba_Sentence`, `Atomic2020_Event`, `ConceptNet_Concept`.

Adding a new modality = adding new type entities + registering an IDecomposer plugin for them. Schema unchanged.

### Universal T0 — codepoints under every tier ladder

T0 atoms are Unicode codepoints, universally. A pixel's color triple `(255, 0, 0)` canonicalizes to a structured byte form whose tier-ladder walk eventually bottoms at codepoint atoms (the digits and punctuation that express the value, or whatever the canonical byte representation expresses). Model-weight scalars, audio samples, dependency labels — all bottom at codepoints via type-specific intermediate tiers.

This guarantees **cross-modal reachability**: every entity at every tier is reachable from every other entity through their shared T0 descendants. Attestations across modalities are first-class because their endpoints live in the same hash space.

### Canonicalization is lossless per type

Each entity type defines a lossless canonical form. The hash is BLAKE3-128 of that canonical form.

- Different encodings that round-trip to the same canonical form → same entity ID. UTF-8 vs UTF-16 of the same codepoint sequence; PNG vs lossless WebP of the same pixel grid; FLAC vs WAV of the same PCM samples.
- Lossy encodings produce *different entities*. A JPEG of an image is a different entity from the PNG it was derived from (different pixel values after decode). An MP3 of a FLAC is a different entity. Lossy-to-lossless relationships are captured as attestations (`IS_LOSSY_ENCODING_OF`), not identity collapse.
- For AI models per Vampire mode: model files are not entities at all. Only the recipe + tokenizer + typed-tensor-calculation attestations remain.

### Cross-format and cross-modal equivalence is attestation, not identity

Same color expressed in RGB vs HSV vs CMYK → different entities (different canonical types, different canonical bytes). Cross-format equivalence is captured by attestations like `SAME_COLOR_AS`. Cross-modal links (`DEPICTS`, `CAPTIONS`, `TRANSCRIBES_AS`, `IS_LOSSY_ENCODING_OF`) are first-class typed attestations.

### Attestation kinds are small fixed vocabularies per modality / per architecture family

Kind entities are content-addressed by their **canonical names** (e.g., `BLAKE3("substrate/kind/Q_PROJECTS/v1")` — a canonical-name string, not a metadata tuple). They are **parameter-free** at the kind level: per-position attribution (layer, head, position, etc.) lives as meta-attestations or context, never as kind-name parameters that explode the kind-entity space.

Fixed vocabularies bootstrapped at install:

- **Modality-agnostic**: `IS_A`, `HAS_PART`, `CO_OCCURS_WITH`, `FOLLOWS`, `PRECEDES`, `OCCURS_IN_CONTEXT`.
- **Text**: `HAS_POS`, `HAS_LEMMA`, `IS_HYPERNYM_OF`, `IS_LEMMA_OF`, `IS_TRANSLATION_OF`, ... (per linguistic-source taxonomies).
- **Visual**: `EXTRACTS_R_CHANNEL`, `EXTRACTS_G_CHANNEL`, `EXTRACTS_B_CHANNEL`, `ADJACENT_TO_PIXEL`, `INDICATES_HUE`, ...
- **Audio**: `IS_AT_SAMPLE`, `HAS_FREQUENCY_PEAK`, ...
- **AI tensor-calculation kinds** (transformer family, ~10): `EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`. Other architecture families have their own fixed lists.
- **Cross-modal**: `DEPICTS`, `CAPTIONS`, `TRANSCRIBES_AS`, `IS_LOSSY_ENCODING_OF`.

### Attestation tuple shapes are a small fixed enumeration

One universal `attestations` table accommodates the fixed shape list determined by which slots of `(subject, kind, object, source, context)` populate plus the cardinality semantics of each kind: binary, binary-contextual, unary, unary-valued (rating-as-scalar), n-ary (via tuple-entity in object or context slot), meta (attestation-as-subject; attestations are entities with content-addressed `id`s). Adding kinds = choosing which shapes apply; never adding columns.

### AI model ingestion uses the same primitives — tokens are text, sequences are content, calculations are typed attestations

- Tokenizer ingestion: each vocab text → text entity (dedup; canonicalization strips BPE/SentencePiece/WordPiece markers like `Ġ`, `▁`, `##` so subword tokens dedup to their underlying text content). Tokenizer entity carries marker attestations describing how to re-mark text at emission.
- Probe input/output sequences → CONTENT physicalities on sequence entities, mantissa-packed trajectories through token references (same primitive prompts ride on per R19).
- Tensor calculations → typed attestations between substrate text entities, of the fixed-vocabulary tensor-calculation kinds, sourced to the model, lottery-ticket-sparse load-bearing entries only.
- Recipe metadata → ordinary attestations on the model entity (`HAS_NUM_LAYERS`, etc.).
- Per-position attribution (layer, head, per-tensor token vocabulary) → **recipe content** on the model's recipe entity (text/JSON describing the model's structure); never per-attestation metadata, never per-position synthetic entity IDs. The architecture template (`IArchitectureTemplate`) consumes recipe + aggregated typed attestations to wire emit-time tensor slots; substrate-side storage of position attribution would be redundant.

The substrate doesn't know or care that a `Q_PROJECTS` attestation came from a transformer rather than from text co-occurrence statistics — typed kind + rated edge + sourced + arena-scoped. Same primitive.

## Consequences

- **One schema for all modalities**, all sources, all data classes (app / substrate / user — per GLOSSARY "Data Class"). No special tables. No model-flavored columns.
- **Adding a new modality = adding type entities + an IDecomposer plugin**. No schema changes. No new attestation columns.
- **Cross-modal queries work natively** through the typed attestation graph because endpoints live in the same hash space (universal T0 + shared kind vocabularies).
- **Lossless dedup at corpus scale**: same canonical content → one row, regardless of how many sources/encodings/modalities arrived at it.
- **No synthetic entity IDs**. All IDs are BLAKE3 of canonical *content* (or canonical name for type/kind entities). Hash-concatenation of arbitrary metadata to manufacture entity IDs is explicitly forbidden.
- **Bootstrap responsibility**: install seeds T0 codepoint entities (UCD), Type entities (universal vocabulary), Kind entities (fixed vocabularies per modality / per architecture), substrate-canonical source entity, physicality-kind enum entities. Per ADR 0006 perfcache + DB seed sibling rule.
- **Type-taxonomy agent** owns the fixed-vocabulary extension process — adding a new kind requires (a) defining canonical name + arena semantics, (b) declaring source-trust policy, (c) registering as entity via substrate-canonical source. Documented vocabulary; controlled extension.

## Alternatives considered

- **Per-modality private hash mechanism** (e.g., images get an image-specific perceptual hash; audio gets an audio fingerprint). Rejected — fragments the substrate's identity space; defeats dedup; conflicts with content-addressing discipline.
- **Per-modality private tables**. Rejected — defeats cross-modal attestation; multiplies schema; violates R10 polymorphic plugin architecture.
- **Encode modality into entity ID** (e.g., prefix bytes for modality). Rejected — entity IDs are BLAKE3 of canonical content; no prefix bytes; modality lives in `type_id`, not in the ID.
- **Encode (layer, head, position) into AI-attestation kind IDs**. Rejected — exactly the synthetic-ID anti-pattern; explodes the kind vocabulary into a per-(layer, head) combinatorial; conflicts with content-addressing.

## References

- [GLOSSARY.md](../../GLOSSARY.md) — "Entity", "Entity Type", "Universal T0", "Canonicalization", "Attestation Kind", "Attestation Tuple Shape", "Per-source decomposition"
- [STANDARDS.md](../../STANDARDS.md) — "ID discipline", "Canonicalization discipline", "Attestation kind discipline"
- [DESIGN.md](../../DESIGN.md) §I (schema with type_id), §I.A (semantic Merkle DAG), §IX (three-phase architecture with bootstrap)
- [ADR 0012](0012-mantissa-packing-format.md) — mantissa-pack uniform across modalities
- [ADR 0015](0015-blake3-for-entity-hashing.md) — BLAKE3-128 entity ID, raw bytes
- [ADR 0036](0036-arena-semantics-and-source-trust.md) — arena semantics for typed kinds
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered seed ingestion order
- [ADR 0039](0039-schema-reorganization-entity-identity-vs-physicality-representation.md) — entity = identity, physicality = representation
