---
name: 'Substrate SQL rules'
description: 'Rules for SQL surface work: migrations, extension SQL, helper functions'
applyTo: 'db/**,extension/**/*.sql*,scripts/**/*.sql'
---
# Substrate SQL rules

- The SQL source of truth is MANIFEST-DRIVEN and LOCKED ([doc 10](../../.scratchpad/10_SQL_Consolidation_Reconciliation.txt)):
  a completeness gate fails configure on any orphan `.sql.in`; EXT_VERSION is
  manifest-derived. Never reintroduce numbered legacy bundles; add new files through the
  manifest or the gate will fail the build.
- Before writing a new helper, check the schema's own catalog:
  `SELECT * FROM api('<substring>');` — one implementation per fact (Rule #6);
  duplication requires a documented reason.
- GiST KNN comparison points MUST reach the planner as genuine bound parameters
  (lesson L2, Rules #1/#4). Inlined constants silently kill the index strategy.
- An expensive STABLE function in a filter executes PER ROW (lesson L12). Hoist it.
- Ranking convention everywhere: `eff_mu = rating − 2·rd` (conservative Glicko-2
  estimate). Consensus keys on `consensus_id(subject, type, object)`.
- Indexes can be landmines armed by correctness fixes (lesson L10) — check write-path
  cost when adding one to attestations/consensus.
- Recycle PG backends only between ingest stages, never mid-stage.
- Verify against live data: `psql -h localhost -U postgres -d laplace`, then
  `SET search_path = laplace, public;`.
