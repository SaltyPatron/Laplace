# ADR 0010: Substrate Synthesis — naming for fully parametric model emission

## Status

**Accepted** — 2026-05-21

## Context

The capability of emitting any-shape model from substrate state (any architecture, any dim, any MoE config, any vocab) needs a name. User explicitly proposed considering alternatives — earlier candidate names were ruled out as trademarked / borrowed; needed a clean, descriptive term.

Per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth 6: the recipe is a **fillable mold**, and synthesis **pours substrate facts into the chosen shape** (dim, dense/MoE, layers, vocab, dtype). The same machinery fills the source's own mold (round-trip) or any other mold (retarget). Per truth 8, the exported model is "the typed, sourced, Glicko-2-rated consensus facts materialized into the mold's tokens" — not a copy, not a weight-average, and not bit-perfect preservation (truth 6: bit-perfect is worthless). This ADR names that capability; it does not specify the algorithm.

## Decision

The term is **"Substrate Synthesis"**.

## Consequences

- Synthesis = pouring substrate facts into a chosen mold; substrate = source of those facts. Clean and descriptive. (Note: "assembling from parts" was the original gloss but undersells it — the mechanism per the anchor is materializing consensus facts into the mold's tokens, not bolting pre-made parts together.)
- Used in docs, code, function names. The CLI surface is universal (no per-model verbs): `synthesize substrate <recipe.json>` (recipe-driven emission) and `synthesize passthrough <model-dir>` (diagnostic), driven through `IArchitectureTemplate` (`materialize_tensor` per DESIGN.md). Invoked via `just synthesize <subcommand>` or the `Laplace.Cli` assembly directly.
- Open to refinement if a sharper term emerges; until then this is canonical. Per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth 10, the name is just a label — state the mechanism, not the label.
- **The synthesis algorithm itself — "pour facts into the mold" at frontier scale — is OPEN per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) (OPEN QUESTIONS).** This ADR settles only the name, not how interior tensor slots are filled from token↔token consensus.

## Alternatives considered

- "Parametric Synthesis" — slightly clearer but more verbose.
- "À la carte Synthesis" — captures freedom-of-choice but awkward in code identifiers.
- "Substrate-driven Emission" — accurate but less catchy.

## References

- Memory: project_laplace_invention.md — "Substrate Synthesis" section
