## Bucket: A1 — CLI / Ingestion / Api.Contracts

### Files read (coverage proof)
- [x] app/Directory.Build.targets
- [x] app/Laplace.Api.Contracts/Laplace.Api.Contracts.csproj
- [x] app/Laplace.Api.Contracts/Requests.cs
- [x] app/Laplace.Api.Contracts/ResponseModels.cs
- [x] app/Laplace.Api.Contracts/Responses.Billing.cs
- [x] app/Laplace.Api.Contracts/Responses.Chat.cs
- [x] app/Laplace.Api.Contracts/Responses.Core.cs
- [x] app/Laplace.Api.Contracts/Responses.Embeddings.cs
- [x] app/Laplace.Api.Contracts/Responses.Evidence.cs
- [x] app/Laplace.Cli/BenchCommands.cs
- [x] app/Laplace.Cli/ChessCommands.cs
- [x] app/Laplace.Cli/CliRuntime.cs
- [x] app/Laplace.Cli/ConsoleLogging.cs
- [x] app/Laplace.Cli/ContentRoundtrip.cs
- [x] app/Laplace.Cli/CpuTopologyCommands.cs
- [x] app/Laplace.Cli/DecompositionCommands.cs
- [x] app/Laplace.Cli/EtlWitnessRegistrations.cs
- [x] app/Laplace.Cli/FoundryCommands.cs (1696 lines, read in full across 2 pages)
- [x] app/Laplace.Cli/FoundryExport.cs (1592 lines, read in full across 2 pages)
- [x] app/Laplace.Cli/IngestCommands.cs
- [x] app/Laplace.Cli/IngestDataPaths.cs
- [x] app/Laplace.Cli/Laplace.Cli.csproj
- [x] app/Laplace.Cli/Program.cs
- [x] app/Laplace.Cli/QueryCommands.cs
- [x] app/Laplace.Ingestion.Tests/Laplace.Ingestion.Tests.csproj
- [x] app/Laplace.Ingestion.Tests/SyntheticDecomposerTests.cs
- [x] app/Laplace.Ingestion.Tests/TransientErrorRetryPolicyTests.cs
- [x] app/Laplace.Ingestion/IIngestObservability.cs
- [x] app/Laplace.Ingestion/IngestFailure.cs
- [x] app/Laplace.Ingestion/IngestProgress.cs
- [x] app/Laplace.Ingestion/IngestRunOptions.cs
- [x] app/Laplace.Ingestion/IngestRunResult.cs
- [x] app/Laplace.Ingestion/IngestRunner.cs
- [x] app/Laplace.Ingestion/Laplace.Ingestion.csproj
- [x] app/Laplace.Ingestion/LayerCompletion.cs
- [x] app/Laplace.Ingestion/TransientErrorRetryPolicy.cs
- [x] app/Laplace.slnx
- [x] app/NativeTestBootstrap.cs

All 38 files read in full. Several `.cs` files contain blank comment lines (decorative whitespace where comments were stripped) — noted, not flagged.

---

### Findings

#### F1 — `EntityTier.Vocabulary` used as a KIND in the layer-completion marker (tier-as-category)
- **FILE:LINE**: `app/Laplace.Ingestion/LayerCompletion.cs:18` (depends on `app/Laplace.SubstrateCRUD/EntityTier.cs:20` — `public const byte Vocabulary = 5;`)
- **SEVERITY**: HIGH
- **CATEGORY**: invention-violation
- **CLAIM**: `LayerCompletion.BuildMarker` stamps the marker entity at tier `EntityTier.Vocabulary` (=5). Invariant 3: tier = compositional depth ONLY, emergent (`max(child)+1`); kind lives in `type_id`/physicality/trust. The `HasLayerCompleted` type marker has no compositional depth — assigning it a fixed tier 5 encodes a category in the depth axis. CLAUDE.md names this exact symbol as "the live violation".
- **VERIFIED**: Read `LayerCompletion.cs` (`.AddEntity(typeId, EntityTier.Vocabulary, ...)`), and grep confirmed `EntityTier.Vocabulary = 5` is the only definition (`EntityTier.cs:20`). The entity is a relation-type meta node, not a tier-5 composite.
- **CONFIDENCE**: high

#### F2 — Broken test: asserts `LayerOrderingViolationException` that the production path can no longer throw
- **FILE:LINE**: `app/Laplace.Ingestion.Tests/SyntheticDecomposerTests.cs:513-526` (`LayerOrderingEnforced_RejectsLayerNWithoutPrereq`); exception class `app/Laplace.Ingestion/IngestRunner.cs:817`
- **SEVERITY**: HIGH
- **CATEGORY**: fake-test / correctness
- **CLAIM**: The test does `Assert.ThrowsAsync<LayerOrderingViolationException>(() => runner.RunAsync(decomposer, options))`, but `RunAsync` no longer throws it. The layer-ordering check was deliberately removed (see comment `IngestRunner.cs:46-51` "the old layer-ordering check was procedural thinking that contradicts the DAG"). Grep across the whole repo shows `LayerOrderingViolationException` has NO `throw` site — only the class definition (lines 817/821) and this one test assertion (line 524). The `HighLayerDecomposer` (LayerOrder=5) yields no units, so `RunAsync` completes normally and the assertion fails. This test is dead/red or never executed.
- **VERIFIED**: Read `RunAsync` in full (no throw of this type); `Grep "LayerOrderingViolationException"` returned exactly 3 hits: 2 definition lines + 1 test assertion, zero throw sites.
- **CONFIDENCE**: high

#### F3 — Multiple flag-gated ingest execution lanes in one runner (converge-don't-fork)
- **FILE:LINE**: `app/Laplace.Ingestion/IngestRunner.cs:139-258` and `303-478`
- **SEVERITY**: HIGH
- **CATEGORY**: fork
- **CLAIM**: `RunAsync` branches into THREE distinct ingest paths: (a) `LAPLACE_INGEST_SYNC=1` fully-synchronous inline path (139-171), (b) single-worker unbounded-channel producer/consumer path (172-252), (c) `RunUnorderedParallelAsync` (253-258, 303-434) which itself forks into a single-consumer drain (385-391) vs an N-lane partitioned commit fan-out gated by `LAPLACE_COMMIT_LANES` (393-433). This is exactly the "flag-gated parallel lanes / multiple commit lanes" disease CLAUDE.md §2 warns against — three+ overlapping implementations of the same apply loop, with `ProcessOneIntentAsync`/`ProcessBatchAsync` duplicated across all of them. The sync lane is self-described as a "diagnostic/mitigation for the native heap-corruption race" (141-145) — a workaround lane left in the trunk.
- **VERIFIED**: Read all branches in `IngestRunner.RunAsync` + `RunUnorderedParallelAsync` + `DrainLaneAsync`.
- **CONFIDENCE**: high

#### F4 — Parity-oracle dual decomposer path for omw/conceptnet/atomic2020/wiktionary (two writers per source)
- **FILE:LINE**: `app/Laplace.Cli/IngestCommands.cs:55-56, 137-160`
- **SEVERITY**: MEDIUM
- **CATEGORY**: fork
- **CLAIM**: `EtlGenericRouted = {omw, conceptnet, atomic2020, wiktionary}`. When `EtlManifest.IsRoutable(sourceKey)` is true these route through the generic `EtlDecomposer` (line 137-140); otherwise the same four sources fall through to their bespoke decomposer cases (`OMWDecomposer` 148, `ConceptNetDecomposer` 152, `Atomic2020Decomposer` 151, `WiktionaryDecomposer` 153). Two parallel implementations of the same source are maintained side-by-side, selected by manifest state — the comment admits this is "to prove parity with their bespoke decomposer." Converge-don't-fork: one of the two should be retired.
- **VERIFIED**: Read the routing `if` and the `switch` arms for all four sources.
- **CONFIDENCE**: high

#### F5 — Parallel write path institutionalizes 23505 unique_violation as an expected, retried conflict (contra "conflicts ≈ 0")
- **FILE:LINE**: `app/Laplace.Ingestion/TransientErrorRetryPolicy.cs:32-55`; consumed at `IngestCommands.cs:477-479` (`workers > 1 ? ConcurrencyRetry : NoRetry`)
- **SEVERITY**: MEDIUM
- **CATEGORY**: invention-violation / perf
- **CLAIM**: `ConcurrencyRetry` (10 attempts) treats `23505 unique_violation` as transient and retries the WHOLE batch. The comment (lines 41-49) states the writer's `ON CONFLICT DO NOTHING` was removed and two parallel workers now legitimately race inserting the same novel content-addressed id, raising 23505 that retry resolves. Invariant 7: a correct ingest has `conflicts ≈ 0`; conflicts firing = the top-down dedup descent was skipped. Building a retry policy around expected 23505 contradicts the dedup-before-compute design and concedes per-row insert races. (The actual INSERT/ON CONFLICT lives in `NpgsqlSubstrateWriter`, SubstrateCRUD bucket — flagged here because this bucket encodes the expectation.) Note also the stale contradicting comment in `SyntheticDecomposerTests.cs:252-256` still claims "entity ON CONFLICT DO NOTHING".
- **VERIFIED**: Read `ConcurrencyRetry`, `IsConcurrencyConflict`, and the `RetryPolicy` selection in `BuildIngestOptions`.
- **CONFIDENCE**: med (the writer's actual SQL is out of this bucket; the policy + comments are in-bucket and explicit)

#### F6 — `model-bench` CLI command is a no-op stub but advertises real work
- **FILE:LINE**: `app/Laplace.Cli/BenchCommands.cs:30-77`; usage string `app/Laplace.Cli/Program.cs:63`
- **SEVERITY**: MEDIUM
- **CATEGORY**: dead-code / correctness
- **CLAIM**: Program help says `model-bench [model-dir] (run the whole-model FFN/relation ETL on a real model; no DB)`. The implementation enumerates models then, per model, does `await Task.CompletedTask;` and prints `"bilinear bench retired (edge-ETL bench pending)"`, forcing `ok = true`. It runs no ETL and always returns success. A command that claims to bench but does nothing — dead/misleading surface.
- **VERIFIED**: Read `ModelBenchCmd` end-to-end; the loop body has no real work and hardcodes `ok = true`.
- **CONFIDENCE**: high

#### F7 — Foundry/export heavy numeric compute runs in C#, not native libs (altitude)
- **FILE:LINE**: `app/Laplace.Cli/FoundryExport.cs` (`ApplyPpmi` 787-812; `CooFromAdj` degree-cap sort 817-835; `FactorSparseRandomized` orchestration 1034-1113; `ProjectOperator` dense assembly) and `app/Laplace.Cli/FoundryCommands.cs` (`TrainByteBpe` 383-427; per-token dense `(M-I)·R` frame-advance `Parallel.For` 1153-1163; `lm_head` accumulation 715-785, 1179-1230)
- **SEVERITY**: MEDIUM
- **CATEGORY**: altitude
- **CLAIM**: Invariant 5 says render/export belongs in `laplace_synthesis`/`laplace_dynamics`. The hottest numeric kernels (SVD `TensorSvdTruncate`, `LaplacianEigenmapsFromSparseGraph`, `ComputeSubstrateGram`, Procrustes) ARE delegated to native — good. But byte-level BPE training, PPMI, PMI/log-odds reweighting, COO degree-cap top-k sorting, the O(vocab·dModel²·nLayers·ops) residual-frame advance, and lm_head row accumulation/normalization are all implemented in managed C#. `TrainByteBpe` even carries `(TODO: move to C/SPI per perf plan)` (line 382). This is heavy lifting misplaced in C#. Lower priority than the ingest path because export is "synthesis, not the product."
- **VERIFIED**: Read both files in full; identified native-delegated vs C#-resident numeric sections.
- **CONFIDENCE**: high

#### F8 — Program help text omits live commands (recall, chat, chess)
- **FILE:LINE**: `app/Laplace.Cli/Program.cs:48-66` vs dispatch `70-90`
- **SEVERITY**: LOW
- **CATEGORY**: other (doc/code drift)
- **CLAIM**: The dispatch handles `recall`, `chat`, `chess` (lines 77, 80, 82) but the usage banner (50-65) does not list them. Minor surface inconsistency.
- **VERIFIED**: Compared the help string against the `switch`.
- **CONFIDENCE**: high

#### F9 — `document` ingest usage still advertises the PRECEDES bigram generator
- **FILE:LINE**: `app/Laplace.Cli/IngestCommands.cs:350` ("entities + physicalities + PRECEDES bigrams")
- **SEVERITY**: LOW
- **CATEGORY**: other
- **CLAIM**: The work map (`iridescent-cooking-waterfall`) aims to retire the bigram generator. The `document` ingest path (via `DocumentDecomposer`, out of this bucket) is still surfaced in the CLI as producing PRECEDES bigrams. Informational pointer to confirm whether this path is on the retire list; the decomposer itself is in another bucket.
- **VERIFIED**: Read the usage string; cross-ref CLAUDE.md §4. Did not trace `DocumentDecomposer` (other bucket).
- **CONFIDENCE**: med

#### F10 — `chess fetch` prints "once the pgn grammar is built into laplace_core" while `ingest chess` is already wired
- **FILE:LINE**: `app/Laplace.Cli/ChessCommands.cs:78` vs `app/Laplace.Cli/IngestCommands.cs:173-174`
- **SEVERITY**: LOW
- **CATEGORY**: other (stale message)
- **CLAIM**: `ChessCommands.FetchAsync` tells the user to run `laplace ingest chess` "once the pgn grammar is built into laplace_core", implying it is not yet available; but `IngestCommands` already routes `"chess"` to `ChessPgnDecomposer`. Message likely stale (the chess-modality branch landed PGN ingest per recent commits). Could not confirm grammar build state from this bucket.
- **VERIFIED**: Read both call sites.
- **CONFIDENCE**: med

#### F11 — `IngestRunResult.Failures` declared `IReadOnlyList` but populated via `List` with `lock`-guarded mutation passed across worker threads
- **FILE:LINE**: `app/Laplace.Ingestion/IngestRunner.cs:41` (`var failures = new List<IngestFailure>()`), mutated under `lock (failures)` at 592/612/713 from `DrainLaneAsync` worker tasks
- **SEVERITY**: LOW
- **CATEGORY**: correctness
- **CLAIM**: Failure aggregation across N parallel lanes locks on the shared `failures` list — correct for writes. But `_obs.OnIntentFailed` is invoked outside any lock from multiple lanes (615, 716); if a non-NoOp observability impl is supplied it must be thread-safe. With the default `NoOpObservability` this is harmless. Minor latent concurrency assumption.
- **VERIFIED**: Read the locking around `failures` and the unguarded `_obs` calls.
- **CONFIDENCE**: med

#### F12 — `Api.Contracts` clean (no logic, pure DTOs)
- **FILE:LINE**: all 8 `Api.Contracts` files
- **SEVERITY**: INFO
- **CATEGORY**: other
- **CLAIM**: The contracts project is pure `record` DTOs with `JsonPropertyName` attributes; no behavior, no native interop, no SQL. No invariant violations. `EmbeddingsResponse`/`EmbeddingProvenance` correctly separate FORM (S³ coord) from MEANING (Glicko-2 neighbours) per the two-level embeddings design. Billing DTOs are dev-sandbox (low priority per audit-priorities). Clean.
- **VERIFIED**: Read all eight files in full.
- **CONFIDENCE**: high

---

### Bucket summary
- CRITICAL: 0
- HIGH: 3 (F1 tier-as-kind in LayerCompletion; F2 broken layer-ordering test; F3 multiple flag-gated ingest lanes)
- MEDIUM: 4 (F4 parity dual-decomposer fork; F5 expected-23505 retry vs conflicts≈0; F6 model-bench no-op stub; F7 foundry C# heavy compute)
- LOW: 4 (F8 help drift; F9 bigram surface; F10 stale chess msg; F11 obs thread-safety)
- INFO: 1 (F12 contracts clean)

**Single worst issue**: F3 — the ingest runner carries three+ overlapping, flag-gated execution/commit lanes (`LAPLACE_INGEST_SYNC`, single-worker channel, `RunUnorderedParallelAsync` with `LAPLACE_COMMIT_LANES` fan-out), one of them an explicit workaround for a native heap-corruption race. This is precisely the "converge, don't fork — delete, don't accrete" disease CLAUDE.md flags as how the codebase has been most harmed, and it sits on the hot write path. F1 (tier-as-kind) is the cleanest single-line invention violation and is the one CLAUDE.md names explicitly.

### Cross-bucket notes (for coordinator)
- F1 depends on `Laplace.SubstrateCRUD/EntityTier.cs:20` (SubstrateCRUD bucket) — the `Vocabulary = 5` constant should be removed there once callers are fixed.
- F5's actual INSERT/ON CONFLICT semantics live in `Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateWriter.cs` (SubstrateCRUD bucket) — verify whether the set-based anti-join truly eliminates cross-worker 23505 or whether the retry is load-bearing.
- `IngestRunner` calls `_writer.ApplyManyAsync` / `ConsensusAccumulatingWriter.MaterializeConsensusAsync` (SubstrateCRUD bucket) — the "heavy compute in native, light merge in DB" invariant must be validated there, not here.
