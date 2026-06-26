## Bucket: A11_crud_migrations

app/Laplace.SubstrateCRUD (+Tests) + app/Laplace.Migrations (+Tests). SubstrateCRUD = the C# DB access layer (EntityTier + writers + reader); Migrations = the DbUp runner.

### Files read (33/33 — all read in full)
- [x] app/Laplace.Migrations.Tests/DbUpFixture.cs — *filename/class mismatch: file is named DbUpFixture.cs but contains `PostgisContainerFixture`*
- [x] app/Laplace.Migrations.Tests/Laplace.Migrations.Tests.csproj
- [x] app/Laplace.Migrations.Tests/PostgisAvailableTests.cs
- [x] app/Laplace.Migrations/Laplace.Migrations.csproj
- [x] app/Laplace.Migrations/Program.cs
- [x] app/Laplace.SubstrateCRUD.Tests/ConsensusAccumulatingWriterTests.cs
- [x] app/Laplace.SubstrateCRUD.Tests/Laplace.SubstrateCRUD.Tests.csproj
- [x] app/Laplace.SubstrateCRUD.Tests/LocalPgFixture.cs
- [x] app/Laplace.SubstrateCRUD.Tests/NpgsqlSubstrateWriterTests.cs
- [x] app/Laplace.SubstrateCRUD.Tests/ScoreLawRoundTripTests.cs
- [x] app/Laplace.SubstrateCRUD.Tests/SecondaryIndexPolicyTests.cs
- [x] app/Laplace.SubstrateCRUD.Tests/SubstrateChangeTests.cs
- [x] app/Laplace.SubstrateCRUD.Tests/SubstratePgCollection.cs
- [x] app/Laplace.SubstrateCRUD.Tests/TypeColumnLawTests.cs
- [x] app/Laplace.SubstrateCRUD/ApplyResult.cs — clean record
- [x] app/Laplace.SubstrateCRUD/ContentBatch.cs
- [x] app/Laplace.SubstrateCRUD/EntityTier.cs
- [x] app/Laplace.SubstrateCRUD/ISubstrateReader.cs
- [x] app/Laplace.SubstrateCRUD/ISubstrateWriter.cs
- [x] app/Laplace.SubstrateCRUD/Laplace.SubstrateCRUD.csproj — clean
- [x] app/Laplace.SubstrateCRUD/Npgsql/CalibratedInverse.cs
- [x] app/Laplace.SubstrateCRUD/Npgsql/ConsensusAccumulatingWriter.cs
- [x] app/Laplace.SubstrateCRUD/Npgsql/CopyBlobValidator.cs — debug-only blob validator (env-gated)
- [x] app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateReader.cs
- [x] app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateWriter.cs
- [x] app/Laplace.SubstrateCRUD/Npgsql/PgBinaryCopy.cs — clean COPY-binary helper
- [x] app/Laplace.SubstrateCRUD/Npgsql/SecondaryIndexPolicy.cs
- [x] app/Laplace.SubstrateCRUD/PhysicalityId.cs
- [x] app/Laplace.SubstrateCRUD/PhysicalityType.cs — clean enum
- [x] app/Laplace.SubstrateCRUD/SubstrateChange.cs — clean records
- [x] app/Laplace.SubstrateCRUD/SubstrateChangeBuilder.cs
- [x] app/Laplace.SubstrateCRUD/SubstrateReferentialIntegrityException.cs — clean (note: never thrown in this bucket; pre-check was removed, see NpgsqlSubstrateWriterTests.ApplyAsync_AcceptsForwardReference_NoPreCheck)

NOTE: the actual migration SQL (`db/migrations/*.sql`) and the SPI functions (`laplace_apply_batch`, `materialize_period_partition`, `finish_consensus_fold`, `relation_type_id`, `entities_exist_bitmap`, …) are NOT in this bucket — they live in `db/migrations` and `extension/laplace_substrate`. Schema-level tier/provenance claims below are bounded by what the C# pins; the SQL itself belongs to another bucket.

---

### FINDINGS

#### F1 — EntityTier.Vocabulary = 5 : tier-as-kind (THE confirmed live violation)
- FILE: app/Laplace.SubstrateCRUD/EntityTier.cs:20
- SEVERITY: HIGH — CATEGORY: invention-violation (invariant 3)
- CLAIM: `public const byte Vocabulary = 5;` encodes a KIND ("Abstract vocabulary (POS, morphology values, languages, category anchors, relation types)") in the depth axis. Tier must be compositional depth only (tier = max(child)+1, emergent per modality). KIND belongs in `type_id` + physicality + trust/source. The doc-comment lines 16-19 are the very rationalization the project memory flags ("Must not share tier 0 with codepoint atoms").
- VERIFIED: read EntityTier.cs in full; the 0-4 constants are the legitimate text grammar ladder, `Vocabulary=5` is a category. Grep confirms it is NOT dead — referenced in 30+ decomposer/ingestion files (UnicodeDecomposer, UDDecomposer, PropBankDecomposer, FrameNetDecomposer, ISODecomposer, Atomic2020Decomposer, VocabularyAnchor, RelationTypeRegistry, PosReference, BootstrapIntentBuilder, etc.). So the violation is propagated repo-wide; this file is its definition site.
- CONFIDENCE: high.

#### F2 — Many flag-gated fold lanes inside one writer (fork / converge-don't-fork)
- FILE: app/Laplace.SubstrateCRUD/Npgsql/ConsensusAccumulatingWriter.cs (whole file)
- SEVERITY: HIGH — CATEGORY: fork (invariant 8) + invention-violation (invariant 4)
- CLAIM: This single writer carries a thicket of mutually-exclusive lanes selected by env vars and ctor flags, exactly the "multiple fold lanes / commit lanes" the project rules call "the disease":
  - `LAPLACE_FOLD_LANE = terminal|bulk` → `_terminalFold` (defer all folding to a full rebuild) vs the incremental period chain (lines 104-109, 504-508).
  - `stageAsWalks` walk-journal lane vs flat accumulator lane (lines 397-424, 645-648).
  - `LAPLACE_FOLD_IMPL = sql|engine` and `LAPLACE_FOLD_PARALLEL=0` → `ParallelWalkFoldAsync` vs SQL fold (lines 671-678, 729-792).
  - `LAPLACE_FOLD_RESUMABLE=1` → `finish_consensus_fold_steps(NULL)` (CALL) vs `finish_consensus_fold()` (SELECT) (lines 709-711).
  - `_freshSource` → `materialize_period_partition_fresh` vs `materialize_period_partition` (lines 575-577).
  - Three public fold entry points: `FoldIncrementalAsync`, `MaterializeConsensusAsync`, and the auto period-flush in `ApplyManyAsync/AppendAsync`.
- VERIFIED: traced every branch in the file; each is a distinct server-side function name, i.e. a distinct lane in the SQL layer too. Comments themselves describe superseded lanes ("Phase 4 replaces this dict entirely with the walk journal", line ~80).
- CONFIDENCE: high (that the lanes exist). Med on severity weighting — some of this is genuine Glicko-2 rating-period batching, but the number of env-gated alternative implementations is the anti-pattern the rules name.

#### F3 — MaterializeConsensusAsync = a "run the fold to catch up" full-table rebuild/swap drain
- FILE: app/Laplace.SubstrateCRUD/Npgsql/ConsensusAccumulatingWriter.cs:662-721 (and the FoldIncrementalAsync docstring at 631-642)
- SEVERITY: MEDIUM — CATEGORY: invention-violation (invariant 4)
- CLAIM: Invariant 4 / memory `consensus-folds-inline-not-drain`: the fold must update INLINE in apply_batch; "separate run-the-fold-to-catch-up drains … are ANTI-PATTERNS." `MaterializeConsensusAsync` is exactly that terminal drain — its terminal lane calls `finish_consensus_fold()` which the FoldIncrementalAsync docstring (lines 636-639) explicitly describes as "the full-table rebuild/swap (finish_consensus_fold / consensus_fold_swap)". The accumulator buffers attestations in an in-memory `(subject,type,object)->Acc` dict (lines 234-263), stages periods to disk, and folds at boundaries — not inline per attestation.
- COUNTERWEIGHT (reported honestly): the canonical online path is the OTHER writer — `NpgsqlSubstrateWriter.ApplyManyAsync → laplace_apply_batch` DOES fold attestations inline (`attestations_folded` in the result, lines 300-315). And `FoldIncrementalAsync` (643-660) is the inline-per-period incremental update used by chess self-play (memory chess-modality-phase1). So the inline path exists and is default for the base writer; the ConsensusAccumulatingWriter's terminal `MaterializeConsensusAsync` rebuild is the drain that conflicts with the invariant. Whether the batched rating-period accumulation is legitimate Glicko-2 semantics or an anti-pattern is the genuine open question — flagged, not asserted.
- VERIFIED: read both writers end-to-end; traced the `_terminalFold` / walk / incremental branches in MaterializeConsensusAsync.
- CONFIDENCE: med.

#### F4 — Top-down O(tier) descent probe is defined but unused; ContentBatch flat-probes every trunk
- FILE: app/Laplace.SubstrateCRUD/ContentBatch.cs:99-160 ; app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateReader.cs:128-155 ; ISubstrateReader.cs:34-36
- SEVERITY: MEDIUM — CATEGORY: dead-code + perf (invariant 7 "dedup BEFORE compute, top-down")
- CLAIM: The reader implements `ContentDescentBitmapAsync` — documented as the "Top-down O(tier) containment probe … re-emitted content costs O(tier-depth) DB checks, not O(nodes)". Grep shows it has NO caller anywhere in the repo (only the interface default + the reader override). The live content-dedup path, `ContentBatch.ProbeAndEmitAsync`, instead calls the FLAT `EntitiesExistBitmapAsync` over EVERY tier>=2 node (lines 122-131), i.e. O(trunks) DB candidates, not the O(tier) descent. Also `ContentBatch.Append` builds the FULL tier tree (`IntentStage.BuildContentTree`, BLAKE3+geometry per node) for every novel canonical BEFORE the existence probe — compute precedes dedup for novel content (the per-canonical root cache only short-circuits exact re-occurrences, lines 67-71/88-90).
- VERIFIED: grep `ContentDescentBitmapAsync` → 2 files, both definitions, zero callers; read ContentBatch in full and confirmed it uses `EntitiesExistBitmapAsync`. The native `EmitContentTree` still skips present subtrees given the bitmap, so correctness holds, but the DB probe is not top-down and the named descent method is wired to nothing.
- CONFIDENCE: high (unused method, flat probe); med (perf impact magnitude — unmeasured).

#### F5 — Invented `substrate/type/X/v1` namespace for relation types and bookkeeping markers
- FILE: app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateReader.cs:18-20,35-36 ; app/Laplace.SubstrateCRUD.Tests/TypeColumnLawTests.cs:47-55
- SEVERITY: MEDIUM — CATEGORY: invention-violation (invariant 6)
- CLAIM: Invariant 6 says relation types anchor on the GWN/ConceptNet inventory name (blake3'd), "never an invented `substrate/type/X/v1` namespace." `TypeColumnLawTests.RelationTypeId_UsesSubstrateTypePath` asserts `relation_type_id('IS_A') = blake3('substrate/type/IS_A/v1')` — i.e. it PINS the invented namespace as correct. `HasSourceEverCompletedAsync`/`HasSourceCompletedAsync` likewise build `substrate/type/HasLayerCompleted/{layerOrder}/v1` (reader lines 18, 35). This is the "convergence index is corrupt … string-walks of opaque keys" condition CLAUDE.md §1 calls out. (The HasLayerCompleted marker is internal bookkeeping, less culpable; IS_A is a real relation type and is the squarely-flagged case.)
- VERIFIED: read the reader and the test; `relation_type_id` SPI itself is out of bucket but the test fixes its contract.
- CONFIDENCE: med (the test pins behavior; the SQL impl is in another bucket).

#### F6 — Laplace.Migrations.Tests does not test migrations
- FILE: app/Laplace.Migrations.Tests/PostgisAvailableTests.cs (all) ; DbUpFixture.cs (all)
- SEVERITY: MEDIUM — CATEGORY: fake-test / coverage gap
- CLAIM: The only test in the Migrations.Tests project spins a `postgis/postgis:18-3.6` testcontainer and asserts `CREATE EXTENSION postgis` + `postgis_full_version()` contains "POSTGIS=". It never invokes `Laplace.Migrations` (`RunUp`/`BuildEngine`/`PerformUpgrade`) — the DbUp runner, EnsureDatabase, script discovery, and the actual schema migrations are entirely unexercised. The project references Laplace.Migrations but uses none of it. So "Migrations.Tests" green proves only that a stock PostGIS image loads — nothing about this repo's migrations. Plus the file `DbUpFixture.cs` contains a class named `PostgisContainerFixture` (stale rename).
- VERIFIED: read both files in full; the fixture image is the upstream postgis image, not a build that has laplace_geom/laplace_substrate; no call into the Program/engine.
- CONFIDENCE: high.

#### F7 — `migrate reset`/`nuke` confirmation can be bypassed by an unrelated global `--yes`
- FILE: app/Laplace.Migrations/Program.cs:94-100
- SEVERITY: LOW — CATEGORY: correctness
- CLAIM: `Confirmed()` returns true if `Environment.GetCommandLineArgs().Contains("--yes")` anywhere on the command line, OR `LAPLACE_CONFIRM` equals the token. `--yes` is a blanket override for both RESET and NUKE (DROP DATABASE) — a single `--yes` meant for one operation green-lights the destructive other. Minor (dev tool), but a DROP DATABASE guard keyed on a generic flag is loose. Usage text (line 274) also claims default db "laplace" while `ResolveConnectionString` defaults `PGDATABASE` to "laplace-dev" (line 216) — doc/code drift.
- VERIFIED: read Program.cs in full.
- CONFIDENCE: high.

#### F8 — CalibratedInverse: dead ctor param + per-instance LUT rebuild
- FILE: app/Laplace.SubstrateCRUD/Npgsql/CalibratedInverse.cs:12-15,17-49
- SEVERITY: LOW — CATEGORY: dead-code / perf
- CLAIM: The `(NpgsqlDataSource ds, long phi)` ctor takes a data source and discards it (`_ = ds;`) — a misleading signature suggesting DB-backed calibration that does none. `Map(n)` builds a 4001-point Glicko-2 inversion LUT in managed code (Glicko2.AccumulateGames ×4001 + sort) lazily per distinct `n`, cached per instance only; constructing a new CalibratedInverse per call rebuilds it. Used by ScoreLawRoundTripTests and (per grep) foundry read-side. The Glicko math itself delegates to Laplace.Engine.Core, so this is orchestration, not a hard altitude violation — but the dead `ds` param and per-instance cache are smells.
- VERIFIED: read the file; ctor ignores ds; LUT cached in `_byN` instance dict.
- CONFIDENCE: high (dead param); low (perf, depends on call pattern).

#### F9 — `_proven` seen-set has no eviction / no bound
- FILE: app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateReader.cs:63,99,105-109,116-117
- SEVERITY: LOW — CATEGORY: perf (RAM)
- CLAIM: The reader's `_proven` ConcurrentDictionary<Hash128,byte> and `_rootCache` grow unbounded for the process lifetime (every probed-present trunk id + every composed canonical root is retained forever). CLAUDE.md mandates peak RAM O(batch + fixed tables), independent of corpus. For a full multi-source ingest these session caches scale with the distinct-content count, not the batch. The comment frames this as the "perfcache"; it has no cap. (Bounded in practice by distinct content, which for full WordNet/Wiktionary is large.)
- VERIFIED: read the reader; no eviction path exists.
- CONFIDENCE: med.

#### F10 — Writer correctly does bulk-insert-of-novel-frontier (NOT a violation — positive confirmation)
- FILE: app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateWriter.cs (whole)
- SEVERITY: INFO — CATEGORY: invention-violation (audit-clear)
- CLAIM (verified clear): `ApplyManyAsync` streams already-computed native tuples via binary COPY into per-call TEMP staging then makes ONE `laplace_apply_batch` SPI call per partition; no client dedup cache, no per-row ON CONFLICT, no client anti-join (novelty decided server-side). Per-content-id parallel partitions are disjoint by `id.lo % N` so cross-partition collisions cannot occur. This matches invariant 5 (thin orchestrator) and 7 (bulk insert of novel frontier). It throws if TestimonyWalks reach the evidence writer (lane separation). The heavy compose ran earlier in laplace_core during Transform. No domain-content-through-text-composer here. A round-trip budget guard (lines 219-231) instruments invariant 7. This is the canonical trunk and it is architecturally correct.
- VERIFIED: traced full ApplyManyAsync → ApplyPartitionAsync → CopyStageAsync path.
- CONFIDENCE: high.

#### F11 — PhysicalityId / entity ids are pure content addresses (NOT a violation — positive confirmation)
- FILE: app/Laplace.SubstrateCRUD/PhysicalityId.cs:7-30 ; SubstrateChange.cs
- SEVERITY: INFO — CATEGORY: audit-clear
- CLAIM (verified clear): `PhysicalityId.Compute` hashes (entityId, type, coords, trajectory) only — NO source, position, or index. `PhysicalityRow.SourceId` is carried as a column/provenance, not folded into the id. Entity ids are supplied pre-computed (16-byte content addresses). `SubstrateChangeBuilder.ComputeIntentId` DOES include sourceId + unit name + member ids, but that is an INTENT/batch identity (provenance metadata), not a substrate entity id — legitimate. Conforms to invariant 1.
- VERIFIED: read both files.
- CONFIDENCE: high.

#### F12 — SecondaryIndexPolicy is the correct anti-band-aid (NOT a violation — positive confirmation)
- FILE: app/Laplace.SubstrateCRUD/Npgsql/SecondaryIndexPolicy.cs:66-79
- SEVERITY: INFO — CATEGORY: audit-clear
- CLAIM (verified clear): `SuspendForBulkLoadAsync` REFUSES to drop secondary indexes on a populated table (only drops on a first empty table), with a comment explaining the dedup probes require the indexes — this aligns with CLAUDE.md "No band-aids / never reflexively drop indexes." Table identifier is regex-validated (`^[a-z_][a-z0-9_]*$`) before interpolation; index names come from pg_catalog and are quoted — no injection. Tests (SecondaryIndexPolicyTests) cover empty-drop+rebuild, populated-keep, dispose-rebuild, and unsafe-identifier-throws against a real DB.
- VERIFIED: read policy + tests.
- CONFIDENCE: high.

#### F13 — Tests are real (fresh DB, rows_new>0) — positive confirmation, with one shared-fixture caveat
- FILE: LocalPgFixture.cs ; NpgsqlSubstrateWriterTests.cs ; ConsensusAccumulatingWriterTests.cs ; SubstrateChangeTests.cs ; TypeColumnLawTests.cs
- SEVERITY: INFO — CATEGORY: audit-clear
- CLAIM (verified clear): `LocalPgFixture` drops + recreates `laplace_substratecrud_test` and installs postgis/laplace_geom/laplace_substrate before the suite. Writer tests assert real inserts (EntitiesInserted==2, DB count==2), real idempotency (second apply → TrunkShortcircuitHit, 0 inserted), real consensus folds (wc==7, rating>neutral, rd decreasing across periods), and a real round-trip budget (O(partitions)). These are not no-op/`rows_new=0`-against-populated-DB fakes. CAVEAT: all PG-backed classes share ONE fixture via `[Collection("substrate-pg")]` → the DB is created once per collection, not per test; tests stay isolated only because each uses distinct `H(seed)` ids. Acceptable but fragile (a future id collision across classes would cross-contaminate). The pure-builder tests (SubstrateChangeTests) need no DB and assert the in-memory attestation fold (games sum, net outcome, mixed-phi throw) correctly.
- VERIFIED: read fixture + all test bodies.
- CONFIDENCE: high.

---

### Bucket summary
- CRITICAL: 0
- HIGH: 2 (F1 tier-as-kind definition site; F2 multi-lane fold fork)
- MEDIUM: 4 (F3 materialize drain; F4 unused top-down descent / flat probe; F5 invented substrate/type namespace; F6 migrations never tested)
- LOW: 3 (F7 --yes blanket confirm; F8 CalibratedInverse dead param; F9 unbounded seen-set)
- INFO/positive: 4 (F10 writer bulk-insert correct; F11 ids content-addressed; F12 index policy correct; F13 tests real)

Single worst issue: **F1 — EntityTier.Vocabulary = 5 (EntityTier.cs:20)** is THE confirmed tier-as-kind violation the project flags as live; it is defined here and propagated to 30+ decomposer/ingestion files. F2 (the env-flag fold-lane thicket in ConsensusAccumulatingWriter) is the worst architectural-altitude issue and the clearest "converge don't fork" breach in this bucket.

Note on disparagement tags: the audited C# is largely free of the ✅/❌/DEAD editorializing; comments here are mostly load-bearing design notes (some superseded, e.g. "Phase 4 replaces this dict"). The status tags the charter warns about live in the .md docs, not these sources.
