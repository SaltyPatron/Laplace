# Bucket: I2_web (web/ — React/TS SPA + Playwright e2e)

### Files read
- [x] web/.gitignore — clean (node_modules/, dist/, *.local)
- [x] web/e2e/chat.spec.ts — real e2e, see findings
- [x] web/index.html — clean SPA shell
- [x] web/openapi/openapi.json — read header + structural grep (4649-line GENERATED OpenAPI 3.1.1 spec; legit; includes /chess paths). Drift finding below.
- [x] web/package-lock.json — read sample + audited every `resolved` host; all `registry.npmjs.org`, lockfileVersion 3. Clean.
- [x] web/package.json — clean; deps react19/zustand, dev playwright/vite/openapi-typescript
- [x] web/playwright.config.ts — real, baseURL 127.0.0.1:5187 (the API host, not vite)
- [x] web/src/App.tsx — clean
- [x] web/src/api/client.ts — clean fetch wrapper
- [x] web/src/api/sse.ts — clean SSE reader
- [x] web/src/api/types.gen.ts — generated; STALE vs openapi.json (finding)
- [x] web/src/billing/BillingView.tsx — clean (real API-driven)
- [x] web/src/chat/ChatView.tsx — real API-driven; ord_used drop finding
- [x] web/src/chat/ProvenanceBadge.tsx — dead ordUsed branch (finding)
- [x] web/src/chat/ReceiptPanel.tsx — clean (real /v1/evidence)
- [x] web/src/chess/ChessView.tsx — real substrate-engine-driven; dev-proxy finding
- [x] web/src/main.tsx — clean
- [x] web/src/store.ts — clean (zustand)
- [x] web/src/styles.css — clean (CSS only)
- [x] web/test-results/.last-run.json — `{"status":"passed","failedTests":[]}` (artifact)
- [x] web/tsconfig.json — clean
- [x] web/tsconfig.tsbuildinfo — TS build cache (generated); references the 11 src files; normal
- [x] web/vite.config.ts — dev-proxy finding

## Cross-checks done against the server (to verify the UI presents the real substrate)
- Chess endpoints exist and shapes match: `app/Laplace.Endpoints.OpenAICompat/EndpointMappings.Chess.cs` maps `/chess/new|legal|move|bestmove|train/start|train/stop|train/status` over `ChessEngineService`. Server records `ChessMoveScore(Uci,EffMu,Rated)`, `ChessBestMove`, `ChessApplyResult`, `ChessTrainStatus` (`app/Laplace.Chess.Service/ChessEngineService.cs:12-20`) — these serialize camelCase and match ChessView's `MoveScore/BestMove/ApplyResult/TrainStatus` interfaces field-for-field. **The chess UI drives the real substrate engine; nothing mocked.**
- Chat: `SubstrateClient.cs:52` (`SELECT reply, eff_mu, witnesses`) is the live source for chat provenance; SSE chunks carry a `laplace` field (golden `Goldens/chat-generate-sse.json` shows `laplace.ord_used` in generate mode). **Chat is substrate-backed, not an external LLM.** No OpenAI/Anthropic/etc. client anywhere in web/ or its data path.
- Billing/evidence views call the real `/v1/billing/*` and `/v1/evidence/*` endpoints. No fabricated data.

---

## Findings

### F1 — Chess tab is broken under `npm run dev` (vite proxy omits `/chess`)
- FILE:LINE: web/vite.config.ts:9-15
- SEVERITY: MEDIUM
- CATEGORY: correctness
- CLAIM: The dev proxy forwards only `/v1`, `/health`, `/openapi` to the API. `ChessView` calls `/chess/legal`, `/chess/bestmove`, `/chess/move`, `/chess/new`, `/chess/train/*` (ChessView.tsx:78,142,156,192,200,202; endpoints confirmed at EndpointMappings.Chess.cs and they sit OUTSIDE `/v1`). Under `vite` dev (port 5173) these requests hit the vite server (SPA fallback / 404), so every chess request fails — the board never scores, bot never moves. Production works (API serves the built SPA from its own origin) and e2e works (baseURL is the API host 5187), which is why this is unnoticed.
- VERIFIED: read vite.config proxy keys; cross-checked the literal paths ChessView posts to; confirmed server routes are non-`/v1`. CONFIDENCE: high

### F2 — Generated API types are stale (no `/chess`), contract drift
- FILE:LINE: web/src/api/types.gen.ts (paths interface ends at `/v1/billing/usage`, line ~1171) vs web/openapi/openapi.json:973-1132 (`/chess/*` present)
- SEVERITY: LOW
- CATEGORY: other (contract drift)
- CLAIM: `openapi.json` (the gen:api input) already contains the `/chess/*` endpoints, but `types.gen.ts` was not regenerated and lacks them. `npm run gen:api` is stale. Harmless today because ChessView hand-rolls its own interfaces instead of using the generated types, but the generated client is no longer a faithful mirror of the spec.
- VERIFIED: grep `/chess/` hits openapi.json but not types.gen.ts; read types.gen paths in full. CONFIDENCE: high

### F3 — `ord_used` provenance is silently dropped in the streaming chat path; ProvenanceBadge `ordUsed` branch is dead
- FILE:LINE: web/src/chat/ChatView.tsx:63-64; web/src/chat/ProvenanceBadge.tsx:8-10
- SEVERITY: MEDIUM
- CATEGORY: correctness / dead-code
- CLAIM: In generate mode the substrate attaches only `laplace.ord_used` per token (no eff_mu/witnesses — see Goldens/chat-generate-sse.json:31-33,49-51,67-69). ChatView builds a provenance entry ONLY when `lap?.eff_mu !== undefined || lap?.witnesses !== undefined`, so `ord_used` is never captured and `ProvenanceEntry.ordUsed` is never set. Consequently the `ProvenanceBadge` `if (entry.ordUsed !== undefined)` branch (the "ord {n}" badge) is unreachable from the live app, and generated (non-recall) replies render with NO provenance badges at all — undercutting the app's stated premise ("μ and witness counts attached"). Recall/define replies (which do carry eff_mu/witnesses) are unaffected.
- VERIFIED: traced ChatView chunk handling + ProvenanceBadge + the SSE golden chunk shape. CONFIDENCE: high

### F4 — e2e tests are REAL but thin; two assert near-nothing
- FILE:LINE: web/e2e/chat.spec.ts:9-17, 19-28
- SEVERITY: LOW
- CATEGORY: fake-test (weak, not fraudulent)
- CLAIM: The suite drives the real app against the real API (no mocks/fixtures/route-stubs anywhere; baseURL is the live API host), so it is not a fake-against-mocks suite — good. But: (a) "chat happy path" only asserts the assistant bubble is non-empty, which the streaming placeholder `'…'` (ChatView.tsx:141) satisfies even on a substrate miss/error, so it does not prove a *grounded* reply or any provenance; (b) "evidence lookup" passes on EITHER `.evidence h3` OR `.error` (line 27), i.e. it passes when the lookup fails. These prove the surfaces render, not that the substrate answered. `.last-run.json` shows passed, but that file is just an artifact, not proof of substrate correctness.
- VERIFIED: read spec in full; cross-checked the `'…'` placeholder and the error branch it tolerates. CONFIDENCE: high

### F5 — `outcomeName` index mapping assumes outcome ∈ {0,1,2}
- FILE:LINE: web/src/chat/ReceiptPanel.tsx:5-9
- SEVERITY: LOW
- CATEGORY: correctness
- CLAIM: `OUTCOME = ['refute','draw','confirm']` indexed by `asNum(outcome)`. The server's `LabeledEvidenceItem.outcome` is typed `number | string` (types.gen.ts:1622). If outcomes are ever a continuous Glicko score (0..1) rather than the integers {0,1,2}, `asNum` yields a fractional/out-of-range index → `OUTCOME[x]` is `undefined`, falling back to `'draw'` and mislabeling refute/confirm. Works only if the API guarantees integer 0/1/2. Not verified against the evidence SQL; flag for the engine bucket to confirm the outcome encoding.
- VERIFIED: read ReceiptPanel + EvidenceResponse/LabeledEvidenceItem types; outcome encoding not traced to SQL. CONFIDENCE: med

### F6 — No hardcoded secrets / fake data / external-model calls (negative finding)
- SEVERITY: INFO
- CATEGORY: other
- CLAIM: Grepped the whole web/ tree (incl. package-lock.json and openapi.json) for sk_live/sk_test/pk_*/api-key/password/token/Bearer/AKIA — no matches. No `anthropic`/`openai`/external-LLM client in web/. Stripe URLs are taken from server responses (`stripe_checkout_url`), never hardcoded. Tenant defaults to `'local-dev'` in localStorage (store.ts:41) — a dev default, not a secret.
- VERIFIED: secret grep + dependency host audit + manual read of all source. CONFIDENCE: high

### Bucket summary
- CRITICAL: 0 | HIGH: 0 | MEDIUM: 2 (F1, F3) | LOW: 3 (F2, F4, F5) | INFO: 1 (F6)
- Overall: the UI faithfully presents the real substrate — chat streams from `SubstrateClient` (Glicko eff_mu/witnesses), chess drives the live `ChessEngineService`, billing/evidence hit real endpoints. No mocks, no fixtures, no external model, no fabricated responses, no secrets. e2e is real (drives app+API) though weak.
- Single worst issue: **F1 — the Chess tab does not work in `npm run dev` because vite.config.ts never proxies `/chess` to the API**, so local dev of the flagship chess modality is broken (masked by e2e/prod hitting the API origin directly).
