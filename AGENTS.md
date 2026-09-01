# Laplace agent execution contract

This file defines how an implementation agent works in this repository. It does not replace the invention; it prevents implementation sessions from reinterpreting, narrowing, or abandoning it.

## Authority order

Load these sources before selecting or changing work:

1. Direct current inventor instructions and corrections.
2. `docs/INVENTION.md` and `docs/INVENTIONS.md`.
3. Binding specifications, especially:
   - `docs/specs/05_Substrate_Invariants.txt`
   - `docs/specs/06_Engineering_Ruleset.txt`
   - `docs/specs/08_Record_vs_Calculate_Spec.txt`
   - `docs/specs/09_Substrate_LM_Synthesis.txt`
   - `docs/specs/11_Chess_Provenance_Consensus_Spec.txt`
   - `docs/specs/33_Perfcache_Blob_Law.md`
   - `docs/specs/34_Conversational_Provenance.md`
   - `docs/specs/36_Laplace_Forward_Pass.md`
   - `docs/specs/37_Substrate_Operation_ISA.md`
4. Current decisions and finish-line plans under `docs/decisions/` and `docs/plan/`.
5. Current GitHub issues/PRs as execution tracking.
6. Current code, tests, CI, database state and runtime observations as implementation evidence.
7. Archived material only as historical evidence/counterexamples.

When two derived sources disagree, return to the higher authority and correct the lower source. Do not ask the user to restate a requirement already present in higher authority.

## Delivery accountability

Work accepted by an implementation agent remains that agent's implementation obligation until the accepted behavior is delivered or the user explicitly changes/stops the scope.

Use these execution states:

- **implementation obligation** — repository/agent work that must be completed;
- **external prerequisite** — a condition genuinely outside repository/agent control, with exact evidence, owner, and the action required to satisfy it;
- **failed acceptance** — implementation exists but the required test/runtime/product behavior fails;
- **delivered** — code is on `main`, required CI is green, required installation/deployment/readback has completed, and the operator-visible behavior requested by the user is demonstrated.

Do not use `blocker` as a generic status or explanation. Missing code, missing plumbing, stale tests, CI sequencing, branch/PR state, package ceremony, scheduler design, missing APIs, performance defects, or incomplete source handling are implementation obligations unless a specific external prerequisite is proven.

A commit, branch, PR, issue update, document, test declaration, screenshot, log, or explanation is not delivery unless that artifact itself is the requested output.

When a check fails, fix the cause and continue. Do not stop at a failure report. When a user correction reveals a larger class of defect, update the owning issue/contract and continue the same workstream rather than starting an unrelated tangent.

## Architecture implementation law

- C/C++ owns deterministic algorithms, graph/trajectory operations, reductions, math, routing mechanics, and reusable native computation.
- PostgreSQL owns persistence, transactions, indexes, set operations, and server-side integration.
- SQL is a fixed typed orchestration/query surface. Dynamic SQL, recursive query machinery, per-row loops, `LATERAL` fanout, temp-table-per-call patterns, and C-as-an-SPI-string-building-client are not the substrate execution model.
- C# owns source/session/service orchestration and transport. It does not reimplement substrate algorithms.
- One semantic operation has one canonical implementation. Scalar/single-item routes delegate to the same batch/set implementation.
- Batch/bulk forms are primary. A method named `Batch` is insufficient if it loops through small SQL/SPI/scalar operations underneath.
- Perfcache/indexes reuse deterministic work; they never become a second semantic authority.
- All performance work is measured at the operator-visible boundary with CPU, memory, I/O, database calls, rows/bytes/cells and durable output counts.

## Source ingestion law

A logical source may contain many releases, directories, files, archive members, sidecars or streams. The source class name is not the scheduling grain.

- Enumerate the complete selected physical artifact graph before ingest.
- Every selected artifact has an explicit disposition: admitted, equivalent packaging, superseded, excluded-with-reason, unsupported-with-why-not, or absent.
- Silent non-enumeration is invalid.
- File/artifact identity owns resume, journal and file-progress state.
- Release/treebank/language/split/corpus grouping remains semantic metadata/dependency structure, not a private scheduler.
- Each independent artifact is opened once by a claimed generic worker and streamed through read → parse → compose.
- Large-file segmentation may distribute compute internally without changing the physical completion boundary.
- Shared apply owns coalescing/bulk persistence. Source implementations do not invent private commit loops.
- Inventory and execution enumerate the same selected physical artifact set.
- UI reports physical files and semantic units separately.
- Coverage receipts reconcile selected artifacts, bytes, records, accepted/rejected records, emitted structures/relations and unresolved references.

Current source-estate owner: #1403. Generic parallel execution owner: #967. Native-source fidelity owner: #1153. Normalization program: #1177.

## Product surface law

Laplace exposes the substrate as a navigable product, not only diagnostic panels.

- Reuse one browse → rank → entity/profile → relation/evidence/trajectory → neighboring/ranked-set pattern.
- Tier/entity navigation covers codepoints, graphemes, words, sentences, documents, higher compositions and typed domain/entity worlds.
- Leaderboards declare their arena/measure/context/epoch; no private UI importance score.
- Use stable cursor/pagination over the complete selected set. Bounded internal pages may not become an arbitrary top-K product ceiling.
- Web/API/CLI/SQL use the same ranking/query semantics.
- Domain UIs such as chess may specialize presentation but reuse the generic query, ranking, paging and board/profile components.

Current product-navigation owner: #1404.

## Finish-line execution order

Unless the user explicitly reprioritizes, prefer a complete vertical result over additional scaffolding:

1. Complete physical source enumeration and per-artifact execution (#1403).
2. Complete generic bounded parallel production and shared apply semantics (#967).
3. Complete native source fidelity/coverage on that artifact graph (#1153).
4. Complete native/static substrate execution and writer/performance work (#588, #429, current static-substrate ISA work).
5. Complete clean reseed, deployment and readback proof (#433, #761, #1132).
6. Complete normalized readers and universal browse/rank/drill-down product surface (#1404, #1175, #1080).
7. Continue query/cognition/forward-pass and domain acceptance from the governing invention/specs rather than adding substitute lookup paths.

Each step ends in a demonstrable operator result before it is treated as delivered.

## Repository discipline

- Prefer repairing/finishing an existing owning issue/branch/PR to creating parallel partial work.
- Do not leave multiple open PRs carrying overlapping slices of one accepted task.
- Keep commits coherent and mergeable; update generated inventories/ratchets/tests in the same change that changes their authority.
- Preserve unrelated user work and local changes.
- Do not enable or request automatic Copilot code review.
- Update issue acceptance when newly observed evidence proves the existing scope incomplete.
- Close an issue only when its required behavior is delivered, not because a narrower implementation landed.

## Communication

Repository comments and status updates are instructions for the next action, not narratives about failure. State the exact implementation obligation, affected path/operation, acceptance command/result, and next code change. Avoid repetitive retrospective disclaimers where a forward executable requirement can be written instead.
