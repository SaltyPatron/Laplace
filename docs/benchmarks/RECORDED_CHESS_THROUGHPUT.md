# Complete recorded chess throughput

The `recorded` benchmark suite measures ordinary Chess Lab jobs from game generation through synchronous PostgreSQL writer completion and exact native game readback. The API, native parser/composer, shared writer and native readback remain the production implementations. The collector checks their receipts and aggregates measured work.


## Paired complete-game comparison with Stockfish — 2026-09-17

[Run 35242394194](https://github.com/SaltyPatron/Laplace/actions/runs/35242394194)
completed a ten-pair, **20-game comparison: Stockfish won 20–0**. Laplace
lost all ten games as White and all ten as Black. Every game ended by checkmate;
there were no draws, clock forfeits, crashes, move cutoffs or adjudicated endings.
The ten distinct native starting positions each appeared twice with colors
swapped, and the original PGN agrees with those readback pairs.

| Observed result | Value |
|---|---:|
| Laplace wins / draws / losses | 0 / 0 / 20 |
| Completed color-balanced opening pairs | 10 |
| Newly committed and exactly read back games / plies | 20 / 1,016 |
| Minimum / maximum plies per game | 18 / 103 |
| Play interval, with two games concurrent | 118.0003033 s |
| Complete server play + recording + readback | 199.1873289 s |
| Complete match and retained replay | 202.5596944 s |
| COPY transactions started / committed | 39 / 39 |
| Retained replay writer counters | All 13 zero |

Both engines used **0.25 seconds per move**, with the existing CuteChess
2,000 ms time margin, and concurrency two. Stockfish identified itself as
Stockfish 19 and actually received Threads=1, Hash=32 MiB, NumaPolicy=system,
UCI_LimitStrength=false and Ponder=false. Laplace used its installed substrate
mode with one search thread and nominal 32 MiB transposition table. This
matches the declared search-thread/TT allocation; it does not equate total
process RSS or database/provider work. The selected opening suite was
authenticated and native game validation established all ten distinct starts.
The request's opening-depth bound was ten plies; the actual starting positions,
not an assumption that every opening had that length, define the comparison.

Ordinary recording established all 20 new complete games, exact ordered move
identities, witness membership and experiment body, with synchronous_commit=on,
fsync, full_page_writes and acknowledged local WAL flush. Its commit interval
was 78.4853853 seconds and exact readback took 1.3654363 seconds. The explicit
retained ingestion then read back the same 20 games and 1,016 plies, with zero
writer calls or inserts and three unchanged retained scope snapshots. These
snapshots cover the recorder's declared explicit entity/content/game/experiment
witness scope; they are not a snapshot of every unrelated database row.

Source `eeb103178b593670a5f204b7c0daca4ef8eab32b`, database incarnation
39,589,789 and recorded-cache generation `681ba884…eace4dc3` remained unchanged.
The comparison ran after the full 2,280-game cache publication below. Its 20
new games are separately recorded database evidence; this run did not export
them into another cache generation. The run-owned API credential was revoked
through the normal API-key lifecycle after completion.

This is a clear loss on this small, short-clock sample. It is not an Elo
estimate, a long-time-control strength claim, or a measurement of the
2,500 recorded-games/s ingestion target. The completed comparison and its
losses are retained without restarting or selecting a more favorable result.

The primary artifact is `10506715000`, ZIP SHA-256
`d81ae9b31a80db2a848a9d3c9cd5fd4cacf245cbcf1f04fa41f1eb93d8399b6b`.
The root comparison receipt is
`f74dfc3efcea0149a5aba613464750a985f490c3c0b15d8252838d7ca4d38536`;
the recording receipt is
`f3af32df5c3459391780709998fd8cab2f57581dd0cbf33baa2b13d57e358b71`;
the replay receipt is
`a1b7e1ddf786e2d4d6da48a0460b642eb4cd2063266380808f5dd6b146d5f194`.
The original 31,322-byte PGN has SHA-256
`0c1af6e76406549f3e9493cc47445a6da496588187d7d5223f683315b6119099`.
[The authenticated reader](https://github.com/SaltyPatron/Laplace/actions/runs/35243502065)
retains the complete JSON and PGN evidence.

## Completed recording after performance deployment — 2026-09-17

[Run 35240399243](https://github.com/SaltyPatron/Laplace/actions/runs/35240399243)
completed **140 further new games at 0.562989154 recorded games/s** on installed
source `eeb103178b593670a5f204b7c0daca4ef8eab32b`. All 7,527 plies passed exact
readback, and the complete replay performed zero work in all 13 writer counters.
The selected Playing IDs are disjoint from the previously completed 140 and
2,000 games, bringing this recorded union to **2,280 games**. The later completed full cache export is documented separately below.

| Observed result | Value |
|---|---:|
| New complete games / distinct lines | 140 / 140 |
| Full plies; minimum / maximum per game | 7,527; 23 / 97 |
| Published fresh-admission denominator | 248.6726413 s |
| Recorded rate | 0.562989154 games/s |
| Whole preparation + fresh + replay workflow | 259.2722243 s |
| Preparation / setup | 5.6639048 / 1.0104989 s |
| Exact zero-writer replay | 3.8682856 s |
| Committed, verified chunks | 2: 80 and 60 games |
| Inserted E / P / A rows | 287,589 / 286,727 / 40,525 |
| COPY transactions started / committed | 26 / 26 |
| Duration-qualified / 2,500 games/s target met | true / false |

The rate uses exactly `140 / 248.6726413`. The inner fresh receipt's
248.3148097-second interval is narrower and is not substituted as the rate
denominator. The source remained the same 256,162,073-byte original PGN with
SHA-256 `9eafc83ed9a97f9dcfc82135448d45653d325a742e497e8d08c3085a6a48b0ea`.
Preparation rechecked all 8,624 eligible complete games, found the prior 2,140
already present, selected these 140, and observed 6,344 further novel games at
that time. This is a dated inventory observation.

The newly exposed existing backend counters identify the dominant next target:

| Nested writer work | Seconds | Share of fresh admission |
|---|---:|---:|
| Consensus upsert, including argument packing and database calls | 197.3125603 | 79.35% |
| Highway-mask updates | 1.7247646 | 0.69% |
| Full consensus acceptance participant | 199.2070600 | 80.11% |
| Interpretation publication | 14.8722338 | 5.98% |
| COPY and its transaction commits | 13.1872114 | 5.30% |
| Physicality provider admission | 11.0317571 | 4.44% |
| Presence verification | 3.1303467 | 1.26% |

These are nested measurements, not additive exclusive phases. Exclusive
`WriterApply` was 243.73376588 seconds, or 98.01% of fresh admission. The
upsert counters cover 40 calls and 25,340 processed cells; mask counters cover
two calls and 40,847 processed pairs. These are work counts, not necessarily
unique cells or pairs. Both successful apply windows were sampled. The
phase journal dropped zero entries, unlike the earlier bounded tails.
Upsert wall time does not separate query planning, locking, evidence scanning,
sorting, and native arithmetic; representative PostgreSQL plans are needed
before assigning it wholly to the math kernel.

This is not a controlled speedup comparison with either prior sample: the
selected games are different and shorter on average, the database already
contains the prior 2,140 games, and the corrected owned producer grant changes
chunk grouping. The observed 614,841 inserted table rows are 4,391.72 per game,
not a count of independent chess concepts. The result establishes correctness
of the deployed changes and identifies the remaining cost; it does not establish
an end-to-end throughput gain or attainment of 2,500 games/s.

Before/after runtime snapshots matched exactly in the same database incarnation
(OID 39,589,789; system 7672946663471807927). The 12-CPU affinity remained
unchanged. Observed one-minute load was 1.13 before and 1.29 after; available
memory was 82,704,932 and 82,700,960 KiB. CPU and memory PSI averages were zero;
I/O `some` avg10 rose from 0.56 to 3.46. These are resource observations,
not proof of an exclusively idle machine.

The normal main lifecycle had already published and reconciled the matching
application, then failed its missing-foundation live gate. That failure and
`fullProductLifecyclePassed=false` remain recorded. The subsequent current
installation proof verified all four managed payloads and the actual serving
generation before this measurement; no successful whole-product claim is made.

The primary artifact is `10505097671`, SHA-256
`a182c361b71404f2a74f731d37a3f3ae5e0fec19e3c8c54fec84900bb6c3d36c`.
Its followup receipt is
`db453119c3fb42a63efa7ffaaeb5111fd40cf87ddcfc09c5b0e93d93b6a4d05b`;
the complete measurement receipt is
`4c4605ac5a83f044b9724663aca1ec37a0bd96632ccd87f5d3577e14ff79c6a8`.
[The authenticated reader](https://github.com/SaltyPatron/Laplace/actions/runs/35241247332)
retains the exact fresh/replay receipts, chunk manifests, machine snapshots,
and before/after runtime identities.

## Full recorded cache after the followup — 2026-09-17

The later cache job in
[run 35240399243](https://github.com/SaltyPatron/Laplace/actions/runs/35240399243)
completed all 12 required phases on the same installed source. Its ordinary
full recorded-corpus export hydrated exactly **2,280 playings**, including the
strict union of the completed 140 + 2,000 + 140 measurements. It exported
163,378 position occurrences and 161,098 transition occurrences, including
139,276 unique transitions. No recorded-selection override was used.

The installed generation is
`681ba884d7af8ca8f7d61bf38101a90b7382c6db4d3374764fc796a3eace4dc3`.
Its paired floors contain 375,774 position records and 146,109 transition
records, including finite/seed coverage; these are not counts of new games.
The current API, UCI, MCP and Lichess payload refresh checks passed. The direct
UCI process mapped both selected files, and the actual API serving proof
verified a canonical recorded witness and both selected mappings. Its persistent
transition-hit counter increased by six around the request. Concurrent requests
were not excluded, so this is a process-lifetime counter observation, not
exclusive request attribution or a throughput measurement.

The recorded cache receipt is
`ac1d70b0e6930841debcb5973bfc75dbe9a81349577c5a6041ac25605bb05284`;
the serving receipt is
`00973ce32d14b7e30e62e057b596410aab9d60c603e72023145b37831bec6aef`.
Artifact `10505532233` has SHA-256
`dbb27abb0f11789cbe0bb2cf05f13395f9a9bc2ad83a90d9153925978ab7be3a`.
[The authenticated cache reader](https://github.com/SaltyPatron/Laplace/actions/runs/35241779535)
retains these receipts, the complete export receipt, the installed pair and
the current runtime identities. The transition-v1 header remains unchanged;
the pair/export receipts provide its source and database binding.
The whole-product lifecycle remains unsuccessful outside this verified chess
scope; no missing-foundation or model proof has been relabelled as passed.

## Completed 2,000-game baseline — 2026-09-17

[Run 35223192542](https://github.com/SaltyPatron/Laplace/actions/runs/35223192542)
completed **2,000 new complete games at 0.596123973 recorded games/s**.
It used unchanged source `ea9f60a1c2bac9fcc36491a6f26403d95c2b8359`,
the same canonical database OID `39589789` and system
`7672946663471807927`, and the original 256,162,073-byte PGN with SHA-256
`9eafc83ed9a97f9dcfc82135448d45653d325a742e497e8d08c3085a6a48b0ea`.
All selected Playing IDs were distinct and excluded the earlier 140 games.

| Complete recorded-game measurement | Actual result |
| --- | ---: |
| Newly recorded games / distinct lines | 2,000 / 2,000 |
| Ordered plies checked by exact readback | 143,025 |
| Plies per game, minimum / mean / maximum | 11 / 71.5125 / 252 |
| Completed chunks | 237 |
| Games per chunk, minimum / median / maximum | 5 / 8 / 34 |
| Fresh admission wall time used for rate | 3,355.0068303 s |
| **Complete recorded games per second** | **0.596123973** |
| Minimum 30-second window satisfied | Yes |
| 2,500 recorded-games/s target attained | **No** |
| Exact replay games / plies | 2,000 / 143,025 |
| Replay writer calls and all 12 other writer counters | 0 |
| Exact replay wall time | 95.1072662 s |
| Complete corpus owner, including setup, preparation and replay | 3,456.8500822 s |

The published rate is exactly `2000 / 3355.0068303`. The enclosing fresh
admission interval is slightly wider than the inner recording phase's
3,354.9111026 seconds; these denominators are not substituted for each other.
Setup took 1.0120514 seconds and preparation 5.6620029 seconds. Preparation
scanned 8,624 complete legal source games, identified the 140 already present
games, selected 2,000, and observed 6,484 further eligible novel games in that
scan. It did not shorten complete games or add synthetic occurrence headers.

Fresh writes acknowledged synchronous commit with `fsync` and
`full_page_writes` enabled. Every game was read back in full. Replay
preserved the exact game bodies and the 29,423-row evidence/standing scope
(byte SHA-256 `6afbb371bacb7f9d8b71ca59e00b54ac8659af5d6bbb4fbf651e8bf024a5e054`).
The final runtime comparison passed; publication-before and publication-after
have the same SHA-256
`97d4fcca107aa1ca9584957bd7120dd4da42754677e7cc3c57656490ca6b421b`.
The whole workflow succeeded. Target attainment was not required for completion.

The larger baseline identifies consensus acceptance as the dominant measured
cost:

| Timing window | Seconds | Scope |
| --- | ---: | --- |
| Writer apply | 3,251.179962 | Exclusive; 96.91% of fresh admission |
| Consensus acceptance | 2,515.565909 | Inside writer; 74.98% of fresh admission |
| Provider admission | 202.144723 | Inside writer; 6.03% of fresh admission |
| Presence verification | 134.755843 | Inside writer |
| COPY and its transaction commits | 113.101556 | Inside writer |
| Exact game readback | 32.090912 | Exclusive, outside writer |
| Before/after scope probes | 44.741568 | Exclusive, outside writer |
| Composition and novelty probe | 16.860300 | Exclusive, outside writer |

All 237 observations for every named writer phase returned; none was
interrupted or rejected. Named writer windows total 2,984.420805 seconds,
leaving 266.759158 seconds of unclassified writer time. Nested times must not
be added to the enclosing writer or admission totals.

This participant contains both consensus upsert/folding and highway-mask work;
this receipt does not separate them. Existing counters can distinguish those
costs in a subsequent measurement.

| Nested cost per complete game | First 140 | Next 2,000 |
| --- | ---: | ---: |
| Consensus acceptance | 0.091999 s | 1.257783 s |
| Provider admission | 0.102729 s | 0.101072 s |
| COPY and transaction commits | 0.065838 s | 0.056551 s |
| Presence verification | 0.024374 s | 0.067378 s |

Consensus cost per game was 13.67 times the earlier sample's value, while
provider cost per game stayed nearly unchanged. Mean game length decreased
from 75.329 to 71.513 plies. These are different novel samples and database
populations, not a controlled causal comparison. The current native consensus
owner reads and folds the complete retained evidence for each touched cell;
that history-dependent work is the next measured optimization target.
An order-sensitive incremental rating update would not preserve the current
canonical-period semantics.

The retained final five consensus calls took 24.640, 22.297, 23.080, 9.163
and 6.990 seconds. Their chunks contained respectively 5/657, 5/593, 5/668,
32/411 and 10/228 games/plies. Their different shapes preclude treating these
five times as a monotonic history trend. The bounded log dropped 6,508 older
entries; all phase totals/minima/maxima remain, but the overwritten heartbeat
file and console phase messages do not reconstruct every earlier chunk's
timing.

The writer inserted 5,587,526 entities, 5,574,826 physicalities and 876,217
attestations: 12,038,569 table rows, or 6,019.2845 per complete game for this
sample. It acknowledged 3,081 COPY transactions and counted 4,266 logical
writer round trips. These counters remain distinct from semantic objects,
network packets and complete games. The lower table-row fanout per game did
not prevent the higher consensus cost.

Before/after snapshots showed 12 logical processors, one-minute load averages
0.11 and 1.13, and available memory 83,567,660 and 83,462,192 kB. The snapshots
do not establish exclusive host ownership or continuous resource utilization.
The unchanged installed recorded cache still covered the previously exported
140 games; completion of these 2,000 admissions does not claim they were
already exported into that cache.

Artifact `10500925768`, 27,057,496 bytes, has SHA-256
`dd223da16b6172d440ed10978a9356771ae34bc19d4259ee7385077a988f545f`.
Evidence is under
`/build/laplace/recovery/canonical-next2000/35223192542-1`.
The completed root receipt SHA-256 is
`7bfb9ac3e021c5c0d4b0644da2ca45bbfd720d3a17a94ce70b8b0301658e19ff`;
the capacity receipt is
`6ebd269f6fb12aa89a3d7a2f67d3ebaeeeeb805ce8b5b4c3e0e26db86cb44ae0`.
The [authenticated reader](https://github.com/SaltyPatron/Laplace/actions/runs/35229354961)
retains the exact component files, chunk manifests, machine snapshots and logs.
The evidence index records all corresponding file identities.


## Completed canonical recording and geometry baseline — 2026-09-17

[Run 35220694099](https://github.com/SaltyPatron/Laplace/actions/runs/35220694099)
completed a new 140-game admission in canonical database OID `39589789`,
PostgreSQL system `7672946663471807927`, using source
`ea9f60a1c2bac9fcc36491a6f26403d95c2b8359`. The input was the exact
140 original complete PGN frames retained by the earlier failed attempt,
exported without changing their game identities. They were new admissions in
this database, with new chunk and scope receipts.

| Complete recorded-game measurement | Actual result |
| --- | ---: |
| Newly recorded games / distinct lines | 140 / 140 |
| Ordered plies checked by exact readback | 10,546 |
| Completed chunks | 18 |
| Fresh admission wall time | 68.6798501 s |
| **Complete recorded games per second** | **2.038443587** |
| Minimum 30-second window satisfied | Yes |
| 2,500 recorded-games/s target attained | **No** |
| Replay readback games / plies | 140 / 10,546 |
| Replay writer calls and all 12 other writer counters | 0 |
| Fresh+replay corpus owner elapsed time | 81.3785449 s |

The recorded rate is exactly `140 / 68.6798501`. Its fresh-admission
denominator includes composition, provider admission, canonical writes,
consensus, exact game readback, scope checks and evidence completion.
Preparation and the subsequent zero-write replay are reported separately;
they are not relabelled as novel recorded games. The replay's complete game
bodies and 4,435-row exact evidence/standing scope matched the fresh admission.
Fresh writes acknowledged synchronous commit with `fsync` and
`full_page_writes` enabled.

The exclusive recording windows identify the main cost:

| Exclusive child window | Seconds |
| --- | ---: |
| Writer apply | 57.981283 |
| Exact readback | 6.235710 |
| Composition and novelty probe | 1.495123 |
| Before/after scope probes combined | 1.936238 |
| Remaining measured child windows | 0.799585 |

Writer apply accounts for 84.42% of the fresh-admission wall time. Its complete
nested phase aggregates cover all 18 applies: provider admission 14.382112 s,
consensus acceptance 12.879812 s, COPY and its transaction commits 9.217334 s,
presence verification 3.412339 s, and other named writer windows 1.948677 s.
These are already included in writer apply. The remaining 16.141010 s is
unclassified writer time. The retained tail shows substantial interpretation
publication between COPY and consensus, but its five complete windows do not
establish that operation's total over all 18 applies. The full aggregates remain
available despite 376 older entries being dropped from the bounded log tail.

The retained before/after host snapshots report 12 logical processors and
affinity to all 12. One-minute load average changed from 0.51 to 1.23 across
recording and reached 1.51 after geometry; available memory remained about
83.5 million kB. CPU and memory pressure averages were zero in those snapshots,
while I/O pressure was nonzero. These are observed machine conditions, not a
claim of an exclusively idle machine or a continuous process-level resource
audit. The four complete machine JSON files are retained with the measurements.

The fresh writer inserted 557,188 canonical entity rows, 556,267 physicality
rows and 66,825 attestation rows. It acknowledged 234 COPY transactions and
reported 324 logical writer round trips. That is 8,430.571 inserted rows across
those three tables per complete game for this exact novelty mix. These are
separate table and logical operation counters, not a count of independent
semantic objects or network packets. No provider or SQL optimization is
inferred to remove all of these costs.

The entity and physicality counts average about 52.8 rows each per recorded
ply. The source trace identifies substantial descriptor graphs, content
carriers and view receipts in that expansion; these are not 52.8 independent
chess concepts. Canonical body identity uses ordered content, while observation
and source-unit context belong to views and testimony. The counters do not
separate exact category shares. Reducing unnecessary representation expansion
remains an optimization opportunity; the measured fanout is not an immutable
lower bound on the work needed to record a game.

The same database then passed all 18 primitive geometry cases. Each case
inserted 100,000 exact existing physicality payloads in ten synchronous
10,000-row COPY transactions, followed by exact committed binary readback.
The table below reports the median of three repeats for each concurrency.

| Concurrent connections | Minimal logged geometry heap, rows/s | Logged full physicality clone, rows/s |
| ---: | ---: | ---: |
| 1 | 230,087 | 32,865 |
| 2 | 400,563 | 50,175 |
| 4 | 540,749 | 72,845 |

The minimal heap stores `id`, `coord` (`PointZM`) and `trajectory`
(`GeometryZM`) without indexes. The full clone carries the actual physicality
columns, checks, generated radius and 15 indexes: seven B-tree, two GiST,
five GIN and one BRIN. Both use the same observed durability settings as the
recording: `synchronous_commit=on`, `fsync=on`,
`full_page_writes=on`. The selected payload contains 31,517,368 geometry
EWKB bytes and 841,174 trajectory vertices; median trajectory length is five
vertices, 95th percentile 27, maximum 254. These are mixed existing database
rows selected by ascending physicality ID, not a chess-only sample.

Geometry rows/s measures warm, empty-target COPY plus connection, check/index
work and acknowledged commits. Source capture, table/index creation, TRUNCATE,
readback and cleanup are outside that write-rate denominator and separately
reported. The clone excludes foreign keys, user triggers, canonical partition
routing and occupied-table/index effects. Neither target performs game
composition, interpretation publication, evidence folding or legal game
readback. These are primitive storage baselines, not ordinary full canonical
admission rates and not recorded-game rates.

The recording and geometry receipts both completed. The outer workflow remains
**failed**: its final geometry identity check compared the JSON string
`"39589789"` with integer `39589789` and stopped before its extra
publication-after check. The authenticated
[result reader](https://github.com/SaltyPatron/Laplace/actions/runs/35221177028)
confirmed the same database, all 18 passing geometry cases and the completed
recording. A separate
[read-only finalization in run 35221551456](https://github.com/SaltyPatron/Laplace/actions/runs/35221551456)
completed at 12:31:22 UTC, authenticated both completed component receipts and
passed the publication-after comparison against the same source and database.
It retained the original failed receipt. Neither admission nor geometry was
reexecuted.

Evidence is retained in artifact `10497405601`,
SHA-256 `028faf189822ae7a1a25f1c01d8625c485e585d84b7080d9d17151ad33ec03b3`,
under
`/build/laplace/recovery/canonical-first140/35220694099-1`.
The completed recording wrapper is `recording/receipt.json`, SHA-256
`6714589eab654c2a468515d8ef8e8f33347b43189ee389f7bac466a73da63c98`;
the geometry receipt is `geometry/receipt.json`, SHA-256
`91925e73bb05a3a726cfcd6f812bbdfd6d37c76eb9f7e42b0ee511f8333638fc`.
The detailed identity and validation history is in
[canonical-identity-evidence.json](canonical-identity-evidence.json).

The installed generation has completed scoped chess-runtime activation.
Its receipt explicitly retains `fullProductLifecyclePassed: false`:
competitive model and corpus-dependent whole-product qualification remain
blocked by absent Stack-v2 source payloads. The later 2,000-game result above is a larger completed baseline, not proof
of sustained 2,500 games/s.
Qualifying that rate for at least 30 seconds requires at least 75,000 novel
complete games.


## Hosted-qualified performance followup — 2026-09-17

The subsequently deployed candidate removes the redundant preliminary plan for current
provider bodies. Every current body still reaches the combined authenticated
plan before provider selection, and the preliminary validation for filtered
admitted bodies remains. It also gives the standalone serial PGN owner its
actual single resident share; shared API/lab producers keep their existing
allocation. A named writer phase measures interpretation publication without
changing its SQL or acceptance semantics.

[Native comparison 35225151054](https://github.com/SaltyPatron/Laplace/actions/runs/35225151054)
passed 549 core tests and nine Syzygy tests for both baseline and candidate,
with the same three pre-existing media skips. All 33 exported ordered binary
streams matched, including the auxiliary interpretation streams. The actual
PostgreSQL-header helper passed 438 checks for each version; its backend doubles
do not establish a PostgreSQL runtime pass. That workflow retains its failed
status because its subsequent managed setup lacked the required opening data.

The corrected
[managed continuation 35225959742](https://github.com/SaltyPatron/Laplace/actions/runs/35225959742)
used the existing finite opening fixture and passed all 11 chunk tests with
zero skips and the actual candidate core mapped. It changed no production
or test assertions.

Five sequential hosted samples put the shared-provider materialization median
at 9.477464 ms before and 9.287374 ms after, and the small current-capture
median at 0.241704 ms before and 0.228063 ms after. The no-provider control
was 0.75% slower and another unchanged plan control varied by 9.7%.
These are finite native timing observations with visible run-order variation,
not a complete-game speedup. The completed 2,000-game baseline used the unchanged source. The later deployed 140-game result above measures the complete recording path, with its different sample, occupancy and chunk grouping explicitly retained.


## Recorded cache and functional chess delivery — 2026-09-17

[Run 35221551456](https://github.com/SaltyPatron/Laplace/actions/runs/35221551456)
completed the matching chess tools and recorded-floor deployment on source
`ea9f60a1` in database OID `39589789`. Normal export legally hydrated
exactly the newly recorded 140 games: 10,546 transition occurrences and
10,058 unique transitions. It built and installed generation
`851c577c8156f696583b47dff1980ab14284e60b78822d5e262b7429e1e04355`.

The paired floors contain 248,869 position records and 17,793 transition
records, including the existing finite and seed coverage. These are not counts
of newly recorded games. All 15 cache deployment phases passed, including
exact UCI mapping of both selected files and actual API serving. The API
process mapped both files, and its persistent transition-hit counter increased
by one around a request for a canonical recorded witness whose position and
transition were absent from reproduced seed floors. Concurrent requests were
not excluded; this is a process-counter observation, not exclusive attribution.
No API position-hit or cache-throughput claim is made. The raw transition-v1
header still has no source identity fields; the generation's pair and export
receipts provide that binding.

The independent tools job completed all 119 tracked files from official
Stockfish commit `edb0d9db6731067ec50ce619ff372b463bc4dd5d`, with exact
byte/native readback and a repeat inserting zero entity, physicality and
attestation rows. It revalidated and reused calibration report
`19fddbd62f90999398cc8f7efc32230505f35e8002496d5a217702345266fbe1`;
the sweep was not repeated. The GUI actually applied Threads 8, Hash 16 MiB
and Ponder false, then completed a 60+1 game: 75 plies, Stockfish 1–0 Laplace,
158.694 seconds. Native PGN/protocol validation and normal GUI exit passed.
This is one functional game in the owned Xvfb session, not a playing-strength,
persistent-desktop or durable-recording measurement. Service restart to
authenticated readiness took 3.504492 seconds; an OS boot was not measured.

The current source inventory found the requested
`/vault/External/Stockfish/SF_19` directory and source layout, but no Git
marker in its bounded depth-two scan. The configured clean official checkout
remained `/build/external/stockfish`.

Cache evidence is artifact `10496819383`, SHA-256
`ea0f4a613ebc61a4cb4a6bef2f39311015655ec3e50a45767347d80ca6ed8e29`.
The inner cache receipt SHA-256 is
`e4a7299e25f6ff32ac4bd0949d4656314de74d52cfd21dc29c3e5dcd0414ad39`;
the serving receipt is
`6a84a4bc17b5b0b2bf0ab085bb248f417a0bd54ebf8906c4eac00c0a9c12829e`.
Tools evidence is artifact `10497043626`, SHA-256
`db0b9ea5a21e7b89c57449704062995543e8e793f14966c2b0d0b3c51f5dfec1`.
The evidence index retains the exact runtime, file, calibration, GUI, source
and publication identities.


## Installed recording attempt — 2026-09-17

[Run 35184890182, attempt 1](https://github.com/SaltyPatron/Laplace/actions/runs/35184890182)
installed source `3a161d0f8390d4d5c3f9309d5a08746418d13b7d`
(tree `f9db5544b0f14cb2eece60a1312706a77f0392ba`). Its native installation
completed all 14 phases, and managed application publication completed all 19
phases. The canonical consensus repair completed for all five chess sources
without changing their evidence counts. The recording pilot was then cancelled
while processing its seventy-ninth chunk.

| Retained measurement | Actual result |
| --- | ---: |
| Requested and parsed complete games | 2,000 |
| Newly recorded games with sealed exact readback | 594 |
| Ordered plies in those sealed games | 47,162 |
| Sealed chunks | 78 |
| Composed candidate games at the last checkpoint | 601 |
| Acknowledged writer calls | 78 |
| Committed COPY transactions | 1,003 |
| Logical writer round trips | 1,403 |
| Inserted entity rows | 1,971,190 |
| Inserted physicality rows | 1,967,407 |
| Inserted attestation rows | 284,295 |
| Journal replay hits | 0 |

The selection excluded the earlier 140 sealed games and reselected the seven
previously incomplete candidates. Composed candidates and the open seventy-ninth
chunk are not added to the 594 sealed-game count. Each of the 78 current chunk
bodies was checked against its manifest hash. Their writer counters are
cumulative: use the last cumulative values above, not the sum of 78 snapshots.
Similar entity and physicality totals do not establish a one-to-one identity law.

For this particular sealed selection, the three table counters average
3,318.502 entity rows, 3,312.133 physicality rows and 478.611 attestation rows
per complete game: 7,109.246 inserted rows across those tables. If that same
novelty mix and representation were sustained at 2,500 games/second, the
arithmetic would require about 17.77 million combined table-row insertions per
second, before accounting for index maintenance, interpretation facets,
consensus work, WAL and exact readback. This is a workload projection, not a
measured storage rate or a forecast of attainable game throughput.

These are separate table-write counters, not a count of independent semantic
objects. Existing canonical entities can be reused, while a new physicality
or observation may still require recording. Different input novelty and less
intermediate expansion can change the per-game write demand. The geometry
COPY baseline and exclusive phase timings are intended to distinguish that
representation cost from avoidable composition, lookup, transaction and
readback overhead; the current expansion is not assumed to be optimal.

The final recording receipt, full-selection replay and after-measurement runtime
guard were not produced. This attempt therefore supplies committed chunk
evidence, not a completed recorded-game rate or the 2,500-games/second result.
Its last heartbeat was at 774.065 seconds; that diagnostic timestamp is not used
as a successful benchmark denominator. The interrupted artifact retained no
exclusive writer/readback timing totals, so those costs cannot be reconstructed
from its sampled phase names.

Evidence is retained in artifact `10482117875`,
`original-native-install-35184890182-1`, with ZIP SHA256
`d97b27055b3a34b6383f6d79d2f11d3dbb07a98e5c7fd6195a99e4701428d2e9`.
The completed managed publication receipt SHA256 is
`5e363d10cd2b8b3309bb343a0ea99840961cd1bf4959d1ed6ba12e008c2c8db3`.
These identify the retained attempt; a later machine check still reads the
currently installed files and PostgreSQL generation.

## Exact native consensus calculation — 2026-09-17

[Qualification 35188643764](https://github.com/SaltyPatron/Laplace/actions/runs/35188643764)
compared the qualified native implementation
`ac5f844627d7597279ae70f07ab084b09ba9b532` with
`ec39c1f3bf57ec534a33dfdf769ffda77a222775`. The change reuses only the
opponent-dependent calculation for adjacent identical rating/RD pairs within
one canonical period. Every retained row keeps its count, score quotient and
remainder, rounding, validation, accumulation order and final solve.

The actual controls passed 759 arithmetic reference cases, 256 whole-period
reference cases, 505 exact old/new grouped comparisons and 46 native tests,
with no UBSan finding. All 30 timed output states and checksums matched.

Each table entry is median CPU milliseconds for 25,000 folds across three
trials on the GitHub-hosted qualifier, using identical compiler settings.
The instrumentation that counts calculations was run separately.

| Input pattern | Previous CPU ms | Cached CPU ms | Change |
| --- | ---: | ---: | ---: |
| One group | 35.930 | 35.736 | 0.54% lower |
| Actual retained 60 rows, identical opponent | 260.703 | 59.739 | 77.09% lower; 4.36× faster |
| Alternating four opponent pairs | 251.667 | 254.199 | 1.01% higher |
| Four-row runs of repeated pairs | 246.804 | 102.221 | 58.58% lower; 2.41× faster |
| Sixty distinct opponent pairs | 258.396 | 259.781 | 0.54% higher |

Measured opponent calculations fell from 60 to one for the retained uniform
case, and from 60 to 15 for repeated four-row runs. All-miss cases retained 60
calculations. This is native calculation time: the measurement excludes SQL
scans, evidence sorting, COPY, complete-game composition and readback, and
does not imply a corresponding recorded-game speedup.

Artifact `10483035849` has ZIP SHA256
`ebdcf39d354757f72622c2483777c27f8c95feb6e549e406e5839128240fb457`.

## Earlier retained corpus evidence — 2026-09-17

The earlier native installation and attempted retained-corpus run is
[35174504991, attempt 1](https://github.com/SaltyPatron/Laplace/actions/runs/35174504991),
using source `ee2995b65e0f0182ae0776a9524443a6240d72b5`. The corpus measurement
**failed**; it does not establish recorded-game capacity.

| Measurement | Actual result |
| --- | --- |
| Requested complete games | 2,000 |
| Complete games committed and exactly read back before failure | 140 |
| Ordered plies in those games | 10,546 |
| Completed admission chunks | 20 |
| Attempted admission chunks | 21 |
| Elapsed time through failure | 60.071 seconds |
| Completed-run recorded games/second | `null` |
| Completed full-selection replay | No |
| 2,500 recorded games/second established | No |

The failed twenty-first chunk contained seven additional candidates. They are
excluded from the 140 complete-game count. Their attempted work remains in the
failure evidence; the retained games and original failed receipt are preserved.
Dividing 140 by the time through failure would not produce a successful complete
benchmark.

Whole-attempt diagnostics retained 21 provider calls totaling 22.155 seconds,
21 COPY/commit calls totaling 8.927 seconds, and 21 consensus calls totaling
4.279 seconds, of which one was interrupted. The enclosing writer interval was
41.557 seconds; exact game readback took another 14.915 seconds. The nested
provider, COPY and consensus intervals must not be added to their enclosing
writer interval. These are workload measurements, not isolated CPU measurements.

The 20 successful applies reported 557,199 inserted entities, 556,281 inserted
physicalities and 66,989 inserted attestations, across 260 COPY transactions.
Those counters exclude the failed apply and are separate from complete games.
Native descriptor and selected-view structures contribute additional canonical
entities and typed physicalities; the similar E/P totals do not establish a
general one-to-one entity/physicality law.

The failure exposed storage-flush-dependent consensus periods. The canonical
recording correction is committed in
[the source ending at dd31256](https://github.com/SaltyPatron/Laplace/commit/dd31256d3e8deca265a9b7861109e20273de52cb):
fully replayable retained testimony is folded as one canonical period per cell,
and source repair uses the same target locking and fresh evidence read as
admission. Mixed transient continuous-score cells retain their exact delta
semantics. A separate checked-arithmetic correction removes saturated
intermediates. Both corrections were subsequently installed by run 35184890182,
whose five-source canonical refold is recorded above. The repaired 60-row cell
retains 2,474 observations and reads rating `1798219672794`, RD `9058987541`
and volatility `59999089` on the native fixed-point scale.

The private host evidence directory for the attempted run is
`/build/laplace/recovery/native-install/35174504991-1/`. The corresponding
`original-native-install-35174504991-1` workflow artifact has ID
`10477948015` and SHA256
`667837b6c1f6dad4b703ab3669f41c8085a6e450579a67cc593d2d1bf40efeda`.
Use the original corpus receipt, completed chunk descriptors, exact readback
scope and writer aggregates together. Generated Stockfish self-play rates,
position-floor row counts, kernel timings and geometry COPY rates are separate
workloads.

## First establish correctness

The direct collector keeps the existing 24-game, depth-4 sweep over concurrency 1, 2 and 4. Each case must finish normal games, preserve its exact executable and experiment identities, acknowledge synchronous local WAL flush, and read back every newly recorded playing, ordered move identity and experiment witness.

```bash
python3 scripts/benchmark-recorded-chess.py \
  --api-base http://127.0.0.1:5187 \
  --output-dir /build/laplace/work/recorded-chess-correctness \
  --games 24 --repeats 1 --concurrency 1,2,4
```

Each concurrency point runs once in this example. The ordinary collector default remains three repeats. Existing API authentication settings (`LAPLACE_API_KEY`, `LAPLACE_QUOTE_ID`, `LAPLACE_PROOF_TENANT`) stay in the environment and are not copied into the evidence.

The schema `laplace.recorded-chess-benchmark/v2` distinguishes a finite sample rate from a sustained capacity result. `sampleTargetRateReached` reports whether a server-workflow sample median reaches the target. Correctness-only runs always have `durationQualified=false` and `targetMet=false`, even when a short sample is fast.

## Measure a sustained window

After inspecting the initial workload and artifact sizes, run the explicit existing-registry suite:

```bash
python3 scripts/benchmark_suite.py run --suite recorded --repeats 1 \
  --receipt-dir /build/laplace/work/recorded-chess-duration \
  --recorded-api-base http://127.0.0.1:5187 \
  --recorded-games 24 --recorded-concurrency 1,2,4 \
  --recorded-duration-seconds 30 --recorded-total-timeout 900 \
  --recorded-max-sustained-cases 4096 \
  --recorded-max-total-artifact-bytes 1073741824
```

The explicit `recorded` suite defaults to a 30-second duration window. It is excluded from `all`, because it writes real game and experiment evidence through the installed API. It requires the installed service and its dependencies; building a separate collector-side native library would not identify the service being measured. The workflow's existing explicit benchmark selector also exposes this suite under its shared host lock.

The collector first runs the correctness sweep, then selects the concurrency with the highest median complete collector throughput. A tie selects the lower concurrency. That selection is only a configuration candidate. A separate sequence of new complete jobs establishes its duration result.

Every job keeps the configured bounded game batch. No enormous single request is substituted to fill the duration. The collector continues until the requested elapsed duration and at least two complete jobs have finished, or an explicit resource/deadline bound fails. A whole job can extend beyond the requested minimum duration; the complete measured interval remains the denominator. The total execution deadline still limits that job.

### Rate boundaries

| Field | Numerator and denominator |
|---|---|
| `gamesPerSecondServerWorkflow` | New verified games / server entry through full play, normal writer completion and exact readback. Excludes API queue, final receipt serialization and artifact download. |
| `gamesPerSecondEndToEnd` | New verified games / one case's start request through observed completion and artifact download. |
| `gamesPerSecondSustainedEndToEnd` | Total distinct new verified games / one enclosing wall interval across repeated complete cases, including queueing, polling, transfer and inter-case bookkeeping. |
| `pliesPerSecondSustainedEndToEnd` | Exact retained ordered move count / that same enclosing interval. |
| `gamesPerDayExtrapolated` | Measured sustained-window rate multiplied by 86,400. This is an arithmetic extrapolation, not a day-long observation. |

Stage times remain available per case. The aggregate denominator is never a sum of stage durations or overlapping job durations. Current jobs finish their entire match before PGN ingestion; the measured sustained boundary is repeated complete workflows. It does not assert a continuous online game-to-writer stream.

`durationQualified` requires a successful window of at least the explicitly requested 30..3600 seconds and at least two complete, distinct jobs. `targetMet` additionally requires at least 2,500 newly recorded and exactly verified games per second over that complete interval. The target is a requirement, not a claimed measured result. A below-target successful measurement still exits successfully and reports `targetMet=false`.

## Bounds, failures and evidence ownership

Game batches are even and bounded to 2..512 games. Initial sweeps allow at most eight concurrency points in 1..64, each repeated 1..10 times. The sustained case ceiling is 2..4096, with a default of 4096. At the default 24 games per case and a 30-second minimum window, this permits 98,304 games and a count-only ceiling of 3,276.8 games/second. The receipt's `targetResourceEnvelope` reports this ceiling, the minimum target game count, and whether an explicitly smaller case limit admits the target. Actual game runtime, polling, transfer, byte limits and the total deadline still apply; this arithmetic does not establish achieved capacity. The total execution deadline is at most two hours; failed owned-job cancellation has a separate maximum ten-second allowance. No other job is cancelled.

Individual original artifacts are limited to 256 MiB, and their cumulative retained bytes are limited to the configured budget, at most 4 GiB. Partial local artifact writes count against that budget and are retained as `.partial`. Job/catalog API metadata is separately limited to 1 MiB per response; local checkpoints are bounded by the admitted job/game counts. These are explicit payload and workload bounds, not a claim to account for the server's total RSS or disk growth.

HTTP deadlines cover a whole response even if bytes keep arriving. Case deadlines also cover artifact downloads and collector verification. The output directory must be new or empty; an old receipt is never replaced by a new run. Within the owned run, JSON checkpoints are atomically replaced, and every case retains the original request, job state, experiment, recording receipt, PGN and transcript where available.

An incomplete or failed case stays in the aggregate evidence. Only completed, validated, distinct cases contribute to the partial-work counter, and any failed window withholds duration qualification and the capacity claim. The original native receipts retain actual inserted/skipped entities, physicalities, attestations, COPY transaction counts and logical writer accounting; rows and plies remain separate denominators.

## Retained input and replay are separate workloads

The ordinary retained-PGN ingestion route measures generation-free admission of an exact retained PGN and experiment. Its first/mixed/repair disposition and exact replay have separate receipts. Replaying an existing playing may demonstrate identity reuse and avoided writes, but it cannot be counted as newly generated-and-recorded throughput. A second generated match is also not a replay of the first experiment.

Use the [PostgreSQL geometry baseline](POSTGRES_GEOMETRY_BASELINE.md) to compare exact storage rows, vertices and byte payloads under acknowledged synchronous commits. That baseline helps locate write cost; it does not replace complete-game throughput.

## Cache reuse and reader lifetime

The chess position map remains native. Its readers retain access through the complete geometry copy; publication blocks new entries, drains the fixed reader shards and then releases the old mapping. Readers search concurrently without the global managed native gate. The exported pointer lookup returns a thread-local copy that survives another thread's remap/unload, but the next lookup on the same thread reuses that slot. New consumers that need independent values should use the caller-owned geometry-copy API.

The transition map publishes an immutable, fully validated replacement. Each reader holds a SafeHandle reference until its lookup completes, so retiring a mapping cannot invalidate a pointer in use. A rejected replacement preserves the previous valid map. Successful load/unload starts a fresh process-local derived-cache generation.

That derived cache has exactly 65,536 slots. Hash collisions evict acceleration entries; complete keys are checked before reuse, and misses follow the existing canonical composition path. Repeating an existing key with a conflicting result, or conflicting with a mapped result, is rejected. Observations expose capacity, occupancy and collision evictions separately from persistent-map hits. These counters describe reuse, not new testimony or admitted games.

Process-local novel transitions are not exported as a learned corpus. The transition v1 file has no source-generation or recipe identity, and its loader has no canonical rebuild/verification binding. The position header contains an emitter source hash, but the current load API does not take an expected source/recipe identity. Durable derived-cache reuse must establish those compatibility and rebuild links before accepting persisted computed entries; a dictionary dump would not establish them. PostgreSQL remains the system of record. File generation alone also does not establish installed lookup hits or cold-boot performance.
