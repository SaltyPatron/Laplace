# Install browser routes

The monorepo owns these routes. `laplace-mono` identifies this implementation;
Refactor and the modular repositories have separate installation owners.
The existing `dev`, `staging` and `api` DNS names are retained here until their
provider callback registrations and consumers are migrated together.

## hart-server

`private-origins.json` declares the staging hostname, listen addresses, allowed
clients, existing certificate, loopback backends and request limits. Set
`LAPLACE_PRIVATE_ORIGINS` in the machine declaration to select another file.
These are deployment values, not requirements for another machine's paths.

From the monorepo checkout, run the admin routing repair:

```sh
sudo bash deploy/linux/site-host.sh --routing-only
```

The normal `site-host.sh` setup also installs this route. The routing-only form
does not rebuild the application or restart PostgreSQL/API/MCP. It validates
certificate coverage/expiry, validates nginx, reloads it and requests the named
HTTPS health endpoint with certificate validation. If activation fails, it
restores the previous configuration. The existing certificate renewal mechanism
still owns renewal; this script does not issue a replacement certificate.

The declared route accepts the LAN and selected VM addresses. Router forwarding,
host firewall rules and Tailscale policy are separate access boundaries; nginx
configuration does not reconcile them. Existing port 8080 remains an internal
route used by the deployed production MCP proxy during the routing migration.

## Windows IIS

Declare `LAPLACE_PUBLIC_BASE_URL`, the installation path (`LAPLACE_IIS_API`),
site/pool names (`LAPLACE_IIS_SITE`, `LAPLACE_IIS_POOL`) and the thumbprint of a
valid TLS certificate with its private key in LocalMachine/My
(`LAPLACE_IIS_CERTIFICATE_THUMBPRINT`) in the machine declaration. Its default
location is `%ProgramData%\Laplace\machine.env`; `LAPLACE_MACHINE_ENV` selects
another file. A code-signing certificate is not a TLS certificate.

```powershell
pwsh -NoProfile -File deploy/windows/Install-LaplaceSite.ps1 -Plan
# Administrator PowerShell, after the declared certificate is installed:
pwsh -NoProfile -File deploy/windows/Install-LaplaceSite.ps1
```

This prepares the IIS route. It does not claim the application is published or
healthy. The current Windows publisher still controls the application pool and
requires elevation; its lifecycle must be corrected before recurring deployment
can be considered an unprivileged operation.
