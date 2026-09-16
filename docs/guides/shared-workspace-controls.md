# Shared workspace controls

These controls address common interaction defects in the existing web product.
They do not complete the product-wide interface work or supply missing native
operations. Source, query, account and operation semantics remain with their
existing owners. Related work: Laplace #1404/#1374 and Laplace-Refactor
#68/#172/#176/#276/#280/#295.

## Read lifecycle

`useReadResource({ key, read, enabled, refreshMs })` is a view-local read
controller. Include the complete tenant, authority/credential revision and query
scope in `key`. A changed key has no previous body's data even on its first
render. Each controller coalesces concurrent refreshes, passes an AbortSignal,
and fences late results even when a provider ignores abort. It is not a shared
cache, an authorization boundary, a job scheduler or a semantic engine.

`refresh()` joins current work; `reload()` deliberately supersedes it. Polling
waits until the prior attempt settles before starting its interval and pauses
while the document is hidden. Refresh failure keeps the last successful
same-scope data and timestamp. Initial loading, empty success, failure and
stopping the read are separate dispositions. Compose `ReadStatus` with the
actual data, not a whole-page loading branch.

Use `enabled: false` for explicitly submitted reads. Effects such as
subscription, retraction and backend termination are not automatically retried.
“Stop waiting” cancels a transport read; it does not claim a durable server job
was cancelled. Durable job cancellation must use that job's own operation.

Query catalogs/results, billing catalogs/usage, and Activity use this same
controller. API helpers accept `signal` without changing existing options or
serializing the signal into headers. Error responses retain the HTTP status,
server message and available request ID; empty 204 responses are valid.

## Panels, dialogs and forms

`Panel` accepts `expandable` (defaulting to `fill`) and an optional accessible
`label`. Expansion moves the same DOM section into the browser top layer where
supported; the fallback uses a fixed layout. It does not remount the panel,
recreate its editor/canvas, navigate, or rerun a query. Restore and Escape return
focus to the expansion button. Nested native dialogs own their Escape. A panel
inside an expanded panel cannot start a competing expansion.

The existing direct-child layout is preserved. Fill panels have an intrinsic
usable minimum instead of collapsing to a title bar. Long content remains
bounded by its declared scroll container. This is not a promise that every
existing route's unrelated CSS is now corrected.

`Modal` uses native modal dialogs for top-layer placement, focus trapping and
opener restoration. Supply a visible title or a meaningful `label`. Background
Escape handlers are not registered. Long dialogs scroll within the viewport.

`Field` makes help visible and binds a single control's existing ID to its
label, help and error; existing descriptions are retained. Composite controls
with multiple inputs retain responsibility for their individual labels.
`Button` suppresses activation while disabled/loading/visuallyDisabled,
including slotted links and child event handlers. These are interaction guards,
not permission checks.

## Navigation and error recovery

Give a `NavTab` its actual `href`, with optional SPA `onClick`. Copy-link,
modified-click and new-tab behavior use the browser's link semantics. The
section hook retains allowed section choices in search parameters and preserves
unrelated parameters. The Operator sections now survive reload and browser Back.

The application shell retains navigation when a routed view throws. Retry and
navigation recover the view; changing tenant recreates routed local state so
old-tenant results and effect receipts do not carry into the new view. This does
not fix the deployment's missing authorization, nor does it make the tenant
header a security boundary.

## Development checks

```sh
cd web
npm ci
npm run typecheck
npx playwright install chromium
npm run test:ui
```

`test:ui` is the common entry point for production-controller/transport tests,
shared Chromium interaction fixtures, and the existing chess browser checks.
The historical `test:chess-ui` package command delegates to it so the currently
registered browser profile runs the shared regressions too, without a second CI
workflow or another package installation. Direct `node scripts/test-chess-ui.mjs`
still runs only the existing chess checks.

`test:read-resource` substitutes fetch and executes the actual TypeScript
controller/transport. `test:workspace-ui` runs actual React components in an
ephemeral Vite server, with held/delayed fixture responses. It checks scope
changes, polling, independent panes, URL state, exact fields, disabled actions,
retained DOM/editor state, nested dialog focus and expansion at four widths.
Fixture data is never imported into the application entry point. These tests
establish component behavior, not native source admission, live authorization,
whole-product completeness or installed performance. Failed browser traces are
retained at the reported temporary path.
