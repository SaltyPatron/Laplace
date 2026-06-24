# Laplace Substrate — SQL Surface Audit

Inventory of the extension's SQL surface: **188 functions** across 28 numbered
`sql/*.sql.in` fragments (concatenated by `laplace_substrate.sql.in` in `#include`
order) + a `generated/` seed pair + a **dead** `inference/` subdir.
Language split: **62 native C** (thin bindings to kernels in `src/*.c`), 20 plpgsql,
108 SQL.

This doc is the map. Sections:
1. TL;DR — what to clean up, what's missing
2. Capability map — "I want to do X" → function
3. Inventory by file (status: CORE / USEFUL / REDUNDANT / DEAD)
4. Naming problems + proposed canonical surface

---

## 1. TL;DR

**Dead / orphaned (delete or wire in — do not leave as-is):**
- **`sql/inference/` (all 6 files)** — NOT in the `#include` graph or `CMakeLists.txt`
  `EXT_SQL_MODULES`. Byte-identical duplicates of functions live in `20_converse.sql.in`
  (`synset_members`, `synonyms`, `translations`, `translate_to`, `language_coverage`,
  `attention`). They ship nothing. Either make `inference/` the real home (update the
  include graph + CMake, delete the `20_converse` copies) or delete the dir. Right now
  it's a trap — it looks authoritative and isn't built.
- **Legacy consensus-fold path** (`14_period_fold`): `materialize_period_partition`,
  `materialize_period_partition_fresh`, `materialize_period_consensus`,
  `create_period_staging(integer)` — superseded by the partitioned fold pipeline
  (`consensus_fold_one_partition` / `finish_consensus_fold` / `*_walks`). Dead.
- **`continue_text`** (`26_generation`) — advertises `p_stop`/`p_boost`/`p_require_pos`
  but the body ignores them and forwards to `walk_text`. Dead params / no-op wrapper.

**Redundant (pure pass-throughs — collapse):**
- `attestation_response_type` → bare `SELECT *` of `attestation_response` (16_inspect)
- `attestation_unary_response_type` → bare `SELECT *` of `attestation_unary_response`
- `entity_curve` → pass-through of `word_curve` (23_structural)
- `nearest_neighbors_4d` → alias of `structural_neighbors` (intentional API alias)
- `eff_mu` (SQL `rating-2*rd`) vs `effective_mu` (C, same value) — two impls; the
  consensus expression indexes inline the arithmetic and call neither.

**Correctness gap (the `king` bug):** `synset_members` (the shared helper that
`synonyms`/`translations`/`translate_to`/`language_coverage`/`attention` ALL call)
traverses only `IS_SYNONYM_OF` and does **no `IS_INSTANCE_OF` filtering**. So named-entity
/ proper-noun synsets (Martin Luther King, B.B. King) flow through as "translations".
Fix belongs in `synset_members` in **`20_converse.sql.in:110-129`** (NOT `inference/` —
that's dead) — one `NOT EXISTS (… IS_INSTANCE_OF …)` clause fixes all five callers at once.

**Naming gap:** the capabilities exist but are scattered under inconsistent names. There is
no single obvious "give me an entity and everything attested about it" or "expand this
entity N hops". See §4.

---

## 2. Capability map — "I want to do X" → function

### Resolve text → entity id
- `word_id(text)` — content-address a single token (C, the atomic primitive)
- `resolve_phrase(text)` — multi-word phrase → one id (C)
- `prompt_words(text)` / `prompt_state(text)` — segment + resolve each token (+language)
- `resolve_topic(phrase, context)` — phrase → id, with pronoun/context fallback

### Render entity id → text
- `label(id)` — clean display name; prefers `HAS_NAME_ALIAS`, strips wrapper paths,
  `HAS_DEFINITION` fallback, **NULL when genuinely unnamed** (no hash placeholder). SQL.
- `realize(id, lang)` — language-scoped chain-completing realizer (C, the authoritative one)
- `render(id)` / `render_text(id, depth)` — geometry/trajectory → text (C)
- `realize_path(path[], types[], [dirs[]], lang)` — render a path as `A —type→ B —type→ C`

### ⭐ Entity → all its attestations / relations  (YOUR Q1)
- **`links(word)`** → `(relation, target, strength)` — every outgoing consensus link,
  rendered. The closest thing to "everything about X", SQL, indexed. **Start here.**
- **`salient_facts(word, lang, limit)`** → top facts across relation types, realized (C)
- **`epistemic_status(word, lang, limit)`** → all relations + verdict
  (refuted/confirmed/contested/thin) — the "with confidence" view, SQL
- `attestations_out(id, limit)` / `attestations_in(id, limit)` — **raw** edges (source,
  outcome, observation_count), subject-side / object-side. SQL.
- `consensus_out(id, limit)` / `consensus_in(id, limit)` — folded relations (rating/rd/
  volatility/witnesses). SQL.
- `related(word, type)` / `related_in(word, type)` — facts scoped to ONE relation type (C)
- `attestation_response(subject, type, source_scope[], context)` — top-k objects for a
  (subject,type), optionally scoped by source/context. SQL.

### ⭐ Recursive / multi-hop / fan-out  (YOUR Q2)
- **`walk_branches(prompt, type, depth, breadth)`** → depth×breadth branching traversal,
  returns the paths. The general "expand N hops with fan-out" (C). **Start here.**
- `walk_strongest(prompt, type, depth)` — greedy single strongest-edge path (C)
- `foundry_crawl(seeds[], budget, hops, fanout, rel_types[])` — BFS weighted crawl, the
  real bulk neighborhood expander (C)
- `hypernyms(word, depth)` — walk IS_A/hypernym chain upward (C)
- `isa_path(x, y, depth)` — taxonomic path between two entities (C)
- `relate_path(x, y, depth)` / `cascade(x, y)` — shortest relation chain, rendered (C)
- `astar_path(start, goals[], max_depth, types[])` — A* over a default semantic type set (SQL→C `astar_path_raw`)
- `relation_summary(x, y)` / `contrast(x, y)` — how two entities relate / differ (C)
- `shared_objects(subjects[], type)` — objects shared across many subjects (fan-in) (C)
- `correlate(words[])` — (relation, target) links shared across a word set (SQL)
- `attention(word, k)` — consensus-weighted neighborhood + geometry ("attention head") (SQL)

### Lexical / semantic reads
- `senses(word [,context[]])`, `define(word [,context[]])`, `examples(word)`,
  `synonyms(word)`, `translations(word)`, `translate_to(word, lang)`,
  `language_coverage(word)`, `synset_members(word)` (shared helper), `gaps(word)`,
  `collocates(word)` (PRECEDES successors)

### Geometry / structure
- `structural_neighbors(word, k)` — 4D KNN (geodesic + Fréchet), the canonical impl
- `structural_cluster(seed, eps)` / `_batch` — trajectory-shape clusters
- `structural_locale(word, near)` — density/isolation profile
- `word_curve(word)` / `word_shape_distance(a,b)` / `anagrams_of(word)`

### Generation
- `recall(prompt, context)` / `recall_session(prompt, session)` — top-level NL router (C)
- `generate(prompt, …)` / `walk_text(prompt, …)` / `walk_continuations(ctx[], …)` —
  stochastic trajectory generation
- `parse_ask(prompt)` — NL question → intent (the router front-end)

### Ops / health / metrics
- `substrate_counts()`, `substrate_health()`, `source_counts()`, `arena_counts()`,
  `consensus_stats()`, `evidence_count()`, `consensus_count()`, `content_count()`,
  `render_gaps()`, `api(like)` (self-catalog: lists every function — `SELECT * FROM api()`)

### Ingest / fold (server-side, called by the runtime not by you)
- `laplace_apply_batch(prefix)` — the one set-based COPY-staging→live merge (C)
- `entities_exist_bitmap(ids[])` / `intent_preflight(...)` — bulk existence probes (C)
- `finish_consensus_fold()` / `consensus_fold_one_partition(...)` — partitioned Glicko-2 fold

---

## 3. Inventory by file

| File | What it is | Notable |
|---|---|---|
| 01–05 | schema: `entities`, `physicalities`, `attestations`, indexes | tables only; `attestations` is the edge table |
| 06 glicko2 | rating kernel bindings + aggregate | CORE |
| 07 cascade | `astar_path_raw` (C A* traversal) | CORE traverser |
| 08 trajectory ops | Fréchet/point-count helpers | USEFUL |
| 10 bootstrap | seeds canonical vocab + deferred FKs (DO block) | CORE |
| 11 exist bitmap | bulk existence probe | CORE |
| 12 consensus schema | `consensus` table + `consensus_id` | CORE |
| 13 mu law | eff_mu / refuted / glicko constants | `eff_mu` vs `effective_mu` redundant |
| 14 period fold | partitioned Glicko-2 fold pipeline | **4 legacy fns DEAD** |
| 15 readback | render/label primitives, `canonical_names` | CORE |
| 16 inspect | `attestations_out/in`, `consensus_out_readable`, `attestation_response` | **2 `_type` dups REDUNDANT** |
| 17 consensus reads | `consensus_out/in`, `completions`, `walk_branches/strongest`, stats | CORE |
| 18 ops surface | counts/metrics/`api()` | USEFUL |
| 19 relation law | relation-type resolve/rank/canonical (C) | USEFUL |
| 20 converse | **the read/recall surface — 44 fns** (label, senses, links, attention, recall…) | CORE; holds the LIVE synonyms/translations |
| 21 seed (+generated) | canonical-name inserts (~290 + POS + ~175 rel types) | data |
| 22 cascade surface | `astar_path`, `cascade` | CORE |
| 23 structural surface | 4D neighbors/clusters, curves, exports | `entity_curve`+`nearest_neighbors_4d` REDUNDANT |
| 24 identity health | invariant checks | CORE |
| 25 intent preflight | batch existence preflight (C) | CORE |
| 26 generation | planes, vocab, walks, variants — 24 fns | `continue_text` DEAD-params |
| 27 apply batch | the set-based merge (C) | CORE |
| **inference/** | **6 fns — orphaned, NOT BUILT, dup of 20_converse** | **ALL DEAD** |

---

## 4. Naming problems + proposed canonical surface

The capabilities exist; the **names don't advertise them**, and overlapping functions have
non-parallel names. Worst offenders for "the pieces you'll need":

**"Everything attested about an entity" is split 6 ways** — `links`, `salient_facts`,
`epistemic_status`, `attestations_out`, `consensus_out`, `related`. No canonical entry.
Proposed: a single `facts(entity, opts)` (rendered, all types, with verdict) as the front
door, with the others as its typed/raw specializations.

**"Expand an entity outward" is split** — `walk_branches`, `walk_strongest`,
`foundry_crawl`, `astar_path`, `hypernyms`, `isa_path`. No single `neighborhood(entity,
hops, fanout, types)`. `walk_branches` is the de-facto general one but the name doesn't say so.

**Raw vs folded is implicit** — `attestations_*` (raw evidence) vs `consensus_*` (folded)
vs `*_readable`/`links` (rendered). A consistent `_raw` / `_consensus` / `_text` suffix
convention would make the tier obvious.

**Recommended cleanups (mechanical, safe):**
1. Delete `sql/inference/` (or wire it in and delete the `20_converse` dupes — pick one).
2. Drop the 4 legacy fold fns, the 2 `_type` pass-throughs, `entity_curve`, and
   `continue_text`'s dead params.
3. Consolidate `eff_mu`/`effective_mu` to one.
4. Add the `IS_INSTANCE_OF` filter to `synset_members` (20_converse.sql.in) — fixes the
   named-entity-in-translations bug for all five callers.
