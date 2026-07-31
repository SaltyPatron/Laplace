# Working rules — Laplace

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) first; it describes the system with
file citations. This file is only the rules for changing it.

Every rule below is enforced by something in the tree — a schema constraint, a CI job, a
test, or a build step that fails without it. Nothing here is a principle for its own
sake. If you find a rule here that no code enforces, delete it.

## Ground truth

- **The running system outranks prose, including this file.** Verify claims at the layer
  they live on: a schema claim against the DDL or a live `psql` query, a build claim by
  running the build, a performance claim by measuring. `docs/INVENTORY.md` is generated
  and CI-gated — trust it over any count written in prose.
- `SELECT * FROM api('<substring>')` lists the installed SQL surface. Check it before
  concluding a helper doesn't exist.
- The extension is the deployment unit for substrate schema and functions. Do not add
  DbUp migrations for substrate objects, and do not hand-`ALTER` or hand-`INSERT` into a
  live database — `.sql.in` files are the schema of record.

## Identity

- Ids are content hashes and are never constructed outside the system. Resolve through
  `canonical_id()`, `word_id()`, `relation_type_id()`, `consensus_id()`.
- `tier` is a column, not part of the hash. Identical content is one id at any tier, so
  never select rows by tier as a proxy for a role.
- Coordinate or Hilbert equality is not identity above tier 0 — composition does not
  preserve them. Order-sensitive judgments use `trajectory`.

## Writes

- Decomposers are pure: content in, `SubstrateChange` records out, zero inline SQL. The
  shared spine (`IngestBatchPipeline` → `ConsensusAccumulatingWriter` →
  `NpgsqlWorkingSetApply`) owns batching, dedup, the tier descent, the fold and the COPY.
- A decomposer must declare in `InitializeAsync` **every** relation type it emits.
  Emitting an undeclared relation faults the native attestation path.
  `DecomposerArchitectureGateTests` pins this.
- The ingest order is fixed: unpack → records → client-side dedup across the working set
  → client-side accumulation → one bulk tier descent → COPY of proven-novel rows. The
  right algorithm at the wrong point in that sequence is a defect.
- Consensus accumulates at ingest. There is no backfill or rebuild path; do not add one.
- Rows are idempotent under re-ingest, but testimony is not — a re-ingest doubles
  observation counts by design. Sources need a marker guard.
- Keep foreign keys off the hot tables. `consensus.sql.in` records why; integrity is
  structural.

## Relations

- `engine/manifest/relation_types.toml` is the source of truth; `scripts/codegen-attestation-law.py`
  compiles it. Never hand-edit generated C.
- Highway bits are assigned alphabetically, so **adding a relation renumbers bits and
  owes a reseed** — regenerate, never backfill.
- `hot = true` is a physical partitioning decision that follows write traffic, not
  importance. It is independent of `rank`, and changing it costs no reseed.

## Reads

- Rank by something the fold produced. An arbitrary `LIMIT` without an ordering is a
  missing ranking, not a safeguard.
- Never render an entity to text in order to classify it. Classification is an indexed
  read on the id; the render is the cost. Text is the right input only at ingest, where
  no entity exists yet.
- Don't resolve names per row. Aggregate ids, then batch through `realize_batch`.
- Per-row set-returning functions, string operations, and both-directions `OR` joins
  belong in C, not in a rewritten CTE.
- An unattested id is not an id attested false. `EXISTS` collapses that distinction and
  is silently wrong on a partial seed; `bool_or` over zero rows is NULL, which is the
  distinction. Test the unseeded case for anything that reads attestations.
- Comparison points for KNN must reach the planner as bound parameters. `EXPLAIN` before
  trusting an index. A `STABLE` function in a filter runs per row.
- `eff_mu` bodies must not carry `SET` or `STRICT` — either kills SQL inlining and the
  index path with it.
- Cost scales with a topic's degree. Timing a read on a rare word tells you nothing about
  a common one; re-time after every seed.

## Build

- After **any** engine rebuild: `build-extensions` then `install-extensions`. The
  extension links the engine statically, so engine freshness is not extension freshness.
  Extension SQL changes need `build-extensions.cmd --reconfigure`.
- `pg_regress` tests the installed extension, not your edited `.sql.in`.
- Run `scripts/win/*.cmd` through Bash (`cmd //c "scripts\\win\\test-all.cmd"`), never
  PowerShell. Never edit a `.cmd` while it is running. `scripts/win/env.cmd` is the
  toolchain source of truth.
- Validate with clean builds. Incremental builds skip the OpenAPI generation step in
  `Laplace.Endpoints.OpenAICompat`, which fails on any host stderr — an incremental green
  is not a green.
- MSB3027 means the output tree is poisoned: clean rebuild.
- Gate a branch before merging without burning a PR run:
  `gh workflow run "Laplace — build, deploy, test" --ref <branch>`.
- CI recreates the database empty. A fixture-backed check passing tells you nothing about
  a populated box; check row counts before making a claim about live behaviour.

## Operations

- One ingest at a time. An unexplained `COPY` means an ingest is running — leave it
  alone. Never kill a `Laplace.Cli`, `psql`, or backend you did not start.
- A push to `main` restarts `laplace-postgresql` and kills any running ingest. Check
  `gh run list` before starting a long one.
- One database: `laplace`. No per-run or ad-hoc databases.
- Tune PostgreSQL through the bootstrap-managed block, not `ALTER SYSTEM` or `/etc`.
- Redirect long-running output to a log file rather than streaming it.
- `/archive` and `/vault` are read-only. Never modify, move, or resize them.

## Concurrent agents

The checkout at the repo root stays on `main`. Agents get their own worktree:

```
scripts/agent-worktree.sh <agent-name> [branch]   # -> .worktrees/<agent-name>
```

Two agents in one working tree is a data-loss problem, not a merge problem: an
uncommitted edit is destroyed by the other agent's `checkout` or `stash` with no
conflict and nothing in reflog. Stage explicit paths — never `git add -A`, which sweeps
another agent's files into your commit. Commit early.
