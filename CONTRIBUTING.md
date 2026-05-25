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
| **GitHub Actions** | CI/CD verifier. Hosted runners for PR-grade checks; self-hosted `hart-server` for integration on push-to-main only. |

---

## Branch strategy

- **`main`** is the only long-lived branch.
- Direct commits to `main` are the norm for solo work.
- Feature branches are reserved for experimental work that may not land; merge with `--no-ff` to preserve history if used.
- CI is the gate. No commits merge without a green run on `hart-server`.

---

## Chunk lifecycle

The lifecycle:

1. **Open the chunk's issue**, read its scope + deliverables + acceptance criteria.

4. **Implement the deliverables.** Use specialized agents (see [`.claude/agents/`](.claude/agents/)) for their domains.
5. **Verify locally before commit** — see [Definition of Done](#definition-of-done) below.
6. **Commit with Conventional-Commits-formatted message**, `Co-Authored-By: Claude` trailer.
7. **Push to main**, watch CI.
8. **Close the issue** via the commit (`Closes #N` in the commit body) once all acceptance criteria are checked.

---

## Definition of Done

A chunk is **Done** only when *every* item below is checked. No partials. No "I'll come back to this." That's how the substrate got 12 months of zero progress in prior iterations.

### Code

- [ ] All chunk acceptance criteria green on the issue.
- [ ] `just build` passes (engine + extension + app + migrations).
- [ ] `just test` passes (engine ctest + extension `installcheck` + app dotnet test).
- [ ] `just verify` passes (where the chunk introduces verifiable invariants — determinism, FK integrity, perf-cache parity).
- [ ] No flat thresholds anywhere ([RULES.md](RULES.md) on lottery-ticket-aware sparsity). Reviewer must specifically grep for hard-coded numeric cutoffs and confirm they reflect probe-validated values, not training-data defaults.
- [ ] No silent failures. Every error path is explicit. Every fallback is logged with a `WARNING` ereport at minimum.
- [ ] No fabricated scaffolding (placeholder files / stubbed-out functions / "TODO: implement"). Either land it or don't.
- [ ] No conventional-AI pattern matching (HNSW / FAISS / RAG / fine-tuning / GEMM-on-hot-path). When tempted, engage the `conventional-ai-skeptic` agent first.
- [ ] Prompt/cascade work preserves ADR 0035: prompt is ingested substrate content; prompt-local observations stay session/source scoped unless explicitly promoted and corroborated; cascade traversal is a compiled C/C++ SRF/operator, not recursive SQL, cursor traversal, or app-layer row-by-row SELECT loops.
- [ ] Strict/speculative/creative traversal modes are explicit where relevant; hallucination/drift are inspectable policy choices with source/evidence traces, not hidden behavior.
- [ ] Consensus work preserves ADR 0036: arena semantics and source trust/source lineage are explicit; raw repetition never counts as truth.
- [ ] No raw-`byte[]` → hex casts, no float ↔ int churn in hot loops, no unnecessary type conversions ([STANDARDS.md](STANDARDS.md)).

### Documentation

- [ ] If the chunk changes a project-wide invariant: explicit user approval, then update `RULES.md` / `STANDARDS.md` / `DESIGN.md` / `GLOSSARY.md` as appropriate.
- [ ] No comments narrating WHAT the code does (well-named identifiers do that). Comments only for non-obvious WHY: subtle invariants, workarounds for specific PG quirks, references to ADRs.

### Migrations & extension

- [ ] If the chunk introduces substrate schema (entities/physicalities/attestations columns, new types, new functions): the change lands in the relevant extension `.sql.in` sources and bumps `default_version` in the matching `.control` file. NOT in `db/migrations/`. (Per [ADR 0023](docs/adr/0023-extension-owns-schema-dbup-orchestrates.md) + [ADR 0034](docs/adr/0034-modular-sql-via-cpp-preprocessor.md).)
- [ ] If the chunk introduces cross-extension orchestration or non-extension operational tables: that goes in `db/migrations/<timestamp>_<name>.sql` via `just migrate-new <name>`.
- [ ] Idempotent SQL only — `CREATE ... IF NOT EXISTS`, `DO $$ ... END $$` for conditional grants, etc.
- [ ] `just db-up` succeeds on a clean target (`just db-nuke && just db-up`).

### Commits & release

- [ ] One topic per commit. Multiple concerns → multiple commits.
- [ ] [Conventional Commits](https://www.conventionalcommits.org/) format (see below).
- [ ] `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer.
- [ ] CI green on `hart-server` for the closing commit.

### Retros

---

## Commit conventions — Conventional Commits

Per [ADR 0020](docs/adr/0020-conventional-commits-and-release-please.md), all commits follow the [Conventional Commits](https://www.conventionalcommits.org/) specification — commits stay searchable/categorizable and each `<type>` carries the SemVer bump intent for whenever automated versioning is reintroduced.

### Format

```
<type>(<scope>)<!>: <description>

[optional body — wrap at ~72 chars; explain the WHY]

[optional footers]
```

### Types

| Type | When | SemVer impact |
|---|---|---|
| `feat` | New user-visible capability | minor bump |
| `fix` | Bug fix | patch bump |
| `perf` | Performance improvement | patch bump |
| `refactor` | Code change that neither fixes a bug nor adds a feature | no bump |
| `build` | Changes to build system / dependencies | no bump |
| `ci` | Changes to CI workflows | no bump |
| `docs` | Documentation only | no bump |
| `style` | Code style only (no logic change) | no bump |
| `test` | Adding or correcting tests | no bump |
| `chore` | Maintenance | no bump |
| `revert` | Revert a prior commit | no bump |

### Scopes (project-specific)

`engine`, `extension`, `app`, `migrations`, `bootstrap`, `ci`, `adr`, `agent`, `sdlc`, or the chunk number (`chunk-2`).

### Breaking changes

Append `!` after the type/scope (`feat(extension)!:`) AND include a `BREAKING CHANGE:` footer. Triggers a major bump.

### Examples

```
feat(extension): add laplace.entities table with BLAKE3-128 PK

Lands the first substrate table. PK is bytea(16) — raw BLAKE3-128 hash,
no hex, no casts. Marked with pg_extension_config_dump so substrate
data survives pg_dump/pg_restore via CREATE EXTENSION laplace.

Refs #42
Closes #43
Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

```
fix(migrations): EnsureDatabase needs explicit maintenance-db connect

Without this, DbUp tried to CREATE DATABASE while connected to the
target DB (chicken-and-egg). Now connects to 'postgres' first, then
re-resolves the target.

Closes #57
```

```
refactor(bootstrap)!: split bootstrap-laplace-runner.sh by mode

BREAKING CHANGE: the script now requires a mode argument
('bootstrap', 'status', or 'reset'). Previously it always
performed bootstrap. Update any cron / automation that invokes
the script without arguments.
```

---

## One-time host setup

**TL;DR — set up `hart-server` (or any new Laplace host) in one command:**

```sh
cd ~/Projects/Laplace && git pull origin main
scripts/setup-host.sh           # or: just setup-host
```

That single (idempotent, re-runnable) script does Layer 0 (root) + Layer 1 (DbUp) end to end. After it completes the host is ready and you can trigger CI:

```sh
gh workflow run integration.yml
```

### What `setup-host.sh` does

| Step | Phase | What | Resettable via |
|---|---|---|---|
| 1 | Layer 0 | `sudo bootstrap-laplace-runner.sh bootstrap` — creates `laplace-runner` system account, installs the GitHub Actions runner at `/var/lib/laplace-runner/actions-runner`, creates PG roles (`laplace_admin` / `laplace_app` / `laplace_readonly`), writes `pg_hba.conf` + `pg_ident.conf` entries for peer auth, writes bounded NOPASSWD sudoers for `make install*`. | `just bootstrap-reset` |
| 2 | Layer 1 | Builds `Laplace.Migrations` (Release) | `just clean-app` |
| 3 | Layer 1 | `sudo -u laplace-runner dotnet run -- up` — DbUp `EnsureDatabase('laplace')`, `CREATE EXTENSION postgis + laplace`, schema USAGE grants, default privileges | `just db-reset` (re-apply migrations) or `just db-nuke` (DROP DATABASE) |

Per [ADR 0018](docs/adr/0018-three-layer-architecture.md), the three layers are **independently resettable** — Layer-0 reset doesn't touch substrate data, Layer-1 reset doesn't touch the runner / roles / auth.

### Granular control (when you only want one layer)

```sh
# Layer 0 only — system account, runner, PG roles, peer auth, sudoers:
sudo scripts/bootstrap-laplace-runner.sh bootstrap
sudo scripts/bootstrap-laplace-runner.sh status
sudo scripts/bootstrap-laplace-runner.sh reset      # requires typing 'RESET'

# Layer 1 only — DbUp:
just db-up                                          # EnsureDatabase + CREATE EXTENSION + grants
just db-status                                      # applied / pending
just db-reset                                       # drop SchemaVersions; re-apply
just db-nuke                                        # DROP DATABASE laplace; requires typing 'NUKE'

# Both layers together — what most people want:
scripts/setup-host.sh                               # bootstrap + db-up
scripts/setup-host.sh status                        # both-layer state
scripts/setup-host.sh reset                         # both-layer teardown; requires typing 'RESET'
```

### What `setup-host.sh` does NOT do

- It doesn't install PostgreSQL, PostGIS, or .NET. Those are host-level prerequisites — see [OPERATIONS.md](OPERATIONS.md) for one-time-per-host system install.
- It doesn't seed the substrate (`just seed-t0`) — that's a Layer-2 operation that runs after the engine + extension builds land in Chunk 3.
- It doesn't `gh auth login` for you. Your `gh` CLI must already be authenticated as a SaltyPatron/Laplace admin (Layer 0 uses your `gh` config to mint runner registration tokens).

---

## Code standards

See [STANDARDS.md](STANDARDS.md). Highlights:

- **Determinism by construction** — no `-ffast-math` on hot paths; fixed-point arithmetic for Glicko-2.
- **One coord type through hot loops** — `float64` end to end; no mixing with `float32`.
- **C ABI at engine boundaries** — `extern "C"`, POD types, no exceptions across the ABI.
- **Bounds-check all deserialization** — no EOF reads, no buffer overruns; PG_TRY/PG_CATCH inside extension wrappers.
- **Raw bytes only for hashes** — `bytea(16)` for BLAKE3-128 PKs; no hex strings, no casts. Per [ADR 0015](docs/adr/0015-blake3-for-entity-hashing.md).
- **No corner-cutting** — see [RULES.md R9](RULES.md). No MVPs, no flat thresholds, no silent failures.

---

## Architectural invariants — zero tolerance

See [RULES.md](RULES.md). The headline rules:

1. **No pattern-matching to conventional AI.** Engage the `conventional-ai-skeptic` agent if you find yourself reaching for HNSW / FAISS / RAG / fine-tuning / GEMM-on-hot-path patterns.
2. **Extend PostGIS, never replace.** Standard `geometry` with Z+M flags; `gist_geometry_ops_nd` for indexing.
3. **Three tables only.** No event log; attestation IS consensus state.
4. **Lottery-ticket-aware sparsity — NEVER flat thresholds.**
5. **DB as dumb columnar store; entity math in C/C++.**
6. **Prompt is ingestion; cascade is compiled.** Prompt-local content is real structure, prompt claims stay scoped unless promoted and corroborated, and there are no context-window buffers, RBAR, cursors, recursive CTE hot paths, or app-layer traversal loops.
7. **Arena/source trust semantics are mandatory.** Glicko-2 is not raw vote counting.
8. **AI model ingest is a codec.** Source-scoped round-trip fidelity is a verification target, not optional behavior.

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

- **[`ci.yml`](.github/workflows/ci.yml)** (GitHub-hosted) — runs on every push + PR. Required-files check, markdown lint, link check, banned-vocabulary scan, Conventional Commits validation.
- **[`integration.yml`](.github/workflows/integration.yml)** (self-hosted `hart-server`) — runs on push-to-main + `workflow_dispatch` ONLY. Build engine + extension + app + migrations → install extension → `db-up` → smoke test the substrate.

Self-hosted workflows **never** run on `pull_request` events from forks — that's the security boundary.

---

## Reporting bugs / requesting changes

For the user — open a GitHub Issue with the appropriate template.

For external parties — see [LICENSE](LICENSE). External contributions are not solicited.
