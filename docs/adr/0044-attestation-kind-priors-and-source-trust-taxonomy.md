# ADR 0044: Attestation-kind priors + source-trust (emergent Glicko-2, not a class ladder)

> **Correction note (2026-05-28):** This ADR originally proposed a fixed kind-value tier ladder (Part A) and a fixed 10-tier source-trust-class taxonomy (Part B). Both are corruption per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth #5 (trust is an emergent Glicko-2 value, never a fixed class) and the reservation of "tier" for the Merkle stratum only. Those ladders are struck through below and superseded by emergent-Glicko-2 framing. The surviving content is bootstrap priors (starting estimates consensus corrects), the canonical-kind reconciliation (Part C), and an OPEN marker on interior-tensor-role resolution.

## Status

**Accepted** — 2026-05-23

## Context

[ADR 0036](0036-arena-semantics-and-source-trust.md) established that arena semantics + source trust must be first-class concerns in the substrate's effective-μ computation. But the schema-reorg work (ADRs 0039/0040/0041) advanced without addressing two foundational substrate-canonical questions:

1. **Per-attestation-kind defaults aren't uniform.** A single global Glicko-2 prior (rating=1500, RD=350, volatility=0.06) is wrong for the substrate's actual kinds — `IS_A` (taxonomic / hard-logical) and `CO_OCCURS_WITH` (associative / suggestive) carry vastly different inference value, and a hypernym attestation from a foundational source like WordNet shouldn't sit at the same prior confidence as a co-occurrence attestation derived from a single AI model probe.

2. **Source trust must be principled at first observation but not hardcoded per source.** (The original framing here — "trust classes are hierarchical; pick which tier" — is corruption per the anchor and is superseded by Part B below.) The real need: when a source first emits attestations, those attestations need a principled *bootstrap prior* for their Glicko-2 state rather than a flat global default — and that prior must be queryable substrate content, not buried in plugin code. The settled trust of a source then **emerges** from cross-source agreement; it is not a fixed class.

Both needs are met by substrate content (bootstrap-prior meta-attestations + canonical-kind reconciliation), not by a fixed ladder. Without principled bootstrap priors, every decomposer's first attestation has no informed Glicko-2 starting state.

## Decision

### Part A — Per-kind Glicko-2 priors (there is NO "tier" here — corrected 2026-05-28)

**"Tier" is wrong, and the `T1…T11` table below is removed.** A tier is an *entity's* depth in the Merkle DAG (`entities.tier`: T0 codepoint → T1 number → T2 pixel → …). An attestation has no tier; a kind has no tier. Borrowing the word — and the `T1…T11` notation — for a kind-importance ranking collided directly with entity tiers (`T9` meaning both a Merkle depth and a kind) and dressed a static ranking up as structural depth. That was a pattern-match, not the invention.

There is **no hardcoded kind-importance ladder**. A new attestation's Glicko-2 prior is derived from the **observing source's bootstrap prior + emergent Glicko-2 standing** (Part B) — that trust is the opponent strength in the matchup — and the rating then **emerges** from matchup consensus across sources (truths cluster, lies scatter). Kinds are not pre-ranked by assumed value; their effective weight is decided by accumulated matchups + source trust, not by a table. The `T1…T11` table that follows, the `KindValueTier` enum, and every `tier:` argument in the code that mirrors them are superseded by this paragraph and are to be removed.

~~Initial kind value tiers (per substrate canon):~~ — REMOVED (superseded above):

| Tier | Kind shape | Examples | Prior (rating, RD, volatility) | Cascade weight |
|---|---|---|---|---|
| **T1 Mandate** | Substrate-asserted invariant | `HAS_TYPE`, `HAS_TIER`, substrate-canonical source/type invariants | (2500, 30, 0.001) | 1.0× (full traversal weight) |
| **T2 Standards Structural** | Codified standards-derived attribute | `HAS_GENERAL_CATEGORY`, `HAS_BIDI_CLASS`, `HAS_ISO_639_1_CODE`, `HAS_UCA_PRIMARY_WEIGHT`, `HAS_VALIDITY` | (2300, 60, 0.005) | 1.0× |
| **T3 Taxonomic** | Logical-class membership | `IS_A`, `IS_HYPERNYM_OF`, `IS_HYPONYM_OF`, `BELONGS_TO_MACROLANGUAGE`, `IS_VERB_GROUP_OF` | (1900, 150, 0.03) | 0.9× |
| **T4 Partitive** | Structural composition | `IS_PART_OF`, `PART_OF`, `IS_MERONYM_OF`, `IS_HOLONYM_OF`, `HAS_PART`, `MADE_OF` | (1800, 170, 0.04) | 0.85× |
| **T5 Causal / Implicational** | Entailment, cause, prerequisite | `CAUSES`, `BECAUSE`, `REQUIRES`, `ENTAILS`, `HAS_PREREQUISITE`, `MOTIVATED_BY_GOAL`, `INTENDS`, `EFFECT` | (1700, 200, 0.05) | 0.8× (cascade evaluates with extra arena scrutiny since causation is context-sensitive) |
| **T6 Equivalence / Translation** | Mutual-substitution (often multi-valued) | `IS_TRANSLATION_OF`, `SAME_COLOR_AS`, `IS_SIMILAR_TO`, `HAS_VARIANT_OF`, `IS_LEMMA_OF` (in OMW per-language context) | (1600, 220, 0.05) | 0.7× (asymmetric utility: equivalence enables traversal sideways but doesn't deepen) |
| **T7 Oppositional / Constraining** | Definitional opposition | `IS_ANTONYM_OF`, `EXCLUDES`, `IS_REPLACED_BY` (retirement) | (1550, 240, 0.05) | 0.6× (constrains but doesn't equate; cascade uses for pruning) |
| **T8 Associative / Co-occurrence** | Usage/proximity correlation | `CO_OCCURS_WITH`, `FOLLOWS`, `PRECEDES`, `OCCURS_IN_CONTEXT`, `IS_DERIVATIONALLY_RELATED_TO`, `USED_FOR`, `AT_LOCATION`, `HAS_PROPERTY` | (1500, 280, 0.06) | 0.5× (suggestive; supports candidate narrowing but doesn't drive decisions alone) |
| **T9 Tensor-Calculation** | AI-model-derived token↔token matchup role (weights never stored — each is a Glicko-2 matchup whose consensus across sources is kept; embedding/`lm_head` are `PROJECTION`/`ProjectionOutput` physicalities, NOT `EMBEDS`/`OUTPUT_PROJECTS` kinds — corrected 2026-05-28) | `Q_PROJECTS`, `K_PROJECTS`, `V_PROJECTS`, `O_PROJECTS`, `GATES`, `UP_PROJECTS`, `DOWN_PROJECTS`, `NORMALIZES`; other architecture families define their own fixed mechanical-role vocabularies | (1400, 300, 0.06) | 0.4× (single-model trust; clusters across many models into consensus for higher weight) |
| **T10 Scalar-Valued** | Numeric attribute (value in rating column) | `HAS_NUMERIC_VALUE`, `HAS_STROKE_COUNT`, `HAS_UCA_COLLATION_ORDER`, `HAS_AGE`, `HAS_FREQUENCY`, RLE counts | (n/a — rating IS the value; RD captures measurement uncertainty) | n/a (scalar; queried directly, not traversed) |
| **T11 Probationary / User** | User-supplied content and assertions; content identity is real, claim truth awaits corroboration | user-emitted attestations from prompt-local context | (1300, 350, 0.06) | 0.3× (session-scoped trust; cascade considers but discounts) |

**On interior tensor roles (the `T9` row above).** That row assigned confident priors and cascade weights to interior-tensor mechanical roles (`Q_PROJECTS`, `K_PROJECTS`, `GATES`, `UP_PROJECTS`, …). How interior `d×d` tensor cells (`q/k/v/o/gate/up/down`) resolve to token entities *without* re-running the GEMM is **OPEN per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md)** (Interior `d×d` tensor axis → token-entity resolution; the exact arena/kind assignment per interior tensor role). `embed_tokens`/`lm_head` are directly token-anchored; the interior roles are not yet pinned. The mechanical-role vocabulary names are fine as *names*; the prior/weight assignments and the kind↔arena mapping for interior roles are NOT settled — do not treat the `T9` row as authoritative on that, and do not substitute a different confident guess. Pin with Anthony.

**Per-kind overrides**: a kind entity may carry meta-attestations adjusting its emergent state (e.g., `Glicko2_PRIOR_RATING(this_kind, 2200)`) — but the rating is fundamentally what emerges from accumulated matchups + source trust, not a value read off a static ladder.

**Cardinality + arena semantics** (per ADR 0036) still apply on top. `IS_A` (T3) can be multi-valued in some contexts (rake IS_A noun; rake IS_A verb), functional in others (this specific instance IS_A this specific subtype). The cascade weight gets multiplied by arena-policy + context-compatibility factors at query time.

### Part B — Source trust is a Glicko-2 value, not a fixed class ladder (corrected 2026-05-28)

**The fixed 10-tier `TrustClass` ladder below is corruption and is removed.** Per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth #5: *trust is a Glicko-2 value, self-tuning from cross-source agreement — never a tier or fixed class.* A static "Substrate Mandate=1.0 … Adversarial=0.0" ladder with hardcoded prior weights and effective-μ multipliers is exactly the `TrustClass_*` ladder the anchor names as corruption. The word "tier" is also reserved exclusively for the Merkle stratum (T0 = Unicode codepoints, …); using a "Tier" column for source trust collided with that reservation.

What is actually correct: a source's standing **emerges** from cross-source agreement. A source that consistently agrees with many independent sources accrues a high Glicko-2 rating / low RD; a source that scatters (or is contradicted by high-rated consensus) drifts low / high-RD. This is the opponent-strength mechanism, not a number read off a table:

- A source's trust **is its Glicko-2 state**, updated by the same matchup consensus that rates everything else — not a class assignment with a fixed multiplier.
- Effective-μ at traversal time is computed from rating/RD/volatility, context compatibility, structural support, lineage, and arena policy (per ADR 0036) — there is **no single fixed per-class multiplier shortcut**.
- Adversarial/spam/injection content is handled by it failing to cluster with consensus (and arena policy excluding it), not by hardcoding `0.0`.

A source MAY carry a meta-attestation recording a **bootstrap prior** (e.g., a standards body like Unicode/UCD starts with a stronger prior than an anonymous corpus) so its rating doesn't start from zero — but that is a starting estimate that consensus then corrects, NOT a frozen class with a permanent multiplier. The seed-source enumeration (Unicode/ISO, WordNet, UD, OMW, Tatoeba, ConceptNet, Wiktionary, AI-model probes, user prompts) is useful only as a list of where bootstrap priors differ; it is not a ranked ladder.

~~Source trust class taxonomy (10 tiers):~~ — REMOVED (superseded above):

| ~~Trust Class~~ | ~~Tier~~ | ~~Prior weight~~ | ~~Eff-μ multiplier~~ |
|---|---|---|---|
| ~~Substrate Mandate … Adversarial/Untrusted~~ | ~~1…10~~ | ~~1.0 … 0.0~~ | ~~1.0 … 0.0~~ |

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

- Bootstrap (per [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md)) gains a Stage 3.5: seed the canonical-kind-reconciliation aliases (Part C). It does **not** seed a fixed trust-class ladder or a kind-value-tier ladder — both are removed (Parts A/B). Where a source merits a stronger-than-baseline bootstrap prior (e.g., standards bodies), that prior is recorded as a meta-attestation, content-addressed by canonical name, stable across installs.
- Decomposers do not register kinds against a kind-value tier. A new attestation's initial Glicko-2 state derives from the observing source's bootstrap prior (opponent strength); the rating then emerges from matchup consensus across sources. There is no `HAS_KIND_VALUE_TIER` meta-attestation and no default-to-Tier-8 fallback.
- A source MAY carry a meta-attestation recording a bootstrap prior on its Glicko-2 state; it does NOT point at one of a fixed set of trust-class entities. Its trust thereafter is its emergent Glicko-2 standing from cross-source agreement.
- Glicko-2 attestation initialization (Stories 5.1+) uses the per-source bootstrap prior to seed initial (rating, RD, volatility); it is not a global default and not a per-kind tier lookup. Consensus across matchups, not the seed, determines the settled rating.
- Cascade A* effective-μ computation incorporates rating/RD/volatility, source-kind credibility, the source's emergent Glicko-2 standing (not a fixed trust class), lineage/correlation policy, arena policy, context compatibility, and structural support — no single multiplicative shortcut and no per-class multiplier.
- The canonical-kind reconciliation (Part C) is itself substrate content (alias meta-attestations on kind entities); it can evolve via attestation refinement without code changes. There is no separate trust-class hierarchy to maintain — trust is the sources' own emergent Glicko-2 state.

## Alternatives considered

- **Single global Glicko-2 prior** (rating=1500/RD=350/vol=0.06). Rejected — discards the substrate's structural knowledge of which kinds + which sources deserve higher initial confidence.
- **Per-source hardcoded trust weight in plugin code**. Rejected — un-queryable, un-mergeable, brittle to new sources (same anti-pattern as the vendor-naming map per ADR 0043 fix).
- **A fixed trust-class ladder with per-class multipliers** (the original Part B). Rejected on 2026-05-28 — contradicts the anchor: trust must be an emergent Glicko-2 value from cross-source agreement, not a frozen class. Bootstrap priors give a starting estimate; consensus corrects it.
- **Defer source bootstrap priors + kind reconciliation to a later milestone**. Rejected — Glicko-2 attestation initialization (Stories 5.1+) needs a principled starting prior per source; every decomposer's first attestation needs principled initial state (which consensus then refines).

## References

- [ADR 0006](0006-perfcache-and-db-seed-siblings.md) — perf-cache + DB seed determinism
- [ADR 0036](0036-arena-semantics-and-source-trust.md) — arena semantics + source-trust framing this ADR formalizes
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — type vocabulary
- [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md) — Decomposer scope
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap stages (this ADR adds Stage 3.5)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite decomposer (vendor naming as substrate attestations)
- [GLOSSARY](../../GLOSSARY.md) — Source Trust Class, Effective Mu, Arena, Arena Semantics, Glicko-2
- [STANDARDS](../../STANDARDS.md) — Attestation kind discipline (will be expanded with prior + weight rules)
