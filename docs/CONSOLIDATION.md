# State of the ETL — working document

Measured 2026-07-31 on **hart-server** (this box is the self-hosted runner) against the
live `laplace` database and the GitHub Actions history.

---

## 0. Retracted — an earlier version of this document was wrong

The first version of this file concluded, from a local database that had been reseeded
seven hours earlier, that most of the ETL had never run. Every one of those conclusions
was false. Recording them so they are not repeated:

| Claim I made | Actual |
|---|---|
| "Chess has never been ingested" | `Seed — chess`: **36 runs, 31 success**, since 07-13. One ran 07-30 01:18→03:44 (2h26m). |
| "8 designed stages that no automation ever invokes" | `Seed — knowledge` covers exactly those: `omw, conceptnet, atomic2020, ud, wiktionary, tatoeba, opensubtitles` — **55 runs, 31 success**, since 07-13. `Seed — documents` covers `document` — **14 runs, 11 success**. |
| "`pipeline.sh` has no ingest phase, so ingest never runs" | Ingest is not a pipeline phase by design. It is **six dedicated workflows** over a reusable core (`_ingest.yml`). Reading `pipeline.sh` and `ensure-foundation.sh` alone told me nothing about ingest. |
| "10 of 51 decomposers are reachable from automation" | The seed dropdowns reach the knowledge 7, `document`, `chess/openings/chess-books/chess-eval`, `code/repo/stack/tiny-codes/tabular/recipe`, and `model`. |
| "14 months of reseeds re-derived the same lexical floor" | Invented. The run journal lives in the database; the database had been reseeded that morning. An empty journal is evidence about the journal, not about history. |

The method error: I treated one machine's freshly-reseeded database as the system's
history, and never opened CI — which is where the durable record actually is.

---

## 1. The ingest architecture (verified)

Seven workflows in `.github/workflows/`:

| Workflow | Sources it dispatches | Runs | Success | Fail | Cancelled | Span |
|---|---|---|---|---|---|---|
| `_ingest.yml` | reusable core: ingest → idempotency → gates | — | — | — | — | — |
| `Seed — foundation` | unicode, iso639, cili, wordnet, verbnet, propbank, framenet, mapnet, wordframenet, semlink, `ladder` | 19 | 16 | 0 | 3 | 07-25 → 07-31 |
| `Seed — knowledge` | omw, conceptnet, atomic2020, ud, wiktionary, tatoeba, opensubtitles | 55 | 31 | 6 | 18 | 07-13 → 07-30 |
| `Seed — documents` | document | 14 | 11 | 2 | 1 | 07-13 → 07-31 |
| `Seed — chess` | chess, openings, chess-books, chess-eval | 36 | 31 | 2 | 3 | 07-13 → 07-30 |
| `Seed — code` | code, repo, stack, tiny-codes, tabular, recipe | 2 | **0** | 0 | 2 | 07-16 → 07-17 |
| `Seed — models` | model (safetensors) | 2 | **0** | 2 | 0 | 07-18 → 07-23 |

One runner: `hart-server`, labels `self-hosted, Linux, X64, laplace, oneapi, postgres-18,
dotnet-10, avx2`.

**The ETL works.** Foundation, knowledge, documents, and chess all run to green
repeatedly, including multi-hour passes.

---

## 2. Model ingestion: 2 attempts, 0 successes, last tried 8 days ago

This is the direct answer to "we can't even try model ingestion."

**Run 29986122984 — 2026-07-23 06:46, failed in 0.2 s** in the first job:

```
invalid safetensor snapshot: missing config.json — safetensors are not self-contained
(unlike GGUF); architecture recipe lives beside the weight blobs
path: /data/models/models--sentence-transformers--all-MiniLM-L6-v2
exit code 2
```

The path passed is a **HuggingFace cache root**, not a snapshot dir. Under that layout
the weights and `config.json` live at `<dir>/snapshots/<revision>/`.

The tree already knows this — and disagrees with itself:

- `app/Laplace.Cli/BenchCommands.cs:93` and `:115` — `Path.Combine(fam, "snapshots")`
- `app/Laplace.Decomposers/Model/ModelDecomposer.cs:36` — `if (s == "snapshots") continue;`
- `app/Laplace.Decomposers.Tests/Model/ModelGateFactorReadbackTests.cs:22` and
  `FactorLens4dTests.cs:19` point at the full
  `…\models--sentence-transformers--all-MiniLM-L6-v2\snapshots\c9745ed…` path
- `app/Laplace.Substrate/Abstractions/SafetensorSnapshotWitness.cs:21` — the validator
  that failed — checks `config.json` **in the directory it is handed** and does not
  descend into `snapshots/<rev>/`

So the bench path resolves HF cache layout and the ingest path does not.

**Run 29625735660 — 2026-07-18 01:43, failed in <60 s** at checkout/setup; the ingest
step never executed. Different cause, not diagnosed here.

The header comment in `seed-models.yml:11-16` says the intended processor is
hart-desktop, unregistered as a runner, and that the workflow "has no runner and will not
start — expected." That is stale: hart-server carries the `laplace` label, so the job
*does* start, and then fails on the above. The comment describes a condition that no
longer holds, which is why the failure reads as expected when it isn't.

`Seed — code` is in a similar state: 2 dispatches, both cancelled, nothing since 07-17.
It has never completed a run.

---

## 3. Roughly five hours of successful seed work is not in the database

Journal (`laplace.ingest_runs(500)`) holds **11 rows, all `ok`, all 2026-07-31
01:17–01:46** — the `Seed — foundation` run (01:17→01:31, 10 sources) and the
`Seed — documents` run (01:38→01:46, the `UserPrompt`/document row).

Not present, despite green CI:

- `Seed — knowledge`, **07-30 17:06→19:37, success, 2h31m** — no rows.
- `Seed — chess`, **07-30 01:18→03:44, success, 2h26m** — and
  `select relname from pg_stat_user_tables where relname ~ 'chess' and n_live_tup > 0`
  returns **nothing**.

Between those and now: seven `Laplace — build, deploy, test` runs (07-31 00:07 through
01:09) and the foundation seed at 01:17.

**I have not established what removed them.** `seed-foundation.yml` drops secondary
*indexes*, not data. `grep -nE 'dropdb|createdb|DROP DATABASE|CREATE DATABASE'` over
`laplace.yml` and `pipeline.sh` returns nothing, so `CLAUDE.md`'s claim that "CI
recreates the database empty" is itself unverified. Naming a culprit here would repeat
the error in §0.

What is established: **multi-hour seeds report success, their rows are later gone, and
nothing anywhere reports the loss.**

## 3.1 The success signal cannot see completeness

From the same journal, `files_done`/`files_total`:

`66/0` · `128/0` · `44/10` · `12/2` · and **FrameNet `33/14900` — status `ok`**.

`files_done` exceeds `files_total` in seven of eleven rows. This is a live defect,
independent of everything above: `status = ok` is not backed by any coherent measure of
whether the input was consumed.

---

## 3.5 Throughput — measured, and it is not what two earlier drafts of this section said

**Corrected finding: there is no chess throughput defect. Chess writes 31,872 rows/s
against WordNet's 39,071 — mid-pack among all sources. It takes ~50 minutes because it
emits 96,684,814 rows from a 134 MB file: 15× the rows-per-byte of WordNet.**

Two earlier drafts claimed (a) chess is ≥9× slower per MB, and (b) the existence probe
explains it. (a) compared WordNet's *whole-run* duration against chess's *batch-only*
time, and used per-MB as the unit when fan-out differs 15×. (b) assumed WordNet's probe
was near-zero; it is 32%. Both are retracted.

### Rows per second — the like-for-like unit

`Seed — foundation` 30595928217 and `Seed — chess` 30505261034, summed `INGEST_BATCH`:

| Source | Rows | Batch s | **Rows/s** |
|---|---|---|---|
| Unicode | 6,088,480 | 33.7 | 180,474 |
| ISO639 | 69,404 | 1.4 | 48,739 |
| SemLink | 118,953 | 2.5 | 48,493 |
| **WordNet** | 2,349,310 | 60.1 | **39,071** |
| **ChessPgn** | **96,684,814** | **3,033.5** | **31,872** |
| WordFrameNet | 58,706 | 2.3 | 26,034 |
| FrameNet | 719,912 | 33.9 | 21,208 |
| PropBank | 139,471 | 9.6 | 14,518 |
| CILI | 1,378,442 | 108.4 | 12,712 |
| MapNet | 14,142 | 1.1 | 12,297 |
| VerbNet | 25,238 | 4.0 | 6,322 |

Chess sits mid-pack — faster than CILI, FrameNet, PropBank, VerbNet and MapNet.

### Where the 50 minutes actually goes: fan-out

| | WordNet | ChessPgn |
|---|---|---|
| Input | 49 MB | 134 MB |
| Rows emitted | 2,349,310 | 96,684,814 |
| **Rows per MB** | 47,945 | **721,528 (15.0×)** |

190,705 games → **507 rows per game**, and that is not a design question — it is a missing
tier-0 floor. See §3.6.

### 3.6 The fan-out is undeduplicated: chess has no vocabulary floor

Summed `rows_new` over the run: **24,023,954 entities + 23,579,228 physicalities +
124,238,708 attestations**. Per game: **126 new entities, 651 new attestations.**

Chess is a closed vocabulary — 64 squares, 12 piece types, a bounded legal-move encoding,
castling/EP state. Under content-addressed identity, new-entity rate must *saturate*:
by game 190,705 almost every position and move already exists, and a game should
contribute a trajectory plus attestations against pre-existing ids.

Measured marginal rate, `INGEST_PROGRESS` deltas:

| Position in run | New rows per game |
|---|---|
| game ~80,000 | 429 |
| game ~190,705 | **552** |

**It rises.** A closed vocabulary cannot do that. Ids are being minted per occurrence,
not resolved against shared content.

The mechanism that would prevent this exists and is proven — for Unicode. From the same
chess run's log:

```
WS_APPLY tier-0 gate ON: unicode L0 layer-complete marker present
  — tier-0 entity ids answer presence client-side, zero probes
```

Unicode has a seeded L0 floor plus `laplace_t0_perfcache.bin`, so its ids resolve
client-side with **zero probes**. Chess has neither, so every position and move is probed
against the database (§3.5, 49% of batch time) and then minted.

**There is no chess perfcache.** Two blobs exist: `laplace_t0_perfcache.bin` and
`laplace_highway_perfcache.bin`. `app/Laplace.Chess/Service/ChessVocabularyCache.cs` is
68 lines whose own doc comment (line 11) reads *"exactly the shape laplace_t0_perfcache
exists for at the codepoint"* — a placeholder naming the missing artifact, not the
artifact.

**#547 is OPEN**: *"chess: game-tier mantissa-packed trajectory is not wired — EmitGame
deposits a bare Document entity."* The trajectory representation is not built.
(#736 — provenance salted into the content hash — is CLOSED; that was a different half.)

Expected shape versus delivered:

| | Should be | Is |
|---|---|---|
| Game representation | 1 trajectory, points = moves | bare Document entity (#547 open) |
| Move/position ids | resolved against a seeded closed vocabulary, zero probes | minted per occurrence, DB-probed |
| New entities per game | → 0 as the vocabulary saturates | 126, and rising |
| Entities, 190,705 games | bounded vocabulary + 190,705 trajectories | 24,023,954 |

### The probe is a real tax, and it is not chess-specific

Probe share of batch time, from the same two runs:

| Source | Probe s | Batch s | Probe % |
|---|---|---|---|
| MapNet | 0.8 | 1.1 | 71% |
| **ChessPgn** | ~1,487 | 3,033.5 | **49%** |
| WordFrameNet | 1.0 | 2.3 | 44% |
| FrameNet | 14.2 | 33.9 | 42% |
| CILI | 41.6 | 108.4 | 38% |
| ISO639 | 0.5 | 1.4 | 37% |
| **WordNet** | 19.5 | 60.1 | **32%** |
| PropBank | 2.8 | 9.6 | 29% |
| SemLink | 0.7 | 2.5 | 29% |
| Unicode | 8.0 | 33.7 | 24% |
| VerbNet | 0.8 | 4.0 | 20% |

Every source pays 20–71%. Deleting it (**#429**, open, already specifies exactly this) is
a genuine ~1.5–2× across the whole ladder. It does **not** close any chess-vs-lexical gap,
because no such gap exists in rows/s.

It also grows with substrate size — within the single chess file, mean probe rose from
18.3 s (first 10 batches) to 26.3 s (last 10), 1.4×. Within CILI, 1,054 ms → 4,227 ms.
So each source ingested makes every later source slower, and each ladder re-run is slower
than the last.

### On genericity

The write spine *is* shared: chess routes through the same `NpgsqlSubstrateWriter` and
`ConsensusAccumulatingWriter` as every lexical source, and
`NpgsqlCommand|ExecuteReader|INSERT INTO` across `app/Laplace.Decomposers` returns
**zero** — the pure-decomposer rule holds, and chess's rows/s confirms it uses the same
path at the same speed.

Above the spine, nothing is shared:

- `app/Laplace.Chess/` is **14,050 lines** in its own project, outside
  `Laplace.Decomposers` entirely. All 18 per-source decomposer directories combined are
  ~14,700 lines.
- 17 `*Decomposer` + 15 `*Source` classes — one pair per source.
- Only 4 `*IngestAdapter` and 4 `*GrammarWitness`.

### Superseded draft below (kept so the error is not re-derived)

## 3.5-OLD Throughput: half of every ingest is the database existence probe

Measured from `Seed — chess` run 30505261034 (07-30 01:18→03:44), ingesting
`LumbrasGigaBase_OTB_1950-1969.pgn` — **134 MB** (140,637,097 bytes), 190,705 games,
one file. OMW is 245 MB, so this file is 55% of OMW's size, as the operator stated.

| | WordNet | This chess file |
|---|---|---|
| Input | 49 MB | 134 MB (2.7×) |
| Time | 122 s (full run, from the journal) | **3,036 s of measured batch time alone** |
| Per MB | 2.5 s/MB | **≥ 22.7 s/MB** |

**≥ 9.1× slower per megabyte.** The comparison is conservative — WordNet's 122 s is its
*entire* run, chess's 3,036 s counts only summed `INGEST_BATCH elapsed_ms` and excludes
parse and compose.

### Where it goes

58 batches, mean 52.3 s each. Summed:

- **ID existence probe: 24.9 min — 49% of all batch time**
- COPY entities / physicalities / attestations: the remainder

A representative batch (`WS_APPLY`, 01:19:43):

```
verify: 265,461e+258,599p+1,072,973a ids probed in 16,731ms
        (present: 9,816e/9,831p/0a)
copy entities:      255,645 rows in  3,226ms
copy physicalities: 248,768 rows in 10,180ms
copy attestations: 1,072,973 rows in  7,479ms
INGEST_BATCH rows=1,655,287 elapsed_ms=40,795
```

16.7 s asking the database which of 265,461 entity ids it already has. Answer: 9,816 —
**3.7%.** The other 96.3% of the probe confirmed novelty that was already determined.

### It scales with the substrate, not the input

Within this single file, probe cost per batch grows:

| | mean probe |
|---|---|
| first 10 batches | 18.3 s |
| last 10 batches | **26.3 s** |

**1.4× growth across one 134 MB file.** The probe is a function of how much is already
in the substrate, so every source ingested makes every later source slower, and every
re-run of the ladder is slower than the last. That is the mechanism behind "inexcusably
slow processes I have to repeat over and over."

### The implementation contradicts the documented law

`CLAUDE.md` states the fixed ingest order:

> unpack → records → **client-side dedup across the working set → client-side
> accumulation** → one bulk tier descent → COPY of **proven-novel** rows

There is no database existence probe in that sequence. Rows are supposed to be proven
novel client-side before COPY. The implementation asks the database instead, per batch,
against a growing substrate. **#429 — "client-dedup + ON CONFLICT offload — drop the DB
existence probe" — is open and describes exactly this deletion.**

### What this says about genericity

The write spine *is* shared: chess routes through the same `NpgsqlSubstrateWriter` and
`ConsensusAccumulatingWriter` as every lexical source, and a grep for
`NpgsqlCommand|ExecuteReader|INSERT INTO` across `app/Laplace.Decomposers` returns
**zero** — the pure-decomposer rule holds.

That makes it worse, not better: **the slow thing is the shared part.** Every source pays
the 49% probe tax, and no per-source fix reduces it.

Above the spine, nothing is shared:

- `app/Laplace.Chess/` is **14,050 lines** in its own project, outside
  `Laplace.Decomposers` entirely. All 18 per-source decomposer directories combined are
  ~14,700 lines.
- 17 `*Decomposer` + 15 `*Source` classes — one pair per source.
- Only 4 `*IngestAdapter` and 4 `*GrammarWitness` — the shared-role abstractions are used
  by a minority of sources.

---

## 3.7 PGN is not one format, and the parser was proven against the easiest dialect

Tag vocabularies extracted from the corpora on disk:

| | Lumbras (18 GB) | TWIC (242 MB) | chess.com (28 MB) |
|---|---|---|---|
| `[Variant]` | **none** | **none** | Chess960 ×311, 3-check ×2 |
| `[FEN]` / `[SetUp]` | **none** | **none** | 318 |
| Clock comments | **none** | **none** | `%clk` ×840,706 |
| Distinct tags | 19 | 20 | 25 |
| Source-specific tags | `BlackFideId`, `ImportDate`, `Merged`, `PlyCount`, `SourceQuality` | `BlackTeam`, `EventType`, `Opening`, `Variation` | `CurrentPosition`, `ECOUrl`, `Link`, `Termination`, `Timezone`, `Tournament`, `UTCDate`, `StartTime`, `EndTime` |

**The only corpus ever ingested at scale — Lumbras, 190,705 games — is the one with no
variants, no FEN tags, and no clocks.** chess.com carries all three.

### Verified defects on the chess.com dialect

**1. Chess960 castling is silently discarded.** `app/Laplace.Chess/Modality/Board.cs:96-105`:

```csharp
b.Castle |= c switch {
    'K' => CastleRights.WhiteKing, 'Q' => CastleRights.WhiteQueen,
    'k' => CastleRights.BlackKing, 'q' => CastleRights.BlackQueen,
    _   => CastleRights.None,          // <-- X-FEN file letters land here
};
```

Chess960 uses Shredder/X-FEN file-letter castling — the file holds `GBgb`, `GDgd`,
`GCgc`. Every one maps to `CastleRights.None`. **311 games replay with castling rights
silently zeroed.** No exception, no warning — the worst failure mode, because it produces
wrong positions that ingest cleanly.

**2. Hard numeric parses with no guard.** `Board.cs:108-109`:

```csharp
b.HalfmoveClock  = parts.Length > 4 ? int.Parse(parts[4]) : 0;
b.FullmoveNumber = parts.Length > 5 ? int.Parse(parts[5]) : 1;
```

and `app/Laplace.Chess/Service/PgnClocks.cs:29-30`:

```csharp
outv[i] = int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60
        + double.Parse(parts[2], CultureInfo.InvariantCulture);
```

No `TryParse`, no try/catch. Any malformed field aborts the whole run — the same
crash-instead-of-skip class as **#596** for `document`.

**3. Seven-field FEN is unmodelled.** The two 3-check games carry
`rnbqkbnr/… w KQkq - 0 1 +0+0` — a check-counter field the parser does not know.

**4. Clocks are all-or-nothing.** `PgnClocks.ClockTokens` returns `null` unless
`ms.Count == moveCount` exactly, so one missing `[%clk]` in a game silently drops the
entire clock series for that game.

**5. `exists=False` on a file that exists.** The operator's log shows
`INGEST_PATH … ecosystem_path=…/MagnusCarlsen_chesscom.pgn exists=False`, immediately
followed by `INGEST_START … input_units=9417 files=1`. The path probe is reporting false
for a file that is then read successfully.

### The regression mechanism

`92adf32` (2026-07-30, "think-time lenses") added `%clk` parsing — a comment dialect that
appears in **zero** Lumbras and **zero** TWIC games. The feature was built against a
source the ingest path had never processed at scale, and its parse sites (defect 2) are
unguarded.

**Not established:** which of these throws the reported
`FormatException … near offset 53. Expected an ASCII digit`. Offset 53 is the digit `0`
in all 318 FEN tags in the file, so the FEN tag strings do not obviously account for it.
Settling it needs a stack trace, not more inspection.

---

## 4. Open questions — ordered, and none of them assume an answer

| # | Question | How to settle it |
|---|---|---|
| **Q1** | What removes seeded rows between a green seed and now? | Re-run `Seed — knowledge` for one small source (`omw`, 245 MB), record the journal row, then run the main pipeline and re-check. Binary answer in ~30 min. |
| **Q2** | Does fixing HF-cache-root resolution make `Seed — models` reach job 2? | Point `SafetensorSnapshotWitness` at `snapshots/<rev>/` the way `BenchCommands.cs:93` does, re-dispatch. The failure is 0.2 s, so the loop is fast. |
| **Q3** | Why did `Seed — models` 07-18 fail before the ingest step? | Not yet diagnosed. Separate from Q2. |
| **Q4** | Why has `Seed — code` never completed a run since 07-17? | 2 dispatches, both cancelled — check whether it was abandoned deliberately. |
| **Q5** | Do `files_done`/`files_total` have a correct source, or is the counter wiring wrong end-to-end? | Trace the two fields from decomposer to `ingest_run_journal`. |

Q1 gates everything else: if seeded rows do not survive, no seed result can be trusted,
including any fix to Q2.

---

## Appendix — documentation corpus (secondary, unaffected by the above)

- **`docs/INVENTION.md` (487 ln, untracked, new 2026-07-31) vs `docs/INVENTIONS.md`
  (283 ln, tracked)** — one character apart, same subject, neither references the other.
- **Number collisions between `docs/specs/` and `.scratchpad/`**: `33`, `34`, `36`, `37`
  each name two different documents. `docs/INDEX.md:83-85` records this was already fixed
  once on 2026-07-18 (`22→32`, `27→27a-d`); it recurred 13 days later.
- `CLAUDE.md` documents `SELECT * FROM api('<substring>')`. It fails as written — the
  function is `laplace.api()` and `laplace` is not on the default `search_path`.
- `/vault/.claude` is 411 MB / 3966 files, of which 21 carry durable value (`CLAUDE.md`,
  `settings.json`, one output style, 19 plans). No `agents/`, `commands/`, or `skills/`
  directory — there is no Windows config to port.
