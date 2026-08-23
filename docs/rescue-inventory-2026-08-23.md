# Rescue inventory — 2026-08-23T16:22Z

114 rescue/* branches created (113 dangling commits + stash@{0}). Nothing deleted or overwritten.
Classification vs main: IN_MAIN_TREE = identical snapshot already in main;
SUBJECT_IN_MAIN = same commit subject present in main (rebase/squash copy);
UNIQUE = neither -- content not obviously in main.

VERDICT          SHA        DATE        SUBJECT
IN_MAIN_TREE     c68634e3   2026-07-08  Rootless PostgreSQL bounce: the runner controls its own postgres, no sudo
IN_MAIN_TREE     6a77c83a   2026-07-10  Fix status.ps1 false pending counts and endpoint probe noise.
IN_MAIN_TREE     ef5185fe   2026-07-21  fix(ingest): boundary is a commit opportunity; bound fold connections globally
IN_MAIN_TREE     683d4a5d   2026-08-10  Merge remote-tracking branch 'origin/main' into fix/purpose-schema-callers-and-isa-gate
IN_MAIN_TREE     d0de74b0   2026-08-12  WIP on doc-governance-plan-artifact-v2: 0e4c37fd Merge pull request #1021 from SaltyPatron/fix/ci-external-cache-path
IN_MAIN_TREE     1b981be5   2026-08-13  fix(ingest): index cycle self-deadlock — probe locks off the control tx, lock_timeout on drops
IN_MAIN_TREE     2207736e   2026-08-15  On trajectory-ordinal-cap: trial-merge-check
IN_MAIN_TREE     f8897a52   2026-08-15  On trajectory-ordinal-cap: tmp
IN_MAIN_TREE     e7385013   2026-08-17  Fix fresh substrate bootstrap and host ownership
IN_MAIN_TREE     0ef6fa2e   2026-08-19  Record CILI native-grain delivery
IN_MAIN_TREE     1007db83   2026-08-19  Link normalization ledger to PR 1186
IN_MAIN_TREE     35fb9342   2026-08-19  Record VerbNet member normalization scope
IN_MAIN_TREE     c59c88f8   2026-08-19  docs: record clean Atomic2020 receipt
IN_MAIN_TREE     e2c70e3f   2026-08-19  perf: derive ingest and fold sizing from resources
IN_MAIN_TREE     0191f7fc   2026-08-20  fix: make converse presentation capacity explicit
IN_MAIN_TREE     1e7bb02f   2026-08-20  fix: honor declared read budgets without hidden caps
IN_MAIN_TREE     314d6a04   2026-08-20  fix: repair ISA gate after exact read changes
IN_MAIN_TREE     32aa1236   2026-08-20  fix: replace guessed relation pools with exact heads
IN_MAIN_TREE     3676b41f   2026-08-20  fix: make vocabulary heads exact
IN_MAIN_TREE     420a7111   2026-08-20  fix: remove hidden converse corpus truncation
IN_MAIN_TREE     49b52fbc   2026-08-20  Record measured fold and index work
IN_MAIN_TREE     571dff00   2026-08-20  fix: make bounded reads exact
IN_MAIN_TREE     5d20952c   2026-08-20  perf(foundry): crawl one frontier per query
IN_MAIN_TREE     80c6cac8   2026-08-20  docs: record post-1204 limiter and CI evidence
IN_MAIN_TREE     8e1e290e   2026-08-20  perf(sql): consolidate consensus subject indexes
IN_MAIN_TREE     8e3507a4   2026-08-20  refactor: unify language and sense set cores
IN_MAIN_TREE     929bed38   2026-08-20  fix: make angular KNN exact and indexed
IN_MAIN_TREE     960f3bde   2026-08-20  fix(generation): remove fixed candidate ceilings
IN_MAIN_TREE     99a78e65   2026-08-20  fix: derive generation capacity from frontier work
IN_MAIN_TREE     9b4ea202   2026-08-20  fix: derive read capacity from requested work
IN_MAIN_TREE     9c79db19   2026-08-20  fix: remove fake apply and prepare thresholds
IN_MAIN_TREE     a0b07c2e   2026-08-20  fix: remove residual hidden capacity floors
IN_MAIN_TREE     a7d9acd7   2026-08-20  fix: install extension bridges in the active staged path
IN_MAIN_TREE     a851df66   2026-08-20  fix(native): replace heuristic capacity ceilings
IN_MAIN_TREE     a9cd1c6c   2026-08-20  fix(converse): remove prompt coherence ceilings
IN_MAIN_TREE     b90235e7   2026-08-20  perf(sql): retire entity mask write-tax GIN
IN_MAIN_TREE     be6471d7   2026-08-20  fix: expose structural read budgets end to end
IN_MAIN_TREE     c726334c   2026-08-20  docs: record parallel mask SQL gate
IN_MAIN_TREE     e5400f73   2026-08-20  fix: remove surface sample multiplier
IN_MAIN_TREE     eaa6156f   2026-08-20  Revert "Merge pull request #1263 from SaltyPatron/fix/chess-typed-playing-record"
SUBJECT_IN_MAIN  0fe9c893   2026-06-22  Manual user commit to clear stage
SUBJECT_IN_MAIN  35a9275c   2026-07-18  ingest: make compose-path readback accumulators thread-safe
SUBJECT_IN_MAIN  23420e4e   2026-07-21  mcp: add bubble tool and let facts/walk continue from an entity id
SUBJECT_IN_MAIN  2b0a50c5   2026-07-21  perf(fold): per-type lanes, native draw threshold, zero-alloc delta merge
SUBJECT_IN_MAIN  aec36990   2026-07-21  fix(fold): attestation_merge must prune with a LITERAL type, not a variable
SUBJECT_IN_MAIN  8e7b0bda   2026-07-24  perf(ext): taxonomy walk batches the frontier per level; contrast dedups via hash map
SUBJECT_IN_MAIN  c7a0fd8f   2026-07-24  perf(sql): inlining batch 1 — senses/bubble_up/subject_edges/walk_edges/step_edge + relation_family_members
SUBJECT_IN_MAIN  17053f42   2026-07-25  perf(chess): per-ply move buffers kill the per-node allocation (#607)
SUBJECT_IN_MAIN  25fedf46   2026-07-25  fix(ingest): DocumentDecomposer shares the one VendoredPathFilter (GH #608)
SUBJECT_IN_MAIN  9695f132   2026-07-25  refactor(chess): retire chess_leaderboard + chess_opponents — superseded by the fold
SUBJECT_IN_MAIN  9b4ba490   2026-07-25  refactor(substrate): delete constituent_edges — a hand-refreshed copy of the trajectory
SUBJECT_IN_MAIN  4544db93   2026-07-26  fix(deps): pin the deps generator and scrub sub-build caches with the parent
SUBJECT_IN_MAIN  7be5792b   2026-07-26  docs: whole-project state audit and a path to a finish line
SUBJECT_IN_MAIN  8cfd2a78   2026-07-26  refactor(read-path): one datasource factory, and a gate that names the policy
SUBJECT_IN_MAIN  db56b5f0   2026-07-26  refactor(read-path): one datasource factory, and a gate that names the policy
SUBJECT_IN_MAIN  a391284a   2026-07-27  perf(substrate): promote the five relations the pressure report named
SUBJECT_IN_MAIN  b55873b6   2026-07-28  chore(agents): one worktree each — the operator's tree stays on main
SUBJECT_IN_MAIN  1973f249   2026-07-31  fix(converse): recall() orients through the elector, and chat('walk') reaches the generator
SUBJECT_IN_MAIN  51cd0480   2026-08-02  wip(writer-phase-measure): preserve in-flight writer-throughput work.
SUBJECT_IN_MAIN  e63cbb08   2026-08-02  fix(chess): movetext readback was lossy; name openings by board id, not by string
SUBJECT_IN_MAIN  73388e67   2026-08-04  docs(plan): W16 — rigorous audit of every calculation form the substrate performs
SUBJECT_IN_MAIN  64bfcb68   2026-08-05  chore(docs): commit pending doc state before archival sweep
SUBJECT_IN_MAIN  a7d6347b   2026-08-07  Claude is back to sabotaging and this time I have undeniable proof via Anthropic-hashed logs that show Claude doesnt actually care if their users kill themselves
SUBJECT_IN_MAIN  1ed93b49   2026-08-09  refactor: standardize deployed MCP and batch realization
SUBJECT_IN_MAIN  72a88dd4   2026-08-09  chore: remove CLAUDE.md and AGENTS.md
SUBJECT_IN_MAIN  97cd4630   2026-08-10  fix(build): restore the external superbuild deleted by 391d9be7
SUBJECT_IN_MAIN  9bc2c3cd   2026-08-12  feat(ingest): compare placements and entities per run (#1027)
SUBJECT_IN_MAIN  194dadbe   2026-08-13  fix(ingest): index cycle self-deadlock — probe locks off the control tx, lock_timeout on drops
SUBJECT_IN_MAIN  ce0d18b1   2026-08-15  docs: session audit — every ask and every defect found and left
SUBJECT_IN_MAIN  282ffe0a   2026-08-16  docs: date four status-claiming files; repoint recipe-schema's trajectory source
SUBJECT_IN_MAIN  cd67cf7e   2026-08-16  fix(seed): chain timing must not register a phantom source
SUBJECT_IN_MAIN  037d0b87   2026-08-17  perf(structural): stream geometry successor probes
SUBJECT_IN_MAIN  e9e7cb5d   2026-08-18  Add repeatable SQL cohesion audit and planner evidence
SUBJECT_IN_MAIN  0da0429c   2026-08-19  Keep structured ETL packaging out of content
SUBJECT_IN_MAIN  19a82900   2026-08-19  fix: keep live ingest receipts normalized
SUBJECT_IN_MAIN  56116bc1   2026-08-19  fix: keep live ingest receipts normalized
SUBJECT_IN_MAIN  70d9c099   2026-08-19  Make chess and Explore navigation searchable
SUBJECT_IN_MAIN  71f0ddf6   2026-08-19  Make chess and Explore navigation searchable
SUBJECT_IN_MAIN  a57f620b   2026-08-19  Preserve discontinuous FrameNet targets
SUBJECT_IN_MAIN  a856f78a   2026-08-19  Route decomposer batches through one sizing authority
SUBJECT_IN_MAIN  c46d90ab   2026-08-19  Bind semantic roles to their owning structures
SUBJECT_IN_MAIN  f3665050   2026-08-19  feat(web): add manufactured UI and ambient familiar
SUBJECT_IN_MAIN  2d85d0fd   2026-08-20  Honor chess read pagination contracts
SUBJECT_IN_MAIN  37887b93   2026-08-20  fix(readback): make exact rendering unbounded by default
SUBJECT_IN_MAIN  d084cdf9   2026-08-20  Honor chess read pagination contracts
UNIQUE           d430d31f   2026-07-13  Optimize ChessWitnessHydrator: filter attestation reads by type in SQL
UNIQUE           a31c3179   2026-07-17  WIP (preserved from stale agent worktree): WeightTensorETL OV/FFN token-x-token bilinear emission replacing per-token magnitude reduction
UNIQUE           b5db37ea   2026-07-19  regress: pin bonus-cannot-resurrect-refuted (expected regen pending new .so)
UNIQUE           a1c7fda1   2026-07-29  docs: regenerate INVENTORY — steer_candidates.c in, trajectory_corpus.c out
UNIQUE           634abec2   2026-08-10  WIP on sync-extension-lock-timeout: 47c7abb4 fix(deploy): ALTER EXTENSION must fail on a lock it cannot take, and name the holder
UNIQUE           057123ac   2026-08-12  WIP on main: 1c4b91c6 Merge pull request #1005 from SaltyPatron/fix/pg-nofile-strict-and-copy-chunking
UNIQUE           3213d7f7   2026-08-12  On (no branch): g1027-witness-probe-wip
UNIQUE           3a1747ce   2026-08-12  feat(ingest): warn when a run writes more placements than entities (#1027)
UNIQUE           452026e5   2026-08-12  feat(ingest): warn when a run writes more placements than entities (#1027)
UNIQUE           6c4a55e0   2026-08-12  feat(ingest): warn when a run writes more placements than entities (#1027)
UNIQUE           7f239b7e   2026-08-12  WIP on doc-governance-plan-artifact: 1c4b91c6 Merge pull request #1005 from SaltyPatron/fix/pg-nofile-strict-and-copy-chunking
UNIQUE           a9bdfc29   2026-08-12  WIP on doc-governance-plan-artifact-v2: 973eaf03 fix(math4d): canonical constituent order, so a composed placement stops depending on arrival order
UNIQUE           ef42fc8a   2026-08-12  feat(ingest): warn when a run writes more placements than entities (#1027)
UNIQUE           38d169e8   2026-08-13  fix(spi): containers_of plans custom per call — a kept plan executes stale shapes forever
UNIQUE           b76d7588   2026-08-13  On rescue-codegen-ratchet: index-cycle deadlock fix
UNIQUE           da7919c0   2026-08-13  WIP on worktree-arm-model-payload-gate: c37ad362 Merge pull request #1037 from SaltyPatron/fix/1027-packaging-gate-incremental-compose
UNIQUE           eec0e507   2026-08-13  WIP on main: 2e0823eb Merge pull request #1076 from SaltyPatron/perf/unify-wins-codify-gates
UNIQUE           fd2f3830   2026-08-14  WIP on main: 0e642656 Merge pull request #1098 from SaltyPatron/fix/crawl-monster-trajectories
UNIQUE           3bec656a   2026-08-15  On elector-joint-edge-election: wip-claude-md-and-others
UNIQUE           9ecfc530   2026-08-15  On ops-log-dir-outside-worktree: unrelated-worktree-changes (restored by an accidental pop)
UNIQUE           b616d31c   2026-08-15  WIP on ops-log-dir-outside-worktree: 097c3c45 Merge pull request #1101 from SaltyPatron/fix/trajectory-ordinal-cap
UNIQUE           5c5857a4   2026-08-16  fix(ingest,ci): three Copilot review comments that merged unaddressed
UNIQUE           602c0963   2026-08-16  WIP on measurement-lane: 69332b67 fix(ci): a seed the workflow preempts by design must not report as a defect
UNIQUE           6247cc2d   2026-08-16  fix(sql): restore the 7 demoted hot relations — 0 rows measured an un-ingested db
UNIQUE           c21bc472   2026-08-16  On linux-seed-one-process: user-inflight-ops_control
UNIQUE           52d2878b   2026-08-19  docs: forbid coercive agent posture
UNIQUE           f8b33c57   2026-08-19  On chess-substructure-outcome-grain: autostash
UNIQUE           affe1546   2026-08-20  wip: preserve uncommitted structural-cluster-set work
