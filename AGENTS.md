# AGENTS.md — Cross-Tool Agent Instructions for Laplace

This file follows the emerging **AGENTS.md** convention (used by Aider, Cursor, Continue, and other agentic tools) for shared cross-tool agent context.

**The authoritative content lives in [CLAUDE.md](CLAUDE.md) — read that file in full before any action.** This file restates the load-bearing rules for tools that read AGENTS.md but not CLAUDE.md.

---

## Project identity

Laplace is a content-addressable geometric-attestation substrate built as a PostgreSQL extension + shared C/C++ engine + thin C# app layer. It **replaces** the conventional AI stack (model files, runtimes, training infrastructure, inference servers, RAG, vector DBs, fine-tuning, distillation pipelines). The substrate IS the model.

**It is not** another AI framework, RAG system, vector database, fine-tuning tool, or wrapper around llama.cpp / vLLM. If your reasoning starts with "this is like X" where X is conventional AI tooling, **stop** and re-read [RULES.md](RULES.md).

Load-bearing mechanics:

- A prompt is ingestion. It is decomposed into substrate entities and represented by a context entity/trajectory before inference; there is no context-window buffer primitive.
- Cascade traversal is compiled. One SQL-call surface enters a C/C++ SRF/operator that owns frontier management, A*, tier transitions, effective-score ranking, and abstention; no recursive CTE/RBAR/cursor/app-loop hot path.
- Consensus is arena-aware and source-trust-aware. Raw repetition does not manufacture truth; independent high-trust structure pulls hard, low-trust/correlated claims remain source-scoped or disputed.
- AI model ingest is a codec. v0.1 proves model → substrate → sparse GGUF → chat for a source-scoped model before broader seed-stack synthesis.

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
8. **Arena/source trust semantics per ADR 0036.** Glicko-2 updates and effective mu require cardinality/context/competition/source-lineage semantics.

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
