# 37 — State of the build, and the path to a finish line

Audit session, 2026-07-26. Origin: *"audit the memories, notes, scratchpad(s), etc. and see
where we really stand and start to get a prioritization and path towards a finish line."*

Method: read the memory store (76 files), `docs/INDEX.md`, all 14 specs, the 27 scratchpad
logs, and all **200 open GitHub issues**; then verified the load-bearing claims **live** —
against the running cluster, the CI history, and `git`. Every status below is a measurement
taken on 2026-07-26, not a doc citation.

**Standing caveat:** the tree is shared and several sessions were active during this audit
(8 live worktrees, a CI run in flight). Nothing here was changed except this doc, the
`docs/INDEX.md` entry for it, and one memory-index line.

---

## 0. The one-paragraph answer

The **invention is not the bottleneck and has not been for a while**. In the last 24 hours
the single biggest conceptual gap in the whole system — spec 36's S7, "meaning steers
emission" — went from *unwritten* to *committed*. What is now blocking is unglamorous and
entirely mechanical: **the substrate database is empty**, `main` is **red**, the test layer
was **stamping failures as passes** until yesterday, and the backlog is **200 undifferentiated
issues** that no longer reflect the spec-36 reframing. The finish line is reachable, but the
next work is *verification and ops*, not invention. One more thing is genuinely missing rather
than broken: the **monetization pillar has no tracker presence at all**, despite being named
the top priority on 2026-07-22.

---

## 1. Live state — verified 2026-07-26

### 1.1 The substrate is EMPTY. This is the top blocker.

```
psql -l                 → laplace exists (recreated by CI's DB job, 06:05Z today)
entities        = 41         (bootstrap mandate rows only)
attestations    = 1
consensus       = 0
physicalities   = 0
```

The database was **dropped** and re-created empty. What was lost:

| Seeded | When (verified via `gh run list`) | Scale |
|---|---|---|
| Foundation ladder | run 30175492643, success 2026-07-25 21:21Z | unicode→semlink, 10 sources |
| Knowledge (incl. wiktionary) | run 30178786715, success 2026-07-25 23:05Z | wiktionary alone was +5.85M entities / +10.34M attestations, 10m27s (spec 36 §4 F8) |

Corroborating facts:
- `journalctl -u laplace-postgresql`: **restart counter is at 15**; PG bounced at 05:55Z and
  again at 06:05Z. (Known behavior — see memory `ci-bounces-shared-pg`.)
- `laplace.yml`'s seed job is **`dispatch-only; idempotent, no-op when present`** and shows
  `skipped` in every recent run. **Nothing reseeds automatically.** The substrate stays empty
  until a human dispatches `seed-foundation.yml` / `seed-knowledge.yml`.
- Source corpora are **intact**: all 23 dirs present under `/vault/Data`; `/vault` 48% used,
  `/opt/laplace` 7% used (241G free). No data-acquisition work is owed.
- Two prior commits show this has happened before and was papered over rather than prevented:
  `9c57063 fix(ci): a dropped DB must not make every push red forever` and
  `41cf20c fix(publish): publish tolerates unseeded substrate`. **The system now tolerates an
  empty substrate instead of noticing one.**

**Why this is #1 and not a footnote:** the highest-value commit in the tree is gated on it.
`8764429 feat(converse): S7 — the walk frontier steers emission, not a constant` closes with:

> **UNVERIFIED.** The gate before wiring it into `chat()`: different seeds must produce
> DIFFERENT text (recombination, not replay), and that text must stay on topic. **Both need a
> seeded substrate.**

So does #588 (write-path perf gates red against the live DB), every `:verify_step`, and rule 5
of spec 36 §5 ("conversational claims are proven at the conversation layer"). An empty
substrate does not block *coding*; it blocks **every claim of done**.

### 1.2 `main` was red for 7 runs — **resolved during this audit**

> **UPDATE, same session:** run **30190371842** (06:02Z) completed **success**. `main` is
> green. #678's fix was the cure and red was simply lagging it. Recorded below as measured
> because the *pattern* matters even though the instance closed.

`gh run list`: failure at 2026-07-25 21:15Z, 21:29Z, 23:04Z, and 2026-07-26 03:10Z, 05:07Z,
05:34Z, 05:53Z. One run in flight at audit time.

Failing job is always `Integration tests — pg_regress || dotnet`; every other job is green
(policy, build, ctest, deploy, DB migrate). Root cause of the last one, from the diffs:

```
extension/.../expected/chess_read.out
-NOTICE:  ✓ chess_read: name folding is content-addressed …
+ERROR:  FAIL: expected 2 SANs scoped to this game, got 0 (context leak?)
test layer failed (ctest-regress_rc=8 dotnet_rc=0)
```

A fix for exactly this is **already on origin/main** (`334564c fix(test): chess_read SAN
surfaces never rendered, so the scope assertion counted 0`, merged as #678) — so red is
lagging the fix, not unexplained. Verify the in-flight run before treating this as open work.

### 1.3 The most important CI finding: green was partly fake

`522aa0c fix(ci): the test layer swallowed failures and stamped them as passes` (merged #676,
yesterday). Combined with the already-recorded `#625` fix (11 data-gated tests early-returning
into false PASS) and memory `validate-clean-builds-getdocument-stderr` (incremental builds skip
`getdocument` ⇒ false green), the honest reading is:

> **No pre-2026-07-25 "green" claim in this project should be trusted without re-running it.**

That is not a defect to fix — it is already fixed. It is a **calibration instruction** for
reading the backlog: every issue whose status rests on a passing gate from before yesterday
needs re-measurement, not archaeology. `#588` (5 write-path gates RED on main vs the live DB)
is the clearest instance and cannot even be re-measured until §1.1 is resolved.

### 1.4 Working-tree attribution (do not stomp)

`main` locally is **ahead 3, behind 10**.

- **Ahead 3** — chess ingest perf, authored 05:12Z–05:37Z *today*, unpushed:
  `64c9e8d` buffered move generation · `25e5915` drop per-ply HAS_SAN · `b7f6775` T0-equivalent
  vocabulary cache. Another session's live work.
- **Behind 10** — merged PRs #673–#678, including the S7 commit and the CI/test fixes above.
- **Risk flagged, not acted on:** `25e5915` *drops* per-ply `HAS_SAN`, while `334564c` on
  origin/main *fixes the test that asserts HAS_SAN renders*. These two touch the same contract
  from opposite directions and will need reconciling by whoever owns the chess perf branch.
- 8 worktrees under `.claude/worktrees/` plus a separate `/home/ahart/Projects/Laplace-ui`
  checkout on `fix/serilog-getdocument-stderr`.

---

## 2. Where the invention actually stands

### 2.1 The forward pass (spec 36) — the gap closed while nobody was looking

Spec 36 (2026-07-25) is the most important doc in the tree: it retired the "open research
question" framing and replaced it with a numbered ladder S0→S10. Status re-measured against
`git log`, 2026-07-26:

| Stage | Spec 36 status (07-25) | Now | Evidence |
|---|---|---|---|
| S0 decompose | ✅ | ✅ | `prompt_state` |
| S1 disambiguate | ❌ missing | 🟡 **built, unverified** | `401b233 fix(lexical): restore the direct context lane senses() lost` |
| S2 orient | ⚠️ degenerate (F2: "parts" beat "car") | 🟡 **fixed, unverified** | `5790a64 breadth picks the TOKEN, denote_mu picks the SENSE`; `ec9fad5 orient in hash space` |
| S3 intent | ❌ missing | ❌ **missing** | frame-evocation lane (`EVOKES_FRAME`, 25,715 edges) resident and unused |
| S4 retrieve | ✅ | ✅ | `walk_branches` |
| S5 compose | ⚠️ partial | ⚠️ partial | spec 18 typed strata |
| S6 propose | ✅ | ✅ | `steered_walk.c` |
| **S7 steer** | ❌ **"the one stage nobody has written"** | 🟡 **BUILT, UNVERIFIED, unwired** | `8764429` → `converse_compose`; additive on purpose, `converse_walk` untouched |
| S8 sample (RD as temperature) | ❌ missing | ❌ **missing** | — |
| S9 render | ⚠️ F8 band-vs-family defect | 🟡 **fixed** | `ec9fad5 select by relation family`; `9af2344 answer in the language that was asked` |
| S10 close | ✅ | ✅ | caller-side deposit |

**Read that table as the headline it is.** Of the six defects spec 36 isolated live (F1–F9),
four have landed fixes in ~24 hours. What remains of the ladder is **S3, S8, the wiring of S7
into `chat()`, and the four gates of spec 36 §5** — all of which are *verification-gated on a
seeded substrate*, per §1.1.

Also worth preserving: `b932050 docs(spec36): the ladder is one program, not the machine` —
the ladder is one *program* over an instruction set (RESOLVE/DECOMPOSE/COMPOSE/ATTEND/FOLD/
RANK/SAMPLE/REALIZE/WITNESS), not a hardcoded pipeline. The unbuilt piece there is the
**opcode resolver**: `query_shapes()` publishing 14 English strings is the same defect as the
retired English intent regex, one level up.

### 2.2 Pillars 1–3 (from memory `product-pillars-billing`, 2026-07-22)

| Pillar | State |
|---|---|
| **1. Substrate query** (converse/recall/walk/facts; OpenAI-compat + MCP) | Deepest built. Blocked only on §2.1's residual + verification. |
| **2. Model audit / interpretability** | Lane exists end-to-end (`ModelDecomposer`, `model_factor.c`, HeadClassifier→ENCODES, `model_jitter_catalog()`), but carries the **largest correctness backlog: 31 open `model-lane` issues**, several of them witnessing defects (#540 `hidden_act` never read; #541 norm epsilon hardcoded; #543/#545 profile mismaps; #479 shards silently unioned; #481 unknown model_type silently falls back to Llama). #488 `model_forward v0` was **merged while labeled UNVERIFIED**. |
| **3. Model export ("build-a-bear")** | Spine exists (`engine/synthesis` + `engine/dynamics` + `FoundryCommands`). No external validation: **#111/#112 — llama.cpp has never been vendored and an exported GGUF has never been proven to load and generate.** That is the single most valuable un-run experiment in the repo. |
| **Monetization** (operator's stated #1 gap, 07-22) | **Not in the tracker at all** — see §3. |

### 2.3 Chess — the proving domain is the most actively worked area

Nine of the last twenty commits are chess. The lane has moved from caches to folds
(`9dfdf21 serve the roster from the fold — the cache is gone`, `0d01cf1 retire
chess_leaderboard + chess_opponents`, `21637b3 delete constituent_edges — a hand-refreshed
copy of the trajectory`) — i.e. the *"a cache means a missing fold"* law being applied
repeatedly and correctly. But **#495 "live end-to-end verification debt"** is still open, and
#512 (board geometry ladder) / #547 (game-tier packed trajectory) are still open
`priority:high` `substrate-law` items — meaning the spec-11 ladder is not finished.

---

## 3. The gap nobody is tracking: monetization

Memory `product-pillars-billing` (2026-07-22) records the operator's words: *"billing and how
I can make money are lacking severely"*, and the requirement that billing
**self-provision after install out of the box** (Stripe products/prices/webhooks/metering
created idempotently by the system, never hand-made in the dashboard).

**Searched all 200 open issues for billing/stripe/tenant/auth/api-key. Result:**

- `#531` — billing: `TryConsumeCredit` belongs in an `app.consume_credit()` function → an
  internal refactor, `priority:low`.
- `#489` — chess HTTP surface unauthenticated (`priority:high`).
- `#550` — live Lichess token in on-disk plaintext.
- `#436` — governance analysis before the write path opens beyond the operator.

**There is no issue for:** Stripe self-provisioning, API-key authentication, tenant-scoped
data isolation, metering per pillar, or entitlements. The memory also records that
*"data is secure because it's filtered behind tenant/user id"* is **aspirational — tenant is a
spoofable header**, and that closing that gap is *the same build* as real billing.

So the stated top-priority product gap has **zero tracker presence**, while 31 issues track
model-lane witnessing details. That is the sharpest prioritization inversion the audit found.

---

## 4. Backlog: 200 issues, and it is not a plan

| count | label | count | label |
|---|---|---|---|
| 50 | area:app | 26 | priority:high |
| 37 | type:enhancement | 25 | area:engine |
| 35 | area:extension | 25 | read-side |
| 35 | type:bug | 25 | tracker-migration |
| 34 | ingest | 21 | substrate-law |
| 31 | model-lane | 20 | priority:low |
| 31 | priority:normal | 15 | story |
| 28 | perf | 11 | foundry / 11 spike |

Structural problems with the list, independent of any single issue:

1. **26 `priority:high` is not a priority** — it is a quarter of the tracker.
2. **25 `tracker-migration`** issues were bulk-imported from `.scratchpad/02` on 2026-07-18
   and carry pre-spec-36, pre-campaign framing.
3. **The list predates the reframing.** Several issues are *already retired as framing* by
   spec 36 §6 but remain open — e.g. **#375** ("Does consensus × geometry × trajectory route
   as well as trained attention at depth") is explicitly retired as a *gating* question and
   redesignated a post-S7 measurement, yet sits open as a `spike`.
4. **Status rests on stale gates** — see §1.3. Anything asserting green needs re-running.
5. Issues have accumulated to 200 with no closing pressure; the last three campaigns
   (`.scratchpad/34`, `35`, `36`) each *drained into* the tracker and none drained it.

---

## 5. Doc & memory hygiene (cheap, do it in passing)

| Finding | Detail |
|---|---|
| `docs/INDEX.md` is stale | Missing `.scratchpad/35_Tech_Debt_Refactor_Campaign` and `.scratchpad/36_LSP_Semantic_Decomposer_Design_GH593` — both exist, 36 dated 07-25. **Fixed in this pass** (this doc added too). |
| Memory index drift | 76 memory files, 75 indexed. `feedback-commit-and-keep-working.md` (07-24) had no `MEMORY.md` pointer. **Fixed in this pass.** |
| Memory store itself | Healthy — no contradictions found, no stale-file claims contradicted by live code in the ones checked. The 07-25 additions (`enumerate-before-declaring-absence`, `stall-language-is-a-tell`, `forward-pass-order-of-operations`) are precisely the lessons spec 36 §4 earned. |
| `.scratchpad/27a–d` | Already annotated `status column STALE — GH issues are authority`. Correct as-is. |
| `docs/decisions/0001-highway-bit-order.md` | Still **PROPOSED, operator decision pending** since 07-18, and it gates `#551` (`priority:high`) and the reseed class `#399`/`#413`. A one-line decision unblocks three issues. |

---

## 6. Prioritization — what the finish line actually requires

Ordered by *what unblocks the most*, not by severity of the item itself.

### P0 — Restore the ability to prove anything (hours, mechanical)

1. **Reseed the substrate.** Dispatch `seed-foundation.yml` then `seed-knowledge.yml`. Corpora
   are resident; the ladder is marker-gated and idempotent. Until this is done, *no*
   conversational, perf, or foundry claim can be made.
2. **Make an empty substrate loud.** The two "tolerate a dropped DB" commits removed the alarm
   without addressing the cause. A dropped substrate should fail a smoke gate with a named
   error, not silently skip a seed job. (New issue owed.)
3. ~~Confirm `main` is green.~~ **Done during this audit** — run 30190371842 succeeded (§1.2).
4. **Reconcile the 3 unpushed chess perf commits** against #678's HAS_SAN contract — owner is
   whoever holds that branch.

### P1 — Close the forward pass (the invention's finish line)

5. **Verify S7** against the reseeded substrate, on the gate its own commit message names:
   different seeds ⇒ different text; text stays on topic. Then **wire `converse_compose` into
   `chat()`**.
6. **Build S8** (RD as temperature) — sampling is the other half of S7 and is a defined
   contract, not research.
7. **Build S3** (intent via frame evocation) — `EVOKES_FRAME` 25,715 edges are resident and
   unqueried. This is doc-22 Phase B / #358.
8. **Assert spec 36 §5 as gates**, all four: one pass · no silent stage skip · every published
   shape reachable and distinct from `describe` · templates labeled as fallback in the
   envelope. Rule 3 in particular is a *build-breaking* gate that would have caught F5 months
   earlier.
9. **Re-verify S1/S2** at the conversation layer post-reseed — spec 36 §4 F9 proved sense
   selection is *evidence-sensitive*, so a seed changes the answer. Re-measure before
   attributing anything to code.

### P2 — Make it sellable (the operator's stated #1 gap; currently untracked)

10. **File the monetization epic** and its children: Stripe self-provisioning (idempotent
    catalog/prices/webhooks/metering at install), API-key auth, tenant-scoped isolation
    (retire the spoofable header), per-pillar metering, entitlements. Nothing here exists in
    the tracker today.
11. **#489** unauthenticated `/chess/*` and **#550** plaintext Lichess token — these are the
    same build as #10 and are the only auth items currently tracked.

### P3 — Prove pillar 3 end-to-end (highest-value single experiment)

12. **#111 → #112:** vendor llama.cpp, then load an exported substrate GGUF and generate.
    Every foundry claim in the catalog rests on an inference that has never been run
    externally. This also finally makes spec 36 §6's retired research question *measurable*
    (#375) instead of open.

### P4 — Pillar 2 correctness sweep (batch it)

13. The witnessing defects are individually small and collectively decide whether pillar 2 is
    honest: **#540** hidden_act, **#541** norm epsilon, **#543/#545** profile mismaps,
    **#479** shard index ignored, **#481** silent Llama fallback, **#537** quantized-container
    refusal, **#488** verify `model_forward v0`. One campaign, one reseed of the model lane.

### P5 — Triage debt, then stop adding to it

14. **Drain the tracker rather than feed it.** Concretely: close #375 as retired-by-spec-36;
    re-scope the 25 `tracker-migration` issues or close them; force `priority:high` down to
    ≤10 by demoting anything not on P0–P3; and get the decision on
    `docs/decisions/0001-highway-bit-order.md` (unblocks #551, #399, #413 with one line).
15. Residual known work, already well documented and correctly deferred: `.scratchpad/35`'s
    remainder (#620/#621 research cores, #627 profile-gated perf), #595 O(n²) grammar_compose,
    #596 malformed-encoding crash, #588 write-path gates.

---

## 7. What "finished" means — proposed definition

The project has no stated definition of done, which is why 200 issues can all feel mandatory.
Proposed, for the operator to accept or rewrite:

> **Laplace is finished-v1 when, on a freshly seeded substrate, all three of these hold:**
>
> 1. **Converse.** `chat()` runs S0→S10 with no silent stage skip; every published shape is
>    reachable and distinct; the reply to "What is a dog?" is *generated* (different seeds ⇒
>    different text, on topic, grounded, with `eff_mu`/witnesses returned) rather than a
>    template — and the four gates of spec 36 §5 are asserted in CI.
> 2. **Export.** A scoped pour produces a GGUF that loads in llama.cpp and generates, and a
>    source/context-filtered pour differs from the default pour in the predicted direction
>    (#112, #114).
> 3. **Sell.** A new install self-provisions its Stripe catalog, authenticates an API key,
>    scopes every read to a tenant that cannot be spoofed, and meters the three pillars.
>
> Everything else — 31 model-lane witnessing details, 28 perf issues, the modality ladders
> (#527), the encyclopedic lane (#372) — is *coverage widening*, not the finish line.

Note the shape of that: item 1 is ~4 stages from done, item 2 is one un-run experiment, item 3
is unstarted and untracked. **Monetization is the long pole**, and it is the one thing nobody
has written an issue for.

---

## 8. Standing risks to re-read before the next session

- **An empty substrate is now silently tolerated** (§1.1). Until that alarm exists, assume the
  substrate may be empty and check before claiming any live result.
- **Pre-07-25 green is not evidence** (§1.3).
- **The tree is shared.** 8 worktrees, unpushed commits at HEAD, CI bounces PG mid-run.
  Attribute `git status` before touching anything (memory `shared-tree-concurrent-sessions`).
- **A seed changes conversational answers** (spec 36 F9). Never attribute a conversational
  result to code without re-measuring after a reseed.
