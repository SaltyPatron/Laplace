# Chess lab guide — driving the engine, watching games, querying the web

Operational how-to for the conventional chess stack (laplace-uci, cutechess,
Stockfish, lichess) and the substrate read surface over the ingested chess
graph. The full modality reference — identity law, the three lanes, the
census, and the closed loop — is [chess.md](chess.md). Verify
commands against `api('chess')` and `/chess/lab/catalog` if this drifts.

## Measuring guided search, recorded throughput and external play

The in-process guided-versus-classical experiment measures the effect of the
selected substrate providers at matched search depth:

```sh
laplace chess substrate-test --mode transition --openings --games 200 --depth 4
```

- `transition` selects transition and position evidence; legacy `fold` and
  `edge` spellings resolve to that same provider configuration.
- `--openings` selects replayed ECO positions from the configured opening
  directory. Terminal positions are rejected before a game starts.
- The selected position evaluator includes constituent outcomes, the learned
  PST residual and available tactical evidence. Syzygy probes participate when
  the current position fits the installed tablebase coverage.
- This experiment's move ceiling can produce adjudicated games. Its game count
  alone does not establish complete-game recorded throughput.

Use the recorded-chess measurement for complete-game admission, acknowledged
commits and exact witness readback. Use CuteChess for external engine play,
retaining the actual engine configuration, colors, openings, transcript and PGN.
Strength conclusions require their own sufficient matched experiment; two
calibration games or a capped-Elo demonstration cannot establish playing strength.
`POST /chess/explore` exposes the selected FEN's available substrate evidence.

## The UCI engine (`laplace-uci`)

Linux deployment publishes the **complete .NET runtime closure** into
`/opt/laplace/app/releases/runtime.*/uci/`, with a stable
`/opt/laplace/app/laplace-uci` launch symlink. Copying just the apphost fails
with `laplace-uci.dll` missing. CI and publish execute the copied runtime's
`uci`, `isready`, and depth-1 legal search before activation; a deliberately
apphost-only copy must fail that check. This proves packaging/search, not
substrate learning or playing strength.

`app/Laplace.Chess.Uci` builds a standalone UCI engine. Its conventional search
uses the selected root and position evidence providers plus available Syzygy
results. Any UCI GUI (cutechess, Arena,
BanksiaGUI) or `cutechess-cli` can drive it — point the GUI at the binary, no
arguments needed.

- Resolution order when the lab launches it: deployed install → build output
  (`build/app/bin/Laplace.Chess.Uci/Release/net10.0/laplace-uci`) → `PATH`.
- Substrate mode: UCI option `Substrate` advertises `substrate` (default) and
  `off` (classical control). `fold` and `edge` remain accepted aliases for
  `substrate`. Environment override: `LAPLACE_UCI_SUBSTRATE`.
- The engine initializes PostgreSQL providers on `isready`, before the move
  clock. Initialization errors are emitted explicitly. A later `go` without
  the required provider state fails; it does not silently change the player.

Manual cutechess-cli invocation (every `key=value` is its own token, and
`proto=uci` is required — cutechess defaults to xboard):

```sh
stockfish_exe="$(python3 scripts/install-stockfish.py --print-path)"
cutechess-cli \
  -engine name=Laplace cmd=/path/to/laplace-uci proto=uci \
  -engine name=Stockfish cmd="$stockfish_exe" proto=uci \
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
1350–2850; the verified Stockfish 19 release advertises 1320–3190. These are
engine strength settings, not a guaranteed human rating at arbitrary clocks.
Disable **Limit Stockfish strength** for full strength: the runner sends
`UCI_LimitStrength=false` and omits `UCI_Elo`. Existing clients retain the
limited/default-2000 behavior unless they explicitly set `limitStrength=false`.
The transcript surfaces unsupported Elo warnings; do not infer the requested
level was accepted from a match merely starting.

### External source checkouts, builds and updates

Stockfish uses the official repository through Laplace's existing external source
mechanism. Linux uses `$LAPLACE_EXTERNAL/stockfish`, with the established
`/build/external` default; Windows uses `$LAPLACE_EXTERNAL/stockfish` when set,
otherwise the repository's `external/stockfish`. `LAPLACE_STOCKFISH_SOURCE` selects
an existing checkout elsewhere. No particular manually chosen folder is treated
as a canonical source location.

The current source pin in `deploy/linux/stockfish-release.json` is
[Stockfish 19](https://github.com/official-stockfish/Stockfish/releases/tag/sf_19),
released September 5, 2026, commit `edb0d9db6731067ec50ce619ff372b463bc4dd5d`.
`setup-host` and CI invoke the source updater and build:

```sh
python3 scripts/install-stockfish.py
python3 scripts/install-stockfish.py --check-latest
python3 scripts/install-stockfish.py --print-path
```

The updater fetches the selected official stable tag when absent, preserves local
edits and prior branch tips, and runs upstream `make profile-build ARCH=native`.
The build downloads and validates its declared NNUE network through upstream's
`net` target. Its compiler job count follows `--jobs`, then the established
`CMAKE_BUILD_PARALLEL_LEVEL` / `LAPLACE_BUILD_JOBS` envelope, then available CPUs.
The checkout's commit is recorded in the existing external `PINS.tsv` alongside
the other dependencies; an explicit source override does not rewrite that roster.

The runtime uses the built executable directly:
`$LAPLACE_EXTERNAL/stockfish/src/stockfish` on Linux, or `src/stockfish.exe` on
Windows. The updater builds an adjacent candidate through upstream's `EXE`
variable, verifies its exact version, required UCI options, readiness and a legal
depth-1 search, then replaces the direct executable. A failed compiler, NNUE
download or search keeps the previous working executable. Repeated runs verify
the source, compiler, native CPU selection and executable hash before reusing a
build. A changed source/toolchain/CPU or `--rebuild` rebuilds it.

`--check-latest` compares the official latest stable tag and its source commit
with the pin and identifies a stale pin explicitly. An upstream update is applied
by updating that source pin and rebuilding the same checkout. No prebuilt
Stockfish release archive, managed binary copy or launch-link installation is
part of this path.

Windows publish first runs `scripts/win/ensure-stockfish-toolchain.cmd`. It reuses
a complete installed GNU-compatible toolchain; otherwise it provisions the
missing GNU make/compiler/shell/download/hash tools through MSYS2 UCRT64. The
[official MSYS2 installer](https://www.msys2.org/docs/installer/) is SHA-256
verified before execution, and package updates follow its
[documented update sequence](https://www.msys2.org/docs/ci/). Windows then runs
the same Python source updater and writes the direct built executable into the
application environment.

API, CLI and ingest discovery give explicit `LAPLACE_STOCKFISH` precedence,
then use the source build before legacy managed/build/PATH locations. The API
configuration snapshot preserves prior environment state for publish rollback;
the source builder itself protects the previous executable when a rebuild fails.
A tournament completes only after zero exit and all expected games scored.

### Measuring the actual machine before choosing engine settings

`scripts/benchmark-chess-environment.py` measures the installed source builds on
the machine where it runs. Its receipt identifies that hostname, CPU model,
visible affinity, every visible cgroup CPU/memory ancestor, executable SHA-256,
source commit, compiler/build receipt, UCI options and source NNUE file. An
80-core CPU model string does not grant 80 CPUs to a container: the effective
capacity is the minimum of process affinity and CPU quota, followed by the
declared service reserve.

Inspect admission without launching any benchmark:

```sh
python3 scripts/benchmark-chess-environment.py \
  --output-dir /build/laplace/work/chess-calibration-plan \
  --plan-only --reserve-cpus 2 --memory-mib 2048
```

Run a measured sweep in a new evidence directory:

```sh
python3 scripts/benchmark-chess-environment.py \
  --output-dir /build/laplace/work/chess-calibration-run \
  --stockfish "$LAPLACE_EXTERNAL/stockfish/src/stockfish" \
  --cutechess /opt/laplace/bin/cutechess-cli \
  --reserve-cpus 2 --memory-mib 2048 --repeats 3 \
  --hash-mib 16,64,256 --match-depth 8 --max-moves 0 \
  --max-seconds 1800 --case-timeout 600
```

The default thread sweep includes powers of two, the observed physical-core count
when it fits the admitted CPU budget, and the observed CPU-budget endpoint.
`--threads`, `--cpu-budget`, `--concurrency` and `--hash-mib` select
explicit points; requested points cannot consume the declared CPU reserve or
exceed memory admission. Fractional CPU quota is retained in the report, and a
sub-one-CPU grant is reported as insufficient for a full search thread rather
than silently rounded up. Existing CPU/memory cgroup limits remain in force.

Each Stockfish configuration runs its real built-in `bench` suite repeatedly.
The first process sample is retained separately from later samples; every bench
resets its transposition table through upstream `ucinewgame`. The OS filesystem
cache is neither flushed nor claimed cold. All node counts, engine times and
NPS values remain visible because different Threads/Hash settings can search
different amounts of work at the same depth. `--bench-limit-type nodes` changes
the per-position limit; SMP can still overshoot that limit.

CuteChess runs complete Stockfish games at each admitted concurrency,
with the same game count and per-move depth, strength limiting disabled and
pondering off. Memory planning includes both resident engines per game;
active search planning uses one search team per game. Completed-game counts,
UCI best moves, PGN results and ply counts must reconcile. Normal calibration has
no move-count cutoff and requires normal game termination. A positive
`--max-moves` explicitly selects a short diagnostic; that mode emits no complete-game
capacity recommendation. Wall and per-process time budgets still apply, and an
unfinished game fails the measurement while retaining its available evidence.

To include a separate two-game Laplace-versus-Stockfish acceptance, provide
`--laplace-uci /path/to/laplace-uci`. It preserves Laplace's configured substrate
mode and records the advertised effective setting. `--laplace-substrate off`
explicitly requests a substrate-disabled packaging check. Both colors are
verified; matched depth is recorded without pretending the engines perform equal
work or inferring Elo from two games.

The output directory contains `report.json`, raw command/transcript logs and
PGNs. Recommendations distinguish bench completion latency, search-node
throughput and tournament throughput, report sample variation and overlapping
ranges, and apply only to measured configurations on that exact environment.
UCI defaults and compiler capabilities do not establish actual NUMA placement or
huge-page backing; those are not measured by this command.
Fewer than three steady samples produce smoke evidence without recommendations.
The command never changes runtime settings or replaces a corpus-evaluator
throughput measurement with a self-play estimate.

Wall time is bounded by `--max-seconds` and per-case `--case-timeout`; timeout
cleanup targets only processes launched by the benchmark. Where the process
namespace permits it, 50-ms sampling reports process-tree RSS/CPU/I/O and
cgroup throttling deltas. Sampling is explicitly a lower-bound/interval measure,
not a kernel-enforced private memory cgroup. Windows Job Object/processor-group
restrictions and mismatched `/proc` namespaces are reported as unavailable
capabilities; the runner never attributes unrelated host processes to its work.
Every failed or incomplete run retains a report with its exact reason.

The named manual benchmark suite is `chess`; it follows the evidence rules in
[MANUAL_BENCHMARK_EVIDENCE.md](../benchmarks/MANUAL_BENCHMARK_EVIDENCE.md) and does
not require an unrelated Laplace core/T0 build merely to calibrate external tools.

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
  recorded games (default depth 10): HAS_EVAL per position + eval-delta
  MOVE_QUALITY (blunder/mistake/inaccuracy) under the ChessStockfish source.
  Completion markers bind each distinct line to its exact version-2 evaluation
  recipe; `LAPLACE_INGEST_MAX_UNITS=N` bounds a smoke. The recipe records the engine
  executable SHA-256, reported identity, effective UCI options, NNUE/tablebase
  identities, depth or node budget, timeout and cold-search policy. Prior caches
  and markers remain intact; new cache files use a `.recipe-<sha256>` namespace
  and verify that identity in their headers. Full recipe metadata is retained
  alongside the calculated testimony rather than relabeling older evaluations.
- Books: `cli ingest chess-books <dir>` (plaintext only today).
- Openings: `cli ingest chess-openings <eco.tsv dir>`.
- Lichess bot: web → Chess panel → lichess start (token in
  `/opt/laplace/secrets/lichess.env`); every ply folds live.

The corpus evaluator accepts `LAPLACE_STOCKFISH_EVAL_THREADS` (default 1),
`LAPLACE_STOCKFISH_EVAL_HASH_MB` (16), `LAPLACE_STOCKFISH_EVAL_NUMA_POLICY`
(`auto`), `LAPLACE_STOCKFISH_EVAL_SYZYGY_PATH`, `LAPLACE_STOCKFISH_EVAL_FILE`
(an explicit compatible NNUE), and `LAPLACE_STOCKFISH_EVAL_TIMEOUT_SECONDS`
(30). Its process pool accounts for threads per engine against the process CPU
grant. `LAPLACE_STOCKFISH_EVAL_PROCESSES` explicitly selects pool capacity; the
recipe records that resource choice and any oversubscription. Measure the corpus
evaluator's own throughput before applying tournament concurrency to ingestion.

The gauntlet UI, preview and start configuration expose `stockfishThreads`,
`stockfishHashMb`, `stockfishNumaPolicy`, and `stockfishSyzygyPath`. Empty values
preserve the actual installed engine defaults. Use the host calibration report
to choose resources for the intended workload. `auto` and `system` NUMA policies
respect process affinity; `hardware` deliberately ignores it.

Each gauntlet retains an `experiment.json` containing the requested options,
actual command, observed UCI configuration, executable/runtime/opening identities,
timing and results. Automatic and manual PGN ingestion attach this receipt to
each game occurrence through separate `HasExperimentReceipt` metadata under the
`ChessGauntlet` source. That provenance survives ordinary local job-artifact
cleanup and remains separate from literal PGN testimony. The original PGN Event
continues to identify the experiment.
