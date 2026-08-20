# Decomposer normalization issue and acceptance ledger

Status date: 2026-08-20

This is the durable work graph for the decomposer normalization campaign. The
campaign-level GitHub tracker is
[#1177](https://github.com/SaltyPatron/Laplace/issues/1177). The implementation
inventory is [DECOMPOSER_NORMALIZATION_STATUS.md](DECOMPOSER_NORMALIZATION_STATUS.md).

This ledger deliberately treats comments, old issue bodies, and design prose as
evidence to verify, not as implementation truth. Status is based on merged code,
tests, current schema, and measured runs. A merged implementation is not the same
thing as production proof; those states are recorded separately.

## Status vocabulary

| State | Meaning |
| --- | --- |
| Delivered | The implementation and a proportionate regression gate are on `main`. |
| Partial | A material slice is on `main`, but the listed acceptance outcome is not complete. |
| Open | The required implementation is not on `main`. |
| Proof pending | The implementation is present, but bounded/live/reseed acceptance evidence is still owed. |

## Governing normalization law

| Information | Primitive |
| --- | --- |
| Literal content | Content-addressed entity/composition |
| Ordered constituents or occurrence coordinates | Trajectory or ordered annotation structure |
| Unordered multi-valued structure | Collection composition |
| One source claiming `(subject, relation, object)` | Attestation |
| Where, when, and by whom a claim was observed | Context/provenance |
| A distinction that changes a proposition's meaning | Subject, object, or semantic composition; never context alone |
| Opaque source identifier or serialization token | Typed reference/encoding, not text content |
| Deterministic consequence | Calculation or perfcache |
| Reusable statistical projection | Explicit, traced, evictable materialization |
| Ordered witness history | Testimony trajectory after proposition grain is correct |

Two negative rules bind every row below:

1. A decomposer records an irreducible source observation at the grain where it is
   true. It does not precompute every question a future reader might ask.
2. The generic pipeline owns execution. A vendor implementation owns source-specific
   discovery, parsing, and composition, not a private thread pool, batching policy,
   retry loop, journal, or database apply protocol.

## Campaign scorecard

| ID | Outcome | State | Merged evidence | Remaining owner and completion gate |
| --- | --- | --- | --- | --- |
| DN-01 | One generic single-file/multi-file execution pipeline | Delivered | #1144 introduced shared largest-first multi-file scheduling; #1166 centralized sizing; #1173 moved model phase fanout into the shared primitive; #1184 allocates a skewed many-file first wave across the compose pool; #1188 removes Syzygy's final private decomposer scheduler. #1203/#1204 remove the remaining vendor/fixed batch, queue, probe, cache, resume, marshal, and I/O limits so single-file, multi-file, model, media, grammar, UD, chess, and SemLink all consume the same resource-derived backpressure plan. Per-file journal boundaries exist only where real files exist. | [#967](https://github.com/SaltyPatron/Laplace/issues/967), [#605](https://github.com/SaltyPatron/Laplace/issues/605): preserve the zero-vendor-scheduler ratchet and prove bounded source profiles; non-ingest service concurrency is explicitly outside this invariant. |
| DN-02 | Safe parallel compose, batching, backpressure, cancellation, and failure propagation | Delivered for compose; apply proof pending | `DecomposerMultiFile<T>` runs one file parser per claimed file through the bounded generic pool; shared phase fanout and source profiles own capacity. #1184 preserves one journal/resume boundary per real file while record-aligned segments borrow spare compose lanes. | [#967](https://github.com/SaltyPatron/Laplace/issues/967): outer database apply dispatch remains intentionally `1` until claim-before-COPY and working-set ownership are race-safe. |
| DN-03 | Witness-source boundaries cannot mix inside an apply | Delivered | #1146 splits changes by `Metadata.SourceId`, fixing the SemLink/PredicateMatrix crash without falsifying their independent provenance. | [#1153](https://github.com/SaltyPatron/Laplace/issues/1153): retain independent provenance while finishing bridge coverage and source receipts. |
| DN-04 | Per-file journals, restart true-skip, and canonical readback | Delivered; live proof pending | #898 and #1019 are closed after code verification; #1160 persists dynamic canonical names before a file completion marker. Kill/restart and batched-probe fixtures are on `main`. | [#433](https://github.com/SaltyPatron/Laplace/issues/433): run a real concurrent kill/restart campaign and prove no fold activity for completed files. Monolith byte-offset checkpoints remain outside the per-file contract. |
| DN-05 | Input, file, and progress counters use one declared grain | Delivered | #960 is closed; #1146 separates record/unit progress from file completion, FrameNet now uses one file pool instead of three nested passes, #1184 refines aggregate multi-file samples, #1189 raises underestimated live denominators to the observed floor, and #1193 estimates CILI at parsed-record rather than raw Turtle-line grain. PredicateMatrix now admits one selected source row as one generic-pipeline record and estimates that same language-filtered population: 426,696 rows unfiltered in the current v1.3 package. The SemLink orchestrator now sums exact JSON top-level records, admitted XML role mappings, and PredicateMatrix rows instead of bypassing phase estimates with physical-line counts. | [#1175](https://github.com/SaltyPatron/Laplace/issues/1175): preserve unit labels and the observed-floor invariant in every common receipt. |
| DN-06 | Whole-process memory bounds scale with concurrency | Delivered for current compose/apply width; proof pending | #993 is closed; the compose envelope is divided across concurrent working sets, and #1159 sizes each builder to retained records. #1203 centralizes fold/apply/cache byte plans; #1204 replaces the historical RAM/16, 4-GiB apply, and 512-MiB compose clamps with actual apply partitions plus the simultaneously-live ownership classes, and divides every queue/cache/I/O/COPY window from that same envelope. #1205 adds the Wiktionary vendor-reuse cache to those resident owners and derives Tatoeba's lazy dense-map chunk from that owner's byte share instead of fixed chunk/outer-array counts. #1207 removes the remaining first-party file-buffer constants, parser-size caps, generic 256/1,024 batch defaults, and Wiktionary's vendor-local worker clamp in favor of the same topology authority. #1209 accounts PostgreSQL shared/private memory, the ingest client, and OS page cache in one conserved plan so the working-set allocator cannot promise PostgreSQL's RAM to the client again. | [#967](https://github.com/SaltyPatron/Laplace/issues/967), [#588](https://github.com/SaltyPatron/Laplace/issues/588): measure RSS/high-water per source and re-evaluate the budget before widening apply concurrency. |
| DN-07 | Index, probe, COPY, and database round-trip efficiency is measured rather than inferred | Open/partial | #1145 coalesces tier probes; #1168 removes duplicate root probes; #1167 exposes apply/COPY concurrency honestly; #1183 removes overlapping highway-mask writers and separates fold drain from index maintenance; #1186 keeps production indexes online; #1193 removes 352,912 duplicate CILI packaging lines before composition on the current vault; #1196 removes an 815-second exact content scan from automatic PGN completion while keeping post-load statistics and GIN maintenance online. #1203 changes consensus from UPDATE+INSERT double probing to one literal-routed PostgreSQL 18 `MERGE`; #1204 gives tier probes, COPY windows, journals, and fold/mask leases byte-derived widths and removes the capped-smoke watchdog. #1205 removes the remaining vendor-specific Tatoeba/Wiktionary count thresholds and keeps delay-free coalescing scheduler-independent. #1206 removes the global SQL advisory lock that was serializing those otherwise-disjoint mask shards, retaining deterministic row acquisition for external overlap. #1208 adds the exact greenfield dynamic-consensus index, and #1209 replaces the remaining present-attestation `UPDATE ... FROM` with one array-backed routed `MERGE`. #1232 removes a measured 119-MB, zero-scan mask GIN after correcting its four reader dependencies. #1233 consolidates the measured subject covering/rank pair into one greenfield ranked covering index without sacrificing the top-k access path. #1234 removes the 9,534-cell and 114,408-attestation fold scheduling gates while retaining only resource-derived command residency. #1235 removes hidden result truncation and the disabled synthetic round-trip budget; native frontier storage now follows actual result cardinality and PostgreSQL's allocation contract. Clean OMW run 32313436484 completed green in 338 seconds versus 716.2 seconds. | [#588](https://github.com/SaltyPatron/Laplace/issues/588), [#429](https://github.com/SaltyPatron/Laplace/issues/429), [#860](https://github.com/SaltyPatron/Laplace/issues/860), [#871](https://github.com/SaltyPatron/Laplace/issues/871), [#908](https://github.com/SaltyPatron/Laplace/issues/908), [#1008](https://github.com/SaltyPatron/Laplace/issues/1008): profile remaining avoidable probes, index versus index-only eligibility, pruning, skew, and bytes per input. SQL semantics remain owned by the SQL audit workstream. |
| DN-08 | LapSight run/source/relation amplification and convergence | Partial | #1148 added exact terminal counters and initial admin amplification; #1170 separated bootstrap/payload accounting; #1172 added fold deposits and observations per deposit; #1183 records consensus-drain and writer-maintenance phases/timings; #1184 fixes sampled multi-file refinement; #1189 prevents impossible live percentages and adds upper-bound relation gates; #1196 adds post-ingest ANALYZE/GIN/summary timing and removes full-corpus exact scans from the hot receipt path. #1204 keeps planner row estimates as diagnostics while source coverage and semantic probes remain the blocking eval evidence. #1234 persists first-fold-to-drain span, summed consensus and highway-mask backend work, SQL call counts, and mask-pair volume so a long terminal phase can be attributed instead of guessed. | [#1175](https://github.com/SaltyPatron/Laplace/issues/1175), [#1080](https://github.com/SaltyPatron/Laplace/issues/1080): add novel consensus, singleton/witness distributions, trajectory vertices, bytes, identity classes, partition/index pressure, alarms, and one UI/API/CI receipt. |
| DN-09 | Global entity identity is independent of tier | Open | Application builders deduplicate by ID, but PostgreSQL still declares `PRIMARY KEY (id, tier)` and `PARTITION BY LIST (tier)`. | [#1008](https://github.com/SaltyPatron/Laplace/issues/1008), [#1132](https://github.com/SaltyPatron/Laplace/issues/1132): enforce one stored logical entity per ID and validate zero cross-tier duplicates after reseed. |
| DN-10 | Content, governed vocabulary, semantic concept, source reference, occurrence, and annotation identities are explicit | Partial | #1146 introduced admission classification; #1149 introduced typed foundation references; #1154/#1157 extended exact lexical identity; #1192 adds class-bound lexical-member identity; #1193 distinguishes CILI semantic concepts from instances without changing their governed ILI reference identity. | [#1038](https://github.com/SaltyPatron/Laplace/issues/1038), [#1041](https://github.com/SaltyPatron/Laplace/issues/1041), [#1153](https://github.com/SaltyPatron/Laplace/issues/1153): finish the identity classes and reject unexplained off-DAG physical-content entities. |
| DN-11 | Context never carries the only proposition-defining distinction | Partial | #1152 scopes PropBank roles, VerbNet predicates, FrameNet roles, SemLink, and PredicateMatrix endpoints to their owners. | [#1153](https://github.com/SaltyPatron/Laplace/issues/1153): complete the per-source scope audit and add a context-collapse gate for every meaning-bearing dimension. |
| DN-12 | Tokenizer and serialization encodings do not mint phantom content | Partial | #1014 is closed: SentencePiece `▁`, GPT `Ġ`, WordPiece `##`, byte tokens, and newline spellings decode to canonical bytes with structural roles. #1186 moves OMW onto bounded direct-line parsing so neither a raw-row AST nor a TSV packaging content tree precedes the selected semantic value. #1187 gives Tatoeba a direct record parser and gives SemLink/generic ETL an AST-witness-only handler, with gates requiring zero packaging probes/physicalities. | [#1042](https://github.com/SaltyPatron/Laplace/issues/1042), [#1153](https://github.com/SaltyPatron/Laplace/issues/1153): finish whitespace/layout admission, model sidecars, checkpoint/tensor coordinates, and artifact provenance. |
| DN-13 | Wiktionary preserves first-class sense bundles | Partial | #1154 moves definitions, examples, semantic relations, registers, POS/language, and external concept links from the ambiguous spelling to a source sense identity; collection morphology remains one set-valued claim. | [#1153](https://github.com/SaltyPatron/Laplace/issues/1153), [#1178](https://github.com/SaltyPatron/Laplace/issues/1178): translation language/sense scope, spans/provenance, pronunciation/audio, artifact identity, and normalized sense-aware reads. |
| DN-14 | WordNet/OMW/CILI preserve exact concepts, senses, references, and release provenance | Partial | #1157 preserves full WordNet sense keys and removes duplicate lemma-to-synset membership testimony; #1149 fixes foundation reference admission; #1193 preserves CILI Concept/Instance, native PWN source mapping, CC-BY/release metadata, canonical version aliases, and one lossless package per mapping version. | [#1153](https://github.com/SaltyPatron/Laplace/issues/1153): lexical pointer scope, adjective position, examples/frames, WN-LMF/OEWN/OMW packages, version/license/confidence, and post-seed CILI coverage receipt. |
| DN-15 | UD stores exact sentence/token annotation occurrences rather than corpus projections onto word types | Delivered for exact parse structure; policy/read proof pending | #1158 stores one ordered parse occurrence carrying token ordinals, forms, lemmas, UPOS/XPOS, FEATS collections, heads, enhanced dependencies, MWT, and MISC. | [#548](https://github.com/SaltyPatron/Laplace/issues/548), [#1176](https://github.com/SaltyPatron/Laplace/issues/1176), [#1178](https://github.com/SaltyPatron/Laplace/issues/1178): namespace XPOS, define derived type-level grammar promotion, prove repeated-token readback, and run post-merge UD. |
| DN-16 | FrameNet, PropBank, VerbNet, PredicateMatrix, and SemLink preserve occurrence and owner scope | Partial | #1152 binds roles/predicates to owning structures; #1161 retains FrameNet target spans and attests the occurrence; #1191 reads native VerbNet `fn_mapping`; #1192 gives every member a class-bound identity and preserves its WordNet, FrameNet, and multi-roleset PropBank mappings there; #1194/#1195 make one selected PredicateMatrix row one generic-pipeline record and retain the semantic information from all 27 native columns across all six language/POS populations, with first-class source predicate/role identity, PB argument alignment, package-repeat suppression, exact phase inventory, and CC-BY-3.0/version/citation metadata. | [#1153](https://github.com/SaltyPatron/Laplace/issues/1153): FrameNet FE layers/semantic types/core sets/valence, PropBank aliases/lexlinks/spans, and VerbNet ordered syntax/restrictions/complete arguments remain. |
| DN-17 | ConceptNet node declarations do not amplify with graph degree, while direct-triple sources remain direct | Delivered for ConceptNet; preservation proof pending | #1162 emits intrinsic node metadata once and uses semantic URI endpoints. Atomic remains one source triple to one testimony; Tatoeba continues to discard numeric scaffolding in favor of content roots. | [#1175](https://github.com/SaltyPatron/Laplace/issues/1175), [#1178](https://github.com/SaltyPatron/Laplace/issues/1178): degree-amplification receipt and normalized reader coverage. |
| DN-18 | OpenSubtitles preserves aligned occurrence structure before semantic promotion | Delivered for storage shape; benchmark/promotion pending | #1163 replaces unconditional `IS_TRANSLATION_OF` deposits with bounded paired trajectories and exact source ranges. | [#1176](https://github.com/SaltyPatron/Laplace/issues/1176), [#1175](https://github.com/SaltyPatron/Laplace/issues/1175), [#1178](https://github.com/SaltyPatron/Laplace/issues/1178): benchmark a bounded corpus against the old shape, prove query parity, define promotion, then decide admission for the 601M-pair corpus. |
| DN-19 | Chess separates physical mechanics, historical playing, and derived art-of-chess analytics | Partial | #1165 removes exact MOVE and per-position OUTCOME testimony recoverable from the playing line trajectory. Live run 32297973379 ingested 190,705 games in 1,065 seconds and replayed as a seven-second no-op; #1189 changes the acceptance gate from requiring MOVE to requiring zero MOVE consensus. Run 32300064613 exposed roughly 2,042 fold observations/game; exact attribution found 771 `ChessAnalysis` OUTCOME rows carrying 1,698,648,916 observations. Analyzer v3 removes that per-ply substructure projection across PGN, live, and learned games and restricts engine evaluation to its exact position. The writer now treats `PLAYS_LINE` as a categorical occurrence/content join, records the result once, and stops materializing exact-line OUTCOME. A move is a bounded operator; `(position, move) → position` lives in the transition perfcache, while occurrence/order lives in the line physicality. The chess CI/CD surface now exposes `chess-analyze`, exact-confirmation source eviction, and resolver parity validation so v2 can be replaced without evicting PGN playings. | [#838](https://github.com/SaltyPatron/Laplace/issues/838), [#1176](https://github.com/SaltyPatron/Laplace/issues/1176), [#1178](https://github.com/SaltyPatron/Laplace/issues/1178): run and measure the v3 source recovery, migrate legacy PGN line outcomes on the next source reseed, finish live/PGN identity and history-sensitive state, define structural-statistic promotion, and publish deterministic transitions/evaluations through perfcache. |
| DN-20 | Ordered media structures use trajectories without duplicating adjacency testimony | Partial | #1169 stores video frames as one trajectory and removes structural `HAS_FRAME`/`PRECEDES_IN_TIME` rows and double decoding. | [#1134](https://github.com/SaltyPatron/Laplace/issues/1134), [#1153](https://github.com/SaltyPatron/Laplace/issues/1153): exact image/audio/document/model reconstruction, tensor/checkpoint composition, and artifact receipts. |
| DN-21 | Reader paths consume normalized structures instead of storage compatibility lies | Open/partial | `bubble_up` already uses trajectory geometry for contextual neighborhood; WordNet readers already understand a surface→sense→synset chain; #1192 makes concept membership/peers, prompt routing, and mesh reads traverse content aliases to source-owned lexical members. | [#1178](https://github.com/SaltyPatron/Laplace/issues/1178): bank/fork sense routing, UD/FrameNet occurrence reads, chess projection, subtitle alignment, containment, and remaining entry-surface parity before deleting shortcuts. |
| DN-22 | Raw occurrence retention is separate from statistical promotion | Open | Normalized occurrence structures now exist for UD, FrameNet, subtitles, and chess, but promotion is not governed. | [#1176](https://github.com/SaltyPatron/Laplace/issues/1176): explicit promotion triggers, dependency lineage, trace, eviction, rebuild, and fallback reads. |
| DN-23 | Evidence storage virtualizes witness history only after proposition grain is correct | Open/design | Existing rows retain exact fold inputs; no testimony virtualization has landed. | [#451](https://github.com/SaltyPatron/Laplace/issues/451): one fact plus ordered witness history preserving source/context/outcome/count/score/opponent uncertainty/time and non-commutative order. |
| DN-24 | Clean bounded and full reseed proves correctness and speed | Proof pending | The pre-drop totals remain the before baseline. Clean OMW run 32313436484 completed every gate in 338 seconds versus the comparable 716.2-second pre-fix receipt (2.12x). Post-#1206 Atomic run 32316303092 completed the payload in 263 seconds versus 418 seconds (37% faster), with exact evidence/relation gates green; only `HasLayerCompleted` failed because `--force` incorrectly suppressed the terminal marker as well as bypassing the pre-run guard. The completion policy is now split and awaits one clean gate receipt. Chess run 32297973379 ingested 190,705 games in 1,065 seconds and replayed in seven seconds. Run 32300064613 ingested another 655,255 games in 3,655.1 seconds, folded 1,338,361,912 observations into 22,707,034 cells, and classified every nonphysical identity (`unexplained=0`); it also exposed the 815-second exact post-ingest scan removed by #1196. Its roughly 2,042 observations/game were subsequently attributed to the v2 per-ply substructure OUTCOME projection rather than to generic fold overhead. | [#433](https://github.com/SaltyPatron/Laplace/issues/433), [#761](https://github.com/SaltyPatron/Laplace/issues/761), [#1132](https://github.com/SaltyPatron/Laplace/issues/1132): prove Atomic's corrected layer marker, evict/rederive analyzer v3 and prove the reduction, then complete bounded fixtures, reader parity, row/byte amplification, and the admitted full-seed envelope. |

## Source disposition

| Source/lane | Current disposition | Tracker |
| --- | --- | --- |
| Unicode/ISO foundation | Preserve as reference spines; verify admission and amplification. | #1038, #1042, #1175 |
| Wiktionary | Sense identity delivered; complete native fields and readers. | #1153, #1178 |
| WordNet | Exact sense identity delivered; complete pointer/native-schema fidelity. | #1153 |
| OMW | Preserve language-scoped lexical testimony; stop composing TSV packaging as content; add package/release provenance and modern adapters. | #1153, #1175 |
| CILI | Typed references plus Concept/Instance, native source maps, canonical package versions, and license/release metadata delivered; remeasure and capture coverage. | #1153, #1175 |
| UD | Exact parse occurrence delivered; namespace XPOS and govern derived grammar promotion. | #548, #1176, #1178 |
| FrameNet | Exact target span delivered; complete FE/semantic/valence schema. | #1153, #1178 |
| PropBank | Scoped role slots delivered; complete aliases, mappings, confidence, and example spans. | #1153 |
| VerbNet | Scoped predicate and member identity, native `fn_mapping`, and PropBank grouping delivered; preserve ordered syntax, restrictions, features, polarity, and queryable complete predicate arguments. | #1153, #1178 |
| PredicateMatrix/SemLink | Independent witness boundary, scoped endpoints, multilingual source predicate/role identity, the semantic information from all 27 PredicateMatrix columns, source-row pipeline grain, package-repeat suppression, exact phase-grain inventory, and license/version receipt delivered; JSON pairing remains parser structure rather than content. | #1153, #1175 |
| MapNet/WFN/XWFN | Preserve bridge provenance; stop silently dropping unresolved historical targets. | #1153 |
| ConceptNet | Semantic endpoints and one-time node declarations delivered. | #1175, #1178 |
| Atomic2020 | Preserve direct triple ingestion; audit, do not trajectory-ize by convention. | #1175 |
| Tatoeba | Content-root identity preserved; direct TSV parsing discards row packaging and external numeric scaffolding. | #433, #1175 |
| OpenSubtitles | Paired trajectories delivered; benchmark and promote selectively before full ingest. | #1175, #1176, #1178 |
| Chess | MOVE, exact-position OUTCOME, and eager substructure OUTCOME projections removed; recover v3 and finish promotion/perfcache policy plus live proof. | #838, #1176, #1178 |
| Model | Token serialization decoding delivered; checkpoint/tensor/artifact fidelity remains. | #1153 |
| Video | Ordered frame trajectory delivered. | #1134, #1175 |
| Image/audio/document | Keep generic content identity; finish exact recipe/reconstruction and layout contracts. | #1042, #1134, #1153 |
| Syzygy | Catalog semantics preserved; #1188 routes unpack through generic bounded fanout. | #605, #967 |

## Required bounded acceptance suite

These gates are not optional examples. They are the smallest falsifiers for the
normalization laws and are owned by #1175/#1178 unless a more specific issue is
listed.

- `bank`: one surface ID, isolated river and finance senses, context-sensitive path.
- `fork`: one surface ID, distinct chess and cutlery concepts.
- Wiktionary sense isolation: definitions/examples/relations cannot cross senses.
- UD repeated tokens: every ordinal and dependency round-trips exactly.
- UD FEATS: one collection composition per morphological analysis.
- FrameNet repeated target text: the annotated span remains distinguishable.
- Context collapse: meaning-bearing role/sense/class changes must change semantic
  identity even when context folding would otherwise collide.
- Opaque references: ILI, synset, sense, roleset, class, release, split, and tokenizer
  serialization keys cannot silently become ordinary text content.
- Tokenizer parity: alternate serialization spellings converge on the same decoded
  content while structural boundary roles remain available.
- Entity/tier uniqueness: zero content IDs occupy multiple physical entity rows.
- ConceptNet degree: 100 incident relations do not create 100 language/POS witnesses.
- Direct versus derived witness: compatibility paths cannot double-vote one source row.
- Chess trajectory recovery: every removed transition/outcome projection is exactly
  reconstructable from the irreducible playing representation.
- Chess live versus PGN: identical content converges while occurrence provenance stays
  distinct where appropriate.
- OpenSubtitles bounded equivalence: frequency, language, source range, alignment, and
  surrounding context remain answerable without pairwise row explosion.
- Resume/cancellation: completed files true-skip with zero refold, the interrupted file
  is bounded, and journal/UI state is truthful.

## Release sequence

1. Finish generic execution and identity/schema prerequisites (#967, #1008, #1038,
   #1041, #1042).
2. Complete native source fidelity and coverage receipts (#1153).
3. Make normalized readers pass before removing any remaining compatibility edge
   (#1178).
4. Define promotion/eviction and then witness virtualization (#1176, then #451).
5. Complete LapSight machine-readable gates (#1175/#1080).
6. Install/deploy the merged extension and run bounded fixtures.
7. Start from a clean database and run the full campaign (#433/#761/#1132).
8. Publish the before/after receipt: information retained, input and row counts,
   entities, physicalities, attestations, novel consensus, singleton distribution,
   trajectory vertices, bytes, throughput, restart correctness, reader parity, and
   partition/index pressure.

The campaign is not complete at “all PRs merged.” It is complete when DN-01 through
DN-24 have delivered implementation and the proof-pending rows have current measured
evidence from the normalized clean seed.
