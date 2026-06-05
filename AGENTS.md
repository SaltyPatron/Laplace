# AGENTS.md

Project instructions for coding agents working in Laplace.

## First Reads

- Read [CLAUDE.md](CLAUDE.md#L1) before changing anything. It is binding for conduct and for the core Laplace invariants.
- Use [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#L1) as the design of record. It explicitly says not to pattern-match Laplace back to conventional AI frames such as embeddings-as-nearest-neighbor, model storage, or GPU inference at [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#L13-L18).
- Use dated status documents as evidence, not authority. Re-check current code and the live DB before making status claims. For example, current CLI dispatch includes `generate` at [app/Laplace.Cli/Program.cs](app/Laplace.Cli/Program.cs#L108), with `GenerateAsync` implemented at [app/Laplace.Cli/Program.cs](app/Laplace.Cli/Program.cs#L465), even if older audit text says generation was unwired.
- Link, do not paste, the long-form model: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/INGESTION-STATUS.md](docs/INGESTION-STATUS.md), [docs/IMPLEMENTATION-AUDIT-2026-06-04.md](docs/IMPLEMENTATION-AUDIT-2026-06-04.md), and [docs/STATUS-2026-06-04T18Z.md](docs/STATUS-2026-06-04T18Z.md).

## Core Model

- Content is identity. Source, model, layer, head, position, magnitude, time, language, modality, and user are witnesses, never identity. The current model-ingest law is stated at [docs/INGESTION-STATUS.md](docs/INGESTION-STATUS.md#L37-L45).
- There is one object: a content-addressed entity with trajectory geometry. A relation is content, not a separate edge category. The architectural definition starts at [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#L144).
- Evidence is provenance. Consensus is the materialized signed Glicko-2 attestation that inference reads. Magnitudes are consumed at ingest, not persisted as a value channel; see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#L220-L224) and [CLAUDE.md](CLAUDE.md#L83).
- Inference, generation, audit, and interpretability are SQL reads over consensus and evidence, not forward passes. The spec states this at [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#L32-L34); the installed read surface lives in [extension/laplace_substrate/sql/17_consensus_reads.sql.in](extension/laplace_substrate/sql/17_consensus_reads.sql.in#L7-L95) and [extension/laplace_substrate/sql/20_converse.sql.in](extension/laplace_substrate/sql/20_converse.sql.in#L1056).
- Geometry is structural, not semantic. Relatedness is ranked consensus mu; structural similarity is trajectory shape; containment/proximity is PostGIS geometry. Do not collapse those axes. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#L184-L200) and [CLAUDE.md](CLAUDE.md#L89).
- A model is a seed/witness, not a codec. Do not describe model ingest, query, or export as encode/decode/roundtrip. Use synthesis/re-export/fill-the-mold language for export. The content `roundtrip` and `db-roundtrip` CLI verbs are real, but they are content reconstruction paths, not model-ingest framing.
- Do not add top-k, floors, budgets, pruning, or threshold selectivity at ingest. Zero is the only non-event; selectivity belongs in `ORDER BY` at query time. This law is explicit at [docs/INGESTION-STATUS.md](docs/INGESTION-STATUS.md#L42-L45).

## Verification Discipline

- Before claiming anything about the codebase, read the relevant file or query the live DB and cite file:line or command output.
- Treat code as intent and the live substrate as reality. Use `just query '<sql>'`, `laplace inspect <text>`, or the relevant CLI surface before making DB-backed claims; [CLAUDE.md](CLAUDE.md#L94) states this rule directly.
- Prefer demonstrations over interpretation. When a behavior is exposed through working surfaces such as `inspect`, `converse`, `nn`, `generate`, `db-roundtrip`, or extension SQL SRFs, verify it there instead of arguing from conventional-AI expectations.
- When docs conflict, reconcile them against current source and live DB state instead of picking the more familiar story.
- Do not invent files, flags, APIs, data paths, counts, or model behavior. If a fact matters, verify it.

## Boundaries

Three layers, three owners. Area-specific rules attach automatically when you edit each tree, via the scoped instruction files in [.github/instructions/](.github/instructions/).

- `engine/` (C/C++) owns deterministic substrate primitives — native math, Glicko, geometry, dynamics, synthesis kernels. No application logic. See [.github/instructions/engine.instructions.md](.github/instructions/engine.instructions.md).
- `extension/` (SQL/C) owns schema, functions, read surfaces, and pg_regress tests; the substrate extension is the deployment unit, and `db/migrations/` is orchestration only. See [.github/instructions/extension-sql.instructions.md](.github/instructions/extension-sql.instructions.md).
- `app/` (C#) owns decomposers, ingestion orchestration, CRUD wrappers, CLI, migrations runner, and endpoint shells. See [.github/instructions/app-csharp.instructions.md](.github/instructions/app-csharp.instructions.md).
- New relation kinds belong in `KindRegistry.Canon` (no per-kind SQL DDL); new substrate tables/functions/seed/verification surfaces belong in an [extension/laplace_substrate/sql/](extension/laplace_substrate/sql/) module plus a regress pin, never in a DbUp migration. See [extension/laplace_substrate/sql/README.md](extension/laplace_substrate/sql/README.md#L31-L38).

## Build And Test

- Use [Justfile](Justfile) as the command surface. `just build` is the canonical CMake/Ninja entry point and configures the Intel oneAPI toolchain, staged install defaults, and PG prefix at [Justfile](Justfile#L105-L111).
- Keep the build-tree library path discipline. `just build`, `just synthesize`, `just verify-perfcache`, and `just test-engine` set `LD_LIBRARY_PATH` so stale installed libraries do not poison runs; see [Justfile](Justfile#L99-L111) and [Justfile](Justfile#L362-L367).
- Use `just test-engine` for C++ GoogleTest/CTest, `just regress` for extension pg_regress, and `just test-app` for .NET/xUnit/Testcontainers. `just test-no-docker` runs the engine and extension surfaces without Docker; commands are defined at [Justfile](Justfile#L356-L383).
- Use `just verify` for determinism, FK, and perfcache validation; the FK check is wired at [Justfile](Justfile#L319-L320).
- Model paths are by convention, not hardcoded snapshots. `just ingest-tinyllama` and `just synthesize-tinyllama` require `LAPLACE_TINYLLAMA_DIR`; see [Justfile](Justfile#L266-L300).
- Database defaults are intentionally recoverable: the CLI defaults unset `LAPLACE_DB` to `laplace-dev`, not production, in [app/Laplace.Cli/Program.cs](app/Laplace.Cli/Program.cs#L47-L52).

## Common Failure Patterns

- Do not put source-derived or witness-derived facts into entity ids. Source, layer, head, position, magnitude, time, and trust belong in evidence or consensus mechanics, never identity.
- Do not use geometry as a synonym for semantic relatedness. S^3 placement is a structural axis; ranked consensus mu is the relatedness axis.
- Do not reintroduce evidence replay or batch rebuild as a consensus path. Consensus accumulates at ingest; evidence is provenance-only. The CLI comment documents this at [app/Laplace.Cli/Program.cs](app/Laplace.Cli/Program.cs#L126-L131).
- Do not bypass the extension module boundary by putting substrate DDL in DbUp migrations.
- Do not repair generation, inference, or synthesis by secretly running the original model, doing query-time GEMM, materializing dense vocab-squared token relations, or treating training data as privileged identity. Those moves translate Laplace back into the system it is deliberately inverting.