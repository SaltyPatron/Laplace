## Bucket: A2_endpoints (app/Laplace.Endpoints.OpenAICompat)

### Files read (coverage proof — all 27 read IN FULL)
- [x] AppComposition.cs
- [x] Auth/TenantResolution.cs
- [x] Billing.cs
- [x] BillingPostgres/PostgresBillingEntitlementStore.cs
- [x] BillingPostgres/PostgresBillingLedger.cs
- [x] BillingPostgres/PostgresBillingQuoteStore.cs
- [x] BillingPostgres/PostgresBillingWebhookEventStore.cs
- [x] BillingPostgres/PostgresStripePriceMap.cs
- [x] Contracts.cs
- [x] EndpointJson.cs
- [x] EndpointMappings.Billing.cs
- [x] EndpointMappings.Chess.cs
- [x] EndpointMappings.Core.cs
- [x] EndpointMappings.Inference.cs
- [x] EndpointMappings.Reports.cs
- [x] EntitlementBilling.cs
- [x] ExplainabilityBilling.cs
- [x] ISubstrateClient.cs
- [x] Laplace.Endpoints.OpenAICompat.csproj
- [x] Middleware.cs
- [x] Program.cs
- [x] QuoteGate.cs
- [x] ReportBilling.cs
- [x] ServerSentEvents.cs
- [x] SubstrateClient.cs
- [x] SynthesisBilling.cs
- [x] TurnWitness.cs

### Headline (invention conformance)
**No external LLM/model call anywhere in this bucket.** Every inference/embedding/report path
issues parameterized SQL against `laplace.*` substrate functions (`recall`, `walk_text`,
`completions`, `consensus_out_readable`, `entity_physicalities`, `walk_branches`,
`attestations_out`, `resolve_topic`, `word_id`, `label`). The GPU-free crystal-ball claim is
upheld here: chat/completions are substrate traversals, not proxied generation. The two-level
embedding (FORM = S³ coord, MEANING = Glicko-2 consensus neighbours) is implemented and kept
separate. The findings below are correctness/doc-mismatch/dead-code and a low-priority auth gap.

---

### Findings

**1. EndpointMappings.Core.cs:36-37 — SEVERITY MEDIUM — CATEGORY correctness / disparagement-inverse (stale self-description)**
CLAIM: `/v1/capabilities` advertises `ChatCompletions.Backend = "laplace.recall_session"` and
`Completions.Backend = "laplace.completions"`. Both are wrong vs. the live code path.
VERIFIED:
- Chat (`EndpointMappings.Inference.cs:42-100`): default model → `substrate.WalkTextStreamAsync`
  → `laplace.walk_text` (SubstrateClient.cs:104-105); only a model name containing `"converse"`
  → `ConverseTurnsAsync` → `laplace.recall` (SubstrateClient.cs:51-55). `recall_session` is
  never invoked by the endpoint assembly — grep shows it only in `app/Laplace.Cli/QueryCommands.cs:69`,
  and SubstrateClient.cs:41 comment explicitly says it was "de-forked" away from `recall_session`.
- Completions (`EndpointMappings.Inference.cs:202-226`): uses `WalkTextStreamAsync` → `laplace.walk_text`,
  NOT `laplace.completions`.
The advertised backend strings are documentation that disagrees with code (charter §: prose is a
defect too). CONFIDENCE high.

**2. Laplace.Endpoints.OpenAICompat.Tests/Goldens/capabilities.json:6,11 — SEVERITY MEDIUM — CATEGORY fake-test**
CLAIM: the golden file pins the same stale backend strings (`"laplace.recall_session"`,
`"laplace.completions"`), so the capabilities golden test asserts and "blesses" the wrong
backends — it locks in the doc/code mismatch instead of catching it.
VERIFIED: grep hit on capabilities.json lines 6/11 mirrors Core.cs:36-37. (File is outside this
bucket but is the test that validates this bucket's endpoint; flagged for the owning bucket.)
CONFIDENCE high.

**3. SubstrateClient.cs:127-164 + ISubstrateClient.cs:25 — SEVERITY LOW — CATEGORY dead-code**
CLAIM: `CompletionsAsync` (the only method that calls `laplace.completions`, the function the
capabilities doc names) is never called by any endpoint. `/v1/completions` uses `WalkTextStreamAsync`.
VERIFIED: grep for `CompletionsAsync` → defined in SubstrateClient.cs, declared in ISubstrateClient.cs,
implemented in test `FakeSubstrateClient.cs:43`; no production call site. Dead production path (the
`laplace.completions` SQL surface is unreachable from the API). CONFIDENCE high.

**4. Auth/TenantResolution.cs:33-38 + Program.cs:67-69 — SEVERITY MEDIUM (low-priority area) — CATEGORY correctness / auth**
CLAIM: tenant identity is taken verbatim from the client-supplied `X-Laplace-Tenant` header with
no authentication. A caller can set any tenant string to (a) evade/share the per-tenant sliding
rate-limit partition (Program.cs:67-69), and (b) read another tenant's entitlements and consume
their plan credits.
VERIFIED: `HeaderTenantResolver.ResolveAsync` returns `header.Trim()` unchecked; `/v1/billing/entitlements`
(EndpointMappings.Billing.cs:101-117) and `/v1/billing/entitlements/consume` (lines 119-147) resolve
tenant solely via this resolver, then `PostgresBillingEntitlementStore.TryConsumeCreditAsync` debits
by tenant string alone. AppComposition.cs:15-21 hard-fails any auth mode except `"header"`, so there
is no stronger mode available. Repo marks auth as dev-sandbox/low priority, but this is a real
cross-tenant billing/credit access path, not just rate-limit. CONFIDENCE high.

**5. EndpointMappings.Inference.cs:280-282 — SEVERITY LOW — CATEGORY invention-violation (minor)**
CLAIM: the dense `embedding` vector returned to OpenAI clients is `[X, Y, Z, M, Radius]` — it packs
`Radius` (compositional depth/tier) into the same 5-vector as the S³ surface coordinate `(X,Y,Z,M)`.
The contract comment (Contracts.cs:21-25, Inference.cs:272-273) says the dense vector is "the S³
geometry coordinate". Radius is depth, a distinct geometric axis from the unit-sphere point; mixing
it into the "coordinate" vector mildly conflates two form axes in the one consumer-facing blob
(though `Form` view exposes them separately, so the separation is preserved structurally).
VERIFIED: vector built at Inference.cs:280-282 from `EmbeddingForm` (SubstrateClient.cs:591-593,
source `laplace.entity_physicalities` x,y,z,m,radius). CONFIDENCE med (judgment call on whether
radius-in-vector counts as a form/form conflation).

**6. Billing.cs:18 + 264 — SEVERITY LOW — CATEGORY dead-code / config**
CLAIM: `StripeBillingOptions.SkipSignatureVerification` gates webhook signature verification
(BillingWebhookHandler.cs:264) but is never wired from any env var. AppComposition.cs:68-76 sets
ApiKey/WebhookSecret/urls/Currency/Bypass only — `SkipSignatureVerification` always defaults `false`.
So the flag is effectively dead config (only settable via test InternalsVisibleTo). This is a *secure*
default (good), but the unwired, test-only signature-skip switch should be noted. CONFIDENCE high.

**7. EndpointMappings.Inference.cs:114-116 — SEVERITY INFO — CATEGORY other (graceful empty, not fake)**
The converse path returns the literal `"I hold no consensus about that yet."` when `recall` yields
zero rows. This is an honest empty-result message, not hardcoded fake content; metadata reports
`ReplyRows: 0`. No invention violation — noting because it is a hardcoded user-facing string.
CONFIDENCE high.

**8. Billing.cs:410-414 (EnsureProductAsync Stripe search) — SEVERITY INFO — CATEGORY correctness (not exploitable)**
The Stripe product-search query is string-interpolated: `Query = $"metadata['laplace_product_id']:'{productKey}'"`.
`productKey` is `price.ProductId`, sourced only from the hardcoded `StaticBillingCatalog` keys
(Billing.cs:96-146), never from request input, so no injection vector today. Flagged only because it
is interpolation into a query DSL; if product ids ever become user-authored it would need escaping.
CONFIDENCE high.

### Notes on things that are CLEAN (verified, not assumed)
- SQL injection: all DB access uses Npgsql parameters (`AddWithValue` / typed `NpgsqlParameter`); the
  hex/word resolve uses parameterized regex `@target ~ '^[0-9a-f]{32}$'` (SubstrateClient.cs:390-395,
  556-561). Postgres billing stores all parameterized. No dynamic SQL concatenation of user input.
- Secrets: Stripe API key + webhook secret read from env (`LAPLACE_STRIPE_*`), never hardcoded;
  DB conn from `LAPLACE_DB` defaulting to unix-socket peer auth (SubstrateClient.cs:629-638).
- Webhook auth: real Stripe `EventUtility.ConstructEvent` signature check; missing secret → reject,
  not accept (BillingWebhookHandler.cs:261-274) — secure default. Idempotency via event-store
  `TryBegin` (INSERT … ON CONFLICT DO NOTHING, PostgresBillingWebhookEventStore.cs:11-23).
- Credit consume (PostgresBillingEntitlementStore.cs:162-202): single SQL `FOR UPDATE` CTE — race-safe,
  no read-modify-write gap. In-memory variant uses a lock (EntitlementBilling.cs:146).
- Forwarded headers locked to loopback proxies only (Program.cs:99-107) — XFF can't be spoofed by
  real clients. Good.
- SSE mid-stream errors surface as a data frame after 200 is committed (ServerSentEvents.cs:32-35,
  used Inference.cs:75-78) — correct handling.
- TurnWitness (TurnWitness.cs): witnesses prompt+reply turns back into the substrate as content via
  `NpgsqlSubstrateWriter.ApplyAsync` with content-addressed dedup (`AlreadyWitnessedAsync` checks the
  physicality exists) — conforms to identity=content / dedup-is-the-hash. Bounded channel DropWrite,
  consecutive-failure circuit breaker. No re-routing of domain content through a wrong composer here.
- Embeddings two-level model intact: FORM from `entity_physicalities`, MEANING from
  `consensus_out_readable` (Glicko-2, rank-weighted), returned in separate fields; `*-form*` model
  name suppresses meaning (Inference.cs:274). Matches the invention's mandate.

### Bucket summary
- CRITICAL: 0
- HIGH: 0
- MEDIUM: 3 (#1 stale capability backends, #2 golden pins the stale strings, #4 unauthenticated
  tenant header → cross-tenant credit/entitlement access + rate-limit evasion)
- LOW: 3 (#3 dead `CompletionsAsync`/`laplace.completions` path, #5 radius packed into form vector,
  #6 unwired signature-skip flag)
- INFO: 2 (#7 hardcoded empty-consensus string, #8 non-exploitable Stripe query interpolation)

**Worst issue:** tie between #1/#2 (the `/v1/capabilities` endpoint and its golden test both lie
about the inference backends — chat/completions actually run `laplace.walk_text`, not the advertised
`laplace.recall_session`/`laplace.completions`) and #4 (client-controlled `X-Laplace-Tenant` header
is the sole tenant identity, enabling cross-tenant credit consumption). #4 is the higher real-world
severity but sits in the repo's explicitly-deprioritized auth/billing surface; #1/#2 are the worst
*invention/correctness-integrity* issue in scope because they make the system misreport its own
substrate routing.
