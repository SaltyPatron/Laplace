# ADR 0042: Bootstrap order + substrate-canonical seeding

## Status

**Accepted** — 2026-05-23

## Context

The substrate's schema (per [ADR 0039](0039-schema-reorganization-entity-identity-vs-physicality-representation.md)) requires that `entities.type_id` is a FK to another entity (the type entity) and that `attestations.kind_id` is a FK to another entity (the kind entity). Decomposers (per [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md)) reference these type + kind entities when emitting rows.

This creates a chicken-and-egg: a decomposer can't emit an entity until its `type_id` exists; the type entity is itself an entity with its own `type_id` (the "Type" meta-type); the attestation kinds the decomposer emits need their kind entities to exist first; the substrate-canonical source entity (which every substrate-canonical attestation cites in `source_id`) needs to exist before any decomposer emits an attestation.

Without a documented bootstrap order + deterministic seeding, the substrate can't reach a usable initial state.

## Decision

A fixed install-time **bootstrap sequence** runs before any decomposer. The sequence is deterministic — same install on same substrate + Unicode version produces byte-identical bootstrap rows.

### Bootstrap stages

**Stage 0 — meta-types (the self-referential root):**

A small fixed set of type entities that describe themselves and each other. Each is content-addressed by BLAKE3-128 of a canonical name string (e.g., `BLAKE3("substrate/type/Type/v1")` for the `Type` meta-type). These rows have `type_id = <Type>` (self-referential for `Type`; FK to `<Type>` for the others).

Stage-0 set: `Type` (self-typed), `Kind`, `PhysicalityKind`, `Source`, `SubstrateCanonical`.

**Stage 1 — substrate-canonical Source entity:**

`<SubstrateCanonical>` is content-addressed at install (name encodes the substrate version). It's the `source_id` of every attestation emitted during bootstrap + by the substrate-canonical layers of UnicodeDecomposer / ISODecomposer / etc.

**Stage 2 — physicality-kind enum entities:**

Three rows of `type_id = <PhysicalityKind>`: `CONTENT`, `BUILDING_BLOCK`, `PROJECTION` (extensible). The `physicalities.kind smallint` column values alias to these entities; the entities exist so per-kind meta-attestations can attach.

**Stage 3 — substrate-canonical attestation-kind vocabulary (modality-agnostic) + kind-value tiers:**

Modality-agnostic kinds: `IS_A`, `HAS_PART`, `CO_OCCURS_WITH`, `FOLLOWS`, `PRECEDES`, `OCCURS_IN_CONTEXT`, `HAS_LANGUAGE`, `IS_TRANSLATION_OF`, `DEPICTS`, `CAPTIONS`, `TRANSCRIBES_AS`, `IS_LOSSY_ENCODING_OF`, `HAS_VARIANT_OF`, `IS_REPLACED_BY`, `HAS_TRUST_CLASS`, `HAS_KIND_VALUE_TIER`, `IS_ALIAS_OF`, ...

Per-modality kinds (`HAS_GENERAL_CATEGORY`, `HAS_VALIDITY`, `IS_HYPERNYM_OF`, `Q_PROJECTS`, ...) are bootstrapped by their owning decomposer at first run, not at install.

Plus the **11 kind-value-tier entities** per [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md) (T1 Mandate / T2 Standards Structural / T3 Taxonomic / T4 Partitive / T5 Causal / T6 Equivalence / T7 Oppositional / T8 Associative / T9 Tensor-Calculation / T10 Scalar-Valued / T11 Probationary). Each tier entity carries the Glicko-2 prior (rating, RD, volatility) + cascade-weight multiplier as meta-attestations.

**Stage 3.5 — source-trust-class taxonomy (10-tier hierarchy):**

Per [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md), bootstrap seeds the 10 trust-class entities:

1. `TrustClass_SubstrateMandate`
2. `TrustClass_StandardsDerived`
3. `TrustClass_AcademicCurated`
4. `TrustClass_AcademicCuratedWithUserInput`
5. `TrustClass_StructuredCorpus`
6. `TrustClass_UserCuratedResource`
7. `TrustClass_AIModelProbe`
8. `TrustClass_AppDerived`
9. `TrustClass_UserPromptContent`
10. `TrustClass_AdversarialUntrusted`

Each entity carries meta-attestations: `HAS_PRIOR_WEIGHT`, `HAS_EFF_MU_MULTIPLIER`, `HAS_ARENA_ADMITTANCE_POLICY`, `HAS_RETENTION_POLICY`. The substrate-canonical source entity gets `HAS_TRUST_CLASS → TrustClass_SubstrateMandate` immediately on bootstrap.

Plus initial **cross-decomposer kind-reconciliation aliases** (per ADR 0044 Part C) — e.g., `IS_ALIAS_OF` meta-attestations linking ConceptNet's `IsA` to canonical `IS_A`, OMW's per-language `IS_LEMMA_OF` variants to the canonical kind, etc.

**Stage 4 — substrate-canonical Entity Type vocabulary:**

The fixed set of universally-applicable type entities — bootstrapped at install so decomposers can reference them:

- `Codepoint` (T0 atomic alphabet)
- `Text` (text content; T1–T6+)
- `Pixel`, `Patch`, `Region`, `Image`, `Image_Collection`
- `Audio_Sample`, `Audio_Frame`, `Audio_Track`
- `Language`, `Script`, `Region` (geopolitical), `Currency`, `Variant`, `Subdivision`, `Unit`
- `Code_Token`, `Code_Span`, `Code_File`, `Code_Repository`, `Programming_Language`
- `Model_Recipe`, `Model_Tokenizer`, `Model_Architecture`

Domain-specific types (`WordNet_Synset`, `UD_Sentence`, `Wiktionary_Entry`, ...) are bootstrapped by their decomposer at first run.

**Stage 5 — perf-cache sibling artifact + DB seed for T0:**

Per [ADR 0006](0006-perfcache-and-db-seed-siblings.md), the perf-cache binary + T0 codepoint entities + their substrate-canonical CONTENT physicalities are derived independently from UCD + UCA. Both are products of `UnicodeDecomposer`'s install-time run; both bottom at the same Unicode UCD version. Byte-identical across machines for the same Unicode version.

**Stage 6+ — layered decomposer runs:**

Each decomposer per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) layer order, on-demand (admin command — `just ingest-decomposer <name>` or similar). Each run is idempotent (re-ingestion of already-observed content is a no-op; new content adds).

### Deterministic seeding

- Stage 0–4 type/kind/source entities have stable content-addressed IDs derived from canonical name strings (no random surrogates, no synthetic-ID concatenation). Same string → same BLAKE3 → same ID across every install.
- Stage 5 perf-cache + T0 seed are byte-identical for the same Unicode UCD + UCA version (per ADR 0006 + ADR 0007 determinism rules).
- Cross-machine determinism is verifiable: hash the substrate's bootstrap-state snapshot post-Stage-5; two installs of the same substrate + Unicode version must produce the same hash.

## Consequences

- The substrate has a fixed install procedure that produces a usable initial state with type vocabulary + modality-agnostic kind vocabulary + T0 atoms + perf-cache.
- Decomposers can assume the type entities they reference + the modality-agnostic kind entities exist; they only need to bootstrap their own domain-specific extensions.
- The chicken-and-egg between `entities.type_id` and the Type meta-type is resolved by content-addressed self-reference.
- Install determinism is provable.
- Adding a new built-in type / modality-agnostic kind requires a substrate version bump (changes the bootstrap hash); decomposer-introduced types/kinds don't bump the substrate version.

## Alternatives considered

- **Surrogate sequential IDs for bootstrap rows.** Rejected — violates STANDARDS ID discipline (content-addressed only).
- **Skip bootstrap; let decomposers self-bootstrap on-demand.** Rejected — first-decomposer-wins races; non-deterministic installs.
- **Hardcode bootstrap IDs as constants in C.** Rejected — IDs ARE the BLAKE3 of canonical names; computing from name is the only correct approach.

## References

- [ADR 0006](0006-perfcache-and-db-seed-siblings.md), [ADR 0015](0015-blake3-for-entity-hashing.md), [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md), [ADR 0039](0039-schema-reorganization-entity-identity-vs-physicality-representation.md), [ADR 0040](0040-multi-modal-entity-types-universal-t0.md), [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md)
- [GLOSSARY.md](../../GLOSSARY.md), [STANDARDS.md](../../STANDARDS.md)
