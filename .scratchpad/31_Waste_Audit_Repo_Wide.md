# 31 — Repo-wide waste/repetition audit (2026-07-17)

> **UPDATE 2026-07-23:** two Tier-2 body findings have since landed —
> witness_precedes_chain is now one batched consensus_upsert deposit (GH #428
> CLOSED), and ChessAnalyze no longer re-stages the same position ~7× per ply
> (StateKey/compose carried forward; merged chess-perf work #584).

> **Status (2026-07-17, PR #337):** Tier 1 — all nine implemented. Tier 2 —
> implemented except: chat band-mass → highway_mask (ranking-semantics product
> decision, deliberately left), trajectory_pairs_ensure incremental fold
> (native scan can't be scoped per-trajectory; probe + skip path cheapened
> instead), Search.Order comparator (tie order is search-tree-affecting),
> recall.c saved-plan conversion (per-call, not per-row; deferred), Windows
> test-all gating / setup-host reorder / Windows deps fingerprint (deferred as
> a Windows-side pass). Tier 3 items remain open by design. Found along the
> way: LichessBot PV-from-empty-TT bug (fixed), laplace_witness novel-cell
> first-game drop (fixed via spine), stale IngestSizingTests pin (fixed),
> Justfile parse failure at HEAD (open), build-system-deps safe.directory
> UNKNOWN-fingerprint bug (open).

Five parallel auditors, one per layer: ingest spine + decomposers, native engine +
extension C, SQL surface, scripts/CI, app/serve + tests. Every finding cites
file:line and was read from actual source. Design-mandated behaviors (no
floors/top-k, per-witness evidence, exact deterministic math) were excluded by
brief. Findings are ranked cross-layer by (impact × frequency × confidence).

---

## TIER 1 — high impact, high confidence, cheap to fix

### 1. Dead per-step palloc of up to ~3.2 GB in the consensus fold step
`extension/laplace_substrate/src/consensus_fold_step.c:120-128`
```c
obs = (glicko2_observation_t*) palloc(sizeof(glicko2_observation_t) * (Size) games);
consensus_fold_apply_partial(&st->st, phi, games, sum_score, tau, obs);
pfree(obs);
```
`consensus_fold_apply_partial` (`consensus_fold_math.h:31-42`) begins with
`(void) obs;` and calls the closed-form `glicko2_fold_uniform_period`
(`engine/core/src/glicko2.c:346-380`) which never touches the buffer. Leftover
from before the closed-form fold fix — this is what remains of the slow-fold
repair. Runs once per (consensus group × partial row) in every period fold;
`games` is bounded at 2^27 × 24 B = ~3.2 GB worst case, O(games) palloc/pfree
churn typical. **Fix: delete the alloc and the parameter.**

### 2. `consensus_adjacency()` calls `relation_rank_resolved()` per edge row — ~10 s of every foundry pour
`extension/laplace_substrate/sql/functions/generation/consensus_adjacency.sql.in:18`
The file's own header measures ~27 µs/call and "rank calls ~10s" (~369k per-row
C calls, ~half the function's runtime). `consensus_layer_plane.sql.in:17-22` and
`consensus_type_plane.sql.in` already have the fix: rank per `DISTINCT type_id`
(≤189 governed types) via a CTE, join back. Apply the same pattern.

### 3. `substrate_health()` runs its verification scans TWICE each — up to 5 full passes over `entities`
`extension/laplace_substrate/sql/functions/identity/substrate_health.sql.in:13-21`
`identity_law_violations()` and `fake_tier_band_count()` each appear once inside
the `ok` boolean and once as their own output column; PG does not CSE
independent scalar subqueries in one targetlist. On a 100M+-row `entities`
that's up to five sequential passes per health-gate call — and this is the
standard prove-the-stack step after every rebuild/seed. **Fix: compute each once
in a CTE/locals, derive `ok` from the columns.**

### 4. `NodeTypeName` marshals a fresh managed string per AST node, in per-record hot loops
`app/Laplace.Core/Core/GrammarDecomposer.cs:72-77` — `Marshal.PtrToStringUTF8`
per call, no cache, though node-type ids are a tiny static uint→name set per
grammar. Hot callers:
- `app/Laplace.Chess/Service/PgnMovetext.cs:42` — per AST node of every PGN
  game (hundreds–thousands of nodes/game × multi-million-game corpora =
  billions of allocations, to compare against 5 constant names).
- `app/Laplace.Substrate/Abstractions/JsonGrammarHelper.cs` (many sites) — per
  node per Wiktionary JSONL row.
- `app/Laplace.Substrate/Abstractions/GrammarRowComposer.cs:404-413` — per node
  per TSV row (Tatoeba, OMW).

Compounding: `GrammarIngestAdapter.cs:58,70` calls
`JsonGrammarHelper.FindRootObjectNode(record.Ast)` on EVERY row of EVERY
grammar modality; for TSV grammars there is never an `"object"` node, so it
scans the whole AST allocating a string per node and returns -1 — pure waste
per row. **Fix: resolve target type-ids once per grammar, compare uints; make
FindRootObjectNode conditional on JSON modality (or id-based).**

### 5. Publish phase completely ungated — every push to main pays full SPA build + 2 dotnet publishes + rsync + API restart
`scripts/pipeline.sh:608-627`, `deploy/linux/deploy.sh:29-111`,
`.github/workflows/laplace.yml:397-422`
Build/install/test all have fp/affected gates; publish has none (only `npm ci`
is stamped). A docs-only or engine-only push rebuilds vite, republishes API+UCI,
rsyncs, and bounces the live service. **Fix: fp gate over `app/` + `web/` +
native inputs, same shape as the install gate.**

### 6. Fresh 32 MB transposition table allocated per chess HTTP request; LichessBot re-allocs per ply
`app/Laplace.Chess/Service/ChessEngineService.cs:183-186` (BuildEngine per
request at :200,:215,:378); `Modality/Search.cs:76-77,88` (TtEntry[2^20] ≈32 MB
LOH, then `Array.Clear` re-zeroes the fresh array); `Service/LichessBot.cs:219,
234,241,261` + `ChessLiveGameHost.cs:96,135,174-193` — every recorded ply
rebuilds Search (32 MB) AND re-fetches the learned PST (synchronous 384-id
`consensus_by_ids` round trip) on the live clock. PST refresh may be deliberate
(live learning); the Search realloc is pure waste — `Think()` clears the TT on
entry anyway. **Fix: pool/cache one Search per service/game.**

### 7. `generate_walk` final ordering: O(n²) insertion sort with `numeric_cmp` DirectFunctionCall per comparison
`extension/laplace_substrate/src/generate_walk.c:410-429`; also per-node
`numeric_add`/`eff_mu_display_numeric` materialization at :388,:394. The file
documents 155–172K-node walks; a 10K-node depth level ≈ ~50M numeric_cmp calls.
**Fix: keep path_mu as int64/double, qsort native comparator, convert to
numeric only at emit.**

### 8. UD lane: relation resolution 2-3× per token edge + O(T²) BLAKE3 dedup per sentence
`app/Laplace.Decomposers/UD/UdSentenceEmitter.cs:95-97,113-114,131-132` —
`ResolveFeature`/`ResolveDeprel` (P/Invoke + 3-4 string allocs each) called
twice/thrice per feature/deprel per token over a ~50-key run-invariant
vocabulary; `:209-218` `AddUnique` re-hashes every collected canonical on every
call (T²/2 Blake3 per sentence instead of T — keep a HashSet<Hash128>);
`:93` per-occurrence `$"{fName}={fVal}"` allocs. Tens of millions of tokens
across UD treebanks. **Fix: memo dictionaries + hash set.**

### 9. `PerfcacheTestFixture.Dispose()` unloads process-global native perfcache mid-suite — rule violation
`app/Laplace.Core.Tests/Core/PerfcacheTestFixture.cs:16,19` — unguarded
`Load` + `Dispose() => CodepointPerfcache.Unload();`, the exact pattern the
project law forbids. Core.Tests runs with default xunit parallelization, so
Unload unmaps the blob under concurrently running tests (flake/AV risk).
`Substrate.Tests/GrammarPerfcacheFixture.cs:11,17-20` is the correct template
(guarded load, no-op Dispose). **Fix: copy the guarded pattern.**

---

## TIER 2 — medium impact (real seconds/allocation storms, bounded surfaces)

### Native / extension C
- **`hypernyms` + `graph_contrast`: 2 unprepared SPI calls per emitted node**
  (`graph_taxonomy.c:332-356`, cap 2048; `graph_contrast.c:249-250`, cap 80) —
  `pg_laplace_realize_batch` (`realize_batch.c`) already exists: N ids in 6
  round trips. Use it.
- **`recall_session` routes the prompt and resolves the topic TWICE per call**
  (`recall.c:1257-1278` then `respond_impl:311-318` re-runs both with identical
  inputs — ~30 regexp_match attempts + a real SQL topic resolution, doubled per
  converse turn). Pass the computed route/topic through.
- **Suffix array fully rebuilt after ANY corpus append**
  (`trajectory_corpus.c:425-429,504-517`) — a one-sentence converse deposit
  triggers a full O(n log n × CAP) re-qsort of the whole stream on the next
  generation call. Merge-in new suffixes (old array is still sorted).
- **Per-attestation linear scan of the 23-alias + 203-canonical relation
  surface tables** (`relation_law.c:304-360`, called per emitted row via
  `attestation_engine.c:252,306` from ChessPgn/PropBank/etc.) — hundreds of
  millions of strcmps recomputing a constant; the id-side lookup already has a
  hash bucket table (`relation_law.c:362-376`), the surface side doesn't.
- **`define_fast` context scoring O(results × candidates) linear bytea_eq join**
  (`recall.c:695-707`) — file already uses HTAB keyed by 16-byte ids elsewhere.
- **`recall.c` serving queries all `SPI_execute_with_args`, no saved plans**
  (throughout; helpers in `spi_common.h`) while generate_walk/realize_batch/
  graph_taxonomy use SPI_prepare+keepplan. Per user-facing call.
- **`relation_rank_resolved` SPI fallback: up to 8 unprepared queries per call,
  no memo, per row when an ungoverned type_id (the family_root/UD class) hits a
  ranked read** (`laplace_substrate.c:718-765`). Per-backend hash memo
  (type_id → rank/NULL) kills the class. Trigger rate unverified — EXPLAIN a
  `consensus_out` on a UD word to confirm.

### SQL
- **`chat()` re-runs topic orientation up to 3× per turn** and the band-mass
  correlated aggregate scans every consensus edge per candidate synset per call
  (`chat.sql.in:54-55,127-141`; `converse.sql.in:42-44`; `converse_walk.sql.in:
  49-50`) — `entities.highway_mask` is the maintained structure and the file
  admits it's the intended fast path.
- **`evidence_receipt()` renders source labels per claim group before ranking**
  (`evidence_receipt.sql.in:52-61`) — thousands of `render()` calls per receipt;
  the Issue-52 rank-then-label fix was applied to object labels only. Fence a
  DISTINCT source→label map.
- **`trajectory_pairs_ensure()`: any single new trajectory → TRUNCATE + full
  whole-corpus rebuild** (`trajectory_pairs_ensure.sql.in:8-27`); no
  incremental watermark arm.
- **`relation_plane('traj','gap',N)` computes gaps 1..N and keeps only gap=N**
  (`relation_plane.sql.in:60-62`), bypassing the maintained trajectory_pairs.
- **`witness_precedes_chain()` writes consensus one bigram at a time**
  (plpgsql loop → 2 statements per pair per chat turn) — batched
  `consensus_upsert` spine exists.

### Ingest C#
- **Chess analyzer re-stages the same position through EmitNodes up to ~7× per
  ply** (`ChessAnalyze.cs:145-197` + `ChessGraph.cs:131-137` — ~200 redundant
  PhysicalityRow allocs/dedup probes per ply instead of ~35); also
  `m.StateKey(state)` recomputes the previous iteration's `StateKey(next)`.
- **Inventory pre-pass decodes whole corpora to strings just to count**
  (`ChessPgnDecomposer.cs:335-354` full ReadLine pass over multi-GB PGN;
  `IngestInventory.cs:70-84,103+` for Tatoeba links ~100M+ lines and per-UD-file
  CoNLL-U counts). Byte-scan like `EtlInventory.EstimateNewlineCount`.
- **`RelationTripleIngest.cs:88-90`: triples never hit the existence-gate
  short-circuit** — `RelationTripleRecord` isn't handled by
  `IngestExistenceGate.TryResolveRoot`, so both phrase tier-trees recompose for
  every one of ConceptNet's ~34M rows, including "dog"/"person" recomposed
  thousands of times.
- **`PosReference.cs:58,68`: every HAS_POS attestation resolves the POS tag
  natively twice** (TrackProbationaryPos re-resolves). Pass the flag through.
- **`TabularDecomposer.cs:40,84,87,100,103`: invariant ids (PREDICTS type id,
  OutcomeId, ColumnId) recomputed per row/cell.** Hoist.
- **`ChessPgnDecomposer.cs:134/160,135/281`: ParseNames + Date TagStr full-text
  scans run twice per game** (~4 redundant scans/game).
- **`TatoebaGrammarWitness.cs:55-56`: language resolved twice per sentence row**
  (~400 codes over ~12M rows — cache code→Hash128).

### Scripts / CI
- **cutechess: full cmake configure + build + reinstall on every publish**
  (`pipeline.sh:537-540,611`, `bootstrap-chess-lab.sh:93-122,192-198`) — no
  stamp, no pin check.
- **CLI rebuilt up to 10× per foundation run + per _ingest job**
  (`ingest-source.sh:25`, `ensure-foundation.sh:77-85`, `_ingest.yml:69-111`),
  worsened by `MSBUILDDISABLENODEREUSE=1`/`UseSharedCompilation=false` making
  each no-op build pay full startup.
- **`sync-external.sh:87-123`: ~5-7 git subprocesses × 310 submodules ≈ 1.5-2k
  spawns per push in the all-noop case** — one `git ls-tree HEAD external/`
  gets every gitlink.
- **Artifact-vs-source stamp gaps (stale-.so class):**
  (a) `pipeline.sh:280` dotnet skip guard passes if ANY one project has a
  Release tree; (b) dotnet test salt omits the install stamp that the regress
  key folds in (`test-parallel.sh:79-92` vs `fp.sh:58-63`) — installed-tree/DB
  changes don't re-key dotnet tests; (c) `Justfile:120-130` `db-fresh` mutates
  the install prefix behind the install stamp; (d) Windows deps gate is
  existence-only, no pin fingerprint (`rebuild-all.cmd:126-130`); (e)
  `installs_present` (`build-system-deps.sh:69-75`) doesn't cover postgis-3.so
  or libtree-sitter.a that CI requires.
- **Windows seed path triple-builds the app and uses none of the three outputs**
  (`seed-everything.cmd:41-43` slnx build + `seed-ladder.cmd` Cli build are dead
  weight; only the R2R publish tree from `seed-step.cmd:121-146` executes).
- **`db-reset.cmd --recycle` runs install-extensions twice** (`db-reset.cmd:
  17-29`) for one pg_terminate_backend's worth of new work.
- **Policy job deletes the codegen stamp every push** (`laplace.yml:130-134`) —
  the build job re-runs codegen forever even with unchanged TOML (3 runs/push).
- **`_ingest.yml:96-111` idempotency proof = two full `count(*)` over
  attestations** — use `evidence_count(p_source => ...)`.
- **Windows `test-all.cmd` has zero change-aware gating** (Linux parity gap;
  `test-parallel.sh` claims parity).
- **setup-host builds the app as root then deletes and rebuilds as runner**
  (`setup-host.sh:144-149` → `:79-97` → `:107-111`); root-owned stamps also
  break later non-root fp_record writes.

### App / serve / tests
- **12 test classes unconditionally re-mmap + BLAKE3 the 90 MB perfcache blob**
  (Decomposers.Tests static ctors calling unguarded `Load` when `LoadDefault()`
  is guarded and already invoked by TestModuleInit). Same root cause: `Load`
  has no already-loaded check — which also makes **every `laplace ingest` run
  load the blob twice** (`IngestCommands.cs:140` then `:453`).
- **Per-node LINQ/closures in the alpha-beta hot loop** (`Search.cs:221,243`,
  `MoveGen.cs:148-165` two fresh Lists per node) — millions of short-lived
  allocs per move at the 1M-node cap.
- **Billing test factories boot the full production composition incl. real
  SubstrateClient + CatalogPrewarm against the live DB**
  (`BillingTestFactories.cs:48-69`; `GoldenFactory.cs:19` shows the fix:
  RemoveAll<IHostedService>).
- **`/v1/embeddings`: one serial DB round trip per input string**
  (`EndpointMappings.Inference.cs:281-300`) — batch via unnest like the file's
  own sibling patterns.

---

## TIER 3 — low impact (listed for completeness)

- `tier_type_id`: BLAKE3 of one of 5 constant strings per emitted node
  (`content_witness_batch.c:21-31,278`); + 3 malloc/free per composite node.
- `isa_path` linear target scan per BFS node (`graph_taxonomy.c:437-444`,
  ~4.3M hash128_eq worst case) — HTAB already in file.
- `cooccurrence_scan`: 2 palloc/pfree per emitted pair
  (`trajectory_generate.c:169-188`).
- `continuations_collect` linear dedup ≤256 cmp per occurrence
  (`trajectory_generate.c:284-285`).
- `lexical_case.c:238-254`: 3 constant relation ids resolved via SQL round trip
  per call — native `rel_type_id()` exists.
- `eigenmaps.cpp:179-195`: n-sized scratch vector allocated per KNN row inside
  the TBB loop (thread-local reuse).
- QK count-then-write kernels double the full projection sweep
  (`qk_pairs_threshold.cpp:77-141`, `qk_pairs_threshold_pruned.cpp:130-200`) —
  currently only exercised by parity tests; `qk_project_cached.cpp` is the fix
  template if re-adopted.
- SQL: `language_coverage()` 4× count subplan; `laplace_nearest_entity()`
  entities probe per KNN candidate when mask is NULL; systemic
  `LAPLACE_STABLE_STRICT` macro + house `SET search_path` keep minting
  non-inlinable scalars (`sqldefines.h.in:5`; the eff_mu law class —
  `top_synset`, `word_language`, `label`, `realize`, `render`,
  `physicality_count` currently affected under bounded call sites).
- fp sweeps recomputed 3-5× per pipeline run; affected-app plan+record
  double-walk (`fp.sh:27-63`, call sites) — ~100-300 ms each, small.
- Policy scans re-grep the file list once per term (12 passes where 1-2 do).
- Perfcache `find` duplicated across yml/test-parallel/phases; double PG bounce
  on version bumps; smoke re-runs Endpoints tests already eligible in
  integration; `seed-step.cmd` PowerShell+WMI process scans 2× per rung.
- Ingest C#: `MarkProven([root])` single-element array per record;
  `pending.ToList()` copy per flush; `Task.Yield()` per record in test streams;
  `RowsOf(intent)` 3× per intent; ContentTierSpine memo re-hashes key bytes per
  lookup (may be net-neutral to change).
- Endpoints: per-request `new JsonSerializerOptions` (`EndpointJson.cs:12-15`);
  UCI/history replay O(plies × legal-moves) string allocs (`UciEngine.cs:
  298-302`, `ChessEngineService.cs:325-330`, `LichessBot.cs:198-201`);
  FoundryExport rebuilds invariant vocab array 8× per synthesis
  (`FoundryExport.cs`, 8 sites); WitnessCatalog dead DI registration;
  ChessLiveGameHostTests re-bootstrap vocabulary per test.

---

## Checked and clean (consolidated)

- **Write spine:** NpgsqlWorkingSetApply (chunked parallel bitmap probes, bound
  params, id-range parallel COPY), ConsensusAccumulatingWriter, descent flush
  (one O(tiers) probe per flush), StreamingUtf8LineReader, consensus_upsert
  (the 2026-07-16 fix is in and measured).
- **Native read core:** generate_walk walk core (prepared plans, batched
  frontier, HTAB dedup), astar_path, perfcache, highway_mask, realize_batch,
  render_text_batch, content_resolve, descent_probe, trajectory corpus caching
  (aside from the suffix rebuild), glicko2 closed-form fold math.
- **SQL:** top_relations bounded pool, salient_facts/consensus_out rank-before-
  render, foundry planes fenced + per-DISTINCT-type ranks, KNN anchors as bound
  params, synset_members MATERIALIZED fence, highway_mask_deposit, single
  Layer-1 migration, regress seeds tiny, eff_mu/edge_rank inline (law holds).
- **CI:** fp.sh design (content-based, success-only, force escape),
  affected-app.py (dependent-closed Merkle, conservative fallbacks),
  build-system-deps fingerprinting, install nm symbol gate, no duplicate ctest
  across lanes, deploy.sh npm stamp + parallel publishes.
- **Serve:** SubstrateClient pooled datasource + auto-prepare + batched unnest
  round trips + TTL/single-flight explore cache, ChessCompose memos,
  substrate bias hosts batching consensus_by_ids, UCI init discipline,
  Substrate.Tests fixtures (the correct guarded pattern), tiered perf/db test
  traits, model ETL (native batch math, no C# scalar loops found).
