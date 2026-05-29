# ADR 0041: Decomposer scope is the full domain ecosystem, not a single file

## Status

**Accepted** — 2026-05-23
**Amended same-day**: composite/parameterized decomposers are explicitly permitted (and expected for complex domains). See [ADR 0043](0043-composite-decomposer-architecture.md). The "one decomposer per domain" principle holds at the top level; internal structure can compose sub-decomposer plugins along orthogonal axes (container format / dtype / semantic architecture / modality binder, parameterized by edition / grammar / treebank / etc.).

## Context

Early issue framing for the per-source ingestion plugins (issues #183–#191 opened 2026-05-23) used "Source"-suffixed naming (UnicodeUCDSource, WordNetSource, ...) with acceptance criteria worded around single canonical files (`UnicodeData.txt`, `data.noun`, etc.).

This was wrong on two axes:

1. **Naming.** The plugin interface is `IDecomposer` (per [ADR 0011](0011-polymorphic-plugin-architecture.md)). The plugin decomposes input content into substrate entities + attestations + physicalities. Naming them `*Source` conflates the plugin role (Decomposer) with the source-entity that ATTESTS through them. `WordNetSource` could mean "the source entity named WordNet" or "the plugin that ingests WordNet" — the latter is the plugin role.

2. **Scope.** Each domain (Unicode, ISO, WordNet, OMW, UD, Wiktionary, Tatoeba, Atomic2020, ConceptNet, TreeSitter, AI models) ships as a *full data ecosystem*: structured primary files (XML, CoNLL-U, safetensors), supplementary text tables, auxiliary cross-reference data, validity registries, sub-source provenance. Lazy single-file decomposers ignore most of what the domain provides:

   - UCD without UCA → no semantically-meaningful codepoint ordering (super-Fibonacci input).
   - UCD without Unihan → no CJK radical / stroke / reading attestations.
   - UCD without emoji → no emoji presentation / modifier / ZWJ-sequence attestations.
   - ISO 639-3 SIL tables without CLDR validity → no current-status (deprecated / private-use / regular) info.
   - WordNet `data.noun` without senses + glosses → no semantic content for the synset entities.
   - UD-Treebanks single-treebank without the full release → no cross-language cross-treebank corroboration.

The richness of the substrate's attestation cloud on the entities a decomposer produces scales with the richness of the decomposer's ingest. Single-file decomposers cap that richness at the file's own information density, leaving structural cross-references on the floor.

## Decision

**Each `IDecomposer` plugin's scope IS its domain's full data ecosystem.** Not a file. Not a sample. Every authoritative + supplementary + cross-reference data set the domain ships is in scope, ingested in one decomposer-run, into the same substrate-state batch.

Naming convention: `<Domain>Decomposer` — `UnicodeDecomposer`, `ISODecomposer`, `WordNetDecomposer`, `OMWDecomposer`, `UDDecomposer`, `WiktionaryDecomposer`, `TatoebaDecomposer`, `Atomic2020Decomposer`, `ConceptNetDecomposer`, `TreeSitterDecomposer`, `TransformerModelDecomposer`, etc.

Each decomposer is responsible for:

1. **Parsing every authoritative file in its domain's ecosystem.** Primary structured format preferred (XML / CoNLL-U / safetensors / JSON), with text supplements where the structured format doesn't carry a property (rare for modern domains).
2. **Bootstrapping the type vocabulary entities its domain introduces.** Per [ADR 0040](0040-multi-modal-entity-types-universal-t0.md). UnicodeDecomposer bootstraps `Codepoint`, `Script`, `Block`, `BiDi_Class`, `Line_Break_Class`, etc. ISODecomposer bootstraps `Language`, `Region`, `Currency`, etc. (with `Script` shared across both — same hash space).
3. **Bootstrapping the attestation kind vocabulary its domain introduces.** Per [ADR 0036](0036-arena-semantics-and-source-trust.md). One kind entity per distinct relation type the decomposer emits.
4. **Emitting cross-references to prior-layer decomposers' entities.** Same hash space, content-addressed. UnicodeDecomposer's `Latn` Script entity is the same row ISODecomposer attaches ISO 15924 metadata to.
5. **Producing per-source `Physicality` rows** (CONTENT / BUILDING_BLOCK / PROJECTION as applicable) per [ADR 0039](0039-schema-reorganization-entity-identity-vs-physicality-representation.md).

Layer order from [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) governs decomposer-run order: each layer assumes prior layers' entities + types + kinds exist.

## Consequences

- **One decomposer per domain, not per file.** Adding new file-format support within a domain extends the decomposer, doesn't add a plugin.
- **Per-domain ecosystem inventories become first-class.** Each decomposer's spec must enumerate the data set it ingests (see the ADR 0037 amended table for current inventory + `/vault/Data/<domain>/` paths).
- **Cross-decomposer dependencies are declared.** Issue / story acceptance for any decomposer lists which prior decomposers' entities it references. Implementation order = dependency order.
- **Content-addressing compounds enrichment.** Same row, many decomposers piling on attestations. Querying any one row reveals the full substrate-wide knowledge cloud accumulated about that entity from every decomposer that touched it.
- **Single-file decomposer code is a smell.** A `UnicodeDecomposer` that parses only `UnicodeData.txt` and ignores UCA / Unihan / emoji / segmentation auxiliary is broken; same for `WordNetDecomposer` that parses only `data.noun`, etc.
- **Substrate trust regime depends on full-ecosystem ingest.** Source trust is a Glicko-2 value that self-tunes from cross-source agreement (per ADR 0036 and `docs/SUBSTRATE-FOUNDATION.md` truth 5) — never a fixed tier or trust class. Full-ecosystem ingest matters because it gives that self-tuning more independent observations to adjudicate against; partial ingest starves the consensus of corroborating signal and misrepresents how much of a source the substrate has actually seen.

## Alternatives considered

- **One plugin per file format.** Rejected — multiplies the plugin surface 10×, fragments domain knowledge across plugins, makes cross-reference resolution awkward.
- **One plugin per data source within a domain** (e.g., separate plugin per WordNet relation file). Rejected — same fragmentation.
- **Keep "Source" naming.** Rejected — overloaded with the "source entity" concept; conflates plugin role with attestation provenance.

## References

- [ADR 0011](0011-polymorphic-plugin-architecture.md) — `IDecomposer` interface
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered seed order (amended 2026-05-23 to use Decomposer naming + per-domain ecosystem inventory)
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — type + kind vocabulary
- [ADR 0036](0036-arena-semantics-and-source-trust.md) — arena semantics + source-trust policy
- [GLOSSARY.md](../../GLOSSARY.md) — "Decomposer", "Cross-decomposer dependency", "Per-decomposer ecosystems"
