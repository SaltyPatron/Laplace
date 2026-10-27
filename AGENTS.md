# Laplace Workspace Engineering & Operations Guide

This guide governs engineering and operational work across the Laplace multi-repository workspace (`E:\Repositories\Laplace`).

---

## 1. Repository Architecture & Ownership

The workspace contains two active implementation stacks, supporting documentation/research repositories, and one frozen historical reference repository:

| Repository / Stack | Role & Ownership | Status |
|---|---|---|
| **[Laplace](Laplace/README.md)** (`laplace-mono`) | Full-stack monorepo: ASP.NET Core / C# API & OpenAI-compatible endpoints (`app/`), React/Vite web UI (`web/`), native C/C++ substrate & PostgreSQL extension (`engine/`, `extension/`), SQL migrations (`db/`), and deployment (`deploy/`). | **Active** |
| **[Laplace-Native](Laplace-Native/README.md)** | Modular native C/C++ SIMD/ISA substrate library (`laplace_native`). | **Active** |
| **[Laplace-postgres](Laplace-postgres/README.md)** | Modular PostgreSQL C extension (`laplace` 1.12) integrating `Laplace-Native` with PostgreSQL 18.6. | **Active** |
| **[Laplace-Engine](Laplace-Engine/README.md)** | Modular C++ engine CLI (`laplace`), recipe compiler/ingester, and firmware executor. | **Active** |
| **[Laplace-MCP](Laplace-MCP/README.md)** | Modular Model Context Protocol server (`laplace-modular-mcp`). | **Active** |
| **[Laplace-UCI](Laplace-UCI/README.md)** | Universal Chess Interface (UCI) adapter and tournament/engine bridge. | **Active** |
| **[Laplace-Operations](Laplace-Operations/README.md)** | Host provisioning (`setup.sh` / `setup.ps1`), PostgreSQL tuning (`tune-pg.*`), client packaging, cross-host deployment (`laplace.ps1`), and operational verification. | **Active** |
| **[Laplace-Wiki](Laplace-Wiki/README.md)** | Canonical documentation and specification wiki published to GitHub Pages. | **Active** |
| **[Laplace-Conventional](Laplace-Conventional/README.md)** | Python/GPU tooling for conventional model comparison, probing, and reference evaluation. | **Active** |
| **[Laplace-Refactor](Laplace-Refactor/README.md)** | Historical cleanroom prototype containing reference specifications and contracts for **recipe-driven ingestion**, typed recipe compilation, and source admission. | **FROZEN (Read-Only Reference)** |

> **Important (`Laplace-Refactor`)**: Do **not** build, deploy, or run CI workflows in `Laplace-Refactor`. Consult it strictly as a read-only architectural reference when implementing or porting recipe-driven ingestion into `Laplace` or `Laplace-Engine`.

---

## 2. Host Topology, Tailscale Mesh & Environment Isolation Contract

Laplace runs across a single-owner, multi-host topology connected over LAN (`192.168.1.0/24`) and Tailscale (`100.64.0.0/10`), fronted by Cloudflare and `hart-router` (OpenWrt split-horizon DNS):

| Host | Tailscale / LAN IP | OS & Environment Isolation Role | Public / Local Origin | Databases & Services |
|---|---|---|---|---|
| **`HART-DESKTOP`** (`hart-desktop-1`) | `100.70.222.78`<br>`192.168.1.x` | **Local Interactive Dev**: Windows 11 workstation & optional local GPU host. **Zero gatekeeping, no background GitHub Actions runners.** Full direct CLI/DB/UI control. | `https://dev.hartonomous.com` (IIS `laplace-mono`) and `http://localhost:5187` (dev source run) | Single custom PostgreSQL 18.6 service (`laplace-postgres`, port 5432, SSPI + TLS):<br>- `laplace-mono` (monorepo substrate DB)<br>- `laplace-desktop-app` (monorepo app/auth/billing DB)<br>- `laplace` (modular substrate DB) |
| **`hart-server`** | `100.76.181.27`<br>`192.168.1.2` | **Staging (CI/CD App Deployment) + Shared Storage/Infra Host**: Ubuntu 22.04 server (**damaged GPU disabled**). App & modular services are deployed **via CI/CD only**; the host itself remains accessible for `/vault` SMB storage, OpenBao, Vaultwarden, backups, and DB hosting. | `https://staging.hartonomous.com` (`laplace-api` on `:5187`, `laplace-mcp` on `:8080`, `laplace-modular-mcp` on `:5188`) | PostgreSQL 18.6 cluster:<br>- `laplace-mono` (staging substrate + app DB, and BCP failover substrate DB for `hart-prod`)<br>- `laplace` (modular substrate DB)<br>Also hosts **OpenBao** (`https://openbao.hartonomous.com`), **Vaultwarden**, SMB shares (`/vault`), and Linux GitHub Actions runners. |
| **`hart-prod`** | `100.124.95.17` | **Locked Enterprise Production (CI/CD Promotion Only)**: Low-cost always-on GCP VM (`project-f5812b96-f924-41a4-94c`, `us-east1-b`). Treated as an isolated appliance: **never manually SSH-patched or built on**. Receives only verified packages promoted from `hart-server` by CI/CD. | `https://api.hartonomous.com`, `https://mcp.hartonomous.com` | Local PostgreSQL 18.6 (`laplace-app` for accounts, sessions, keys, billing). Connects to `hart-server` (or `hart-cloud` when active) over Tailscale for read-heavy substrate queries. |
| **`hart-cloud`** | `100.103.255.122` | **On-Demand AVX-512 DB & Qualification Host**: High-spec GCP `c3-standard-8` VM. | Internal / Tailscale | **Kept `TERMINATED` (powered off) when idle** to preserve GCP trial credit. Started on demand via CI lease (`qualify_cloud` / `cloud`) and auto-stopped when idle. |
| **`hart-router`** | `192.168.1.1` | OpenWrt router | LAN gateway | Split-horizon DNS and LAN routing. |

---

**hart-server operating role:** This host is primarily a deployment target and a persistent shared-services/storage machine. Preserve its archives, vault, model checkpoints, corpora, databases, OpenBao, Vaultwarden, Qdrant, DNS, game servers, and certificate renewal/distribution when cleaning source or build outputs. Determine ownership and active references before retiring a directory; recovered historical snapshots are archival material.

Routine builds, verification, delivery, and recovery should be reproducible through CI/CD and the existing operator commands, with unchanged inputs reused. The existing primary repositories may be used for focused work that needs the server's local data or services or does not reproduce adequately through CI/CD. Land that work on `main`, retire its temporary copies, and feed repeatable operations back into the normal workflow. The aim is to reduce the operator's need to log into this machine; source access here does not authorize unrelated service or storage changes.

## 3. Execution Boundaries: What Is Automated vs. How the User Executes

A critical architectural rule across Laplace is keeping the four execution planes distinct and friction-free:

### A. One-Time Host Provisioning (Elevated Admin / `sudo`)
- **Linux**: `sudo ./setup.sh` (in `Laplace-Operations`) and `sudo bash deploy/linux/site-host.sh` (in `Laplace`).
- **Windows (`HART-DESKTOP`)**: `pwsh -File setup.ps1` or `Laplace/deploy/windows/Install-IisRuntime.ps1` in an elevated Administrator PowerShell.
- **Windows UAC / ACL Invariant**: On `HART-DESKTOP`, the developer (`HART-DESKTOP\ahart`, SID `S-1-5-21-1955383224-1799969050-1119319971-1001`) works day-to-day in a **non-elevated** shell where Windows UAC filters out `BUILTIN\Administrators` (`S-1-5-32-544`). Every elevated script that configures ACLs on `E:\Data\Laplace`, `D:\Libraries\build\Laplace`, `D:\Data\Laplace`, or `C:\ProgramData\Laplace` **must** explicitly grant `$declared.windows_sid` (`HART-DESKTOP\ahart`) `FullControl` alongside `SYSTEM`, `BUILTIN\Administrators`, and any IIS AppPool/service SID. If permissions ever drift, run `pwsh -File E:\Repositories\Laplace\Laplace-Operations\installation\Repair-DesktopPermissions.ps1` once from an elevated prompt.

### B. Automated CI/CD Delivery (GitHub Actions on Linux Runners)
- Merging to `main` in `Laplace` runs `.github/workflows/laplace.yml` (`Product — main delivery`), which builds, tests, deploys to `hart-server` (staging), verifies runtime health, and promotes the verified bundle to `hart-prod` (production).
- Merging to `main` in `Laplace-Native`, `Laplace-postgres`, `Laplace-Engine`, or `Laplace-MCP` triggers modular build/test and dispatches `main-merged` to `Laplace-Operations` to deploy and verify on `hart-server`.
- **Database Reseed Policy**: Push/merge workflows **never** automatically drop or reseed databases. Destructive resets (`drop` / `reseed` / `nuke`) require explicit manual dispatch or CLI confirmation.

### C. Normal Operator & Developer CLI (Non-Elevated Shell on `HART-DESKTOP` or `hart-server`)
- From any normal terminal directory without needing developer environment variables (`%LOCALAPPDATA%\Laplace\bin`):
  - `laplace-mono --where` / `laplace-mono status` / `laplace-mono ingest document <path>` — Monorepo CLI (`laplace-mono.cmd`).
  - `laplace --where` / `laplace status` / `laplace ingest ...` — Modular Engine CLI (`laplace.cmd`).
  - `laplace-ops runs` — View recent CI/CD runs across all 6 Laplace repositories in one screen (`laplace-ops.cmd`).
  - `laplace-ops deploy -Target desktop` — One-command update for `HART-DESKTOP` (builds native + managed + web + UCI/chess, publishes to `https://dev.hartonomous.com` and `laplace-mono`, runs local DB migrations on `laplace-desktop-app` and `laplace-mono`, and verifies health using the enrolled OpenBao identity).
  - `laplace-ops db -Database <substrate|application|both> -DatabaseOperation <status|migrate|repair|remigrate|recreate> [-Yes]` — Inspect, migrate, repair (recreate after drop), or reset databases without rebuilding the app.
  - `laplace-ops restart-iis` — Cleanly recycle the local IIS site via `app_offline.htm` and verify `/health` and `/ready`.
  - `laplace-ops components -Target desktop` — Rebuilds and packages `Laplace-Native` + `Laplace-Engine` into `laplace.cmd`.
  - `laplace-ops prepare | build | test | start | stop | status | verify -Target desktop` — Local desktop development lifecycle.
  - `laplace-ops status | verify | deploy -Target staging|production -Wait` — Remote environment control and verification.
- From `E:\Repositories\Laplace\Laplace-Operations`:
  - `./installation/Verify-DesktopDatabases.ps1` and `./installation/Verify-PlatformAccess.ps1` — Verify local PostgreSQL, SSPI/TLS, CLIs, and platform admin access.

### D. Browser Web UI & HTTP API (`https://dev.hartonomous.com`, `https://staging.hartonomous.com`, `https://api.hartonomous.com`)
- End-user and operator browser interface for chat, substrate exploration, chess, and platform administration.
- **Ingest Admin Portal & Recipe Studio**: A dedicated, permission-gated workbench inside the Web UI (restricted to `installation-owner` / provider admins on hosted deployments; open to local operators in self-hosted/open-source builds) for managing the generic ingestion pipeline without writing C/C# code:
  - **Corpus Estate & Acquisition**: Browse/edit the corpus list and `order` DAG, add new corpora from the UI, trigger server-side dataset download/unpacking into the corpus estate, and inspect file trees.
  - **Tree-sitter Packaging Inspector & Attestation Explorer**: Run outer and chained inner Tree-sitter grammars over sample records from a corpus to inspect recovered AST nodes, fields, candidate sub-grammars, highway ID collisions (`highway.bin`), and unresolved field obligations.
  - **Laplace-Generated & User-Authored Recipes**: Laplace inspects sample ASTs + values against the substrate and `highway.bin` to propose draft `.recipe` / `.format` definitions; admins (or open-source users) refine recipes in the UI with live `--plan`, `--claims`, and `--no-load` round-trip preview before running assembly-line ingestion.
- **Default Platform Administrators**: The canonical application migration `20260611000000_app_billing.sql` (part of the consolidated 3-migration schema alongside `20260606000000_layer1_database.sql` and `20261015000000_substrate_runtime_grants.sql`) seeds the 3 owner OAuth bindings (`aharttn@gmail.com` Google, `thesaltypatron@gmail.com` Google, `thesaltypatron@gmail.com` Microsoft) as audited `installation-owner` administrators whenever any application database is created, migrated (`up` / `app-up`), or recreated (`nuke` / `app-nuke`). No commit-file ceremony or manual SQL is needed after recreating a database.

---

## 4. Workspace & Engineering Rules

- **Build & Scratch Directories**: Configured via `C:\ProgramData\Laplace\machine.env` (Windows) and `/etc/laplace/machine.env` (Linux). On `HART-DESKTOP`:
  - Source root: `E:\Repositories\Laplace`
  - Build root: `D:\Libraries\build\Laplace`
  - Data / corpora root: `E:\Data\Ingest`
  - Work / runtime state: `E:\Data\Laplace\work` and `E:\Data\Laplace\services`
  - PostgreSQL data / WAL: `E:\Data\Laplace\pgdata` and `D:\Data\Laplace\pg_wal`
- **No Worktree or Script Sprawl**: Work directly on `main` (or a short-lived feature branch cleaned up immediately upon merge). Do not leave orphaned `wt-*` git worktrees, duplicate build directories under `D:\Libraries\build\Laplace`, or one-off repair scripts in the repositories.
- **Communication & Verification**: Keep communication direct, technical, and concise. Describe concrete code changes, verification results, and any remaining blockers. Complete implementation tasks with build and test verification before reporting completion.
- **Zero 5G Bandwidth Burn**: `HART-DESKTOP` operates on a metered 5G connection. Never download, clone, or copy bulk external corpora or large model checkpoints over the internet from `HART-DESKTOP`; always inspect local datasets under `E:\Data\Ingest` or metadata/samples already in the workspace.

---

## 5. Canonical Substrate, Perf-Cache, Chained Tree-sitter Grammars, Assembly-Line ETL & Mold-A-Model Laws

Every agent working in any Laplace repository must obey these non-negotiable architectural invariants:

### A. Division of Ownership: Native C + Perf-Cache vs. Thin C# & SQL Orchestrators
- **Client-Side Decomposition & Reduction (Native C + Perf-Cache)**: It is faster and vastly more efficient to break content down and deduplicate it on the client before talking to PostgreSQL. C# is a **thin orchestrator** over native C (`Laplace-Native` / `laplace_core`) and `Npgsql` binary `COPY` streams—never allocate per-node managed C# heap objects (`Intent`, `KeptRow`, per-row dictionaries) on the hot path, and never hand-roll C# math/BPE/SVD/attribution wheels that duplicate native C.
- **Perf-Cache (`tier0.bin` + `highway.bin`)**:
  - **Bit-Perfect Coordinates (`4 ULP`)**: Comes from compiler options (`-fp-model=precise` / `/fp:precise`, no FMA contraction), CORE-MATH (`cr_atan2`, `cr_hypot`, `cr_cbrt`, `cr_sin`, `cr_cos`), and $2^{53}$ / `fp1e9` fixed-point quantization on $S^3$.
  - **Universal Content Determinism**: Guaranteed by the memory-mapped perf-cache (`tier0.bin` + `highway.bin`). Same content = same 16-byte `blake3` hash everywhere (Broadwell, Raptor Lake, Sapphire Rapids, T2 Mac, Raspberry Pi, or PlayStation), eliminating database round-trips and lookups for the entire Tier-0 Unicode floor and Linguistic Super Highway.
- **Database-Side Set Merge & Continuous Series Updates (Thin SQL + `Laplace-postgres` C Extension)**: SQL is a thin set-based orchestrator over native C extension functions (`laplace_attest_series`, `laplace_attested`, `laplace_attribution`, `laplace_clean_pending_list`, etc.). In `Laplace` (`laplace-mono`), 100% of SQL statements across substrate, application (`app.*`), billing, auth, and chess live in `engine/core/src/sql_catalog.def` and are executed via `NpgsqlCatalog.Command(...)` (enforced by `ReadPathArchitectureGateTests` with zero hand-written SQL exemptions). The monorepo database schema is consolidated into three canonical migrations (`20260606000000_layer1_database.sql`, `20260611000000_app_billing.sql`, `20261015000000_substrate_runtime_grants.sql`). Never pull `consensus` rows back over the socket to update standings on the client, and never maintain parallel drifting SQL stacks.

### B. Packaging vs. Product: Chained Tree-sitter $\rightarrow$ Laplace Grammars (Bidirectional Ingest & Export)
A file is the **packaging** we open to extract the raw **product** (canonical content + source-attested claims), while retaining the packaging hierarchy as provenance trunks (`[metadata, content]`). Because Laplace's Merkle DAG *is* an AST, Tree-sitter and Laplace recipes form a single, bidirectional grammar pipeline across all corpora (WordNet, Universal Dependencies, Wiktionary/Wiktextract, Tatoeba, ATOMIC, HateSpeech, FrameNet, PropBank, VerbNet, ConceptNet, PGN chess, and source code):
1. **Multi-Level Tree-sitter Grammar Chaining (`ts_parser_set_included_ranges`)**:
   - **Outer Packaging Grammar**: Every standardized container format is parsed by its compiled Tree-sitter grammar (`tree-sitter-xml` for WN-LMF/FrameNet/PropBank/UCD, `tree-sitter-json` for Wiktextract/JSONL, `tree-sitter-turtle` for CILI/FrameBase, `tree-sitter-csv`/`tsv`/`psv` for Tatoeba/ATOMIC/HateSpeech/GeoNames/ConceptNet, `tree-sitter-conllu` for UD, `tree-sitter-wndb` for Princeton WordNet, `tree-sitter-pgn` for chess, plus 300+ code grammars).
   - **Inner / Injected Sub-Grammars**: Whenever an outer grammar field contains structured sub-syntax (such as CoNLL-U `FEATS` `Key=Val|Key=Val`, `DEPS`, `MISC`, TSV columns containing JSON objects or pipe-delimited lists, or structured identifier notation like `drop%2:38:00::` / `mcr:ili-30-01976841-v`), the child parser is invoked directly on the parent `TSNode` byte range via `ts_parser_set_included_ranges`, chaining down to UAX #29 text leaves and the `tier0.bin` / `highway.bin` perf-caches.
2. **Compiled Integer Symbol Dispatch (Zero Runtime String Parsing)**:
   - `.format` and `.recipe` files are Laplace semantic grammars. At recipe load time, every node type and field rule is compiled once against `ts_language_symbol_for_name()` and `ts_language_field_id_for_name()` into flat $O(1)$ integer jump tables (`uint16_t TSSymbol` / `TSFieldId` $\rightarrow$ disposition & composition instructions).
   - Worker threads walk the `TSTree` using `TSTreeCursor` + compiled integer jump tables directly over the input byte buffer, reusing thread-local `TSParser` instances (`ts_parser_reset`) and thread-local arenas across records.
3. **Bidirectional AST $\leftrightarrow$ Merkle DAG**:
   - Ingest maps `Bytes -> Chained Tree-sitter AST -> Laplace Merkle DAG + Claims`.
   - Realization / export is the exact inverse (`Laplace Merkle DAG -> AST -> Formatted Bytes`), enabling lossless round-trip reconstruction and cross-format synthesis.

### C. Bidirectional Tier Traversal Law (Leaf-to-Trunk Creation + Trunk-to-Leaf Deduplication)
Tiers are recursive domain altitude (`codepoint` $\rightarrow$ `grapheme` $\rightarrow$ `word` $\rightarrow$ `phrase/sentence` $\rightarrow$ `section/record` $\rightarrow$ `file content tree` $\rightarrow$ `file trunk [metadata, content]` $\rightarrow$ `corpus witness trunk`), enabling two complementary directional optimizations:
1. **Leaf $\rightarrow$ Trunk (Bottom-Up on the Client for Creation & In-RAM Deduplication)**:
   - Always compose bottom-up from the perf-cache leaves (`tier0.bin` and `highway.bin`) through canonical tier boundaries (UAX #29 for text leaves and chained Tree-sitter AST nodes for structured records/files).
   - This guarantees bit-perfect BLAKE3 IDs and $S^3$ coordinates and prevents arbitrary BPE-style fragmentation (`"m iss iss ippi"` vs. `"miss ippi"`): `"mississippi"` is composed once at the word tier from its graphemes/codepoints, and every occurrence in the batch collapses in $O(1)$ into the client's in-RAM node table (`NODE[]` / `staged_set`).
2. **Trunk $\rightarrow$ Leaf (Top-Down Against PostgreSQL for Self-Reducing Subtree Pruning)**:
   - Deduplication against PostgreSQL always proceeds top-down from the highest logical roots (File Trunk $\rightarrow$ Section/Record Trunks $\rightarrow$ Sentences $\rightarrow$ Words), skipping synthetic binary-tree (`blocks_of`) spine nodes.
   - By the Merkle/DAG containment invariant, if a node at tier $T$ already exists in PostgreSQL (or in the client's perf-cache / `staged_set`), **its entire descendant subtree is already recorded and is immediately amputated on the client** (`keep = recorded`). Only novel branches descend to the next frontier and are `COPY`ed to the database.

### D. True Assembly-Line Record Streaming ETL (Hundreds of Thousands to Millions of Records/sec)
Ingestion is a continuous, non-blocking **assembly line** driven by record framing from the outer packaging grammar—never a row-by-row chumper and never a lagged end-of-source "consensus fold":
1. **$O(1)$ Pre-Check**: Probe the file trunk / OS metadata identity before reading or decomposing file bytes; never run a 2-pass (`pass 0` / `pass 1`) full-file decomposition or re-read files from disk.
2. **Single-Pass Grammar-Bounded Record Streaming**: Stream files in a single pass, slicing strictly on outer grammar record boundaries (XML `<LexicalEntry>` / `<Synset>`, Turtle statement block, JSONL line, CoNLL-U sentence block, PGN game, CSV/TSV row). Never split at arbitrary byte offsets.
3. **Parallel Arena-Allocated Record Workers**: Worker threads pull record slices from the ring buffer, parse each record via thread-local `TSParser` + chained sub-grammars + compiled integer jump tables, compose bottom-up into the lock-free `NODE[]`/`VTX[]` table, and accumulate per-witness `(claim, games, score)` series directly in-stream using thread-local arenas reset per record (`0` `malloc`/`free` calls per record). Cross-record internal pointers (`key` / `refer`) resolve against a lock-free concurrent `key_hash -> EntityID` table—never by holding a whole-file AST in RAM.
4. **Continuous Pipelined `stage.*` COPY & In-DB Series Updates**:
   - As each batch of records fills the working window, top-down `Trunk -> Leaf` deduplication prunes known subtrees, and novel `entity` / `physicality` rows + accumulated `(claim, games, score)` series stream via binary `COPY` into `UNLOGGED` `stage.*` tables using bounded connection concurrency (`min(cores, max_pool_size - headroom)`).
   - Never use 16-connection Two-Phase Commit (`PREPARE TRANSACTION` / `COMMIT PREPARED`) on `UNLOGGED` staging tables, and never stall workers waiting for an end-of-source "consensus fold". Standings update in PostgreSQL via set-based `laplace_attest_series` (`stage.consensus_<h>` $\rightarrow$ `public.consensus_<h>`) as batches flow through the assembly line, followed by Hilbert-ordered `stage.*` $\rightarrow$ `public.*` set merge, GIN pending-list cleanup, and atomic `witness` trunk registration.

### E. Explicitly Deprecated & Forbidden Patterns (Do Not Extend or Maintain)
The following legacy patterns in `Laplace-Engine/src/` and `Laplace` are **deprecated** and must be replaced, never extended:
1. **`STree` / `SNode` Intermediate Heap DOM (`Laplace-Engine/src/structure.c`)**:
   - `g_node()`, `STree`, `SNode`, and `s_grammar_split()` (`copy_around` / `graft` / `copy_rec`, which copies 2 MB chunks into `<_>...</_>` buffers and single-threadedly grafts a whole-file DOM in RAM) are **deprecated**. Walk `TSNode` / `TSTreeCursor` directly into `compose()`.
2. **Bespoke Hand-Rolled Format Scanners & Split Codepaths (`structure.c`, `recipe.c`)**:
   - `s_decompose()`, `tier_read()`, `part_end()`, `mini_json()`, `parts_of()`, and the `s_boundary()` single-thread bug (`if (l->ntier > 1 && l->tier[0].quoted) return n;`) in `structure.c` are **deprecated**.
   - Blocking `csv`/`tsv`/`psv` from loading `tree-sitter-csv` (`recipe.c:322`) and maintaining separate hardcoded C walkers (`attest_json_node`, `attest_xml_node`, `attest_delimited`, `attest_code_node` in `recipe.c`) are **deprecated**. All formats use chained Tree-sitter grammars + compiled recipe dispatch.
3. **Runtime String Path Building & Per-Row Heap Churn (`Laplace-Engine/src/say.c`)**:
   - `child_path()` (`snprintf` + `strtok_r` on every AST node), `dis_scan()` linear string loops, `owed()` (`snprintf` on every claim), and per-row `records_of()` heap allocations (`lp_idmap *rm`, `of[]`, `recs`, `at[]`, `ord[]`, `fill[]`, `part[]` allocated per row) are **deprecated**. Use precompiled integer `TSSymbol`/`TSFieldId` jump tables and thread-local arenas.
4. **Two-Pass Stretch Reads, Lagged End-of-Source `standings_write()`, 2PC on Unlogged Tables & Client-RAM Partition Rewrites (`ingest.c`, `db.c`)**:
   - Whole-file slurping (`read_all`), 2-pass stretch reads (`pass 0` + `table_reset()` + `pass 1` in `ingest.c`), and disk re-reads (`ingest.c:503-514`) are **deprecated**.
   - Lagged end-of-source `standings_write()` (`ingest.c:557`, `db.c:195-254`) with `#pragma omp critical` + `DELETE FROM stage.consensus_%x` is **deprecated** ("consensus fold" as a lagged batch-end phase is forbidden).
   - 16-connection `PREPARE TRANSACTION` / `COMMIT PREPARED` on `UNLOGGED` `stage.*` tables, the 528-table startup loop in `stage_open()`, and client-RAM partition rewrites (`L->rewrite` pulling partitions back via `COPY TO STDOUT` in `db.c:448-484`) are **deprecated**.

### F. The Three Proofs & Testing Discipline
- **Proof 1 (`db-roundtrip`)**: Decompose real multi-domain inputs into `entity` + `physicality` and recompose 100% byte-for-byte from PostgreSQL alone while verifying all structural invariants.
- **Proof 2 (`Linguistic Super Highway`)**: Fixed-width bit-banger masks (`upos`, `lexfile`, `deprel`, `vnrole`, `kind`) + cross-source identifier indexing, filtering, and hops (`ili`, `Synset`, `fnframe`, `fnfe`, `fnlu`, `vnclass`, `pbroleset`, `vaframe`). Every highway node is the canonical Tier-0 composition of its identifier string (`i46360`, `oewn-00001740-n`, `abandon.01`, `va:0001f`, `leave-51.2`, `NOUN`), with crosswalk edges baked into `highway.bin`. Generating or using `highway.bin` must **never** require the full raw corpus suite on disk or a `.nodes` sidecar dump; corpus definitions (`skos:definition`, roleset/frame glosses) are attested as claims about those highway identifier nodes during Proof 3.
- **Proof 3 (`Full Corpus Ingestion`)**: Full multi-corpus ingestion on top of Proofs 1 and 2, where cross-source claims collide on the deterministic highway identifier nodes and update standings via `laplace_attest_series`.
- **No Fake Ceremony Gates in Dev CI**: `SaltyPatron/Laplace-Prototype` is the cleanroom test platform for proving core algorithms; active development CI must stay fast and must not block on multi-corpus ingestion gates or mocked/simulated ceremony tests.

### G. BLAKE3-Addressed Multi-Tier Model Export & Mold-A-Model ("The BitTorrent of AI")
- **BLAKE3 Hashes Replace Flat Integer Tokens (`laplace-blake3-v1`)**: Exported GGUF models (`laplace.tokenizer.scheme = "laplace-blake3-v1"`, `laplace.vocab.size`, `laplace.vocab.blake3_ids`) replace flat BPE/SentencePiece integer token IDs with **16-byte `blake3` entity IDs** across **all tiers** of the DAG (codepoint, grapheme, word, phrase, AST node, chess move, concept), locking exported cartridges to Laplace's decomposer + perf-cache. Commercial cartridges are encrypted with `FoundryCartridgeCrypto` (`LPCART01` AES-256-GCM envelope).
- **Mold-A-Model & Royalty Attribution**: Every query receipt and **Mold-A-Model** synthesis computes exact per-witness attribution (`share_bp`, summing to `10000` basis points) from containment provenance (`laplace_attested` / `laplace_attribution` over `physicality` + `witness`) weighted by Glicko-2 Fisher information ($I_i = 1/\sigma_i^2$) inside PostgreSQL + native C, persisted in `app.billing_royalties` and exposed via `GET /v1/billing/royalties`.


