# Session audit — historical compatibility pointer

Status: **historical session record; not current repository status, backlog, architecture or authority.**

The previous contents of this file captured one high-conflict implementation session: then-current SQL timings, pipeline failures, ingest incidents, unmerged PRs, database observations, agent-conduct failures and a list of defects found/left open.

That record remains in Git history for accountability and archaeology. It is intentionally no longer presented at a top-level `docs/SESSION-AUDIT.md` path as though its counts, priorities and runtime state were current.

Use instead:

- [`README.md`](README.md) — documentation authority map;
- [`INVENTION.md`](INVENTION.md) / [`INVENTIONS.md`](INVENTIONS.md) — invention;
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — current as-built architecture/divergences;
- [`../TASKS.md`](../TASKS.md) — current task/status navigation;
- current GitHub issues/PRs — acceptance/ownership;
- current CI/live measurements — present implementation/runtime evidence.

## What remains useful from the old audit

The old audit demonstrated several durable process/architecture lessons:

- syntax conversion is not a performance refactor;
- finding a defect is not fixing/delivering it;
- source changes are not installed/live proof;
- historical DB/query timings must not be quoted after the implementation/data generation changes without remeasurement;
- repeated SQL/planner/boundary overhead can dominate useful semantic work;
- per-file/per-artifact ingestion and idempotent completion require explicit receipts rather than optimistic status text;
- user-requested scope must not be abandoned in favor of easier adjacent work;
- status reports must distinguish landed, deployed, measured and delivered behavior.

Those laws are now generalized in `AGENTS.md`, the current architecture docs and benchmark/execution contracts.

## Historical defects are not automatically current defects

A defect named in the old session audit may still exist, may have been repaired, or may have been superseded by a different implementation. Reproduce it against current source/install before using it as present evidence.

Likewise, old counts such as function totals, index sizes, partition counts, ingest percentages, database row counts, latency values, branch/PR state and CI failures are historical observations only.

## Current anti-staleness rule

A dated session/audit may generate issues, tests, decisions or architecture corrections. Once those durable artifacts exist, the session narrative does not become a permanent product plan.

If current evidence contradicts a historical session note, update the current-facing authority/evidence and keep the old record as history rather than forcing the present system to match the old snapshot.
