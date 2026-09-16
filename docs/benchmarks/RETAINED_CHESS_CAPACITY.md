# Retained chess ingestion capacity

This collector measures newly recorded, complete PLAYING occurrences through the ordinary Chess Lab retained-PGN route. Engine generation happens before its timed interval. It does not change the production ingestor, canonical identity, PGN headers, or database contents to make an already recorded occurrence appear new.

The source workload is **Laplace substrate UCI versus Stockfish**, run by the official Cute Chess CLI. Every prepared job retains its actual command, requested options, executable hashes, managed/native Laplace payload identities, and completed game observations. Its generation time is not comparable to a Stockfish-versus-Stockfish calibration. Neither generation rate belongs in the ingestion numerator or denominator.

Run preparation and measurement under the existing host benchmark owner/exclusive lock, against the installed API and its normal database. These commands write real source observations through the existing endpoint; they do not use a disposable substitute writer.

~~~bash
python3 scripts/benchmark-retained-chess-capacity.py prepare \
  --output-dir /build/laplace/work/chess-capacity-corpus \
  --jobs 2 --games-per-job 24 --depth 4 --concurrency 1 \
  --timeout-seconds 3600

python3 scripts/benchmark-retained-chess-capacity.py measure \
  --corpus-dir /build/laplace/work/chess-capacity-corpus \
  --output-dir /build/laplace/work/chess-capacity-measurement \
  --minimum-seconds 30 --replays 1 --target-games-per-second 2500 \
  --timeout-seconds 3600
~~~

Both output directories must be new. Normal complete games have no move cap, resignation adjudication, or draw adjudication. Search depth is a generation setting; it does not shorten the recorded trajectory. The retained native parser and complete-game verifier, not a Python PGN parser, establish legal trajectory and normal terminal outcome.

Preparation can instead select a pool of actual completed jobs with repeated --job-id ID arguments. It downloads their original games.pgn and experiment.json bytes without alteration. The source jobs must have been created with automatic ingestion disabled. This requirement alone does not prove novelty: measurement still rejects any admission that reports existing or repaired PLAYING occurrences.

The API currently retains up to 32 terminal jobs in process memory. Keep preparation and admission in the same API process and avoid pruning the selected jobs with unrelated new jobs. Saved evidence survives independently on disk, but it does not register an old job in a restarted service. This collector does not invent an import route for copied artifacts.

The timed window begins immediately before the first admission request and ends after the final receipt download, validation, cross-job identity/content checks, and post-pool scope retention. It includes normal PGN parsing and normalization, novelty checks, calculated lanes, shared writer work, synchronous local WAL acknowledgement, exact native game and witness readback, service overhead, transport, and collector verification. Initial corpus verification, engine generation, subsequent replay controls, and final summary serialization are outside that interval.

Admissions are sequential through the existing API. The result describes this API workload and selected corpus; it is not a claim about the machine's maximum parallel ingestion rate.

| Report quantity | Meaning |
|---|---|
| newlyRecordedPlayings / newlyRecordedCompleteGames | Distinct authentic occurrences that were all novel, applied, committed, and read back exactly. |
| contentInventory.distinctOrderedLines | Distinct native start-position and ordered move-ID bodies among those occurrences. Shared content remains shared. |
| writer.entitiesInserted | Newly inserted entity rows reported by the normal shared writer. This is not the number of games or lines. |
| writer.physicalitiesInserted | Newly inserted physicality rows reported by that writer. It is a separate storage count, not a second disjoint universe of objects. |
| writer.attestationsInserted | Newly inserted attestation rows reported by that writer. |
| observedNewPlayingsPerSecond | Accepted fresh PLAYING count divided by the one complete admission window. |
| sumServiceSeconds | Diagnostic sum of the service operation durations; it is not substituted for the enclosing wall interval. |

Do not add entity and physicality counts and call the result unique objects. A game can reuse existing players, positions, moves, lines and other content while supplying a distinct authentic playing context. The collector reports those differences explicitly.

Every source job is then admitted again, unchanged. All replay writer counters must be zero; canonical game bodies must match; and the selected before/after identities and observation counts must be unchanged. Later fresh jobs may legitimately increase an observation on a shared row. The collector therefore retains the latest actually observed value for each scoped identity across fresh admissions and rebases each earlier job's replay comparison to that post-pool subset. It requires nondecreasing counts and coverage while building that aggregate. This is an aggregate of ordered observations, not an atomic database-wide snapshot. Growth during exact replay remains a failure.

The output contains source/job provenance, each service receipt, partial failure evidence, post-pool-scope.json, and receipt.json. A failed later batch preserves the previously accepted counts but does not publish a successful capacity result. A failed replay leaves the measurement unqualified. A timeout may prevent retrieval of the newest server receipt; it never converts unknown completion into accepted games.

A qualified result requires every requested occurrence to pass, every replay control to pass, at least two distinct ordered lines, and at least 30 seconds in the actual admission window. A shorter or single-line sample can pass integrity checks but has targetVerdict "unqualified" and cannot substantiate sustained capacity. Two lines are only a minimum variety check; the complete content inventory determines how narrow the corpus really was.

Increase the pool or real games per job when the prepared corpus is exhausted too quickly. Do not loop the same occurrences and count them as new, pad the interval with sleep, or generate more matches inside the admission interval. For a 2500-game/second claim over 30 seconds, the prepared pool must contain at least 75,000 distinct authentic occurrences. A smaller pool cannot establish that target even if its brief admission is faster.

The source controls run with:

~~~bash
python3 scripts/test-retained-chess-capacity.py
~~~

They exercise real local HTTP transport and failure paths with receipt fixtures, plus counting, shared-scope rebasing and qualification counterexamples. They do not constitute PostgreSQL execution, engine generation, or a performance measurement. The policy registry runs them as policy-retained-chess-capacity.
