---
name: 'Next task'
description: 'Recommend the next highest-leverage task from the product finish line and live GitHub ownership'
agent: agent
---
Recommend work; do not implement it.

Read in this order:

1. `docs/DOCUMENTATION_GOVERNANCE.md` — authority hierarchy.
2. `docs/plan/REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md` — product outcome.
3. GitHub epic #924 and its live blocking/dependency graph.
4. The relevant normative files in `docs/specs/`.
5. Current code, installed schema/API, tests, and runtime measurements needed to verify
   candidate gaps.

Never select work from `.scratchpad/`, `docs/archive/`, checkpoints, old backlog lists,
agent plans, prompts, memories, or issue prose that contradicts current evidence.

Rank the top three verified candidates by:

- direct contribution to the #924 final demonstration;
- dependency unblocking and consolidation of rival paths;
- preservation of identity, provenance, typed execution, and CPU-native architecture;
- behavioral proof through MCP/OpenAI/code/model/export surfaces;
- availability of a bounded issue with explicit acceptance.

For each candidate provide evidence, dependency argument, issue owner, first mechanical
step, required seed/runtime profile, and executable acceptance. State any stale issue
claim that must be corrected before work begins.
