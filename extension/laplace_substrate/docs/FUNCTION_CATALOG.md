# Laplace Substrate — Function Catalog (deep-dive + empirical)

Companion to `SQL_SURFACE.md` (the high-level map). This is the **per-function reference**:
what each really returns, how it's built, and — for the read surface — what it **actually does
when run against the live seeded substrate** (empirically tested, not assumed).

Status legend: **✅ works** · **⚠️ degraded** (returns, but noisy/polluted/mis-ranked) ·
**❌ broken** (empty/wrong) · **🔒 internal** (write/fold/ingest — not user-callable) ·
**↪ alias/dup** · **🌱 staged** (defined, not yet wired — keep).
Nothing here is marked for deletion. "Not wired" = not yet wired.

Units: ratings/rd/volatility are **fixed-point ×1e9** (1500 → `1500000000000`).
`eff_mu = rating − 2*rd` (fixed-point); `eff_mu_display = round(eff_mu/1e9,3)`;
`refuted = rating + 2*rd < neutral_mu`.

---

## 0. SYSTEMIC EMPIRICAL FINDINGS (the cross-cutting truths)

These came out of running the surface live (`king`, `dog`, `happy`, `run`, `water`).

### F1 — Scaffolding owns the consensus, so mu-ranked "facts" surface grammar, not meaning
`arena_counts()` by volume (relations / witnesses):
```
PRECEDES            2,090,790 / 14,910,465   ← word-order; most-witnessed thing in the DB
IS_SYNONYM_OF       2,064,759 /  2,886,140
HAS_LANGUAGE        1,680,593 /  3,766,586
HAS_POS             1,626,214 /  4,207,708
<unrendered hash>     995,210 /  1,143,124   ← a relation type with NO canonical name (F7)
HAS_DEFINITION        479,449 /    711,984   ← the actual semantic payload, far down
HAS_LINE_BREAK / HAS_EAST_ASIAN_WIDTH / HAS_BLOCK / HAS_AGE  ← Unicode codepoint props, huge
IS_TYPED_AS           348,696 /  3,758,619
```
Scaffolding (POS, language, line-break, east-asian-width, block, age, typed-as, lemma, features)
has both the most edges **and** the highest witness counts → highest `eff_mu`. So any function that
ranks a subject's edges by mu and doesn't filter scaffolding shows grammar first. Confirmed in:
`links`, `salient_facts`, `epistemic_status`, `walk_strongest`, `walk_branches`, `attention`
(partial). **This is the #1 recall-quality lever — a shared scaffolding deny-list / relation-rank
floor, applied centrally (today only `attention` filters, and incompletely — F4).**

### F2 — Instance/proper-noun pollution in the synset surface
`translations(king)`/`translate_to(king,'French')`/`attention(king)` surface `Martin Luther King`,
`B.B. King`, `Billie Jean King` as "translations/synonyms". Root cause: `synset_members` traverses
`IS_SYNONYM_OF` without excluding `IS_INSTANCE_OF` (named-entity) synsets. **Fix in flight by the
other agent** (one `NOT EXISTS (… IS_INSTANCE_OF …)` in `synset_members`, 20_converse.sql.in).
NOTE: the deep-dive of `synset_members` shows the filter may already be partially present in the
live copy — must re-test after their change lands.

### F3 — `translate_to` doesn't dedup or case-fold targets
`translate_to(king,'French')` → `roi`, `Roi`, `Magnat` (case-dup), plus `roue`/`pouvoir` (wrong
senses). Needs case-insensitive dedup + the F2 instance filter.

### F4 — `attention` deny-list is incomplete
It excludes HAS_POS/PRECEDES/etc. but **not** `FEAT_NUMBER` (and the other `FEAT_*`), so
`attention(king)` still shows `Number=Sing`. The deny-list should be a shared, complete set (F1).

### F5 — `collocates` is broken (always empty)
`collocates('run')` → 0 rows even though `PRECEDES` has 2.09M edges. PRECEDES connects positional
/sentence-stream tokens, **not** the lemma entity that `word_id('run')` returns — so the subject
never matches. Needs to resolve through the lemma's occurrences, or PRECEDES needs a lemma-level
projection.

### F6 — `structural_neighbors` is geometrically real but semantically random
`structural_neighbors('king')` → `グリーンランドで…`, `geomorfologinen`, `Grow`, `já não` at
geodesic 0.01–0.017. The 4D coords are populated but not yet semantically organized, so nearest-
in-space ≠ nearest-in-meaning. Geometry plumbing works; the embedding doesn't carry semantics yet.

### F7 — A relation type renders as a bare hash
`arena_counts` shows a type `ab29c48dadcf3f52…` with 995K relations and no canonical name →
`render_gaps()` territory. Needs a canonical name seeded (likely a `FEAT_*`/UD or codepoint-prop
type missing from `21_seed`).

### F8 — Sense-level edges have flat/zero mu
`senses(king)`/`define(king)` return correct glosses but every `eff_mu = 0.00`; `related(dog,IS_A)`
returns all senses at identical `1321.13`. Sense membership isn't differentiated by strength, so
"best sense" ordering is effectively arbitrary.

### F9 — `walk_strongest`/`walk_branches`/`cascade` follow scaffolding or loops, not meaning
`walk_strongest(dog)` → one step to `NOUN` (HAS_POS, highest mu) then stops. `cascade(dog,cat)` →
`dog —syn→ dog ←syn— orang ←syn— guy` (synonym-loop garbage) while `relate_path(dog,cat)` finds the
clean `dog→canine→carnivore←feline←cat`. The graph walkers need the F1 scaffolding floor + cycle
avoidance; `relate_path`/`isa_path` are the gold standard to copy.

### F10 — Data integrity: 5,858 identity-law violations
`substrate_health()` → `ok=false`, `identity_violations=5858` (entities with tier ∉ 0..5). Real
integrity issue to chase (which source/decomposer emits out-of-range tiers).

### What WORKS well (empirically)
`recall()` router (✅ dispatches to the clean functions — `define`, `synonyms`, `isa_path`):
`recall('is a dog an animal')` → "Yes — dog —is a→ domestic animal —is a→ animal";
`recall('define king')`/`recall('synonyms for happy')` clean. · `isa_path`, `relate_path`
(the best read functions) · `synonyms` (glad/felicitous/content, real ranking) · `hypernyms`
names (clean taxonomy; but gloss empty + 1.5s slow) · `word_id`/`resolve_phrase`/`prompt_state`/
`label`/`realize`/`render`.

---

## 1. Ingest / write / fold (🔒 internal — not user-callable)

`laplace_apply_batch(prefix)` 🔒 — the one set-based COPY-staging→live merge (**C SPI** in
`apply_batch.c`; thin `LANGUAGE C` binding in `27_apply_batch.sql.in`). Staging merge:
dedupe temp → LEFT JOIN subtract existing ids → sorted append (entities by id, physicalities by
`hilbert_index`); folds duplicate attestations (observation_count +=). Returns
`(entities_inserted, physicalities_inserted, attestations_inserted, attestations_folded,
entities_skipped, physicalities_skipped)` — skipped counts instrument descent slip-through (target ≈0).
No ON CONFLICT. Client-side: `laplace_grammar_compose_probe` + `content_descent_bitmap` for O(tier)
existence before compose.
· `entities_exist_bitmap(ids[])` / `intent_preflight(...)` 🔒 — bulk existence probes (C); used by
converse audit and C# containment readers, not the apply merge path.
· Glicko-2: `laplace_glicko2_accumulate` (aggregate), `laplace_glicko2_accumulate_games` (scalar
batch), `laplace_score`/`_inverse`, constants `glicko2_neutral_mu/initial_rd/initial_volatility/tau`.
· `consensus_id(s,t,o)` — BLAKE3 of subject‖type‖COALESCE(object,zero16). · mu-law: `eff_mu`,
`effective_mu` (↪ C dup of eff_mu, parity-test only), `eff_mu_display`, `refuted`.
· Fold pipeline (all 🔒, heavy side effects — create/drop staging, mutate consensus, atomic swap):
`create_period_staging`(×2 overloads), `period_staging_table`, `materialize_period_partition(_fresh)`
(LIVE — C# ConsensusAccumulatingWriter), `consensus_fold_one_partition`, `consensus_fold_partition`
(engine lane, C), `consensus_fold_walks` (C), `finish_consensus_fold`, `finish_consensus_fold_steps`
(PROCEDURE, mid-fold COMMITs), `consensus_fold_swap` (atomic table swap, empty-swap guarded),
`walk_fold_prepare`/`finalize`, `drop_period_staging`, `create_walk_staging`.
`materialize_period_consensus` + 1-arg `create_period_staging` = test-only (no prod caller).
**GUC policy:** no function-level `SET work_mem` / `session_replication_role` /
`maintenance_work_mem`; callers use `SET LOCAL` in txn (see `NpgsqlSubstrateWriter`,
`ConsensusAccumulatingWriter`). Resumable `finish_consensus_fold_steps` re-applies
`SET LOCAL session_replication_role` per partition after internal `COMMIT`.
Invariants: one φ per (subj,type,obj) per period (`period_phi_mixed` tripwire); walk XOR flat;
empty-swap protection.

## 2. Render / label / realize  (the three labelers — different fallback philosophies)

- `render_text(id, depth=32)` (C) — pure content reconstruction by walking constituents → UTF-8.
  **NULL** if not reconstructible. ❗ ERROR if id ≠ 16 bytes. `render_text_fast(=8)`,
  `render_text_batch(ids[],=8)` variants. ✅ tested.
- `render(id)` (SQL, 15) — name-FIRST: `canonical_names` → tier-0 codepoint glyph → `render_text`
  → **always** `encode(hex)||'…'` placeholder (never NULL for a valid id). ✅
- `label(id)` (SQL, 20) — display name: `HAS_NAME_ALIAS` (top eff_mu) → 7-regex wrapper-strip of
  `render()` (rejecting the hex placeholder) → `HAS_DEFINITION`. **NULL (loud)** when unnamed. ✅
- `realize(id, lang)` (C, content_resolve.c) — the authoritative one. 6-branch chain:
  synset→lemma (FIRST, so a synset shows its lemma not its `i46360` key) → `render_text` →
  IS_TRANSLATION_OF → HAS_NAME/HAS_NAME_ALIAS → canonical-strip → DEFINES. **NULL (loud)**, no
  placeholder. Language preference only re-orders, never excludes. ✅
- `type_label(type)` — strips `substrate/type/.../v1`, lowercases. · `realize_path(path[],types[]
  [,dirs[]],lang)` — renders `A —type→ B`. ✅ (used by `recall('is a …')`, clean).
- helpers: `canonical_id(name)`, `register_canonical(s)` (first-writer-wins; returns INPUT count
  not inserted count — ⚠️ misleading), `codepoint_for_id`, `constituents`, `vertex_atom/tier`.

## 3. Entity → attestations / relations  (the lookup family)

Raw evidence (provenance, unfolded, repeats per source):
- `attestations_out(id, limit=40)` → (type_id, object_id, source_id, context_id, outcome,
  observation_count), subject side. `attestations_in` = object side (returns subject_id). ✅
Folded consensus (one row per (subj,type,obj)):
- `consensus_out(id)` / `consensus_in(id)` → raw rating/rd/volatility/witnesses (machine form). ✅
- `consensus_out_readable(id)` → render()'d type/object + eff_mu_display (human form). ⚠️ uses
  `render()` so can show hex placeholders; includes unary (object NULL) rows.
- `completions(subject)` → strongest binary objects (object first col). LIVE (SubstrateClient,
  though no HTTP route reaches it — dead path).
- `attestation_response(subj,type,source_scope[],context,top_k)` → top-k objects; **scope is a
  visibility GATE via EXISTS on attestations, NOT a re-aggregation** — μ stays the global folded
  value. `attestation_unary_response` (object NULL). `_type` variants ↪ bare `SELECT *` pass-throughs.
- `top_relations(limit=50,type)` / `top_relations_readable(=10)` → global strongest binary edges.
Rendered/curated:
- `links(word)` ⚠️ — EVERY subject-side consensus link (relation, target, strength); **no limit,
  no scaffolding filter** → `links('dog')` shows HAS_POS/HAS_XPOS/HAS_LANGUAGE/FEAT_NUMBER/IS_LEMMA_OF
  first (F1). The raw inventory; needs a filtered sibling.
- `salient_facts(word,lang,limit)` (C) ⚠️ — "top salient facts"; still mixes cross-language synonyms
  (`asu`,`hund`,`perseguir`) and frames. · `epistemic_status(word)` ⚠️ — every relation + verdict
  (refuted/confirmed/contested/thin), but only excludes HAS_SENSE/IS_SENSE_OF/HAS_LANGUAGE → shows
  `is typed as WordNet_Synset` ×6 first (F1). · `related(word,type)` / `related_in` (C) — facts for
  ONE relation type; ⚠️ `related(dog,IS_A)` flat-mu slang (`villain`,`sausage`) (F8).
- ops counters: `evidence_count(type,source,object)`, `consensus_count(type)`, `content_count(source)`,
  `multi_source_entity_count`, `substrate_counts`, `arena_counts`, `source_counts`, `entity_type_counts`,
  `consensus_tier_distribution`, `render_gaps`, `api(like)` (self-catalog: `SELECT * FROM api()`). ✅

## 4. Lexical / semantic  (word → meaning)

- `senses(word [,context[]])` (C) — senses + synset + eff_mu; ⚠️ eff_mu all 0 (F8). 2-arg overload
  = context-disambiguated (+score col). · `define(word [,context[]] ,limit)` (C) — glosses; ✅ text
  correct, ⚠️ mu 0. · `examples(word)` (C). · `hypernyms(word,depth)` (C) ⚠️ — clean taxonomy names
  but **gloss col empty + ~1.5s slow**.
- synset triad off `synset_members(word)` (senses→synset→IS_SYNONYM_OF, bidirectional; the shared
  helper — fix F2 here once): `synonyms(word)` ✅ (same-language; happy→glad/felicitous/content),
  `translations(word)` ⚠️F2, `translate_to(word,lang)` ⚠️F2/F3, `language_coverage(word)`.
- `shared_objects(subjects[],type)` (C) — objects shared across subjects. · `gaps(word)` (C) —
  arenas with no facts. · `contrast(x,y)` (C) — distinguishing facts.

## 5. Paths / traversal / geometry

- ✅ `isa_path(x,y,depth)` (C) → (path[], types[], path_mu); render via `realize_path`. **GOLD.**
- ✅ `relate_path(x,y,depth)` (C) → pre-rendered chain + path_mu + plane. **GOLD** (taxonomy-aware,
  honors edge direction). · `relation_summary(x,y)` (C) — best relation + plane + mu + usage +
  geodesic + verdict. · `usage_overlap(x,y)` (C) — shared-context count.
- ⚠️ `cascade(x,y)` (C, 22) — A* over a default 13-type set; **picks synonym-loop garbage** (F9).
  `astar_path(start,goals[],…)` SQL wrapper over `astar_path_raw` (C, 07). 
- ⚠️ `walk_strongest(prompt,type,depth)` / `walk_branches(prompt,type,depth,breadth)` (C, 17) —
  greedy / branching walks; **follow scaffolding** (F9). · `foundry_crawl(seeds[],budget,hops,
  fanout,types)` (C) — BFS weighted crawl (vocab building).
- ⚠️ `structural_neighbors(word,k)` (plpgsql) — 4D KNN (geodesic+frechet); **semantically random**
  (F6). `nearest_neighbors_4d` ↪ alias. `structural_locale(word,near)` — density/isolation.
  `structural_cluster(seed,eps)`(C)/`_batch`. · `attention(word,k)` (SQL) ⚠️ — consensus-weighted
  neighborhood + geometry; instance pollution (F2) + FEAT_NUMBER leak (F4). · `correlate(words[])`
  (SQL) — (relation,target) shared across words. · `word_curve`/`entity_curve`(↪)/`anagrams_of`/
  `word_shape_distance`/`collocates`(❌ F5).

## 6. Generation

`recall(prompt,context)` ✅ / `recall_session(prompt,session)` (C routers — parse_ask → resolve_topic
→ dispatch to define/translate/synonyms/related/isa_path/walk; recall_session writes `session_topics`).
The product surface; works well. · `parse_ask(prompt)` (C) — NL → intent. · `generate(prompt,…)` /
`walk_text(prompt,…)` (prompt-seeded → deterministic) / `walk_continuations(ctx[],…)` (C) — stochastic
trajectory generation. · `continue_text` 🌱 — wraps walk_text; `p_stop`/`p_boost`/`p_require_pos`
currently ignored, `mu` always NULL (params staged, not implemented). · `variant_walk`/`respell_variant`/
`consensus_peer` (C) — variant generation. · `recall_trajectories(word)` — real attested sentences
containing the word (tier-3, ~7-token preferred).
Graph/plane builders for model export (all vocab-restricted edge tables): `relation_plane`,
`entity_relation_plane`, `consensus_layer_plane`, `consensus_type_plane` (resolves rank once/type —
the others do it per-edge RBAR), `consensus_adjacency`, `trajectory_pairs_plane` (reads the
`trajectory_pairs` materialized cache — call `trajectory_pairs_ensure` first), `metric_edges`
(distance not similarity — inverse polarity), `grapheme_order`/`word_order`, vocab builders
`foundry_vocab`/`foundry_vocab_crawl`/`grapheme_floor_vocab`/`corpus_word_vocab`. `model_recipes`,
`stream_stats`/`stream_reset`/`cooccurrence_scan`/`trajectory_cooccurrence(_by_stride)`.

## 7. Health / preflight

`substrate_health()` ⚠️ — currently `ok=false`, `identity_violations=5858` (F10). ·
`identity_law_violations()` (tier ∉ 0..5), `fake_tier_band_count()`, `compositional_type_ids/
is_compositional_type/compositional_tier_distribution`, `consensus_stats(_approx)`,
`period_staging_status`. ✅

---

## 8. Fix / wire / harden checklist  (NOT delete — understand → wire → harden)

Ordered by recall-quality leverage:
1. **Shared scaffolding floor (F1)** — central deny-list + relation-rank floor applied to `links`,
   `salient_facts`, `epistemic_status`, `walk_strongest`/`walk_branches`, `attention`. Biggest lever.
2. **Instance filter (F2)** in `synset_members` — in flight (other agent); re-test after.
3. **`translate_to` dedup + case-fold (F3)`**; complete `attention` deny-list (F4 — add FEAT_*).
4. **`collocates` (F5)** — resolve PRECEDES through lemma occurrences (currently always empty).
5. **`cascade`/walkers (F9)** — adopt `relate_path`'s scaffolding floor + cycle avoidance.
6. **Sense mu (F8)** — give sense membership real strength so "best sense" is meaningful.
7. **Seed the unrendered relation type (F7)**; chase the **5,858 tier violations (F10)**.
8. **`hypernyms` gloss + latency**; **structural embedding semantics (F6, large).**
9. Architecture (enterprise-grade): move surfaces out of the monolithic `20_converse.sql.in` into
   modular files in the **public namespace** (drop `@extschema@`/`SET search_path` ceremony — the
   other agent's pattern); collapse the ↪ pass-throughs (`*_type`, `entity_curve`,
   `nearest_neighbors_4d`) into thin documented aliases; reconcile `inference/` (the 🌱 AI-query
   copies) vs the live `20_converse` originals into ONE home, wired into the build.
