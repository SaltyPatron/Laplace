---
name: 'Documentation governance'
description: 'Authority and archival rules for specifications, plans, and historical records'
applyTo: '{.scratchpad/**,docs/**,.github/agents/**,.github/prompts/**,.cursor/rules/**}'
---
# Documentation governance

Read `docs/DOCUMENTATION_GOVERNANCE.md` before changing an agent-facing document.

- `docs/specs/` contains stable normative contracts only. No status, dated live counts,
  incident narratives, execution logs, branch/PR notes, or mutable issue checklists.
- GitHub issues are the only active work-status system.
- `docs/archive/` and `.scratchpad/` are frozen historical evidence. Never select work,
  infer current state, or add new status there.
- Preserve superseded material in Git history/archive; keep the current specification
  coherent instead of layering contradictory annotations into it.
- Countable facts come from generated inventory or source, not prose.
- Verify factual claims at their owning layer. Issue prose and agent notes are not proof.
- Standing instructions must be stable, non-accusatory, free of threats/incident stories,
  and usable across harnesses with capability-aware fallbacks.
- Active requirements found only in history must be restated in a current spec or GitHub
  issue before implementation.
