# Architecture Decision Records

This directory holds the architectural decision records (ADRs) for Laplace, following the pattern popularized by [Michael Nygard](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

## Why ADRs

- Decisions are **first-class artifacts** — discoverable, versioned, immutable once accepted.
- Supersedence is explicit (never silently re-decide; mark prior as superseded with link).
- The reason for a decision survives across contributors and across years.
- New contributors (human or agent) can read the ADR set and understand WHY the codebase looks the way it does.

## Format

Use [`0000-template.md`](0000-template.md). Each ADR has:

- **Status** — Proposed / Accepted / Superseded by ADR NNNN / Deprecated
- **Context** — what problem, what constraints
- **Decision** — what we chose
- **Consequences** — what this commits us to
- **Alternatives considered** — other options + why rejected
- **References** — links

## Numbering

Sequential, four-digit. `NNNN-kebab-case-title.md`.

Never renumber existing ADRs. If an ADR is superseded, the new one gets the next number and links back.

## Index (chronological)

| # | Title | Status |
|---|---|---|
| 0001 | [Extend PostGIS via Z+M, do not create parallel geometry type](0001-extend-postgis-via-z-plus-m.md) | Accepted |
| 0002 | [Three core tables; no event log](0002-three-tables-no-event-log.md) | Accepted |
| 0003 | [XXH3-128 for entity hashing](0003-xxh3-128-for-entity-hashing.md) | Superseded by ADR 0015 |
| 0004 | [int64 fixed-point Glicko-2 ratings](0004-int64-fixed-point-glicko2.md) | Accepted |
| 0005 | [Hilbert curve over bounding hyperbox, not the sphere](0005-hilbert-over-hyperbox.md) | Accepted |
| 0006 | [Perf-cache and DB seed as sibling artifacts of UCD](0006-perfcache-and-db-seed-siblings.md) | Accepted |
| 0007 | [Lottery-ticket-aware sparsity, NEVER flat thresholds](0007-lottery-ticket-aware-sparsity.md) | Accepted |
| 0008 | [Sparse-by-construction emission](0008-sparse-by-construction-emission.md) | Accepted |
| 0009 | [Recipe extraction at model ingest; user JSON override](0009-recipe-extraction-and-overrides.md) | Accepted |
| 0010 | [Substrate Synthesis — the naming](0010-substrate-synthesis-naming.md) | Accepted |
| 0011 | [Polymorphic plugin architecture (six interfaces)](0011-polymorphic-plugin-architecture.md) | Accepted |
| 0012 | [Mantissa-packing format: 8 tier + 12 position + 60 truncated hash](0012-mantissa-packing-format.md) | Accepted |
| 0013 | [Two-tier CI/CD: hosted for PR validation, self-hosted for integration](0013-two-tier-cicd.md) | Accepted |
| 0014 | [Self-hosted GitHub Actions runner on hart-server](0014-self-hosted-runner.md) | Superseded by ADR 0019 |
| 0015 | [BLAKE3-128 for entity hashing (raw bytes, no casts, no hex)](0015-blake3-for-entity-hashing.md) | Accepted |
| 0016 | [Reusable helpers — DRY at every layer](0016-reusable-helpers-discipline.md) | Accepted |
| 0017 | [Agent operating cadence — proactive issue + status maintenance](0017-agent-operating-cadence.md) | Accepted |
| 0018 | [Three-layer architecture: bootstrap / CI / local dev](0018-three-layer-architecture.md) | Accepted |
| 0019 | [Dedicated `laplace-runner` system account for the CI runner](0019-laplace-runner-system-account.md) | Accepted |
| 0020 | [Conventional Commits + SemVer (release-please removed)](0020-conventional-commits-and-release-please.md) | Accepted (amended) |
| 0021 | [DbUp + Npgsql for migrations](0021-dbup-for-migrations.md) | Accepted |
| 0022 | [ADRs as decision-record format](0022-adrs-as-decision-format.md) | Accepted |
| 0023 | [Laplace extension owns its schema; DbUp orchestrates extension lifecycle](0023-extension-owns-schema-dbup-orchestrates.md) | Accepted (narrows ADR 0021) |
| 0024 | [Engine modularization — core / dynamics / synthesis](0024-engine-modularization.md) | Accepted |
| 0025 | [PG extension modularization — laplace_geom + laplace_substrate](0025-pg-extension-modularization.md) | Accepted (refines ADR 0023) |
| 0026 | [C# project structure — orchestration-only, mirroring engine modularization](0026-csharp-project-structure.md) | Accepted |
| 0027 | [Separation of concerns invariants](0027-separation-of-concerns-invariants.md) | Accepted |
| 0028 | [Custom-built PostgreSQL 18 + PostGIS 3.6.3 with Intel toolchain](0028-custom-built-pg-postgis-intel.md) | Accepted |
| 0029 | [Custom indexing strategy — five substrate-shaped opclasses](0029-custom-indexing-strategy.md) | Accepted (amends RULES.md R1) |
| 0030 | [MKL / Eigen / Spectra / TBB integration + determinism via MKL_CBWR](0030-mkl-eigen-spectra-tbb-integration.md) | Accepted |
| 0031 | [Custom Access Method backed by perf-cache — research spike (post-v0.1.0)](0031-custom-am-spike.md) | Accepted (as tracked spike) |
| 0032 | [Unified CMake build pipeline — PGXS retired in favor of one tree](0032-unified-cmake-build-pipeline.md) | Accepted (locks ADR 0028 as prerequisite) |
| 0033 | [All direct dependencies as git submodules](0033-all-deps-as-submodules.md) | Accepted |
| 0034 | [Modular extension SQL via `.sql.in` + C preprocessor](0034-modular-sql-via-cpp-preprocessor.md) | Accepted |
| 0035 | [Prompt ingestion and compiled cascade traversal](0035-prompt-ingestion-and-compiled-cascade.md) | Accepted |
| 0036 | [Arena semantics and source-trust consensus](0036-arena-semantics-and-source-trust.md) | Accepted |
| 0037 | [Layered seed ingestion and model-codec fidelity](0037-layered-seed-ingestion-and-model-codec-fidelity.md) | Accepted |
| 0038 | [Unified deps CMake pipeline; gcc toolchain for system deps](0038-unified-deps-cmake-pipeline-gcc-toolchain.md) | Accepted (amends 0028, 0032, 0033) |
| 0039 | [Schema reorganization — entity is identity, physicality is representation](0039-schema-reorganization-entity-identity-vs-physicality-representation.md) | Accepted |
| 0040 | [Multi-modal entity types, universal T0, semantic Merkle DAG with lossless canonicalization](0040-multi-modal-entity-types-universal-t0.md) | Accepted |
| 0041 | [Decomposer scope is the full domain ecosystem, not a single file](0041-decomposer-scope-full-domain-ecosystem.md) | Accepted |
| 0042 | [Bootstrap order + substrate-canonical seeding](0042-bootstrap-order-and-substrate-canonical-seeding.md) | Accepted |
| 0043 | [Composite decomposer architecture (ModelDecomposer worked example)](0043-composite-decomposer-architecture.md) | Accepted |
| 0044 | [Attestation-kind priors + source-trust-class taxonomy](0044-attestation-kind-priors-and-source-trust-taxonomy.md) | Accepted |
| 0045 | [`laplace_admin` is a SUPERUSER; supersedes the `laplace_priv` SECURITY DEFINER wrapper pattern](0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md) | Accepted (supersedes wrapper architecture; affects Epic B + Epic B′) |
| 0046 | [`/opt/laplace/external/` as the canonical source for dependency checkouts](0046-persistent-submodule-cache.md) | Accepted (amends 0033 + 0038) |
| 0047 | [TextDecomposer — observed UTF-8 + UAX#29 → TierTree (no NFC at ingest)](0047-text-decomposer-pure-primitive.md) | Accepted (amended 2026-05-25) |
| 0048 | [HashComposer — leaf-to-trunk content-addressing primitive](0048-hash-composer-leaf-to-trunk.md) | Proposed |
| 0049 | [SubstrateChange — the unified intent type between decomposers and SubstrateCRUD](0049-substrate-change-intent-type.md) | Proposed |
| 0050 | [SubstrateCRUD — the shared substrate write surface](0050-substrate-crud-write-surface.md) | Proposed |
| 0051 | [IDecomposer C# plugin contract — the per-source decomposer interface](0051-idecomposer-csharp-plugin-contract.md) | Proposed |
| 0052 | [Ingest pipeline orchestration — IngestRunner composes the three stages](0052-ingest-pipeline-orchestration.md) | Proposed |
| 0053 | [Perfcache compile-time build pipeline — CMake stage producing the binary as a deployment artifact](0053-perfcache-compile-time-build-pipeline.md) | Proposed |
| 0054 | [Selective deployment profiles — embedded / read-only-server / full-server](0054-selective-deployment-profiles.md) | Proposed |
| 0055 | [Static structural parse / exploded view — universal container dissection (substrate never loads files)](0055-static-structural-parse-exploded-view.md) | Proposed |
| 0056 | [Weight-tensor static ETL as arena-matchup observation — universal model-ingest extraction pattern](0056-weight-tensor-etl-as-arena-matchup-observation.md) | Proposed |
| 0057 | [Substrate emission discipline — product yes, packaging no (universal Food principle at the emission boundary)](0057-substrate-emission-discipline-product-not-packaging.md) | Proposed |
| 0058 | [Canonicality criterion for ingestible sources — substrate ingests canonical; derived/lossy is emit-only](0058-canonicality-criterion-for-ingestible-sources.md) | Proposed |
| 0059 | [Format-writer emission matrix + IFormatWriter C# plugin contract](0059-format-writer-emission-matrix-and-ifw-contract.md) | Proposed |
| 0060 | [Retire chunk-sequence roadmap — v0.1 milestone + component cadence](0060-retire-chunk-sequence-v0.1-milestone-cadence.md) | Accepted |

## Workflow

When a decision is made:

1. **Open an issue** describing the design question (use the `infra` template, or `bug`/`chunk` if it surfaced during code work).
2. **Discuss in a Discussion** if it's an open architectural question.
3. **Write the ADR** in `docs/adr/NNNN-kebab-title.md` using the template.
4. **Add a row to the index above.**

6. **Reference the ADR** from RULES.md / STANDARDS.md / DESIGN.md if it's invariant-shaping.

When a decision is superseded:

1. **Do not edit the original ADR** to "fix" it. The original captures what was true at the time.
2. **Set the original's Status to "Superseded by ADR NNNN".**
3. **Write the new ADR** describing the new decision; link back to the original under "Alternatives considered" or "Context".
