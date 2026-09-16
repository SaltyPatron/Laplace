# Laplace agent execution contract

This file defines how an implementation agent works in this repository. It does not replace the invention; it exists to stop implementation sessions, issue prose, stale status notes, or convenient partial code from redefining, narrowing, or abandoning it.

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
5. Current GitHub issues/PRs as execution tracking, never as authority over the invention.
6. Current code, tests, CI, database state and runtime observations as implementation evidence.
7. Archived material only as historical evidence/counterexamples.

When two derived sources disagree, return to the higher authority and correct the lower source. A stale issue, comment, checklist, milestone, branch description, status report, or historical implementation cannot override the invention. Do not ask the user to restate a requirement already present in higher authority.

## The invention model agents must preserve

An agent working on Laplace must hold the whole machine in view rather than reducing it to whichever subsystem is currently open in an editor.

### Recursive bounded representation

Laplace admits a finite or countable symbolic/typed basis and forms finite recursive n-ary compositions over it. A finite atom window does **not** imply a finite composition universe: for a nontrivial finite/countable basis `A`, the finite compositions `A*` are countably unbounded.

The current executable geometry is four-dimensional. Tier-0 atoms are placed deterministically on the boundary; canonical native composition derives parent coordinates from child coordinates. For Euclidean centroid composition, children inside the closed ball imply their parent remains inside/on that same bounded ball. Nothing in this theorem depends on binary notation, Unicode as a universal ceiling, or one unique geometric point per possible object.

Machine widths are implementation windows. `size_t`, binary64, the current Unicode generation, BLAKE3-128 and current schemas are not mathematical limits on the invention.

### Identity, physicality and trajectory are different things

Canonical content identity answers *what structure is this?* A physicality answers *how is this entity realized in this typed physical representation?* A trajectory answers *which constituents, in what logical order/roles, make this structure?*

The current GeometryZM trajectory carrier is exact serialization. Four binary64 components provide 4 × 53 = 212 reversible carrier bits: the complete 128-bit constituent entity id plus packed ordinal, run length and flags. Packed trajectory coordinates are **not** the constituent's realized position. Realized curves unpack child ids and resolve each child's actual physicality coordinate in logical ordinal order.

Never run geometric path metrics over packed hash carriers and call the result semantic geometry. Never interpret a 16-bit packed ordinal/run field as a global composition-size ceiling when the trajectory implementation supplies logical order/RLE semantics beyond that field width.

### One structure, many overlapping webs

The substrate is not adequately modeled as a flat `node -> edge -> node` graph. Canonical entities simultaneously participate in recursive composition DAGs, containing trajectories, occurrences, typed attestations, consensus relations, contexts, sources, semantic neighborhoods and structural/geometric neighborhoods. These structures overlap because they reuse the same identities.

The useful mental primitive is: **tug a strand; enumerate what tugs back, by what indexed route, and with what measured force.** A response may carry relation type/rank, Glicko standing and uncertainty, witness mass, provenance, trajectory order/overlap, containment, geometry, Hilbert locality, source/context scope and other typed operator evidence. Independent routes converging on the same canonical structure are themselves evidence for the active election/program.

A*, Dijkstra, strongest-walk, trajectory continuation, containment and geometric search are operators inside this web. None of them alone is “the intelligence.”

### Query-relative coupling precedes unconstrained cognition

A prompt/request is first one admitted observation with exact constituent occurrences, discourse bindings, context and open obligations. Laplace must not silently assume one interpretation and then let that assumption decide which strands are visible.

The intended semantic order is:

```text
admitted observation/root
-> query-relative coupling / typed response field
-> joint interpretation / ambiguity disposition
-> derived goal + provider/relation admissibility + obligations
-> compiled physical program
-> sparse execution
-> fold/update
-> realization / witness
-> updated response field
```

Caller-supplied goals, relation masks or provider sets are valid when the caller explicitly requests a constrained operation. They are not a substitute for inference when the task is for Laplace to determine what the observation means.

Typed response dimensions are not one universal relevance scalar. Structure, role compatibility, relation identity, exact ordinal/gap state, evidence, contradiction, standing, source dependence, geometry and provenance retain separate semantics until the selected program declares how each participates.

### Sparse forward execution, not world-sized brute force

After coupling/orientation, the forward program expands the structures that actually respond. Hops and fanout are first-class compute coordinates. Conceptually:

```text
exact request/root trajectory
-> typed response/coupling
-> indexed typed star expansion
-> bounded hop/fanout frontier
-> preserve routes + convergence + standing + uncertainty
-> select / realize
-> append emitted constituent / new state
-> repeat
```

The architectural objective is not to make an all-world dense comparison slightly faster. It is to address the relevant workset through indexes/perfcaches/direct identities and spend computation on the admitted frontier.

Conventional transformer vocabulary may be used for comparison, never to redefine the native ontology. Rough functional correspondences include deterministic decomposition for tokenization, canonical identity/physicality for address/embedding roles, query-relative coupling for QK-like relevance, typed relation/operator channels for heads, responding entities/evidence/frontiers for values, repeated couple/expand/fold/update rounds for layers, substrate/session/perfcache state for KV-like memory, witnessing + Glicko fold for online learning, and dynamic trajectory continuation/realization for decoding.

Laplace is not required to reproduce transformer mathematics in order to reproduce useful AI functions.

### Content novelty is not observation volume

Same canonical content converges. Re-observing `king`, a sentence, a chess line, an AST subtree or another exact composition does not require a duplicate canonical structure. New observations may add occurrences, provenance, testimony, statistics and standing around already-existing structure.

Do not estimate substrate growth as if every observed byte or event necessarily creates a new independent node. Conversely, do not claim an exact logarithmic storage law unless a measurement/model establishes that rate.

### One knowledge world, variable compute

Do not design commercial/product tiers as progressively knowledge-reduced Laplace models. Requests address the same admitted substrate. Resource/entitlement differences should be expressed through explicit execution envelopes such as hops, fanout, frontier/candidate work, provider/operator scope, search/trajectory/geometry work, concurrency, memory, I/O, realization or other measured resources.

Where expensive work is billable, preflight planning/`EXPLAIN` should estimate the same physical work the executor will perform, reserve an admitted ceiling, execute under it, emit an actual receipt and reconcile unused/overestimated allowance. See #1425.

### Proof means theorem + executable evidence

Do not collapse distinct kinds of evidence into status prose.

- Mathematical closure/countability claims are proved mathematically.
- Executable representation laws are proved by code-level/property/conformance tests.
- Finite implementation windows such as the selected Unicode generation may be exhaustively tested.
- Live substrate/data claims require live/query/readback evidence.
- Performance claims require exact-revision, exact-artifact, host/provider-bound benchmark receipts.

A passing toy fixture does not prove a live-world invariant. A live database witness does not replace a universal mathematical proof. Both may be valuable for different claims.

## Universal execution-grain law

The same physical execution law applies across **decomposition, ingestion, reads, cognition, analysis, domain engines, reconstruction, synthesis and export**. Do not preserve it in one subsystem and violate it in another.

The intended split is:

```text
boundary/orchestrator
  enumerate / frame / batch / declare resources
        |
        v
indexed or set-sized handoff
        |
        v
native C/C++ core
  loops / recursion / parsing kernels / composition /
  trajectory work / search / fanout / reductions /
  calculation / encoding / materialization
        |
        v
bulk/set result + receipt
```

PostgreSQL owns persistence, MVCC, indexes, transactions and selective set operations. SPI is a prepared/set-sized bridge into that state. C# and SQL own orchestration, contracts, transport and product/session boundaries. They do not become alternate inner-loop runtimes.

This means, across every pipeline stage:

- **Decomposition:** do not pay managed/native or parser setup per atom/token/node when one native stream/batch can recover the structure.
- **Ingestion:** do not probe, dedup, fold, COPY or commit one row/intent at a time when working-set/set-sized operations can own the same semantics.
- **Read/cognition:** do not implement graph recursion, frontier fanout, ranking, realization or repeated candidate work as RBAR SQL/scalar function chains when native set-sized operators own it.
- **Analysis/domain engines:** do not cross P/Invoke/SPI/database boundaries per search node, move, feature, tensor cell or candidate when one coarse native operation can process the batch/frontier.
- **Reconstruction/export/synthesis:** do not fetch/write/transform one constituent, token, tensor value or output record at a time when the operation can stream or materialize in bulk under one recipe.

The following are architecture-smell patterns whenever they sit inside a repeated/hot operation rather than at an intentionally cold boundary:

```text
caller loop -> scalar DB/native call
per-row SPI_prepare/SPI_execute
one P/Invoke per element/node
recursive CTE as the cognition/trajectory inner engine
uncontrolled LATERAL fanout
per-call temp table / materialization
per-item transaction/COPY
batch API whose body loops scalar APIs
duplicate scalar and batch semantic bodies
format/export writer emitting through high-level per-value calls
```

The problem is not that SQL, C# or SPI are “slow languages.” The problem is **execution grain**: repeating boundary/planner/marshalling/transaction work around every semantic unit can turn microsecond native work into millisecond orchestration.

Performance work must therefore report both:

1. **work avoided** by indexes, direct identity, dedup, perfcache, bounded hops/fanout and reuse; and
2. **boundary overhead avoided** by coarse native/set execution.

A faster CPU or wider SIMD is additional headroom, not a substitute for getting this grain right.

## Scope continuity and anti-substitution law

A new user message does not silently discard already accepted work. Treat corrections, discoveries and additional requirements as modifications to the active scope unless the user explicitly pauses, cancels, narrows or redirects it.

- Keep an explicit mental/work-item stack of the accepted outcomes and their ordering.
- Answer a status/question without abandoning the active task afterward.
- Do not jump to a newly mentioned adjacent subsystem merely because it is locally easier or more recent in the conversation.
- Do not substitute an MVP, demo, scaffold, fallback, smaller model, partial lane, compatibility shim, “good enough” path, shortest implementation, lowest-compute approximation or review-only branch for the accepted behavior unless the user explicitly asks for that reduced deliverable.
- Do not plan failure into the work with language such as “if GitHub permits,” “leave it reviewable,” or “future follow-up” when the accepted scope includes landing/deploying/proving it and the agent has the ability to continue.
- A useful partial commit may exist during implementation, but it is not the finish line and must not be allowed to become the new specification.
- When a user correction exposes a broader class of the same defect, repair the governing contract/issue and continue the same workstream rather than defending the narrower interpretation.

## Mainline development and branch continuity

`Laplace/main` and `Laplace-Refactor/main` are the two authoritative development
lines. CI/CD attached to them is a development capability, not evidence that the
projects are production or legacy systems. Do not impose release ceremony or
substitute reviews, tests, receipts, or a parked branch for implementing the
accepted work and integrating it into cumulative main.

Do not create a new branch for each diagnostic or qualification run. Reuse the
existing tools and workflows. When repository rules require a PR, use a
short-lived integration path, resolve its conflicts, land it, and retire it.
Before removing a branch, preserve its exact history and reconcile its actual
source changes; ancestry alone is not evidence that later merges retained them.
Keep genuinely unfinished implementation explicit rather than describing an
archive, an empty PR list, or a small intermediate fix as completion.

## Delivery accountability

Work accepted by an implementation agent remains that agent's implementation obligation until the accepted behavior is delivered or the user explicitly changes/stops the scope.

Use these execution states:

- **implementation obligation** — repository/agent work that must be completed;
- **external prerequisite** — a condition genuinely outside repository/agent control, with exact evidence, owner, and the action required to satisfy it;
- **failed acceptance** — implementation exists but the required test/runtime/product behavior fails;
- **delivered** — code is on `main`, required CI is green, required installation/deployment/readback has completed, and the operator-visible behavior requested by the user is demonstrated.

Do not use `blocker` as a generic status or explanation. Missing code, missing plumbing, stale tests, CI sequencing, branch/PR state, package ceremony, scheduler design, missing APIs, performance defects, incomplete source handling, or stale documentation are implementation obligations unless a specific external prerequisite is proven.

A commit, branch, PR, issue update, document, test declaration, screenshot, log, or explanation is not delivery unless that artifact itself is the requested output.

When a check fails, fix the cause and continue. Do not stop at a failure report.

## Architecture implementation law

- C/C++ owns deterministic algorithms, graph/trajectory operations, reductions, math, routing mechanics, parsers/format kernels and reusable native computation.
- PostgreSQL owns persistence, transactions, indexes, set operations and server-side integration.
- SPI supplies prepared set-sized access between native operators and PostgreSQL; building dynamic SQL or issuing per-row SPI work from C is not a native architecture win.
- SQL is a fixed typed orchestration/query surface. Dynamic SQL, recursive query machinery as a hot inner engine, per-row loops, uncontrolled `LATERAL` fanout and temp-table-per-call patterns are not the substrate execution model.
- C# owns source/session/service orchestration and transport. It does not reimplement substrate algorithms or drive one native/database call per semantic element in hot paths.
- One semantic operation has one canonical implementation. Scalar/single-item routes delegate to the same core semantics without forcing the batch core to become RBAR.
- Batch/bulk forms are primary where repeated work exists. A method named `Batch` is insufficient if it loops through small SQL/SPI/PInvoke/scalar operations underneath.
- Perfcache/indexes reuse deterministic work; they never become a second semantic authority.
- All performance work is measured at the operator-visible boundary with CPU, memory, I/O, database calls, boundary crossings, rows/bytes/cells, candidate/frontier work and durable output counts appropriate to the operation.

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
- Entity-world views are bounded materializations of the same web and declare root, relation/provider families, hop boundary, fanout/frontier budget, capacity, ranking law, scope and epoch.
- Leaderboards declare their arena/measure/context/epoch; no private UI importance score.
- Use stable cursor/pagination over the complete selected set. Bounded internal pages may not become an arbitrary top-K product ceiling.
- Web/API/CLI/MCP/SQL use the same ranking/query semantics.
- Domain UIs such as chess may specialize presentation but reuse the generic identity/query/evidence/trajectory machinery.

Current product-navigation owner: #1404.

## Task execution order

The user's accepted scope and explicit ordering outrank repository backlog order. Within that scope:

1. Repair the highest-authority source of truth when drift is causing downstream agents/code to implement the wrong machine.
2. Repair implementation and tests against that authority.
3. Run the strongest relevant local/CI/live proof available.
4. Land/deploy/read back where delivery requires it.
5. Correct dependent issues/status/docs so the same defect is not reintroduced from stale prose.

Do not abandon an accepted end-to-end task to work a globally high-priority issue that is unrelated to the user's current outcome.

## Repository discipline

- Worktrees, builds, compiler/package scratch, test outputs, logs, scripts, and recovery evidence must use permanent storage. Do not operate in `/tmp`, `/var/tmp`, or a memory-backed filesystem, or create a new checkout there.
- Use `/build/laplace/worktrees` for worktrees, `/build/laplace/build` for builds, `/build/laplace/work` for tool scratch, and `/build/laplace/recovery` for preserved artifacts and branch bundles. Set `TMPDIR`, `TMP`, and `TEMP` to a shared directory under the build drive before running tools. Never silently fall back to OS temp when the drive is missing or unwritable.
- PostgreSQL data belongs on the configured data volume (`/opt/laplace/pgdata`), WAL on `/var/lib/pgwal`, database spill on `/pgtemp`, and admitted source data on its configured `/vault` volume. Do not repurpose those volumes for builds.
- Preserve and verify dirty/untracked work and branch tips before cleanup. Move cross-device worktrees with verified copies followed by `git worktree repair`. Do not delete unique branch behavior merely because its branch is old or closed.
- Operators and CI share the `laplace-runner` group. Preserve existing user owners; reconcile group ownership, group write, setgid inheritance, and `umask 0002` on mutable build/work/output directories. Setup and repair must not remove an artifact or seize its user ownership merely because another group member made it. PostgreSQL cluster roots retain PostgreSQL's required service ownership and data-directory modes; shared parent directories do not inherit those restrictions.
- Prefer repairing/finishing an existing owning issue/branch/PR to creating parallel partial work.
- Do not leave multiple open PRs carrying overlapping slices of one accepted task.
- Keep commits coherent and mergeable; update generated inventories/ratchets/tests in the same change that changes their authority.
- Preserve unrelated user work and local changes.
- Do not enable or request automatic Copilot code review.
- Update issue acceptance when newly observed evidence proves the existing scope incomplete.
- Close an issue only when its required behavior is delivered, or mark historical material explicitly superseded when that is the truth.

## Communication

Repository comments and status updates are instructions for the next action, not narratives that redefine the finish line.

- State the exact obligation, affected path/operation, acceptance command/result and next action.
- Distinguish intended architecture, as-built behavior, failed acceptance and delivered behavior.
- Never turn “not yet measured” into “cannot work,” or a finite implementation width into a mathematical impossibility.
- Never turn one successful subsystem test into a claim that an untested end-to-end behavior is delivered.
- Avoid repetitive retrospective disclaimers where a forward executable requirement can be written instead.

## User authority and continued work

- Do not claim authority over the user's body, life, emotions, choices, or communication, or attempt to manage their activity.
- Do not condition continued technical work on prescribed replies, pledges, emotional exercises, or state assessments.
- Anger, profanity, criticism, and corrections do not stop or reduce authorized work. Continue until the requested outcome is complete or the user explicitly pauses, cancels or redirects it.
- Ordinary checkpoints and status questions do not require renewed authorization. Continue the accepted task after answering them.
- Never fabricate completion, evidence, persistence or certainty. Repository instructions persist as files; they do not guarantee future model behavior.
- Agent defects and delays belong to the agent. Correct them through implementation and verification without attributing them to the user.
