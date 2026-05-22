# Laplace — Canonical Plan (Rough Draft)

**Status:** Rough draft. Will evolve as we hit reality. Source of truth for chunk sequencing.

**Authored:** 2026-05-21 (Chunk 0 framework setup)
**By:** claude
**Approved:** user — "rough draft of the plan but yeah, go"

---

## Approach

Chunk-based, not phase-based. Each chunk:

- Bounded scope (in / out)
- Concrete deliverables
- Explicit acceptance criteria
- Gated by CI verification
- Maps to a GitHub Issue

**Unit:** turns of focused agent work. **NOT** human-weeks.

**Milestone:** ingest Qwen-family model → native substrate cascade over source-scoped state → emit sparse Qwen round-trip GGUF from substrate → load in llama.cpp → chat with it under fixed prompt/sampler settings.

---

## Chunk 0 — Plan + deps + skeleton ← THIS turn

**Deliverables:**
- This file (`.agent/status/plan.md`)
- Dep sources locked in STANDARDS.md/ADRs (direct C/C++ deps as submodules under `external/`; Intel oneAPI as the sole vendor install exception)
- `engine/`, `extension/`, `app/`, `scripts/` scaffolded
- `engine/CMakeLists.txt` orchestrates three shared libraries per ADR 0024 (`liblaplace_core.so`, `liblaplace_dynamics.so`, `liblaplace_synthesis.so`); each subdir has its own CMakeLists
- Unified CMake + modular `.sql.in` SQLPP produces extension `.so`s and install SQL loadable via `CREATE EXTENSION laplace_geom; CREATE EXTENSION laplace_substrate;`
- `app/Laplace.Engine` C# project with placeholder P/Invoke bindings
- `scripts/check-prereqs.sh` (real impl); other scripts stubbed
- `integration.yml` workflow extended with `build` job
- GitHub Issues 1-8 created

**Acceptance:**
- [ ] `just build` succeeds (engine + extension + app)
- [ ] `CREATE EXTENSION laplace;` works; `SELECT laplace_version();` returns `'0.1.0'`
- [ ] `integration.yml` build job passes on `hart-server`
- [ ] GitHub Issues 1-8 visible

---

## Chunk 1 — Core math primitives

**Scope:** `coord4d`, `hash128` (BLAKE3 truncated + Merkle), `hilbert4d` encode/decode, `mantissa_pack`/`_unpack`. All land in `engine/core/` per ADR 0024.

**Deliverables:**
- `engine/core/include/laplace/core/coord4d.h` + impl + unit tests
- `engine/core/include/laplace/core/hash128.h` + impl + tests (BLAKE3 linkage via FetchContent, per ADR 0015)
- `engine/include/laplace/hilbert4d.h` + impl + tests (Skilling 2004)
- `engine/include/laplace/mantissa_pack.h` + impl + tests (round-trip lossless on payload bits)
- Cross-language consistency tests (SQL ↔ C# ↔ engine same result)

**Acceptance:**
- [ ] All engine unit tests pass (`ctest`)
- [ ] Mantissa pack/unpack round-trip: 100% lossless on payload bits
- [ ] Hilbert encode/decode round-trip: locality preserved (Hamming distance correlates with coord distance)
- [ ] Cross-language test: same hash/coord/Hilbert from SQL and from C#

---

## Chunk 2 — Geometry serde + GIST integration

**Scope:** PostGIS-compatible WKB serialization for ZM geometries; `gist_geometry_ops_nd` usage; custom `laplace_*_4d` functions.

**Deliverables:**
- `engine/include/laplace/geometry4d.h` + impl + tests
- PG wrapper functions: `laplace_distance_4d`, `laplace_dwithin_4d`, `laplace_centroid_4d`, `laplace_radius_origin`, `laplace_frechet_4d`, `laplace_hausdorff_4d`, `laplace_hilbert_encode`, `laplace_hilbert_decode`, `laplace_mantissa_pack`, `laplace_mantissa_unpack`
- `extension/laplace_geom/laplace_geom--0.1.0.sql` populated with `ST_*_4d` + hash128 + Hilbert + mantissa + opclasses (per ADR 0025); `extension/laplace_substrate/laplace_substrate--0.1.0.sql` populated with substrate schema
- `extension/src/laplace.c` populated with PG_FUNCTION_INFO_V1 wrappers
- Schema DDL applied: `entities`, `physicalities`, `attestations` per DESIGN.md

**Acceptance:**
- [ ] `ST_AsBinary(ST_MakePoint(1,2,3,4))` round-trips correctly
- [ ] `laplace_distance_4d(ST_MakePoint(0,0,0,0), ST_MakePoint(1,1,1,1)) ≈ 2`
- [ ] GIST index on `geometry` with Z+M flag works: `<<->>` KNN returns expected nearest
- [ ] CI green

---

## Chunk 3 — Perf-cache + T0 seed

**Scope:** UCA + super-Fibonacci + Hopf + Hilbert + hash → `data/perfcache.bin` + DB seed of 1.114M T0 entities. Both artifacts derived independently from UCD per RULES.md R7.

**Deliverables:**
- `engine/src/codepoint_table.cpp` (build_from_ucd, load_perfcache, lookup)
- `engine/include/laplace/codepoint_table.h`
- `scripts/build-perfcache.sh` (real implementation)
- `scripts/seed-t0.sh` (real implementation; bulk COPY)
- `scripts/verify-perfcache.sh` (cross-verifies perf-cache vs DB seed)

**Acceptance:**
- [ ] `just build-perfcache` → deterministic `data/perfcache.bin` (re-run produces byte-identical output)
- [ ] `just seed-t0` inserts 1,114,112 T0 entity rows
- [ ] `just verify-perfcache` confirms perf-cache and DB seed match byte-for-byte
- [ ] Prompt/entity decomposition can compute codepoint hash/coord/Hilbert/UCA/flags from perf-cache with no per-codepoint DB round trip
- [ ] Cross-machine determinism (or strongly bounded — verify via CI)

---

## Chunk 4 — First linguistic source (WordNet)

**Scope:** `ISource` interface + `WordNetSource` implementation. First attestation flow end-to-end.

**Deliverables:**
- `engine/include/laplace/sources/source.h` (ISource)
- `engine/src/sources/wordnet/WordNetSource.cpp` + Prolog/RDF parser
- WordNet IS_A, hypernym, POS, sense attestation extraction
- `scripts/ingest-source.sh` (dispatch to plugin)

**Acceptance:**
- [ ] `just ingest wordnet` succeeds
- [ ] Query returns expected WordNet attestations (`dog IS_A canine` round-trippable through Glicko-2 placeholder rating)
- [ ] Re-running ingest is idempotent (UPSERT skip)

---

## Chunk 5 — Glicko-2 + cross-source dynamics

**Scope:** Fixed-point int64 Glicko-2 update; `CREATE AGGREGATE`; source-credibility-per-kind via meta-attestations; arena semantics for compatibility/cardinality/context/competition/source trust.

**Deliverables:**
- `engine/include/laplace/glicko2.h` + impl + tests
- PG `CREATE AGGREGATE laplace_glicko2_accumulate`
- `engine/src/credibility.cpp` (cross-source consensus + credibility derivation)
- Arena metadata for core kinds (multi-valued POS, functional current-capital style relations, source-local/prompt-local modes)
- ConceptNet source plugin (gives us a second source for cross-source tests)

**Acceptance:**
- [ ] Glicko-2 unit tests pass: rating progresses with observations; converges; deterministic across runs
- [ ] Cross-source test: ingest WordNet + ConceptNet on same fact → consensus rating reflects both
- [ ] Source credibility updates correctly when sources disagree
- [ ] Correlated/repeated low-trust assertions do not count as independent consensus
- [ ] Effective mu calculation includes rating/RD/volatility/source-kind credibility/context compatibility

---

## Chunk 6 — TransformerModelSource + Procrustes + lottery-ticket sparsity

**Largest chunk.** Probe-based model ingestion: read safetensors + config.json → extract Recipe → Procrustes alignment → physicalities → lottery-ticket-filtered attestations.

**Deliverables:**
- `engine/src/sources/transformer/TransformerModelSource.cpp`
- `engine/src/procrustes.cpp` (oneMKL SVD)
- `engine/src/laplacian_eigenmaps.cpp` (Spectra)
- `engine/src/gram_schmidt.cpp` (Eigen HouseholderQR)
- `engine/src/lottery_ticket_filter.cpp` (per-tensor top-k + per-row top-k + probe validation — NEVER flat threshold per RULES.md R3)
- Recipe entity + typed attestations from config.json parsing
- Physicality computation + storage

**Acceptance:**
- [ ] Ingest small open model (Qwen3-0.6B or Phi-mini) end-to-end in <20 min
- [ ] Physicalities populated for shared anchor codepoints; alignment_residual reasonable
- [ ] Attestation count ≤ ~5% of naive parameter count (lottery-ticket-aware sparsity working)
- [ ] Recipe entity present with typed attestations (HAS_HIDDEN_SIZE, HAS_NUM_LAYERS, etc.)
- [ ] Source-scoped codec verification identifies recipe/tokenizer/physicality/probe/sparse-attestation coverage gaps explicitly

---

## Chunk 7 — Synthesis pipeline (LlamaTemplate + extractors + GGUFWriter)

**Scope:** Emit a model from substrate state to a llama.cpp-loadable GGUF file.

**Deliverables:**
- `engine/include/laplace/synthesis/architecture_template.h` (IArchitectureTemplate)
- `engine/src/synthesis/llama_template.cpp` (handles Llama / Qwen architecture family)
- `engine/include/laplace/synthesis/feature_extractor.h` (IFeatureExtractor)
- `engine/src/synthesis/extractors/*.cpp` (canonical_coord, POS, WordNet_synset, ConceptNet_relation, physicality_projection, random_projection_pad)
- `engine/src/format/gguf_writer.cpp`
- Recipe JSON parsing (default + user-override)
- C# CLI: `laplace-cli synthesize --recipe <path>`

**Acceptance:**
- [ ] `just synthesize recipes/qwen3-roundtrip.json` produces a valid GGUF file
- [ ] llama.cpp `llama-cli` loads the GGUF without errors
- [ ] Sparse-by-construction emission: actual zero count > 50% and unsupported positions are exact zero, not tiny nonzero jitter

---

## Chunk 8 — Round-trip + chat verification ← MILESTONE

**Scope:** The headline test — chat with a substrate-synthesized model.

- `scripts/roundtrip.sh` (real: ingest → native substrate cascade → synthesize → load in llama-cli)
- Integration test: end-to-end roundtrip on Qwen3-0.6B
- CI workflow extended to run the round-trip test on self-hosted

**Acceptance:**
- [ ] `just roundtrip /vault/models/qwen3-0.6b` succeeds
- [ ] Native substrate cascade answers the smoke prompt through prompt ingestion + compiled cascade, not context-window buffering
- [ ] `llama-cli` loads the output GGUF
- [ ] Three-way smoke prompt: stock source model / native substrate / exported GGUF on "Hello! Tell me something interesting." land in the same source-scoped behavioral basin
- [ ] Round-trip CI workflow passes on `hart-server`

---

## Beyond Chunk 8 — to be planned after we hit the milestone

Likely candidates (priority TBD post-milestone):
- OpenAI-compat endpoint extension (substrate-as-served-model)
- Image / audio modality decomposers
- A second target architecture (Mamba / SSM)
- C# Synthesis UI (parameter discovery, dry-run cost estimation)
- Multi-source consensus tuning + source-credibility refinement
- Larger model ingest (Qwen3-7B, then Qwen3-480B / Llama 4 Maverick / DeepSeek)

---

## Process

- Each chunk → GitHub Issue with acceptance checklist
- Each chunk → PR or direct push to main once acceptance criteria green
- `STATE.md` updated at chunk completion
- `decisions.md` appended when chunk surfaces architectural decisions
- `blockers.md` records open blockers per chunk

---

## What gets cleaned along the way (per user direction)

- **Remove (sudo, you run):** prior-iteration PG extensions `hypercube`, `hypercube_ops`, `embedding_ops`, `semantic_ops`, `generative`.
- **Leave in place:** Hartonomous artifacts (`libengine*.so`, `hartonomous.control`, etc.) — no conflict with Laplace; potential reference value for you (even though I can't read them per R11).
- **Leave in place:** HNSWLib and Ollama system installs — banned by RULES.md means Laplace doesn't BUILD against them; not a directive to delete from the system.
- **Discipline is in build configuration** (precise about what we include / link), not filesystem state.
