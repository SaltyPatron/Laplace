# SQL refactor task list

Every item below is a **class** of defect found by measurement in this session, with the
site count from a corpus sweep. The recurring failure was fixing one instance and moving
on; each item here is closed only when the whole class is swept.

Measurements are live against the running substrate unless marked otherwise.

---

## A. Function call on a partition key — kills pruning

`consensus.relation_highway_band(c.type_id)`, `consensus.relation_type_in_family(c.type_id, …)`
in predicate position. `laplace.consensus` is `LIST (type_id)` → `HASH (subject_id)` = 216
leaves; `laplace.attestations` is `LIST (type_id)` = 73 leaves. A function on the key prunes
nothing and runs per edge.

Fix: `type_id = ANY (laplace.relation_band_types(ARRAY[…]))` (STABLE → InitPlan → runtime
pruning) or `= ANY (consensus.relation_family_ids('X'))` (IMMUTABLE → folds to a literal →
plan-time pruning). Negations become `<> ALL (…)`, which lets LIST pruning exclude leaves.

- [x] `consensus/salient_facts.sql.in` ×2 — negation → `<> ALL (folded literal)`. 29.5 → 26.5 ms
      warm, identical 3,944 rows. Kept: removes a per-row C call, folds at plan time.
- [x] `inspect/evidence_receipt.sql.in` ×2 — same shape.
- [x] `lexical/senses_with_context.sql.in` ×3 — **applied, measured, REVERTED.**

Equivalence of the substitution is exact: `relation_band_types(ARRAY[1,7])` = 47 types = the
`highway_band IN (1,7)` set; `ARRAY[2,4,7]` = 83 = 83.

**THE RULE IS CONDITIONAL, and applying it blindly is a regression.** Measured standalone on
`senses_with_context.own_sent`, identical 11,591 rows: **function-on-key 46 ms, band-array
331 ms** — 7× worse. End to end, `lexical.senses(dog,[animal,pet])` went 1,891 → 3,150 ms,
and back to 1,884 ms on revert.

**Cause, from `EXPLAIN (ANALYZE, BUFFERS)` — not inferred from wall clock.** The band-array
form plans **Seq Scans over the consensus partitions, 346,116 buffers**; the function-on-key
form keeps an index scan driven by `subject_id` with the band as a cheap C filter.
`laplace.relation_band_types` is **STABLE**, so it cannot fold to a literal and arrives as a
runtime array. `subject_id IN (SELECT … FROM cand)` had already reduced the set to 64
candidates, and rather than combine the two predicates on
`consensus_subject_type_btree`, the planner dropped the subject index path entirely.

Apply this fix **only where the type predicate is the selective one**. Where another predicate
has already reduced the working set, a function on the partition key is a filter, not a
pruning barrier, and it is the cheaper form.

## B. Scalar `realize.render_text` where `render_text_batch` already exists

§15: the canonical batch body exists and callers were never rewired. Measured on 40 ids,
identical md5: per-row **487 ms**, batch **106 ms** (4.6×). `render_text` fans out into
`realize.constituents_closure`, which pg_stat_statements shows at 24,929 calls / 85 s.

13 call sites across 12 files: `analysis/render_trace`, `converse/converse_facts`,
`realize/realize_has_name`, `chess/chess_game`, `realize/resolve_name`, `realize/realize`,
`model/model_factor`, `readback/render_text`, `realize/realize_translation`,
`realize/realize_defines`, `readback/render`, `realize/realize_synset_lemma`.

- [x] **CLOSED.** 6 of 13 hits were comments or the definition itself; 2 more render a
      single id and correctly stay scalar. The 5 real sites are fixed:
      - `realize._defines` and `taxonomy.synset_gloss` order by `eff_mu`, not by the text, so
        the winner is picked in id space and rendered **once**. They were also
        byte-identical bodies of each other (§15); `synset_gloss` now delegates.
      - `realize._has_name`, `realize._synset_lemma`, `realize._translation` filter on the
        rendered text, so they rank ids and take ONE `render_text_batch`.
      Parity 15 ids x 5 functions, 0 mismatches; the same five-call sweep took over
      120 s before and 10.9 s after.

## C. Per-row scalars with no batch form — 37 call sites

Measured per-call cost, and call counts from `laplace.pg_stat_statements`:

| function | lang | per call | sites | observed |
|---|---|---|---|---|
| `lexical.word_case_variants` | C | 39 ms | 8 | 2,256 calls = 88 s |
| `lexical.lexical_peers` | sql | 35 ms | 13 | 2,097 calls = 73 s |
| `converse.attested_language` | sql | 71 ms | 3 | — |
| `structural.containers_of` | C | 83 ms | 7 | truncates at 1,000 |
| `structural.geometry_neighbours` | sql | — | 6 | 14 s/call for *dog* (bubble_up comment) |

- [x] `converse.attested_language_batch(bytea[])` written and installed.
- [ ] `lexical.lexical_peers_batch(bytea[])` — blocks further `bubble_up_batch` gains.
- [ ] `lexical.word_case_variants` array form (C; needs a preload bounce).
- [ ] `structural.geometry_neighbours` array form.

**Measured caveat, do not skip:** routing a set through a batch *function argument* forces
the set to materialise as one Datum before the call. In `bubble_up_batch` that made the whole
call **3,155 → 4,950 ms**; joining the relation directly was **413 ms** vs **587 ms** for the
array argument on 1,362 ids. Batch functions are for callers that already hold an array.

## D. Set-returning functions lying to the planner — 249 of 255

Only 6 declare a real `ROWS`: the five model-lane functions and `lexical.senses` (64).
Everything else ships Postgres' 1000 default. Proven consequence: `st_dumppoints` at
rows=1000/procost=5000 multiplied against an unprunable 64-way Append and planned
`trajectory_unpacked_points` at cost **983,329**, over `parallel_setup_cost`, so every call
paid `Gather Merge` startup to return **3.47 rows**.

- [x] `generation.trajectory_unpacked_points` → `ROWS 6` (mean 5.93, max 15 over 500).
- [ ] Declare from a measured cardinality or a structural bound only — never a guess.
- [ ] Array-proportional functions (`bubble_up_batch` ≈ 50.6 rows/term) need a planner
      SUPPORT function; a constant `ROWS` is wrong for every N but one.

## E. Candidate truncation and defaulted caps

92 hardcoded `LIMIT n` in function bodies: **76 are `LIMIT 1`** (argmax pick, different
question) and **16 truncate a candidate set**: `160` ×3, `500` ×2, `400000` ×2, `3000`, `60`,
`48`, `40`, `32`, `24`, `8`, `6`, and `converse/chat.sql.in:248 LIMIT 3`. Plus **106 functions**
with a numeric-defaulted cap parameter (`p_limit`, `p_k`, `p_max_*`).

Bounding candidates is legitimate — spec 36 makes SCAN *"discover bounded candidates"* a
stage. The defect is a bound applied **per token before the joint election** (§7), or a cap
that silently drops rows without saying so.

- [x] `bubble_up_batch` `p_k` defaults to NULL (no truncation).
- [ ] Audit the 16: keep with a stated bound, or remove.
- [ ] Any surviving cap must log or return what it dropped.

## F. Rendering to classify

`converse_compose.sql.in` renders every distinct frontier token to text to ask
`realize.is_all_whitespace()` — a boolean — then discards the text. `generation.separator_ids()`
answers it in id space, and **its own comment already forbids the render fallback**:
*"Rendering to classify is the read-side law inverted — classification is an indexed read on
the id; the realize.render is the cost. NO RENDER FALLBACK."* Also English-centric: whitespace
is a Unicode general-category fact (Zs/Zl/Zp/Cc), not a string property.

Cost is **604 ms of 52,171 ms** — a correctness/consistency fix, not a hot spot. Say so.

- [x] **CLOSED.** Replaced with `= ANY (generation.separator_ids())`. Verified over 362 real
      trajectory tokens: render-side and id-side agree on every one, 0 disagreements.
      The comment at that site argued against the id form on the grounds that general
      category is attested on codepoints while `toks` holds words — true of a per-token
      `HAS_GENERAL_CATEGORY` probe, false of `separator_ids()`, which is the closed set
      including compositions.

**Recorded, not assumed:** there is **no UAX#29 word_break data ingested** (0 rows matching
`word_break` in `canonical_names`); only `line_break` and `general_category` exist.

## G. `generation.separator_ids()` — 9.45 s to return 85 ids

Its comment claims "bounded by the alphabet, not by the corpus". It is not: the `clusters`
arm scans **every tier-1 entity with a trajectory** to discover that CRLF is the one
separator-only grapheme cluster. Measured split: `atoms` arm **12 ms**, whole function
**9.45 s** warm.

Approaches measured and rejected, with reasons:
- first-constituent index pre-filter → **599,753 rows**; a leading separator is common.
- `structural.containers_of` over the 84 atoms → **25.7 s**, worse, and truncates at 1,000.

- [x] **CLOSED — 9,450 ms to 10.3 ms, 917x, identical 85 ids
      (fingerprint 20a10803bf930f506c88c9b4a24391b5).** Entity identity is a BLAKE3 hash of
      the content at every tier, so the cluster did not need discovering:
      `laplace.word_id(chr(32))` IS the space atom's id and
      `laplace.word_id(chr(13)||chr(10))` IS the CRLF cluster's id
      (`cafeb486a01baa2c1ee30a8b78e2ff4d`, the single non-atom id the scan returned).
      CR+LF is not a special case — it is a composition hashed exactly as `'as'` is, and
      composition lifts the property (§3, Merkle); the `bool_and` the old body computed
      WAS that definition.

## H. `structural.containers_of` — C that loops SPI per element

`containers_of.c:62` issues one `SPI_execute_plan` **per frontier element**, with `LIMIT $2`
in the query text and `p_limit DEFAULT 1000`. Measured: 83 ms for one id, and it **returns
exactly 1,000 rows** — silently truncated. The 84-atom fan-out costs 25.7 s.

- [ ] Take an array of roots and expand the frontier in one pass.
- [ ] Remove the cap or report what it dropped (CLAUDE.md: a cap gets the question *why does
      the implementation need it at all*).

## I. Dual definitions (§15)

- [x] `converse.attested_language` vs `bubble_up`'s inline `cand_lang` — same argmax, two
      bodies. Canonical body now exists as `attested_language_batch`.
- [ ] Collapse `taxonomy.bubble_up` into `bubble_up_batch(ARRAY[term])` so one body owns the
      election. Blocked on: the scalar's `LIMIT p_k` has no final tiebreak, so a truncating k
      is not reproducible (33 of 835 rows differed between two equally valid cuts); untruncated
      the two forms are byte-identical over 1,012 rows.
- [ ] `lexical.senses` / `lexical.senses(word, context)` / `senses_with_context` overlap.

## J. Subquery on a HASH partition key

`c.subject_id IN (SELECT synset_id FROM cand)` — `subject_id` is the HASH key, and a semi-join
cannot prune hash partitions (pruning needs a value). Sites in `senses_with_context`,
`salient_facts`, `evidence_receipt`.

- [ ] Measure whether binding the set (`= ANY (ARRAY(SELECT …))`) enables runtime pruning
      here, and apply only where measured — the same substitution was **45× slower** for the
      language-context filter (418 ms → 18,919 ms), because the working set had already been
      reduced upstream.

---

## The rule this session actually established

Not "joins are bad" and not "arrays are the workaround". Measured at three sites:

| join column | prunes? | fastest form | numbers |
|---|---|---|---|
| `attestations.subject_id` (leads its index, 73 leaves) | yes | plain `JOIN` | 413 ms vs 587 (array) vs 1,145 (semi-join) |
| `consensus.object_id` (indexed, prunes at **neither** level, 216 leaves) | no | `= ANY (ARRAY(SELECT …))` | 60 ms vs 49,861 (join); same join with `enable_hashjoin=off` 136 ms |
| any set read back from a **CTE column** | never | — | per-row variable, no pruning, disk sort |

The planner's cost constants are already tuned (`random_page_cost` 1.1, `effective_cache_size`
81 GB, `shared_buffers` 32 GB). Where it chose wrong, the query gave it a shape it could not
prune — that is ours, not its.


---

## K. Tier is a property of the container, not of the entity

Established by measurement while fixing F, and it invalidates a filter used on the
generation path.

The DAG is content-addressed and recursive, so the same content recurs at many levels
and dedupes trunk-down: ingest the verses, then the bible, and the verses resolve to the
ids already recorded, now one level deeper. `entities.tier` is one scalar per entity, so
it can only be the minimum over every container that content ever appeared in.

- `laplace.word_id('a')`, `word_id('I')`, `word_id('狼')` are all tier **0** and typed
  **Codepoint** — a single-character word IS the codepoint entity, same content, same
  hash, one entity. No tier test and no type test separates "a used as a word" from "a
  the codepoint".
- The trajectory's packed vertex tier does NOT disambiguate either: measured, the word
  `'a'` occurs **152 times** inside sentence trajectories carrying stored tier 0, and the
  stored tier equals the entities floor on all 3,642 points sampled.
- Only the **container's** tier says which level a constituent sits at.

- [x] `converse_compose` `WHERE u.ctier = 2` removed — its `gids` are already tier-3
      containers, so their constituents are word-position by construction, and the tier
      test was deleting every single-character and single-grapheme CJK word.
- [x] `trajectory_unpacked_points` now reads the tier from the packed vertex flags
      instead of a per-point `entities` lookup (3,642 points, 0 mismatches).
- [ ] Remaining `ctier = 2` sites named in CLAUDE.md: `converse_tiered.sql.in:150`,
      `senses_with_context.sql.in:95`, `explore_anchor_neighbors.sql.in:89`,
      `variant_synth.c:266`. Each needs the same question: what is the container?


---

## L. MEASUREMENT VALIDITY — read before trusting any wall-clock number in this file

`pg_stat_activity` on 2026-08-15 showed an **active ingest run** (`UPDATE
laplace.ingest_run_journal SET status = …`, `pg_advisory_lock`, 6 runs completed in the prior
6 hours) concurrent with the benchmarking in this session.

`generation.compose_batch('what is a wolf', 12)` measured **264,605 → 244,533 → 81,701 →
316,998 → 144,256 → 144,947 → 153,203 → 301,571 ms** across the session for near-identical
code. Those are not comparable to each other, and several causal claims were drawn from
single runs of them.

**Findings from plan structure do not depend on load and stand:** buffer counts, plan cost,
node types, loop counts, `Gather` presence, and output parity. **Findings from end-to-end
wall clock do not stand** until re-measured on a quiet database, repeated.

Before any further optimisation work: confirm no ingest run is active, then measure each
variant at least 3 times.

## M. Partitioning — the detector already exists and its output was never acted on

`ops.consensus_partition_pressure(min_rows bigint DEFAULT 100000)` names unpartitioned
relations by share of DEFAULT. Live:

| relation | rows | % of DEFAULT |
|---|---|---|
| **HAS_FEATURE** | **186,562,442** | **84.93** |
| TRANSCRIBES_AS | 5,192,208 | 2.36 |
| DERIVED_FROM | 3,836,962 | 1.75 |
| HAS_THINK_CLASS | 3,022,618 | 1.38 |
| ETYMOLOGICALLY_DERIVED_FROM | 2,606,533 | 1.19 |
| HAS_ETYMOLOGY | 2,595,036 | 1.18 |
| HAS_CLOCK | 2,531,014 | 1.15 |
| HAS_EXAMPLE | 1,803,197 | 0.82 |

The 59.2% DEFAULT skew is **one relation**, not 399 sharing a bucket. Meanwhile
`sql/generated/seed_relation_partitions.sql.in:9` hardcodes 26 `hot` types of which **8 hold
0 rows**, so ~80 leaves exist for ~175 rows while the largest relation in the substrate has
none.

- [ ] **NOT DONE, deliberately.** Adding `HAS_FEATURE` to the `hot` array would make the next
      `just install` create the partition and **drain 186M rows out of DEFAULT**, taking
      AccessExclusive locks while ingestion is running. That migration needs a quiet window
      and an explicit decision — it is not a source edit to slip in.
- [ ] Drop the 8 zero-row named partitions (16 leaves × 2 tables) — cheap, and narrows every
      unpruned Append.

## N. A text decomposer is writing sequence as edges

Sequence is geometry in every lane; text ingestion emits trajectories and PRECEDES is read
back from them. Measured by `source_id` over the 120 live PRECEDES attestations (all written
2026-08-13/14): **`FrameNetDecomposer` 89**, plus two sources not in `ops.source_status()`
(29 and 2).

- [ ] Fix at the decomposer — it should emit a trajectory, not PRECEDES edges.


## O. The perfcache — corrections to two claims made earlier in this file

`engine/core/include/laplace/core/perfcache_format.h` defines a flat record for ALL
**1,114,112** codepoints: `codepoint, uca_order, coord[4], hilbert, **hash128**, flags`, with
flags packing **GB (grapheme break), WB (word break), SB (sentence break), INCB, CCC**.

- **"There is no UAX#29 word_break data ingested (0 rows)" was WRONG.** UAX#29 segmentation
  properties are compiled into the perfcache and available O(1) by codepoint index. The
  earlier claim came from grepping `canonical_names` and declaring absence — the wrong search.
- **"`is_all_whitespace` is English/ASCII hardcoding" was WRONG.** It is
  `pg_laplace_is_all_whitespace` in `perfcache.c`, an O(1) read of the compiled UCD record.
  Its only defect is that it takes **text**, so callers rendered to reach it.
- The record carries **`hash128`**, so codepoint → entity id is an **array index**, not a hash
  computation and not a query. `laplace.word_id` is itself perfcache-backed
  (`pg_laplace_word_id`).

Exported to SQL today: `word_id`, `codepoint_for_id`, `is_all_whitespace`, `atom_window`,
`chess_position_ready`. **Missing and worth adding:** an id-taking separator/category
predicate, so classification never needs a surface.

## P. Case folding is not universal — landed

`dog` / `Dog` / `DOG` are different content, therefore different hashes, therefore different
entities (`01cdcce1…` vs `af1513f0…`, equal = false). Merging them asserts an equivalence the
identity scheme denies, and case folding exists only in bicameral scripts. Where the relation
is real the graph attests it — `dog` and `Dog` carry an `IS_SYNONYM_OF` edge.

`lexical_peers` widened **unconditionally**: 11,964 calls / 96 ms / 1,146 s, driving
`word_case_variants` (12,303 / 96 ms / 1,179 s) and its inner per-id render (12,223 / 88 ms /
1,081 s). Measured on 20 common words, all 20 resolve exactly and the old body returned 3
peers for every one — fired 100%, needed 0%.

- [x] Widening is now a fallback behind an indexed exact probe (`c4a95c1e`).
      `lexical.senses(dog,[animal,pet])` 1,884 → **388 ms**; `bubble_up_batch` 20 terms
      3,155 → **1,092 ms**; `top_synset('wolf')` unchanged; `senses('wolf')` 39 rows at
      **117.8 ms** — all under the same concurrent ingest.
- [ ] `word_case_variants` (C) still does hash → text → hash with five SPI round trips on the
      fallback path. The codepoints are already packed in the trajectory vertices
      (`ATOM_SHIFT 31`), and the case maps are attested
      (`HAS_LOWERCASE_MAPPING`/`UPPERCASE`/`TITLECASE`), so it needs no render at all. A SQL
      rewrite was tried and rejected: it is RBAR (one row per character) and was not
      equivalent (3/18 mismatches on single-character words). The correct form is C over the
      perfcache — needs a rebuild and a PG bounce.
