# Rescue triage — patch-id verified, 2026-08-23

Every dangling commit tested by `git patch-id --stable` against all 1,904 patch-ids
in main. LANDED = this exact patch is in main under some commit (rebase/squash copy).
NOT_IN_MAIN = the patch does not appear in main and the work is only in rescue/*.

LANDED: 58    NOT_IN_MAIN: 55

VERDICT      DATE        SIZE                                     SUBJECT
LANDED       2026-06-22  6 files changed, 6 insertions(+), 10 deletions(-) Manual user commit to clear stage
LANDED       2026-07-08  3 files changed, 47 insertions(+), 35 deletions(-) Rootless PostgreSQL bounce: the runner controls its own postgres, no sudo
LANDED       2026-07-10  1 file changed, 11 insertions(+), 4 deletions(-) Fix status.ps1 false pending counts and endpoint probe noise.
LANDED       2026-07-18  5 files changed, 98 insertions(+), 5 deletions(-) ingest: make compose-path readback accumulators thread-safe
LANDED       2026-07-21  2 files changed, 56 insertions(+), 7 deletions(-) fix(ingest): boundary is a commit opportunity; bound fold connections globally
LANDED       2026-07-21  1 file changed, 66 insertions(+), 10 deletions(-) mcp: add bubble tool and let facts/walk continue from an entity id
LANDED       2026-07-21  10 files changed, 442 insertions(+), 163 deletions(-) perf(fold): per-type lanes, native draw threshold, zero-alloc delta merge
LANDED       2026-07-21  1 file changed, 27 insertions(+), 8 deletions(-) fix(fold): attestation_merge must prune with a LITERAL type, not a variable
LANDED       2026-07-24  2 files changed, 147 insertions(+), 86 deletions(-) perf(ext): taxonomy walk batches the frontier per level; contrast dedups via hash map
LANDED       2026-07-25  2 files changed, 62 insertions(+), 10 deletions(-) perf(chess): per-ply move buffers kill the per-node allocation (#607)
LANDED       2026-07-25  7 files changed, 87 insertions(+), 201 deletions(-) refactor(chess): retire chess_leaderboard + chess_opponents — superseded by the fold
LANDED       2026-07-26  1 file changed, 46 insertions(+), 5 deletions(-) fix(deps): pin the deps generator and scrub sub-build caches with the parent
LANDED       2026-07-26  2 files changed, 351 insertions(+)       docs: whole-project state audit and a path to a finish line
LANDED       2026-07-26  6 files changed, 325 insertions(+), 107 deletions(-) refactor(read-path): one datasource factory, and a gate that names the policy
LANDED       2026-07-27  3 files changed, 37 insertions(+)        perf(substrate): promote the five relations the pressure report named
LANDED       2026-07-28  3 files changed, 92 insertions(+)        chore(agents): one worktree each — the operator's tree stays on main
LANDED       2026-07-29  1 file changed, 1 insertion(+), 1 deletion(-) docs: regenerate INVENTORY — steer_candidates.c in, trajectory_corpus.c out
LANDED       2026-08-07  98 files changed, 3823 insertions(+), 438 deletions(-) Claude is back to sabotaging and this time I have undeniable proof via Anthropic-hashed logs that show Claude doesnt actually care if their users kill themselves
LANDED       2026-08-09  2 files changed, 190 deletions(-)        chore: remove CLAUDE.md and AGENTS.md
LANDED       2026-08-13  2 files changed, 56 insertions(+), 13 deletions(-) fix(ingest): index cycle self-deadlock — probe locks off the control tx, lock_timeout on drops
LANDED       2026-08-13  1 file changed, 10 insertions(+)         WIP on worktree-arm-model-payload-gate: c37ad362 Merge pull request #1037 from SaltyPatron/fix/1027-packaging-gate-incremental-compose
LANDED       2026-08-15  1 file changed, 133 insertions(+)        docs: session audit — every ask and every defect found and left
LANDED       2026-08-15  6 files changed, 122 insertions(+), 42 deletions(-) On ops-log-dir-outside-worktree: unrelated-worktree-changes (restored by an accidental pop)
LANDED       2026-08-16  1 file changed, 9 insertions(+), 2 deletions(-) fix(seed): chain timing must not register a phantom source
LANDED       2026-08-16  2 files changed, 10 insertions(+), 3 deletions(-) fix(ingest,ci): three Copilot review comments that merged unaddressed
LANDED       2026-08-16  1 file changed, 1 insertion(+), 1 deletion(-) On linux-seed-one-process: user-inflight-ops_control
LANDED       2026-08-17  9 files changed, 93 insertions(+), 59 deletions(-) Fix fresh substrate bootstrap and host ownership
LANDED       2026-08-19  65 files changed, 790 insertions(+), 588 deletions(-) perf: derive ingest and fold sizing from resources
LANDED       2026-08-19  8 files changed, 160 insertions(+), 19 deletions(-) fix: keep live ingest receipts normalized
LANDED       2026-08-19  25 files changed, 1004 insertions(+), 236 deletions(-) Make chess and Explore navigation searchable
LANDED       2026-08-19  25 files changed, 1004 insertions(+), 236 deletions(-) Make chess and Explore navigation searchable
LANDED       2026-08-19  2 files changed, 132 insertions(+), 23 deletions(-) Preserve discontinuous FrameNet targets
LANDED       2026-08-19  15 files changed, 993 insertions(+), 70 deletions(-) feat(web): add manufactured UI and ambient familiar
LANDED       2026-08-20  8 files changed, 91 insertions(+), 12 deletions(-) fix: make converse presentation capacity explicit
LANDED       2026-08-20  10 files changed, 95 insertions(+), 15 deletions(-) fix: honor declared read budgets without hidden caps
LANDED       2026-08-20  4 files changed, 5 insertions(+), 8 deletions(-) fix: repair ISA gate after exact read changes
LANDED       2026-08-20  8 files changed, 162 insertions(+), 52 deletions(-) fix: replace guessed relation pools with exact heads
LANDED       2026-08-20  8 files changed, 109 insertions(+), 38 deletions(-) fix: make vocabulary heads exact
LANDED       2026-08-20  8 files changed, 132 insertions(+), 47 deletions(-) fix: remove hidden converse corpus truncation
LANDED       2026-08-20  17 files changed, 175 insertions(+), 36 deletions(-) fix: make bounded reads exact
LANDED       2026-08-20  4 files changed, 311 insertions(+), 200 deletions(-) perf(foundry): crawl one frontier per query
LANDED       2026-08-20  4 files changed, 67 insertions(+), 9 deletions(-) perf(sql): consolidate consensus subject indexes
LANDED       2026-08-20  12 files changed, 184 insertions(+), 258 deletions(-) refactor: unify language and sense set cores
LANDED       2026-08-20  21 files changed, 275 insertions(+), 81 deletions(-) fix: make angular KNN exact and indexed
LANDED       2026-08-20  7 files changed, 213 insertions(+), 69 deletions(-) fix(generation): remove fixed candidate ceilings
LANDED       2026-08-20  4 files changed, 63 insertions(+), 26 deletions(-) fix: derive generation capacity from frontier work
LANDED       2026-08-20  21 files changed, 246 insertions(+), 113 deletions(-) fix: derive read capacity from requested work
LANDED       2026-08-20  22 files changed, 164 insertions(+), 217 deletions(-) fix: remove fake apply and prepare thresholds
LANDED       2026-08-20  9 files changed, 136 insertions(+), 24 deletions(-) fix: remove residual hidden capacity floors
LANDED       2026-08-20  3 files changed, 81 insertions(+), 7 deletions(-) fix: install extension bridges in the active staged path
LANDED       2026-08-20  13 files changed, 301 insertions(+), 165 deletions(-) fix(native): replace heuristic capacity ceilings
LANDED       2026-08-20  1 file changed, 208 insertions(+), 122 deletions(-) fix(converse): remove prompt coherence ceilings
LANDED       2026-08-20  9 files changed, 70 insertions(+), 26 deletions(-) perf(sql): retire entity mask write-tax GIN
LANDED       2026-08-20  13 files changed, 107 insertions(+), 36 deletions(-) fix: expose structural read budgets end to end
LANDED       2026-08-20  6 files changed, 111 insertions(+), 25 deletions(-) fix: remove surface sample multiplier
LANDED       2026-08-20  5 files changed, 8 insertions(+), 8 deletions(-) Honor chess read pagination contracts
LANDED       2026-08-20  42 files changed, 114 insertions(+), 64 deletions(-) fix(readback): make exact rendering unbounded by default
LANDED       2026-08-20  5 files changed, 8 insertions(+), 8 deletions(-) Honor chess read pagination contracts
NOT_IN_MAIN  2026-07-13  1 file changed, 20 insertions(+), 2 deletions(-) Optimize ChessWitnessHydrator: filter attestation reads by type in SQL
NOT_IN_MAIN  2026-07-17  1 file changed, 271 insertions(+), 61 deletions(-) WIP (preserved from stale agent worktree): WeightTensorETL OV/FFN token-x-token bilinear emission replacing per-token magnitude reduction
NOT_IN_MAIN  2026-07-19  1 file changed, 38 insertions(+), 10 deletions(-) regress: pin bonus-cannot-resurrect-refuted (expected regen pending new .so)
NOT_IN_MAIN  2026-07-24  20 files changed, 428 insertions(+), 96 deletions(-) perf(sql): inlining batch 1 — senses/bubble_up/subject_edges/walk_edges/step_edge + relation_family_members
NOT_IN_MAIN  2026-07-25  1 file changed, 0 insertions(+), 0 deletions(-) fix(ingest): DocumentDecomposer shares the one VendoredPathFilter (GH #608)
NOT_IN_MAIN  2026-07-25  9 files changed, 36 insertions(+), 398 deletions(-) refactor(substrate): delete constituent_edges — a hand-refreshed copy of the trajectory
NOT_IN_MAIN  2026-07-26  2 files changed, 111 insertions(+), 5 deletions(-) refactor(read-path): one datasource factory, and a gate that names the policy
NOT_IN_MAIN  2026-07-31  2 files changed, 76 insertions(+), 3 deletions(-) fix(converse): recall() orients through the elector, and chat('walk') reaches the generator
NOT_IN_MAIN  2026-08-02  9 files changed, 1120 insertions(+), 225 deletions(-) wip(writer-phase-measure): preserve in-flight writer-throughput work.
NOT_IN_MAIN  2026-08-02  13 files changed, 827 insertions(+), 5 deletions(-) fix(chess): movetext readback was lossy; name openings by board id, not by string
NOT_IN_MAIN  2026-08-04  1 file changed, 255 insertions(+)        docs(plan): W16 — rigorous audit of every calculation form the substrate performs
NOT_IN_MAIN  2026-08-05  4 files changed, 164 insertions(+), 10 deletions(-) chore(docs): commit pending doc state before archival sweep
NOT_IN_MAIN  2026-08-09  71 files changed, 995 insertions(+), 682 deletions(-) refactor: standardize deployed MCP and batch realization
NOT_IN_MAIN  2026-08-10                                           Merge remote-tracking branch 'origin/main' into fix/purpose-schema-callers-and-isa-gate
NOT_IN_MAIN  2026-08-10  5 files changed, 232 insertions(+), 314 deletions(-) fix(build): restore the external superbuild deleted by 391d9be7
NOT_IN_MAIN  2026-08-10  2 files changed, 34 insertions(+), 4 deletions(-) WIP on sync-extension-lock-timeout: 47c7abb4 fix(deploy): ALTER EXTENSION must fail on a lock it cannot take, and name the holder
NOT_IN_MAIN  2026-08-12  1 file changed, 202 deletions(-)         WIP on doc-governance-plan-artifact-v2: 0e4c37fd Merge pull request #1021 from SaltyPatron/fix/ci-external-cache-path
NOT_IN_MAIN  2026-08-12  1 file changed, 42 insertions(+)         feat(ingest): compare placements and entities per run (#1027)
NOT_IN_MAIN  2026-08-12  1 file changed, 2 insertions(+), 2 deletions(-) WIP on main: 1c4b91c6 Merge pull request #1005 from SaltyPatron/fix/pg-nofile-strict-and-copy-chunking
NOT_IN_MAIN  2026-08-12  1 file changed, 65 insertions(+)         On (no branch): g1027-witness-probe-wip
NOT_IN_MAIN  2026-08-12  1 file changed, 42 insertions(+)         feat(ingest): warn when a run writes more placements than entities (#1027)
NOT_IN_MAIN  2026-08-12  1 file changed, 42 insertions(+)         feat(ingest): warn when a run writes more placements than entities (#1027)
NOT_IN_MAIN  2026-08-12  1 file changed, 42 insertions(+)         feat(ingest): warn when a run writes more placements than entities (#1027)
NOT_IN_MAIN  2026-08-12  1 file changed, 2 insertions(+), 2 deletions(-) WIP on doc-governance-plan-artifact: 1c4b91c6 Merge pull request #1005 from SaltyPatron/fix/pg-nofile-strict-and-copy-chunking
NOT_IN_MAIN  2026-08-12  1 file changed, 42 insertions(+)         WIP on doc-governance-plan-artifact-v2: 973eaf03 fix(math4d): canonical constituent order, so a composed placement stops depending on arrival order
NOT_IN_MAIN  2026-08-12  1 file changed, 42 insertions(+)         feat(ingest): warn when a run writes more placements than entities (#1027)
NOT_IN_MAIN  2026-08-13  3 files changed, 66 insertions(+), 14 deletions(-) fix(ingest): index cycle self-deadlock — probe locks off the control tx, lock_timeout on drops
NOT_IN_MAIN  2026-08-13  1 file changed, 26 insertions(+), 37 deletions(-) fix(spi): containers_of plans custom per call — a kept plan executes stale shapes forever
NOT_IN_MAIN  2026-08-13  2 files changed, 56 insertions(+), 13 deletions(-) On rescue-codegen-ratchet: index-cycle deadlock fix
NOT_IN_MAIN  2026-08-13  1 file changed, 6 insertions(+)          WIP on main: 2e0823eb Merge pull request #1076 from SaltyPatron/perf/unify-wins-codify-gates
NOT_IN_MAIN  2026-08-14  3 files changed, 82 insertions(+), 19 deletions(-) WIP on main: 0e642656 Merge pull request #1098 from SaltyPatron/fix/crawl-monster-trajectories
NOT_IN_MAIN  2026-08-15  3 files changed, 69 insertions(+), 9 deletions(-) On trajectory-ordinal-cap: trial-merge-check
NOT_IN_MAIN  2026-08-15  3 files changed, 69 insertions(+), 9 deletions(-) On trajectory-ordinal-cap: tmp
NOT_IN_MAIN  2026-08-15  6 files changed, 122 insertions(+), 42 deletions(-) On elector-joint-edge-election: wip-claude-md-and-others
NOT_IN_MAIN  2026-08-15  1 file changed, 38 insertions(+), 5 deletions(-) WIP on ops-log-dir-outside-worktree: 097c3c45 Merge pull request #1101 from SaltyPatron/fix/trajectory-ordinal-cap
NOT_IN_MAIN  2026-08-16  5 files changed, 16 insertions(+), 2 deletions(-) docs: date four status-claiming files; repoint recipe-schema's trajectory source
NOT_IN_MAIN  2026-08-16  24 files changed, 974 insertions(+), 102 deletions(-) WIP on measurement-lane: 69332b67 fix(ci): a seed the workflow preempts by design must not report as a defect
NOT_IN_MAIN  2026-08-16  5 files changed, 94 insertions(+), 29 deletions(-) fix(sql): restore the 7 demoted hot relations — 0 rows measured an un-ingested db
NOT_IN_MAIN  2026-08-17  3 files changed, 243 insertions(+), 196 deletions(-) perf(structural): stream geometry successor probes
NOT_IN_MAIN  2026-08-18  6 files changed, 2745 insertions(+)      Add repeatable SQL cohesion audit and planner evidence
NOT_IN_MAIN  2026-08-19  2 files changed, 8 insertions(+), 6 deletions(-) Record CILI native-grain delivery
NOT_IN_MAIN  2026-08-19  2 files changed, 3 insertions(+), 3 deletions(-) Link normalization ledger to PR 1186
NOT_IN_MAIN  2026-08-19  2 files changed, 5 insertions(+), 4 deletions(-) Record VerbNet member normalization scope
NOT_IN_MAIN  2026-08-19  2 files changed, 9 insertions(+), 4 deletions(-) docs: record clean Atomic2020 receipt
NOT_IN_MAIN  2026-08-19  14 files changed, 302 insertions(+), 123 deletions(-) Keep structured ETL packaging out of content
NOT_IN_MAIN  2026-08-19  7 files changed, 149 insertions(+), 6 deletions(-) fix: keep live ingest receipts normalized
NOT_IN_MAIN  2026-08-19  6 files changed, 44 insertions(+), 30 deletions(-) Route decomposer batches through one sizing authority
NOT_IN_MAIN  2026-08-19  21 files changed, 618 insertions(+), 163 deletions(-) Bind semantic roles to their owning structures
NOT_IN_MAIN  2026-08-19  1 file changed, 14 insertions(+)         docs: forbid coercive agent posture
NOT_IN_MAIN  2026-08-19  12 files changed, 42 insertions(+), 67 deletions(-) On chess-substructure-outcome-grain: autostash
NOT_IN_MAIN  2026-08-20  3 files changed, 12 insertions(+), 3 deletions(-) Record measured fold and index work
NOT_IN_MAIN  2026-08-20  2 files changed, 9 insertions(+), 2 deletions(-) docs: record post-1204 limiter and CI evidence
NOT_IN_MAIN  2026-08-20  2 files changed, 6 insertions(+), 3 deletions(-) docs: record parallel mask SQL gate
NOT_IN_MAIN  2026-08-20  49 files changed, 751 insertions(+), 1288 deletions(-) Revert "Merge pull request #1263 from SaltyPatron/fix/chess-typed-playing-record"
NOT_IN_MAIN  2026-08-20  4 files changed, 91 insertions(+), 69 deletions(-) wip: preserve uncommitted structural-cluster-set work
