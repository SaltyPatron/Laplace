# Laplace — Project State

**Last updated:** 2026-05-21 (foundation + CI/CD initialized; runner online)
**Updated by:** claude (project framework + CI/CD setup)

---

## Current phase

**Phase 0 — Foundation documentation + CI/CD.** Complete.

Framework documentation, rules, glossary, standards, design spec, operations manual, agent definitions, and status-tracking infrastructure all in place. GitHub Actions CI/CD active (hosted PR-validation + self-hosted integration on `hart-server`). No code yet.

## Repository

- **GitHub:** https://github.com/SaltyPatron/Laplace (public)
- **Default branch:** `main`
- **Commits:** `b7614f2` (initial .gitignore) → `fb46bb3` (framework foundation) → `b68322a` (CI/CD workflows)

## Runner

- **Name:** `hart-server`
- **Service:** `actions.runner.SaltyPatron-Laplace.hart-server.service` (systemd, enabled, running)
- **Labels:** `self-hosted, Linux, X64, laplace, oneapi, postgres-18, dotnet-10, avx2`
- **Capabilities verified:** Intel oneAPI 2026.0.0, PostgreSQL 18.3, PostGIS 3.6.3, .NET 10.0.107, Eigen 3.4.0, ICU 70.1, libxxhash 0.8.1
- **Security posture:** push-to-main + workflow_dispatch ONLY on self-hosted; never pull_request from forks.

## Workflows

- **CI (GitHub-hosted):** `.github/workflows/ci.yml` — required-files check, markdown lint, link check, banned-vocabulary scan. Triggers on push, PR, manual.
- **Integration (self-hosted):** `.github/workflows/integration.yml` — capability check now; expands as code lands. Triggers on push to main + manual ONLY.

## Milestones

| # | Milestone | Status | Verified by |
|---|---|---|---|
| 0 | Framework documentation in place | ✅ DONE | initial framework init |
| 1 | Chunk 1 — engine skeleton + extension scaffold + schema | ⏳ NEXT | — |
| 2 | Chunk 2 — coord4d + hash128 + hilbert4d + mantissa pack | — | — |
| 3 | Chunk 3 — geometry4d serialize/deserialize + GIST integration | — | — |
| 4 | Chunk 4 — perf-cache build from Unicode UCD + DB seed | — | — |
| 5 | Chunk 5 — first linguistic source plugin (WordNet) + ingestion verified | — | — |
| 6 | Chunk 6 — Glicko-2 fixed-point + cross-source consensus working | — | — |
| 7 | Chunk 7 — TransformerModelSource probe + Procrustes pipeline + physicalities | — | — |
| 8 | Chunk 8 — first synthesis pipeline (LlamaTemplate / QwenTemplate) | — | — |
| 9 | Chunk 9 — Qwen3 round-trip → llama.cpp → chat (the milestone) | — | — |

(Chunk numbering is provisional; user will produce the canonical plan next.)

## In progress

None. Foundation documentation just completed; awaiting user's plan-building turn.

## Recently completed

- 2026-05-21: Created project framework — CLAUDE.md, AGENTS.md, copilot-instructions.md, README.md, GLOSSARY.md, RULES.md, STANDARDS.md, DESIGN.md, OPERATIONS.md, Justfile
- 2026-05-21: Created `.claude/agents/` — substrate-architect, postgres-extension, cpp-performance, type-taxonomy, ingestion-pipeline, verification, conventional-ai-skeptic
- 2026-05-21: Created `.claude/settings.json`
- 2026-05-21: Created `.agent/` directory with README + status skeleton

## Open blockers

See [blockers.md](blockers.md).

## Decisions log

See [decisions.md](decisions.md).

## Verifications

No verifications yet — no code to verify.
