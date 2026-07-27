# 37 — The Substrate Operation ISA

Status: **PROPOSED — not binding.** Drafted 2026-07-27.
Companion: `docs/specs/36` (forward-pass stages), `docs/specs/06` (engineering
rules), `.scratchpad/38` (the audit this is derived from — evidence, not law).

> **Read this caveat before treating anything below as settled.** This draft was
> written from a partial read: roughly 40–50 of the 332 SQL function bodies, a
> handful of the 27 extension C files, and none of `recall.c` (1413 lines),
> `generate_walk.c` (1299), `NpgsqlWorkingSetApply` (1049), `Decomposer.cs`, the
> 105 decomposers, or ~26k lines of tests. The rest was counted by name and
> grep. Counts in §4 are therefore **estimates, not a census**, and §1's claim
> that "there are no other operations" is an assertion the evidence does not yet
> support.
>
> A concrete example of the error rate: the first draft of the audit claimed κ
> was duplicated between C and SQL. It was not — it is deliberately
> single-sourced through `foundry_rd_kappa()`, and `glicko2.h:80–92` says so.
> That claim was withdrawn after reading the code.
>
> Each opcode becomes binding only when its family has been read at body level
> and its gate is green in CI. **OP4 (`WEIGHT`) is the first and currently the
> only one verified end-to-end** — see §8 step 1 and the status note there.

---

## 0. Why this exists

The substrate has one identity law, one fold, one geometry — and, on the read and
export sides, **six ways to weight an edge, twenty-two ways to scan one, six ways
to ask what follows what, and seventeen ways to turn an id into text.** None of
them are abstractions over a shared core; they are independent bodies kept in
agreement by comments, and the audit found them already disagreeing.

The recurring mechanism is specific and worth naming, because every rule below
exists to stop it:

> **An operation gets a canonical implementation. The orchestrator that should
> call it is never rewired. Both survive. They drift.**

`prompt_coherence` (native, zero callers), `seed-chain` (fast seed, unwired),
`converse_tiered` (S8, off), `realize_batch` beside `realize`, `define_fast`
beside `define`, the Glicko weight in C beside the same weight in SQL.

This document defines the **instruction set**: ten operations, their exact
contracts, the one legal order they execute in, and the gates that fail a build
when either is violated. Everything in the substrate's read, generate, and export
paths is expressed as a program over these ten. There are no other operations.

---

## 1. The instruction set

Ten opcodes. Each has **exactly one implementation**. Variants are *modes*
(parameters), never sibling functions.

### OP1 `RESOLVE` — surface → content ids

```
resolve(text, lang := NULL) → TABLE(ord int, token_id bytea, lang_id bytea)
```

Segments through the native word-break and resolves each token to its content id.
Abstains (`NULL` id) rather than minting. Language is *inferred from the request*,
never assumed. **No sense resolution here** — that is OP2.

Canonical impl: `prompt_state` over `word_segment` (native segmentation).
Absorbs: `prompt_words`, `resolve_ref`, `resolve_phrase`, `resolve_topic`,
`resolve_last_word`, `word_id`, `word_language`.

### OP2 `SENSE` — id → candidate senses

```
sense(token_ids bytea[], context bytea[] := NULL, k int := 64)
  → TABLE(token bytea, sense_id bytea, synset_id bytea, denote_mu numeric, witnesses bigint)
```

Set-based over **all** tokens in one call — never one call per token. `denote_mu`
is reported, never used alone to elect (see OP3).

Canonical impl: native, replacing the `bubble_up` CTE cascade.
Absorbs: `senses`, `senses_with_context`, `bubble_up`, `top_synset`,
`lexical_peers`, and the four `*_bootstrap` bodies.

### OP3 `ELECT` — joint topic + sense + relation

```
elect(token_ids bytea[]) → TABLE(ord int, token bytea, synset_id bytea,
                                 coherence float8, rel_mass float8,
                                 peers bigint, rel_type_id bytea, denote_mu numeric)
```

Reads the graph **between** the prompt's tokens. Ranks a candidate by rated mass
to the *other* tokens' candidates (coherence), and by rated mass in a relation
type another token *names* (rel_mass). `denote_mu` is the tiebreak only.

Canonical impl: `prompt_coherence` (`src/prompt_coherence.c`) — **already written,
currently unwired.** Wiring it is P0.

**Law:** no election may rank on a single-token scalar. The discriminating
information is not in any one token — that is what answered *"What is a pawn in
chess?"* with *"A is the 1st letter of the Roman alphabet."*

### OP4 `WEIGHT` — the one edge-strength function

```
weight(rating int8, rd int8, witness_count int8, type_id bytea, mode weight_mode)
  → float8
```

```
mode = CONSERVATIVE  -- rating − 2·rd            (ranking key; the indexed expression)
     | SALIENCE      -- relation_rank × eff_mu/1e9
     | COMPLETE      -- rank × (rating−neutral) × e^(−κ·rd) × wc/(wc+halfmax)
     | STRENGTH      -- logistic on (eff_mu−neutral)/1e9, clamped [0.05, 1.0]
```

**One implementation, in C** (`engine/core/src/glicko2.c`), exposed to SQL as a
single function. κ and the witness half-max are **defined once, in C**, and read
from there by SQL — not restated.

Every existing formula maps to a mode. `CONSERVATIVE` remains inlinable so
`consensus_*_eff_mu_btree` still serves it (`eff-mu-inlining-law`).

**Law:** no function body may open-code `rating − 2·rd` or any of the four
formulas. Index expressions are the sole exemption.

### OP5 `SCAN` — the one edge read

```
scan(subjects bytea[], direction, type_filter bytea[] := NULL,
     refuted refuted_policy, mode weight_mode, cap int)
  → TABLE(subject_id, type_id, object_id, rating, rd, witness_count, w float8)
```

```
direction = OUT | IN | BOTH
refuted   = EXCLUDE | INCLUDE | ONLY
```

Native, against the partitioned `consensus`. `BOTH` is two indexed range reads,
**never an `OR` predicate** — that shape is unservable by the consensus indexes
and is what produced the 280s hang.

`refuted` is **explicit and required**. Today `consensus_out`/`consensus_in`
silently include refuted edges while their siblings exclude them; after this spec
that is a caller's stated choice or it does not compile.

Absorbs the 22-function neighbour family (§4 disposition table).

### OP6 `SELECT` — rank, cap, exclude

```
select(edges, k int, exclude bytea[] := NULL, tiebreak := id_asc) → edges
```

Not separately callable — it is the tail of OP5 and OP7, specified so its
semantics are one thing. Ordering is `w DESC, id ASC` (deterministic).

**Law:** `k` is a **query limit**. It is never a floor, a threshold, or a
truncation of what a source asserted. No `WHERE w > c` outside OP4's declared
modes.

### OP7 `TRAVERSE` — the frontier fixpoint

```
traverse(seeds bytea[], plan traverse_plan) → TABLE(depth, path bytea[], types bytea[],
                                                    entity_id, w, path_w, witnesses)

plan = { strategy: BEAM | DIJKSTRA | ASTAR | GREEDY,
         depth, breadth, type_filter, intent_mask, topic_bias,
         weight_mode, geometry: bool, visited: PATH | GLOBAL }
```

One frontier engine, one visited-set, one beam. Strategies are plan fields.

Absorbs: `walk_branches`, `walk_strongest`, `astar_path`, `cascade`,
`foundry_crawl`, `explore_web`, `graph_taxonomy`, `containers_of`,
`geometry_successors` (as a `SEQUENCE`-sourced plan).

### OP8 `SEQUENCE` — what follows what

```
sequence(scope, vocab bytea[] := NULL, gap int, weighting seq_weighting, cap int)
  → TABLE(subject_id, object_id, w float8)

scope     = WORD_IN_SENTENCE | SENTENCE_IN_DOCUMENT | POINT_IN_TRAJECTORY
weighting = COUNT | CONDITIONAL | GAP_DISCOUNTED | ASSOCIATION
```

Native, over `laplace_trajectory_constituents`. `scope` replaces the
copy-pasted tier predicate that made `word_order` and `sentence_order` two
structurally identical queries.

Absorbs: `word_order`, `sentence_order`, `cooccurrence_scan`,
`trajectory_cooccurrence`, `trajectory_pairs_plane`,
`sentence_order_word_bridge`, and the C# `ApplyPpmi`.

**Law — the sequence epistemology, stated (§5).** Sequence is read from geometry
and does not pass through the fold. `ASSOCIATION` (PPMI) is therefore a
*declared, named* weighting of counts, computed **in C alongside the others**,
never a client-side re-weighting applied after the fact. `pmi > 0` is part of the
`ASSOCIATION` definition, not an ad-hoc filter, and it is the only place a
non-`LIMIT` cut is legal in the entire ISA.

### OP9 `REALIZE` — ids → text

```
realize(ids bytea[], lang bytea := NULL, context bytea[] := NULL) → text[]
```

Batch only. Positionally aligned. **Abstains with `NULL`** — never fabricates hex
styled as a label.

Canonical impl: `realize_batch` (`src/realize_batch.c`).
`realize(id)` becomes `realize(ARRAY[id])[1]` — a wrapper, not a second ladder.

Absorbs: `realize`, `resolve_name`, `_realize_*` (5), `label`, `label_or_hex`,
`type_label`, `render`, `render_text`, `render_text_fast`, `render_text_batch`,
`canonical_names`, `realize_path`.

### OP10 `WITNESS` — the OODA close

```
witness(turn: {tenant, session, user?, prompt, response}) → applied
```

Floor check → writer → tenant scope (cached) → bootstrap (once) → user
attribution (once per session) → build turn change → apply with inline fold.

Canonical impl: one `TurnCloser` in `Laplace.Substrate`, called by MCP,
OpenAICompat, and CLI. The three current copies already diverge — only one checks
the substrate floor.

---

## 2. The canonical order

Every read is a program over OP1–OP10 in this order. This supersedes nothing in
spec 36 — it is spec 36 §3 expressed as opcodes.

```
S0  ingest prompt as content                        (writer spine)
S1  RESOLVE   text → token ids
S2  SENSE     tokens → candidate senses             (one call, all tokens)
S3  ELECT     joint topic + sense + relation
S4  PLAN      shape → an opcode program             (from the shape table, §3)
S5  SCAN / TRAVERSE                                  ── hash space only
S6  SELECT    rank, cap, exclude                     ── hash space only
S7  SEQUENCE  (generative shapes only)               ── hash space only
S8  REALIZE   ids → text                             ── the ONLY render point
S9  COMPOSE   text → response envelope
S10 WITNESS   deposit prompt + response, fold
```

### The ordering laws

- **L1 — Hash space until S8.** No `realize`, `label`, `render_text`, `type_label`,
  string comparison, or regex over a rendered surface may execute before S8.
  Ranking on a rendered string is how orientation picked `parts` over `car`;
  rendering per row before a `LIMIT` is how `chat()` went down.
- **L2 — One WEIGHT.** OP4, by mode. No open-coded formula in any body.
- **L3 — REALIZE once, last, batched, on survivors only.**
- **L4 — Vocabularies are generated.** Relation names, read shapes, salience
  bands, and the ingest source roster are emitted from their manifest into every
  language. A quoted literal naming one of them is a build failure.
- **L5 — No silent skips.** Every stage reports `ran | degraded | skipped` with a
  reason in the response envelope. A stage that cannot run says so to the caller.
- **L6 — No operator floors, caps, or top-k.** `k` is a query limit. The single
  exemption is `SEQUENCE(ASSOCIATION)`, declared in OP8.
- **L7 — One implementation per opcode.** An optimized variant is a mode. A
  "fast" sibling function is a defect, not an optimization.
- **L8 — A canonical implementation with zero callers is a build failure.** This
  is the direct countermeasure to the failure mode in §0. Writing the fast path
  and leaving the orchestrator on the slow one must not pass CI.

---

## 3. Shape table — the ISA's program memory

The read shapes are **data**, in one place, generated into every consumer. Today
the set is written longhand in five places (`query_shapes()`, `route_intents[]`,
`kSingleArgIntents[]`, the `strcmp` ladder, and `chat()`'s private branch list).

```
shape        arity  needs_type  accepts_lang  program
──────────── ────── ─────────── ───────────── ────────────────────────────────────
define         1        no          no        SENSE → SCAN(HAS_DEFINITION) → REALIZE
what_is        1        no          no        define ⧺ TRAVERSE(BEAM, IS_A family)
describe       1        no          no        SCAN(BOTH, content bands) → SELECT → REALIZE
synonyms       1        no          no        SCAN(OUT, equivalence band) → REALIZE
translate      1        no          yes       SCAN(IS_TRANSLATION_OF) → REALIZE(lang)
languages      1        no          no        SCAN(HAS_LANGUAGE) → REALIZE
examples       1        no          no        SCAN(HAS_EXAMPLE) → REALIZE
related        1        yes         no        SCAN(OUT, type) → SELECT → REALIZE
related_in     1        yes         no        SCAN(IN,  type) → SELECT → REALIZE
is_a           2        no          no        TRAVERSE(DIJKSTRA, IS_A family, 2 seeds)
reason         2        no          no        TRAVERSE(ASTAR) + SEQUENCE overlap
walk           1        no          no        TRAVERSE(GREEDY, unfiltered)
complete       1        no          no        TRAVERSE(BEAM, COMPLETES_TO)
fallback       1        no          no        define, else walk
```

`related` and `related_in` are one row with a `direction` column once OP5 lands.

**Law:** every shape published here must be reachable, must differ from
`describe`, and must be dispatched from this table — not from a `strcmp` ladder.

---

## 4. Disposition — where every current operation goes

Full per-function mapping lives in `.scratchpad/38`. The families:

| Current | Count | Disposition |
|---|---|---|
| `eff_mu`, `effective_mu`, `edge_rank`, `laplace_walk_edge_weight`, `consensus_adjacency` inline weight, `laplace_edge_strength`, open-coded sites | 7 | **fold → OP4** |
| `consensus_out/in/by_ids/neighbors_*/step_edge/walk_edges`, `related`, `related_in`, `related_objects`, `explore_web_neighbors`, `foundry_crawl_neighbors`, `shared_objects`, `completions`, `top_relations`, `salient_facts`, `classify_circuit`, … | 22 | **fold → OP5**; keep named views where a name aids callers |
| `walk_branches`, `walk_strongest`, `astar_path`, `cascade`, `foundry_crawl`, `explore_web`, `bubble_up` walk, `containers_of`, `graph_taxonomy` | 9 | **fold → OP7** |
| `word_order`, `sentence_order`, `cooccurrence_scan`, `trajectory_cooccurrence`, `trajectory_pairs_plane`, `ApplyPpmi` | 6 | **fold → OP8** |
| `realize` + 16 name/render helpers | 17 | **fold → OP9** |
| `senses`, `senses_with_context`, `bubble_up`, `top_synset` + 4 `_bootstrap` | 8 | **fold → OP2**; delete the bootstrap pairs (§6) |
| `define`, `define_with_context`, `define_fast` + 2 `_bootstrap` | 5 | **fold → shape `define`** |
| 18 `recall_*_response` adapters | 18 | **delete** — the shape table replaces them |
| `query_shapes`, `route_intents[]`, `kSingleArgIntents[]`, `strcmp` ladder, `chat()` branches | 5 | **fold → §3 table** |
| 3 turn-close implementations | 3 | **fold → OP10** |
| 9 ingest-roster declarations | 9 | **fold → one generated roster** |
| 6 ingest-mutex + 11 verify implementations | 17 | **fold → CLI subcommands** |

Target: **332 SQL function files → under 180**, with the reduction concentrated in
`SCAN`, `REALIZE`, and the recall adapters. The chess, model, ops, and inspect
surfaces are largely legitimate breadth and are not reduction targets.

---

## 5. The two epistemologies — a decision, not an accident

The audit found that relational structure carries the Glicko-complete weight while
sequence structure is built from raw counts, PPMI-reweighted, floored, and
degree-capped twice — with nothing in the docs stating that boundary.

**Ruling.** The boundary is real and is hereby law:

- **Relational structure is adjudicated.** Every edge in `consensus` carries
  rating/rd/volatility/witness_count. OP4 is the only way to weight it. No
  client-side re-weighting, anywhere, ever.
- **Sequence structure is geometric and unadjudicated.** The trajectory holds the
  ordered sequence losslessly; materializing word adjacency as attestations was
  removed deliberately (`continuation_conditional_plane`, Pillar 5). Sequence is
  therefore counted, not folded.
- **Consequence, binding:** every transform applied to sequence counts is a
  **named `seq_weighting` in OP8, computed in C**. `COUNT`, `CONDITIONAL`,
  `GAP_DISCOUNTED`, `ASSOCIATION`. The C# `ApplyPpmi`/`Normalize`/`PositivePart`/
  `TrimRowToTopK` chain is deleted; `consensus_adjacency`'s degree cap is the only
  cap.
- **The exported model must declare which planes are adjudicated and which are
  counted.** A pour that cannot say this is not reproducible.

`FoundryDefaults`' ~25 swept scalars (`AttnGain`, `FloorCorrectionGain`,
`FactorSpectrumAlpha`, …) are **app-metadata that decides exported weights**. They
move under pour-scoped provenance: attested, versioned, reproducible from the
substrate. `const` in a library is not a recipe.

---

## 6. Install-time arbitration is banned

`senses()` and `define()` each have two same-signature bodies with **different
semantics**, and which one is live is decided by line order in a 397-line
manifest, with no assertion afterward.

**Law:** two bodies may never share a signature. Break the
`senses → bubble_up → lexical_peers → senses` install cycle with a forward
declaration or by making OP2 native (preferred — it removes the cycle entirely).
If a bootstrap body is unavoidable, install must assert the final body won.

---

## 7. Gates

Each is mechanical and belongs in the policy job. A rule without a gate is a
comment, and this document exists because comments were the parity mechanism.

| # | Gate | Fails when |
|---|---|---|
| **G1** | weight literalism | `rating - 2 * rd` (any spacing) appears outside `mu/eff_mu.sql.in`, `glicko2.c`, and `sql/indexes/` |
| **G2** | render-before-select | a scalar `realize|label|render_text|type_label` appears inside a row-producing `SELECT` in any `.sql.in` |
| **G3** | vocabulary literalism | `relation_type_id('LITERAL')` in SQL, `rel_type_id("LITERAL")` in C, or a governed relation name as a C# string literal |
| **G4** | **dead canonical** | any opcode entry point, or any function whose header claims to supersede another, has zero callers |
| **G5** | shape parity | `query_shapes()`, the C dispatch, and the client menu are not all generated from the §3 table |
| **G6** | weight parity | the C and SQL results of OP4 differ on a fixed vector, per mode |
| **G7** | roster parity | the ingest source roster is declared in more than one place |
| **G8** | band literalism | a salience band appears as an integer literal instead of via `relation_band_catalog()` |
| **G9** | envelope | `chat()` returns without a per-stage status for S1–S10 |
| **G10** | one mutex | more than one implementation of the ingest mutex or the `evidence_count` verify exists |

**G4 is the important one.** It is the gate that would have caught
`prompt_coherence`, `seed-chain`, `converse_tiered`, and `realize_batch` — the
failure mode this whole document is written against.

---

## 8. Sequencing of the work

The ISA lands in dependency order. Each step is independently shippable and
leaves the tree green.

1. **OP4 + G1 + G6.** One weight, four modes, parity-tested. Unblocks everything
   that ranks.
2. **OP3 wired + G4 + G9.** `prompt_coherence` on the hot path behind the
   envelope; timed on `dog`/`tree`/`music`/`river`, not `pawn`. Fix
   `converse`/`converse_walk`'s orientation in the same change — the outage class
   stays open until they stop running the 29.6s band-mass scan.
3. **OP9 + G2.** `realize` becomes a wrapper; the 30 per-row bodies are swept.
4. **L4 vocabularies + G3/G5/G7/G8.** Generation from the manifests; the five
   shape declarations collapse to the §3 table.
5. **OP5 + OP7.** The native scan and the one frontier engine; the 31 SQL
   neighbour/walk functions become views or die.
6. **OP8 + §5 ruling.** Sequence weightings move to C; the C# transform chain is
   deleted; the pour declares its planes.
7. **OP1 + OP2 + §6.** Native resolve/sense; the bootstrap pairs go.
8. **OP10 + G10.** One `TurnCloser`; one mutex; one verify; the ops scripts become
   thin shims over CLI subcommands.

---

## 9. What this does not change

The ingest spine (`IngestBatchPipeline` → `SubstrateChangeBuilder` →
`ConsensusAccumulatingWriter` → `NpgsqlWorkingSetApply` under `IngestRunner`) is
the model this ISA is imitating, not a target. The decomposer layer, `engine/core`,
the consensus views, the model lane's native interop, and the chess
`consensus_by_ids` consolidation are all correct. The identity law, the fold, the
tier ladder, and the geometry are untouched — this document is about the
*operations over* them, not the substrate itself.
