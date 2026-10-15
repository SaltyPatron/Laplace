# CI/CD operator runbook

Issue: #1674

This is the operator-facing inventory. The Actions page should read like a control panel; internal reusable workflows are implementation details and are not manually dispatchable.

| Workflow | Run it when | Normal inputs | Mutates | Resource / concurrency contract | Outputs |
| --- | --- | --- | --- | --- | --- |
| **Product — main delivery** | Product-affecting pushes to main; manual replay through Product — maintenance. | Exact selected revision; optional production promotion and explicit cloud qualification. | Builds, installs and migrates staging; publishes the verified package to production. | Normal runner checkout, shared host lock, queued promotions. Cloud remains off unless explicitly requested. | Installed CLI/API/MCP/native readback, database lifecycle checks and production package. |
| **Product — maintenance** | Inspect, reconcile, deploy or update chess tools on the selected environment. | Operation and target declared by the workflow. | Selected installed lifecycle operation. | Target runner and setup-owned restart triggers; no runner sudo. | Host status, operation log and installed readback. |
| **Product — competitive proof** | Explicit expensive product/model qualification on staging. | Exact dispatched source; model, corpus and runtime paths from machine configuration. | Qualification, candidate installation, existing database migration/tests, model admission/synthesis, authenticated live operations. | One normal runner checkout and one host reservation for the whole operation. No production promotion or cloud startup. | Component logs, DB results, model behavior report and installed live result; failures remain failures. |
| **Audit — full product qualification** | Explicit source qualification; no schedule. | Optional clean build, forced codegen, serial native tests. | Builds every native/managed/web component and runs native, managed, UCI and browser development suites. Installed services are not replaced. | Configured build/work directories, normal runner checkout, same host reservation as delivery. | All component results, selected source revision and installed status before/after. |
| **Database — maintenance** | Inspect or deliberately maintain the installed database. | `status`, `migrate`, `repair`, `reindex`, `remigrate`, `recreate`; selecting `recreate` is the explicit destructive authorization. | Installed database and, for repair/recreate, installed extension/runtime configuration. | Holds the installed-host resource for the operation; it does not rebuild the product. | Operation log and database-health result. |
| **Data — foundation ingest** | Complete the ordered foundation stack or deliberately ingest one foundation source. | Ladder/source choice, database, optional force; raw path is advanced-only. | Substrate/database evidence. | Delegates to **Internal — substrate ingest** under the ingest/host reservation. | Ingest plan, durable journal/gate evidence. |
| **Data — content ingest** | Ingest a registered content/language source. | Source, all/English/custom language scope, database, optional idempotency; advanced path override. | Substrate/database evidence; optional complete-source replacement. | Internal substrate-ingest owner; destructive replacement requires two explicit confirmations. | Ingest plan, journal/gate evidence, optional idempotency receipt. |
| **Data — documents ingest** | Ingest Project Gutenberg or another registered document collection. | Named corpus and database; advanced custom path only when needed. | Substrate/database evidence. | Internal substrate-ingest owner. | Ingest plan and journal/gate evidence. |
| **Data — code ingest** | Ingest Laplace or a registered code authority/corpus. | Ingest mode, named corpus, database; advanced custom path for custom sources. | Substrate/database evidence. | Internal substrate-ingest owner. | Ingest plan and journal evidence. |
| **Data — chess ingest** | Ingest a named chess corpus or run a registered derived chess ingestion operation. | Source, corpus, Lumbras slice, derive-after-games, database, optional idempotency; advanced path override. | Substrate/database evidence. | Internal substrate-ingest owner. Complete-source replacement is only for appropriate pathless derived sources and requires two confirmations. | Ingest plan, source gates, optional derived move outcomes/transitions and idempotency evidence. |
| **Data — models ingest** | Ingest/synthesize an installed model snapshot. | Proof default, Qwen2.5-Coder, TinyLlama, phi-2, or advanced custom snapshot; database; synthesize toggle. | Substrate/model proof outputs. | Internal substrate-ingest owner. Named presets resolve configured or installed snapshots on the runner. | Ingest/verification result and, when requested, synthesized GGUF. |
| **Observe — API** | Capture evidence about the currently running API. | Base URL. | Nothing in the product; evidence only. | Read-only diagnostic work under the host reservation so evidence is not collected while the deployment is being mutated. | 30-day diagnostic artifact plus summary; collector failure is preserved and then reported. |
| **Observe — UI** | Capture screenshots/browser diagnostics for the running UI. | Base URL and optional route selection. | Nothing in the product; tooling cache only. | Read-only diagnostic work under the host reservation. | 30-day UI evidence artifact/screenshots plus summary; collector failure is preserved and then reported. |
| **Measure — benchmark** | Produce deliberate versioned performance/calibration evidence. | Target ref, suite, repeats, scale controls and optional corpus overrides. | No product deployment, but may prepare/build the exact benchmark revision. | Owns the quiet measured host for the whole benchmark so results are not contaminated by concurrent product/ingest work. | 90-day versioned benchmark artifact, manifest and summary. |
| **Policy — CI contract** | Normally automatic on CI/policy changes; manual dispatch is only for policy verification. | None. | Nothing. | GitHub-hosted; never consumes the Laplace self-hosted runner. | Fast static workflow/lifecycle/operator contract result. |

## Internal workflows

**Internal — product stage** is shared by full and competitive qualification. It checks out the exact selected revision in the normal staging runner workspace and calls `deploy/linux/site.sh qualification` or `competitive`. It neither creates per-revision Git worktrees nor injects provider/billing secrets. Competitive live checks obtain a temporary API key from the installed deployment identity and revoke it afterward.

**Internal — substrate ingest** is the single ingest mutation owner. Domain workflows resolve friendly presets into the low-level source/path/language/model inputs and then call it. It is not manually dispatchable.

Installation prepares native and CLI payloads on the build filesystem before
publishing them to `/opt/laplace`. Serving caches and extension modules are not
deleted in advance. Native replacement uses delayed renames; a failed copy leaves
the previous files intact. Ingest releases preserve library symlinks and share
unchanged files with the previous immutable runtime. Old content-addressed
PostgreSQL modules remain available for databases that still reference them.

Seed and deployment workflows use the same run heartbeat/advisory-beacon predicate
as Operator. A fresh heartbeat or held beacon keeps a run live. Recovery cancels a
run and its unfinished file rows only when both witnesses are absent/stale, retaining
all committed substrate content.

## Normal source-to-product story

Platform access inspection uses **Product — maintenance**, operation `access-audit`,
target `staging` or `production`. The existing installation credential reads the
people, role-event and provider-client endpoints. The report contains aggregate
roles, providers and transitions, anonymous-denial results, response limits, and
the collector and installed revisions. Account names, email addresses, provider
subjects, client IDs and credentials are excluded from public logs and artifacts.
These aggregates do not establish an owner's exact identity binding or exercise
a browser session. The operation does not assign roles or change service accounts.

Changes to this observer or its maintenance workflow run the observer tests first,
then read both installed application environments under their shared host locks.
This validates the observer against deployed APIs without rebuilding or activating
application packages. Cloud is not an application-audit target.

Initial administrator enrollment uses `access-enroll`, target `staging`, and a
reviewed `.github/access-requests/staging-*.json` request. Merging one new or changed
request also executes it. Changes to maintenance code alone only run the access
audits. Deploy the API's `initialRoleAssignment` capability before adding a request;
workflow concurrency alone does not order separate deployment and enrollment runs.

The request selects existing, previously signed-in Google/Microsoft bindings with
a SHA-256 verification fingerprint over six UTF-8 fields, each preceded by its
unsigned 8-byte big-endian byte length: target, role, random 64-hex nonce, provider,
issuer, subject. This fingerprint verifies the selected request; it is not a new
identity, a Laplace content identity, or a source of authority. The explicit operator
request authorizes the assignment. Raw subjects, local account IDs and credentials
must not be committed to the public repository. The helper never matches by email
and never creates or merges accounts.

Under the exclusive installed host lock, the helper reads the declared application
database, checks the serving API agrees, then calls the existing audited role API
with `onlyIfNeverAssigned: true`. The server checks current role and all prior role
events atomically. Existing or revoked roles are not initial enrollment candidates.
A private journal under the persistent declared work root is written before each
request. Reruns verify recorded assignments and may continue selections that were
never attempted. A lost response is reconciled through audit readback; pending
transmissions are never repeated. Preserve this
journal across application database recovery. Failed partial requests report failure
and retain the journal; they do not undo already committed, authorized grants.
Only reduced provider/result receipts are public. Browser sessions remain a separate
acceptance check. This operation neither promotes application packages nor starts
hart-cloud and is not a cross-environment authority synchronization policy.

Persistent installation owners are an explicit, separate policy. `owner-enroll`
accepts reviewed `.github/owner-requests/{staging,production}-*.json` requests with
`kind: persistent-installation-owners` and the same exact provider selectors, plus
`client_id_sha256` for each independently reviewed current OAuth registration.
Deploy the `persistentInstallationOwners` API capability before submitting a request.
The helper requires existing authenticated bindings, audited administrator grants,
and matching serving OAuth client registrations. It writes the literal bindings,
client IDs and recorded authentication times only to the private host file
`LAPLACE_WORK_ROOT/platform-owners/policy.json`. It then uses the existing configure
and restart lifecycle and verifies that the serving API identifies both accounts
as installation owners. Public receipts contain only provider names and results.

Application migration, repair, recreation, startup and validated sign-in reconcile
that policy. A recreated database gets new local relational handles for those exact
provider bindings, administrator grants and audit events; it gets no passwords,
tokens or browser sessions. Administrator includes operator permissions. Existing
accounts retain their profiles, workspace membership and history. Policy-owned
accounts cannot be demoted in the role UI/API; removing owner authority requires an
explicit change to the private installation policy. Other users keep normal role
assignment and revocation behavior. Reconciliation never matches email addresses.

Preserve the private owner file across database recreation and machine backup.
`LAPLACE_PLATFORM_OWNER_POLICY_FILE` can explicitly select another absolute private
file. A mismatched OAuth client or malformed policy fails before database replacement.
No policy means no automatic owner grant; the migration command reports that state.
CI runs the real `app-up`, `app-check` and `app-nuke` commands against an isolated
PostgreSQL fixture to verify restoration, historical login timestamps, idempotent
audit events, and the absence of restored sessions.

A product-affecting push is intentionally boring:

```text
Plan affected product work
  -> Qualify affected product inputs
  -> Install, validate, publish, and activate exact candidate
```

Main delivery uses the configured site lifecycle. Explicit qualification builds every source component; it does not substitute old component receipts for execution.

Run source qualification as a normal installed-host user with `bash deploy/linux/site.sh qualification`, or dispatch **Audit — full product qualification**. Run `bash deploy/linux/site.sh competitive` only when full model/corpus admission and live product qualification are intended. Neither command creates a PostgreSQL instance.

The full-qualification workflow accepts `model_hub` for an existing model corpus directory. Without that input it uses the host's `HF_HUB_CACHE`; it does not download a replacement corpus or assume a particular drive layout.

For installed chess tools, run **Product — maintenance**, operation `chess-lab`, on the selected environment, or run `bash deploy/linux/site.sh chess-lab` there. Linux setup uses an explicit `LAPLACE_FASTCHESS` executable when declared. Otherwise it verifies the pinned release under `LAPLACE_DEPS` (or `LAPLACE_DEPS_PREFIX`, then the installation's `tools` directory), downloading it only when missing. The API and CLI receive the same selected executable. Declare `LAPLACE_CHESS_LAB_DIR` to choose job storage; its default follows `LAPLACE_API_STATE_DIR`, so the API's service identity owns its recorded games and transcripts. Compiler scratch and game storage are separate settings.

## What is deliberately not in the Actions operator surface

The site lifecycle owns build, install, database, publication and verification. `scripts/test-parallel.sh` retains the native/managed/browser/database/live suite definitions used by explicit qualification.
