---
name: 'Next task'
description: 'Determine the next highest-leverage task from the stabilization plan and verification debt'
agent: agent
---
Determine the next task to work on in the Laplace repo. Do not start implementing —
produce a ranked recommendation.

1. Read [.scratchpad/13_Stabilization_Audit_and_Plan.txt](../../.scratchpad/13_Stabilization_Audit_and_Plan.txt)
   in full — it is THE active plan. Note which phase items are marked done vs open.
2. Read the status index at the top of [.scratchpad/02_Identified_Issues.txt](../../.scratchpad/02_Identified_Issues.txt)
   for open issues and verification debt (fixed-but-not-proven items).
3. Check for foundry-frontier context in [.scratchpad/14_Foundry_Root_Cause_and_Research.txt](../../.scratchpad/14_Foundry_Root_Cause_and_Research.txt)
   (prescriptions P1-P10 and their status).
4. Judge every candidate by the arc: content → decomposer records → dedup/Glicko →
   bulk-novel COPY → consensus → recall/walk → Mold-A-Model. Work that doesn't shorten
   or de-risk that arc is out.
5. Output: top 3 candidates ranked, each with (a) which doc/phase it comes from,
   (b) why it's next (dependency or risk argument), (c) the concrete first step,
   (d) how completion will be verified against live data.

If the docs contradict each other on status, say so explicitly and recommend a
doc-reconciler pass first.
