# The Laplace forward pass

## Purpose

Laplace uses one stateful, typed execution program for conversation, code, games, model-scoped queries, analysis and export. API adapters bind requests to this program; they do not implement rival cognition/generation paths.

The program operates over one shared witnessed substrate. It does not first choose a smaller knowledge model and it does not assume one interpretation merely to decide which part of the world may respond.

## Canonical program

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

The stages are semantic contracts, not an instruction to introduce eleven SQL/client round trips. A conforming implementation may fuse stages inside one coarse native operator as long as the trace/receipt preserves the required state transitions and evidence boundaries.

### RESOLVE

Admit/resolve the exact current observation/root, constituent occurrences, session/context identities, prior discourse bindings, world/time/source boundary, open obligations and requested output contract.

Unicode/grammar decomposition and tier ascent/descent recover structure. They do not privilege one token, noun, regex, topic word or renderable label as the interpretation root.

The prompt/request is first one exact observation/trunk with ordered constituents.

### COUPLE

Compute the **query-relative coupling/response field** of the admitted observation against every eligible indexed plane under the caller's hard scope/resource boundary.

The whole active state participates:

```text
current root / trajectory
constituent occurrences + order/gaps
prior discourse bindings
open obligations
world/time/source context
explicit hard caller constraints
```

Eligible responses may come from:

```text
exact identity / composition
containment / parent trajectories
ordered occurrence / continuation
semantic relation families
attestation / contradiction / consensus standing
source/context dependence
structural and geometric neighborhoods
deterministic calculation/tool/domain planes
prior session/frontier state
```

A response retains its typed route and evidence. Relation identity, role compatibility, exact ordinal/gap state, containment, support/refutation, rating/RD/volatility/witnesses, source dependence, geometry/locality and provenance are **not** immediately collapsed into one universal relevance scalar.

Multiple compatible routes converging on the same candidate are part of the response. Dependent routes do not automatically count as independent witnesses.

COUPLE answers:

> What in the admitted web actually responds to this exact observation, by which route, and with what typed force?

It does **not** answer by first selecting one sense/provider/relation mask and then asking only that reduced world.

### ORIENT

Construct the joint task/discourse interpretation from the coupling field.

Candidate senses, roles, bindings, topics, operations and obligations constrain one another through the complete observation. The result may be:

```text
unique enough to execute
multiple surviving interpretations / ambiguity
inconsistent / impossible under current evidence
resource-bounded / more compute required
```

Ambiguity is a valid state. Do not manufacture certainty merely to obtain one route.

Caller declarations may impose hard scope or explicitly request a constrained operation. A default policy/provider mask must not substitute for ORIENT when the system is expected to infer meaning.

### ROUTE

Compile the oriented state into an executable cognition program: eligible relation/provider/operator families, salience bands, modalities, circuit/domain planes, hop/fanout/frontier limits, required calculations and output obligations.

Policy normally follows interpretation. ROUTE must preserve surviving alternatives where the operation requires them.

The route is also the basis for preflight work estimation: it should expose the same hop/fanout/provider/index/operator dimensions that execution will consume rather than inventing a separate billing model.

### SCAN

Discover the bounded responding frontier through exact identity, containment, indexes, perfcache, PostGIS/Hilbert locality, source/context filters, typed graph operations and declared calculation providers.

The primitive work shape is an indexed star expansion around the active root/frontier. Selected spokes become subsequent centers until the hop/resource boundary is reached.

Approximate geometry may nominate candidates but cannot establish exact identity or truth.

A*, Dijkstra, strongest-first walk, containment, trajectory continuation and geometric search are operators inside SCAN/ROUTE. None is itself the definition of cognition.

### COMPOSE

Build/fold the active typed frontier from the responding routes, trajectories, factors, tiers, relation bands, standing, contradiction and uncertainty.

Corroborating and conflicting witnesses remain visible. Different evidence dimensions remain typed until the operation contract declares a gate, cost, ranking dimension or fold.

Convergent routes may strengthen a candidate only under the declared dependence/provenance law.

### PROPOSE

Produce legal/grammatical/typed next constituents, semantic acts or actions from the composed frontier.

Proposal may combine physical continuation, graph evidence, model-circuit testimony, code grammar, tool results or domain operators without committing to an answer.

Routing state and output eligibility are separate. Reaching a frame, sense, category or other identity does not license emitting its label merely because it is renderable. An output operation must establish the result-bearing relation/ordered observation/semantic act first.

Conversely, an explicitly selected typed result does not require a text physicality; realization supplies the requested surface after selection without replacing identity.

### STEER

Apply the oriented task, discourse state, source/world scope, hop/fanout/resource limits, ordinal continuity, standing/uncertainty, observed outcomes, obligations and current residual/frontier state to the proposal set.

STEER is query-relative. It must not silently reintroduce a global popularity score or a preselected semantic interpretation discarded by COUPLE/ORIENT.

### SELECT

Select under the declared deterministic or stochastic policy.

The selected item/act must be supported by the admitted evidence and constraints, and selection must retain enough receipt state to explain which routes/standing/obligations determined eligibility.

Pooled model consensus is consumed as witnessed standing/state. N external model answers are not adjudicated here by a hidden judge.

### REALIZE

Render the selected semantic act/entity/action into the requested surface without using rendering to reclassify it.

Realization is bulk/batchable and preserves exact identity. It is subject to the same native execution-grain law as the rest of Laplace; one selected structure must not become thousands of avoidable SQL/PInvoke/high-level per-constituent crossings.

### WITNESS

Append the observation/turn/action/tool/result and its receipt through the governed write lane when the operation contract calls for witnessing.

Calculated outcomes identify analyzer/tool/version/recipe. Reads alone do not witness.

New evidence or emitted content changes the active state and therefore the next coupling field.

## Stateful emission

Every emitted constituent/semantic act updates the active trajectory, discourse bindings, obligations, residual/frontier state and query-relative coupling before the next constituent is selected.

Building one semantic frontier and draining it without feedback is not a conforming forward pass.

Conceptually:

```text
state_t
-> couple/orient
-> sparse hop/fanout execution
-> select/realize/witness
-> state_t+1
-> recompute affected coupling/frontier
-> ...
```

## Persistent execution state, KV correspondence and context

The conventional KV-cache comparison is functional, not literal. In an
autoregressive transformer each attention layer can retain key/value tensors for
previous token positions so later decode steps reuse them. Laplace does not turn the
forward pass into a second tensor-cache ontology.

The shared persistent state available to a pass includes, as applicable:

```text
canonical entity/content identity + recursive composition DAG
occurrences / containers / trajectories / exact order and gaps
attestations / witnesses / contradiction / provenance
consensus standing + uncertainty
physicalities / coordinates / Hilbert locality / geometric state
source / context / world / time scope
session trajectory / discourse bindings / open obligations
admitted deterministic calculations and their receipts
```

The pass may materialize an in-memory frontier, residual state, candidate set or
operator-local cache to execute efficiently. Such state is a bounded working
projection over the shared substrate. It must retain canonical ids/routes needed to
rejoin durable state, must not become an untracked private knowledge graph, and must
not be treated as a second semantic authority.

Laplace has no fixed transformer-style token context window. A request is still
finite: its compute envelope bounds hops, fanout, candidate work, provider/operator
scope, memory, I/O, concurrency and output. These are execution limits over the same
addressable knowledge world, not a rule that state older than a fixed token count is
discarded or inaccessible.

## Processing layers and extensibility

A Laplace “layer” means one routed processing/fold round over the enabled typed
planes/operators; it does not mean one fixed neural block and it does not mean that
every provider runs in a universal serial order.

Typed channels may include exact composition/containment, occurrence/trajectory,
attestation and witnessing, standing/uncertainty, physicality, coordinate/Hilbert
locality, centroid/Fréchet or other geometry, source/context/time, model-circuit
testimony, and deterministic domain/tool calculations. ROUTE selects the lawful
operators and SCAN/COMPOSE/PROPOSE/STEER consume their typed results. New operator
families may be added through the governed ISA/registry without redefining canonical
identity or inventing a parallel cognition engine.

Model export consumes these same typed planes through an explicit recipe/template and
materializes the tensor/package contract of the target architecture. Export is a
projection of substrate state; it is not a second source of truth, and support for a
new target architecture requires an explicit mapping/writer plus validation.

## Compute envelope and preflight

Hops, fanout/frontier width, provider/operator families, candidate work, trajectory expansion, calculation work, memory/I/O/concurrency and realization/output are explicit execution dimensions over the **same knowledge world**.

An entitled lower-cost request is not implemented by pointing it at a deliberately knowledge-reduced Laplace model.

Where work is billable or capacity-controlled, the forward program should support:

```text
plan / EXPLAIN
-> derive semantic work and target-machine work from the compiled program/artifact
-> resolve an exact/symbolic/conditional cycle expression against declared machine/clock/state
-> reserve admitted compute ceiling
-> execute with hard resource counters
-> emit actual work receipt
-> reconcile/refund unused reserve
```

For a fully specified artifact/program, executed path/counts, target ISA and microarchitecture, scheduling/memory model, initial machine state and clock, preflight may calculate elapsed cycles and corresponding machine time directly. If path counts, cache state, scheduler interference, I/O service time or concurrent load are not fixed, retain those terms as symbolic, conditional or distributional state rather than collapsing them into a benchmark average. Empirical measurements validate/calibrate incomplete physical or environmental models and compare calculated versus observed receipts; they do not define cost when the required machine semantics are already known.

## Native execution grain

The stage diagram is not permission to put cognition in RBAR SQL.

The hot implementation shape is:

```text
bounded/indexed set access through PostgreSQL/SPI
-> one coarse native C/C++ program
-> native loops / recursion / frontier work / reductions
-> bounded result + trace/receipt
```

Repeated per-candidate SQL functions, recursive CTEs as the inner cognition engine, per-row SPI, per-element P/Invoke or batch APIs implemented as scalar-call loops violate the physical architecture even if they preserve semantic output.

## Heterogeneous source consensus

Checkpoint sources, corpora, tools, user feedback and domain observations meet through canonical content and typed evidence. A pass may explicitly scope source A, source B or pooled A+B for diagnosis. Pooled mode produces one path/act from the admitted evidence; it is not runtime majority voting or a hidden judge.

## Code lane

Code generation uses the same program with grammar/AST trajectories and toolchain operations. Generated code is staged as content, compiled/tested under declared tools and the outcomes are witnessed before a subsequent decision can learn from them.

## Trace contract

Each pass exposes a bounded typed trace/receipt sufficient to audit at least:

```text
resolved root / occurrence ids
active scope + hard caller constraints
coupling channels / responding route families
surviving interpretations / ambiguity disposition
compiled provider/operator program
hop/fanout/frontier/candidate counts
ordered/context/occurrence coverage
evidence cells + contradiction + standing/uncertainty
obligation state
selected semantic act/item
realization identity/fingerprint
state transition / writes
actual resource/work counters
```

MCP, HTTP, CLI/SQL inspection, streaming and export adapters must agree at this semantic level.

## Acceptance

- Whole-prompt interpretation is affected by constituent order/context and prior discourse, not by a preselected noun/topic/regex root.
- Competing senses/providers remain eligible through COUPLE until joint evidence can orient them or ambiguity is explicitly retained.
- Default relation/provider masks do not choose the interpretation before query-relative response is computed.
- Multiple independent routes converging on one candidate are visible in the trace; dependence/provenance prevents duplicate roots from masquerading as independent support.
- Hop/fanout/resource ceilings bound actual work over the same substrate and appear in preflight + execution receipts.
- Multi-turn correction, anaphora, topic return and abstention work after restart.
- Each emitted constituent changes the next-step coupling/frontier state.
- Equivalent MCP/OpenAI/CLI operation requests share semantic traces.
- Code generate/compile/test feedback affects a later pass.
- Incompatible model sources can participate in one pooled answer without a hidden judge.
- Realization cannot turn an unresolved/incomplete search into answer content.
- No external answer-writing LLM or GPU is required for the canonical native pass.
- The hot path executes as coarse native/set work rather than stage-by-stage RBAR SQL.
