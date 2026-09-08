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

Uncommitted implementations exist for native display/browse/container reads,
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
ISA gates pass; the catalog owns 22 statements with 486 legacy runtime literals
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
