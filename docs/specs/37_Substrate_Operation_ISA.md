# Substrate operation ISA

## Contract

Every read, cognition/generation, game, code, model-analysis and export path is a typed program over the operation families below. Public SQL/MCP/HTTP/CLI tools bind and compose these operations; they do not invent private semantic implementations.

Opcode identifiers are stable names, **not execution-order numbers**. New semantic families are appended without renumbering historical identifiers used by plans/tests/receipts. Therefore `OP10 COUPLE` executes between `OP0 RESOLVE` and `OP1 ORIENT` in the canonical unconstrained cognition program.

| Opcode | Family | Contract |
|---|---|---|
| OP0 | RESOLVE | surface/content/scope → exact admitted root, constituent occurrences, bindings, obligations and typed input |
| OP1 | ORIENT | typed coupling field + discourse/task state → joint interpretation / ambiguity disposition |
| OP2 | ROUTE | orientation + hard caller/resource constraints → typed providers, relations, bands, modalities, operators and work envelope |
| OP3 | SCAN | routed typed query/frontier → bounded indexed star expansion / candidates |
| OP4 | COMPOSE | candidate routes/evidence → typed frontier/trajectory/state fold |
| OP5 | PROPOSE | frontier → valid next entities/semantic acts/actions |
| OP6 | STEER | proposals + oriented state/evidence/obligations → ranked/admitted proposals |
| OP7 | SELECT | admitted proposals + declared policy → selected identity/semantic act/action |
| OP8 | REALIZE | selected identity/act → requested external surface |
| OP9 | WITNESS | outcome/receipt → governed append-only observation/testimony |
| OP10 | COUPLE | admitted active state → typed query-relative response field over every eligible indexed plane |

## Canonical ordering

The normal unconstrained cognition order is:

```text
OP0 RESOLVE
-> OP10 COUPLE
-> OP1 ORIENT
-> OP2 ROUTE
-> OP3 SCAN
-> OP4 COMPOSE
-> OP5 PROPOSE
-> OP6 STEER
-> OP7 SELECT
-> OP8 REALIZE
-> OP9 WITNESS
```

A program may omit an operation only when its precondition is already established by the caller/receipt and the trace identifies that fact.

For example, an explicit “run this declared relation query under this exact provider mask” operation may arrive already oriented/routed. A normal-language request asking Laplace to determine meaning may **not** omit COUPLE/ORIENT merely because a convenient default relation mask exists.

Multi-hop/multi-constituent programs may loop the response/execution portion. Each emitted act/constituent changes active state, so the affected coupling/frontier is recomputed before the next selection.

OP9 follows an actual outcome when the program calls for witnessing; a read is never silently testimony merely because it executed.

## OP10 COUPLE law

COUPLE is the ISA-level expression of the spider-colony-web primitive:

> tug the admitted structure and preserve what tugs back, through which route, with what typed force.

Inputs include the exact admitted root/trajectory, constituent occurrences/order, prior discourse bindings, open obligations, world/time/source context and hard caller/resource constraints.

Eligible channels may include exact composition/containment, ordered occurrence, semantic relations, evidence/contradiction/consensus, dependence/provenance, geometry/locality, deterministic calculation/domain providers and prior session/frontier state.

Its output is **typed response state**, not one mandatory scalar score. Relation identity, structural/role compatibility, ordinal/gap continuity, support/refutation, rating/RD/volatility/witness count, source/context dependence, geometry and provenance remain distinguishable until an operation contract declares how they participate.

Different routes converging on one candidate remain visible. Dependent routes do not automatically become independent witnesses.

## Shape and typing

Each operation declares:

- input/output entity classes and physicalities;
- relation/provider/operator families;
- source/context/world/time scope;
- null/unknown/ambiguous behavior;
- ordering and score domains;
- hop/fanout/frontier/resource bounds;
- dependence/provenance treatment;
- receipt fields and completion obligations.

Shape is data/program memory, not endpoint-specific switch logic.

The ISA distinguishes at least:

```text
exact identity                  vs spatial/index candidate discovery
packed trajectory carrier       vs realized child geometry
observed testimony              vs deterministic/analytic calculation
sequence/occurrence evidence    vs proposition consensus
source-scoped                   vs pooled reads
coupling/interpretation         vs policy/routing
proposal                        vs selection
selection                       vs realization
work estimate/reservation       vs actual work receipt
```

## Sparse execution law

ROUTE/SCAN operate over explicit hop/fanout/frontier/provider bounds rather than an obligation to compare the world with itself.

Conceptually:

```text
active state
-> indexed star expansion
-> bounded admitted spokes
-> next centers
-> convergence / typed fold
-> repeat to hop/resource boundary
```

A*, Dijkstra, strongest-walk, containment, trajectory continuation, geometry and deterministic providers are operators callable by the routed program. No single search operator is the cognition ISA.

## Resource/preflight contract

The same typed program used for execution supplies the resource plan used for estimation/billing/capacity admission.

A conforming expensive/billable program can report before execution:

```text
hop/fanout/frontier limits
provider/operator families
expected index/set reads
candidate/trajectory/calculation work
memory/I/O/concurrency envelope
realization/output envelope
reserved compute ceiling
```

Execution reports actual work against the same dimensions so unused reserve can be reconciled and estimators calibrated from real receipts.

A lower resource tier changes this execution envelope over the same entitled substrate; it does not silently route to a deliberately knowledge-reduced Laplace model.

## Physical implementation law

The ISA is a semantic program, not a prescription to cross a process/database boundary once per opcode or per candidate.

The hot physical shape is:

```text
bounded/set-sized PostgreSQL index access through prepared SPI
-> coarse native C/C++ program
-> loops / recursion / parsing / frontier / reduction / calculation
-> bounded result + receipt
```

SQL/C# adapters orchestrate the program and transport state. Repeated per-row SPI, scalar SQL chains, recursive CTEs as the cognition inner engine, per-element P/Invoke, per-item transactions/COPY or nominal batch APIs implemented as scalar loops are non-conforming when a coarse native/set operator can own the same semantics.

This execution-grain law applies to decomposition, ingestion, reads, cognition, analysis/domain engines, reconstruction, synthesis and export—not only to OP3 SCAN.

## Implementation parity

There is one canonical implementation per operation fact. Reference and accelerated/native/provider paths require parity tests appropriate to their contract. Endpoint-specific helpers delegate to the same semantic program.

A perfcache/GPU/native accelerator changes physical execution, not the semantic authority. A miss or provider absence cannot redefine identity/truth.

## Receipts

A completed program can report enough bounded state to audit:

```text
resolved ids / root / occurrences
scope + hard constraints
coupling channels and route families
interpretation / ambiguity disposition
compiled operation/provider sequence
hop/fanout/frontier/candidate reductions
evidence cells + contradiction + standing/uncertainty
dependence/provenance roots
obligation closure
selected semantic act/identity
realization fingerprint
writes/witnesses
estimated/reserved work
actual CPU/memory/I/O/database/boundary work where measured
```

Receipts are content-addressable where the corresponding recipe/state boundary is complete.

## Acceptance

- Every product surface maps to an inspectable ISA program.
- Unconstrained cognition executes OP10 COUPLE before OP1 ORIENT and OP2 ROUTE.
- Competing interpretations remain visible until joint evidence or an explicit caller constraint resolves them.
- Static/architecture gates reject untyped private operation implementations and hot RBAR substitutes.
- Native/reference/accelerated paths agree on the semantic contract: ids, ordering, scores, unknown/ambiguity and output identity.
- MCP/OpenAI/CLI/SQL requests with equivalent semantic input execute equivalent programs.
- Model/source ablation changes declared scope/provider state, not endpoint code.
- Hops/fanout/resource ceilings appear in both preflight and actual receipts.
- The same knowledge substrate remains addressable across resource tiers; only entitled scope and execution budget change.
