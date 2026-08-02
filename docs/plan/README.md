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
| W5 | [Evaluation harness](W5_Evaluation_Harness.md) | #755 | 1 | specified |
| W6 | [Architecture gates](W6_Architecture_Gates.md) — spec 37 G1–G10, elector invariant | #758 | 1 | specified |
| W7 | [Questions route themselves](W7_Questions_Route_Themselves.md) — relation naming | #756 | 5 | specified |
| W8 | [`infer()` to C](W8_Infer_To_C.md) — both directions, n-hop bias, multi-step | #757 | 5 | specified |
| W9 | [Discourse memory](W9_Discourse_Memory.md) — witnessed turns into orientation | #759 | 5 | specified |
| W10 | `BEGIN ATOMIC` — in-database dependency enforcement | #764 | 1 | see W3 §1; specify when W3 lands |
| W11 | Corpus seeding through finished lanes | #761 | 7 | pending W2 |
| W12 | `source_roster` returns bootstrap rows, not source content | #760 | — | small, specify inline |

## Findings that changed the plan

Research for these specs refuted three claims that were already written down —
two of them mine, from earlier the same day. They are recorded in the specs
rather than quietly corrected, because the wrong version is usually the
appealing one:

1. **"'opening' → `HAS_ECO` is attestations, not code."** Wrong twice over.
   `prompt_coherence` consults no attested alias — name matching is pure C over
   the manifest string — and even a perfect alias would not route that question,
   because `HAS_ECO` hangs off a chess *position* entity that is never a prompt
   token's synset. See [W7 §0](W7_Questions_Route_Themselves.md).
2. **"The four electors."** There are **five** — `infer.sql.in` became one the
   same night the invariant was documented, while the prose still said four.
   The drift the gate is meant to catch, committed hours after it was described.
   See [W6 §1](W6_Architecture_Gates.md).
3. **Spec 37's OP9 prescription** ("`realize(id)` becomes
   `realize(ARRAY[id])[1]`") is refuted by an in-tree measurement: collapsing
   the scalar into a batch wrapper makes every remaining scalar caller **3.2×
   slower**. Two bodies is correct; the win comes from moving *callers* off the
   per-row shape, not from deleting a body.

The pattern is worth naming: **every one of these was a plausible claim written
by someone who had not run the measurement.** That is what these documents exist
to stop.

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
