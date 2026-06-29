# Laplace — operating manual (read this before building/running anything)

Windows host. Toolchain: **Intel oneAPI `icx`/`icpx`**, Ninja, CMake (VS 2026 bundled), Windows SDK
`rc`/`mt`, **PostgreSQL 18**, .NET 10. Every `.cmd` lives in `scripts\win\` and sources `env.cmd` first.

## env.cmd (sourced by every script)
Sets `PGBIN` (PG18), prepends `build-win\{core,dynamics,synthesis}` + Intel oneAPI + VS to `PATH`,
and: `INGEST=D:\Data\Ingest`, `REPOS=D:\Repositories`, `LAPLACE_MODEL_HUB=D:\Models\hub`,
`LAPLACE_DB=Host=localhost;...;Database=laplace`. Always run engine/ingest commands **through a script
that sources env.cmd** (or source it yourself) — otherwise the native DLLs won't resolve.

## Build & deploy — use the scripts, never hand-copy artifacts
- **Full clean/rebuild/redeploy:** `scripts\win\rebuild-all.cmd`
  (Phase 1 clean → 2 codegen → 3 engine → 4 extensions → 5 **deploy/install** → 6 app → 7 perfcache).
  Linux CI / self-hosted runner: `scripts/pipeline.sh --mode hot|fresh|build-only`.
  Flags: `--skip-clean`, `--skip-app`.
- **Engine only:** `build-engine.cmd [--clean-first|--reconfigure] [targets]` → `build-win\` (Release, icx):
  `laplace_core.dll`, `laplace_dynamics.dll`, `laplace_synthesis.dll`.
- **Extensions only:** `build-extensions.cmd` → `build-win-ext\`: `laplace_geom.dll`, `laplace_substrate.dll`.
- **App:** `dotnet build app\Laplace.slnx -c Release`. Output: `app\Laplace.Cli\bin\Release\net10.0`.
- **Deploy:** `install-extensions.cmd [--recycle]` — copies engine+extension DLLs into the **custom
  library path `D:\Data\Postgres\laplace\lib`** via a `swapcopy` HOT-SWAP (renames a locked `.dll` to
  `.stale~<rand>`, drops the new one; backends pick it up on reconnect), generates SQL into
  `...\share\extension`, and sets `dynamic_library_path` / `extension_control_path`.
- **ASAN engine:** `build-engine-asan.cmd [targets]` → `build-win-asan\` (RelWithDebInfo, has `.pdb`).

## Where the engine actually loads from (this matters for crash repro)
- **Postgres backends** (server-side: the `laplace_substrate` extension, e.g. the OpenSubtitles ingest):
  load `laplace_substrate.dll` + `laplace_core.dll` from `D:\Data\Postgres\laplace\lib` via the DB GUC
  `dynamic_library_path = $libdir;D:/Data/Postgres/laplace/lib`. Query it: `SHOW dynamic_library_path;`.
- **.NET CLI** (client-side: omw/wiktionary/tatoeba ingest): `[LibraryImport("laplace_core")]` default
  resolver → app `bin` then `PATH`. **Do NOT hand-copy DLLs into `bin`** to force a different build —
  the loader prefers `bin` over PATH, and poking installed artifacts is wrong. For native crash repro
  use the **standalone native ASAN test** `build-win-asan\core\tests\laplace_core_tests.exe` (fully
  native — the .NET CLR swallows `0xC0000005` as "Fatal error" before ASAN can report).

## Database
PG18 cluster at `D:\Data\Postgres`, port 5432, db **`laplace`** (Win service `postgresql-x64-18`;
restart via Services, not `pg_ctl`). `db-reset.cmd [--recycle]`: terminate laplace backends → DROP +
`createdb laplace` → `CREATE EXTENSION postgis, laplace_geom, laplace_substrate` (regenerates the SQL
from `extension\laplace_substrate\sql\*.sql.in` and deploys) → `substrate_health()`. A pgAdmin/pgAgent
session may be live on this cluster and has dropped `laplace` mid-work before — check before blaming a pipeline.

## Seeding
`seed-step.cmd <step> [path]` (one decomposer) or `seed-stage.cmd <stage>`. Data under `D:\Data\Ingest`.
Runs `dotnet run --project Laplace.Cli ... -c Release --no-build -- ingest <step> [path]` (needs a prior
Release build). Granular dependency order: unicode, iso639, cili, wordnet, verbnet, propbank, framenet,
mapnet, wordframenet, semlink, ud, document, chess (chess = `ingest chess D:\Data\Ingest\Games\Chess`).

## Chess engine, gauntlet & substrate fusion
Full backlog + priorities: **`docs\chess-engine-roadmap-2026-06-27.md`** (read this for the chess side).
- **Layers:** `Laplace.Modality.Chess` = pure rules + the **classical engine** (`Evaluation` PeSTO/toggleable
  `EvalTerm` overlays, `Search` α-β/quiescence/TT/time-mgmt, `MatchRunner`, `IRootBias`); `Laplace.Chess.Uci` →
  **`laplace-uci.exe`** (UCI, no DB, fast startup); `Laplace.Chess.Service` = substrate bridge (`SubstrateRootBias`,
  `ChessGraph`, decomposers). The engine is **~2105 Elo** (beat balanced Stockfish `UCI_Elo=2000` +105±31/500g).
- **CLI:** `laplace chess <move|selfplay|fetch|substrate-test>`; `laplace ingest <chess|openings> <path>`.
  `substrate-test` = guided(substrate root prior) vs pure classical, parallel (`--concurrency`, 8P+16E box).
- **Gauntlet (our own, not a dependency):** `scripts\win\build-cutechess.cmd` builds `external\cutechess`
  (submodule) → `build-cutechess\cutechess-cli.exe` (Qt6.8.3 fetched prebuilt via aqtinstall → `D:\Qt`). Run vs
  `D:\stockfish\...avx2.exe`. cutechess needs `D:\Qt\6.8.3\msvc2022_64\bin` on PATH + a `tc=` (`tc=inf depth=N`).
- **Tests:** chess suites are in `test-app.cmd` (Modality.Chess / Chess.Service / Chess.Uci).

## Gotchas
- Shells (Bash + PowerShell tools) **share a working directory** — a `cd` in one affects the other; use
  absolute paths or `Set-Location D:\Repositories\Laplace` first. A relative `scripts\win\...` from the
  wrong cwd fails with "system cannot find the path specified".
- `.cmd` files written by tooling default to **LF**; cmd.exe needs **CRLF** or it mis-tokenizes every line.
- **`ChessCompose` native compose is NOT thread-safe** (AccessViolation under parallel) — serialize it.
- Never put a `C:\Program Files` (or other system) path in the same script as `Remove-Item`/destructive
  ops — the harness guard blocks the whole command and reports it as "Remove-Item on system path C:\Program".
- ASAN-instrumented core won't report inside the .NET CLI (CLR SEH). Use the native test exe.

---

## Rules for AI agents — stop drifting from the invention

### Identity & tier (most commonly violated)
- `id = blake3(content)`. Tier = `max(child)+1`, emergent from composition, **never stamped, never a
  fixed integer category, never a vocabulary enum**. The text ladder 0–4 is UAX29 only; every modality
  composes its own depth. Hardcoding `Vocabulary = 5` or any tier-as-category is a bug.
- **Two orthogonal axes — do not collapse them.** (1) Compositional / geometric: tier, coord, radius,
  Hilbert — built bottom-up by `grammar_compose`. (2) Referential / semantic: typed attestation edges
  (IS_A, HAS_POS, HAS_SENSE, PRECEDES…) linking nodes top-down via Glicko-2 consensus. Meaning is the
  fold, not the coordinates.
- Meta vocabulary (POS, RelationType, Language, ILI/synset) is **distinct from its name-as-text**. A
  concept node and the word "noun" are different entities with different ids. `type_id + physicality +
  trust/source` carry the distinction — never tier, never merge meta into content.
- Content-addressing is non-negotiable: `OfCanonical("language:iso3")` hashes a language as a stable
  namespace key (correct — a language is not text). Position identity (chess) = Merkle over ALL surface
  tokens. Any surface change orphans the ingested seed; that is the re-key decision gate.

### Ingest write-path (the bulk of past agent mistakes)
- **Stage 1** (tree-sitter / grammar parse) → streams raw records. **Stage 2** (hash → descent →
  Hilbert-sorted bulk append → Glicko fold) is pure native engine. They are **separate**. Stage 1 is
  out of the loop once records emit. Never route structured content (chess positions, code, SCADA) back
  through the UAX29 text composer — it was the root of the chess row-explosion and AccessViolation crash.
- **Descent is the only existence check** — once, top-down on the reduced T2+ trunk set, before the
  insert. The insert looks up nothing. No `ON CONFLICT`, no per-row anti-join at insert time. If
  `ON CONFLICT` fires at scale, the descent was skipped — that is the bug to fix.
- Bulk append is ordered by `hilbert_index` → sequential B-tree / GiST maintenance → no page-split cliff.
  **Never drop indexes**: they are the dedup probe and every read path. "Indexes are expensive" is the
  random-order half-truth; sequential-order maintenance is cheap.
- Descent gates the **compute** too: a present top trunk ⟹ skip decomposition entirely, not just the
  insert. Dedup key for physicalities is the **BLAKE3 id only** — there is no `(entity_id, type)` unique
  constraint, and adding one is a phantom check against a non-existent constraint.

### Measurement discipline
- No perf number enters code, memory, or a report without a committed, re-runnable script + exact output
  + hardware (i9-14900KS, 48 GB, RAID-0 NVMe, PG18 native Windows). Training knowledge ("B-trees are
  normally slow on random keys") is a hypothesis — verify against the actual workload.
- Size runs to the claim: 24 games ≠ a 500-game Elo distribution. One hot batch ≠ a population mean.
- Elo anchors: always `UCI_LimitStrength=true UCI_Elo=X` balanced Stockfish. Depth-limited Stockfish is
  positionally superhuman and tactically blind — a bad anchor.

### Chess substrate
- Raw `MOVE`-edge `eff_mu` ≈ popularity, not strength (spurious-correlation trap; raw test came back
  null, −17). The fair fusion test is substructure-fold over opening-seeded positions.
- `ChessCompose` native compose is NOT thread-safe — serialize all calls through the static gate.

### Foundry synthesis
- Reconcile the rank band window `[0.30–0.86]` in `FoundryCommands.cs` with `engine/manifest/
  relation_types.toml` whenever ranks change. After recalibration IS_A=0.90 and PRECEDES=0.18 both
  fell outside the window silently, excluding taxonomy's strongest edges and the sequential edge —
  that killed the embedding geometry. The build-a-bear path is the designed-for-coherence path.

### Design authority — stop here and ask Anthony
Open decisions that are **not** for an AI agent to resolve unilaterally:
- Meta-node identity (content-addressed-but-distinct vs. namespaced scheme vs. other).
- Re-key for feature enrichment (one-time re-ingest; orphans the seed — the biggest architectural fork).
- Lichess go-live (outward-facing public games; token in `deploy\secrets\lichess.env`).
- Raw engine-strength climb direction (pursue, or leave at ~2105 and let the substrate be the differentiator).
- Trust-class structure changes, any source-level re-seeding that would double-count testimony.

Do not scope-reduce, offer partial deliveries, or fork the plan without explicit direction.

### Tests & tooling
- Run tests via `scripts\win\test-app.cmd`, **never** `dotnet test` directly (requires oneAPI, perfcache,
  and PG env that env.cmd sets up).
- Unit-test each piece **before** running a live ingest event. Isolate → prove → chain. The SVD row-
  explosion bug should have been a unit test, not discovered inside a 2 GB ingest run.
