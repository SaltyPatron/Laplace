# Verified code audit — 2026-06-17

**This is a verification ledger, not authority.** Every claim below cites `file:line`. It is
trustworthy only insofar as you re-read the cited code. It was produced by reading the actual
source this date — NOT from code comments, docs, or prior notes. Where a figure exists only in a
comment (unverified by execution), it is marked `[COMMENT]`. Corrections to earlier comment-sourced
claims are marked `[CORRECTED]`.

How to defeat drift/sabotage with this file:
- It is git-tracked — `git log -p docs/VERIFIED-CODE-AUDIT-2026-06-17.md` shows any later edit; revert with git.
- Delete it freely (`rm`); it is a new standalone file, nothing depends on it.
- The load-bearing invariant (fold == replay, determinism) is already pinned by a test
  (`test_glicko2.cpp` FoldUniformMatchesObservationLoop) — code-level sabotage of it trips that test.

---

## Thesis
A transformer replaced by a content-addressed evidence database. Identity = BLAKE3-128 over
canonical bytes; geometry = unit-S³ coordinates with 212-bit identity packed into PostGIS vertices;
"training" = per-relation Glicko-2 rating of witnessed claims in int64 fixed point; "inference" =
ranked B-tree probe over the conservative rating (`rating − 2·rd`), or a walk down it.

Pipeline: `bytes (text|code) → grapheme floor + tier tree → content-addressed entities + trajectories
→ witness attestations (Glicko games) → consensus fold (one rating period/relation) → consensus table
→ { eff_mu index read | trajectory walk | GGUF export }`.

## 1 — One identity space (text + code)
- `tier = max(child_tier)+1`, cap 255 — `grammar_compose.cpp:137`. Graphemes tier 1 (`:295`), word floor tier 2 (`:163`).
- Composite id = `BLAKE3(0x01 ‖ ordered child-ids)` — `hash128.c:16-26`. Tier is NOT in the hash (`(void)tier`).
- Unary node adopts child id — `hash_composer.c:24-25`. Emission dedups on a seen-set — `grammar_compose.cpp:207-224,:311`.
  ⇒ "dog" as paragraph/sentence/word is ONE entity; collapse law = content-addressing, not a rule.
- Type ids = hashed strings: `BLAKE3("substrate/type/<CANONICAL>/v1")` — `relation_law.c:179-185`;
  grammar nodes `…/grammar/<modality>/<nodetype>/v1` — `grammar_compose.cpp:20-28`. No id tables.

## 2 — Geometry (two channels, one exact)
- Unit S³ coord: `r=√(s/n)`, `R=√(1−s/n)`, `r²+R²=1` — `super_fibonacci.c:17-25`. Parent coord = centroid of children — `hash_composer.c:29`.
- Trajectory vertex = 4 doubles, exponent pinned `0x3FF`; 212 payload bits = 128 hash + 16 ordinal + 16 run_length + 52 flags; exact pack/unpack — `mantissa.c:45-69,:118-138`.
- Same law stores a testimony walk: zigzag Glicko score in flags, games in run_length — `mantissa.c:79-99`.
- Hilbert key: each axis quantized `[-1,1]→uint32` (LOSSY) — `hilbert4d.c:10-16`; `memcmp` = curve order — `:122-124`.
  ⇒ identity packing is exact; Hilbert is a separate lossy sort key.

## 3 — Witnesses → games
- Surface relation → `(type_id, rank, symmetry, flip)` — `attestation_engine.c:222-223`.
- `φ = 350 − 320·w` — `:14-15,:106-111`, where `w = rank × trust` — `:242,:296`. [CORRECTED: earlier "trust→φ" dropped the rank factor.]
- Outcome pivots at score 0.5: confirm/refute/draw — `:113-119`.
- Attestation id = `BLAKE3(subject ‖ type ‖ object ‖ source ‖ context)` (80 bytes) — `:80-104`. Per-source row ⇒ re-ingesting a source ADDS games.
- Symmetric relations: canonical endpoint order by hash compare — `:70-76`.
- Word order = relation: consecutive siblings emit `PRECEDES(a,b)`, games-counted — `grammar_compose.cpp:399-432`.

## 4 — Rating core (the "weights")
- Glicko-2 in int64 fixed point ×1e9, `__int128` products — `glicko2.c:17-36`; integer isqrt `:38-65`, 14-term exp `:67-96`, atanh log `:98-121`; g/E/Illinois solve `:149-231`. No libm.
- `eff_mu = rating − 2·rd` — `:433-435`. `refuted = rating + 2·rd < neutral` — `13_mu_law.sql.in:20-23`.
- RD inflates per empty period (decay) — `:406-427`. Initial RD 350, vol 0.06, τ 0.5 — `13_mu_law.sql.in:30-35`.

## 5 — The fold (deterministic, lossless)
- One Glicko period per relation from staged `(games, sum_score)` — `glicko2.c:346-380`:
  `v_inv = games·fp_mul(g_sq,E_1mE)`; `q=sum/games`, `rem=sum−q·(games−1)`;
  `delta_inner=(games−1)·fp_mul(g_j,q−E_j)+fp_mul(g_j,rem−E_j)`.
  Summing N identical rounded int64 terms = integer multiply, and `q·(games−1)+rem==sum` exactly
  ⇒ bit-identical to replaying every game, lossless.
- Both lanes call one shared `glicko2_finish_period` — `:183-270` (from `:325` and `:378`).

## 6 — Storage & inference
- `consensus(id PK = BLAKE3(subj‖type‖obj|zero), subject, type, object, rating, rd, volatility, witness_count, last_observed)` — `12_consensus_schema.sql.in:1-11,:34-41`.
- Read = ranked index probe: `completions/consensus_out/top_relations` `ORDER BY eff_mu(rating,rd) DESC LIMIT k` — `17_consensus_reads.sql.in:12-31`,
  served by `consensus_subject_eff_mu_btree (subject_id, ((rating-2*rd)) DESC)` — `12:30-32`.
- Beam walks `walk_strongest/walk_branches` native C over the same metric — `17:44-58`.

## 7 — Generation & variants
- Walk kernel deterministic given a seed; endpoint substitutes `floor(random()*9e18)` when no seed — `26_generation.sql.in:826`.
- `variant_synth.c:89,:339` use `ORDER BY random() LIMIT 1`.
  ⇒ both surfaces BREAK the repeatable invariant. [VERIFIED defect]

## 8 — Export
- Reads folded consensus by `eff_mu` (no re-fold). Exact readout `FillCoordHead` — `FoundryExport.cs:1341`: q/k select the 4 coord dims ⇒ `q·k = cos` on S³.
- `ReportMetricHeadFidelity` — `:448-468` is real code measuring direct-frame vs factored recall. The "100% vs 8%" is `[COMMENT]` at `:1338-1339` (harness real, numbers not executed here).
- Operator path: MKL SVD / Laplacian-eigenmap bases (lossy). `FactorSparseRandomized` is CALLED by `FactorAdjacency` at `:924` — [CORRECTED: earlier "dead code" was wrong]; the randomized Halko sketch is reachable and violates the lossless/deterministic ideal the rigid frame already meets.

## Verified defects (code, not comments)
1. Endpoint generation nondeterministic without a seed — `26_generation.sql.in:826`.
2. Variant synthesis nondeterministic — `variant_synth.c:89,:339`.
3. Randomized SVD export path reachable — `FoundryExport.cs:924`.
4. Head-fidelity figures are comment-asserted (measuring code is real) — `FoundryExport.cs:1338-1339` vs `:448-468`.

## Not opened this session (no claims made)
C fold engine k-way merge internals (`consensus_fold_engine.c`), ingest staging merge
(`SubstrateStagingMerge.cs`), endpoint host, perfcache loader.
