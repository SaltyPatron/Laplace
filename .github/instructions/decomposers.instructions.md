---
name: 'Decomposer rules'
description: 'Binding rules for writing or modifying decomposers (content → SubstrateChange streams)'
applyTo: 'app/Laplace.Decomposers/**'
---
# Decomposer rules (binding — specs 06 and 08)

- Decomposers are PURE: content in → `SubstrateChange` record streams out. ZERO inline
  SQL.
  Batching/dedup/Glicko fold/COPY belong to the pipeline spine
  (`IngestBatchPipeline` working-set mode → `ConsensusAccumulatingWriter` → `NpgsqlWorkingSetApply`).
- The spec is the SEQUENCE: the right algorithm at the wrong pipeline stage is a
  violation ([doc 06](../../docs/specs/06_Engineering_Ruleset.txt) Rule #8).
- Ingestion is RECORDING, not processing ([doc 08](../../docs/specs/08_Record_vs_Calculate_Spec.txt)):
  transcribe only what the source literally asserts (witnessed layer); anything derived
  goes to the versioned, evictable calculated layer. Never mix them.
- SourceIds are load-bearing identity — never change one. Re-ingest hash identity is the
  regression test for this.
- Duplicate content-addressed inserts mean "we agree", not "error" (lesson L9). Do not
  add prevention logic for them.
- Do not create a new ingest lane by copying a historical implementation. Verify the
  live CLI dispatch and shared-spine contract, then use or consolidate the canonical
  path under an active GitHub issue.
- `outcome ∈ {Loss=0, Draw=1, Win=2}` is bit-identical to chess `PlyOutcome` by design.
- Tier is a floor, not identity: same content = same id at every tier; tier is NEVER
  mixed into the BLAKE3 hash.
