# CI/CD operator model

Issue: #1674

## Purpose

Laplace CI/CD has two distinct jobs:

1. turn an exact source revision into a correctly running product; and
2. expose deliberate operator operations such as ingest, database maintenance, observability, benchmarks, and proof runs.

Those jobs should share reusable implementation primitives, but they should not share one confusing operator surface.

The operator model is therefore:

- **automatic product delivery** is one dependency-aware daisy chain;
- **manual workflows** represent meaningful operations an operator intentionally asks for;
- **internal reusable workflows** are implementation details, clearly named `Internal — ...`;
- **historical/one-off workflows** are not permanent operator controls.

## Canonical product delivery

The primary product workflow should mean one thing:

> Take this exact repository revision from source to a correctly running Laplace product, doing only the work invalidated by this revision.

The target graph is:

```text
Plan / invalidate
  -> Build affected components
  -> Qualify affected components
  -> Assemble exact candidate
  -> Apply required install/schema/application mutations
  -> Publish
  -> Live qualification
  -> Working-product receipt
```

The graph shown in GitHub Actions should read left-to-right like that. Manual build/install/test stages do not belong as mutually exclusive branches inside the same top-level delivery workflow.

## Incremental qualification

Incremental compilation is not enough. Laplace needs **incremental qualification**.

For each product component, retain enough identity to answer:

- what exact production inputs produced this artifact?
- what generated inputs were consumed?
- what dependency artifact digests were consumed?
- what toolchain/runtime identity produced it?
- what relevant configuration was active?
- what exact test definition qualified it?
- what qualification result was produced?

A component can be reused when that effective identity is unchanged. A change invalidates that component and the downstream components that actually depend on it.

This means a web-only change should not rebuild PostgreSQL extensions, and a native-only change should not reinstall unrelated web dependencies unless the declared dependency graph says it must.

A full qualification remains available explicitly and on a periodic audit cadence. Its purpose is to verify the dependency model itself and catch missing invalidation edges.

Build-system caches are acceleration only. They are not the source of truth for qualification. Candidate artifacts and qualification receipts need explicit identities.

Relevant platform/build references:

- GitHub manual workflow inputs: https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#onworkflow_dispatch
- GitHub reusable workflows: https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows
- GitHub dependency caching: https://docs.github.com/en/actions/concepts/workflows-and-actions/dependency-caching
- CMake Ninja generator: https://cmake.org/cmake/help/latest/generator/Ninja.html
- MSBuild incremental builds: https://learn.microsoft.com/en-us/visualstudio/msbuild/incremental-builds

## Operator-facing manual workflows

The Actions UI is a control panel, not an implementation inventory.

The intended manual surface is:

| Workflow | Why an operator runs it |
| --- | --- |
| **Data — foundation ingest** | Complete or deliberately rebuild the ordered foundation signal stack. |
| **Data — knowledge ingest** | Ingest one registered linguistic/knowledge source, optionally scoped by language. |
| **Data — documents ingest** | Ingest a named document collection such as Project Gutenberg or test fixtures. |
| **Data — code ingest** | Ingest this repository, registered authority repositories, Stack/Tiny Codes, or an explicit advanced corpus. |
| **Data — chess ingest** | Ingest named game/opening/book/tablebase corpora or run chess-derived ingestion operations. |
| **Data — models ingest** | Select an installed model snapshot by name and ingest/synthesize it. |
| **Database — maintenance** | Perform explicit database lifecycle operations. |
| **Observe — UI** | Capture UI screenshots/browser evidence against a selected deployment. |
| **Observe — API** | Capture API diagnostics/evidence against a selected deployment. |
| **Measure — benchmark** | Produce versioned benchmark evidence under an explicit measurement reservation. |
| **Product — competitive proof** | Run the expensive competitive/product proof intentionally. |

### Manual workflow contracts

| Workflow | Primary inputs | Mutates | Resource ownership / concurrency | Durable result |
| --- | --- | --- | --- | --- |
| **Data — foundation ingest** | ladder/source, database, force, advanced path override | canonical substrate data | reusable ingest owner; database/ingest host reservation | journal + source/layer gate evidence |
| **Data — knowledge ingest** | source, language scope, database, optional replacement/idempotency | selected canonical source | reusable ingest owner; database/ingest host reservation | journal + consensus/layer gate evidence |
| **Data — documents ingest** | named collection, database, optional advanced path | document evidence | reusable ingest owner; database/ingest host reservation | journal + document gate evidence |
| **Data — code ingest** | ingest mode, named corpus, database, optional advanced path | code/repository evidence | reusable ingest owner; database/ingest host reservation | journal evidence and configured gates |
| **Data — chess ingest** | source, named corpus, Lumbras slice, derived work, replacement/idempotency | chess evidence/derived layers | reusable ingest owner; database/ingest host reservation | journal + chess gate evidence |
| **Data — models ingest** | named installed model, database, synthesize toggle | model evidence and optional GGUF output | reusable ingest owner; database/ingest host reservation | model journal/gate and optional synthesized model |
| **Database — maintenance** | explicit operation and destructive confirmation where required | installed database | serialized installed-database/host mutation | operation result and database checks |
| **Observe — UI** | base URL, routes | no product state | explicit read-only evidence run; browser/tooling state is separate from product build state | screenshots/browser diagnostics artifact |
| **Observe — API** | base URL | no product state | explicit read-only evidence run | API diagnostics artifact |
| **Measure — benchmark** | exact ref, suite, repeats/scale/corpus options | no product/database state by default; reserves quiet host | exclusive measurement reservation | versioned benchmark evidence artifact |
| **Product — manual operation** | one explicit maintenance operation plus build/test toggles | varies by chosen operation | reusable product-stage resource policy | stage result / installed-product receipt as applicable |
| **Product — competitive proof** | exact selected revision | candidate/install/product proof state | composed product-stage ownership; intentionally expensive | competitive proof/model evidence plus live verification |
| **Audit — full product qualification** | optional clean/codegen/serial toggles | candidate build state only, not installed product | weekly/manual read-only qualification; no installed-host mutation lock | complete source-build/test qualification receipts |

The automatic **Product — main delivery** workflow is not a manual control. It owns the single `Plan → Qualify → Deliver` source-to-running-product story. `Internal — product stage` and `Internal — substrate ingest` are reusable implementation owners and expose no normal manual dispatch surface.

The following are not normal operator controls:

- repository contract validation;
- reusable product stages;
- reusable substrate-ingest mutation machinery;
- temporary repair/reproduction workflows.

They may exist as workflows, but should be clearly named as policy or `Internal — ...` and should not expose a second low-level manual form.

## Manual ingest UX

GitHub supports typed `workflow_dispatch` inputs such as `choice` and `boolean`, but it does not provide dynamic dependent dropdowns or a server filesystem picker. The right response is not to make the operator type server paths.

The ingestion UI rules are:

1. **Named presets first.** Common datasets are dropdown choices.
2. **Paths resolve on the runner.** The workflow maps a preset to the mounted path.
3. **Advanced path override is an escape hatch.** It stays blank for normal operation and is labelled accordingly.
4. **No decomposer-name confirmation puzzle.** Destructive source replacement uses two explicit booleans; the workflow resolves the decomposer internally.
5. **Useful defaults.** A normal run should require changing only the choice that identifies what the operator wants.
6. **No invalid Cartesian products where avoidable.** Domain-specific workflows own domain-specific choices.
7. **Plan before mutation.** The run summary records revision, source, resolved input, database, language scope, replacement flags, gates, idempotency, and derived work before ingestion starts.
8. **One mutation owner.** All wrappers delegate to the same reusable substrate-ingest workflow.

### Foundation

Normal controls:

- source: ordered ladder or one registered foundation source;
- database;
- force every ladder rung.

A custom path is advanced-only. The default is the registered source location.

### Knowledge

Normal controls:

- source;
- language scope: all / English / custom;
- database;
- optional idempotency proof.

Replacement of a complete source requires two explicit confirmations. A path override is advanced-only.

### Documents

Normal controls:

- Project Gutenberg;
- fixtures;
- electronics fixtures;
- Alice smoke input;
- custom path.

The operator selects the collection, not `/vault/Data/...`.

### Code

Normal controls:

- ingest mode;
- named corpus: Laplace, CPython, PostgreSQL, .NET runtime, .NET docs, Tree-sitter, or registered Stack/Tiny Codes;
- database.

The workflow resolves the actual checkout or `/vault/Data/code-authority/...` path. Custom code/repo/tabular/recipe inputs keep an advanced path override.

### Chess

Chess already has useful configuration and should retain it:

- source/derived operation;
- named corpus;
- Lumbras era slice;
- derive-after-games;
- idempotency;
- explicit complete-source replacement.

The common corpus choices resolve their paths internally. The raw path field is advanced-only.

### Models

Normal controls are model names, not Hugging Face cache directories:

- proof default;
- Qwen2.5-Coder;
- TinyLlama;
- phi-2;
- custom path.

The runner resolves configured environment paths first and may fall back to installed `/vault/models/<family>/snapshots/*` snapshots.

## Resource ownership

Workflow modularity is incomplete unless resource ownership is modular.

The eventual product/ingest execution model should distinguish at least:

- source/build workspace;
- database mutation;
- ingestion/database-write work;
- deployment/application mutation;
- quiet-host measurement;
- read-only observability.

A long ingest must not block source-only build/test work merely because both happen on the same physical machine. Locks and runner labels should represent actual conflicting resources.

## Rollout order

1. Clean manual ingestion operator interfaces without changing the underlying mutation owner.
2. Classify every surviving workflow as automatic policy, operator interface, or internal primitive.
3. Clean stale historical workflow identities/runs after preserving any artifacts worth retaining.
4. Introduce the product invalidation planner and qualification receipts.
5. Convert the primary product workflow into the single source-to-working-product daisy chain.
6. Split runner/resource ownership so unrelated work can execute concurrently.
7. Add periodic full qualification as an audit of incremental invalidation correctness.

## Acceptance properties

The system is in the intended state when:

- an operator can run common ingests without knowing a runner filesystem path;
- the Actions form uses choices/booleans for ordinary decisions;
- each manual workflow describes one meaningful operation;
- the primary product graph has one start-to-finish story;
- a change only rebuilds/retests the components it invalidates;
- prior qualification is reused only when its full effective identity still matches;
- stateful mutation begins only after the exact candidate is qualified;
- every run explains what it plans to do before doing it.
