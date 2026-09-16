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
`scripts/bootstrap-chess-lab.sh --cutechess-gui` and build both official CMake
targets, `cli` and `gui`. Windows publishing invokes
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
python3 scripts/check-chess-dependencies.py --prefix /opt/laplace --check-latest --cutechess-gui
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

## Installed Cute Chess GUI and Qt capability

Linux setup and product publication build the official GUI executable from the
same verified Cute Chess source as the tournament CLI. They install the direct
executable at `/opt/laplace/bin/cutechess` by default and persist
`LAPLACE_CUTECHESS_GUI` plus `LAPLACE_CUTECHESS_GUI_RECEIPT` in the service
configuration. The retained `laplace-cutechess-gui-build.json` binds the source
commit and committed bytes, executable SHA-256, CMake cache, selected Qt SDK
configuration files and available platform/SVG plugin hashes.

The probe executes that GUI with `-platform offscreen --version`. Upstream
constructs its QApplication before handling this option. A passing receipt
requires the selected Cute Chess and Qt versions, successful process exit, and
Qt's loader diagnostic identifying the exact selected offscreen plugin. Linux
probes use temporary XDG settings directories under the build volume, preserving
the operator's application settings. The doctor rechecks the current source,
installed executable and Qt inputs against the retained build before probing.

This establishes headless QApplication initialization. The receipt explicitly
leaves interactive desktop readiness unverified: it does not open a window,
run the event loop, or prove X11/Wayland connectivity. Platform and SVG plugin
files are inventoried separately; their presence does not imply they were loaded.
The seven required SDK modules are Core, Gui, Widgets, Concurrent, Svg,
PrintSupport and Core5Compat. SDK module configuration hashes do not claim
complete runtime shared-library identity.

Launch the installed GUI from a working desktop session with
`/opt/laplace/bin/cutechess`. The doctor receipt supplies the actual configured
direct executable and selected Qt runtime/plugin environment when paths differ.
On Linux this prepends the selected SDK library directory ahead of inherited
library search paths. Qt is
the GUI runtime; it is not another server to boot.

Bare chess bootstrap calls may opt in with `--cutechess-gui` or
`LAPLACE_CUTECHESS_GUI_BUILD=1`. Once a GUI executable or build receipt exists,
later bootstrap calls rebuild it from the selected source alongside the CLI.
Product publish always requests the GUI, and its unchanged-input path rechecks
the installed GUI against the retained source build. The read-only chess
benchmark readiness step also requires this proof. These dependency checks do not
start corpus admission, game generation, or throughput benchmarks; those remain
explicit operator operations.

The separate virtual-X11 acceptance helper tests the installed GUI's event loop
and actions on an owned display:

```sh
python3 scripts/chess-x11-runtime.py \
  --root /opt/laplace/tools/chess/x11-runtime --deadline-seconds 280 \
  --output /build/laplace/recovery/cutechess-x11-manual-001.json \
  --evidence-output /build/laplace/recovery/cutechess-x11-manual-001
python3 scripts/check-cutechess-gui-session.py \
  --x11-runtime-receipt /build/laplace/recovery/cutechess-x11-manual-001.json \
  --binary /opt/laplace/bin/cutechess \
  --receipt /build/cutechess/laplace-cutechess-gui-build.json \
  --work /build/laplace/work \
  --output-dir /build/laplace/recovery/cutechess-gui-session-manual-001 \
  --timeout-seconds 60
```

Use the installed configuration's GUI and receipt paths when they differ. The
output directory must be new so a prior acceptance result cannot be overwritten.
The runtime owner first inventories the complete installed tool set. On the
measured Ubuntu 22.04 amd64 machine, missing Xvfb and xdotool can instead come
from authenticated official Ubuntu packages extracted under the selected private
prefix. Apt resolves the missing library closure; signed archive indexes, package
SHA-256 values and selected executable/library/resource hashes are retained.
Both helper processes authenticate the exact selection, and the GUI worker keeps
the selected Qt libraries first when adding the private X11 library directories.
A `tools-selected` receipt establishes dependency selection; the separate actual
GUI session establishes interaction readiness.

Normal host setup can still install X11 host dependencies through the narrow
`bash scripts/bootstrap-laplace-runner.sh chess-gui-runtime` command. It inventories
installed versions before attempting missing-package installation through root
or noninteractive sudo.

The helper requires `xvfb`, `xauth`, `xdotool` and `x11-utils`, plus the host
libraries needed by the selected SDK's xcb platform plugin, following
[Qt's Linux platform requirements](https://doc.qt.io/qt-6/linux-requirements.html).
It starts its own
authenticated Xvfb display, binds the GUI process and executable to the verified
source build, and requires the selected xcb plugin to load. It observes a mapped
main window, opens the actual New Game dialog with Ctrl+N, cancels with Escape,
and requires a normal zero-status exit after Ctrl+Q. Both windows must belong to
the same GUI process, and the dialog must be transient for the observed main
window. Forced cleanup never counts as successful application exit.

The receipt's scope is `virtual-x11-interactive`. It proves these actual window
and event-loop interactions; it does not prove an operator's desktop session,
engine play, or visual board correctness. The receipt and process logs are
retained on failure as well as success. Temporary settings, Xauthority and display
processes belong to this invocation and are removed when it finishes.

## Measured hart-server configuration (2026-09-15)

[Candidate calibration run 34958542147](https://github.com/SaltyPatron/Laplace-Refactor/actions/runs/34958542147)
completed on the actual Intel Core i7-6850K host: six physical cores, twelve logical
CPUs, affinity 0–11 and one NUMA node. The accepted observation ran from
10:51:49.006804 to 10:55:57.770316 UTC (248.764 seconds). Its memory grant was
7,040 MiB including a 512 MiB margin; peak sampled RSS was 6,154.48 MiB. Binary
identities and the observed resource envelope remained stable.

| Workload | Best measured starting setting | Median result |
| --- | --- | --- |
| One Stockfish process, 51-position depth-12 suite | Threads=4, Hash=64 MiB | 2.186 seconds |
| Stockfish self-play, capped 24-ply launch diagnostic | Concurrency=4; each engine Threads=1, Hash=16 MiB; ponder off | 11.518 games/second |
| Same capped Stockfish diagnostic, next concurrency point | Concurrency=6; same engine settings | 11.297 games/second |

The full grid tested Stockfish Threads=1,2,4,6,8,12 with Hash=16,64,256 MiB, and
CuteChess concurrency=1,2,4,6,8,12. Each profile has one warmup and three measured
samples: 96 timed processes, 24 PGNs, and 384 games including warmups. Every game
ended at the imposed 24-ply limit. These generated samples were not complete
recorded games and establish no Laplace recording rate. All transcript/PGN hashes and the medians were
independently checked. The single-process winner's measured range was
2.128–2.511 seconds. CuteChess concurrency four led six by about 1.96%, with
overlapping ranges. Repeat calibration for a different machine, CPU reservation,
search budget, opening suite, tablebase configuration or executable.

These settings describe two different measured workloads. Do not turn the
single-process four-thread result into four threads for every concurrent game,
or treat the CuteChess result as a measured Laplace evaluation-worker count.
The captured fresh Stockfish defaults were Threads=1, Hash=16 MiB and
NumaPolicy=auto. This candidate receipt does not establish the deployed services'
configuration. It also does not establish Laplace playing strength or Elo.

Retained evidence is the `candidate-chess-dependencies-34958542147-1` artifact,
ID `10392833423`, ZIP SHA-256
`15b902f010a6a4a9d1d0f5dd65e562ca91a4293af8337a34a879a69aef3afb62`.
The earlier candidate attempt failed its final memory-headroom check and remains
failed; only this completed attempt supports these provisional settings.

## Official Stockfish source as a Laplace corpus

Stockfish source admission is an explicit operator operation, not a dependency of
ordinary build or application delivery. `scripts/ingest-stockfish-corpus.py` uses
the same installed configuration and explicit source override as the dependency
doctor and installer. An explicit executable must be the direct
`src/stockfish` build of that checkout. The selected Git commit comes from
`deploy/linux/stockfish-release.json`; the existing private build receipt must bind
that commit to the executable's actual SHA-256.

The opt-in `ingest repo` path observes the committed Git tree and verifies every
tracked regular file's actual bytes against its Git blob identity. It does not
write a manifest into the upstream checkout. Untracked/ignored build products
are outside that selected source tree. Every tracked entry appears in the artifact
graph, with its Git identity, actual regular-file SHA-256, and admission disposition.

Files supported by the loaded native grammar registry enter the existing repository
and source-file decomposition pipeline. The Stockfish selection requires C++;
`.h` files use that declared C++ context. Native parser ERROR/missing-node counts
and complete/partial concrete-syntax-tree coverage are retained explicitly. A partial
syntax tree is not a claim of complete C++ understanding. The existing native
full-source composer preserves all source spans and gaps; zero-width recovery nodes
remain diagnostics and never invent bytes.

Files without a grammar can enter the existing native text content owner only when
native UTF-8/NFC normalization leaves their bytes exactly unchanged. They are labeled
**raw text**, with no grammar or syntax-completeness claim. Other files and Git
pointers remain explicitly **unadmitted**, even when their hashes are retained.
Receipts distinguish native C++ and other grammar admission, partial syntax trees,
raw-text admission, and unadmitted entries. Every admitted representation still
requires exact reconstruction of the original tracked bytes from PostgreSQL.

Public Git/build provenance is itself admitted through the existing native text
content owner and linked from the repository through `REFERENCES`. Its body excludes
raw Git remote configuration and private receipt paths. Successful acceptance
requires the actual PostgreSQL run/file journals, exact native reconstruction of
every admitted source file, its exact file metadata and the provenance content, and the stored provenance
relation. A second ordinary ingest must reconstruct the same content, use existing
per-file completion proofs, and insert zero entity, physicality or attestation rows
with zero new consensus observations/cells. Both runs and their raw logs are retained.

Each admission receipt binds the OS-reported loaded Laplace core module path and
the native/managed files' SHA-256 before and after the run. Native grammar providers
are linked into that core. `runtime-inventory.json` separately retains the selected
CMake cache digest, configured core-byte comparison, external `PINS.tsv` digest and
the actual C++ grammar/runtime Git identities where available. These current source
observations do not establish which checkout bytes compiled the loaded binary;
that stronger source-to-binary claim requires a build receipt. Missing or ambiguous
inventory remains explicit. The final proof binds the inventory file's SHA-256 and
requires the observed loaded core file to still match the admission receipt.

Run the same proof against an already built, matching CLI, holding the existing
host reservation while the admission runs:

```sh
LAPLACE_STOCKFISH_SOURCE=/vault/External/Stockfish/SF_19 \
flock --exclusive --close /build/laplace/work/host-resource.lock \
  python3 scripts/ingest-stockfish-corpus.py \
    --prefix /opt/laplace \
    --cli /path/to/the/matching/Laplace.Cli \
    --output /build/laplace/work/stockfish-corpus-proof-001
```

The output directory must be fresh and outside the upstream repository. `receipt.json`
exists only after both actual database readbacks and the no-amplification check pass;
partial runs retain their logs and completed observations. The command above holds
the existing shared host lock, while the CLI uses the canonical ingest lane.
Source-corpus readiness is distinct from PGN/opening/evaluation ingestion,
external engine benchmarks, and any playing-strength result.

For measurements, dispatch the existing **Laplace — benchmark evidence** workflow
(`.github/workflows/benchmark-evidence.yml`) against the selected installed revision.
Its `chess`, `recorded`, and `geometry` suites remain independently selectable.
Ordinary delivery does not dispatch that workflow or require those measurements to
finish. Delivery still admits its operational bundle, publishes applications, and
verifies ordinary operational execution; removing the chess workload is not a
claim that general instruction grounding or playing-strength targets are complete.

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

`LAPLACE_SYZYGY` and the explicit `chess-syzygy` input also accept multiple roots:
separate them with `:` on Unix or `;` on Windows. Runtime, input inventory and
ingestion recurse through every selected root, including WDL and DTZ kept on
different volumes, and pass the actual containing directories to Fathom. Every
selected directory must exist and the aggregate material names must have nonempty
WDL/DTZ pairs. An invalid explicit selection fails without substituting a default
directory. These packaging checks do not certify table checksums, full material
coverage, or the result of a native probe.

Use the [official Lichess tablebase mirror](https://tablebase.lichess.ovh/tables/standard/)
and its checksum lists when acquiring tables. The existing dataset acquisition
workflow owns resumable downloads and source admission. Downloading PGN archives,
opening files or tablebases does not by itself prove that their content has been
ingested into the substrate. The chess input reader opens `.pgn.zst` and `.zst`
files through native `libzstd`, including concatenated frames; input inventory
and execution use the same file selection. It streams through two 128 KiB buffers
and defaults to a 128 MiB decoder history window. Truncated frames, bad checksums
and missing libraries fail ingestion. A cancelled or failed reader releases its
file and decoder. Archive size does not require an equally large memory allocation.

Chess setup updates and builds Zstandard 1.5.7 from the official
[`facebook/zstd` repository](https://github.com/facebook/zstd/releases/tag/v1.5.7)
under the configured external source root. It records the exact shared library in
`LAPLACE_ZSTD_LIBRARY`, preserving PostgreSQL's system library. Bootstrap and
`check-chess-dependencies.py` verify the actual streaming ABI and selected release
by decoding a checksummed PGN fixture. Use `LAPLACE_ZSTD_LIBRARY` for an explicit
absolute shared-library path. That selection must load successfully; it is never
replaced by another library. On Windows, publication carries the selected Zstandard
DLL beside the application. `LAPLACE_ZSTD_WINDOW_LOG_MAX` explicitly changes the
admitted native history window (`2^value` bytes, default `27`) if a corpus requires
a larger window and the machine has the memory. This controls decoder history,
not a limit on archive length. The Python readiness probe tests that selected
library and records its version, path and SHA-256 when the loader exposes the path.
These codec checks do not claim that a downloaded corpus has been ingested.

Chess API, CLI and corpus jobs share installed configuration. Explicit process
settings take precedence over `<prefix>/app/laplace-api.env`, followed by the
legacy chess environment files. An explicitly selected missing engine fails
instead of silently choosing a different executable. Benchmark children inherit
the installed database, perfcache and substrate settings through their private
environment; receipts do not retain secret values. Stockfish and Cute Chess matches
do not require the entire Lichess game database.

`GET /chess/lichess/status` reports account readiness separately from process
health. Startup verifies the token, its `bot:play` scope and the account's BOT
title before establishing the event stream. It does not upgrade an account
automatically. Token contents are never part of the dependency report or CLI
startup output. Use the managed service's existing server-side token configuration.


## Explicit complete acceptance on the installed machine

The benchmark evidence workflow also offers suite `acceptance`.
It runs independently of product deployment. After the selected main revision
has completed deployment, create an explicitly named
`verify/chess-acceptance-<invocation>` branch at that **exact commit**, without
an extra branch-only commit. That narrowly scoped push invokes the same acceptance
job and provides an execution path when workflow dispatch is unavailable. Ordinary
main pushes do not select this work.

The acceptance receipt records the requested and executed revision, fixed profile,
individual phase status and duration, and every retained artifact's SHA-256.
The installed-form native guard verifies the source fingerprints, tested CMake
install bytes, installed extension SQL, migrations and all required chess ROMs.
Fresh managed publications must match the current API, UCI, MCP and Lichess DLL
and runtime configuration bytes. A mismatched runtime blocks corpus admission
and game recording; it is never attributed to the requested revision.

Before measurement, the explicit job inventories the requested local Stockfish
directory without reading source/configuration contents, selects the authenticated
X11 runtime without requiring host package installation, invokes the normal
official-source chess provisioning owner,
and records latest-release/NNUE/dependency and actual GUI interaction checks.
It retains API/UI response observations, service timestamps, and sanitized Lichess
account/event-stream readiness. A running process alone does not establish online
readiness. An unconfigured online account remains visibly unconfigured.

The fixed acceptance profile then retains:

- Full selected official Stockfish source admission and exact native readback,
  followed by a separate zero-amplification repeat.
- Complete generated and recorded games: 24 games, depth 4, concurrency 1/2/4,
  three repetitions, 600 seconds per case, a 7,200-second collector deadline,
  and a separate duration-qualified window of at least 30 seconds.
- Retained source admission: 16 complete games and two exact zero-writer replays,
  with generation, admission and replay rates kept separate.
- Prepared-corpus admission capacity: two authentic 24-game matches are completed
  and retained before one sequential admission window. Every fresh playing is
  committed and read back; each source job then has one exact zero-writer replay.
  Preparation and measurement have separate 3,600-second deadlines and receipts.
  A sample shorter than 30 seconds remains unqualified for sustained capacity.
- GeometryZM storage: 100,000 existing unique rows, 10,000 rows per transaction,
  concurrency 1/2/4, three repetitions and exact committed binary readback.
- Stockfish/CuteChess configuration calibration with three repetitions and two
  reserved logical CPUs, followed by another installed-native and service check.

One shared host lock covers this sequence, including adjacent preparation and
admission without an API restart between them. The self-hosted acceptance job has
a finite 600-minute outer deadline to accommodate the independent per-phase
envelopes; that deadline is not a claimed runtime. Independent measurements retain their
own failures; a failed match does not erase the separate geometry result.
Complete games have no move cap or adjudicated early stop. Geometry rows and
retained replays are never counted as newly recorded games. The 2,500-games/s
target has separate verdicts for the complete recorded workflow and prepared-corpus
admission. A 48-game prepared pool cannot establish 2,500 games/s over 30 seconds;
that would require at least 75,000 authentic fresh occurrences. See
[retained capacity](../benchmarks/RETAINED_CHESS_CAPACITY.md) for the exact timing
scope, content inventory and qualification rules.

The job preserves failure evidence and excludes the private corpus selection file
from its uploaded artifact. Service response times are labeled as response latency;
current unit timestamps do not constitute a cold application boot measurement.
The explicit runtime selection and its acquisition evidence are retained with the
GUI proof. Private tool selection is authenticated again immediately before launch.
Before corpus and game measurement, the explicit startup phase invokes the existing
API restart and managed activation owners, then verifies the complete application,
UI, and configured Lichess account/event stream. It records old/new process IDs and
monotonic process starts and measures restart request through full readiness. This
is service startup with existing OS caches, not a machine cold reboot. Preserved
operator-stopped services receive no startup claim. Services are not restarted
during recorded-game timing.
