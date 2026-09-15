# Chess dependencies and installed readiness

The chess runtime requires the published Laplace UCI application, Stockfish and
`cutechess-cli` with its runtime libraries. The Lichess service uses Laplace's own
C# Bot API client. It does not require the separate Python `lichess-bot` project,
`python-chess`, a browser extension, or a ChatGPT plugin.

## Verified upstream versions (2026-09-15)

| Component | Selected version | Installation contract |
| --- | --- | --- |
| Stockfish | [19, released 2026-09-05](https://stockfishchess.org/blog/2026/stockfish-19/) | Build the official Git repository with its Makefile and matching NNUE; exercise UCI handshake, readiness and depth-one legal search on the resulting executable. |
| Cute Chess / cutechess-cli | [1.5.1, released 2026-06-14](https://github.com/cutechess/cutechess/releases/tag/v1.5.1) | Update the official Git source checkout and build through CMake with the required Qt modules. |
| Fathom | [`c9c6fef0dddc05d2e242c183acf5833149ab676d`](https://github.com/jdart1/Fathom/commit/c9c6fef0dddc05d2e242c183acf5833149ab676d) | Current upstream source commit, already selected for the native Syzygy library. No separate Python prober is needed. |
| Lichess openings | [`4b8622759e7ae6f93f011cc6c83a3823401ab45e`](https://github.com/lichess-org/chess-openings/commit/4b8622759e7ae6f93f011cc6c83a3823401ab45e) | Current upstream commit; `a.tsv` through `e.tsv` in the configured chess data directory. |
| Lichess | [Hosted Bot API](https://lichess.org/api#tag/Bot) | A valid token with `bot:play`, a BOT account, and working HTTPS/event streaming. There is no local Lichess server release to install for this client. |

Stockfish uses its upstream architecture selection and NNUE build targets. Version
19 no longer uses the secondary neural network introduced in Stockfish 16.1. Do
not attach an old `EvalFileSmall` requirement or carry an old external `EvalFile`
override into the new engine without checking compatibility.

Cute Chess requires Qt >=6.8, including Core5Compat and Svg, CMake >=3.20 and a
C++17 compiler. Executing `cutechess-cli --version` verifies that the dynamic
loader and Qt libraries can actually load. Windows builds also deploy their Qt
runtime libraries through Qt's own deployment tool.

## Installation and checks

The normal Linux host setup and product publish invoke
`scripts/bootstrap-chess-lab.sh`. Windows publishing invokes
`scripts/win/build-cutechess.cmd` and builds Stockfish from its Git checkout.
The source trees use `LAPLACE_EXTERNAL`: `/build/external` by default on the
Linux host and the repository's `external` directory on Windows. Source updates
verify the official upstream revision and preserve local changes. Stockfish's
executable is used directly from its build output under `stockfish/src`.

`LAPLACE_STOCKFISH_SOURCE` may explicitly select another existing source checkout.
A manually staged folder is not assumed to be Laplace's configured external
root. The existing `LAPLACE_STOCKFISH` executable override still takes precedence.

Run the installed dependency report from the repository:

```sh
python3 scripts/check-chess-dependencies.py --prefix /opt/laplace --check-latest
```

On Windows, provide the published UCI executable and use the same environment as
the application's build/publish configuration:

```bat
python scripts\check-chess-dependencies.py --uci "<published-app>\laplace-uci.exe" --check-latest
```

The report exercises the configured Stockfish binary and Laplace UCI binary,
checks Cute Chess's actual version/runtime, inventories opening files and paired
Syzygy material files, and returns failure if a required executable is missing or
does not work. `--check-latest` compares both release locks with the official
stable release APIs. A new upstream release produces a visible failure until the
new source identities are verified and the locks updated; it never silently
substitutes a development build during an experiment.

`--require-data` additionally fails when opening files or paired tablebase files
are missing. File presence and pairing do not certify complete tablebase coverage
or checksums. A successful executable report alone does not claim complete data
ingestion or online Lichess connectivity. The normal publish also runs an actual
short Cute Chess match against the published Laplace UCI application.

## Data and online requirements

Opening acquisition stores Lichess's files under
`$LAPLACE_DATA_ROOT/Games/Chess/lichess-openings`. Runtime/build discovery also
accepts the older `openings` layout. An explicit `LAPLACE_CHESS_OPENINGS` setting
takes precedence.

Syzygy exact endgame probing requires both WDL (`.rtbw`) and DTZ (`.rtbz`) files.
The complete standard three-to-five-piece sets contain 145 files of each kind,
about 984 MB together. Six-piece tables also need the smaller-piece sets for
positions reached after captures. Discovery retains the common tablebase root
and passes the required subdirectories to Fathom. A larger-piece directory must
not hide the smaller sets. Six- and seven-piece downloads are substantial data
choices; ordinary UCI play does not require downloading every tablebase.

Use the [official Lichess tablebase mirror](https://tablebase.lichess.ovh/tables/standard/)
and its checksum lists when acquiring tables. The existing dataset acquisition
workflow owns resumable downloads and source admission. Downloading PGN archives,
opening files or tablebases does not by itself prove that their content has been
ingested into the substrate. Compressed Lichess PGN datasets require the existing
zstd codec path; Stockfish and Cute Chess matches do not require the entire
Lichess game database.

`GET /chess/lichess/status` reports account readiness separately from process
health. Startup verifies the token, its `bot:play` scope and the account's BOT
title before establishing the event stream. It does not upgrade an account
automatically. Token contents are never part of the dependency report or CLI
startup output. Use the managed service's existing server-side token configuration.
