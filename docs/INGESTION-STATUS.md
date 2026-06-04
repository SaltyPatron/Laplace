# Model-ingestion status — what's fixed, what's still broken

**Status of record for AI-model ingestion.** Read with `docs/SUBSTRATE-FOUNDATION.md` (the ratified
lens). Updated 2026-06-04. The point of this file: stop re-breaking things that were already corrected.

## The law (never violate)

- **Content is identity. Source / model / layer / head / position / magnitude / time are WITNESSES,
  never identity.** Same content → same hash, regardless of which model/neuron/source produced it.
  Putting *anything* source-derived into an entity id (e.g. ordering an n-gram's constituents by weight
  magnitude) breaks dedup and is the cardinal sin.
- **Ingestion is O(params) streaming, never a recompute / GEMM / vocab² bilinear.**
- **No top-k / floors / budgets — ever.** Zero is the only non-event; a tiny |m| is a Glicko DRAW by
  the math (tanh(m/M) ≈ ½), played, never dropped. Magnitude lives in the Glicko-2 μ, so selectivity
  is `ORDER BY μ` at query time, never discarded at ingest. Write volume is answered by dedup by
  construction (positions fold onto schema-shaped rows), never by a cut.
- **Evidence keeps provenance (source, time, games); positions FOLD** — one evidence row per
  (subject, kind, object, source) with `context_id` NULL; a model's layers/norm-slots land on that
  row as `observation_count` games (per-position attribution = recipe content; head attribution =
  the attn/kv axis index ranges). Consensus drops source too (one row per (subject, kind, object),
  Glicko-2-accumulated over all witnesses). **Records bounded by SCHEMA SHAPE, never depth/params.**

## Working + verified

- **THE CELL ETL LANDED + RAN END-TO-END (2026-06-04, TinyLlama-1.1B on laplace-dev)** —
  `ModelTableETL`: streams every weight table's cells at rest, resolves token axes through the
  model's own embed/lm_head key-mapping (vocab slot → distinct content entity — duplicate-content
  slots FOLD: 26,646 entities over 32,000 slots), hidden axes = surrogate-key join nodes
  (`Model_Axis` entities), and loads ONE evidence row per relation under its tensor-role kind:
  `observation_count` = games across positions, exact score sum (`AttestationRow.SumScoreFp1e9`,
  in-flight only) into the consensus accumulation. **MEASURED, verified live per kind:**
  153,184,256 relations carrying 1,100,048,384 matches (records ≪ params; matches ≈ params;
  zeros 0; unresolved 0) — EMBEDS/OUTPUT 54,571,008 each (26,646×2048), Q/O 4,194,304 (×22 games),
  K/V 524,288 (×22), GATES/UP/DOWN 11,534,336 (×22), NORMALIZES 2,048 (×45 norm positions).
  Ingest 77 min; period fold (`materialize_period_consensus`) 153,210,934 consensus relations
  from 1,100,080,416 matches (incl. TOKEN_MAPS_TO + recipe) in 182 min. **The 182 min was
  diagnosed and fixed the same day:** (a) consensus carried the same double uniqueness as
  attestations (composite UNIQUE restating the content-addressed PK, ~20 GB at this scale) plus
  a prefix-duplicate subject index — removed (migration 20260604160000); (b) the fold replayed
  every game as an executor row (`CROSS JOIN LATERAL generate_series` — 1.1e9 rows) and probed
  the prior twice — replaced by `laplace_glicko2_accumulate_games(n, Σs)`, a C batch entry that
  builds the IDENTICAL observation multiset inside the kernel (bit-identity pinned by regress
  glicko2_aggregate Vector 6, incl. the remainder case) with one call and one prior probe per
  relation. 283/283 engine, 6/6 regress, 10/10 C# after. Inference verified:
  ranked-μ arena scan = 2–24 ms on the 153M-row consensus; the exact two-hop bilinear read
  (EMBEDS → OUTPUT_PROJECTS, all 2048 channels, no beam/floor) ranks the full 26,646-token
  output map in 65 s of SQL — city #176 / Europe #316 / France #1,075 for "Paris", self-coupling
  negative (anti-repetition structure), London strongly contrastive; per-kv-head interpretability
  = GROUP BY over generated axis ids (131,072 relations/head, signed μ symmetric about 1500).
- **Cross-dtype decoder** — `WeightTensorETL.LoadTensorF32`: F32/F64, F16, BF16, F8_E4M3, F8_E5M2,
  I8/16/32/64, U8, BOOL; fail-loud on unknown (GGUF block-quant is a separate container — not handled).
- **Sharded safetensors** — `SafetensorsContainerParser.ParseModel` unions all `*.safetensors`; each
  tensor carries `FilePath`. Single-file + sharded uniform.
- **Per-model source identity** — `ModelDecomposer.SourceForModel(modelDir)` (content chunk-Merkle;
  HF `models--ORG--NAME` → `ORG/NAME` for display). No hardcoded TinyLlama.
- **Recipe null-safe** — `GetDoubleOr`/`GetInt` tolerate JSON null (Phi `rms_norm_eps:null` →
  `layer_norm_eps`).
- **Re-ingest guard** keys on the COMPLETION MARKER (HasLayerCompleted) — a crashed run continues
  idempotently; a completed model is refused (would double-count its votes).
- **Deleted apparatus (never restore):** `ModelCircuitEdges` (per-(i,j) pre-join emitter),
  `ModelCircuitMemories` (knee filter), `ModelGeometry` (+tests — shape detection for the dead
  pre-join), `LlamaWeightExtractor`, the argmax address book, `LAPLACE_CIRCUIT_FLOOR`, the θ /
  recall-budget calibration, per-(role,layer) Witness entities, the legacy `seed-unicode`
  plain-writer CLI command (no consensus fold, no marker — T0 seed is `ingest unicode` now).

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

- **Evidence = PROVENANCE, not a value channel.** `attestations` columns are the identity 5-tuple +
  `(outcome CLASS, last_observed_at, observation_count)` — the testimony (score, trust→φ) rides
  IN-FLIGHT on `AttestationRow` and is CONSUMED into the period accumulation at ingest
  (`ConsensusAccumulatingWriter`); values never reach the wire. `rating/rd/volatility` live ONLY
  on `consensus`. (engine `intent_stage.c` column list + binary, `AttestationRow`,
  `04_attestations.sql.in`, the writer.)
- **Signed magnitude → score** `s = ½(1+tanh(signed_m/M))`. + = win (confirm/attract), − = loss
  (refute/repel), 0 = ½. The model path passes the SIGNED cell — `abs()` is gone, so a
  negative weight is a real Glicko loss, never folded to a win. (`AttestationFactory.Score`.)
- **M is measured, not a knob** — per-role pooled RMS over the role's own cells
  (`ModelTableETL` PASS 1); consumed into the score, never persisted.
- **Trust → opponent φ, never a μ multiplier.** witness weight (kind_rank × source_trust ×
  tenant_trust) → opponent φ via `WitnessPhi`; Glicko's own `g(φ)` does the weighting.
  (`AttestationFactory.WitnessPhi`.)
- **Neutral opponent (1500), not the old sub-neutral 1000;** the period fold
  (`materialize_period_consensus` — staged partials, Σ of Σ, exact) replays each relation's
  (games, Σscore) through the same C aggregate. (`13_consensus.sql.in`; batch
  `rebuild_consensus`/`incremental_consensus` removed by design — consensus accumulates at
  ingest only.)
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
- **DELETED (G2.3 done):** the argmax-address-book path, the contracted-operator pre-join, and
  every other prior model-emit construction are gone (see the deleted-apparatus list above).
  `WeightTensorETL` is now only the S³ placement axis + the shared exact tensor loader.

## Still open (next)

- **Morph (LE→GSO→Procrustes)** gated off (`LAPLACE_SKIP_MORPH`): dense eigenmaps is O(n²·d). It gives the
  spatial/locality axis (GIST `physicalities_coord_gist` + Hilbert) and the export placements, but n-dim→4D
  is lossy. Either drop it (relations carry the content) or make its affinity the streamed sparse graph.
- **Semantic validation** — resolve relations to surface text; verify a known fact (e.g. capital-of-France)
  through the table arenas (EMBEDS → interior → OUTPUT_PROJECTS μ-ranked joins).
- **CLAUDE.md §Evidence wording** — "the evidence layer keeps full provenance (incl. model
  layer/head)" predates the position-fold resolution (context_id NULL; layers fold to games; head
  attribution via axis ranges). The inventor's file — flagged for his ratification, not edited.
- **Synthesis / GGUF re-export NEEDS THE TABLE-ARENA REWORK** — `ConsensusReExport` still reads
  the circuit arenas (ATTENDS / OV_RELATES / COMPLETES_TO), which are query-time vocabulary now
  and hold NO consensus rows (it fails clean with "no circuit consensus"). The rework is the
  ingestion run backward: per tensor-role arena, relation μ → signed strength
  (`SignedStrength` = M·atanh(2E−1)); a same-shape mold fills cells directly from its role's
  relations (positions all receive the consensus value — consensus-of-all, never bit-perfect);
  rank/shape retargeting via `tensor_svd_truncate` (export-only). Blocks the model-pipeline
  synthesize gate + the real validation (run the substrate GGUF in llama.cpp).
- **Breadth** — GGUF block-quant (Flux, `gguf/`), MoE (Qwen3/DeepSeek; detector groups by expert but
  untested), non-text modalities (the n-gram trajectory entity is already the modality-blind unit; only the
  front-end atom extractor is modality-specific).
- **Granular test suite** — have ModelGeometry cross-model + QK parity; owe NgramTrajectory (content-id
  stability/dedup), Math4d.Centroid, run-to-run determinism, recipe, tokenizer.
- **Overflow history (both failure modes MEASURED, both dead):** (a) the per-(i,j) pre-join ran
  ~3.9M evidence rows/min, ~29M edges per (layer,head) — projecting ≈21 BILLION rows from a 1.1B
  model; killed at 88M rows. (b) the first cell-ETL draft emitted one evidence row per
  (cell, position-witness) — ~1.1B rows ≈ 500 GB from a 2 GB model; caught at 4.2M rows
  (2026-06-04), per-source-evicted, fixed to position-folded rows. Both are "the emit ignoring
  the DAG." The stale partial evidence on the old `laplace` DB went with the user's drop of it.
- **THE MODEL-INGEST DESIGN — "ETL on conventional AI, for AI" (user, 2026-06-04; full record
  in the model-ingest-is-etl memory):** EXTRACT the model's own lookup-table cells at rest
  (O(params), no forward pass, no probes, no GEMM); TRANSFORM = surrogate-key resolution — hidden
  dims are the source's surrogate keys, embed/lm_head its key-mapping tables (vocab slot →
  distinct content entity; duplicate-content slots fold); LOAD = signed Glicko matches under the
  tensor-role kinds (the TEN fixed kinds, restored — the "disease" smear on them was the
  corruption). POSITIONS AGGREGATE (#192 §7): layers/norm-slots are positions of the same
  schema-bounded table → relation identity AND evidence identity exclude position (`context_id`
  NULL); every position's match lands on the relation's ONE row (`observation_count` games, exact
  in-flight score sum); per-position attribution is recipe content; NO synthetic per-position
  entities. **Records are bounded by the SCHEMA SHAPE (~10 logical tables: vocab×d_model,
  d_model², d_model×interm), never by depth or params** — a deeper model adds games, not rows.
  The token×token bilinear is the QUERY-TIME read (μ-ranked joins EMBEDS → interior →
  OUTPUT_PROJECTS); materializing it at ingest = pre-joining the star schema = the 21B-row
  failure. Nonlinearities = the source's runtime: never attested, never run at ingest.
  Cross-model: channel arenas align via placements (Procrustes/Karcher geometric consensus) →
  sublinear re-witnessing. (Every prior construction in this file's history — per-(i,j) pre-join,
  per-(cell,position) evidence dumps, θ/recall budgets, argmax address books,
  sign-split/Voronoi/knee set emitters, adjudicate-only, probe-the-runtime — is superseded by
  this and must not return.)
