# ADR 0019: Dedicated `laplace-runner` system account for the CI runner

## Status

**Accepted** — 2026-05-21 (supersedes ADR 0014)

## Context

Initial runner setup (ADR 0014) used the developer's interactive OS user (`ahart`) for the GitHub Actions runner. Problems:

- Conflates the developer's interactive identity with CI's automated identity.
- Sudo operations in CI require the developer's password — blocking automation.
- Permissions can't be bounded — `ahart` is a full sudoer.
- File ownership in `_work/` and runner state mixed with user's home directory.
- A compromised runner would have the developer's identity, not a sandboxed CI role.

## Decision

Create a dedicated system account `laplace-runner`:

- System UID (< 1000)
- `--no-create-home` — no home directory in `/home`
- `--shell /usr/sbin/nologin` — no interactive login
- `--home-dir /var/lib/laplace-runner`

Install the GitHub Actions runner at `/var/lib/laplace-runner/actions-runner/`, owned by `laplace-runner:laplace-runner`.

Systemd service runs as `laplace-runner`.

Permissions:
- **PG access**: peer auth mapping `laplace-runner` OS user → `laplace_admin` PG role (via `/etc/postgresql/18/main/pg_ident.conf` + `pg_hba.conf`). No password needed for local PG access; `laplace_admin` has CREATEDB + CREATEROLE.
- **Bounded sudo**: `/etc/sudoers.d/laplace-runner` grants `NOPASSWD: /usr/bin/make install*, /usr/bin/make USE_PGXS=1 *install*` — only enough to install PG extensions.
- **Read-only access** to /vault/Data (world-readable for ingestion) and /vault/models.

## Consequences

- CI runs as a system account with bounded permissions — proper separation from the developer's interactive identity.
- Compromised runner can install PG extensions (bounded scope), not anything the developer's sudo can do.
- the developer's `ahart` PG role no longer needs superuser privileges (ADR cleanup step).
- All CI database operations happen as `laplace_admin` via peer auth — no passwords.

## References

- ADR 0014 (superseded)
- ADR 0018 (three-layer architecture)
- `scripts/bootstrap-laplace-runner.sh`
