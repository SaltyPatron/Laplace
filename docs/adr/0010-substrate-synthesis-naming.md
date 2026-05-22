# ADR 0010: Substrate Synthesis — naming for fully parametric model emission

## Status

**Accepted** — 2026-05-21

## Context

The capability of emitting any-shape model from substrate state (any architecture, any dim, any MoE config, any vocab) needs a name. User explicitly proposed considering alternatives — earlier candidate names were ruled out as trademarked / borrowed; needed a clean, descriptive term.

## Decision

The term is **"Substrate Synthesis"**.

## Consequences

- Synthesis = assembling from parts; substrate = source. Clean and descriptive.
- Used in docs, code, function names (`laplace-cli synthesize`, `IArchitectureTemplate`).
- Open to refinement if a sharper term emerges; until then this is canonical.

## Alternatives considered

- "Parametric Synthesis" — slightly clearer but more verbose.
- "À la carte Synthesis" — captures freedom-of-choice but awkward in code identifiers.
- "Substrate-driven Emission" — accurate but less catchy.

## References

- Memory: project_laplace_invention.md — "Substrate Synthesis" section
