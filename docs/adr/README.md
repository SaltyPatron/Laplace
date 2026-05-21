# Architecture Decision Records

This directory holds the architectural decision records (ADRs) for Laplace, following the pattern popularized by [Michael Nygard](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

## Why ADRs

- Decisions are **first-class artifacts** — discoverable, versioned, immutable once accepted.
- Supersedence is explicit (never silently re-decide; mark prior as superseded with link).
- The reason for a decision survives across contributors and across years.
- New contributors (human or agent) can read the ADR set and understand WHY the codebase looks the way it does.

## Format

Use [`0000-template.md`](0000-template.md). Each ADR has:

- **Status** — Proposed / Accepted / Superseded by ADR NNNN / Deprecated
- **Context** — what problem, what constraints
- **Decision** — what we chose
- **Consequences** — what this commits us to
- **Alternatives considered** — other options + why rejected
- **References** — links

## Numbering

Sequential, four-digit. `NNNN-kebab-case-title.md`.

Never renumber existing ADRs. If an ADR is superseded, the new one gets the next number and links back.

## Index (chronological)

| # | Title | Status |
|---|---|---|
| 0001 | [Extend PostGIS via Z+M, do not create parallel geometry type](0001-extend-postgis-via-z-plus-m.md) | Accepted |
| 0002 | [Three core tables; no event log](0002-three-tables-no-event-log.md) | Accepted |
| 0003 | [XXH3-128 for entity hashing](0003-xxh3-128-for-entity-hashing.md) | Superseded by ADR 0015 |
| 0004 | [int64 fixed-point Glicko-2 ratings](0004-int64-fixed-point-glicko2.md) | Accepted |
| 0005 | [Hilbert curve over bounding hyperbox, not the sphere](0005-hilbert-over-hyperbox.md) | Accepted |
| 0006 | [Perf-cache and DB seed as sibling artifacts of UCD](0006-perfcache-and-db-seed-siblings.md) | Accepted |
| 0007 | [Lottery-ticket-aware sparsity, NEVER flat thresholds](0007-lottery-ticket-aware-sparsity.md) | Accepted |
| 0008 | [Sparse-by-construction emission](0008-sparse-by-construction-emission.md) | Accepted |
| 0009 | [Recipe extraction at model ingest; user JSON override](0009-recipe-extraction-and-overrides.md) | Accepted |
| 0010 | [Substrate Synthesis — the naming](0010-substrate-synthesis-naming.md) | Accepted |
| 0011 | [Polymorphic plugin architecture (six interfaces)](0011-polymorphic-plugin-architecture.md) | Accepted |
| 0012 | [Mantissa-packing format: 8 tier + 12 position + 60 truncated hash](0012-mantissa-packing-format.md) | Accepted |
| 0013 | [Two-tier CI/CD: hosted for PR validation, self-hosted for integration](0013-two-tier-cicd.md) | Accepted |
| 0014 | [Self-hosted GitHub Actions runner on hart-server](0014-self-hosted-runner.md) | Superseded by ADR 0019 |
| 0015 | [BLAKE3-128 for entity hashing (raw bytes, no casts, no hex)](0015-blake3-for-entity-hashing.md) | Accepted |
| 0016 | [Reusable helpers — DRY at every layer](0016-reusable-helpers-discipline.md) | Accepted |
| 0017 | [Agent operating cadence — proactive issue + status maintenance](0017-agent-operating-cadence.md) | Accepted |
| 0018 | [Three-layer architecture: bootstrap / CI / local dev](0018-three-layer-architecture.md) | Accepted |
| 0019 | [Dedicated `laplace-runner` system account for the CI runner](0019-laplace-runner-system-account.md) | Accepted |
| 0020 | [Conventional Commits + release-please + SemVer](0020-conventional-commits-and-release-please.md) | Accepted |
| 0021 | [DbUp + Npgsql for migrations](0021-dbup-for-migrations.md) | Accepted |
| 0022 | [ADRs as decision-record format](0022-adrs-as-decision-format.md) | Accepted |
| 0023 | [Laplace extension owns its schema; DbUp orchestrates extension lifecycle](0023-extension-owns-schema-dbup-orchestrates.md) | Accepted (narrows ADR 0021) |

## Workflow

When a decision is made:

1. **Open an issue** describing the design question (use the `infra` template, or `bug`/`chunk` if it surfaced during code work).
2. **Discuss in a Discussion** if it's an open architectural question.
3. **Write the ADR** in `docs/adr/NNNN-kebab-title.md` using the template.
4. **Add a row to the index above.**
5. **Append `.agent/status/decisions.md`** with a one-line summary + link to the ADR.
6. **Reference the ADR** from RULES.md / STANDARDS.md / DESIGN.md if it's invariant-shaping.

When a decision is superseded:

1. **Do not edit the original ADR** to "fix" it. The original captures what was true at the time.
2. **Set the original's Status to "Superseded by ADR NNNN".**
3. **Write the new ADR** describing the new decision; link back to the original under "Alternatives considered" or "Context".
