# Laplace

[![Integration](https://github.com/SaltyPatron/Laplace/actions/workflows/integration.yml/badge.svg?branch=main)](https://github.com/SaltyPatron/Laplace/actions/workflows/integration.yml)
[![CI](https://github.com/SaltyPatron/Laplace/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/SaltyPatron/Laplace/actions/workflows/ci.yml)
[![Milestone](https://img.shields.io/github/milestones/progress-percent/SaltyPatron/Laplace/1?label=v0.1.0%20Chattable%20Qwen3%20Roundtrip)](https://github.com/SaltyPatron/Laplace/milestone/1)
[![Open Issues](https://img.shields.io/github/issues/SaltyPatron/Laplace?label=open%20issues)](https://github.com/SaltyPatron/Laplace/issues)
[![License: Proprietary](https://img.shields.io/badge/license-Proprietary-red.svg)](LICENSE)
[![PostgreSQL 18](https://img.shields.io/badge/PostgreSQL-18-blue.svg)](https://www.postgresql.org/)
[![PostGIS 3.6](https://img.shields.io/badge/PostGIS-3.6-blue.svg)](https://postgis.net/)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)
[![Intel oneAPI 2026](https://img.shields.io/badge/Intel%20oneAPI-2026-0071c5.svg)](https://www.intel.com/content/www/us/en/developer/tools/oneapi/overview.html)

A content-addressable geometric-attestation substrate that replaces the conventional AI stack.

---

## What this is

Models, corpora, and linguistic resources ingest into a single PostgreSQL database via per-source plugins. Semantic content lives as **typed attestations** between **Unicode-anchored entities** on the surface of a 4-sphere (S³) and within its abstraction-graded interior. Inference is **cascading-tier nearest-neighbor with Glicko-2-calibrated A\*** through the attestation DAG — CPU-native, no GEMM, no GPU at runtime, microsecond response, O(tier) ≈ O(constant) per cascade step regardless of substrate size.

**Substrate Synthesis** emits any model from substrate state: any architecture, any dimensionality, any MoE config, any vocab; sparse-by-construction; consensus-enriched. Protocol-endpoint extensions (OpenAI-compat, ...) make the substrate the served model directly — dissolving llama.cpp / vLLM / TensorRT-LLM from the deployment stack.

## What this replaces

| Conventional layer | Laplace equivalent |
|---|---|
| Model files (safetensors / GGUF / ONNX) | Substrate state |
| Inference runtimes (llama.cpp / vLLM / TensorRT-LLM / Triton) | Substrate + protocol-endpoint extensions |
| Training (gradient descent) | Ingestion (attestation accumulation) |
| Fine-tuning / LoRA / adapters | `WHERE` clause on substrate |
| Distillation | `SELECT ... INTO model_file` |
| Pruning | `DELETE WHERE rating < threshold` |
| Unlearning | `DELETE WHERE source = M` |
| Ensembling | Glicko-2 consensus across sources |
| RAG / vector DB | Multi-vertical NN over substrate |
| Context window | Prompt is ingestion — effectively infinite |

## Architecture

```mermaid
graph TB
    subgraph Clients["Clients (OpenAI SDK / LangChain / agent frameworks / browsers)"]
        Client[Standard HTTP / SSE]
    end
    subgraph App["C# App Layer (.NET 10)"]
        Endpoints[Protocol Endpoints<br/>OpenAI-compat · Anthropic-compat · ...]
        Synthesis[Substrate Synthesis UI<br/>+ recipe management]
        Interop[Native Interop<br/>P/Invoke → engine C ABI]
    end
    subgraph Engine["Engine (C/C++) — 3 shared libs per ADR 0024"]
        Math[liblaplace_core<br/>coord4d · hash128 (BLAKE3) · hilbert4d · mantissa<br/>geometry4d · glicko2 · astar]
        Pipeline[liblaplace_dynamics<br/>Procrustes · Laplacian eigenmaps · Gram-Schmidt<br/>Lottery-ticket-aware sparsity · oneMKL · Spectra · TBB]
        Templates[liblaplace_synthesis<br/>Recipe · LlamaTemplate · feature extractors · GGUFWriter]
        Sources[C# Source plugins → loaded into substrate via Engine.Dynamics<br/>WordNet · UD · Wiktionary · ConceptNet · Transformer · TextCorpus]
    end
    subgraph DB["PostgreSQL 18 + PostGIS 3.6 — 2 extensions per ADR 0025"]
        ExtGeom[laplace_geom<br/>ST_*_4d · hash128 type<br/>laplace_btree_hash128_ops · laplace_gist_s3_ops]
        ExtSub[laplace_substrate<br/>entities · physicalities · attestations<br/>Glicko-2 aggregate · cascade SRFs<br/>laplace_sp_trajectory_ops · laplace_brin_tier_ops]
        Tables[(entities · physicalities · attestations)]
    end
    subgraph Storage["External"]
        UCD[Unicode UCD]
        Models["/vault/models/ — ingested models"]
        Corpora["/vault/Data/ — linguistic resources + text corpora"]
        Perfcache["data/perfcache.bin (mmap'd)"]
    end

    Client --> Endpoints
    Endpoints --> Interop
    Synthesis --> Interop
    Interop -.same .so.- Math
    Ext -.same .so.- Math
    Sources --> Pipeline
    Pipeline --> Math
    Math --> Ext
    Ext --> Tables
    Tables --> Indexes
    UCD --> Perfcache
    UCD --> Tables
    Models --> Sources
    Corpora --> Sources
    Templates --> Math
    Math -.synthesis output.- Output[GGUF / safetensors / ONNX]
```

## Stack

| Component | Choice | Why |
|---|---|---|
| **Database** | PostgreSQL 18 + PostGIS 3.6 (extended via custom 4D-aware functions) | Decades of mature spatial-DB engineering; we **extend** PostGIS rather than replace |
| **Engine** | C/C++ shared library | Native speed; single source of math truth |
| **Linear algebra** | Intel oneMKL (SVD / BLAS / LAPACK) + Eigen (small matrices) + Spectra (sparse eigendecomp) | Industry-standard CPU-native math |
| **Hashing** | BLAKE3 truncated to 128 bits (per ADR 0015) | 128-bit collision-safe for ~10¹⁸ entities; SIMD-vectorized; raw `bytea(16)` end-to-end |
| **App layer** | C# / .NET 10 | Plugin host for protocol-endpoint extensions; Synthesis UI |
| **Compiler** | Intel `icx` / `icpx` 2026 (primary) · `gcc` 11 / `clang` 14 (fallback) | AVX2 baseline → AVX-512 deployment-target dispatch |
| **Build** | CMake + Ninja (engine) · PGXS (extension) · `dotnet build` (app) | Standard tooling per stack |
| **CI/CD** | GitHub Actions: hosted (PR validation) + self-hosted `hart-server` (integration build/test) | Two-tier; trusted self-hosted on push-to-main only |

See [STANDARDS.md](STANDARDS.md) for the locked dependency-source table.

## Status

| | |
|---|---|
| **Current chunk** | Chunk 0 ✅ done → **Chunk 1 in queue** ([#1](https://github.com/SaltyPatron/Laplace/issues/1)) |
| **Milestone** | [v0.1.0 — Chattable Qwen3 Roundtrip](https://github.com/SaltyPatron/Laplace/milestone/1) |
| **Open issues** | [github.com/SaltyPatron/Laplace/issues](https://github.com/SaltyPatron/Laplace/issues) |
| **All chunks** | [Issues filter: chunk-*](https://github.com/SaltyPatron/Laplace/issues?q=is%3Aopen+label%3A%22type%3Aenhancement%22) |
| **Recent CI runs** | [github.com/SaltyPatron/Laplace/actions](https://github.com/SaltyPatron/Laplace/actions) |
| **Roadmap** | [.agent/status/plan.md](.agent/status/plan.md) |

Live project state: [.agent/status/STATE.md](.agent/status/STATE.md)

## Getting started

### Prerequisites

```sh
just check-prereqs
```

Required: PostgreSQL 18, PostGIS 3.6, Intel oneAPI 2026, .NET 10, Eigen 3.4, ICU 70, cmake, ninja, just. (BLAKE3 + Spectra fetched at build time via CMake FetchContent.)

### Build everything

```sh
just build
```

Builds the C/C++ engine (CMake + Ninja), the PG extension (PGXS), and the C# app (dotnet).

### Set up the database

```sh
just setup            # launch PG + install extension + create db + apply schema + seed T0 (one-time)
```

### Iterate

```sh
just ingest wordnet                          # ingest a linguistic source
just query "SELECT count(*) FROM entities;"  # query the substrate
just synthesize recipes/qwen3-roundtrip.json # emit a model from substrate state
just roundtrip /vault/models/qwen3-0.6b      # ingest model → synthesize → load in llama.cpp
just verify                                  # determinism + FK + perf-cache checks
just status                                  # current chunk + open blockers
```

See [OPERATIONS.md](OPERATIONS.md) for the full operational reference.

## Documentation

| File | Purpose |
|---|---|
| [CLAUDE.md](CLAUDE.md) | Project instructions for Claude Code |
| [AGENTS.md](AGENTS.md) | Cross-tool agent spec |
| [GLOSSARY.md](GLOSSARY.md) | Every term defined; anti-vocabulary section flags conventional-AI patterns |
| [RULES.md](RULES.md) | Architectural invariants (zero tolerance) |
| [STANDARDS.md](STANDARDS.md) | Datatype, naming, coding standards; locked dep table |
| [DESIGN.md](DESIGN.md) | Engineering spec — schema, types, function inventory, indexing strategy |
| [OPERATIONS.md](OPERATIONS.md) | Build / launch / ingest / query / synthesize / verify procedures |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Workflow conventions (mostly for agents) |
| [.agent/status/plan.md](.agent/status/plan.md) | Canonical chunk plan |
| [.agent/status/STATE.md](.agent/status/STATE.md) | Current project state |
| [.agent/status/decisions.md](.agent/status/decisions.md) | Append-only decision log |
| [.agent/status/blockers.md](.agent/status/blockers.md) | Open blockers |

## For AI agents working on this code

Start with [CLAUDE.md](CLAUDE.md). Specialized agents live in [.claude/agents/](.claude/agents/).

Hard rules in [RULES.md](RULES.md) — pattern-matching to conventional AI is sabotage. If you reach for HNSW / FAISS / RAG / fine-tuning / GEMM-on-hot-path, **stop** and re-read.

## Authorship

**Sole inventor, designer, and developer:** Anthony Hart ([@SaltyPatron](https://github.com/SaltyPatron)).

AI agent collaboration is via Claude (Anthropic) operating under the constraints in [CLAUDE.md](CLAUDE.md), [RULES.md](RULES.md), and the specialized agents in [.claude/agents/](./.claude/agents/). All inventions, designs, decisions, and direction originate with the copyright holder.

## License

**Proprietary — All rights reserved.** Copyright (c) 2026 Anthony Hart. See [LICENSE](LICENSE).
