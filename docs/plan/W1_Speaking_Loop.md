# W1 — The speaking loop: wire the steered lane into chat

**Issue:** #751 · **Plan:** `COMPLETION_PLAN.md` R1 / Phase 2 · **Blocks:** every
"does it converse" question · **Blocked by:** nothing (the machinery exists)

---

## 1. Why this exists

The system comprehends and cannot talk. Ask it a question and it fills a frame
from consensus (correct, useful, not generated). Ask for the walk shape and it
emits fragments — measured 2026-08-01, `chat(shape='walk')` on "What is rain?":

> *drops of a month is extremely diverse , golf balls, and in shining gutters .
> \_pluere\_, dew. as 1822 ,*

Every fragment is real witnessed corpus. The composition is not steered. This
document specifies why, and what to do, and it is short on new construction
because **the steered generator already exists and is not called.**

## 2. How it works today

### 2.1 The call chain

```
chat(prompt, …, shape='walk')                   chat.sql.in:26
  ctx := session_last_resolved(session)         chat.sql.in:49    ← one topic id
  lang := argmax Σ eff_mu over HAS_LANGUAGE     chat.sql.in:81-90
  topic election (cands)                        chat.sql.in:97+   ← NOT used by walk
  shape NOT IN (…,'walk',…) → recall_intent     chat.sql.in:384   ← walk excluded
  out := converse_walk(prompt, 40, NULL)        chat.sql.in:389-410  ← p_seed NULL,
                                                                       p_lang dropped
converse_walk                                   converse_walk.sql.in
  ORIENT   prompt_coherence → (topic_word, syn)  :58-66
  GATHER A topic's gloss, band 1                 :101-106   weight = 50  (CONSTANT)
  GATHER B containers_of(topic_word,1,400) t3    :107-114   weight = 40  (CONSTANT)
           trajectory_unpacked_points(gid)       :136-139
           SENT sentinel per gid                 :171       weight = 0
  SEED     rng := md5(prompt) when p_seed NULL   :194
  WALK     steered_walk_raw(stream,w,core,starts,40,5,rng)  :198-199
  REALIZE  render_text_batch over emitted ids    :209-230
steered_walk_raw → pg_laplace_steered_walk      steered_walk.c:157
  intern stream → int32 vocab                    :213-241
  trigram (a,b)→positions, bigram b→positions    :255-275
  seed from core_seq[0..1] if present            :278-283
  per step: trigram lookup, else bigram backoff  :344-420
  score = sw_score(weight, rng, pos)             :367,388
          = weight * (|rng + pos*2654435761| % 100000)   :142-150
```

### 2.2 Why it is unsteered — three independent facts

1. **The weight vector is provenance, not meaning.** Every token from the
   topic's own gloss carries 50; every token from a containing sentence carries
   40; forever, for all prompts (`converse_walk.sql.in:103,107,171`). A ratio of
   1.25 multiplied by a uniform hash in `[0,100000)` loses the comparison a large
   fraction of the time. Selection is effectively a seeded-random pick among
   attested successors of the current bigram. This diagnosis is already written
   in the tree by a previous session — `converse_compose.sql.in:3-11` states it
   verbatim: *"its steer is a CONSTANT … That is a provenance weight … not a
   semantic one … The walker is therefore unsteered in the only sense that
   matters."*
2. **The walker cannot steer even in principle.** `steered_walk.c:8` — *"Pure
   computation over the arguments — no SPI."* It never reads `consensus`, never
   calls `steer_candidates`, and has no access to the topic id. Any steering must
   arrive **pre-baked in `p_weights`.**
3. **The anti-repeat set forces incoherence.** The visited-trigram exclusion
   (`steered_walk.c:365,412-415`) actively pushes the walk to splice across
   unrelated gathered sentences at shared bigram pivots. Constant weights plus
   forced splicing is precisely a word-salad generator.

Two further defects in the same lane: `chat` passes `p_seed => NULL`
(`chat.sql.in:410`) so output is **deterministic per prompt** — there is no
sampling diversity at all; and `p_lang` is never threaded into `converse_walk`,
so the language-of-the-request fix documented at `chat.sql.in:51-56` does not
cover this branch.

### 2.3 The steered generator that exists and is not called

`walk_continuations` (`generation/walk_continuations.sql.in:1-7` →
`trajectory_generate.c:66,169`) is a complete S6→S7→S8 loop:

```
S6 PROPOSE  trajectory_continuations over trailing k, backoff k=max_order..1
                                                    trajectory_generate.c:250-293
   FLOOR    walk_completes_floor when dry            :296-327
S7 STEER    steer_candidates(cands, frontier)        :331-378
   COMBINE  eff = weight * (edges>0 ? steer : 1.0)   :386-399
            edges>0 && steer<=0  → EXCLUDED
S8 SAMPLE   Gumbel: key = -log(u)/eff^(1/temp)       :417-431
```

`steer_candidates` (`generation/steer_candidates.sql.in:33-37` →
`steer_candidates.c:119`) computes coverage-weighted signed consensus mass
between a candidate set and a frontier — attention scores from rated evidence.
Its only caller in the entire tree is `trajectory_generate.c:346`.

**`chat` never calls `walk_continuations`.** The two generation lanes are
disjoint: `walk_text`/`generate`/`continue_text` and the C# `WalkTextAsync`
reach the steered lane; the conversational surface does not.

### 2.4 Two installed bridges with zero callers

- **`converse_compose`** (`converse_compose.sql.in:41-45`, body calls
  `steered_walk_raw` at `:180`, installed at `manifest.install:184`) — builds a
  **frontier-derived** weight vector for the same walker contract, and takes
  `p_lang` (`:43`). Its header `:37-40` says: *"NOT YET MEASURED … Do not wire
  this into chat() until that run exists."* **The measurement was never run.**
  This is the smallest available fix in the entire plan.
- **`converse_tiered`** (`converse_tiered.sql.in:53`, installed at
  `manifest.install:185`) — sentence-tier composition. Unwired by the hotfix
  comment at `chat.sql.in:429-436`, which claims its fixes "exist on
  perf/content-ladder-ledger and did NOT make this merge." **That comment is
  stale:** `ceef97d` landed in `main` on 2026-07-28 and fixed all three
  measured hangs (`containers_of` >100s arm removed; `top_synset` 73s replaced
  by an indexed sense-family join, 2.67s, identical 66 concepts; the dead
  shared-concept steer). `dog` went from hang to 4 clauses in 5.3s.

### 2.5 The published contract does not describe the behavior

`query_shapes.sql.in:22` publishes `walk` as *"greedy strongest-edge chain from
the topic"* — that is `recall_intent('walk', …)`. `chat.sql.in:384` explicitly
excludes `walk` from that dispatch and routes to `converse_walk`. A caller
reading the published surface gets a different function than the one that runs.

## 3. How it should work

A conversational turn is a **loop of forward passes**, not one lookup:

```
ORIENT     elect the topic + sense + named relation        (prompt_coherence)
PROPOSE    surface distribution over next tokens           (trajectory_continuations)
STEER      score each candidate against the concept        (steer_candidates)
           frontier — attention from rated evidence
COMBINE    eff = surface_weight × steer                    (never argmax yet)
SAMPLE     Gumbel with temperature                         (collapse ONCE)
EXTEND     append, advance the frontier
REPEAT     until sentinel or budget
WITNESS    deposit prompt + response, fold                 (the OODA close)
```

Two design rules this lane must obey, both already law elsewhere in the tree:

- **The distribution is carried, not collapsed, until the sampling step.** The
  current walker collapses at every step by construction (a scalar weight into a
  hash comparison). The steered lane does it correctly.
- **The steering signal must be pre-baked before the walker.** `steered_walk.c`
  has no SPI by design and that is correct — the frontier scoring belongs in the
  SQL/native gather, exactly where `converse_compose` puts it.

**The frontier must actually advance.** In `trajectory_generate.c`, `frontier`
is filled once from the input context (`:220,232`) and `n_frontier` is never
incremented; `ctx` grows at `:447` but `frontier` does not. So
`steer_candidates` is re-run every step against the **fixed prompt ids** —
a static prior recomputed identically, which is precisely the S7-vs-prior
distinction the header at `:17-19` and `steer_candidates.sql.in:4-6` claim to
avoid. Both comments overstate the code.

## 4. What to consider

| Decision | Options | Notes |
|---|---|---|
| Which bridge first | `converse_compose` (small, same contract, gated only on an unrun measurement) vs `walk_continuations` (the full S6-S8 loop, different contract) | Run the `converse_compose` measurement first — it is one run and it either passes or tells you the frontier weights are also insufficient. Do not skip it: its own header forbids wiring before measuring, and honoring that is the cheapest way to avoid repeating this lane's history. |
| Seed | session-derived vs per-call random vs NULL (today) | Deterministic-per-prompt is defensible for tests and indefensible for conversation. Thread a session-derived seed so a repeated prompt in one session varies, and regress tests can pin a fixed seed. |
| `p_lang` | thread it | `converse_compose` already accepts it; `converse_walk` has no parameter for it. Whichever wins must carry language, or the Bulgarian-gloss failure documented at `chat.sql.in:51-56` returns through this branch. |
| Frontier growth | fix `n_frontier`, or leave static | Fix it. A static frontier makes S7 a prompt prior, not a steering signal, and the comments already claim the behavior that the fix would deliver. |
| `converse_tiered` | rewire for `describe` under a latency budget | Its fixes are in `main`. Re-measure on `dog`/`car`/`glacier` before wiring; the hotfix that unwired it was correct **at the time** and its rationale is now stale, not wrong-in-hindsight. |
| `kappa`, `covered` | pass / fetch or delete | `steer_plan` binds 2 args (`trajectory_generate.c:112-116`) so `p_kappa` always defaults; the `covered` OUT column (`steer_candidates.sql.in:35`) is never selected (`:114`). Either use them or remove them — an unused knob is a lie about tunability. |
| Timeout | none today, deliberately | `chat.sql.in:391-400` explains: a `SET LOCAL` budget needs an exception handler, and PostgreSQL reports statement timeout and client cancel with the same condition (57014), so the handler eats the operator's Ctrl-C. Do not add one without solving that. |

**Trap:** do not "fix" the walker by giving it SPI. Its purity is what makes it
`IMMUTABLE STRICT PARALLEL SAFE` (`steered_walk_raw.sql.in:4-14`) and cheap.
The steering belongs in the caller.

## 5. Where to look

| Concern | File |
|---|---|
| Dispatch, shape branches, stale comment | `sql/functions/converse/chat.sql.in:384-436` |
| Unsteered gather + constant weights | `sql/functions/converse/converse_walk.sql.in:101-199` |
| The pure walker | `src/steered_walk.c` (`:8` no-SPI, `:142-150` score, `:344-420` step) |
| The steered loop that is not called | `src/trajectory_generate.c:242-448` |
| Attention from rated evidence | `src/steer_candidates.c:119`, `sql/functions/generation/steer_candidates.sql.in` |
| Bridge 1 (frontier weights, has lang) | `sql/functions/converse/converse_compose.sql.in:3-11,37-45,180` |
| Bridge 2 (sentence-tier, perf-fixed) | `sql/functions/converse/converse_tiered.sql.in`, fix commit `ceef97d` |
| Published-vs-actual shape | `sql/functions/converse/query_shapes.sql.in:22` |
| Existing generation tests (counts only) | `tests/sql/generation_corpus.sql:110-186` |

## 6. Acceptance

Behavioral, held-out, and scored by W5's harness — not "it looks better":

1. `converse_compose`'s measurement exists and is recorded (latency + output) on
   at least `dog`, `car`, `glacier`, `pawn`, `rain`.
2. `chat(shape='walk')` returns **on-topic, multi-sentence** output on the
   seeded fixture for ≥5 prompts never used during development.
3. The same prompt in the same session, called twice, differs (seed threading).
4. A prompt asked in a non-English language is answered from that language's
   surfaces (lang threading).
5. `query_shapes` describes what `chat` actually runs.
6. A regress test exists that **fails** if the walk shape emits salad — pinned
   deterministic-seed output on a fixture. Today no test in the tree would fail
   (`converse_walk` appears in zero tests).

## 7. Risks

- **Latency.** `converse_walk('dog', 40)` already measured past the default
  statement timeout (`chat.sql.in:384-388`). The steered lane adds an SPI call
  per emitted token. Budget it before wiring, or the fix trades salad for
  timeouts.
- **Corpus dependence.** Fluency is bounded by what has been read; on the
  foundation-only seed even a correctly steered walk will be thin. Do not judge
  this work's success before W2 lands corpus. Judge it on *on-topic-ness*, which
  is steering, rather than on eloquence, which is data.
- **This lane has burned every previous attempt by shipping on the example it
  was developed against.** `converse_tiered` "answered only for `pawn`, which
  has few senses and few usages — the single topic it was verified on before
  being wired in" (`chat.sql.in:415-418`). Held-out probes are not optional here.
