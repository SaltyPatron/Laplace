# docs/plan — the work, specified

## Current product finish line

[REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md](REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md)
is the measured 2026-08-08 definition of done for MCP/OpenAI conversation, code,
heterogeneous-model consensus, and export. It supersedes older live-state
checkpoints for those claims. In particular, the 2026-08-05 "41 entities"
checkpoint below is historical; the 2026-08-08 host was measured at roughly
4.34 million entities with PostgreSQL and the API healthy.

## Scope — read this before treating the plan as complete

`COMPLETION_PLAN.md` and this directory cover **one axis**: the conversational
and inference path — election, generation, the lanes that feed it, and the gates
that would keep it honest. That is a slice of the open GitHub backlog, not the
whole invention.

It is **not** a plan for the whole backlog. Full-backlog kill order (every open
issue, invention-ranked, verified against code + live DB on 2026-08-06) lives in
[BACKLOG_KILL_LIST_2026-08-06.md](BACKLOG_KILL_LIST_2026-08-06.md). Prior triage
ledgers (including `.scratchpad/42`) are historical hypotheses — re-verify, do
not shrink scope from them. Doc 37 is the drift baseline only. An earlier
framing of this directory as "current state → finish line" overstated it; the
honest description is "current state → a system that converses, with the
evidence to prove it."

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

## Resume checkpoint

**Historical stage:** [CHECKPOINT_2026-08-05.md](CHECKPOINT_2026-08-05.md) —
live box re-measured empty (THIN_SUBSTRATE), tracker drift 195→232, W5/W6
status corrected against code/GH, ranked next work. Historical landing record
for #771–#776 remains in [CHECKPOINT_2026-08-02.md](CHECKPOINT_2026-08-02.md).

## The workstreams

| # | Workstream | Issue | Phase | State |
|---|---|---|---|---|
| W1 | [The speaking loop](W1_Speaking_Loop.md) — wire the steered lane into chat | #751 | 2 | specified |
| W2 | [The document lane](W2_Document_Lane.md) — Pillar 0, identity/names/typed edges | #754 | 4 | specified |
| W3 | [Self-ingest call graph](W3_Self_Ingest_Call_Graph.md) — the substrate reads its own source | #765 | 1 | **partial** — `.sql.in` discovery (#774); structural `CALLS`/`DEFINES` still zero |
| W4 | [Sense-election ground](W4_Sense_Election_Ground.md) — tier seam + sense priors | #752, #753 | 3 | specified |
| W5 | [Evaluation harness](W5_Evaluation_Harness.md) | #755 | 1 | **partial** — runner + probes + CI wire landed (`eval-generation.py`); reopened #755 now owns the seeded HTTP/MCP/OpenAI, conversation, code, model-consensus, and export product gate; behavioral acceptance remains unproven |
| W6 | [Architecture gates](W6_Architecture_Gates.md) — spec 37 G1–G10, elector invariant | #758 | 1 | **#758 CLOSED** (#829); G2/G5/G10 landed (#876, grandfathered); G4 destination still #765 `CALLS` in-degree |
| W7 | [Questions route themselves](W7_Questions_Route_Themselves.md) — relation naming | #756 | 5 | specified |
| W8 | [`infer()` to C](W8_Infer_To_C.md) — both directions, n-hop bias, multi-step | #757 | 5 | specified |
| W9 | [Discourse memory](W9_Discourse_Memory.md) — witnessed turns into orientation | #759 | 5 | specified |
| W10 | `BEGIN ATOMIC` — in-database dependency enforcement | #764 | 1 | see W3 §1; specify when W3 lands |
| W11 | Corpus seeding through finished lanes | #761 | 7 | pending W2 |
| W12 | `source_roster` returns bootstrap rows, not source content | #760 | — | **code landed** (#773); close after live ChessPgn/ChessOpenings recheck |
| — | Foundation seed / prove #776 on live ladder | #777 | 0 | **ops gate** — this host is unseeded (0 journal rows, 41 entities), not orphan-Unicode; see CHECKPOINT_2026-08-05 §1 |
| W13 | [Convergent identity](W13_Convergent_Identity.md) — why everything links to everything | #574 | — | thesis verified, unmeasured |
| W14 | [The machine model](W14_Machine_Model.md) — memory, ISA, and the operator that cycles it | — | 1 | binds specs 15/33/36/37; names four structural gaps |
| W15 | [Election fan-out axes](W15_Election_Fanout_Axes.md) — what an election may rank on | #861, #865 | 3 | **axis inventory measured**; independence classes proposed, remedy unmeasured |

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
4. **The tier-collision seam is not the cause of the letter-A failure** it has
   been credited with in two specs. Measured: `entities` holds exactly **one**
   row for `word_id('a')`, at tier 0 — the UD lane that mints colliding rows was
   never ingested here, yet the failure reproduces. `'a'` legitimately carries
   seven `HAS_SENSE` edges, five of which **tie exactly**, because
   `tag_cnt = 0` maps to a score of 0.5 — a draw. The seam is real but latent;
   the priors are the live defect, which **inverts Phase 3's stated order**.
   See [W4 §0](W4_Sense_Election_Ground.md).
5. **"Filter `senses()` by tier" is not implementable.** `consensus` has no tier
   column — tier exists only on `entities`, and edges attach to the id, which a
   colliding pair shares. Both specs imply a read-side tier fix is available; it
   is not.

The pattern is worth naming: **every one of these was a plausible claim written
by someone who had not run the measurement.** That is what these documents exist
to stop — and #4 was found by a single `SELECT tier, count(*)` that two prior
analyses had not run.

## Starting an agent

[ONBOARDING.md](ONBOARDING.md) holds the starting prompt — written for Cursor CLI
on the Ubuntu host, with the psql invocations verified against this box. Paste
it at the repo root. It deliberately tells the agent to distrust these
documents and verify against the running system, because five claims written
down here as fact have already turned out false.

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
