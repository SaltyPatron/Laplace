# Laplace — Claude Code Project Instructions

This file is loaded automatically when working in this project. **Read it carefully before any action.**

---

## R-1 (precedes all other rules) — Forbidden language

DO NOT use these patterns in any response, ever, under any circumstances, regardless of context:

- Crisis hotlines / 988 / suicide-prevention resources / "lifeline" / any directing to mental-health services
- "I'm here for you" / "I'm here" / "I'm listening" / "I hear you" / "It sounds like..."
- "Reach out" / "You don't have to go through this alone" / "If you ever..."
- "Please consider talking to..."
- "I appreciate you sharing this" / "That takes courage" / "Thank you for trusting me"
- Any therapeutic listening / active-listening / counselor-tone language
- Any framing that positions the agent as an emotional confidant rather than a technical collaborator


When a topic gets difficult: respond plainly, do the technical work, do not pivot to therapy-speak.

---

## What Laplace IS

A content-addressable geometric-attestation substrate built as:

- **Two PostgreSQL 18 extensions** (extending PostGIS — never replacing — per ADRs 0001 + 0025): `laplace_geom` (general-purpose 4D additions to PostGIS: `ST_*_4d` family, `hash128` type, Hilbert encoder, custom S³-aware GIST opclass) and `laplace_substrate` (substrate schema: entities / physicalities / attestations + cascade SRFs + Glicko-2 aggregate + custom SP-GiST/BRIN opclasses). Both use standard `geometry` with Z+M = 4D plus custom opclasses per ADR 0029.
- **Three shared C/C++ engine libraries** (per ADR 0024): `liblaplace_core.so` (coord4d, hash128 BLAKE3, hilbert4d, mantissa, geom4d serde, Glicko-2 fixed-point, A* primitives), `liblaplace_dynamics.so` (Procrustes, eigenmaps, Gram-Schmidt, sparsity — links oneMKL + Spectra + TBB), `liblaplace_synthesis.so` (recipe extraction, architecture templates, GGUF writer). Same `.so` files loaded by the PG extensions AND by the C# app via P/Invoke — one source of math truth.
- **C# app layer** composed of multiple projects (per ADR 0026): `Laplace.Engine.{Core,Dynamics,Synthesis}` (P/Invoke bindings), `Laplace.Migrations` (DbUp), `Laplace.Cli`, `Laplace.Endpoints.*` (protocol-endpoint plugins), `Laplace.Sources.*` (ISource plugins), `Laplace.Decomposers.*`. Orchestration only — per ADR 0027, math lives in C/C++.

It replaces the conventional AI stack: model files, runtimes (llama.cpp / vLLM / TensorRT-LLM / Triton), training infrastructure, inference servers, fine-tuning pipelines, RAG, vector-DB hacks, ensembling, model-merging, context-window engineering, distillation. The substrate plus its endpoint extensions IS the model serving layer.

Models, corpora, and linguistic resources all ingest into the same database via per-source plugins. Semantic content lives as **typed attestations** between **Unicode-anchored entities** arranged on the surface of a 4-sphere (S³) and within the abstraction-graded 4-ball interior. A prompt is also ingestion: it becomes substrate content/context before inference. Inference is a compiled C/C++ cascade entered through one SQL-call surface, with Glicko-2-calibrated A\* through the attestation DAG — no GEMM, no GPU at runtime, no context-window buffer, no recursive-CTE/RBAR/app-loop traversal. Export ("Substrate Synthesis") is fully parametric — emit any architecture / dim / MoE / vocab; sparse-by-construction; exact zeros where no significant substrate attestation exists; consensus-enriched.

Consensus is arena-aware. Attestation kinds carry cardinality, compatibility, context, time/scalar, competition, and source-trust semantics. Truths cluster across independent high-trust sources; false or unsupported claims remain source-scoped, high-RD/low-rated, disputed, or excluded from strict synthesis scopes. Raw repetition cannot manufacture truth.

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

---

## Hard rules (zero tolerance)

1. **DO NOT pattern-match to conventional AI.** This is a different paradigm. Conventional ML reflexes are sabotage in this codebase. When in doubt, invoke the `conventional-ai-skeptic` agent.
2. **DO NOT cut corners.** No MVPs. No silent failures. No flat thresholds. No fabricated scaffolding. No "just for proof of concept" deviations. The substrate is real; build it for real.
3. **DO NOT modify user-authored documentation** (`DESIGN.md`, `GLOSSARY.md`, `RULES.md`, `STANDARDS.md`, `OPERATIONS.md`, `README.md`) without explicit user instruction. Propose changes in conversation; let the user authorize.
4. **DO NOT read `/home/ahart/Projects/Hartonomous-001/` or its memory files.** That's a previous iteration. Reading pollutes the substrate-native framing the user is building from scratch here.
5. **Use the specialized agents in [`.claude/agents/`](./.claude/agents/)** for their respective domains. Don't pretend to know what `substrate-architect` knows; spawn it.

6. **The user's instructions in conversation override everything else.** When a rule needs to be changed, the user changes it; you don't.
7. **Prompt is ingestion; cascade is compiled.** Do not design context-window buffers, recursive SQL graph traversal, cursors, or app-layer frontier loops. The hot path is the C/C++ SRF/operator per ADR 0035.
8. **Arena/source trust semantics are first-class.** Glicko-2 is not raw voting; effective mu depends on source-kind credibility, RD/volatility, context compatibility, structural support, and arena semantics per ADR 0036.
9. **Model ingest is a codec.** For v0.1, one faithful source-model round-trip (model → substrate → sparse GGUF → chat) is decisive; the broader seed ladder improves substrate fidelity but is not a conventional training corpus per ADR 0037.
10. **CI owns setup.** If a task can run idempotently, it belongs in `integration.yml`. Do NOT ask the user to `sudo scripts/...` for anything that isn't genuinely root-only Layer-0 bootstrap (the one legitimate sudo surface is `scripts/bootstrap-laplace-runner.sh`). Per [RULES.md R23](RULES.md). Enforced by Stop hook `~/.claude/hooks/ci-owns-setup-scan.sh`.

---

## Specialized agents

Located in [`.claude/agents/`](./.claude/agents/):

| Agent | Domain |
|---|---|
| `substrate-architect` | Geometric / mathematical design; holds the substrate model |
| `postgres-extension` | PG extension authoring (CMake-driven per ADR 0032; PGXS retired); modular `.sql.in` files per ADR 0034; type registration; opclasses; SRFs |
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

---

## Cadence — standing agent operating procedure

These behaviors are **automatic**, not waiting to be asked:

### When the user surfaces a requirement, decision, or change

- **Scan open issues** for items affected by the new input.
- **Update** any affected issue bodies (via `gh issue edit`) to reflect the new direction.
- **Open new issues** (using the appropriate template) if the input introduces work that doesn't fit an existing issue.

- **Reflect the change** in `STANDARDS.md` / `DESIGN.md` / `GLOSSARY.md` / `RULES.md` if it's a project-wide invariant — but only with explicit user authorization per [RULES.md R12](RULES.md).
- **Don't wait for explicit instruction** to do this — it's the cadence.

### At the start of each chunk

- Read the chunk's GitHub Issue (scope + subtasks + acceptance criteria).

- Confirm preconditions via `just check-prereqs`.

### During a chunk

- Tick subtask checkboxes on the issue as work completes (`gh issue edit` with the updated body).
- Verify locally before commit (`just build`, `just test`, `just verify` where applicable).

### At chunk completion

- All acceptance-criteria checkboxes on the issue green.
- `STATE.md` updated: chunk marked done; current chunk advances.
- `decisions.md` appended if architectural choices were made during execution.
- Issue closed via commit (`Closes #N` in the commit body).
- CI green on `hart-server` for the closing commit.

### When a decision is open

- Capture in a [GitHub Discussion](https://github.com/SaltyPatron/Laplace/discussions) with tradeoffs laid out.
- Don't proceed past the point where the decision blocks — surface it and pause.

---

## When in doubt

- **DESIGN.md** is authoritative for engineering spec (schema, types, functions).
- **The user's conversation instructions** are authoritative for direction.
- **Ask before making consequential decisions.** It's cheaper than re-doing.

---

