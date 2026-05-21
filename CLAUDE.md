# Laplace — Claude Code Project Instructions

This file is loaded automatically when working in this project. **Read it carefully before any action.**

---

## What Laplace IS

A content-addressable geometric-attestation substrate built as:

- **PostgreSQL 18 extension** (extending PostGIS — never replacing — using standard `geometry` with Z+M = 4D and `gist_geometry_ops_nd` for indexing)
- **Shared C/C++ engine library** (same `.so` loaded by the PG extension AND by the C# app via P/Invoke — one source of math truth)
- **Thin C# app layer** for orchestration + plugin host for protocol-endpoint extensions (OpenAI-compat, etc.)

It replaces the conventional AI stack: model files, runtimes (llama.cpp / vLLM / TensorRT-LLM / Triton), training infrastructure, inference servers, fine-tuning pipelines, RAG, vector-DB hacks, ensembling, model-merging, context-window engineering, distillation. The substrate plus its endpoint extensions IS the model serving layer.

Models, corpora, and linguistic resources all ingest into the same database via per-source plugins. Semantic content lives as **typed attestations** between **Unicode-anchored entities** arranged on the surface of a 4-sphere (S³) and within the abstraction-graded 4-ball interior. Inference is **cascading-tier nearest-neighbor** with **Glicko-2-calibrated A\*** through the attestation DAG — no GEMM, no GPU at runtime, CPU-native, microsecond response, O(tier) ≈ O(constant). Export ("Substrate Synthesis") is fully parametric — emit any architecture / dim / MoE / vocab; sparse-by-construction; consensus-enriched.

## What Laplace IS NOT

- NOT another AI framework
- NOT a wrapper around llama.cpp / vLLM / TensorRT-LLM / Ollama
- NOT a RAG system
- NOT a conventional vector database (HNSW / FAISS / ScaNN / Milvus / Pinecone)
- NOT fine-tuning, distillation, model merging, LoRA, adapters, or any gradient-based pipeline
- NOT a knowledge graph in the conventional Neo4j/RDF sense
- NOT a database with AI bolted on top

**If your reasoning starts with "this is like X" where X is any of the above, STOP.** Re-read [RULES.md](RULES.md). Engage the `conventional-ai-skeptic` agent.

---

## Read order before any work

1. **This file (CLAUDE.md)**
2. **[GLOSSARY.md](GLOSSARY.md)** — terminology lock; every term means exactly what's defined there
3. **[RULES.md](RULES.md)** — architectural invariants you MUST NOT violate
4. **[STANDARDS.md](STANDARDS.md)** — datatype, naming, coding standards
5. **[DESIGN.md](DESIGN.md)** — engineering spec: schema, types, function inventory, indexing strategy
6. **[OPERATIONS.md](OPERATIONS.md)** — build / run / launch / update / query procedures
7. **Memory files** at `/home/ahart/.claude/projects/-home-ahart-Projects-Laplace/memory/` — durable cross-conversation context (substrate concepts, anti-sabotage rules, performance regime)

---

## Hard rules (zero tolerance)

1. **DO NOT pattern-match to conventional AI.** This is a different paradigm. Conventional ML reflexes are sabotage in this codebase. When in doubt, invoke the `conventional-ai-skeptic` agent.
2. **DO NOT cut corners.** No MVPs. No silent failures. No flat thresholds. No fabricated scaffolding. No "just for proof of concept" deviations. The substrate is real; build it for real.
3. **DO NOT modify user-authored documentation** (`DESIGN.md`, `GLOSSARY.md`, `RULES.md`, `STANDARDS.md`, `OPERATIONS.md`, `README.md`) without explicit user instruction. Propose changes in conversation; let the user authorize.
4. **DO NOT read `/home/ahart/Projects/Hartonomous-001/` or its memory files.** That's a previous iteration. Reading pollutes the substrate-native framing the user is building from scratch here.
5. **Use the specialized agents in [`.claude/agents/`](./.claude/agents/)** for their respective domains. Don't pretend to know what `substrate-architect` knows; spawn it.
6. **Status tracking goes in `.agent/status/`** — never in user-facing documentation.
7. **The user's instructions in conversation override everything else.** When a rule needs to be changed, the user changes it; you don't.

---

## Specialized agents

Located in [`.claude/agents/`](./.claude/agents/):

| Agent | Domain |
|---|---|
| `substrate-architect` | Geometric / mathematical design; holds the substrate model |
| `postgres-extension` | PG extension authoring; PGXS; type registration; opclasses; SRFs |
| `cpp-performance` | SIMD / AVX2, Eigen, oneMKL, cache behavior, memory layout |
| `type-taxonomy` | Attestation kind hierarchy; per-architecture and per-source-schema types |
| `ingestion-pipeline` | Source plugin authoring; probe protocols; lottery-ticket sparse recording |
| `verification` | Determinism checks; hash-roundtrip tests; cross-machine reproducibility |
| `conventional-ai-skeptic` | Challenges drift toward GEMM / embedding / learned-NN / RAG / fine-tuning patterns |

Spawn them via the `Agent` tool with `subagent_type` matching the file name.

---

## Iteration framework

See [OPERATIONS.md](OPERATIONS.md) for the canonical procedures. Common commands live in the [Justfile](Justfile) at project root. Scripts live in `scripts/`.

Quick reference:

```sh
just build           # Build engine library + PG extension
just launch          # Start Postgres cluster + load extension
just seed            # Seed T0 codepoint entities from Unicode UCD
just ingest <source> # Run an ingestion plugin
just query "<sql>"   # Run a substrate query
just synthesize <recipe.json>  # Substrate Synthesis emission
just verify          # Integrity checks (determinism, FK, perf-cache vs DB)
just status          # Agent-tracked progress + open blockers
just clean           # Clean build artifacts
just test            # Run all tests
```

---

## Status tracking — agent territory

`.agent/status/` is where agents track progress, decisions, and blockers. Do NOT put status updates in user-facing documentation. Status files use a structured format defined in `.agent/README.md`.

---

## When in doubt

- **Memory files** at `/home/ahart/.claude/projects/-home-ahart-Projects-Laplace/memory/` are authoritative for substrate concepts.
- **DESIGN.md** is authoritative for engineering spec (schema, types, functions).
- **The user's conversation instructions** are authoritative for direction.
- **Ask before making consequential decisions.** It's cheaper than re-doing.

---

## Memory pointers (durable context across conversations)

- `MEMORY.md` — index
- `project_laplace_invention.md` — full invention spec
- `project_laplace_performance.md` — performance regime + libraries + datatype standards
- `project_laplace_origin.md` — origin context (do-not-read-Hartonomous-files)
- `feedback_no_sabotage.md` — anti-sabotage rules (zero tolerance)
- `feedback_memory_discipline.md` — memory hygiene
