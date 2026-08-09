# Laplace working rules

Read these entry points before changing the repository:

1. [Documentation governance](docs/DOCUMENTATION_GOVERNANCE.md) — authority hierarchy.
2. [Architecture](docs/ARCHITECTURE.md) — system shape with source citations.
3. [Normative specifications](docs/specs/README.md) — stable contracts.
4. [AGENTS.md](AGENTS.md) — harness and operational adaptation.

The running system and source evidence outrank prose. GitHub owns active work status.
Historical material in `.scratchpad/` and `docs/archive/` never selects work.

## Authorization and communication

- Work only within the implementation/mutation scope authorized by the current request.
- Research and specification do not imply runtime implementation authority.
- Inside authorized scope, execute safe known next steps without requiring a ceremonial
  trigger from the operator.
- Keep agent communication technical. Do not use human-relationship, therapeutic,
  emotional, crisis, hotline, or moral-authority framing.
- Report concrete evidence, uncertainty, affected files/state, and remaining acceptance.

## Ground truth

- Verify schema/API claims through the installed surface. Use
  `SET search_path = laplace, public;` and `api('<substring>')` before hand-writing SQL.
- Prefer canonical operations such as `substrate_health()`, `source_roster()`,
  `ingest_runs()`, and approximate count surfaces when exact scans are unnecessary.
- `docs/INVENTORY.md` owns generated countable facts. Do not hardcode changing counts in
  instructions or normative specifications.
- A sandbox permission failure, read-only view, or network error is evidence about that
  harness context only. Verify host mounts, services, credentials, and deployment state
  in an authorized host context before declaring them broken.
- Extension `.sql.in` files are schema source of truth. Do not hand-alter live substrate
  objects or add application migrations for extension-owned schema.

## Identity and physicality

- Resolve ids through canonical identity helpers; never construct them externally.
- Tier is not part of identity. Do not use tier as a proxy for role or source.
- Coordinate/Hilbert equality is not identity above tier 0.
- Ordered judgments use trajectories; geometric path metrics use realized curves.
- Mantissa-packed trajectories are typed serialization, not ordinary geometry.

## Writes and ingest

- Decomposers are pure content-to-`SubstrateChange` streams with no SQL.
- A decomposer declares every relation it emits.
- The Rule #8 sequence is fixed:
  `unpack → records → working-set dedup → accumulation → bulk descent → apply/COPY → fold completion`.
- Consensus folds during ingest. Do not create an unowned rebuild/backfill path.
- Re-ingest is identity-idempotent but adds observations by design; sources require a
  completion/marker guard.
- One ingest at a time. Do not kill another process's CLI, `psql`, or backend.

## Relations and reads

- `engine/manifest/relation_types.toml` owns canonical relations and append-only bits.
- Rank by a value produced by the fold. An unordered `LIMIT` is not ranking.
- Unknown is distinct from refuted; preserve the zero-row/NULL case.
- Do not render entities to classify them or resolve names per row.
- Batch ids through canonical realization/scoring operations.
- Keep KNN comparison points planner-visible and verify index use with `EXPLAIN`.
- Measure common/high-degree topics and declared seed profiles, not only rare examples.

## Build and verification

- After any engine rebuild, rebuild and install extensions before testing them.
- Extension SQL changes require reconfiguration when the build embeds a version hash.
- `pg_regress` tests the installed extension.
- Validate clean builds; incremental builds may skip generators.
- Treat copy/output-tree failures as requiring a clean rebuild of the affected build
  output, never a destructive cleanup of source worktrees.
- Fixture tests do not prove populated-system behavior. Name the seed/runtime profile.

## Database and host operations

- PostgreSQL lifecycle is service-managed. Never start the live cluster with `pg_ctl`.
- Never use hidden elevation or create UAC prompts.
- Never reset/drop the database unless the current request explicitly authorizes it.
- Run the repository service guard before restart/deploy paths.
- Outer concurrent index creation remains one unless a measured design changes it.
- Verify mount options with `findmnt` in the execution context that will perform the
  operation; do not encode mutable `/vault` or `/archive` mount state as repository law.

## Concurrent work and Git

The root checkout stays on `main`. Use:

`scripts/agent-worktree.sh <name> [branch]`

for every change branch. Stage explicit paths; never `git add -A`.

Never use `git checkout`, `git switch`, `git restore`, `git stash`, `git reset --hard`,
or `git clean` in this repository. Read other refs with `git show`/`git diff`; create
work through isolated worktrees. Preserve unrelated changes.

Use harness-native read/edit tools where available. Use `rg`/`rg --files` for discovery
and `apply_patch` for edits in this harness. Shell commands are for builds, tests,
version control, database clients, and other execution—not hidden file rewriting.
