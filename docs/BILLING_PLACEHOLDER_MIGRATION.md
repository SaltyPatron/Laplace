# Billing placeholder migration and measured-compute replacement

Tracking: #1425, #1428, #1429, #1431, #1436.

The current Billing UI and credit schedule in this repository contain historical/prototype scaffolding. Their arbitrary plan names, thresholds, conversions, and endpoint-flat debit values are not product authority.

The governing replacement semantics live in `docs/INVENTION.md`, the forward-pass/ISA contracts, and the measured resource-plan law below. Related work in other repositories may implement the same law; it does not become normative merely by being newer or cleaner.

## Placeholder semantics to retire

Do not preserve as authority merely because they exist in code/UI:

- arbitrary Free / Supporter / Pro plan limits;
- arbitrary request/concurrency/storage/export values;
- generic `credits` as a universal work unit without measured mapping;
- arbitrary money-to-credit conversions;
- hard-coded per-action debit values for chat/query/explore/ingest/export/forge;
- HTTP payment/allowance behavior tied directly to those placeholder values.

These values were not derived from actual Laplace semantic work envelopes, physical plans, serviceable host capacity, estimator confidence, or measured accepted-work throughput.

## Preserve reusable plumbing

Inventory and preserve reusable mechanics independently of pricing semantics:

- authentication/principal/account association;
- API-key creation/revocation/scoping;
- subscription/payment-provider adapters and durable webhook/event handling;
- idempotency and reservation/job identities;
- usage/activity/history surfaces;
- operation entry points;
- UI layout/interaction patterns;
- tests proving authentication, authorization, replay safety, transactionality or concurrency without asserting arbitrary credit math.

## Replacement execution model

```text
membership / grant / purchased allowance
        |
        v
logical Laplace program
        |
        v
semantic work envelope
  admitted roots / active observation
  coupling/provider/operator families
  hop depth
  fanout / candidate / frontier widths
  trajectory / containment expansion
  relation / geometry / calculation work
  standing/evidence cells
  realization/output obligations
        |
        v
physical plan
  indexes / perfcaches / native work
  PostgreSQL/SPI/set work
  CPU / memory / I/O / network / storage
  concurrency / service headroom
        |
        v
preflight estimate + confidence
        |
        v
reserve allowance / run / queue / deny / quote
        |
        v
bounded execution
        |
        v
actual execution receipt
        |
        v
reconcile unused reserve + calibrate estimator
```

The cheaper product tier is **not** a deliberately less knowledgeable Laplace model. The same admitted knowledge world can be served under different explicit compute envelopes: fewer hops, lower fanout, narrower provider sets, lower concurrency, smaller output budgets, or other declared resource constraints.

A token count may be useful for external comparison. It is not the native unit of Laplace work.

## Serviceable capacity, not benchmark self-destruction

Customer/service limits must calibrate against **serviceable** capacity, not the largest point a benchmark can print while starving the host.

The managed benchmark workflow therefore distinguishes:

```text
serviceable-headroom
  default; reserves host CPU capacity for PostgreSQL/runner/product/control plane

saturation-allowed
  explicit opt-in; may use every allowed logical CPU; not normal service capacity
```

On the known 6C/12T managed host the default benchmark scale plan reserves two logical CPUs and resolves to `1,2,3,4,6,8,10`; the 12-worker boundary is explicit saturation only.

See `docs/benchmarks/SCALING_MODES.md` and `docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`.

A future serviceable receipt should include direct database/product/runner liveness so the configured reserve is proven adequate, not merely assumed.

## Useful historical workload evidence

Historical measurements remain calibration observations, not pricing constants. Examples include:

- corpus-scale UD ingest runs with actual units/files/staged rows/wall time;
- native single-thread composition floors;
- file-grain makespan and independent-stream scaling curves;
- SQL `EXPLAIN ANALYZE` / buffer/cardinality evidence;
- reconstruction/roundtrip timings;
- future complete cognition accepted-work receipts.

Do not infer missing CPU, memory, I/O, energy, or monetary cost dimensions from wall time alone.

## Optimization must not become permanent customer tax

Receipts should expose enough execution-grain evidence to separate semantic useful work from implementation waste.

If one operation performs thousands of avoidable scalar SQL/SPI/PInvoke crossings, that overhead is a defect to optimize—not a reason to permanently price the intended semantic operation as though the defect were fundamental.

The cost model should therefore retain both semantic coordinates (hops/fanout/providers/candidates/etc.) and physical coordinates (calls/rows/CPU/I/O/etc.) so estimator drift and architectural inefficiency are visible.

## Provider model

Patreon, Stripe, enterprise contracts, grants, test allowances, or future providers are external entitlement/assertion sources. They map into one canonical membership/allowance model.

Normal included calls should reserve and consume internal allowance rather than generate a payment transaction per request. True a-la-carte expensive work can receive an explicit quote/hard ceiling before execution.

## Acceptance

- placeholder credit conversions and endpoint-flat prices are explicitly non-authoritative;
- reusable auth/account/API-key/payment/event plumbing is separated from placeholder pricing math;
- each priced/admission-controlled operation maps to a real semantic work envelope and physical plan;
- preflight and actual receipts share enough dimensions to measure estimator error;
- hop/fanout/provider/trajectory/evidence/output work remains visible where applicable;
- serviceable capacity is calibrated from headroom-preserving runs, not destructive saturation;
- saturation evidence is labeled and never silently promoted to normal customer capacity;
- implementation waste remains measurable and optimizable rather than becoming hidden pricing authority;
- cross-repository references remain coordination links, not semantic delegation.
