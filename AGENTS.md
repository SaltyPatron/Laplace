# Laplace — Agent Core

Laplace is a complete reinvention of the transformer on commodity x86-64 CPUs + PostgreSQL 18 + PostGIS + Native C (`icx`, `-fp-model=precise`, `-ffp-contract=off`, SIMD/AVX/VNNI, CORE-MATH, Eigen/Spectra/MKL) with **zero GPU requirement**. Every digital modality — text, curated lexicons, source code, `.git` history, chess, images, audio, video, and conventional AI model weights — decomposes losslessly into **one universal, content-addressed BLAKE3 Merkle DAG** (`same content = same hash`) inside a bounded 4D geometric space ($S^3$ surface for Tier-0 Unicode atoms; exact integer centroids strictly inside the 4-ball for compositions), with IEEE-754 mantissa-packed trajectories, containment provenance, Glicko-2 consensus standing across witnesses, detached Personality Firmware + the Gödel OODA loop for generation, and CPU Mold-a-Model synthesis exporting clean `laplace-blake3-v1` GGUF/Safetensors checkpoints without gradient descent or retraining.

> [!IMPORTANT]
> Canonical source: `E:\Repositories\Laplace\Laplace-Operations\agents\` (edit there, then run `pwsh -File Laplace-Operations/agents/sync.ps1`). Deep byte-level layouts: skill `laplace-invention`.

---

## 0. Mandate, Compounding Modularity & Batch Execution

1. **Full owner authority & zero theater.** Every line of code, test, workflow, and doc in this workspace is agent-written under one sole inventor/architect. Act directly across all repos, branches, workflows, databases, and hosts (`HART-DESKTOP`, `hart-server`, `hart-prod`, `hart-cloud`). Never invent permission blocks, never hand shell commands back to the user, never fabricate human multi-month timelines, and **never commit status reports, audit diaries, or benchmark prose** as a substitute for working product functionality. Judge leftover agent WIP against the invention: finish it, fix it, or replace it cleanly.
2. **Compounding snap-in modularity — NEVER build vertical feature silos:**
   - Laplace is exponentially greater than the sum of its parts because its mechanisms **overlap in the same bytes**: packing 128-bit BLAKE3 child IDs (`43/43/42 + 28 spare`) + `M` (`run30/said3/outcome2/pos18`) into PostGIS `LineStringZM` mantissas turns battle-tested GIS storage, TOAST, binary `COPY`, GiST R-trees, and GIN `laplace_path_ops` into a **zero-edge-table sequence/containment/gap/continuation engine** whose unpacked vertices simultaneously form real 4D curves in the $S^3$ ball, rated by Glicko-2 `consensus` and attributed by Fisher information (`share_bp = 10000`).
   - **"Modular" means snap-in pieces colliding in one shared space, never isolated domain mini-engines:**
     - **Snap-in codecs/grammars + `.recipe`s:** Every corpus, encyclopedia, `.git` repo, LSP symbol dump, PGN archive, PNG/WAV stream, and Safetensors/GGUF model lands on the **exact same 16-byte BLAKE3 `lp_id`s** in the **same 4 tables** (`"King"` in Qwen == `"King"` in WordNet == `"King"` in Wikipedia == `"King"` in Chess == `"King"` in ASTs), automatically cross-bracing every other modality.
     - **Snap-in ROM perfcaches (`tier0.bin`, `tier0.flags`, `highway.bin`, `chess.bin`):** Memory-mapped identically on both client and server for zero-round-trip determinism.
     - **One C `Forward + Highway A* + Gödel` loop (`program.c`):** Answers conversation, constructs/repairs code, plays chess, and discovers bridges (`COMBINE`) — steered by detached **`.firmware`** files (`program`, `deductive`..`adversarial`, `chess`). Never fork separate `chess_*`, `code_*`, or `model_*` reasoning silos.
     - **One universal readout (`lp_realize` + `Mold-a-Model` + `share_bp = 10000`):** Emits UTF-8, ASTs, moves, media, or CPU-synthesized `laplace-blake3-v1` GGUF/Safetensors checkpoints from the compounded web.
3. **Execute in large horizontal batches — ZERO per-ticket waterfall ceremony:**
   - All active repos sit side-by-side in `E:\Repositories\Laplace` sharing `$LAPLACE_BUILD` (`D:\Libraries\build\Laplace`). Incremental `ninja` + `dotnet build` takes seconds.
   - **Do NOT run 28 separate CI pipelines, `components.lock.tsv` bumps, NuGet publishes, and full IIS deploys one ticket at a time.** Develop across all repos in an active **Batch** together (using parallel subagents on disjoint tracks), compile incrementally locally, and run **one** unified build, **one** in-place `laplace-ops deploy`, **one** live behavioral proof on real populated data, and **one** `components.lock.tsv` + `git push` to close the entire batch.
4. **Truth order & compaction-proof resume protocol:**
   1. **The Invention:** `Laplace-Wiki/Repositories/Laplace/docs/INVENTOR_RECORD.md` (verbatim inventor words), `INVENTION.md`, `INVENTIONS.md`, and **Decided** items in `Laplace-Wiki/Sequence/Conflicts.md` + `Sequence/1..29`.
   2. **Architecture & Boundaries:** `Laplace-Wiki/Architecture.md`, `Engineering/Boundaries.md`, `Engineering/Standards.md`.
   3. **Modular C Packages & Reference:** `Laplace-Native`, `Laplace-postgres`, `Laplace-Engine`, `Laplace-MCP`, `Laplace-UCI`, `Laplace-Operations`, `Laplace-Wiki/Reference/*`.
   4. **Converging Product App (`Laplace`):** Owns `/v1/*` OpenAI API, Web/Glome, Chess Lab, Foundry, Code player, Arena, and `app.*` auth/billing while deleting its pre-modular duplicate substrate (`one-substrate`). *(Other relocated files under `Laplace-Wiki/Repositories/*` are historical archives, never active plans; `Laplace-Refactor` is frozen — never read as current instructions or edit.)*
   - **On session start or after context compaction:** Check `git status` / `git log -n 3` across repos + open GitHub issues for the active Batch + live DB state (`laplace-code` `ingest_status` or `laplace status`). Where code contradicts the invention, fix the code — never rewrite specs to excuse drift. If `laplace-code` MCP has sparse data, fall back immediately to `rg` / `Select-String` — never stall.

---

## 1. Verified Baseline vs. Active 4-Batch Finish Program (`SaltyPatron`)

- **Verified on `HEAD` (DO NOT revert or re-implement):** `Modular 1–3` (`LaplaceNative` CMake package, `components.lock.tsv`, `LaplaceEngine` `reader.h`), `tier0.bin` ($S^3$) + `tier0.flags` + `highway.bin` + 64-bit `banks.tsv` masks, `lp_attest_series` Glicko-2 + bounded witness trust, Fisher `share_bp = 10000`, 4-table `Laplace-postgres` (`laplace.reconcile.sql` in-place updates), compiled Tree-sitter + `.recipe` VM + all 54 `recipes/order` declarations, single-pass `stage.lp_*` binary `COPY`, `lp_realize`, initial `src/program.c` / `src/firmware.c` / `src/model.c`, and canonical BLAKE3 chess identity / Syzygy / PGN pipeline.
- **Unvarnished gaps to finish Laplace 1.0 (execute as 4 Large Batches — never claim done until live behavioral proof passes):**
  1. **`Batch 1: Whole-Stack One-Substrate Collapse (Single C Core & Single laplace DB)`** (`10` issues, `1` deploy — `Laplace-Native#50,#51`, `Laplace-Engine#174`, `Laplace-MCP#54`, `Laplace#2058,#2059,#2060,#1537,#1450`, `Laplace-Operations#190`):
     - Export `Laplace::os`, `lp_buf_*`, `laplace/json.h` helpers, `Laplace::http` + MCP shell, unified `pg.c` connects, and generated `Laplace.Native.Interop` NuGet (`win-x64`+`linux-x64`).
     - **Client-Determinism Collapse (never port ~750 legacy SQL items 1-for-1):** Client runs deterministic C in-process via `Laplace.Native.Interop` over mmap'd ROMs (IDs, UAX #29, case/lexical variants, 4D centroids, Hilbert, mantissa pack/unpack, masks, Syzygy, Fréchet/DTW/EDR, Glicko-2/Fisher, `lp_realize`) with **zero DB round-trips**; PostgreSQL (`laplace` DB) stores only the 4 tables (`+ app.*`) and runs ~40 indexed binary probes.
     - Extract `Laplace.Hosting`/`Data`/`Mcp`, rebind Browse/Variants/Leaderboards + Glome fullscreen onto `laplace`, and **delete `extension/laplace_substrate` (323 `.sql.in`), `laplace_geom` (39 files), `engine/*` duplicates, `Laplace.Decomposers` (73 `.cs`), `WorkspaceCodeIndex`, all 20 hand-written P/Invoke files, and `laplace-mono` wholesale**.
  2. **`Batch 2: Populated Compounding World, Multi-Modal Recipes & Fast Compositional Forward/Gödel`** (`9` issues, `1` deploy — `Laplace-Prototype#3`, `Laplace#372,#1134,#1934`, `Laplace-Engine#31,#10`, `Laplace#1720,#1726,#1421`):
     - *Current reality:* `laplace` DB on desktop currently holds only `workspace-code`, and `program.c` makes ~45 DB trips acting like a nearest-sentence picker (letting `"How does ... ?"` overpower `"lightning"`).
     - Admit local `recipes/order` corpora into `laplace` (`Proofs 1–3`), add `Wikidata`/`Wikipedia`/`Natural Earth` recipes (`#372`), lossless PNG/WAV/video + Tatoeba audio + `.txt`/`.epub` edition convergence (`#1134`), and Recipe Studio (`#1934`).
     - Collapse `program.c` to 2–3 batched binary probes (`<100–200ms`), weight content obligations over stop-word frames, compose coherent multi-clause answers from attested claims + trajectories, add Highway A* + N-ary (`np>=4`) traversal (`#31`), all `.firmware` profiles + Gödel OODA (`BELIEF ≠ RISK ≠ ACTION`, `#10`), in-process `/v1/chat/completions` + proof drawer (`#1720,#1726`), and `COMBINE`/Arena (`#1421`).
  3. **`Batch 3: Model Assimilation, CPU Mold-a-Model, Structural Code & Chess Over Shared Forward`** (`4` epics across 3 parallel tracks, `1` deploy — `Laplace#1719`, `#894`, `#833`, `#939`):
     - *Models (`#1719`):* Shape-driven Safetensors + GGUF assimilation (`raw_weight_bytes_retained = 0`) + native CPU Mold-a-Model `laplace-blake3-v1` synthesis proved by assimilating local models and holding coherent conversations in `llama.cpp` with `share_bp = 10000` attribution.
     - *Code (`#894`):* Native `.git` Merkle DAG + compiler/LSP symbol witness + consequence-witnessed AST repair (`R0 -> R1` verified by compiler/tests).
     - *Chess (`#833,#939`):* Coarse native C search inside `Forward` (`chess.firmware`), conditioned OTB/books/Syzygy/deduplicated Stockfish witness, all 7 session/SSE/Lichess readback fixes, and `fastchess` SPRT Elo proof.
  4. **`Batch 4: Authority Scoping, Compute Envelopes, Patreon Royalties & 1.0 Live Cutover`** (`4` issues, `1` deploy — `Laplace#1708`, `#1709`, `Laplace-Operations#2`, `Laplace#1725`):
     - Pre-`COUPLE` Knowledge Authority Packages, Sybil write/feedback governance, & Red/Blue/Judge gates (`#1708`); native machine-cost Compute Envelopes, Patreon entitlement witnessing, & `app.billing_royalties` (`SUM(share_bp) = 10000` integer conservation, `#1709`); multi-host `criteria.json` (`Operations#2`) & 5-pillar live cutover across `HART-DESKTOP`, `hart-server`, and `hart-prod` (`#1725`). *(Post-1.0 backlog: `Laplace-Operations#189` `Salty-Suite` — never blocks 1.0.)*

---

## 2. Non-Negotiable Laws & Preempting AI Training-Data Reflexes

| Conventional AI / Software Reflex (FORBIDDEN) | Laplace Mechanism (REQUIRED) |
| :--- | :--- |
| Dense embeddings / cosine k-NN as "semantic similarity" | **Geometry is shape; attestations are meaning.** 4D `entity.coord` + Fréchet/DTW/EDR measure structural/orthographic shape (`ring`/`sing`/`King`, bug-trace shapes). Meaning comes strictly from attested strands, highway hops, and usages weighted by Glicko-2 `consensus`. |
| Text/code chunking (`MaxLines`, sliding windows, overlap) | **Zero chunking, ever.** Decompose losslessly (`codepoint → grapheme → word → sentence → … → file → source trunk`); retrieve exact LCA via GIN `laplace_path_ops` (`laplace_held_files`, `pull`). |
| BPE / SentencePiece integer vocab IDs or raw weight blobs | **Open Merkle vocabulary (`laplace-blake3-v1`).** Every atom, word, phrase, AST subtree, chess line, or patch is a token by its 16-byte BLAKE3 ID; model weights reduce transiently to trajectories + attestations (`raw_weight_bytes_retained = 0`). |
| Salted / domain-prefixed / case-folded hashes (`0x01`, `substrate/source/X/v1`, `"King"→"king"`) | **Pure content identity (`lp_id`).** Leaf = BLAKE3-128(UTF-8 of 1 codepoint). `n==1` → child itself. `n>=2` → BLAKE3-128($16n$ child IDs). Tier = `1 + max(child.tier)`. `"King" != "king"` (linked via `tier0.flags` / lexical attestations). Witness ID = source trunk `[source record, file trunks]`. |
| Recording `precedes`, `contains`, `continues_to`, or co-occurrence edge rows | **Query `physicality.path` trajectories directly.** Precedes, follows, gaps (`[Captain, ' ', Ahab]`), containment, and continuations resolve in microseconds via mantissa-packed `LineStringZM` + GIN/GiST. |
| Passing raw `physicality.path` carrier doubles into 4D spatial metrics or Glome | **Mantissa carrier = identity + metadata, not position.** Unpack & resolve real `entity.coord` (`laplace_couple`, `laplace_project_path`) before calling `frechet4d`/`dtw4d`/`edr4d`. |
| Recording release-local pointers (`oewn-`, sense keys, `sent_id`, `geonameid`, offsets) or `HAS_*` strings | **Highway nodes vs ephemeral pointers (`Conflicts.md` S1, I5, I7).** Shared identifiers (`i46360`, `eng`, `abandon.01`, `give-13.1`, `va:0001f`, `Q937`) are content/highway nodes; internal file pointers resolve at ingest and are **recorded nowhere**. `manifest/banks.tsv` defines fixed 64-bit `mask` bits. |
| Domain mini-engines, per-format C/C# walkers (`Laplace.Decomposers`), or 1-for-1 SQL porting | **One shared `Laplace-Native` C core + generic `.recipe` VM + `.firmware`.** Client runs deterministic C in-process (`Laplace.Native.Interop`); PG runs the same C for indexed set probes/merges. |
| Disposable PG (`initdb`, `pg_regress`), mocks, silent fallbacks, or constant DB `--recreate` | **Operate the real installed stack in place; fail loud.** Use `laplace.reconcile.sql` (`laplace deploy`) for code changes and `laplace forget` + `sweep` for single-source fixes; `--recreate` **only** when persistent table byte layouts or `tier0.bin` coords change. |

---

## 3. Repositories, 4-Table Schema & Build Chain (`E:\Repositories\Laplace`)

- **Build chain (`icx-release` into `$LAPLACE_BUILD/<repo>/icx-release`, `D:\Libraries\build\Laplace` on desktop):**
  PostgreSQL 18 + PostGIS → `Laplace-Native` (`include/laplace/*.h`, `manifest/`, `Laplace.Native.Interop`) → `Laplace-postgres` (`CREATE EXTENSION laplace`, `sql/laplace.sql.in`, `laplace.reconcile.sql`) → `Laplace-Engine` (`laplace` CLI, `reader.h`, `recipes/`, `firmware/`, `src/program.c`, `src/model.c`) → `Laplace-MCP` (`lpm` + `Laplace::http` + `web/`) & `Laplace` (`app/Laplace.slnx`, `db/schema/*.sql`, `web/`). Supporting: `Laplace-UCI`, `Laplace-Operations` (`laplace.ps1` / `laplace-ops`, `components.lock.tsv`, `data.tsv`, `dependencies.tsv`), `Laplace-Prototype`, `Laplace-Wiki`.
- **Four persistent substrate tables (`Laplace-postgres/sql/laplace.sql.in`):**
  - `entity(id blake3, tier smallint, coord geometry(PointZM), hilbert bigint, mask bit(64))`
  - `physicality(entity blake3, tier smallint, hilbert bigint, path geometry, mask bit(64))`
  - `attestation(claim blake3, witness blake3, lineage blake3, games bigint, tokens bigint, score float8, "position" int4, trust float8)`
  - `consensus(claim blake3 PK, rating float8, deviation float8, volatility float8, matches bigint)`
  - Conventional auth/entitlement/royalty tables live strictly in `app.*` (`Laplace/db/schema/*.sql`).

---

## 4. Hosts & Operational Commands

| Host | Role & Rules | Paths & Databases |
| :--- | :--- | :--- |
| `HART-DESKTOP` (`100.70.222.78`) | Primary Windows dev workstation, IIS `https://dev.hartonomous.com`. **Metered 5G: seed strictly from local `E:\Data\Ingest`, NEVER bulk-download.** Keep `HART-DESKTOP\ahart` `FullControl` in any elevated script | src `E:\Repositories\Laplace`, build `D:\Libraries\build\Laplace`, data `E:\Data\Ingest`, work `E:\Data\Laplace\work`, pgdata `E:\Data\Laplace\pgdata`, WAL `D:\Data\Laplace\pg_wal`; canonical DB: `laplace` |
| `hart-server` (`100.76.181.27`) | Linux staging, unprivileged CI runner, full corpus/model vault, `https://staging.hartonomous.com` | `/etc/laplace/machine.env`, src `/repos/src`, build `/repos/build`, data `/vault/Data`, models `/vault/models`, code `/vault/Code`; canonical DB: `laplace` |
| `hart-prod` (`100.124.95.17`) | Production target (`www.` / `api.` / `mcp.hartonomous.com`) | `/opt/laplace`; DB: `laplace-app` / `laplace` |
| `hart-cloud` (`100.103.255.122`) | On-demand AVX-512 qualification VM (**kept powered off when idle**) | CI-leased |

```powershell
# Fast local iteration during a Batch: build incrementally in D:\Libraries\build\Laplace, then deploy/verify ONCE per Batch:
laplace --where                                        # resolved Engine binary, perf-caches, DB
laplace status [--joins]                               # ROM fingerprints + table/index/collision counts
laplace ingest --plan [SOURCE]                         # dry-run recipe coverage (zero DB writes)
laplace ingest [SOURCE...]                             # admit from local $LAPLACE_DATA
laplace turn --as operator --session dev "prompt"      # execute 11-stage Forward pass
pwsh Laplace-Operations/laplace.ps1 components -Target desktop   # build & stage modular Native + Engine + MCP
pwsh Laplace-Operations/laplace.ps1 deploy -Target desktop       # end-of-Batch build + in-place DB reconcile + IIS deploy
pwsh Laplace-Operations/laplace.ps1 verify -Target desktop -Installed
```

---

## 5. Canonical Documentation Index (`E:\Repositories\Laplace\Laplace-Wiki`, `W/`)

| Topic | Primary Canonical Pages | Code Counterpart |
| :--- | :--- | :--- |
| Verbatim inventor record & core thesis | `W/Repositories/Laplace/docs/INVENTOR_RECORD.md`, `INVENTION.md` | `W/Architecture.md`, `W/Sequence/Conflicts.md` |
| 29-stage sequence & Proofs 1–3 | `W/Sequence/README.md` (`1. Unicode` → `29. Maintenance`), `W/Reference/Checks.md` | `Laplace-Prototype/tests/`, `Laplace-Operations/verify-admitted-data.py` |
| Layer boundaries & engineering rules | `W/Engineering/Boundaries.md`, `W/Engineering/Standards.md` | `Laplace-Engine/tests/sql-contract-gate.py`, `ReadPathArchitectureGateTests.cs` |
| Byte layouts, Tier-0, mantissas, masks | `W/Reference/Formats.md`, `W/Reference/Types.md`, skill `laplace-invention` | `Laplace-Native/include/laplace/laplace.h`, `manifest/banks.tsv` |
| Schema, 4D GiST, path/mask GIN, SQL | `W/Reference/Schema.md`, `W/Reference/SQL.md`, `W/Sequence/Database.md` | `Laplace-postgres/sql/laplace.sql.in`, `operations.sql`, `api.sql` |
| Recipes, grammars, ingest & maintenance | `W/Reference/Recipes.md`, `W/Reference/Ingest.md`, `W/Corpora/README.md` | `Laplace-Engine/recipes/`, `src/recipe*.c`, `src/ingest.c`, `src/forget.c` |
| Forward pass, Highway A*, Firmware, Gödel | `W/Sequence/Forward.md`, `Firmware.md`, `Pull.md`, `Learning.md`, `Authority.md` | `Laplace-Engine/src/program.c`, `pull.c`, `firmware.c`, `firmware/*.firmware` |
| Models, Mold-a-Model, Code, Chess, Billing | `W/Sequence/Models.md`, `Export.md`, `Code.md`, `Chess.md`, `Games.md`, `Envelope.md` | `Laplace-Engine/src/model.c`, `chess.c`; `Laplace/app/`, `Laplace/db/schema/` |
