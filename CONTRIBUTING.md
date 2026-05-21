# Contributing to Laplace

## Authorship

Laplace is the sole work of **Anthony Hart** ([@SaltyPatron](https://github.com/SaltyPatron)). The project is proprietary; see [LICENSE](LICENSE).

Direct human collaborators: **none**. Anthony works alone with Claude as the AI agent collaborator. External pull requests are not solicited and will not be accepted unless explicitly invited and licensed.

This document is the operating manual for that collaboration — primarily for Anthony and for AI agents working on the codebase.

---

## Operating model

| Actor | Role |
|---|---|
| **Anthony Hart** | Sole inventor, designer, decision-maker, copyright holder. Pushes to `main`. |
| **Claude (Anthropic)** | AI agent collaborator. Operates under [CLAUDE.md](CLAUDE.md), [RULES.md](RULES.md), specialized agents in [.claude/agents/](.claude/agents/). Co-authors commits via `Co-Authored-By:` trailer. |
| **GitHub Actions** | CI/CD verifier. Hosted runners for PR-grade checks; self-hosted `hart-server` for integration (build / test / verify) on push-to-main only. |

---

## Branch strategy

- **`main`** is the only long-lived branch.
- Direct commits to `main` are the norm for solo work.
- Feature branches are reserved for experimental work that may not land; merge with `--no-ff` to preserve history if used.
- CI is the gate. No commits merge without a green run on `hart-server`.

---

## Chunk lifecycle

Every chunk of work corresponds to a GitHub Issue labeled `chunk-<N>` and assigned to the active milestone (`v0.1.0 — Chattable Qwen3 Roundtrip` currently).

The lifecycle:

1. **Open the chunk's issue**, read its scope + deliverables + acceptance criteria.
2. **Read [`.agent/status/plan.md`](.agent/status/plan.md)** for context on what comes next.
3. **Confirm preconditions:** `just check-prereqs`. Open blockers, if any, in `.agent/status/blockers.md`.
4. **Implement the deliverables.** Use specialized agents (see [`.claude/agents/`](.claude/agents/)) for their domains.
5. **Verify locally before commit:**
   - `just build` — engine + extension + app
   - `just test` — engine ctest + extension pg_regress + app dotnet test
   - `just verify` (where applicable for the chunk) — determinism, FK, perf-cache
6. **Commit with descriptive message**, `Co-Authored-By: Claude` trailer.
7. **Push to main**, watch CI.
8. **Close the issue** via the commit (`Closes #N` in the commit body) once all acceptance criteria are checked.
9. **Update `.agent/status/STATE.md`** to mark the chunk complete.
10. **Append `.agent/status/decisions.md`** if the chunk surfaced new architectural decisions.

---

## Commit conventions

- One topic per commit. If multiple concerns, multiple commits.
- Imperative mood ("add X", not "added X").
- Subject ≤ 72 chars; capitalize first letter; no trailing period.
- Body wrapped at ~72 chars, explaining the *why* — not the *what* (the diff shows the what).
- Reference issues with `Closes #N` / `Refs #N`.
- `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer when applicable.
- Subject prefix conventions (informal but consistent):
  - `chunk N: <summary>` — landing a chunk
  - `ci: <summary>` — CI/CD changes
  - `status: <summary>` — `.agent/status/` updates
  - `docs: <summary>` — documentation-only changes
  - `fix: <summary>` — bug fixes
  - `infra: <summary>` — repository/tooling infrastructure

---

## Code standards

See [STANDARDS.md](STANDARDS.md). Highlights:

- **Determinism by construction** — no `-ffast-math` on hot paths; fixed-point arithmetic for Glicko-2.
- **One coord type through hot loops** — `float64` end to end; no mixing with `float32`.
- **C ABI at engine boundaries** — `extern "C"`, POD types, no exceptions across the ABI.
- **Bounds-check all deserialization** — no EOF reads, no buffer overruns; PG_TRY/PG_CATCH inside extension wrappers.
- **No corner-cutting** — see [RULES.md R9](RULES.md). No MVPs, no flat thresholds, no silent failures.

---

## Architectural invariants — zero tolerance

See [RULES.md](RULES.md). The headline rules:

1. **No pattern-matching to conventional AI.** Engage the `conventional-ai-skeptic` agent if you find yourself reaching for HNSW / FAISS / RAG / fine-tuning / GEMM-on-hot-path patterns.
2. **Extend PostGIS, never replace.** Standard `geometry` with Z+M flags; `gist_geometry_ops_nd` for indexing.
3. **Three tables only.** No event log; attestation IS consensus state.
4. **Lottery-ticket-aware sparsity — NEVER flat thresholds.**
5. **DB as dumb columnar store; entity math in C/C++.**
6. **No status updates in user-authored docs** — `.agent/status/` is for status.

---

## When to ask before acting

- Removing or migrating data
- Changing architectural decisions in [DESIGN.md](DESIGN.md)
- Adding a new dependency to [STANDARDS.md](STANDARDS.md)
- Modifying user-authored docs (`DESIGN.md`, `GLOSSARY.md`, `RULES.md`, `STANDARDS.md`, `OPERATIONS.md`, `README.md`)
- Anything outside the current chunk's scope

When in doubt: ask. The cost of asking is low; the cost of unwinding bad assumptions is high.

---

## CI integration

Two workflows, both required:

- **`ci.yml`** (GitHub-hosted) — runs on every push + PR. Required-files check, markdown lint, link check, banned-vocabulary scan.
- **`integration.yml`** (self-hosted `hart-server`) — runs on push-to-main + `workflow_dispatch` ONLY. Build engine + extension + app, install extension, create ephemeral test DB, smoke-test extension.

Self-hosted workflows **never** run on `pull_request` events from forks — that's the security boundary.

### One-time runner setup (for integration.yml to fully pass)

These commands need to be run once on the runner host (`hart-server`) by a sudoer so `ahart` can install extensions and operate Postgres without password prompts in CI:

```sh
# 1. Make ahart a Postgres superuser (so createdb/dropdb/psql work as ahart)
sudo -u postgres createuser --superuser ahart
sudo -u postgres createdb ahart        # default DB matching the OS user (peer auth)

# 2. Allow ahart NOPASSWD sudo for `make` (used by `make install` for extensions)
sudo bash -c 'cat > /etc/sudoers.d/laplace-runner <<EOF
ahart ALL=(root) NOPASSWD: /usr/bin/make install*, /usr/bin/make USE_PGXS=1 *install*
EOF'
sudo chmod 440 /etc/sudoers.d/laplace-runner
sudo visudo -c                          # verify syntax
```

After this, the `integration.yml` `extension-smoke-test` job can install the extension, create a test database, run the smoke test, and clean up — all without password prompts in CI.

---

## Reporting bugs / requesting changes

For Anthony — open a GitHub Issue with the appropriate template.

For external parties — see [LICENSE](LICENSE). External contributions are not solicited.
