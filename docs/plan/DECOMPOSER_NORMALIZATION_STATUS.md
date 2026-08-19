# Decomposer normalization campaign status

Status date: 2026-08-19

This is the integration inventory for the decomposer campaign. It records what is
on `main` and what still requires product work. A pull request is not counted as
delivered until it is merged.

## Tracking authority

- [DECOMPOSER_NORMALIZATION_ISSUE_LEDGER.md](DECOMPOSER_NORMALIZATION_ISSUE_LEDGER.md)
  maps every expected outcome to merged evidence, an owning GitHub issue, and a
  falsifiable completion gate.
- [GitHub #1177](https://github.com/SaltyPatron/Laplace/issues/1177) is the campaign
  completion graph. #1175 owns LapSight, #1176 owns derived-consensus promotion and
  eviction, #1178 owns normalized readers, and #1153 owns native source fidelity.
- [SUBSTRATE_COHESION_STATUS.md](SUBSTRATE_COHESION_STATUS.md) and its
  [issue ledger](SUBSTRATE_COHESION_ISSUE_LEDGER.md) own the shared SQL/schema,
  content-identity, modality/media, operation-surface, extension, and reseed gates.
- Historical comments and issue descriptions are inputs to verification, not the
  status authority. Current code, tests, schema, and measured runs decide status.

## Governing laws

- Content is a composition; order is a trajectory; unordered multi-value state is
  a collection.
- Attest only a source claim. Keep provenance in context, but never use context as
  the sole carrier of a proposition-defining distinction.
- Preserve occurrence identity and coordinates instead of projecting occurrences
  onto reusable types.
- Resolve opaque source references as governed identities rather than decomposing
  their serialization as text.
- Calculate deterministic consequences and materialize evictable aggregates only
  when they are useful; do not store query-convenience edges as testimony.
- The generic ingest pipeline owns scheduling, backpressure, batch sizing, source
  boundaries, journals, and telemetry. Vendor decomposers supply source-specific
  enumeration, parsing, and composition.

## Landed on main

| Area | Delivery |
| --- | --- |
| Generic multi-file execution | #1144 schedules files largest-first and coalesces compose fragments by the real apply bounds; #1145 coalesces concurrent tier probes; #1184 divides the first LPT wave across the compose pool so one dominant file can be segmented even when the corpus contains more files than workers. |
| Admission gates | #1146 splits applies at witness-source boundaries, fixes record/file accounting grain, and distinguishes physical content from lawful governed identities. |
| Fresh seed lifecycle | #1147 makes deferred-index recovery run for every fresh foundation build. |
| Initial LapSight accounting | #1148 persists exact terminal input/file counters and exposes raw per-source row amplification in the admin ingest view; #1183 separates consensus drain from writer/index maintenance; #1184 fixes aggregate multi-file denominator refinement. |
| Reference admission | #1149 introduces typed reference identities across the foundation lexical resources instead of text-composing opaque IDs. |
| Partition count visibility | #1150 fixes partitioned estimates and includes physicalities in terminal `ANALYZE`. |
| ISA ratchet repair | #1151 restores the merged-main gate after the reference cleanup without relaxing the policy. |
| Proposition identity | #1152 binds PropBank, VerbNet, FrameNet, SemLink, and PredicateMatrix roles/predicates to their owning semantic structures instead of context or global labels. |

## Integrated in the 2026-08-19 batch

| PR | Area | Result |
| --- | --- | --- |
| #1154 | Wiktionary | Preserves first-class sense bundles and moves glosses, examples, relations, registers, language, and external concept links off the ambiguous surface. |
| #1157 | WordNet | Preserves exact sense keys and removes duplicate lemma-to-synset membership testimony. |
| #1158 | UD | Stores one exact ordered parse annotation per sentence, including token ordinals, dependencies, FEATS, MWT, and MISC, rather than spraying occurrence facts onto word types. |
| #1159 | Builder capacity | Sizes each concurrent working-set builder to the records it can retain. |
| #1160 | Per-file resume | Persists dynamic canonical names before a file journal is marked complete. |
| #1161 | FrameNet | Preserves exact annotated spans and targets `EVOKES_FRAME` at the occurrence structure. |
| #1162 | ConceptNet | Declares intrinsic node metadata once and uses sense-bearing semantic endpoints for relation testimony. |
| #1163 | OpenSubtitles | Replaces one semantic deposit per aligned pair with bounded paired ordered trajectories and exact source ranges. |
| #1165 | Chess | Removes exact MOVE/position-outcome testimony already recoverable from playing line trajectories. |
| #1166 | Sizing authority | Removes vendor-local batch defaults and protects the central sizing authority with an architecture gate. |
| #1167 | Concurrency telemetry | Reports available I/O workers, actual outer apply dispatch width, and internal COPY partitions separately. |
| #1168 | Presence probes | Avoids probing a deferred content root twice during tier descent. |
| #1169 | Video | Stores one ordered frame trajectory and removes structural `HAS_FRAME`/`PRECEDES_IN_TIME` testimony and double decoding. |
| #1170 | Bootstrap accounting | Includes initialization writes in run totals and entity admission, separately from payload amplification. |
| #1171 | Bootstrap ownership | Stops vendors from witnessing deterministic governed relation hierarchy. |
| #1172 | LapSight fold metrics | Emits run-local payload/bootstrap amplification and observations-per-cell-deposit from the generic runner. |
| #1173 | Generic parallel fanout | Moves model layer fanout, backpressure, cancellation, and failures into a shared generic pipeline primitive. |
| #1183 | Fold tail and completion phases | Replaces overlapping highway-mask writers with stable entity-sharded FIFO lanes and records live/terminal consensus-drain versus writer-maintenance timing. |
| #1184 | Skewed multi-file execution | Lets the generic pool segment a dominant file inside a many-file corpus, corrects aggregate sampled-inventory refinement, and isolates the process-wide signal test that cancelled unrelated integration tests. |
| #1186 | Online-index and OMW admission policy | Removes automatic index dropping/rebuilding from production ingest and makes OMW parse bounded UTF-8 records directly so only lemma/definition/example values enter the content spine. |
| #1187 | Structured ETL admission | Keeps SemLink/generic grammar ASTs parser-only and moves Tatoeba numeric/TSV scaffolding outside the content spine. |
| #1188 | Final vendor scheduler | Routes Syzygy board enumeration through the bounded generic fanout, leaving only non-ingest service concurrency on the scheduler allowlist. |
| #1189 | Live receipt correctness | Raises sampled denominators to the observed floor, caps UI progress at 100%, forbids reintroduced chess `MOVE` consensus, and ratchets production foundation ingest to keep indexes online. |
| #1190 | Eval receipt identity | Scores the actual elected topic token used by production orientation, reports its selected sense separately, and treats the baseline source roster as a required floor rather than a brittle exact snapshot of a concurrently seeded database. |
| #1191 | VerbNet native FrameNet mapping | Reads the actual VerbNet 3.4 `fn_mapping` field, including its multi-frame shape and `None` sentinel, instead of the nonexistent `fnframe` attribute that dropped every direct mapping. |
| #1192 | VerbNet member grain | Preserves class-bound `VerbNet_Member` identity from native member keys, moves WordNet/FrameNet/PropBank mappings off shared lemma content, and makes concept/prompt/mesh readers traverse the member's content alias instead of requiring duplicate surface testimony. |
| #1193 | CILI native grain and packaging dedupe | Preserves Concept versus Instance, native PWN source mapping, license/release metadata, and canonical map-reference identity while selecting one lossless serialization per version instead of treating duplicate Turtle/tab exports as witnesses. |
| #1194 | PredicateMatrix pipeline grain | Makes one selected native PredicateMatrix row one generic-pipeline record instead of projecting each input into a private multi-change lane. |
| #1195 | PredicateMatrix native identity | Preserves all six language/POS populations, predicate-bound roles, PB/FN/VN/WN/MCR/ESO semantics across the 27-column row, package-repeat suppression, exact phase-grain inventory, and a measured source profile. |
| #1196 | Bounded post-ingest completion | Keeps UI/planner `ANALYZE`, GIN draining, and estimated totals in the automatic path while moving unbounded exact source scans to explicit `stats <source>`; emits `LAPSIGHT_POST_INGEST` phase timing. |
| #1198 | Registered grammar execution | Routes structured decomposer formats through their registered grammar contracts instead of private parser/batching paths. |
| #1200 | Chess result/trajectory grain | Records the playing result once, makes `PLAYS_LINE` a categorical occurrence/content join, and removes exact-line outcome materialization. |
| #1201 | Chess recovery surface | Exposes source-scoped analyzer eviction/rederive through the reusable CI/CD ingest path. |
| #1202 | Source-scoped chess gates | Validates the evidence owned by the named decomposer instead of letting unrelated global consensus satisfy or fail its gate. |
| #1203 | Resource-derived ingest and fold | Removes vendor batch defaults and fixed 65,536-cell/fold/cache/COPY limits; derives the generic pipeline from RAM, CPU topology, and row transit width; changes consensus upsert to one partition-routed PostgreSQL 18 `MERGE`. |
| #1204 | Residual limiter and eval cleanup | Removes remaining fixed queue/cache/probe/journal/I/O/resume/marshal limits, replaces the 4-GiB/512-MiB working-set clamps with topology ownership, makes mask/fold scheduling work-conserving, and stops planner row-count shrink from invalidating semantic evals. |

The related source-fidelity audit (#1155), durable working agreement (#1156), and
canonical lexical-peer batch core (#1164) were integrated in the same batch,
although they are not decomposer implementations. Campaign-summary PR #1182 is now
merged as the durable substrate-cohesion companion ledger; it is not counted as
delivered decomposer implementation.

## Latest live acceptance receipt

- [Clean OMW run 32313436484](https://github.com/SaltyPatron/Laplace/actions/runs/32313436484)
  completed ingest in 338 seconds with every journal, throughput, health, layer, and
  `HAS_LANGUAGE` gate green. The comparable pre-fix clean receipt was 716.2 seconds:
  2.12x faster while indexes remained online. This closes the requested post-#1186 OMW
  remeasurement; PR #1204's larger topology-owned envelope still requires its own live run.
- [Clean Atomic2020 run 32314149469](https://github.com/SaltyPatron/Laplace/actions/runs/32314149469)
  completed ingest in 418 seconds with every journal, throughput, health, layer, and relation
  gate green. That is effectively flat against the prior 414-second receipt and isolates its
  remaining cost to consensus/mask processing rather than producer throughput. #1204 changes
  that tail's long-lived mask connection leases; the post-deploy rerun is the proof gate.

- [Chess run 32297973379](https://github.com/SaltyPatron/Laplace/actions/runs/32297973379)
  ingested 190,705 games in 1,065 seconds, then journal replay completed as a seven-second
  no-op. The run declared 11,783,549 novel entities, 11,142,803 physicalities, 3,018,585
  source attestations, and 6,340,104 folded cells from 399,069,276 observations.
- Its 640,746 entity/physicality delta was fully classified as governed nonphysical
  identity (`unexplained=0`), rather than an off-DAG content leak.
- Every semantic gate passed except the stale requirement for `MOVE > 0`. #1189 reverses
  that obsolete assertion: `MOVE` consensus must remain zero because #1165 made the exact
  transition recoverable from the playing trajectory. Playing, players, result, and
  outcome gates remain positive.
- The live extractor exceeded its sampled 189,852-game estimate and briefly reported
  100.4%. #1189 makes observed progress a monotonic denominator floor and caps percentage
  rendering defensively, so future receipts cannot report impossible progress.
- The earlier #1183 integration red was an unrelated test receiving process-wide
  cancellation; #1184 isolated that signal test and both later runs passed integration.
  The #1184/#1185 eval reds then exposed a receipt identity bug: token `hot` selected its
  OMW `beautiful`/attractive sense, and the harness mislabeled the sense as the topic even
  though production returns token `hot`. #1190 scores the topic and retains the sense as
  diagnostics without weakening exact election or forward-answer gates.
- [Chess run 32300064613](https://github.com/SaltyPatron/Laplace/actions/runs/32300064613)
  then ingested the 1970--1989 file: 655,255 games in 3,655.1 seconds, 37,360,190
  novel entities, 35,199,320 physicalities, and 1,338,361,912 observations folded into
  22,707,034 cells. The run classified the complete entity/physicality delta
  (`unexplained=0`), and journal, replay, and throughput gates passed.
- That run exposed a separate completion-envelope defect: after the payload and fold
  completed, `ops.content_count(source)` spent another 815 seconds in `DataFileRead`.
  The read-only diagnostic was cancelled without affecting the committed ingest. #1196
  removes exact full-corpus source scans from automatic completion while retaining the
  statistics refresh that keeps UI counts truthful.
- Both PGN workflow conclusions are red only because they checked out #1186, whose
  gate still required `MOVE > 0`; the actual playing/result/outcome checks passed.
  Current `main` (#1189) requires `MOVE = 0`, matching the normalized trajectory law.
- Relation attribution identified the remaining 2,042-observations/game signal rather
  than accepting it as fold overhead. The live `ChessAnalysis` source contained only
  771 distinct `OUTCOME` evidence rows carrying 1,698,648,916 observations: every game
  result had been projected onto every closed-vocabulary position substructure at every
  ply. Analyzer v3 removes that eager projection from PGN analysis, live games, and the
  turn learner. It also keeps engine evaluation at the exact evaluated position instead
  of projecting one evaluation onto all 25--36 constituents.
- The storage law is now explicit. A position is reusable state content. A move is the
  bounded piece/from/to/flags/promotion operator. Applying that operator to a position is
  a transition resolved by `ChessTransitionFloor`; its occurrence is the adjacent step at
  an ordinal in the line physicality. `PLAYS_LINE` is therefore a categorical
  playing-to-content join, not a scored edge. `HAS_RESULT` witnesses the playing's result
  once. Raw ingest no longer creates an exact-line OUTCOME cell; move, line, position, and
  substructure performance are recovered by joining the playing's result to its trajectory,
  then promoted only when a reusable statistical product is deliberately requested.
- Player and head-to-head ratings plus the small think-class vocabulary remain bounded,
  explicitly reusable statistical projections. They are not used to reconstruct which
  move occurred. The line-detail and opening-game readers now count actual `PLAYS_LINE`
  occurrences even when no legacy/promoted line OUTCOME cell exists. The aggregate
  opening leaderboard still depends on an explicit promotion and remains a #1178 reader
  migration rather than introducing another hand-written relation lookup here.
- Existing v2 analysis evidence must be evicted by source before v3 is derived. Re-running v3 on
  top of v2 would leave the obsolete observations in place and only add a new analysis
  marker. The recovery order is therefore `evict ChessAnalysis` followed by
  `ingest chess-analyze`; the PGN witness source and playing trajectories remain intact.
  Legacy line OUTCOME rows belong to the PGN source and therefore remain in the current
  database until a PGN-source eviction/reseed; readers treat them as optional promotion,
  never as the authoritative occurrence count.
- `seed-chess.yml` now exposes `chess-analyze` and forwards the reusable ingest
  workflow's exact-confirmation eviction controls. The same repair makes every source
  advertised by the chess dispatch UI reachable by its resolver; policy validation now
  fails if a future advertised chess source is rejected before dispatch. This provides
  one atomic CI/CD recovery run: evict only `ChessAnalysis`, then force its complete v3
  rederive from the surviving PGN playing/trajectory evidence.
- Chess acceptance thresholds are source-scoped evidence checks. This prevents a lawful
  `MOVE` population owned by the opening catalog from either failing the PGN/analyzer
  zero-`MOVE` invariant or satisfying a different chess source's positive gate. Global
  consensus remains the folded product; admission proves what the named source emitted.

## Remaining product work after integration

### Generic pipeline and performance

- Keep the vendor scheduler architecture gate at zero. #1188 moved Syzygy onto the
  shared bounded fanout; the remaining allowlisted `ChessLabService` concurrency is a
  service workload, not decomposer orchestration. Tracked by #967 and #605.
- Make outer database applies safely concurrent only after claim-before-COPY and
  working-set ownership are race-safe. Until then `apply_dispatch_workers=1` is
  intentional and must remain reported honestly. Tracked by #967.
- Audit source profiles against measured compose fanout and memory, then benchmark
  bounded foundation, Wiktionary, UD, chess, model, media, and OpenSubtitles runs.
- Rerun clean Atomic2020 after #1204 deploys and compare producer, consensus-drain, and mask
  timing against the #1203 418-second and prior 414-second receipts; the older receipt's
  consensus drain occupied about 247 seconds. Clean OMW is now measured at 338 seconds
  versus 716.2 seconds before the generic resource-derived pipeline; its semantic values still
  compose at grapheme/content grain while TSV packaging creates no content physicalities.
- Re-run analyzer v3 after source eviction and confirm the completion envelope reports
  bounded `analyze_ms`, `gin_drain_ms`, and `summary_ms` without an automatic exact-source
  scan. The 1970--1989 payload itself sustained about 179 games/s and 25.6K novel rows/s.
  Verify that its former roughly 2,042 fold observations/game substructure projection is
  absent, then collect relation-level singleton/witness distributions for the remaining
  natural-grain metadata and motif populations.
- Validate end-to-end resume, cancellation, per-file journals, canonical readback,
  and UI progress under actual concurrent multi-file failure/restart scenarios.
- Audit remaining avoidable index probes, index-only eligibility, partition skew,
  COPY/apply cadence, database round trips, allocations, and bytes per input.
  Index availability is now an invariant: improve live-index writes instead of
  dropping the production read surface.
  Tracked by #588, #429, #860, #871, #908, #1008, and #1175.

### Identity and source fidelity

- Complete source-reference admission for any opaque identifiers not covered by
  #1149/#1154/#1157; add explicit governed identities rather than catch-all hashes.
  Tracked by #1041 and #1153.
- Resolve governed vocabulary entities that currently look like content while
  lawfully having no content physicality.
- Enforce one content ID globally across tiers in the physical schema. The current
  `(id, tier)` database key is a SQL/schema workstream and remains unresolved.
  Tracked by #1008 and #1132.
- Apply the remaining source-fidelity findings from #1155: exact scoped FrameNet roles,
  complete VerbNet syntax/restriction/argument admission, source-versioned OMW/WordNet
  inputs, and artifact/revision identity for model sources. PredicateMatrix now keeps one
  selected source row as one generic-pipeline unit; preserves all language/POS populations,
  native predicate/role identity, and the semantic information carried by the 27-column
  PB/FN/VN/MCR/ESO row; suppresses repeated package projections; carries its
  CC-BY-3.0/version/citation receipt; and uses a measured resident profile. SemLink inventory
  now sums JSON records, admitted XML roles, and filtered PredicateMatrix rows at the same grain
  extraction reports. #1193 closes CILI Concept/Instance, source-map, license, and release admission.
- Finish tokenizer/layout admission regression coverage across model families and
  non-text modalities; serialization markers and whitespace must not mint phantom
  semantic content.

### Natural-grain representation

- Audit every remaining relation emission for four smells: derived consequence,
  reader-convenience duplicate, occurrence projected onto type, and set/sequence
  sprayed into independent rows.
- Normalize any remaining media/document/model structures that duplicate ordered
  composition as testimony. In particular, model checkpoint/tensor coordinate
  structure still needs a lawful binary/tensor composition primitive before its
  compatibility edges can be removed.
- Complete chess validation for live-versus-PGN identity, cross-source historical
  playing identity, history-sensitive state, bounded query projections, an explicit
  promotion policy for reusable structural outcome statistics, and deterministic
  transition/evaluation perfcache publication.
- Correct #1180 before benchmarking OpenSubtitles: the current sequence/alignment
  preimages include source schema, language, ordinals, and arbitrary batch
  boundaries. Then benchmark the corrected aligned-trajectory representation
  against the old pairwise relation form and design the explicit
  translation-consensus promotion policy before a full 601-million-pair ingest.
- Audit language, POS, features, definitions, examples, sense membership,
  containment, and correspondence per source at the exact source assertion grain.

### Evidence, materialization, and readers

- Update every reader that still depends on compatibility edges before removing
  those edges. Sense-aware lexical, taxonomy, converse, chess, and containment
  paths must traverse normalized structures directly. Tracked by #1178.
- Define statistical promotion and eviction policies for one-off occurrence data;
  raw evidence remains lossless while hot/repeated aggregates are rebuildable.
  Tracked by #1176.
- Implement testimony virtualization only after proposition grain is stable: one
  fact plus an ordered witness history preserving all exact non-commutative fold
  inputs, provenance, counts, time, scores, and opponent uncertainty.
- Represent source dependency/correlation so copied corpora do not masquerade as
  independent corroboration, and ensure `observation_count` means actual source
  observations rather than repeated code paths or graph degree.

### LapSight and release gates

- Add per-relation unique consensus counts, singleton percentages, witness
  distributions, trajectory vertices, bytes per input, partition pressure/skew,
  and entity/physicality breakdown by identity class. Tracked by #1175.
- Add automatic amplification alarms and bounded-source acceptance thresholds;
  current #1172 reports cell deposits, not novel consensus cells or singleton rate.
- Expose the complete LapSight surface in the UI and CI artifacts rather than only
  logs and the initial admin ingest counters.
- Run the exact regression fixtures: `bank`, `fork`, Wiktionary sense isolation,
  repeated-token UD, UD feature collections, duplicate FrameNet spans, context
  collapse, opaque references, tokenizer parity, entity-tier uniqueness,
  ConceptNet degree amplification, chess trajectory recovery, and bounded
  OpenSubtitles equivalence.
- After all identity-changing work is integrated, deploy/install the extension,
  start from a clean database, run bounded source gates, then perform the full seed.
  Compare information retained, row amplification, database bytes, throughput,
  restart correctness, and reader parity against the recorded pre-drop baseline.

## Completion definition

The campaign is complete only when vendors contain source-specific logic but no
private ingest orchestration; irreducible observations round-trip losslessly;
structural and deterministic facts are not duplicated as testimony; per-source and
per-relation amplification is visible and gated; normalized readers pass; and a
clean full reseed finishes with truthful UI and database accounting.
