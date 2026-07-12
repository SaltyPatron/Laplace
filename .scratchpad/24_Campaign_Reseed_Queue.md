# Campaign reseed queue — valet orchestration (Wave 5)

**Do NOT execute** until the operator orders a batched reseed. Code for the listed
relations has landed; highway bits renumber alphabetically on codegen and **owe one
full reseed**. Never ship partial bit-order changes.

## Queued items (batch into ONE reseed)

1. **Credit / license relations** (added 2026-07-12 Wave 5):
   - `HAS_LICENSE`
   - `HAS_ATTRIBUTION`
   - `HAS_SOURCE_URL`
   - `HAS_CITATION`
   - `HAS_VERSION`
   Source: `engine/manifest/relation_types.toml`. Deposit path:
   `SourceVocabularyBootstrap.DepositLicenseAsync` (sealed Initialize via
   `RegisterManifestAsync`).

2. **Prior campaign debt** (still open — do not drop):
   - UCD-as-source / Unihan scope tier relations (if any landed separately)
   - 189-bit → N-bit highway reseed from any earlier relation adds on this branch

## Operator steps (when ordered)

```text
cmd /c "scripts\win\db-reset.cmd"          # ONLY when explicitly ordered
cmd /c "scripts\win\seed-foundation.cmd"
cmd /c "scripts\win\seed-everything.cmd"   # or sequenced seed-step per source
```

After reseed: verify `SELECT * FROM api('highway');` / relation counts, and that a
source with a non-Unknown `SourceLicense` (e.g. Unicode) has credit attestation rows.

## Out of this queue

- Foundry A3/A4 (not valet campaign)
- Model recipe harvest (code path already in `ModelDecomposer` SynthRecipePhase /
  `RecipeSynthesizer` — no reseed required for recipe entities)
