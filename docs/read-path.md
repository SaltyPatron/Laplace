# The read path: current execution contract

This file describes the current read/cognition execution law. The previous version was a valuable measured audit from 2026-08-14/15, but its phased repair instructions became stale as those paths changed. That exact historical report remains available in Git history; it must not be used as a current implementation plan.

Authority for the invention is [`INVENTION.md`](INVENTION.md), [`INVENTIONS.md`](INVENTIONS.md), [`specs/36_Laplace_Forward_Pass.md`](specs/36_Laplace_Forward_Pass.md), [`specs/37_Substrate_Operation_ISA.md`](specs/37_Substrate_Operation_ISA.md), and `AGENTS.md`.

---

## 1. What a read is operating over

Laplace does not read one flat graph.

The same canonical identities participate simultaneously in:

```text
recursive composition / child-parent DAGs
ordered packed trajectories
realized child-coordinate curves
containing occurrences / larger trajectories
typed relation + consensus state
attributed attestations / provenance / dependence
source / context / world / time state
Hilbert / spatial / geometric neighborhoods
deterministic calculation/domain providers
session/frontier/obligation state
```

Those planes overlap on exact identities. The useful read primitive is therefore:

> **Given this exact active structure, which indexed planes respond, through which routes, and with what typed evidence?**

A direct definition lookup, containment query, A* path or nearest-neighbor calculation is one operation inside that larger machine, not the read architecture by itself.

---

## 2. Identity, sequence and occurrence

Canonical executable identity is derived from declared content/recipe state rather than from a source-specific row id.

For native multi-child composition, `engine/core/src/hash_composer.c` calls `hash128_merkle(tier, ordered_child_ids, n)`; single-child composition preserves the child id. Source identity is not part of that canonical content recipe.

Normal convergence of equal canonical content is **content-address convergence**, not a cryptographic “collision.”

Sequence order lives in the exact trajectory/composition structure. Packed GeometryZM trajectory vertices carry complete constituent ids plus ordinal/run/flag metadata; they are not child positions. Geometric path reads resolve constituent ids to child physicality coordinates before measuring the realized curve.

Occurrence/containment is different from identity. One canonical entity can be found inside many larger trajectories/contexts without copying the entity once per occurrence.

---

## 3. Query-relative cognition begins before search

The current forward contract is:

```text
RESOLVE
-> COUPLE
-> ORIENT
-> ROUTE
-> SCAN
-> COMPOSE
-> PROPOSE
-> STEER
-> SELECT
-> REALIZE
-> WITNESS
```

The semantic stages do not imply separate database round trips.

### RESOLVE

Admit the complete prompt/request root, constituent occurrences, session/discourse bindings, scope and open obligations.

### COUPLE

Pull every eligible indexed plane under the hard scope/resource boundary and preserve its typed response.

This is the spider-colony-web step: the whole observation tugs the substrate before one unconstrained interpretation/provider/relation mask is frozen.

Responses can carry different meanings:

```text
structural/role compatibility
containment / trajectory order / gap state
semantic relation identity
support / contradiction
rating / RD / volatility / witness breadth
provenance / dependence
source / context / world scope
geometry / Hilbert locality
deterministic provider results
```

They are not required to become one relevance scalar.

### ORIENT / ROUTE

Jointly surviving interpretations determine the task/program, ambiguity disposition, eligible provider/relation/operator families and explicit work envelope.

A caller can deliberately supply a constrained operation. A default mask cannot substitute for interpretation when Laplace is expected to infer what the observation means.

### SCAN onward

The program performs bounded indexed star expansions, folds the resulting typed frontier, proposes/selects a semantic act and only then realizes a requested surface.

A*/Dijkstra, strongest-walk, continuation, containment and geometric operators are tools used by the routed program.

---

## 4. Current canonical forward-program surface

The current extension exposes one native forward-program authority through:

```text
generation.forward_program(...)
```

in `extension/laplace_substrate/sql/functions/generation/walk_continuations.sql.in`, backed by the native `pg_laplace_forward_trace` entry point.

Its declared program owns:

```text
exact prompt admission
query-relative routing
candidate adjudication
obligation closure
selection
semantic-act fingerprinting
working-state extension
terminal execution receipt
```

The trace/receipt exposes root/candidate/context/channel/occurrence/relation/opposition/support/standing/source/context/obligation/completion/semantic-act state.

`generation.forward_text(...)` in `walk_text.sql.in` executes that canonical program once, accepts only completed semantic-act output, then batch-realizes the selected ids. It does not promote an exhausted/unresolved search frontier into answer text.

`converse.forward_turn(...)` adds prior session turn/content identities as prior frontier state rather than rendering and reparsing a transcript. Normal `converse.chat(...)` projects the canonical forward-turn output.

These facts supersede the old 2026-08 audit statement that the S6/S7/S8 generation path still needed to be wired.

They do **not** prove that every intended coupling channel is complete. Complete typed response-field coverage remains an acceptance obligation and must be proved against the native trace/program rather than inferred from names.

---

## 5. Sparse work: hops and fanout

Laplace's read cost is governed by the responding workset, not merely by total world size.

Conceptually:

```text
root/frontier
-> indexed responders
-> admit at most the routed fanout/resource envelope
-> responders become next star centers
-> converge/fold routes
-> repeat to hop/resource boundary
```

Useful work dimensions include:

```text
roots / occurrences
hops
fanout / candidate / frontier widths
relation/provider/operator families
index probes / rows / physicalities touched
trajectory/containment expansion
geometry/calculation work
standing/evidence cells
realization/output work
```

A direct content/perfcache address may be effectively O(1) within its declared finite window; a B-tree/GiST/GIN lookup has its own index complexity; sorting or pairwise operations over an admitted frontier may be superlinear in that frontier. Receipts name the actual provider/algorithm rather than calling every read `O(K)`.

---

## 6. PostgreSQL indexes the world; native code executes the repeated work

The hot physical shape is:

```text
PostgreSQL
  selective indexed/set access
        |
        | prepared/set-sized SPI
        v
native C/C++
  loops / recursion / frontier expansion / trajectory work /
  graph/search mechanics / reductions / deterministic calculations /
  batch realization/materialization
        |
        v
bounded result + receipt
```

SQL and C# remain orchestration/contract/transport layers.

The design is not “SQL is slow, C is fast.” The critical property is **execution grain**.

These are architecture smells inside hot/repeated paths:

```text
one SQL call per candidate
one SPI call/prepare per row
one P/Invoke per node/token/value
recursive CTE as the inner cognition engine
unbounded LATERAL fanout
per-call temp-table/materialization
nominal batch API implemented as scalar-call loop
```

One set-sized indexed fetch plus a native loop can replace thousands of boundary/planner/marshalling crossings.

The same rule applies outside reads—to decomposition, ingestion, analysis/domain engines, reconstruction, synthesis and export.

---

## 7. Partition/index access law

PostgreSQL remains valuable because it can nominate the correct workset cheaply when calls supply the keys/indexable predicates the physical schema actually owns.

Callers should therefore:

- supply relation/type/subject/direction/scope information needed for partition/index pruning;
- prefer canonical typed accessors/operators over hand-rolled scans;
- avoid joins whose shape defeats available partition/index keys when a bounded set/probe form expresses the same semantics;
- declare structural cardinality bounds where the planner needs them;
- keep census/admin operations separate from interactive read budgets;
- measure plan cost and executor cost independently where planning a huge partition tree is itself material.

Historical audit numbers are evidence for why these rules exist, not permanent performance constants.

---

## 8. Realization is downstream of semantic selection

Read/cognition operates on canonical ids and typed state. Human text/notation is produced afterward.

A selected semantic identity does not need a precomputed text label to be valid. `realize.batch`, text renderers and other codec/domain realizers supply the requested surface.

Realization must not:

- decide what the selected object “really was” after selection;
- convert an unresolved frontier into an answer simply because its ids can be rendered;
- execute one avoidable high-level call per constituent when the output can be materialized in a coarse native/batch operation.

---

## 9. Preflight, billing and resource admission

Because work is explicit, planning can estimate the same physical dimensions execution will consume.

A billable/capacity-controlled request can follow:

```text
EXPLAIN / plan
-> estimate hop/fanout/provider/index/calculation/I/O work
-> reserve compute-credit ceiling
-> execute with hard counters
-> emit actual receipt
-> reconcile unused reserve
```

This controls how far/broadly one request may pull the same knowledge world. It is not implemented by choosing a deliberately less knowledgeable Laplace model.

Predicted wall time is calibrated from prior receipts; actual elapsed time still depends on cache state, scheduler contention, storage and concurrent work.

---

## 10. Performance evidence

Read performance should be reported in units matching the work actually performed, for example:

```text
request/semantic-act latency
indexed web responses/s
candidate/frontier cells examined and admitted
consensus/evidence cells resolved
trajectory constituents / containment hits
routes/hops expanded
realized structures/bytes
CPU / memory / I/O / DB calls / boundary crossings
```

Token-equivalent rates are useful familiar normalizations for ingest/composition workloads but do not describe every read operation.

`docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md` governs benchmark receipts and now distinguishes serviceable host capacity from deliberate full-machine saturation.

---

## 11. Current proof boundary

Current code/tests demonstrate important pieces of this architecture, including exact trajectory/mantissa round-trip, large-sequence trajectory handling, Unicode/Tier-0 placement properties, canonical native forward-program plumbing and retained-database reconstruction.

Still-open proof/implementation obligations include:

- exhaustive live recursive physicality/reference closure over the populated substrate;
- complete typed coupling-field coverage across all intended response planes;
- removal of remaining RBAR/boundary-grain violations across every pipeline;
- reconciliation of the native centroid versus current managed Karcher parent-coordinate laws;
- serviceable-host resource reserve in the executable benchmark suite.

These are implementation obligations. They do not redefine the invention downward.
