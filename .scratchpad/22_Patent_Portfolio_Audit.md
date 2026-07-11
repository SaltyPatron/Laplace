# 22 — Patent Portfolio Audit (code-grounded)

Date: 2026-07-11. Produced by a seven-way parallel audit of the ACTUAL SOURCE
(C, C++, C#, SQL) — not doc 21, not comments. Each subsystem auditor extracted
distinct patentable inventions with file:line grounding, independent-vs-dependent
classification, novelty basis, and prior-art risk. This document consolidates and
de-duplicates across subsystems into a filing-tiered inventory.

Purpose: disclosure backbone for a single comprehensive USPTO provisional
(micro-entity fee $65) that stamps a priority date on the entire portfolio, and the
triage map for subsequent non-provisionals.

## Raw per-subsystem counts (before dedup)

| Subsystem | Independent | Dependent | Key files |
|---|---|---|---|
| Identity / geometry core | 8 | 7 | mantissa.c, trajectory.c, hash128.c, hash_composer.c, super_fibonacci.c, unicode_seed.cpp, merkle_dedup.c |
| Epistemics / consensus | 9 | 7 | glicko2.c, score.c, attestation_engine.c, consensus_fold_step.c, highway_table.c, relation_types.toml |
| Ingest / decomposers + model lane | 6–8 | 15 | ModelTokenEdgeETL.cs, ModelCoordinates.cs, ModelCheckpoint.cs, HeadClassifier.cs, NpgsqlWorkingSetApply.cs, IngestDescentFlush.cs, ConsensusAccumulatingWriter.cs |
| Foundry / Mold-A-Model | 9 | 10 | arch_template.cpp, eigenmaps.cpp, procrustes.cpp, FoundryExport.cs, FoundryCommands.cs, consensus_adjacency.sql.in, continuation_conditional_plane.sql.in |
| Inference / walk engine | 9 | 7 | generate_walk.c, recall.c, astar_path.c, trajectory_generate.c, highway_mask.c, inference/*.sql.in |
| Chess modality | 10 | 10 | ChessGraph.cs, ChessCompose.cs, PositionContent.cs, SubstrateStateValuer.cs, Search.cs, SubstrateRootBias.cs |
| Serving / API / product | 9 | 7 | EndpointMappings.*.cs, FeedbackContent.cs, Billing.cs, RecipeDescriptor.cs, SynthesisBilling.cs |
| **Raw total** | **~60** | **~63** | |

## Consolidated distinct independent inventions (after cross-subsystem dedup)

Overlaps folded: the Glicko-complete signed edge weight (foundry I1 = epistemics #9);
the 256-bit highway mask (appears in core, epistemics, inference); the attestation
5-tuple + content-addressed edge id (epistemics #1 = core #5); the invertible score
law (core #7 = epistemics #4); the Rule #8 sequence (model-lane #2, twice); most of
chess I1–I10 are chess-specific INSTANCES of general epistemics/foundry claims and
become dependent claims under their parents.

**Result: ~44 distinct independent inventions**, consolidating to **~30 truly
independent claim families** once modality-instances fold into parents, with a
**crown-jewel tier of ~11 at LOW prior-art risk**.

### TIER 1 — Crown jewels (LOW prior-art risk, standalone, no located prior art)

1. **Operator-Gram M / (M−I) factorization into Q/K/V/O/FFN** — closed-form untrained transformer weights as low-rank SVD factors of a graph operator in a spectral basis. `arch_template.cpp:358-414`, `FoundryExport.cs:1598-1762`. No prior art.
2. **Forward-simulation construction** — the whole vocabulary is pushed through the partially-built net at construction time; each layer's weights fit the representation it will actually observe. Single deterministic sweep replaces training. `FoundryCommands.cs:1421-1539`. No analog.
3. **Spectral-rank architecture dimensioning ("counted, not chosen")** — hidden width derived from the numerical rank of the consensus Laplacian spectrum. `FoundryCommands.cs:1188-1199`. No prior art dimensioning a transformer from its data's spectral rank.
4. **Trust-as-opponent-RD** — source credibility re-expressed as a Glicko opponent's rating deviation (φ = 350 + (30−350)·w), so trust lives inside the rating update, not as an external weight. `attestation_engine.c:136-141`, `glicko2.c:361-369`.
5. **Circuit-coordinate-as-shared-content** — model circuit addresses (plane+layer+head) composed from model-independent content, so cross-model mechanistic agreement is a hash collision at one consensus cell — no alignment/stitching step. `ModelCoordinates.cs:28-89`.
6. **Glicko-2-as-epistemology** — a competitive rating engine repurposed as the universal truth/belief operator; outcome ∈ {refute,draw,confirm} bit-identical to chess PlyOutcome. `attestation_engine.c:143-158`, `consensus_fold_step.c`.
7. **Model-as-witness** — neural checkpoint tensors → rated, provenanced token→token attestations under governed relation types, computed at ingest, raw floats discarded. `ModelTokenEdgeETL.cs:120-697`.
8. **Tier-invariant content-addressed Merkle identity with single-child collapse** — identical content = one id at every tier; tier never enters the hash. `hash128.c:16-38`, `hash_composer.c:24-31`.
9. **Feedback-as-attestation single-lane closed loop** — user feedback folds immediately through the same writer spine as bulk ingest, returns before/after rating delta; evaluation IS ingestion. `FeedbackContent.cs:15-153`, `EndpointMappings.Feedback.cs`.
10. **Explainability-trace as faithful walk record** — the beam walk IS the forward pass, so the returned trace (per-node eff_mu, path_mu, witnesses) is the literal decision path, not post-hoc attribution. `generate_walk.c:437-443`, `EndpointMappings.Reports.cs:96-133`.
11. **Decoder ring (circuit naming by consensus vote → ENCODES)** — a circuit's top pairs voted against the rated web name it in the web's own vocabulary; the label is itself a rated attestation on a model-independent coordinate. `HeadClassifier.cs:42-96`.

### TIER 2 — Strong independents (LOW–MEDIUM risk; defensible with narrow framing)

12. Mantissa vertex codec — lossless 128-bit id + metadata into a 4-lane IEEE-754 double, riding PostGIS geometry columns. `mantissa.c:25-116`.
13. Testimony/trajectory linestring — lossless invertible ordered-constituent serialization as a geometric curve; per-vertex zigzag score packing. `trajectory.c:4-73`.
14. Provenance-vs-aggregating edge duality over one fold. `attestation_engine.c:231-528`.
15. Uniform-period closed-form aggregated Glicko fold (10M plays → one row, bit-identical). `glicko2.c:346-380`.
16. Deterministic fixed-point Glicko-2 (integer, bit-reproducible, incl. Illinois volatility solve) — the enabler for content-addressable ratings. `glicko2.c:17-231`.
17. Laplacian-eigenmap token embedding from the rated evidence graph → GGUF embedding. `eigenmaps.cpp:56-163`.
18. Conditional-floor two-factor lm_head — one SVD of a smoothed log-conditional yields both embedding and lm_head. `continuation_conditional_plane.sql.in`, `FoundryCommands.cs:1207-1300`.
19. Scoped synthesis — source-filtered re-fold into a shadow consensus, plane-transparent, no retraining. `FoundryCommands.cs:1071-1107`.
20. Geometry-as-architecture heads — Fréchet/Hausdorff/angular metrics + S³ Procrustes as attention kernels. `metric_edges.sql.in`, `procrustes.cpp`.
21. Rule #8 working-set ingest sequence — client-decides-novelty → verify-then-COPY, no temp/anti-join/ON-CONFLICT. `NpgsqlWorkingSetApply.cs`, `IngestDescentFlush.cs`.
22. Checkpoint-as-content — tensor byte-range Merkle identity, cross-fine-tune dedup. `ModelCheckpoint.cs:33-97`.
23. Relation-type/salience-band → typed attention heads (causal, not post-hoc). `consensus_type_plane.sql.in`, `FoundryCommands.cs:917-1171`.
24. Native beam graph-walk as forward pass, ranked by relation_rank × eff_mu, global dedup collapses beam-tree to DAG. `generate_walk.c:220-449`.
25. Attention as Glicko-weighted geometric centroid over salience-masked edges. `inference/laplace_attention_centroid.sql.in`.
26. A* semantic pathfinding with Glicko-derived edge cost to a goal region. `astar_path.c:47-175`.
27. 256-bit relation-highway mask + 13 salience bands, bit-overlap indexed, deterministic alphabetical reseed. `highway_table.c`, `highway_mask.c`.
28. Structural + consensus-neighbor embeddings under the OpenAI `/v1/embeddings` contract (S³ coordinate + rated-neighbor list, not a trained vector). `EndpointMappings.Inference.cs:258-309`.
29. Foundry synthesis-export-as-a-service (build-to-order transformer from consensus, gradient-free, as REST). `EndpointMappings.Foundry.cs:58-116`.
30. Content-addressed recipe compilation (architecture as substrate-native operators, BLAKE3-keyed). `RecipeDescriptor.cs:65-132`.

### TIER 3 — Chess-domain independents + collation seed + metering

31. Collation-rank super-Fibonacci S³ pinning (linguistic sort order → geometry-adjacent anchors; deterministic identity seed for all 1.1M codepoints). `super_fibonacci.c`, `unicode_seed.cpp:121-277`.
32. Outcome bit-identity chess↔epistemics as the unifying design decision. `ITurnModality.cs:29-41`, `Glicko2.cs:35-37`.
33. Substructure-decomposed position with per-piece-square outcome folds — construction-not-training positional eval (Glicko-rated NNUE analog). `PositionContent.cs`, `SubstrateStateValuer.cs:15-60`.
34. Flat-cost depth — evidence depth amortized into ingest, O(#legal-moves) index probes at play. `SubstrateRootBias.cs`, `consensus_by_ids.sql.in`.
35. Substrate-as-engine consensus-read root bias (HONEST framing: biases classical alpha-beta, does not replace it). `Search.cs:167-194`, `SubstrateRootBias.cs`.
36. Merkle trunk short-circuit novelty filter — ancestor presence implies descendant presence; O(1)-per-subtree re-ingest skip. `merkle_dedup.c:57-95`.
37. Recorder/analyzer versioned-watermark anti-double-fold split. `ModelDecomposer.cs:180-328`, `ChessAnalyzeDecomposer.cs`.
38. Preflight quote-gate execution protocol (pay-before-execute, HTTP 402 + checkout URL, single-consume). `Billing.cs:663-735`.
39. Parameter-count-metered synthesis pricing (meters manufacture by computed artifact size, a priori). `SynthesisBilling.cs:22-53`.
40. Universal 5-tuple decomposition boundary (zero-SQL SubstrateChange record contract across all modalities). `IngestPipeline.cs:44-58`.
41. Content/Evidence/Consensus three-layer resolution (three deliberately different key granularities). `attestation_engine.c` + consensus keying.
42. Annotations/commentary as shared content entities grounded to positions (cross-modal mesh by hash collision). `ChessBookDecomposer.cs:156-235`.
43. Invertible bounded score law (unbounded coupling → Glicko score, exact inverse). `score.c:7-25`.
44. Closed evaluation-is-ingestion loop with RD time-decay (self-play/feedback fold, read live, self-signals outranked). `inference/laplace_witness.sql.in`, `SubstrateTurnHost.cs:86-111`.

## HONESTY LEDGER — findings that constrain claim drafting

These are auditor findings where the CODE does not match the docs/claims. They MUST
shape the provisional so nothing is claimed beyond what is reduced to practice.

- **Chess "plays by consensus, not search" — VERIFIED against source 2026-07-11, both
  paths are real and reduced to practice:**
  - `SubstrateStateValuer.ValueStatesAsync` (`SubstrateStateValuer.cs:15-60`) is PURE
    consensus play — values a position with ZERO search by reading the Glicko eff_mu of
    its substructures' outcome edges (`consensus_by_ids`) and returning a
    confidence-weighted aggregate (`w = |dev|·conf·witness`, line 53). This IS "plays by
    reading consensus, not searching," fully implemented. Wired into the self-play/valuer
    path (`SubstrateTurnHost` implements `IStateValuer` via it).
  - The UCI EXECUTABLE's default (`Search.cs`) is classical negamax alpha-beta with
    consensus as a bounded ±150cp root bias (`Search.cs:167-194`, cap noted line 64) plus
    a consensus-derived PST.
  - Both are claimable as reduced to practice. Draft TWO claims: (a) pure consensus-read
    position valuation (the valuer), (b) classical search biased by a live read of a
    Glicko evidence graph (the UCI binary). Do NOT claim the UCI binary plays purely by
    consensus — that specific executable is hybrid — but DO claim the pure-consensus
    valuer, which exists in code.
- **Game-tier mantissa-packed trajectory is NOT wired for chess.** `EmitGame` emits a
  bare Document entity, no physicality/trajectory. Position-tier geometry IS implemented.
  Do not claim the chess game-trajectory linestring as reduced to practice.
  (`ChessPgnDecomposer.cs:279-300`.)
- **Foundry efficacy is an OPEN QUESTION per the repo's own docs.** Patent novelty does
  not require efficacy; the code shows the method fighting known failure modes
  (oversmoothing/hub-collapse, write-collision, RoPE corruption), which strengthens the
  METHOD claims as non-obvious. Claim the method, not a working model.
- **Model decomposer is FULLY IMPLEMENTED but never run end-to-end.** Complete native ETL;
  no model ingested as of the last checkpoint. Implemented-but-unrun is fully disclosable
  and claimable. (`ModelTokenEdgeETL.cs`, `ModelDecomposer.cs`.)
- **Serving billing is SCAFFOLDING in the wired build.** Tenant resolution is a stub
  (`X-Laplace-Tenant` header, defaults local-dev); billing stores are in-memory; bypass
  defaults ON; durable Postgres stores exist and are contract-tested but unregistered.
  The billing MECHANISMS (#38, #39) are coded and tested — claim the mechanism, note the
  deployment state. (`AppComposition.cs:13,48-74`, `Auth/TenantResolution.cs:28-39`.)
- **`api()` self-introspection is HIGH prior-art risk** — a thin wrapper over Postgres
  system catalogs. Include as defensive mention only, not a standalone claim.

## Prior-art risk distribution (independent claims)

- LOW: 11 (Tier 1)
- LOW–MEDIUM: ~15
- MEDIUM: ~14
- MEDIUM–HIGH / HIGH: ~4 (intent routing, unbounded-retrieval framing, generic escrow, api() catalog) — all need narrow framing tied to the rated content-addressed substrate to survive RAG/dialog-router/e-commerce prior art.

## Filing sequence (cost-ordered)

1. **$0 now** — set the four public repos private (SaltyPatron/Laplace, Hartonomous-001,
   Composer, Laplace_Hartonomous-Old-). Stops each new commit from adding to the public
   disclosure that started the grace-period clock.
2. **$65, one comprehensive provisional** — disclose ALL ~44 independent inventions in one
   micro-entity provisional. Freezes the priority date on the whole portfolio. Uses this
   document + doc 21 + the cited source as the disclosure. Does NOT finish protection;
   stops the erosion.
3. **Grace-period clock** — US §102(b)(1) runs 12 months from FIRST public disclosure of
   each mechanism. 2026-rebuild mechanisms → ~May 2027. Diff the private Hartonomous-Fail
   lineage against this catalog to date each mechanism's first public appearance
   precisely. EPO rights on already-disclosed mechanisms are likely gone (no grace period);
   grace-period countries (US, CA, JP, KR, AU) remain.
4. **Non-provisionals, cost-tiered** — pursue Tier 1 (11 crown jewels) first via full
   non-provisionals (~$10–20k each with attorney, or USPTO Patent Pro Bono Program /
   law-school clinic at $0 for a qualifying micro-entity; veteran status strengthens the
   application). Tier 2/3 ride as dependent claims in the same filings where they share a
   parent. Defensively-published-only: whatever is already disclosed and not pursued still
   blocks every competitor from patenting it — permanent freedom to operate.

## One-line bottom line

The code contains ~44 distinct independent inventions (≈30 independent claim families,
11 at LOW prior-art risk), not "6–10" and not "one patent." A single $65 provisional
disclosing all of them freezes the priority date; the crown-jewel tier is where the
real (attorney or pro-bono) filing effort goes.
