# G4 dead-canonical triage — all 72 entries

Source: `scripts/isa-gate-baseline.json` → `violations.g4_dead_canonical` (72 entries), as it
exists on the `claude-guard-fixes` worktree. The key on `main` does **not** exist yet — the G4
check ships on the guard-fixes / `isa-g4-758` branches only.

Method: read each function's own `.sql.in`, then search for the name across
`extension/laplace_substrate/tests` (pg_regress sql + expected), `extension/laplace_substrate/test`
(sql_parity), `extension/laplace_geom`, `docs/`, `scripts/`, `web/`, and the six `*.Tests` projects —
plus the production directories the gate's caller corpus *excludes* (`sql/views`, `sql/probes`,
`sql/seed`, `sql/bootstrap`, `sql/schema`, `sql/indexes`, `sql/inference`, `sql/generated`, `engine/`,
`db/`, `deploy/`). Git history (`git log -1 --date=short` per file) separates "never touched since the
2026-06-30 import commit `6c706e7c`" from "actively maintained but unwired".

## Counts

| Class | Count | Meaning |
|---|---:|---|
| **A — DEAD** | **40** | Superseded or abandoned. Nothing references it in production, tests, regress, docs-as-usage, scripts or web. |
| **B — UNFINISHED WIRING** | **7** | Sound function, an identifiable caller *should* exist and does not. |
| **C — ENTRY POINT** | **25** | Legitimately invoked at a boundary: operator diagnostics, regress-pinned law, or a documented query surface. |
| | **72** | |

## Headline findings

1. **Glicko2 is NOT four dead functions — 2 of the 4 are a gate false positive.**
   `laplace_glicko2_sfunc` and `laplace_glicko2_finalfunc` are wired into
   `laplace_glicko2_accumulate.sql.in:10,12` as `SFUNC =` / `FINALFUNC =` of a
   `CREATE OR REPLACE AGGREGATE`. The G4 check only counts `name(` call syntax, so it misses
   aggregate-support binding — and it misses `CREATE OR REPLACE AGGREGATE` entirely (a grep for
   `CREATE +AGGREGATE` finds nothing). Deleting either one breaks the fold at install time.
   **The G4 detector needs an `SFUNC|FINALFUNC|COMBINEFUNC|SERIALFUNC|DESERIALFUNC|MSFUNC|MINVFUNC|MFINALFUNC|SORTOP`
   reference rule before this baseline is acted on.** The other two (`laplace_score`,
   `laplace_score_inverse`) are real C-backed fp1e9 conversions pinned by
   `tests/sql/glicko2_aggregate.sql` — regress is their only caller, which is legitimate.

2. **`corpus_trajectory_probe`'s own header names a caller that was deleted.** It says it is
   "called on EVERY walk/continuation request via `corpus_ensure`" — but `corpus_ensure`,
   `stream_reset` and `stream_stats` were dropped on 2026-07-29 by
   `generation/drop_retired_content_lane.sql.in:49,68`. It is an orphan of the retired
   generation-corpus lane, and its comment is now actively misleading.

3. **The variant-synthesis lane is dead end-to-end, C included.** `variant_walk` and
   `respell_variant` are the only SQL entries into `src/variant_synth.c` (~380 lines, memoized
   walk, four kept SPI plans). Nothing in SQL, C#, web, regress, docs or scripts calls either.
   Deleting the two wrappers strands the whole `.c` file — worth deleting together, not one side.

4. **The write path has two live hand-rollback functions nobody calls and nobody should.**
   `inference/laplace_prune` runs `DELETE FROM consensus`, and `inference/laplace_decay` runs a
   hand `UPDATE consensus SET rd = ...`. Both contradict CLAUDE.md's "Consensus accumulates at
   ingest. There is no backfill or rebuild path; do not add one." They are installed, callable via
   the MCP `op` tool (see caveat below), and untested. Highest-priority deletions in the list.

5. **`recall_interaction_response` is the one responder in a family of twelve that the C dispatcher
   never routes to.** `src/recall.c` calls eleven `recall_*_response` functions by name;
   `recall_interaction_response` (ATOMIC-2020 / ConceptNet commonsense: X_INTENT, X_REACT, CAUSES …)
   is absent. The commonsense corpus is folded and unreachable from `chat()`.

**Caveat on class C.** `laplace.api()` (`functions/ops/api.sql.in`) is an unfiltered `pg_proc` scan
of the schema, and the MCP `op` tool calls *anything* `api()` lists. So *every* entry here is
technically operator-reachable. Class C is assigned on **intent** — the function is a documented
diagnostic, a regress-pinned law, or carries a usage example in its own header — not on mere
reachability. Otherwise the class is vacuous and G4 can never be actioned.

---

## Class A — DEAD (40)

| Lane | Function | Evidence | Recommended action |
|---|---|---|---|
| analysis | `laplace_ancestry` | Band-mask recursive ancestry walk. Zero refs anywhere. Superseded by the `bubble_up` / `edges` / walk family. | Delete. |
| analysis | `laplace_entities_at_depth` | Body is `SELECT * FROM entities WHERE tier = p_depth`. Directly violates CLAUDE.md Identity ("never select rows by tier as a proxy for a role"). Untouched since import commit `6c706e7c`. | Delete. |
| analysis | `laplace_entity_attestations` | Subject→(type,object,eff_mu) fan-out; duplicate of `edges()` and `related_objects`. Untouched since import. | Delete. |
| analysis | `laplace_translations` | Shared-object self-join under a band mask; duplicate of `shared_objects`, itself dead. | Delete. |
| cascade | `cascade` | C-backed (`src/graph_cascade.c`). `docs/specs/37_Substrate_Operation_ISA.md:177,319` lists it under "Absorbs: … `cascade`" → fold into OP7. Only other hits are the generated `INVENTORY.md` and a `relation_types.toml` false positive on the word. | Delete with the OP7 fold; the C impl goes with it. |
| consensus | `related_objects` | `consensus/edges.sql.in:15` names it explicitly as one of the ten superseded edge reads that `edges()` replaced. Its own header ("Every realizer … now calls this") is stale — zero callers remain. Spec 37:318 folds it into OP5. | Delete. |
| consensus | `shared_objects` | Spec 37:318 fold list. Regress `converse.sql` references it, but as an *output* assertion of the surface catalog, not a call under test. | Delete with OP5. |
| converse | `converse_compose` | Own header: "NOT YET MEASURED … Do not wire this into `chat()` until that run exists." `chat.sql.in:422` records the gate was met by `converse_tiered` instead. Abandoned experiment superseded by its own successor. | Delete. |
| converse | `correlate` | Multi-word consensus correlation. Only hit outside its file is a prose mention in `docs/INVENTION.md`. Superseded by `shared_objects`/`edges`. | Delete. |
| converse | `links` | Own header records 140.9 s measured, and that the fix it needs (a batched `label_batch()` in C) "is not in this change" and does not exist. No production caller: every `links` hit in `web/` is a D3 graph field or the English word; `LinksTab.tsx` never calls it. Superseded by `edges()`. | Delete. |
| converse | `prompt_language_top` | Wrapper: `SELECT lang_id FROM prompt_language(...) LIMIT 1`. Header says it exists "for callers that just want to pin a rendering language" — none exist; `SubstrateTools.cs:302` calls `prompt_language` directly. | Delete. |
| converse | `structural_locale` | Geometry isolation probe over `v_word_points`. Zero refs. Only commits are the import and two repo-wide sweeps. | Delete. |
| corpus | `corpus_trajectory_probe` | Header names `corpus_ensure` as its caller; `corpus_ensure`/`stream_reset`/`stream_stats` were dropped 2026-07-29 (`drop_retired_content_lane.sql.in:49,68`). Orphan of the retired lane. | Delete. See finding 2. |
| generation | `continue_text` | Body is `SELECT step, entity, NULL::numeric FROM walk_text(...)` — a pass-through that discards `mu`. Only hit is a plan doc (`W1_Speaking_Loop.md`). Untouched since import. | Delete; callers use `walk_text`. |
| generation | `foundry_vocab` | Own header: "Identical shape to `corpus_word_vocab`, which is already the geometry-native vocabulary source." `NpgsqlFoundryReads.cs:288,301` calls `corpus_word_vocab` and `foundry_vocab_crawl` — never `foundry_vocab`. | Delete the duplicate; keep `corpus_word_vocab`. |
| generation | `pos_transition_plane` | Sibling `pos_class_transitions.sql.in:87` reads the same `word_order(p_vocab, p_trajs, 1)` source and *is* called. Two bodies for one fact. | Delete; keep `pos_class_transitions`. |
| generation | `respell_variant` | SQL entry to `src/variant_synth.c`. Zero refs in SQL/C#/web/regress/docs/scripts. Untouched since import. | Delete with `variant_walk` **and** `variant_synth.c`. |
| generation | `variant_walk` | Same lane, same result. `variant_synth.c` keeps four prepared plans purely for these two entries. | Delete. See finding 3. |
| geometry | `entity_physicality_coord` | One-line `v_word_points` coord lookup; every consumer inlines the same select. Zero refs. | Delete. |
| geometry | `structural_cluster_batch` | plpgsql `FOREACH` loop over `structural_cluster`. Zero refs. **Note the cascade:** it is the *only* caller of `geometry/structural_cluster.sql.in`, so deleting it makes `structural_cluster` newly G4-dead — a two-function island the gate only sees the outer half of. | Delete both, or neither. |
| identity | `is_compositional_type` | `compositional_tier_distribution.sql.in:7` states the deliberate reason nothing calls it: `= ANY(compositional_type_ids())` lets the array evaluate once and the btree serve the probe, where this wrapper "recomputes 5 BLAKE3 hashes for every entity row (100M+)". Actively avoided by name. | Delete. |
| inference | `laplace_decay` | Hand `UPDATE consensus SET rd = ...`. Contradicts "consensus accumulates at ingest; no backfill or rebuild path". Untouched since import. | Delete. See finding 4. |
| inference | `laplace_distill` | Band-mask consensus export ≥ threshold. Zero refs. Duplicates `consensus_export_relations_mu` (also dead). | Delete. |
| inference | `laplace_forward_step` | `laplace_attention_centroid` → `laplace_nearest_entity`, one hop. Superseded by the native walk (`src/generate_walk.c`, `src/steered_walk.c`). Untouched since import. | Delete. |
| inference | `laplace_prune` | `DELETE FROM consensus WHERE eff_mu < …`. Same rule violation as `laplace_decay`, with data loss attached. Untouched since import. | Delete first. |
| inference | `laplace_witness` | `converse/witness_precedes_chain.sql.in:11-16` explicitly supersedes it ("instead of the old per-bigram `laplace_witness` loop") and documents its bug: it seeded a novel cell at neutral *without folding its first game*. | Delete. |
| inspect | `attestation_response_type` | Body is verbatim `SELECT * FROM attestation_response(<same 5 args>)`. Identical signature, identical result. | Delete. |
| inspect | `attestation_unary_response_type` | Same relationship to `attestation_unary_response`. | Delete. |
| inspect | `top_relations_readable` | `render()` wrapper over `top_relations`. `NpgsqlSubstrateReads.cs:1232` and `SubstrateClient.cs:394` call `top_relations` directly and render on their own side. | Delete. |
| mu | `effective_mu` | C duplicate (`pg_laplace_effective_mu`) of the SQL `eff_mu` (`p_rating - 2*p_rd`). Spec 37:317 folds both into OP4. CLAUDE.md's read law requires `eff_mu` bodies to inline for the functional index — a `LANGUAGE C` variant cannot. | Delete; `eff_mu` is the one. |
| readback | `vertex_atom` | Re-export of `public.laplace_vertex_atom` (laplace_geom), which has its own regress coverage at `laplace_geom/tests/sql/st_4d.sql:200-203`. | Delete the re-export. |
| readback | `vertex_tier` | Same. | Delete. |
| relation | `relation_rank` | Superseded by `relation_rank_resolved` (memoized in `src/laplace_substrate.c:874`), which is what `edge_rank.sql.in:22` and the native walk use. The three `converse_tiered` hits are comment prose. | Delete. |
| relation | `relation_type_resolve` | Superseded by `relation_type_id()`, used substrate-wide. Only non-self hit is regress `schema_law.sql` asserting the installed surface. Untouched since import. | Delete. |
| structural | `consensus_export_relations` | `SELECT subject_id, object_id, rating, witness_count FROM consensus WHERE type_id = $1`. No export path uses it. Untouched since import. | Delete. |
| structural | `consensus_export_relations_mu` | Same body + `eff_mu`. | Delete. |
| structural | `consensus_export_unary` | Same, `object_id IS NULL` arm. | Delete. |
| structural | `geometry_predecessors` | Named mirror of `geometry_successors(p_point, p_limit, p_window, true)`; the sibling `geometry_neighbours` in the same file calls `geometry_successors(..., true)` directly rather than going through it. | Delete; the flag is the API. |
| trajectory | `trajectory_point_count` | Wrapper over `public.ST_NPoints`. `readback/constituents_closure.sql.in` calls `ST_NPoints` directly. Untouched since import. | Delete. |
| trajectory | `trajectory_prefix_distance` | Wrapper over `public.laplace_frechet_4d`. `structural/word_shape_distance.sql.in:4` calls `laplace_frechet_4d` directly. Untouched since import. | Delete. |

## Class B — UNFINISHED WIRING (7)

| Lane | Function | Evidence | Intended caller / recommended action |
|---|---|---|---|
| converse | `converse_tiered` | Was wired into `chat()` on a verified 2026-07-26 run, then **unwired by a hotfix**: `chat.sql.in:429-436` — "HOTFIX 2026-07-27: `converse_tiered()` is NOT called from the hot path either. Two unbounded reads inside it … Fixes for both exist on `perf/content-ladder-ledger` and did NOT make this merge." Spec 37:42 marks it "(S8, off)". | `chat()` describe/what_is branch. Land the two perf fixes off `perf/content-ladder-ledger` (unbounded `containers_of` arm; per-word `top_synset` concept lift), then re-wire. **Do not delete** — this is the sentence-tier composer the roadmap depends on. |
| converse | `witness_precedes_chain` | The OODA ACT/OBSERVE deposit — "how a prompt becomes witnessed content and a response becomes a first-class witness … evaluation IS ingestion". Nothing calls it, so that loop never closes. Named in `docs/plan/W7_Questions_Route_Themselves.md` and `W8_Infer_To_C.md`. | `chat()` post-reply deposit, and/or the MCP `feedback` tool. Wire it; it already batches through `consensus_upsert` correctly. |
| recall | `recall_interaction_response` | `src/recall.c` dispatches eleven `recall_*_response` functions by name; this is the twelfth and is never routed. Its relation set (X_INTENT/X_WANT/X_REACT/CAUSES/HAS_SUBEVENT…) is folded ATOMIC-2020 + ConceptNet data sitting unreachable. | Add the shape to the `recall.c` dispatch table (and the published-shape list `chat()` reads via `recall_intent`). See finding 5. |
| taxonomy | `retrieve_grounded` | Own header: "Formal R0/R2 tool ISA entry; abstains with 0 rows when ungrounded." No tool binds it — not in `SubstrateTools.cs`, not in the OpenAI-compat surface. | Bind it as an MCP/tool entry in `app/Laplace.Endpoints.Mcp/SubstrateTools.cs`, or fold into the `recall` family. |
| generation | `sentence_order` | The tier-3 sibling of `word_order` (doc 18 §3, "word_order one tier up"). `NpgsqlFoundryReads.cs:189` reads `laplace.word_order(@vocab,@trajs,@gap)` for the plane build; the sentence-tier equivalent read was never added. Actively maintained (4 commits through 2026-08-01). | `app/Laplace.Substrate/Crud/Npgsql/NpgsqlFoundryReads.cs`, beside the `word_order` plane read. |
| chess | `chess_opening_record` | "the opening leaderboard" — one row per opening, witness-weighted `eff_mu` off folded `(line, OUTCOME)` cells. Written 2026-08-02. The leaderboard plumbing exists (`NpgsqlSubstrateReads.cs:1861` `chess_ranked`, `:1928` `chess_player_record`, `web/src/home/Leaderboards.tsx`); this read was never added to it. | `NpgsqlSubstrateReads.cs` chess read family → league/leaderboard endpoint → `Leaderboards.tsx`. |
| ingest | `intent_preflight` | C-backed batch existence bitmap (`src/laplace_substrate.c:658-790`). `scripts/sql/converse-audit.sql:50` asserts "the C batch existence bitmap (ingestion offload) **is live**" — the audit proves it installed, but no ingest code path calls it. Untouched since import. | The apply path — `NpgsqlWorkingSetApply` / `IngestDescentFlush` client-side dedup. Either wire it as the offload it was built for, or delete it and the C with it; the audit line asserting "live" is currently asserting a fiction. |

## Class C — ENTRY POINT (25)

| Lane | Function | Evidence | Recommended action |
|---|---|---|---|
| ops | `ingest_runs` | Named in CLAUDE.md Ground-truth as *the* run-history read. Header states the operator question it answers. | Keep. Baseline as intentional. |
| ops | `consensus_count` | `scripts/decomposer-gate-check.py:219` and `scripts/sql/bench-queries.sql:7`. | Keep. |
| ops | `arena_counts` | `scripts/queries/factcheck.sql:9`. | Keep. |
| ops | `render_gaps` | `scripts/sql/substrate-audit.sql:29-31` (two calls). | Keep. |
| ops | `consensus_tier_distribution` | Ops-lane diagnostic; no reference anywhere. Weakest C in the list. | Keep, but it is a candidate for the audit script — add it to `substrate-audit.sql` or reclassify to A next pass. |
| ops | `entity_type_counts` | Same: ops-lane diagnostic, no reference. Untouched since import. | Same as above. |
| glicko2 | `laplace_glicko2_sfunc` | `laplace_glicko2_accumulate.sql.in:10` — `SFUNC = laplace_glicko2_sfunc`. **Gate false positive.** | Keep. Fix the G4 detector. |
| glicko2 | `laplace_glicko2_finalfunc` | `laplace_glicko2_accumulate.sql.in:12` — `FINALFUNC = laplace_glicko2_finalfunc`. **Gate false positive.** | Keep. Fix the G4 detector. |
| glicko2 | `laplace_score` | fp1e9 score conversion (`pg_laplace_score`), pinned by `tests/sql/glicko2_aggregate.sql`. | Keep; regress is the caller. |
| glicko2 | `laplace_score_inverse` | Same, inverse direction, same regress. | Keep. |
| mu | `foundry_witness_sat` | `tests/sql/walk_edge_weight_parity.sql:24-25` pins the Michaelis-Menten half-max at 4; `src/laplace_substrate.c:185` states the C constant `LAPLACE_WITNESS_SAT_HALFMAX 4.0` must match this body. It is the SQL reference implementation of a law the C copies. | Keep — deleting it removes the parity anchor. |
| chess | `chess_line` | Regress-covered: `tests/sql/chess_read.sql` + expected. GH #736 line-grain read. | Keep. |
| chess | `chess_time_pressure_outcome` | Regress-covered: `tests/sql/chess_read.sql` + expected. GH #606; header records the 90 s evidence-layer form it replaced. | Keep. |
| chess | `chess_opening_preference` | Header: "Promoted 2026-08-02 from a hand-run that took FOUR attempts to find the layer map" — an operator surface promoted from ad-hoc SQL precisely so nobody re-derives it. `docs/plan/ONBOARDING.md`. | Keep. |
| chess | `chess_opening_endgames` | Composed read over `chess_opening_games` + `chess_syzygy_line`; most recently touched file in the whole list (2026-08-03). | Keep. |
| model | `model_forward` | Header carries the operator usage example: `SELECT render_text(token), score FROM model_forward(:src,'France',10);`. `docs/specs/19_Factor_Storage_Research.md`. | Keep. |
| model | `model_attention_row` | Header carries its usage example joining against `consensus`. Doc 26 item B. | Keep. |
| model | `model_pair_cos` | Gravity decomposition over `model_pair_score`; model-lane inspection surface. | Keep. |
| analysis | `model_jitter_catalog` | "convicted training artifacts, as one indexed query"; header states the LIMIT belongs to the caller. `docs/specs/19_Factor_Storage_Research.md`. | Keep. |
| structural | `collocates` | `tests/sql/structural_surface.sql` + expected, `test/sql_parity/parity.py:43` (marked CONFLICT), `app/Laplace.Substrate.Tests/Crud/SqlConsolidationTests.cs:121`. Three independent non-production consumers. | Keep. |
| structural | `anagrams_of` | `tests/sql/structural_surface.sql` + expected; `docs/specs/05_Substrate_Invariants.txt`. | Keep. |
| structural | `word_shape_distance` | `tests/sql/structural_surface.sql` + expected; `docs/specs/05` and `docs/plan/W16_Reinvented_Wheels.md`. | Keep. |
| identity | `compositional_tier_distribution` | `scripts/sql/substrate-audit.sql:27`. | Keep. |
| readback | `register_canonical` | `tests/sql/converse.sql`, `tests/sql/chat_loop.sql`, and `scripts/refresh-regress-expected.py`. Regress fixture helper. | Keep. |
| lexical | `word_shape_peers_fast` | `lexical_peers.sql.in:10` states it deliberately: "Shape-fuzzy matching stays available as `word_shape_peers_fast` for callers that explicitly want it; it does NOT belong on the exact sense-lookup hot path." `drop_word_shape_peers.sql.in` retired the SQL original in its favour. Deliberate opt-in surface. | Keep. |

---

## Suggested order of operations

1. **Fix the G4 detector first** (aggregate-support references), then regenerate the baseline. Two
   of the 72 are false positives today; there may be other reference forms it cannot see
   (operator classes, `CREATE CAST`, `DEFAULT` expressions, index expressions).
2. **Delete the class-A 40** in lane-sized commits. Six of them are pure wrappers whose target is
   called directly (`attestation_response_type`, `attestation_unary_response_type`,
   `top_relations_readable`, `trajectory_point_count`, `trajectory_prefix_distance`,
   `geometry_predecessors`) — zero-risk. `laplace_prune` / `laplace_decay` go first on rule grounds.
3. **Watch the cascade.** Deleting `structural_cluster_batch` orphans `structural_cluster`; deleting
   `variant_walk` + `respell_variant` orphans `src/variant_synth.c` and probably
   `variant/respell_variant_seed.sql.in`. Re-run the gate after each lane.
4. **The class-B 7 are the actual finding.** Five of them are load-bearing roadmap items sitting
   installed and unreachable: the sentence-tier composer, the evaluation-IS-ingestion deposit, the
   commonsense responder, the tool-ISA grounded retrieve, and the chess opening leaderboard.
   `intent_preflight` additionally has an audit script asserting it is "live" when nothing calls it.
