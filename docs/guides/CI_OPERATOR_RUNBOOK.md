# CI/CD operator runbook

Issue: #1674

This is the operator-facing inventory. The Actions page should read like a control panel; internal reusable workflows are implementation details and are not manually dispatchable.

| Workflow | Run it when | Normal inputs | Mutates | Resource / concurrency contract | Outputs |
| --- | --- | --- | --- | --- | --- |
| **Product — main delivery** | Never manually. Every product-affecting push to `main` enters this path automatically. | Exact pushed revision; impact plan is derived from the Git diff. | Only the planner-selected install/database/publication state after qualification. | Plan runs hosted. Qualification owns only its exact revision worktree and may be superseded. Delivery reserves the installed-host resource and is non-preemptible once mutation starts. | Impact plan, qualification receipts, exact deployed revision and live verification. |
| **Product — maintenance** | Explicitly reconcile the installed product or deliberately deploy the selected revision outside normal main delivery. | `reconcile` or `deploy`; deploy-only clean/codegen/serial switches. | Reconcile touches installed/database state; deploy performs the full product lifecycle. | Reuses **Internal — product stage** and therefore the same exact-candidate and installed-host reservations as automatic delivery. | Product stage summary and the normal deployment/live-verification receipts. |
| **Product — competitive proof** | Intentionally run the expensive product/model proof. | No normal knobs; it proves the exact dispatched revision. | Candidate install/database state, proof-model outputs, activation. | Ordered reusable stages; stateful stages reserve the installed host. | Product qualification + database proof + synthesized model proof + live activation result. |
| **Audit — full product qualification** | Explicit audit, or the scheduled dependency-graph backstop. | Optional full clean, forced codegen, serial native tests. | No installed-product mutation; qualification only. | Exact-revision build worktree. It deliberately runs the complete build/dev matrix regardless of incremental receipts. | Full qualification result used to catch missing impact/invalidation edges. |
| **Database — maintenance** | Inspect or deliberately maintain the installed database. | `status`, `migrate`, `repair`, `reindex`, `remigrate`, `recreate`; recreate requires two explicit booleans. | Installed database and, for repair/recreate, installed extension/runtime configuration. | Holds the installed-host resource for the operation; it does not rebuild the product. | Operation log and database-health result. |
| **Data — foundation ingest** | Complete the ordered foundation stack or deliberately ingest one foundation source. | Ladder/source choice, database, optional force; raw path is advanced-only. | Substrate/database evidence. | Delegates to **Internal — substrate ingest** under the ingest/host reservation. | Ingest plan, durable journal/gate evidence. |
| **Data — knowledge ingest** | Ingest a registered knowledge/language source. | Source, all/English/custom language scope, database, optional idempotency; advanced path override. | Substrate/database evidence; optional complete-source replacement. | Internal substrate-ingest owner; destructive replacement requires two explicit confirmations. | Ingest plan, journal/gate evidence, optional idempotency receipt. |
| **Data — documents ingest** | Ingest Project Gutenberg or another registered document collection. | Named corpus and database; advanced custom path only when needed. | Substrate/database evidence. | Internal substrate-ingest owner. | Ingest plan and journal/gate evidence. |
| **Data — code ingest** | Ingest Laplace or a registered code authority/corpus. | Ingest mode, named corpus, database; advanced custom path for custom sources. | Substrate/database evidence. | Internal substrate-ingest owner. | Ingest plan and journal evidence. |
| **Data — chess ingest** | Ingest a named chess corpus or run a registered derived chess ingestion operation. | Source, corpus, Lumbras slice, derive-after-games, database, optional idempotency; advanced path override. | Substrate/database evidence. | Internal substrate-ingest owner. Complete-source replacement is only for appropriate pathless derived sources and requires two confirmations. | Ingest plan, source gates, optional derived move outcomes/transitions and idempotency evidence. |
| **Data — models ingest** | Ingest/synthesize an installed model snapshot. | Proof default, Qwen2.5-Coder, TinyLlama, phi-2, or advanced custom snapshot; database; synthesize toggle. | Substrate/model proof outputs. | Internal substrate-ingest owner. Named presets resolve configured or installed snapshots on the runner. | Ingest/verification result and, when requested, synthesized GGUF. |
| **Observe — API** | Capture evidence about the currently running API. | Base URL. | Nothing in the product; evidence only. | Read-only diagnostic work under the host reservation so evidence is not collected while the deployment is being mutated. | 30-day diagnostic artifact plus summary; collector failure is preserved and then reported. |
| **Observe — UI** | Capture screenshots/browser diagnostics for the running UI. | Base URL and optional route selection. | Nothing in the product; tooling cache only. | Read-only diagnostic work under the host reservation. | 30-day UI evidence artifact/screenshots plus summary; collector failure is preserved and then reported. |
| **Measure — benchmark** | Produce deliberate versioned performance/calibration evidence. | Target ref, suite, repeats, scale controls and optional corpus overrides. | No product deployment, but may prepare/build the exact benchmark revision. | Owns the quiet measured host for the whole benchmark so results are not contaminated by concurrent product/ingest work. | 90-day versioned benchmark artifact, manifest and summary. |
| **Policy — CI contract** | Normally automatic on CI/policy changes; manual dispatch is only for policy verification. | None. | Nothing. | GitHub-hosted; never consumes the Laplace self-hosted runner. | Fast static workflow/lifecycle/operator contract result. |

## Internal workflows

**Internal — product stage** is the reusable exact-candidate execution primitive. It is not a second operator UI. It owns per-revision worktrees, candidate locks, freshness checks, the installed-host reservation for stateful stages, and passes the planner-selected build/test/delivery scope into `product-ci.sh`.

**Internal — substrate ingest** is the single ingest mutation owner. Domain workflows resolve friendly presets into the low-level source/path/language/model inputs and then call it. It is not manually dispatchable.

## Normal source-to-product story

A product-affecting push is intentionally boring:

```text
Plan affected product work
  -> Qualify affected product inputs
  -> Install, validate, publish, and activate exact candidate
```

The planning step decides what actually changed. Qualification builds only the affected candidate components and reruns only invalidated suites; a content-addressed qualification receipt may reuse a prior pass when the suite's production/test/toolchain inputs are unchanged. Delivery then performs only the planner-selected stateful operations and always retains a small live verification floor.

The weekly/manual full-qualification audit deliberately ignores those savings. It exists to prove that the impact graph itself has not missed a dependency.

## What is deliberately not in the Actions operator surface

Low-level stages such as `build`, `install`, `test-dev`, `test-db`, `test-live`, `applications`, `check`, and `provision` remain available to internal scripts and reusable orchestration where needed. They are not separate operator choices because they are implementation stages, not end goals.
