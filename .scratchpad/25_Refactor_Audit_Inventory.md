# 25 — Repo Refactor Audit Inventory (breadth-first pass, 2026-07-14)

Scope actually swept: extension SQL surface (330 functions across ~7.9k lines of
`.sql.in`), C# spine + decomposers (~81k lines, sampled at the hot paths), layer
boundaries (math placement, duplicate implementations). NOT deep-audited this pass:
Endpoints.OpenAICompat internals, Chess services line-by-line, `web/` (26k lines
TS/TSX), quality of engine/'s own C/C++ (only its *boundaries* were checked).
Inventory only — no code changed.

Severity: perf impact × law violation × blast radius. Each finding carries a fix
sketch. "EXPLAIN-gate" = confirm on live data before investing (per the engineering
rule: EXPLAIN before trusting an index / profile before optimizing).

## STATUS (updated 2026-07-14, branch claude/audit-refactor)

- [DONE] M2 — ConvertPartialToWalk stackalloc (commit b19f08e)
- [DONE] M5 — sync-over-async classified; GrammarIngestAdapter TryWrite (b19f08e);
  real remainder escalated as M8 (below)
- [DONE] M1 — salient_facts + evidence_receipt family fencing (8d2a59e)
- [DONE] H1 — converse_walk WALK native (steered_walk.c), GATHER set-based (0f32643);
  verified on hart-server: GATHER 789ms→231ms, outputs bit-identical
- [DONE] H3 — was PARTLY STALE: native word_case_variants already replaced the hot
  path; swept the 3 dead SQL fns (word_case_class_surface/map_surface/
  grapheme_case_target) (59064fe)
- [DONE] H4 (new, from profiling) — salient_facts rank-before-label + word_language
  hoist (commit 34707af); the #1 serving hot spot. Isolated on hart-server real
  data: 870ms→260ms (3.3x). Was NOT the M1 family-fencing cost.
- [RE-RANKED] H2 — see note under HIGH: by the frequency×cost×impact metric this is
  LOWER priority than serving-path work (it's a one-shot export, not per-query).
- [OPEN] evidence_receipt 310ms (profiling next), M3, M4, M6, M7, M8, L1, L2, L3

## LABELING-DISCIPLINE CENSUS (2026-07-14, operator's three questions)

Three systemic patterns, quantified across the SQL surface. The salient_facts
and structural fixes are single instances; this is the full population.

### #1 — Rendering/labeling BEFORE the final output boundary
122 render/label call sites total across the surface:
  render_text 41 · realize 32 · render 24 · type_label 12 ·
  _realize_synset_lemma 4 · walk_text 3 · label_or_hex 3 ·
  realize_translation 2 · synset_gloss 1
The violation is labeling a pool larger than the output, before ORDER/LIMIT.
Confirmed instances and status:
  - salient_facts — labeled 413 rows for 24 out. FIXED (34707af), 870→260ms.
  - evidence_receipt — already rank-first (its own Issue 52); labels an 80-row
    pool. The remaining 310ms is the `folded` string_agg(render(source_id))
    over every (type,object,source) triple. OPEN — candidate: fold source
    labels once per DISTINCT source_id, not per triple.
  - converse.sql / chat.sql — 8 and 12 label sites; render inside the generator
    loop. Partly addressed by H1 (converse_walk). converse()/chat() bodies OPEN.
Hot files by label-site count: chat(12), converse(8), evidence_receipt(5),
converse_walk(5 pre-H1), resolve_name(4), realize_path_with_dirs(4).

### #2 — Working on LABELS instead of IDs (filter/sort/group on rendered text)
Contained — the surface mostly ranks on ids/eff_mu, not text. Live instances:
  - WHERE on rendered text: 1 — translate_to:35 (language reference matched by
    rendered surface; already resolved once at the input boundary, acceptable).
  - ORDER BY on realize(): 5 — the converse-family ORIENT step (converse:42,
    chat:52/128, converse_walk:47) runs realize(candidate synset) per prompt
    token to pick the richest concept, plus evidence_receipt:59 orders the
    source_labels string_agg by rendered text. The ORIENT realize()-per-candidate
    is the one worth fixing (bounded by prompt-token count, but on the hot path).
  - GROUP BY on rendered text: 0 live (translate_to already fixed this — homograph
    conflation was the documented bug; it now groups on (member,lang) ids).

### #3 — Returning IDs/hashes instead of resolved words + missing fanout hops
This is the largest surface and the 3D-UI question.
  - 85 functions RETURN a bytea id column; 31 in serving categories
    (recall/converse/consensus/taxonomy/link/inspect/realize/lexical).
    Returning the id ALONGSIDE a label is correct (the UI needs the id to hop);
    returning it INSTEAD of a label is the bug.
  - 39 return columns literally named *_id bytea.
  - 8 hex-hash fallbacks (a node stopping on an identifier). Audit:
      * structural_neighbors_of:53 — render_fast→hex, NO synset hop. The 3D-UI
        bug: synset/ILI hubs plotted as hashes. FIXED (8234427) → realize().
      * structural_neighbors:42 — WHERE render_fast IS NOT NULL silently DROPPED
        hubs from the 3D view. FIXED (8234427) → realize(), drop only unlabelable.
      * consensus_out_labeled:30, evidence_receipt:96, label:37, render:22 —
        CORRECT: synset-lemma/realize hop FIRST, hex only as genuine last resort.
      * constituent_edges_rebuild:65 — a RAISE NOTICE log line, not output. OK.

### Design answer — should a node stop on an ILI/synset id?
No. `realize()` IS the canonical full-mesh resolver and the answer to all three:
  resolve_name (HAS_NAME primary + HAS_NAME_ALIAS + synset-lemma sibling hop,
  family-aware) → render_text → translation → canonical → gloss, and it ABSTAINS
  (NULL) rather than emitting a hash.
The correct pattern everywhere, now proven cheap by the rank-before-label work:
  1. rank + LIMIT on IDs / eff_mu (never on rendered text);
  2. resolve labels ONLY over the survivor pool, via realize() (the full mesh,
     not bare render_text / render_text_fast);
  3. hex only as a genuine last resort, and NEVER as a silent WHERE-drop.
A synset/ILI node with sibling name/lemma/translation edges must surface the
real word — stopping on the identifier when a one-hop resolves it is the bug.
Remaining #3 sweep (OPEN): the other 30 serving functions that return bytea ids
— audit each for "id WITH label" vs "id INSTEAD of label"; the ones feeding the
UI (explore/converse/link families) are priority.

## SERVING-PATH LATENCY (hart-server real data, warm, 2026-07-14)

Measured per the operator's frequency×cost×impact steer. This is the real
priority order for serving-path work — one-shot paths (foundry H2) rank below.

| fn | warm ms | status |
|----|---------|--------|
| salient_facts | 575 → ~175 (proj.) | FIXED 34707af (3.3x isolated) |
| evidence_receipt | 310 | NEXT — already rank-first; cost is the attestations scan? |
| converse | 147 | acceptable (H1 already helped converse_walk) |
| recall | 132 | main serve entrypoint; profile after evidence_receipt |
| define | 25 | fine |
| lexical_peers / senses / bubble_up | 3–11 | native path healthy |

---

## HIGH

### H1. `converse_walk` — the whole n-gram walk runs in plpgsql arrays
`extension/laplace_substrate/sql/functions/converse/converse_walk.sql.in`
- WALK loop (`:115-140`): per step, TWO `generate_subscripts` scans over the full
  stream array (`:118-128`) → O(steps × n) element-wise plpgsql evaluation.
- Visited set is `text[]` of hex-concatenated triples with `<> ALL(visited)`
  (`:121,127,134,137`) → O(v) string-list scan per candidate, string churn per step.
- GATHER filters whitespace tokens by calling `render_text(entity,64)` per token
  (`:79`) — an SPI render round-trip to test blankness.
- ORIENT (`:46-54`): correlated `(SELECT count(*) FROM consensus ...)` in ORDER BY,
  per prompt token, plus `top_synset`/`render_text`/`realize` per row.
- This REINVENTS `trajectory_generate.c` (native n-gram descent with consensus
  fallback) with a topic-weight steer bolted on. Known converse() latency complaint
  (see memory: latency = per-node SPI, not architecture).
**Fix sketch:** native SPI function (extend `trajectory_generate.c` with a weighted
stream + steer + anti-repeat hash set); plpgsql keeps only ORIENT orchestration.
Whitespace test via tier/type or perfcache codepoint class, never render_text.
**Blast:** converse()/chat() serving latency — the user-facing surface.

### H2. Foundry export numerics live in C# — math-in-native law violation
`app/Laplace.Cli/FoundryExport.cs` (2,056 lines; 18 transcendental sites:
`:452,522,1040,1160,1163,1221-1233,1331,1523,1544,1558,1662,1693,1760,1966,2003,2048`)
- PMI (`:1040`), U·√S spectral post-scaling (`:1160`), row normalization
  (`:1163,1558,1662,1693`), spectral cap scaling (`:1523,1544`), Box-Muller
  (`:2048`), SiLU (`:2003`) — all managed-double loops over vocab×dim matrices.
- Both native namespaces (`Laplace.Engine.Dynamics/Synthesis NativeInterop`) are
  already imported at the top of the file — the plumbing exists.
- `FoundryCommands.cs` has 14 more sites of the same species.
**Fix sketch:** move kernels into `engine/synthesis` (tensor_decompose already owns
this territory); C# keeps orchestration. One-shot export ⇒ perf medium, but this is
the largest single violation of "all math in C/C++/SPI" and it sits in the product
path (Mold-A-Model).
**Blast:** foundry export speed + single-implementation guarantee (exported weights
must decompose back to witnesses — two math homes makes that audit two audits).

### H3. `word_case_map_surface` — per-grapheme SPI render round-trips for case mapping
`extension/laplace_substrate/sql/functions/lexical/word_case_map_surface.sql.in:9-28`
- Case-mapping ONE word costs 2–4 `render_text`/`grapheme_case_target` calls per
  grapheme, each an SPI function call, plus `constituents(p_word)` twice (`:27-28`).
- Case mapping is UCD data — exactly what the t0 perfcache holds natively.
**Fix sketch:** native `word_case_map(word_id, map)` walking constituents once and
mapping codepoints against the perfcache. Second `constituents()` call for
`min(ordinal)` folds into the same pass.
**Blast:** word_case_variant_ids → variants() → lexical serving and ingest witness
paths.

---

## MEDIUM

### M1. `relation_in_family()` used as a per-row filter — EXPLAIN-gate
`extension/laplace_substrate/sql/functions/relation/relation_in_family.sql.in`
called per-row in e.g. `consensus/salient_facts.sql.in:18-24`.
- STABLE SQL wrapper whose body is `C-check OR EXISTS(SELECT ... FROM consensus)`.
  If the planner doesn't inline it (SET search_path on the function BLOCKS inlining
  — pg rule: functions with SET clauses are never inlined), that's an SPI subplan
  per candidate row.
**Fix sketch:** confirm with EXPLAIN ANALYZE on live data. If confirmed: fence the
family's id-set once per query (MATERIALIZED CTE or array), or maintain a native
dynamic-family bitmap refreshed at fold time (static families already ride the
highway manifest).
**Blast:** salient_facts and any serving function filtering by family per row.

### M2. `ConvertPartialToWalk` — 3 heap Lists per relation per period flush
`app/Laplace.Substrate/Crud/Npgsql/ConsensusAccumulatingWriter.cs:633-667`
- Per accumulated relation: `List<Hash128>` + `List<long>` + `List<ushort>` (cap 2)
  then `CollectionsMarshal.AsSpan` → `TestimonyWalk.Pack`. Run counts are ≤3 in
  practice (two runs + ushort splits only past 65k games).
**Fix sketch:** stackalloc / inline fixed-size buffers with a rare-case spill.
Millions of relations per epoch ⇒ tens of millions of tiny allocations per seed.

### M3. `Acc`-per-relation heap graph in the accumulator — measure first
`ConsensusAccumulatingWriter.cs:25-36, 360`
- One `Acc` class instance + ConcurrentDictionary entry with a 48-byte tuple key
  per unique relation; millions live simultaneously per working set. Per-Acc
  `lock(acc)` is why it's a class.
**Fix sketch:** only after GC evidence (dotnet-counters / VTune during a big seed):
sharded striped-lock struct table, or push accumulation across the native boundary
(content_witness_batch.c already crosses per batch).
**Blast:** working-set ingest memory ceiling + gen2 pauses.

### M4. `GrammarRowReader.FeedChunkFields` — string[] per row for ALL tabular ingest
`app/Laplace.Substrate/Abstractions/GrammarRowReader.cs:106-108`
- Every row of every tabular/tree-sitter corpus materializes `string[]` fields into
  `List<string[]>`. At OpenSubtitles/Tatoeba/TinyCodes scale that is per-row managed
  string churn between two native layers (tree-sitter → BLAKE3 hasher).
**Fix sketch:** span-based field slices over the chunk buffer feeding the composer
without intermediate strings where the consumer allows; measure first.

### M5. Sync-over-async — RESOLVED 2026-07-14: mostly benign by design
Classified all 32 sites. Outcome:
- FIXED: `GrammarIngestAdapter.cs:336,430` — added `TryWrite` fast path before the
  blocking channel write on the pinned parse workers.
- EXONERATED: `NpgsqlWorkingSetApply:198-200` + `FoundryCommands:581-584` read
  `.Result` after `await Task.WhenAll` (completed); `CpuTopology:336,381` +
  grammar workers are dedicated pinned threads (blocking = backpressure design);
  `SemLinkIngestAdapter:152` is `.Result` inside ContinueWith; `OpenGameAsync`
  returns `Task.CompletedTask`; ~10 hits are property names, not Task.Result.
- TOLERATED: `AppComposition.cs:25` — once-ever DI singleton factory at startup.
- ESCALATED → see M8 below: `MatchRunner.cs:196` blocks per ply, but the real cost
  is the per-ply design it wraps.

### M8. Chess live-learning: DB apply + FoldIncrementalAsync PER PLY behind one gate
`app/Laplace.Chess/Service/ChessLiveGameHost.cs:73-107` (RecordPlyAsync),
consumed per-ply from `MatchRunner.cs:193-197` inside `Parallel.For`.
- Every ply of every parallel lab game: semaphore → SubstrateChange build → DB
  apply → incremental fold, serialized process-wide by `_writeGate`, blocking a
  threadpool thread each time. N-game concurrency degrades to 1-ply-at-a-time.
- NOT mechanically fixable: batching plies changes learning-loop semantics (UCI
  reads consensus mid-game). Needs an operator design decision — e.g. per-game
  flush for lab matches (keep per-ply only for live lichess), or an async channel
  consumer owning the writes.

### M6. CTE materialization is implicit almost everywhere
- 73 files use CTEs; only 7 make an explicit MATERIALIZED / NOT MATERIALIZED
  decision. PG12+ inlines single-referenced CTEs — often right, but the law
  ("an expensive STABLE function in a filter runs per row; a MATERIALIZED CTE
  fences") is only ENFORCED where someone thought about it.
**Fix sketch:** phase-2 EXPLAIN pass over the top serving functions
(salient_facts, concept_peers, epistemic_status, retrieve_grounded, bubble_up,
converse family) on live data; annotate every CTE with an explicit decision.

### M7. Monolith files
- `FoundryCommands.cs` 2,218 · `FoundryExport.cs` 2,056 · `CpuTopology.cs` 1,517 (!)
  · `ModelTokenEdgeETL.cs` 1,203 · `SubstrateClient.Explore.cs` 924.
- `CpuTopology.cs` at 1.5k lines with 23 ToList/ToArray sites for what is
  "detect cores once at startup" deserves its own review.

---

## LOW

### L1. Hand-maintained relation exclusion lists in serving SQL
`salient_facts.sql.in:25-34` — 16-entry `NOT IN (relation_type_id(...))` list.
IMMUTABLE ⇒ const-folded, zero runtime cost; the cost is governance drift (the list
is a shadow salience-band). Fix: drive exclusions from `relation_types.toml` bands.

### L2. `DelimitedContent.Split` returns `List<string>` per call
`app/Laplace.Substrate/Abstractions/DelimitedContent.cs:10-12` — cold-ish; batch
with M4 if touched.

### L3. LINQ materialization chains — 34 production sites, mostly file discovery
One-per-file-at-startup (WordNetDecomposer:181, UDDecomposer:77, etc.). Not churn.
Exception to re-check: `IngestDescentFlush.cs:40` (`Pending.Select(...).ToList()`
per flush) — small n, but it's on the descent path.

---

## EXONERATED (checked, do not re-audit)

- `relation_type_id` / `eff_mu` / `canonical_id` — IMMUTABLE over the native hash /
  trivially inlined; long call-lists const-fold at plan time.
- Volatility discipline: only `register_canonical(s)` default to VOLATILE — they
  write; correct.
- Fold family plpgsql loops (`finish_consensus_fold` etc.) — partition/DDL
  orchestration around native `consensus_fold_*` calls. Correct layer.
- `Glicko2.cs` — pure P/Invoke wrapper over `engine/core/src/glicko2.c`. No
  duplicate math.
- `translate_to.sql.in` — already refactored correctly (id-space dedup, render
  above the sort, language resolved once at the input boundary).
- `ConsensusAccumulatingWriter` architecture — parallel chunked accumulate, raw
  binary COPY, span-based row writer, retry-safe ordering. Sound; only M2/M3 above.
- The "2M lines in engine/" scare — tree-sitter generated parsers
  (`engine/core/grammars/generated/`), not audit surface.

## PHASE-2 CHECKLIST (needs live data / profiling, per the verify-on-live law)

1. EXPLAIN ANALYZE the M1/M6 serving functions on hart-desktop's seeded DB.
2. dotnet-counters/VTune a working-set ingest for M3/M4 allocation evidence.
3. Deep-audit unswept surfaces: Endpoints request path, Chess services,
   `web/`, engine/'s own C (esp. `grammar_compose.cpp` 1,258 lines).
4. `IngestDescentFlush`/`IngestExistenceGate` descent path — line-by-line for
   allocation churn (only skimmed).
5. H1 fix design doc: steered-walk native API shape (stream+weights in, ids out).
