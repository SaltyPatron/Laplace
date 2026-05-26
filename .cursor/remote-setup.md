# Cursor remote / SSH setup (Laplace)

One-time steps on the machine running the **Cursor UI** (not necessarily the Ubuntu server).

## Disable context-heavy MCPs

Built-in servers (`cursor-ide-browser`, `cursor-backend-control`) add tool schemas every turn. On headless SSH they are useless.

1. **Cursor Settings → MCP**
2. Disable **cursor-ide-browser** for this workspace (or globally on remote SSH)
3. Optionally disable **cursor-backend-control** unless you use Automations

Do **not** enable Docker Desktop MCP / `MCP_DOCKER` — it merges many tool packs and burns context.

## Prefer shell over MCP

| Need | Use |
|------|-----|
| Issues / PRs | `gh` (auto-approved in `.vscode/settings.json`) |
| Build / test | `just build`, `just test-no-docker`, `just verify` |
| DB | `just query '...'` |
| Session anchor | `just anchor <issue#>` |

## Session file

```bash
cp .laplace-session.example .laplace-session
# edit ISSUE= and MODE=
```

Hooks read `.laplace-session` on **sessionStart** and **preCompact**.

## Terminal auto-approve (optional)

In Cursor Settings → Agents → Auto-run, consider allowing: `just`, `cmake`, `ctest`, `scripts/`.
