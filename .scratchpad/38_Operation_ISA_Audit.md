# 38 — Operation / ISA Audit (deep pass)

Date: 2026-07-27. Branch `fix/chat-hang-restore` @ `684c094`.
Method: function **bodies** read and diffed, not names matched. Every claim cites
`file:line`. Credits in §12 and §16 are as load-bearing as the defects — this is
not a "delete duplicates" list.

> **This document is evidence, not law.** The binding output is
> **`docs/specs/37_Substrate_Operation_ISA.md`** — the ten opcodes, their
> contracts, the one legal order, the disposition of every operation catalogued
> here, and the ten CI gates. Read 37 to know what to build; read this to know
> why.

Surfaces covered: 332 SQL function files, 27 extension C files, `engine/core`
+ `engine/dynamics` + `engine/synthesis`, `Laplace.Substrate` (17.8k lines),
`Laplace.Decomposers` (14.7k), `Laplace.Cli` (8.5k), `Laplace.Endpoints.*`
(11.9k), `Laplace.Chess` (10.9k), the install/upgrade manifests, and the
relation manifest.

---

## 0. Verdict

There is one recurring failure mode, and it appears at **every** layer — SQL,
extension C, C#, and the shell/ops scripts:

> **An operation gets a canonical implementation. The orchestrator that should
> call it doesn't get rewired. Both survive. They drift.**

`prompt_coherence` (ported to C, zero callers, §9). `seed-chain` (the one-process
seed, not called by any top-level entry, §13). `converse_tiered` (S8, hotfixed
off, §9). `realize_batch` vs `realize` (§8). The C vs SQL Glicko weight (§2).
`define_fast` vs `define` (§10). Every one is the same shape.

Underneath that: **the vocabularies that define the system's instruction set are
written out longhand, in every language, everywhere they are used** — relation
names (391 sites), read shapes (5 places), salience bands (4), and the ingest
source roster (**9**, §14).

Three things are structurally wrong, in order of severity:

1. **The export path runs a second, incompatible epistemology.** Relational
   structure comes from the Glicko fold; sequence structure is built from raw
   uncounted-by-the-fold co-occurrence, re-weighted with PPMI, floored at
   `pmi > 0`, and degree-capped twice. The exported model is half-adjudicated.
2. **The #686 outage class is not closed.** The slow orientation was removed from
   `chat()`'s primary path and left intact in `converse()` and `converse_walk()`,
   which `chat()` calls as its fallback at three sites.
3. **The instruction vocabulary is string literals everywhere.** 212 hardcoded
   `relation_type_id('NAME')` sites over 76 distinct names in SQL, 17 in C, ~162
   in C#; the read-shape set is written out longhand in five places; the salience
   bands are magic integers in four.

---

## 1. The opcodes, with measured implementation counts

| # | Opcode | Impls | Distinct semantics among them |
|---|--------|-------|-------------------------------|
| 1 | `RESOLVE` surface → id[] | 9 | 2 |
| 2 | `SENSE` id → (sense, synset)[] | 5 | 3 |
| 3 | `SCAN` (id, dir, filter) → edges | 22 | **12** (§3) |
| 4 | `WEIGHT` (rating, rd, wc, type) → scalar | 7 | **6** (§2) |
| 5 | `SELECT` rank + top-k + visited | 8 | 4 |
| 6 | `TRAVERSE` frontier fixpoint | 9 | 6 |
| 7 | `SEQUENCE` "what follows what" | **6** | **4 weightings** (§5) |
| 8 | `REALIZE` id[] → text[] | 17 | 3 |
| 9 | `COMPOSE` text[] → response | 4 | 3 |
| 10 | `WITNESS` turn → change → fold | 3 | 2 |

"Distinct semantics" = how many of the implementations would return a *different
row set or ordering* on the same input. That column is the real problem: these
are not stylistic copies.

---

## 2. WEIGHT — seven implementations, six semantics

| Formula | Site | Consumers |
|---|---|---|
| `rating − 2·rd` (SQL, inlinable) | `mu/eff_mu.sql.in:2` | ~16 ORDER BY sites |
| `rating − 2·rd` open-coded | `related_objects.sql.in:14,19`; `chat.sql.in:261,266,271,292,299`; `converse_facts.sql.in:52,66,122,127,155,162`; `highway_mask.c:332–338`; `prompt_coherence.c:214,591` | scattered |
| `rating − 2·rd` (C) | `glicko2.c:434` → SQL `effective_mu()` | one parity test |
| `relation_rank × eff_mu/1e9` | `relation/edge_rank.sql.in` | `consensus_out/in`, `completions`, `top_relations`, `links`, `epistemic_status`, `consensus_walk_edges` |
| **Glicko-complete (C)** `(rating−neutral)·e^(−κ·rd)·wc/(wc+4)` | `glicko2.c:473` | `generate_walk.c:632` |
| **Glicko-complete (SQL)**, retyped | `consensus_adjacency.sql.in:36–44` | Foundry export |
| logistic on `(eff_mu−neutral)/1e9`, clamped `[0.05,1.0]` | `spi_common.h:118` | `foundry_crawl`, `explore_web` |

### 2.1 The two "identical" Glicko-complete copies are not one function

`generate_walk.c:156` asserts its weight is *"the SAME formula doc 14 P5 already
ratified for the Foundry export path (`consensus_adjacency.sql.in`)."* It was the
same algebra typed twice.

**Correction to this audit's first draft:** I claimed κ was duplicated. It was
not. κ is a C *parameter* precisely so both native callers fetch the SQL tunable
`foundry_rd_kappa()` through `spi_fetch_rd_kappa()` (`generate_walk.c:488`,
`astar_path.c:267`) — deliberately single-sourced, and `glicko2.h:80–92` says so.
The claim was wrong and is withdrawn.

What was genuinely duplicated:

- **the formula itself** — `laplace_walk_edge_weight` (`glicko2.c:473`) and a
  hand-typed SQL expression in `consensus_adjacency.sql.in`'s `summed` CTE. The
  Foundry export was not calling the native copy; it re-derived the algebra.
- **the witness half-max** — `#define LAPLACE_WITNESS_SAT_HALFMAX 4.0`
  (`glicko2.c:444`) and `foundry_witness_sat()`'s `4.0`. The C comment said it
  *"mirrors foundry_witness_sat.sql.in exactly"* — the parity mechanism was a
  comment.

The only cross-language parity test in the tree (`tests/sql/word_law.sql:63`)
pinned `eff_mu = effective_mu`, which is `rating − 2·rd` on both sides and cannot
drift. Nothing bound the formula that decides both what the engine walks and what
the foundry exports.

**Fixed 2026-07-27.** `walk_edge_weight()` (`sql/functions/mu/walk_edge_weight.sql.in`
→ `pg_laplace_walk_edge_weight`) is now the single SQL entry to the C body;
`consensus_adjacency` calls it and resolves κ once per call instead of per row.
Equivalence verified on **200,000 live consensus rows: bit-identical, max abs
diff 0**. `tests/sql/walk_edge_weight_parity.sql` pins the formula, the three
constants, and the sign law, and is registered in the regress list.

### 2.2 A centralization primitive that violates its own doctrine

`related_objects.sql.in:1–8` declares itself *"The one relation-select
primitive… One implementation per fact (06 Rule #6)."* Lines 14 and 19 open-code
`(g.rating - 2 * g.rd)` — the exact practice `consensus_by_ids.sql.in:8–9` says
it exists to eliminate. Two centralization primitives, opposite conventions.

---

## 3. SCAN — 22 spellings, 12 semantics

Every one is `WHERE subject_id|object_id = X [AND type…] ORDER BY <weight> LIMIT k`.
The axes that actually differ:

| fn | table/view | direction expressed as | rank | refuted? |
|---|---|---|---|---|
| `consensus_out` | `consensus` **raw** | separate fn | `edge_rank` | **included** |
| `consensus_in` | `consensus` **raw** | separate fn | `edge_rank` | **included** |
| `consensus_by_ids` (×2) | `consensus` | by pk | none | included |
| `consensus_neighbors_directed` | `v_consensus_unrefuted` | separate fn | `eff_mu` | excluded |
| `consensus_neighbors_undirected` | unrefuted | `UNION ALL` | `eff_mu` | excluded |
| `consensus_step_edge` | unrefuted | **`OR` predicate** | `eff_mu` | excluded |
| `consensus_walk_edges` | unrefuted | separate fn | `edge_rank` | excluded |
| `explore_web_neighbors` | `consensus` **raw** | `UNION ALL` + bool col | `eff_mu` | **included** |
| `foundry_crawl_neighbors` | `consensus` **raw** | separate fn | `eff_mu` | **included** |
| `related` / `related_in` | unrefuted | **two functions** | `eff_mu` | excluded |
| `related_objects` | unrefuted | separate fn | raw `rating−2rd` | excluded |
| `completions` | `v_consensus_resolved` | separate fn | `edge_rank(eff_mu_raw)` | resolved |
| `top_relations` | `v_consensus_resolved` | global | `edge_rank` | resolved |
| `salient_facts` | unrefuted | separate fn | `eff_mu` | excluded |
| `shared_objects` | unrefuted | multi-subject | support, then sum | excluded |
| `classify_circuit` | `consensus` raw | pair | `row_number` | included |

**Three tables/views, four rank dialects, four ways to say "direction," and an
inconsistent refuted policy.** `consensus_out`/`consensus_in` — the pair a new
caller reaches for first — are the only neighbour functions that return refuted
edges, alongside the two crawl feeders.

**`related` and `related_in` are the same body** with `object_id`↔`subject_id`
swapped, including both comment blocks verbatim. That is `SCAN(direction)`
written twice.

**`consensus_step_edge` uses the both-directions `OR`** (`(subject=from AND
object=to) OR (subject=to AND object=from)`) — the exact anti-pattern
`prompt_coherence.sql.in:9–11` blames for the 280s hang — and `graph_cascade.c`
executes it **once per path step**.

---

## 4. The #686 outage class is still open

`chat()` was hotfixed to remove three costs from its orientation
(`chat.sql.in:100–135, 193–217`):

- `lower(realize(x.syn)) = x.surface` — a render + string compare per candidate;
- a correlated `count(*)` band-mass subquery — *"measured 29.6s for one candidate
  on the seeded corpus"*;
- `top_synset()` per token — *"73s, because top_synset is bubble_up per constituent."*

**All three are still live, verbatim, in `converse()` and `converse_walk()`:**

```
converse.sql.in:35-47      converse_walk.sql.in:56-68
  (lower(realize(x.syn, p_lang)) = x.surface AND char_length >= 3) AS exact_hit,
  (SELECT count(*) FROM consensus c
     WHERE c.subject_id = x.syn
       AND relation_highway_band(c.type_id) IN (b_def,b_tax,b_part,b_assoc)) AS band_mass
  ... FROM (SELECT DISTINCT top_synset(p.id) AS syn, lower(render_text(p.id,32)) ...)
  ORDER BY y.exact_hit DESC NULLS LAST, y.band_mass DESC, char_length(y.surface) DESC
```

`top_synset(w)` is `bubble_up(w, NULL, 1)` (`taxonomy/top_synset.sql.in`), and
`bubble_up` is a nine-CTE cascade (`taxonomy/bubble_up.sql.in`).

**And `chat()` calls `converse()` on three paths** — `chat.sql.in:241` (no topic
resolved), `:359` and `:364` (both fallbacks). So any prompt that fails to orient
re-enters the read that took chat() down. The hotfix moved the hazard behind a
conditional; it did not remove it.

The two orientation blocks in `converse` and `converse_walk` are **copy-paste
duplicates of each other**, down to the local `b_def/b_tax/b_part/b_assoc :=
1/2/4/7` declarations.

---

## 5. SEQUENCE — six implementations, four weightings, one exported model

"What token follows what" is the operation that determines the exported model's
language behaviour. It exists six times:

| impl | layer | output |
|---|---|---|
| `word_order(vocab, trajs, gap)` | SQL | raw `count(*)` |
| `sentence_order(docs, gap)` | SQL | raw `count(*)` |
| `cooccurrence_scan(max_gap)` | **C** | `(gap, subj, obj, cnt)` |
| `trajectory_cooccurrence(window)` | SQL over the C one | `cnt, subject_total` |
| `trajectory_pairs` (table) → `trajectory_pairs_plane` | materialized | `(cnt/subject_total)/gap` |
| `geometry_successors(point, …)` | **C** | successor + frequency |

`word_order.sql.in` and `sentence_order.sql.in` are **structurally identical
queries** — same `t / g / bg / cnt` CTE names, same `lead(…) OVER (PARTITION BY …
ORDER BY ord)`, differing only in `e.tier > 2` vs `e.tier = 4` and the vocab
join. One is not implemented in terms of the other.

Four incompatible weightings of that same adjacency:

1. raw count — `word_order`, `sentence_order`
2. `(cnt/subject_total)/gap` — `trajectory_pairs_plane`
3. add-k log-conditional `log((c(x,y)+k)/(c(x)+k·V))` — `continuation_conditional_plane`
4. **PPMI**, in C# — `FoundryExport.cs:1008` `ApplyPpmi`

Downstream, `walk_continuations` (C), `trajectory_generate.c`, `steered_walk.c`
and `geometry_successors.c` each re-derive or re-consume this again.
`CLAUDE.md` already records that `steered_walk.c`/`trajectory_generate.c`
consolidation is "tracked open work" — the scope is four more than that.

---

## 6. The export path runs a second epistemology

This is the most consequential finding.

`continuation_conditional_plane.sql.in:14–20` records a deliberate decision: the
PRECEDES-consensus leg was **removed**, and sequence is read from geometry
instead — *"the trajectory already holds the ordered sequence losslessly."*
Coherent. But the consequence is not stated anywhere as law: **sequence never
enters the fold**, so none of the adjudication machinery — Glicko, RD, witness
count, source trust, refutation — applies to it.

What is applied instead, in C#, before the weights reach the model:

```
FoundryExport.cs:1002    if (FoundryDefaults.Ppmi) ApplyPpmi(adj);       // Ppmi = true (default)
FoundryExport.cs:1028      double pmi = Math.Log(w * n / denom);
FoundryExport.cs:1029      if (pmi > 0) nr.Add((o, pmi));                // ← floor: edges dropped
FoundryExport.cs:1003    return CooFromAdj(adj, degreeCap);             // ← top-k per row
FoundryExport.cs:1070    Normalize(p)                                   // ← max-abs rescale
FoundryExport.cs:1085    PositivePart(p)                                // ← negatives dropped
```

Against `CLAUDE.md`: *"The fold plus read-side RD/eff_mu IS the noise model: no
operator-invented floors, caps, or top-k anywhere. Top-k exists only as a query
LIMIT."* Here there are four operator transforms in one chain, and the degree cap
is applied **twice** — `consensus_adjacency(p_degree_cap DEFAULT 64)` already caps
via `row_number()` (`consensus_adjacency.sql.in:47–52`), then `CooFromAdj` caps
again client-side.

PPMI is the canonical count-based distributional-semantics statistic. Applying it
to a count plane is a defensible normalization *of counts* — the issue is not that
PPMI is wrong arithmetic, it is that **half the exported model is built from
counts at all**, on a substrate whose thesis is that adjudicated evidence replaces
count statistics. The relational planes carry `rank × signed_mu × e^(−κ·rd) ×
witness_sat`; the sequence planes carry `log(c·n/(r·c))` with a floor. Two
epistemologies, one GGUF.

**Correction to a first-pass reading:** `ApplyPpmi` runs on `word_order`'s raw
counts, **not** on `consensus_adjacency`'s Glicko weight (`FoundryExport.cs:456`
reads adjacency in a different function). PPMI does not overwrite the fold. The
finding is the split, not an overwrite.

### 6.1 `FoundryDefaults` is a hyperparameter table

`Laplace.Core/Core/FoundryDefaults.cs` — *"synthesis knobs — constants in code,
not env or config files"* — holds ~25 tuned scalars: `AttnGain 0.5`,
`ResidGain 0.5`, `FloorCorrectionGain 0.10` (*"Gain sweep 2026-07-09 … 0.10 is
the Pareto knee"*), `FactorSpectrumAlpha 0.25`, `GateZ 6.0`, `CtxQk 8.0`,
`CapFrac 0.05`, `HilbertPeScale 0.25`, `MetricBasisGain 4.0`, `CoordScale 20.0`.

These are empirically-swept hyperparameters — the thing a construction-not-training
path claims not to need — living as `const` in a library, versioned only by git.
They are not attested, not scoped to a pour, and not reproducible from the
substrate. Whatever their merit, they are **app-metadata that decides exported
weights** and they belong under the same provenance discipline as everything else
(`substrate-vs-metadata-boundary`).

---

## 7. The instruction vocabulary is string literals

### 7.1 Relation names

| layer | hardcoded `relation_type_id('NAME')` sites | distinct names |
|---|---|---|
| SQL functions | **212** across 40 files | **76** |
| extension C | 17 (`graph_contrast.c` 11, `recall.c` 3, `graph_taxonomy.c` 3) | — |
| C# | ~162 string literals (`"IS_A"` 45, `"HAS_DEFINITION"` 40, `"PRECEDES"` 21, `"HAS_LANGUAGE"` 21, …) | — |

The relation vocabulary is *governed data* — `engine/manifest/relation_types.toml`,
codegen'd to `highway_manifest.h`, CI-gated for determinism. Every consumer then
re-enters it as a quoted string. Renaming a governed relation compiles clean and
fails at runtime, per call site. `related_objects.sql.in:4–6` records that this
already bit once (the `DEFINES`/`HAS_DEFINITION` bug).

### 7.2 Read shapes — five copies

1. `query_shapes.sql.in` — 14 shapes as a SQL `VALUES` literal.
2. `recall_route.c:64` `route_intents[]` — comment claims it is *"Kept in one
   place so the C dispatch, the SQL catalog (query_shapes) and any UI stay in
   parity."* It is the second copy.
3. `recall.c:347` `kSingleArgIntents[]` — a data table covering **4 of 14**.
4. `recall.c:404–555` — a `strcmp` if/else ladder for the other 10. The
   tabularization was started and abandoned.
5. `chat.sql.in:313–330` — a sixth private opinion, including a
   `NOT IN ('describe','fallback','walk','what_is')` escape hatch.

Plus 18 one-line `recall_*_response` SQL adapters whose only job is to normalize
to `(reply, eff_mu, witnesses)`.

### 7.3 Salience bands — magic integers in four places

`b_def=1, b_tax=2, b_part=4, b_assoc=7` are declared as local plpgsql variables in
`converse.sql.in:22–25`, `converse_walk.sql.in:34–37`, `converse_facts.sql.in:31–34`,
and appear as bare literals in `chat.sql.in:262,267,272,295,302`. The band ladder
is governed manifest data with a published catalog (`relation_band_catalog()`,
used at `chat.sql.in:304`) — four consumers ignore it.

### 7.4 Operator-invented ontology filters

- `salient_facts.sql.in` — a **17-relation `NOT IN` blacklist** plus two family
  exclusions, hand-listed in the body.
- `gaps.sql.in` — a hardcoded 9-relation "expected arenas" list.
- `non_kin_assoc_types()` — 4 family roots excluded from the kin band.
- `label_is_content()` — three regexes, including `p_label !~ ' '`, which drops
  **every multi-word concept**. Its own header calls the predicate *"a stoplist in
  disguise… retained only as a last-resort guard"* — and `converse_facts.sql.in:161`
  still has it in the `WHERE`.

Each is a defensible local judgement. Collectively they are a hand-maintained
ontology living in query bodies, on a substrate whose thesis is that band + family
+ Glicko decide salience.

---

## 8. REALIZE — 17 sites, and the scalar/batch pair is two implementations

`realize()` (`realize/realize.sql.in`) is a 5-arm SQL `COALESCE` ladder.
`realize_batch()` is native C whose header states it *"Reproduces the scalar
COALESCE ladder exactly"* and refers to "parity notes." Two implementations,
reconciled by prose. The correct shape is `realize(id) := realize_batch(ARRAY[id])[1]`.

Around them: `_realize_has_name`, `_realize_synset_lemma`, `_realize_translation`,
`_realize_canonical`, `_realize_defines`, `resolve_name`, `label`, `label_or_hex`,
`label_is_content`, `type_label`, `render`, `render_text`, `render_text_fast`,
`render_text_batch`, `canonical_names`. `resolve_name.sql.in:11–30` documents that
three of these previously *disagreed about what a name is*; it folded two and left
the rest.

### 8.1 REALIZE before SELECT — 30 bodies

`never-resolve-names-per-row` is the project's own law, and `related`/`related_in`
/`salient_facts`/`relate_path` were each individually fixed for it. Thirty bodies
still call a scalar realizer inside a row-producing SELECT. On the hot path:

- **`chat.sql.in:261,266,271`** — ELABORATE, three subqueries, `realize()` per row.
- **`chat.sql.in:292,298`** — BAND LENS, `realize()` per row on **both** arms of a
  `UNION ALL` over *every* in- and out-edge of the topic, before grouping and
  before any limit. Unbounded in topic degree — the #686 shape exactly.
- **`converse_facts.sql.in`** — `realize()` per row in the `parts`, `kin`,
  `taxonomy` and `web` arms (four sites).
- `define`, `define_bootstrap`, `define_with_context(_bootstrap)`,
  `recall_walk_response`, `recall_what_is_response`, `recall_examples_response`,
  `recall_interaction_response`, `recall_fallback_*`, `links`, `concept_peers`,
  `consensus_out_labeled`, `evidence_receipt`, `synset_gloss`, `retrieve_grounded`,
  `model_factor`, `corpus_word_vocab`, `grapheme_floor_vocab`,
  `recall_trajectories`, `examples`.
- **In C:** `spi_common.h:153,171` publish `spi_realize`/`spi_label` as per-id SPI
  calls (`recall.c:208,219,451`). `graph_contrast.c:296` and `graph_taxonomy.c:367`
  each independently discovered and fixed this **locally** — two private batchings,
  no shared primitive.

---

## 9. Order of operations — spec 36 §3 is not what runs

`CLAUDE.md` / `docs/specs/36`: *"ONE canonical order of operations (S0→S10)… No
stage may be skipped silently: a stage that cannot run degrades explicitly and
says so in the response envelope."* Both halves are false today.

**S1/S2/S3 is ported, callable, and wired to nothing.** `prompt_coherence` was
ported to C in the tip commit `684c094`; `prompt_coherence.sql.in:29` declares it.
Grep across all SQL and C#: **zero call sites.** The only mentions in `chat.sql.in`
(180, 193, 216) are comments explaining why it is not called. `chat()` still runs
the superseded election (218–234) that its own comment describes as answering
*"What is a pawn in chess?"* with *"A is the 1st letter of the Roman alphabet"*
and resolving *car* → TZAR. **The tip commit's payload is dead code.**

**S8 is off, so S9 is the success path.** `chat.sql.in:345–357`: `converse_tiered()`
is hotfixed off (a `containers_of()` arm >100s on `dog`; a per-word `top_synset()`
at 73s), and `converse_about` — template prose — carries `describe`. Spec 36 says
template prose *"is the S9 FALLBACK… never the success path."*

**No envelope exists.** `chat()` returns bare `text` (`chat.sql.in:35`). A caller
cannot distinguish "S1–S3 elected this topic" from "S1–S3 is disabled and this is
the known-wrong fallback."

**`chat()` is not thin.** Header: *"a THIN orchestrator."* Body: inline language
tally (81–90), inline topic election duplicating `prompt_coherence` (218–234),
three hand-rolled band reads (260–275), a hand-rolled bidirectional band lens
(286–308). ~200 of 375 lines are commentary on superseded designs — the file is a
changelog that executes.

---

## 10. `define()` and `senses()` — four bodies, install-order arbitration

`manifest.install`:

```
140  lexical/senses_bootstrap.sql.in            -- CREATE senses(bytea)
142  lexical/define_bootstrap.sql.in            -- CREATE define(bytea,int)
218  lexical/senses.sql.in                      -- REPLACES senses(bytea)
220  lexical/define.sql.in                      -- REPLACES define(bytea,int)
```

The `_bootstrap` files are **same-signature** bodies with **different semantics**
(`define_bootstrap` reads `HAS_DEFINITION` straight off the word; `define` goes
through `lexical_peers`). They exist to break the
`senses → bubble_up → lexical_peers → senses` install cycle — legitimate, but:

- which body is live is decided by line order in a hand-maintained 397-line
  manifest, with **no post-install assertion** that the real body won;
- I diffed `manifest.install` against `manifest.upgrade` — they agree on this
  ordering today (only `bootstrap/`, `seed/`, and one dropped index differ). That
  is luck, not a gate;
- on top sits `define_fast` (`recall/define_fast.sql.in` → `recall.c`), a **native
  reimplementation of the whole chain**, written because the SQL composition
  measured *"48+ seconds / 2.27M shared-buffer hits for a single word."*

Four bodies for one operation; one wins by manifest position; one is a from-scratch
C rewrite of the other three.

---

## 11. WITNESS — the close is implemented three times

`chat.sql.in:15–19` makes this policy: the close happens at the frontend, in every
caller. The *payload* is centralized (`ConversationContent.TryBuildTurnChange`,
`BuildTenantBootstrapChanges`). The *sequence* is not — each frontend
re-implements: load perfcache → `ConsensusAccumulatingWriter(NpgsqlSubstrateWriter)`
→ resolve tenant scope → apply bootstrap → cache scope → attribute user once →
build turn change → apply.

- `Endpoints.Mcp/SubstrateTools.cs:472–507` — own `_turnBootstrapped`/`_turnDepositBroken` latches.
- `Endpoints.OpenAICompat/TurnWitness.cs:62–130` — own `scopes` dict,
  `attributedSessions` set, `floorPresent` check, failure counter.
- A third in `Laplace.Cli`.

They already diverge: **only `TurnWitness` checks `FloorPresentAsync`** before
depositing. MCP does not. Same close, different preconditions.

---

## 12. What is right — do not touch

- **The ingest spine.** `IngestBatchPipeline` → `SubstrateChangeBuilder` →
  `ConsensusAccumulatingWriter` → `NpgsqlWorkingSetApply`, under `IngestRunner`.
  One sequence, explicit ordering contracts, lazy per-file sources, parallel by
  default, existence gate short-circuiting on trunk roots. **This is the template.**
- **The decomposer layer.** 105 files, three shared bases (`ComposeDecomposer`,
  `GrammarComposeDecomposer`, `GrammarDecomposer`), shared builders. Thin valets.
- **The grammar lanes** look like duplicates by name; `GrammarIngestHandler` (row)
  and `GrammarComposeHandler` (whole-file) are a real taxonomy with distinct live
  callers (OMW/SemLink vs Code/Stack/TinyCodes/Repo). **Checked and cleared.**
- **The chess lane consolidated and held.** `LearnedPst`, `SubstrateStateValuer`,
  `SubstrateTurnHost`, `SubstrateRootBias` all now read
  `consensus_by_ids($1,$2)` — four hand-copied `(rating-2*rd)` reads collapsed onto
  one type-pruned primitive. This is what the rest of the read side should look like.
- **The app layer mostly respects the function surface.** Only 20 raw
  `FROM consensus|entities|attestations|physicalities` references across 12 C#
  files outside tests.
- **Native math is genuinely used** where it matters:
  `DynInterop.LaplacianEigenmapsFromSparseGraph`, `GramSchmidtOrthonormalize`,
  `ProcrustesFit/Residual/Apply` (`FoundryExport.cs:1430,1442,1491–1526,1663`).
  The C# remainder is graph assembly and weighting, not the linear algebra.
- **`label_is_content`, `consensus_by_ids`, `related_objects`, `resolve_name`,
  `edge_rank`, `non_kin_assoc_types`** are all *correctly shaped* consolidations.
  The failure is that each was done once, locally, without a rule that stopped the
  next one being re-derived.

---

## 12b. MEASURED 2026-07-27 — OP3 is fast, correct-ish, and NOT the fix

Ran against the live substrate (113.7M consensus rows), extension
`6bf54084bed2596f`.

**The native port's perf gate passes.** `prompt_coherence()` on the topics that
took >280s as SQL:

| prompt | rows | time |
|---|---|---|
| What is a pawn in chess? | 7 | 3.88 s |
| What is a dog? | 5 | 2.19 s |
| What are the parts of a car? | 8 | 2.94 s |
| Tell me about a tree | 6 | 2.34 s |
| What is music? | 3 | 1.42 s |
| What is a river? | 5 | 2.08 s |

**Its correctness gate does not.** Under the ranking its own header documents
(`rel_mass`, then `peers`, then `coherence`, then `denote_mu`):

| prompt | elects | verdict |
|---|---|---|
| What is a pawn in chess? | token `is` → *"et"* | wrong |
| What is a dog? | token `dog` → *"it"* | right token, wrong sense |
| What is a river? | token `is` → *"et"* | wrong |
| What are the parts of a car? | token `car` → **"automobile"** | **right** |

Only the shape where a prompt token *names a relation* elects correctly — that
is `rel_mass` working exactly as designed. `coherence` and `peers` do not
discriminate: function words are wired to everything, so they carry the most
mass (`of` 5.5e13 vs `car` 4.1e12) and the most peers. This is the same failure
as `denote_mu` and as breadth before it — **three scalars, three ports, same
outcome**, because mass-shaped signals cannot separate function words from
content words.

Not a language artifact: re-checked with English pinned, `dog` still elects a
synset realizing as *"it"*.

**Decision: OP3 is NOT wired into `chat()`.** Doing so would replace one known
-wrong election with a different known-wrong election. §17 step 2 is amended
accordingly.

### 12b.1 The actual root cause is upstream, in the identity/partition seam

`chat.sql.in` blames the *election* for answering "What is a pawn in chess?"
with "A is the 1st letter of the Roman alphabet", and a whole native port was
written against that diagnosis. The cause is two layers up:

```
word_id('a') = canonical_id('a') = 17762fddd969a453925d65717ac3eea2
entities rows for that id:   tier=0 type=Codepoint      -- LATIN SMALL LETTER A
                             tier=2 type=POS
senses(word_id('a')) -> a | a | LATIN SMALL LETTER A | CYRILLIC SMALL LETTER A | la | and
```

`entities` is **`PRIMARY KEY (id, tier)`**, LIST-partitioned by `tier`. Postgres
requires the partition key inside the PK, so partitioning by tier *forces tier
into the identity key* — and one content hash can therefore hold one row per
tier. **73 ids do**, and every one is a single character: `a A e b z u O R 9 - /
।`. Those are among the highest-frequency tokens in any English prompt, so the
blast radius is nothing like the row count.

The hash is clean — tier is genuinely not mixed in, exactly as the law says. But
`CLAUDE.md`'s first invariant, *"Same content = same id at every tier… 'Fine' as
a one-word reply IS the sentence IS the word — one id"*, is **unenforceable at
the schema level**: the schema models one-entity-per-(id, tier), and the read
side then unions their senses.

That union is the bug. It is not a ranking problem, and no election — however
fast or well-designed — can fix it, because both sense sets are legitimately
attached to the same id.

**Open, and the real next question:** is the tier-2 `type=POS` row for `a` a
correct attestation (a POS-tagged surface, in which case the read must
disambiguate by requested tier/type) or a decomposer minting a POS-typed entity
at the surface's identity (in which case it is a witness-boundary defect and the
fix is at ingest)? 36,049 POS-typed tier-2 entities exist and none render, so
they are not surface content in general — which points at the second. Not yet
resolved; it needs the grammar/UD lane read at body level, which this audit has
not done.

---

## 13. The ops layer — 111 scripts, two orchestrations, one law per copy

Previously unaudited. `scripts/win/` holds **83** `.cmd`/`.ps1`; `scripts/`
holds **28** `.sh`. This is the largest single "same thing, slightly
differently" surface in the repo, and it carries real invariants.

### 15.1 Twelve seed scripts, two independent orchestrations

```
seed-everything ─┐                            seed-chain ── Laplace.Cli ingest chain <12 sources>
seed-full ───────┼─→ seed-ladder ─→ seed-stage ×6 ─→ seed-step ×N     (ONE process)
                 │                    (101 ln)      (329 ln)
                 └─ db-reset, audit
seed-substrate, seed-continue, seed-deferred-lexical, seed-post-wiktionary,
seed-resume-prove                             ← five more entry points
```

`seed-chain.cmd`'s own header states the reason it exists:

> *"One-process foundation seed: the whole ladder runs through `ingest chain` in
> a single Laplace.Cli — one startup, one perfcache map, one native runtime
> init, instead of one per source (**seed-step.cmd pays those 12x**)."*

**Nothing calls `seed-chain`.** `seed-everything` → `seed-ladder`, `seed-full` →
`seed-ladder`, and `seed-ladder` → `seed-stage` → `seed-step` — the 12×-startup
path. The fast path was written, documented, and left unwired. This is the
`prompt_coherence` shape again, in shell.

`seed-everything` and `seed-full` are the same script: kill/reset → ladder →
`substrate-audit.sql`. `seed-full` adds `rebuild-all` and drops the lockfile and
the timestamped log. Neither is expressible in terms of the other.

### 15.2 Invariants re-implemented per script

- **The one-ingest-at-a-time mutex** — the same
  `Get-CimInstance Win32_Process | Where-Object { … -match 'Laplace\.Cli' }`
  block appears in `seed-chain.cmd`, `seed-everything.cmd`,
  `seed-post-wiktionary.cmd`, `seed-step.cmd`, `bench-matrix.ps1`,
  `tree-lock.ps1`. Six copies of a binding operational law
  (`CLAUDE.md`: *"One ingest at a time"*). Any script that forgets it breaks the law
  silently.
- **The per-source `evidence_count` verify** — 11 implementations across both
  toolchains: `seed-step.cmd:verify_step`, `seed-chain.cmd` (inline, with its own
  hardcoded 13-source list), `qa-isolated.cmd`, `seed-layer-check.ps1`,
  `seed-layer-check-batch.ps1`, `derive-model-source.ps1`, `audit-decomposers.sh`,
  `decomposer-ensure-floor.sh`, `ensure-foundation.sh`, `model-synthesize-ci.sh`,
  `sql/chess-test-status.sql`.
- **Five operations exist on both toolchains** with no shared logic: `converse`,
  `decomposer-matrix`, `decomposer-promote`, `decomposer-test`, `setup-host`.

### 15.3 The shape this should take

The CLI already *is* the operation surface — `ingest chain` proves it. Most of
these 111 scripts are argument-marshalling and a mutex around one CLI call.
`seed`, `verify`, and the mutex belong as CLI subcommands (`laplace seed stage
knowledge`, `laplace verify source wordnet`) with **one** cross-platform
implementation and **one** lock. The scripts shrink to thin platform shims.

---

## 14. The ingest source roster is written out nine times

| # | Site | Form | Entries |
|---|---|---|---|
| 1 | `Cli/IngestDispatchTable.cs:32` `Routes` | dict of lambdas | 33 |
| 2 | `Decomposers/Composition/SeedIngestComposition.cs:85` `Resolve` | switch | 24 |
| 3 | same file `:34–57` | `AddTransient<T>()` | 24 |
| 4 | `Cli/IngestDataPaths.cs:7` `RelativeByCli` | dict of paths | 22 |
| 5 | `scripts/win/seed-step.cmd:48–75` | `goto` ladder | 28 |
| 6 | `scripts/win/seed-stage.cmd` `:stage_knowledge` | `for %%s in (…)` | 13 |
| 7 | `scripts/win/seed-chain.cmd` | `ingest chain …` arg list | 12 |
| 8 | `scripts/win/seed-chain.cmd` verify | decomposer **class names** | 13 |
| 9 | `EtlManifest` | separate routing lane | — |

**They already disagree.** `seed-chain` (7) omits `conceptnet`, `ud`, and
`wiktionary`, which `seed-stage` (6) includes — so the two seed orchestrations
produce *different substrates*. `IngestDataPaths` (4) carries `image` and `audio`
keys that no dispatch route can reach.

`IngestDispatchTable`'s header claims *"Table-driven ingest dispatch (doc 13
Phase 1). One registry; no special-case ordering forks."* **17 of its 33 rows are
byte-identical except for one string:**

```csharp
["wordnet"] = cli => IngestCommands.IngestViaRunnerAsync(
    CliRuntime.Decomposers.Resolve("wordnet"), IngestDataPaths.Resolve("wordnet", cli.Path),
    skipLayerCheck: false, cli),
```

A table whose rows are copy-pasted lambdas is a `switch` with extra syntax. The
genuine table form is `["wordnet"] = Standard("wordnet")`, leaving only the ~10
rows that actually differ (chess fusion, model, code, ETL) visible as exceptions.

---

## 15. Smaller findings from the broad sweep

- **`IngestRunner` duplicates its own flush policy.** The sequential path
  (`IngestRunner.cs:245–290`) and the compose-ahead channel path (`:314–390`)
  each independently implement the same four-branch decision: process intent →
  flush-before-add if the next intent would exceed the COPY budget → flush on
  boundary if the batch is worth a COPY → final flush. The reasoning comments
  (177–210) are excellent and apply to both; the code is written twice. A budget
  or boundary rule changed in one path silently diverges from the other.
- **The API reaches past the public surface into a private helper.**
  `Laplace.Endpoints.OpenAICompat` calls `laplace._realize_synset_lemma(` twice —
  an underscore-prefixed internal arm of `realize()`. It also calls
  `laplace.eff_mu(` three times, computing the confidence axis in the HTTP layer.
  Otherwise the API is well-behaved: **74 distinct published functions** called,
  only 20 raw table references repo-wide.
- **`engine/core` is clean.** 32 files, no duplicate primitive pairs. The
  segmentation family (`grapheme_break`/`word_break`/`sentence_break`/
  `grapheme_floor`) is UAX-mandated separation, and the id family
  (`hash128`/`hash_composer`/`merkle_dedup`/`tier_tree`) is real layering.
  **Checked and cleared.**
- **Three QK kernels** in `engine/synthesis` — `qk_pairs_threshold.cpp` (143),
  `qk_pairs_threshold_pruned.cpp` (200), `qk_project_cached.cpp` (205). The
  pruned/cached split is the documented projection-cache optimization
  (`qk-pruned-projection-cache`), so this is a deliberate fast/reference pair —
  but it is a **third** instance of the same "optimized version added beside the
  original" pattern, and it needs the same treatment: one entry point, mode
  selected internally, one test proving bit-identity.

---

## 16. Closing the coverage gap

The subsystems held back from the earlier passes, now checked. Most are clean —
recorded so the reduction targets stay honest.

**`Laplace.Chess` (74 files) — clean, checked and cleared.** `MoveGen` is the
single move generator (`Modality/MoveGen.cs`, `Legal`/`Pseudo`/`IsSquareAttacked`
all `static … (Board b, …)`). `Board.ToFen`/`FromFen` is the single FEN
parse/emit. The 19 `Fen` hits in `ChessEngineService.cs` are `state.Board.ToFen()`
calls and record fields — pass-through, not reimplementation. `San.Resolve`/`ToSan`
is single-source. No duplicated board logic.

**The model lane — clean.** `ModelTokenEdgeETL.cs` (1149 lines) routes every
numeric operation through `DynInterop`: `F32ToF64`, `LayerNormRowsD`,
`ExpandKvHeadsD`, `ProjectEmbeddingD`, `FfnWriteVectorsD`, `AddRowVectorD`,
`HypotRowsD`. 24 `Math.`/loop hits across 1149 lines. This is what "C# and SQL
orchestrate, native does the math" looks like when it is obeyed — and it is the
direct contrast with `FoundryExport.cs` (§6), which does its graph assembly and
re-weighting client-side.

**The consensus views — clean, and the best-shaped consolidation in the tree.**
`v_consensus_unrefuted`'s header: *"Shared predicate for the ~21 functions that
each reimplemented `NOT refuted(c.rating, c.rd)` inline."* `v_consensus_resolved`
publishes `eff_mu_raw`/`eff_mu` once. `v_consensus_edges` layers a stricter
filter and says so. **The defect is not the views — it is that nothing says which
one a given read must use**, which is precisely how §3's four-read-surface
divergence arose.

**Billing — one duplicated state machine.** `IBillingEntitlementStore` has two
full implementations: `InMemoryBillingEntitlementStore` (`EntitlementBilling.cs:56–199`)
and `PostgresBillingEntitlementStore` (`:13–199`). The interface+double pattern is
correct, but the **business rules** — plan activation, renewal, deactivation, and
`TryConsumeCreditAsync`'s credit arithmetic — are written twice. A store should
persist; it should not re-decide when a credit is consumable. Same shape for
`IBillingConfigStore`. Not on the substrate's critical path, but it is the same
class of defect.

**`Laplace.Migrations` — trivial.** One `Program.cs`. Consistent with
`extension-is-the-deployment-unit`: substrate objects ship with the extension, not
as migrations. Nothing to consolidate.

**`web/` (161 files, 18k lines) — minor.** Two SSE clients (`api/sse.ts` and
`chess/lab/sse.ts`) and one component (`layout/SubstrateStatusBanner.tsx`) issuing
a raw `fetch` instead of going through `api/client.ts`. Small, but it is the same
"bypass the shared surface" pattern.

**Tests — minor.** `LocalPgFixture` declared twice across the suites.

**Still not read at body level:** `FoundryCommands.cs` (2238 lines) and
`SubstrateClient.Explore.cs` (1055). Both are orchestration over surfaces already
audited here; neither is expected to change the findings, but neither has been
verified.

---

## 17. Consolidation plan

### P0 — one WEIGHT, one epistemology
1. Expose `laplace_walk_edge_weight` as a SQL C function; delete the SQL
   restatement at `consensus_adjacency.sql.in:36–44`. One direction for κ and the
   witness half-max, not two constants.
2. Cross-language parity regress over **that** formula (extend the
   `word_law.sql:63` pattern past the trivial case).
3. Define `WEIGHT(rating, rd, wc, type, mode)` with modes
   `{conservative, salience, complete, strength}` — the four legitimately different
   questions — and route all seven current formulas through it.
4. Replace open-coded `rating − 2·rd` in function bodies with `eff_mu()` (index
   expressions stay literal — `eff-mu-inlining-law`).
5. **Decide the sequence epistemology explicitly and write it into spec 36/14.**
   Either sequence enters the fold (a `FOLLOWS`/`CO_OCCURS_WITH` lane with real
   Glicko state), or the geometry-derived count plane is declared law and PPMI /
   `pmi>0` / the double degree cap are justified in that frame. Today it is neither
   — it is an undocumented second epistemology with four operator transforms.
6. Move `FoundryDefaults`' 25 tuned scalars under provenance: attested,
   pour-scoped, reproducible. `const` in a library is not a recipe.

### P0 — the forward pass runs its own ladder
7. Wire `prompt_coherence()` into `chat()` S1/S2/S3, replacing lines 218–234.
   Time on `dog`/`tree`/`music`/`river` **before** merge, per `CLAUDE.md`'s own
   high-degree rule — not on `pawn`.
8. **Fix `converse()` and `converse_walk()`'s orientation** (§4). Until then the
   outage class is open behind a conditional. Both should call the same oriented
   entry `chat()` uses; the duplicated block should not exist twice.
9. Change `chat()`'s return to an envelope with per-stage `ran | degraded |
   skipped` + reason. This is what makes "no stage may be skipped silently"
   enforceable rather than aspirational.
10. Fix `converse_tiered`'s two unbounded reads (the `containers_of` arm, the
    per-word `top_synset`) and restore S8 ahead of S9. Fixes reportedly exist on
    `perf/content-ladder-ledger` and did not make the merge — recover them.

### P1 — REALIZE once, last
11. `realize(id, lang) := realize_batch(ARRAY[id], lang)[1]`. One body.
12. Rewrite `chat.sql.in` ELABORATE (260–275) and BAND LENS (286–308) to rank/limit
    on ids then one `realize_batch` — the shape `related`/`salient_facts` already use.
13. Same for `converse_facts`' four arms.
14. Promote `graph_contrast.c`/`graph_taxonomy.c`'s local batching into a shared
    `spi_realize_batch`; retire `spi_realize`/`spi_label`.
15. Sweep the remaining ~24 bodies.

### P1 — the vocabulary becomes data
16. Generate SQL and C# relation-name constants from `relation_types.toml` (the
    codegen that already produces `highway_manifest.h`). 212 + 17 + 162 literals
    become symbols; a rename becomes a build break.
17. One shape table as the single source; `query_shapes()` selects it,
    `route_intents[]` and the `strcmp` ladder index into it, `chat()`'s private
    branch list (313–330) is deleted. Finish `kSingleArgIntents` — all 14, not 4.
18. Replace the four local band-constant declarations with `relation_band_catalog()`.
19. Move the hand-listed ontology filters (`salient_facts`' 17-name blacklist,
    `gaps`' 9-name list, `non_kin_assoc_types`, `label_is_content`) into governed
    manifest data, or delete them in favour of band + family + Glicko.

### P2 — one SCAN, one SEQUENCE, one close
20. `SCAN(subject, direction, type_filter, refuted_policy, weight_mode, cap)` in C
    against partitioned `consensus`; the ~22 SQL neighbour functions become thin
    named views. Delete `related_in`. Make the refuted policy an explicit argument
    so `consensus_out`'s current inclusion becomes a choice, not an accident.
    Replace `consensus_step_edge`'s `OR` with two indexed probes.
21. One `SEQUENCE(scope, gap, weighting)` primitive. `word_order` and
    `sentence_order` collapse to one call with a tier argument; the C
    `cooccurrence_scan` is the engine; the four weightings become named modes.
22. `TurnCloser` in `Laplace.Substrate`: floor check → writer → scope cache →
    bootstrap → attribution → build → apply. MCP, OpenAICompat, CLI call it.

### P1 — the ops layer becomes CLI subcommands
24. **Wire `seed-chain` into `seed-ladder`/`seed-everything`/`seed-full`**, or
    delete it. Today the 12×-startup path is the only one anything calls.
25. One source roster (§14). Generate the shell lists, `IngestDataPaths`,
    `SeedIngestComposition`, and the DI registration from
    `IngestDispatchTable`/`EtlManifest` — or from a manifest both read. Collapse
    the 17 identical `Routes` rows to `Standard("<key>")` so only genuine
    exceptions are visible.
26. Move the ingest mutex and the `evidence_count` verify into the CLI
    (`laplace seed …`, `laplace verify source …`) — **one** lock, **one** verify,
    both cross-platform. 6 mutex copies and 11 verify copies go to 1 each.
27. Deduplicate `IngestRunner`'s two flush paths (`:245–290` vs `:314–390`) onto
    one budget/boundary policy object.

### P2 — the leaks
28. `Laplace.Endpoints.OpenAICompat` must not call `laplace._realize_synset_lemma`
    (private) or compute `laplace.eff_mu` itself. Route through `resolve_name` /
    the published confidence column.
29. Give the three QK kernels one entry point with an internal mode, plus a
    bit-identity test between reference and pruned/cached.

### P3 — lexical
23. Collapse `define`/`senses` × `{plain, _bootstrap, _with_context}` onto
    `define_fast`'s native path. If the cycle-break must survive, add a
    post-install assertion that the real body won — never leave it to line order.

---

## 18. Metrics

| | now | target |
|---|---|---|
| SQL function files | 332 | < 180 |
| `WEIGHT` implementations | 7 (6 semantics) | 1 opcode, 4 modes |
| `SCAN` implementations | 22 (12 semantics) | 1 + thin views |
| `SEQUENCE` implementations | 6 (4 weightings) | 1 + named modes |
| `REALIZE` bodies | 17 | 1 + accessors |
| Per-row realize sites | 30 SQL + 3 C | 0 |
| Hardcoded relation names | 212 SQL / 17 C / 162 C# | 0 (generated) |
| Places the shape set is written | 5 | 1 |
| Places band constants are written | 4 | 1 |
| Spec 36 stages wired | S1–S3 ✗, S8 ✗ | all |
| `chat.sql.in` | 375 lines | orchestrator only |
| Turn-close implementations | 3 | 1 |
| Places the ingest source roster is written | 9 | 1 |
| Ingest-mutex implementations | 6 | 1 |
| `evidence_count` verify implementations | 11 | 1 |
| Ops scripts | 111 (83 win + 28 sh) | thin shims over CLI subcommands |
| Seed entry points | 12 | 1 (+ stage/step args) |
| Seed orchestrations | 2 (only the slow one wired) | 1 (the fast one) |
