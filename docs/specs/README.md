# Design specification index

These files are binding contracts for parts of the invention. They are read under `docs/INVENTION.md` and `docs/INVENTIONS.md`; where a spec disagrees with them, the spec is corrected.

Specifications:

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
- `38_Collections_Are_Compositions.md` — set-valued facts as one composition entity plus one attestation.
- `39_Personality_Firmware.md` — personality firmware: the versioned, content-addressed program over the ISA that parameterizes the forward program without changing knowledge, truth or authority; the OODA loop and the Gödel extension lane.

The common physical implementation law applies across all of them: repeated algorithmic work belongs in coarse native/set execution, while PostgreSQL owns durable indexed state/set access and SQL/C# remain orchestration/contract boundaries.

## Capability synthesis

Binding operation specs are read with [`../CAPABILITIES.md`](../CAPABILITIES.md). Each spec is one part of the integrated machine, not an isolated component: structural software construction, knowledge authority/governance, repair learning, machine-cost derivation and repository-root mutation all run through the same contracts.
