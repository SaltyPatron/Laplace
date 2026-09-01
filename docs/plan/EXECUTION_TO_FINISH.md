# Laplace execution to finish

This is the forward execution plan for the current repository. It turns the invention, specifications, audits and issue ledgers into one ordered implementation path. It is an execution projection, not a replacement for `docs/INVENTION.md` or the binding specifications.

## Authority

Implementation decisions must trace to:

1. current inventor instructions/corrections;
2. `docs/INVENTION.md` and `docs/INVENTIONS.md`;
3. `docs/specs/05_Substrate_Invariants.txt`;
4. `docs/specs/06_Engineering_Ruleset.txt`;
5. `docs/specs/08_Record_vs_Calculate_Spec.txt`;
6. `docs/specs/09_Substrate_LM_Synthesis.txt`;
7. `docs/specs/11_Chess_Provenance_Consensus_Spec.txt`;
8. `docs/specs/33_Perfcache_Blob_Law.md`;
9. `docs/specs/34_Conversational_Provenance.md`;
10. `docs/specs/36_Laplace_Forward_Pass.md`;
11. `docs/specs/37_Substrate_Operation_ISA.md`;
12. current decision records and the decomposer/substrate issue ledgers;
13. GitHub issues/PRs as execution ownership;
14. current code/runtime/CI as implementation evidence.

When a lower-level artifact contradicts a higher authority, correct the lower artifact.

## Delivery state

Every accepted work item is one of:

- **implementation obligation** — code/product work the agent must finish;
- **external prerequisite** — a proven condition outside repository/agent control with evidence, owner and satisfaction action;
- **failed acceptance** — the implementation exists but the required executable behavior fails;
- **delivered** — authoritative `main`, required CI, required deployment/readback and the requested operator-visible result all agree.

The next action for an implementation obligation or failed acceptance is code/test/deploy work, not another failure narrative.

## Finish line 1 — complete source estates and generic ingest

Owners: #1403, #967, #1153, parent #1177.

### Required result

Every selected source release exposes the complete physical artifact graph and every independent artifact enters one generic bounded read → parse → compose → bulk-apply pipeline.

### Source acceptance set

At minimum audit and execute the complete selected physical inputs for:

- Unicode/UCD/UCA: UCD XML, DUCET and all selected UCD property/auxiliary files;
- ISO registries;
- CILI;
- WordNet: all selected `dict` data/index/sense/exception/sentence/frame/support files;
- VerbNet: explicitly select and account for 3.4, `verbnet-test`, `vn-gl` and any other observed release trees rather than hard-wiring one directory;
- FrameNet: frame, LU, fulltext and selected supporting files;
- UD Treebanks: every selected `.conllu` file across treebank directories;
- Atomic2020: train/dev/test and any selected release sidecars;
- OMW, PropBank, SemLink, MapNet, WordFrameNet/XWFN, Predicate Matrix and other foundation sources;
- single-file controls such as ConceptNet/Wiktionary where applicable.

### Required implementation

- One source-estate enumerator/profile contract.
- Inventory set equals execution set after explicit dispositions/filtering.
- File/artifact identity owns resume/journal/file progress.
- Semantic unit counts remain separate from file counts.
- Generic scheduler owns concurrency/backpressure/memory.
- Source adapters only enumerate, parse and compose source-specific structure.
- Shared apply owns database transaction/coalescing behavior.
- Coverage receipt accounts for selected files, bytes, records, accepted/rejected records, fields/relations emitted and unresolved references.

### Acceptance

Run complete-source fixtures plus real selected estate inventory; hidden-file and alternate-directory mutants must fail. A clean run must show the same physical file set in inventory, journal, execution telemetry and final receipt.

## Finish line 2 — native/static substrate execution

Owners: #588, #429 and the static-substrate ISA work merged from #1394.

### Required result

C/C++ owns reusable deterministic computation; PostgreSQL owns persistence/set operations; SQL and C# orchestrate fixed typed operations.

### Remove the class of defect

- dynamic SQL/SPI string-building as an algorithm;
- recursive/CTE/LATERAL graph execution where a native bulk operation owns the semantics;
- loops around `SPI_execute*` or scalar SQL calls;
- per-call temp tables/indexes;
- duplicated SQL and native implementations of one semantic fact;
- batch APIs that decompose into repeated small database calls;
- nested PostgreSQL parallelism underneath caller-owned parallelism unless explicitly planned by one resource authority.

### Acceptance

ISA/source gates reject regression of retired execution shapes. Hot operations expose measured batch width, calls, rows/cells, CPU, WAL/I/O and wall time. Single-item parity delegates to the same batch implementation.

## Finish line 3 — deterministic evidence, time and standing

Owners include #1395, #1397 and the governing chess/evidence specifications.

### Required result

Historical evidence carries source-observed event time and order-sensitive standing/replay consumes oldest → newest event order. Batch boundaries may not change rating history.

### Required implementation

- Source event timestamp enters the canonical attestation/evidence boundary.
- Ingest wall-clock remains ingestion/provenance metadata, not historical event time.
- Glicko periods are explicit event/time periods, not dictionary groups keyed by opponent state or arbitrary commit batches.
- Fixed-point/native math fails closed on impossible states and preserves rating/RD/volatility invariants.
- Repair/refold paths use the same chronological implementation as live admission.

### Acceptance

Known game histories reproduce stable ratings independent of file/batch/thread partitioning. Reverse-order and batch-size mutants must fail. No consensus row can reach invalid volatility/rating state.

## Finish line 4 — clean reseed, deployment and readback

Owners: #433, #761, #1132 plus source/substrate owners above.

### Required result

A clean database ingests the configured foundation through the production path, publishes truthful progress, completes maintenance/fold work, deploys the exact code tested, and serves read paths against that state.

### Acceptance receipt

Record:

- exact commit/package/loaded native object;
- selected source artifacts and counts;
- per-source elapsed time and throughput;
- read/parse/compose/apply/fold/drain/maintenance timing;
- database calls, rows/bytes/cells, WAL and I/O;
- final entity/physicality/attestation/consensus counts;
- resume/restart proof;
- representative reader results.

Do not tune around a source until the generic execution measurements identify its actual dominant operation.

## Finish line 5 — reusable product navigation and readers

Owners: #1404, #1175, #1080 plus normalized reader work.

### Required result

The product exposes the substrate as a navigable entity world rather than isolated diagnostic top-K panels.

### Required surface

- tier/altitude navigation: codepoints → graphemes → words → sentences → documents → higher compositions;
- typed entity/relation/domain navigation;
- sortable/filterable/paginated leaderboards over declared arenas/measures;
- stable epoch/cursor URL state;
- rank → profile → evidence/relations/trajectory → neighboring/ranked-set drill-down;
- common query/ranking/paging semantics across SQL/API/CLI/web;
- reusable components for board/profile/table/navigation/domain views.

Representative proof: Unicode, lexical words, sentence/composition, relation/entity type and chess/player-style ranking.

## Finish line 6 — query, conversation and forward pass

Authority: `docs/specs/34_Conversational_Provenance.md`, `36_Laplace_Forward_Pass.md`, `37_Substrate_Operation_ISA.md`, and `plan/REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md`.

### Required result

Queries execute real typed forward/cognition programs over admitted structure/evidence/geometry/standing/context rather than lookup/hop/top-k substitutes.

### Required implementation

- UAX29/grammar decomposition feeds exact composition/trajectory state.
- Query-relative typed connection/search programs use declared relation/time/dependence/resource semantics.
- Perfcache/indexed operations accelerate deterministic calculations.
- Observation planes and seeded semantic fact planes remain distinct and combine at execution time.
- Generated Q/K/V/O or equivalent operator programs derive from the active witnessed/calculated planes rather than a permanent flattened embedding.
- Conversation retains prompt/session/source/provenance occurrence state and writes new observations through the same substrate laws.

### Acceptance

Whole-route prompts/questions must demonstrate structure-sensitive continuation/reasoning that changes when relevant witnessed state changes, survives restart, and can explain the supporting path/receipt without falling back to hard-coded domain routes.

## Finish line 7 — domain product acceptance

Chess is a representative downstream product, not a private engine.

Required chess result includes:

- exact player identity handling with witnessed aliases rather than destructive folding;
- oldest→newest game/event admission;
- correct ratings/standings;
- performant substrate-aware search/perfcache use;
- Lab evaluation isolated from recording/write runtime unless recording is explicitly requested;
- reusable full board/replay/profile components;
- player/game leaderboards and drill-down through generic product surfaces;
- Lichess production defaults that meet interactive latency while deeper search uses substrate acceleration rather than only raising conventional alpha-beta depth.

Use the same acceptance discipline for future domains.

## Agent course

For every work session:

1. load `AGENTS.md` and this plan;
2. identify the earliest unfinished finish-line obligation affected by the user's request;
3. inspect the owning issue/current `main`/runtime before changing code;
4. implement the smallest coherent slice that ends in operator-visible behavior;
5. update tests/contracts/generated artifacts in the same change;
6. merge to `main` when acceptance passes;
7. deploy/read back where the behavior requires it;
8. update the owning issue with the demonstrated result and the next executable obligation;
9. continue until the accepted scope is delivered or the user changes/stops it.

Do not replace implementation with additional plans, PR accumulation, status prose or new local exceptions.
