---
description: "Use when editing C# under app/ — decomposers, ingestion orchestration, SubstrateCRUD, CLI, migrations runner, endpoints. Covers the IDecomposer streaming contract, content-addressed convergence, consensus-accumulates-at-ingest, and dotnet test. Keywords: decomposer, IDecomposer, SubstrateChange, IngestRunner, ConsensusAccumulatingWriter, Npgsql, CLI, ON CONFLICT, dotnet test."
applyTo: "app/**"
---
# App (C#) — decomposers, ingestion, CLI

Read [AGENTS.md](../../AGENTS.md) first for the invariants; this file is the app-local layer.

- The app owns orchestration, not substrate math: decomposers (witness collection), ingestion pipeline, CRUD wrappers, CLI, migrations runner, endpoint shells. Substrate primitives stay in the engine; schema/functions stay in the extension.
- New source adapters implement [IDecomposer](../../app/Laplace.Decomposers.Abstractions/IDecomposer.cs#L38-L74): `InitializeAsync` → stream `SubstrateChange` via `IAsyncEnumerable` with backpressure → `DisposeAsync`. Streaming is mandatory; frontier-scale sources cannot buffer.
- Content-address everything: the same canonical content must produce the same BLAKE3-128 id, so cross-decomposer convergence is automatic via `ON CONFLICT DO NOTHING`. Never put source/layer/position/magnitude/time into an entity id.
- `SourceId` is `BLAKE3-128("substrate/source/<Name>/v1")`; `LayerOrder` enforces the bootstrap dependency DAG (Layer N requires Layers 0..N-1 complete).
- Consensus accumulates AT INGEST through `ConsensusAccumulatingWriter`; evidence is provenance-only (no values). There is no evidence-replay/batch-rebuild path — do not add one. The CLI documents this at [app/Laplace.Cli/Program.cs](../../app/Laplace.Cli/Program.cs#L126-L131).
- Re-ingesting a completed model is refused by design (it would double-count votes). To re-run, reset with `just db-fresh`; never bypass the guard. See [app/Laplace.Cli/Program.cs](../../app/Laplace.Cli/Program.cs#L745-L775).
- Model paths resolve by convention (`LAPLACE_TINYLLAMA_DIR`, snapshot discovery), never a hardcoded SHA. The DB default for unset `LAPLACE_DB` is `laplace-dev`, not production. See [app/Laplace.Cli/Program.cs](../../app/Laplace.Cli/Program.cs#L47-L52).
- Test with `just test-app` (xUnit + Testcontainers, needs Docker) or `just test-no-docker` for the engine+regress surfaces. See [Justfile](../../Justfile#L356-L358).
