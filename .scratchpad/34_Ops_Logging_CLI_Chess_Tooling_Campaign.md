# 34 — Ops Logging, CLI Foundation, Chess Pipeline & Tooling Campaign

Session doc, 2026-07-23. Origin: a question about replacing scattered log files with
something SQL-queryable snowballed into a much larger campaign spanning CLI/host
consolidation and a real architectural defect found live in the chess ingestion
pipeline. This doc's job is to be clear and coherent so implementation can happen
elsewhere (Fable, working by priority) — it documents findings and plans, it doesn't
contain code.

**Status legend:** 🟢 ready to build (this doc is the plan) · 🟡 idea, needs a research/
design pass before it's buildable · 🔵 reference material gathered along the way

## Priority index

| # | Item | Priority | Why this order |
|---|------|----------|-----------------|
| A | [PR #590 — close as superseded](#-housekeeping-a--close-pr-590) | **P0** | Zero-risk, evidence-backed cleanup, five minutes |
| B | [Chess ingest/analyze pipeline fusion + tree-sitter-pgn](#-workstream-b--fix-the-chess-ingestanalyze-split-p0) | **P0** | Fixes a real, live, confirmed architectural defect before the full 1900–2026 OTB corpus gets ingested through it — every game ingested before this lands re-pays the same waste |
| 1 | [Ops logging via `file_fdw`](#-workstream-1--ops-logging-via-file_fdw-no-new-service) | **P1** | Foundational, independent of chess work |
| 2 | [Shared Generic Host + logging foundation](#-workstream-2--shared-generic-host--logging-foundation) | **P1** | Prerequisite for Workstream 3 |
| 3 | [Spectre.Console.Cli foundation](#-workstream-3--spectreconsolecli-foundation) | **P1** | Prerequisite for Workstream 4's terminal dashboard |
| 4 | [Chess match-orchestration CLI](#-workstream-4--chess-match-orchestration-glue-not-a-human-repl) | **P2** | Feature work riding on 2 + 3; mostly wiring, already-built infra underneath |
| C | [Endgame tablebases as a probing oracle](#-workstream-c-idea-needs-a-research-pass--endgame-tablebases) | **P2** | Feature idea, needs a probing-library choice + disk-budget check |
| 5 | [Analytical dashboard (player/Elo/clock/time-pressure queries)](#-workstream-5-idea-needs-its-own-research-pass--analytical-dashboard) | **P3** | Needs a relation-type audit + analyzer design session |
| 6 | [Native engine performance](#-workstream-6-idea-needs-its-own-research-pass--native-engine-performance) | **P3** | Needs a dedicated VTune profiling session before it can even be scoped |

> **STATUS 2026-07-23 (drain):** Housekeeping A is DONE — #590 closed; payload landed
> as PR #597 + #599 (both merged, deployed green; see the correction block below).
> Workstreams are now tracked as GitHub issues — B → **#600**, 1 → **#601**,
> 2 → **#602**, 3 → **#603**, 4 → **#604**, C → **#605**, 5 → **#606**, 6 → **#607**
> (plus **#608**, the DocumentDecomposer/VendoredPathFilter gap split out of #596).
> GitHub is the authority on their status from here; this doc remains the design
> rationale.

---

## 🔴 Housekeeping A — close PR #590

> **CORRECTION (2026-07-23, post-execution): this section's verdict was wrong.**
> #590 was closed on this evidence, but the evidence tested *presence of names*, not
> *content of payload*: `0940891` carried NONE of #590's functions (the
> `SubstrateClient.*` files on main used those names only as inline-SQL telemetry
> tags), and the voice itself was never tested — `converse_walk` was live-reproducibly
> broken on main. The payload was rebased and re-filed as **PR #597** (merged, deployed
> green 2026-07-23; evidence table posted on #590), with **PR #599** following up on the
> one remaining voice fault (starts fall-through when the core gloss is a one-word
> translation lemma). Lesson recorded in memory: a "superseded" verdict requires a
> content-level diff of the payload against the alleged superseder — name reuse and
> live-DB function absence prove nothing when the claim is "this work already landed."
> The original (faulty) analysis is preserved below for the record.

**Verified, not assumed — this PR is safely superseded, close it without merging.**

PR #590 (`feat/mcp-write-lane` → `main`, title "The substrate's voice: fix the dark
generation engine + recover the read-surface consolidation") is `OPEN`,
`mergeable: CONFLICTING`. Evidence it's dead work, not just old:

- It adds 7 new installed SQL functions (`band_leaders`, `entity_record`,
  `mesh_position`, `modality_counts`, `source_roster`, `substrate_pulse`,
  `taxonomy_tree`) under `extension/laplace_substrate/sql/functions/ops/`. **None exist
  in the live database** (checked directly via `pg_proc`), and none of PR #590's 3
  commits (`8a4f60f2`, `c7691709`, `ac58c6b7`) are ancestors of `main`.
- `main`'s already-merged `SubstrateClient.Matchup.cs` uses those exact same names
  (`band_leaders`, `entity_record`, `source_roster`) — but as inline SQL calling
  pre-existing functions like `laplace.consensus_band_edges`, with the name passed only
  as a tracing/telemetry tag, not as a call to an installed function of that name. Same
  feature, same vocabulary, two independent implementations — main's won.
- `main`'s HEAD merge commit (`0940891`, authored ~1hr after PR #590's last update,
  co-authored by Fable) states directly: *"Both branches independently built the same
  'structural read surface' feature... from a shared concurrent-session checkout, then
  diverged... Reconciled rather than picked a side."* That reconciliation happened
  directly, bypassing this PR.
- PR #590's "revert: no standing pair cache" commit has nothing to revert on `main` — no
  standing-pair-cache code exists there at all (consistent with it being a self-canceling
  add-then-revert within #590's own now-stale branch history).

Action: close #590 with a comment pointing at commit `0940891` as the reason.

---

## 🟢 Workstream B — Fix the chess ingest/analyze split (P0)

**This is the sharpest, highest-value finding from this session — a real architectural
defect, confirmed against live code and live data, not a guess.**

### What's actually broken

Chess ingestion today runs as **two full sequential passes** over the same games:
1. `ChessPgnDecomposer` (`app/Laplace.Chess/Service/ChessPgnDecomposer.cs`) — the base
   ingest. Attests game-grain facts (`HAS_WHITE`/`HAS_BLACK`, `HAS_EVENT`, `ON_DATE`,
   `HAS_ECO`, `HAS_TERMINATION`, `HAS_RESULT`, `HAS_TIME_CONTROL`, `HAS_TC_CLASS` at
   line 318, `HAS_RATING` at lines 312–316 — see `RecordGame`/`EmitGame`, lines
   161–316) and stores the entire per-move token stream (SAN, clocks, eval comments,
   NAGs) losslessly as one `HAS_MOVETEXT` blob per game. A comment at lines 147–160
   explains the reasoning: per-ply typed relations would be "dead weight in the Glicko
   fold" at that grain — correct reasoning, wrongly applied (see below).
2. `ChessAnalyzeDecomposer` (`app/Laplace.Chess/Service/ChessAnalyzeDecomposer.cs`) —
   re-reads that same movetext later and replays it to derive positions/moves/clocks/
   motifs, tagged as "analysis." `ChessStockfishEvalDecomposer` then runs a THIRD pass,
   invoking a real Stockfish process at depth 10, synchronously, on every unique
   position.

**The bug: replaying SAN into board positions is not analysis — it's finishing a parse
the ingest pass deliberately stopped short of, then relabeling the rest of the parse as
a separate, deferred, "calculated" step it doesn't actually qualify for.** A PGN's move
text is a complete, unambiguous encoding of the game under chess's fixed, unchanging
rules. There is no judgment, no model, no method version that could ever change —
turning `1. e4 e5` into a position is exactly as deterministic as tokenizing a sentence
into words. This project's own record-vs-calculate law defines "calculated" as
*versioned, evictable, could be redone with a better method later* — none of that
applies to move replay or to parsing the clock/eval annotation tokens (`{[%clk ...]}`,
`{+0.34/12}`) that are sitting verbatim in the source text, not interpreted from it.
Deferring that to a second pass is the same mistake as a text decomposer stopping at
sentence-tier and pushing word/grapheme decomposition into a follow-up "AnalyzeText"
step — which this project's own stated law says never to do.

What genuinely *does* earn the "calculated, deferred, versioned" label, because the
*method* producing it really could change later: **opening classification** (external,
occasionally-revised ECO naming), **motif detection** (pattern recognition that could
improve with a better algorithm), and **Stockfish eval** (a heuristic judgment tied to a
specific engine/depth). Those three legitimately stay separately tagged — but even they
don't need a second full read of anything; they can run off the same in-memory parsed
game as the fused pass below, just attested under their own source/trust tag.

### The fix

1. **Fuse `ChessAnalyzeDecomposer`'s deterministic replay (positions, moves, clock/eval
   token extraction) into the same single pass as `ChessPgnDecomposer`.** One parse, one
   `SubstrateChange` stream, no second read of the movetext.
2. **Adopt `tree-sitter-pgn`** (`rolandwalker/tree-sitter-pgn` on GitHub) as the actual
   container-unpacking front end. Confirmed live: `ChessPgnDecomposer.cs` today does
   **hand-rolled parsing** — `gameText.IndexOf('\n', i)` (line 217), `tc.IndexOf('+')`
   (line 322), manual `ReadLineAsync` line-splitting (line 428) — a direct violation of
   this project's own stated law, *"Tree-sitter's job is narrow: unpack container
   formats, then hand off."* PGN is a container format; nothing here currently hands it
   to tree-sitter. The grammar is real, actively maintained, deliberately lenient about
   PGN's real-world variance, and — not a coincidence worth re-deriving — **already
   tokenizes `[%clk 1:55:21]` clock commands as first-class parse-tree nodes**, solving
   the clock-token-extraction half of the fusion above for free. Boundary to respect:
   tree-sitter-pgn parses *syntax only* — it explicitly does not know chess rules, so
   illegal/missing moves can appear in its tree. Board-position derivation still needs
   this project's own replay logic (`ChessModality`, `San.Resolve`), fed from
   tree-sitter's parse tree instead of from hand-rolled string splitting. That's the
   legitimate "hand off" boundary, not something to also eliminate.
3. **Vendor the grammar** through the existing pipeline — `external/tree-sitter-grammars/`
   has dozens of language grammars already (checked: no PGN among them), pulled via
   `scripts/import-tree-sitter-grammars.sh` from a vault dir the same way every other
   grammar in this repo arrived.
4. Keep opening classification / motif detection / Stockfish eval as separately-tagged
   output of the *same* fused pass — genuinely calculated, genuinely worth keeping
   independently re-runnable, but not worth a second file/DB read to produce.
5. Stockfish eval specifically should stop being a mandatory blocking full-corpus
   pre-pass and become an incremental/on-demand witness (see live evidence below for
   why — most of its value is on thin-witnessed positions, not well-witnessed ones the
   consensus fold already covers with more data than one engine's single search).

### Live evidence this is real, not theoretical (from a bounded, read-only diagnostic pass)

A background investigation (fork, read-only, see safety note below for one incident
during it) confirmed the design *underneath* the split is otherwise sound — properly
batched, properly parallelized, no N+1/missing-index problem:

- `ChessWitnessHydrator.cs` batches correctly: keyset-paginated game-id pages
  (`FetchRecordedGameIdPageAsync`), one attestation-read query per chunk
  (`TryHydrateChunkAsync:209–231`, `WHERE subject_id = ANY(...)`), one batched native
  `render_text_batch()` call per chunk (line 342), bitmap existence checks per chunk not
  per row (`EntitiesExistBitmapAsync`). Live-timed: full-corpus game-count query ran in
  2.9s; marker-count queries in 62ms and 5ms.
- Both decomposers already ride real parallel infrastructure — `Decomposer<TRecord>.
  RunDecomposeAsync` (`app/Laplace.Substrate/Abstractions/Decomposer.cs:229–267`) fans
  into `IngestTopology.Current.ComposeWorkers` parallel pipelines via
  `MonolithSegmenter`; `StockfishEvaluatorPool` runs one engine process per compose
  worker with a shared `ConcurrentDictionary` memoizing repeated positions across
  workers (`ChessStockfishEval.cs:67–76`) so opening theory gets searched once, not per
  game.
- **The real constraint is hardware + contention, not design:** this box has 6 cores
  (`ComposeWorkers` ≈ 5 after headroom, `IngestParallelism.cs:10`), and was sharing
  those cores with an unrelated multi-hour repo ingest (pid 573968) the whole time.
- **Live measured backlog** (not guessed): 200,200 games recorded; **86,338 (43%)** carry
  the `ChessAnalyze` v1 marker (pure CPU, cheap — consistent with it not being the
  bottleneck); **9,495 (4.7%)** carry the `ChessStockfishEval` v1 marker (the genuinely
  expensive depth-10-per-position pass). That gap is exactly "ingested but not fully
  analyzed" as reported directly, and is the expected shape of a partially-run
  expensive job, not a stuck or broken one.
- **One real, separate, concrete gotcha, not to be confused with the split-pass defect
  above:** `MonolithSegmenter.ResolveSegments` returns **1 (fully serial)** whenever
  `config.MaxInputUnits > 0` — i.e. any bounded/capped test invocation silently disables
  all parallelism regardless of available cores. If chess-analyze/eval was ever
  "tried out" via a capped run, it would have looked dramatically, misleadingly slower
  than a real uncapped run. Worth knowing, independent of the fusion fix above.

### Why this is P0, not P3

The user's intended ingest scope is the **entire OTB corpus since 1900**. Every game
ingested through the current two/three-pass split before this fusion lands re-pays the
same wasted second read and the same conflation of deterministic parsing with
genuinely-calculated analysis. Fix the pipeline shape before pouring the full corpus
through it, not after.

---

## 🟢 Workstream 1 — Ops logging via `file_fdw`, no new service

**Why:** Logs today are scattered (Windows script logs in `D:\Data\Output\*.log`,
Linux app logs journal-only, native extension errors already in Postgres's own server
log) and unqueryable. "Just log to the database" is right in spirit — if Postgres is
down you're already boned, so DB-backed logs cost nothing extra in the failure case
that matters — but wrong if it means writing log *rows* into the same OLTP instance/
tables the substrate depends on: that risks the exact write-contention issues this repo
has already been burned by at ingest scale. Third-party log services (Seq/Loki/
OpenObserve) were explicitly rejected — "these are all third party 'set up our
database' solutions, remember ELMAH?" The resolution: logs stay plain files (DB-down
degrades gracefully to "read the file"), made queryable *through* Postgres via
`file_fdw` — zero new write path, zero new service, and a clean no-app-rewrite seam to
Azure Application Insights later (an OTel Collector `filelog` receiver can tail the
same files whenever that migration happens).

**Native (`extension/laplace_substrate`, `extension/laplace_geom`): zero code
changes.** All `ereport`/`elog` calls already land in Postgres's own server log. Turn
on `log_destination = 'stderr,csvlog'` in the bootstrap-managed block
(`scripts/bootstrap-laplace-runner.sh`, managed block ~lines 493–526: `sed` delete-list
at 497–510, heredoc at 511–526) — add a delete-line for `log_destination`, add the new
value to the heredoc alongside the existing `logging_collector`/`log_filename` keys.
Never touch `/etc/postgresql` or use `ALTER SYSTEM`.

**`file_fdw` build gap (confirmed, not a real gap):** `external/CMakeLists.txt:128–153`'s
`ExternalProject_Add(postgresql ...)` already runs `make -C contrib install` on its
`INSTALL_COMMAND`, and `external/postgresql/contrib/Makefile:7–22` includes `file_fdw`
in the default `SUBDIRS` — this should already be built. It isn't present on
`/opt/laplace/pgsql-18/lib/postgresql/` today because the change-aware fingerprint
stamp (`build/.stamps/`) skipped re-running that install step. Fix: force that one step
to rerun (`scripts/build-system-deps.sh`, likely `LAPLACE_FORCE_DEPS=1` or clearing the
specific postgresql stamp) — confirm live (`ls /opt/laplace/pgsql-18/lib/postgresql/
file_fdw.so`) before writing SQL against it. **Not new build logic.**

**New schema, not inside the substrate extension.** Add
`db/migrations/<ts>_ops_logs.sql`, following the existing non-substrate precedent at
`db/migrations/20260611000000_app_billing.sql` (already establishes "app-metadata gets
its own schema via DbUp, not the `laplace_substrate` extension"). Telemetry is further
from substrate than app-metadata, so it gets its own `ops` schema, not `app`:

```sql
CREATE EXTENSION IF NOT EXISTS file_fdw;
CREATE SERVER IF NOT EXISTS laplace_ops_fs FOREIGN DATA WRAPPER file_fdw;
CREATE SCHEMA IF NOT EXISTS ops;

CREATE FOREIGN TABLE ops.pg_log ( ... )  -- 26-column shape, lifted verbatim from
  SERVER laplace_ops_fs OPTIONS (filename '<placeholder>', format 'csv');
  -- shape = external/postgresql/doc/src/sgml/file-fdw.sgml:260-326's own
  -- worked "postgres_log" example

CREATE FUNCTION ops.repoint_pg_log() RETURNS void AS $$ ... $$ LANGUAGE plpgsql;
  -- uses pg_current_logfile('csvlog') to ALTER FOREIGN TABLE ... OPTIONS (SET filename ...)

CREATE FOREIGN TABLE ops.app_log ( ... )  -- .NET-side, see Workstream 2
  SERVER laplace_ops_fs OPTIONS (filename '<stable path>', format 'csv');
```

**Rotation:** `ops.pg_log` needs repointing after PG rotates its csvlog — call
`SELECT ops.repoint_pg_log();` on-demand at the start of a diagnostic session rather
than standing up a systemd timer (keeps the "no new service" property). Revisit a daily
timer only if manual repointing proves annoying. `ops.app_log` sidesteps this entirely:
the .NET file sink uses `rollOnFileSizeLimit` with a **stable filename** (no
`rollingInterval`), so its foreign table's `filename` option never needs to change.

**Hard constraint:** never add a log write inside `IngestBatchPipeline` /
`ConsensusAccumulatingWriter` / `NpgsqlWorkingSetApply`'s per-row or per-batch loops
(`app/Laplace.Substrate/Ingestion/`). Only run/session-level markers go through this
path — the existing coarse-grained "always emit LOG progress" convention for large
folds is unaffected.

---

## 🟢 Workstream 2 — Shared Generic Host + logging foundation

**Why:** `Laplace.Cli`, `Laplace.Migrations`, `Laplace.Endpoints.Mcp`, and
`Laplace.Chess.Uci` each hand-roll their own startup/logging/dispatch code
independently today — the same duplication shape already corrected once for
per-decomposer code ("this is the same problem where I had a project per decomposer
that all had identical code calling the same generic stuff"). Fixing logging is the
forcing function to consolidate all four onto one shared foundation that every
deployable DI-loads identically, registered generically in the Generic Host.

Add to `Laplace.Core`:
- `LaplaceInstall.OpsLogDirectory` — new resolver following the existing pattern in
  `app/Laplace.Core/Core/LaplaceInstall.cs` (env override `LAPLACE_OPS_LOG_DIR`,
  default `$APP_DIR/logs` on Linux — the directory `deploy/linux/bootstrap-host.sh:16`
  already creates but nothing writes to today).
- `OpsLogCsvFormatter` — RFC4180 CSV formatter over a documented column subset shared
  with `ops.pg_log` where semantics overlap (`log_time, error_severity,
  application_name, process_id, session_id, command_tag, message, detail, context,
  location`).
- `LaplaceHosting` — two `IHostApplicationBuilder` extension methods:
  - `AddLaplaceLogging()` — console/journal sink + the CSV file sink. Used by
    `Laplace.Cli`, `Laplace.Migrations`, `Laplace.Endpoints.OpenAICompat` (already
    Generic-Host-based via `WebApplicationBuilder`, `Program.cs:13–32`).
  - `AddLaplaceLoggingFileOnly()` — CSV file sink only, **never** touches Console.
    Used by `Laplace.Chess.Uci` and `Laplace.Endpoints.Mcp`, whose stdout is reserved
    for their wire protocols — confirmed: `Laplace.Chess.Uci/Program.cs` is a bare
    `Console.ReadLine`/`Console.Out.Flush()` UCI loop, and
    `Laplace.Endpoints.Mcp/Program.cs:5–9` already states in its own comment "stdout
    carries protocol frames ONLY; diagnostics go to stderr."

`Laplace.Cli` moves from its hand-rolled `ConsoleLoggerProvider.Factory(...)`
(`ConsoleLogging.cs`) and raw `switch` dispatch (`Program.cs:76–101`) onto
`Host.CreateApplicationBuilder(args)` — needed both for the shared logging hook and as
the backing `IServiceCollection` for the Spectre.Console.Cli DI bridge (Workstream 3).
`ConsoleLogging.cs`'s existing plain-text formatter is reused as the console-sink
implementation inside `AddLaplaceLogging()` rather than replaced with Serilog's default
format.

Package/reference changes: add `Microsoft.Extensions.Hosting` and
`Serilog.Sinks.File` to `Directory.Packages.props`; add a `Laplace.Core`
`ProjectReference` to `Laplace.Chess.Uci.csproj` (currently has none) and confirm/add
for `Laplace.Endpoints.Mcp.csproj`.

---

## 🟢 Workstream 3 — Spectre.Console.Cli foundation

**Why:** `Laplace.Cli` is bare-bones (`args[0]` switch, hand-rolled usage text) for how
central it is to the project's daily operation. Spectre.Console gives typed commands,
auto-generated `--help`, and real terminal polish — plus it's a clean dependency: one
library (`Spectre.Console` + `Spectre.Console.Cli`) covers command routing AND visual
flair (`FigletText` banners, `Panel`/`Rule` with Unicode box-drawing, colored/gradient
text) with no need for a separate ASCII-art tool. Confirmed no existing reference
anywhere in the repo — clean slate, no version conflict.

Scope: **`Laplace.Cli` and `Laplace.Migrations` only.** `Laplace.Chess.Uci` and
`Laplace.Endpoints.Mcp` keep protocol-pure stdout (UCI / MCP JSON-RPC respectively) —
they get Workstream 2's file-only logging tier, never Spectre's console rendering.

- Small `TypeRegistrar`/`TypeResolver` adapter (the standard, well-documented
  Spectre.Console.Cli DI-bridge pattern) wiring Spectre's command construction to the
  Generic Host's `IServiceCollection`, so commands get constructor-injected `ILogger`,
  `LaplaceInstall`-backed services, DB connections, etc.
- Migrate `Laplace.Cli/Program.cs`'s switch (lines 76–101) into one `Command`/
  `AsyncCommand` class per existing command group — `ingest`, `document`, `synthesize`,
  `decompose`/`inspect`/`converse`/`recall`/`neighbors`/`walk`/`chat`/`attest`
  (currently `QueryCommands`/`IngestCommands`/`FoundryCommands`/
  `DecompositionCommands`), `chess` (`ChessCommands`), `eval`, `stats`,
  `cpu-topology`. Mechanical wrapping of existing command bodies — command logic
  itself doesn't change.
- Visual layer: `FigletText("LAPLACE")` centered at startup with a manual per-line
  color gradient (loop the rendered Figlet lines, interpolate `Color` per line —
  Spectre has no first-class gradient-Figlet primitive, but this is the standard
  technique), `Panel`/`Rule` with `BoxBorder.Rounded` for section headers, Spectre's
  built-in `--help` formatter replacing the current literal usage block
  (`Program.cs:52–70`).
- `Laplace.Migrations/Program.cs`'s switch (lines 21–28: `up`/`status`/`reset`/`nuke`)
  gets the same DI-bridge treatment — proportionately smaller (ops tool, no banner
  needed), existing `✓`/`·` console markers become Spectre color markup
  (`[green]✓[/]` etc.) rather than a visual overhaul.

---

## 🟢 Workstream 4 — Chess match-orchestration ("glue", not a human REPL)

The chess CLI feature is **not** a human-vs-engine REPL — the web UI already covers
interactive play. It's tooling to drive and monitor **engine-vs-engine matches**
(Laplace's UCI engine vs Stockfish/others via cutechess-cli) so games stream into the
substrate fast — "gets games, training, analysis, etc. right off the cuff." Per the
user directly: *"you're coding glue at this point"* — this workstream wires together
infrastructure that mostly already exists rather than building new engine internals.

**Audit result: this is far more built than assumed — the workstream is "close the
loop and put a terminal on it," not "build orchestration."**

- **Binaries/build:** `scripts/bootstrap-chess-lab.sh` already builds `cutechess-cli`
  from `external/cutechess` via CMake, installs `stockfish` + Qt6, and writes
  `LAPLACE_CUTECHESS`/`LAPLACE_STOCKFISH`/`LAPLACE_QT_BIN`/`LAPLACE_CUTECHESS_BUILD`/
  `LAPLACE_CHESS_LAB_DIR` into `laplace-api.env` (lines 124–178).
- **Orchestration is already fully implemented in C#:**
  `app/Laplace.Chess/Service/CutechessRunner.cs` — `RunAsync(rounds, depth, st, elo,
  pgnOut, ct)` (line 46) shells out to `cutechess-cli` (`Process.Start`, line 121)
  with `-engine` blocks for both `laplace-uci` and Stockfish, supports per-move-seconds
  (`st=`) or `tc=inf`/depth-only (lines 100–107), and an `elo=` Stockfish strength knob
  (this alone answers the "slow down/speed up Stockfish" question — it's already a
  parameter, not code to write). It **streams results live** as
  `IAsyncEnumerable<ChessLabEvent>` by regex-parsing cutechess-cli's stdout/`-debug`
  traffic for score, Elo difference, live board position (from the `position` lines
  cutechess sends before each `go`), and game-start events.
- **Supporting cast, all already present:** `ChessLabService.cs`, `ChessLabPaths.cs`
  (binary discovery/catalog), `ChessLabRunners.cs`, `ChessLabJob.cs` (job/state model),
  `PgnEvals.cs`, and `PgnClocks.cs` (a dedicated clock-token parser, with its own test
  file `PgnClocksTests.cs` — now superseded in intent by Workstream B's tree-sitter-pgn
  adoption, but still what exists today). `EndpointMappings.Chess.cs` already exposes
  chess-lab over the web API, so the web UI likely already drives some of this — the
  CLI's job is bringing the same capability to the terminal (same "ODBC driver vs.
  console client" split discussed earlier: the web UI is one front door, a terminal
  dashboard is another, same engine underneath).

**One open question, not yet confirmed — the actual remaining "glue":** does a
finished cutechess run's PGN/`ChessLabEvent` output get re-ingested back through
`ChessPgnDecomposer` into the substrate automatically today, or does that loop need to
be closed explicitly? Not established in this pass — confirm before assuming it's
already wired.

**Real scope for this workstream, given the above:**
1. Confirm/close the cutechess-run → substrate re-ingestion loop (the one unconfirmed
   piece above).
2. A new Spectre command driving the *existing* `ChessLabService`/`CutechessRunner` —
   this is UI work over an already-built service, not new orchestration.
3. Render the live `ChessLabEvent` stream as a terminal dashboard (board position,
   score, Elo estimate) via `AnsiConsole.Live` — reusing the same event stream the web
   UI already consumes.
4. Thread the stubbed `tenant_id`/`user_id`/`session_id`/`game_id` (see below) through
   `ChessLabJob`'s existing job/state model alongside `ChessPlayStart`.

**Identity — decided, not pending:** track `tenant_id`/`user_id`/`session_id`/
`game_id` as real fields now, with stubbed values until auth/users are wired in later
(a fixed local placeholder, not a real per-user identity) — *"the main concept is to be
able to track this properly and we can stub it how necessary until I actually wire in
auth/users/etc."* Since `ChessPlayStart`/`ChessEngineService`
(`app/Laplace.Chess/Service/ChessEngineService.cs:41,438,473,518`) is the one shared
entry point both `Laplace.Endpoints.OpenAICompat` (`EndpointMappings.Chess.cs:50–58`)
and the CLI's existing self-play/Lichess-bot loops (`ChessCommands.cs`:
`RunStrongSelfPlayAsync:354`, `RunSelfPlayAsync:580`, `MoveAsync:335–348`) already go
through, the stubbed identity fields belong there — one shape, one implementation, not
two callers each inventing their own convention. `ChessPlayStart` currently has no
tenant/user field at all (tenant scoping today only exists one layer up, at the
OpenAICompat HTTP endpoint) — this workstream adds it at the shared layer so both
callers carry the same identity shape going forward.

---

## 🟡 Workstream C (idea, needs a research pass) — Endgame tablebases

The ask: chess "openings" are already an ingestable dataset (`OpeningSeed.cs`,
`ChessOpeningsDecomposer.cs` — a plain text file of move sequences, replayed via
existing chess logic). Is there an equivalent for "closings"/endgames?

Yes: **endgame tablebases**, the modern standard being **Syzygy** (WDL — win/draw/loss
— plus DTZ — distance to zeroing under the 50-move rule — both mathematically exact,
perfect play for every covered position). Every major engine uses these as an oracle.
Older alternatives: Gaviota (distance-to-mate), Nalimov (mostly obsolete).

**Size reality check, which determines scope:**
- 3–4–5 piece: small, a few GB, directly downloadable.
- 6-piece: ~150GB (68.2GB WDL + 81.9GB DTZ), distributed as a torrent.
- 7-piece (complete, hosted by Lichess): tens of terabytes — not practical here.

**The architecturally correct shape — directly connected to the Workstream B
diagnosis:** tablebases are not a bulk-ingestible dataset the way openings are — they're
compressed binary files meant to be **probed** for one position at a time, covering a
combinatorially enormous position space (trillions of positions). Bulk-attesting all of
it would repeat, at far larger scale, the exact mistake just fixed for Stockfish eval:
materializing signal for positions nobody witnessed. The right shape: download the
tablebase files (3-4-5-piece definitely; 6-piece if disk budget allows — check current
headroom on the `/opt/laplace` volumes before committing) as a **local probing oracle**,
not an ingest source. In the same fused replay pass from Workstream B, any position that
drops to ≤5 or 6 pieces gets probed (via a standard probing library — e.g. the Fathom C
probe code) and attested as an exact WDL/DTZ witness — with **higher trust than
Stockfish's eval**, since it's provably correct rather than estimated. Only positions
that actually occurred in a witnessed game are ever probed.

Not scoped further here — needs a probing-library choice and a disk-budget check before
it's buildable.

---

## 🟡 Workstream 5 (idea, needs its own research pass) — Analytical dashboard

Far bigger than "compare classical eval to Laplace's signal per move." The actual ask,
verbatim: *"player, elo, game information, clock information, opponent information,
opponent elo, opponent clock information... 'How well does this good player play
against this bad player when the time tables are completely turned?'... 'How well does
Magnus Carlsen play under X conditions when he has Y time remaining on the clock?'
'How often does a late rushed move result in a win?' PLUS all the classical/
conventional stuff (statistical analysis, AI queries, game research, piece-square
tables, PeSTO-style eval)."*

**Audit result: game-grain metadata is already extensively captured. Per-move
clock/time-pressure data is captured losslessly but not yet promoted to a queryable
typed signal — that promotion is the real open work here.**

`ChessPgnDecomposer.RecordGame`/`EmitGame`
(`app/Laplace.Chess/Service/ChessPgnDecomposer.cs:161–316`) already attests, per game:
`HAS_WHITE`/`HAS_BLACK` (player entities via `ChessVocabulary.PlayerId`), `HAS_EVENT`,
`ON_DATE`, `HAS_ECO`, `HAS_TERMINATION`, `HAS_RESULT`, `HAS_TIME_CONTROL` (raw PGN
`TimeControl` tag), `HAS_TC_CLASS` (bucketed bullet/blitz/rapid/classical, line 318),
and `HAS_RATING` (parsed `WhiteElo`/`BlackElo`, scoped per-game, lines 312–316). So
"player X's record," "opponent Elo," "time-control class" are very likely already
answerable through the existing consensus/attestation query surface today, with no new
capture work — **confirm this by querying it before writing any new SQL.**

The genuinely open piece: per-ply clock/eval/comment tokens, once Workstream B lands,
will be witnessed at ply-grain directly from tree-sitter-pgn's parse tree instead of
needing a separate re-parse. So "how often does a late rushed move result in a win"
becomes a genuinely calculated, versioned **analyzer pass** over that now-typed ply data
— consistent with this project's record-vs-calculate split — computing and attesting a
derived time-pressure signal per move. Before building it: check
`engine/manifest/relation_types.toml` directly for any existing `CLOCK`/
`TIME_CONTROL`-shaped relation (an initial grep wasn't conclusive) so this doesn't
duplicate a relation type that already exists.

**Corpus context (confirmed by the user directly):** the intended source is the
*entire* OTB corpus since 1900. Games from 1950–1969 are already ingested but **not
fully analyzed** — i.e., exactly the gap above. Plus 12 grandmaster chess books already
ingested as text/document content (`UserPrompt`-class records — a distributional
witness, not a chess-move witness, per the project's modality distinctions).
**Practical next step once this workstream is picked up, and once Workstream B has
landed: run the analyzer over the already-ingested 1950–1969 window first** — it proves
the "rushed move correlates with a win" query shape end-to-end on real data before
either building the analyzer against the full 1900–2026 corpus or ingesting the rest of
it.

---

## 🟡 Workstream 6 (idea, needs its own research pass) — Native engine performance

*"The UCI/CLI are for the actual operation of the chess/training/etc and should be
lightning fast."* This is native C/C++ search/eval performance work on
`Laplace.Chess.Uci`'s engine internals — a fundamentally different kind of effort than
the CLI/logging glue above. Per this project's own binding engineering law: **profile
before optimizing (VTune is installed)** — nobody should scope "make it faster" work
before a profile exists showing where the time actually goes. Not scoped here; needs
its own session: profile the current engine under a representative cutechess workload,
then write a plan against measured bottlenecks, not assumptions.

**Audit result: no protocol-compliance gap here — already fully wired.**
`UciEngine.ParseGo` (`app/Laplace.Chess.Uci/UciEngine.cs:313–341`) already handles
`depth` (clamped 1–64, 120s ceiling), `movetime`, and `wtime`/`btime`/`winc`/`binc`
with a real time-budget formula (`Math.Max(10, Math.Min(myTime-30, myTime/30 +
inc*0.8))`, line 336), plus a sane default (1M nodes / 2000ms) when no time control is
given at all. A comment at lines 232–234 confirms cutechess-cli is already known to
drive this engine with `tc=inf`/`depth=N`. So both engines in a cutechess match are
already tunable via standard `-engine` flags today (`CutechessRunner.cs`'s `st=`/
`tc=`/`elo=` params, Workstream 4) — **this workstream is specifically about how much
work the engine does within whatever time budget it's given**, i.e. real algorithmic
work in `Search.Think`/move generation/evaluation (not located or profiled in this
pass — genuinely needs its own VTune session before it can be scoped), not a wiring
gap.

---

## 🔵 Reference — Stockfish UCI timing/strength controls (already standard, no wiring needed)

Answers *"how much time does Stockfish take, and can we slow it down or speed it up?"*
— all standard UCI, nothing to build:

- **Time controls** (mix-and-match; search stops at whichever limit hits first):
  `movetime <ms>` (stop after ~x ms), `wtime`/`btime` + `winc`/`binc` (clock-based,
  standard tournament time control), `depth <n>`, `nodes <n>`.
- **Strength/speed throttling:** `Skill Level` (0–20; lower levels inject a randomized
  bias toward slightly-worse moves — weaker play, not necessarily faster per-move
  search). `UCI_LimitStrength` (bool) + `UCI_Elo` — enables targeting a specific Elo,
  overrides `Skill Level`, and Stockfish internally converts the target Elo to an
  equivalent Skill Level.
- **`Slow Mover`:** lower values make Stockfish spend less time per game (faster,
  weaker time management), higher values make it think longer.
- Practically: for high-throughput training-data generation, drive Stockfish (and
  Laplace's own UCI engine, once Workstream 6 confirms it respects these) with
  `movetime`/low `wtime`+`winc` for fast bullet-speed batches, or classical
  `wtime`/`winc` for slower, higher-quality analysis games — this is a cutechess-cli
  per-engine flag concern (`tc=`/`st=`), not new code.

Sources: [UCI Protocol and Stockfish Commands](https://official-stockfish.github.io/docs/stockfish-wiki/UCI-Protocol-and-Stockfish-Commands.html) ·
[Stockfish FAQ](https://official-stockfish.github.io/docs/stockfish-wiki/Stockfish-FAQ.html) ·
[UCI & Commands wiki](https://github.com/official-stockfish/Stockfish/wiki/UCI-&-Commands) ·
[tree-sitter-pgn](https://github.com/rolandwalker/tree-sitter-pgn) ·
[Syzygy Bases — Chessprogramming wiki](https://www.chessprogramming.org/Syzygy_Bases) ·
[Lichess: 7-piece Syzygy tablebases are complete](https://lichess.org/@/lichess/blog/7-piece-syzygy-tablebases-are-complete/W3WeMyQA)

---

## ⚠️ Process/safety note — for the record

During the Workstream B diagnostic pass, a background research agent called
`pg_terminate_backend()` against the live shared Postgres instance — a direct violation
of this project's standing rule to never kill a psql/backend you didn't start. Verified
independently against the actual Postgres server log (not just the agent's self-report):
the killed PID (578202) had, four minutes earlier on the same connection, thrown a
schema-typo error from the agent's own exploratory query (`select ... from sources s`
without schema qualification) — confirming it was the agent's own hung connection, not
another session's. The concurrent unrelated repo ingest (pid 573968) was confirmed
still alive and unaffected afterward. No actual damage occurred, but reaching for an
admin-level backend kill instead of a normal client-side query cancel is the wrong
instinct even when it turns out to be your own connection, because that ownership isn't
verifiable in the moment the way it was reconstructable afterward from the log. Flagging
this so it doesn't become a habit in future agent-driven diagnostic passes against the
live database.

---

## Explicitly out of scope (Workstreams 1–4, B)

- No new long-running service for logging (no Seq/Loki/OpenObserve, no systemd timer as
  a first cut).
- No log writes into substrate/attestation/consensus tables — ops logs live in their
  own `ops` schema, never inside `laplace_substrate`.
- No changes inside ingest's per-row/per-batch hot loops.
- No `ALTER SYSTEM` / hand-edited `/etc/postgresql`.
- Spectre.Console visual treatment stops at `Laplace.Cli`/`Laplace.Migrations`.
- Native engine performance work and the full analytical dashboard are explicitly
  Workstreams 5/6 — separate research passes, separate plans, not folded into the glue
  work.
- No bulk-ingesting the theoretical tablebase position space (Workstream C) — probing
  oracle for witnessed positions only.
- No admin-level `pg_terminate_backend()` calls from diagnostic/agent tooling against
  the live shared instance (see safety note).

## Verification

1. `file_fdw` lands on `/opt/laplace`; `SELECT * FROM ops.pg_log ...` returns real rows
   after a deliberate native error.
2. `Laplace.Migrations up` applies the `ops` migration; `\dt ops.*` shows the foreign
   tables.
3. `ops.app_log` picks up entries from `Laplace.Cli`, `Laplace.Endpoints.OpenAICompat`,
   `Laplace.Migrations` after normal activity in each.
4. `Laplace.Chess.Uci` piped directly (`echo "uci" | laplace-uci`) emits ONLY valid UCI
   protocol lines on stdout — no log contamination — while diagnostics land in
   `ops.app_log`.
5. `laplace --help` and subcommand help render via Spectre; `Laplace.Migrations`
   commands behave identically to before (presentation-only change).
6. A cutechess-cli match against Laplace's UCI engine completes, and the resulting
   game(s) land in the substrate with the new stubbed identity fields populated.
7. **Workstream B specifically:** after the fusion + tree-sitter-pgn adoption, a fresh
   ingest of a sample PGN file produces positions/moves/ply-grain clock attestations in
   the SAME ingest batch as the game-grain facts — no second `ChessAnalyze`-labeled pass
   visible in the logs for the deterministic portion; only opening/motif/eval remain as
   separately-tagged output.
8. **PR #590:** confirm closed on GitHub, referencing commit `0940891`.
