# ADR 0002: Three core tables; no event log

## Status

**Accepted** — 2026-05-21

## Context

Early design considered four tables: `entities`, `physicalities`, `attestations`, `observations` (an event log of individual attestation observations for audit/provenance). User pushed back: attestation IS consensus state per source per tuple; repeated identical observations from the same source are idempotent, not separate event records.

## Decision

The substrate has **exactly three** tables: `entities`, `physicalities`, `attestations`. No event log.

- One row per `(subject, kind, object, source, context)` tuple in `attestations`.
- Repeated assertions from the same source are no-ops (`INSERT ON CONFLICT DO NOTHING`).
- Provenance is the `source_id` column, which references the source entity.
- Glicko-2 dynamics live in arena-resolved observation updates. Effective support derives from the Glicko-2 value itself (rating μ, RD, volatility) adjusted by source-kind credibility, lineage, context compatibility, current state, and structural support. Trust is a self-tuning Glicko-2 value emerging from cross-source agreement — **not** a fixed tier or trust-class ladder (per [SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth 5; "tier" is reserved exclusively for the Merkle stratum, T0 = Unicode codepoints).

## Consequences

- Schema is leaner.
- Repetition by a low-credibility source doesn't inflate ratings (one row, one credibility-weighted contribution).
- Cross-source consensus emerges from per-source rows + meta-credibility, not from event counting.
- No event log means no easy "show me every observation in order" query — provenance must be reconstructed from `source_id` + source lineage meta-attestations + `last_observed_at`.

## References

- [RULES.md R2, R5](../../RULES.md)
- Memory: project_laplace_invention.md — "Attestation IS consensus state" section
