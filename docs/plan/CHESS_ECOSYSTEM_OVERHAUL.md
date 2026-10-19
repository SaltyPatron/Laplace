# Chess ecosystem overhaul

Program of record for [Laplace#1912](https://github.com/SaltyPatron/Laplace/issues/1912). Chess remains the modality in [`../specs/11_Chess_Provenance_Consensus_Spec.txt`](../specs/11_Chess_Provenance_Consensus_Spec.txt): a game is an ordered trajectory, a move is a witnessed transition, engine analysis is calculated testimony, and play is a forward pass. This note orders the work. It does not replace that spec.

Recorded game results, calculated evaluations, and tablebase facts stay source-separable. Classical search may propose moves. It does not become the decision. Witnessed continuations, openings, book explanations, and tablebase probes steer that proposal through typed evidence.

Delivery constraint: hart-prod is promoted by CI/CD. Source and installation declarations change here. Live `/etc` and `/var/lib` files change when that declaration is installed, not by a hand edit on the host.

## Order

| Sprint | Outcome | Blocks |
| --- | --- | --- |
| 1 | The shared Lichess controller can activate any declared executor, and a database recreate does not remove UCI or Lichess read access | Live play on every later sprint |
| 2 | Exact witnessed transitions and position evaluations steer search beyond the root, with a prior that leaves a finite sample intact | Commentary that cites the same evidence the engine used |
| 3 | Commentary is synthesized from the transition, the opening, and a salience-ranked precedent | |
| 4 | Books, the Lichess openings catalog, Stockfish, clocks, and Syzygy enter as the right kind of testimony | |
| 5 | Fetchers keep corpus identity, and one grammar-bounded pass admits a game | |
| 6 | Retrograde checkmate/Syzygy unrolling, minimal causal mating kernels, >5-man sub-board Syzygy projection, flat-horizon search amputation, and multi-dimensional player/clock/opening/rule analytics | |

## Sprint 1 — Relay

hart-prod owns the single Lichess bot stream (`Laplace.Endpoints.Lichess`, loopback). `laplace-uci-connect` relays each game to the selected executor:

| Profile | Executor |
| --- | --- |
| `mono-development` | hart-desktop, `wss://uci-dev.hartonomous.com/uci/mono-development` |
| `mono-staging` | hart-server, `wss://staging.hartonomous.com/uci/mono-staging` |
| `mono-prod` | hart-prod local connector, `ws://127.0.0.1:5192/uci/mono-prod` |

`mono-development` in the controller selection is an executor choice. It is the desktop engine. [`../guides/lichess-routing.md`](../guides/lichess-routing.md) says the installed routes read and record hart-server `laplace-mono`. The desktop process must report that selected read source. A local desktop database is a different read source and has to be declared before the controller will accept it. Read and recording identities may differ once both are declared.

Acceptance:

- `UciLichessEngine` names the cluster and database the process reported and the cluster and database the selected read source requires. A missing report is named as no identity.
- The Linux UCI service user does not read `/opt/laplace/app/laplace-api.env`. Credentials stay in the service-owned engine environment file. `LD_LIBRARY_PATH`, `LAPLACE_INSTALL_PREFIX`, and `LAPLACE_OPS_LOG_DIR` are connector variables. The service log directory is created for that user. `LAPLACE_PERFCACHE_BIN` stays unset so the engine resolves the blob under the install prefix.
- `20261015000000_substrate_runtime_grants.sql` grants the existing writer contract to `laplace_runtime` and to `laplace_mono_lichess_*`, because the Lichess host records live games through `SubstrateAccess.Ingest`. `laplace_mono_uci_*` gets the read contract (`USAGE`, `SELECT`, sequence use, function execute, no write and no `MAINTAIN`). Migrate re-executes that script, so a database that already applied the earlier reader grant for `laplace_mono_lichess_*` receives the writer contract on the next migrate.
- A failed activation or an exited worker retries on a 5s, 10s, 30s, 60s schedule while the selected profile stays in force. A new selection revision retries immediately. Off does not retry.
- `ChessRuntimeConfiguration` treats an unreadable installed environment file as absent. `ChessEngineConnections` turns an unreadable connector file into `ArgumentException`. `/chess/lab/catalog` returns an empty connection list and `connectionError` instead of HTTP 500. There is no `/v1/admin/overview` route; the admin overview section does not read this file.

The committed UCI declaration `Laplace-UCI/installations/hart-server.json` contains `mono-staging` only. A host `connector.json` that adds `mono-prod` is a rendered installation. Laplace main delivery restarts `laplace-mono-lichess` and, when `/etc/laplace-mono/uci/delivery.json` is present, asks the root path unit to run `setup.py --refresh`. That refresh rewrites `connector.json` from the installed declaration and drops `laplace-api.env` from every profile. Chess host setup arms the path unit when the declaration has `uci_setup`. Laplace-UCI CI publishes the connector and does not itself render the host file.

## Sprint 2 — Move selection

Turn scoring and `LearnedPst` still shrink with `ChessShrink.DefaultK0` of 15,000. Exact root transitions use prior strength 5. `Search` still consults that bias at ply 0 only. Leaf evaluation adds tapered passed pawns, mobility, king safety, outposts, and hanging-piece pressure, and blends one witnessed centipawn at quiescence.

Acceptance:

- Exact board transitions use a small prior or the witness's Fisher information `1/σ²`. A line with 10 witnesses and prior strength 5 keeps two thirds of its empirical edge. Player strength and a witnessed Stockfish evaluation increase precision. A context-free move prior is only a move-ordering tiebreak of at most 3 centipawns when the position has no exact witness.
- Interior nodes can order and blend from a position table seeded by witnessed evaluations, openings, and the active game. The table is a cache of substrate state, not a second source of identity.
- Leaf evaluation adds pawn structure, bishop pair, open-file rooks, mobility, king safety, and hanging-piece pressure, blended with witnessed `EVAL_CP` by precision.
- With the 3-4-5 Syzygy set loaded, root moves are filtered by DTZ and interior nodes return WDL when five or fewer pieces remain.

`SubstrateRootBias` keeps the exact-transition contract. Ten result witnesses at unit precision use prior strength 5. A tighter result deviation, and a witnessed `HAS_EVAL` count on the arrival position, increase precision. The evaluation's centipawn mean stays out of the recorded-result mean. A context-free move average is not mixed into an exact transition, and without one it is clamped to 3 centipawns.

`SearchContinuationTable` is the interior cache. It is keyed by `ChessPositionIdentity` and seeded from a witnessed evaluation, an opening line, and the active game before search. At ply greater than 0 the preferred move is ordered after a transposition hit. The witnessed evaluation is not added on the way down, so it cannot accumulate once per ply. A move-only table does not clear the transposition table. Seeding a witnessed evaluation changes the leaf score, so that stamp does clear it. The leaf blend runs once at quiescence stand-pat, weighted by `n/(n+5)` and clamped to ±512. The search does not query the database from that hot path. `ChessEngineService`, `ChessLiveGameHost`, and `ManagedSearchBackend` build a per-search overlay on the shared opening book and seed the active line. `HAS_EVAL` centipawns are read once per search when a substrate connection is open.

## Sprint 3 — Commentary and concepts

`PositionHistoryAsync` orders playings by the mover's Glicko-2 lower bound (`rating - 2·rd`), then source Elo, then an opening or motif match, and it reports that witness count. The continuation read reports the next-move distribution. A comment states the opening transition, the tactical or structural delta, and the principal variation. A master precedent needs Elo 2400 or a lower bound at the neutral mean, and a book precedent needs an `EXPLAINS` text long enough to be a judgment. Gambit, fork, pin, skewer, discovered attack, sacrifice, zugzwang, fianchetto, outpost, minority attack, and opposition are one chess concept each; domain membership admits that sense.

Acceptance:

- Precedents are ordered by Glicko-2 rating minus two deviations, player rating, and the opening or motif, not by GIN page order.
- Chess senses of gambit, fork, pin, skewer, discovered attack, sacrifice, zugzwang, fianchetto, outpost, minority attack, and opposition meet their opening trajectories, book explanations, and board deltas on the same concept. Domain membership is the disambiguator.
- A comment states the opening transition, the structural or tactical delta, the principal variation, and a master or book precedent when that precedent is actually salient.

## Sprint 4 — Books, openings, Stockfish, clocks, tablebases

Acceptance:

- Book lines that start from a diagram FEN or a move-number continuation replay from that anchor. Prose attaches `EXPLAINS` to the replayed trajectory and to the chess concepts it names. Lines that fail are not silently called diagram-anchored when an anchor was available.
- The Lichess openings catalog (`a.tsv` through `e.tsv`) is the opening classifier, by UCI prefix and by transposition. The hardcoded fallback list is not the catalog.
- Stockfish recipe identity does not include host thread or worker counts. `EVAL_CP` and the recommended move belong to the position. Move quality and the evaluation change belong to the move transition.
- Clock pace is attested on the move, game, and player trajectory. It is not a context-free word.
- Syzygy ingestion probes witnessed positions of five or fewer pieces and their legal neighbors. It does not enumerate the five-man space in C#.

`StockfishEvaluationRecipe` identity is the engine bytes, the depth or node budget, the hash, and MultiPV. Thread counts and the resolved worker budget stay on the process and out of `Recipe.Id`. `HAS_EVAL` on the position is the side-to-move centipawn evaluation. The engine's recommended move is a `HAS_FEATURE` of that position, content `chess/stockfish-recommended/<uci>`, and only when that move is legal there. The played move's evaluation change is `HAS_EVAL` on `ChessCompose.TransitionKey`, and `MOVE_QUALITY` uses that same transition as its subject.

Syzygy package enumeration stays at three men. `ChessSyzygy.DeriveGame` probes each witnessed position of at most five men, and each legal one-ply neighbor, against the loaded 3-4-5 tables. Each probe attests WDL, DTZ, and the optimal tablebase transition. A five-man table is not placed in C#.

A book line replays from a diagram FEN when that FEN precedes the moves and they are legal there. A move number other than white's first move replays from the position already reached in the same paragraph when the side and fullmove agree. The paragraph attests `EXPLAINS` on that trajectory and on each chess concept it names, with the book-domain member as the context. A line that had a FEN or a continuation number and still does not replay is refused as `anchored-line-unreplayable`, not as a diagram-anchored line.

Opening classification reads `a.tsv` through `e.tsv`. A game matches the longest UCI prefix and, when a catalog line ends on a position the game reached by a different move order, that deeper position. ECO, the opening name, the family, the variation, and each concept the name contains are substrate entities. There is no hardcoded opening list.

Clock pace is `HAS_THINK_CLASS` on the played transition, the line, and the mover, with the playing as context. The class word is not an outcome with a null context.

## Sprint 5 — Corpus identity

Acceptance:

- Chess.com archive downloads are serial, send a contact `User-Agent`, and keep monthly PGN and ETag files. A finished month is not downloaded again. The current month uses `If-None-Match`.
- A game keeps the corpus it came from (`lumbrasgigabase-otb`, `lumbrasgigabase-online`, `twic`, `lichess`, `chesscom`) and the PGN tags Site, Round, UTCDate, UTCTime, titles, and FIDE ids. Decomposition, opening classification, motifs, clocks, and outcomes are one grammar-bounded pass per game, staged and folded once per file.

Chess.com archives download one month at a time. The request names `LaplaceChessIngester/1.0 (https://hartonomous.com; contact: aharttn@gmail.com)`. Each month is `{yyyy}-{MM}.pgn` with a sibling `.etag` under the chess data root at `chesscom/<user>/`. A finished month already on disk is not requested again. The current month sends `If-None-Match`.

A game's witness source is `lumbrasgigabase-otb`, `lumbrasgigabase-online`, `twic`, `lichess`, or `chesscom` when the file path or the Site tag says so. Site, Round, UTCDate, UTCTime, the titles, and the FIDE ids are kept on that game. Decomposition, opening classification, motifs, clocks, and outcomes are the same grammar pass, and that pass is staged once for the file. `ChessPgnDecomposer.RecordingWitnessId`, `ChessPgnIngestor.BootstrapSourcesAsync`, `ChessWitnessHydrator`, `ChessMoveOutcomes`, and `ChessStartingSideInventory` resolve the corpus witness source so exact readback and hydration recognize every corpus-stamped playing.

## Sprint 6 — Retrograde causal kernels, sub-board Syzygy projection, flat-horizon amputation, and multi-dimensional analytics

Traditional engines search forward in $O(b^d)$ while discarding player, clock, rating, opening, and event provenance. Traditional tablebases (Syzygy) stop at 5–7 pieces because they brute-force enumerate $\binom{64}{k}$ boards in a vacuum. Because Laplace stores ordered `LineTier` trajectories (`Content` and `Projection`), `SegmentTier` sub-board constellations, `PositionTier` boards, and `SubstructureTier` `piece×square` atoms with 16-byte BLAKE3 IDs, $S^3$ Karcher mean coordinates, 128-bit Hilbert indices, and Glicko-2 `(rating, rd, witness_count)` standing, horizon depth and retrograde induction become flat $O(1)$ index operations:

1. **Backward Checkmate & >5-Man Syzygy Unrolling (`ChessRetrograde.UnrollLine`, `SeedSearchContinuations`, `ProbeOnePlySyzygyClosure`)**:
   - Terminal checkmates ($\text{DTZ} = 0$, $\text{WDL} = 0$ for the mated side) and 3-4-5 Syzygy verdicts propagate backwards along witnessed game trajectories into $>5$-piece predecessors, recording exact `Wdl`, `PliesToClose`, and `IsCheckmate`.
   - On 6-piece boards (`maxTbMen + 1`), `ProbeOnePlySyzygyClosure` tests legal captures that drop into a $\le 5$-piece tablebase win and propagates that exact Syzygy conversion backwards into earlier middlegame plies.
   - When multiple lines reach the same ancestor position, `SearchContinuationTable.SeedRetrogradeCheckpoint` retains the highest `Wdl` and the **fastest checkmate / shortest closure** (`min PliesToClose`).
2. **Minimal Causal Mating Kernels & Retrograde Constellation Cones (`ChessRetrograde.ExtractMatingKernel`, `UnrollCausalConstellations`, `BoardContainsConstellation`)**:
   - At any checkmate on a crowded board (e.g. 28 pieces), `ExtractMatingKernel` isolates the 3–7 `SubstructureTier` `piece×square` atoms that actually cause the mate: the mated King, the checking piece(s), any attacker defending a contact checker, friendly pieces blocking the King's flight squares, and enemy pieces controlling the King's flight squares.
   - `UnrollCausalConstellations` walks that minimal kernel backwards $k$ plies along the trajectory, composing a content-addressed `SegmentTier` Merkle entity (`Hash128.Merkle(SegmentTier, atomIds)`) and $S^3$ Karcher mean coordinate at each step before mate.
   - Any 20–32 piece board can be tested against a constellation via `BoardContainsConstellation` (or Hilbert/$S^3$ neighborhood lookup) without enumerating irrelevant bystander pieces.
3. **Sub-Board Syzygy Projection Above Five Men (`ChessRetrograde.ProjectSubBoardForSyzygy`)**:
   - On boards with $>5$ pieces, `ProjectSubBoardForSyzygy` retains both Kings plus the highest-urgency attackers/defenders around the target King and advanced passed pawns, projecting the crowded board onto a legal $\le 5$-man sub-board and probing Fathom/Syzygy to detect underlying tablebase wins before captures even occur.
4. **Flat-Horizon Search Amputation (`SearchContinuationTable.AmputationWitnesses`, `Search.Negamax`)**:
   - At interior search nodes (`ply > 0`), retrograde-solved checkpoints return `RetrogradeScore` immediately in $O(1)$ without expanding child moves.
   - Positions with high-precision witnessed evaluations (`witnesses >= AmputationWitnesses`, $\ge 75\%$ precision weight under $K_0 = 5$) amputate remaining interior depth (`depth >= 2`) directly to quiescence (`Quiesce`), flattening search depth cost.
5. **Multi-Dimensional "7/10 Games" Rule Discovery & Human/Temporal Correlation (`ChessRetrograde.DiscoverWinningConditions`)**:
   - Walks games backwards from checkmates and terminal outcomes to extract single and conjunctive structural/tactical conditions (`bishop-pair`, `rook-open-file`, `passed-pawn`, `material-advantage`, `fork`, `pin`, `skewer`, and pairs such as `bishop-pair + rook-open-file`) with empirical and $K_0 = 5$ shrunk win rates ($\ge 70\%$) and `FastestCheckmatePlies`.
   - Because `playingId` (`ContextId`), `lineId`, `playerId`, `WhiteElo`/`BlackElo`, `TimeControl`, `ECO`/`Opening`, `HAS_THINK_CLASS` (`rushed`, `normal`, `deep`, `planned_quick`, `pressed_think`, `flagging`), `MOVE_QUALITY` (`blunder`, `mistake`, `inaccuracy`), and `HAS_EVAL` all join in 16-byte ID space, queries can correlate board constellations and transitions with player rating bands, time controls, and clock-pressure blunders in a single indexed pass.

