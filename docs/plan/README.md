# docs/plan — the work, specified

`docs/COMPLETION_PLAN.md` is the map: gap register, phases, sequencing. **This
directory is the terrain** — one document per workstream, deep enough that an
implementer (human or agent) can execute without re-deriving the analysis.

Read `COMPLETION_PLAN.md` §0 first. Its standard of evidence governs every
document here.

## Form

Every workstream document follows the same shape, because the shape is the
point:

1. **Why this exists** — the user-visible symptom, not the abstraction.
2. **How it works today** — cited to `file:line`, including the parts that are
   correct. Anything not determinable from the code is marked as such rather
   than guessed.
3. **How it should work** — design intent and the reasoning behind it, so a
   future change can tell a violation from an evolution.
4. **What to consider** — the decision points an implementer actually hits,
   with tradeoffs, plus the traps that have already claimed attempts here.
5. **Where to look** — a citation index, so the first hour is reading the right
   files instead of finding them.
6. **Acceptance** — behavioral and held-out. Never "it looks better."
7. **Risks** — including what makes this work *look* done when it isn't.

## The workstreams

| # | Workstream | Issue | Phase | State |
|---|---|---|---|---|
| W1 | [The speaking loop](W1_Speaking_Loop.md) — wire the steered lane into chat | #751 | 2 | specified |
| W2 | [The document lane](W2_Document_Lane.md) — Pillar 0, identity/names/typed edges | #754 | 4 | specified |
| W3 | [Self-ingest call graph](W3_Self_Ingest_Call_Graph.md) — the substrate reads its own source | #765 | 1 | specified |
| W4 | Tier-collision seam + sense priors | #752, #753 | 3 | in research |
| W5 | Evaluation harness | #755 | 1 | in research |
| W6 | Architecture gates (spec 37 G1–G10, elector invariant) | #758 | 1 | in research |
| W7 | Questions route themselves (relation-name attestation, `infer` rel_type) | #756 | 5 | in research |
| W8 | `infer()` to C (both directions, n-hop bias, multi-step) | #757 | 5 | in research |
| W9 | Discourse memory (witnessed turns into orientation) | #759 | 5 | in research |
| W10 | `BEGIN ATOMIC` — in-database dependency enforcement | #764 | 1 | in research |
| W11 | Corpus seeding through finished lanes | #761 | 7 | pending W2 |
| W12 | `source_roster` returns bootstrap rows, not source content | #760 | — | small, specify inline |

## How to use this directory

- **Starting work:** read the workstream document end to end before opening an
  editor. Its §5 tells you which files to read first; its §7 tells you how this
  work has failed before.
- **Finishing work:** the acceptance section is the definition of done. If you
  cannot demonstrate every item, the work is not done — say which items are
  outstanding rather than declaring completion.
- **Disagreeing with a document:** the running system outranks it. Verify at the
  layer the claim lives on, then fix the document in the same change that proves
  it wrong. A stale spec that nobody corrected is how this repo accumulated the
  drift these documents exist to remove.
- **Adding a workstream:** same shape, add a row above, link the issue both ways.
