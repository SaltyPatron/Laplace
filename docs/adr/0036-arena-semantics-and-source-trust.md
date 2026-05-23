# ADR 0036: Arena semantics and source-trust consensus

## Status

**Accepted** — 2026-05-22

## Context

Glicko-2 gives Laplace rating, RD, and volatility, but the rating update needs substrate-native semantics. Not every observation means the same thing. Shorthand such as `rake HAS_POS NOUN` means a generic observation envelope with `kind_id = HAS_POS`, `subject_id = rake`, and `object_id = NOUN`; `HAS_POS` is the semantic kind entity, not a bespoke function call. Some attestation kinds are multi-valued and compatible (`rake HAS_POS NOUN` and `rake HAS_POS VERB`). Some are functional under a context (`France HAS_CURRENT_CAPITAL Paris` vs. `France HAS_CURRENT_CAPITAL Los Angeles`). Some are scalar, temporal, symmetric, inverse-functional, or context-required.

Laplace must also distinguish independent high-trust convergence from low-trust repetition. Foundational constants, standards, curated academic resources, user-curated linked resources, corpora, model-derived observations, prompts, and ordinary user content are not equal sources. Prompt content identity is a real content fact; prompt/user claims are low-trust source-scoped observations unless promoted. Repetition from one source or a correlated source family must not become fake consensus.

## Decision

Attestation kinds carry arena semantics. At minimum, each arena/kind defines:

- compatibility: multi-valued, functional, inverse-functional, mutually exclusive set, scalar axis, symmetric relation, etc.
- cardinality: single-valued, multi-valued, bounded set, unbounded set, scalar, tuple-valued, etc.
- context policy: context-free, context-required, temporal interval, comparison frame, source-local, prompt-local, fiction/speculation mode, etc.
- observation update scope: which tuple slots decide whether an incoming observation updates the same current attestation state or a separate state
- conflict policy: which alternatives within the update scope are incompatible; only functional or mutually exclusive arenas create disagreement pressure
- source-trust policy: which source classes are allowed, preferred, discounted, or prompt-local
- lineage policy: how source lineage/correlation families affect independence of support
- effective-score formula inputs: rating, RD, volatility, source credibility for kind, context compatibility, lineage independence, and structural support

Source credibility is tracked per source per attestation kind. Source classes are ordered by trust and purpose, including:

1. foundational constants and project-defined invariants
2. standards bodies / Unicode-derived artifacts
3. curated academic resources
4. academically linked user-curated resources
5. structured corpora and treebanks
6. AI model-derived probe observations
7. prompt-local or ordinary user content

The operational rule is: incoming observations update current attestation state through the relevant arena's policy. Truths cluster across independent, high-trust, structurally adjacent observations; false or unsupported claims may cluster inside correlated low-trust source families but remain source-scoped, low-rated, high-RD, context-bound, disputed, or excluded from strict synthesis scopes.

An arena is not global all-pairs competition. `rake HAS_POS NOUN` and `rake HAS_POS VERB` are compatible lexical-observation shorthand and can strengthen independently. Qualifiers such as POS scheme, language, treebank, tensor position, or grammar rule are represented as context entities, object/value entities, source metadata, recipe content, or meta-attestations, not opaque `params[]`. A functional/current-time arena such as current capital creates conflict only among incompatible observations in the same update scope.

Bad claims are not deleted merely because they are bad. They are contained as observations about their sources/communities unless they win the relevant arena competition under high-trust evidence.

Prompt-local observations can seed traversal immediately because they bind existing entities into a live context. That is not the same as global attestation promotion. Strict arenas must treat prompt/user claims as prompt-local until source-trust policy, lineage policy, context compatibility, and independent structural support justify broader scope.

## Consequences

- Glicko-2 updates are not raw vote counting. They are arena-aware observation updates against current attestation state.
- Repeated assertions from one source remain idempotent; repeated copies from a source family are discounted by provenance/lineage.
- `Nihon` vs. `Japan` is represented as identity/alias/translation/endonym-exonym structure, not contradiction.
- `France HAS_CURRENT_CAPITAL Paris` vs. `France HAS_CURRENT_CAPITAL Los Angeles` is competition inside a functional current-capital arena.
- Misinformation saturation is contained: the substrate can answer both "what is true under strict source scope" and "what does this low-trust group claim" without confusing the two.
- Honest abstention becomes mechanical: no path, weak path, high RD, high volatility, or unresolved arena conflict can return abstention instead of invented certainty.
- Drift and hallucination become explicit traversal-mode choices rather than opaque model failures.

## Alternatives considered

- **Flat confidence score per edge.** Rejected — cannot distinguish compatible multiplicity from contradiction.
- **Raw source count voting.** Rejected — correlated repetition can manufacture fake consensus.
- **Deleting low-trust claims.** Rejected — loses useful source/community evidence and prevents analysis of disputed claims.

## References

- [RULES.md R5](../../RULES.md) — attestation is consensus state
- [RULES.md R20](../../RULES.md) — arena semantics and source trust are mandatory
- [GLOSSARY.md](../../GLOSSARY.md) — Arena, Source Trust Class, Truths Cluster / Lies Scatter, Honest Abstention
- [DESIGN.md](../../DESIGN.md) — source trust and arena semantics section
