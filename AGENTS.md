# AGENTS.md — Cross-Tool Agent Instructions for Laplace

This file follows the emerging **AGENTS.md** convention (used by Aider, Cursor, Continue, and other agentic tools) for shared cross-tool agent context.

**The authoritative content lives in [CLAUDE.md](CLAUDE.md) — read that file in full before any action.** This file restates the load-bearing rules for tools that read AGENTS.md but not CLAUDE.md.

---

## Project identity

Laplace is a content-addressable geometric-attestation substrate built as a PostgreSQL extension + shared C/C++ engine + thin C# app layer. It **replaces** the conventional AI stack (model files, runtimes, training infrastructure, inference servers, RAG, vector DBs, fine-tuning, distillation pipelines). The substrate IS the model.

**It is not** another AI framework, RAG system, vector database, fine-tuning tool, or wrapper around llama.cpp / vLLM. If your reasoning starts with "this is like X" where X is conventional AI tooling, **stop** and re-read [RULES.md](RULES.md).

Load-bearing mechanics:

- A prompt is ingestion. It is decomposed into substrate entities and represented by a context entity/trajectory before inference; there is no context-window buffer primitive. Prompt-local content records real occurrence/order/composition and can tug existing entity links, but user claims stay prompt/session/source scoped unless explicitly promoted and corroborated.
- Universal T0 is the language-agnostic semiotic foundation. ISO, WordNet, OMW, Wiktionary, UD, Tatoeba, prompts, text, books, code, images, audio, model recipes, and model-derived observations all decompose through type-specific tiers down to Unicode codepoint entities in one hash space.
- The S³ glome is the canonical shared embedding frame — every source is morphed onto the Unicode-anchored frame (Procrustes / Laplacian-eigenmaps / Gram-Schmidt), which is the cross-model/dim/vocab consensus moat. The geometry carries meaning; it is not "just an index" and physicalities are not a non-knowledge layer (forbidden framing per docs/SUBSTRATE-FOUNDATION.md truth 3). Geometry seeds candidates; what pulls back and how hard is Glicko-2 effective-μ across typed arenas (RD, volatility, source trust, lineage, context, arena policy) — retrieval is NOT nearest-neighbor, not spatial closeness.
- Cascade traversal is compiled. One SQL-call surface enters a C/C++ SRF/operator that owns frontier management, A*, tier transitions, effective-score ranking, and abstention; no recursive CTE/RBAR/cursor/app-loop hot path.
- Consensus is arena-aware and source-trust-aware. Incoming observations update current attestation state through typed arena policy; raw repetition does not manufacture truth; independent high-trust structure pulls hard, low-trust/correlated claims remain source-scoped or disputed.
- AI model ingest is a streaming O(params) ETL of weight tables, never a recompute and never bit-perfect preservation ("codec" is a banned label — it implies round-trip preservation, which is worthless; per docs/SUBSTRATE-FOUNDATION.md truths 1, 6, 10). Stream each weight tensor, emit significant cells as Glicko-2 matchup observations (weight = outcome; source-model trust = opponent strength); store only the emergent consensus rating, discard the blob. It must scale linearly to frontier models. Forbidden: GEMM at ingest (E·W·Wᵀ·Eᵀ over vocab²), materializing a vocab² matchup space, or a flat top-k that discards most of the model. Synthesis pours substrate facts into a chosen recipe mold (same machinery for round-trip into the source's own mold or retarget to any other shape — dim, dense/MoE, layers, vocab, dtype). NOTE: interior d×d tensor axis → token-entity resolution (q/k/v/o/gate/up/down) is OPEN per docs/SUBSTRATE-FOUNDATION.md; only embed_tokens/lm_head are directly token-anchored.

---

## Required reading (in order)

1. [CLAUDE.md](CLAUDE.md) — full project instructions
2. [GLOSSARY.md](GLOSSARY.md) — terminology lock
3. [RULES.md](RULES.md) — architectural invariants
4. [STANDARDS.md](STANDARDS.md) — datatype + coding standards
5. [DESIGN.md](DESIGN.md) — engineering spec
6. [OPERATIONS.md](OPERATIONS.md) — build / run / launch / query procedures

---

## Hard rules (zero tolerance)

1. **No pattern-matching to conventional AI.** Different paradigm. Conventional reflexes are sabotage.
2. **No corner-cutting.** No MVPs, no silent failures, no flat thresholds, no fabricated scaffolding.
3. **No modifying user-authored docs** (`DESIGN.md`, `GLOSSARY.md`, `RULES.md`, `STANDARDS.md`, `OPERATIONS.md`, `README.md`) without explicit user instruction.
4. **No reading `/home/ahart/Projects/Hartonomous-001/`** — previous iteration; reading pollutes the framing.

6. **User instructions in conversation override everything.**
7. **Prompt ingestion + compiled cascade per ADR 0035.** No context-window architecture, recursive SQL graph walk, cursor traversal, or app-layer frontier loop.
8. **Arena/source trust semantics per ADR 0036.** Glicko-2 updates and effective mu require compatibility, cardinality, context policy, observation update scope, conflict policy, source-trust policy, lineage policy, and structural-support semantics.

---

## Iteration framework

See [OPERATIONS.md](OPERATIONS.md). Quick reference:

```sh
just build / launch / seed / ingest / query / synthesize / verify / status / clean / test
```

---

## Tool-specific notes

- **Claude Code (`claude`):** loads CLAUDE.md automatically; uses agents in `.claude/agents/`.
- **GitHub Copilot:** loads `.github/copilot-instructions.md`.
- **Cursor:** loads `.cursorrules` (condensed invariants) + this file.
- **Aider / Continue:** load this file (AGENTS.md).
- **Any tool not in the above list:** load this file. If a tool reads NEITHER CLAUDE.md nor AGENTS.md, route it through one that does.
