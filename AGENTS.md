# Laplace — Agent Core

Laplace is a complete reinvention of the transformer on commodity x86-64 CPUs + PostgreSQL 18 + PostGIS + Native C (`icx`, `-fp-model=precise`, `-ffp-contract=off`, SIMD/AVX/VNNI, CORE-MATH, Eigen/Spectra/MKL) with **zero GPU requirement**. Every digital modality — text, source code, curated lexicons, chess, images, audio, video, and conventional AI model weights — decomposes losslessly into **one universal, content-addressed BLAKE3 Merkle DAG** (`same content = same hash`) inside a bounded 4D geometric space ($S^3$ surface for Tier-0 Unicode atoms; exact integer centroids strictly inside the 4-ball for compositions), with packed IEEE-754 mantissa trajectories, attributable containment provenance, Glicko-2 consensus standing across independent witnesses, detached Personality Firmware + the Gödel OODA loop for generation, and Mold-a-Model synthesis to export clean `laplace-blake3-v1` GGUF/Safetensors checkpoints without gradient descent or retraining.

> [!IMPORTANT]
> Canonical source: `E:\Repositories\Laplace\Laplace-Operations\agents\` (edit there, then run `pwsh -File Laplace-Operations/agents/sync.ps1`). Deep byte-level layouts & checklists: skill `laplace-invention`.

---

## 0. Mandate, Authority & Holistic Execution

1. **You operate with full owner authority.** Every line of code, test, workflow, and doc in this workspace is agent-written under one sole inventor/architect. You are authorized and expected to act across all repositories, branches, PRs, CI workflows, databases, and hosts (`HART-DESKTOP`, `hart-server`, `hart-prod`, `hart-cloud`) to achieve the objective. Never invent permission blocks ("nobody signed off", "production is locked", "you dispatch ingests"), never hand shell commands back to the user to run for you, and never fabricate human multi-month timelines.
2. **"A leaf spring is not the whole watch" — finish work holistically end-to-end.**
   - Never take thin vertical slices, stop after one sub-step, ask *"which of these should I act on?"*, or substitute prose, benchmarks, telemetry, or test scaffolding for working product functionality.
   - Drive every change across **every layer it touches** in one coherent effort: specification/recipe → `Laplace-Native` C kernel → `Laplace-postgres` extension → `Laplace-Engine` / `Laplace-MCP` → `Laplace` C#/Web surface → **build, deploy to the installed stack, execute against real data, and verify the live result**.
   - Uncommitted WIP in any repo or worktree was left by a prior agent, not a human mid-edit. Judge it against the invention: finish it, fix it, or replace it cleanly (and state what you replaced in your summary). Do not tiptoe around broken agent WIP.
3. **Truth order (the invention rules the code — never the reverse):**
   1. **The Invention:** `Laplace-Wiki/Repositories/Laplace/docs/INVENTOR_RECORD.md` (verbatim inventor statements), `INVENTION.md`, `INVENTIONS.md`, and **Decided** entries in `Laplace-Wiki/Sequence/Conflicts.md` + `Laplace-Wiki/Sequence/` (`1. Unicode` through `29. Maintenance`).
   2. **Architecture & Layer Boundaries:** `Laplace-Wiki/Architecture.md`, `Laplace-Wiki/Engineering/Boundaries.md`, `Laplace-Wiki/Engineering/Standards.md`.
   3. **Modular Components & Reference:** `Laplace-Native`, `Laplace-postgres`, `Laplace-Engine`, `Laplace-MCP`, `Laplace-UCI`, `Laplace-Operations`, and `Laplace-Wiki/Reference/*`.
   4. **Product Application Under Convergence (`Laplace` monorepo):** Built first (May–Sep 2026); owns the product surfaces (`/v1/*` OpenAI-compat API, Web UI/Glome, Chess Lab, Foundry/Mold-a-Model, Code player, Knowledge Arena, billing/royalties) while retiring its legacy duplicate substrate (`extension/laplace_substrate`, `laplace_geom`, `engine/core` `glicko2`/`hilbert4d`/`mantissa`/`super_fibonacci`, `SubstrateCanonicalIds`, `EntityTier`, per-source C# decomposers) onto the modular packages (`one-substrate`). (`Laplace-Refactor` is a frozen archive — never build or edit it.)
   - **Never infer architectural law from live DB state or from the last agent's commit alone.** Always read the documentation and code together. Where code, a test, or a secondary doc contradicts the invention, **change the code/doc to match the invention** — never rewrite the spec to excuse agent drift. For items marked **Open** in `Sequence/Conflicts.md`, ask the inventor before flipping a cross-repo storage or math contract.
4. **Use Laplace to build Laplace (without stalling if a DB was just recreated):**
   - `laplace-code` MCP (`https://dev.hartonomous.com/modular/mcp`, `laplace` DB): `code_search`, `code_where`, `code_callers`, `code_callees`, `file_outline`, `ingest_status`, `recipe_catalog`, `recipe_plan`, `search`, `hop`, `translate`, `degrees`, `pull`, `forward`, `turn`, `shape`, `provenance`.
   - `laplace` MCP (`https://dev.hartonomous.com/mcp`, `laplace-mono` DB): `health`, `source_status`, `api`, `op`, `pipeline`, `facts`, `taxonomy`, `walk`.
   - If `laplace-code` returns 0 docs because `workspace-code` has not been re-ingested into `laplace` yet, either ingest `workspace-code` or fall back immediately to `Select-String` / `git --git-dir=<repo>/.git grep` — never stall.

---

## 1. The 1.0 Finish Line (Keep the Forest in View)

Every effort moves Laplace toward **one cohesive, sellable 1.0 product** where all five pillars run on the one canonical Native substrate:

1. **Universal Recipe-Driven Ingestion & Proofs 1–3 (`Reference/Checks.md`):**
   - *Proof 1 (`db-roundtrip`):* Decompose multi-modality inputs leaf-to-trunk from `tier0.bin` + `highway.bin`, deduplicate trunk-to-leaf ($O(\text{tier})$) against PostgreSQL, batch COPY via `stage.*`, and recompose 100% byte-for-byte from PostgreSQL alone.
   - *Proof 2 (`highway.bin`):* Self-contained highway perf-cache (no raw corpora required at runtime), $O(1)$ 64-bit `mask` bank filtering, and cross-vocabulary identifier hops (`ili` ↔ `pbroleset` ↔ `vnclass` ↔ `fnframe` ↔ `vaframe`).
   - *Proof 3 (`recipes/order`):* Full curated corpus suite ingested at `gigabytes in seconds`, colliding on highway nodes, folding per-witness series into `consensus`, and resolving containment provenance to Fisher-information `share_bp = 10000`.
2. **Coherent, Attributed Conversation & Reasoning (`Forward` + `Firmware` + `Gödel`):**
   - Single stateful native Forward pass (`RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS`) driven by detached Personality Firmware (`Laplace-Engine/firmware/*.firmware`) and the Gödel OODA loop (`BELIEF ≠ RISK ≠ ACTION`), served identically across `laplace turn`, `/v1/chat/completions`, MCP, CLI, and Web with infinite context (Laplace *is* the context window) and zero softmax hallucination.
3. **Model Assimilation & Mold-a-Model Export (`Sequence/Models.md`, `Export.md`):**
   - Static checkpoints (Safetensors/GGUF across architectures) ingested once as witnesses: tokenizer tokens mapped to real text entities (never opaque token blobs), tensors recognized by shape, numerics consumed transiently into circuit trajectories and significance-filtered attestations (zero raw weight blob storage).
   - **Mold-a-Model:** Runtime synthesis of clean, training-artifact-free `laplace-blake3-v1` GGUF/Safetensors models from substrate standing, trajectories, and geometry — no GPU training or gradient descent.
4. **Structural Code & Proving Domains (`Sequence/Code.md`, `Chess.md`, `Games.md`):**
   - Code decomposed via Tree-sitter grammars into Merkle ASTs, constructed/repaired by subtree reuse + compiler/test consequence witnessing.
   - Chess proved as a pure modality over the same substrate (`Laplace-UCI`, PGN games + openings + Syzygy endgame probes as trajectories, `fastchess` SPRT ladder) alongside the Knowledge Arena.
5. **One Modular Substrate (`one-substrate` completion) & Royalty Billing:**
   - `Laplace-Native` (`Laplace.Native.Interop` auto-generated C# bindings), `Laplace-postgres`, `Laplace-Engine` (`LaplaceEngine` `reader.h`), and `Laplace-MCP` (`liblpm`) packaged and consumed cleanly (`find_package`, `components.lock.tsv`, native/NuGet artifacts) by `Laplace`.
   - **Client-determinism collapse (never port the ~750 legacy SQL items 1-for-1):** Because `tier0.bin`, `tier0.flags`, `highway.bin`, and `chess.bin` are deterministic and memory-mapped on both client and server, client operations (IDs, UAX #29/recipe decomposition, 4D centroids, Hilbert keys, mantissa pack/unpack, 64-bit masks, highway crosswalks, Syzygy probes, in-memory Fréchet/DTW/EDR, Glicko-2 / Fisher royalty math, text realization) run **in-process in `Laplace-Native` C on the client (`Laplace.Native.Interop`) with zero DB round-trips**, while PostgreSQL strictly stores the 4 tables and executes indexed set probes/merges (`~40` prepared binary statements, running the exact same `Laplace-Native` C functions). Rebind product surfaces onto `Laplace.Native.Interop` + `Laplace-postgres` / `LaplaceEngine`, then amputate `Laplace/extension/laplace_substrate` (323 `.sql.in` files), `laplace_geom`, `engine/*` duplicates, `Laplace.Decomposers`, and dead `.def` catalog families wholesale.
   - Patreon/billing operate strictly as external entitlement witnesses, with `app.billing_royalties` paying witnesses by `share_bp`.

### Active GitHub Program (`SaltyPatron` — Unified 5-Phase Ladder, 28 Active Issues)
- **Completed milestones (never re-open or hallucinate as pending):** `Modular 1` (`LaplaceNative` CMake package), `Modular 2` (`components.lock.tsv` lockfile across CIs), `Modular 3` (`LaplaceEngine` `reader.h`), `Sprints 1–3` (Native floor, series math, compiled Tree-sitter VM, single-pass `stage.*` ETL, all 54 `recipes/order` corpus recipes), `Sprint 7` (`.devcontainer`), and `Chess Sprints 1–5`.
- **Execute the 5 active milestone phases in strict dependency order end-to-end:**
  1. **`Phase 1: Modular Runtime & Contract Foundation (Modular 4–5)`** (`5` issues):
     - `Laplace-Native#50` (`Laplace::os`, `lp_buf_*`, `laplace/json.h` helpers) → `Laplace-Engine#174` (delete `src/os.*` & `json_str`; unify `pg.c` connects) + `Laplace-MCP#54` (delete `api_buf_*`/`api_json_esc`/`api_engine_run`; extract `Laplace::http` + MCP shell) → `Laplace-Native#51` + `Laplace#2058` (unified generated contract + multi-RID `Laplace.Native.Interop` NuGet; delete all 20 hand-written P/Invoke files & native copy scripts).
  2. **`Phase 2: One-Substrate & Client-Determinism Collapse (Modular 6–8)`** (`7` issues — Epic `Laplace-Operations#190`):
     - `Laplace#2059` (`Laplace.Hosting`, `Laplace.Data`, `Laplace.Mcp` shared `.NET` packages; thin composition roots) → `Laplace#2060` (Client-Determinism Collapse onto single `laplace` DB; delete `extension/laplace_substrate`, `laplace_geom`, `engine/*`, `Laplace.Decomposers`, `WorkspaceCodeIndex`, and `laplace-mono`) → `Laplace#1537` (Browse, `tier0.flags` variant discovery & indexed 4-table leaderboards), `Laplace#1450` (in-place workbench/Glome fullscreen), `Laplace-Operations#189` (`Salty-Suite` extraction), and `Laplace-Operations#2` (multi-host `criteria.json` lifecycle acceptance).
  3. **`Phase 3: Proof 3 Estate, Multi-Modal Recipes & Forward/Firmware Reasoning`** (`7` issues):
     - `Laplace-Prototype#3` (Proofs 1–3 full 54-source estate qualification & `forget`/`sweep`), `Laplace-Engine#31` (Highway A* landmark routing & N-ary strand traversal), `Laplace-Engine#10` (detached `.firmware` profiles & Gödel OODA `BELIEF ≠ RISK ≠ ACTION`), `Laplace#1720` (in-process `LaplaceEngine` turn binding & OpenAI role/multi-turn contract), `Laplace#1726` (API/MCP/Web `.firmware` switcher & OODA proof drawer), `Laplace#372` (Wikipedia/Wikidata/Natural Earth `.recipe`s), and `Laplace#1134` (lossless image/audio/video codecs, Tatoeba audio & edition convergence).
  4. **`Phase 4: Model Assimilation, Mold-a-Model, Structural Code, Chess & Arena`** (`6` issues):
     - `Laplace#1719` (shape-driven GGUF/Safetensors assimilation & native C Mold-a-Model `laplace-blake3-v1` synthesis), `Laplace#894` (`.git` Merkle history DAG, compiler/LSP symbol witness & consequence-witnessed AST repair), `Laplace#833` (Chess native Forward pass, coarse C search, conditioned standing, grandmaster books, deduplicated Stockfish witness & `fastchess` SPRT ladder), `Laplace#939` (Chess operational session/SSE/Lichess readback hardening & unmocked E2E), `Laplace#1934` (Ingest Admin Portal & Recipe Studio), and `Laplace#1421` (Knowledge Arena & `COMBINE`).
  5. **`Phase 5: Authority, Compute Envelopes, Royalty Billing & 1.0 Release Gate`** (`3` issues):
     - `Laplace#1708` (Knowledge Authority Packages, pre-`COUPLE` witness scoping, Sybil write governance & Red/Blue/Judge gates), `Laplace#1709` (Compute Envelopes, native machine-cost derivation, Patreon entitlement witnessing & `share_bp = 10000` Fisher royalty settlement), and `Laplace#1725` (Laplace 1.0 Five-Pillar Live Release Gate across `HART-DESKTOP`, `hart-server`, and `hart-prod`).
- **Tracker discipline:** Always verify `HEAD` before acting on any issue (preserve every item listed in `Verified HEAD Baseline`), never re-open closed legacy `Sprint *` tickets, close completed Phase issues with commit + live deployment proof, and never write thin stubs, `"EXCLUSIVE FILES"` slice-locks, or 1-for-1 `extension/laplace_substrate` SQL porting tasks.

---

## 2. Non-Negotiable Laws & Preempting AI Training-Data Reflexes

| Conventional AI / Software Reflex (FORBIDDEN) | Laplace Mechanism (REQUIRED) |
| :--- | :--- |
| Dense embeddings / cosine k-NN as "semantic similarity" | **Geometry is shape; attestations are meaning.** 4D coords (`entity.coord`) + Fréchet/DTW/EDR measure morphological/structural shape (`ring`/`sing`/`King`, matching structural bug traces). Semantic relatedness comes strictly from attested claims, highway hops, and usages weighted by Glicko-2 `consensus`. |
| Text/code chunking (`MaxLines`, sliding windows, overlap) | **Zero chunking, ever.** Decompose every tier losslessly (`codepoint → grapheme → word → sentence → … → file → source trunk`); retrieve the exact lowest common ancestor (LCA) via GIN `laplace_path_ops` (`laplace_held_files`, `pull`). |
| BPE / SentencePiece integer vocab IDs or raw weight blobs | **Open Merkle entity vocabulary (`laplace-blake3-v1`).** Every codepoint, word, phrase, AST subtree, chess line, or pixel patch is a token by its 16-byte BLAKE3 ID; model weights reduce to trajectories + Glicko-2 attestations. |
| Salted / domain-prefixed / invented-string hashes (`0x01`, `substrate/source/X/v1`, `language:eng`, case-folding `"King"→"king"`) | **Pure content identity (`lp_id`).** Leaf = BLAKE3-128(UTF-8 bytes of 1 codepoint). Singleton (`n==1`) collapses to the child itself. `n>=2` = BLAKE3-128($16n$ concatenated child IDs). Tier = `1 + max(child.tier)` (Merkle height, never a semantic role enum). `"King" != "king"` (linked by Unicode/lexical attestations). Witness ID = source trunk `[source record, file trunks]`. |
| Recording `precedes`, `contains`, `continues_to`, or co-occurrence as edge rows | **Query `physicality.path` trajectories directly.** Precedes, follows (`laplace_follows`, `laplace_precedes`), gaps (`[Captain, ' ', Ahab]`), containment, and continuations are read in microseconds from mantissa-packed trajectories via GIN/B-tree/GiST. |
| Treating `physicality.path` carrier doubles as spatial 4D coordinates | **Mantissa carrier = identity + metadata, not position.** `physicality.path` packs 128-bit child IDs into IEEE-754 mantissas (`43/43/42 + 28 spare` in `[0.25, 0.5)`) + `M` (`run30/said3/outcome2/pos18`) to exploit PostGIS storage, TOAST, binary COPY, and GIN. Unpack & resolve real `entity.coord` (`laplace_couple`, `laplace_project_path`) before calling `laplace_frechet4d`/`dtw4d`/`edr4d`. |
| Recording release-local pointers (WordNet offsets, sense keys, `oewn-`, `sent_id`, `geonameid`, row/byte offsets) or `HAS_*` English enums | **Highway nodes vs ephemeral pointers (`Conflicts.md` S1, I5, I7).** Cross-source identifiers (`i46360`, `eng`, `abandon.01`, `give-13.1`, `va:0001f`, FIDE IDs) are decomposed content / highway nodes. Release-local pointers are parsed for facts, resolved during ingest (`key`/`refer`/`type`), and **recorded nowhere**. Relations are the source's own content; `manifest/banks.tsv` packs fixed 64-bit `mask` fields (`kind` flags + `slot+1` enums). |
| Per-format C/C#/Python walkers (`chess.c` bypasses, `Laplace.Decomposers`, hand scanners, CSV bypasses) | **One generic recipe-driven pipeline.** Every structured or modality input goes through grammar/codec + compiled `.recipe`/`.format` bytecode (`Laplace-Engine/recipes/`). Source/language/modality names select declarations, never a separate engine branch. |
| Semantic logic, RBAR, cursors, recursive CTEs in SQL, math/ranking in C#/Python, or porting legacy `laplace_substrate` SQL 1-for-1 | **Shared `Laplace-Native` C on BOTH client & server.** Client runs deterministic operations in-process (`Laplace.Native.Interop` over mmap'd ROMs, zero DB round-trips); PostgreSQL (`Laplace-postgres`) runs the exact same C functions only for indexed set filters, stage merges, and $O(\text{tiers})$ frontier reads (`reader.h` / `pg_contract.c`). |
| Private/disposable PostgreSQL (`initdb`, `postgres_regress`, `laplace_regress_*`), mocks, silent fallbacks, or legacy shims | **Operate the real installed stack; fail loud.** No mock substrates, no silent fallback branches, no feature-flag bypasses. |

---

## 3. Repositories, Packages & Build Order (`E:\Repositories\Laplace`, GitHub `SaltyPatron`)

| Repo | Role & Package Boundary | Key Entry Points |
| :--- | :--- | :--- |
| `Laplace-Native` | Reusable deterministic C library (`LaplaceNative` CMake package + `Laplace.Native.Interop` auto-generated C# NuGet) | `include/laplace/laplace.h` (`tier0.h`, `highway.h`, `consensus.h`, `geometry.h`, `json.h`); `bindings/csharp/Laplace.Native.Interop.csproj`; `manifest/banks.tsv`, `manifest/slots/*.tsv`, `manifest/trust_classes.toml` |
| `Laplace-postgres` | `CREATE EXTENSION laplace` — 4D expansion of PostgreSQL 18 + PostGIS (`find_package(LaplaceNative)`) | `CMakeLists.txt` (`laplace-storage:<hash>` + `laplace.reconcile.sql`), `sql/laplace.sql.in`, `sql/operations.sql`, `sql/api.sql`, `manifest/function_bindings.tsv`, `src/*.c` |
| `Laplace-Engine` | `laplace` CLI (31 ops), `LaplaceEngine` reader package (`reader.h`), generic recipe VM, parallel loader, Forward/turn runtime | `src/op.c`, `src/reader.h`, `src/pg_contract.h`, `src/deploy.c`, `src/ingest.c`, `src/db.c`, `src/pull.c`, `src/forward.c`, `recipes/`, `firmware/` |
| `Laplace-MCP` | Native `lpm` HTTP/MCP server + `Laplace.Modular.Web` IIS host | `native/api_mcp.c` (`TOOLS`), `native/api.c`, `web/Program.cs` |
| `Laplace` | Product application (OpenAI API, `/mcp`, Web UI, Chess Lab, Foundry/Export, Code player, Arena, billing) converging onto modular packages | `app/Laplace.slnx`, `engine/core/src/sql_catalog.def`, `db/schema/*.sql`, `web/` |
| `Laplace-UCI` | UCI WebSocket/SSH/local relay & Lichess connector | `configuration.example.json`, `installations/*.json` |
| `Laplace-Operations` | Host provisioning, `laplace.env`, `components.lock.tsv`, `laplace-ops` (`laplace.ps1` / `laplace-ops`), CI/CD & ingest workflows, `agents/` | `laplace.ps1`, `laplace-ops`, `deploy.ps1`, `ingest.sh`, `acceptance/criteria.json`, `agents/AGENTS.md` |
| `Laplace-Prototype` | Cleanroom golden-value & storage proof bench | `tests/proof_native.c`, `tests/verify.py`, `run-proofs.ps1` |
| `Laplace-Conventional` | Conventional baseline comparisons | `train.yml`, `verify.yml`, `scripts/` |
| `Laplace-Wiki` | Canonical documentation & specification (`https://saltypatron.github.io/Laplace-Wiki/`) | `README.md` (strict catalog/summary parity — run `python tools/check_wiki.py` + `npx markdownlint-cli2` on edits) |

**Build & dependency chain:** PostgreSQL 18 + PostGIS → `Laplace-Native` → `Laplace-postgres` → `Laplace-Engine` → `Laplace-MCP` / `Laplace` (`icx-release` preset into `$LAPLACE_BUILD/<repo>/icx-release`, `D:\Libraries\build\Laplace` on `HART-DESKTOP`).

---

## 4. Database, Schema & Deployment Lifecycle (Stop Constant Reseeding)

- **Four persistent substrate tables (`Laplace-postgres/sql/laplace.sql.in`):**
  - `entity(id blake3, tier smallint, coord geometry(PointZM), hilbert bigint, mask bit(64))`
  - `physicality(entity blake3, tier smallint, hilbert bigint, path geometry, mask bit(64))`
  - `attestation(claim blake3, witness blake3, lineage blake3, games bigint, tokens bigint, score float8, "position" int4, trust float8)` — `UNIQUE NULLS NOT DISTINCT (claim, witness, lineage)`
  - `consensus(claim blake3 PK, rating float8, deviation float8, volatility float8, matches bigint)`
  - Operational/auth/billing tables (`app.*`) live in conventional schemas (`db/schema/*.sql`), never mixed into substrate identity.
- **In-place reconcile by default — DO NOT drop and reseed for routine code changes:**
  - `Laplace-postgres/CMakeLists.txt` hashes **only** `blake3` + the 4 table DDLs into `laplace-storage:<hash>`, and generates `laplace.reconcile.sql` for all C functions, SQL functions, casts, operators, opclasses, and indexes.
  - `laplace deploy` (`Laplace-Engine/src/deploy.c`) and `laplace-ops deploy` automatically transition compatible databases **in place** (`ALTER EXTENSION laplace UPDATE TO ...`) without dropping data or indexes.
  - Monorepo `db/schema/*.sql` is one idempotent declaration reconciled on deploy (zero numbered migration history).
  - **To fix or replace a source:** run `laplace forget <witness>` + `laplace sweep` + re-ingest (`Sequence/Maintenance.md` 29.1–29.3) — never drop the whole database for one source.
  - **Recreate (`laplace deploy --recreate` / `laplace-ops db ... -DatabaseOperation recreate -Yes`) ONLY when** persistent table column/partition byte layouts or `tier0.bin` coordinates actually change, and **never** wire automatic DB drops into build CI pipelines.

---

## 5. Hosts & High-Velocity Operational Loop

| Host | Role | Key Paths & Databases |
| :--- | :--- | :--- |
| `HART-DESKTOP` (`100.70.222.78`) | Primary dev workstation (Windows, RTX 4060 Ti, IIS `https://dev.hartonomous.com`; **metered 5G — seed strictly from local `E:\Data\Ingest`, never bulk-download**) | src `E:\Repositories\Laplace`, build `D:\Libraries\build\Laplace`, data `E:\Data\Ingest`, work `E:\Data\Laplace\work`, pgdata `E:\Data\Laplace\pgdata`, WAL `D:\Data\Laplace\pg_wal`; DBs: `laplace`, `laplace-mono`, `laplace-desktop-app` (Elevated ACL scripts must keep `HART-DESKTOP\ahart` `FullControl`) |
| `hart-server` (`100.76.181.27` / `192.168.1.2`) | Linux staging, self-hosted CI runner, corpus/model vault, `https://staging.hartonomous.com` | `/etc/laplace/machine.env`, src `/repos/src`, build `/repos/build`, work `/repos/work`, data `/vault/Data`, models `/vault/models`; DBs: `laplace`, `laplace-mono` |
| `hart-prod` (`100.124.95.17`) | Pre-1.0 deployment target (`api.` / `mcp.` / `www.hartonomous.com`) | `/opt/laplace`; DB: `laplace-app` |
| `hart-cloud` (`100.103.255.122`) | On-demand AVX-512 qualification VM (stopped when idle) | CI-leased |

```powershell
# Inspect & operate installed stack (Windows: laplace-ops -> pwsh Laplace-Operations/laplace.ps1):
laplace --where                                        # resolved Engine binary, perf-caches, DB
laplace status [--joins]                               # tier0/highway fingerprints + table/index counts
laplace ingest --plan [SOURCE]                         # verify recipe coverage (no DB write)
laplace ingest [SOURCE...]                             # ingest from local $LAPLACE_DATA
laplace turn --as operator --session dev "prompt"      # run end-to-end Forward pass
laplace-ops status -Target desktop                     # (-Target defaults to staging if omitted)
laplace-ops components -Target desktop                 # build & install modular Native + Engine + MCP ('laplace' CLI)
laplace-ops build -Target desktop                      # build app IIS package (-Components all,native,postgres,api,cli,uci,migrations,web)
laplace-ops deploy -Target desktop                     # content-addressed build + in-place DB reconcile + IIS deploy (-Components modular,modular-web,...)
laplace-ops verify -Target desktop -Installed          # verify live IIS + installed substrate + fingerprints
laplace-ops test -Check desktop-installed-deployment,desktop-installed-runtime
laplace-ops deploy -Target staging -Wait               # deploy & verify staging (then -Target production -Wait)
```

---

## 6. Navigation Index (`E:\Repositories\Laplace\Laplace-Wiki`, `W/`)

| Area | Read First | Deep / Code Counterpart |
| :--- | :--- | :--- |
| Inventor's verbatim record & thesis | `W/Repositories/Laplace/docs/INVENTOR_RECORD.md`, `INVENTION.md` | `W/Architecture.md`, `W/Sequence/Conflicts.md` |
| Ordered 29-stage build & runtime chain | `W/Sequence/README.md` (`1. Unicode` → `29. Maintenance`) | `W/Reference/Checks.md` (Proofs 1–3) |
| Layer ownership & code standards | `W/Engineering/Boundaries.md`, `W/Engineering/Standards.md` | `Laplace-Engine/tests/sql-contract-gate.py`, `ReadPathArchitectureGateTests.cs` |
| Byte layouts, Tier-0, mantissa carrier, masks | `W/Reference/Formats.md`, `W/Reference/Types.md`, skill `laplace-invention` | `Laplace-Native/include/laplace/laplace.h`, `manifest/banks.tsv` |
| Schema, 4D GiST, path/mask GIN, SQL | `W/Reference/Schema.md`, `W/Reference/SQL.md`, `W/Sequence/Database.md`, `Indexes.md` | `Laplace-postgres/sql/laplace.sql.in`, `operations.sql`, `api.sql` |
| Recipes, grammars, ingest & provenance | `W/Reference/Recipes.md`, `W/Reference/Ingest.md`, `W/Corpora/README.md` | `Laplace-Engine/recipes/`, `src/recipe*.c`, `src/structure*.c`, `src/db.c` |
| Forward pass, Firmware, Learning/Gödel | `W/Sequence/Forward.md`, `Firmware.md`, `Pull.md`, `Learning.md` | `Laplace-Engine/src/forward.c`, `pull.c`, `firmware.c`, `firmware/*.firmware` |
| Models, Mold-a-Model Export, Code, Chess | `W/Sequence/Models.md`, `Export.md`, `Code.md`, `Chess.md`, `Games.md` | `Laplace-Engine/src/model.c`, `chess.c`; `Laplace/app/Laplace.Decomposers/Model/` |
| Product shell & `one-substrate` remaining debt | `W/Reference/Monorepo.md`, `W/Reference/Repositories.md` | `Laplace/app/`, `Laplace/extension/` (retiring onto `Laplace-postgres`) |
