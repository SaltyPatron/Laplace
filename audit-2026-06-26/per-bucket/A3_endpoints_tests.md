# Bucket: A3 — app/Laplace.Endpoints.OpenAICompat.Tests

Audited the API test project (52 files). Cross-checked every test assertion and golden
against the real endpoint code in `app/Laplace.Endpoints.OpenAICompat/` (EndpointMappings.Core.cs,
EndpointMappings.Inference.cs, SubstrateClient.cs, Contracts.cs).

### Files read (coverage proof)
- [x] BillingStoreContractTests.cs
- [x] BillingTestFactories.cs
- [x] EndpointContractTests.cs
- [x] FakeSubstrateClient.cs
- [x] GoldenFactory.cs
- [x] GoldenJson.cs
- [x] GoldenShapeTests.cs
- [x] Laplace.Endpoints.OpenAICompat.Tests.csproj
- [x] Goldens/audit-200.json
- [x] Goldens/audit-no-quote-402.json
- [x] Goldens/audit-quote-200.json
- [x] Goldens/billing-catalog.json
- [x] Goldens/billing-plans.json
- [x] Goldens/billing-products.json
- [x] Goldens/capabilities.json
- [x] Goldens/catalog-sync-200.json
- [x] Goldens/chat-converse-200.json
- [x] Goldens/chat-converse-sse.json
- [x] Goldens/chat-generate-200.json
- [x] Goldens/chat-generate-sse.json
- [x] Goldens/chat-invalid-json-400.json
- [x] Goldens/chat-missing-model-400.json
- [x] Goldens/chat-no-quote-402.json
- [x] Goldens/chat-pending-quote-402.json
- [x] Goldens/completions-200-logprobs.json
- [x] Goldens/completions-200.json
- [x] Goldens/completions-missing-prompt-400.json
- [x] Goldens/completions-no-quote-402.json
- [x] Goldens/completions-sse.json
- [x] Goldens/consume-200.json
- [x] Goldens/consume-402.json
- [x] Goldens/embeddings-missing-input-400.json
- [x] Goldens/entitlements-200.json
- [x] Goldens/evidence-200.json
- [x] Goldens/evidence-404.json
- [x] Goldens/explain-200.json
- [x] Goldens/explain-invalid-depth-400.json
- [x] Goldens/explain-quote-200.json
- [x] Goldens/health.json
- [x] Goldens/models.json
- [x] Goldens/plan-subscribe-200.json
- [x] Goldens/plan-subscribe-unknown-400.json
- [x] Goldens/preflight-200.json
- [x] Goldens/preflight-unknown-service-400.json
- [x] Goldens/quote-get-200.json
- [x] Goldens/quote-not-found-400.json
- [x] Goldens/recipe-quote-200.json
- [x] Goldens/synthesis-quote-200.json
- [x] Goldens/usage-200.json
- [x] Goldens/visualization-quote-200.json
- [x] Goldens/viz-200.json
- [x] Goldens/webhook-approve-200.json

---

## Findings

### F1 — capabilities golden pins TWO wrong backend-routing strings (confirms + extends sibling finding)
**FILE:** Goldens/capabilities.json:6, :11 ; EndpointContractTests.cs:55-56 ; EndpointMappings.Core.cs:36-37
**SEVERITY:** MEDIUM  **CATEGORY:** correctness / fake-test (golden pins wrong behavior)
**CLAIM:** The capabilities response advertises backends that the code does not use:
- `chat_completions.backend = "laplace.recall_session"`. The real path `SubstrateClient.ConverseTurnsAsync`
  (SubstrateClient.cs:51-55) runs `laplace.recall(@p, laplace.resolve_topic(...))` and its own code comment
  (lines 40-50) says it was *deliberately de-forked away from* `recall_session` ("STABLE, not the VOLATILE
  recall_session that appended to ... session_topics"). So the advertised backend names the exact thing the
  code abandoned.
- `completions.backend = "laplace.completions"`. The real `/v1/completions` endpoint calls
  `substrate.WalkTextStreamAsync` (EndpointMappings.Inference.cs:202,224), whose SQL is
  `laplace.walk_text(...)` (SubstrateClient.cs:104-105). `laplace.completions` is never invoked by any
  endpoint (see F2).
**VERIFIED:** Traced capabilities literal (EndpointMappings.Core.cs:35-43) → golden (capabilities.json) →
test assertion `EndpointContractTests.Capabilities_ExposeLiveAndPendingStatus` and `GoldenShapeTests.Golden_Capabilities`.
Compared advertised strings against the actual SQL each endpoint issues. The golden + both tests enshrine the
wrong strings, so they actively protect the drift instead of catching it.
**CONFIDENCE:** high.

### F2 — `CompletionsAsync` / `laplace.completions` is dead code (prod + fake stub)
**FILE:** SubstrateClient.cs:127-164 ; ISubstrateClient.cs:25 ; FakeSubstrateClient.cs:43-47 ; Contracts.cs:8 (CompletionRow)
**SEVERITY:** LOW  **CATEGORY:** dead-code
**CLAIM:** `CompletionsAsync` (the `laplace.completions(...)` SQL read) and its `CompletionRow` contract are
defined and implemented in both the real client and the fake, but no endpoint or test ever calls them. Grep for
`CompletionsAsync` returns only the interface decl + the two implementations. The `/v1/completions` endpoint uses
`WalkTextStreamAsync` instead. The fake's `CompletionsAsync` is therefore an untested stub for a dead method.
**VERIFIED:** Grep `CompletionsAsync|WalkTextStreamAsync` across `*.cs` — only decl/impl sites for the former, real
call sites only for the latter.
**CONFIDENCE:** high.

### F3 — OpenAI `token_logprobs` populated with n-gram stride, not log-probabilities; goldens pin it
**FILE:** EndpointMappings.Inference.cs:209,240 ; Contracts.cs:15 ; Goldens/completions-200-logprobs.json:13-17 ; Goldens/completions-sse.json
**SEVERITY:** MEDIUM  **CATEGORY:** correctness (wire-contract) / fake-test (golden pins wrong behavior)
**CLAIM:** `GenerateToken(int Step, string Token, decimal Mu)` — the third field is named `Mu` but the real client
fills it from the `stride_used` column (`new GenerateToken(step, tok, ord)`, SubstrateClient.cs:117-123; SQL selects
`stride_used`). The chunk path even renders it as `ord_used` (EndpointMappings.Inference.cs:67; chat-generate-sse.json
shows `"ord_used": 5`). But the `/v1/completions` logprobs path emits this same value into OpenAI's
`logprobs.token_logprobs`: `new CompletionLogprobs([(double)token.Mu])` (line 209, 240). Result: golden
`completions-200-logprobs.json` and `completions-sse.json` pin `token_logprobs` = `[5,4,3]` — positive integers that
are n-gram strides, not log-probabilities (real OpenAI clients require values ≤ 0). The golden tests enshrine this
semantically-wrong field instead of flagging it.
**VERIFIED:** Traced `GenerateToken.Mu` definition → real producer (`stride_used`) → completions consumer
(`token_logprobs`) → golden values `[5,4,3]` matching the FakeSubstrateClient stride args `5,4,3` (FakeSubstrateClient.cs:35,39,40).
**CONFIDENCE:** high (value is stride/order); med on severity (whether intended).

### F4 — No test exercises the real substrate query path; all inference goldens assert against the fake
**FILE:** GoldenFactory.cs:18-19 ; GoldenShapeTests.cs (all inference/evidence/audit/viz/explain tests) ; FakeSubstrateClient.cs
**SEVERITY:** MEDIUM  **CATEGORY:** fake-test (scope) / coverage-gap
**CLAIM:** `GoldenFactory` removes the real `ISubstrateClient` and substitutes `FakeSubstrateClient`
(hardcoded whale/cetacean rows). Every chat/completions/embeddings/evidence/audit/visualization/explain golden
therefore validates only the HTTP serialization + billing-gate + model-routing layer — never the SQL in
`SubstrateClient.cs` (recall/walk_text/completions/walk_branches/attestations_out/entity_physicalities/
consensus_out_readable/substrate_counts/consensus_stats). That SQL has ZERO automated coverage in this bucket.
These are honestly-scoped *wire-shape* tests (legitimate as such), but they must not be read as proof the
substrate produces anything; the data-bearing behavior is untested. The capabilities-string drift in F1/F2 is a
direct symptom — nothing tests that the advertised backend equals the executed one.
**VERIFIED:** GoldenFactory.cs:18-19 (`RemoveAll<ISubstrateClient>` + `AddSingleton<FakeSubstrateClient>`); every
golden value matches FakeSubstrateClient constants, not DB output.
**CONFIDENCE:** high.

### F5 — `HealthReady_WhenSubstrateUnreachable_Returns503` is environment-fragile
**FILE:** EndpointContractTests.cs:31-45
**SEVERITY:** LOW  **CATEGORY:** fake-test (fragility)
**CLAIM:** This test uses `SignedWebhookFactory`, which does NOT swap in the fake — it runs the real
`SubstrateClient` against whatever `LAPLACE_DB` resolves to (default `Host=/var/run/postgresql;...laplace-dev`).
It asserts 503 + `substrate_reachable=false`. On CI without a DB it passes; on a developer machine with a
reachable, seeded DB `ReadinessAsync` returns `Ready=true` → 200 and the test FAILS. Its sibling
`GoldenShapeTests.HealthReady_WhenSubstrateSeeded_Returns200WithCounts` uses the fake (`Ready=true`) and asserts
the opposite — both green only because they use different fixtures and different environment assumptions.
**VERIFIED:** SignedWebhookFactory (BillingTestFactories.cs:10-27) only PostConfigures StripeBillingOptions, never
touches ISubstrateClient; SubstrateClient.BuildConnectionString (SubstrateClient.cs:629-638) reads LAPLACE_DB.
**CONFIDENCE:** high.

### F6 — Webhook happy-path tests bypass signature verification; golden still reports `verified: true`
**FILE:** BillingTestFactories.cs:14-27 ; GoldenFactory.cs:20-24 ; Goldens/webhook-approve-200.json:3 ; EndpointContractTests.cs (Approve/Activate/Renew/Duplicate)
**SEVERITY:** LOW  **CATEGORY:** fake-test (partial)
**CLAIM:** All webhook approval/plan-activation/renewal/duplicate tests run with
`SkipSignatureVerification = true` and a stub `Sign()` that returns a fixed `"t=...,v1=test"` (never a real HMAC).
So the post-verification logic is tested, but the HMAC path that produces a genuine `verified:true` is not — yet
`webhook-approve-200.json` pins `"verified": true`. Real signature handling IS covered separately and genuinely by
`StripeWebhook_InvalidSignature_IsRejected` (StrictWebhookFactory, `v1=deadbeef` → 400 `invalid_signature`) and
`StripeWebhook_UnconfiguredSecret_FailsClosed` (400 `webhook_secret_unconfigured`), which only pass if verification
actually runs — those are real security tests. Net: verification is exercised, but the `verified:true` golden value
is produced under skip-verification, so it doesn't prove a valid signature was checked.
**VERIFIED:** BillingTestFactories.cs:14-27 (`SkipSignatureVerification=true`, fake Sign), strict/unconfigured
factories at lines 29-45; EndpointContractTests.cs:447-470 + 420-444 exercise the real reject paths.
**CONFIDENCE:** high.

### F7 — Static-config goldens are self-referential (low value, not defects)
**FILE:** Goldens/models.json, capabilities.json, billing-catalog.json, billing-products.json, billing-plans.json, catalog-sync-200.json
**SEVERITY:** INFO  **CATEGORY:** other
**CLAIM:** These goldens pin hand-written constant tables (model list, price catalog, plan bundles) to themselves;
they catch accidental edits but assert nothing about behavior. Fine as drift guards. Flagged only so they aren't
mistaken for behavioral coverage. (capabilities.json is the exception — it pins *wrong* constants, see F1.)
**VERIFIED:** Each golden matches the literal in EndpointMappings.Core.cs / billing definition code.
**CONFIDENCE:** high.

---

## Genuinely-real tests in this bucket (not hollow)
- `BillingStoreContractTests` (BillingStoreContractTests.cs): shared abstract contract run for real against the
  `InMemory*` stores (always) and `Postgres*` stores (skippable, gated on reachable `LAPLACE_DB` + `app.billing_quotes`).
  The InMemory variant tests real billing logic (quote lifecycle, ledger ordering, entitlement consume/exhaust/
  renew/cancel, webhook-event idempotency, price-map overwrite). The Postgres skip is a legitimate `[SkippableFact]`,
  not a fake — `TryBuild` probes `SELECT 1 FROM app.billing_quotes` and skips cleanly if absent. NOT hollow.
- The billing/validation contract tests (400/402 paths, synthesis/explain/audit/viz/recipe quote scaling,
  ordering assertions like `largeAmount > smallAmount`) exercise the real billing-quote math (substrate-independent)
  and are real behavioral tests.
- Signature reject + fail-closed webhook tests (F6) are real security tests.
- Mid-stream SSE failure test (`Chat_Generate_Sse_MidStreamFailure...`, GoldenShapeTests.cs:200-220) drives the real
  endpoint error-framing via the fake's `trigger-stream-error` sentinel — a real test of the endpoint's
  error-frame-then-[DONE] contract.

## No tests are skipped/ignored/empty/assert-nothing beyond the documented `[SkippableFact]` Postgres gate.
No tests insert into a populated DB and assert `rows_new=0`. No invention-architecture violations in the test code
itself (these are app-layer billing/HTTP tests, dev-sandbox priority per CLAUDE.md §2).

---

### Bucket summary
- CRITICAL: 0
- HIGH: 0
- MEDIUM: 3 (F1 capabilities wrong backend strings; F3 logprobs=stride pinned by goldens; F4 zero real-substrate coverage)
- LOW: 3 (F2 dead `CompletionsAsync`; F5 env-fragile readiness test; F6 verified:true under skip-verification)
- INFO: 1 (F7 self-referential config goldens)

**Single worst issue:** F1 — the `/v1/capabilities` golden (and the two tests asserting it) pin backend-routing
strings that contradict the code: `chat_completions` advertises `laplace.recall_session` (the path the code's own
comment says it abandoned for `laplace.recall`), and `completions` advertises `laplace.completions` (a method that
is never called; the endpoint actually runs `laplace.walk_text`). The golden tests cement the lie instead of
catching it. Confirms and extends the sibling auditor's finding (two wrong strings, not one).
