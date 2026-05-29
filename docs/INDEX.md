# INDEX.md — Documentation map for Laplace

A navigable table of contents for the whole `docs/` corpus plus the root-level core
documents and the specialized agent definitions. Every one-line purpose below is
grounded in **[`docs/SUBSTRATE-FOUNDATION.md`](SUBSTRATE-FOUNDATION.md)** — the ratified
lens — not in older prose, so this index can be trusted even where an individual ADR or
doc still carries conventional-AI drift.

> **Read order to understand the invention:** start with the foundation, then the core
> docs in the order CLAUDE.md prescribes, then dip into ADRs by topic. Where any doc or
> ADR below conflicts with the foundation on the conceptual core, the foundation wins and
> the other artifact is the thing to correct.

---

## 0. The lens (read first)

| File | Purpose |
|---|---|
| [SUBSTRATE-FOUNDATION.md](SUBSTRATE-FOUNDATION.md) | **Authoritative ratified core.** The invention in one paragraph + ten core truths (model ingest = streaming O(params) ETL of weight cells as Glicko-2 matchup outcomes; S³ glome = the one canonical embedding frame; inference = indexed A\*, not GEMM; trust = self-tuning Glicko-2, never a tier; bit-perfect is worthless; numbers are grounded compositional entities; CPU-native, no GPU) + the explicitly OPEN questions that must be flagged, never invented. |

---

## 1. Core documents (project root)

| File | Purpose |
|---|---|
| [README.md](../README.md) | Project front door — what Laplace is and how to get oriented. |
| [CLAUDE.md](../CLAUDE.md) | Agent operating instructions: what Laplace IS / IS NOT, hard rules, read order, agent roster, cadence. |
| [GLOSSARY.md](../GLOSSARY.md) | Terminology lock — every Laplace term (entity, physicality, attestation, arena, tier, T0, synthesis) means exactly what is defined here. |
| [RULES.md](../RULES.md) | Architectural invariants, zero tolerance — the lines that must not be crossed (no GEMM hot path, no conventional-AI reflexes, doc-currency, CI-owns-setup, no restraint-promise theater). |
| [STANDARDS.md](../STANDARDS.md) | Datatype, naming, and coding standards (fixed-point, cast minimization, SIMD posture, C-ABI discipline). |
| [DESIGN.md](../DESIGN.md) | Engineering spec — schema, types, function inventory, indexing strategy, synthesis `materialize_tensor` surface. |
| [OPERATIONS.md](../OPERATIONS.md) | Build / launch / seed / ingest / query / synthesize / verify procedures; "when in doubt" cadence. |

---

## 2. Architecture Decision Records

| File | Purpose |
|---|---|
| [adr/README.md](adr/README.md) | How ADRs work in this repo + the running index of decisions. |
| [adr/0000-template.md](adr/0000-template.md) | The ADR template all decisions are written against. |

### 2a. Substrate model & invention core

| ADR | Title — corrected purpose |
|---|---|
| [0002](adr/0002-three-tables-no-event-log.md) | Three core tables; no event log — entities / physicalities / attestations are the whole store; current attestation state, not a replay log. |
| [0007](adr/0007-lottery-ticket-aware-sparsity.md) | Sparsity is emergent from matchup consensus, never weight-magnitude pruning — significant cells survive as observations; flat top-k that discards most of the model is the disease (foundation truth 1). |
| [0035](adr/0035-prompt-ingestion-and-compiled-cascade.md) | Prompt ingestion + compiled cascade — a prompt is ingestion; inference is **indexed A\*** (index seeks to seed, `rating DESC` to expand), bounded ≈ O(tier-depth), not GEMM and not recursive-CTE traversal (foundation truth 4). |
| [0036](adr/0036-arena-semantics-and-source-trust.md) | Arena semantics + source-trust consensus — what pulls back is **Glicko-2 effective-μ across typed arenas**, not distance and not raw voting (foundation truth 3). |
| [0037](adr/0037-layered-seed-ingestion-and-model-codec-fidelity.md) | Layered seed ingestion + model-ingest fidelity — semantic ingest of a model is the mandatory spine; seed-source attestations are **optional enrichment**; fidelity is behavioral, never bit-perfect (foundation truths 6, "codec" name banned per truth 10). |
| [0039](adr/0039-schema-reorganization-entity-identity-vs-physicality-representation.md) | Entity is identity, physicality is representation — but physicalities are **not** "just an index"; geometry carries meaning, dynamics over it are attestation-based (foundation truth 3 forbidden framings). |
| [0040](adr/0040-multi-modal-entity-types-universal-t0.md) | Multi-modal entity types + universal T0 — every modality bottoms in the **same** Unicode codepoints; `255`/`1` is one shared entity across text/pixel/weight/index (foundation truth 7; T0 = codepoints only). |
| [0044](adr/0044-attestation-kind-priors-and-source-trust-taxonomy.md) | Attestation-kind priors + source trust — priors seed Glicko-2; trust is **emergent and self-tuning from cross-source agreement, never a class ladder or trust tier** (foundation truth 5). |
| [0049](adr/0049-substrate-change-intent-type.md) | `SubstrateChange` — the unified intent type decomposers emit toward the write surface. |
| [0050](adr/0050-substrate-crud-write-surface.md) | `SubstrateCRUD` — the shared write surface; authoring facts (CRUD over entities) replaces gradient descent (foundation truth 8). |

### 2b. Geometry, indexing & hashing

| ADR | Title — corrected purpose |
|---|---|
| [0001](adr/0001-extend-postgis-via-z-plus-m.md) | Extend PostGIS via Z+M; do not create a parallel geometry type — 4D rides standard `geometry`. |
| [0003](adr/0003-xxh3-128-for-entity-hashing.md) | XXH3-128 for entity hashing — superseded by 0015 (BLAKE3). |
| [0005](adr/0005-hilbert-over-hyperbox.md) | Hilbert curve over the bounding hyperbox, not the sphere — locality-preserving 1D access key for the S³ frame. |
| [0012](adr/0012-mantissa-packing-format.md) | Mantissa-packing format — 212-bit per-vertex payload (XYZ = entity_id, M = metadata). |
| [0015](adr/0015-blake3-for-entity-hashing.md) | BLAKE3-128 for entity hashing (raw bytes, no casts, no hex) — content addresses for the Merkle DAG. |
| [0029](adr/0029-custom-indexing-strategy.md) | Custom indexing strategy — five substrate-shaped opclasses; the geometry **seeds** candidates, it does not decide retrieval (foundation truth 3). |
| [0048](adr/0048-hash-composer-leaf-to-trunk.md) | HashComposer — leaf-to-trunk content-addressing primitive building the Merkle DAG. |

### 2c. Ingest, decompose & synthesize

| ADR | Title — corrected purpose |
|---|---|
| [0008](adr/0008-sparse-by-construction-emission.md) | Sparse-by-construction emission — exact zeros where no significant attestation exists; never densify on export. |
| [0009](adr/0009-recipe-extraction-and-overrides.md) | Recipe extraction at ingest + user JSON override — the recipe is a **fillable mold**; synthesis pours facts into the source's own shape (round-trip) or any other (retarget) (foundation truth 6). |
| [0010](adr/0010-substrate-synthesis-naming.md) | "Substrate Synthesis" naming for fully parametric emission — emit any dim / dense-or-MoE / vocab / dtype from the consensus of authored facts (foundation truth 8). |
| [0011](adr/0011-polymorphic-plugin-architecture.md) | Polymorphic plugin architecture — six interfaces; universal class surface, no per-model-family names. |
| [0041](adr/0041-decomposer-scope-full-domain-ecosystem.md) | Decomposer scope is the full domain ecosystem, not a single file. |
| [0043](adr/0043-composite-decomposer-architecture.md) | Composite decomposer architecture — `ModelDecomposer<ContainerFormat>` worked example; vendor naming is substrate content, not hardcoded switches. |
| [0047](adr/0047-text-decomposer-pure-primitive.md) | TextDecomposer — pure primitive (observed UTF-8 + UAX#29 → TierTree); decomposition only, no attestation. |
| [0051](adr/0051-idecomposer-csharp-plugin-contract.md) | `IDecomposer` C# plugin contract — the per-source decomposer interface. |
| [0052](adr/0052-ingest-pipeline-orchestration.md) | Ingest pipeline orchestration — `IngestRunner` composes decompose → dedup → attest. |
| [0055](adr/0055-static-structural-parse-exploded-view.md) | Static structural parse / exploded view — universal container dissection; the substrate **never loads files**, it streams structure (foundation truth 1). |
| [0056](adr/0056-weight-tensor-etl-as-arena-matchup-observation.md) | Weight-tensor static ETL as arena-matchup observation — **the** model-ingest pattern: stream the tensor, each significant cell = one Glicko-2 matchup outcome (weight = outcome, source trust = opponent), store only consensus; never recompute, never GEMM at ingest (foundation truths 1, 2). |
| [0057](adr/0057-substrate-emission-discipline-product-not-packaging.md) | Emission discipline — emit the product (resynthesized model), never re-package the ingested blob; dissolve-and-resynthesize at the boundary. |
| [0058](adr/0058-canonicality-criterion-for-ingestible-sources.md) | Canonicality criterion — substrate ingests canonical sources; derived/lossy artifacts are emit-only. |
| [0059](adr/0059-format-writer-emission-matrix-and-ifw-contract.md) | Format-writer emission matrix + `IFormatWriter` contract — which output formats writers must produce. |

### 2d. Engine, build & dependencies

| ADR | Title — corrected purpose |
|---|---|
| [0024](adr/0024-engine-modularization.md) | Engine modularization — `liblaplace_core` / `_dynamics` / `_synthesis`; one source of math truth loaded by both PG and the C# app. |
| [0028](adr/0028-custom-built-pg-postgis-intel.md) | Custom-built PostgreSQL 18 + PostGIS 3.6.3 with the Intel toolchain. |
| [0030](adr/0030-mkl-eigen-spectra-tbb-integration.md) | MKL / Eigen / Spectra / TBB integration + determinism via `MKL_CBWR` — CPU-native math, no CUDA (foundation truth 9). |
| [0032](adr/0032-unified-cmake-build-pipeline.md) | Unified CMake build pipeline (Path B) — PGXS retired in favor of one tree. |
| [0033](adr/0033-all-deps-as-submodules.md) | All direct dependencies as git submodules — "code against the repo, not the package." |
| [0038](adr/0038-unified-deps-cmake-pipeline-gcc-toolchain.md) | Unified deps CMake pipeline; gcc toolchain for system deps. |
| [0046](adr/0046-persistent-submodule-cache.md) | `/opt/laplace/external/` as the canonical source for dependency checkouts. |
| [0053](adr/0053-perfcache-compile-time-build-pipeline.md) | Perfcache compile-time build pipeline — CMake stage producing the binary as a deployment artifact. |

### 2e. PG extension & storage layout

| ADR | Title — corrected purpose |
|---|---|
| [0025](adr/0025-pg-extension-modularization.md) | PG extension modularization — `laplace_geom` + `laplace_substrate`. |
| [0031](adr/0031-custom-am-spike.md) | Custom Access Method backed by perf-cache — research spike, post-v0.1.0. |
| [0034](adr/0034-modular-sql-via-cpp-preprocessor.md) | Modular extension SQL via `.sql.in` + C preprocessor (PostGIS pattern). |
| [0042](adr/0042-bootstrap-order-and-substrate-canonical-seeding.md) | Bootstrap order + substrate-canonical seeding — T0 codepoints seeded first (foundation truth 7). |

### 2f. Operations, CI & process

| ADR | Title — corrected purpose |
|---|---|
| [0006](adr/0006-perfcache-and-db-seed-siblings.md) | Perf-cache and DB seed as sibling artifacts of the UCD. |
| [0013](adr/0013-two-tier-cicd.md) | Two-track CI/CD — hosted for PR validation, self-hosted for integration. |
| [0014](adr/0014-self-hosted-runner.md) | Self-hosted GitHub Actions runner on hart-server. |
| [0016](adr/0016-reusable-helpers-discipline.md) | Reusable helpers — DRY at every layer. |
| [0017](adr/0017-agent-operating-cadence.md) | Agent operating cadence — proactive issue + status maintenance. |
| [0018](adr/0018-three-layer-architecture.md) | Three-layer architecture — bootstrap / CI / local dev. |
| [0019](adr/0019-laplace-runner-system-account.md) | Dedicated `laplace-runner` system account for the CI runner. |
| [0020](adr/0020-conventional-commits-and-release-please.md) | Conventional Commits + SemVer (release-please removed). |
| [0021](adr/0021-dbup-for-migrations.md) | DbUp + Npgsql for migrations. |
| [0022](adr/0022-adrs-as-decision-format.md) | ADRs as the decision-record format. |
| [0023](adr/0023-extension-owns-schema-dbup-orchestrates.md) | Extension owns its schema; DbUp orchestrates extension lifecycle. |
| [0026](adr/0026-csharp-project-structure.md) | C# project structure — orchestration-only, mirroring engine modularization. |
| [0027](adr/0027-separation-of-concerns-invariants.md) | Separation-of-concerns invariants — math in C/C++, orchestration in C#. |
| [0045](adr/0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md) | `laplace_admin` is a SUPERUSER; supersedes the `laplace_priv` SECURITY DEFINER wrapper. |
| [0054](adr/0054-selective-deployment-profiles.md) | Selective deployment profiles — embedded / read-only-server / full-server. |
| [0060](adr/0060-retire-chunk-sequence-v0.1-milestone-cadence.md) | Retire the chunk-sequence roadmap — track by v0.1 milestone + component, reconcile the tracker to code reality. |

---

## 3. Research & working notes (`docs/research/`)

These are scratch / dated working notes, not invariants. Treat them as history.

| File | Purpose |
|---|---|
| [research/grounded-model-codec-foundation-2026-05-28.md](research/grounded-model-codec-foundation-2026-05-28.md) | Working note that crystallized into the ratified foundation: model ingest = streaming weight-cell observations, T0 = codepoints only. (The lens supersedes any drift here.) |
| [research/substrate-algorithms.md](research/substrate-algorithms.md) | Research scratchpad of substrate-native algorithms (Procrustes / Laplacian-eigenmaps / Gram-Schmidt morph onto the S³ frame; A\* primitives). |
| [research/stream-plan-2026-05-27.md](research/stream-plan-2026-05-27.md) | "Repair the substrate, then build the substrate" — the multi-stream work plan that drove the codec revert and Stream A–H scaffolds. |
| [research/drift-register-2026-05-28.md](research/drift-register-2026-05-28.md) | Register of identified conventional-AI drifts to be corrected toward the foundation. |
| [research/pending-r12-diffs-2026-05-28.patch](research/pending-r12-diffs-2026-05-28.patch) | Queued doc diffs (DESIGN.md et al.) awaiting R12 user authorization before landing. |

---

## 4. Specialized agents (`.claude/agents/`)

Spawn via the `Agent` tool with `subagent_type` = the file name. Use them for their domains
per CLAUDE.md hard rule 5; they navigate the corpus but do not replace it.

| Agent | Domain |
|---|---|
| [substrate-architect](../.claude/agents/substrate-architect.md) | Holds the canonical substrate / geometric model — S³ + 4-ball + Hilbert, tier hierarchy, attestation taxonomy, prompt ingestion, indexed-A\* cascade, arena/source-trust, synthesis. Forbidden from GPU / matmul / conventional-AI suggestions. |
| [conventional-ai-skeptic](../.claude/agents/conventional-ai-skeptic.md) | Adversarial guard — catches drift toward HNSW / FAISS / RAG / fine-tuning / GEMM-on-hot-path / cosine-NN / flat thresholds / raw-vote consensus / recursive-SQL traversal before it enters the codebase. |
| [cpp-performance](../.claude/agents/cpp-performance.md) | C/C++ engine — SIMD/AVX2(-512), oneMKL, Eigen, Spectra, oneTBB, BLAKE3, cascade/A\* frontier, cache-friendly layout, determinism pinning, C-ABI design. No approximate-NN or gradient libraries. |
| [postgres-extension](../.claude/agents/postgres-extension.md) | PG extension authoring — unified CMake, `.sql.in` modules, function registration, GIST/SP-GiST/BRIN opclasses, Glicko-2 aggregate, set-returning cascade functions, memory contexts. PG 18 + PostGIS 3.6.3. |
| [type-taxonomy](../.claude/agents/type-taxonomy.md) | Curator of the attestation-kind vocabulary — base classes, per-architecture mechanical-role kinds, per-source-schema types, cross-source equivalence, observation-resolved arena/source-trust semantics. |
| [ingestion-pipeline](../.claude/agents/ingestion-pipeline.md) | Source-plugin design — ISource, prompt ingestion, layered seed order, parse-based + probe-based plugins, morph/alignment pipeline, matchup-consensus sparsity, source lineage/trust, recipe extraction. |
| [verification](../.claude/agents/verification.md) | Catches correctness regressions before they cascade — determinism checks, hash-roundtrip, cross-machine reproducibility, perf-cache-vs-DB integrity. |
