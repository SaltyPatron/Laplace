# Laplace execution / acceptance workstreams

This file is a compatibility/workstream index, not a permanent global execution order.

The previous revision arranged the repository into numbered “finish lines” and told agents to pick the earliest unfinished one. That was useful for one campaign, but it could override a later explicit user task and make a historical backlog sequence look like part of the invention. The detailed prior order remains available in Git history.

## Authority

Active work follows:

1. current inventor instruction/correction;
2. `docs/INVENTION.md` and `docs/INVENTIONS.md`;
3. binding specs and `AGENTS.md`;
4. current scoped decisions/plans;
5. current GitHub issues/PRs;
6. current code/tests/CI/live evidence.

A plan may organize work. It may not reduce the intended behavior to an MVP, fallback, cheapest path, smallest model, shortest search, one operator, or the first implementation that happens to pass a narrow test.

## Delivery states

Use the repository-wide states from `AGENTS.md`:

- **implementation obligation** — accepted repository/product work still owed;
- **external prerequisite** — genuinely outside repository/agent control, with evidence and required action;
- **failed acceptance** — implementation exists but the requested/proved behavior fails;
- **delivered** — accepted code is on the intended authoritative branch/main state and required CI/install/deploy/readback/operator-visible proof agrees.

A branch, PR, issue comment, plan, test declaration or review is not delivery unless that artifact itself was the requested deliverable.

## Workstream A — source estates and canonical ingest

Representative owners historically include #1403, #967, #1153 and #1177.

Required outcome:

```text
complete selected physical artifact graph
-> explicit disposition for every artifact
-> one-pass/bounded source streaming
-> typed source recovery/decomposition
-> canonical recursive composition / reuse
-> set-sized persistence + evidence fold
-> truthful per-artifact and aggregate receipts
```

Source-specific parsing/grammar remains source-specific. Identity/composition/persistence/fold law remains common.

The same universal execution-grain rule applies here: avoid caller loops around per-atom/per-record DB or native calls when one coarse native/set operation can own the repeated work.

## Workstream B — native/static execution grain

Representative owners historically include #588, #429, #951, #1047 and related operation-ISA work.

Required outcome:

```text
PostgreSQL = durable indexed state / MVCC / transactions / set access
SPI        = prepared, set-sized bridge
C/C++      = repeated algorithms / loops / recursion / parsing / composition /
             trajectories / search / reductions / encoding/materialization
C#/SQL     = orchestration / contracts / transport
```

This law is universal across decomposition, ingestion, reads/cognition, analysis/domain engines, reconstruction, synthesis and export.

Patterns to remove from hot paths include per-row SPI, per-element P/Invoke, scalar SQL in caller loops, recursive CTEs as cognition/search inner engines, uncontrolled `LATERAL`, per-call temp tables, per-item transactions/COPY and “batch” APIs implemented as scalar loops.

## Workstream C — deterministic evidence, chronology and standing

Representative owners include evidence/fold issues such as #1395, #1397 and current governing specs/issues.

Required outcome:

- observed event/source chronology remains distinct from ingest/worker/batch order;
- deterministic calculations remain distinct from attributed testimony;
- evidence dependence/provenance remains queryable;
- standing/replay uses declared recipe-owned periods/order;
- Glicko/fold math fails closed on invalid state and preserves rating/RD/volatility semantics;
- retry/replay is idempotent and batch/thread partitioning cannot change semantic results.

## Workstream D — deployment, reseed and live readback

Representative owners historically include #433, #761, #1132 and source/substrate owners.

Required outcome is not “a seed script ran.” It is an exact source/artifact/generation being installed, populated and read back through production paths with truthful receipts.

Useful receipts identify code/package/native artifact, selected source artifacts, phase timings/work, DB/I/O/resource state, resume/restart behavior, final state and representative reads.

## Workstream E — navigable product/world surfaces

Representative owners include #1404 and current product-navigation issues.

The product exposes the same canonical substrate through reusable browse/rank/profile/evidence/trajectory/world views rather than inventing private UI semantics or arbitrary top-K ceilings.

A UI hierarchy is a navigation grammar, not proof that the substrate itself is a tree.

## Workstream F — query/cognition/forward program

Authority is `docs/specs/36_Laplace_Forward_Pass.md` and `docs/specs/37_Substrate_Operation_ISA.md`.

Current canonical semantic order:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

The whole admitted observation/root, constituent occurrences, prior discourse bindings and open obligations produce a query-relative typed coupling/response field before unconstrained interpretation/provider policy is frozen.

A*, Dijkstra, strongest-walk, trajectory continuation, containment and geometry are routed operators—not cognition by themselves.

Hops, fanout/frontier, provider/operator families and other explicit resources bound sparse work over one shared knowledge world.

## Workstream G — proving domains and export/consumer products

Chess, code, model analysis/export and future domains are proving/consumer surfaces of the common machine rather than permission to grow private intelligence stacks.

Domain acceptance should exercise common identity, recursive trajectories, occurrence/provenance, evidence/standing, coupling/search, realization, witnessing and resource receipts while preserving domain-specific rules/grammars.

Export/synthesis is also subject to the native execution-grain law; correctness of a file format does not excuse per-value high-level boundary overhead.

## Proof workstream

Proof is cross-cutting, not “finish line N.”

Different claims require different evidence:

```text
bounded recursive geometry        -> mathematical proof + executable implementation tests
exact carrier/trajectory           -> round-trip/property tests
finite atom generation             -> exhaustive finite checks where feasible
live referential/recursive closure -> live counterexample scan + counts
conversation/cognition             -> semantic traces + adversarial end-to-end acceptance
performance                        -> exact revision/artifact/host/provider/workload receipts
product delivery                   -> install/deploy/readback/operator-visible proof
```

The exhaustive live recursive physicality/trajectory/reference closure gate remains an implementation obligation; existing unit/reconstruction tests do not silently stand in for it.

## Resource/billing workstream

Product tiers change the admitted execution envelope, not the amount of knowledge Laplace possesses.

The same physical program should support:

```text
EXPLAIN / preflight
-> estimate hops/fanout/providers/index/calculation/resource work
-> reserve allowed compute
-> execute under hard counters
-> emit actual receipt
-> reconcile/refund unused reserve
```

Serviceable host capacity reserves database/product/runner/control-plane headroom. Full-machine saturation is a separate explicit experiment.

## How an agent chooses the next action

Do **not** choose the numerically earliest workstream above.

Instead:

1. load the accepted user outcome and current authority;
2. identify every workstream touched by that outcome;
3. repair source-of-truth drift first when it would otherwise cause implementation toward the wrong machine;
4. implement/verify the actual accepted behavior rather than a smaller substitute;
5. land/deploy/read back where the accepted finish line requires it;
6. correct dependent issues/status/docs so stale prose does not reintroduce the defect;
7. continue until the accepted scope is delivered or the user explicitly changes/stops it.

The workstream letters are organization only. They are not priority.