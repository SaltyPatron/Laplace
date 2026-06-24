# Dynamic Vocabulary Audit — Ranks & Attestations (pre-reseed gate)

Read-only audit of POS / Sense / Deprel / Feature / Enhanced-deprel vocabulary: what rank each
family carries, and what attestations it emits vs. the complete `SeedCanonical` pattern. Basis
for the pre-reseed fixes. **Reseed is held until these land.** Measured on the live `laplace` DB
(i9-14900KS) + source as of this audit.

---

## A. RANKS

### A1. Authoritative source = native manifest, not the C# constants
`engine/manifest/relation_types.toml` `[ranks]` (→ generated `relation_law.c`, used by
`relation_rank_resolved`). Header says it plainly: *"Recalibrated for SEMANTIC SALIENCE (recall),
not structural authority … also drives model-export plane weighting."* 13 bands:

```
mandate 1.0 · definitional 0.97 · taxonomic 0.90 · equivalence 0.82 · partitive 0.73
· causal 0.64 · oppositional 0.45 · associative 0.36 · tensor_calculation 0.27
· lexical_glue 0.18 · scalar_valued 0.12 · standards_structural 0.08 · probationary 0.05
```

Live spot-checks match exactly: `HAS_DEFINITION 0.97`, `IS_A 0.90`, `IS_SYNONYM_OF 0.82`,
`DEP_DET 0.73`, `COMPLETES_TO/ATTENDS 0.27`, `PRECEDES/HAS_POS 0.18`, `HAS_LANGUAGE 0.08`.

### A2. The C# `RelationTypeRank` table is STALE and drifted — reconcile or delete
`WitnessConstants.cs` defines a *parallel* band table that no longer matches the manifest, yet is
referenced in **27 places**:

| C# constant | C# value | Manifest equivalent | Manifest value | Drift |
|---|---|---|---|---|
| StandardsStructural | 0.91 | standards_structural | 0.08 | **0.83 — catastrophic** |
| Taxonomic | 0.82 | taxonomic | 0.90 | 0.08 |
| Equivalence | 0.55 | equivalence | 0.82 | 0.27 |
| Partitive | 0.73 | partitive | 0.73 | ok |
| Associative | 0.36 | associative | 0.36 | ok |

Actual rank resolution goes through native `Resolve*`, so this table only bites where code uses
the constant directly (ResponseContent/UserPromptContent = Associative 0.36, which happens to
agree). But anything relying on `StandardsStructural` expecting 0.91 is **~0.83 wrong** vs live.
**Action:** delete the C# table or regenerate it from the manifest; never maintain two.

### A3. Recalibration starves the foundry (root-confirmed)
`definitional 0.97` and `taxonomic 0.90` sit **above the foundry's 0.86 band ceiling** → excluded
from synthesis. The recall-optimized recalibration (content on top) is in direct tension with the
foundry, which needs exactly those high bands. The manifest comment confirms one weight drives
both read-ranking and export plane weighting — so they can't be tuned independently today.

### A4. Unranked-type bucket (audit gap)
In a 2% `consensus` sample, ≥1 relation type returns **NULL rank** carrying ~18.5k sample edges
(≈0.9M extrapolated). `relation_rank_resolved(type) × eff_mu` → NULL → those edges are invisible
to rank-weighted ranking. Cause: type not in the manifest. **Action:** find and band it.

### A5. Questionable band: senses ranked as glue
`HAS_SENSE 0.18`, `IS_SENSE_OF 0.18` (lexical_glue) — same floor as `PRECEDES`/`HAS_POS`. For a
recall-salience scheme, lemma→sense arguably deserves higher. `IS_TYPED_AS 0.08` (the sense parent
link) and `HAS_NAME_ALIAS 0.08` are at the standards floor. **Decision needed.**

---

## B. ATTESTATION COMPLETENESS (vs `SeedCanonical`, which emits entity + IS_A + substrate-native HAS_NAME_ALIAS, RelationTypeRegistry.cs:170-186)

| Family | Entity | IS_A parent | Substrate-native HAS_NAME_ALIAS | Rank src | Incomplete? |
|---|---|---|---|---|---|
| **Senses/synsets** | ✅ | ✅ (`IS_TYPED_AS WordNetSense`) | ✅ `sense→HAS_NAME_ALIAS→lemma` (WordNetDecomposer.cs:339-340) | native surface | **No** (parent uses IS_TYPED_AS not IS_A) |
| **Deprel** | ✅ | ✅ @AcademicCurated | ❌ readback only (`SeedDynamic`, RelationTypeRegistry.cs:198) | native ResolveDeprel | **Yes** — no alias |
| **Enhanced deprel** | ✅ | ✅ | ❌ readback only (same `SeedDynamic`) | native | **Yes** — no alias |
| **FEAT_ type** | ✅ | ✅ | ❌ readback only (same `SeedDynamic`) | native ResolveFeature | **Yes** — no alias |
| **FEAT_ value entity** | ✅ | ❌ | ❌ `TrackUdFeatureValue` only (UDDecomposer.cs:322-324) | n/a | **Yes** — no IS_A, no alias |
| **POS tags** | ✅ | ❌ (only EntityRow type field) | ❌ alias only for *probationary* tags, readback (PosReference.cs:61) | native ResolvePos | **Yes** — no IS_A, no canonical alias |

### B1. Render-path bug (separate from seeding)
`render_text(type_id)` renders **no** relation-type name — 100% empty across the sample, even for
canonical types that *have* `HAS_NAME_ALIAS`. `label()` works for canonical (readback `canonical_names`)
but returns NULL for dynamic types. So dynamic vocab is illegible through every path.

---

## C. Single fix loci (for when the gate work starts — not yet applied)

1. **`RelationTypeRegistry.SeedDynamic`** (RelationTypeRegistry.cs:188-208) — add the `SeedCanonical`
   182-185 block (`ContentWitnessBatch.Emit` + `HAS_NAME_ALIAS`). Fixes deprel + enhanced-deprel +
   FEAT_ type names in one place.
2. **`PosReference.SeedCanonical`** — emit IS_A(→PosTypeId) + HAS_NAME_ALIAS for canonical tags.
3. **`UDDecomposer.cs:323-324`** — FEAT_ value entity needs IS_A + HAS_NAME_ALIAS.
4. **`render_text`** — fall back to `HAS_NAME_ALIAS`/`label` for vocabulary-tier entities.
5. **Manifest** — band the NULL-rank type (A4); reconcile/delete stale C# `RelationTypeRank` (A2);
   decide sense band (A5); decide foundry-ceiling vs recall recalibration (A3).
6. **Compose kernel** — single-child tier collapse (separate tier-bug track; same reseed gate).

Order: land 1-4 + 6 in code → rebuild extension/binaries → reseed once → verify deterministic tiers
and legible/ranked dynamic vocab. 5 is config/decision work that can interleave.
