# Query, trajectory, and chess repair — 2026-09-08

This checklist preserves the complete accepted session scope. A checked diagnostic
does not mean its repair has shipped. Delivery requires main, passing required checks,
installation, and live API/browser/database readback. Existing owners: #588/#429
(native/static execution), #1404 (product queries), #1175/#1080 (readers/navigation).

## Current inventor clarification

Interactive queries and model export consume the same identity-bearing physicality
trajectories and typed evidence. Index filtering and targeted A* frontier expansion
must precede materialization. The "spider web colony" response retains which source,
relation, context, trajectory and standing/uncertainty answered an input; a largest
unrelated edge cannot substitute for that answer. Preserve packed identities, ordinal
structure and mapped native state through operation; realize the requested surface
last. Export is another projection of those same selected structures, not a separate
text-search representation. Measure admitted manifests, expanded nodes, cache misses,
round trips and database pages as well as elapsed time and returned rows.

## Execution checklist

- [x] Reproduce Browse `transformer`, `Captain Ahab`, `Carlsen, Magnus`, Explore
  containers, the home leaders URL, and the actual chess tab.
- [x] Verify query instrumentation: `pg_stat_statements` 1.12 is installed in schema
  `laplace` and preloaded. Preserve accumulated statistics; do not reset them.
- [x] Read existing operator catalog (`ops.api`, formerly `laplace.api`) and trace
  prompt composition/witnessing, physicality manifests, containment, and gap APIs.
- [x] Compare displayed chess ranks with stored OUTCOME cells, game outcomes,
  source Elo, provider profiles, and a native replay of selected outcome evidence.
- [ ] Finish the query-by-query execution audit: installed SQL, caller, actual
  plan, rows, buffers/I/O, database calls, and API/browser timing. Include Browse,
  containers, labels, neighbors, leaders, chess roster/search/initials, career
  record, source ratings, game pages, opponents, and profile/preview reads.
- [ ] Audit and activate perfcache use for every intended consumer in these
  paths. Reconcile the actual blob inventory, writers, loaders, loaded versions,
  and call sites: content floor/composition, reverse identity, relation/highway
  routing, typed number/notation, chess position, trajectory/graph/model caches
  where present. Prove which database work each consumer avoids. Validate
  generation/source compatibility, atomic publication, stale/corrupt rejection,
  and parity of ids, order, scores, unknowns, and source scope. A cache that
  exists or loads but is bypassed is unfinished; perfcache must not become a
  second truth or a way to conceal incomplete query results.
- [ ] Complete the shared native query path (the SQL transformer): search uses
  the same content composition and substrate operators as a prompt, keeping its
  complete query DAG in memory and omitting persistence for now. A SQL-string
  catalog alone does not implement this behavior.
- [ ] Preserve exact ordered physicality paths, repeated ids, run lengths,
  constituent tiers, and intervening content. SPACE is an entity, never an
  assumed universal word boundary. Gap/ordinal relations must preserve their
  actual intervening entities. Test punctuation, Japanese, repetition, reversed
  order, and composed multiword names. Membership, sequence, and attested identity
  links must not be conflated. No character-bag search or display-text identity.
- [ ] Reuse/fix the existing containment and trajectory operations; remove the
  speculative private Browse frontier. Keep binary ids/packed manifests through
  native work. Fix quadratic constituent deduplication and row/array expansion
  where measurements and source inspection establish the defect.
- [ ] Make realization a final, lazy, batched operation. Eliminate duplicate
  container-label work and eager definition/type/source fallback reads. Preserve
  the display contract and exact content rendering independently of matching.
- [ ] Repair the failing home leaders query and its overflow path. Keep page
  sizes bounded, prevent caller-header rotation from bypassing request controls,
  bound concurrent expensive work, and retain navigation over the complete set.
- [ ] Separate source Elo, canonical relation-scoped Laplace Glicko-2 standing,
  and recorded chess outcome counts throughout ingestion, queries, and UI.
  Remove the strongest-edge-to-Elo loading swap and generic maximum-edge score.
- [ ] Repair damaged chess standing from its retained provenance with the
  canonical native fold, audit the evidence/calibration corrections, and verify
  future writes. Preserve source Elo and counts. Do not fabricate missing game
  chronology or replace a ranking with a win-percentage heuristic. Valid signed
  Laplace standing is not constrained to the range of chess Elo.
- [ ] Fix chess list, name lookup, initials, career, source-rating, opponent,
  profile, and game-page SQL. One selected arena/measure and stable page order;
  no per-player rendering or full-roster computation hidden behind a small LIMIT.
- [ ] Finish the native SQL catalog and C# marshalling with typed parameters.
  Add an enforced gate against new/changed inline SQL and duplicate query owners;
  inventory remaining migration debt explicitly. EF/ORM-generated queries must
  be distinguished from hand-written strings rather than silently exempting all
  application queries.
- [ ] Add meaningful regression/parity tests and mutation checks for the above;
  run required source, native, PostgreSQL, .NET, and browser checks with matching
  native binaries/perfcache artifacts. Fix failures rather than relax assertions.
- [ ] Merge, install, and demonstrate the exact formerly failing/incorrect
  operations in the live product. Record before/after evidence and remaining
  constraints without declaring unshipped local edits delivered.

## Verified findings

Measurements are individual observations or accumulated statistics, not a user-
specified numerical latency requirement. Cache state and selected ids matter.

| Operation | Evidence | Established defect |
| --- | --- | --- |
| Home leaders, bands 1/2/4/5, limit 5 | HTTP 500; bigint overflow in `consensus.refuted` | `rating + 2*rd` overflows on damaged cells |
| Browse `transformer` | Original HTTP 503 at 30 s; temporary SQL repair returned 12 hits in ~0.96 s | Bad index plan plus unordered character matching; faster response still returned wrong matches |
| Browse `Captain Ahab` | HTTP 200 with zero hits | Existing `containers_containing_all` finds 115 matching compositions; Browse discards usable DAG structure |
| Browse `Carlsen, Magnus` | Current path returns a player and surface | Existing whole-word containment finds 3 compositions; character-bag success does not prove correct identity/path matching |
| Transformer containers, 3 hops, 50 rows | HTTP 503 at ~31 s; raw ids ~49 ms | Caller renders per row and repeats display batch; expensive fallback work dominates |
| Display labels | 41 recorded calls: mean 7227 ms, max 28689 ms | Eager fallbacks; reverse-definition plan applies type restriction above a broad partition scan |
| Chess list | Browser and API show DanielNaroditsky first, rating 23440705.592, conservative 23440005.592 | Damaged stored OUTCOME standing is exposed as a player rating |
| Chess ranked plan, 50-row page | 77,611 player rows and 86,148 result rows; 650 ms observed, then 294 ms warm | Full materialized roster before page; 153–197 ms in final name batch; parameterized calculated order prevents direct ranked-index paging |
| Magnus profile preview | PLAYED_BY eff_mu 61549171.007 | Generic card displays this first, then replaces it with peak source Elo 3410 |
| Daniel recorded outcomes | 589 games: 176 wins, 98 draws, 315 losses; 225 score points | Counts reconcile independently of damaged standing; source peak Elo 3459 |
| Magnus recorded outcomes | 9690 games: 6800 wins, 1204 draws, 1686 losses; 7402 score points | Counts reconcile; provider/mode Elo values are separate assertions |
| Native replay, retained observed order | Daniel 3170.538 with stored opponent Elo; 1382.440 with neutral comparison | Neither reproduces the stored 23.4 million. Replay assumptions and rating-period history must remain explicit |
| Constituent array helper | Nested linear search for every decoded vertex, individual bytea allocation | Quadratic dedup for distinct ids; deduped projection is not an ordered trajectory |
| `generation.word_adjacency` | Positional indexing into that deduped array, SQL generate_series/LATERAL | Native canonical ordinal visitor now preserves repetition/RLE. Selected Captain/Ahab probe: old 136.449 ms, native 15.782 ms, both 105 rows; single observations with different cache state, not a cold/warm parity benchmark |
| Floor existence/rendering | Tier reader sent mapped IDs to PostgreSQL; render requested their closure; extension duplicated core reverse index | Local native batch answers floor presence before application transport; render/closure skip floor leaves; one shared reverse index |
| `generation.relation_plane('traj','next')` | Production regression reached a 45 s statement timeout inside whole-corpus LATERAL expansion, row_number, and self-join | Native ordinal repair remains required for this existing caller; isolated corpus regression passes without the production corpus scan |

## Local state at audit

Local commits implement native display/browse/container reads,
overflow arithmetic, chess calibration/repair, public-query controls, a SQL
catalog/C# bridge, and UI labeling. These are not delivered. Browse currently
delegates whole-composition membership to the existing containment API; complete
in-memory query DAG and ordered/gap execution remain open. The earlier broad
private DAG expansion was removed after a failed diagnostic, not deployed.

Native Glicko and selected endpoint/chess tests passed earlier. With matching
native artifacts, both display gates and both new perfcache reader tests pass
(4/4). The reader regression uses a disposed data source and proves all-floor
entity existence, tier existence, and content descent avoid opening a connection.
Core codepoint/whitespace checks pass (17/17), including positional batch bits,
duplicates, unknowns, output bounds, and stale-Unicode rejection. SQL catalog and
ISA gates pass; the merged catalog owns 42 statements with 471 legacy runtime literals
still inventoried. The complete generation corpus fixture passes in the isolated
`laplace_query_repair_verify` database with the new adjacency binding, including
repeated separators/words, ordinal gaps and RLE. Bindings were rolled back.
Historical repair committed for all 77,611 typed chess players: 1,463,956 retained
evidence rows and 604,499 consensus cells, with zero remaining source-Elo
calibrations in OUTCOME/PLAYED_BY. The first attempt rolled back in full; the second
committed after restoring the original installed procedure. Private binary backups,
checksums, replay recipe, transaction logs and API/browser readbacks are retained
under `/home/ahart/Backups/Laplace/query-repair-20260908/`. Daniel now reads
1382.439897964 with RD 63.883247128 over 589 games; Magnus reads 1731.908270350
with RD 64.660732781 over 9690 games. These are retained-evidence Laplace folds,
not source Elo. Future writer correction is still unshipped.

Installed display SQL fails the actual duplicated-input batch regression (five
rows for three inputs); native replacement passes its isolated SQL fixture. Full
workspace build encountered runner-owned generated artifacts; an isolated checkout
provides independent build output. Reseeding is authorized if needed after clean
SQL and deployment are verified. Fresh installation must also be tested without
old database contents masking missing extension dependencies.


## Home readback and delivery checkpoint

The installed home request returned HTTP 200 in 3.635631 s after native retained-
evidence repair of Chess_Substructure OUTCOME cells: 923 selected typed subjects,
772 evidence rows/cells, 752,419,529 observations preserved, 26 saturated cells
reconstructed. All evidence bytes and calibration remained identical; repeated
reconstruction was byte-identical. Backup/recipe/readback live in the private
`query-repair-20260908/substructure` directory. Home subjects in bands 4 and 5
still have blank labels in the installed display path. This is failed acceptance,
not completion of the home leaderboard work.

Local commits 207fefe6 / 41db49af / 8eb4c550 include the original repairs, bounded
native chess roster selection with exact int64 ordering, the fixed home preview
route, and explicit provenance-preserving general cell replay. Full proof exposed
and corrected a missing ASP.NET rate-limiter import, existing managed witness
admission outside native C, and stale gate entries after moving SQL ownership.
Full proof and coordinated main deployment remain implementation obligations.
The inventor authorized integration onto main and a fresh database/reseed. PR
#1514 is now integrated locally in c34e9838, including conversation SQL migrated
into the shared native catalog without increasing the inline-SQL exceptions. No
additional feature branches or worktrees are to be created until this work is clean.

## Current-data execution receipt

Direct psql execution against the installed database is preserved in the private
`query-repair-20260908/forward-demo/current-data.sql` and `.log`, with selected
evidence and A* in `elected-evidence.sql` and `elected-evidence-corrected.log`.
These are read-only queries; no model ingestion or prompt recording was required.

- `dog` resolves to `01cdcce152940fce07b431bc4f3bc2d5` in 0.548 ms.
- Its physicality closure retains three ordered constituents (58.087 ms).
- A one-hop route returns eight rated edges (71.724 ms).
- Trajectory successors return twelve candidates with separator IDs (25.419 ms).
- Three native forward steps select `loyal friend`, `pet`, `animal` (297.930 ms).
  The first two have retained IS_A evidence with 12 and 10 observations and their
  original source/context/calibration. This demonstrates a bounded walk, not a
  completed conversational response.
- Typed A* finds dog → pet in 10.662 ms.
- Final display of those three selected identities costs 544.823 ms: display is
  still a material installed-path offender. Punctuation display also duplicates
  rows for one input; the pending native replacement addresses that defect.
- The earlier installed `converse.chat('dog')` call reached its 15-second timeout
  in `relation_mask_types` under `forward_text`. End-to-end chat remains failed
  acceptance until the combined deployment passes the same operator request.
- The attempted DENOTES distribution was empty: this database has no DENOTES
  family. The actual sense edge is IS_SYNONYM_OF; the demonstrated IS_A
  distribution is over retained evidence, not an invented relation.

Maintenance scripts used Python to orchestrate historical repairs and capture
diagnostics. They are not a Laplace inference dependency. The executed substructure
script is preserved as `substructure/repair-executed.py`; its native replay and
commit are recorded in `substructure/manifest.json`. The chess transaction is
preserved as `attempt2/apply.sql`. Subsequent database demonstrations use explicit
SQL through psql. Existing repository build/CI Python checks remain identified
as such; their use must not conceal database mutation.

## Main deployment readback

PR #1514 merged and main reached `16a6fa65`. Exact-revision local proof passed
2,950 executed checks (five skips); fresh PostgreSQL regression passed all 34
substrate and three geometry cases. Main CI also passed build, DEV/BAT, installation,
database lifecycle and database QA. The first application publish exhausted the
16 GiB `/opt/laplace` volume. PostgreSQL package inputs were checksum-verified and
relocated to `/build/laplace/work/package-inputs-20260908/postgresql`, preserving
the original path through a symlink and freeing 1.6 GiB. The application retry
published successfully; no live database recreation has occurred.

Live product acceptance then exposed `08P01`: native catalog SQL uses positional
parameters, while migrated callers retained named Npgsql bindings. Browse, leaders,
labels/containers and conversation calls require the corrected positional adapter.
The database regression now exercises named caller bindings, repeated result
positions, direct forward-turn transport and atomic conversation append. The
original batch test used positional bindings and therefore missed the defect.
This runtime failure keeps deployment acceptance open despite successful build/QA.

Preserved all 1,116 accumulated live `pg_stat_statements` entries before recreation
in the private repair evidence directory. `pg_stat_statements` 1.12 is installed
and preloaded; `track_io_timing` was off, so historical block timing cannot be
claimed as measured I/O latency. Block counts and execution time remain available.

## Deployed query costs and observation scope

Main `bca541eb` deployed the positional binding correction. Browser readback
returned 200 for homepage leaders and the 50-row chess list, with the explicit
Laplace Glicko-2 label. Browse returned 200 for transformer (772 ms), Captain Ahab
(342 ms), and Carlsen (78 ms). Captain Ahab still admits reversed word order;
ordered composition matching remains an implementation obligation. Live chat
acceptance failed on the first cold request; the diagnostic retry completed in
29 seconds with an incoherent concatenation of 128 substrate selections. This
does not meet conversational acceptance. Database recreation has not run.

Direct psql measurements, including their executable SQL, are preserved in the
private repair evidence directory:

- `observation-query-audit.sql/.log`: pg_stat_statements 1.12, tracking `all`,
  reset timestamp 2026-08-18; 5,755 entries deallocated. These retained cumulative
  aggregates span revisions and include nested statements: do not add parent and
  child totals or treat their means as current endpoint timings. The completed
  128-step forward turn accounts for 28.82 seconds and 28,013,980 buffer hits.
- `leaders-deployed-plan.sql/.log`: nested plans show a band query discarding
  901,396 entries from score-ordered indexes before returning five results.
  Final display of 40 IDs accounts for another 995.6 ms. Both selected-family
  access and final label resolution remain measured performance obligations.
- `observation-scope-plan.sql/.log`: a direct indexed attestation lookup for the
  dog ID grouped by source/context/type executes in 28.854 ms, with 19.043 ms
  planning and 523 hits/1,318 reads. There are 250 source/context groups. Retained
  ConceptNet English evidence contains 1,081 rows and 1,528 observations. This
  proves evidence availability, not that frequency alone determines scope.

The inventor's observation-based filtering requirement belongs before expansion
and steering. Preserve every ordered prompt constituent and intervening entity;
resolve witnessed occurrences, then constrain source/context/relation operands
using those observations and session context. Carry that same scope through
admission, scoring and receipts. `ops.attestation_response` currently gates on
scoped attestation existence but returns global consensus scores and witness
counts; this is not a scoped fold. `consensus.scoped_consensus` replays selected
sources but uses a whole-source SQL aggregate and has no context operand. Neither
is the completed native, batched, observation-scoped forward operation.

The native consensus reader also previously bound only the first index column,
fetching heap rows before rejecting other endpoint/family constraints. The repair
binds the complete available constrained prefix and adds a subject/object/type
index for S7's endpoint intersection. On an isolated copy of 3,100 retained live
dog/pet/animal cells, old and new readers produce exactly equal scores, edge
counts and coverage. Repeated warm measurements fall from 1,440 to 87 buffer
hits and 0.861–0.879 ms to 0.326–0.367 ms (`steering-endpoint-compare.sql/.log`).
This is an isolated steering measurement, not an end-to-end chat speedup. The
additional index must still be deployed and its live storage/ingestion cost
measured. Fresh-install regression covers duplicate inputs, family filtering,
endpoint intersections and unrelated-cell exclusion.

## Question acceptance and repeated corpus work

The inventor's question `How does lightning work?` fails the deployed forward
pass at a 45-second statement timeout (`lightning-forward-acceptance.sql/.log`).
This is the acceptance case; a successful `dog` HTTP envelope is only a transport
smoke check. No coherent explanatory answer has been demonstrated.

Nested execution plans identify an unscoped suffix fallback in
`trajectory_continuations.c`: after a full-context miss it queries trajectories
containing the final identity alone. When that identity is SPACE
(`00263ca9f57f7177f495e3711f8cdd59`), individual partitions return about 145,000
trajectories and one query takes 6.36 seconds. The walk repeats this corpus-wide
read. Preserve SPACE, multiplicity and ordering; repair evidence scope and
ordered trajectory access instead of removing separators or accepting an
arbitrary truncated result. The current forward route also passes NULL relation
constraints and has no source/context operand. This is a semantic execution
obligation as well as a performance obligation.

Two further native scan/reuse repairs are independently measured:

- Neighborhood selection can stop a descending endpoint/effective-mu index
  range only once its score is strictly below the full selected heap's bound.
  Equal-score entries remain eligible for exact identity/type/direction election;
  each frontier identity has its own range. An exhaustive regression checks the
  boundary among hundreds of tied neighbors. Without a verified canonical index
  expression/operator family the reader retains the complete scan.
- Candidate content/physicality admission is cached, including absent IDs, for
  one forward-call snapshot. Only previously unexamined IDs enter the existing
  typed batch query. The question trace previously repeated roughly 175 ms
  content-presence queries at each step.

The real question's four word neighborhoods retain exactly the same 32 edges
while buffer accesses fall from 57,169 hits to 6,313 hits + 1,094 reads
(`lightning-neighbor-compare.sql/.log`). Three forward selections remain exactly
equal with content reuse, taking 3.778 seconds versus 4.291 seconds before
(`lightning-three-step-compare.sql/.log`). They are `Intentionally_act`,
`Being_employed`, `Cause_to_end`: still wrong for this explanatory question.
The complete question still times out with the ordered neighborhood repair
alone (`lightning-ranked-forward.sql/.log`). Do not describe these access-path
repairs as delivered conversation or substitute three-step timing for answer
acceptance. All temporary function bindings used for comparisons were rolled
back. Main d1f2f16d installed/deployed successfully but seeded live product CI
still fails; database recreation and reseed remain outstanding.

The suffix reader now shortens failed indexed operands geometrically, retaining
the complete native matcher and accepting only matches at least as long as the
current probe. Any successful length-k probe contains all possible matches of
length >= k, so this preserves the greatest exact stride and every occurrence
count. It no longer jumps directly from a failed long context to its last ID.
Regressions exercise a five-ID input whose best suffix has length four, then
length two, then a lone separator; repeated words and separators remain intact.
This is a native access-path correction, not inferred source/context scope.
The full question still hits 45 seconds with this correction: longer suffixes
reduce the observed postings, but globally common word/separator combinations
still read large corpus sets (`lightning-suffix-forward.sql/.log`). Question
acceptance therefore remains failed.

## Inventor correction: generic DAG operands, not question scenarios

The next execution change must follow the inventor's correction: the operand is
a Merkle DAG root/subtree, including a prompt already represented in the DAG.
Scenario-specific question recognition is not a prerequisite and extending the
four-row WHAT_IS frame-route table is not this work. Text is an ingress/realization
surface. The same operation must accept other compositions and modalities.

The generic data flow to implement and verify is:

1. Resolve the root and its ordered constituent occurrences. Keep identity,
   ordinal, multiplicity, separators, parentage and original root together.
   Deduplicate storage probes only; remap their results to all input occurrences.
2. Read observations addressed by those identities, then their attestation
   bindings. Keep subject/relation/object, source, context and occurrence/root
   identity. Context classes remain typed: common language is not proof of
   common sentence occurrence. Case/sense/frame alternatives are witnessed
   candidate relations, not replacement prompt identities.
3. Compare the resulting evidence structures jointly. Input occurrences address
   query-side bindings; candidate occurrences address key-side bindings. Native
   comparisons operate on declared compatible relation/role/source/context
   dimensions and preserve disagreement and missing evidence. They must not rank
   one word's global degree as interpretation of the complete operand.
4. Retrieve the target bindings and ordered physicality continuations belonging
   to those admitted comparisons. Keep evidence/root scope on this value-side
   read. A common final constituent does not independently authorize expansion
   over all corpus trajectories.
5. Apply the declared native weighting/reduction/composition operator to these
   operands. Preserve score domains and source receipts; the output projection
   produces a typed state/composition, not a concatenation of independently
   rendered high-ranking labels. A conditional comparison can use a declared
   pooled standing as a prior. An explicitly source-only standing cannot silently
   be replaced by the pooled score. Do not add per-word re-ingestion/refolding as
   an inferred prerequisite for ordinary pooled inference.
6. Update the active state and ordered trajectory with the selected constituent
   or composition. Reuse unchanged indexed/mapped operands; resolve only newly
   required observations. Realize the selected structure at the output boundary
   and witness actual outcomes through the existing writer.

These are target-neutral operations; Q/K/V/O materialization is a consumer of
their typed comparisons and transformations. Full numerical equivalence to any
particular dense checkpoint has not been demonstrated by the graph traces.

Direct current-data receipts are `lightning-observation-logic.sql/.log` and
`lightning-role-evidence.sql/.log`. The root resolves to 58 tree nodes and eight
lexical-boundary occurrences, preserving all three SPACE occurrences and `?`.
Fourteen witnessed case variants address 4,788 distinct attestation rows in a
514 ms batch. Lightning has 353 rows across five sources, including electrical
discharge, clouds and thunder. Witnessed frame candidates include How → Means
and work → Being_operational / Being_employed, among others. This establishes
available operands; it does not establish that the current program performs
their joint comparison or produces a supported answer. No data reset is needed
to expose that missing operation. A clean reseed remains authorized and pending.

### Observation-bound continuation execution

The installed joint sense read was measured directly before reusing it:
`converse.prompt_coherence('How does lightning work?')` took 5.784 seconds,
299,855 shared hits and 99,866 reads. It returns four senses after accumulating
all incident mass; it is not yet a suitable default ORIENT implementation.
Receipt: `lightning-joint-comparison.sql/.log` in the session backup directory.

The direct observation-root projection for the same prompt found 4,108
attestations, 811 contexts, 2,901 candidate root IDs and 2,142 physicalities.
Fetching those physicalities through the entity indexes took 1.462 seconds cold.
Receipt: `lightning-trajectory-scope.sql/.log`. These counts describe a candidate
support superset, not joint semantic agreement or sentence co-occurrence.

The product forward path now binds its resolved prompt/session IDs to the native
continuation operation. Native code batches witnessed object/context roots,
deduplicates storage reads while retaining physical occurrences, and retains
packed trajectories within the request snapshot. Each selected identity extends
the next step's observed support. A suffix miss stays within that support;
it cannot fall back to the global SPACE posting. All suffix lengths use the
existing native ordered matcher. Explicit standalone corpus queries retain their
unscoped overload; exact observation-scoped queries use the same implementation.
The two new typed reads live in the native SQL catalog.

Validation: 34 substrate and 3 geometry regressions pass, including empty scope,
SPACE, duplicate witness paths, and scope extension after selection. SQL catalog
and install/upgrade dependency gates pass (44 catalog entries). This is a request
snapshot cache, not completion of the persistent mmap perfcache work. Joint
attestation comparison, source/context-conditioned scoring and coherent answer
composition remain implementation obligations; this change does not establish
that chat works. Live timing and output must be measured after deployment.

### Native physicality routing and current acceptance

The observation-bound deployment (89976800, extension e956e6ca9e7cec30) still
failed the 128-step lightning read at 45 seconds. Its first three selections were
Being_employed, Intentionally_act and Locale_by_use. Candidate support alone does
not implement joint interpretation. The first three steps used 2,261,972 shared
hits and 4.250 seconds. Receipt: `lightning-observed-forward*`.

A second measured access defect was the unpartitioned operand: a canonical-ID
SQL array probe sent all 2,901 IDs to every physicality leaf, performing roughly
2,000 index searches per leaf. All 2,142 matching current-data physicalities had
the canonical BLAKE3(entity ID || little-endian Content type) identity.
`content_trajectory_read.c` now uses the existing native physicality-ID function,
PostgreSQL's actual partition hash/support metadata, and one PK array probe of
only the IDs belonging to each leaf. It preserves MVCC/SELECT permissions and
rejects an incompatible storage layout. The scoped reader consumes canonical
Content physicalities; arbitrary legacy geometry-ID rows are not additional
canonical Content placements. Corpus-scoped compatibility reads are unchanged.

Transactional live replacement/readback (rolled back) retained the same three
selected identities, reducing shared hits to 1,724,964 plus 356 reads and elapsed
time to 3.750 seconds. All 128 steps completed in 35.747 seconds with 22,497,764
hits and 51,728 reads. This remains failed chat/performance acceptance. Receipt:
`lightning-native-partition-routing.sql/.log`. The old array-based physicality
catalog query is removed; 43 fixed catalog entries remain.

The live provenance gate now exercises ApplyConversationTurnAsync and verifies
ordered occurrences, distinct tenant evidence/dependency cells, common content
IDs, source-isolated membership, and exact roles/surfaces in a read-only
transaction. Its obsolete PRECEDES/content-root assumption was incompatible with
spec 34. The corrected live test passes (2 seconds). An initial local run loaded
stale app-local September 4 native libraries; explicit LAPLACE_ENGINE_BUILD
pointed the repeat at this tree's build and resolved that artifact mismatch.

The official FIDE XML repeats ID 5019168; its two records differ in foa_title,
which is outside the existing player display projection. Equal projected records
now share one identity in every snapshot index. Conflicting projections still
fail explicitly rather than choosing a rating. All 13 targeted FIDE tests pass,
including the current official profile/search/roster read. Source ZIP and exact
record excerpts are preserved as `fide-current.zip` and
`fide-duplicate-5019168.txt` in the backup directory. Full source-field fidelity
remains distinct from this display-index repair.

### Clean database and foundation readback

Main d5e5c9ab was installed before the authorized database recreation. Workflow
34214197912 completed recreation; 34214512043 completed all ten foundation
sources in 18m14.448s, recording 9,235,610 attestations. Knowledge, documents and
chess restoration remain outstanding. The workflow passed, but five source
throughput comparisons remain slow (Unicode, ISO639, CILI, WordNet, PropBank);
a successful ingest is not a throughput acceptance claim. Original source/file
inventories, final run journals and statement statistics remain in the private
session backup. The main product proof ran against the empty database during
this transition and must be repeated against seeded data.

Fresh foundation readback still fails conversational acceptance. Twelve `dog`
steps take 825.509 ms and emit frame names; three lightning steps take 691.862 ms
and select Being_employed, Intentionally_act and Locale_by_use. Receipts:
`fresh-foundation-forward.sql/.log` and `fresh-dog-route.sql/.log`.

The route trace identifies an independent scope defect: the initial forward
frontier uses Browse's bidirectional crawl, reaching unrelated words backward
through HAS_LANGUAGE and HAS_POS. The crawl now exposes an explicit direction
operand using the existing native neighbor implementation. Forward routing sets
it true; canonical symmetric reverse evidence remains eligible. Filtering occurs
before pair election and fanout, so invalid reverse edges consume no slots.
Browse retains its bidirectional behavior. A regression verifies both behaviors
and a lower-ranked symmetric edge surviving a stronger invalid reverse edge.

Transactional current-data readback of this change (rolled back after measuring)
returns 45 dog route edges and 12 forward steps in 329.974 ms (368.506 ms under
EXPLAIN, 255,694 shared hits). It reaches the witnessed WordNet definition but
still mixes senses and frame labels. Lightning remains incorrect. These are
routing/access gains, not joint interpretation or answer composition acceptance.
Receipt: `fresh-directed-route.sql/.log`.

### Exact scoped source counts

`ops.attestation_response` and its unary variant returned consensus witness_count
as an int32 source count. That conflated repeated observations with independent
sources and overflowed for large witness totals. One native batch now reads exact
subject/relation evidence from the SQL catalog, applies source/context scope,
deduplicates (subject, object, source), and joins witnessed cells to native pooled
consensus standing. Unary and binary evidence remain separate. Standing scope is
explicitly `pooled`: source eligibility must not pretend to be a source-only
Glicko refold. Scalar SQL wrappers delegate to that batch; C# uses catalog entries.

Regression fixtures exercise five billion repeated witnesses, multiple sources
and contexts, unsupported consensus cells, unary/binary separation, per-subject
limits, duplicate operands, empty source scope and scalar/batch parity. Native
and managed builds, catalog gate (46 queries), and install/upgrade dependency
and drop-order gates pass. Deployment/readback remains required for this change.

### Seeded readback and document observations

Main 37d7563f installed the direction/source-count repairs (extension
b5f7b12d0bab2186). Workflow 34217550742 passed build, native/managed tests,
installation, application delivery and database QA. Its product proof failed:
`DefaultForwardPass_ReachesWitnessedAnswerThroughDirectAndConversationPaths`
does not obtain `cold` for `The opposite of hot is`. Preserve that acceptance.
Independent distinct-source SQL agrees with the deployed native source counts;
receipts are `deployed-evidence-route.sql/.log` in the session backup.

Atomic ingest 34218183196 recorded 1,246,582 attestations across 3/3 files in
4m33.788s. The inventor's document ingest 34218945047 recorded 4,656,933 entities
and 760 attestations across 195/195 files in 8m50.393s. Both journals finished
`ok`; both workflows failed the subsequent throughput gate because no accepted
baseline exists for those sources. The pre-recreation backup has only the ten
foundation baselines: recreation did not remove Atomic/Documents baselines.
Neither current result has been accepted merely to turn its gate green.

The native trajectory scope now admits document observations through the existing
`browse.containing` catalog operation, with the complete supplied AND-member set.
One indexed set read discovers roots; the canonical partition reader retains each
packed trajectory once. Order, repeated IDs and SPACE remain native ordinal facts.
No per-word semantic attestation is required. Scoped ordinal support survives
missing direct graph testimony; explicit negative steering remains distinct.

### Inventor correction: establish output purpose before selection

`trajectory_generate.c` previously promoted every routed node with renderable
content into the output pool. Frame category names share canonical content IDs,
so testing Word/tier/content presence could not establish answer eligibility.
It also discarded legitimate opaque typed results that lacked text physicalities.

The full native operation now accepts `p_output_relation_types` independently of
its routing/steering operand. Only supported endpoints of that declared projection
enter as graph outputs. NULL/empty projection leaves graph state internal; exact
physical continuations remain independently admissible. The compatibility
continuation operation binds the manifest's `CONTINUATION_OUTPUT` set, preserving
completion/continuation testimony. Other operations still require election from
the joint input evidence; the default text adapter has not implemented that step.

Direction and positive output support are applied before the fanout bound. A
refuted requested claim cannot be resurrected by a different positive relation.
Output support does not require a duplicate steering read. The native selection
returns exact typed IDs without content/type/render probes, removing the obsolete
`generation.semantic_presence` SQL catalog query. The catalog now has 45 entries;
469 pre-existing inline runtime literals remain migration debt. Rendering remains
at the final boundary. SQL chat returns the canonical pass's selected surface,
including NULL for no selected output; it cannot manufacture a scaffold response.

Regressions cover unspecified/empty/wrong output purpose, explicit frame output,
opaque non-content targets, asymmetric and symmetric direction, refutation before
a one-slot bound, no duplicate steering requirement, ordinal observation scope,
and physical membership versus ordered proof. No frame-label blacklist or
question-specific string routing was added. Current native build, catalog,
manifest generation, ISA and install/upgrade dependency gates pass.

CI caught the SQL chat adapter's attempted reuse of its legacy `no_forward`
scaffold. The forward source contract prohibits that substitution. The fallback
is removed; the small graph-only fixture now asserts exact canonical-forward
parity and no fabricated output when it has neither ordinal nor completion
testimony. The seeded natural-language hot/cold acceptance remains unchanged.

Transactional current-data proof is retained in
`output-projection-readback.sql` and `output-projection-readback-final.log`.
Unrequested graph-only output is empty; explicitly requesting EVOKES_FRAME still
selects the `Animals` frame. Explicit IS_ANTONYM_OF selects a `cold` identity and
only then resolves its display label. This demonstrates the typed operation,
not inference of that operation from a natural-language prompt.

Conversational acceptance remains failed: default `dog` follows an observed
Atomic sentence, the hot/opposite prompt follows irrelevant prose, and the
lightning question produces no continuation. Complete ORIENT/ROUTE by comparing
the whole input's witnessed structures and retaining their bindings through
selection. Independent words' accumulated neighborhoods and a common suffix
must not substitute for that comparison. The full session checklist above
remains active, including performance, perfcache, readers and chess restoration.

The subsequent inventor-triggered OMW run 34222144989 finished ingestion in
825 seconds (`rc=0`) and likewise failed its absent-baseline gate. During this
run, the stock PostgreSQL GIN statistics reader measured 5,915 pending pages
(46 MB) across the 64 content indexes. The normal post-ingest drain completed
in 824 ms; a later statistics read found zero pending pages. The four-root
hot/opposite containment query earlier used 25,403 shared buffers and 176.867 ms
execution; after cleanup it used 455 shared buffers and 12.85 ms execution,
plus 34.296 ms planning. These are concurrent-state observations, not a
controlled throughput benchmark. They establish that the existing end-of-ingest
drain does not eliminate the pending-list cost for concurrent readers. Receipts:
`containment-pending-pages.sql/.log`, `containment-pending-pages-after-omw.log`,
`joint-observation-membership.sql/.log`, `containment-during-omw-plan.json`.

### Native membership, display and preserved-context corrections

The common Content membership reader now uses PostgreSQL's GIN bitmap/table
access methods directly under the active snapshot. SQL containing-all, Browse,
Explore parent expansion and unscoped/scoped trajectory containment delegate to
that implementation. No SPI cursor or nested SQL planning remains in those
membership reads. The reader validates expression/predicate/index coverage,
checks SELECT/RLS, rechecks lossy bitmap rows, and preserves complete distinct
entity sets separately from per-physicality ordinal observations. Two unused
catalog statements and the retired trajectory inline SQL exemptions are removed.
The catalog contains 43 statements and 468 legacy runtime literals remain.

Native regressions pass (34 substrate, 3 geometry), including forced lossy bitmap
AND/OR parity, NULL/empty/duplicate operands, duplicate physicalities, scalar SRF,
write visibility, SELECT permissions and RLS rejection. All 26 policy checks pass.
Live transactional comparison of native membership against the SQL predicate
returned zero differences over 4,852 dog-containing entities. Initial and later
measurements span another concurrent ingest; do not present those as a controlled
throughput gain. Receipts: native-membership-readback.sql/.log and JSON plans.

Entity 1578bd5f78a60229e09b58f86f0002c7 is a sentence inside document
fbaf6a1386f2712429cd598ead2cdb20 and file 3be71f60f90e0f4d7be45a08b85c1ac4.
The two-container API response was 440,651 bytes: the document's label alone was
421,807 characters. Its first_observed_by is itself; the metadata label fallback
reconstructed the complete book. The display reader now applies its existing
first-unit preview to unnamed compositions and high-tier provenance operands.
Live transactional readback gives a 61-character book title for document/file,
while the sentence retains its 69-character content. This does not yet implement
source-file metadata navigation or retaining every occurrence/path to a selected
passage. Those remain required generic container behavior, not book exceptions.

The volt/joule matchup exposed premature context election: volt's word identity
870de6c70babd677ccd97163710956f5 is shared across languages. Automatic language
selection chose Catalan (20cc979df43b76b61335520cfb99733a); lexical.senses then
returned shared WordNet synsets for stroll/environs/turn. The English electrical
sense aa450bdb44f3b5c5a59d571cee27b789, linked to bbd6910ea13edacec9cb0b12ef693e71,
is present but filtered out. The displayed English glosses are stored definition
entities reached through those shared synsets: this proves cross-language meaning
retrieval, not generation of translated prose. No ILI/frame hop was established
in this particular readback. Preserve source-qualified referential bindings and
input/output language separately; compare both inputs before electing context.

The inventor's later Carlsen request returned zero rows despite 10,056 committed
Chess_Player entities. Tested current canonical Carlsen identities were absent;
FIDE completion/name evidence still requires reconciliation. Current search also
runs its candidate query twice on an exact miss, caps candidates before requested
sorting, discards ordering in its character-membership SQL, and reimplements
selection in C#. Repair through the shared substrate operations, preserving the
whole forward-pass obligation rather than substituting a private name search.

The active ChessPgn writer demonstrated a separate write bottleneck: backend
532266 waited on the advisory lock held by 532006 during highway_mask_deposit in
a transaction already six minutes old. pg_stat_statements.track=all identifies
mask UPDATE ... FROM unnest as the temporary-file writer (2,117,586 temp blocks
in one accumulated entry). Completed ChessSyzygy reported 98,517 ms in four mask
calls over 3,887,804 pairs. The current atomic writer includes evidence, folds and
mask chunks (configured 1,098,326 cells) in one transaction. Do not split away
atomic evidence/consensus correctness or merely delete the mutex. Replace the
expensive shared write operation and measure complete apply/commit latency.

### Chess roster/profile consistency

The profile adapter compared the rendered type label `chess player` with the
literal `Chess_Player`, rejecting valid roster identities including Alekhine
96c1a24736d8d6e47afab1387c6b3826. It now retains the raw type ID through the
facet read and checks the canonical Chess_Player ID before career reads. The
real SubstrateClient roster-to-profile live test passes against committed game
evidence, alongside 28 chess contract tests. Website delivery still requires
the queued application deployment; this is not a live-site completion claim.

The unrated roster branch admitted typed placeholders with no name or game
evidence, including 000b4e7e9af60880fe78ea142f464897. It now requires a witnessed
name relation for profile-only rows. The native regression preserves valid
zero-game profiles and excludes a type-only placeholder; all database
regressions and all 26 policy checks pass. Align exact/profile admission with
this evidence requirement as the shared chess reader is completed.

FIDE's 100 profiles have since committed. Carlsen, Magnus is present as
1d4e192691072dfcc07f259ba97b18b1, but the search still returns zero. His name's
tier-3 Content trajectory contains word_id('Carlsen'), not the seven character
IDs used by the current query. Repair the native tier traversal and remove the
duplicate candidate call; absence of this player is no longer the explanation.

### Shared mask persistence and the completed PGN run

ChessPgn run a3f094fe-8d5f-420a-ac0b-7cc8a6dbeb4a completed the writer in
1,467 seconds and the CLI in 1,511 seconds. Its journal records 248,935 ms in
eight mask calls over 6,875,705 pairs and 323,303 ms in consensus calls.
Post-writer ANALYZE took 28,923 ms and GIN maintenance 12,917 ms. Workflow
34226407498 then failed its missing-baseline gate; subsequent correctness jobs
were skipped. The completed run's 13,749.9 novel rows/s is now explicitly
accepted as the pre-change comparison reference, not as a performance target
or proof that the next run improved.

The mask writer now obtains identity, tier, mask, tableoid and ctid in one
fixed set read. C removes no-ops, sorts identity/tier lock order, locks the
actual tuple locations, rechecks the latest mask, and invokes PostgreSQL's
table/executor update with constraints and index maintenance. This removes the
second indexed lock query and UPDATE FROM unnest's third identity lookup and
temporary materialization. Read-only transactions, privileges and row security
are checked; unsupported triggers/rules/stored generated columns fail explicitly
instead of being bypassed. Existing transaction and advisory-lock ownership are
unchanged. The SQL catalog owns both remaining fixed statements.

The comparison uses 100,000 actual tier-2 IDs copied into a private verification
database, the previous committed C implementation compiled as a benchmark-only
module, and old/new/new/old order with rollback and vacuum between runs.
Old: 2,824.052 / 2,227.276 ms; new: 1,815.687 / 1,693.608 ms. Both implementations
produce every requested bit. Each old run spilled 855 temporary blocks; neither
new run spilled. Warm comparison: 1,746,099 versus 1,457,490 shared buffer hits,
with identical 61,003,509 WAL bytes. This is a component measurement, not a
claimed full-ingest speedup. Receipts are mask-writer-comparison.sql/.log and
mask-entities-100k.copy under the session's private receipt directory.

Four overlapping workers completed 20 mask calls with 1,993 actual row updates,
zero failures/deadlocks/serialization failures, and zero mask drift after
restoration. Database regressions cover accumulated bits, replay, multiple tiers,
constraint failure/rollback, UPDATE privilege and RLS. Required remaining proof:
install the writer and compare the next complete physical-artifact ingest,
including durable counts, mask/consensus time and post-ingest maintenance.

Profile deployment readback now passes: Alekhine's real API profile returns
HTTP 200 in 409.731 ms. This closes the rendered-type rejection observed on
that page; Carlsen's tier search and complete chess eligibility remain open.

## Physical source failures and bot decision acceptance (2026-09-08)

CodeDecomposer run `f52a2bd6-503b-40cd-96f6-667a1b51bf12` completed 2,060 of
2,474 selected files and failed 414. The managed error incorrectly named
`laplace_grammar_compose_probe`; this lane calls `laplace_grammar_source_compose`.
The native source composer rejected every zero-width AST span, including ordinary
Markdown continuation nodes, parser recovery tokens, and an empty SQL syntax root
for a newline-only physical file. Keep those nodes in the syntax AST, omit their
nonexistent bytes from physical composition, and compose the actual source-edge
bytes even when the entire parser root is empty. Nonempty spans still require
valid bounds, ancestry and nonoverlapping ordered coverage.

Native recheck of all 414 exact files in the runner checkout: 414 successes,
zero failures (`/tmp/laplace-code-failed-recheck.log`). Native grammar acceptance:
14 passed. Managed grammar acceptance: 10 passed. Generic worker/database
acceptance: four passed, including byte-for-byte reconstruction of the failed
C header, TSX component and newline-only SQL artifact. Full deployed ingest replay
and its journal reconciliation remain required before delivery.

The inventor also requires proof that bot decisions actually use Laplace.
`LichessBot` calls `ChessLiveGameHost.BuildSearch` / `Search.Think` and posts that
move; this inspected path does not call Stockfish. It enables `EvalTerm.All`
alongside `SubstrateRootBias` and `SubstrateBoardEvaluator`. That conventional
search with evidence contributions does not prove the complete spec-11 decision
program. Required acceptance remains per-move proposal/steering/selection/witness
receipts using real substrate evidence, source separation for engine testimony,
and demonstrated influence of observed outcomes on later decisions. Do not label
classical search or a fixture with fabricated evidence as that acceptance.

Deployed source replay `9c13b907-1166-4722-a9af-9ac55b1f12c4` on the fixed
native payload recorded `status=ok`, 2,474 files done and zero failed units.
It admitted 302,370 novel rows through 30 database round trips in 40,923 ms;
CLI including maintenance took 79 seconds. This reused the earlier successful
files and is not a like-for-like speed benchmark. Workflow 34233408139 failed
only the absent-throughput-baseline check and skipped its requested idempotency
job. Correctness jobs must depend on successful journal verification independently
of the performance verdict; performance failures must remain visible/red.

Chess search now uses ordered native containment over the same composed query
floor as ingestion, collapses identity-preserving singleton wrappers, and ascends
complete Content frontiers to witnessed names. A character-set match cannot stand
in for ordering or a higher-tier word operand. One native operation owns exact
lookup, complete candidate standings, requested sort/page and final batched labels.
The managed double search, hidden 2,000-candidate ceiling and display-text fuzzy
ranker are removed. Regression coverage includes surname/full-name floors,
ordered fragments versus anagrams, profile-only players, exact-only lookup,
canonical outcome arena and both pages of ascending game counts. The shared RLE
matcher also has an independent expanded-sequence oracle for terminal matches.
Required live Carlsen/API readback remains pending deployment.

Coding-corpus continuity: nine TinyCodes Parquet shards and Stack v2 C/C++ shards
exist under `/vault/models`, while CLI defaults only search the ingest data root.
TinyCodes extraction currently reduces the prompt to keywords and retains the
response; repair must retain the complete observed prompt/response pair and its
source identity. A corpus having loaded, or source identities having deduplicated,
is not proof of generated code repair. Preserve the repository defect → relevant
witnesses → generated patch → compiler/tests acceptance obligation alongside chess,
query/forward-pass, ingestion and product repairs.

Fresh installed MCP STDIO execution succeeds (`runtime.VxaDpG`); the already
connected Codex MCP process still points at the deleted `runtime.yA1pBH` release.
Use the fresh installed MCP protocol process for current development calls.
Native MCP coding acceptance on 2026-09-08 asked for a C `add` function and returned
128 periods after 26,132.9 ms, recorded as witnessed output. This is failed
forward-pass/coding acceptance, not a generated repair or proof of intelligence.
Receipt: `/tmp/laplace-native-coding-response.jsonl`. The complete prompt must
condition admitted evidence; punctuation-only continuation is not that operation.

TinyCodes full prompt/response preservation is implemented through the existing
native full-source composer and ordered composition operation. The corpus's
complete prompt and grammar-composed response are separate ordered constituents;
source-qualified HAS_EXAMPLE testimony carries that observation root as context.
Different instructions paired with identical code remain different observations.
The database test reconstructs both texts exactly, including decomposed Unicode,
and verifies the negation-sensitive observation identity. The CLI also resolves
existing coding corpora in the sibling model vault when the ingest-root path is
absent. Full physical TinyCodes ingest and coding acceptance remain required.

The live chess native read now returns `Carlsen, Magnus` from the partial surname
(752.462 ms for the first measured SQL call). The HTTP read exposed a missing
`text[]`/`bool` mapping in the shared managed catalog validator before SQL execution.
Commit `0d125a87` adds scalar/array mapping for every catalog parameter type and a
real managed/PostgreSQL test of the six-parameter search reader. API delivery and
readback of that correction remain required.

## Physical coding-corpus inspection and reader acceptance

The installed managed catalog binding fix returns the live Carlsen search with HTTP
200, one Magnus profile, in 479 ms (one observation). The current CI product proof
still fails the natural forward-pass hot/cold acceptance; do not call CI green.

A read through the repository's C# Parquet reader inspected the full selected
`/vault/models/tiny-codes/part_9_1632520.parquet`: 19,574,261 bytes, **32,516** rows,
no empty prompts or responses, 13 language values. Its 2,327 Cypher rows were
silently skipped because no Cypher grammar is installed. Responses without a
language grammar now retain their complete observed text through native Markdown
composition. This preserves text; it does not assert a Cypher AST. The generic
physical-worker regression ingests a Cypher record and reconstructs its prompt
and response byte for byte. All five grammar-source database tests pass.

Footer inspection of `/vault/models/stack-v2/data/C++/train-00000-of-00007.parquet`
found 9,048,860 rows in 2,887,418,190 bytes, with blob/repository/revision identities,
paths, languages and other metadata, **no content column**. The Stack reader now
reports the missing payload explicitly instead of returning a successful empty
stream. Four Parquet reader tests pass. Resolving the referenced source payloads,
retaining complete source row metadata and composing embedded code regions remain
implementation obligations; this inspection is not proof that those code bodies
have been ingested or that the forward pass can edit and test code.

The corpus/chat cross-path test then exposed distinct Markdown-source and
canonical-text roots for the same plain-text instruction. The pair now keeps
its exact observed source trajectory while its HAS_EXAMPLE subject is composed
and staged by the same ContentTierSpine used for conversation admission. A DB
regression resolves the original prompt through `converse.prompt_tree` and
retrieves the witnessed response directly by that ID. NFD bytes remain intact
in the observation context. Native tree resident bytes include this retained
canonical tree. Missing task_id no longer fabricates a CodeConcept from the
Parquet shard filename and row ordinal.

Repository replay and idempotency workflow 34237635006 completed both native
runs with 2,476/2,476 files and zero failures. The second run took 1.03 seconds
in the ingest journal, and CodeDecomposer evidence remained exactly 10,638
before/after. This establishes replay evidence-count stability, not a compile,
cleanup or generalization result. The workflow is still red because its
throughput gate has no accepted CodeDecomposer baseline; the correctness job
now executes and passes independently of that performance-gate failure.

## Repository semantic query payload

A live exact-name containment query for `laplace_grammar_source_compose` returned
96 source structures in 199.6 ms (34,877 shared-buffer hits, 6,557 reads). Its CALLS
lookup returned zero, although the source contains calls. The stored repository
has 9,184 CALLS and 1,452 canonical HAS_DEFINITION witnesses; `DEFINES` is an alias
and must not be used as a raw `relation_type_id` key to count definitions.

The managed grammar resource glob referenced the removed repo-local external
folder, instead of the configured native dependency tree. Only owned Python/SQL
query resources were packaged. Managed builds now obtain the query root from
the linked native build's CMake configuration, with explicit dependency-path and
repo-local fallbacks, and reject a missing pinned payload. This also covers seed
workflow CLI rebuilds that do not inherit the build workflow environment.
The TypeScript/other query reader accepts both nested and root query locations.
Owned C, C++ and C# supplements record named call sites; these remain references
to names, not claims that overload/scope resolution has already found declarations.
All packaged queries compile against their linked parsers, and complete native
source composition records definitions and calls in C, C++ and C#. Nine focused
grammar-source tests pass. Replay/readback with this payload is still required.
