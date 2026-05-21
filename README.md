# Laplace

A content-addressable geometric-attestation substrate that replaces the conventional AI stack.

Models, corpora, and linguistic resources ingest into a single database via per-source plugins. Semantic content lives as typed attestations between Unicode-anchored entities on the surface of a 4-sphere and within its abstraction-graded interior. Inference is cascading-tier nearest-neighbor with Glicko-2-calibrated A* through the attestation DAG — CPU-native, no GEMM, no GPU at runtime. Export ("Substrate Synthesis") is fully parametric — emit any architecture, dim, MoE config, or vocab; sparse-by-construction; consensus-enriched.

## What Laplace replaces

| Conventional layer | Laplace equivalent |
|---|---|
| Model files (safetensors / GGUF / ONNX) | Substrate state |
| Runtimes (llama.cpp / vLLM / TensorRT-LLM) | Substrate + protocol-endpoint extensions |
| Training (gradient descent) | Ingestion (attestation accumulation) |
| Fine-tuning | `WHERE` clause |
| Distillation | `SELECT ... INTO model_file` |
| Pruning | `DELETE WHERE rating < threshold` |
| Unlearning | `DELETE WHERE source = M` |
| RAG / vector DB | Multi-vertical NN over substrate |
| Context window | Prompt is ingestion — infinite |
| Ensembling | Glicko-2 consensus across sources |
| Knowledge graphs | Substrate's typed attestations |

## Stack

- **PostgreSQL 18** + **PostGIS 3.6** (extended, not replaced — standard `geometry` with Z+M = 4D)
- **C/C++ engine library** — Intel oneMKL, Eigen, Spectra, oneTBB, libxxhash (no HNSWLib, no oneDNN)
- **C# app layer (.NET 10)** — Synthesis surface + plugin host for protocol endpoints
- **PostgreSQL extension** — thin wrappers around the engine, custom 4D-aware functions
- **Unicode UCD** — universal vocabulary (1.114M codepoints on S³)

## Getting started

Read the spec in this order:

1. [GLOSSARY.md](GLOSSARY.md) — terminology
2. [RULES.md](RULES.md) — architectural invariants
3. [STANDARDS.md](STANDARDS.md) — datatype + coding standards
4. [DESIGN.md](DESIGN.md) — schema, types, function inventory
5. [OPERATIONS.md](OPERATIONS.md) — build / run / launch / query

## For AI agents working on this code

See [CLAUDE.md](CLAUDE.md) and [AGENTS.md](AGENTS.md). Specialized agents live in [`.claude/agents/`](./.claude/agents/).

## Status

Early development. See `.agent/status/STATE.md` for current progress.
