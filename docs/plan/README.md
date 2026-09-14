# Plan / workstream index

Files under `docs/plan/` are scoped implementation/product campaigns. They are not a second invention specification and no dated plan permanently owns the global work order.

Read [`../README.md`](../README.md), [`../INVENTION.md`](../INVENTION.md), [`../INVENTIONS.md`](../INVENTIONS.md) and [`../../AGENTS.md`](../../AGENTS.md) before treating a plan as active.

A current inventor instruction may change priority/order without invalidating the useful acceptance requirements inside an older scoped plan.

## Conversation / model-consensus product contract

[`REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md`](REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md) is the scoped acceptance contract for conversation, code/tool feedback, heterogeneous-model participation and deterministic realization/export surfaces.

It is **not** the definition of Laplace as a whole. The canonical forward/coupling program is owned by `docs/specs/36_Laplace_Forward_Pass.md` and `37_Substrate_Operation_ISA.md`.

## Execution/workstream coordination

[`EXECUTION_TO_FINISH.md`](EXECUTION_TO_FINISH.md) is a workstream/acceptance index. Historical fixed “finish line 1 → 2 → 3...” ordering has been retired; active scope/order follows the authority stack.

[`WORKSTREAMS.md`](WORKSTREAMS.md) contains the W1–W17 design decomposition. Treat it as decomposition/coverage, not a mandatory global schedule unless current authority explicitly selects it.

## Ingest / decomposer authority

[`DATASET_ESTATE_MODERNIZATION.md`](DATASET_ESTATE_MODERNIZATION.md) covers the selected `/vault/Data` release/artifact/provenance/supersession estate. Its machine-readable companion is [`docs/source-estate.tsv`](../source-estate.tsv).

[`INGEST_BOUNDARY_AND_RECIPE_LAW.md`](INGEST_BOUNDARY_AND_RECIPE_LAW.md) separates artifact, transport, parser/source-object, canonical semantic and persistence boundaries. Source providers recover source structure; the common machine owns canonical admission/identity/composition/dedup under declared recipes and physical-plan invariance.

[`DECOMPOSER_NORMALIZATION_STATUS.md`](DECOMPOSER_NORMALIZATION_STATUS.md) and [`DECOMPOSER_NORMALIZATION_ISSUE_LEDGER.md`](DECOMPOSER_NORMALIZATION_ISSUE_LEDGER.md) are campaign status/issue artifacts. Re-verify their statuses before using them; “vendor composition” means source-specific recovery/mapping, not private canonical identity/composition authority.

All ingestion work also obeys the universal execution-grain law: enumerate/frame at the boundary, use set/batch handoff, execute repeated composition/parsing/normalization work in coarse native operators, and persist/fold in set-sized operations rather than per-element caller loops.

## Substrate cohesion / SQL

[`SUBSTRATE_COHESION_STATUS.md`](SUBSTRATE_COHESION_STATUS.md) and [`SUBSTRATE_COHESION_ISSUE_LEDGER.md`](SUBSTRATE_COHESION_ISSUE_LEDGER.md) are now explicitly historical campaign summaries/indexes. Their detailed August status percentages/order were retired from current authority while retaining their issue/evidence value.

## Delivery / performance

Current performance/capacity evidence law is in [`../benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`](../benchmarks/MANUAL_BENCHMARK_EVIDENCE.md).

Managed-host capacity distinguishes serviceable throughput from explicit saturation. A benchmark that consumes all schedulable resources and makes normal database/product/runner/control-plane operation unavailable is not a valid serviceable-capacity point.

## Plan usage rule

When using any plan in this directory:

1. identify its date/scope;
2. confirm the requirement still agrees with current invention/specs;
3. re-check issue/code/runtime status;
4. preserve useful acceptance/counterexamples;
5. do not inherit stale global priority, completion percentage, finite machine limit, another-repository ownership, or reduced/MVP substitute from the plan.
