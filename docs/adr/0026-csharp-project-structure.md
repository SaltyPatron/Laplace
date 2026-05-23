# ADR 0026: C# project structure — orchestration-only, mirroring engine modularization

## Status

**Accepted** — 2026-05-21

## Context

The C# app layer ([STANDARDS.md](../../STANDARDS.md), [RULES.md R6](../../RULES.md)) is *orchestration only*. All math, hashing, geometry, linalg, sparsity, and codec work lives in the C/C++ engine. C# loads the engine via P/Invoke, drives pipelines, hosts plugins, runs the migrations app, exposes protocol endpoints.

The original plan had a small set of C# projects:

- `Laplace.Engine` — P/Invoke bindings to the single engine `.so`
- `Laplace.Synthesis` — Substrate Synthesis surface
- `Laplace.Endpoints.OpenAI` — OpenAI-compat plugin

That worked for the single-engine-library plan, but [ADR 0024](0024-engine-modularization.md) splits the engine into three libraries (`liblaplace_core`, `liblaplace_dynamics`, `liblaplace_synthesis`), and the substrate's source / decomposer / endpoint plugin space is growing (ISource, IDecomposer, IArchitectureTemplate, IFormatWriter, IFeatureExtractor, IProtocolEndpoint per [RULES.md R10](../../RULES.md)).

A flat 3-project layout would mix binding code with plugin implementations, force ingest-time and export-time code into the same assembly, and obscure the dependency direction.

## Decision

Adopt a modular C# project layout under `app/`, with explicit separation between **binding projects** (one per engine library), **functional projects** (Migrations, Cli, plugin host), and **plugin projects** (one per source / decomposer / endpoint).

### Binding projects (one per engine `.so`)

| Project | P/Invoke target | Purpose |
|---|---|---|
| `Laplace.Engine.Core` | `liblaplace_core.so` | Bindings for coord4d, hash128, hilbert4d, mantissa, geom4d serde, Glicko-2 fixed-point, A* primitives |
| `Laplace.Engine.Dynamics` | `liblaplace_dynamics.so` | Bindings for Procrustes, eigenmaps, Gram-Schmidt, sparsity passes — used only at ingest time |
| `Laplace.Engine.Synthesis` | `liblaplace_synthesis.so` | Bindings for recipe extraction, architecture templates, feature extractors, native package writers, and proof/compatibility writers such as GGUF — used only at export time |

Each binding project declares `[StructLayout(LayoutKind.Sequential)]` POD structs matching the engine C ABI byte-for-byte (per [STANDARDS.md](../../STANDARDS.md) datatype standards). `LibraryImport` source-generators (preferred over `DllImport` in .NET 10) emit the marshalling boilerplate.

### Functional projects (substrate orchestration)

| Project | Purpose |
|---|---|
| `Laplace.Migrations` | DbUp + Npgsql migration runner (existing; per ADR 0021) |
| `Laplace.Cli` | Top-level CLI; `cascade`, `synthesize`, `roundtrip` subcommands |
| `Laplace.Endpoints` | Plugin host for `IProtocolEndpoint` plugins; HTTP/SSE serving stack |
| `Laplace.Sources` | Plugin host for `ISource` plugins; ingestion orchestration |

### Plugin projects (one per concrete implementation)

| Project | Implements |
|---|---|
| `Laplace.Sources.WordNet` | `ISource` for WordNet (lemmas, synsets, sense relations) |
| `Laplace.Sources.Transformer` | `ISource` for Transformer model ingest (uses Engine.Dynamics) |
| `Laplace.Sources.ConceptNet` | `ISource` for ConceptNet |
| `Laplace.Sources.TextCorpus` | `ISource` for plain-text corpora |
| `Laplace.Decomposers.Safetensors` | `IDecomposer` for safetensors tensor unpacking |
| `Laplace.Decomposers.WordNet` | `IDecomposer` for WordNet Prolog/RDF data |
| `Laplace.Decomposers.Text` | `IDecomposer` for text (UAX#29 grapheme clusters via ICU) |
| `Laplace.Endpoints.OpenAi` | `IProtocolEndpoint` for OpenAI-compat HTTP API |
| (future) `Laplace.Endpoints.Anthropic` | `IProtocolEndpoint` for Anthropic-compat API |

### Solution layout

```
app/
├── Laplace.slnx
├── Laplace.Engine.Core/
├── Laplace.Engine.Dynamics/
├── Laplace.Engine.Synthesis/
├── Laplace.Migrations/
├── Laplace.Cli/
├── Laplace.Endpoints/
├── Laplace.Endpoints.OpenAi/
├── Laplace.Sources/
├── Laplace.Sources.WordNet/
├── Laplace.Sources.Transformer/
├── Laplace.Sources.ConceptNet/
├── Laplace.Sources.TextCorpus/
├── Laplace.Decomposers.Safetensors/
├── Laplace.Decomposers.WordNet/
└── Laplace.Decomposers.Text/
```

### Dependency direction

```
Laplace.Cli           depends on Endpoints + Sources + Synthesis + Core
Laplace.Endpoints.*   depends on Endpoints + Engine.Core
Laplace.Sources.*     depends on Sources + Engine.Core + (some Engine.Dynamics)
Laplace.Decomposers.* depends on Engine.Core
Laplace.Migrations    depends on Npgsql + DbUp (no engine dep)
Laplace.Endpoints     depends on Engine.Core
Laplace.Sources       depends on Engine.Core
Laplace.Engine.Synthesis depends on Engine.Dynamics + Engine.Core
Laplace.Engine.Dynamics  depends on Engine.Core
Laplace.Engine.Core      depends on nothing else (root)
```

Plugin projects use the host project's interface contract (e.g., `ISource`) but do NOT depend on each other.

## Consequences

- **Each plugin is one project.** Adding WordNet doesn't require touching any other source. Same for endpoints, decomposers.
- **Build-time isolation.** A bug in `Laplace.Sources.Transformer` doesn't prevent building `Laplace.Sources.WordNet`. Tests can be scoped to one project.
- **Runtime composition** via plugin host loading. `Laplace.Cli` discovers `ISource` implementations at runtime; doesn't reference them by type.
- **Engine binding boundaries match `.so` boundaries.** If you want to call into Procrustes, you depend on `Laplace.Engine.Dynamics`, not on a kitchen-sink `Laplace.Engine`. Compile-time dependency graph mirrors runtime `.so` graph.
- **More projects to track** — ~13+ C# projects at v0.1.0, vs. the prior 3. Mitigated by `Laplace.slnx` as the single coordination point and per-project test discoverability.
- **Plugin discovery convention.** Projects matching the pattern `Laplace.Sources.*` / `Laplace.Endpoints.*` / `Laplace.Decomposers.*` are discovered by the host at runtime via Reflection (or via an `AssemblyAttribute` registration). The convention IS the contract.

## Alternatives considered

- **Status quo: 3 projects (Engine, Synthesis, Endpoints.OpenAI).** Rejected — doesn't scale; conflates binding code with implementation code; misses the natural seams.
- **One project per engine library + one mega-project for plugins.** Rejected — plugin projects would couple-by-being-in-same-assembly; tests would have to load all plugins.
- **Single solution file mixing C# + C++ projects (CMake + .NET hybrid).** Rejected — .NET tooling (`dotnet build`, `dotnet test`) doesn't natively understand C++ projects; cross-language build coordination via the top-level Justfile is cleaner.

## References

- [.NET 10 LibraryImport source generator](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation)
- [.slnx solution format](https://devblogs.microsoft.com/dotnet/introducing-slnx-our-new-solution-file-format/)
- [ADR 0024](0024-engine-modularization.md) — engine `.so` boundaries (matched 1:1 by binding projects)
- [ADR 0025](0025-pg-extension-modularization.md) — PG extension layout (orthogonal axis but same modularization spirit)
- [ADR 0027](0027-separation-of-concerns-invariants.md) — per-layer may/must-not matrix (this ADR establishes the C# side)
- ADR 0021 (DbUp + Npgsql) — `Laplace.Migrations` project
- RULES.md R6 (orchestration in C#; math in C/C++); R10 (polymorphic plugin architecture)
- `app/`, `app/Laplace.slnx`
