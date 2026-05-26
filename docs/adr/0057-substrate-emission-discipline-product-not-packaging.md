# ADR 0057: Substrate emission discipline — product yes, packaging no (universal Food principle at the emission boundary)

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

Tracks planning issue #225.

## Context

The 2026-05-24 conversation surfaced the universal substrate posture toward source packaging vs source content: *"Taking the product out of the packaging and throwing away the packaging."*

This framing is partially captured already:

- [GLOSSARY Vampire mode](../../GLOSSARY.md) codifies the AI-model-specific instance: *"drain knowledge into attestations; discard the weight bytes entirely; synthesize fresh packaging on demand. Model files do NOT become substrate entities at the byte level... Round-trips emit fresh files from current substrate state, not copies."*
- [GLOSSARY Food principle](../../GLOSSARY.md) generalizes Vampire mode: *"any digital artifact — AI models (Vampire mode), document corpora, image archives, video collections, audio libraries, code repositories, scientific datasets, government open data, knowledge graphs, database exports, web archives, ANY structured or semi-structured data ecosystem — is food, not an artifact to preserve."*
- [RULES.md R4](../../RULES.md) sparse-by-construction emission specifies emission shape for AI models but not the universal emission discipline.

What's missing: **the explicit emission-boundary invariant that codifies what's bit-perfect emittable (PRODUCT) vs what's never preserved (PACKAGING)** across every source decomposer + every `IFormatWriter`. Without this, future decomposer / writer authors may quietly slip toward packaging-preservation patterns (e.g., "let's keep the original `data.noun` file alongside the substrate state so we can re-emit it identically"; "let's preserve the safetensors header byte layout so we can byte-compare round-trips"). Each such slip violates the Food principle at the architectural level while looking like a feature.

The 2026-05-24 conversation made the boundary concrete with specific examples for WordNet (product = synset+lemma+relation typed attestations; packaging = `data.noun` byte layout, lexicographer file format), Wiktionary (product = definitions+IPA+etymology; packaging = XML dump structure, MediaWiki templates), and AI models (product = recipe + tokenizer + typed mechanical-role attestations; packaging = safetensors header byte layout + weight bytes per Vampire mode). The pattern is identical across all sources. This ADR codifies it as a universal invariant.

## Decision

**Substrate emission preserves the PRODUCT (substrate-canonical content + typed attestations); the substrate NEVER preserves source PACKAGING. Substrate Synthesis output is FRESH synthesis from substrate state, not packaging-preserving re-export of any single source.**

### The universal product / packaging distinction

For every source the substrate ingests, the per-source `IDecomposer` per [ADR 0051](0051-idecomposer-csharp-plugin-contract.md) is responsible for separating:

- **Product** — typed knowledge content the substrate extracts as entities + physicalities + attestations. Bit-perfect emittable through trajectory walks per [ADR 0012 mantissa-packing](0012-mantissa-packing-format.md) (text content reconstructs exactly from its CONTENT physicality trajectory; typed-knowledge edges reconstruct exactly from the attestation rows).
- **Packaging** — source-format-specific byte layout, file structure, container metadata, framework-specific encoding conventions. Discarded after the decomposer extracts the product. Never preserved as substrate entities at the byte level.

Per-source examples (from the 2026-05-24 conversation):

| Source | Product (substrate-canonical; bit-perfect emittable) | Packaging (discarded after decomposition) |
|---|---|---|
| **Unicode UCD/UCA/UAX/Unihan/emoji** | Codepoint properties as typed attestations on Codepoint T0 entities (`HAS_GENERAL_CATEGORY`, `HAS_SCRIPT`, `HAS_UCA_PRIMARY_WEIGHT`, ...); the substrate-canonical CONTENT physicality coord per codepoint | UCDXML file structure, per-text-file UCD format, Unihan zip layout, emoji-test format |
| **ISO 639-3 / 15924 / 10646 / BCP-47 / CLDR / Glottolog** | Language / Script / Region / Currency entities + typed attestations (`HAS_ISO_639_1_CODE`, `BELONGS_TO_MACROLANGUAGE`, `HAS_LIKELY_SCRIPT`, ...) | per-registry CSV/JSON/XML formats, internal field separators, encoding conventions |
| **WordNet 3.0** | Synset content (gloss text, lemma text, example phrases) as Text entities; typed relations (`IS_HYPERNYM_OF`, `IS_MERONYM_OF`, `IS_ANTONYM_OF`, ...) | `data.noun` / `data.verb` / `data.adj` / `data.adv` byte layout, lexicographer file offsets, exception list format, sense index format, ILI mapping CSVs |
| **OMW** | Cross-lingual lemma↔synset mappings as `IS_LEMMA_OF` attestations with Language context; per-language Text lemma entities | per-language pack file structure, internal CSV/tab format, license headers |
| **UD treebanks** | Token text + lemma text + dependency relations + morph features as typed attestations on UD_Token/UD_Sentence entities | CoNLL-U file format, per-treebank metadata files, train/dev/test split conventions |
| **Wiktionary** | Definitions, etymologies, IPA pronunciations, inflection forms, translations, usage examples — all as Text entities with typed attestations | XML dump structure, MediaWiki template syntax, wiki-markup conventions |
| **Tatoeba** | Sentence text + per-sentence Language attestations + `IS_TRANSLATION_OF` pair attestations; audio recordings as Audio_Track entities | sentences.csv format, links.csv format, audio file directory hierarchy |
| **ConceptNet** | Concept text + ~30 typed relations + sub-source attribution attestations | URI scheme, JSON-LD wire encoding, per-sub-source format quirks |
| **Atomic2020** | Event text + ~25 commonsense relation attestations | TSV format |
| **TreeSitter grammars** | Grammar rules + highlight queries + keyword/operator vocabularies; parsed code structure when code is ingested | `grammar.js` / `src/parser.c` build artifacts, per-grammar repo layout |
| **AI models (per Vampire mode + ADR 0056 ETL)** | Recipe entity content (config + tokenizer + architecture metadata as text/JSON); tokenizer vocab as Text entities; typed mechanical-role attestations between substrate entities per [ADR 0056](0056-weight-tensor-etl-as-arena-matchup-observation.md) | safetensors header byte layout, shard file structure, **weight bytes** (Vampire-mode discarded), pickle bytestream, ZIP container offsets |
| **Image corpora** | Pixel entities + region entities + image-collection entities + typed attestations (CONTENT physicality trajectories per ADR 0012 reconstruct image bytes exactly from constituent pixels) | container format (PNG/JPEG/WebP/AVIF/HEIC/raw/etc.), per-format chunk layout, EXIF metadata wire format |
| **Audio corpora** | Audio sample entities + frame entities + track entities + typed attestations | container format (FLAC/WAV/MP3/Opus/etc.), per-format header structure, ID3 wire format |
| **Code repositories** | Token entities (via TreeSitter parse per `Code_Token`) + Code_Span / Code_File / Code_Repository entities + parse-tree attestations | repo-on-disk layout, per-file system metadata, encoding conventions |
| **Database exports** | Row content as Text/numeric entities + per-table-schema attestations | SQL dialect-specific dump format, vendor-specific encoding |
| **Web archives** | Page text content as Text entities + per-page HAS_LANGUAGE / HAS_URL / OCCURRED_ON_DATE attestations | WARC format, HTTP wire protocol, HTML tag structure (stripped per source pre-canonicalization) |

The pattern is identical across all sources: **the typed-knowledge-extractable content is product; the source-format-specific wrapper is packaging.**

### Emission discipline

When the substrate emits — whether via:

- **Substrate Synthesis** ([GLOSSARY Substrate Synthesis](../../GLOSSARY.md)) for AI model package generation per recipe
- **`just query`** for substrate-state SQL queries
- **`just cascade`** for compiled-cascade inference
- **CLI / endpoint plugin** for protocol responses
- **Future format converters** for cross-modality re-export

— the emission produces **fresh content** from substrate state. The fresh content carries:

1. **Substrate-canonical text reconstructed from CONTENT physicality trajectories** (per [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) + [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) + [ADR 0012 mantissa-packing](0012-mantissa-packing-format.md)) — bit-perfect to the original canonical text bytes (modulo equivalent encodings per [GLOSSARY Canonicalization](../../GLOSSARY.md)).
2. **Typed-knowledge edges** reconstructed from substrate attestations — bit-perfect to the substrate's accumulated typed-knowledge state.
3. **Synthesized packaging** that wraps the substrate state into whatever format the emission target requires (safetensors / GGUF / WordNet-data.noun-format / Wiktionary-XML-format / etc.). The packaging is synthesized fresh per the target's format spec; it is NOT preserved from any source.
4. **Cross-source enrichment, consensus rating, and deduplication baked in.** Per [GLOSSARY Substrate Synthesis](../../GLOSSARY.md): *"the synthesized output is superior to any single ingested source — deduplicated, consensus-rated, cross-source-enriched, modality-agnostic, composable."*

### What "bit-perfect emittable" means

- **Product is bit-perfect emittable at the observed-content level.** The text "the quick brown fox" reconstructs from its CONTENT physicality trajectory through T3 → T2 → T1 → T0, emitting the **observed** UTF-8 bytes exactly ([ADR 0047](0047-text-decomposer-pure-primitive.md) — no NFC rewrite at ingest). Walking the trajectory produces a bit-identical reconstruction of what was ingested.
- **Product is NOT bit-perfect emittable at the source-packaging level.** Reconstructing WordNet's exact `data.noun` byte layout from substrate state is not a substrate capability + not a substrate goal. The substrate could emit a *synthesized* WordNet-style file (using the substrate's accumulated synset / lemma / relation knowledge to populate WordNet's data-file format), but the emitted file would be a fresh synthesis — cross-source-enriched, possibly differently-ordered, with the substrate's accumulated knowledge from all sources that touched the same synsets/lemmas. NOT a byte-identical copy of the original `data.noun`.
- **AI models extend the same pattern via Vampire mode.** Weight bytes are not bit-perfect emittable (the substrate doesn't preserve them per ADR 0056). The substrate emits *synthesized* model packages per recipe; the emitted weights are derived from substrate attestations under the recipe's `knowledge_scope` per [ADR 0009 recipe extraction](0009-recipe-extraction-and-overrides.md) + sparse-by-construction emission per [R4](../../RULES.md). The emitted package is loadable by external runtimes (llama.cpp, etc. — per [GLOSSARY Round-trip](../../GLOSSARY.md)) but behaviorally aligned via codec fidelity per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md), not byte-identical to any source.

### Round-trip tests verify PRODUCT behavioral alignment, not packaging byte-equality

The Chunk 8 round-trip ([per the Justfile `just roundtrip`](../../Justfile) + Story 8.x): ingest model M → emit M' via Substrate Synthesis using M's own recipe → load M' in llama.cpp → chat. The verification gate is BEHAVIORAL: does M' produce comparable completions to M under fixed prompt + sampler? It is NOT byte-equality: M' is NOT a copy of M's weight bytes; M' is a fresh synthesis from substrate state.

Per [GLOSSARY Round-trip](../../GLOSSARY.md): *"M' is architecturally identical to M but weights are substrate-consensus, not M-original."*

Same shape applies for any future per-source round-trip tests:

- **WordNet round-trip** would verify the substrate emits a WordNet-shape output (typed-knowledge content reproducing original synset+lemma+relation structure) — NOT byte-identical to the original `data.noun`. Cross-source enrichment intentionally adds attestations from OMW + ConceptNet + Wiktionary that didn't exist in WordNet alone.
- **Wiktionary round-trip** would verify substrate emits Wiktionary-shape per-entry output (definition + IPA + etymology + translations + examples) — NOT byte-identical to the original XML dump.

Per-source round-trip tests are emission-side verifications of *substrate codec fidelity*, not preservation tests.

### Per-decomposer responsibility

Per [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md), each per-source decomposer is responsible for:

1. **Identifying the product / packaging boundary for its source.** Documented in the per-source decomposer's ADR (e.g., the WordNetDecomposer ADR specifies which fields in `data.noun` become substrate attestations vs which fields are discarded as packaging).
2. **Extracting the product as substrate state** (entities + physicalities + attestations) via the universal three-stage pipeline ([TextDecomposer / ADR 0047](0047-text-decomposer-pure-primitive.md) → [HashComposer / ADR 0048](0048-hash-composer-leaf-to-trunk.md) → [SubstrateCRUD / ADR 0050](0050-substrate-crud-write-surface.md) via SubstrateChange intent per [ADR 0049](0049-substrate-change-intent-type.md)).
3. **Discarding the packaging.** No "preserve original source files alongside substrate state" optimization is permitted. The substrate is not a backup of its sources.

Per `IFormatWriter` plugin (one per emission target):

1. **Synthesizing the target's packaging fresh** from substrate state. The writer knows the format spec; it constructs a conforming output using substrate-supplied content.
2. **Accepting cross-source enriched content** as input. The substrate's accumulated knowledge across all sources contributing to the requested scope is the input; the writer doesn't choose which source's content to use (the recipe + cross-source consensus per [ADR 0036](0036-arena-semantics-and-source-trust.md) does).

### Per-Justfile-target responsibility

- `just ingest <source>` runs the per-source decomposer per [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md). Output: substrate state. Source packaging discarded after.
- `just synthesize <recipe.json>` emits a fresh Substrate Synthesis package per recipe. Output: a fresh model package (safetensors + accompanying files). NOT a copy of any source.
- `just roundtrip <model_path>` per Chunk 8: ingest → synthesize → load externally → chat. Verifies codec fidelity behaviorally.
- `just query "<sql>"` returns substrate-state query results. The results are substrate state, not source packaging.

## Consequences

- **The Food principle is operationally enforced at the emission boundary**, not just declarative in GLOSSARY.
- **Storage discipline**: the substrate does NOT keep source files around after decomposition. Disk-resident `/vault/Data/` + `/vault/models/` are *inputs to ingest*, not substrate state. Once a source has been ingested + its contribution accumulated into substrate attestations, the substrate can answer queries about that source's content without re-reading the source files. (The user may keep the source files around for re-ingest / verification / auditing — but the substrate doesn't depend on them being there for query / cascade / synthesis.)
- **Per-source round-trip tests verify behavioral alignment, not byte equality.** Codec-fidelity tests follow the substrate-codec-fidelity framing per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md). No test should assert `cmp original_source_file emitted_source_file → identical`.
- **Cross-source-enriched output is the norm.** Emitting a WordNet-shape file from the substrate produces something *better* than the original WordNet (cross-source-enriched with OMW + ConceptNet + Wiktionary attestations on the same synsets). The architecture-template / recipe-scope mechanism controls which source attestations contribute to which emission.
- **The substrate becomes self-describing at the emission boundary**: the per-source decomposer's ADR documents what product it extracts and what packaging it discards. This becomes the substrate's per-source contract — testable, auditable.
- **AI model emissions inherit this discipline.** Vampire mode is a special case; the universal Food principle is the parent. The substrate's emission discipline for models (sparse-by-construction + cross-source consensus + recipe-driven) follows directly from the universal pattern.
- **`IFormatWriter` plugins focus on packaging synthesis**, not on content extraction. Content is substrate state; writers compose it into target formats. Same shape for safetensors / GGUF / WordNet-data.noun / Wiktionary-XML / future formats.
- **Source-file storage is a user concern, not a substrate concern.** Whether the user keeps `/vault/Data/Wordnet/WordNet-3.0/` around after ingest is up to them; the substrate operates identically with or without the source files.

## Alternatives considered

- **Preserve source files as substrate content entities alongside extracted attestations.** Rejected — violates the Food principle. Source files are packaging; preserving them inflates substrate state with format-specific encoding that adds no typed-knowledge value. Re-emission preserving byte-equality is not a substrate capability + not a substrate goal.
- **Bit-perfect round-trip tests** (assert emitted source files are byte-identical to original). Rejected — would require preserving packaging + would prevent cross-source enrichment from naturally affecting emission. The behavioral codec-fidelity framing per ADR 0037 is the correct shape.
- **Different emission discipline per source family.** Rejected — the Food principle is universal across modalities + source classes. Per-source variation is in *which fields become product* vs *which become packaging*, not in *whether packaging is preserved* (it isn't, ever).
- **Allow optional source-file preservation as a deployment-profile choice.** Rejected — even an opt-in preservation creates a parallel substrate-shape that has to be maintained alongside the canonical no-preservation one. One discipline; no optional shape.

## References

- [RULES R3](../../RULES.md) — lottery-ticket-aware sparsity (only product survives; noise discarded)
- [RULES R4](../../RULES.md) — sparse-by-construction emission (AI-model-specific instance)
- [RULES R6](../../RULES.md) — DB as dumb columnar store; substrate stores product
- [RULES R22](../../RULES.md) — use existing types (per-source packaging types are not substrate types)
- [STANDARDS Storage-class discipline](../../STANDARDS.md) — content / metadata / attestation / lookup / index five-way distinction
- [GLOSSARY Vampire mode](../../GLOSSARY.md) — AI-model-specific instance of Food principle
- [GLOSSARY Food principle](../../GLOSSARY.md) — universal ingestion posture
- [GLOSSARY Canonicalization](../../GLOSSARY.md) — lossy conversions are not equivalent under canonicalization
- [GLOSSARY Substrate Synthesis](../../GLOSSARY.md) — fresh synthesis from substrate state
- [GLOSSARY Round-trip](../../GLOSSARY.md) — codec-fidelity behavioral verification
- [DESIGN.md VII](../../DESIGN.md) — model-codec fidelity
- [DESIGN.md VIII](../../DESIGN.md) — recipe extraction + custom synthesis
- [ADR 0009 recipe extraction + overrides](0009-recipe-extraction-and-overrides.md)
- [ADR 0011 polymorphic plugin architecture](0011-polymorphic-plugin-architecture.md) — IFormatWriter plugin family
- [ADR 0012 mantissa packing](0012-mantissa-packing-format.md) — CONTENT physicality trajectory mechanics
- [ADR 0037 layered seed ingestion + model-codec fidelity](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — model codec fidelity framing
- [ADR 0041 decomposer scope = full domain ecosystem](0041-decomposer-scope-full-domain-ecosystem.md) — per-source decomposer responsibility
- [ADR 0043 composite decomposer architecture](0043-composite-decomposer-architecture.md) — ModelDecomposer composition
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md) — text content extraction
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) — content-addressing
- [ADR 0049 SubstrateChange intent](0049-substrate-change-intent-type.md)
- [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md) — substrate write surface
- [ADR 0051 IDecomposer](0051-idecomposer-csharp-plugin-contract.md) — per-source decomposer responsibility
- [ADR 0052 IngestRunner](0052-ingest-pipeline-orchestration.md) — orchestration
- [ADR 0055 static structural parse / exploded view](0055-static-structural-parse-exploded-view.md) — container packaging discarded after dissection
- [ADR 0056 weight-tensor ETL as arena-matchup observation](0056-weight-tensor-etl-as-arena-matchup-observation.md) — Vampire mode realized as universal ETL
- Tracking issue #225 — this ADR closes that tracking
- Conversation 2026-05-24: *"Taking the product out of the packaging and throwing away the packaging"* + *"we EXPORT those because we can"* + Food principle generalization beyond AI models
