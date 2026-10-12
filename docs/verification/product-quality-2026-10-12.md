# Product quality follow-up — 2026-10-12

## Corrected behavior

- Operator API-key issuance for a nonexistent tenant had returned a misleading service-unavailable response when PostgreSQL rejected the tenant foreign key. The store now translates only `api_keys_tenant_fk` violations into a typed missing-tenant failure; the HTTP envelope returns 404 `tenant_not_found`. It neither creates a tenant nor issues a key. Other database failures keep their existing handling.
- A successful paid preflight no longer sets the billing-bypass flag merely because no receipt has arrived. Only the server's explicit `billing_enforced: false` enables that message. A preflight completing after the workspace, principal, service, or units change cannot invoke the previous read or install its quote.
- Chess training exposes status-read and command failures. A lost command response marks status unknown instead of presenting an old idle/running value as confirmed. Refresh recovers the server's actual status; duplicate commands are gated. Each status read owns its cancellation signal, so a read started before a stop cannot be shared by the post-stop refresh or overwrite its result.
- The data UI test now intercepts the raw upload and raw download endpoints actually used by DataView, checks the original body and metadata, and uses the current download control. Its prior JSON-only interception caused the broad UI command to fail before observing any upload.

## Verification

On HART-DESKTOP, in `C:\Users\ahart\Laplace-product-quality`:

- Endpoint test project built successfully. Existing build warnings remain; no errors.
- Eleven VSTest tests passed: four middleware timeout/error-envelope cases, API-key service roundtrip, and six key-mode/authorization cases.
- Frontend production build, including TypeScript compilation, passed.
- `npm --prefix web run test:read-resource`: 51 passed, zero failed/skipped. Three new cases exercise raw Blob admission without JSON materialization, retained receipts when stopping, and unconfirmed transport failures without retry.
- The broad `test:ui` attempt passed its 48 Node cases and workspace UI portion, then failed because the data harness still expected the obsolete JSON upload. The harness was corrected and syntax-checked. The complete composite runner was not rerun; the affected upload/readback interaction was subsequently checked directly in the browser as described below.

On hart-server, the compiled candidate endpoint test assembly ran from `/repos/work/product-quality/endpoint-followup-tests`, against the installed `laplace-mono` PostgreSQL database and native libraries:

- `PostgresBillingStoreContractTests.ApiKeyForUnknownTenant_IsRejectedWithoutIssuingAKey` passed, not skipped. It exercised the real FK rejection and verified no key existed for the nonexistent test tenant.
- The serving application, native installation, database service, and deployment configuration were not replaced by these checks.

## Browser observations

The browser loaded the production React components through Vite on loopback port 5192. Fixture transport intercepted every API call; these are UI/transport observations, not claims that a live training run or paid transaction occurred.

`tests/action-quality.html`:

1. A 250-cent approved quote displayed `2.50 usd — status approved` and invoked the read once, without a bypass claim.
2. Explicit server billing bypass opened the read automatically and displayed the bypass message.
3. Switching workspace while a quote was pending prevented the old response from invoking the read.
4. Lost start/stop responses displayed errors and unknown status. Refresh recovered the fixture's actual running/idle state. Successful commands cleared the action error.
5. Failed initial status read disabled Start; restoring reads and refreshing recovered idle status and enabled Start.
6. A held running-status response was released after Stop had confirmed idle. The UI remained idle, with Start enabled and Stop disabled.

`tests/data-raw.html`:

- Selecting two files caused no upload. Starting sent exactly one raw body with the expected name/path.
- Original bytes included a UTF-8 BOM, case distinctions, CRLF and LF, and a decomposed accent. The fixture compared the uploaded body byte-for-byte.
- Navigating away during the held request stopped the remaining queue. Completing that request retained one admitted receipt and one queued file; no second upload occurred.
- Compact content readback opened, and the separate raw download contained the exact original 22 bytes. Both source and downloaded SHA-256: `2d3aa7bb8fb70ba0ea5f94e3709de056abed082041f8cf0920a06298f3c7f3b3`.
- Switching tenant cleared the prior selection and its admission receipts. The content address in the URL was read again under the new tenant scope.

Regression scenarios are also recorded in `web/e2e/action-quality.spec.ts`; that Playwright spec was not invoked by the CLI during this verification.

Local logs are under `C:\Users\ahart\Laplace-product-audit-20261011`: `endpoint-followup-build.log`, `endpoint-followup-tests.log`, `endpoint-followup-postgres.log`, `action-build.log`, `action-node-tests.log`, and `action-ui-tests.log`.
