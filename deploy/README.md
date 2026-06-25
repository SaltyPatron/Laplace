# Deploy

The API (`app/Laplace.Endpoints.OpenAICompat`, ASP.NET Core net10) **self-hosts
the React SPA** (`web/`) from its own `wwwroot`, and the web client uses relative
paths — so every environment is **single-origin**: build `web/` → `wwwroot`,
publish the API, point `LAPLACE_DB` at a migrated Postgres-with-substrate.

One app, two targets, **one parameterized pipeline**. Environment differences are
config (env vars / GitHub Actions variables), never forked code. Both targets need
the native engine libs (`laplace_core/dynamics/synthesis`) reachable by the API.

## Windows (local, IIS)

Data layer is already in place (local PG 18 `laplace` DB has the schema +
`laplace_substrate` + `recall_session`).

```powershell
# 1. (once, elevated) ensure Hosting Bundle + create IIS site/app pool
#    If aspnetcorev2.dll is missing it tells you to install the bundle:
#      winget install Microsoft.DotNet.HostingBundle.10 ; iisreset
deploy\windows\Install-LaplaceSite.ps1          # site 'Laplace' on http://localhost:8080

# 2. real config: copy + edit (gitignored). Add ;Password=... if PG needs it.
copy deploy\windows\laplace-api.env.example deploy\windows\laplace-api.env

# 3. build SPA + publish API + inject env into web.config + sync to the site
deploy\windows\publish.ps1                       # -> D:\Data\inetsrv\laplace-api
```

Redeploy = re-run `publish.ps1`. IIS recycles the app pool on `web.config` change.
The site's physical path is **`D:\Data\inetsrv\laplace-api`** (a user-writable folder, NOT
`C:\inetpub`) so `publish.ps1` syncs into it with no elevation — only the one-time
`Install-LaplaceSite.ps1` (IIS metabase config) needs admin.

## hart-server (Linux, nginx + systemd, LAN-only)

Native engine + extension are installed under `/opt/laplace`; deploy runs as
`laplace-runner`. nginx fronts on **:8080** (coexists with the existing vhosts) →
Kestrel on `127.0.0.1:5187`. Reachable on the LAN at `http://hart-server:8080`.

```bash
# 1. (once, root) systemd unit + nginx vhost + narrow sudoers grant
sudo deploy/linux/bootstrap-host.sh
#    then edit secrets in /opt/laplace/app/laplace-api.env (CICD won't touch it)

# 2. routine deploy (what the GitHub Actions 'deploy-app' workflow runs)
just build && just install        # native engine + extension
dotnet build app/Laplace.Migrations/Laplace.Migrations.csproj -c Release
deploy/linux/deploy.sh            # build SPA, publish API, migrate DB, restart
```

CI: `.github/workflows/deploy-app.yml` (workflow_dispatch + push to `main`
touching `app/…OpenAICompat`, `web/`, or `deploy/linux/`).

## Config knobs (env)

`LAPLACE_DB` (Npgsql conn string) · `LAPLACE_AUTH_MODE=header` ·
`LAPLACE_BILLING_STORE=memory|postgres` · `LAPLACE_BILLING_BYPASS=true` ·
`LAPLACE_CORS_ORIGINS` (unused single-origin) · `LAPLACE_LOG_DIR/_JSON` ·
`LAPLACE_RATELIMIT_PERMIN` · `OTEL_EXPORTER_OTLP_ENDPOINT`. Linux also:
`LD_LIBRARY_PATH=/opt/laplace/lib`, `ASPNETCORE_URLS=http://127.0.0.1:5187`.
