# Laplace — Agent Core

Laplace is a deterministic, content-addressed, recursively compositional substrate: every modality (text, code, structured corpora, chess, images, audio, model weights) decomposes losslessly into a BLAKE3 Merkle DAG with 4D $S^3$ / 4-ball geometry, attributable containment provenance, and Glicko-2 consensus standing. Native C (`icx`, precise FP, no FMA contraction, CORE-MATH) computes; PostgreSQL 18 + PostGIS + the `laplace` extension stores and indexes; SQL and C# only orchestrate.

> [!IMPORTANT]
> This file holds **stable laws and navigation only**. Versions, counts, column lists and line numbers drift weekly — always read them from the source of truth named below; never quote numbers from memory. When this file and the code disagree, the **code wins**; record the drift (§9).
>
> Canonical source: `E:\Repositories\Laplace\Laplace-Operations\agents\` (edit there, then run `agents\sync.ps1`). Deep byte-level reference: skill `laplace-invention`.

---

## 0. How to work on Laplace (use Laplace to build Laplace)

1. **Search the substrate first, then read the file.** The `laplace-code` MCP server indexes every Laplace repo and the Wiki as a multi-tier DAG. Both servers are HTTP (Bearer auth), not stdio: `laplace-code` = `https://dev.hartonomous.com/modular/mcp`, `laplace` = `https://dev.hartonomous.com/mcp`. If your tool list has no `laplace-code` tools, the server is not registered for your client; register it rather than working without it.
   - `code_search` (`scope: code|docs|all`) → ranked file + line ranges; `code_where` (definition), `code_callers` / `code_callees` (call graph), `file_outline` (symbols of one file).
   - `ingest_status` → what the database holds, running sessions, per-source ingest logs. `recipe_catalog` / `recipe_plan` → recipe coverage for a source.
   - `search`, `hop`, `translate`, `degrees`, `pull`, `forward`, `turn`, `shape`, `provenance`, `library`, `catalog` → exercise the actual read path (doubles as a smoke test).
   - `code_search` takes a few seconds; batch queries, keep `limit` small. `code_where` / `code_callers` / `code_callees` / `file_outline` read the `workspace-code` ingest in the database and return nothing until that source is ingested. Fall back to `Select-String` / `git grep` for exact symbol lookups.
   - The `laplace` (monorepo) MCP server is for `api` / `op` (SQL catalog discovery), `health`, `source_status`, `pipeline`, `facts`, `taxonomy`, `walk`. Do not use its `code_search`.
2. **Truth order:** code (`laplace.h`, `sql/*.sql.in`, Engine `src/`) > the engineering standard (`Laplace-Wiki/Engineering/Standards.md`, `Boundaries.md`; binding law for how code is written, see item 6) > Wiki `Reference/` (as-built) > Wiki `Sequence/` (spec & build order) > Wiki `Sequence/Conflicts.md` (decided disagreements) > everything under `Laplace-Wiki/Repositories/` (moved, often historical).
3. **Check repo state before editing.** Several repos routinely carry large uncommitted WIP (`git -C <repo> status --short`). Never revert, stash, or "clean up" changes you did not make.
4. **Ignore** `Laplace-Refactor` as a code repository: it is frozen, so never build, modify or run CI in it. Its architecture docs promoted to the Wiki `Engineering/` section are binding (item 6); everything else there is history. Also ignore `Laplace-Wiki/Repositories/**/archive/`, dated `2026-*` audit/handoff/verification notes, and `Workspace/` scratchpads unless investigating history.
5. **Verify claims you write down.** Every path, symbol, flag, or number you put in a doc or agent file must be confirmed in code in the same session.
6. **Read the engineering standard before writing or reviewing code:** `Laplace-Wiki/Engineering/Standards.md` and `Engineering/Boundaries.md` (mandatory, every language). Core laws:
   - One semantic implementation, in Native C and the PostgreSQL integration; SQL and C# only orchestrate generated typed surfaces and never hold a second copy of identity, composition, evidence, ranking or realization.
   - Generics: a module supplies only its varying law (metadata, validation, kernel, schema, conformance vectors); source, language and modality names never select another engine branch.
   - Fixed-width types at every persisted, generated, wire and ABI boundary; ownership, nullability, byte order and overflow are explicit.
   - No semantic algorithms, recursive search, cursors or RBAR in SQL; set-oriented, prepared, schema-qualified plans only, with no per-row crossings on batch paths.
   - Contracts generate identifiers, ABI declarations and bindings; nothing repeated is hand-copied.

---

## 1. Repositories (`E:\Repositories\Laplace`, GitHub `SaltyPatron`) and where truth lives

| Repo | Owns | Source of truth for |
| :--- | :--- | :--- |
| `Laplace-Native` | Deterministic C library | `include/laplace/laplace.h` (+ `tier0.h`, `highway.h`, `consensus.h`, `geometry.h`); `manifest/banks.tsv`, `manifest/slots/*.tsv`, `manifest/trust_classes.toml`, `manifest/function_bindings.tsv`; tier-0 generator `tools/tier0.c` |
| `Laplace-postgres` | `CREATE EXTENSION laplace` | Version: `laplace.control` `default_version`. Schema/DDL: `sql/laplace.sql.in`. Indexes, staging, consensus SQL: `sql/operations.sql`, `sql/api.sql`. C: `laplace_pg.c`, `couple.c`, `provenance.c`, `attribution.c`, `gin_path.c`, `gin_mask.c`, `gist4d.c`, `realize.c`, `walk.c` |
| `Laplace-Engine` | `laplace` CLI, recipe compiler, record VM, ingest/loader, native reader | Command table `src/op.c`; recipes `recipes/` (`.recipe`, `.format`, `formats/grammars/`); firmware `firmware/` |
| `Laplace-MCP` | Native `lpm` MCP/HTTP server + `Laplace.Modular.Web` IIS host | MCP tool list: `native/api_mcp.c` (`TOOLS`); routes: `web/Program.cs` |
| `Laplace` (monorepo) | C# app, OpenAI-compat API, `/mcp`, Recipe Studio, Chess Lab, Foundry, billing, web UI | SQL: `engine/core/src/sql_catalog.def` → `sql_catalog/*.def`; declared schema: `db/schema/`; MCP tools: `app/Laplace.Endpoints.Mcp/SubstrateTools.cs` |
| `Laplace-Operations` | Host setup, `laplace.env`, `laplace-ops` controller, CI ingest | `laplace.ps1` (Windows ops), `laplace-ops` (Linux bash), `ingest.sh`/`ingest.ps1`, `installation/*.json`, `.github/workflows/ingest.yml`, `agents/` (these files) |
| `Laplace-UCI` | UCI WebSocket relay / Lichess connector | `configuration.example.json`, `installations/*.json` |
| `Laplace-Prototype` | Native proofs bench | `tests/proof_native.c`, `run-proofs.ps1` |
| `Laplace-Conventional` | Conventional-model training/eval baselines | `train.yml`, `verify.yml`, `scripts/` |
| `Laplace-Wiki` | **All documentation** (repo READMEs are 3-line stubs pointing here) | `README.md` page tree; own rules in `Laplace-Wiki/AGENTS.md` (`python3 tools/check_wiki.py` before commit) |
| `Laplace-Refactor` | **Frozen** — never build, modify, or run CI | — |

Build order: PostgreSQL → `Laplace-Native` → `Laplace-postgres` → `Laplace-Engine` (→ MCP / monorepo). Every C repo has CMake preset `icx-release` (Ninja, output `$LAPLACE_BUILD/<repo>/<preset>`); Native also has `icx-debug`, `gcc-release` and ctest presets. postgres tests: `ctest` → `postgres_regress` (cases in `tests/regression_cases.txt`). Engine tests: mostly `EXCLUDE_FROM_ALL` targets in `CMakeLists.txt` + scripts in `tests/`; CI recipe in `.github/workflows/laplace.yml`.

---

## 2. Substrate laws (stable; details in skill `laplace-invention` and `laplace.h`)

- **Identity** `lp_id` = first 16 bytes of BLAKE3-256. No salt of any kind (domain, tier, type, recipe, version, source, position). Codepoint: hash of its UTF-8 bytes (surrogates in generalized 3-byte form).
- **Singleton collapse:** a 1-child composition *is* the child (`lp_ref_compose`: `*out = ch[0]`) — no re-hash, no wrapper, no tier bump.
- **Merkle height:** for $n \ge 2$ children, `id` = BLAKE3-128 over the $16n$ concatenated child IDs in order (repeats included), and `tier = 1 + max(child.tier)` (`lp_tier_above`, enforced inside compose regardless of caller). **Tiers are heights, never roles**: `[H,e,l,l,o]` is tier 1; a sentence of tier-1 words is tier 2. Any doc saying "grapheme = 1, word = 2, sentence = 3" is wrong.
- **Universal convergence:** the same content is the same entity everywhere (word, identifier, title, substring, digit run `[2][5][5]`, $2\times2$ pixel patch). Never mint numeric/media values as new atoms; never hash invented strings (`language:eng`, `source/X/v1`); a file is always `[metadata, content]`, never a byte blob.
- **Tier-0 floor:** 1,114,112 × 64 B `lp_tier0_record` (`id`, `int64 m[4]` fixed point $m/2^{53}$, `hilbert`, DUCET `rank`, pad). DUCET rank $r$ → $r$-th Hilbert-ordered Super-Fibonacci point on $S^3$ (CORE-MATH `cr_sin`/`cr_cos`, correctly-rounded `sqrt`), nudged inside $\sum m^2 \le 2^{106}$. Shards: `ascii`, `utf`, `cjk`, `ext` (select with `LAPLACE_TIER0_PROFILE` or `@profile` path suffix; IDs/coords identical in every profile). Flags: 32 B/codepoint, same shards.
- **Centroid:** exact `__int128` integer mean truncated toward zero → compositions lie strictly inside the 4-ball. Centroids are many-to-one (`[c,a,t]` and `[a,c,t]` share one); IDs and trajectories are not.
- **Hilbert:** 4D 16-bit Skilling + `pdep`; stored as `h ^ 0x8000000000000000` so signed `bigint` order = unsigned Hilbert order.
- **Packed path vertices ≠ geometry.** `physicality.path` is `POINT ZM` / `LINESTRING ZM` EWKB whose X/Y/Z mantissas carry the child `lp_id` (43+43+42 bits, values in [0.25, 0.5), 28 spare bits = 4-bit tag + 24-bit payload) and whose M carries run length (30 bits), role `LP_SAID_*` (3 bits), outcome (2 bits) and position (18 bits). This is a deliberate exploitation of PostGIS storage, EWKB/binary COPY, TOAST and index machinery as an ordered ID-sequence carrier. Its axes are hash bits that uniformly fill the cube [0.25, 0.5)³, so spatial predicates on `path` answer **identity** questions (exact vertex match, ID-bit ranges) and never **shape**. Multi-vertex bounding boxes cover almost the whole cube, and distance on carrier doubles is meaningless. (UI symptom: carrier values drawn as coordinates show up as a square "hologram" on the glome.) Trajectory similarity = unpack IDs → resolve each child's real coordinate (`entity.coord` / `tier0.bin` / `highway.bin`) → measure (see `laplace_couple`, `laplace_project_path`). The raw `laplace_frechet4d` / `dtw4d` / `edr4d` SQL functions measure **the geometry they are given** — pass realized curves, not `path`.
- **Segmentation:** tailored UAX #29 — separators always separate at word tier (`3.14` → `[3][.][14]`, `a_b` → `[a][_][b]`). Adjacent repeats collapse into run length; periodic blocks factor leftmost-shortest, recursively (`banana` → `b [an]×2 a`).
- **Banks are fields, not bitmaps:** `manifest/banks.tsv` assigns disjoint fields in one 64-bit `mask` carrier. `kind` is a flag set (bit = slot); every other bank (`upos`, `lexfile`, `deprel`, `vnrole`, `wnframe`, …) is an enum storing slot+1 (0 = absent). Banks filter; they are never word entities and never concept hops. Unicode property records (flags) stay 256-bit and separate.
- **Recording law:** persistent cross-release identifiers (CILI `i46360`, ISO 639, PropBank `abandon.01`, VerbNet `give-13.1`, FrameNet, VerbAtlas, GeoNames, Unicode scalars) are entities / highway nodes. Release-local packaging pointers (WordNet offsets, sense keys, `oewn-` IDs, CoNLL-U `id`/`sent_id`, XML IDs, row numbers, byte offsets) are resolved during ingest (`key` / `refer` / `type`) and recorded **nowhere**. A lexical sense is `[lemma, language, identifier]`. Relations come from the source's semantics, never markup attribute names or invented `HAS_*` strings.
- **Highway** (`$LAPLACE_HIGHWAY`): shards `core` (banks), `wordnet` (ILI), `frames` (roles); `lexical`/`full` = all (`LAPLACE_HIGHWAY_PROFILE`). Building or using it never needs raw corpora on disk.
- **Geometry is shape, not meaning.** A coordinate on $S^3$ / in the 4-ball is the shape of a Merkle DAG segment: Tier-0 placement plus centroids of constituents, and the realized trajectory through them. Closeness in 4D means *compositional / morphological* similarity (shared or similarly-placed constituents, similar trajectory). It is **not** an AI-embedding "semantic neighbor". Meaning and relatedness come from attested evidence: relations, usages, observations, examples, highway identifiers, all weighted by consensus. Never present GiST k-NN on `entity.coord` as semantic similarity.
  - **Why shape matters ("find one bug, fix 50,000"):** ingest error logs from 10,000 companies. Logs from 300 repositories have realized trajectories with very low Fréchet distance, meaning the same structural failure. Attested fixes exist for N of them (physicalities under their witnesses, with standing). Propose those fixes to the rest, try them, and fold the outcomes back into consensus. Shape finds the candidates; attestation and consensus decide what's true. Embeddings can't do this deterministically, losslessly, or with attribution.
- **No chunking, ever.** Every tier of every modality is recorded, so retrieval works at any tier:
  - containment (GIN `laplace_path_ops`; the lowest common ancestor is the retrieval unit);
  - shape (GiST on `entity.coord`, realized trajectory comparison);
  - meaning (attested hops and relations weighted by consensus).

---

## 3. Database (verify every detail in `Laplace-postgres/sql/laplace.sql.in` + `operations.sql`)

- `entity` and `physicality`: 256 range partitions each on byte 0 of the ID. `entity` carries `id`, `tier`, `coord` (PointZM), `hilbert`, `mask bit(64)`. `physicality` carries `entity`, `tier`, `hilbert`, `path`, `mask`.
- Indexes: GiST on `entity.coord` with Laplace opclasses (`laplace_point4d_ops`, `laplace_angular4d_ops`), Hilbert btrees, and one composite GIN `(path laplace_path_ops, mask laplace_mask_ops)` on `physicality` (+ partial GINs). Read the `WITH (...)` settings from `operations.sql`; do not quote them from memory.
- `consensus`: 16 partitions, Glicko-2 `rating`, `deviation`, `volatility`, `matches`.
- **Witnesses and provenance** (Wiki `Sequence/Sources.md` 9.4, `Sequence/Attestations.md` 12.2–12.3, `Semantics/Attestations.md`):
  - **The trunk node is the source is the witness.** WordNet, UD and Wiktionary are each one source trunk `[source record, its files' trunks in path order]`: one Merkle DAG of source → files → records, stored as `entity` + `physicality`. A treebank or lexicon inside it is a file under the trunk, never a witness of its own. **No witness table**: the trunk ID is the witness.
  - **Physicalities are usages, observations and examples**: how building blocks combine. Building blocks are content and content is building blocks. A physicality is **not** an attestation.
  - **Attestations are their own records** (`attestation(claim, witness, lineage, games, tokens, score, "position", trust)`, partitioned by claim, `UNIQUE NULLS NOT DISTINCT (claim, witness, lineage)`, in `sql/laplace.sql.in`). There is one per claim, witness and lineage; the witness is the source trunk ID, and a witness is known when it has attestations (no registry). WordNet attesting "dog is a noun" is a different attestation from Wiktionary, UD or ConceptNet attesting the same claim.
  - **Consensus** is the round table: every attestation of a claim, folded into one Glicko-2 standing per claim.
  - **Trust** is the source's declared class (`manifest/trust_classes.toml` → native law), carried with the declared lineage on each attestation row. The class prior seeds and bounds each attestation ($s_{eff} = 0.5 + t(s - 0.5)$, `Sequence/Conflicts.md`). Lineage keeps derived copies from counting as independent witnesses.
  - Persistent tables: `entity`, `physicality`, `attestation`, `consensus`.
- Database crossings are $O(\text{tiers})$, never $O(\text{rows})$. Standings update **inside PostgreSQL**. Never pull `consensus` rows to the client to update them.
- Never `DROP EXTENSION laplace CASCADE`. Never reset or reseed shared databases (`laplace`, `laplace-mono`) without explicit instruction.
- Monorepo: 100% of SQL lives in `engine/core/src/sql_catalog.def` (+ `sql_catalog/*.def`) and runs via `NpgsqlCatalog.Command(...)`. `ReadPathArchitectureGateTests` enforces zero hand-written SQL. Schema = ONE declaration in `db/schema/*.sql` (idempotent `CREATE ... IF NOT EXISTS`, `CREATE OR REPLACE`, reconciling grants), applied in full on every create and every deploy by `Laplace.Migrations`; there is no run-once migration journal (`schemaversions` is gone). Change the declaration in place; never add a per-change file. `status` reports drift between the declaration and the live catalog.

---

## 4. Ingest architecture (Engine; current shape)

Bytes → chained Tree-sitter AST → compiled recipe bytecode → record VM → lock-free node table → top-down dedup → binary COPY into per-run `stage.*` tables → in-DB merge + series fold.

- **Recipe compile once:** `recipe.c` (parse/select) → `recipe_compile.c` (`say_compile`) + `structure_compile.c` (binds `ts_language_symbol_for_name` / `ts_language_field_id_for_name` → integer jump tables) → immutable `recipe_program.h` bytecode. Every field in a curated source needs an explicit disposition (`content`, `key`, `refer`, `type`, `metadata`, `omit`, `perfcache`, `own`, `thing`, `attest`, `relate`, `pair`); unhandled fields stop ingest.
- **Stream records, single pass:** `structure_records.c` slices on outer-grammar record boundaries (XML element, Turtle block, JSONL line, CoNLL-U sentence, PGN game, CSV row) with thread-local `TSParser` (`grammars.c`, `ts_parser_reset`). Injected sub-grammars use `ts_parser_set_included_ranges` on the parent node's range (`structure_decode.c`). Scheduling: `record_stream.c`.
- **Record VM:** `say.c` `record_execute` + `record_*.c`, `recipe_says.c`; per-worker arenas (`record_worker.h`), zero per-record malloc. Cross-record keys resolve through the concurrent key table (`table.c`).
- **Node table:** `table.c` lock-free open addressing, CAS claim, `compose`. Every repeated substructure collapses in RAM.
- **Dedup top-down:** `db.c` `prune`. If a node exists (DB, perf-cache, or staged set), its whole subtree is amputated. Skip synthetic `blocks_of` spine nodes.
- **Load:** `db.c` / `db_metadata.c` → binary COPY (`pg.c`) into per-run, per-lane `stage.lp_*_<xid>_<lane>` tables (lanes = `min(cores, pool, available)`) → `laplace_merge_stage`, `laplace_accumulate_series`, `laplace_attest_stage`, partition GIN cleanup. Follow the calls in `db.c` rather than this list when changing it.

**Removed — never reintroduce:** `STree`/`SNode`/`g_node`/`s_grammar_split` heap DOM; hand-rolled scanners (`s_decompose`, `tier_read`, `part_end`, `mini_json`, `parts_of`, `s_boundary`); per-format C walkers (`attest_json_node` etc.) and any csv/tsv bypass of Tree-sitter; runtime string path building (`child_path`, `dis_scan`, `snprintf` per claim) and per-row heap churn; two-pass reads; lagged end-of-source `standings_write`; 2PC on unlogged tables; 528-table stage startup; client-RAM partition rewrites (`COPY TO STDOUT`).
**Still present, to be retired:** `read_all` whole-file slurp for non-streamable recipes (`recipe_decompose.c`); in `Laplace-MCP`: heading/paragraph chunker `admit_text_blocks` (`native/lpm_index.c`), in-RAM BM25 (`native/lpm.c`), `workspace.lpm` sidecar (`web/NativeRuntime.cs`). Target: all MCP search over `entity` + `physicality` + `consensus` + perf-caches.

**C# is a thin orchestrator.** No per-node managed objects on hot paths; never reimplement native math, BPE, SVD, or attribution in C#.

---

## 5. Forward program & proofs

- Forward: `RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS`. Three orthogonal axes: Knowledge (substrate), Authority (principal scope), Compute Envelope (hop/fanout/frontier/memory budget). Detached personality firmware (`Laplace-Engine/firmware/*.firmware`) is rated by consequences and never mixed into substrate truth. Spec: Wiki `Sequence/Forward.md`, `Sequence/Firmware.md`.
- Proofs: (1) round-trip: decompose → recompose byte-for-byte from PostgreSQL alone. (2) highway: bank/identifier filtering and hops without raw corpora. (3) full corpus ingestion: cross-source claims collide on highway identifiers and fold into consensus.
- Export `laplace-blake3-v1` (monorepo `FoundrySynthesis.Export.cs`): GGUF with 16-byte BLAKE3 entity IDs as vocabulary. A token can be **any entity at any tier**: a codepoint, word, phrase, AST subtree, chess line or pixel patch. Each token comes with its deterministic 4D coordinate (shape) and its attested relations (meaning), so the vocabulary is open, lossless (detokenize = recompose from the DAG), shared across models, and attributable per token. This replaces BPE/SentencePiece integer IDs. Royalty attribution: `share_bp` summing to 10000, from containment provenance weighted by Glicko-2 Fisher information, persisted in `app.billing_royalties`.

---

## 6. Hosts & environments (truth: `C:\ProgramData\Laplace\machine.env`, `Laplace-Operations/laplace.env`, `installation/*.json`)

| Host | Role | Paths | DBs |
| :--- | :--- | :--- | :--- |
| `HART-DESKTOP` (`100.70.222.78`) | Interactive dev, RTX 4060 Ti, IIS `https://dev.hartonomous.com` | src `E:\Repositories\Laplace`, build `D:\Libraries\build\Laplace`, data `E:\Data\Ingest`, work `E:\Data\Laplace\work`, pgdata `E:\Data\Laplace\pgdata`, WAL `D:\Data\Laplace\pg_wal` | `laplace` (modular), `laplace-mono`, `laplace-desktop-app` |
| `hart-server` (`100.76.181.27`, LAN `192.168.1.2`) | Staging + failover DB, OpenBao, Vaultwarden, self-hosted CI runner, `https://staging.hartonomous.com` | `/etc/laplace/machine.env`; src `/repos/src`, build `/repos/build`, work `/repos/work`, data `/vault/Data`, models `/vault/models` | `laplace`, `laplace-mono` |
| `hart-prod` (`100.124.95.17`) | Locked prod (`api.` / `mcp.hartonomous.com`), CI/CD promotion only | `/opt/laplace` | `laplace-app`; substrate proxied |
| `hart-cloud` (`100.103.255.122`) | On-demand AVX-512 qualification; TERMINATED when idle (CI lease) | — | — |
| `hart-router` (`192.168.1.1`) | OpenWrt, split-horizon DNS | — | — |

- **Elevated ACL scripts on HART-DESKTOP** must grant `HART-DESKTOP\ahart` (SID `S-1-5-21-1955383224-1799969050-1119319971-1001`) FullControl alongside SYSTEM, Administrators, and service/AppPool SIDs (`Repair-DesktopPermissions.ps1`).
- **HART-DESKTOP is on metered 5G:** never download bulk corpora or model checkpoints there.
- The 3 owner OAuth bindings are seeded as `installation-owner` admins by `db/schema/30_app.sql`.

---

## 7. Commands

```powershell
# Client launchers (Laplace-Operations/installation/client.py → %LOCALAPPDATA%\Laplace\bin):
laplace --where                 # resolved binary, client profile, database
laplace-mono --where
# Engine CLI (laplace help | laplace describe; table in Laplace-Engine/src/op.c):
laplace status [--joins]        # perf-cache fingerprints + table/index counts
laplace ingest --plan [SOURCE]  # recipe coverage, no load
laplace ingest --claims SOURCE  # dry-run decompose, print claims
laplace ingest --catalog
laplace replay                  # recompute standings from containment, bit-for-bit
# Operations (Windows: laplace-ops → pwsh Laplace-Operations/laplace.ps1; `laplace-ops help`):
laplace-ops runs                                   # recent CI/CD runs
laplace-ops status -Target desktop                 # NOTE: no -Target defaults to staging
laplace-ops build|verify|deploy -Target desktop    # deploy -Components modular-web for MCP/IIS only
laplace-ops db -Database both -DatabaseOperation status
laplace-ops deploy -Target staging -Wait           # then -Target production -Wait to promote
laplace-ops ingest staging [SOURCE...] -Wait       # dispatches Laplace-Operations ingest.yml on hart-server
laplace-ops data staging ...                       # data.sh over SSH to $LAPLACE_PEER
```

**Staging ingests (hart-server):** `ingest.yml` runs `laplace ingest --catalog` and then `ingest.sh` on the self-hosted runner, with one ingest per database (flock `$LAPLACE_LOCKS/ingest-<db>.lock`). Logs: `$LAPLACE_WORK/logs/ingest-runs/<UTC>/<step>.log|.exit` + `wal.tsv` (`ingest.sh`). The CI runner sets `LAPLACE_WORK=/repos/work/agents/Laplace-Operations`, so CI runs land in `/repos/work/agents/Laplace-Operations/logs/ingest-runs/`; manual `laplace-mono ingest chain` runs log to `/repos/work/agents/Laplace/laplace-mono/ingest-logs/laplace-ingest-chain.log`. Monitor with `laplace-ops runs`, `gh run watch`, or MCP `ingest_status`. Do not start, stop, or reseed staging ingests unless asked.

---

## 8. Documentation index (`E:\Repositories\Laplace\Laplace-Wiki`, abbreviated `W/`)

| Topic | Read first | Then |
| :--- | :--- | :--- |
| Overview / page tree | `W/README.md`, `W/Architecture.md`, `W/Query.md` | `W/Reference/Glossary.md` |
| Build order (30 stages) | `W/Sequence/README.md` | each `W/Sequence/<Stage>.md` |
| Byte layouts (ID, tier-0, flags, path vertex, banks) | `W/Reference/Formats.md` | `W/Reference/Native.md`, `W/Storage/Identity.md`, `W/Storage/Physicality.md` |
| Tier / composition rules | `W/Storage/Compositions.md` | `laplace.h` (`lp_tier_above`) |
| Projection, Tier-0, perf-caches | `W/Sequence/Projection.md`, `Tier-0.md`, `Perfcaches.md` | `W/Research/Placement.md`, `Numerics.md` |
| Highway / banks / registries | `W/Reference/Types.md`, `W/Sequence/Registries.md` | `manifest/banks.tsv`, `W/Repositories/Laplace/docs/decisions/0001*`, `0004*` |
| Schema / SQL / indexes | `W/Reference/Schema.md`, `SQL.md`, `W/Sequence/Indexes.md` | `sql/laplace.sql.in` (truth) |
| Recipes / `.format` | `W/Reference/Recipes.md`, `W/Sequence/Recipes.md` | `W/Corpora/TreeSitter.md`, `W/Repositories/Laplace-Engine/recipes/BRIEF.md` |
| Sources / witnesses | `W/Sequence/Sources.md` | `W/Semantics/Attestations.md` |
| Ingest pipeline | `W/Reference/Ingest.md`, `W/Sequence/Content.md` | `W/Storage/Ingestion.md`, `W/Research/Performance.md` |
| Claims / attestations / consensus | `W/Sequence/Attestations.md`, `Consensus.md` | `W/Semantics/*`, `W/Research/Trust.md` |
| Reads (hop, translate, pull) | `W/Reference/Reads.md`, `W/Sequence/Pull.md` | `W/Sequence/Web.md`, `W/Corpora/Hops.md` |
| Per-corpus specifics | `W/Corpora/README.md` (source table) | `W/Corpora/<Source>.md` |
| CLI / env | `W/Reference/CLI.md`, `Environment.md` | `src/op.c` (truth) |
| Build / checks | `W/Reference/Build.md`, `Checks.md` | `W/Sequence/Builds.md`, `W/Operations/Builds.md` |
| Forward / firmware / authority | `W/Sequence/Forward.md`, `Firmware.md`, `Authority.md`, `Envelope.md` | `W/Reference/Firmware.md` |
| Models / export / code / chess | `W/Sequence/Models.md`, `Export.md`, `Code.md`, `Chess.md`, `Games.md` | `W/Reference/Monorepo.md` |
| Ops / deploy / hosts / secrets | `W/Operations/*`, `W/Repositories/Laplace-Operations/docs/installation-map.md`, `operating-laplace.md`, `delivery.md` | `secrets-*.md`, `operator-database-access.md` |
| MCP | `W/Repositories/Laplace-MCP/docs/API.md`, `W/Reference/Laplace-MCP.md` | `native/api_mcp.c` (truth) |
| Monorepo app / billing / UCI | `W/Reference/Monorepo.md`, `W/Repositories/Laplace/docs/INDEX.md` | `W/Repositories/Laplace-UCI/*` |
| Spec disagreements | `W/Sequence/Conflicts.md` | — |

---

## 9. Known drift (fix the doc *or* the code, then delete the line)

- Wiki `Reference/Repositories.md` / `SQL.md` / `CLI.md` quote stale extension version, function count, and command count. Code is truth.
- **Two path-carrier encodings:** Native `lp_id_to_xyz` (exponent 1021, 43/43/42 + 28 spare) versus monorepo `engine/core/src/mantissa.c` `mantissa_pack` / `extension/laplace_geom` (exponent 1023, 53/53/22 + flags). `laplace` and `laplace-mono` paths are not interchangeable. Converge on the Native one.
- Web glome `Laplace/web/src/explore/glome/GlomeCanvas.tsx` `unpackMantissaCoord` decodes Native M as a "51-bit run count". The real M is run30/said3/outcome2/position18.
- `laplace-code` MCP still runs on the `workspace.lpm` sidecar with BM25 and a paragraph chunker (§4).
- `public.attestation` replaced `public.witness` without an extension version bump (both shipped as 1.63). A database created at 1.63 before that change cannot gain `attestation` by `ALTER EXTENSION … UPDATE`; recreate it. Greenfield until a release is deployed: no extension upgrade paths and no version bumps; a schema change recreates the database.
