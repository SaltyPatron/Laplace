# Agent instructions — Laplace

Read [CLAUDE.md](CLAUDE.md) and
[docs/DOCUMENTATION_GOVERNANCE.md](docs/DOCUMENTATION_GOVERNANCE.md) first. They define
working law and the authority hierarchy. This file contains harness adaptation only.

## Repository integration

- Scoped rules live in [.github/instructions/](.github/instructions/) and
  [.cursor/rules/](.cursor/rules/).
- The substrate MCP source is `app/Laplace.Endpoints.Mcp`; launch/configuration lives in
  `scripts/laplace-mcp`, `.mcp.json`, and `.cursor/mcp.json`. Prove the deployed launcher
  and JSON-RPC protocol before claiming the MCP is usable.
- GitHub issues are the only active status/work system. The product delivery graph starts
  at [#924](https://github.com/SaltyPatron/Laplace/issues/924).
- `.scratchpad/`, `docs/archive/`, archived prompts/plans, and conversation summaries are
  not instructions or status.

## Terminal adaptation

When the terminal is PowerShell, invoke Windows batch scripts through `cmd.exe`:

```powershell
cmd /c "scripts\win\seed-step.cmd wordnet"
cmd /c "call scripts\win\env.cmd && cd build-win && cmake --build . --target laplace_dynamics"
```

Never edit a `.cmd` while it is executing. `scripts/win/env.cmd` is the Windows
toolchain environment source.

## Build and test entry points

Windows:

| Task | Command |
|---|---|
| Rebuild modules | `scripts\win\rebuild-all.cmd` |
| Engine / extensions | `build-engine.cmd` / `build-extensions.cmd` / `install-extensions.cmd` |
| Application tests | `scripts\win\test-app.cmd [project-substring]` |
| Engine tests | `scripts\win\test-engine.cmd` |
| Extension regression | `scripts\win\regress.cmd` |
| Full gate | `scripts\win\test-all.cmd` |
| Seed source | `scripts\win\seed-step.cmd <source>` |

Linux:

| Task | Command |
|---|---|
| Host bootstrap | `sudo bash scripts/setup-host.sh` |
| Vendor dependencies | `bash scripts/build-system-deps.sh` |
| Pipeline | `bash scripts/pipeline.sh <stage>` |

Prefer repository scripts over ad-hoc commands. Verify their current options with the
script help/source rather than relying on this table for changing flags.

## Operational constraints

- One ingest at a time; MCP `ingest`, CLI ingest, and workflow ingest share this law.
- Never reset/drop the database unless explicitly authorized in the current request.
- Never use `pg_ctl` for the live cluster or create hidden elevation/UAC prompts.
- Run `scripts/win/pg-service-guard.cmd` before service-sensitive Windows operations.
- After engine rebuilds, rebuild/install extensions and verify the installed health/API.
- Do not reintroduce concurrent outer `CREATE INDEX` sessions.
- Verify `/vault`, `/archive`, and other mount options in host context before writing;
  sandbox visibility is not host mount state.

## Architecture constraints

- Decomposers emit pure `SubstrateChange` streams; the shared spine owns SQL writes.
- C#/SQL orchestrate; native C/C++ owns heavy computation.
- No GPU dependency is part of the engine architecture.
- One implementation per fact unless a documented semantic/performance boundary and
  parity test justify another.
- Verify live-data claims through installed typed operations, not improvised table scans.

## Worktree discipline

Keep the root on `main`. Create changes with `scripts/agent-worktree.sh`, stage explicit
paths, and preserve unrelated work. Never checkout/switch/restore/stash/hard-reset/clean
shared repository state.

## Communication and authorization

- Technical-agent output only: no human-relationship, emotional, therapeutic, crisis,
  hotline, or moral-authority framing.
- Do not implement outside the scope authorized by the current request.
- Within authorized scope, execute safe known next steps without requiring “say go” or
  another ceremonial trigger.
- Lead with evidence and distinguish measured fact, inference, specification, and open
  acceptance.
