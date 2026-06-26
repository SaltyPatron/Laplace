## Bucket: X1_substrate_csrc (extension/laplace_substrate/src)

### Files read (coverage — all 29 read IN FULL)
- [x] apply_batch.c
- [x] astar_path.c
- [x] consensus_fold_engine.c
- [x] consensus_fold_io.h
- [x] consensus_fold_math.h
- [x] consensus_fold_step.c
- [x] consensus_fold_walks.c
- [x] consensus_lexical_reads.c
- [x] consensus_reads.c
- [x] content_resolve.c
- [x] foundry_crawl.c
- [x] generate_walk.c
- [x] graph_cascade.c
- [x] graph_contrast.c
- [x] graph_geometry_reads.c
- [x] graph_taxonomy.c
- [x] graph_taxonomy.h
- [x] laplace_substrate.c
- [x] perfcache.c
- [x] perfcache_native.h
- [x] recall.c
- [x] recall_route.c
- [x] recall_route.h
- [x] spi_common.h
- [x] spi_nested.h
- [x] trajectory_corpus.c
- [x] trajectory_corpus.h
- [x] trajectory_generate.c
- [x] variant_synth.c

---

### FINDING 1 — apply_batch.c uses `ON CONFLICT` for entities + physicalities; the file's own header doc claims the opposite
- **FILE:LINE**: apply_batch.c:137 and :170 (code); :16-:35 and :140-:147 (contradicting prose)
- **SEVERITY**: HIGH
- **CATEGORY**: invention-violation (invariant 7) + disparagement/stale-doc (prose is a defect)
- **CLAIM**: Invariant 7 mandates "NO `ON CONFLICT`, NO per-row anti-join" in the load — the descent already proved the frontier novel, so re-checking is the mistake. The entities insert (line 131-137) ends `ON CONFLICT (id) DO NOTHING`; the physicalities insert (159-170) ends `ON CONFLICT (id) DO NOTHING`. Worse, the top-of-file header doc describes a *different* implementation: lines 16-22 say entities use "`INSERT … SELECT DISTINCT ON (id) … WHERE NOT EXISTS (id)`" and physicalities "anti-join BOTH the id PK and the (entity_id, type) UNIQUE. **NO ON CONFLICT**"; lines 33-35 reiterate "there is no ON CONFLICT and no retry loop here. The set-based anti-join is the ONLY novelty mechanism." The inline comment block at 140-147 describes a "hilbert-range cursor scan fills the existing_bitmap … merkle_dedup_filter_novel + INSERT … WHERE s.id = ANY($1)" — none of which appears in the actual statement (159-170). So the header documents code that is not present.
- **VERIFIED**: Read both statements in full. Entities: `... FROM %s s ORDER BY s.id ON CONFLICT (id) DO NOTHING`. Physicalities: `... ORDER BY d.hilbert_index ON CONFLICT (id) DO NOTHING`. Attestations (3) DO use a real `WHERE NOT EXISTS` anti-join (226) plus a `FOR UPDATE SKIP LOCKED` fold UPDATE (194-212) — so only the attestations path matches the doc; entities/physicalities do not.
- **NUANCE**: `ON CONFLICT DO NOTHING` on the id PK is functionally idempotent and, with partition-by-id.lo%N disjoint key spaces, will rarely fire. The defect is twofold: (a) it violates the "pure bulk append of the proven-novel frontier" invariant by re-probing the unique index per row, and (b) the file's authoritative header doc is simply false about what the code does — a doc defect that will mislead the next reader.
- **CONFIDENCE**: high

### FINDING 2 — Four parallel Glicko-2 consensus-fold implementations (fork / fold-lanes)
- **FILE:LINE**: consensus_fold_step.c:57 (`pg_laplace_consensus_fold_step` SQL aggregate sfunc/finalfunc); consensus_fold_engine.c:384 (`pg_laplace_consensus_fold_partition`, external merge-sort engine); consensus_fold_walks.c:98 (`pg_laplace_consensus_fold_walks`, walk-staging engine); laplace_substrate.c:47/106/182 (`pg_laplace_glicko2_sfunc`/`finalfunc` + `pg_laplace_glicko2_accumulate_games`)
- **SEVERITY**: HIGH
- **CATEGORY**: fork (invariant 8 — "converge don't fork … flag-gated parallel lanes (… fold lanes) … each new lane is the disease")
- **CLAIM**: There are at minimum three distinct *terminal* Glicko fold code paths (step-aggregate, merge-sort engine, walk engine), plus a second standalone aggregate (`glicko2_sfunc`/`finalfunc`) and a per-call `accumulate_games`, all computing the same Glicko-2 period update over the same `consensus` seeds. consensus_fold_engine.c and consensus_fold_walks.c both read staging tables + `consensus` seeds and write `consensus_next`, duplicating the seed scan, the partition routing (`fold_route_identity` vs `fold_route_subject`), the merge, and the bulk-insert emit (shared `fold_out_emit`). This is the multiple-fold-lane pattern the project rules explicitly forbid.
- **VERIFIED**: Read all four files; confirmed each independently calls `glicko2_init` + `glicko2_fold_uniform_period`/`glicko2_update_period`/`consensus_fold_apply_partial` and (for the two engines) emits to `consensus_next` via `fold_out_emit`. Routing helpers differ between engines (consensus_fold_io.h:71 vs :84).
- **CONFIDENCE**: high

### FINDING 3 — Consensus (rating/rd) is folded by a periodic terminal batch over staging + seeds, NOT inline in apply_batch
- **FILE:LINE**: apply_batch.c:173-229 (apply only sums `observation_count`); consensus_fold_engine.c:291-343 + 518-633 (seed scan of `consensus` → `consensus_next`); consensus_fold_walks.c:233-485
- **SEVERITY**: HIGH
- **CATEGORY**: invention-violation (invariant 4 / memory `consensus-folds-inline-not-drain`)
- **CLAIM**: Invariant 4 states the consensus fold "updates INLINE in `laplace_apply_batch` (online/immediate)" and that "separate 'run the fold to catch up' drains … are ANTI-PATTERNS." In the actual code, `apply_batch` performs **no Glicko computation at all** — it only folds `attestations.observation_count` (sum) and `last_observed_at` (greatest). The Glicko-2 rating/rd/volatility consensus is computed entirely by the separate terminal-fold engines (`consensus_fold_partition` / `consensus_fold_walks`), which scan period-partition staging tables PLUS re-scan the entire `consensus` table as "seeds" (consensus_fold_engine.c:291-343) and write a fresh `consensus_next`. That re-scan-everything-and-rebuild shape is precisely the "run the fold to catch up" drain the invariant names as an anti-pattern.
- **VERIFIED**: apply_batch.c attestation block (173-229) touches only `observation_count`/`last_observed_at` — grep of the file shows no `rating`/`rd`/`volatility`/`glicko` references. consensus_fold_engine.c `fold_scan_seeds` reads `rating,rd,volatility,witness_count` from `consensus` and the engine writes `consensus_next`.
- **NUANCE**: This may be the genuine intended *epoch/terminal* fold design and the "inline" claim in CLAUDE.md/memory may be aspirational; per the trust order (code > doc) I report what the code does. Either way there is a real disagreement between the documented invariant and the implementation — worth a decision by the lead.
- **CONFIDENCE**: high (that the fold is batch/terminal, not inline); medium (on classifying it as a violation vs. the real design)

### FINDING 4 — The invented `substrate/type/.../v1` namespace is string-walked/built across the read + synth surface (corrupt convergence index)
- **FILE:LINE**: content_resolve.c:290-293 (`regexp_replace(n.name,'^substrate/[a-z_]+/(.+)/v1$','\1')`); variant_synth.c:340 (`n.name = 'substrate/type/grammar/' || $1 || '/' || $2 || '/v1'`); generate_walk.c:31-32; graph_taxonomy.c:140-141; consensus_reads.c:207 and :216-217 (`render(c.type_id) LIKE 'substrate/type/%'`, `canonical_names … LIKE 'substrate/type/%'`)
- **SEVERITY**: HIGH
- **CATEGORY**: invention-violation (invariant 6 — concepts/types must anchor on real external ids, "never an invented `substrate/type/X/v1` namespace"; the charter's "string-walk of opaque keys / corrupt index")
- **CLAIM**: Multiple read and synthesis paths depend on parsing or constructing the invented `substrate/type/.../v1` canonical-name namespace as a string. RB_CANONICAL in `realize` strips it with a regex to recover a label; `respell_variant` builds a `substrate/type/grammar/<modality>/<node_type>/v1` literal to seed a walk; the walk/taxonomy/salient-facts queries detect "typing edges" by `LIKE 'substrate/type/%'` on canonical names. Invariant 6 says the convergence backbone must be real external ids (ILI/UPOS/ISO-639/relation-inventory names), content-addressed — not an invented namespace. These call sites are read-side confirmation that the index is, as the charter states, "corrupt" (typing/identity encoded as parseable opaque key strings rather than content-addressed external anchors).
- **VERIFIED**: Read each SQL literal directly in the listed files.
- **CONFIDENCE**: high

### FINDING 5 — graph_geometry_reads.c runs per-candidate SPI calls inside C loops (RBAR perf footgun)
- **FILE:LINE**: graph_geometry_reads.c:105-158 (`nearest_neighbors_4d`: SPI `laplace_angular_distance_4d` per candidate, `knn_limit = max(k*20,200)`); :397-418 (`structural_cluster`: `spi_entity_curve` + `spi_frechet_4d` per candidate, `knn_limit = max(lim*20,2000)`); :542-577 (`structural_locale`: `spi_angular_distance_4d` per candidate, up to 3000)
- **SEVERITY**: MEDIUM
- **CATEGORY**: perf / altitude
- **CLAIM**: Each of these read functions first runs one set-based KNN (`ORDER BY coord <<->> $1 LIMIT N`) and then loops over up to N candidates issuing one fresh `SPI_execute_with_args` *per row* to compute the distance/Fréchet. `structural_cluster` thus fires ~2000+ separate SPI round-trips (each its own plan execution) per call. The distance could be computed set-based in the original KNN query (or via one array call), avoiding thousands of nested SPI executions. Heavy per-row work that belongs in one set op / native batch — an altitude+perf violation (these are reads, not the ingest path, so not the top mandate, but real).
- **VERIFIED**: Read the loops; confirmed `SPI_execute_with_args` is called inside `for (… n_cand …)` / `for (… n_scored …)`.
- **CONFIDENCE**: high

### FINDING 6 — astar_path.c calls SPI_finish() before iterating astar_next() (potential use-after-finish)
- **FILE:LINE**: astar_path.c:156-181 (SPI_connect at 156; `astar_open` at 164 with `spi_expand` callback + ctx; `SPI_finish()` at 167; `astar_next` loop at 169-181)
- **SEVERITY**: MEDIUM
- **CATEGORY**: correctness (needs cross-file confirmation in laplace/core/astar)
- **CLAIM**: `spi_expand` (the neighbor-expansion callback) executes SPI plans and writes into `ctx.nodebuf`, which is `palloc`'d *after* `SPI_connect` (line 161, i.e. inside the SPI procedure context). `SPI_finish()` is called at line 167, then `astar_next(q, …)` is driven at 169-181. If `astar_next` lazily invokes `spi_expand` (rather than the whole search completing inside `astar_open`), it would run SPI plans and touch `ctx.nodebuf` after SPI has been finished and its memory context torn down — a use-after-free / SPI-not-connected crash. If `astar_open` is fully eager (search completes before it returns, `astar_next` only walks the reconstructed path), the code is correct but fragile (the ordering invites the bug on any refactor).
- **VERIFIED**: Traced within this file; cannot see `laplace/core/astar.c` (out of bucket) to confirm eager vs lazy. Flagging for the lead to verify `astar_open` does all expansion before returning.
- **CONFIDENCE**: medium

### FINDING 7 — cooccurrence_scan emits subject/object/gap pair counts from the corpus stream (candidate trajectory-pair backfill)
- **FILE:LINE**: trajectory_generate.c:46-192 (`pg_laplace_cooccurrence_scan`)
- **SEVERITY**: LOW
- **CATEGORY**: invention-violation (candidate, invariant 4 — "`trajectory_pairs` backfills are invented anti-patterns")
- **CLAIM**: `cooccurrence_scan` walks the generation corpus stream and produces (gap, subject_id, object_id, count) tuples — i.e. co-occurrence pair statistics. If the C# caller folds these back into the substrate as PRECEDES/CO_OCCURS_WITH attestations, that is the `trajectory_pairs` backfill the invariant forbids (occurrences should already be folded inline from attestations, not re-derived by a corpus scan). The fold-back is in the caller (out of bucket), so this is a candidate, not a confirmed violation. The `walk_continuations` PPM/suffix-array generator in the same file (286-469) is the live generation engine and appears to be the n-gram/bigram-style generator the `iridescent` plan wants retired — note for the lead, not flagged as a defect.
- **VERIFIED**: Read the scan; it produces pair counts. Did not trace the caller.
- **CONFIDENCE**: low (intent) / high (that it computes pair counts)

### FINDING 8 — trajectory_corpus.c interpolates a GUC string directly into SQL (SUSET-gated injection)
- **FILE:LINE**: trajectory_corpus.c:279-284 (`appendStringInfo(&q, " AND … a.source_id = laplace.source_id('%s'))", c->document_source)`)
- **SEVERITY**: LOW
- **CATEGORY**: correctness / unsafe dynamic SQL
- **CLAIM**: `document_source` (from GUC `laplace_substrate.corpus_document_source`, default `UserPrompt`) is interpolated unescaped via `%s` into a SQL string built with `appendStringInfo`, then run via `SPI_cursor_open_with_args`. A value containing a single quote would break the query or inject SQL. The GUC is `PGC_SUSET` (superuser-only to set), so the practical risk is low, but it should use `quote_literal_cstr`. Everything else in the bucket correctly uses parameterized `SPI_execute_with_args`; `apply_batch.c` correctly uses `quote_identifier`; `intent_preflight`'s `snprintf` table name is caller-hardcoded (entities/physicalities/attestations), so not injectable.
- **VERIFIED**: Read the `appendStringInfo` call and the GUC definition (`DefineCustomStringVariable`, PGC_SUSET, lines 56-60).
- **CONFIDENCE**: high (it is unescaped); low (exploitability, SUSET-gated)

### FINDING 9 — Minor: duplicated TAX_WALK_CAP macro; inconsistent SPI connect helpers
- **FILE:LINE**: consensus_reads.c:29 (`#define TAX_WALK_CAP 2048`) duplicates graph_taxonomy.h:11 (and graph_geometry_reads.c uses TAX_WALK_CAP via graph_taxonomy.h). generate_walk.c:108 uses bare `SPI_connect`/`SPI_finish` while sibling reads use the nesting-safe `laplace_spi_connect`/`laplace_spi_finish` (spi_nested.h).
- **SEVERITY**: LOW
- **CATEGORY**: other / consistency
- **CLAIM**: Harmless today (the macro values match; `walk_branches` is a top-level SRF so bare SPI_connect works), but the duplicated cap and the two connect conventions are drift that should be unified — `walk_branches` would break if ever called nested, unlike `walk_strongest` in the same file which already uses the nesting-safe helper.
- **CONFIDENCE**: high

---

### Notes on things that are CORRECT (to prevent re-flagging)
- The read surface (consensus_reads.c, consensus_lexical_reads.c, graph_taxonomy.c, graph_cascade.c, graph_contrast.c, recall.c, content_resolve.c) routes through the **consensus fold** for meaning: every ranking uses `laplace.eff_mu(rating, rd)` and gates on `NOT laplace.refuted(rating, rd)`. Geometry (`coord <<->>`, `angular_distance_4d`, `frechet_4d`) is used only as *form* signals (KNN seeding / structural neighborhoods / a secondary "structurally near" hint in relation_summary), never as the meaning ranking. This matches the invariant (coords = form, fold = meaning).
- All SQL reads are parameterized (`SPI_execute_with_args`) — no injection there. `apply_batch.c` embeds the staging prefix only via `quote_identifier` (apply_batch.c:56-61).
- `content_descent_bitmap` (laplace_substrate.c:358-468) is a real O(tier) top-down Merkle containment probe (recursive CTE that stops descending under a present trunk) — matches invariant 7's "present trunk ⟹ whole subtree present ⟹ skip."
- The Glicko math kernels are in native libs and the conservation guard in consensus_fold_walks.c:510-515 (`games_in != games_folded → ERROR`) is a real invariant check, not a fake test.
- consensus_fold_engine.c is a genuine external merge-sort (in-memory run → BufFile spill → k-way heap merge) — heavy compute correctly in C (good altitude).
- The `"CONSERVATION VIOLATION"` / `"sabotage"` strings encountered are runtime guards / fix-rationale comments, not the kind of Claude-authored status disparagement the charter warns about.

---

### Bucket summary
- CRITICAL: 0
- HIGH: 4 (Findings 1, 2, 3, 4)
- MEDIUM: 2 (Findings 5, 6)
- LOW: 3 (Findings 7, 8, 9)
- **Single worst issue**: Finding 2/3 together — the Glicko consensus is computed by **multiple parallel terminal-fold engines** (step aggregate + merge-sort partition engine + walk engine + a second standalone aggregate) running as a **periodic batch drain over staging + a full re-scan of `consensus` seeds**, while `apply_batch` does no Glicko at all. This is simultaneously the fork/fold-lane disease (invariant 8) and the "run-the-fold-to-catch-up drain" anti-pattern (invariant 4) the project rules explicitly forbid. Finding 1 (apply_batch uses ON CONFLICT while its own header doc swears it does not) is the most clear-cut single defect: code and authoritative prose directly contradict.
