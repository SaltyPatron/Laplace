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

The cheaper product tier is **not** a deliberately less knowledgeable Laplace model. The same admitted content world can be served under different explicit compute envelopes: fewer hops, lower fanout, narrower provider sets, lower concurrency, smaller output budgets, or other declared resource constraints.

A token count may be useful for external comparison. It is not the native unit of Laplace work.

## Content entitlement is separate from compute entitlement

"One content world, variable compute" forbids selling a deliberately dumber model as the cheap tier. It does **not** mean every principal automatically has authority over every private/licensed/restricted portion of that world.

Keep two independent entitlement axes:

```text
CONTENT / CAPABILITY ENTITLEMENT
  which scopes/packages the principal may DISCOVER/INSPECT/COUPLE/TRAVERSE/
  DERIVE/REALIZE/PERSIST/EXPORT/EXECUTE/DELEGATE

COMPUTE ENTITLEMENT
  how much hop/fanout/provider/candidate/trajectory/geometry/CPU/memory/I/O/output
  work this request may consume
```

A content package is an authority manifest over shared canonical state, not a separately trained or copied model. Packages may be purchased, granted by a school/employer, inherited through an explicit role/relationship, or temporarily activated for a task.

Stripe/Patreon/contracts are evidence that an external commercial event occurred. They do not become semantic authority; the canonical Laplace entitlement/grant is the authority object.

This makes the product economically honest: a customer may ask a sophisticated question cheaply when it resolves near the surface, or spend heavily on a simple question by demanding exhaustive depth/breadth. Billing follows admitted/actual cognition work, not a "smarter model" brand.

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

## Provider model and two-sided Seeder/Royalty settlement ("The BitTorrent of AI")

Patreon, Stripe, enterprise contracts, grants, test allowances, or future providers are external entitlement/assertion sources. They map into one canonical membership/allowance model.

Normal included calls should reserve and consume internal allowance rather than generate a payment transaction per request. True a-la-carte expensive work can receive an explicit quote/hard ceiling before execution.

Laplace also supports a two-sided Seeder & Knowledge Royalty ledger ("the BitTorrent of AI") over the same measured receipts:

1. **Compute & Storage Seeding (Hardware Contribution)**:
   - Contributors who host verified perfcache ROMs, execute bounded `OP10 COUPLE` / `OP8 REALIZE` / synthesis work, or serve content-addressed substrate subtrees earn **Compute Allowance Credits** mapped directly from the physical work receipt they served.
2. **Knowledge Seeding & Micro-Royalty Settlement (Content Contribution)**:
   - Every corpus, repository, or user-ingested dataset is a **witness trunk** (`[source record, file trunks]`). A principal may keep their witness trunk `private`, `tenant`, `public`, or `licensed` (a gated Content Package).
   - When a billed query (`OP10 COUPLE` → `OP8 REALIZE`), deep audit, or **Mold-A-Model** synthesis (`OP8 REALIZE` / `EXPORT`) consumes claims/strands from the substrate under an authorized scope (`scope blake3[]`), the native C + SQL attribution function (`laplace_attribution` / `consensus.source_attribution`) walks the containment DAG / evidence bindings in C and computes exact deterministic **basis-point contribution shares (`share_bp`, summing to `10000`)** per witness trunk (`source_id`), weighted by Glicko-2 Fisher information ($I_{c,s} = g(\varphi_s)^2 E(1 - E)$ / $1/\varphi^2$ and trust-bounded games).
   - The same SQL transaction that reconciles the caller's compute/royalty debit settles the royalty pool across each contributing `source_id` (`billing.settle_usage_attribution` → `billing_royalty_allocations` / `source_royalty_balances`), crediting each licensed witness owner in exact proportion to `share_bp`.
   - High-uncertainty (`high φ`), uncorroborated, or conflicting spam sources naturally receive near-zero Fisher information weight and fail election in `OP12 STEER`, preventing sybil/spam farming by mathematical construction.

## Single standard SQL + native C implementation law (no hand-rolled wheels)

All substrate folding, scoped consensus, containment provenance (`walk_up` / `attested`), basis-point source attribution (`share_bp`), and **Mold-A-Model** synthesis/billing math must execute through **one canonical SQL script backed by native C** (`Laplace-postgres/sql/laplace--1.12.sql` + `src/laplace_pg.c` in the modular stack; `extension/laplace_substrate` + `engine/core/src/sql_catalog.def` in the monorepo):

- Do **not** hand-roll multi-line SQL CTEs or duplicate math in C# (`Laplace.Substrate/Crud/Npgsql/*`, `BillingPostgres/*`, `FoundryCommands.cs`, `SynthesisBilling.cs`).
- `ReadPathArchitectureGateTests` must cover `Crud/Npgsql/*` and `BillingPostgres/*` with zero exemptions so every database operation binds a named statement in `sql_catalog.def` or calls a native C extension operator.

## Acceptance

- placeholder credit conversions and endpoint-flat prices are explicitly non-authoritative;
- reusable auth/account/API-key/payment/event plumbing is separated from placeholder pricing math;
- each priced/admission-controlled operation maps to a real semantic work envelope and physical plan;
- preflight and actual receipts share enough dimensions to measure estimator error;
- hop/fanout/provider/trajectory/evidence/output work remains visible where applicable;
- every billed query and Mold-A-Model synthesis emits a native C + SQL source attribution receipt (`source_id`, `claims_touched`, `games`, `fisher_weight`, `share_bp` summing to `10000`) that drives deterministic royalty settlement;
- zero hand-rolled SQL CTEs or duplicate C# synthesis/attribution math remain outside the canonical SQL + native C surface;
- serviceable capacity is calibrated from headroom-preserving runs, not destructive saturation;
- saturation evidence is labeled and never silently promoted to normal customer capacity;
- implementation waste remains measurable and optimizable rather than becoming hidden pricing authority;
- cross-repository references remain coordination links, not semantic delegation.
