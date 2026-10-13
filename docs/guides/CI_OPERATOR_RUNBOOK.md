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

A product-affecting push is intentionally boring:

```text
Plan affected product work
  -> Qualify affected product inputs
  -> Install, validate, publish, and activate exact candidate
```

Main delivery uses the configured site lifecycle. Explicit qualification builds every source component; it does not substitute old component receipts for execution.

Run source qualification as a normal installed-host user with `bash deploy/linux/site.sh qualification`, or dispatch **Audit — full product qualification**. Run `bash deploy/linux/site.sh competitive` only when full model/corpus admission and live product qualification are intended. Neither command creates a PostgreSQL instance.

## What is deliberately not in the Actions operator surface

The site lifecycle owns build, install, database, publication and verification. `scripts/test-parallel.sh` retains the native/managed/browser/database/live suite definitions used by explicit qualification.
