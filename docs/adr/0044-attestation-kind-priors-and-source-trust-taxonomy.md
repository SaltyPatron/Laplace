# ADR 0044: Attestation-kind priors + source-trust-class taxonomy

## Status

**Accepted** — 2026-05-23

## Context

[ADR 0036](0036-arena-semantics-and-source-trust.md) established that arena semantics + source trust must be first-class concerns in the substrate's effective-μ computation. But the schema-reorg work (ADRs 0039/0040/0041) advanced without addressing two foundational substrate-canonical questions:

1. **Per-attestation-kind defaults aren't uniform.** A single global Glicko-2 prior (rating=1500, RD=350, volatility=0.06) is wrong for the substrate's actual kinds — `IS_A` (taxonomic / hard-logical) and `CO_OCCURS_WITH` (associative / suggestive) carry vastly different inference value, and a hypernym attestation from a foundational source like WordNet shouldn't sit at the same prior confidence as a co-occurrence attestation derived from a single AI model probe.

2. **Source trust classes are hierarchical, not per-source-hardcoded.** Adding a new source = picking which tier in a documented trust hierarchy it falls under, recording that as a meta-attestation. The trust class entity carries the prior weight, effective-μ multiplier, arena admittance policy, and retention policy.

Both axes need substrate-canonical entities + meta-attestations seeded at bootstrap. Without this work, every decomposer's first attestation has no principled Glicko-2 state to assign, and every source has no principled trust weighting to apply.

## Decision

### Part A — Attestation-kind value tiers + Glicko-2 priors

The substrate's modality-agnostic kinds + the per-modality kinds each decomposer introduces are partitioned into **value tiers** by their semantic load. Each tier has a prior Glicko-2 state + a cascade-weight multiplier. Each kind entity carries meta-attestations declaring its tier (or, for kinds with idiosyncratic semantics, override values directly).

Initial kind value tiers (per substrate canon):

| Tier | Kind shape | Examples | Prior (rating, RD, volatility) | Cascade weight |
|---|---|---|---|---|
| **T1 Mandate** | Substrate-asserted invariant | `HAS_TYPE`, `HAS_TIER`, `HAS_CANONICAL_COORD` (when emitted by substrate-canonical source) | (2500, 30, 0.001) | 1.0× (full traversal weight) |
| **T2 Standards Structural** | Codified standards-derived attribute | `HAS_GENERAL_CATEGORY`, `HAS_BIDI_CLASS`, `HAS_ISO_639_1_CODE`, `HAS_UCA_PRIMARY_WEIGHT`, `HAS_VALIDITY` | (2300, 60, 0.005) | 1.0× |
| **T3 Taxonomic** | Logical-class membership | `IS_A`, `IS_HYPERNYM_OF`, `IS_HYPONYM_OF`, `BELONGS_TO_MACROLANGUAGE`, `IS_VERB_GROUP_OF` | (1900, 150, 0.03) | 0.9× |
| **T4 Partitive** | Structural composition | `IS_PART_OF`, `PART_OF`, `IS_MERONYM_OF`, `IS_HOLONYM_OF`, `HAS_PART`, `MADE_OF` | (1800, 170, 0.04) | 0.85× |
| **T5 Causal / Implicational** | Entailment, cause, prerequisite | `CAUSES`, `BECAUSE`, `REQUIRES`, `ENTAILS`, `HAS_PREREQUISITE`, `MOTIVATED_BY_GOAL`, `INTENDS`, `EFFECT` | (1700, 200, 0.05) | 0.8× (cascade evaluates with extra arena scrutiny since causation is context-sensitive) |
| **T6 Equivalence / Translation** | Mutual-substitution (often multi-valued) | `IS_TRANSLATION_OF`, `SAME_COLOR_AS`, `IS_SIMILAR_TO`, `HAS_VARIANT_OF`, `IS_LEMMA_OF` (in OMW per-language context) | (1600, 220, 0.05) | 0.7× (asymmetric utility: equivalence enables traversal sideways but doesn't deepen) |
| **T7 Oppositional / Constraining** | Definitional opposition | `IS_ANTONYM_OF`, `EXCLUDES`, `IS_REPLACED_BY` (retirement) | (1550, 240, 0.05) | 0.6× (constrains but doesn't equate; cascade uses for pruning) |
| **T8 Associative / Co-occurrence** | Usage/proximity correlation | `CO_OCCURS_WITH`, `FOLLOWS`, `PRECEDES`, `OCCURS_IN_CONTEXT`, `IS_DERIVATIONALLY_RELATED_TO`, `USED_FOR`, `AT_LOCATION`, `HAS_PROPERTY` | (1500, 280, 0.06) | 0.5× (suggestive; supports candidate narrowing but doesn't drive decisions alone) |
| **T9 Tensor-Calculation** | AI-model-derived computational role | `EMBEDS`, `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`, `OUTPUT_PROJECTS`, `TENSOR_NAME_MEANS_MECHANICAL_ROLE` | (1400, 300, 0.06) | 0.4× (single-model-probe trust; cluster across many models for higher weight) |
| **T10 Scalar-Valued** | Numeric attribute (value in rating column) | `HAS_NUMERIC_VALUE`, `HAS_STROKE_COUNT`, `HAS_UCA_COLLATION_ORDER`, `HAS_AGE`, `HAS_FREQUENCY`, RLE counts | (n/a — rating IS the value; RD captures measurement uncertainty) | n/a (scalar; queried directly, not traversed) |
| **T11 Probationary / User** | User-supplied content; awaits corroboration | user-emitted attestations from prompt-local context | (1300, 350, 0.06) | 0.3× (session-scoped trust; cascade considers but discounts) |

**Per-kind overrides**: a kind entity may carry meta-attestations overriding its tier defaults (e.g., `Glicko2_PRIOR_RATING(this_kind, 2200)` to bump a particular kind into higher confidence based on substrate-side analysis).

**Cardinality + arena semantics** (per ADR 0036) still apply on top. `IS_A` (T3) can be multi-valued in some contexts (rake IS_A noun; rake IS_A verb), functional in others (this specific instance IS_A this specific subtype). The cascade weight gets multiplied by arena-policy + context-compatibility factors at query time.

### Part B — Source trust class taxonomy (10 tiers)

Each source entity carries a `HAS_TRUST_CLASS` meta-attestation pointing at one of the substrate-canonical trust-class entities. Adding a new source = picking its tier, recording the meta-attestation. The trust class entity carries:

- **Prior weight** — multiplied into the Glicko-2 prior when initializing attestations sourced under this class.
- **Effective-μ multiplier** — multiplies the kind-tier cascade weight at traversal time.
- **Arena admittance policy** — which arenas accept attestations from this class (e.g., Substrate Mandate arena is closed below tier 1).
- **Retention policy** — time-decay rate; ephemeral vs durable.

| Trust Class | Tier | Examples | Prior weight | Eff-μ multiplier | Arena admittance | Retention |
|---|---|---|---|---|---|---|
| **Substrate Mandate** | 1 | substrate-canonical source itself | 1.0 | 1.0 | all (including closed-mandate arenas) | infinite |
| **Standards-Derived** | 2 | Unicode/UCD/UCA/UAX; ISO 639/15924/10646; BCP-47 (IANA); W3C/IETF/RFC bodies | 0.95 | 0.95 | all open arenas | infinite |
| **Academic Curated** | 3 | Princeton WordNet; Universal Dependencies; NSF-funded KBs (Atomic2020) | 0.85 | 0.85 | open + curated-only arenas | infinite |
| **Academic Curated with User Input** | 4 | OMW; CLDR community contributions; ConceptNet's WordNet/JMDict sub-sources | 0.78 | 0.78 | open + curated-only + community-vetted arenas | infinite |
| **Structured Corpus** | 5 | Tatoeba; ConceptNet's Wikipedia sub-source; structured public datasets | 0.70 | 0.70 | open + corpus arenas | long (durable; reviewable) |
| **User-Curated Resource** | 6 | Wiktionary; Common Crawl tier; OMCS within ConceptNet | 0.60 | 0.60 | open + user-curated arenas | long |
| **AI Model Probe** | 7 | TransformerModelDecomposer probe observations from a single model | 0.50 | 0.50 | model-probe arena + cross-model-consensus arena | medium (decays without cross-model corroboration) |
| **App-Derived** | 8 | runtime logs, internal state, app-side derivations | 0.40 | 0.40 | app-internal arenas + telemetry arenas | short (operational; trimmed by retention policy) |
| **User Prompt / User Content** | 9 | prompt-local user assertions; uploaded content awaiting corroboration | 0.30 | 0.30 | user-scope arenas; cross-user arenas only after corroboration | session-scoped + optional opt-in durability |
| **Adversarial / Untrusted** | 10 | flagged content (known spam, suspected prompt-injection, deliberately corrupted) | 0.0 | 0.0 (effectively excluded) | closed (no arena admits) | short (preserved for analysis-of-attacks, not for reasoning) |

Tier 1 trust class is reserved for the substrate-canonical source entity itself (bootstrapped at install per ADR 0042 Stage 1). Tiers 2–6 are bootstrapped at install too (sources within those tiers register against the existing trust-class entity by `HAS_TRUST_CLASS` meta-attestation). Tiers 7–10 are runtime-assigned when their first source registers.

### Part C — Cross-decomposer kind taxonomy (initial reconciliation)

A small canonical reconciliation table for kinds that appear with different names across decomposers but mean the same thing. The type-taxonomy agent owns the full reconciliation; this initial table seeds the unification:

| Canonical kind | Aliases observed across decomposers |
|---|---|
| `IS_A` | WordNet's hypernymy when treated as IS_A; ConceptNet's `IsA`; UD's referencing of `cop`-based class assertions in some treebanks |
| `IS_HYPERNYM_OF` | distinct from `IS_A` — strictly *type-of-type* relations, used taxonomically (WordNet, ConceptNet under `RelatedTo:hypernym` flag) |
| `IS_PART_OF` | ConceptNet `PartOf`; WordNet member/part/substance meronymy converged |
| `IS_MERONYM_OF` | inverse of `IS_PART_OF` — kept distinct because WordNet distinguishes Member/Part/Substance meronymy; sub-tier kinds carry the meronymy subtype as context-id |
| `CAUSES` | WordNet `Causes`; ConceptNet `Causes`; Atomic2020 `because` (inverse direction; reverses to `IS_CAUSED_BY`) |
| `IS_TRANSLATION_OF` | OMW per-synset bridges; Wiktionary translations; Tatoeba sentence pairs; ConceptNet `TranslationOf` |
| `IS_LEMMA_OF` | WordNet/OMW lemma→synset; the OMW variant carries the language context |
| `HAS_LANGUAGE` | UD per-treebank, Wiktionary per-entry, Tatoeba per-sentence — same kind, applies wherever content has a language |
| `OCCURS_IN_CONTEXT` | UD's per-sentence POS, WordNet sense in a gloss — generalized context-binding pattern |

Decomposers SHOULD emit the canonical kind name when their source's term is a synonym; the type-taxonomy agent registers per-decomposer aliasing rules as meta-attestations on the kind entity (`IS_ALIAS_OF` between vendor name and canonical kind). The substrate's cascade traverses the canonical kind; aliases are query-time conveniences.

## Consequences

- Bootstrap (per [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md)) gains a Stage 3.5: seed the 10 trust-class entities + the 11 kind-value-tier entities + the canonical-kind-reconciliation aliases. Each is content-addressed by canonical name; stable across installs.
- Every decomposer at first run registers its emitted kinds against the tier system (meta-attestations: `HAS_KIND_VALUE_TIER`); novel kinds default to Tier 8 Associative until a decomposer / type-taxonomy override specifies otherwise.
- Every source at first observation registers a `HAS_TRUST_CLASS` meta-attestation pointing at one of the 10 trust-class entities.
- Glicko-2 attestation initialization (Stories 5.1+) uses the per-kind prior tier + per-source trust class weight to compute initial (rating, RD, volatility); not a global default.
- Cascade A* effective-μ computation incorporates kind cascade weight × source trust multiplier × arena policy × context compatibility — no single multiplicative shortcut.
- The kind taxonomy + trust class hierarchy are themselves substrate content (meta-attestations on kind/source/trust-class entities); they can evolve via attestation refinement without code changes.

## Alternatives considered

- **Single global Glicko-2 prior** (rating=1500/RD=350/vol=0.06). Rejected — discards the substrate's structural knowledge of which kinds + which sources deserve higher initial confidence.
- **Per-source hardcoded trust weight in plugin code**. Rejected — un-queryable, un-mergeable, brittle to new sources (same anti-pattern as the vendor-naming map per ADR 0043 fix).
- **Defer kind-priors + trust-class taxonomy to a later milestone**. Rejected — Glicko-2 attestation initialization (Stories 5.1+) is blocked without it; every decomposer's first attestation needs principled state.

## References

- [ADR 0006](0006-perfcache-and-db-seed-siblings.md) — perf-cache + DB seed determinism
- [ADR 0036](0036-arena-semantics-and-source-trust.md) — arena semantics + source-trust framing this ADR formalizes
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — type vocabulary
- [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md) — Decomposer scope
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap stages (this ADR adds Stage 3.5)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite decomposer (vendor naming as substrate attestations)
- [GLOSSARY](../../GLOSSARY.md) — Source Trust Class, Effective Mu, Arena, Arena Semantics, Glicko-2
- [STANDARDS](../../STANDARDS.md) — Attestation kind discipline (will be expanded with prior + weight rules)
