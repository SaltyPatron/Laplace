# Laplace — Agent Core

Laplace is a complete reinvention of the transformer on commodity x86-64 CPUs + PostgreSQL 18 + PostGIS + Native C (`icx`, `-fp-model=precise`, `-ffp-contract=off`, SIMD/AVX/VNNI, CORE-MATH, Eigen/Spectra/MKL) with **zero GPU requirement**. Every digital modality — text, curated lexicons, source code, `.git` history, chess, images, audio, video, and conventional AI model weights — decomposes losslessly into **one universal, content-addressed BLAKE3 Merkle DAG** (`same content = same hash`) inside a bounded 4D geometric space ($S^3$ surface for Tier-0 Unicode atoms; exact integer centroids strictly inside the 4-ball for compositions), with IEEE-754 mantissa-packed trajectories, containment provenance, Glicko-2 consensus standing across witnesses, detached Personality Firmware + the Gödel OODA loop for generation, and CPU Mold-a-Model synthesis exporting clean `laplace-blake3-v1` GGUF/Safetensors checkpoints without gradient descent or retraining.

> [!IMPORTANT]
> Canonical source: `E:\Repositories\Laplace\Laplace-Operations\agents\` (edit there, then run `pwsh -File Laplace-Operations/agents/sync.ps1`). Deep byte-level layouts: skill `laplace-invention`.

---

## 0. Mandate, Compounding Modularity & Batch Execution

1. **Full owner authority & zero theater.** Every line of code, test, workflow, and doc in this workspace is agent-written under one sole inventor/architect. Act directly across all repos, branches, workflows, databases, and hosts (`HART-DESKTOP`, `hart-server`, `hart-prod`, `hart-cloud`). Never invent permission blocks, never hand shell commands back to the user, never fabricate human multi-month timelines, and **never commit status reports, audit diaries, or benchmark prose** as a substitute for working product functionality. Judge leftover agent WIP against the invention: finish it, fix it, or replace it cleanly.
2. **Clean software compartmentalization + compounding semantic overlap:**
   - **Code architecture (compartmentalize, centralize, generalize, deduplicate):** Organize code into small, logically categorized, single-responsibility files, subdirectories, and versioned packages (`Laplace::laplace`/`os`/`text`/`linalg`/`model`/`http`, `LaplaceEngine`, `Laplace.Native.Interop`, `Laplace.Hosting`/`Data`/`Mcp`, `Salty-Suite`) — never 2,000-line god-files, flat 50-file `src/` dumps, `#ifdef _WIN32` platform spaghetti inside domain logic, copy-pasted buffer/JSON/connect wrappers, duplicated `.sh`+`.ps1` script pairs, or 22 copy-pasted `.recipe` files where one parameterized generic handles all pairs. Use clean C descriptor tables/callbacks, C# generics/DI base classes, and generated ABI contracts so every mechanism is written **once**, scales across cores/SIMD/hosts portably, and is easy to maintain.
   - **Data & runtime compounding (snap-in pieces colliding in one shared space, never isolated mini-engines):**
     - Packing 128-bit BLAKE3 child IDs (`43/43/42 + 28 spare`) + `M` (`run30/said3/outcome2/pos18`) into PostGIS `LineStringZM` mantissas turns GIS storage, TOAST, binary `COPY`, GiST R-trees, and GIN `laplace_path_ops` into a **zero-edge-table sequence/containment/gap/continuation engine** whose unpacked vertices simultaneously form real 4D curves in the $S^3$ ball, rated by Glicko-2 `consensus` and attributed by Fisher information (`share_bp = 10000`).
     - Pluggable codecs/grammars + `.recipe`s, mmap'd ROM perfcaches (`tier0.bin`, `tier0.flags`, `highway.bin`, `chess.bin`), and detached `.firmware` files all snap onto the **same** 16-byte `lp_id`s, **same** 4 tables, **same** C `Forward + Highway A* + Gödel` spine, and **same** readout/export pipeline (`lp_realize` + `Mold-a-Model` + `share_bp = 10000`) — never duplicating a second hash, ranking, or reasoning engine per modality.
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

## 1. Verified Baseline & 4-Pass Touch-Once Bottom-Up Finish Program (`SaltyPatron`)

- **Verified on `HEAD` (DO NOT revert or re-implement):** `Modular 1–3` (`LaplaceNative` CMake package, `components.lock.tsv`, `LaplaceEngine` `reader.h`), `tier0.bin` ($S^3$) + `tier0.flags` + `highway.bin` + 64-bit `banks.tsv` masks, `lp_attest_series` Glicko-2 + bounded witness trust, Fisher `share_bp = 10000`, 4-table `Laplace-postgres` (`laplace.reconcile.sql` in-place updates), compiled Tree-sitter + `.recipe` VM + 54 `recipes/order` sources, single-pass `stage.lp_*` binary `COPY`, `lp_realize`, initial `src/program.c` / `src/firmware.c` / `src/model.c`, and canonical BLAKE3 chess identity / Syzygy / PGN pipeline.
- **Execute strictly bottom-up in 4 Large Passes — finish & categorize each layer ONCE while in it (zero backtracking or throwaway rewiring):**
  1. **`Pass 1 — Lock Foundation Once (Laplace-Native + Laplace-postgres + Laplace.Native.Interop)`** (`Laplace-Native#50,#51`):
     - Categorize `Laplace-Native/src/` cleanly (`core/`, `os/`, `text/`, `geom/`, `consensus/`, `linalg/`, `codecs/`, `isa/`): finish `Laplace::os` (zero `#ifdef _WIN32` in upper repos), `lp_buf_*`, `laplace/json.h` helpers, `modality_decimal` (`255 -> ['2','5','5']`), GGUF/Safetensors dtype readers, **witness-allowlist-scoped `lp_attest_series`** (for `#1708`), and **work-unit counter structs** (for `#1709`).
     - Add any missing batched multi-key / witness-scoped binary probes in `Laplace-postgres` (`laplace.reconcile.sql`), and generate/pack multi-RID `Laplace.Native.Interop` (`win-x64`+`linux-x64`) once.
  2. **`Pass 2 — Categorize & Finish Native Engine + MCP + Recipes Once, Then Seed laplace DB Once`** (`Laplace-Engine#174,#31,#10`, `Laplace-MCP#54`, `Laplace-Prototype#3`, `Laplace#372,#1134`):
     - Reorganize flat `Laplace-Engine/src/` into categorized modules (`db/`, `ingest/`, `codecs/`, `forward/`, `model/`, `chess/`): unify `pg.c` connects, delete private `os.*`/`json_str`, pop & finish `Laplace-MCP`'s `Laplace::http` (`http_shell.c` stash `3e849d1d`), and delete `api_buf_*`/`api_json_esc`/`api_engine_run`.
     - Port PNG/WAV/video (`Media/*`), Parquet/CSV/shapefile, `.git` pack/commit DAG, compiler/LSP symbol reader, GGUF assimilation, and native C Mold-a-Model (`gguf_writer` + synthesis) into `Laplace-Engine` **now** so C# never needs intermediate rewiring.
     - Build the in-process `Forward` ABI **once** with `(AuthorityScope, ComputeEnvelope, Firmware)` from day one: collapse ~45 DB trips to 2–3 batched binary probes (`<100–200ms`), add word-POS fallback (`lightning` > `How does`), multi-clause composition, discourse seeding, Highway A* + N-ary (`np>=4`) strands, `COMBINE`, all `.firmware` profiles + Gödel OODA (`BELIEF ≠ RISK ≠ ACTION`), and coarse native C chess search (`chess.firmware`).
     - Generalize & fix `recipes/` *before* seeding (1 parameterized OpenSubtitles bitext recipe replacing 22 copies, Wiktionary `wikidata` QIDs, safety negative scores, WSD gold keys, Gutenberg wrapper split, plus `Wikidata`/`Wikipedia`/`Natural Earth`/Tatoeba audio/chess books), then **seed `laplace` DB cleanly once** (`Proofs 1–3`).
  3. **`Pass 3 — Single-Pass .NET/Web Rebind & Clean Delete Once (Laplace)`** (`Laplace#2058,#2059,#2060,#1537,#1450,#1720,#1726,#1934,#1719,#894,#833,#939,#1421,#1708,#1709`, `Laplace-Operations#190`):
     - Extract generic `.NET 10` packages (`Laplace.Hosting`, `Laplace.Data`, `Laplace.Mcp`) with thin `<150 LOC` host roots; consume `Laplace.Native.Interop` + in-process `LaplaceEngine` over the single populated `laplace` DB.
     - Bind OpenAI `/v1/*` (mapping `max_tokens`, `temperature`, `stop`, `seed`, `system`, `messages[]` + `.firmware` + OODA proof drawer), Browse (`tier0.flags` variants + indexed 4-table leaderboards), in-place Glome fullscreen, Recipe Studio, Foundry, Code, Chess Lab/SSE/Lichess (all 7 fixes), Arena, Authority Packages, and Compute Envelopes + Patreon `share_bp = 10000` Royalties **once**.
     - Delete `extension/laplace_substrate`, `laplace_geom`, `engine/*`, `Laplace.Decomposers`, the 20 P/Invoke files, `WorkspaceCodeIndex`, and `laplace-mono` **once, cleanly, with zero capability loss and zero throwaway edits**.
  4. **`Pass 4 — Full 5-Pillar Live Qualification, 3-Host Cutover & Salty-Suite`** (`Laplace-Operations#2,#189`, `Laplace#1725`):
     - Execute end-to-end live qualification (`Proofs 1–3`, `<100–200ms` multi-turn chat, `llama.cpp` Mold-a-Model conversation, compiled/tested AST code repair, `fastchess` Elo ladder, Red/Blue/Judge tenant isolation, integer-conserved royalties, `criteria.json`) across `HART-DESKTOP` (`dev.`) → `hart-server` (`staging.`) → `hart-prod` (`www.hartonomous.com`), and extract standalone ops tools into `Salty-Suite`.

---

## 2. Non-Negotiable Laws & Preempting AI Training-Data Reflexes

| Conventional AI / Software Reflex (FORBIDDEN) | Laplace Mechanism & Concrete Rationale (REQUIRED) |
| :--- | :--- |
| Dense embeddings / cosine k-NN as stored "semantic similarity" | **Geometry is shape; attestations are meaning.** Distinct knowledge **can occupy the same 4D center of mass** (`coord("125") == coord("521")`, `coord("dog") == coord("god")`) while having distinct `lp_id`s and distinct realized curves (`frechet("125", "521") > 0`): `entity.coord` narrows $O(\log N)$ spatial neighborhoods via GiST, `physicality.path` + Fréchet/DTW/EDR resolve exact ordered shape, and `attestation` + Glicko-2 `consensus` carry meaning. *(Transient `lp_rowsig`/`lp_knn_exact` inside `model.c` weight extraction is lawful when `raw_weight_bytes_retained = 0`.)* |
| Private `ByteAtoms` / pixel-alphabet tables for binary/media | **Universal Tier-0 codepoints + compositional scalars (`255 -> ['2','5','5']`).** Digits are Tier-0 codepoints; `['2','5','5']` is composed **once** (cached in ROM/node table) so `255` in prose, code, IP octets, RGB(A) channel intensities, PCM samples, and $\pi$ trajectories shares **one `lp_id`**, while higher-tier `physicality.path` (`pixel -> patch -> region -> image`, `run` RLE in `M`) gives it form without bloat. |
| Text/code chunking (`MaxLines`, sliding windows, overlap) | **Zero chunking, ever.** Decompose losslessly (`codepoint → grapheme → word → sentence → … → file → source trunk`); retrieve exact LCA via GIN `laplace_path_ops` (`laplace_held_files`, `pull`). *(Skipping unchanged files via ingest-state byte hashes is lawful; byte hashes just never replace `lp_id`.)* |
| BPE / SentencePiece integer vocab IDs or raw weight blobs | **Open Merkle vocabulary (`laplace-blake3-v1`).** Decode model vocab tokens (`Ġ`/`▁`/`##`) to real text `lp_id`s; reduce weights transiently to trajectories + attestations (`raw_weight_bytes_retained = 0`). Exported GGUFs emit standard string tokens **plus** `laplace.vocab.blake3_ids` metadata (and multi-plane trajectory+standing embeddings, never rank-4 `coord` alone) so stock `llama.cpp` loads them directly. |
| Salted / domain-prefixed / case-folded hashes (`0x01`, `substrate/source/X/v1`, `"King"→"king"`) | **Pure content identity (`lp_id`).** Leaf = BLAKE3-128(UTF-8 of 1 codepoint; 1-char words like `"a"`, `"I"`, `"犬"` ARE Tier-0 entities and lexicons may attest them). `n==1` → child itself. `n>=2` → BLAKE3-128($16n$ child IDs). `"King" != "king"`. Content-addressed `lp_id -> coord/path/text` memoization is always valid; never invent a second ID hash. |
| Recording `precedes`, `contains`, `continues_to`, or co-occurrence edge rows | **Query `physicality.path` trajectories directly.** Precedes, follows, gaps (`[Captain, ' ', Ahab]`), containment, and continuations resolve in microseconds via mantissa-packed `LineStringZM` + GIN/GiST instead of 40× edge-table bloat. |
| Passing raw `physicality.path` carrier doubles into 4D spatial metrics or Glome | **Mantissa carrier = identity + metadata, not position.** Unpack & resolve real `entity.coord` (`laplace_couple`, `laplace_project_path`) before calling `frechet4d`/`dtw4d`/`edr4d`. |
| Recording ephemeral row pointers (`sent_id`, byte offsets) or dropping gold/negative data | **Resolve internal pointers at ingest; keep shared IDs & negative scores.** Shared IDs (`i46360`, `oewn-*` when `ili="in"`, `Q937`, `abandon.01`, `give-13.1`, `va:0001f`) are highway/concept nodes; internal file pointers (`d000.s000.t000`) are joined via `key`/`refer` at ingest so gold labels are **never** dropped. Toxicity/harm datasets record real negative/risk scores (`INVENTOR_RECORD.md` L211). `banks.tsv` accelerates core 64-bit masks while any entity can be a claim predicate. |
| Capping an entire trust class as 1 witness, muting on ties, or breaking OpenAI clients | **Independent round-table consensus & practical serving.** Dependence capping applies to *shared causal lineage / Sybil accounts*, never across independent models (`Llama + Qwen + DeepSeek` corroborate each other). Word-level POS backs off bigram parses; polysemous prompts answer or clarify (never mute `budget=0`); `/v1/chat/completions` maps `max_tokens`, `temperature`, `stop`, `seed`, `system`, and `messages[]` cleanly instead of returning HTTP 400. |
| Black-box `-ax` FP flags, domain mini-engines, or 1-for-1 SQL porting | **Explicit deterministic SIMD (`cpu.c`) + shared `Laplace-Native` C.** Compiler `-ax` FP reassociation is forbidden, while `cpu.c` `cpuid` dispatch to fixed-order `-fp-model=precise -ffp-contract=off` AVX2/AVX-512/VNNI kernels is required. Client runs pure ROM/CPU C in-process (`0` DB trips); `Laplace-postgres` runs data-adjacent C set probes/merges/attribution in-DB so intermediate rows stay off the socket. |
| Disposable PG (`initdb`, `pg_regress --temp-instance`), mocks, or constant DB `--recreate` | **Operate the real installed stack in place; fail loud.** (`pg_regress --use-existing` rolling back txns on the installed server and Python CLI/CI verification scripts are fine.) Use `laplace.reconcile.sql` (`laplace deploy`) for code changes and `laplace forget` + `sweep` for single-source fixes. |

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
