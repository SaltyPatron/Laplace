# Laplace current task index

This file is a navigation/status index. It is **not** invention authority and it does not impose a fixed global execution order.

Authority is defined by `AGENTS.md`, `docs/README.md`, `docs/INVENTION.md`, `docs/INVENTIONS.md`, the binding specs, and the current inventor request. GitHub issues own bounded implementation/acceptance work; code/runtime/CI prove implementation state.

Historical recovery diaries, database snapshots, old branch state, and cross-repository coordination plans must not be treated as current truth merely because they once appeared in this file.

## Current high-value obligations

### Live host revision coherence (hart-server)

Observed 2026-09-19. `origin/main` is `e7b4ec7f`. `/opt/laplace/current` still names a 2026-09-17 release. `/opt/laplace/lib` received a 2026-09-18 23:45 cmake install (T0 v4, `laplace_execution_526daf316a640d4b.so`) that aborted during `ALTER EXTENSION`. PostgreSQL functions still bind `laplace_execution_e080f3990ea9221a` (file absent). `/opt/laplace/app` still ships 2026-09-17 `liblaplace_core`, so the API rejects the v4 T0 blob and turn-witness stays offline.

`Product — main delivery` often reports success while skipping install. Qualification is one self-hosted job with a 15-minute `managed-dev` test deadline (`LAPLACE_MANAGED_TEST_TIMEOUT`). `/build` is at capacity (~121G under `/build/laplace/build`).

Next actions: restore one deployed revision (application, prefix libs, SQL MODULE, T0), then make `/health/ready` and `check-deployed-revision.sh` fail closed on split state. Do not treat Laplace-Refactor as the live product.

### Forward-pass completion

Primary current issue: #1401.

The governing forward contract is:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

The complete admitted observation must be able to tug every eligible indexed plane before a default sense/provider mask freezes the interpretation. A*, walk, KNN, trajectory continuation, chess search, relation reads and calculators are operators inside this program, not separate cognition engines.

Current code/runtime status must be re-read before changing implementation; dated issue comments and archived forward-path gap reports are not sufficient.

### Benchmark / capacity evidence

Owners include #1432, #1436 and #1451.

The managed-host benchmark workflow now has source changes on `docs/canonical-invention-contract-20260914` that:

- derive an explicit serviceable scale plan before measurement;
- reserve two logical CPUs by default on the managed runner;
- resolve the known 6C/12T host to `1,2,3,4,6,8,10` by default;
- reject scale points above the serviceable ceiling unless `allow_saturation=true` is explicitly selected;
- write `scale-plan.json` and expose the resolved worker/mode in the evidence summary;
- retain full logical-CPU saturation as a separate opt-in experiment.

Actions run `34823625126` remains an incomplete/failing counterexample from the prior all-logical-CPU default. Do not invent a more specific terminal cause without evidence.

The next proof step is CI/source validation plus a new serviceable `scale`/`throughput` dispatch on the fixed revision. #1451 separately owns single-semantic-DAG frontier parallelism; replicated streams are not that proof.

### Bounded-domain executable proof gate

The invention proof is stronger than a prose theorem but the full live recursive CI proof gate is not yet complete.

A correct gate must distinguish:

```text
coord                 real placement
packed trajectory     exact constituent manifest
realized curve        child physicality coordinates ordered by ordinal
```

It must verify bounded parent/child placement, exact unpack/ordinal/RLE count, constituent existence, realized curves, recursive reconstruction where decoders exist, and lawful coordinate collisions. Packed mantissa values must never be tested as if they were spatial positions.

### Execution grain

The same performance law applies across the machine:

```text
semantic structure / operation
        !=
worker / SQL call / SPI call / PInvoke call
```

Repeated hot work belongs at the coarsest lawful native/set boundary with explicit receipts. Per-candidate/per-row/per-node database or managed/native crossings are implementation defects even when semantic results are correct.

### Content identity / physicality consistency

Current source has at least one documented realization divergence: native composition uses Euclidean centroid while managed `NgramTrajectory`/some chess/model paths use Karcher mean. Both preserve the no-outside-boundary invariant, but they are not the same placement recipe. Architecture/tests must not pretend otherwise; implementation should converge on the selected canonical recipe when that decision is applied.

Current Hash128 and binary64 carrier widths are implementation windows, not theorem limits.

## Delivery meaning

A change is not delivered merely because:

- a plan or issue describes it;
- code exists on an unmerged branch;
- a PR is open;
- unit tests pass locally;
- a benchmark starts;
- a source tree looks correct.

Delivery means the authoritative branch, required CI, installed/deployed artifact where applicable, readback/proof, and operator-visible behavior agree with the requested acceptance boundary.

## Cross-repository references

`Laplace-Refactor` may contain related implementations/issues. Those links are coordination and comparative evidence unless the current user explicitly scopes work there. They do not delegate this repository's invention, acceptance, or implementation obligations away.

## Historical material

Dated session audits, recovery notes, completion plans and campaign ledgers are preserved as evidence in Git history and/or `docs/archive/`. They must be re-verified before being cited as current status.
