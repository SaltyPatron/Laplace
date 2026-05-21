# ADR 0018: Three-layer architecture — bootstrap / CI / local dev

## Status

**Accepted** — 2026-05-21

## Context

Setup operations were being mixed: one-time-root-needed steps (creating a system user, configuring sudoers, configuring pg_hba.conf), per-deploy operations (creating databases, applying schema, installing extensions), and local-dev operations (running tests, applying migrations on a dev box). When all three are bundled into a single "setup script," the script becomes a manual one-shot — the agent (CI) can't do most of the work because it needs to run as a privileged user just once.

The right model separates the three layers.

## Decision

Three explicit layers:

### Layer 0 — Bootstrap (truly one-time, root-needed)
- Create system accounts (e.g., `laplace-runner`)
- Install + register GitHub Actions runner
- Install systemd services
- Create PG roles (postgres-superuser-required: `CREATE ROLE laplace_admin ...`)
- Configure pg_hba.conf / pg_ident.conf (root-required: write to /etc/postgresql)
- Write sudoers entries (root-required)

Implemented as **one** script: `scripts/bootstrap-laplace-runner.sh`. Idempotent. Anthony runs ONCE: `sudo bootstrap-laplace-runner.sh`. Forever-after.

### Layer 1 — CI workflow (runs on every push; idempotent; agent-driven)
- Build engine + extension + app
- Apply versioned migrations (dbmate up)
- Install / upgrade extension into PG (bounded sudo for `make install`)
- Run tests
- Ingest sources (when per-chunk work warrants)
- Verify (determinism, FK, perf-cache)

The runner has bounded NOPASSWD sudo for `make install*` only.

### Layer 2 — Local dev (Justfile recipes)
- Mirror Layer 1 ops for local execution
- `just db-setup`, `just db-reset`, `just migrate`, `just ingest`, ...

Same idempotent logic; same migrations; same tools.

## Consequences

- Layer 0 is the irreducible minimum manual operation. Approximately one command per machine, ever.
- Layer 1 means the agent does the ongoing work — schema migrations, extension upgrades, ingestion — without Anthony's involvement.
- Layer 2 lets Anthony reproduce CI state locally for debug.
- Migrations replace ad-hoc setup scripts (see ADR 0021).

## References

- [CLAUDE.md](../../CLAUDE.md) — "Cadence" section
- ADR 0019 (laplace-runner — the Layer 0 system account)
- ADR 0021 (migration framework)
