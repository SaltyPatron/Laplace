# Billing placeholder migration and refactor handoff

Tracking: #1425
Normative replacement: `SaltyPatron/Laplace-Refactor#140`

## Status

The current Billing UI and credit schedule in this repository are historical/prototype scaffolding. They are useful for discovering routes, account/API-key plumbing, UX needs, and operations that eventually require commercial policy, but their plan names, thresholds, credit conversion, and per-operation debit values are **not product authority**.

Do not carry them into the refactor as requirements merely because they already exist in code/UI.

## Placeholder semantics to retire

The existing surface currently presents concepts including:

- arbitrary Free / Supporter / Pro plan limits;
- arbitrary request/concurrency/storage/export values;
- generic `credits` as the canonical consumption unit;
- `$0.10 = 100 credits`;
- hard-coded action costs such as chat/query/explore/ingest/model-export/model-forge credits;
- HTTP 402 behavior tied directly to insufficient placeholder credits.

These values were not derived from measured Laplace resource plans, infrastructure cost, estimator confidence, accepted benchmark work, or actual supported throughput.

## What is worth preserving

Inventory and preserve reusable implementation mechanics independently of pricing semantics:

- authentication and principal/account association;
- API-key creation/revocation/scoping mechanisms;
- billing-page/product-surface layout patterns;
- any subscription/payment-provider abstraction that is not coupled to placeholder credit math;
- job identifiers and idempotency primitives;
- HTTP error/response translation patterns;
- model export / forge / ingest operation entry points;
- usage/activity/history UI patterns;
- any durable event/webhook processing primitives;
- tests that prove authentication, authorization, replay safety, or concurrency behavior without asserting arbitrary credit values.

Anything reusable should be lifted only after separating it from private hard-coded plan/pricing tables.

## Replacement model

```text
Patreon / Stripe / enterprise / grant / future provider
   -> authenticated external assertion
   -> canonical Laplace membership/additive grant
   -> native entitlement calculation
   -> logical program
   -> physical resource plan
   -> preflight cost envelope
   -> run / queue / deny / a-la-carte disposition
   -> atomic allowance/resource reservation
   -> bounded execution
   -> execution receipt
   -> reconciliation / payment settlement
```

Patreon is a subscription source, not a separate class of user. Stripe direct subscriptions map to the same canonical membership model. Stripe may additionally sell additive throttles/limits and true a-la-carte high-cost work such as model export.

Normal calls do not generate a card transaction. Included work reserves and consumes internal Laplace allowance. Separately charged work gets an explicit quote and hard ceiling before expensive execution.

## Resource economics

The replacement system must cost the actual Laplace physical plan. Relevant dimensions include CPU/core-time, memory/byte-seconds, I/O, storage/retention, network, PostgreSQL workers/connections, nested libraries, external tool/provider costs, artifact size, topology/provider class, and cache/world-state reuse.

A token count is not the Laplace unit of work. Competitor token pricing is external comparison evidence only.

## Existing performance evidence

A current Operator run provides a useful workload observation for future calibration:

- `UDDecomposer` complete;
- `6021/6021` units;
- `2,177,867/2,177,867` input;
- `686/686` files;
- displayed staged E/P/A `14,253,943 / 12,011,828 / 4,324,744`;
- displayed throughput `16,458 rows/s`;
- displayed wall time `30m 58s`.

The future estimator must capture full machine-readable resource receipts for comparable runs. Wall time alone is not cost. ConceptNet churn/failure visible in the same Operator session is explicitly unrelated and handled elsewhere.

## Refactor issue map

- `SaltyPatron/Laplace-Refactor#140` — parent architecture
- `#141` — canonical membership and provider-neutral entitlement
- `#142` — preflight physical-plan costing/reservation/reconciliation
- `#143` — Patreon + Stripe adapters
- `#144` — competitor pricing / tokens-per-penny evidence
- `#145` — membership/quote/throttle UI and API
- `#146` — real-workload estimator calibration
- `#148` — a-la-carte high-cost jobs
- `#149` — measured plan/throttle policy

This repository remains implementation/history evidence. The refactor issues and architecture document are the forward normative design.
