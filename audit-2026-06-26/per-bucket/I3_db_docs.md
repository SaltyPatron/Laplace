# Bucket: I3_db_docs (db/migrations + docs/)

### Files read
- [x] `db/migrations/.gitkeep` — empty placeholder (1 blank line).
- [x] `db/migrations/20260606000000_layer1_database.sql` — extensions + role grants only.
- [x] `db/migrations/20260611000000_app_billing.sql` — app-schema billing tables (low priority per charter).
- [x] `docs/CAPABILITY_VALUATION.md` — valuation/marketing doc.
- [x] `docs/DYNAMIC_VOCAB_AUDIT.md` — prior Claude audit; **central finding A2 is now STALE**.
- [x] `docs/FOUNDRY_SYNTHESIS_FINDINGS.md` — prior investigation; **root-cause #1 now STALE**.
- [x] `docs/bench/checks.sql` — clean reusable state-check script.
- [x] `docs/bench/empirical-ingest-log.md` — honest measured log; contradicts the "ground-truth" doc on ON CONFLICT.
- [x] `docs/bench/hilbert_bulk_insert_bench.sql` — clean repro bench.
- [x] `docs/bench/hilbert_range_partition_bench.sql` — clean repro bench.
- [x] `docs/bench/hilbert_scale_insert_bench.sql` — clean repro bench.
- [x] `docs/bench/sample_activity.sql` — clean activity sampler.
- [x] `docs/convergence-index-and-inference.md` — design model; self-labeled target; corruption section ACCURATE to code.
- [x] `docs/ingestion-write-path-architecture.md` — labeled "ground truth" but partly aspirational.
- [x] `docs/refounding-vocabulary-on-content-addresses.md` — model doc; EntityTier.Vocabulary=5 claim ACCURATE.

Cross-checked code: `WitnessConstants.cs`, `EntityTier.cs`, `engine/manifest/relation_types.toml`,
`extension/laplace_substrate/src/apply_batch.c`, `NpgsqlSubstrateWriter.cs`, `FoundryCommands.cs`,
`IliMap.cs`, `ConceptAnchor.cs`, `scripts/laplace-bench.sql` (exists).

---

## Findings

### F1 — `docs/DYNAMIC_VOCAB_AUDIT.md:26-41` (§A2) — STALE: the "catastrophic 0.83 drift" is already fixed
- SEVERITY: MEDIUM · CATEGORY: other (stale-doc) · CONFIDENCE: high
- CLAIM in doc: C# `RelationTypeRank` (`WitnessConstants.cs`) is STALE/drifted —
  `StandardsStructural = 0.91` (vs manifest 0.08, "**0.83 — catastrophic**"), `Taxonomic = 0.82`
  (vs 0.90), `Equivalence = 0.55` (vs 0.82). "Action: delete the C# table or regenerate it."
- VERIFIED: Read `app/Laplace.Decomposers.Abstractions/WitnessConstants.cs:11-23`. The table now
  reads `Taxonomic=0.90`, `Equivalence=0.82`, `StandardsStructural=0.08`, `Definitional=0.97`,
  `Mandate=1.00` — i.e. it **already matches** `engine/manifest/relation_types.toml:7-19` exactly
  (verified manifest values). The file's own header comment (lines 3-8) documents the fix:
  *"They were STALE after the 'semantic salience' recalibration … Realigned to the manifest bands here."*
- IMPACT: The doc's headline finding and its prescribed action are obsolete. A reader trusting it
  would "fix" an already-fixed table. The doc is a snapshot of a now-closed debt, not current state.

### F2 — `docs/FOUNDRY_SYNTHESIS_FINDINGS.md:20-27,104-132,197-201` — STALE: band-misalignment ("THE killer", root cause #1) already fixed
- SEVERITY: MEDIUM · CATEGORY: other (stale-doc) · CONFIDENCE: high
- CLAIM in doc: synthesis builds embedding/operators from bands `sim[0.50–0.60]`, `att[0.30–0.50]`,
  `pre[0.60–0.68]`, `rel[0.68–0.86]` (cited `FoundryCommands.cs:521-527`). The 0.86 ceiling
  **excludes IS_A (0.90) and HAS_DEFINITION (0.97)** — "taxonomy's strongest edges … dropped",
  declared root-cause #1 "THE killer". §6 fix #1: "Raise the top ceiling to ≥0.97".
- VERIFIED: Read `app/Laplace.Cli/FoundryCommands.cs:523-541`. The bands are now
  `sim=LayerAsync(0.78,0.87)`, `rel=LayerAsync(0.70,1.001)`, `pre=LayerAsync(0.55,0.70)`,
  `att=LayerAsync(0.30,0.52)`. The `rel` band top is **1.001**, and its inline comment reads
  *"partitive+taxonomic+definitional+mandate → V/O taxonomic routing"*. So IS_A (0.90),
  HAS_DEFINITION (0.97), and mandate (1.0) are now **included** — the exact fix the doc prescribes.
- NOTE: also the path moved — `FoundryCommands.cs` lives at `app/Laplace.Cli/FoundryCommands.cs`,
  not the doc-cited `app/Laplace.Foundry/...` (build-a-bear/main-path line numbers are all shifted).
- RESIDUAL TRUTH: `PRECEDES (0.18)` is still below the `att` floor (0.30); but the doc itself notes
  PRECEDES enters via `word_order`/`traj`, and code `:676` builds `completion = Union(pre, traj)`.
  So the secondary claims are partially intact; the *primary* root cause is closed.
- IMPACT: The doc's TL;DR ("THE killer") and §3 band table misrepresent the current code. Whoever
  reseeds/repours trusting this would re-fix a closed bug.

### F3 — `extension/laplace_substrate/src/apply_batch.c:16-22` vs `:155-170` — self-contradictory header comment + ON CONFLICT violates charter/doc invariant
- SEVERITY: HIGH · CATEGORY: invention-violation / correctness (doc-defect in code comment) · CONFIDENCE: high
- The apply_batch header (lines 16-22) claims physicalities dedup uses a **`(entity_id, type)` UNIQUE**
  anti-join and "**NO ON CONFLICT**": *"anti-join BOTH the id PK and the (entity_id, type) UNIQUE.
  NO ON CONFLICT."* The actual body (lines 155-170) directly **contradicts** it:
  *"dedup key is the id PK ('dedup is the hash' — **no (entity_id,type) unique exists**)"* and emits
  `ON CONFLICT (id) DO NOTHING` (lines 137, 170). The header is stale/wrong — a defect per the charter
  (prose disagreeing with code is itself a bug).
- Separately: the **code's use of `ON CONFLICT (id) DO NOTHING`** as the cross-batch dedup
  (lines 121, 137, 156-157, 170) contradicts charter invariant #7 and
  `docs/ingestion-write-path-architecture.md:79-81` ("**no lookup, no anti-join, no `ON CONFLICT`** —
  the set is already novel; checking again is the mistake"). The descent is supposed to prove novelty
  so the insert needs no arbiter. The shipped insert still relies on ON CONFLICT.
- VERIFIED: grep of `apply_batch.c` (header + insert bodies). This is the architecture doc and the
  code disagreeing — one of them is wrong; flagging both.

### F4 — `docs/ingestion-write-path-architecture.md` — labeled "ground truth" but is part-aspirational; one §4 item already done, one not
- SEVERITY: MEDIUM · CATEGORY: other (overclaimed-doc) · CONFIDENCE: high
- Header (line 1) calls it "Architecture & Decisions (**ground truth**)" / "Authoritative reference".
  But §3.4/§5 prescribe "no ON CONFLICT", while the live code (apply_batch.c, F3) and the bucket's own
  `empirical-ingest-log.md:38,47` ("**ON CONFLICT** … triggers OFF … Well-built") show the running
  system *does* use ON CONFLICT. So the doc is a target spec, not a description of the running path.
- §4 "what must change" status vs code (verified):
  - "Remove phantom `(entity_id,type)` anti-join in apply_batch.c" — **DONE in the code body**
    (apply_batch.c:155 explicitly states no such unique exists; uses id PK). The doc lists it as a
    pending change; stale.
  - "Hilbert-range partitioning (replace `id.lo % N`)" — **NOT done.** `NpgsqlSubstrateWriter.cs:45-46,151`
    still partitions "natively by `id.lo % N` into N disjoint partitions (intent_stage_partition)".
    Doc accurate that this is still pending.
- IMPACT: A reader treating this as "ground truth" gets a mix of done/not-done/aspirational without a
  way to tell which. The two bucket docs (this + empirical-ingest-log) disagree on ON CONFLICT.

### F5 — `db/migrations/` — substrate schema is NOT here; cannot be audited for tier-as-kind/provenance-in-id from migrations alone
- SEVERITY: INFO · CATEGORY: other · CONFIDENCE: high
- `20260606000000_layer1_database.sql` only runs `CREATE EXTENSION postgis/laplace_geom/laplace_substrate`
  + role GRANTs. The entities/physicalities/attestations DDL (tier columns, content-address PK, indexes)
  lives inside the `laplace_substrate` extension SQL (`extension/laplace_substrate/sql/*.sql.in`), not in
  `db/migrations/`. So the charter's schema invariants (tier≠kind, no provenance-in-id, index set) must be
  audited in the extension bucket, not here. Migrations themselves are correct and idempotent
  (`IF NOT EXISTS`, role-guarded `DO $$` blocks). No SQL-injection surface (no dynamic user input).
- `20260611000000_app_billing.sql` is app-layer (Stripe quotes/usage/entitlements); correct DDL,
  low priority per charter. Note the leading blank lines (1-4, 36-37) are cosmetic only.

### F6 — `docs/convergence-index-and-inference.md` — ACCURATE: design-labeled, and its "current corruption" claims check out against code
- SEVERITY: INFO · CATEGORY: other (doc-accurate) · CONFIDENCE: high
- Doc is correctly self-labeled (line 6): "architectural model … Parts are **target, not current state**".
- Spot-checked its falsifiable "current corruption" claims (§"Current corruption"):
  1. Opaque concept keys: `ConceptAnchor.EmitAnchor` (`ConceptAnchor.cs:25-29`) → `ContentEmitter.Emit(b, ili, …)`
     where `ili` is the raw string from `SourceEntityIdConventions.WordNetIli` — confirmed string-walk identity.
  2. ILI resolution is a file lookup that returns null on miss: `IliMap.Resolve` (`IliMap.cs:63-64`) is
     `_byKey.TryGetValue(...) ? ili : null`, loaded from `ili-map-pwn30.tab` (`:28,34-51`) — confirmed
     silent-miss file gamble.
- This doc is honest about target vs current and accurate where checkable. No disparagement of the invention.

### F7 — `docs/refounding-vocabulary-on-content-addresses.md` — ACCURATE: the `EntityTier.Vocabulary = 5` violation it flags is live
- SEVERITY: INFO (doc accurate) / the underlying CODE issue is HIGH · CATEGORY: invention-violation · CONFIDENCE: high
- Doc (§"The axes", §"meta↔content link") flags `Vocabulary = 5` as KIND jammed into the depth axis.
  VERIFIED live: `app/Laplace.SubstrateCRUD/EntityTier.cs:20` `public const byte Vocabulary = 5;`
  (with a comment admitting "Abstract vocabulary (POS, morphology values, languages, category anchors,
  relation types)" — i.e. a kind, not a depth). Used widely (TabularDecomposer, RepoDecomposer,
  Atomic2020Decomposer, LayerCompletion, PCoreParallelCompose). This is the charter's named violation #3,
  still present in code. The doc is correct and self-labels "implementation NOT started".

### F8 — `docs/CAPABILITY_VALUATION.md` — valuation/pitch doc; measured numbers unverified but honestly scoped; not disparagement
- SEVERITY: LOW · CATEGORY: other · CONFIDENCE: med
- References `scripts/laplace-bench.sql` (verified it EXISTS) for the latency/throughput rows; conventional-AI
  $ figures are explicitly labeled "(est.)" industry estimates, not measurements (lines 4, 114-116). I did
  **not** run the bench, so the ms/throughput rows (e.g. `isa_path 9 ms`, `recall ~211/s`) are unverified.
  The doc honestly lists "Known-degraded (honest)" items (`hypernyms` ~0.4/s, `salient_facts` timeout,
  `generate`/`walk_text` emits nothing — rank-recalibration debt). Per charter this is the invention's own
  pitch with self-disclosed limits, not Claude disparagement. Note it cites `scripts/laplace-bench.sql`
  while the bucket's bench/ dir has *different* scripts — minor naming divergence, both exist.

### F9 — `docs/bench/*.sql` + `empirical-ingest-log.md` — clean, reproducible, honest
- SEVERITY: INFO · CATEGORY: other · CONFIDENCE: high
- The four hilbert bench scripts are self-contained, use UNLOGGED throwaway tables / rolled-back txns
  against live `physicalities`, name hardware of record, and are re-runnable — exactly the measurement
  discipline the charter wants. `checks.sql`/`sample_activity.sql` are read-only state probes.
  `empirical-ingest-log.md` is an honest measured log (it even corrects its own prior wrong assumptions:
  "these overturn my earlier assumptions … Wrong target the whole time"). No fabricated numbers; no
  disparagement. The only cross-doc tension is the ON CONFLICT point in F4.

---

### Bucket summary
- CRITICAL: 0 · HIGH: 1 (F3) · MEDIUM: 3 (F1, F2, F4) · LOW: 1 (F8) · INFO: 4 (F5, F6, F7, F9)
- Worst issue: **F3** — `apply_batch.c`'s header comment is self-contradictory (claims a phantom
  `(entity_id,type)` UNIQUE + "NO ON CONFLICT" that the body explicitly denies and overrides), and the
  shipped insert uses `ON CONFLICT (id) DO NOTHING` as the dedup arbiter — contradicting charter
  invariant #7 and the ingestion-write-path doc's "no ON CONFLICT" prescription.
- Pattern across the docs: **two prior Claude audits (DYNAMIC_VOCAB_AUDIT, FOUNDRY_SYNTHESIS_FINDINGS)
  describe rank-recalibration debt that has since been PAID in code** (the C# rank table realigned to the
  manifest; the foundry `rel` band ceiling raised to 1.001 to admit IS_A/HAS_DEFINITION/mandate). They
  read as live to-do lists but are closed-debt snapshots — stale and misleading if trusted. The two
  *architecture* docs (convergence-index, ingestion-write-path) are accurate where checkable but the latter
  overclaims "ground truth" while prescribing a state the running code does not yet match.
