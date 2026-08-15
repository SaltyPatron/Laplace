# The read path: measured shape, calling convention, order of operations

Everything here was measured against the live substrate on 2026-08-14/15 and carries its
number. Where a thing is inferred rather than measured it says so. `docs/sql-cascade.md`
holds the raw cascade and caller inventory this plan is built on.

---

## 1. The shape of the machine

**Identity is computed, evidence is stored.** `laplace.relation_type_id('IS_A')` →
`realize.canonical_id` → `laplace_hash128_blake3(convert_to(name,'UTF8'))`. It never touches a
table, which is why it is honestly `IMMUTABLE`. Any process, in any language, on any machine,
derives the same id for the same name with no registry and no coordination — that is what makes
cross-source pooling a hash collision rather than an entity-resolution pass. The database holds
the *testimony about* those ids, plus `laplace.canonical_names` (10,253 rows, 1.2 MB) for the
reverse direction that realization needs.

**A sequence is an entity.** `hot dog` is `ebffb84ed9cbad9821843b7c1b7ff0c2`, tier 3, composing
exactly `[hot 36fcbb29, SPACE 00263ca9, dog 01cdcce1]`, carrying **904 consensus edges of its
own**. `hotdog` is different bytes and therefore a different entity — `9cd3d102…`, tier 2, 175
edges. Asking whether a sequence occurs is a hash lookup on the merkle DAG. Counting adjacency
is the weakest available form of that question.

**Order lives in the trajectory, not on an edge.** Text emits no `PRECEDES` attestations;
`PRECEDES` is populated only by model ingestion (115 consensus rows live, all model-lane
residue). Sequence is fetched with `laplace_trajectory_constituent_ids`, and the *id array* is
what `physicalities_constituents_gin` indexes — 346 trajectories containing both `hot` and `dog`
come back by `@>` in **1.6 s**, against **57 s** for the unpack path. Prefix lookup by first
constituent is **74.7 ms** for 1,934 compositions starting with `hot`.

**Tier is a floor, not a kind.** `word_id('a')`, `word_id('I')` and `word_id('狼')` are all
**tier 0**; `word_id('hot')` is tier 2. Every `ctier = 2` filter silently deletes single-grapheme
words and every single-grapheme CJK word. Live sites: `converse_compose.sql.in:183`,
`converse_tiered.sql.in:150`, `senses_with_context.sql.in:95`,
`explore_anchor_neighbors.sql.in:89`, `variant_synth.c:266`.

**Separators are entities, not delimiters.** Deleting them is a Latin-script assumption that
destroys content: off the prefix index, `hot` continues with `HYPHEN-MINUS → tempered` and
`COMMA → SPACE` as well as `SPACE → water`. Carry what sits between (`sep_id`), which is what
`generation.trajectory_continuations` already does. For non-delimiting scripts the separator set
is empty and pairs are direct — no branch, no special case.

**The forward pass already has a staged design.** `trajectory_generate.c` implements
`walk_continuations` as **S6 PROPOSE** (`generation.trajectory_continuations`, read off
`physicalities.trajectory` via GIN containment + ordinal window, path-bounded) → **S7 STEER**
(`generation.steer_candidates`, re-ranked by consensus mass reaching the live frontier, scored by
`walk_score.h` — the same kernel `walk_branches` uses, so proposing and steering cannot disagree)
→ **S8 SAMPLE** (Gumbel over top-k), with `walk_completes_floor` as the floor when the sequence
well is dry. The combination is signed and multiplicative, and **unattested is ×1, not 0** —
only *refuted* (steer ≤ 0) is excluded. `converse.walk` / `steered_walk.c` is the corpus-era
trigram lane that this replaced.

---

## 2. The calling convention

### 2.1 Supply the partition keys

`laplace.consensus` is `LIST (type_id)` over 208 named partitions plus a DEFAULT, and **each is
itself `HASH (subject_id)` over 8** — **216 leaf partitions**. Measured leaves touched:

| predicate | leaves |
|---|---|
| `subject_id` + `type_id` | **1** |
| `object_id` + `type_id` | 8 |
| `subject_id` only | 27 |
| `object_id` only | **216** |

Forward-edge reads prune at both levels. **Reverse-edge reads prune at neither**, because the
hash key is `subject_id`. A 216× spread separates the best case from the worst.

### 2.2 Use the canonical accessor

```sql
consensus.edges_raw(p_subject bytea, p_direction text, p_types bytea[],
                    p_limit integer, p_refuted boolean, p_rank text)
  RETURNS TABLE(direction text, type_id bytea, neighbour_id bytea,
                rating bigint, rd bigint, volatility bigint, witness_count bigint)
```

Every axis is a parameter, including the partition key and direction. Measured:

| call | rows | time |
|---|---|---|
| `('wolf','out',NULL,100)` | 100 | **18.2 ms** |
| `('wolf','out',[IS_A],100)` | 17 | **10.7 ms** |
| `('wolf','in',NULL,100)` | 100 | **68.8 ms** |

Even the reverse direction — 216 partitions when hand-rolled — is 68.8 ms through it.

**88 SQL functions read `laplace.consensus` / `v_consensus_unrefuted` directly. Two use
`edges_raw`** (`consensus.edges`, `consensus.related_objects`). That is the single largest
standardization gap in the tree. (Caveat: this counts SQL callers; C callers were not counted.)

**It does not solve fan-out.** Inside a LATERAL the accessor costs ~**806 ms per call** — 20
one-hop neighbours expanding to 720 two-hop took **16.1 s**, and 200 exceeded 120 s. Single reads
are cheap; iterated expansion is not.

### 2.3 When nothing can prune, probe — don't join

A read that constrains only `object_id` cannot prune, and the planner then prefers a full
index-only scan of every partition over N probes. Measured on `converse.infer`'s in-edge arm,
identical 71,405 rows:

| form | time |
|---|---|
| `JOIN … ON c.object_id = b.synset_id` | 49,861 ms |
| `LATERAL` correlated | 104,764 ms |
| `= ANY (ARRAY(SELECT …))` | **60 ms** |

An accurate row estimate does **not** substitute: the same join against an `ANALYZE`d 229-row
temp table still cost 49,861 ms. Forcing nested loops with `enable_hashjoin=off` gave 136 ms,
which is how the plan shape — not the cardinality — was identified as the defect.

### 2.4 What folds and what does not

`relation_type_id('X')` with a **constant** argument folds to a literal `bytea` at plan time —
sixteen of them in a `WHERE` clause cost sixteen hashes once, during planning. A function whose
argument is a **column** cannot fold: `consensus.relation_type_in_family(c.type_id, …)` runs per
row (compiled C, `procost 1`, 17.9 ms across all 426 relation types).

### 2.5 Declare row estimates from bounds, never guesses

**239 of the 244 set-returning functions ship Postgres' default `prorows = 1000`**; only the five
model-lane functions declare a real one. A wrong estimate on the inner side of a join is what
makes scanning look cheaper than probing. Declare `ROWS` only from a measured cardinality or a
structural bound — `lexical.senses` is `bubble_up(…, 64)` and is now `ROWS 64`. The tier sweep
records `rows_out` per function, which is where the remaining declarations should come from.

### 2.6 Planning is not free

`consensus.salient_facts(word_id('water'))` plans a **533-node** tree in **545 ms** to return 24
rows, because it references `consensus` several times and each reference expands across the
partition set. Execution was 5,167 ms. Count plan nodes before blaming a scan.

---

## 3. Ground truth

**Cascade.** 381 functions, 9 schemas, 573 call edges, **no cycles**. Tiers 0–8: 130, 82, 51, 50,
15, 25, 14, 5, **1**. Tier 8 is `converse.chat` alone.

**Callers.** public/app 180 · SQL-internal 73 · C-internal 56 · **no caller anywhere 45** ·
scripts 13 · tests 6.

**Compute placement.** 57 C kernels against 324 SQL/plpgsql orchestrators, of which 50 run window
functions, 65 use LATERAL and 50 carry bodies over 2 KB. `converse.chat` is **27,077 bytes** of
plpgsql at tier 8.

**Tier-0 outliers.** `ops.consensus_tier_distribution` **549,925 ms** · `consensus.stats`
**134,740 ms** · `generation.separator_ids` 10–26 s · `ops.app_log` 201 ms · the other 18
zero-argument leaves ≤ 41 ms.

**Two naming paths.** `realize.realize_canonical` reads `laplace.canonical_names`;
`realize.resolve_name` has **zero** references to it. Of 200 sampled bootstrap names,
`resolve_name` answers 158 and is **silent on 42 (21%)** — the cause of the nulls seen all
session (`resolve_name(wolf)` empty while `label_batch` returns "wolf").

**Language is recorded and then folded away.** `laplace.attestations.context_id` carries it —
across 1,411 sampled attestations with a context, **879 (62%) are `Language` entities**.
INVENTION §5 excludes source and context from consensus identity, which is exactly why every
consensus-only read is language-blind. Provenance beats assertions: `attested_language` (argmax
over `SUM(sum_score_fp1e9)` per context) gives lobo→**Spanish**, lupo→**Italian**, where
`word_language` (argmax over `HAS_LANGUAGE` assertions) says Portuguese and Esperanto.

**The §7 joint term exists and is discarded.** `prompt_coherence` computes `coherence` — rated
mass between one token's candidate senses and the *other* tokens' — and it is **not zero**: for
"What is a wolf?" it ranges 949e9 → 21e12 across candidates, a 22× spread. **No caller orders on
it.** Every caller orders on `specificity`, which `pc_load_icf` made inverse container frequency
— IDF — so the seat goes to whatever is rarest: *hä*, *gyatt*, *er*. For a prompt with one
content word and three glue words the joint term is structurally silent, which is why the IDF
prior was introduced in the first place.

---

## 4. Order of operations

Verification runs **bottom-up** (a function cannot be trusted until everything it calls is), but
repair leverage runs **by shared cost** (a tier-0 primitive is paid by everything above it). Run
both, in this order.

### Phase 1 — close tier 0 (130 leaves)

1. Run `scripts/sql-tier-sweep.sql` per tier; it fixtures one value per argument type, forces
   scalar evaluation, and caps each call at 5 s. Record ms, `rows_out`, errors.
2. Reclassify the two census leaves. `consensus.stats` (134 s) and `ops.consensus_tier_distribution`
   (549 s) count the whole substrate by contract; they will never meet a 200 ms budget. The
   snapshot infrastructure already exists — `laplace.ingest_run_journal` carries per-run
   `entities`, `physicalities`, `attestations` deltas with `started_at`/`ended_at`/`status`, and
   `ops.substrate_counts` (0.4 ms) already reads `pg_class.reltuples`. Make the exact ones
   event-boundary snapshots or mark them admin-only; do not "optimize" a census.
3. `separator_ids` (10–26 s) is a leaf taxed by `relation_plane`, `trajectory_continuations` and
   `word_adjacency` alike. Its atoms arm is 28 ms; its clusters arm is 4,372 ms scanning 57,736
   tier-1 clusters to find 2. **Three query rewrites were measured and rejected** — GIN `<@`
   107.8 s, GIN `&&` overlap 103.0 s shortlisting 32.4 M, first-constituent index 4.04 s against
   a 4.37 s baseline. The set is alphabet-bounded (85 ids) and its own comment calls it "the same
   class of lookup as `relation_type_id()`", which is served from a compiled perfcache. Compile
   it; do not scan for it.
4. Emit `ROWS` declarations from the sweep's `rows_out` where a structural bound exists.

### Phase 2 — the accessor migration (86 functions)

Move hand-rolled `laplace.consensus` reads onto `consensus.edges_raw`. Order by
(on the forward/export path) × (measured ms). This fixes cost and correctness together: the
accessor takes direction, types, refuted and rank as explicit axes, so a migrated caller cannot
silently omit the partition key or read the wrong direction. Reverse-edge readers are the
priority — they are the ones paying 216 leaves.

Where a read genuinely spans all types, use `= ANY(ARRAY(…))` rather than a join (§2.3).

### Phase 3 — one naming path

`realize.resolve_name` must resolve what `canonical_names` holds, or the registry must be reached
through the same body. 21% of the bootstrap vocabulary is unresolvable today. One body for one
truth (§15).

### Phase 4 — the forward pass

1. Make `coherence` reachable: it is computed, non-zero, discriminating and unused. Any ordering
   change here is content law and belongs to the operator, but the signal being *discarded* is a
   defect regardless of which key wins.
2. Wire the S6→S7→S8 pipeline as the generation path and isolate the corpus-era trigram lane so
   the two cannot be confused. `generation.walk_continuations(p_ctx, p_steps, p_max_stride,
   p_spread, p_breadth, p_seed)` is installed and was never exercised in this session.
3. Language scoping belongs on the attestation join (context survives there), not on consensus
   (which folds it away by §5).

### Phase 5 — account for the 45 unwired

They come in coherent families — circuit (`circuit_matrix`, `circuit_row`, `circuit_coupling`,
`adjudicated_row`, `adjudicated_coupling`), model lane (`model_attention_row`, `model_pair_cos`,
`distill`, `decay`, `prune`, `witness`), 4-D geometry (`nearest_neighbors_4d`,
`trajectory_prefix_distance`, `word_shape_distance`, `geometry_predecessors`), the three
`consensus_export_*`, the `ops` diagnostics, the chess opening surfaces. Wire, isolate, or record
why each exists — this is incomplete work, not dead weight.

### Phase 6 — compute placement

Rank by compute sitting in the orchestration layer: `converse.facts` (9,381 B),
`generation.grapheme_order` (2,069 B), `converse.chat` (27,077 B), `generation.walk_batch`
(16,028 B), `generation.compose_batch` (15,098 B), `ops.evidence_receipt` (8,269 B),
`generation.foundry_vocab_crawl` (6,184 B), `converse.infer` (5,761 B),
`consensus.salient_facts` (4,777 B), `consensus.relate_path` (4,416 B). §15 says native holds the
math and the extension is a thin versioned surface; these are the ten furthest from that.

---

## 5. Measuring without lying to yourself

Traps hit and paid for during this research:

- **`count(*)` does not evaluate a scalar function.** `SELECT count(*) FROM (SELECT fn()) q`
  needs no column, so the planner elides the call. It reported `separator_ids` at 0.2 ms against
  a real 10–26 s. Cast to text and test `IS NOT NULL`.
- **Comments create phantom call edges.** Matching function names against raw
  `pg_get_functiondef` counts prose mentions as calls: 624 edges and six "mutually recursive"
  pairs, all six artifacts. Stripped: 573 edges, zero cycles.
- **Read the signature before using a column.** Three wrong guesses in one session —
  `physicalities.type_id` (it is `type`), `consensus_adjacency.weight` (it is `w`),
  `edges_raw.id` (it is `neighbour_id`).
- **A killed `psql` does not kill its backend.** An orphaned harness holding catalog locks blocks
  `ALTER EXTENSION` with `canceling statement due to lock timeout`. Check `pg_stat_activity`
  before diagnosing anything else; two syncs failed to this.
- **Postgres rejects regex repetition counts above 255.**
- **Distinguish leaves touched from leaves existing.** "27 partitions" was what a subject-only
  read touches; 216 exist.
- **A slow number can be your own query.** "2-hop costs 17.6 s" was a badly written query; the
  same question answered properly is 3.16 s. Rewrite before declaring a wall.

---

## 6. Phase 1 progress, 2026-08-15

### Landed

| fix | before | after |
|---|---|---|
| `ops.source_status` evidence for the tail | nine sources reported **0** | exact: 1,642,792 attestations, every value matching an independent count |
| `consensus.stats_approx` | a row of **all NULLs** in 1.8 ms | 370,411,328 / 351,518,016 / ratio 1.054 in 1.455 ms |
| `consensus.cell` partition keys | 97.1 ms, all 216 leaves | **21.0 ms**, one leaf |
| `realize.resolve_name` canonical fallback | 158 of 200 bootstrap names | 162 of 200 |
| `lexical.senses` row estimate | `prorows` 1000 | `ROWS 64` (structural bound) |

**`source_status` was the significant one.** `ops.source_counts_approx()` reads `pg_stats`
most-common-values on the **parent** `laplace.attestations`; that list holds exactly **ten**
entries, so only the ten largest sources could ever report non-zero and every other source
COALESCEd to 0. CLAUDE.md recorded the resulting zeros as "successful-looking runs that
deposited nothing: a gate failing open" — false on both counts. ChessPgn alone holds 1,472,737.
Raising the statistics target cannot fix it: the target is already 100 and with one source at
84% of 371M rows, a source at 0.004% is about one row in the sample.

Two candidate repairs were measured and **rejected**: aggregating the *child* partitions'
statistics (180 partitions, 584 MCV entries, which do see VerbNet in 7 and MapNet in 1) gives a
lower bound only — 13,757 against a true 40,170 for WordFrameNet — because a source contributes
nothing from partitions where it is present but not common; and the journal's `attestations`
column is a **deposit** count, not a row count (SemLink 76,182 there against 16,404 actual rows,
a 4.6:1 merge ratio that is the idempotence law working, not loss).

### Tier 0, measured so far

The SQL at tier 0 is close to clean. The expensive leaves are **C**, which cannot be fixed
without the preload bounce:

| leaf | ms | language |
|---|---|---|
| `consensus.stats` | 92,192 | sql — census by contract, 1.5 ms sibling exists |
| `converse.contrast` | **20,160** | c — verified 21.6 s on distinct args, 11.7 s on identical |
| `converse.cascade` | 1,693 | c |
| `converse.define_fast` | 693 | c — named "fast" |
| `consensus.cell` | 269 → **21** | sql — fixed |
| `converse.astar_path_raw` | 208 | c |

### What the harness had to learn

- **Fixture by parameter name, not type.** A `bytea` named `p_type_id` wants a relation type id;
  handing it `word_id('wolf')` makes the function correctly return NULL, which a null-probe then
  reports as a defect. Six false positives (`relation_canonical`, `relation_highway_band`,
  `relation_highway_bit`, `relation_rank`, `relation_rank_resolved`, `chess.outcome`) became one.
- **Probe null-ness, not just timing.** `consensus.stats_approx` returned a row of all NULLs in
  1.8 ms and passed a timing-only sweep as "ok".
- **`lock_timeout`, not just `statement_timeout`.** The latter does not bound a lock wait, so a
  sweep running while anything requests AccessExclusive convoys behind it — measured, a stall of
  9 minutes at 42 of 130, with my own `ALTER EXTENSION` as the other half of the convoy.
- **COMMIT per function.** A single `DO` block is one transaction: no partial results are
  readable and catalog locks are held for the whole run. The harness is a procedure now.

### Still open at tier 0

`consensus.walk_branches` needs a 32-byte `p_intent_mask` and has no fixture. Domain ids (chess
games, positions) have no fixture, so chess flags stay suspect. 108 of the 130 leaves take
arguments, and the sweep only covers those whose types are all fixtured.

### Tier 1, measured (82 of 82)

Very different from tier 0: **25 of 82 exceed 2 seconds**, 6 more exceed 200 ms, 4 return all
NULLs. The SQL ones capped at the 2 s ceiling:

`realize.render_gaps` · `generation.prune` · `generation.model_jitter_catalog` ·
`generation.distill` · `generation.witness` · `generation.astar_path` ·
`generation.foundry_vocab_crawl` · `generation.recall_trajectories` ·
`generation.word_adjacency` · `ops.source_bootstrap_present` · `ops.surface_sample` ·
`ops.source_roster` · `chess.opening_record` · `converse.relation_bands`

Several of those are in the unwired-45 (`prune`, `distill`, `witness`, `astar_path`,
`model_jitter_catalog`, `foundry_vocab_crawl`) — unwired *and* slow, which is consistent with
never having been exercised.

**A wall worth naming: reads by `source_id` cannot prune.** `ops.source_roster` takes 2,421 ms
to return 8 rows, and the exact per-source counts in `source_status` cost ~1.8 s each, for the
same reason: `source_id` is not a partition key at either level (`LIST (type_id)` →
`HASH (subject_id)`), so any source-scoped read fans across all 216 leaves. That is schema
shape, not a query defect, and no rewrite fixes it — it wants either a source-keyed index
structure or maintained per-source counters.

### The measurement instrument, finally correct

`scripts/sql-tier-sweep.sh` (shell, one psql statement per function) replaced the SQL harness
because **`statement_timeout` bounds the top-level statement**: inside a plpgsql procedure the
inner `EXECUTE`s are not separately capped, so a per-iteration `SET LOCAL` capped nothing
(`converse.hypernyms` ran 4.5 minutes under a 2 s cap) and a session-level `SET` killed the
whole `CALL` instead (the sweep exited after 31 of 130). The shell form measured 65 functions in
90 seconds where the procedure stalled twice.
