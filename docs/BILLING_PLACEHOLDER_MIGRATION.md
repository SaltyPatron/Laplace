# Billing / compute-envelope migration

Tracking: #1425

Status: **the old hard-coded pricing/credit UI is placeholder history; the replacement law is part of the Laplace architecture in this repository.** Related work may also be tracked in `Laplace-Refactor`, but another repository/issue does not supersede `INVENTION.md`, `AGENTS.md`, the forward-pass spec, or this repository's current accepted work.

## What is being retired

Historical Billing UI/code has presented placeholder concepts such as:

- arbitrary Free / Supporter / Pro request/concurrency/storage/export limits;
- generic credits disconnected from actual work;
- fixed money-to-credit conversion;
- hard-coded action costs for chat/query/explore/ingest/model export/forge;
- HTTP 402 behavior tied directly to those placeholder values.

Those values were not derived from the physical Laplace program, measured host capacity, estimator error, accepted quality/resource envelopes or actual workload receipts. They are not product authority.

Reusable account/auth/payment/UI mechanics may be preserved independently of placeholder pricing semantics.

## Governing product law: one knowledge world, variable compute

Laplace does not create commercial tiers by pointing cheaper requests at deliberately less knowledgeable models.

Entitled requests address the same admitted knowledge world. What changes is the **execution envelope** the request is allowed to activate.

Primary semantic/work dimensions include, as applicable:

```text
admitted roots / active observation size
coupling channels / provider families
hop depth
fanout / candidate / frontier widths
relation/operator families
containment / trajectory expansion
geometry / graph / deterministic-calculation work
standing/evidence cells
source/world/time scope when entitlement constrains scope
realization / output work
```

These are then translated by the physical plan into machine resources:

```text
CPU/core time
memory / working-set bytes
PostgreSQL/SPI calls and rows/cells
I/O
network
storage / retention
concurrency / worker grants
optional accelerator/provider work
external tool/provider charges
artifact/output bytes
```

The billing model therefore follows the cognition/execution program instead of inventing a second unit of work.

## Hops and fanout are first-class commercial controls

A simple product vocabulary may eventually present user-friendly modes, but underneath them the system should be able to state the actual work envelope.

For example, two requests can have the same knowledge access while receiving different admitted search budgets:

```text
request A: H = 2, fanout <= 8
request B: H = 6, adaptive fanout <= 128
```

This does **not** mean those literal values are product plans. It illustrates the law: charge/control the amount of web Laplace is allowed to tug, not the amount of knowledge the system is allowed to possess.

The real plan can also constrain provider/operator families, trajectory/geometry/calculation work, concurrency, output and other typed resources.

## Preflight must cost the same program that executes

The intended flow is:

```text
authenticated request
-> entitlement / additive grant resolution
-> exact request admission
-> COUPLE / ORIENT / ROUTE planning boundary
-> physical plan / EXPLAIN
-> estimated work + calibrated runtime/cost envelope
-> allowance/compute-credit availability check
-> atomic reserve
-> bounded execution under hard counters
-> execution receipt
-> reconcile/refund unused reserve
-> optional external payment settlement for separately charged work
```

The estimator must not use a private pricing-only cost model unrelated to execution.

The plan/receipt should expose the same dimensions before and after the run so calibration is possible:

```text
estimated vs actual hops
estimated vs actual fanout/frontier/candidates
estimated vs actual index/rows/cells
provider/operator selection
trajectory/containment/calculation work
CPU / memory / I/O / DB / network / accelerator work
output/artifact size
wall time
```

## Exact budget versus predicted wall time

Laplace can know an admitted work ceiling exactly when it is expressed as hard counters/resources.

It can often estimate elapsed time very tightly from previous receipts on a controlled host, but wall time is not mathematically exact before execution because cache state, scheduler contention, database load, storage latency and concurrent work can change it.

Therefore billing/admission distinguishes:

```text
exact declared maximum work / reserved allowance
calibrated predicted runtime/resource use
actual execution receipt
```

## Adaptive execution and refunds

A request need not consume its entire authorized envelope.

If query-relative coupling and standing converge early, obligations close, or uncertainty is already below the operation's declared threshold, execution may stop before the maximum hop/fanout/resource boundary.

Unused reserved allowance is reconciled/refunded rather than being charged merely because it was available.

Conversely, an execution that reaches its ceiling while required obligations remain open can return a resource-bounded disposition or request additional authorized compute instead of silently exceeding the user's balance.

## Membership/payment providers are adapters

Patreon, Stripe, enterprise contracts, grants or future providers supply authenticated external entitlement/payment assertions. They do not define separate classes of Laplace intelligence.

A provider-neutral shape is:

```text
external membership/payment assertion
-> canonical Laplace entitlement / additive allowance
-> compute envelope / quote policy
-> common execution program
```

Normal included calls need not produce a card transaction. High-cost separately charged work can receive an explicit quote/hard ceiling before execution.

## Compute credits

“Compute credit” is acceptable as an accounting abstraction **only if it is backed by the actual measured execution dimensions** above.

It must not become a magic token-equivalent or arbitrary per-endpoint debit table.

A credit schedule may normalize several physical dimensions for product simplicity, but the underlying receipt must remain available so pricing/allowance can be recalibrated without changing cognition semantics.

## Token pricing is comparison evidence, not the native unit

A conventional-model token count can be useful for competitor pricing/performance comparisons. It is not the native Laplace unit of work.

One Laplace operation may resolve a large exact trajectory, many evidence/consensus cells and many convergent paths in one indexed/native operation. Conversely, a short textual request may deliberately authorize deep/high-fanout reasoning.

Useful commercial/performance comparisons therefore include accepted-work quality, latency, machine resources and actual web/structural work—not only surface tokens.

## Serviceable capacity constrains sellable capacity

A host's theoretical saturation point is not automatically sellable capacity.

Capacity admission must reserve the resources required for PostgreSQL, the product, control plane, monitoring and operator/runner health. A benchmark point that consumes every schedulable CPU and knocks over normal service is a saturation experiment, not the serviceable compute pool available for customer reservations.

See #1436 and `docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`.

## Execution-grain economics

Billing must not normalize away avoidable implementation waste.

The universal physical law is:

```text
indexed/set-sized handoff
-> native C/C++ repeated work
-> bulk/set result
```

If an implementation turns one logical operation into thousands of scalar SQL/SPI/PInvoke/parser/materialization crossings, those costs identify an implementation defect; they do not automatically become the permanent price of the semantic operation.

Estimator calibration should therefore retain boundary-call/batch-size data where it matters so optimization can lower real cost without changing the user's semantic contract.

## What should be preserved from the old billing surface

Preserve reusable mechanics only after separating them from placeholder credit math, including where sound:

- authentication and principal/account association;
- API-key creation/revocation/scoping;
- provider-neutral membership/subscription primitives;
- payment webhook/event replay safety;
- job ids/idempotency;
- usage/activity/history UI patterns;
- quote/reservation/receipt UI/API shapes;
- error translation and explicit insufficient-resource dispositions;
- high-cost job entry points such as ingest/export/forge where they remain valid operations.

## Acceptance

- [ ] no product tier is implemented as a deliberately knowledge-reduced Laplace model;
- [ ] entitlement resolves to explicit operation/resource envelopes;
- [ ] hop/fanout/frontier/provider/operator work is visible in plan/receipt where applicable;
- [ ] preflight estimates the same physical program that executes;
- [ ] allowance is reserved atomically before expensive work and hard ceilings are enforced;
- [ ] actual work is receipted and unused reserve is reconciled/refunded;
- [ ] estimator error is measured/calibrated from receipts rather than hidden;
- [ ] serviceable capacity, not destructive saturation, determines normal host admission;
- [ ] placeholder fixed per-endpoint credit prices are removed or explicitly labeled non-authoritative until calibrated;
- [ ] payment-provider adapters do not redefine cognition or knowledge access;
- [ ] execution-grain defects are tracked as optimization/architecture defects rather than silently normalized into permanent pricing.

## Cross-repository tracking

Related implementation work may exist in `Laplace-Refactor` issues such as the former billing/entitlement/costing series. Those references are coordination aids only. They are not the normative replacement for this repository's invention/product law.
