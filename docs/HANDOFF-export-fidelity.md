# Handoff — Export Fidelity / Per-Layer Arenas (session continuation)

Hand this file to the next session verbatim. It is the complete state of the export-fidelity + model-deposition work.

## 2026-06-10 RESUMPTION — what actually happened to the per-layer rewire

**The checkpoint (`c213b47`) per-slot cell fold was DESTROYED by the next commit (`169fc03`,
"AI sabotage commit"), which replaced ModelTableETL.cs wholesale with a behavioral-planes-only
ETL.** Every later commit refined the impostor; no cell arena was ever deposited again, which is
why audit-export could never measure anything. Recovered 2026-06-10:

- `ModelCellETL.cs` — the checkpoint fold resurrected and ported to the current API
  (NativeAttestation.Score/Aggregated, slot.RelationTypeId, EntityTier.Vocabulary), epoch-fenced
  axis entities, plus a native **aggregated-batch path** (`laplace_attestation_aggregated_batch_build`
  + `laplace_score_batch_fp` bindings) that amortizes the per-cell P/Invoke + relation-law ceremony.
- `LAPLACE_MODEL_PLANES=cells|behavioral|all` — cells are the export testimony; the behavioral
  planes (token↔token) are world-knowledge adjudication. Both streams compose under the runner's
  monotonic-epoch law (cells 0/1, behavioral 2/3).
- **IngestRunner epoch buffering fixed**: the epoch barrier buffered an ENTIRE epoch in RAM before
  the first apply (a per-layer model epoch ≈ 1.1B rows → OOM; this also explains a prior
  "hang"). Epochs now flush in bounded waves — an epoch is an unordered set, so waves preserve
  the fence law.
- **Deposit speed levers measured**: live-index attestation upserts ≈ 36k rows/s (8h/model — too
  slow). `LAPLACE_PERSIST_EVIDENCE=0` (consensus-only, the disposable-iteration lane) engages the
  period-fold staging path at ~940k relations/s; fold-side ETL then bottlenecks ~260k/s → ~70 min
  for TinyLlama at full grain. The aggregated-batch path (above, built but not yet benchmarked)
  targets the fold-side ceremony (~3.8 µs/relation).
- Behavioral ETL fixes: layer-context ENTITIES are now emitted (they were stamped as dangling
  context_ids → verify-fk failure), and the relation-law lookup no longer recomputes BLAKE3 per
  table entry per call (precomputed id cache in generated relation_law.c — the old cost was
  ~144 hashes per attestation build, billions per deposit).
- Known behavioral-planes issue (separate hunt): ATTENDS first-burst under full parallelism
  produced one AV and one stall pre-fix; planes=cells quarantines it. Fidelity gaps flagged:
  ATTENDS sums across heads; RMSNorm γ not folded into projections; FfnTokenPairsTile SiLU
  application unverified.

**Fleet-scale RULING (user, 2026-06-10, late session): token↔token planes ARE the deposition
product.** Cell arenas are the weight matrix re-encoded 35× larger into endpoints no other
witness can ever address — they cannot fuse, they never meet the text testimony, and they are
"raw weights + static model instructions," not knowledge. They are DEMOTED to an on-demand
carriage instrument (`LAPLACE_MODEL_PLANES=cells`) for synthesis-target validation only; the
original artifact already sits content-addressed in D:\Models\hub.

The behavioral planes are what scales: consensus identity excludes source AND context, so
ATTENDS/SIMILAR_TO/OV_RELATES/COMPLETES_TO pairs collapse across layers AND across witnesses —
every model in the 2.4TB fleet strengthens the SAME token-pair rows as Glicko games. Fidelity
work done same session: RMSNorm γ now folds into Q/K/V/gate/up columns before the planes are
computed (exact under the post-projection cosine); FfnTokenPairsTile verified applying SiLU
(ffn_edges.cpp). Open fidelity item: ATTENDS sums across heads (per-head = head entity as
context_id, ~32× volume, noise-floor-controlled) — decide before the fleet run.

## What this work is

Depositing a transformer's weights as adjudicated testimony, then synthesizing a GGUF back out. Two laws were wrong and are now fixed in code (committed); a third change (per-layer arenas) is **freshly written but NOT yet validated by a live run**.

## Committed and proven (git log, newest first)

- `a73f7e6` — C# routes the Score law through the engine kernel; `tanh` retired everywhere.
- `a65bdf6` — Score-law SQL surface `laplace.laplace_score` / `laplace_score_inverse`, regress 8/8.
- `2ea85da` — `audit-export` verb + rational Score-law kernel `engine/core/src/score.c` (7/7 gtest).
- `226add1` — A1 Score-law round-trip test (live PG fold).
- earlier tonight: P0 config, P1 epoch folds, P4a FrameNet, Unicode single-pass, deadlock fix, instrumentation — all green, regress 8/8.

**Two laws fixed:**
1. **Rational Score law** `s = ½(1 + v/(M+|v|))` replaced `tanh(v/M)`. tanh saturated at ~10.7·M and crushed attention-sink outliers (the babble cause). Rational law: outliers v/M = 10/30/100 now recover at 100% (were 60/20/6%). One implementation in `engine/core/src/score.c`, exposed to SQL (`laplace_score*`) and C# (`Laplace.Engine.Core.ScoreLaw`). LAW: this lives in the engine only; SQL and C# orchestrate, never re-implement.
2. **Per-layer arenas** (JUST WRITTEN, UNVALIDATED): collapsing 22 layers into one consensus relation destroyed projections (audit measured cos-vs-original ≈ 0.20). Fix: each (role, layer) is its own type. See `app/Laplace.Decomposers.Model/ModelArenaPlan.cs` — the single contract all three consumers iterate.

## The export law (user ruling — do not violate)

The substrate NEVER replays a witness's numbers back into their architecture. Consensus is a new epistemic object (all witnesses adjudicated together). Export = render every arena FROM consensus. The cell-replay render (`synthesize substrate` Stream B) is a **pipeline-validation instrument only** — it proved lossless carriage (cos:law = 1.0000). The real export (A5, not yet built) is generative: Laplacian Eigenmaps over the relation planes → Gram-Schmidt + Procrustes onto the S³ anchors via content-addressed correspondences → render at the recipe's dimensionality. "Export their lm_head" is a dead concept.

## Identity-vs-provenance law (user ruling)

- Relation about the **witness's own mechanism** (the tensor-role arenas): layer enters the TYPE identity (`Q_PROJECTS/L7/v1`). Endpoints are per-source axis entities, so nothing cross-witness fragments.
- Relation about the **world** ("dog IS_A noun" extracted from a model): ONE shared arena always; the layer rides as `context_id` (the docs §13 provenance pattern). Layers agreeing = games strengthening one relation, not orphans.

## IMMEDIATE NEXT STEP (where to resume)

The per-layer rewire (`ModelArenaPlan.cs`, `ModelTableETL.cs` per-slot fold, `ModelDecomposer.InitializeAsync` per-layer type seeding, `ConsensusReExport`/`Program.cs SynthesizeFromSubstrateAsync`/`ExportAudit.cs` per-slot reads) **compiles** (CLI builds, `Laplace.Decomposers.Model.Tests` 11/11) but has NOT run live. Validate:

1. Fast-iteration loop the user authorized: fresh DB, ingest unicode + iso639 only, then deposit the two models. Linguistic ladder deferred — data is disposable until export is perfect.
2. Deposit TinyLlama snapshot: `ingest safetensors D:\Models\hub\models--TinyLlama--TinyLlama-1.1B-Chat-v1.0\snapshots\fe8a4ea1ffedaf415f4da2f062534de366a451e6`. Expect ~1.1B relations now (per-layer), not 153M. **B1 bulk-fresh apply is committed** (NpgsqlSubstrateWriter `bulkFreshSource`, set true by `IngestSafetensorSnapshotAsync`) — skips the attestation existence check. If apply is still the bottleneck, B2 (drop attestations secondary indexes before the role stream, rebuild after) is the next lever — NOT yet built.
3. `audit-export <model-dir>`: per-slot `cos:orig` should now be HIGH per layer (was 0.20 folded). That is the proof per-layer worked.
4. Then `synthesize substrate <config.json> out.gguf` and behaviorally check with `D:\Libraries\llama.cpp\build\bin\Release\llama-completion.exe -m out.gguf -ngl 99 -p "The capital of France is" -n 48` (NOT llama-cli — `-no-cnv` unsupported there; GPUs present: 4060 Ti + 1080 Ti). Coherent English = the §5 round-trip closed.

## Environment (every dotnet/cmake/psql invocation)

- Build/run wrapper (MANDATORY — native DLL load + solution platform): `cmd /c "call D:\Repositories\Laplace\scripts\win\env.cmd >nul 2>&1 && set Platform=&& cd /d D:\Repositories\Laplace && <cmd>"`. The `set Platform=` clears a stray var that breaks the solution config; omitting `env.cmd` fails native interop.
- DB: `LAPLACE_DB=Host=localhost;Username=postgres;Password=postgres;Database=laplace`. PG18 at localhost, postgres/postgres, tuned (scripts/win/tune-pg.cmd, committed). Perfcache: `LAPLACE_PERFCACHE_BIN=D:\Repositories\Laplace\build-win\core\perfcache\laplace_t0_perfcache.bin`.
- Model deposition envs: `LAPLACE_INGEST_WORKERS=8 LAPLACE_INGEST_BATCH=1 LAPLACE_INGEST_COMMIT_ROWS=250000 LAPLACE_FOLD_WORKERS=8 LAPLACE_DECOMPOSE_WORKERS=10 LAPLACE_STAGING_THRESHOLD=20000000`.
- Engine build: `cmake --build D:\Repositories\Laplace\build-win`; extensions: `scripts\win\build-extensions.cmd` then `install-extensions.cmd`; regress: `scripts\win\regress.cmd` (8/8 expected).
- `LAPLACE_SYNTH_ARENAS=EMBEDS,Q,…` (csv) ablation: render named arenas from consensus, copy the rest from the source snapshot verbatim. Validates plumbing only.

## Hard-won laws (violating these = the user deletes documentation and is genuinely harmed)

- **Vocabulary**: never use ML/compression jargon ("codec", "encode/decode", "embedding table", "compression"). Use project terms: Score law, deposition, testimony, witness, arena, fold, epoch, evict, render. ("pour"/"mold" were DELETED from the vocabulary 2026-06-10.) The vocabulary IS the ontology.
- **Tests**: each layer owns one pin (engine gtest = math, pg_regress = SQL surface, C# integration = end-to-end fold). NEVER hand-type expected pg_regress output — run, review the actual, bless from `build-win-ext/regress_substrate/results/`.
- **Self-containment**: any decomposer whose later batches reference earlier batches' entities must stage every referenced entity in-batch or have it DB-committed first (the fence) — required for parallel apply. Proven on FrameNet, Unicode, model axes.
- **Eviction**: pre-fold pruning = one `DELETE WHERE source_id`; partial failed depositions MUST be evicted before re-running or games double-count.
- Comment-free code. CRLF for `.cmd`. Windows is the platform. Do NOT read `D:\Repositories\X_BONEYARD`.
- The engine computes; SQL and C# orchestrate and marshal to C/C++ wherever a law exists.

## Open items after per-layer validates

- B2 bulk index strategy if apply is slow at ~1.1B rows.
- Phi-2 deposition (recipe `ArchitectureProfile.Phi` exists but UNVERIFIED — many Phi-2 HF exports fuse `query_key_value`/`dense` and carry biases the ETL currently ignores; first run will tell).
- A5 generative export (LE+GSO+PA from consensus) — the real export, design-doc-then-build.
- C2 full-ladder acceptance run (user runs the whole model farm + seed sources, backs up the final DB) — AFTER export is perfect.
