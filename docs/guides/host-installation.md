# Monorepo installation and repair

Run the monorepo installer from the `SaltyPatron/Laplace` checkout on the selected
Linux host. It reads that host's `LAPLACE_MACHINE_ENV` declaration (by default
`/etc/laplace/machine.env`). Paths are directories; `/build` is not required.

```sh
bash deploy/linux/site-host.sh --check
sudo bash deploy/linux/site-host.sh
```

The default operation installs the API/MCP service envelopes and restart requests,
configures routing, reconciles declared workload credentials and database roles,
and installs the declared browser/chess identities. It reuses the existing cluster.
The API/MCP identity installer lives under `deploy/linux/runtime`; it no longer
invokes Operations setup to install its credential refresh timer. Existing scoped
AppRole credentials are retained and checked. A human Bao login is used only for
first enrollment; a rejected installed enrollment does not fall back to that login.

This installer requires a prepared shared host and a published application release.
Operations owns shared packages, custom PostgreSQL, network and storage preparation;
`deploy/linux/site.sh` owns nonprivileged build, migration and publication. A missing
release is an error, not an installed application. The Windows application installer
remains `deploy/windows/Install-LaplaceSite.ps1`, with its installation and enrollment
arguments; the human CLI is described in [installed-mono-client.md](installed-mono-client.md).

`scripts/setup-host.sh` with no mode (or `setup`) and
`deploy/linux/bootstrap-host.sh` forward to this same installer. Their default path
does not invoke the retired standalone cluster bootstrap or restore runner sudo
grants. Explicit legacy diagnostic modes in `scripts/setup-host.sh` have not been
converted into this lifecycle and should not be used as alternative full installers.

`--check` resolves the service configuration and reads the installed runtime
prerequisites without fetching source, changing configuration or restarting services.
It reports private workload authentication as requiring a privileged check when run
without access to the enrollment. It does not claim installation or credential refresh.

`--runtime-only` reconciles API/MCP identity and credential refresh. `--runtime-manifest
/absolute/declaration.json` selects a runtime declaration explicitly; otherwise
`LAPLACE_RUNTIME_MANIFEST`, then the matching host declaration in `deploy/linux/runtime`,
selects it. A declared missing file fails. `--as-is` uses the checked-out revision;
ordinary privileged setup fast-forwards the clean checkout before running it.

Service files use the configured installation prefix, lock directory, PostgreSQL
service, runtime accounts and mutable state directories. The API and MCP retain
their existing service names. `laplace-database-refresh` refreshes their database
credentials; installation stops and disables the previous timer before enabling
the replacement. Refactor and modular Laplace are separate installations.

The installed refresh executable is root-owned, outside the source checkout. Its
timer logs into OpenBao with the workload's own enrollment, verifies the selected
database credentials and native access, and reloads serving connections when needed.
See [OpenBao AppRole](https://openbao.org/docs/auth/approle/) for the authentication
method. The [systemd service specification](https://github.com/systemd/systemd/blob/main/man/systemd.service.xml)
defines command parsing; generated units are also checked with the installed
`systemd-analyze verify`, including directory names containing spaces and `%`.
