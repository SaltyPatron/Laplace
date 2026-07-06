---
name: 'Windows script rules'
description: 'Rules for editing/running scripts/win/*.cmd batch scripts'
applyTo: 'scripts/win/**'
---
# Windows script rules (scripts/win/*.cmd)

- NEVER invoke a `.cmd` directly from PowerShell on this machine
  ([PowerShell#27634](https://github.com/PowerShell/PowerShell/issues/27634) regression).
  Always wrap: `cmd /c "scripts\win\seed-step.cmd wordnet"`.
- NEVER edit a `.cmd` while any invocation of it is executing — cmd.exe re-reads the
  file by byte offset (lesson L5). A guard hook enforces this for seed scripts.
- `env.cmd` is the toolchain source of truth and is idempotent via `LAPLACE_ENV_LOADED`.
  Scripts self-load it with `call "%~dp0env.cmd"`. `dotnet` stays bare (no path override).
- cmd.exe `goto` into/out of parenthesized blocks is a real parser trap (lesson L8) —
  keep labels at top level; use `EnableDelayedExpansion` and `!var!` inside loops.
- `seed-step.cmd` runs an independent `:verify_step` after the CLI — that verdict is the
  truth, not the CLI's own summary line.
- The ingest mutex guard matches on process COMMAND LINE (`Laplace.Cli` via
  Win32_Process); `dotnet run` launches as `dotnet.exe`, so prefer the built CLI path.
- One ingest at a time. No parallel agent sessions against Postgres mid-write.
