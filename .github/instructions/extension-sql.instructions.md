---
description: "Use when editing PostgreSQL extension SQL/C under extension/ or DbUp migrations under db/migrations/ — the extension-is-deployment-unit law, SQL module load order, the cpp comment hazard, KindRegistry.Canon for kinds, the ops/gate surface, regress pins, and the Layer-1-only migration boundary. Keywords: laplace_substrate, sql.in, pg_regress, migration, DbUp, consensus reads, ops surface, kinds."
applyTo: ["extension/**", "db/migrations/**"]
---
# Extension SQL/C + migrations

Read [AGENTS.md](../../AGENTS.md) first for the invariants; this file is the extension/migration-local layer.

- The extension is the deployment unit — it ships the full substrate schema, every function, and the readback seed. DbUp migrations under [db/migrations/](../../db/migrations/) are orchestration ONLY (extensions, roles, grants, lifecycle). See [extension/laplace_substrate/sql/README.md](../../extension/laplace_substrate/sql/README.md#L3-L8).
- Layer-1 boundary: if you are writing `CREATE TABLE`, `CREATE FUNCTION`, or `INSERT` for `laplace.*` in a migration, STOP — it belongs in the matching `extension/laplace_substrate/sql/NN_*.sql.in` module plus a regress pin. The scaffolder warns about this at [Justfile](../../Justfile#L190-L201).
- Module numbering equals load order (concatenated through `cpp`). Keep references flowing backward. See the module map in [extension/laplace_substrate/sql/README.md](../../extension/laplace_substrate/sql/README.md#L17-L35).
- cpp hazard: never write `*/` inside a SQL comment (e.g. write `DEP_* / FEAT_*`, not `DEP_*/FEAT_*`) — it terminates the comment block. See [extension/laplace_substrate/sql/README.md](../../extension/laplace_substrate/sql/README.md#L11-L14).
- New relation kinds go in `KindRegistry.Canon` (C#) — the single source of truth for rank/symmetry/roll-up. SQL needs no per-kind DDL; kinds are entities. See [extension/laplace_substrate/sql/README.md](../../extension/laplace_substrate/sql/README.md#L36-L38).
- New verification/gate queries become functions in the ops surface (`18_ops_surface`), never hand-written at a call site. See [extension/laplace_substrate/sql/README.md](../../extension/laplace_substrate/sql/README.md#L32).
- The ranked-μ inference reads (`top_relations`, `completions`, `consensus_in/out`, `generate_tree`, `generate_greedy`) live in [extension/laplace_substrate/sql/17_consensus_reads.sql.in](../../extension/laplace_substrate/sql/17_consensus_reads.sql.in#L7-L95). Inference is an indexed read; do not add query-time GEMM or dense vocab² materialization.
- Test with `just regress` (pg_regress; requires `just install` first to stage the extension). See [Justfile](../../Justfile#L377-L383).
