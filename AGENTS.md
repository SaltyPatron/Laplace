# Laplace — Agent Core

Laplace is a complete reinvention of the transformer on commodity x86-64 CPUs + PostgreSQL 18 + PostGIS + Native C (`icx`, `-fp-model=precise`, `-ffp-contract=off`, SIMD/AVX/VNNI, CORE-MATH, Eigen/Spectra/MKL) with **zero GPU requirement**. Every digital modality — text, curated lexicons, source code, `.git` history, chess, images, audio, video, and conventional AI model weights — decomposes losslessly into **one universal, content-addressed BLAKE3 Merkle DAG** (`same content = same hash`) inside a bounded 4D geometric space ($S^3$ surface for Tier-0 Unicode atoms; exact integer centroids strictly inside the 4-ball for compositions), with IEEE-754 mantissa-packed trajectories, containment provenance, Glicko-2 consensus standing across witnesses, detached Personality Firmware + the Gödel OODA loop for generation, and CPU Mold-a-Model synthesis exporting clean `laplace-blake3-v1` GGUF/Safetensors checkpoints without gradient descent or retraining.

> [!IMPORTANT]
> Canonical source: `E:\Repositories\Laplace\Laplace-Operations\agents\` (edit there, then run `pwsh -File Laplace-Operations/agents/sync.ps1`). Deep byte-level layouts: skill `laplace-invention`.

---

## 0. Mandate, Compounding Modularity & Batch Execution

1. **Full owner authority & zero theater.** Every line of code, test, workflow, and doc in this workspace is agent-written under one sole inventor/architect. Act directly across all repos, branches, workflows, databases, and hosts (`HART-DESKTOP`, `hart-server`, `hart-prod`, `hart-cloud`). Never invent permission blocks, hand shell commands back to the user, fabricate multi-month timelines, or **commit status reports, audit diaries, or benchmark prose** as substitutes for working product code. Judge leftover agent WIP against the invention: finish, fix, or replace it cleanly.
2. **Clean software compartmentalization + compounding semantic overlap:**
   - **Code architecture (compartmentalize, centralize, generalize, deduplicate):** Organize code into small, categorized, single-responsibility files, subdirectories, and versioned packages (`Laplace::laplace`/`os`/`text`/`linalg`/`model`/`http`, `LaplaceEngine`, `Laplace.Native.Interop`, `Laplace.Hosting`/`Data`/`Mcp`, `Salty-Suite`) — never 2,000-line god-files, flat 50-file `src/` dumps, `#ifdef _WIN32` spaghetti inside domain logic, copy-pasted buffer/JSON/connect wrappers, duplicated `.sh`+`.ps1` pairs, or 22 copy-pasted `.recipe` files where 1 parameterized generic handles all pairs. Use C descriptor tables/callbacks, C# generics/DI base classes, and generated ABI contracts so each mechanism is written **once**.
   - **Data & runtime compounding (snap-in pieces colliding in one shared space, never isolated mini-engines):**
     - **`entity` = scale-free deterministic 4D handle; `physicality` = directed shape + microsecond GIN salience:** Any granularity of concept (Tier-0 codepoint on $S^3$, `"255"`, Tier-2 word, Tier-3 idiom/C statement, function AST, book, `.git` repo root) sits at one deterministic `(id, coord)` in `[-1, 1]^4` (`entity.mask` = strictly 64-bit `banks.tsv` highway bits, never origin/channel), so **any tier's `entity` acts directly as a native AI "token"**. Packing child IDs (`43/43/42 + 28 spare`) + `M` (`run30/said3/outcome2/pos18`) into `LineStringZM` + GIN `laplace_path_ops` yields containment, precedes, follows, gaps, **monotone-forward 4D curves** (`frechet4d`/`dtw4d`/`edr4d` separating `"cat"`/`"act"`/`"tac"`), and **microsecond salience (`laplace_salience`: GIN $I(e)$, span contraction, `M`-outcome `WIN`/`LOSS` contrast)** with zero edge tables.
     - **Talking = Internal Monologue = Coding (one OODA spine):** Codecs/grammars + `.recipe`s, ROM perfcaches (`tier0.bin`, `tier0.flags`, `highway.bin`, `chess.bin`), `.firmware` profiles, external turns (`UserPromptContent` `0.30` / `ResponseContent` `0.20`), internal self-prompts (`InternalMonologue` `0.10`), and compiler/test/engine outcomes (`ToolchainVerification` `0.90`) all snap onto the **same** `lp_id`s, **same** 4 tables, **same** C `Forward + Salience + Highway A* + Gödel` loop, and **same** readout/export pipeline (`lp_realize` + `Mold-a-Model` + `share_bp = 10000`).
3. **Execute in large horizontal Passes — ZERO per-ticket waterfall ceremony:**
   - Active repos sit side-by-side in `E:\Repositories\Laplace` sharing `$LAPLACE_BUILD` (`D:\Libraries\build\Laplace`). Incremental `ninja` + `dotnet build` takes seconds.
   - **NEVER run 28 separate CI pipelines, `components.lock.tsv` bumps, NuGet publishes, and IIS deploys one ticket at a time.** Develop across all repos in an active **Pass** together (using parallel subagents on disjoint tracks), compile incrementally locally, and run **one** unified build, **one** in-place `laplace.ps1 deploy`, **one** live behavioral proof on populated data, and **one** `components.lock.tsv` + `git push` per Pass.
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
     - Categorize `Laplace-Native/src/` (`core/`, `os/`, `text/`, `geom/`, `consensus/`, `linalg/`, `codecs/`, `isa/`): `Laplace::os` (zero `#ifdef _WIN32` in upper repos), `lp_buf_*`, `laplace/json.h`, `modality_decimal` (`255 -> ['2','5','5']`), GGUF/Safetensors dtype readers, witness-scoped `lp_attest_series` (`#1708`), work-unit counters (`#1709`), and append `ToolchainVerification = 0.90` + `InternalMonologue = 0.10` in `manifest/trust_classes.toml`.
     - In `Laplace-postgres` (`frontier_gin.c`, `couple.c`, `laplace.reconcile.sql`), expose **microsecond `laplace_salience`** (exact GIN `PATH_MEMBER` holder cardinality `df` + continuous $I(e) = -\log_2\frac{\text{df}+1}{N+1}$ eliminating binary `hub` blindness, joint span contraction, and `M`-outcome `WIN`/`LOSS` differential) + **salience-weighted 4D query centroids** ($\sum w_i x_i / \sum w_i$, replacing flat `1/N` in `couple.c`), plus batched witness-scoped probes and multi-RID `Laplace.Native.Interop`.
  2. **`Pass 2 — Categorize & Finish Native Engine + MCP + Recipes Once, Then Seed laplace DB Once`** (`Laplace-Engine#174,#31,#10`, `Laplace-MCP#54`, `Laplace-Prototype#3`, `Laplace#372,#1134`):
     - Reorganize flat `Laplace-Engine/src/` into categorized modules (`db/`, `ingest/`, `codecs/`, `forward/`, `model/`, `chess/`): unify `pg.c` connects, delete private `os.*`/`json_str`, pop & finish `Laplace-MCP`'s `Laplace::http` (`http_shell.c` stash `3e849d1d`), and delete `api_buf_*`/`api_json_esc`/`api_engine_run`.
     - Port PNG/WAV/video (`Media/*`), Parquet/CSV/shapefile, `.git` pack/commit DAG, compiler/LSP symbol reader, GGUF assimilation, and native C Mold-a-Model (`gguf_writer` + synthesis) into `Laplace-Engine` **now** so C# never needs intermediate rewiring.
     - Build the in-process `Forward` ABI **once** with `(AuthorityScope, ComputeEnvelope, Firmware)` + **microsecond Salience, multi-tier `entity` tokens, and closed-loop OODA**: collapse ~45 DB trips to 2–3 batched binary probes (`<100–200ms`); drive `COUPLE`/`ORIENT` with `laplace_salience` + word-POS fallback (`lightning` $\gg$ `How does`, zero flat `1/N` query centroids, zero `budget = 0` mute on `nambig > 0`); resolve & emit **arbitrary-tier `entity` handles** (`tier 0..N`) as native tokens; execute **`InternalMonologue` (`0.10`) self-prompting + `ToolchainVerification` (`0.90`) code/action verification** (`R0 + Δ → R1`, GIN `LOSS` vs `WIN` causal localization *"every time datatype X enters function Y, compile fails"*, self-correction); plus Highway A*, N-ary (`np>=4`) strands, `COMBINE`, all `.firmware` profiles, and coarse C chess search (`chess.firmware`).
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
| Dense embeddings / cosine k-NN as stored "semantic similarity" | **Scale-free 4D handles (`entity`) + directed curves (`physicality`) + standing (`consensus`).** Any granularity of concept sits at a deterministic 4D centroid in `[-1, 1]^4`; permutations share a centroid (`coord("cat") == coord("act") == coord("tac")`), static traces differ or reverse (`hausdorff4d("cat","tac") == 0`), and monotone-forward `frechet4d`/`dtw4d`/`edr4d` resolve exact forward direction (`frechet4d("cat","tac") > 0`) while `attestation` + Glicko-2 carry meaning. *(Transient `lp_rowsig`/`lp_knn_exact` in `model.c` is lawful when `raw_weight_bytes_retained = 0`.)* |
| Flat `1/N` query centroids, binary `hub` cliffs, or ignoring salience | **Microsecond trajectory salience (`laplace_salience`).** Read exact GIN `PATH_MEMBER` cardinalities (`df` even above `fan`) for continuous self-information $I(e) = -\log_2\frac{\text{df}+1}{N+1}$, joint bitmap contraction (collapsing `"How does ... ?"` frames so `"lightning"` anchors the query), and `M`-outcome `WIN`/`LOSS` differential; salience-weight query centroids ($\sum w_i x_i / \sum w_i$), while canonical stored `entity.coord` stays strictly deterministic from content alone. |
| Private `ByteAtoms` / pixel-alphabet tables for binary/media | **Universal Tier-0 codepoints + compositional scalars (`255 -> ['2','5','5']`).** Digits are Tier-0 codepoints; `['2','5','5']` composes **once** so `255` in prose, code, IP octets, RGB(A) intensities, PCM samples, and $\pi$ shares **one `lp_id`**, while higher-tier `physicality.path` (`pixel -> patch -> region -> image`, `run` RLE in `M`) gives form. |
| Text/code chunking (`MaxLines`, sliding windows) or flat word-only tokens | **Zero chunking; arbitrary-tier `entity` handles ARE the native "tokens".** Decompose losslessly (`codepoint → grapheme → word → clause/statement → function → file → trunk`); `Forward` resolves/emits any tier's `entity` (`tier 0..N`) directly in one step when standing warrants, dropping to lower tiers only for novel composition, and retrieves exact LCA via GIN `laplace_path_ops`. |
| Separate "code generator" mini-engines or polluting `entity.mask` with turn channel | **One OODA loop (`Talking = Internal Monologue = Coding`) in the same 4 tables.** `entity.mask` is strictly `banks.tsv` highway bits. Channel and consequence live on trajectory vertices (`said`/`outcome`/spare) + `trust_classes.toml`: `InternalMonologue` (`0.10`) records working thoughts/AST deltas (`R0 + Δ → R1`) without self-corroborating truth; `ToolchainVerification` (`0.90`) witnesses compiler/test `WIN`/`LOSS` on salient divergent AST constituents (*"every time datatype X enters function Y, compile fails"*). |
| BPE / SentencePiece integer vocab IDs or raw weight blobs | **Open Merkle vocabulary (`laplace-blake3-v1`).** Decode model vocab tokens (`Ġ`/`▁`/`##`) to real text `lp_id`s; reduce weights transiently to trajectories + attestations (`raw_weight_bytes_retained = 0`). Exported GGUFs emit string tokens + `laplace.vocab.blake3_ids` (multi-tier concepts + multi-plane trajectory+standing embeddings, never rank-4 `coord` alone) for stock `llama.cpp`. |
| Salted / domain-prefixed / case-folded hashes (`0x01`, `substrate/source/X/v1`, `"King"→"king"`) | **Pure content identity (`lp_id`).** Leaf = BLAKE3-128(UTF-8 of 1 codepoint; 1-char words `"a"`, `"I"`, `"犬"` ARE Tier-0 entities). `n==1` → child itself. `n>=2` → BLAKE3-128($16n$ child IDs). `"King" != "king"`. `lp_id -> coord/path/text` memoization is valid; never invent a second ID hash. |
| Recording `precedes`, `contains`, `continues_to`, or co-occurrence edge rows | **Query `physicality.path` trajectories directly.** Precedes, follows, gaps (`[Captain, ' ', Ahab]`), containment, continuations, and salience resolve in microseconds via `LineStringZM` + GIN/GiST without 40× edge-table bloat. |
| Passing raw `physicality.path` carrier doubles into 4D spatial metrics or Glome | **Mantissa carrier = identity + metadata, not position.** Unpack & resolve real `entity.coord` (`laplace_couple`, `laplace_project_path`) before `frechet4d`/`dtw4d`/`edr4d`. |
| Recording ephemeral row pointers (`sent_id`, byte offsets) or dropping gold/negative data | **Resolve internal pointers at ingest; keep shared IDs & negative scores.** Shared IDs (`i46360`, `oewn-*` when `ili="in"`, `Q937`, `abandon.01`, `give-13.1`, `va:0001f`) are highway nodes; local pointers (`d000.s000.t000`) join via `key`/`refer` at ingest. Toxicity/harm datasets keep negative/risk scores (`INVENTOR_RECORD.md` L211). |
| Capping an entire trust class as 1 witness, muting on ties, or breaking OpenAI clients | **Independent round-table consensus & practical serving.** Dependence capping applies to *shared causal lineage / Sybils*, never across independent models (`Llama + Qwen + DeepSeek` corroborate). Polysemous prompts answer or clarify (never `budget=0`); `/v1/chat/completions` maps `max_tokens`, `temperature`, `stop`, `seed`, `system`, `messages[]`. |
| Black-box `-ax` FP flags, domain mini-engines, or 1-for-1 SQL porting | **Explicit deterministic SIMD (`cpu.c`) + shared `Laplace-Native` C.** `-ax` FP reassociation is forbidden; `cpu.c` `cpuid` dispatch to `-fp-model=precise -ffp-contract=off` AVX2/AVX-512/VNNI kernels is required. Client runs pure ROM/CPU C (`0` DB trips); `Laplace-postgres` runs data-adjacent C set probes/merges in-DB. |
| Disposable PG (`initdb`, `pg_regress --temp-instance`), mocks, or constant DB `--recreate` | **Operate the real installed stack in place; fail loud.** (`pg_regress --use-existing` rolling back txns and Python CLI/CI scripts are fine.) Use `laplace.reconcile.sql` (`laplace deploy`) for code updates and `laplace forget` + `sweep` for single-source fixes. |

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
| `HART-DESKTOP` (`100.70.222.78`) | Primary Windows workstation, IIS `https://dev.hartonomous.com`. **Metered 5G: seed strictly from local `E:\Data\Ingest`, NEVER bulk-download.** Keep `HART-DESKTOP\ahart` `FullControl` | src `E:\Repositories\Laplace`, build `D:\Libraries\build\Laplace`, data `E:\Data\Ingest`, work `E:\Data\Laplace\work`, pgdata `E:\Data\Laplace\pgdata`, WAL `D:\Data\Laplace\pg_wal`; DB: `laplace` |
| `hart-server` (`100.76.181.27`) | Linux staging, CI runner, corpus/model vault, `https://staging.hartonomous.com` | `/etc/laplace/machine.env`, src `/repos/src`, build `/repos/build`, data `/vault/Data`, models `/vault/models`, code `/vault/Code`; DB: `laplace` |
| `hart-prod` (`100.124.95.17`) | Production (`www.` / `api.` / `mcp.hartonomous.com`) | `/opt/laplace`; DB: `laplace` |
| `hart-cloud` (`100.103.255.122`) | On-demand AVX-512 qualification VM (**powered off when idle**) | CI-leased |

```powershell
# Iterate incrementally in D:\Libraries\build\Laplace during a Pass; deploy/verify ONCE per Pass:
laplace --where                                        # resolved Engine binary, perf-caches, DB
laplace status [--joins]                               # ROM fingerprints + table/index/collision counts
laplace ingest [--plan] [SOURCE...]                    # dry-run (--plan) or admit from local $LAPLACE_DATA
laplace turn --as operator --session dev "prompt"      # execute Forward + Salience + OODA pass
pwsh Laplace-Operations/laplace.ps1 components -Target desktop   # build & stage Native + Engine + MCP
pwsh Laplace-Operations/laplace.ps1 deploy -Target desktop       # end-of-Pass build + DB reconcile + IIS deploy
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
