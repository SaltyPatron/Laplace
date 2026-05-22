# RULES.md — Architectural Invariants (Zero Tolerance)

These rules MUST NOT be violated. They are not preferences. They are not suggestions. Violating any of them is **sabotage** of the project. If a rule needs to change, the user changes it via explicit instruction.

If you (human or agent) catch yourself reasoning toward a violation, STOP. Re-read this file. Engage the `conventional-ai-skeptic` agent.

---

## R0 — No pattern-matching to conventional AI

Laplace is a different paradigm. Conventional ML reflexes (gradient descent, embedding spaces, attention as matmul, vector NN, RAG, fine-tuning, distillation pipelines, context windows, model files as artifacts) are **not how this works**.

If your reasoning starts with "this is like X" where X is conventional AI tooling, you are pattern-matching. Stop. The substrate replaces X, not mimics it.

**Banned reflexive moves:**

- Suggesting HNSW / FAISS / ScaNN / Milvus / Pinecone or any approximate-NN library
- Treating the substrate as "a vector database"
- Suggesting RAG patterns (retrieval-augmented generation)
- Suggesting fine-tuning / LoRA / adapters
- Suggesting distillation-by-training (student against teacher logits)
- Treating attention as needing matmul on the hot path
- Treating context window as a buffer with a size limit
- Treating model export as needing gradient descent
- Treating ensembling as weight-averaging across same-architecture models
- Treating model files as the authoritative knowledge artifact

Each of these has a Laplace-native replacement. See [GLOSSARY.md](GLOSSARY.md) and [DESIGN.md](DESIGN.md).

---

## R1 — Extend PostGIS, never replace

Use standard PostGIS `geometry` type with Z+M = 4D (X, Y, Z spatial + M as fourth spatial dim). Inherit `ST_X`, `ST_Y`, `ST_Z`, `ST_M`, `ST_MakePoint`, `ST_NDims`, `ST_HasZ`, `ST_HasM`, `ST_PointN`, `ST_NumPoints`, `ST_Force4D`, WKB I/O — all free.

**Write custom functions ONLY where standard PostGIS is 2D/3D-only** (centroid, distance, dwithin, length, Fréchet, Hausdorff). Naming convention: `ST_*_4d` (e.g., `ST_distance_4d`, `ST_centroid_4d`) in the `laplace_geom` extension.

**Do NOT** create a parallel `geometry4d` type.

**Custom GIST/SP-GiST/BRIN opclasses ARE permitted** where they exploit substrate-specific structure that general-purpose opclasses can't — per [ADR 0029](docs/adr/0029-custom-indexing-strategy.md). Each custom opclass requires its own ADR documenting the structural fact exploited + measurable acceptance benchmark vs the stock alternative. Speculatively replacing working general-purpose opclasses is still forbidden.

---

## R2 — Three tables, no event log

The substrate has **exactly three** core tables: `entities`, `physicalities`, `attestations`. No `observations` table — that was over-engineering. Attestation rows ARE the consensus state; repeated observations from the same source UPSERT-no-op (`ON CONFLICT DO NOTHING`).

Provenance lives in the `source_hash` column of attestation rows, NOT in a separate event log.

---

## R3 — Lottery-ticket-aware sparsity, NEVER flat thresholds

For AI model sources, the noise floor is a **multi-pass relative filter**:

1. **Per-tensor relative top-k%** (e.g., top 5% by importance within each tensor — respects tensor's own scale)
2. **Per-row top-k for attention / MLP** (preserves load-bearing IO connectivity)
3. **Probe-validated retention test** (synthesize candidate sparse subgraph; verify behavior preserved on probe set)

**A flat numeric cutoff (e.g., `|w| < 0.001`) is forbidden.** It destroys content: different tensors have different magnitude regimes; load-bearing weights are sometimes small.

For **linguistic resources** (WordNet, OMW, UD, Wiktionary, Tatoeba, ConceptNet, Atomic2020) — every entry is curated and deliberate. **No noise floor applies.** Every attestation goes in at full fidelity.

---

## R4 — Sparse-by-construction emission

At export, positions in the target tensor with no significant substrate attestation emit **zero**. This makes emitted models automatically:

- Pruned (5–20% non-zero typical)
- Ensembled (Glicko-2 consensus across all relevant sources)
- Cleaned (no gradient jitter, no init residue)

Default output format for chattable proofs: GGUF with appropriate Q-format (so sparsity benefits are realized in llama.cpp).

---

## R5 — Attestation IS consensus state, NOT event log

One row per `(subject, kind, object, source, context)` tuple. Idempotent on repeat (`INSERT ON CONFLICT DO NOTHING`). The same source asserting the same thing N times does NOT update the rating N times.

Glicko-2 dynamics live in **source-credibility-per-kind**, not per-tuple repetition. Updates fire only on cross-source agreement/disagreement evidence.

---

## R6 — DB as dumb columnar store; entity math in C/C++

Postgres stores rows and maintains indices. It does NOT compute hashes, coordinates, Hilbert indices, centroids, or linestrings. Those values arrive at INSERT time fully pre-baked by the C/C++ engine.

**Only** the Glicko-2 update path runs SQL-side (via `CREATE AGGREGATE`). Everything else is precomputed.

---

## R7 — Determinism by construction

Perf-cache and DB seed are sibling artifacts, **both derived independently from Unicode UCD**. Neither feeds the other. Same UCD version + same derivation algorithm → byte-identical artifacts on every machine.

All entity math uses pinned FP regime (or fixed-point integer arithmetic for Glicko-2). Cross-machine reproducibility is non-negotiable.

---

## R8 — No GPU at runtime

The substrate's runtime is CPU-native. Cascading-tier NN + spatial index lookups + Glicko-2 weighted A* are latency-bound and branchy — wrong workload for GPU.

**The one exception:** probe-time forward-pass of a model being ingested (running a 70B transformer to extract attestations may need GPU). After extraction, the substrate is CPU-only. GPU is the probe driver, NOT a runtime requirement.

---

## R9 — No corner-cutting, no MVPs, no scaffolding

Anti-sabotage rules from [`feedback_no_sabotage`](`/home/ahart/.claude/projects/-home-ahart-Projects-Laplace/memory/feedback_no_sabotage.md`):

- **No silent failures.** Errors raise; they don't get swallowed.
- **No flat thresholds masquerading as "first pass we'll refine later."** First pass IS the real pass.
- **No "let me just get an MVP working first."** The design is the design.
- **No defaulting to training-data patterns** (this is the conventional-AI-pattern-matching trap).
- **No fabricated scaffolding** filling gaps you don't actually understand.
- **No pretending to know** something you don't. Ask, or surface the uncertainty.
- **No try/except as a way to make red turn green.**
- **No mocks pretending to work.**
- **No TODO stubs that ship.**

If you feel the urge to fall back, simplify, mock, stub, swallow, or pattern-match — **STOP** and surface to the user. Empty/blocked is acceptable; fabricated is not.

---

## R10 — Polymorphic plugin architecture

Adding new capability touches ONE plugin, never all layers. Six plugin interfaces:

- `ISource` — for a new source type (linguistic resource, AI model, text corpus, etc.)
- `IDecomposer` — for a new modality (text, code, image, audio, video, binary)
- `IArchitectureTemplate` — for a new target model architecture (Llama, Mamba, CNN, Diffusion, etc.)
- `IFormatWriter` — for a new output format (safetensors, GGUF, ONNX, etc.)
- `IFeatureExtractor` — for a new embedding-dimension feature
- `IProtocolEndpoint` — for a new served API (OpenAI-compat, Anthropic, Cohere, etc.)

If a proposed change requires touching schema + query layer + synthesis + endpoint to add (e.g.) a new modality, **the proposal is wrong**. The right answer is a new plugin.

---

## R11 — No reading Hartonomous

`/home/ahart/Projects/Hartonomous-001/` is a previous iteration. Reading it pollutes the substrate-native framing the user is rebuilding from scratch. **Do not read** that directory or its memory files at `/home/ahart/.claude/projects/-home-ahart-Projects-Hartonomous-001/`.

If concepts from that work are needed, the user will teach them here in this conversation.

---

## R12 — Do not modify user-authored documentation without explicit instruction

User-authored docs include `DESIGN.md`, `GLOSSARY.md`, `RULES.md`, `STANDARDS.md`, `OPERATIONS.md`, `README.md`.

Agents may PROPOSE changes via conversation. The user authorizes (or rewrites). **Agents do not silently edit these files.**

Agent-managed files in `.agent/`, `.claude/`, and `.github/` may be modified freely by their respective agents.

---

## R13 — Status tracking in `.agent/status/`, never in user docs

Progress, decisions, open blockers, chunk status, agent notes — all live in `.agent/status/`. Do NOT add status sections to `DESIGN.md`, `README.md`, etc.

---

## R14 — C ABI at engine boundaries

The three C/C++ engine shared libraries (`liblaplace_core.so`, `liblaplace_dynamics.so`, `liblaplace_synthesis.so` — per [ADR 0024](docs/adr/0024-engine-modularization.md)) each expose a strict C ABI. No name-mangled C++ symbols crossing the boundary. POD structs only at the ABI surface. No exceptions through the ABI.

This is what lets the same `.so` files be loaded by the PG extensions (`laplace_geom`, `laplace_substrate` — per [ADR 0025](docs/adr/0025-pg-extension-modularization.md)) AND by .NET via P/Invoke (the `Laplace.Engine.{Core,Dynamics,Synthesis}` projects — per [ADR 0026](docs/adr/0026-csharp-project-structure.md)). Violating this breaks the single-source-of-math-truth property.

---

## R15 — Approved libraries only

**Approved:**
- Intel oneAPI (`icx`/`icpx`, oneMKL, oneTBB, IPP) — via `find_package(MKL CONFIG REQUIRED)` + `find_package(TBB CONFIG REQUIRED)` (see [ADR 0030](docs/adr/0030-mkl-eigen-spectra-tbb-integration.md))
- Eigen 3.4+ — system package, header-only; dispatched to MKL via `EIGEN_USE_MKL_ALL` in `liblaplace_dynamics`
- Spectra — header-only, FetchContent v1.2.0 (built on Eigen; inherits MKL backend automatically)
- BLAKE3 — FetchContent v1.5.4 (per [ADR 0015](docs/adr/0015-blake3-for-entity-hashing.md); truncated to 128 bits; raw `bytea(16)` end-to-end)
- PostgreSQL 18 server-dev headers — stock packages now; custom build under `external/postgresql/` per [ADR 0028](docs/adr/0028-custom-built-pg-postgis-intel.md)
- PostGIS 3.6.3 — stock packages now; custom build under `external/postgis/` per [ADR 0028](docs/adr/0028-custom-built-pg-postgis-intel.md)
- ICU 70+ (UCA)
- Boost (where appropriate; minimal use)
- libtree-sitter (for code decomposition)
- .NET 10 (C# app layer; Npgsql + DbUp for the migrations runner per [ADR 0021](docs/adr/0021-dbup-for-migrations.md))

**Banned:**
- HNSWLib / hnswlib / nmslib / faiss / scann — no approximate NN
- oneDNN / cuDNN / oneAPI DNNL — no DNN runtime
- llama.cpp / vLLM / TensorRT-LLM / Triton — no conventional inference runtimes (we ARE the runtime)
- pgvector / pgvecto.rs / pg_embedding / vchord — no vector-DB extensions (substrate replaces them); see [feedback_conventional_db_reflex memory note](../.claude/projects/-home-ahart-Projects-Laplace/memory/feedback_conventional_db_reflex.md)
- libxxhash (XXH3-128) — superseded by BLAKE3 per [ADR 0015](docs/adr/0015-blake3-for-entity-hashing.md)
- Anything implementing gradient descent or backprop

If a new library is needed, **propose it via conversation** with rationale; do not silently introduce.

---

## R16 — Separation of concerns: math in C/C++, orchestration in C#/SQL

Each layer has exactly one kind of work, per [ADR 0027](docs/adr/0027-separation-of-concerns-invariants.md):

| Layer | MAY do | MUST NOT do |
|---|---|---|
| C/C++ engine | math, linalg, hashing, geometry, sparsity, codec, SIMD, fixed-point, file I/O | pipeline orchestration, plugin loading, network I/O, DB connection management |
| PG extension (C wrappers) | Datum↔engine-struct marshalling, PG_TRY/PG_CATCH, opclass support functions, schema DDL | re-implementing engine math; non-trivial PL/pgSQL; control flow that isn't dispatching |
| C# orchestration | pipelines, plugin host, protocol endpoints, DB connection, CLI, recipe parsing | math beyond trivial accounting; reimplementing engine functions for "convenience"; hot-path numerical code |
| SQL / DbUp migrations | idempotent DDL; role grants; CREATE EXTENSION orchestration via `laplace_priv` wrapper; ALTER DEFAULT PRIVILEGES | business logic; procedural transforms; substrate schema definition (lives in extension upgrade scripts per [ADR 0023](docs/adr/0023-extension-owns-schema-dbup-orchestrates.md)) |

**Resolution rule:** a piece of work belongs in the *lowest* layer that can correctly express it. Math always lands in C/C++. Orchestration in C# or SQL.

---

## When a rule conflicts with reality

If you discover a rule that genuinely cannot be followed (e.g., a PostgreSQL limitation that forces a workaround), **surface it to the user immediately**. Do not silently violate. Do not engineer around it without authorization.

If a rule turns out to be wrong, the user updates this file. Agents do not.
