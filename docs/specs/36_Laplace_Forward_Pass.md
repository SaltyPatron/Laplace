# 36 — The Laplace Forward Pass: order of operations, paired against the transformer

Status: **spec** (binding). Created 2026-07-25. Supersedes the "open research question"
framing in [09](09_Substrate_LM_Synthesis.txt) and the phase list in
[.scratchpad/22](../../.scratchpad/22_Conversational_Engine_Plan.md) — see §6.

## 0. Why this doc exists

Every stage of a conventional transformer has a built Laplace counterpart. What does not
exist is a **defined order in which those stages run**. A transformer is standardized: one
spine, one sequence, every model on earth runs the same eleven steps in the same order. On
the Laplace side there are seven partial engines, each implementing a different subset of
the pass, none composing with the others, and the one wired to `chat()` is the subset that
skips generation entirely.

That is the whole problem. Not a missing capability — a missing **sequence**.

This doc fixes the sequence. §1 and §2 pair the two stacks operation-for-operation. §3
defines the canonical Laplace forward pass as a numbered ladder. §4 is the live evidence of
where the current path breaks. §5 is the standardization contract. §6 retires the stall
language in the older docs.

---

## 1. Training time — ingestion is what gradient descent is for

Gradient descent exists to solve one problem: *find edge weights that make the observed
corpus likely.* It solves it by iterative search because a neural net has no index into its
own corpus — it can only nudge parameters and re-measure. Laplace computes the same
quantity in closed form, per edge, from the evidence, and keeps the provenance.

| Transformer training | Laplace ingestion | Where |
|---|---|---|
| Corpus → learned BPE vocab (lossy, OOV, id≠content) | Corpus → decomposer → content-addressed tier ladder (lossless, no OOV, id **is** content) | `Laplace.Decomposers`, `text_decomposer.c` |
| Random weight init | *No init.* There is nothing to initialize; structure is data | — |
| Forward pass over a batch | Decompose a document → `SubstrateChange` record stream | `IngestBatchPipeline` |
| Loss function vs. a target | *No objective function.* The target is what the source literally asserted | spec 08 |
| Backprop + gradient step | **Glicko-2 fold** — each attestation is one match played | `consensus_fold_step.c`, `glicko2.c` |
| Learning rate / momentum | **RD and volatility** — a per-edge, data-derived step size | `glicko2.c` |
| Epochs until convergence | **Witness count.** RD shrinks as witnesses accumulate | `consensus.witness_count` |
| The learned weight | `eff_mu = rating − 2·rd` | `consensus` |
| Regularization / dropout | Unnecessary — no free capacity to overfit; more evidence only tightens RD | — |
| Catastrophic forgetting | **Impossible** — evidence appends rows, never overwrites | spec 05 |
| Checkpoint file | The `consensus` table | — |
| Fine-tuning | Scoped re-fold (filter by source/context → re-fold) | `FoundryCommands` |
| "Why did it answer that?" — unanswerable | `eff_mu`, `witnesses`, `path` are returned columns | `converse_facts` |

**The load-bearing sentence.** Gradient descent is a *search procedure for a quantity
Laplace measures directly.* The Glicko fold is not an approximation of backprop; it is the
closed-form answer to backprop's question, with an uncertainty term (RD) that backprop does
not even produce. Everything gradient descent yields, ingestion yields — plus provenance,
plus per-edge confidence, plus incrementality, minus the search.

Ingestion is therefore **complete** as a training story. It is not the gap.

---

## 2. Inference time — the pairing, stage by stage

| # | Transformer stage | Laplace counterpart | Built? | Code |
|---|---|---|---|---|
| 0 | Tokenize (BPE) | `prompt_state()` → content ids | ✅ | `prompt_state.sql.in` |
| 1 | Embed lookup (`W_E[t]`) | Entity id **is** the address; no projection needed. Semantic embed (eigenmap) exists for *export*, not for inference | ✅ | `eigenmaps.cpp` |
| 2 | Positional encoding | Hilbert index + trajectory ordinal. `physicalities.trajectory` is the ordered sequence structure — lossless, already resident (1.88M tier-3) | ✅ | `hilbert4d.c`, `trajectory.c` |
| 3 | QK · softmax → relevance over context | Relation-typed edge lookup ranked by `relation_rank × eff_mu × exp(−κ·rd) × witness-saturation` — a normalized relevance distribution that is **sparse and indexed** instead of dense and quadratic | ✅ | `generate_walk.c` (`walk_branches`) |
| 4 | h learned heads | 203 relation types = 203 **named** heads; `highway_mask` = channel bank; salience bands = head priors | ✅ | `highway_mask.c` |
| 5 | OV / value aggregation | The walk's collected frontier | ✅ | `generate_walk.c` |
| 6 | Residual + LayerNorm | Frontier carried across hops; typed strata | ⚠️ partial | spec 18 |
| 7 | FFN (key–value memory) | The substrate *is* an explicit KV memory | ✅ | `consensus` |
| 8 | Stack N layers | N hops of the walk | ✅ | `generate_walk.c` |
| 9 | `lm_head` → logits | Completion operator / conditional floor | ⚠️ export-side only | `tensor_decompose.cpp` |
| 10 | Softmax + sample (temperature) | Rank by `eff_mu`, **RD as temperature** | ❌ **unbuilt** | — |
| 11 | **Append token → loop** (autoregressive) | Two disjoint engines, neither wired to `chat()` | ❌ **not composed** | `steered_walk.c`, `trajectory_generate.c` |
| 12 | KV cache | Session trajectory / working set | ⚠️ stopgap (`session_topics`) | `session_topics.sql.in` |
| 13 | Context window limit | **None** — the prompt is ingested as content | ✅ | spec 09 |

Read the table by its failure column. Stages 0–8 are built and are the *retrieval* half.
Stages 9–11 are the *generation* half, and that is where the pass dies.

### 2.1 The structural difference that matters

A transformer **interleaves retrieval and generation at every single emitted token**.
Attention (stage 3) and `lm_head` (stage 9) run in the same loop iteration; the retrieved
context directly conditions the next-token distribution.

Laplace currently runs them as **two unconnected programs**:

- `walk_branches` — retrieval over graph facts. Knows meaning. Emits no language.
- `steered_walk` / `trajectory_generate` — token-order walks. Emit language. Do not consult
  the live semantic frontier; `converse_walk` computes a topic weight *once*, up front, and
  never re-steers.

Nothing composes them per emitted token. **That is the sequencing gap.** It is not a
research unknown; it is an unwritten loop body with a defined contract (§3, stage 9–11).

---

## 3. The canonical Laplace forward pass

This is the standard. One spine, one order. Every conversational entry point runs *this*,
not a private subset.

```
S0  DECOMPOSE   prompt → content ids                         [prompt_state]
S1  DISAMBIGUATE  each token → its sense, resolved BY THE     [MISSING]
                  OTHER TOKENS in the prompt
S2  ORIENT      the prompt's topic set (plural, weighted)     [partial: argmax, single]
S3  INTENT      prompt → salience band(s) via frame evocation [MISSING]
S4  RETRIEVE    beam-walk the consensus graph under the       [walk_branches]
                intent mask → the semantic frontier
S5  COMPOSE     frontier → typed strata, carried across hops  [partial]
--- per emitted token, loop S6→S8 ---
S6  PROPOSE     next CONCEPT candidates at whatever tier      [walk_continuations /
                carries the evidence. Ordered constituents      steered_walk]
                come from physicalities.trajectory (CONTAINS/
                PRECEDES are views of it, never its source).
S7  STEER       re-rank candidates by the LIVE frontier (S4)  [MISSING — the loop body]
S8  SAMPLE      pick, using RD as temperature                 [MISSING]
--- end loop ---
S9  RENDER      token ids → text                              [render_text/realize_batch]
S10 CLOSE       deposit prompt + response as witnesses        [built, caller-side]
```

**Token-by-token emission is an artifact of having ONE tier.** A transformer's vocabulary is
a single stratum (BPE subwords, between t1 and t2), so sentence and document meaning must be
reconstructed by composing token vectors through layers — learned and lossy. Here every tier
carries its own attestations. Measured 0.05% sample, 2026-07-25 — attestation **subjects**:
t2 word 57%, t3 sentence **30%**, t0 codepoint 12%, t4 doc 0.05%; **objects**: t2 60%,
t3 **39%**, t4 1.5%. ~43% of the subject side is not at word tier.

So S6–S8 must not assume token emission. Generation proposes at whichever tier holds the
evidence — emit an attested sentence as ONE step and compose down only where needed. The
template failure (F4) is what happens when sentence-tier output is attempted with word-tier
machinery: the sentence tier was right there, carrying a third of the graph, unused.

(Prior draft error, recorded: "gloss sentences have zero outgoing relations" was true of
glosses — which are `HAS_DEFINITION` *objects* — and was wrongly generalized to tier 3 as a
whole. One shape was sampled, a tier was concluded about.)

**S7 is the invention's actual forward pass** and it is the one stage nobody has written.
"Sequence proposes, meaning steers" is already written in `converse_walk`'s header comment
— but the code steers by a *static* weight computed before the walk begins, not by the
frontier the walk is standing on. Making that steering live, per token, is the build.

Note what S1–S3 are: they are the stages that a transformer gets *for free* from
self-attention over the prompt. Laplace must do them explicitly because its retrieval is
indexed rather than dense. They are cheap — all three are graph reads — but they are load-
bearing, and all three are currently missing or degenerate.

---

## 4. Isolation — why it cannot converse today

All findings verified live against `laplace` on this host, 2026-07-25, foundation-only seed.

**F1 — No intent inference, by design.** `chat.sql.in:7-8`: *"the caller names the read;
nothing is inferred from how the prompt is phrased."* So `"What is a dog?"` and `"Why is the
sky blue?"` take an identical code path. Confirmed: both returned describe-templates; the
"why" was never read. **S3 missing.**

**F2 — Topic orientation picks the wrong token.** Orientation is `argmax` of content-band
consensus mass over prompt tokens (`chat.sql.in:58-71`). `"What are the parts of a car?"` →
resolved topic **`parts`** (*"the local environment"*), not `car`. The intent word outranked
the subject. **S2 degenerate.**

**F3 — No sense disambiguation.** `top_synset(p.id)` picks one sense per token with no
reference to the rest of the prompt. `"What is a dog?"` → *"a dull unattractive unpleasant
girl or woman."* In a transformer the surrounding tokens resolve this for free; here nothing
does. **S1 missing.**

**F4 — The default reply is a template, not generation.** `converse_about` →
`converse_facts` → fixed sentence frames. Output shape is always *"X is a kind of Y, which is
a kind of Z."* That is a filled form, not language.

**F5 — The free-form generator is unreachable from `chat()`.** `chat.sql.in:160` excludes
`'walk'` from the responder branch, so it falls through to `converse_about`. Confirmed live:
`chat('What is a dog?', …, shape=>'walk')` returns the **byte-identical** template. And
`converse_walk` — the actual free-form engine — is bound to **no published shape at all**
(`query_shapes()` lists 14; none reach it). The one component that generates language is
dead code from the chat surface.

**F6 — Called directly, the generator does not recombine.** `converse_walk('dog',40)` →
`"dull unattractive unpleasant girl or woman"` — the gloss returned verbatim, zero
recombination — and it exceeds the default statement timeout.

**F7 — The sequence layer is populated — thinly, but sufficiently. It is not the blocker.**
`physicalities.trajectory` **is** the ordered sequence structure. Live, foundation-only:
**1,876,342 tier-3** and **24,976 tier-4** physicalities carry trajectories, and they
unpack in order — `trajectory_unpacked_points` on a `dog` sentence reconstructs
`"swim like a dog in shallow water"`, 6 ordered word points. `dog` has 909 tier-3
containers. `converse_walk` reads exactly this (`converse_walk.sql.in:89-93`), never
PRECEDES.

> **CORRECTION 2026-07-25 (same session).** An earlier draft of this section counted
> `PRECEDES` consensus edges (93) and declared the sequence layer unseeded, making a full
> reseed a prerequisite. That was **wrong and is retracted.** It measured a table the
> generator does not read. Word order lives in the trajectory; `CONTAINS` is membership in
> it and `PRECEDES` is adjacency within it — both are *views derivable from the
> trajectory*, not its source of truth. Materializing them as consensus rows to "fill" the
> sequence layer would be precisely the backfill-a-missing-fold inversion the engineering
> law forbids. **A low PRECEDES count is not a status signal. Do not cite it.**

The corrected reading is sharper: `converse_walk('dog',40)` had 909 attested sentences of
real usage available and still returned the gloss verbatim with zero recombination. The
material was there and the walk did not use it. That is a **code** failure, isolated.

**F8 — `chat` renders by BAND where it must select by FAMILY.** Exposed by the wiktionary
seed (2026-07-25: +5.85M entities / +10.34M attestations, 10m27s). Bands rank *importance*;
families say what a relation *means*. Rendering raw bands mixes them:

- Band 2 (taxonomic) carries `MEMBER_OF_VERBNET_CLASS`, so the "is a kind of" clause emits
  *"Hound is a kind of Cotheme"* / *"a kind of bully-59.5"* — class membership rendered as
  hypernymy.
- Band 7 (associative) carries `HAS_ETYMOLOGY`, `ETYMOLOGICALLY_DERIVED_FROM`,
  `ETYMOLOGICALLY_RELATED_TO`, `HAS_EXAMPLE`, `HAS_USAGE_REGISTER`, so the "is related to"
  clause emitted `*hund`, `*hundaz`, `hounden`, `archaic` — proto-form reconstructions as
  semantic relatedness.

Foundation-only had too few relation types per band for this to surface. The fix is in S9
(render): select the relation families a clause is about (`IS_A` family for hypernymy,
associative *minus* the etymology/example/register families for relatedness), not
`relation_highway_band(...) = n`.

**F9 — the wiktionary seed FIXED F3 with no code change.** `chat('What is a dog?')` moved
from *"a dull unattractive unpleasant girl or woman"* to *"Any canine animal"*: the added
witness mass reordered the senses. F2 was byte-identical across the same seed
(*"Parts is the local environment"*), confirming it is purely code. **Sense selection is
evidence-sensitive; topic orientation is not.** Re-measure F1/F3 after any seed before
attributing either to code.

### 4.1 Root cause, stated once

**One cause: no composed pass.** Stages S1, S3, S7, S8 are unbuilt and S2 is degenerate;
`chat()` runs {S0, S2-degenerate, S4, template} and stops. The corpus is present, ordered,
and reachable.

The foundation-only corpus is **thin but sufficient to build and test every stage against**
— 909 usage sentences for a single common word is enough signal to write S7 and see it
work or fail. More corpus widens coverage and is worth doing on its own merits, but no
stage of §3 is blocked on it, and a reseed will not move a single one of F1–F6. Treating a
seed as a gate on conversational work is a stall. Build against what is resident; seed to
widen, not to unblock.

---

### 4.2 S1 is built-but-unwired, and the discriminating form is proven

`chat()` resolves senses through `top_synset(word)` → `bubble_up(word, NULL::bytea[], 1)`.
`bubble_up` ranks by `score = base_eff_mu × (1 + ln(1 + domain_hits))`. With a NULL domain
context — which `top_synset` hardcodes — `domain_hits` is 0 for every sense, so
`score = base_eff_mu`: the HAS_SENSE edge's value, **near-constant across all senses of a
word** (12 senses of `dog`, 3 distinct values). Sense selection is therefore effectively
arbitrary. That is F3's mechanism.

`bubble_up` already accepts `p_domain_context`. It is never passed. But passing it does not
fix this either: `domain_hits` is computed over the synset's `members`, which resolve to the
**shared lemma** — identical for every sense of the word — so the signal is uniform.
Measured live: context `{animal, pet, bark}` moved every `dog` sense from 0 to exactly 1
hit, discriminating nothing, in **33s** (matching the 37s warning already in the source).

**Four disambiguation paths exist. None discriminate.** Measured live 2026-07-25:

| path | result |
|---|---|
| `top_synset` → `bubble_up(w, NULL, 1)` | score collapses to `base_eff_mu`, near-constant across senses → arbitrary |
| `bubble_up(w, ctx, k)` | scores over the SHARED lemma → all 12 `dog` senses got identical hits; 33s |
| `senses(w, ctx[])` (`senses_with_context`) | needs a direct context→synset consensus edge; words reach synsets via HAS_SENSE, so boost is **0** — byte-identical rankings for opposite contexts |
| gloss-token overlap (prototyped here) | **fails on real prompts.** 0 overlap for `mammal`, `ate/ballpark`, `barked/mailman`; returned *"morally reprehensible"* for "What is a dog?" off function words |

> **CORRECTION (same session).** An earlier draft of this section reported the gloss-overlap
> prototype as "three contexts, three correct senses" and proposed it as the S1 build. That
> demo was **circular** — the context words were hand-picked out of the target glosses
> (`sausage/roll/beef` are literally the frankfurter gloss). Tested against realistic
> prompts it fails outright. Retracted. Surface-token overlap is the classic Lesk sparsity
> failure and is not the answer.

A 2-hop concept-space expansion was also tried: 1 of 3 correct, and every candidate tied at
identical mass — the "hit" arrives through a shared high-degree hub, which is the same
hub-collapse mode spec 09 documents. Not discrimination.

**Where the signal actually is.** Usage co-occurrence in the trajectory layer, measured over
the tier-3 sentences containing the word — thin but correctly ordered on the foundation-only
corpus:

| co-token with `dog` | sentences | points to |
|---|---|---|
| bark | 14 | canine |
| animal | 8 | canine |
| woman | 6 | pejorative |
| pet | 4 | canine |
| sausage | 3 | frankfurter |
| mammal | 2 | canine |
| ballpark | 0 | — |

The ordering is right and it separates the senses. **No built path reads it** — all four
consult the lexical relation graph instead. Cost drops 33s → 7.5s by computing co-occurrence
as a **set intersection of `containers_of` results** instead of unpacking trajectories
(identical counts, verified); still too slow for a turn, but it is an indexing problem.

> **RETRACTED 2026-07-25.** An earlier draft of this section concluded: *"this substrate
> cannot express sense-tagged usage… a named gap: a relation type plus a decomposer."*
> **That was false, and it was the most damaging thing in this document** — acting on it
> would have meant building a relation type and a decomposer for evidence that is already
> resident. It was inferred from two narrow probes (gloss sentences have no *outgoing*
> relations; no relation literally named `*_SENSE_TAG`) and generalized into a claim about
> the whole substrate. "We can't express it" was a gatekeeping conclusion, not a
> measurement.

**Sense-resolution is expressed — by OWNERSHIP, not per-token tagging.** A gloss or example
sentence is *owned* by the synset it hangs off. The occurrence of a word inside that
sentence is therefore already sense-resolved on the owning side. Verified: the 14 sentences
where `dog` co-occurs with `bark` are owned by `yip.01`, `bow-wow`, `Make_noise/bark.v`, and
`bark` — a PropBank roleset, a synset, and a frame. That *is* the tagged link.

**Every attempt above queried ONE band (definitional) and ignored the rest of the mesh.**
Resident and unused, measured live:

| lane | edges | what it carries |
|---|---|---|
| `HAS_SEMANTIC_ROLE` | 29,103 | role structure |
| `EVOKES_FRAME` | 25,715 | word → frame (the language-agnostic layer, doc 22 Phase B) |
| `HAS_VERB_FRAME` | 23,744 | syntactic frames |
| `ROLE_CORRESPONDS_TO` | 14,157 | cross-resource role mapping |
| `HAS_FRAME_ELEMENT` | 11,428 | frame elements |
| `HAS_THEMATIC_ROLE` | 1,069 | VerbNet class → Agent/Theme/… |
| `HAS_EXAMPLE` | — | **sense-tagged usage** (e.g. *"The dogs barked at the stranger"* owned by the bark sense) |

Plus the ILI hub, `CORRESPONDS_TO` (bark → `Make_noise`, `Communication_noise`, `pit-10.7`),
and `HAS_LEX_CATEGORY` (`verb.communication`, `noun.event`).

The S1 build therefore runs over the mesh as specified in CLAUDE.md — surface → lemma →
sense → ILI → frame/class/roleset → roles — not over one band. Open and unmeasured: whether
thematic roles carry selectional restrictions (`+animate`) in this seed; the probe that would
have answered it used a wrong canonical key and returned nothing, which is **not** evidence
of absence. Measure it with the right key before concluding anything.

**Standing correction.** Six approaches failed here, and each failure report reached for a
missing-evidence explanation. In every case the evidence was resident and unqueried. Before
writing that the substrate lacks something, enumerate what it holds.

## 5. Standardization contract

The piecemeal-POC condition is the absence of these five rules. They are now binding.

1. **One pass.** `chat()` is the only conversational entry point and it runs S0→S10 in
   order. `converse`, `converse_about`, `converse_walk`, `converse_facts` become internal
   stages of that pass, not sibling entry points.
2. **No stage may be skipped silently.** If a stage cannot run (no intent resolved, no
   sequence evidence), it degrades explicitly and the degradation is a returned field, not
   an invisible fallback into a template. Today's silent fall-through to `converse_about` is
   exactly the failure that hid F5 for months.
3. **Every published shape must be reachable.** `query_shapes()` is a contract. A shape that
   returns another shape's output is a broken build, and a gate must assert it: for each
   published shape, the reply must differ from the `describe` reply for the same topic.
4. **Templates are a floor, never the target.** `converse_facts` prose is the S9 fallback
   when the sequence layer is starved. It is never the success path, and it must be labeled
   as fallback in the response envelope so no one mistakes a filled form for generation.
5. **Conversational claims are proven at the conversation layer.** Not by row counts, not by
   `substrate_health()`, not by a passing unit test — by running prompts through `chat()`
   and reading the replies. Per the standing law: verify at the claim's layer.

---

## 6. Retired framing

These statements are superseded. They are recorded here because each was load-bearing in
prior sessions as a reason to defer the build.

- **09 §"REDUCED RESIDUE": *"ONE measurable question: does consensus × geometry ×
  trajectory ROUTE as well as trained attention, at depth?"*** — Retired as a *gating*
  question. It is a measurement that comes **after** S7 exists, not a prerequisite for
  building it. Routing quality cannot be measured because the routing stage has never been
  written. Build S7, then measure. Citing this as an open research risk is a stall.
- **CLAUDE.md: *"The one open research question (doc 09): does consensus × geometry ×
  trajectory ROUTE as well as trained attention at depth."*** — Same retirement. Reworded
  to name it as an unbuilt stage with a defined contract (§3 S7).
- **22 Phase F: *"remains fully open… needs deeper C."*** — Superseded by §3. Phase F is
  stages S6–S8 of a defined ladder with named entry points, not an open-ended research
  phase. "Needs deeper C" is a scope statement, not a blocker.
- **09 §FAITHFUL DIAGNOSIS: *"faithful = hallucination."*** — Already retired in 09 itself.
  Restated here so it is not re-imported: that run was a bigram lookup with the layers
  switched off. It is not evidence about this architecture.

**Standing rule.** An unbuilt pipeline stage is an unbuilt pipeline stage. Naming it a
research question converts a task into a risk, and a risk into a reason to wait. When a
stage has a defined input, a defined output, and a named code site, it is work — say so.
