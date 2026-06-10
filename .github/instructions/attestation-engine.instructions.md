# Attestation engine law

Attestation **policy** (relation resolve, alias flip, symmetry orient, family membership, POS tag normalization, φ/score, attestation id) lives in **native code only**:

- `engine/core/src/generated/relation_law.c` — manifest-driven relation tables
- `engine/core/src/generated/pos_law.c` — POS tag normalization
- `engine/core/src/attestation_engine.c` — staging API
- `extension/laplace_substrate/src/laplace_substrate.c` — SQL-callable SPI (`relation_type_resolve`, `relation_type_in_family`)

## Orchestration layers

### C# (witness adapters)

**Allowed**

- Parse corpus → surface strings + entity ids
- `NativeAttestation.*` / `LibraryImport` into `laplace_core`
- Append to `IntentStage` or `SubstrateChangeBuilder` rows returned by native

**Forbidden** in `app/Laplace.Decomposers.*`

- `AttestationFactory` (deleted — use `NativeAttestation` only)
- `RelationTypeRegistry.Attest` / `AttestWeighted` (deleted — use `NativeAttestation.Categorical` / `ResolvedScored`)
- Hand-maintained alias maps, rank tables, φ/score math, `ScoreFp1e9` assignment, `AttestationOutcome` branching
- `new AttestationRow(...)` with locally computed id/outcome/score on witness paths

Use `NativeAttestation.Categorical`, `CategoricalResolved`, `ResolvedScored`, `Aggregated`, `PosUpos`, `PosXpos`, etc. All score/φ/outcome/aggregation law is in `attestation_engine.c`.

### SQL

**Allowed**

- `laplace.relation_type_resolve(text)`
- `laplace.relation_type_in_family(bytea, text)`

**Forbidden**

- Growing `NOT IN (relation_type_id('HAS_POS'), …)` policy lists — use `relation_type_in_family`

## Manifest

Single source: `engine/manifest/relation_types.toml`, `engine/manifest/pos_tags.toml`.

Regenerate after edits:

```powershell
scripts/codegen-attestation-law.ps1
```

CI must fail if generated outputs drift.

## Tests

- `engine/core/tests/test_relation_law.cpp` (gtest)
- `extension/laplace_substrate/tests/sql/schema_law.sql` (pg_regress)

Both must agree on POS family law (`HAS_UPOS` → `HAS_POS`, `HAS_XPOS` ∈ family).
