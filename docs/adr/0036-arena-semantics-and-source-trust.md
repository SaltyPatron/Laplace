# ADR 0036: Arena semantics and source-trust consensus

## Status

**Accepted** — 2026-05-22

## Context

Glicko-2 gives Laplace rating, RD, and volatility, but the rating update needs substrate-native semantics. Not every disagreement means the same thing. Some attestation kinds are multi-valued and compatible (`rake HAS_POS NOUN` and `rake HAS_POS VERB`). Some are functional under a context (`France HAS_CURRENT_CAPITAL Paris` vs. `France HAS_CURRENT_CAPITAL Los Angeles`). Some are scalar, temporal, symmetric, inverse-functional, or context-required.

Laplace must also distinguish independent high-trust convergence from low-trust repetition. Foundational constants, standards, curated academic resources, user-curated linked resources, corpora, model-derived observations, prompts, and ordinary user content are not equal sources. Repetition from one source or a correlated source family must not become fake consensus.

## Decision

Attestation kinds carry arena semantics. At minimum, each arena/kind defines:

- compatibility: multi-valued, functional, inverse-functional, mutually exclusive set, scalar axis, symmetric relation, etc.
- context policy: context-free, context-required, temporal interval, comparison frame, source-local, prompt-local, fiction/speculation mode, etc.
- competition set: which `(subject, kind, context)` group competes for one or more winning objects
- source-trust policy: which source classes are allowed, preferred, discounted, or prompt-local
- effective-score formula inputs: rating, RD, volatility, source credibility for kind, context compatibility, and structural support

Source credibility is tracked per source per attestation kind. Source classes are ordered by trust and purpose, including:

1. foundational constants and project-defined invariants
2. standards bodies / Unicode-derived artifacts
3. curated academic resources
4. academically linked user-curated resources
5. structured corpora and treebanks
6. AI model-derived probe observations
7. prompt-local or ordinary user content

The operational rule is: truths cluster across independent, high-trust, structurally adjacent sources; false or unsupported claims may cluster inside correlated low-trust source families but remain source-scoped, low-rated, high-RD, context-bound, disputed, or excluded from strict synthesis scopes.

Bad claims are not deleted merely because they are bad. They are contained as observations about their sources/communities unless they win the relevant arena competition under high-trust evidence.

## Consequences

- Glicko-2 updates are not raw vote counting. They are arena-aware agreement/disagreement events.
- Repeated assertions from one source remain idempotent; repeated copies from a source family are discounted by provenance/lineage.
- `Nihon` vs. `Japan` is represented as identity/alias/translation/endonym-exonym structure, not contradiction.
- `France HAS_CURRENT_CAPITAL Paris` vs. `France HAS_CURRENT_CAPITAL Los Angeles` is competition inside a functional current-capital arena.
- Misinformation saturation is contained: the substrate can answer both "what is true under strict source scope" and "what does this low-trust group claim" without confusing the two.
- Honest abstention becomes mechanical: no path, weak path, high RD, high volatility, or unresolved arena conflict can return abstention instead of invented certainty.

## Alternatives considered

- **Flat confidence score per edge.** Rejected — cannot distinguish compatible multiplicity from contradiction.
- **Raw source count voting.** Rejected — correlated repetition can manufacture fake consensus.
- **Deleting low-trust claims.** Rejected — loses useful source/community evidence and prevents analysis of disputed claims.

## References

- [RULES.md R5](../../RULES.md) — attestation is consensus state
- [RULES.md R20](../../RULES.md) — arena semantics and source trust are mandatory
- [GLOSSARY.md](../../GLOSSARY.md) — Arena, Source Trust Class, Truths Cluster / Lies Scatter, Honest Abstention
- [DESIGN.md](../../DESIGN.md) — source trust and arena semantics section
