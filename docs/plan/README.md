# Plan and design index

This directory contains stable implementation context and behavioral acceptance—not
project status. GitHub issues are the only active work tracker. Dated checkpoints,
backlog snapshots, and agent prompts are preserved under `docs/archive/`.

## Product finish line

[REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md](REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md)
defines the required MCP/OpenAI product: real stateful conversation, code generation
with toolchain feedback, heterogeneous-model pooled consensus, source inspection, and
deterministic export.

Delivery is owned by GitHub epic
[#924](https://github.com/SaltyPatron/Laplace/issues/924). Its blocking graph includes:

- [#920](https://github.com/SaltyPatron/Laplace/issues/920) — deployed MCP protocol proof.
- [#921](https://github.com/SaltyPatron/Laplace/issues/921) — one stateful dynamic-frontier forward pass.
- [#922](https://github.com/SaltyPatron/Laplace/issues/922) — honest OpenAI roles, parameters, tools, and code contract.
- [#923](https://github.com/SaltyPatron/Laplace/issues/923) — source-scoped model circuit comparison and cross-architecture correlation.
- [#927](https://github.com/SaltyPatron/Laplace/issues/927) — one pooled heterogeneous-source consensus forward pass with ablation proof.
- [#928](https://github.com/SaltyPatron/Laplace/issues/928) — deterministic source-scoped and pooled model export with provenance receipts.
- [#929](https://github.com/SaltyPatron/Laplace/issues/929) — dataset capability manifests for tiers, relations, trajectories, and product contribution.
- [#755](https://github.com/SaltyPatron/Laplace/issues/755) — seeded end-to-end product acceptance.

Documentation and instruction authority is governed by
[#926](https://github.com/SaltyPatron/Laplace/issues/926).

The issue state must be checked live. This file intentionally carries no open/closed or
partial/landed column.

## Workstream contracts

[WORKSTREAMS.md](WORKSTREAMS.md) defines W1–W17 without mutable implementation status.
Detailed prior analyses are preserved in `docs/archive/plans/workstreams-v1/` and do not
create work unless a current GitHub issue restates their requirement and acceptance.

## Required work-item shape

An implementer starts from a GitHub issue and uses the matching design for context. The
issue must contain the user-visible outcome, verified gap, dependencies, non-success
criteria, behavioral acceptance, seed/runtime profile, and provenance/trace evidence.

If a design contradicts current code or the running system, record the measurement in
the issue and update the design in the same change. Never add a status ledger here.
