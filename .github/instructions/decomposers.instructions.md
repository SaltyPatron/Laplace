---
name: 'Decomposer rules'
description: 'Binding rules for writing or modifying decomposers (content → SubstrateChange streams)'
applyTo: 'app/Laplace.Decomposers/**'
---
# Decomposer rules (binding — Rule #8, docs 06/08/13)

- Decomposers are PURE: content in → `SubstrateChange` record streams out. ZERO inline
  SQL — this is a protected property, verified by audit ([doc 07 §1](../../.scratchpad/07_SQL_Surface_Audit.txt)).
  Batching/dedup/Glicko fold/COPY belong to the pipeline spine
  (`IngestBatchPipeline` working-set mode → `ConsensusAccumulatingWriter` → `NpgsqlWorkingSetApply`).
- The spec is the SEQUENCE: the right algorithm at the wrong pipeline stage is a
  violation ([doc 06](../../.scratchpad/06_Engineering_Ruleset.txt) Rule #8).
- Ingestion is RECORDING, not processing ([doc 08](../../.scratchpad/08_Record_vs_Calculate_Spec.txt)):
  transcribe only what the source literally asserts (witnessed layer); anything derived
  goes to the versioned, evictable calculated layer. Never mix them.
- SourceIds are load-bearing identity — never change one. Re-ingest hash identity is the
  regression test for this.
- Duplicate content-addressed inserts mean "we agree", not "error" (lesson L9). Do not
  add prevention logic for them.
- There are SEVEN historical ingestion lanes (Issue 45, [doc 13 §2.1](../../.scratchpad/13_Stabilization_Audit_and_Plan.txt)).
  Do NOT create a new source by copying the nearest neighbor. Check doc 13 Phase 1 for
  the target lane first; prefer the working-set pipeline lanes (`DecomposerBatch`,
  `StructuredGrammarIngest`). Four sources (atomic2020, conceptnet, omw, wiktionary)
  have TWO parallel implementations — verify which lane CLI dispatch
  (`IngestCommands.cs`) actually routes to before editing either.
- `outcome ∈ {Loss=0, Draw=1, Win=2}` is bit-identical to chess `PlyOutcome` by design.
- Tier is a floor, not identity: same content = same id at every tier; tier is NEVER
  mixed into the BLAKE3 hash.
