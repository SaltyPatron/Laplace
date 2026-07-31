# Campaign: chess backlog + MCP as the standard operations surface (2026-07-30)

Status snapshot at pause. Plan of record: the ten-PR train approved 2026-07-29.
Design annex (identity re-key, eviction, fold bias, syzygy prober decision) was
written into the plan file, not the repo; the binding decisions are restated below
so this document stands alone.

## Why the campaign existed

Two operator rulings started it:

1. **`ChessVocabulary.GameId` salted provenance into a content hash**
   (`chess/game/{white}|{black}|{date}|{moves}`) — a violation of the identity law
   (GH #736). Identical play by different players minted different entities, so
   "how many times was this line played" could not be a witness count, and identical
   lines stored duplicate trajectory linestrings.
2. **The MCP server is the mandated operations surface.** Hand-written SQL through
   the `sql` escape hatch is the same violation as not using the MCP at all: every
   operation an agent needs must be a NAMED catalog function plus an MCP tool with
   documented purpose. This is why PR-10 exists and why it is scheduled last.

## Merged

| PR | Subject |
|---|---|
| #735 | Windows PostgreSQL build lane (`scripts/win/build-pg.cmd`, env vars, full-run bat) |
| #737 | MCP server publishes with CI/CD (`laplace-mcp` alongside API/SPA/uci) |
| #738 | chess eff_mu/rating/rd display-scaled (4 SQL functions + 2 audit-caught consumers) |
| #739 | evidence carries the fold's inputs (`sum_score_fp1e9`, `opponent_rd_fp1e9`) |
| #740 | **the #736 identity re-key** — content-addressed lines, provenance events |

## Open

| PR | Subject | Gate state |
|---|---|---|
| #741 | SEE + sacrifice/gambit motifs (game + position grain) | 328/328 chess tests, no regress |
| #742 | `chess_read` fixture re-staged in line/event shape + `chess_player_moves` re-key | **merging this makes main green** |
| #743 | think-time lenses (planned_quick / pressed_think / flagging) | 316/316 chess tests, no regress |
| #744 | `evict_source` — retraction + refold (#508) | SQL compiles; pg_regress not executed |
| #745 | Syzygy probe lane (Fathom kernel) | **build gate never ran** — see caveat |

#741 and #743 both touch `ChessAnalyze.AppendGame`; whichever merges second takes a
small mechanical conflict.

## Not started

- **PR-5 — `rate_paired`**: the fold collection-bias fix. Diagnosed precisely: every
  batch-delta folds against a **neutral 1500 phantom** (`consensus_fold_math.h`), so
  opponent strength never enters and a curated wins-only collection walks a rating up
  without bound. That is why `chess_ranked` is topped by Bill Wall / Emil Diemer /
  Platz rather than by strong players. Ruled-out hypotheses: `chess_ranked` already
  ranks by eff_mu with no witness floor (not a display or floor bug); trust shaping
  alone only slows the walk. Designed fix: native iterative Glicko sweeps over
  `PLAYED_BY` evidence with **real opponents** at the previous sweep's rating/RD,
  source trust entering as opponent-RD inflation, and **witnessed PGN Elo tags as
  priors** to fix the gauge freedom (pairwise ratings determine only differences).
  Runs as a canonical rebuild verb — the neutral inline fold stays as the online
  approximation, exactly the `highway_mask_deposit` vs `_rebuild` pattern.
- **PR-9 — `chess_position_witnesses`**: the position-anecdote read ("Fischer played
  this in 1962"). Two-hop indexed read off the re-key; must be EXPLAINed and timed on
  the STARTING POSITION (the highest-degree node in the substrate) before shipping.
- **PR-10 — MCP tool families + first MCP test project**: chess (13 functions), ops
  (`consensus_partition_pressure`, `substrate_pulse`, `ingest_runs`, `ops.app_log`
  readback), evidence (`evidence_receipt` — explainability has no MCP surface today).
  Nothing tests `McpServer.Handle` at all right now. Also owed: a bounded cold-box
  mode for the `health` tool (`substrate_health()` times out at 15s on a
  crash-recovered box, while `substrate_pulse()` answered the whole triage in one
  call), and demoting `sql` in the catalog to documented last-resort.

## Findings and rulings recorded during the campaign

- **Fathom placement is wrong in #745 as pushed.** The vendored Syzygy prober was
  wired into `engine/core`, which `extension/laplace_substrate/CMakeLists.txt:112`
  links — so the tablebase prober would ship inside every PostgreSQL backend.
  Operator ruling: **Syzygy is decomposition/validation tooling, not part of
  Laplace.** It must follow the ChessStockfish pattern (an external tool the ingest
  lane resolves), not the `laplace_core` pattern (the substrate's own math). Fixing
  this is a prerequisite for merging #745.
- **`chess_player_moves` was missed by the #740 re-key** — it joined MOVE evidence
  `context_id` against colour-fact *subjects*; under #736 the subject is the line and
  the context is the event, so it returned nothing against newly-ingested data. Fixed
  in #742 with regress coverage that fails against both the old function and the old
  staging.
- **Deferred DB validation bites at merge.** #740 stated its DB validation as
  deferred and turned main red on exactly that. The lesson the repo already knew
  (#688/#689) repeated: branch-dispatch CI (`gh workflow run ... --ref <branch>`) is
  the same gate one step earlier, and is cheap compared to a red main.
- **An apphost needs its entry DLL trio.** #737 found that copying only the apphost
  binary (the existing `laplace-uci` overlay pattern) produces a binary that passes
  `test -x` and cannot execute. `laplace-uci` has the same latent defect — untouched,
  flagged for follow-up.

## Operational state (hart-server)

- **The 2026-07-30 05:05 UTC hard hang was a dying disk**, not the app: `/dev/sdf`
  (WD Green, 89,705 power-on hours) reported `Reallocated_Sector_Ct` FAILING_NOW,
  657 pending sectors and **8,994 UDMA CRC errors** — bus-level faults that hang a
  box under load. It held 74 MB. Removed. The UD seed then ran 2h31m to success on
  the same workload that had killed it, which is the confirmation.
- Substrate survived intact (last flush committed one minute before the hang);
  `Seed — knowledge / ud` resumed through the novelty gate with no double-count.
- **Both schema-changing PRs (#739, #740) are merged, so a reseed is owed.** The
  campaign was deliberately batched so the whole train costs exactly one reseed.
- Syzygy 3-4-5 tables staged: 290 files (145 WDL + 145 DTZ, 939 MB) at
  `/vault/Data/Games/Chess/syzygy/3-4-5/`. 6-men (~150 GB) deferred.
- `/opt/laplace` rides a RAID0 of two Intel SSDs — no redundancy under the install
  tree. Noted, not addressed.
- The Windows standalone PostgreSQL service (`postgresql-x64-18`) is **retired in
  place**; the intended local cluster is the custom build from `external/postgresql`
  via #735's lane. Until that runs locally, all local DB validation defers to CI.

## Merge order from here

1. **#742** — makes main green.
2. #741 / #743 in either order (mechanical conflict for the second).
3. #744.
4. #745 **only after** the Fathom relocation above.
5. Reseed when convenient — already owed by merged work, and it is one reseed for
   everything.
