# Design specification index

These files describe binding behavioral/architectural parts of the invention. They are **not** evidence that a feature is implemented and they do not override a later explicit inventor correction or the canonical invention statement in `docs/INVENTION.md` / `INVENTIONS.md`.

Read them under the authority order in `AGENTS.md` and `docs/README.md`. When a lower/current spec contradicts higher authority, repair the spec rather than narrowing the invention around the stale wording.

Historical annotated versions are preserved under [`docs/archive/specs-v1/`](../archive/specs-v1/README.md) and are non-authoritative historical evidence.

Current specification set:

- `05_Substrate_Invariants.txt` — identity, tiers, physicality, evidence, consensus.
- `06_Engineering_Ruleset.txt` — implementation and operational constraints.
- `08_Record_vs_Calculate_Spec.txt` — observation versus deterministic/derived calculation state.
- `09_Substrate_LM_Synthesis.txt` — substrate-native model construction and consensus.
- `11_Chess_Provenance_Consensus_Spec.txt` — chess as a witnessed modality/proving domain.
- `12_Mold_A_Model_Synthesis_Map.txt` — conventional consumer-slot and substrate-program map.
- `33_Perfcache_Blob_Law.md` — rebuildable derived accelerator contract.
- `34_Conversational_Provenance.md` — tenant/session/turn provenance.
- `36_Laplace_Forward_Pass.md` — canonical stateful forward program, including `RESOLVE → COUPLE → ORIENT → ROUTE → ...`.
- `37_Substrate_Operation_ISA.md` — typed operation algebra; stable opcode ids are names, not execution-order numbers (`OP10 COUPLE` executes after `OP0 RESOLVE` in unconstrained cognition).

The common physical implementation law applies across all of them: repeated algorithmic work belongs in coarse native/set execution, while PostgreSQL owns durable indexed state/set access and SQL/C# remain orchestration/contract boundaries.