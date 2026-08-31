# Operational maintenance and CI/CD authority

This document is the repository-owned source of truth for maintaining the legacy Laplace implementation. It exists so the operating contract is not left in chat history, a local agent transcript, or a scheduled-task prompt that can silently become stale.

The coordinating GitHub story is [#1373](https://github.com/SaltyPatron/Laplace/issues/1373).

## Operator categories

All human-facing operations converge on five categories:

1. **release** — policy, dependency proof, build, DEV/BAT, install, migrate/sync, publish, activate, and deployment-health verification;
2. **database** — status, migrate, repair, reindex, remigrate, recreate, and seed-independent health;
3. **seed** — plan, run, resume, or evict/rederive by governed profile or source;
4. **verify** — database QA, seeded/live product QA, semantic evaluation, source fidelity, and explicit performance proof;
5. **maintenance** — runner, host, dependency, repository-hygiene, and diagnostic operations.

An action has one implementation and one authority boundary. GitHub Actions, Linux aliases, Windows wrappers, UI, API, MCP, and scheduled maintenance must call that implementation rather than reproduce it.

## Current completed boundaries

- [#1367](https://github.com/SaltyPatron/Laplace/issues/1367) is complete: verified application publication and activation occur before environment QA on the single self-hosted runner. Post-delivery QA may remain red evidence but cannot roll back a deployment-health-verified payload.
- [#1375](https://github.com/SaltyPatron/Laplace/issues/1375) is complete through PR [#1385](https://github.com/SaltyPatron/Laplace/pull/1385): one machine-readable test registry selects `policy`, native/managed DEV/BAT, database QA, seeded/live product, and performance profiles before execution and emits exact receipts.
- PR validation is isolated from the persistent main workspace and deployment concurrency through [#1383](https://github.com/SaltyPatron/Laplace/pull/1383).

These boundaries are not permission to weaken failing tests. They state which operation a failure owns.

## Active technical-debt graph

- [#1374](https://github.com/SaltyPatron/Laplace/issues/1374) — replace overlapping seed workflows and path resolvers with one manifest-driven seed pipeline.
- [#1376](https://github.com/SaltyPatron/Laplace/issues/1376) — introduce the governed `laplace ops` control plane and make wrappers thin.
- [#1377](https://github.com/SaltyPatron/Laplace/issues/1377) — replace persistent shared-workspace coupling with immutable build receipts and explicit runner roles.
- [#1378](https://github.com/SaltyPatron/Laplace/issues/1378) — make one machine-readable operation receipt authoritative across Actions, CLI, UI, API, and MCP.
- [#1080](https://github.com/SaltyPatron/Laplace/issues/1080) / PR [#1379](https://github.com/SaltyPatron/Laplace/pull/1379) — move ingest throughput comparison out of an Actions-only log parser and into the shared run-journal path.
- [#1370](https://github.com/SaltyPatron/Laplace/issues/1370), [#1371](https://github.com/SaltyPatron/Laplace/issues/1371), and [#1372](https://github.com/SaltyPatron/Laplace/issues/1372) — repair the consensus/attestation merge planner cliffs and expose live fold ownership, progress, and stalls.
- [#929](https://github.com/SaltyPatron/Laplace/issues/929), [#951](https://github.com/SaltyPatron/Laplace/issues/951), [#958](https://github.com/SaltyPatron/Laplace/issues/958), and [#1175](https://github.com/SaltyPatron/Laplace/issues/1175) remain supporting owners for source capability, one implementation per operation fact, lifecycle-versus-ingest exclusion, and LapSight observability.

Do not open a duplicate PR for a path already owned above. Verify the diagnosis and current branch before acting.

## Maintainer run contract

Every maintenance run performs this sequence.

### 1. Refresh repository state

Read the complete open issue and pull-request inventory, not a hand-picked sample. Record:

- triage state, priority, labels, milestone, area/module, age, and security/durability/performance/product/CI classification;
- explicit and inferred blocking relationships;
- open branches or PRs already owning the same code path;
- whether an old diagnosis still reproduces on current `main`, the installed runtime, or the current database.

Sample old backlog items as well as recent failures. The loudest screenshot or workflow failure does not define repository scope.

### 2. Audit Actions before changing code

Inspect current main and exact-head PR runs. A green job is accepted only when its executable receipt proves the work actually ran. Required profiles or comparisons with zero selected/executed/compared items fail.

Check for:

- skipped deployment caused by unrelated QA;
- false green checks, stale artifacts, or shared-workspace residue;
- workflow/job names that no longer match their authority;
- duplicate selectors or mutation paths outside the governed registry;
- self-hosted runner ordering and lifecycle/ingest collisions.

### 3. Choose one bounded owner

Select the highest-impact actionable problem not already being repaired. Verify its present-code root cause before editing. Follow the failure through architecture, data model, decomposition, ingest/apply/fold/durability, read/inference paths, services/UI, security, tests, and CI/CD as relevant.

### 4. Implement through the repository path

Use a new branch and the repository's CI/CD contracts. Add production-path regression tests and acceptance evidence. Do not directly mutate production merely to make a check pass, re-baseline broken behavior upward, raise timeouts in place of diagnosis, or replace Laplace's content-addressed substrate with a workaround.

Delete the superseded implementation when parity is proven. A new wrapper beside the old implementation is not consolidation.

### 5. Finish the GitHub and documentation loop

Before ending the run:

- update the owning issue with current reproduction, root cause, affected components, dependencies, implementation, and exact acceptance;
- update the coordinating epic or dependency issue;
- update repository documentation when an authority boundary, operator command, receipt, or pipeline category changes;
- make the PR body state what is complete and what remains outside scope;
- audit the exact-head Actions result.

If implementation is blocked, the issue update is the deliverable. It must be precise enough for the next run to continue without reconstructing the investigation.

## PR completion law

A pull request marked ready to merge means:

- every acceptance item in its body is complete;
- exact-head required CI is green;
- real self-hosted runtime evidence exists where the change affects install, database, service, or deployment behavior;
- no known cleanup is hidden as prose in the PR;
- no superseded active path remains unless a separate removal issue and dependency are explicit;
- no duplicate/conflicting PR owns the same implementation;
- the issue and documentation are current.

Incomplete work remains draft. A draft with completed exact-head proof must be promoted or replaced immediately; it must not be abandoned because a tooling mutation failed.

## Scheduled maintainer task text

The scheduled maintainer must use the following scope. This section is the canonical text to copy into the scheduler whenever the task is created or corrected.

> Work on `SaltyPatron/Laplace` as an active maintainer across the entire legacy iteration. At the start of every run, refresh the complete inventory of all open issues and pull requests, including old backlog items, labels, milestones, dependencies, age, priority, affected area, current-main reproducibility, blocking relationships, and whether another branch or PR already owns the work.
>
> The immediate priority is technical debt and ease of operation through CI/CD. Treat #1373 and its active children as the coordinated work graph, together with supporting source-capability, lifecycle, throughput, planner, and observability issues.
>
> Audit current GitHub Actions results before changing code. Verify that checks prove the behavior their names claim, that required profiles execute nonzero work, and that false-green, stale-artifact, skipped-deployment, and shared-workspace failures are detected.
>
> Converge the operator surface on `release`, `database`, `seed`, `verify`, and `maintenance`. Replace overlapping workflow, shell, Justfile, Windows, UI, API, and MCP implementations with one governed operation catalog and executor. Delete superseded paths after parity proof rather than retaining indefinite compatibility variants.
>
> Preserve the executable distinction between DEV/BAT, database QA, seeded/live product acceptance, semantic evaluation, and performance. Database health is seed-independent. Verified application publication and activation occur before environment QA; downstream QA may remain red evidence but cannot withhold or roll back a healthy payload.
>
> Replace persistent `_work` residue and mtime assumptions with immutable source-SHA/content-addressed artifact receipts. Every operation emits one authoritative receipt stating what ran, what changed, selected/executed/skipped/compared counts, artifact/runtime/database/source identity, timings, terminal status, and reason. A required verifier that performed zero work fails.
>
> Prefer real production-path unit, PostgreSQL integration, performance, and activation tests over source scanners and helper-only proxies. Static gates are supplementary and require mutation evidence.
>
> Implement one focused change on a new branch, use the self-hosted `laplace-runner` CI/CD path, update the owning and coordinating issues plus repository documentation, and keep the pull request draft until every stated acceptance item is complete. A ready PR means the work is complete.
>
> If implementation is blocked, update or create a precise issue with current reproduction, root cause, affected components, dependencies, implementation sequence, and exact acceptance. Do not divert into unrelated explanations while the active scope is CI/CD, documentation, issue hygiene, and operational technical debt.

## End-of-run receipt

Every maintainer run ends with only these states:

- **DONE** — merged commit/PR, closed issue, and exact acceptance receipt;
- **BLOCKED** — exact unavailable capability or dependency and the updated GitHub owner;
- **FAILED** — exact failing command/job and the updated diagnosis.

Plans and descriptions are not completion receipts.