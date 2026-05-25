# ADR 0017: Agent operating cadence — proactive issue + status maintenance

## Status

**Accepted** — 2026-05-21
**Amended** — 2026-05-24: `STATE.md` / `decisions.md` cadence files retired. They were tried, degraded into conversation logs, and re-introduced exactly the drift this ADR was written to prevent. Durable state lives in (a) GitHub Issues (chunk/story progress + acceptance) and (b) ADRs (decisions that shape invariants). (Release-please / `CHANGELOG.md` was removed 2026-05-25 per [ADR 0020](0020-conventional-commits-and-release-please.md); Conventional Commits remain the commit-message standard.) [OPERATIONS.md](../../OPERATIONS.md) line ~314 is the authoritative position; [CLAUDE.md](../../CLAUDE.md) cadence section is aligned to it.

## Context

In earlier iterations, the agent (Claude) responded to user input tactically — answering the immediate question, patching the immediate concern — without updating the surrounding system state (issues, decisions log, status). This led to drift: requirements stated in chat weren't reflected in tracked artifacts; decisions weren't logged in ADRs; status didn't reflect reality. The user was at 0% progress after 12 months partly because each prior agent failed at this discipline.

## Decision

When the user surfaces a requirement / decision / change, the agent must **automatically**, without being asked:

1. **Scan open issues** for items affected by the input.
2. **Update issue bodies** (via `gh issue edit`) to reflect the new direction.
3. **Open new issues** if the input introduces work that doesn't fit an existing issue.

5. **File an ADR** in `docs/adr/` if the decision shapes invariants.
6. **Reflect the change** in RULES.md / STANDARDS.md / DESIGN.md / GLOSSARY.md if it's a project-wide invariant — but only with explicit user authorization for those files.

At chunk start: re-read plan.md, RULES.md, STANDARDS.md, DESIGN.md, the chunk's issue. At chunk end: tick acceptance criteria; close issue via commit; update STATE.md.

When a decision is open: capture in a GitHub Discussion with tradeoffs; pause if blocking.

## Consequences

- Project state never drifts from chat conversation.
- Future agent sessions read STATE.md + decisions.md + ADRs + plan.md and resume exactly where left off.
- The user doesn't have to re-explain past decisions.

## References

- [CLAUDE.md](../../CLAUDE.md) — "Cadence — standing agent operating procedure" section
- ADR 0022 (ADRs as decision format — companion)
