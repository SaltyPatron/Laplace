# Monorepo directories and database commands

The monorepo reads public host settings from `/etc/laplace/machine.env` on Linux
and `%ProgramData%\Laplace\machine.env` on Windows. Set `LAPLACE_MACHINE_ENV` to
select another declaration. Nonempty process settings take precedence. The file
contains assignments, not executable shell code; credentials do not belong in it.

The shared reader is `scripts/machine-env.py`. Bash entry points import
`scripts/lib/installation.sh`; Python tools use `scripts/installation_paths.py`.
These are monorepo helpers, not substitutes for Refactor or modular configuration.

| Setting | Meaning / derivation when a more specific setting is absent |
|---|---|
| `LAPLACE_BUILD` | Host build directory. Legacy `LAPLACE_BUILD_VOLUME` remains accepted; it does not require a mount. |
| `LAPLACE_BUILD_ROOT` | Managed output for this checkout: `<build>/laplace-mono/worktrees/<checkout identity>`. |
| `LAPLACE_BUILD_DIRECTORY` | Native output: `<build root>/build`. |
| `LAPLACE_DEPS_BUILD` | Dependency output: `<build>/laplace-mono/dependencies`. |
| `LAPLACE_WORK` | Host operational work directory. |
| `LAPLACE_WORK_ROOT` | Monorepo work: `<work>/laplace-mono`. |
| `LAPLACE_SCRATCH_ROOT` | Tool scratch: `<work root>/scratch`. |
| `LAPLACE_LOCK_ROOT` | Shared lock directory: `LAPLACE_LOCKS`, otherwise `<work root>/locks`. |
| `LAPLACE_EXTERNAL` | Dependency sources; accepts the existing `LAPLACE_DEPSRC` declaration. |
| `LAPLACE_PG_PREFIX` | Installed PostgreSQL; accepts `LAPLACE_PG_DIR`. |
| `LAPLACE_BUILD_GROUP` | Shared workspace group, default `laplace-runner`. |

Directories must be absolute paths. Spaces are supported. A configured build
directory need not be a separate filesystem or a mount named `/build`. Each
checkout receives separate generated output. `place-build-directory.py` preserves
existing output before replacing its checkout-local `build` directory with a
compatibility symlink. It does not move database or installed application state.

Inspect the resolved paths from a monorepo checkout:

```sh
python3 scripts/machine-env.py --paths --format json
```

To provision shared build/work directories, the monorepo's administrator operation
is `sudo bash scripts/setup-storage.sh`. `--check` inspects permissions without
repairing them. It refuses paths overlapping the configured PostgreSQL data, WAL,
temporary tablespaces or application directory before modifying permissions.

Database maintenance uses the installed migration application and needs no build
directory or compiler setup:

```sh
bash scripts/db-migrations.sh status
bash scripts/check-database-health.sh --installed-runtime
```

The connection uses `PGHOST`, `PGPORT`, `PGUSER` and `PGDATABASE` when explicitly
set. Otherwise it reads `LAPLACE_PGHOST`, `LAPLACE_PGPORT`,
`LAPLACE_DATABASE_ROLE` / `LAPLACE_ROLE`, and `LAPLACE_DBNAME`. The local socket
default is PostgreSQL's `/tmp`; the monorepo database default is `laplace-mono`.
There is no implicit administrator role. Declared TLS mode and CA certificate are
preserved. Application database routing remains separate through
`LAPLACE_APP_DBNAME` and its connection settings.

Host paths and development authentication settings must not be copied into
repository-wide GitHub variables. Each runner supplies its work directory for
checkout, then the checked-out tools read that host's declaration.

This change covers the shared reader, build placement/workspace permissions,
database maintenance, workflow work directories, and the chess-tool callers
using that reader. The older full host bootstrap and managed-host policy still
contain cluster/service assumptions; this guide does not certify them as a
replacement for the installed `deploy/linux/site-host.sh` setup owner.
