---
name: 'Design doc rules'
description: 'How to update the living design docs (docs/specs/ + .scratchpad/) without causing doc drift'
applyTo: '{.scratchpad/**,docs/specs/**,docs/INVENTIONS.md,docs/INDEX.md}'
---
# Design doc rules

Living specs live in `docs/specs/` (binding law, annotate-on-supersede); `.scratchpad/`
holds session logs and campaign docs (historical, append-only). `docs/INDEX.md` maps
them. Doc drift IS the battle (doc 13 §2.4). When editing them:

- Update status IN PLACE; never append a contradiction hundreds of lines below an old
  claim. A reader must not need the full file to know current truth.
- Open work lives in GitHub issues (since 2026-07-18).
  [02_Identified_Issues.txt](../../.scratchpad/02_Identified_Issues.txt) is the CLOSED
  historical tracker — never append or update entries there; its header maps old
  numbers to GH issues. (Historical lesson: issue numbers were reused and caused
  misfiled fixes — GH numbering makes that structural.)
- Every claim must be verified against source in the current session, or cite the doc
  that verified it. Nothing is carried forward on faith (grounding rule from 05/06).
- Docs 05 (invariants) and 06 (engineering rules), in `docs/specs/`, are BINDING —
  changing them changes the law; do so only with explicit user direction.
- When a fix lands, also fix the stale "violations"/"known gaps" paragraphs it
  invalidates (06 Rule #8 violations and CLAUDE.md "Known gaps" have drifted before).
- Doc 13 is historical stabilization notes — verify against code; active remediation
  lives in `docs/specs/16`, `.scratchpad/17`, and `.cursor/plans/`.
