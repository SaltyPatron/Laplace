# 17 — Decomposer full-stack audit (code-verified)

[HISTORICAL, marked 2026-07-18: this doc's Section 8 target ("Decomposer<T>
extract-only; delete dead lanes; extend architecture gate to Chess") is now
EXECUTED and verified -- dead paths (OMWEtlRegistration, WiktionaryEtlRegistration,
PCoreParallelCompose, etl_witness_conceptnet.c) are absent from the tree; Chess
decomposers use ComposeDecomposer<T>; the architecture gate test covers Chess
(DecomposerArchitectureGateTests.cs). Kept for its original code-verified
inventory value, not as an active remediation target -- see
.cursor/plans/full_stack_remediation_bdaba5c3.plan.md for current state.]

Date: 2026-07-06. Method: grep + read of every production `*Decomposer.cs`, helper ingest modules, Rule #8 spine, native `engine/core`, extension SPI.

**Purpose:** code-verified inventory — **input to** [18_Remediation_Plan.md](18_Remediation_Plan.md), not the finish line. Maps every decomposer + spine brand + sabotage/disproof for harness and code unfuck.

## Trust order

1. Code (cited below)
2. Author binding docs: 05, 06, 08, 09, 11, 12
3. Ops: `witness-manifest.json`, `decomposer-gates.json`
4. **NOT authority:** scratchpad/13, next-task pipeline chain, compacted 02 index alone

---

## §0 — Why (09/08/05, not doc 13)

- Substrate = explicit attestation graph + Glicko consensus; models **constructed** (12), not trained.
- Decomposers = **witness boundary** (08). Calculated layer separate.
- Forked spine brands → same content, different Rule #8 path → bad consensus → bad export.

## §1 — Rule #8 lens (06 L69–96)

| Step | Canonical code |
|------|----------------|
| 1 Unpack | `GrammarComposeIngestSupport`, `grammar_compose.cpp` |
| 2 Records | `IDecomposer` + `IIngestRecordHandler<T>`; no inline SQL |
| 3 Dedup | `WorkingSetMode`, one builder per stream |
| 4 Glicko | `ConsensusAccumulatingWriter` + `glicko2.c` |
| 5 O(tiers) descent | `ContentTierSpine` → `TierTreeDescent` → `tier_batch_existence_probe` |
| 6 COPY | `NpgsqlWorkingSetApply` |

**06 L93–94 gap:** per-flush O(tiers) in `IngestDescentFlush.cs` L28–31; bulk across whole working set not landed.

**Leaf→trunk compose:** `ContentTierSpine.BuildTree` → native `text_decomposer` + `hash_composer`.  
**Trunk→leaf existence:** `TierTreeDescent.cs` L236–268; tier-0 via `CodepointPerfcache` L155–158 before SPI.

## §2 — Perfcaches

| Blob | Loader | Role |
|------|--------|------|
| `laplace_t0_perfcache.bin` | `CodepointPerfcache` | Tier-0 fast path, UAX tables |
| `laplace_highway_perfcache.bin` | `HighwayPerfcache` | `highway_mask` on attestations |
| PG `perfcache_native` | extension | SQL descent fast path |

Apply: `NpgsqlWorkingSetApply` entity probe — perfcache fast path **OFF** (L24–28).

## §3 — Spine brands (grep 2026-07-06)

| Brand | Users |
|-------|-------|
| `RelationTripleDecomposerBase` | ConceptNet, Atomic2020, OpenSubtitles |
| `DecomposerBatch` | ISO×6, CILI×3, WordNet×4, PropBank, VerbNet, FrameNet×3, Model×6, Recipe, Tabular×2, Repo†, ChessAnalyze, ChessPgn† |
| `StructuredGrammarIngest` | Wiktionary, Tatoeba, EtlDecomposer |
| `GrammarComposeIngestSupport` | Code, Stack, TinyCodes, Repo† |
| `IngestBatchPipeline` direct | UD, Document, ChessPgn†, SemLink adapters |
| `CategoryCorrespondenceIngestSupport` | MapNet, WordFrameNet, FnLu bridge, PredicateMatrix |
| `OMWGrammarIngest` | OMW |
| SemLink orchestrator | SemLinkDecomposer L36–101 |
| Hand builder | Unicode, ChessOpenings |
| Stubs | Image, Audio |

Gate: `DecomposerArchitectureGateTests` — no Chess; Unicode/UD allowlisted.

## §4 — Per-decomposer table

| Source | Input | Spine | Hand-roll / dup | vs `Decomposer<T>` | Prereqs |
|--------|-------|-------|-----------------|-------------------|---------|
| unicode | computed UCD | hand | L66–75 init; L78–535 builders | **Exception** | — |
| iso639 | 6 TSV | DecomposerBatch×6 L59–100 | Stage* lambdas | Far | unicode |
| cili | ttl+tab | DecomposerBatch×3 L50–76 | Emit lambdas L81–105 | Medium | iso639 |
| document | dir txt | Pipeline multi-file L37 | — | Near | iso639 |
| wordnet | monoliths×4 | DecomposerBatch×4 | ContentWitnessBatch L380–388 | Far | cili |
| omw | dir .tab | OMWGrammarIngest L46 | dead EtlRegistration | Medium | wordnet |
| verbnet | dir XML | DecomposerBatch L52 | EmitClass ≈ PropBank | Far | iso639 |
| propbank | dir XML | DecomposerBatch L61 | ≈ VerbNet | Far | iso639 |
| framenet | dir XML×3 | DecomposerBatch L99–111 | LemmaOf dup L360–491 | Far | wordnet |
| mapnet | 2 tab | CategoryCorrespondence | thin | Near | wordnet+framenet |
| wordframenet | tab | CategoryCorrespondence | thin | Near | wordnet+framenet |
| semlink | JSON+PM+XML | orchestrator | 4 sub-ingests | Medium | nets hub |
| conceptnet | csv | Triple base | Canonicalize dup | **Reference** | iso639 |
| atomic2020 | 3 tsv | Triple base | seed L61 | **Reference** | iso639 |
| opensubtitles | zip dir | Triple base | ZipIngest helper | **Reference** | iso639 |
| ud | conllu dir | Pipeline+handler | period L169 | Near | iso639 |
| wiktionary | jsonl | StructuredGrammar L60 | dead EtlRegistration | Medium | iso639 |
| tatoeba | 2 csv epochs | StructuredGrammar×2 | epoch state | Medium | iso639 |
| code | dir | GrammarCompose L34 | — | Near | code capstone |
| repo | dir+root | Batch+Compose | StageRepoRoot | Split | code capstone |
| stack | parquet dir | GrammarCompose L103 | parquet≈TinyCodes L154–197 | Near | code capstone |
| tiny-codes | parquet | GrammarCompose L72 | ≈ Stack | Near | code capstone |
| tabular | csv dir | Batch after RAM L58–133 | **08 violation** | Worst | code capstone |
| model | multi dir | DecomposerBatch×6 + TokenEdgeETL | multi-phase | Far | models |
| recipe | 1 file | DecomposerBatch L54 | trivial | Trivial | — |
| chess-pgn | pgn dir | **Dual** L71+L77 | two lanes | Fix L2 | chess |
| chess-analyze | pgn | DecomposerBatch | calculated (08) | OK | chess |
| chess-openings | tsv | **hand** L31–71 | no pipeline | **Must fix** | chess |
| image/audio | — | stub yield break | — | N/A | — |
| etl-decomposer | manifest | StructuredGrammar; NativeGrammar L90–92 | fallback only | Delete dup rows | varies |

## §5 — Dead paths

- `OMWEtlRegistration`, `WiktionaryEtlRegistration` — not CLI-routed
- `PCoreParallelCompose` — tests only
- `etl_witness_conceptnet.c` — parallel to live ConceptNet decomposer

## §6 — Cross-stack duplication (Rule #6)

See plan §6: ContentTierSpine vs `content_witness_batch.c`; parquet Stack/TinyCodes; XML PropBank/VerbNet; tab bridges; FrameNet lemma helpers; Glicko; descent_probe; highway_table.

## §7 — Contaminated doc disproofs

| doc 13 claim | Code |
|--------------|------|
| CILI on PCoreParallelCompose | `CILIDecomposer.cs` DecomposerBatch only |
| CLI special-case dual-lane | `IngestDispatchTable.cs` L32–109 |
| Pipeline chain = invention | 09/05/01 are invention; chain is heuristic |

## §8 — Target (execute on request)

`Decomposer<T>` extract-only; delete dead lanes; extend architecture gate to Chess.

Verify: `SourceIdPinTests` → isolated DB → `seed-step.cmd :verify_step` → 0 novel rows.
