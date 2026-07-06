---
name: 'Next task'
description: 'Determine the next highest-leverage task from binding docs and code-verified open work'
agent: agent
---
Determine the next task to work on in the Laplace repo. Do not start implementing —
produce a ranked recommendation.

Trust order (do NOT invert):
1. Running code (`app/`, `engine/`, `extension/`) — verify claims with file:line or grep.
2. Binding author docs: `.scratchpad/05`, `06`, `08`, `09`, `11`, `12`.
3. Operational config: `scripts/win/witness-manifest.json`, `scripts/decomposer-gates.json`.
4. Do NOT treat as authority: `.scratchpad/13`, compacted `02` status index alone,
   or the one-line "pipeline chain" — those are agent-written summaries that have drifted.

Read for context:
- [.scratchpad/17_Decomposer_Full_Stack_Audit.md](../../.scratchpad/17_Decomposer_Full_Stack_Audit.md)
  if decomposer / ingest-spine work is in scope.
- [.scratchpad/06_Engineering_Ruleset.txt](../../.scratchpad/06_Engineering_Ruleset.txt)
  Rule #8 ingest sequence and Rule #6 one-implementation-per-fact.
- [.scratchpad/09_Substrate_LM_Synthesis.txt](../../.scratchpad/09_Substrate_LM_Synthesis.txt)
  for invention framing (construct-don't-train, spider-web Laplacian).
- Open items in [.scratchpad/02_Identified_Issues.txt](../../.scratchpad/02_Identified_Issues.txt)
  ONLY after verifying each candidate against code (status index alone is not truth).

Judge candidates by whether they:
- Put more witnessed attestations through ONE Rule #8 spine door (`IngestBatchPipeline`
  → `ContentTierSpine` / handlers → fold → `NpgsqlWorkingSetApply`).
- Remove duplicate C# / native / SQL implementations of the same fact (Rule #6).
- De-risk Mold-A-Model export by improving consensus supply (honest witnesses), per 09/12.

Output: top 3 candidates ranked, each with (a) code/doc evidence, (b) dependency argument,
(c) concrete first step, (d) live-data verification (`psql`, `seed-step.cmd :verify_step`,
or targeted test).

If docs contradict code, code wins — say so explicitly.
