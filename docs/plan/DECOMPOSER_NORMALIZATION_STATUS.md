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
| Generic multi-file execution | #1144 schedules files largest-first and coalesces compose fragments by the real apply bounds; #1145 coalesces concurrent tier probes. |
| Admission gates | #1146 splits applies at witness-source boundaries, fixes record/file accounting grain, and distinguishes physical content from lawful governed identities. |
| Fresh seed lifecycle | #1147 makes deferred-index recovery run for every fresh foundation build. |
| Initial LapSight accounting | #1148 persists exact terminal input/file counters and exposes raw per-source row amplification in the admin ingest view. |
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

The related source-fidelity audit (#1155), durable working agreement (#1156), and
canonical lexical-peer batch core (#1164) were integrated in the same batch,
although they are not decomposer implementations. After the batch, the repository
had no open pull requests.

## Remaining product work after integration

### Generic pipeline and performance

- Remove or justify every remaining vendor-owned scheduler. The known next target
  is the Syzygy unpack fanout; non-decomposer service concurrency remains outside
  this invariant. Tracked by #967 and #605.
- Make outer database applies safely concurrent only after claim-before-COPY and
  working-set ownership are race-safe. Until then `apply_dispatch_workers=1` is
  intentional and must remain reported honestly. Tracked by #967.
- Audit source profiles against measured compose fanout and memory, then benchmark
  bounded foundation, Wiktionary, UD, chess, model, media, and OpenSubtitles runs.
- Validate end-to-end resume, cancellation, per-file journals, canonical readback,
  and UI progress under actual concurrent multi-file failure/restart scenarios.
- Audit remaining avoidable index probes, index-only eligibility, partition skew,
  COPY/apply cadence, database round trips, allocations, and bytes per input.
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
- Apply the source-fidelity findings from #1155: exact scoped FrameNet roles,
  correct VerbNet mapping fields, full PredicateMatrix admission, source-versioned
  OMW/WordNet inputs, and artifact/revision identity for model sources.
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
  playing identity, history-sensitive state, bounded query projections, and
  deterministic/perfcache publication.
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
