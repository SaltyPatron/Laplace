# Laplace documentation

Laplace is specified by the invention documents. These are the documents that say what the machine does.

## Invention

- [`INVENTION.md`](INVENTION.md) — the machine: identity, bounded composition, physicality, trajectory, evidence, coupling, execution, realization.
- [`CAPABILITIES.md`](CAPABILITIES.md) — what those laws compose into: one knowledge world, separate authority and compute, construction, repair, and machine cost.
- [`INVENTIONS.md`](INVENTIONS.md) — the mechanism catalog. It does not override `INVENTION.md`.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) — how this repository implements that machine.
- [`OPERATING_SEQUENCE.md`](OPERATING_SEQUENCE.md) — admission through a witnessed turn.
- [`../AGENTS.md`](../AGENTS.md) — implementation of the invention in this repository.

## Specifications

Binding contracts, read under the invention:

- [`specs/05_Substrate_Invariants.txt`](specs/05_Substrate_Invariants.txt)
- [`specs/06_Engineering_Ruleset.txt`](specs/06_Engineering_Ruleset.txt)
- [`specs/08_Record_vs_Calculate_Spec.txt`](specs/08_Record_vs_Calculate_Spec.txt)
- [`specs/09_Substrate_LM_Synthesis.txt`](specs/09_Substrate_LM_Synthesis.txt)
- [`specs/11_Chess_Provenance_Consensus_Spec.txt`](specs/11_Chess_Provenance_Consensus_Spec.txt)
- [`specs/33_Perfcache_Blob_Law.md`](specs/33_Perfcache_Blob_Law.md)
- [`specs/34_Conversational_Provenance.md`](specs/34_Conversational_Provenance.md)
- [`specs/36_Laplace_Forward_Pass.md`](specs/36_Laplace_Forward_Pass.md)
- [`specs/37_Substrate_Operation_ISA.md`](specs/37_Substrate_Operation_ISA.md)
- [`specs/38_Collections_Are_Compositions.md`](specs/38_Collections_Are_Compositions.md)

The forward program is:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

`COUPLE` is the typed response field of the admitted observation. Domain search, chess, geometry, and model execution are operators inside that program.

```text
canonical content
        !=
coordinate              placement in the bounded frame
        !=
packed trajectory       constituent manifest
        !=
realized curve          child placements in ordinal order
        !=
occurrence / testimony / calculation / consensus
```

## Execution

Repeated work crosses SQL, SPI, and managed/native boundaries at the set, not at the element. PostgreSQL persists and indexes. Native code runs the loops. C# and SQL orchestrate.

Guides under [`guides/`](guides/) describe how to operate that machine. They do not define another one. [`plan/`](plan/) records mechanism contracts for ingest, conversation, and delivery. [`INVENTORY.md`](INVENTORY.md) is the generated catalog.
