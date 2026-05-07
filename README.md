# Laplace

Universal digital content substrate. Single tiered `entity` table (tier 0 = the full 1,114,112-codepoint Unicode atom pool placed on S³ via super-Fibonacci) + `edge` table where edge types and roles are themselves substrate entities. Three-layer Glicko-2 (rated-source attestation across source / entity / edge). GEOMETRY4D parallel PostgreSQL type family with full S³ / 4-ball / Voronoi support. Gödel Engine over OODA loops at three scales is the behavioral engine that turns the substrate into AGI/ASI capability.

The architecture is documented in `docs/substrate-synthesis.md`. The build plan is at `..\..\..\Users\ahart\.claude\plans\time-for-you-to-scalable-wind.md`.

## Build

```powershell
.\scripts\build.ps1            # native (CMake) + managed (.NET 10)
```

## Test

```powershell
.\scripts\test.ps1             # native CTest + managed xUnit + SQL pgTAP, AddressSanitizer-instrumented
```

## Database

```powershell
.\scripts\db-bootstrap.ps1     # creates DB, installs laplace_pg extension, applies migrations
.\scripts\db-reset.ps1         # drops + recreates + bootstraps (requires explicit -Force)
```

## Seed

```powershell
.\scripts\seed-foundational.ps1   # UCD / UCA / Unihan / ISO 639 — full 1,114,112 codepoints
.\scripts\seed-secondary.ps1      # WordNet / OMW / UD / Wiktionary / Tatoeba / ATOMIC / ArXiv
```

## Ingest

```powershell
.\scripts\ingest-model.ps1 -ModelDir D:\Models\hub\models--sentence-transformers--all-MiniLM-L6-v2
.\scripts\ingest-text.ps1  -File path\to\file.txt
.\scripts\ingest-image.ps1 -File path\to\image.png
.\scripts\ingest-audio.ps1 -File path\to\sample.wav
.\scripts\ingest-video.ps1 -File path\to\clip.mp4
# ... per-modality scripts
```

## Export

```powershell
.\scripts\export-model.ps1 -Query "all-MiniLM-L6-v2 with rating threshold 0.9" -Format huggingface -Out D:\exports\minilm-refined
```

## Long-horizon Gödel task

```powershell
laplace godel-task --task "find drug-target pairs that match the structural trajectory of known cancer cures"
```

## Layout

- `ext/laplace_pg/` — native PostgreSQL extension + standalone shared library (BLAKE3, GEOMETRY4D, S³, super-Fibonacci, Glicko-2, Laplacian eigenmap, Gram-Schmidt, exact KNN, Voronoi 4D, ICU UAX29, NFC, Hilbert, RLE, FFT, spectral features, image/audio/video decode, tensor decode)
- `src/` — managed .NET projects (one per concern: Core abstractions + impls, Pipeline, per-modality decomposers, per-modality recomposers, Inference, Cognition (Gödel + OODA + behavioral patterns), CLI)
- `sql/` — schema migrations + seed bootstrap SQL
- `tests/` — CTest (native) + xUnit (managed) + pgTAP (SQL)
- `scripts/` — PowerShell automation
- `docs/` — substrate-synthesis.md (architecture), usable-code-catalog.md (audit of prior iteration code), sabotage-audit.md (failure mode catalog), coding-standards.md (concrete conventions)

## Status

Phase 1 — framework / scaffolding (Track A). See plan for tracks B–K and convergence gates G1–G10.
