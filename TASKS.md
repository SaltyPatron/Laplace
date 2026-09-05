## Recovery implementation — current evidence, 2026-09-05

- Worktree `/tmp/laplace-content-recovery`, existing PR #1496. Not yet on main or
  installed in original production. Historical checkpoints below are superseded.
- Native full-source composition preserves exact Python bytes, grammar structure,
  dynamic tier floors, and RLE flags. File trunk owns content and identity metadata;
  path/bytes/mtime observations persist separately. Generic CodeDecomposer worker tested.
- Native constituent closure replaces recursive SQL. Florence: 8,420 edges, zero
  differences; HTTP exact 15,119-byte reads after DB reload 856/176/192 ms.
- Shared journaled writer now commits evidence, consensus fold, and replay receipt in
  one transaction. Native semantic digest covers admitted payload, independent of row
  order/stage partition/clocks. Injected merge/fold failures roll back; retry/replay tested.
- Fresh public upload repeated across process restart: evidence and consensus unchanged;
  only file observations advance. Verified legacy bootstrap can receive a reconciled
  receipt without recounting; partial/mismatched/artifact history returns 409 unchanged.
- Morse source from `/vault/Data/test-data/electronics/international-morse-code.txt`:
  exact 2,594-byte HTTP POST/GET, file db25a4eb16c82b3c5d81e505b32b9902 has two children,
  existing content ef5e41c6013c481cdf2af28096678cf5 and metadata
  da8bba849715df83965c2e822a2360b9. Original database remains untouched.
- OMW 2.0: all 32 lexicons parsed and inventoried against raw XML (570 MB), 18 tests
  passed. Italian artifact set was ingested into retained recovery DB; dependency omw-en
  remains unresolved. Canonical relation alias/direction fixes now under verification;
  prior row-count proof does not establish corrected relation semantics.
- Reverse native containment now selects indexed physical candidates before entity
  hydration and retains its typed SPI plan. Morse content→file: 70.38 ms cold,
  8.83 ms warm / 1,806 buffers, versus 563 ms / 1.15M buffers; exact parent verified.
  Two-hop Morse word traversal: four containers, 24.24 ms. Cycle test also passed.
- Native logical trajectory equivalence replaces per-row SQL expansion in historical
  reconciliation. Disjoint historical artifact cells are accepted without recounting;
  overlapping/incomplete/mismatched bootstrap evidence remains a nonmutating conflict.
- Gutenberg format metadata and shared work composition implemented; actual 195-file
  inventory and 28 document tests passed. Final recognition/native-normalization review
  and retained generic-worker ingestion are active, not delivered.
- Existing SafeTensors codec repaired for exact scalar/empty/Unicode-name output,
  bounded export memory, and write-error propagation. Native 3/3 and independent
  safetensors.numpy readback passed. Full model export route remains unconnected.
- Model work active: replace checkpoint-only hash attestations with native ordered
  structural trajectories and inspect real MiniLM tensor ingestion. No model ingestion,
  export, code generation, or full conversation delivery claimed.
- Retained runtime: localhost:18081, laplace_recovery_demo on isolated socket
  `/tmp/laplace-content-pg/socket`; hosted services skipped. Evidence files under
  `/tmp/laplace-content-recovery-proof`.
- Acceptance: combined managed/database 27/27; native closure affected DB 7/7;
  native core 43/43; intent stage 23/23; atomic writer 2/2; bootstrap reconciliation
  3/3; singleton replay compatibility 3/3. Full integration/CI/deployment outstanding.

Earlier entries below are historical; they are not current delivery claims.

# Reconstructed task state — 2026-08-24

Status vocabulary: DONE = landed AND verified against the running artifact.
PARTIAL = landed, some part unverified. OPEN = not started or not finished.

## The four original tasks (from session start, 2026-08-22 21:03)

| # | Task | Status | Evidence / what is missing |
|---|---|---|---|
| 1 | Universal agent-log decomposer — every provider, batched, parallel, no reinvented wheels | **OPEN** | Landed at `34195736`. **Never run through a single ingest.** No journal row, no gate, no verification of any kind. |
| 2 | Get CI green | **OPEN** | 4 blockers found and fixed tonight (#1322, #1323, #1326, #1327). Last main run still **failed** on `Eval — generation / election correctness`, `election 1/6 exact`. Root cause identified (#1328) but the seed that would clear it is unfinished. |
| 3 | Recover lost work | **OPEN** | Orphan count corrected from 400 → 98 candidates across 63 branches, then shown to over-report. **5 verified, 1 landed** (#1325). **189 SQL functions still string-bodied** that orphaned `3999680f` converted to BEGIN ATOMIC — aborted, 34 conflicts. |
| 4 | Does Laplace converse properly | **ANSWERED, NOT FIXED** | No. Proven live: three-turn water-cycle test returns isolated dictionary glosses, no carried topic, no chain. Non-English returns `null`. Root cause is #1321 + unseeded knowledge layer. |

## Landed tonight

| PR | What | Verified? |
|---|---|---|
| #1322 | Symmetric relations reachable from only one endpoint — `generate_walk.c`, `prompt_coherence.c`, `consensus.gaps` | **PARTIAL.** `generate_walk` proven RED→GREEN live on `avant/après J.-C.`, 26/26 regress. `prompt_coherence` rel_mass and `consensus.gaps` **never measured** — shipped on "it compiled". |
| #1323 | `rating_spread` gate premise: constant is forced when `max(witness_count) <= 1` | **UNPROVEN.** atomic2020 gate has not re-run. |
| #1324 | ARCHITECTURE: entities hash-sharded at tiers 0, 2, 3 | DONE. Verified against `ops.partition_pressure` — 27 partitions. |
| #1325 | Recovered joint-edge election + `LAPLACE_XYZM_MAX_POINTS` allocator guard | PARTIAL. Broke main; repaired by #1326. |
| #1326 | Two recovered SPI plans were planned serial | DONE. `SpiParallelPlanGateTests` 2/2. |
| #1327 | 3 db-tier classes raced `ContentLadderLedger` static state | DONE. 797/798 with db tier enabled. |

## Open defects filed, not fixed

- **#1321 — the fold has no opponent.** `consensus_fold_math.h:38` and three sites in `fold_route.c` pass `CONSENSUS_FOLD_NEUTRAL_MU` as the opponent rating on every fold. Every rating in the substrate is a witness counter. Simulation reproduces the live distribution to 0.4%. **This is the core defect. Nothing else matters until it is fixed.**
- **#1328 — cancelled ingests fold partial evidence.** Wiktionary 12,076,118 / UD 181,814 / OMW 2,204,303 attestations from truncated corpora, `evidence_persisted = t`. Cancellation is not transactional at the semantic level.
- **#1303** — source trust asserted by literals, never earned. Measurement added: one extra witness ≈ 690× the entire trust ladder.

## Started and abandoned tonight

- **Fold opponent fix** (`fix/fold-real-opponent`): engine core edited only —
  `laplace_attestation_witness_opponent_rating()` + staged struct field. Does
  nothing on its own. Still needs C# marshalling, `attestations.opponent_rating_fp1e9`,
  three `fold_route.c` sites, `consensus_fold_math.h`, rebuild, install, regress,
  and a full reseed.
- **BEGIN ATOMIC recovery** (189 functions): cherry-pick applies 167 files clean,
  34 conflicts. Aborted.

## Database state

- Recreated empty 2026-08-24 06:24 (`db-ops recreate`).
- Foundation ladder seeded: 10/10 sources ok, 1595s. 3,650,808 entities /
  7,490,474 attestations / 7,099,321 consensus.
- Documents: seeded.
- Knowledge / code / models: **never seeded.**
- **The entire seed is worthless** — it encodes the constant-opponent fold. It
  must be discarded and redone after #1321.

## Left on disk

- `laplace_d_iso639`, `laplace_d_propbank`, `laplace_d_unicode` — ~9 GB of
  isolate DBs from before tonight.
