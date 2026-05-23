# ADR 0019: Dedicated `laplace-runner` system account for the CI runner

## Status

**Accepted** — 2026-05-21 (supersedes ADR 0014)

**Amended** — 2026-05-23: The bounded-sudoers clause for `cmake --install` / `make install*` is **retired**. With `CMAKE_INSTALL_PREFIX=/opt/laplace` (laplace-runner-group-writable, setgid 2775) and PG's `extension_control_path` / `dynamic_library_path` pointing at the same prefix via `bootstrap_pg_extension_paths`, the runner installs extensions into a directory it already owns — no escalation needed. `bootstrap_remove_legacy_sudoers` in `scripts/bootstrap-laplace-runner.sh` removes the `/etc/sudoers.d/laplace-runner` artifact from prior bootstraps.

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
- **PG access**: peer auth mapping `laplace-runner` OS user → `laplace_admin` PG role (via `/etc/postgresql/18/main/pg_ident.conf` + `pg_hba.conf`). No password needed for local PG access. **`laplace_admin` is `SUPERUSER` + `CREATEDB` + `CREATEROLE`** (amended 2026-05-23 per [ADR 0045](0045-laplace-admin-superuser-supersedes-laplace-priv-wrapper.md) — the prior `CREATEDB + CREATEROLE`-only configuration required a `SECURITY DEFINER` wrapper to install non-trusted extensions like PostGIS, which broke `just db-nuke` and made trusted-extension ownership transfer require additional `ALTER OWNER` machinery; the dedicated-cluster superuser pattern collapses both).
- **No sudo for installs** (per 2026-05-23 amendment above): `cmake --install build --prefix /opt/laplace` writes into a directory the runner already owns via group membership + setgid. The old bounded-NOPASSWD sudoers entry has been removed. See `bootstrap_remove_legacy_sudoers` in `scripts/bootstrap-laplace-runner.sh`.
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
