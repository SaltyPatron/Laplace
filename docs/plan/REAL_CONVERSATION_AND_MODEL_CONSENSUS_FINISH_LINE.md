# Real conversation and heterogeneous-model consensus

Status: **scoped product acceptance contract, not the definition of Laplace as a whole and not a current-status report.**

The original 2026-08-08/09 contract established durable requirements for MCP/OpenAI-compatible conversation, code/tool feedback, heterogeneous-model participation and deterministic realization/export. Its old forward-stage sequence and several implementation assumptions became stale; the canonical cognition law now lives in `docs/specs/36_Laplace_Forward_Pass.md` and `37_Substrate_Operation_ISA.md`.

Delivery state must be re-verified against current issues, code, CI, installed artifacts and live behavior.

## Product claim

Laplace serves one substrate-native program over the selected witnessed/calculated world. It is not a checkpoint merger, answer ensemble, retrieval wrapper, hidden judge or endpoint-specific inference stack.

Corpora, conversations, code, games, tools, conventional models and generated outputs participate as explicit content/sources/witnesses/calculation providers over shared canonical structures. Their evidence can converge while provenance, disagreement, dependence and uncertainty remain inspectable.

## Representation laws used by conversation

Conversation uses the same representation as every other domain:

- admitted atomic/typed structure composes recursively into exact finite higher structures;
- canonical executable identity is derived under a declared recipe; source/provenance/worker/batch facts do not silently remint equal content;
- current native multi-child Merkle composition includes its declared tier in the executable recipe; single-child composition preserves the child id;
- current Tier-0 text generation uses deterministic S³/Super-Fibonacci placement inside the common 4D frame;
- parent physicality `coord`, packed trajectory carrier and realized child-coordinate curve are different state;
- a packed GeometryZM trajectory vertex carries the complete 128-bit constituent id plus ordinal/run/typed flags and is not a child spatial position;
- containment/trajectory/order/occurrence remain addressable without duplicating canonical content;
- observations/testimony, deterministic calculations and folded consensus standing remain different state classes.

The bounded-composition proof and carrier details live in `docs/INVENTION.md`.

## Conversation is an ordered witnessed trajectory

A conversation/session is an ordered content/occurrence structure. Each turn has exact content plus role/source/context/tool/calculation/witness state as applicable.

Appending a turn creates new current state without destroying the prior prefix. Summaries/caches may accelerate navigation but do not replace the underlying turn/content trajectory.

Conversation state includes more than prior rendered text. The forward program can retain exact prior turn/content identities, bindings, source/world/time scope, tool/calculation outcomes and open obligations.

## Canonical cognition program

The current semantic program is:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

These are semantic stages and may be fused inside one coarse native operator; they do not imply one SQL/client boundary per stage.

### RESOLVE

Admit the exact current request/root, constituent occurrences/order, prior discourse bindings, world/time/source scope, hard caller constraints and open obligations.

### COUPLE

Compute the query-relative typed response field of the whole admitted observation against every eligible indexed plane under the hard scope/resource boundary.

Eligible responses may include recursive composition/containment, ordered occurrence/continuation, typed semantic relations, evidence/contradiction/standing, source/context/dependence, geometry/locality, deterministic tools/calculations, model testimony and prior session/frontier state.

The response is not prematurely collapsed into one scalar. Relation identity, role compatibility, ordinal/gap state, support/refutation, rating/RD/volatility/witness breadth, source dependence, geometry and provenance stay typed.

### ORIENT

Jointly surviving senses/bindings/tasks/obligations constrain one another through the entire observation. The result may be unique enough to execute, ambiguous, inconsistent/impossible, or resource-bounded.

Ambiguity is allowed to remain ambiguity.

### ROUTE

Compile the oriented state into eligible provider/relation/operator families plus explicit hop/fanout/frontier/calculation/resource bounds.

Caller-supplied masks are valid when the caller explicitly requests a constrained operation. A default provider/relation mask must not secretly decide interpretation before COUPLE/ORIENT.

### SCAN / COMPOSE / PROPOSE / STEER / SELECT

Use indexed star expansion and typed folds over the responding frontier. A*, Dijkstra, strongest-walk, containment, continuation, geometry, model/circuit providers and tools are operators inside the routed program.

Multiple compatible routes converging on the same candidate remain visible and may be evidence under the declared dependence law.

### REALIZE / WITNESS

Only a selected semantic act/entity/action is rendered to the requested protocol. Realization must not turn an unresolved frontier into answer content merely because some ids have labels.

When the operation calls for it, the resulting turn/action/tool outcome and its receipt are witnessed through the governed write lane. The new observation changes the next coupling/frontier state.

## Stateful generation

Each emitted constituent/semantic act updates active state before the next one is selected:

```text
state_t
-> couple/orient
-> bounded hop/fanout execution
-> select/realize/witness
-> state_t+1
-> recompute affected coupling/frontier
```

Building one global candidate list and draining it without feedback is not the intended conversation machine.

## Heterogeneous models are participants, not a judge panel

An admitted checkpoint remains an exact source/calculation boundary. Tokenizers, tensors, layers, heads, experts, factors, Q/K/V/O/FFN roles and measured executions can remain source-scoped and queryable.

Cross-model alignment is not established merely because two local layer/head ordinals match. Shared entity/trajectory coverage, structural/functional behavior, relations, geometry and witnessed outcomes may provide explicit comparison evidence.

Pooled operation means the routed program can consume evidence from several selected sources/providers under one substrate law. It does not mean N answer strings are generated and a hidden LLM/judge votes on them.

Ablation must be possible: source A only, source B only, pooled A+B, etc. The receipt identifies the selected scope/providers and how they affected the active state.

## Code/tool feedback uses the same loop

Code generation is not a private model path.

```text
generate/select code structure
-> stage exact content
-> run declared toolchain/test/calculation provider
-> witness/record the result with provenance/recipe
-> next cognition round sees the changed evidence state
```

Compile/test failure is a typed outcome, not reason to delete/remint the code entity.

## No architectural fixed context window

Conversation history is addressable substrate state rather than a transformer-only fixed token buffer.

That does not mean every request scans unlimited history. Routing/hops/fanout/provider/source/time/resource bounds control the work explicitly. The difference is that older admitted state does not become structurally unreachable solely because it fell out of a fixed model context window.

## Realization/export

Human text, tool JSON, code, chess notation and model/export files are realizations/consumer artifacts of selected canonical state.

They do not own identity. Rendering/export must preserve source/recipe/receipt state and obey the same universal execution-grain law as ingestion and cognition: bulk/stream/coarse native materialization instead of avoidable per-token/per-cell/per-constituent boundary calls.

## Native physical execution

The serving/product path inherits the repository-wide physical law:

```text
PostgreSQL = durable indexed state / set access
SPI        = prepared, set-sized bridge
native C/C++ = repeated coupling/search/trajectory/reduction/realization work
C#/SQL     = orchestration / contracts / transport
```

The semantic stage diagram does not authorize RBAR SQL, recursive CTE cognition, per-candidate SPI/PInvoke or endpoint-private inference implementations.

## Resource tiers / billing

All entitled tiers query the same knowledge world. Product/resource levels differ by explicit execution envelope, not by pointing cheaper users at an intentionally less knowledgeable Laplace model.

Possible controls include hops, fanout/frontier/candidates, provider/operator families, calculations, memory/I/O/concurrency and output.

The product should support plan/`EXPLAIN` → estimated work → allowance reservation → bounded execution → actual receipt → reconciliation/refund. Predicted wall time is calibrated from historical receipts rather than promised mathematically exact before execution.

## Receipts / WHY / WHY_NOT

A completed operation should be able to expose bounded typed evidence such as:

```text
resolved root + constituent occurrences
active scope / hard caller constraints
coupling channels / responding route families
surviving interpretation / ambiguity disposition
compiled provider/operator program
hops / fanout / candidates / frontier
standing / contradiction / uncertainty / dependence roots
obligation state
selected semantic act/entity
action/tool/calculation outcomes
realization/output fingerprint
writes/witnesses
estimated/reserved/actual work
```

`WHY_NOT` is a typed failure/resource/ambiguity receipt, not a generic apology string.

## Acceptance

A conforming conversation/model-consensus product demonstrates at least:

- real multi-turn state survives restart and affects later selection;
- whole-prompt/ordered-context changes interpretation; constituent order and discourse matter;
- competing senses/providers remain eligible through COUPLE until joint evidence or explicit caller constraint resolves them;
- ambiguous prompts can remain ambiguous rather than forcing one label;
- source A/B/pooled ablation changes evidence/state in a receipted way without a hidden judge;
- tool/code outcomes feed later cognition through governed state;
- each emitted constituent/act updates the next coupling/frontier;
- OpenAI-compatible, MCP, CLI/SQL and other equivalent semantic fronts execute the same underlying operation program;
- realization occurs after semantic selection;
- no endpoint-private lookup/template/LLM path substitutes for the canonical program;
- hop/fanout/resource limits appear in both preflight and actual receipts;
- lower resource tiers preserve knowledge access and vary compute envelope instead;
- hot repeated execution remains coarse native/set work rather than per-stage/per-candidate RBAR;
- exact source/artifact/session/output identities make the result reproducible/auditable within its declared deterministic boundary.

## Status / ownership

This contract does not claim all acceptance above is currently delivered. Current ownership/state lives in GitHub issues plus current source/CI/live proof.

A related `Laplace-Refactor` implementation may pursue the same contract. It is coordination/evidence, not a separate definition of what this product must mean.
