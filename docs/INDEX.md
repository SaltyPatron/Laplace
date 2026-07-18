# Doc index

Two classes. **spec** = living law: kept current, superseded statements annotated in
place with a date, never silently rewritten. **log** = historical session/campaign
record: append-only, never "fixed" — verify any claim against code before trusting.
Dates are last-substantive-edit (git). The generated inventory (`docs/INVENTORY.md`,
once PR-2 lands) owns every countable fact; prose docs cite it instead of embedding
counts.

## Entry points

- [README.md](../README.md) — what Laplace is, the epistemic map (arrives with PR-3)
- [CLAUDE.md](../CLAUDE.md) — the operating law: architecture, binding rules, build/seed tables
- [AGENTS.md](../AGENTS.md) — agent conduct + PG service law + binding-doc pointers
- [docs/INVENTIONS.md](INVENTIONS.md) — spec — the invention catalog: 41 mechanisms, code-cited (2026-07-10)

## Specs — `docs/specs/` (binding, annotate-on-supersede)

- [05_Substrate_Invariants.txt](specs/05_Substrate_Invariants.txt) — spec — the axioms; identity/tier/geometry law (2026-07-02)
- [06_Engineering_Ruleset.txt](specs/06_Engineering_Ruleset.txt) — spec — Rules #1–#12; Rule #8 is the ingest sequence (2026-07-05)
- [08_Record_vs_Calculate_Spec.txt](specs/08_Record_vs_Calculate_Spec.txt) — spec — witnessed vs calculated layers; analyzer versioning (2026-07-04)
- [09_Substrate_LM_Synthesis.txt](specs/09_Substrate_LM_Synthesis.txt) — spec — construct-don't-train thesis; the open routing question (2026-07-10)
- [11_Chess_Provenance_Consensus_Spec.txt](specs/11_Chess_Provenance_Consensus_Spec.txt) — spec — three-layer provenance/consensus + the chess board ladder (2026-07-04)
- [12_Mold_A_Model_Synthesis_Map.txt](specs/12_Mold_A_Model_Synthesis_Map.txt) — spec — substrate primitive → transformer slot bijection (2026-07-10)
- [14_Foundry_Root_Cause_and_Research.txt](specs/14_Foundry_Root_Cause_and_Research.txt) — spec — foundry working doc: mechanisms M1–M5, prescriptions P1–P10 (2026-07-10)
- [15_Godel_Engine_OODA_Loop.txt](specs/15_Godel_Engine_OODA_Loop.txt) — spec — the closed loop: walk/deposit/feedback/fold; §3C richer forward pass (2026-07-18)
- [16_tier_correct_attestation_and_hub_unification.md](specs/16_tier_correct_attestation_and_hub_unification.md) — spec — tier-correct attestation; ILI hub mesh fixes P1–P7 (2026-07-12)
- [18_Typed_Residual_Stream_and_Mesh.md](specs/18_Typed_Residual_Stream_and_Mesh.md) — spec — typed strata replacing the anonymous residual; mesh factorization (2026-07-10)
- [19_Factor_Storage_Research.md](specs/19_Factor_Storage_Research.md) — spec — factor/projection record law; mantissa FACTOR vertices; blob candidate (2026-07-10)

## Logs and campaigns — `.scratchpad/` (historical, append-only)

- [01_Initial_review.txt](../.scratchpad/01_Initial_review.txt) — log — first whole-repo review (2026-07-01)
- [02_Identified_Issues.txt](../.scratchpad/02_Identified_Issues.txt) — tracker — compacted issue tracker + lessons L1–L12 (2026-07-18; migrates to GH issues in PR-4)
- [03_Chess.txt](../.scratchpad/03_Chess.txt) — log — chess domain review (2026-07-01)
- [04_Chess_Fixes.txt](../.scratchpad/04_Chess_Fixes.txt) — log — chess fix sagas (2026-07-10)
- [07_SQL_Surface_Audit.txt](../.scratchpad/07_SQL_Surface_Audit.txt) — log — SQL surface audit + P-roadmap (2026-07-05)
- [10_SQL_Consolidation_Reconciliation.txt](../.scratchpad/10_SQL_Consolidation_Reconciliation.txt) — log — manifest lockdown reconciliation (2026-07-04)
- [13_Stabilization_Audit_and_Plan.txt](../.scratchpad/13_Stabilization_Audit_and_Plan.txt) — log — historical stabilization notes; NOT the active plan (2026-07-08)
- [17_Decomposer_Full_Stack_Audit.md](../.scratchpad/17_Decomposer_Full_Stack_Audit.md) — log — decomposer/spine audit, code-verified (2026-07-18)
- [20_Session_Violation_Ledger_2026-07-09.md](../.scratchpad/20_Session_Violation_Ledger_2026-07-09.md) — log — session violation ledger (2026-07-12)
- [22_Conversational_Engine_Plan.md](../.scratchpad/22_Conversational_Engine_Plan.md) — campaign — converse/chat phases A–F with dated status (2026-07-18)
- [23_Perfcache_Codegen_Valet.md](../.scratchpad/23_Perfcache_Codegen_Valet.md) — log — perfcache codegen valet notes (2026-07-12)
- [24_Campaign_Reseed_Queue.md](../.scratchpad/24_Campaign_Reseed_Queue.md) — campaign — queued reseed items; do NOT execute until KEYMASTER (2026-07-12)
- [25_Refactor_Audit_Inventory.md](../.scratchpad/25_Refactor_Audit_Inventory.md) — log — audit-refactor inventory + baseline timings (2026-07-15)
- [26_Uncracked_List_Campaign.md](../.scratchpad/26_Uncracked_List_Campaign.md) — campaign — the A→I critical path; factor gates; Argentina gate (2026-07-15)
- [27a_Primitive_Index_Attention.md](../.scratchpad/27a_Primitive_Index_Attention.md) — log — transformer primitive index: attention (2026-07-15)
- [27b_Primitive_Index_Containers_Gates.md](../.scratchpad/27b_Primitive_Index_Containers_Gates.md) — log — primitive index: containers/gates (2026-07-15)
- [27c_Primitive_Index_FFN_Norms_Embeddings.md](../.scratchpad/27c_Primitive_Index_FFN_Norms_Embeddings.md) — log — primitive index: FFN/norms/embeddings (2026-07-15)
- [27d_Primitive_Index_Local_Inventory.md](../.scratchpad/27d_Primitive_Index_Local_Inventory.md) — log — primitive index: local model inventory (2026-07-15)
- [28_Model_Lane_Perf_Ledger.md](../.scratchpad/28_Model_Lane_Perf_Ledger.md) — log — model-lane perf ledger (2026-07-15)
- [29_Witness_Trajectory_Evidence.md](../.scratchpad/29_Witness_Trajectory_Evidence.md) — log — witness-trajectory evidence notes (2026-07-15)
- [30_Git_Object_Witnessing_Repo_Decomposer.md](../.scratchpad/30_Git_Object_Witnessing_Repo_Decomposer.md) — log — repo decomposer / git-object witnessing design notes (2026-07-17)
- [31_Waste_Audit_Repo_Wide.md](../.scratchpad/31_Waste_Audit_Repo_Wide.md) — log — five-auditor waste audit, tiered findings + status (2026-07-17)
- [32_Patent_Portfolio_Audit.md](../.scratchpad/32_Patent_Portfolio_Audit.md) — log — patent portfolio audit (2026-07-11; renumbered from 22 to fix collision)
- [session-tasks.md](../.scratchpad/session-tasks.md) — campaign — ingest-lane session task list (2026-07-18)

## `docs/invention/` (agent onboarding)

- [00-CONTINUITY.md](invention/00-CONTINUITY.md) — onboarding — rewritten as pointer + verified traps in PR-3
- [05-synthesis-layers-heads.md](invention/05-synthesis-layers-heads.md) — notes — synthesis layers/heads; verify-or-annotate pass owed (PR-3)
- [modality-ladder-law.md](invention/modality-ladder-law.md) — notes — modality ladder law; verify-or-annotate pass owed (PR-3)
- [recipe-schema.md](invention/recipe-schema.md) — notes — model recipe schema; verify-or-annotate pass owed (PR-3)

## Decisions — `docs/decisions/`

- 0001-highway-bit-order (arrives with PR-5) — append-only bit registry vs alphabetical codegen + reseed

Renumbering note: `22_Patent_Portfolio_Audit` → `32` and the four `27_*` files →
`27a`–`27d` (2026-07-18) resolved filename collisions; prose citations of "doc 22"/
"doc 27" in older logs predate the rename.
