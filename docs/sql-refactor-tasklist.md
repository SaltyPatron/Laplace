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

**THE RULE IS CONDITIONAL, and applying it blindly is a regression.** In
`senses_with_context.own_sent` the predicate `subject_id IN (SELECT … FROM cand)` had already
reduced the set to 64 candidates, so `relation_highway_band(c.type_id)` was a cheap C
post-filter on a handful of rows. Replacing it with `type_id = ANY(47 ids)` gave the planner a
second indexable predicate and it began enumerating 64×47 combinations. Measured standalone,
identical 11,591 rows: **function-on-key 46 ms, band-array 331 ms** — 7× worse. End to end,
`lexical.senses(dog,[animal,pet])` went 1,891 → 3,150 ms, and back to 1,884 ms on revert.

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

- [ ] Rewire each site that renders more than one id.
- [ ] Leave genuinely scalar sites (one id, one row) alone and say so per site.

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

- [ ] Replace with `= ANY (generation.separator_ids())` — blocked on I.

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

- [ ] Derive the cluster set from the alphabet (UCD tables are compiled in via
      `laplace_ucd_tables_emit`), not from a corpus scan.

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
