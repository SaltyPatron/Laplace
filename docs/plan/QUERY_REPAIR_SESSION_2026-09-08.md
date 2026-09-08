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
