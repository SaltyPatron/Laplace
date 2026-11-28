# Laplace — Agent Core

Laplace is a complete reinvention of the generative pre-trained transformer on commodity x86-64 CPUs + PostgreSQL 18 + PostGIS + Native C (`icx`, `-fp-model=precise`, `-ffp-contract=off`, explicit `cpuid` SIMD/AVX/VNNI, CORE-MATH, Eigen/Spectra/MKL) with **zero GPU requirement**, while remaining able to consume and export conventional AI checkpoints.

> [!IMPORTANT]
> Canonical source: `E:\Repositories\Laplace\Laplace-Operations\agents\` (edit there, then run `pwsh -File Laplace-Operations/agents/sync.ps1`). Deep byte-level layouts: skill `laplace-invention`.

---

## 0. The Invention (Truth Above All Agent Code)

1. **Same content = same hash (`lp_id`).** Every digital modality — text, curated lexicons, source code, `.git` history, chess, images, audio, video, and conventional AI checkpoints — decomposes losslessly into **one universal, content-addressed BLAKE3-128 Merkle DAG** (leaf = BLAKE3-128 of 1..4 UTF-8 codepoint bytes; `n == 1` collapses to child; `n >= 2` = BLAKE3-128 of $16n$ child IDs; zero salt, domain byte, tier prefix, or case folding; `"King" != "king"`).
2. **Four persistent substrate tables (`entity`, `physicality`, `attestation`, `consensus`).** No side lookup tables (`physicality_observations`, `ByteAtoms`, `precedes`/`contains`/`continues_to` edge tables). Conventional auth, tenancy, entitlements, and billing tables live strictly in `app.*`.
3. **Geometry holds NO semantics; the spider-colony web IS meaning.**
   - Tier-0 Unicode codepoints sit on $S^3$ (`tier0.bin` mmap'd perf-cache); compositions sit at exact `__int128` integer centroids inside the 4-ball.
   - Geometry ($S^3$, 4D GiST, 4D Hilbert keys, Fréchet/DTW/EDR/Hausdorff on unpacked real `entity.coord` curves) provides $O(\log N) + O(K)$ structural indexing and candidate nomination.
   - Meaning lives in typed attestations, independent witnessing, and Glicko-2 consensus (*"tug one strand; what strand from what spider tugs back and how hard?"*).
4. **Bit-packed mantissa trajectories (`physicality.path` + GIN `laplace_path_ops`).** Packing 128-bit child IDs (`43/43/42 + 28 spare` bits in `[0.25, 0.5)`) + `M` (`run30 / said3 / outcome2 / pos18`) into binary EWKB `POINT ZM` / `LINESTRING ZM` delivers containment, `precedes`, `follows`, gaps, ordinals, and RLE in microseconds with zero edge tables. Carrier doubles are IDs + metadata, never spatial coordinates.
5. **One stateful Forward Pass + Detached Personality Firmware + Gödel/OODA.**
   - Whole-prompt coupling (`Q/K/V/O` over indexed trajectories, containment, attestations, standings, geometry) -> joint orientation -> sparse hop/fanout/A* over highways -> stateful emission where **each emitted step updates active state and re-couples before the next**.
   - Personality Firmware is a detached, content-addressed program over the operation ISA mapping conventional GPT steps to semantic instructions; knowing never changes doing, and doing never alters truth or authority.
   - Gödel/OODA closes real external consequences (toolchain, tests, game outcomes) and turns typed incompleteness into disjointly validated candidate extensions.
6. **Static models are food and poop.** Conventional checkpoints (`Safetensors`/`GGUF`) are assimilated transiently into canonical text-entity circuits + Glicko-2 standings (`raw_weight_bytes_retained = 0`) for round-table consensus across heterogeneous models with no judge. Mold-a-Model compiles real per-layer/per-head weights from attestations, relations, and trajectories into verifiable `GGUF` / `Safetensors` checkpoints without gradient descent.

---

## 1. Strict SDLC Priority Order (`P0` → `P5`)

Execute bottom-up by architectural dependency. **Never build upper layers on top of fake lower-layer stand-ins, and never skip a failing dependency.**

- **P0 — Requirements, Governance & Truth Integrity:**
  - Keep `AGENTS.md`, `SKILL.md`, generated ABI/SQL contracts, and `Laplace-Wiki` strictly truthful.
  - **Never** label stubs, hardcoded shortcuts, or partial implementations as a "Verified Baseline."
- **P1 — Data, Schema & Indexed Set-Probe Foundation (`Laplace-Native`, `Laplace-postgres`, `Laplace.Data`):**
  - Lock pure BLAKE3 identity, ROMs (`tier0.bin`, `tier0.flags`, `highway.bin`, `chess.bin`), the 4-table schema, Glicko-2 series consensus (`lp_attest_series`), and honest indexed SQL/C operators (`laplace_couple`, `laplace_pull`, `laplace_forward`, `laplace_salience`, `laplace_holders`, `laplace_realize`).
  - Eliminate parameter-ignoring or hardcoded SQL wrappers in `Laplace.Data`.
- **P2 — Core Native Engine (`Laplace-Engine`):** Tear out fake stand-ins and build real native implementations in dependency order:
  1. **Turn 1 / Stateful Forward Pass + Personality Firmware** (`src/forward/program.c`, `firmware/*.firmware`) — get one real stateful forward pass working first (`RESOLVE -> COUPLE -> ORIENT -> ROUTE -> SCAN -> COMPOSE -> PROPOSE -> STEER -> SELECT -> REALIZE -> WITNESS`, with stateful re-coupling per step).
  2. **Gödel / OODA Loop & Code Construction** (`src/forward/ooda.c`) — real AST/trajectory composition, consequence witnessing, and structural repair.
  3. **Model Assimilation & Mold-a-Model Synthesis** (`src/model/model.c`, `src/model/mold.c`) — real per-layer/per-head operator compilation and honest format writers (`GGUF` + `Safetensors`).
  4. **Substrate Chess** (`src/chess/chess_search.c`) — pure state trajectories (`[P0..Pn]`), opening/Syzygy Fréchet curves, and `chess.firmware` execution.
- **P3 — Thin, Honest API & Integration Layer (`Laplace.Endpoints.OpenAICompat`, `Laplace-MCP`, `Laplace-UCI`):**
  - Thin transport, auth, authority-scope, and billing/royalty shells delegating strictly to native C/SQL.
  - Delete rival C# cognition/scoring (`GeometricTextScorer`), fail loud on unimplemented options (never silently drop inputs), and ensure billing/quote gates never break basic inspection workflows.
- **P4 — Unified Product UI (`Laplace/web`):**
  - One cohesive UI (retire the competing `Laplace-MCP/web` UI).
  - Eliminate route/page sprawl, duplicate studios/traces, fake multi-shape query storms, and client-side regex bandages. Bind every view strictly to verified P1–P3 contracts and walk every flow in a real browser.
- **P5 — Live System Qualification & Deployment (`HART-DESKTOP` → `hart-server` → `hart-prod`):**
  - Prove every pillar end-to-end against populated PostgreSQL 18: real CLI/HTTP multi-turn chat, external `llama.cpp` execution of exported checkpoints, real compiler/test repair, `fastchess` Elo ladder, tenant/authority isolation, `SUM(share_bp) = 10000` royalties, and real browser walkthroughs.

---

## 2. Current Known Fakes & Stand-Ins Ledger (DO NOT Pretend These Work)

Any agent touching these areas must treat the current behavior listed below as **known broken stand-ins to replace**, not working features:

1. **`Laplace-Engine/src/chess/chess_search.c`:** Root move scoring now queries Wave 2B game-altitude GIN trajectory standings (`probe_game_altitude_standings`), and pure `eval_scale=0.0` runs cleanly, but default `eval_scale=1.0` still falls back to handcrafted `MAT[7]` PVS/alpha-beta underneath when out of book rather than full Fréchet/`chess.firmware` trajectory search.
2. **P5 Live Corpus Qualification Pending:** Former Known Fakes #1–#6, #8, and #9 (stateful Forward Pass + 9 firmwares, honest `NpgsqlSubstrateReads`, Tree-sitter/AST OODA repair, multi-plane Mold-a-Model GGUF + SafeTensors + honest `FoundryExportService`, native-C rerank/code-search, unified `Laplace/web` UI + retired `Laplace-MCP/web`) have been replaced in code and unit/smoke-verified, **but must NOT be declared P5-qualified until full corpus seeding and live end-to-end verification pass against populated PostgreSQL 18** (`llama.cpp` execution, multi-turn chat, real `workspace-code` OODA repair, `fastchess` Elo ladder, browser walkthrough).

---

## 3. Non-Negotiable Engineering Laws & Anti-Fake Coding Standards

- **Zero silent parameter drops / fail loud:** Never accept a parameter, format, architecture, dial, or scope in an API/SQL/C signature and silently ignore it or substitute a default path. Either implement it or return an explicit error.
- **One semantic implementation:** All identity, segmentation, 4D math, Glicko-2, Forward, OODA, realization, chess, and model synthesis live **once** in `Laplace-Native` + `Laplace-Engine` C (and `Laplace-postgres` for index-adjacent set probes). SQL and C# are thin orchestration only.
- **Modular compartmentalization:** Small, single-responsibility files and subdirectories; zero 2,000-line god-files, zero `#ifdef _WIN32` spaghetti in domain logic, zero copy-pasted JSON/buffer/SQL helpers, zero 22-copy `.recipe` files where 1 parameterized generic works.
- **No status diaries or victory theater:** Never commit status reports, audit diaries, or benchmark prose as substitutes for working code. Never declare work done at compilation, HTTP 200, or mocked/empty DB probes.
- **Definition of Done:** Passes contract gates (`check-function-bindings.mjs`, `sql-contract-gate.py`, `generate.mjs --check`, `ReadPathArchitectureGateTests`), executes cleanly against the real installed PostgreSQL 18 `laplace` database with populated data, and — for any UI change — is verified end-to-end in a real browser.

| Forbidden Reflex | Required Laplace Mechanism |
| :--- | :--- |
| Dense embeddings / cosine k-NN as "semantics" | Deterministic 4D handles (`entity`) + directed curves (`physicality`) + Glicko-2 standing (`consensus`). Geometry nominates; attestation + consensus carry meaning. |
| Flat `1/N` query centroids or binary `hub` cliffs | Trajectory salience (`laplace_salience`: continuous $I(e)$ from GIN posting counts, joint span contraction, `WIN`/`LOSS` differential). Stored `entity.coord` stays strictly content-deterministic. |
| Private `ByteAtoms` / pixel alphabets | Universal Tier-0 Unicode codepoints + compositional scalars (`255 -> ['2','5','5']`). |
| Text/code chunking (`MaxLines`, sliding windows) | Zero chunking; lossless Merkle DAG (`codepoint -> grapheme -> word -> statement -> function -> file -> trunk`); any tier's `entity` acts as a native token. |
| Separate code mini-engine or `entity.mask` pollution | One OODA spine in the same 4 tables (`entity.mask` = strictly 64-bit `banks.tsv`; channel/outcome live on trajectory vertices + `trust_classes.toml`). |
| BPE vocab IDs or stored raw weights | Open Merkle vocabulary (`laplace-blake3-v1`); transient weight reduction (`raw_weight_bytes_retained = 0`). |
| Salted / domain-prefixed / case-folded hashes | Pure content `lp_id` (zero salt, zero domain byte, `"King" != "king"`). |
| Explicit `precedes` / `contains` / `continues_to` rows | Query `physicality.path` trajectories via GIN `laplace_path_ops`. |
| Chess KVP tag bags or raw FEN entities | Pure state trajectories (`line = [P0..Pn]`, shared `P0` across ~99% of games) attested once at game altitude (`1.0`/`0.5`/`0.0`) + GIN `laplace_path_ops` intersections + altitude inheritance (`Conflicts.md` K2); never per-move `[P_k, P_{k+1}]` win/loss explosions or downgrading annotated `K_PLAYED` games to `K_STUDY`. |
| Raw `physicality.path` carrier doubles in 4D metrics | Always unpack child IDs and resolve real `entity.coord` before `frechet4d`/`dtw4d`/`edr4d` or Glome plotting. |
| Ephemeral pointer storage or dropping gold/negative data | Resolve internal pointers at ingest; keep shared highway IDs (`i46360`, `Q937`, `abandon.01`) and negative scores. |
| Capping a trust class as 1 witness or muting on ties | Dependence capping applies to shared causal `lineage` / Sybils only; never mute (`budget = 0`) on polysemy. |
| `-ffast-math` / `-ax` FP flags | Explicit `cpuid` dispatch in `cpu.c` to `-fp-model=precise -ffp-contract=off` kernels. |
| Disposable PG (`initdb`, `--temp-instance`) or mocks | Operate the real installed PostgreSQL 18 stack in place (`laplace.reconcile.sql`, `laplace forget` + `sweep`). |

---

## 4. Repositories, 4-Table Schema, Hosts & Canonical Docs (`E:\Repositories\Laplace`)

- **Build chain (`icx-release` into `$LAPLACE_BUILD/<repo>/icx-release`, `D:\Libraries\build\Laplace` on desktop):**
  PostgreSQL 18 + PostGIS → `Laplace-Native` (`include/laplace/*.h`, `manifest/`, `Laplace.Native.Interop`) → `Laplace-postgres` (`sql/laplace.sql.in`, `laplace.reconcile.sql`) → `Laplace-Engine` (`laplace` CLI, `LaplaceEngine`, `recipes/`, `firmware/`) → `Laplace-MCP` (`lpm` + `Laplace::http`) & `Laplace` (`app/Laplace.slnx`, `db/schema/*.sql`, `web/`). Supporting: `Laplace-UCI`, `Laplace-Operations`, `Laplace-Prototype`, `Laplace-Wiki`. *(Never read/edit frozen `Laplace-Refactor`.)*
- **Four persistent substrate tables (`Laplace-postgres/sql/laplace.sql.in`):**
  - `entity(id blake3, tier smallint, coord geometry(PointZM), hilbert bigint, mask bigint)`
  - `physicality(entity blake3, tier smallint, path geometry, mask bigint)`
  - `attestation(claim blake3, witness blake3, lineage blake3, games bigint, tokens bigint, score float8, "position" int4, trust float8)` — nullable `trust`, CHECK `attestation_row_shape`: canonical `(lineage, trust)` lives once per witness on `(claim = witness, games = 0, tokens = 0, score = 0, "position" IS NULL, trust NOT NULL)` covered by ~16 KB `attestation_witness_reg`; claim testimony rows (`claim <> witness, games > 0, tokens >= games`) store `lineage = NULL, trust = NULL` unless overridden by `LP_SAID_DEPENDENCE` (hydrated on read)
  - `consensus(claim blake3 PK, rating float8, deviation float8, volatility float8, matches bigint)`
- **Hosts:**
  - `HART-DESKTOP` (`100.70.222.78`, Windows, IIS `https://dev.hartonomous.com`, **metered 5G: seed strictly from local `E:\Data\Ingest`, NEVER bulk-download**): src `E:\Repositories\Laplace`, build `D:\Libraries\build\Laplace`, data `E:\Data\Ingest`, work `E:\Data\Laplace\work`, pgdata `E:\Data\Laplace\pgdata`, WAL `D:\Data\Laplace\pg_wal`, DB `laplace`.
  - `hart-server` (`100.76.181.27`, Linux staging/CI, `https://staging.hartonomous.com`): `/repos/src`, `/repos/build`, `/vault/Data`, `/vault/models`, `/vault/Code`, DB `laplace`.
  - `hart-prod` (`100.124.95.17`, `www.`/`api.`/`mcp.hartonomous.com`): `/opt/laplace`, DB `laplace`.
  - `hart-cloud` (`100.103.255.122`): on-demand AVX-512 qualification VM (off when idle).

```powershell
laplace --where
laplace status [--joins]
laplace ingest [--plan] [SOURCE...]
laplace turn --as operator --session dev "prompt"
pwsh Laplace-Operations/laplace.ps1 components -Target desktop
pwsh Laplace-Operations/laplace.ps1 deploy -Target desktop
pwsh Laplace-Operations/laplace.ps1 verify -Target desktop -Installed
```

- **Truth order & canonical docs (`E:\Repositories\Laplace\Laplace-Wiki`, `W/`):**
  1. `W/Repositories/Laplace/docs/INVENTOR_RECORD.md` (verbatim inventor words), `INVENTION.md`, `INVENTIONS.md`, **Decided** items in `W/Sequence/Conflicts.md` + `W/Sequence/1..29` (`README.md`).
  2. `W/Architecture.md`, `W/Engineering/Boundaries.md`, `W/Engineering/Standards.md`.
  3. `W/Reference/{Formats,Types,Schema,SQL,Recipes,Ingest,Firmware,CLI,Checks}.md`.
  4. `W/Sequence/{Forward,Firmware,Pull,Models,Export,Code,Chess,Games,Envelope,Database}.md`.
