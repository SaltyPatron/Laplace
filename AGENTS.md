# Laplace Workspace Engineering & Operations Guide

This guide governs engineering and operational work across the Laplace multi-repository workspace (`E:\Repositories\Laplace`).

---

## 1. Repository Architecture & Ownership

The workspace contains two active implementation stacks, supporting documentation/research repositories, and one frozen historical reference repository:

| Repository / Stack | Role & Ownership | Status |
|---|---|---|
| **[Laplace](Laplace/README.md)** (`laplace-mono`) | Full-stack monorepo: ASP.NET Core / C# API & OpenAI-compatible endpoints (`app/`), React/Vite web UI (`web/`), native C/C++ substrate & PostgreSQL extension (`engine/`, `extension/`), SQL migrations (`db/`), and deployment (`deploy/`). | **Active** |
| **[Laplace-Native](Laplace-Native/README.md)** | Modular native C/C++ SIMD/ISA substrate library (`laplace_native`). | **Active** |
| **[Laplace-postgres](Laplace-postgres/README.md)** | Modular PostgreSQL C extension (`laplace` 1.12) integrating `Laplace-Native` with PostgreSQL 18.6. | **Active** |
| **[Laplace-Engine](Laplace-Engine/README.md)** | Modular C++ engine CLI (`laplace`), recipe compiler/ingester, and firmware executor. | **Active** |
| **[Laplace-MCP](Laplace-MCP/README.md)** | Modular Model Context Protocol server (`laplace-modular-mcp`). | **Active** |
| **[Laplace-UCI](Laplace-UCI/README.md)** | Universal Chess Interface (UCI) adapter and tournament/engine bridge. | **Active** |
| **[Laplace-Operations](Laplace-Operations/README.md)** | Host provisioning (`setup.sh` / `setup.ps1`), PostgreSQL tuning (`tune-pg.*`), client packaging, cross-host deployment (`laplace.ps1`), and operational verification. | **Active** |
| **[Laplace-Wiki](Laplace-Wiki/README.md)** | Canonical documentation and specification wiki published to GitHub Pages. | **Active** |
| **[Laplace-Conventional](Laplace-Conventional/README.md)** | Python/GPU tooling for conventional model comparison, probing, and reference evaluation. | **Active** |
| **[Laplace-Refactor](Laplace-Refactor/README.md)** | Historical cleanroom prototype containing reference specifications and contracts for **recipe-driven ingestion**, typed recipe compilation, and source admission. | **FROZEN (Read-Only Reference)** |

> **Important (`Laplace-Refactor`)**: Do **not** build, deploy, or run CI workflows in `Laplace-Refactor`. Consult it strictly as a read-only architectural reference when implementing or porting recipe-driven ingestion into `Laplace` or `Laplace-Engine`.

---

## 2. Host Topology, Tailscale Mesh & Environment Isolation Contract

Laplace runs across a single-owner, multi-host topology connected over LAN (`192.168.1.0/24`) and Tailscale (`100.64.0.0/10`), fronted by Cloudflare and `hart-router` (OpenWrt split-horizon DNS):

| Host | Tailscale / LAN IP | OS & Environment Isolation Role | Public / Local Origin | Databases & Services |
|---|---|---|---|---|
| **`HART-DESKTOP`** (`hart-desktop-1`) | `100.70.222.78`<br>`192.168.1.x` | **Local Interactive Dev**: Windows 11 workstation & optional local GPU host. **Zero gatekeeping, no background GitHub Actions runners.** Full direct CLI/DB/UI control. | `https://dev.hartonomous.com` (IIS `laplace-mono`) and `http://localhost:5187` (dev source run) | Single custom PostgreSQL 18.6 service (`laplace-postgres`, port 5432, SSPI + TLS):<br>- `laplace-mono` (monorepo substrate DB)<br>- `laplace-desktop-app` (monorepo app/auth/billing DB)<br>- `laplace` (modular substrate DB) |
| **`hart-server`** | `100.76.181.27`<br>`192.168.1.2` | **Staging (CI/CD App Deployment) + Shared Storage/Infra Host**: Ubuntu 22.04 server (**damaged GPU disabled**). App & modular services are deployed **via CI/CD only**; the host itself remains accessible for `/vault` SMB storage, OpenBao, Vaultwarden, backups, and DB hosting. | `https://staging.hartonomous.com` (`laplace-api` on `:5187`, `laplace-mcp` on `:8080`, `laplace-modular-mcp` on `:5188`) | PostgreSQL 18.6 cluster:<br>- `laplace-mono` (staging substrate + app DB, and BCP failover substrate DB for `hart-prod`)<br>- `laplace` (modular substrate DB)<br>Also hosts **OpenBao** (`https://openbao.hartonomous.com`), **Vaultwarden**, SMB shares (`/vault`), and Linux GitHub Actions runners. |
| **`hart-prod`** | `100.124.95.17` | **Locked Enterprise Production (CI/CD Promotion Only)**: Low-cost always-on GCP VM (`project-f5812b96-f924-41a4-94c`, `us-east1-b`). Treated as an isolated appliance: **never manually SSH-patched or built on**. Receives only verified packages promoted from `hart-server` by CI/CD. | `https://api.hartonomous.com`, `https://mcp.hartonomous.com` | Local PostgreSQL 18.6 (`laplace-app` for accounts, sessions, keys, billing). Connects to `hart-server` (or `hart-cloud` when active) over Tailscale for read-heavy substrate queries. |
| **`hart-cloud`** | `100.103.255.122` | **On-Demand AVX-512 DB & Qualification Host**: High-spec GCP `c3-standard-8` VM. | Internal / Tailscale | **Kept `TERMINATED` (powered off) when idle** to preserve GCP trial credit. Started on demand via CI lease (`qualify_cloud` / `cloud`) and auto-stopped when idle. |
| **`hart-router`** | `192.168.1.1` | OpenWrt router | LAN gateway | Split-horizon DNS and LAN routing. |

---

## 3. Execution Boundaries: What Is Automated vs. How the User Executes

A critical architectural rule across Laplace is keeping the four execution planes distinct and friction-free:

### A. One-Time Host Provisioning (Elevated Admin / `sudo`)
- **Linux**: `sudo ./setup.sh` (in `Laplace-Operations`) and `sudo bash deploy/linux/site-host.sh` (in `Laplace`).
- **Windows (`HART-DESKTOP`)**: `pwsh -File setup.ps1` or `Laplace/deploy/windows/Install-IisRuntime.ps1` in an elevated Administrator PowerShell.
- **Windows UAC / ACL Invariant**: On `HART-DESKTOP`, the developer (`HART-DESKTOP\ahart`, SID `S-1-5-21-1955383224-1799969050-1119319971-1001`) works day-to-day in a **non-elevated** shell where Windows UAC filters out `BUILTIN\Administrators` (`S-1-5-32-544`). Every elevated script that configures ACLs on `E:\Data\Laplace`, `D:\Libraries\build\Laplace`, `D:\Data\Laplace`, or `C:\ProgramData\Laplace` **must** explicitly grant `$declared.windows_sid` (`HART-DESKTOP\ahart`) `FullControl` alongside `SYSTEM`, `BUILTIN\Administrators`, and any IIS AppPool/service SID. If permissions ever drift, run `pwsh -File E:\Repositories\Laplace\Laplace-Operations\installation\Repair-DesktopPermissions.ps1` once from an elevated prompt.

### B. Automated CI/CD Delivery (GitHub Actions on Linux Runners)
- Merging to `main` in `Laplace` runs `.github/workflows/laplace.yml` (`Product — main delivery`), which builds, tests, deploys to `hart-server` (staging), verifies runtime health, and promotes the verified bundle to `hart-prod` (production).
- Merging to `main` in `Laplace-Native`, `Laplace-postgres`, `Laplace-Engine`, or `Laplace-MCP` triggers modular build/test and dispatches `main-merged` to `Laplace-Operations` to deploy and verify on `hart-server`.
- **Database Reseed Policy**: Push/merge workflows **never** automatically drop or reseed databases. Destructive resets (`drop` / `reseed` / `nuke`) require explicit manual dispatch or CLI confirmation.

### C. Normal Operator & Developer CLI (Non-Elevated Shell on `HART-DESKTOP` or `hart-server`)
- From any normal terminal directory without needing developer environment variables (`%LOCALAPPDATA%\Laplace\bin`):
  - `laplace-mono --where` / `laplace-mono status` / `laplace-mono ingest document <path>` — Monorepo CLI (`laplace-mono.cmd`).
  - `laplace --where` / `laplace status` / `laplace ingest ...` — Modular Engine CLI (`laplace.cmd`).
  - `laplace-ops runs` — View recent CI/CD runs across all 6 Laplace repositories in one screen (`laplace-ops.cmd`).
  - `laplace-ops deploy -Target desktop` — One-command update for `HART-DESKTOP` (builds native + managed + web + UCI/chess, publishes to `https://dev.hartonomous.com` and `laplace-mono`, runs local DB migrations on `laplace-desktop-app` and `laplace-mono`, and verifies health using the enrolled OpenBao identity).
  - `laplace-ops components -Target desktop` — Rebuilds and packages `Laplace-Native` + `Laplace-Engine` into `laplace.cmd`.
  - `laplace-ops prepare | build | test | start | stop | status | verify -Target desktop` — Local desktop development lifecycle.
  - `laplace-ops status | verify | deploy -Target staging|production -Wait` — Remote environment control and verification.
- From `E:\Repositories\Laplace\Laplace-Operations`:
  - `./installation/Verify-DesktopDatabases.ps1` and `./installation/Verify-PlatformAccess.ps1` — Verify local PostgreSQL, SSPI/TLS, CLIs, and platform admin access.

### D. Browser Web UI & HTTP API (`https://dev.hartonomous.com`, `https://staging.hartonomous.com`, `https://api.hartonomous.com`)
- End-user and operator browser interface for chat, substrate exploration, chess, and platform administration.
- **Default Platform Administrators**: The database migration `20261016000000_app_default_platform_administrators.sql` seeds the 3 owner OAuth bindings (`aharttn@gmail.com` Google, `thesaltypatron@gmail.com` Google, `thesaltypatron@gmail.com` Microsoft) as audited `installation-owner` administrators whenever any application database is created, migrated (`up` / `app-up`), or recreated (`nuke` / `app-nuke`). No commit-file ceremony or manual SQL is needed after recreating a database.

---

## 4. Workspace & Engineering Rules

- **Build & Scratch Directories**: Configured via `C:\ProgramData\Laplace\machine.env` (Windows) and `/etc/laplace/machine.env` (Linux). On `HART-DESKTOP`:
  - Source root: `E:\Repositories\Laplace`
  - Build root: `D:\Libraries\build\Laplace`
  - Data / corpora root: `E:\Data\Ingest`
  - Work / runtime state: `E:\Data\Laplace\work` and `E:\Data\Laplace\services`
  - PostgreSQL data / WAL: `E:\Data\Laplace\pgdata` and `D:\Data\Laplace\pg_wal`
- **No Worktree or Script Sprawl**: Work directly on `main` (or a short-lived feature branch cleaned up immediately upon merge). Do not leave orphaned `wt-*` git worktrees, duplicate build directories under `D:\Libraries\build\Laplace`, or one-off repair scripts in the repositories.
- **Communication & Verification**: Keep communication direct, technical, and concise. Describe concrete code changes, verification results, and any remaining blockers. Complete implementation tasks with build and test verification before reporting completion.

