# Chess lab guide — driving the engine, watching games, querying the web

Operational how-to for the conventional chess stack (laplace-uci, cutechess,
Stockfish, lichess) and the substrate read surface over the ingested chess
graph. The full modality reference — identity law, the three lanes, the
census, the closed loop — is [chess.md](chess.md). Written 2026-07-23; verify
commands against `api('chess')` and `/chess/lab/catalog` if this drifts.

## The UCI engine (`laplace-uci`)

`app/Laplace.Chess.Uci` builds a standalone UCI engine. It is an alpha-beta
search whose root move ordering is biased by live substrate consensus, with a
learned PST overlay folded from recorded games. Any UCI GUI (cutechess, Arena,
BanksiaGUI) or `cutechess-cli` can drive it — point the GUI at the binary, no
arguments needed.

- Resolution order when the lab launches it: deployed install → build output
  (`build/app/bin/Laplace.Chess.Uci/Release/net10.0/laplace-uci`) → `PATH`.
- Substrate mode: UCI option `Substrate` = `fold` (default; substructure
  OUTCOME folds), `edge` (raw MOVE-edge consensus), `off` (pure search). Env
  override: `LAPLACE_UCI_SUBSTRATE`.
- The engine connects to Postgres on `isready`, never on the move clock, and
  degrades to pure search with an `info string` if the DB is unreachable.

Manual cutechess-cli invocation (every `key=value` is its own token, and
`proto=uci` is required — cutechess defaults to xboard):

```sh
cutechess-cli \
  -engine name=Laplace cmd=/path/to/laplace-uci proto=uci \
  -engine name=Stockfish cmd=/usr/games/stockfish proto=uci \
      option.UCI_LimitStrength=true option.UCI_Elo=2000 \
  -each st=1 timemargin=2000 \
  -rounds 10 -pgnout games.pgn -debug
```

`st=1` = one second per move (watchable, ~2–3 min/game). Depth-limited play
(`-each tc=inf depth=8`) has **no clock at all** — a deep search can sit on a
single move for up to its 120 s internal ceiling; use it only for strength
tests you don't intend to watch.

## Watching games live

Web → **Chess Lab** tab. Jobs that play games stream every ply over SSE as
board events, rendered on a live board in the job view:

- `cutechess` — Laplace vs Stockfish. Config: `rounds`, `st` (sec/move,
  default 1), `elo` (Stockfish cap, default 2000). Setting `depth` > 0
  switches to the unclocked depth mode.
- `substrate-test` — consensus-guided vs pure search, in-process, parallel;
  the live board follows the most recent game, selector pins one.
- `ladder` — eval-term ablation ladder, same live view.

Finished cutechess jobs auto-ingest their `games.pgn` into the substrate
(novelty-gated; `ingest=false` to opt out), so every match played feeds the
next match's bias — the loop the lab exists for.

## Querying the chess web

SQL (`psql` → `SET search_path = laplace, public;` — discover with
`SELECT * FROM api('chess')`):

- `chess_moves(position_id, limit)` — ranked continuations out of a position
  from MOVE consensus (eff_mu-ordered opening explorer).
- `chess_player_moves(position_id, player_id, as_white, limit)` — one
  player's actual continuations with per-game provenance (games, score).
- `consensus_by_ids(ids[], type_id)` — typed batch lookup; prunes to one
  relation partition (the untyped form Append-scans all ~290).

Position ids are composed (merkle) ids, not `canonical_id(surface)` — get
them from the HTTP surface, which composes ids from FEN:

```sh
curl -s localhost:5188/chess/explore -H 'content-type: application/json' \
  -d '{"fen":"rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
       "player":"magnuscarlsen","limit":10}'
```

Returns each legal continuation with SAN, consensus deviation (`effMu`),
witness count, and — when `player` is set — that player's game count and
score with the move, resolved via MOVE-evidence game context →
HAS_WHITE/HAS_BLACK. The same panel lives in the web play view ("Explore").

## Feeding the web

- `cli chess fetch <user> [--site chesscom|lichess]` → monthly-archive PGN →
  `cli ingest chess <file>` (records witnessed headers/movetext, then the
  analyzer derives positions, MOVE/OUTCOME edges, motifs, openings, clocks).
- `cli ingest chess-eval [--analyze-depth N]` — stockfish eval pass over
  recorded games (default depth 12): HAS_EVAL per position + eval-delta
  MOVE_QUALITY (blunder/mistake/inaccuracy) under the ChessStockfish source.
  Marker-gated per game/version; `LAPLACE_INGEST_MAX_UNITS=N` bounds a smoke.
- Books: `cli ingest chess-books <dir>` (plaintext only today).
- Openings: `cli ingest chess-openings <eco.tsv dir>`.
- Lichess bot: web → Chess panel → lichess start (token in
  `/opt/laplace/secrets/lichess.env`); every ply folds live.
