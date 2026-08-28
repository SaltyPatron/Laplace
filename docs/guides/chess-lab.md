# Chess lab guide — driving the engine, watching games, querying the web

Operational how-to for the conventional chess stack (laplace-uci, cutechess,
Stockfish, lichess) and the substrate read surface over the ingested chess
graph. The full modality reference — identity law, the three lanes, the
census, and the closed loop — is [chess.md](chess.md). Verify
commands against `api('chess')` and `/chess/lab/catalog` if this drifts.

## How to measure Laplace (read this before cutechess)

**Primary protocol — does the SoR help?** In-process guided vs pure at matched
depth, on positions the corpus actually covers:

```sh
laplace chess substrate-test --mode fold --openings --learned --games 200 --depth 4
```

- `fold` = substructure OUTCOME generalization (default UCI `Substrate`); `edge`
  = raw MOVE-edge μ (poisoned at startpos — Na3 can outrank e4; see #447 / #834).
- `--openings` seeds from ECO TSV under the chess games dir (this host:
  `/vault/Data/Games/Chess/openings/`).
- `--learned` blends corpus PST (UCI always does this; CLI does not unless flagged).
- Tune STEER straw: `--cp-per-point` / `--cap` (UCI hardcodes 8 / 150 today).

**Preflight the eyes:** `POST /chess/explore` with the FEN (and optional
`player`) before trusting any Elo number — you are reading SCAN/WEIGHT, not
guessing. Syzygy / shape / motifs / think-class are queryable (`api('chess')`)
but **not yet wired into UCI STEER** (#833).

**cutechess vs Stockfish** (`st=1`, `UCI_Elo=2000`) is a **watchable external
demo**, not the scientific floor. It does not pass an openings book, does not
expose cp/cap, and is easy to misread as “Laplace is weak” when the recipe never
took advantage of fold+openings+explore. Tracked: #834. Framing:
`.scratchpad/44` §8.

## The UCI engine (`laplace-uci`)

Linux deployment publishes the **complete .NET runtime closure** into
`/opt/laplace/app/releases/runtime.*/uci/`, with a stable
`/opt/laplace/app/laplace-uci` launch symlink. Copying just the apphost fails
with `laplace-uci.dll` missing. CI and publish execute the copied runtime's
`uci`, `isready`, and depth-1 legal search before activation; a deliberately
apphost-only copy must fail that check. This proves packaging/search, not
substrate learning or playing strength.

`app/Laplace.Chess.Uci` builds a standalone UCI engine. Truncated Chess Forward
Pass: classical alpha-beta (`PROPOSE`) with root consensus STEER (`Substrate`
fold/edge/off) and learned PST overlay. Any UCI GUI (cutechess, Arena,
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
  -engine name=Stockfish cmd=/opt/laplace/bin/stockfish proto=uci \
      option.UCI_LimitStrength=true option.UCI_Elo=2000 \
  -each st=1 timemargin=2000 \
  -rounds 10 -pgnout games.pgn -debug all
```

`st=1` = one second per move (watchable, ~2–3 min/game). Depth-limited play
(`-each tc=inf depth=8`) has **no clock at all** — a deep search can sit on a
single move for up to its 120 s internal ceiling; use it only for strength
tests you don't intend to watch.

`-rounds 10` is **ten games**, not ten pairs: cutechess-cli(6) says the option
"should be used to set the total number of games to play" for a two-engine
match, and the colours alternate between rounds on their own.

`-debug all`, never a bare `-debug`. The parser turns an argument-less option
into a boolean, and upstream `a70c5915` made the `-debug` branch reject exactly
that, so a bare flag kills the process before the first game with
`Warning: Empty value for option "-debug"` (exit 1). `all` is the only accepted
value; it also sends `debug on` to both engines, which is more transcript, which
is the reason to pass `-debug` at all.

2000 is the default Elo cap, not a fixed level. The live engine's UCI handshake
is authoritative for its range: the former Ubuntu Stockfish 14.1 advertises
1350–2850; the verified Stockfish 18 release advertises 1320–3190. These are
engine strength settings, not a guaranteed human rating at arbitrary clocks.
Disable **Limit Stockfish strength** for full strength: the runner sends
`UCI_LimitStrength=false` and omits `UCI_Elo`. Existing clients retain the
limited/default-2000 behavior unless they explicitly set `limitStrength=false`.
The transcript surfaces unsupported Elo warnings; do not infer the requested
level was accepted from a match merely starting.

### Persistent Linux engine installation

`setup-host.sh` (through its runner bootstrap) and the existing CI publish phase
both use `bootstrap-chess-lab.sh`. Stockfish comes from the versioned, SHA-256
locked official release in `deploy/linux/stockfish-release.json`, **not Ubuntu's
older package**. The current lock is [Stockfish 18](https://github.com/official-stockfish/Stockfish/releases/tag/sf_18).
Linux x86-64 AVX2 and baseline artifacts are supported; other architectures fail
explicitly pending a verified release artifact. cutechess remains built from the
external source pin; the verified host version 1.5.1 matches
[upstream v1.5.1](https://github.com/cutechess/cutechess/releases/tag/v1.5.1).

The Stockfish installer verifies the archive before extraction and a real UCI
handshake before switching `/opt/laplace/bin/stockfish`. Immutable releases,
including upstream source/license material, remain under `/opt/laplace/stockfish`.
Cached CI publishes recheck the installed hash and version. Distro binaries and
unmanaged replacements are not overwritten. Upgrade the lock through review/CI;
no floating `latest` download or manual binary-copy step is required. Existing
provisioned hosts need no new privileged policy installation for this repair.
API, CLI and ingest discovery prefer the managed installation before build/PATH
fallbacks; an explicit `LAPLACE_STOCKFISH` override still takes precedence.

CI snapshots the prior Stockfish launch pointer and only its API environment key.
Rollback restores those alongside the previous API/UCI payload, retains releases,
and preserves unrelated environment changes. Runtime processes are never restarted
by the dependency installer itself. A tournament is completed only after a zero
exit and all expected games scored; a `0 - 0 - 0` score is a failure, not success.

## Watching games live

Web → **Lab**, which is three surfaces, not one page:

| Route | Surface | For |
| --- | --- | --- |
| `/lab` | Experiments | In-process substrate runs: lift test, overlay ladder, learned PST, tactics, review |
| `/lab/gauntlet` | Engine Gauntlet | laplace-uci vs Stockfish through cutechess-cli |
| `/lab/lichess` | Lichess | Bot connectivity, player-game fetch |

Each shows only its own jobs. Jobs that play games stream every ply over SSE as
board events, rendered on a live board:

- **Gauntlet** — Laplace vs Stockfish. Config: `rounds` (games), `st` (sec/move,
  default 1), `elo` (Stockfish cap, default 2000), `limitStrength` (default true),
  `concurrency`, `ingest`.
  Setting `depth` > 0 switches to the unclocked depth mode. The form previews
  the exact argv before you start it, from the same `BuildArguments` the job
  uses, resolved against this host's binaries:
  `GET /chess/lab/cutechess/preview?rounds=&depth=&st=&elo=&limitStrength=&concurrency=`.
- `substrate-test` — consensus-guided vs pure search, in-process, parallel;
  the live board follows the most recent game, selector pins one.
- `ladder` — eval-term ablation ladder, same live view.

### The transcript

A gauntlet is an external process, so the lab keeps its raw I/O rather than a
summary of it — the launched command, every UCI line both engines exchanged
(tagged by engine and direction), and anything either wrote to stderr, in the
order it happened.

- `GET /chess/lab/jobs/{id}/terminal[?after=N]` — SSE. Replays the scrollback
  ring before following live, serves any number of viewers at once, and resumes
  from `after` when a connection drops. Line `seq` is monotonic: a gap is
  exactly what that viewer missed, and the pane draws it as an elision instead
  of pretending the transcript is continuous.
- `GET /chess/lab/jobs/{id}/terminal.txt` — the complete transcript. Served from
  the job's `transcript.log` artifact when it exists (the ring is bounded; the
  file is not).

This is a separate channel from `/events` on purpose. That one is a single
consumed queue sized for semantic events — a second viewer steals frames from
the first, a late viewer sees nothing that already happened, and a burst of UCI
chatter evicts the progress and result frames sharing it.

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
  `cli ingest chess <file>` (records witnessed headers and the typed move trajectory, then the
  analyzer derives positions, MOVE/OUTCOME edges, motifs, openings, clocks).
- `cli ingest chess-eval [--depth N | --nodes N]` — stockfish eval pass over
  recorded games (default depth 10, the v1 census budget): HAS_EVAL per position + eval-delta
  MOVE_QUALITY (blunder/mistake/inaccuracy) under the ChessStockfish source.
  Marker-gated per game/version; `LAPLACE_INGEST_MAX_UNITS=N` bounds a smoke.
- Books: `cli ingest chess-books <dir>` (plaintext only today).
- Openings: `cli ingest chess-openings <eco.tsv dir>`.
- Lichess bot: web → Chess panel → lichess start (token in
  `/opt/laplace/secrets/lichess.env`); every ply folds live.
