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

## Session file (optional)

Agents take issue # from your message and run `just anchor N` themselves. `.laplace-session` only helps hooks if you want a default issue before you type.

## Terminal auto-approve (optional)

In Cursor Settings → Agents → Auto-run, consider allowing: `just`, `cmake`, `ctest`, `scripts/`.
