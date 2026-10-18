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
- `20261015000000_substrate_runtime_grants.sql` grants the existing writer contract to `laplace_runtime` and a read contract (`USAGE`, `SELECT`, sequence use, function execute, no write and no `MAINTAIN`) to unprivileged roles named `laplace_mono_uci_*` and `laplace_mono_lichess_*`.
- A failed activation or an exited worker retries on a 5s, 10s, 30s, 60s schedule while the selected profile stays in force. A new selection revision retries immediately. Off does not retry.
- `ChessRuntimeConfiguration` treats an unreadable installed environment file as absent. `ChessEngineConnections` turns an unreadable connector file into `ArgumentException`. `/chess/lab/catalog` returns an empty connection list and `connectionError` instead of HTTP 500. There is no `/v1/admin/overview` route; the admin overview section does not read this file.

The committed UCI declaration `Laplace-UCI/installations/hart-server.json` contains `mono-staging` only. A host `connector.json` that adds `mono-prod` is a rendered installation. Reinstall it from the declaration after the declaration no longer points at `laplace-api.env`.

## Sprint 2 — Move selection

`ChessShrink.DefaultK0` is 15,000. `SubstrateRootBias` applies that shrinkage, and `Search` consults the root bias at ply 0. An exact `(position, MOVE, next position)` transition with a few dozen witnesses is reduced to zero centipawns after integer rounding. Context-free move averages are mixed with those exact transitions. Leaf evaluation averages piece-square atoms and does not see pawn structure, king safety, or a witnessed position evaluation.

Acceptance:

- Exact board transitions use a small prior or the witness's Fisher information `1/σ²`. A line with 10 witnesses and prior strength 5 keeps two thirds of its empirical edge. Player strength and a witnessed Stockfish evaluation increase precision. A context-free move prior is only a move-ordering tiebreak of at most 3 centipawns when the position has no exact witness.
- Interior nodes can order and blend from a position table seeded by witnessed evaluations, openings, and the active game. The table is a cache of substrate state, not a second source of identity.
- Leaf evaluation adds pawn structure, bishop pair, open-file rooks, mobility, king safety, and hanging-piece pressure, blended with witnessed `EVAL_CP` by precision.
- With the 3-4-5 Syzygy set loaded, root moves are filtered by DTZ and interior nodes return WDL when five or fewer pieces remain.

## Sprint 3 — Commentary and concepts

`PositionHistoryAsync` takes an unordered GIN sample and the commentary formats a fixed sentence from the first dated hit. Book text is truncated from one `EXPLAINS` row. Motif names are a fixed C# switch.

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

## Sprint 5 — Corpus identity

Acceptance:

- Chess.com archive downloads are serial, send a contact `User-Agent`, and keep monthly PGN and ETag files. A finished month is not downloaded again. The current month uses `If-None-Match`.
- A game keeps the corpus it came from (`lumbrasgigabase-otb`, `lumbrasgigabase-online`, `twic`, `lichess`, `chesscom`) and the PGN tags Site, Round, UTCDate, UTCTime, titles, and FIDE ids. Decomposition, opening classification, motifs, clocks, and outcomes are one grammar-bounded pass per game, staged and folded once per file.
