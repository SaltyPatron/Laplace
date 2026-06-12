# HANDOFF: the RecordIngest engine (decomposition law, Stage 3)

Status 2026-06-11: recon complete, engine unbuilt. Plan of record:
`C:\Users\ahart\.claude\plans\delightful-waddling-valley.md` (approved). Stages 0-2
are code-complete (commit 09da8a2 + 7dc555f); their acceptances ride the TinyLlama
fold recovery. This file is the implementation brief for Stage 3, distilled from a
full-tree recon so the builder does not re-survey.

## The law being built

Decomposers become **parser-adapter + RecordSpec manifest**. One engine owns every
concern the 17 hand-rolled `DecomposeAsync` bodies re-decide today: streaming,
batching, phases/fences, dedup scopes, canonical seeding, content emission,
parallelism, inventory/progress. Requirements are set by the RICHEST sources (UD,
Code, Model, Unicode), not the simplest. No exemptions; best implementations get
PROMOTED into the engine.

## Surfaces the engine slots into (verified)

- `IDecomposer` (app/Laplace.Decomposers.Abstractions/IDecomposer.cs): SourceId,
  SourceName, LayerOrder, TrustClassId, InitializeAsync, DecomposeAsync,
  EstimateUnitCountAsync, CanonicalNamesForReadback. The engine is a plain
  IDecomposer — IngestRunner needs ZERO changes.
- `IIngestCommitPolicy` { CommitParallelism: StrictSerial | EpochBarrier |
  Unordered } — runner picks one of three commit paths (IngestRunner.cs:112-114;
  paths at 144-206 / 263-338 / 340-427). EpochBarrier is default; epochs must be
  monotone (runner throws on regression, IngestRunner.cs:395-400).
- `IIngestInventoryProvider.DescribeInputAsync` → IngestInventory(UnitType,
  TotalInputUnits, Files) — preferred over EstimateUnitCountAsync (runner:704-710).
- Period-boundary markers: an empty SubstrateChange named
  `period-boundary/{stem}` advances the runner's files-done counter
  (UDDecomposer.cs:100-101 emit; IngestRunner.cs:716-721 consume).
- `SubstrateChangeBuilder`: AddEntity/AddPhysicality/AddAttestation (attestation
  fold-on-dup with Glicko draw rule at φ=500M), AddIntentStage, lazy coalesced
  `ContentStage` (one IntentStage per change — the Stage-1 fix; ALL engine content
  emission goes through it). Build() freezes + computes intent id.
- Writer RT phases (NpgsqlSubstrateWriter): preflight 1 RT + 2/prebuilt stage +
  3 entity + 3 physicality + 3/1 attestation (delta-update lane). RT budget guard:
  LAPLACE_RT_BUDGET_PER_10K (64), LAPLACE_RT_BUDGET_ENFORCE.

## What gets promoted (the adapters)

1. **Line records**: StreamingUtf8LineReader (Abstractions) — 1 MB chunks, carry
   buffer, CRLF-safe, ReadOnlyMemory<byte> per line. TSV framing via TsvSpan;
   JSONL framing = witness-parses-bytes (Wiktionary shape).
2. **CoNLL-U sentence iterator**: UDDecomposer.ParseSentencesAsync (340-408) —
   blank-line sentence boundary, `#` metadata, multiword tokens. Promote as a
   sentence-record adapter.
3. **ZIP paired-line**: OpenSubtitlesFastIngest.ReadPairedLinesAsync (73-97) —
   the only temp-file divergence in the tree; engine version should pair streams
   without temp files if feasible.
4. **Grammar-compose (AST records)**: StructuredGrammarIngest + IGrammarWitness
   { ModalityId, WalkRow(in GrammarComposeContext, in RowContext, builder) } —
   tree-sitter rows; Materialize(witnessWeight) → (ents, phys, atts, root).
5. **Tensor-path records**: ModelTableETL.EmitAsync — ArchitectureProfile.Paths
   (PathSpec) streamed as records; phase 1 entities epoch N, phase 2 matchups
   epoch N+1.
6. **Perf-cache id resolver (the O(tier) law)**: Unicode's in-process derivation
   (CodepointPerfcache + HashComposer; ContentEmitter._rootMemo 1M-entry memo).
   Engine id resolvers are pluggable; this is the floor (Unicode = 11 RT / 252k
   rows — porting it LAST proves no regression of the best case).
7. **Worker law**: UD's file queue (ConcurrentQueue + per-worker builders +
   bounded channel workers*4), LAPLACE_DECOMPOSE_WORKERS (≠ LAPLACE_INGEST_WORKERS,
   which is commit parallelism). A spec declares its parallelism class.
8. **Byte prefilter hook**: Tatoeba/Wiktionary/ConceptNet row filters (language
   pre-match on raw bytes before parse).

## Dedup scopes (unify; today heterogeneous)

- per-batch entity dedup: seenEntBatch HashSet cleared at batch boundary (UD:95).
- per-run attestation dedup: ConcurrentIdSet seenAttRun (RelationTypeRegistry.
  SeedDynamic:180-193 — type entity once per batch, IS_A parent once per run).
- builder-level: _seenEntities/_seenPhysicalities silent skip; attestation folding.
Engine: declarative "seed once per {batch|run|init}" semantics, one tracker.

## Dynamic relation families (core product — carry losslessly)

SeedDeprel/SeedEnhancedDeprel/ResolveFeature/PosUpos (UD), SeedDynamic
(ConceptNet). POS/sense/deprel/feature arenas are flagship; claims/witnesses must
express dynamic-family resolution. PosReference.SeedCanonical, LanguageReference
(iso-639-3.tab), BootstrapIntentBuilder (AddType/AddRelationType in
InitializeAsync; feeds CanonicalNamesForReadback).

## The claims law (tier-witness law made unrepresentable-to-violate)

A declarative ClaimSpec's object is either:
- **witnessed-content**: engine Emits the content tree (natural tier + trajectory)
  through builder.ContentStage, then attests — Emit-then-attest, never RootId-only;
- **reference-to-existing**: declared dependency on an earlier phase, fence-checked.
There is NO id-without-witness expression in a manifest. (RootId ghosts: the
Wiktionary bug, fixed 09da8a2; lawful two-pass re-derivation documented in
WordNet/FrameNet/UD, commit 7dc555f.)

## Phases/fences

Ordered PhaseSpecs with commit epochs subsume: RelationTripleDecomposerBase
two-pass (TriplePass enum), Tatoeba sentence→link fence (SetCommitEpoch 0/1),
Model entity→matchup epochs. Declared deps, engine-ordered, runner-enforced.

## Batching

One row-budget law (engine default); per-spec override only with a stated reason
recorded in the spec. Today's folklore: 512 (Wiktionary) … 8192 (ConceptNet) with
hand-tuned builder capacity multipliers — replace with measured default.

## Port order (Stage 4 — no exemptions)

ISO639 (proof + RT acceptance <100 from 16,681) → UD (flagship; POS/deprel/feature
arenas bit-for-bit; witnessed-stopwords law goes live) → WordNet+OMW → FrameNet/
PropBank/VerbNet/SemLink → Tatoeba/ConceptNet/Atomic2020/Wiktionary/OpenSubtitles
(delete five FastIngest helpers) → Document → Code → Unicode (floor proof) →
Model. Per port: fresh-DB A/B counts identical + per-tier content-physicality
counts + sampled id equality; ladder timing same-or-better; MiniLM loop green.
Wiktionary's A/B baseline is the Stage-2-FIXED behavior.

## Acceptance currently banked

- A-side counts of pre-fix laplace_minilm floor: D:\Temp\minilm-ab-before.txt
  (Unicode 1,116,425 phys / 873,826 atts; ISO639 11,045 phys / 34,427 atts —
  ignore source 38754af4… partial-model rows; that DB gets evicted).
