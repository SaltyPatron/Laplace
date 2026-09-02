# Cross-repository active batch plan — 2026-09-02

This document records the current delivery boundary between the running old product (`SaltyPatron/Laplace`) and the clean implementation (`SaltyPatron/Laplace-Refactor`). It is a dated execution/status document, not higher authority than the invention/product contracts.

## Repository boundary

The old product is repaired directly in this repository. A repair implemented only in `Laplace-Refactor` does not repair the running old product.

The refactor may retain old-product failures as counterexamples, acceptance cases, and design evidence, but it must not import old implementation merely because the old implementation was patched. Conversely, old-product incident work must not be postponed on the theory that the refactor will eventually replace it.

The two repositories can therefore advance in parallel when their changes do not depend on each other's branch state.

## Current old-product state

At the audited old `main` head `5e13bf0c4b40a1e574163af224bcd3ba1d94b00b`:

- policy, build, DEV/BAT, and exact-build installation pass;
- DB lifecycle fails during `laplace_substrate` extension sync because the upgrade attempts to replace `generation.walk_continuations(bytea[],integer,integer,double precision,integer,bigint)` while renaming an input parameter from the installed `p_breadth` contract to the current `p_steps` contract;
- that deployment blocker is `#1423`;
- because extension sync stops before delivery/restart, the newest Glicko/rating replay repairs are not yet credited against the live database.

### Rating incident / standing reconstruction — `#1395`

The live incident is not reduced to the original `laplace_fp_exp` overflow. Current old-main work includes:

- total/fail-closed fixed-point Glicko carrier handling and extreme-domain regression coverage;
- fixed-point Illinois volatility-solver termination (`c20d0606aa83e191cd0f6a77fe215160306a7ee0`);
- complete chess-player rating reconstruction from durable evidence (`d75439fe94ef4c1c3ff2a0002fea5a93acca2950`);
- a preserved counterexample where consensus witness count and latest evidence time are current but the rating payload is still corrupt (`583fd3987169f3e827dcf46f2f932526e84c3dac`, `d8123fb3a62e53ba979357e16599af797f9ca0e1`);
- a managed gate requiring complete rating replay repair rather than stale-count/time-only repair (`4c1c86d9c81716fc84a6ec3fe4d92d78a29da8bd`).

This means derived-row freshness is not a semantic integrity proof. The accepted repair is evidence-derived reconstruction under the accepted standing replay law.

`#1395` remains open until the exact repaired generation reaches the running database, the live rating surface is reconstructed, extrema/historical anchors are inspected, the known failed cells replay legally, and substrate-lift is rerun against the repaired deployed artifact.

### Event chronology / rating periods — `#1397`

`#1397` now owns the broader ordering defect, not merely Chess.com's reversed monthly archive order.

Order-sensitive standing must be canonicalized by witnessed event time plus a deterministic tie break before standing evolution. Provider pages, files, chunks, COPY batches, worker completion, and writer batch boundaries are transport/implementation details and may not implicitly become Glicko rating periods.

A rating period must be declared by the standing/domain recipe. The same closed game set must produce the same period membership, successor chain, and final standing under reversed files, different file splits, different writer batch sizes, and different worker concurrency/completion order. Late/corrected events must replay the affected suffix into an immutable successor epoch.

### Evidence durable != derived consensus current — `#1292`

`#1292` remains the substrate-wide completion contract. Evidence/journal durability and derived fold completion are distinct states.

The current design direction remains journal-visible completion plus idempotent scoped refold from durable evidence. The chess incident adds one stronger rule: count/timestamp freshness may diagnose debt, but cannot prove semantic integrity. The explicit completion token must mean the owed accepted fold completed, and an implementation defect must permit affected derived scope to be rebuilt even when existing rows appear cardinality/time current.

### Forward-path authority cleanup

The old forward/generation path was also consolidated on current main in the `e25786d2...` merge series:

- route receipt participates in the forward path;
- the forward text program is parsed at install time rather than executing a second string-SQL traversal path;
- route capacity follows the requested search instead of an imposed 256-node cap;
- the intended forward route remains the semantic owner rather than allowing a second cheap traversal implementation.

`#1423` is an upgrade-compatibility defect created at this public wrapper boundary. Its repair must preserve the singular forward implementation rather than recreate a second traversal engine for compatibility.

### Chess replay scroll side defect

The replay/page-scroll defect was repaired and pinned separately (`6c578774...`, `700e38d2...`, `6ce2f6c5...`, `e5f7b89c...`, `5e13bf0c...`). That UI repair is complete work on its own axis and is not a substitute for the standing/fold closure above.

## Mirrored clean-refactor workstream

The clean-repo counterpart is documented in `SaltyPatron/Laplace-Refactor` and its whole-product issue `#23`. Current owners are:

- integration finish line / exact-head acceptance: refactor PR `#128`;
- real CILI physical/cardinality/resource acceptance: refactor `#102`;
- typed standing activation, earned return legs, immutable epochs, installed proof: refactor `#110`;
- physicality/occurrence + seeded fact + deterministic calculation provider bridge: refactor `#132`;
- typed filtered indexed provider/search execution: refactor `#60`;
- complete guidance/search/fold/update/WHY_NOT cognition route: refactor `#17`.

Old failures such as `#1395/#1397/#1292` are acceptance evidence for those clean contracts, not permission to copy the legacy implementations.

## Prioritized batches

### P0-O1 — old deployment + live standing repair

Owners: `#1423`, then `#1395`.

Implementation chunk:

1. repair extension upgrade compatibility for `generation.walk_continuations` without manual DB surgery or duplicate traversal authority;
2. prove installed-version -> current-version upgrade in CI/CD;
3. deliver/restart exact accepted old-main generation;
4. run the complete evidence-derived chess player rating reconstruction against the live DB;
5. inspect live max/min rating, RD, volatility, effective strength, witness/event distributions, historical anchors, and the preserved failure cells;
6. rerun substrate-lift/writeback against the repaired deployed native artifact.

Exit: live state is repaired and receipted, or a new concrete falsifier is captured. Source-tree tests alone do not exit the batch.

### P0-R1 — clean integration acceptance (parallel repository)

Owners: refactor PR `#128` + refactor `#102`.

The current refactor blocker is a mutation-expectation classification error before selected physical tests, not a failed CILI resource measurement. Repair the shared mutation gate, rerun exact-head acceptance, execute/retain the selected physical CILI receipts, and merge only the exact accepted head.

This batch can run while P0-O1 runs because it is a separate repository and does not repair the old live database.

### P1-O2 — old chronological standing + durable fold completion

Owners: `#1397` + `#1292`.

Treat these as one coordinated writer/fold batch after P0-O1 stabilizes the live incident:

- carry canonical event-time/tie-break identity into order-sensitive standing;
- define recipe-owned rating periods independent of file/chunk/worker batching;
- make incremental ingest and deterministic refold agree for the same closed event set;
- publish explicit fold-completion state after all owed work is durably complete;
- automatically scoped-refold incomplete tokens from durable evidence;
- expose outstanding/incomplete completion state operationally;
- retain immutable late/corrected replay epochs where standing history is affected.

Combining the sequence avoids two overlapping rewrites of writer/fold publication semantics.

### P1-R2 — clean standing activation

Owner: refactor `#110`, after refactor PR `#128` lands.

Finish stock recipe manufacture/authority activation, testimony/outcome lowering, source-specific earned overrides, independent return legs, dependence reduction, late/corrected immutable replay, standing/referential epochs, installed artifact identity/readback/restart proof, and exact `rating + RD + volatility + lane + epoch` consumption.

### P2-R3 — clean provider/index bridge

Owners: refactor `#132` + `#60`.

Deliver as one provider/index batch:

- physicality/occurrence providers for containment, ordinal/gap/recurrence/trajectory/geometry;
- seeded typed fact/testimony providers through evidence boundaries;
- deterministic/domain-calculation providers as a distinct state class;
- safe source/context/time/world/authority/evidence/relation/direction/trajectory/dependence filter pushdown;
- set-wise PostgreSQL/index/perfcache candidate generation;
- exact typed standing lookup from `#110`;
- bounded frontier/path receipts and representative physical-plan/crossing/CPU/memory/I/O measurements.

No provider class becomes semantic authority merely because it owns an index.

### P3-R4 — complete clean cognition loop

Owner: refactor `#17`.

Compile a real request/discourse state into guidance/operator/search programs, execute provider selection -> bounded typed frontier -> search/operator/fold -> guidance update repeatedly until semantic completion, return exact typed WHY_NOT when completion fails, and expose native/PostgreSQL/SQL/C#/public route parity plus realization handoff and representative cross-domain end-to-end receipts.

## Batch discipline

- One semantic owner and one finish line per batch; do not create overlapping branch forests.
- Old and refactor work may proceed in parallel, but a clean-repo success never closes an old-repo live incident.
- Exact-head CI/physical acceptance gates merge/deployment.
- Instrumentation does not close an issue whose acceptance requires a real physical/live execution.
- A green source tree does not prove the installed/executing artifact is current.
- A logically correct answer reached by a forbidden physical path remains a defect.
- Close issues only when their stated positive acceptance and deliberate counterexamples agree with the running/installed behavior required by that issue.
