# ADR 0022: ADRs as the decision-record format

## Status

**Accepted** — 2026-05-21

## Context

## Decision

Adopt **Architecture Decision Records (ADRs)** per Michael Nygard's pattern as the canonical decision format.

Each ADR is a markdown file under `docs/adr/NNNN-kebab-title.md` with sections: Status / Context / Decision / Consequences / Alternatives Considered / References.

When an ADR is superseded:
- The original is NOT edited (immutable record).
- Status is changed to "Superseded by ADR NNNN".
- A new ADR is written for the new decision.

## Consequences

- Decisions are immutable historical artifacts — they record what was true when made.
- Supersedence is explicit; the trail is traceable.
- New ADR template (`0000-template.md`) lowers the cost of writing one.
- The `docs/adr/README.md` index is the canonical decision map.

## References

- Michael Nygard, "Documenting Architecture Decisions" (2011)
- [docs/adr/README.md](README.md)
