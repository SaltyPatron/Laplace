# Campaign reseed queue — Part 1 + modalities (living)

**Do NOT execute** until the operator orders KEYMASTER (`db-reset` / foundation / seed).
Code and TOML for the listed items may land earlier; highway bits renumber alphabetically
on codegen and **owe one full reseed**. Never ship partial bit-order changes.

Updated: 2026-07-12 (pre-KEYMASTER: M0 TOML + codegen; R1/R2 code ready).

## Queued items (batch into ONE reseed)

Exact governed canonicals in `engine/manifest/relation_types.toml` for this
campaign batch (aliases do not add bits — they map to a canonical):

### 1. Credit / license (+5 canonicals)

- `HAS_LICENSE`
- `HAS_ATTRIBUTION`
- `HAS_SOURCE_URL`
- `HAS_CITATION`
- `HAS_VERSION`

Deposit path: `SourceVocabularyBootstrap.DepositLicenseAsync` (sealed Initialize
via `RegisterManifestAsync`).

### 2. HAS_SENSE family_root fixes (TOML already corrected)

| Relation | `family_root` / `parent` |
|---|---|
| `HAS_SENSE` | self / null |
| `IS_SENSE_OF` | `HAS_SENSE` / `HAS_SENSE` |
| `IS_SYNONYM_OF` | `HAS_SENSE` / `HAS_SENSE` |

`IS_TRANSLATION_OF` stays under `SEMANTIC_EQUIVALENCE`.
`bubble_up` reads the family via `relation_type_in_family(..., 'HAS_SENSE')`.

### 3. Modality canonicals (+10) — declared in TOML; no ingest until KEYMASTER

**Audio:**
- `HAS_RECORDING` (alias flip: `RECORDING_OF`)
- `HAS_SPECTRAL_PEAK` (calculated-layer; versioned analyzer later)
- `HAS_ONSET_SEGMENT` (calculated-layer)

**Image (witnessed infra):**
- `HAS_REGION`
- `HAS_PATCH` (parent/family `HAS_REGION`)
- reuse existing: `ADJACENT_TO_PIXEL`, `IS_PIXEL_OF`, `DEPICTS`, `CAPTIONS`

**Video (witnessed infra):**
- `HAS_FRAME` (alias flip: `IS_FRAME_OF`)
- `PRECEDES_IN_TIME`

**Code (AST witnessed structure):**
- `HAS_AST_CHILD`
- `HAS_AST_KIND`

Highway bit count after codegen must include every name above (credit + modality).
Aliases `RECORDING_OF` / `IS_FRAME_OF` are not separate bits.

### 4. Prior campaign debt (still open — do not drop)

- UCD-as-source / Unihan scope tier relations (if any landed separately)
- Prior 194-bit highway → post-codegen N-bit reseed (this branch)

## Wave 0 data paths (R5 later — paths only, no ingest)

Electronics + Morse + NCVEC pool (local):

- `D:\Data\Ingest\test-data\electronics\` (canonical pool dir)
  - `standard-electrical-dictionary-sloane.txt`
  - `hawkins-electrical-guide-v02.txt`
  - `lessons-in-wireless-telegraphy-morgan.txt`
  - `wireless-telegraph-construction-amateurs-morgan.txt`
  - `wireless-telegraphy-telephony-explained-morgan.txt`
  - `radio-amateurs-hand-book-collins.txt`
  - `international-morse-code.txt`
  - `ncvec-2024-2028-extra-class-pool.pdf` (Extra 2024–2028)
- Mirror copies under `D:\Data\Ingest\test-data\text\` (same principles/Morse text; no NCVEC PDF there)
- Tatoeba audio root: `D:\Data\Ingest\Tatoeba\audio`
- Missing for R5 acquire later: Technician 2026–2030 + General 2023–2027 text pools
  (Extra PDF present; NCVEC fetch historically 403'd — extract/mirror as text before exam harness)

## Operator steps (when KEYMASTER ordered)

```text
cmd /c "scripts\win\db-reset.cmd"          # ONLY when explicitly ordered
cmd /c "scripts\win\seed-foundation.cmd"
cmd /c "scripts\win\seed-everything.cmd"   # or sequenced seed-step per source
```

After reseed: verify `SELECT * FROM api('highway');` / relation counts, credit
attestations for a non-Unknown `SourceLicense`, and `laplace_highway_ready()` /
mask population. Then unlock R3 performance gate + modality ingest.

## Out of this queue

- Foundry A3/A4
- Part 2 (AI checkpoint scrape)
- Part 4 (Stockfish / chess analysis)
- Model recipe harvest (no reseed for recipe entities)
