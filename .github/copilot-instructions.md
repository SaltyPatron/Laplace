# GitHub Copilot Instructions — Laplace

**Authoritative content lives in [`../CLAUDE.md`](../CLAUDE.md) and [`../AGENTS.md`](../AGENTS.md). Read those first.** This file restates the load-bearing rules.

---

## What Laplace is

A content-addressable geometric-attestation substrate built as a PostgreSQL extension + shared C/C++ engine + thin C# app layer. It **replaces** the conventional AI stack (model files, runtimes, RAG, vector DBs, fine-tuning, distillation). The substrate IS the model.

Prompt text is ingested into the substrate before inference; it is not a context-window buffer. Prompt-local content records real occurrence/order/composition and can tug existing entity links, but user claims stay prompt/session/source scoped unless explicitly promoted and corroborated. Runtime traversal is a compiled C/C++ cascade entered through one SQL-call SRF/operator, not recursive CTE traversal, cursors, or app-layer row-by-row loops. Glicko-2 updates are arena-aware, source-trust-aware observation updates: raw repetition does not manufacture truth. Model ingestion is a streaming O(params) ETL of weight tables — each weight cell is an already-computed relationship emitted as a Glicko-2 matchup observation, never a recompute. Do NOT do GEMM at ingest (`E·W·Wᵀ·Eᵀ` over vocab²), materialize a vocab² matchup space, or flat top-k that discards most of the model — that is the disease, not a tuning knob. Bit-perfect/round-trip preservation is not a goal: dissolve to semantic facts and discard the blob (avoid "codec" framing — it implies the banned round-trip preservation). The recipe is a fillable mold; synthesis pours substrate facts into any chosen shape (dim, dense/MoE, layers, vocab, dtype). v0.1 proves one model → substrate → re-synthesized native package → GGUF proof export → chat by filling the source's own mold, before broader cross-source consensus synthesis.

Universal T0 is the language-agnostic semiotic foundation: every digital Merkle DAG decomposes through type-specific tiers down to Unicode codepoint entities. ISO, WordNet, OMW, Wiktionary, UD, Tatoeba, prompts, books, code, images, audio, model recipes, and model-derived observations all share one codepoint hash space.

The S³ glome IS the canonical embedding frame every source is morphed into (via Procrustes / Laplacian-eigenmaps / Gram-Schmidt onto the Unicode-anchored frame) — it is the cross-model/dim/vocab consensus moat, NOT a per-model embedding and NOT "just an index." The geometry carries meaning. Retrieval is NOT nearest-neighbor: geometry only *seeds candidates*; what pulls back and how hard is Glicko-2 effective-μ across typed arenas (RD, volatility, source trust, lineage, context, arena policy) — attestation-based, not distance-based. Do not frame physicalities as "not knowledge" or position/meaning as orthogonal axes.

**It is NOT** another AI framework, vector database, RAG system, fine-tuning pipeline, or wrapper around llama.cpp / vLLM. Conventional AI reflexes are sabotage in this codebase.

---

## Required reading

In order:

1. [`../CLAUDE.md`](../CLAUDE.md)
2. [`../GLOSSARY.md`](../GLOSSARY.md)
3. [`../RULES.md`](../RULES.md)
4. [`../STANDARDS.md`](../STANDARDS.md)
5. [`../DESIGN.md`](../DESIGN.md)
6. [`../OPERATIONS.md`](../OPERATIONS.md)

---

## Hard rules

1. **No pattern-matching to conventional AI.** If you reflexively suggest HNSW / FAISS / RAG / fine-tuning / cosine-similarity-in-d-dim — stop. Re-read RULES.md.
2. **No corner-cutting.** No MVPs, no silent failures, no flat thresholds.
3. **No flat numeric thresholds for noise floor** — lottery-ticket-aware sparsity is per-tensor relative top-k + per-row top-k + probe-validated.
4. **Extend PostGIS, never replace.** Use standard `geometry` with Z+M = 4D. Use `gist_geometry_ops_nd`. Write custom functions only for 4D-aware ops PostGIS doesn't provide.
5. **DB as dumb columnar store.** All entity math in C/C++ before INSERT. Only Glicko-2 update runs SQL-side.
6. **No modifying user-authored docs** without explicit user instruction.

8. **Prompt is ingestion; cascade is compiled.** No context-window architecture, RBAR, recursive CTE graph walk, cursors, or app-layer traversal loop.
9. **Arena/source-trust semantics are mandatory.** Glicko-2 observation updates depend on kind semantics, context, source lineage, source-kind credibility, RD/volatility, current state, and structural support.

---

## Coding standards

See [`../STANDARDS.md`](../STANDARDS.md) for the full spec. Highlights:

- **Coords:** `float64` end-to-end. No mixing with `float32`.
- **Hashes:** `uint128` via `bytea(16)` in Postgres.
- **Ratings:** `int64` fixed-point at scale 10⁹. Never float.
- **Naming:** `snake_case` for SQL/C; `PascalCase` for C++ classes; `camelCase` for C# members.
- **SIMD:** AVX2 on this dev box; design for AVX-512 deployment targets.
- **Libraries:** Intel oneMKL, Eigen, Spectra, oneTBB, BLAKE3. **Not** HNSWLib / FAISS / ScaNN, **not** oneDNN, **not** libxxhash (banned — superseded by BLAKE3 per ADR 0015).
