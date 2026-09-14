# Substrate cohesion campaign status

Status: **historical campaign summary; 2026-08-20 detailed status/percentages are preserved in Git history.**

This document used to present then-current completion percentages and an executive verdict for the substrate-cohesion campaign around #1132. Those numbers were useful in that session but are not durable architectural facts and are no longer current status authority.

Use:

- `docs/INVENTION.md` / `docs/INVENTIONS.md` for the invention;
- `docs/ARCHITECTURE.md` for architecture as currently observed;
- `AGENTS.md` for the implementation/delivery contract;
- `docs/specs/36_Laplace_Forward_Pass.md` and `37_Substrate_Operation_ISA.md` for cognition/operation law;
- current GitHub issues/PRs plus current CI/live evidence for implementation status;
- `SUBSTRATE_COHESION_ISSUE_LEDGER.md` only as an ownership/history index.

## What the 2026-08 campaign established

The campaign produced durable evidence that several classes of problem matter across Laplace:

- hand-written SQL/read families can drift from one semantic law;
- partition/index keys and planner shape materially affect read cost;
- repeated scalar DB/SPI/PInvoke boundaries can dominate the work they wrap;
- identity, physicality, occurrence, testimony and calculation need separate homes/contracts;
- perfcaches/masks are accelerators rather than semantic authorities;
- source-specific decomposers must not acquire private persistence/identity semantics;
- source/media reconstruction needs exact recipes rather than flattened values;
- deployment/reseed/benchmark state must be proved, not inferred from merged code.

Those lessons remain valid. The exact August counts, percentages and sequencing do not.

## Corrections established since that status report

Several later findings are now part of the higher-authority architecture and must not be overwritten by the historical campaign framing.

### Recursive bounded representation

Laplace is not “one hash/point per fact.” Finite/countable atoms form unbounded finite recursive compositions. The current native centroid composition remains inside the fixed bounded 4D ball, while exact ordered constituents live in trajectories/DAGs.

Packed trajectory carrier values are not realized spatial positions.

### Current executable identity nuance

Current native multi-child identity is a BLAKE3-derived hash over the Merkle domain plus the **ordered child-id sequence**. Although `hash128_merkle` retains a `tier` parameter in its ABI, `engine/core/src/hash128.c` explicitly discards it with `(void)tier`; tier is not mixed into the current content id. Single-child composition collapses to the child id.

Source/provenance/worker/batch/container facts are likewise separate from canonical content identity. Normal same-content convergence is content-address convergence, not a cryptographic collision.

### Query-relative coupling

The current cognition contract is not “choose a relation mask and walk.” The whole admitted observation participates in a typed coupling/response field before unconstrained interpretation/policy/routing is fixed:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

Sparse hops/fanout operate after/under that orientation.

### Universal execution grain

The native/set-sized law applies everywhere, not only SQL reads:

```text
boundary/orchestration
-> indexed/set-sized handoff
-> native C/C++ repeated algorithmic work
-> bulk/set result + receipt
```

Decomposition, ingestion, cognition, analysis/domain engines, reconstruction, synthesis and export can all be made slow by RBAR/per-element boundary crossings even when the underlying representation is efficient.

### Performance capacity

A benchmark must distinguish serviceable capacity from deliberate machine saturation. On a live managed host, consuming every schedulable CPU until PostgreSQL/product/runner/control-plane availability is lost is not a valid serviceable-capacity result.

See #1436 and `docs/benchmarks/MANUAL_BENCHMARK_EVIDENCE.md`.

## Current open architecture/proof obligations

This list is intentionally architectural rather than a replacement issue backlog:

1. reconcile current native centroid versus managed/domain Karcher parent-coordinate laws;
2. land the exhaustive live recursive physicality/trajectory/reference closure proof gate (#1562);
3. prove complete typed query-relative coupling coverage channel by channel;
4. continue replacing legacy RBAR/small-boundary execution across every pipeline with canonical coarse native/set operators;
5. produce a fresh managed-host serviceable-capacity receipt under the now-enforced headroom policy;
6. add query/cognition preflight-vs-actual work receipts in hops/fanout/responders/resources (#1561);
7. prove claimed product/cognition quality and resource economics end to end with exact receipts.

Each of those is an implementation obligation tracked by current issues/code/tests. None is permission to narrow the invention.

## Historical detail

For the exact 2026-08-20 SQL counts, lane percentages, then-current media/perfcache/storage findings and row-by-row status matrix, inspect the Git revision immediately before the 2026-09-14 documentation reconciliation.

Do not quote those historical percentages as current completion without remeasurement.
