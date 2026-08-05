# 33 — Walk sign law, mask queue, and read-path audit

Session date: 2026-07-21. Substrate: hart-server `laplace`.

> **STATUS 2026-07-23:** §1–§2 MERGED as PR #559. The voice PRs #597/#599
> (trajectory_corpus.c + trajectory_unpacked_points) work exactly O8's territory
> ("generate() returns empty with 96M points resident") and part of O4 — re-verify
> those two before actioning. O2 (`lower()` sites in converse/chat/converse_walk)
> and O10 (WeightTensorETL refuse block) confirmed STILL OPEN as of this date.
Status key: **FIXED** (in tree, tested) · **OPEN** (evidenced, unstarted) · **CORRECTION** (a
belief that was wrong and must not be re-imported).

---

## 0. The headline

The substrate was never missing the knowledge, the epistemics, or the machinery for
conversation. **One term in one expression hid 99% of the graph**, and everything
downstream looked broken as a result — which is why the answer to "what's missing from
model ingestion / export / SQL to get conversational" turned out to be: almost nothing.

Everything else in this document is optimization and hygiene. None of it was why the
system could not talk.

---

## 1. FIXED — the walk sign law

`laplace_walk_edge_weight` (`engine/core/src/glicko2.c:447`) took its SIGN from
`(rating − 2·rd) − neutral` — the conservative lower bound — then multiplied by
`exp(−κ·rd)`. RD was counted twice, and a *confidence* test was doing a *verdict's* job.

Glicko-2 adjudicates win/draw/loss against neutral 1500, so sign belongs to the rating.
RD is a confidence interval. Because ~90% of consensus cells carry `witness_count = 1`
and therefore a wide RD, the bound fell below neutral for claims that had **won**, and
`generate_walk.c:707` stops placing at the first non-positive score.

Measured over 300,000 consensus rows:

| Glicko verdict | rows | walk scored positive | walk scored negative |
|---|---|---|---|
| WON (rating > 1500) | 300,000 | 2,886 | **297,114 (99.04%)** |

Subject `dog` (1,902 out-edges at time of measurement): **4** walkable under the old
formula, **1,896** under the new. Invisible until the fix: `IS_A canine`, `IS_A mammal`,
`IS_A domestic animal`, `IS_SYNONYM_OF hund / gos / كلب`.

Walk depth curve, before → after:

| depth | before | after | time after |
|---|---|---|---|
| 1 | 2 | 12 | 0.9s |
| 2 | 4 | 107 | 1.8s |
| 3 | 4 | 692 | 5.5s |
| 4 | 4 | **4,016** | 22.7s |

`chat('What is a dog?')` went from *"Dog is a dull unattractive unpleasant girl or
woman."* to *"Canis Familiaris is latin name for dog."*

**Why this is a deletion, not an addition:** the old behaviour was an accidental floor at
`eff_mu ≥ 1500` — the operator-invented floor the substrate invariants forbid. The new
law separates *uncertain* (ranks low via `exp(−κ·rd)` × witness saturation, stays
walkable — a new observation worth verifying) from *refuted* (negative, still
dead-ends). It agrees with `mu/refuted.sql.in`, which already tests the OPTIMISTIC bound
`rating + 2·rd < neutral`.

`eff_mu` remains the correct conservative RANKING key everywhere it orders. Only its use
as a sign/gate changed.

**Sites:** `engine/core/src/glicko2.c:447` plus 7 SQL mirrors so C and SQL cannot
diverge — `generation/consensus_adjacency`, `consensus_layer_plane`,
`consensus_layer_plane_masked`, `consensus_type_plane`, `relation_plane` (×2),
`entity_relation_plane`. Zero `eff_mu(...) − glicko2_neutral_mu()` sites remain.

**Gate:** 405/405 ctest incl. `regress_laplace_substrate` and the `syn_bad` refuted
fixture that guards dead-ending.

---

## 2. FIXED — highway mask: ingest no longer triggers a full rebuild

`ConsensusAccumulatingWriter` tracked the touched-entity set in a client-side
`HashSet<Hash128>` capped at 8,388,608. Any ingest exceeding the cap **discarded the set**
and fell back to `highway_mask_rebuild()` — a full pass over every entity in the
substrate. Every large source therefore paid a multi-minute full rebuild. That is
batch-rebuild-as-a-class, which the substrate forbids: the fold-time refresh is the
intended mechanism and the full rebuild is a rare REGENERATE tool for highway-bit
renumbering only.

It was also **silent**: the procedure used `RAISE NOTICE`, which is client-only, and with
`log_min_messages` at its WARNING default it reached the server log never. The ingest
writer discards notices. A multi-minute rebuild looked like a dead stall on both ends.

**Change:**
- new `schema/tables/highway_mask_dirty.sql.in` — UNLOGGED queue, `id bytea PRIMARY KEY`
- `fold/consensus_upsert.sql.in` queues every subject/object it writes, in the same
  transaction as the cell write — exact, PK-deduplicated, unbounded
- new `highway/highway_mask_drain.sql.in` — batched, COMMIT per batch, rows leave the
  queue only after their masks land, so it is resumable and idempotent; `RAISE LOG` with
  percent and elapsed
- `ConsensusAccumulatingWriter` calls `highway_mask_drain()`; the HashSet, the cap, and
  the overflow/full-rebuild branch are deleted
- `ops/highway_mask_rebuild.sql.in` (still the regenerate tool) now drives batches from
  candidates — consensus participants ∪ already-masked entities — instead of all ~9M
  entities; the second arm preserves the self-healing "clear a stale mask after a
  per-source evict" guarantee that a participants-only scan would break

**Gate:** 405/405 ctest, dotnet 0 errors.

---

## 3. OPEN — defects, evidenced

**O1 — topic selection picks function words.** Newly exposed by the sign fix: with edges
no longer hidden, glue out-masses content. `chat('What can a dog do?')` answers about
*"Latin Small Letter A"*; `chat('Tell me about the moon')` answers about *"The"*.
Root cause is two-fold and neither part is the mass metric:
- `converse/chat.sql.in:58-71` sorts on `exact_hit` (`lower(realize(syn)) = surface`) as
  the PRIMARY key and `char_length(surface)` as tiebreak — two of three sort keys are
  string operations (see O2 and §4).
- `top_synset()` collapses content words to sparse leaves while leaving glue intact:
  `dog` 2,082 edges → `canis familiaris` 7; `can` 477 → `缶` (a kanji); `a` 2,178 →
  unchanged; `do` 975 → unchanged.

**O2 — `lower()` contaminates identity, 6 sites.** `King` (7b59b246…) and `king`
(cdce9588…) are distinct entities, correctly. The bridge is already attested at tier 0
(`K --HAS_LOWERCASE_MAPPING--> k`, a rated Glicko cell; 1,488 lc / 1,505 uc / 1,509 tc)
and the correct native primitive `word_case_variants()` (`src/lexical_case.c`) already
returns King/king/KING. Six sites fabricate the link with a locale string function,
injecting an unrated, unprovenanced identity:
`generation/pos_class_transitions.sql.in:28`, `converse/converse.sql.in:43`,
`converse/converse_walk.sql.in:52`, `converse/chat.sql.in:67` and `:169`,
`inference/translate_to.sql.in:51` (the last as a *join predicate*, which also forecloses
any index).

**O3 — walk latency.** Depth 4 = 22.7s, ~5.6ms/node. Too slow to serve. Highway masks are
**1% populated** (5,292 / 500,000 sampled) after the cancelled rebuild, so mask-gated
band routing cannot prune and `walk_branches` Append-scans every relation-type partition.
Run `highway_mask_drain()` / the regenerate tool first, then re-measure before optimizing.

**O4 — vocab-first corpus scan.** 11 copies of
`DISTINCT ON (p.entity_id) … ORDER BY p.entity_id, p.id LIMIT p_trajs` scan the whole
corpus and apply the vocab filter LAST. `physicalities_constituents_gin` exists on every
partition and is used by exactly one function — `generation/recall_trajectories.sql.in`,
the pattern to copy (`laplace_trajectory_constituent_ids(traj) @> ARRAY[id]`).

| approach | corpus read | time |
|---|---|---|
| scan-then-filter, `p_trajs`=100k (default) | 1.7% | 1,720ms |
| scan-then-filter, `p_trajs`=2M | 34% | 14,900ms |
| GIN vocab-first | **100%** | **84–138ms** |

Deletes `p_trajs` as a concept — with vocab-first there is nothing to sample.

**O5 — layer violation: math above the engine.** The engine is properly built (MKL/cblas
21 files, Eigen 6, TBB 10, AVX intrinsics 5, Spectra 1). C# has **zero** SIMD/intrinsics
anywhere. That is correct where C# orchestrates and wrong where it computes:
- `FoundryExport.cs` — ~123 numeric lines. `BlockOrthonormalizeLeft:1642` calls native
  Gram-Schmidt and, on rank deficiency, falls into a **hand-rolled managed modified
  Gram-Schmidt**. `gram_schmidt.cpp:13` is Householder QR via Eigen. MGS and Householder
  do not produce the same basis under near-collinearity — which is exactly when the
  fallback fires. That is a correctness divergence hiding inside a perf complaint. Also
  `Silu:2006` (duplicates `model_math.cpp`), `Gaussian:2046`, `FillRows`/`FillHead*`.
- `ConsensusAccumulatingWriter.cs` — 0 native calls in 564 lines. NOTE the honest sizing:
  ~500 of those are semaphores, pooling, Npgsql marshalling and async chaining, which
  belong in C#. The actual leak is `BuildDelta` (~47 lines) + `AttestationMergeMath`.
  `BuildDelta` allocates a `Delta` **class** per consensus cell (millions of GC objects)
  and keys a `Dictionary` on a ValueTuple containing a nullable `Hash128`.
- `AttestationMergeMath.ClassifyOutcome:39` duplicates native
  `laplace_attestation_outcome_from_score_fp` against a **second copy of the threshold
  constant** (`Glicko2.ScoreDraw` vs `kScoreHalfFp = 500000000`). They agree today only
  by coincidence; nothing enforces it.

**O6 — plane combinators unreachable from the read path.** normalize / positive-part /
union / degree-cap / PPMI exist only in `FoundryExport.cs`
(`CooFromAdj:1053`…`Union:1105`). `PLANE` — `TABLE(subject_id, object_id, w)` — is already
the universal operand across 13+ producers; the combinators should live behind it with
FoundryExport calling the same implementation.

**O7 — `trajectory_pairs` never built.** The cache, its fingerprint meta, and
`trajectory_pairs_ensure()` are fully specified; the table is empty.

**O8 — `generate()` returns empty** with 96M ordered points resident. Root cause not
isolated. `corpus_trajectory_probe()` returning rows=3 is CORRECT (an O(1) staleness
signature counting rows at `max(observed_at)`, not corpus size), so the fingerprint is not
at fault.

**O9 — index expressions hardcode the μ law.** `consensus_eff_mu_btree` et al. are defined
on `((rating - 2*rd))` rather than `eff_mu(rating, rd)`, so changing the definition
silently leaves three indexes behind. Costs a reindex on a 5.6M-row table.

**O10 — safetensors block-quant refusal.** `WeightTensorETL.cs:41-45` refuses unknown
dtypes including GGUF block-quants. Per operator: it should RECORD, not refuse —
record-don't-interpret. Believed to be an agent-introduced regression, and suspected cause
of model ingest defaulting to GGUF.

**O11 — dead/misleading code.** `glicko2_update()` (`glicko2.c:393`) maps trust to opponent
*rating* with a fixed rd=30 and has **zero callers**; it contradicts the live path and
should be deleted.

**O12 — duplication census (root cause of the above).** The write side has one spine
(`IngestBatchPipeline` → `ConsensusAccumulatingWriter` → `NpgsqlWorkingSetApply`). The
read side has none:

| operation | independent implementations |
|---|---|
| corpus trajectory scan preamble | 11 |
| LATERAL trajectory unpack | 16 files |
| corpus adjacency (same fact) | 3 |
| eff_mu ranking | 94 calls + 5 files inlining `rating - 2*rd` |
| topic ranking | 3 |
| vocab producers | 4 |
| case resolution | 1 native + 6 `lower()` |
| emission / generation | 3 |
| raw SQL literals in deployables | 59 (Cli 23, Mcp 15, OpenAICompat 21) |

---

## 4. Binding law recorded this session

**Hash-space until render.** From prompt-decomposition to the final projection it is
hashes only — indexing, cost, pathing, fanout/hops, mask routing, Glicko weight.
`render`/`render_text`/`realize` and every string operation (`lower`, `char_length`,
surface equality) belong ONLY in the final output projection. A surface comparison in the
middle is simultaneously the language dependence that breaks omni-glottal behaviour, the
identity contamination of O2, and a per-row STABLE call. Hubs are ADDRESSES, not names.

**Truths cluster, lies scatter — already implemented, no new machinery needed.** The
discrimination is emergent from connectivity plus the existing trust differential; it
only failed to operate because the walk could not traverse. Verified chain:
`SourceTrust × RelationTypeRank → laplace_attestation_witness_phi = 350 + (30−350)·w →
staged.PhiFp1e9 → consensus_upsert u.phi → obs.opponent_rd`. For a taxonomic claim,
AcademicCurated gives φ≈105 vs UserPrompt φ≈264 — academic testimony moves a rating ~2.5×
harder per observation. A 1-witness/0-against claim is a NEW OBSERVATION, not a lie: it
must stay walkable to be verified, which the sign fix restores. **Do not build a
corroboration/clustering cache.**

---

## 5. CORRECTIONS — wrong beliefs from this session, do not re-import

1. **"The substrate has no sequence data."** False. Counting `consensus_r_precedes` ≈ 0
   measures a table that is empty BY DESIGN — `TextEntityBuilder.cs:216-249` deletes text
   word-adjacency on purpose. Sequence lives in tier-3 trajectories: 5.88M sentences,
   96.5M ordered points, recovered via `containers_of` + `word_order`.
2. **"The model decomposer emits no token→token edge, that's the gap."** False.
   `APPEARS_IN(token → circuit coordinate)` + the per-circuit packed testimony linestring
   IS the storage; coupling is the query-time join through the shared coordinate. The n²
   tile was removed deliberately (`ModelTokenEdgeETL.cs:684-687`).
3. **"ConceptNet-class knowledge landed."** Wrong attribution, inferred from relation
   names. It was **Atomic2020Decomposer**. Always query `attestations.source_id`.
4. **"The cross-lingual mesh is live via ILI."** Wrong. `dog IS_SYNONYM_OF hund/cane` came
   from **ConceptNetDecomposer** (170) and WordNet (8) — a flat datasource, not the ILI
   hub. **OMW is NOT ingested.** CILI minted 2.2M ILI nodes but little connects language
   lemmas to them.
5. **"The 5 inline `rating - 2*rd` sites de-index."** False. `eff_mu` is a plain inlining
   SQL function that normalizes to exactly the index expression. Folding them is hygiene,
   not performance. (The real fragility is O9, the reverse direction.)
6. **"Client-side accumulation is the documented design, so the C# is fine."** False —
   conflated location with language. "Client-side" means the ingest HOST; both C# and
   native run there. It says nothing about implementation language. See O5.
7. **"VNNI doesn't apply."** Too categorical. VNNI is int8/int16 MAC → int32, so it is
   wrong for the int64 fp1e9 accumulation and wrong for mask AND/popcount
   (`vpternlogq` + `VPOPCNTQ`). But with an all-ones operand `VPDPBUSD` is a horizontal
   byte-sum, which DOES fit SIMD hash-probe match tallying, and would be the right
   instruction for quantized tensor ingest if O10 is fixed.

---

## 6. Measurement discipline (three drifts this session)

- Three separate measurements drifted because a seed or ingest was running mid-measure.
  **Check `pg_stat_activity` before trusting any number.**
- **Killing a psql client does not cancel its backend.** An orphaned aggregate ran 8m44s
  against a live ingest before being noticed. Use `pg_cancel_backend`.
- Verify at the claim's layer: query the live DB or call the installed function. A
  hand-rolled lateral join timed out at 15s where the installed `word_order()` was
  instant.
