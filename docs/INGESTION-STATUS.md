# Model-ingestion status — what's fixed, what's still broken

**Status of record for AI-model ingestion.** Read with `docs/SUBSTRATE-FOUNDATION.md` (the ratified
lens). Updated 2026-06-01. The point of this file: stop re-breaking things that were already corrected.

## The law (never violate)

- **Content is identity. Source / model / layer / head / position / magnitude / time are WITNESSES,
  never identity.** Same content → same hash, regardless of which model/neuron/source produced it.
  Putting *anything* source-derived into an entity id (e.g. ordering an n-gram's constituents by weight
  magnitude) breaks dedup and is the cardinal sin.
- **Ingestion is O(params) streaming, never a recompute / GEMM / vocab² bilinear.**
- **No top-k / floors / budgets — ever.** Zero is the only non-event; a tiny |m| is a Glicko DRAW by
  the math (tanh(m/M) ≈ ½), played, never dropped. Magnitude lives in the Glicko-2 μ, so selectivity
  is `ORDER BY μ` at query time, never discarded at ingest. Write volume is answered by the set→set
  Merkle attestation (dedup by construction), never by a cut.
- **Evidence keeps provenance (source, layer, head, time); consensus drops it** (one row per
  (subject, kind, object), Glicko-2-accumulated over all witnesses).

## Working + verified

- **Generic shape-based circuit detection** — `ModelGeometry.Detect` finds QK/OV/FFN from tensor SHAPE +
  universal HF config dims, NO per-family code. Cross-model test passes on TinyLlama (GQA) and Phi-2
  (MHA, `dense`/`fc1`/`fc2`). `ModelGeometryTests`.
- **Contracted-bilinear circuit ingest** — `WeightTensorETL.EmitCircuitMemoriesAsync` →
  `ModelCircuitEdges.Emit`: project each circuit's two sides through E / E_U once, contract
  M = Left·Rightᵀ tile-by-tile (engine `bilinear_edges_tile`, exact f64), emit SIGNED token×token
  observations. The earlier argmax address-book emit — and the perf numbers measured on it
  (TinyLlama ~31s, Phi-2 ~90s) — is superseded; it is the dead path queued for deletion below.
- **Cross-dtype decoder** — `LoadRawBF16AsF32`: F32/F64, F16, BF16, F8_E4M3, F8_E5M2, I8/16/32/64, U8,
  BOOL; fail-loud on unknown (GGUF block-quant is a separate container — not handled).
- **Sharded safetensors** — `SafetensorsContainerParser.ParseModel` unions all `*.safetensors`; each
  tensor carries `FilePath`. Single-file + sharded uniform.
- **Per-model source identity** — `ModelDecomposer.SourceForModel(modelDir)` (HF `models--ORG--NAME` →
  `ORG/NAME`). No hardcoded TinyLlama. Guard/synth/stats use it; stats counts model kinds globally.
- **Recipe null-safe** — `GetDoubleOr`/`GetInt` tolerate JSON null (Phi `rms_norm_eps:null` →
  `layer_norm_eps`).
- **Deterministic QK projection** — `qk_project_cached.cpp` reverted from 8-lane plain to serial Neumaier;
  bit-identical to the all-pairs reference; parity test un-skipped, 6/6.
- **All circuits emitted** — QK→ATTENDS, OV→OV_RELATES, FFN→COMPLETES_TO, each with its (layer,head)
  Witness as `attestations.context_id` (evidence provenance).
- **Re-ingest guard** keys on COMPLETES_TO (idempotent; no second DB hammer).
- **Top-k removed; the floor env knob is gone** — `LAPLACE_CIRCUIT_FLOOR` no longer exists in code.
  The surviving cut is `ModelCircuitEdges.CalibrateArena`'s θ (recall-budget-derived) — still a
  floor/budget, an OPEN violation of "no floors / budgets ever" (see Still open).

## Broken / being unfucked (in this pass)

- **[CARDINAL] n-gram id was source-tainted** — constituents were ordered by weight |magnitude|
  (source/position) → same token-set → different Merkle id per witness → NO dedup. FIX: order by content
  (`Hash128.CompareToBytewise`), magnitude only in μ. (`WeightTensorETL.EmitUnit`.)
- **Consensus kept context** — the then-extant `rebuild_consensus` (since removed by design) GROUP BY included `context_id`, and layer/head is in
  context → witnesses never collapsed (consensus rows == evidence rows; zero dedup). FIX: key consensus on
  (subject, kind, object) only; drop source AND context. (`13_consensus.sql.in`.)
- **Queries were ad-hoc** — ranked-μ / completions / dedup-stats were hand-run each session. FIX: permanent
  substrate functions (`top_relations`, `completions`, `consensus_stats`) in the extension SQL.

## Signed-observation consensus (ARCHITECTURE.md §10) — LANDED + verified

The evidence/consensus split is now real and the Glicko math is DERIVED, not knobbed:

- **Evidence = OBSERVATION, not state.** `attestations` columns are now
  `(score, opponent_rd, arena_m)` — one Glicko-2 match per witness. `rating/rd/volatility`
  live ONLY on `consensus`. (engine `intent_stage.c` column list + binary, `AttestationRow`,
  `04_attestations.sql.in`, the writer.)
- **Signed magnitude → score** `s = ½(1+tanh(signed_m/M))`. + = win (confirm/attract), − = loss
  (refute/repel), 0 = ½. The model path passes the SIGNED coupling — `abs()` is gone, so a
  negative weight is a real Glicko loss, never folded to a win. (`AttestationFactory.Score`,
  `WeightTensorETL` `CreateObservation`.)
- **M is measured, not a knob** — per-arena RMS of the tensor (`WeightTensorETL.MeasureRms`);
  stored in `arena_m` for audit. The energy-`0.99` floor knob is no longer the score source.
- **Trust → opponent φ, never a μ multiplier.** witness weight (kind_rank × source_trust ×
  tenant_trust) → `opponent_rd` via `WitnessPhi`; Glicko's own `g(φ)` does the weighting.
  (`AttestationFactory.WitnessPhi`.)
- **Neutral opponent (1500), not the old sub-neutral 1000;** `incremental_consensus` feeds the C
  aggregate the stored `score`/`opponent_rd` and replays each row `observation_count` times
  (occurrences = games) via `generate_series`. (`13_consensus.sql.in`; the batch `rebuild_consensus`
  was removed by design — consensus accumulates at ingest only.)
- **VERIFIED end-to-end** (`tests/sql/consensus_signed.sql`, live DB): confirm μ=1640e9 >
  neutral 1500e9 > refute μ=1359e9 (symmetric); draw = exactly neutral; trusted (1640) > crank
  (1629) at identical score; 8-games (1748) > 1-game (1640). Engine 37/37 tests, 28/28 factory
  tests pass; `CREATE EXTENSION` loads all edited SQL.
- The math kernel (`glicko2.c`, paper-pinned) was already the authority via the C aggregate —
  the fix was the per-row arguments, not the math.

## Tier / trust corruption (truth #5 — "tier" is the entity Merkle stratum ONLY)

- **`KindValueTier` (a tier on KINDS) and `TrustClass` (a tier/class ladder on TRUST) are
  corruption.** Tier is reserved for the entity Merkle depth (T0 codepoint → up). Trust is a
  Glicko-2 value (rating/rd/vol), never a class/tier. Kind significance is a kind RANK, not a tier.
- **DONE in the §10 landing:** tier PRIORS on μ are gone (`TierPrior` deleted, no μ seeded from a
  tier); tier is OUT of evidence (rows are score/opponent_rd/arena_m); `BootstrapIntentBuilder`
  no longer mints tier entities or HAS_VALUE_TIER meta-attestations; the model path carries no
  `T9`. Kind significance + source trust are now numeric (`KindRank`/`SourceTrust` ∈(0,1]) folded
  into opponent φ.
- **PURGED (verified live):** the `KindValueTier`/`TrustClass` enums are deleted; significance +
  source trust are numeric (`KindRank`/`SourceTrust` ∈(0,1]) folded into opponent φ. (`TrustClass`
  survives only as a per-decomposer `Hash128` id of its seeded `substrate/trust_class/*` entity —
  correct; the trust-class *entities* are not purged.) The `substrate/kind_tier/T*` entities +
  `HAS_KIND_VALUE_TIER` attestation are no longer seeded in `10_bootstrap.sql.in` — the bootstrap
  regress now asserts they stay gone (purge-guard) and that the 16 canonical kind arenas are present.
- **STILL TO DELETE (G2.3):** the dead `ExtractAsync` / `BuildAddressBook` / `DetectEnergyFloor`
  argmax-address-book path in `WeightTensorETL` — removed once the faithful contracted-operator
  path carries all three circuit kinds (ATTENDS / OV_RELATES / COMPLETES_TO).

## Still open (next)

- **Morph (LE→GSO→Procrustes)** gated off (`LAPLACE_SKIP_MORPH`): dense eigenmaps is O(n²·d). It gives the
  spatial/locality axis (GIST `physicalities_coord_gist` + Hilbert) and the export placements, but n-dim→4D
  is lossy. Either drop it (relations carry the content) or make its affinity the streamed sparse graph.
- **Dedup magnitude vs granularity** — per-neuron n-grams may be too specific to recur; token→token recurs
  most. Measure `consensus_stats` after the cardinal fix; decide granularity.
- **Semantic validation** — resolve relations to surface text; verify a known fact (e.g. capital-of-France).
- **Synthesis / GGUF re-export REWRITTEN (2026-06-03)** — `synthesize substrate` now reads the
  CONSENSUS circuit arenas (ATTENDS / OV_RELATES / COMPLETES_TO), inverts the signed score map
  (`ConsensusReExport.SignedStrength`), builds the spectral token basis over the union graph,
  SVD-factors each arena through the basis (`tensor_svd_truncate` — export-only) and fills the
  mold's q/k, v/o, up/down at the recipe rank. Compiles + unit-suite green; END-TO-END RUN AGAINST
  AN INGESTED MODEL STILL OWED (DBs were dropped mid-session; restore + re-ingest first).
- **Breadth** — GGUF block-quant (Flux, `gguf/`), MoE (Qwen3/DeepSeek; detector groups by expert but
  untested), non-text modalities (the n-gram trajectory entity is already the modality-blind unit; only the
  front-end atom extractor is modality-specific).
- **Granular test suite** — have ModelGeometry cross-model + QK parity; owe NgramTrajectory (content-id
  stability/dedup), Math4d.Centroid, run-to-run determinism, recipe, tokenizer.
- **θ / recall-budget calibration is an OPEN violation** — `ModelCircuitEdges.CalibrateArena` derives
  θ from a top-K retrieval-fidelity budget and SKIPS sub-θ couplings instead of playing them as draws;
  "no floors / budgets ever" stands. Likewise the per-(i,j) edge emit (10⁸+ rows per circuit) is the
  per-cell dump the set→set Merkle-hyperedge attestation exists to replace — dedup by construction is
  the write-volume answer, not a cut. Both need the set→set emitter.
- **Per-cell cost MEASURED (2026-06-04):** TinyLlama via the per-(i,j) emit ran at ~3.9M evidence
  rows/min with ~29M edges per (layer,head) witness — projecting ≈21 BILLION rows / multiple days
  for one 1.1B model. Killed at 88M rows / 3 of 726 witnesses. The billion-edge overflow is the
  emit ignoring the DAG, exactly as the correction records. Partial evidence awaits per-source
  eviction (`DELETE FROM laplace.attestations WHERE source_id = <model content id>`).
- **THE MODEL-INGEST DESIGN — "ETL on conventional AI, for AI" (user, 2026-06-04; full record
  in the model-ingest-is-etl memory):** EXTRACT the model's own lookup-table cells at rest
  (O(params), no forward pass, no probes, no GEMM); TRANSFORM = surrogate-key resolution — hidden
  dims are the source's surrogate keys, embed/lm_head its key-mapping tables, dots/cosine the
  resolution join; LOAD = one signed Glicko match per cell under its tensor-role kind (the TEN
  fixed kinds, restored — the "disease" smear on them was the corruption). POSITIONS AGGREGATE
  (#192 §7): layers/heads are positions of the same schema-bounded table → witnesses on the SAME
  rows; per-position attribution is recipe content. **Records are bounded by the SCHEMA SHAPE
  (~10 logical tables: vocab×d_model, d_model², d_model×interm), never by depth or params** —
  a deeper model adds games, not rows. The token×token bilinear is the QUERY-TIME read (μ-ranked
  joins EMBEDS → interior → OUTPUT_PROJECTS); materializing it at ingest = pre-joining the star
  schema = the 21B-row failure. Nonlinearities = the source's runtime: never attested, never run
  at ingest. Cross-model: channel arenas align via placements (Procrustes/Karcher geometric
  consensus) → sublinear re-witnessing. (Every prior construction in this file's history —
  per-(i,j) pre-join, θ/recall budgets, argmax address books, sign-split/Voronoi/knee set
  emitters, adjudicate-only, probe-the-runtime — is superseded by this and must not return.)
