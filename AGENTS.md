# Laplace — Architecture, Invariants & Operational Reference

Laplace is a deterministic, content-addressed, recursively compositional computer and cognition architecture over persistent 4D geometric state. Its scope spans language, software construction, chess and other games, images, audio, video, model circuit ingestion and export, consequence-driven learning, and the browsable entity world. It replaces opaque dense-model embeddings, arbitrary text chunking, and neural rerankers with exact reusable multi-tier structure, 4D $S^3$ / 4-ball geometry, 4D trajectory similarity, attributable containment provenance, and Glicko-2 consensus standing. Native C (`icx`, `-fp-model=precise` / `/fp:precise`, no FMA contraction, CORE-MATH) performs repeated computation; PostgreSQL 18.6 + PostGIS provides durable indexed 4D geometry and set access; SQL and C# bind operations and orchestrate lifecycle.

---

## 1. Mathematical, Geometric & Byte-Level Substrate Laws (`Laplace-Native` / `laplace.h`)

- **Identity (`lp_id`, 16 bytes)**: First 16 bytes of BLAKE3-256 (`uint8_t b[16]`, 32 lowercase hex digits), with **zero** domain byte, tier, type, recipe, version, source, or position salt.
  - Codepoint ID (`lp_id_codepoint`): BLAKE3-128 of its 1–4 UTF-8 bytes (surrogates in generalized 3-byte form).
  - 1-child composition (`lp_id_compose`): No re-hash; equals the child's ID.
  - $n \ge 2$ composition (`lp_id_compose`): BLAKE3-128 over the $16n$ contiguous bytes of the children's IDs in exact sequence order, repeats included.
  - Universal Structural Convergence: `[k,i,n,g]` is one entity everywhere regardless of whether it appears as a word, identifier, title, or substring. Finite digital scalars reuse canonical digit trajectories (`0.34567` $\rightarrow$ `[0][.][3][4][5][6][7]`, `255` $\rightarrow$ `[2][5][5]`); an ordered $2\times2$ pixel patch inside any $8\times8$ block, image, or video frame is the same global subpatch entity. Never mint arbitrary numeric or media values as new Tier-0 atoms, and never hash made-up strings (`substrate/source/X/v1`, `language:eng`) or raw file byte blobs (`file_close`: a file is always `[metadata, content]`).
- **Tier-0 Floor (`$LAPLACE_TIER0`, `lp_tier0_record`, 64 B $\times$ 1,114,112 = 71,303,168 B)**:
  - Layout: `id` (16 B), fixed-point `m[4]` (`int64_t[4]`, 32 B, value $m / 2^{53}$ per axis), `hilbert` (`uint64_t`, 8 B), `rank` (`uint32_t`, 4 B DUCET total order rank), `pad` (`0`, 4 B).
  - Placement: DUCET collation rank $r$ maps to the $r$-th 4D Hilbert-ordered Marc Alexa Super-Fibonacci point on $S^3$ (`cr_sin`, `cr_cos`, `cr_atan2`, `cr_hypot`, `cr_cbrt`), nudged inward so $\sum_{d=0}^3 m_d^2 \le 2^{106}$ (`lp_coord_inside`).
  - Universal Content Determinism (`4 ULP`): Guaranteed by compiler flags + CORE-MATH + $2^{53}$ / `fp1e9` fixed-point quantization on $S^3$ and the memory-mapped perf-cache (`tier0.bin` + `flags.bin` + `highway.bin`). Same content = same 16-byte `blake3` hash and 4D coordinate across all architectures without database lookups.
  - Composition Centroid (`lp_coord_centroid`): Exact integer mean $s_d = \sum m_{i,d}$ in `__int128`, truncated toward zero (`s_d / n`), placing compositions strictly inside the closed unit 4-ball.
  - Centroid vs. Trajectory: Coordinate equality is deliberately many-to-one with respect to content identity (`[c,a,t]` and `[a,c,t]` share a 4D centroid while retaining distinct `blake3` IDs and distinct 4D trajectories).
  - Hilbert Index (`lp_hilbert4`): 4D 16-bit Skilling transpose + bit interleave (`pdep`), stored in PostgreSQL `bigint` as `h ^ 0x8000000000000000` so signed `bigint` order equals unsigned Hilbert order.
  - Companion Flags (`$LAPLACE_FLAGS`, `lp_flags`, 32 B / 256 bits per codepoint + `.layout`): All UCD binary and enumerated properties indexed $O(1)$ without database reads.
- **Packed 4D ZM Path Geometry (`lp_id_to_xyz`, 32 B/vertex EWKB) vs. Realized 4D Trajectories**:
  - Stored in `physicality.path` as `POINT ZM` (1 run, 37 B EWKB `0xC0000001`) or `LINESTRING ZM` ($n$ runs, $9 + 32n$ B EWKB `0xC0000002`). Always transmitted as binary EWKB / `PGCOPY` (never text WKT).
  - `X, Y, Z` pack the 128-bit constituent `lp_id` into the low $43 + 43 + 42$ mantissa bits of positive IEEE-754 doubles with exponent $1021$ (`[0.25, 0.5)`). The remaining $9 + 9 + 10 = 28$ mantissa bits (`lp_xyz_spare`) hold a 4-bit layout tag and 24-bit perf-cache slot payload; scans mask to the 128 ID bits (`lp_xyz_id_mask`).
  - `M` packs the RLE run length in its low 30 bits (`LP_M_RUN_BITS`, `lp_m_run`) and vertex role above it (`lp_m_said`): `LP_SAID_CLAIM = 1`, `LP_SAID_RECORD = 2`, `LP_SAID_TUPLE = 3`, `LP_SAID_METADATA = 4`.
  - **Trajectory Realization Law**: Packed `X, Y, Z, M` vertices serialize constituent **identity, order, run-length, and role** — they are **not** spatial coordinates. Never apply spatial metrics directly to packed carrier doubles. Realized 4D curves (`laplace_frechet4d`, `laplace_dtw4d`, `laplace_edr4d`) unpack the child `lp_id` sequence (`lp_xyz_to_id` / `lp_path_vertices`), resolve each child's real 4D coordinate (`entity.coord` or `tier0.bin` / `highway.bin`), and compute curve similarity over those ordered 4D points in $\mathbb{R}^4 / S^3$.
- **Segmentation & Repeat Factoring**:
  - Tailored UAX #29: At the word tier, separators always separate (`WB6, WB7, WB11, WB12, WB13a, WB13b` disabled: `3.14` $\rightarrow$ `[3][.][14]`, `a_b` $\rightarrow$ `[a][_][b]`).
  - Adjacent identical constituents collapse into vertex run length; periodic sub-blocks factor deterministically from content alone (leftmost shortest period, recursive: `banana` $\rightarrow$ `b [an]×2 a`).

---

## 2. Schema, Multi-Tier Search, Highway Perf-Cache & Recording Law (`Laplace-postgres` & `Laplace-Engine`)

- **Core Schema (`CREATE EXTENSION laplace`, `laplace--1.12.sql`)**:
  - `entity` (256 range partitions `entity_00`..`entity_ff` on byte 0 of `id`): `id blake3`, `tier smallint`, `lexical smallint`, `kind smallint`, `coord geometry(PointZM)`, `hilbert bigint`. Indexed by `(id)`, `(hilbert)`, and `USING gist (coord gist_geometry_ops_nd)`.
  - `physicality` (256 range partitions `physicality_00`..`physicality_ff` on byte 0 of `entity`): `entity blake3`, `tier smallint`, `hilbert bigint`, `path geometry`, `mask bit(256)`. Indexed by `(entity)` and one composite `USING gin (path laplace_path_ops, mask laplace_mask_ops) WITH (gin_pending_list_limit = 262144)` that intersects constituent containment and mask bits in a single posting-list scan.
  - `witness`: `id blake3` (source trunk `[source_record, file_trunks...]`), `lineage blake3`, `trust double precision`.
  - `consensus` (16 partitions `consensus_0`..`consensus_f`, `fillfactor = 80`): `claim blake3 PRIMARY KEY`, `rating float8`, `deviation float8`, `volatility float8`, `matches int4`.
  - Provenance is containment (`laplace_attested`, `laplace_attribution`, `laplace held`, `laplace replay`); the legacy `attestation` table is retired in `1.12`. Never run `DROP EXTENSION laplace CASCADE`.
- **Multi-Tier, Multi-Modality Search & Retrieval (Why Laplace Never Chunks Content)**:
  - Content is **never** cut at arbitrary line or token lengths. Every modality decomposes recursively across tiers (`0` = codepoint/atom $\rightarrow$ `1` = grapheme $\rightarrow$ `2` = word/token $\rightarrow$ `3` = phrase/sentence/AST expression $\rightarrow$ `4` = paragraph/section/AST declaration $\rightarrow$ `5+` = file content tree $\rightarrow$ `[metadata, content]` file trunk $\rightarrow$ witness trunk).
  - Because every node at every tier is recorded in `entity` and `physicality`, **any tier of any modality can be searched and compared directly**:
    1. **Exact & Containment Search (`laplace_containers`, `laplace_paths`, GIN `laplace_path_ops`)**: Querying a codepoint, grapheme, word, phrase, sentence, paragraph, AST node, or chess move sequence decomposes the query to its `lp_id` and constituents, immediately locating every parent container at tier $T+1, T+2, \dots$ that holds those constituents. The lowest common ancestor (LCA) in the DAG that holds the query constituents is the natural unit of retrieval.
    2. **4D Geometric Neighborhood (`entity.coord <-> target`, `laplace_distance4d`, `laplace_couple`)**: GiST 4D k-NN over real $S^3$ / 4-ball centroids finds morphologically and compositionally proximate entities at any tier.
    3. **4D Trajectory Shape (`laplace_frechet4d`, `laplace_dtw4d`, `laplace_edr4d`)**: Unpacks constituent `lp_id` sequences from `physicality.path` mantissas, resolves their real 4D coordinates, and scores ordered sequence/curve shape across any tier (grapheme trajectories inside words, word trajectories inside sentences, sentence trajectories inside paragraphs, statement trajectories inside functions).
    4. **Concept & Attestation Hops (`laplace_claims`, `laplace_follows`, `highway.bin`, `consensus`)**: Bridges across vocabulary and languages by walking attested claims (`[lemma, language, identifier]`, `[concept, relation, concept]`, `[declaration, "declared identifier", name]`) weighted by Glicko-2 consensus standing.
- **Identifiers vs. Packaging Indexes (The Recording Law)**:
  - **Persistent Identifiers are Entities**: Cross-release identifiers cited across resources — CILI `i46360`, ISO 639 codes, PropBank rolesets (`abandon.01`), VerbNet classes (`give-13.1`), FrameNet frames (`fnframe`, `fnfe`, `fnlu`), VerbAtlas frames (`va:0001f`), GeoNames IDs, Unicode scalars — are canonical Tier-0 compositions of their identifier string and highway nodes in `$LAPLACE_HIGHWAY`.
  - **Packaging Pointers are Recorded Nowhere**: Release-local keys and internal file pointers — WordNet synset offsets (`02084071-n` in PWN 3.0 vs `02086723-n` in PWN 3.1), sense keys (`dog%1:05:00::`), `oewn-` IDs, CoNLL-U `id`/`sent_id`, XML element IDs, dump row numbers, byte offsets — are resolved during ingestion (`key`, `refer`, `type` directives) to the target entity/ILI and **never** written as entities or `HAS_EXTERNAL_ID` claims.
- **Highway Banks vs. Concept Hops (`$LAPLACE_HIGHWAY`, `manifest/banks.tsv`, `manifest/slots/*.tsv`)**:
  - Highway banks (`kind` width 8, `upos` 32, `lexfile` 64, `deprel` 64, `vnrole` 64, `wnframe` 64) are frozen-slot filter bitmasks (`laplace_bank_bit`, `?`, `?&`, `?|`), never English word entities and never concept hops.
  - Generating or using `highway.bin` must never require the full raw corpus suite on disk or a `.nodes` sidecar dump; corpus definitions (`skos:definition`, roleset/frame glosses) are attested as claims about highway identifier nodes during ingestion.
  - A lexical sense is the composition `[lemma, language, identifier]` where `language` is an ISO 639 constituent, never a mask bit on the concept.
  - Relations come from the source's semantics, never raw XML/JSON markup attribute names or invented `HAS_*` enum strings.

---

## 3. Native C, Tree-sitter Chaining, Tier Traversal, Assembly-Line ETL & Forbidden Patterns

### A. Division of Ownership: Native C + Perf-Cache vs. Thin C# & SQL Orchestrators
- **Client-Side Decomposition & Reduction (Native C + Perf-Cache)**: Content decomposition and deduplication occur on the client in native C (`Laplace-Native` / `Laplace-Engine`) before talking to PostgreSQL. C# is a **thin orchestrator** over native C and `Npgsql` binary `COPY` streams — never allocate per-node managed C# heap objects (`Intent`, `KeptRow`, per-row dictionaries) on the hot path, and never reimplement C# math/BPE/SVD/attribution logic that duplicates native C.
- **Database-Side Set Merge & Continuous Series Updates (Thin SQL + `Laplace-postgres` C Extension)**: SQL is a thin set-based orchestrator over native C extension functions (`laplace_attest_series`, `laplace_attested`, `laplace_attribution`, `laplace_clean_pending_list`). Database crossings are $O(\text{tiers})$, never $O(\text{nodes/atoms/records})$.
  - In `Laplace` (`laplace-mono`), 100% of SQL statements across substrate, application (`app.*`), billing, auth, and chess reside in `engine/core/src/sql_catalog.def` and execute via `NpgsqlCatalog.Command(...)` (enforced by `ReadPathArchitectureGateTests` with zero hand-written SQL exemptions).
  - The monorepo database schema is consolidated into three canonical migrations (`20260606000000_layer1_database.sql`, `20260611000000_app_billing.sql`, `20261015000000_substrate_runtime_grants.sql`). Never pull `consensus` rows back over the socket to update standings on the client.

### B. Packaging vs. Product: Chained Tree-sitter $\rightarrow$ Laplace Grammars
1. **Multi-Level Tree-sitter Grammar Chaining (`ts_parser_set_included_ranges`)**:
   - **Outer Packaging Grammar**: Every standardized container format is parsed by its compiled Tree-sitter grammar (`tree-sitter-xml` for WN-LMF/FrameNet/PropBank/UCD, `tree-sitter-json` for Wiktextract/JSONL, `tree-sitter-turtle` for CILI/FrameBase, `tree-sitter-csv`/`tsv`/`psv` for Tatoeba/ATOMIC/HateSpeech/GeoNames/ConceptNet, `tree-sitter-conllu` for UD, `tree-sitter-wndb` for Princeton WordNet, `tree-sitter-pgn` for chess, plus 300+ code grammars).
   - **Inner / Injected Sub-Grammars**: When an outer grammar field contains structured sub-syntax (CoNLL-U `FEATS` `Key=Val|Key=Val`, `DEPS`, `MISC`, TSV columns containing JSON or pipe-delimited lists, or structured identifier notation like `drop%2:38:00::` / `mcr:ili-30-01976841-v`), the child parser is invoked directly on the parent `TSNode` byte range via `ts_parser_set_included_ranges`, chaining down to UAX #29 text leaves and `tier0.bin` / `highway.bin`.
2. **Compiled Integer Symbol Dispatch (Zero Runtime String Parsing)**:
   - `.format` and `.recipe` files are Laplace semantic grammars. Every field in a curated source must have an explicit recipe disposition (`content`, `key`, `refer`, `type`, `metadata`, `omit`, `perfcache`, `own`, `thing`, `attest`, `relate`, `pair`); unhandled fields stop ingestion as unresolved obligations.
   - At recipe load time, every node type and field rule is compiled once against `ts_language_symbol_for_name()` and `ts_language_field_id_for_name()` into flat $O(1)$ integer jump tables (`uint16_t TSSymbol` / `TSFieldId` $\rightarrow$ disposition & composition instructions).
   - Worker threads walk the `TSTree` using `TSTreeCursor` + compiled integer jump tables directly over the input byte buffer, reusing thread-local `TSParser` instances (`ts_parser_reset`) and thread-local arenas across records.
3. **Bidirectional AST $\leftrightarrow$ Merkle DAG**:
   - Ingest maps `Bytes -> Chained Tree-sitter AST -> Laplace Merkle DAG + Claims`.
   - Realization / export is the exact inverse (`Laplace Merkle DAG -> AST -> Formatted Bytes`), enabling lossless byte-for-byte round-trip reconstruction and cross-format synthesis.

### C. Bidirectional Tier Traversal Law (`Leaf -> Trunk` Creation + `Trunk -> Leaf` Deduplication)
1. **Leaf $\rightarrow$ Trunk (Bottom-Up on Client for Creation & In-RAM Deduplication)**:
   - Compose bottom-up from perf-cache leaves (`tier0.bin` and `highway.bin`) through canonical tier boundaries (UAX #29 for text leaves and chained Tree-sitter AST nodes for structured records/files). Every repeated substructure in the batch collapses in $O(1)$ into the lock-free client node table (`NODE[]` / `staged_set`).
2. **Trunk $\rightarrow$ Leaf (Top-Down Against PostgreSQL for Self-Reducing Subtree Pruning)**:
   - Deduplication against PostgreSQL proceeds top-down from the highest logical roots (File Trunk $\rightarrow$ Section/Record Trunks $\rightarrow$ Sentences $\rightarrow$ Words), skipping synthetic binary-tree (`blocks_of`) spine nodes.
   - By the Merkle DAG containment invariant, if a node at tier $T$ already exists in PostgreSQL (or in the client's perf-cache / `staged_set`), **its entire descendant subtree is already recorded and is immediately amputated on the client** (`keep = recorded`). Only novel branches descend to the next frontier and are `COPY`ed to the database.

### D. Assembly-Line Record Streaming ETL
1. **$O(1)$ Pre-Check**: Probe the file trunk / OS metadata identity before reading or decomposing file bytes; never run a 2-pass (`pass 0` / `pass 1`) full-file decomposition or re-read files from disk.
2. **Single-Pass Grammar-Bounded Record Streaming**: Stream files in a single pass, slicing strictly on outer grammar record boundaries (XML `<LexicalEntry>` / `<Synset>`, Turtle statement block, JSONL line, CoNLL-U sentence block, PGN game, CSV/TSV row). Never split at arbitrary byte offsets.
3. **Parallel Arena-Allocated Record Workers**: Worker threads pull record slices from the ring buffer, parse each record via thread-local `TSParser` + chained sub-grammars + compiled integer jump tables, compose bottom-up into the lock-free `NODE[]`/`VTX[]` table, and accumulate per-witness `(claim, games, score)` series in-stream using thread-local arenas reset per record (`0` `malloc`/`free` calls per record). Cross-record internal pointers (`key` / `refer`) resolve against a lock-free concurrent `key_hash -> EntityID` table.
4. **Continuous Pipelined `stage.*` COPY & In-DB Series Updates**:
   - Top-down `Trunk -> Leaf` deduplication prunes known subtrees, and novel `entity` / `physicality` rows + accumulated `(claim, games, score)` series stream via binary `PGCOPY` into `UNLOGGED` `stage.*` tables using bounded connection concurrency (`min(cores, max_pool_size - headroom)`).
   - Standings update inside PostgreSQL via set-based `laplace_attest_series` (`stage.consensus_<h>` $\rightarrow$ `public.consensus_<h>`) at `trust_classes.toml` priors, followed by Hilbert-ordered `stage.*` $\rightarrow$ `public.*` set merge, GIN pending-list cleanup (`laplace_clean_pending_list`), and atomic `witness` trunk registration.

### E. Explicitly Deprecated & Forbidden Patterns (Never Extend or Reintroduce)
1. **`STree` / `SNode` Intermediate Heap DOM (`Laplace-Engine/src/structure.c`)**:
   - `g_node()`, `STree`, `SNode`, and `s_grammar_split()` (`copy_around` / `graft` / `copy_rec`, copying 2 MB chunks into `<_>...</_>` buffers and single-threadedly grafting a whole-file DOM in RAM) are **deprecated**. Walk `TSNode` / `TSTreeCursor` directly into `compose()`.
2. **Bespoke Hand-Rolled Format Scanners & Split Codepaths (`structure.c`, `recipe.c`)**:
   - `s_decompose()`, `tier_read()`, `part_end()`, `mini_json()`, `parts_of()`, and the `s_boundary()` single-thread bug (`if (l->ntier > 1 && l->tier[0].quoted) return n;`) in `structure.c` are **deprecated**.
   - Blocking `csv`/`tsv`/`psv` from loading `tree-sitter-csv` (`recipe.c:322`) and maintaining separate hardcoded C walkers (`attest_json_node`, `attest_xml_node`, `attest_delimited`, `attest_code_node` in `recipe.c`) are **deprecated**. All formats use chained Tree-sitter grammars + compiled recipe dispatch.
3. **Runtime String Path Building & Per-Row Heap Churn (`Laplace-Engine/src/say.c`)**:
   - `child_path()` (`snprintf` + `strtok_r` on every AST node), `dis_scan()` linear string loops, `owed()` (`snprintf` on every claim), and per-row `records_of()` heap allocations (`lp_idmap *rm`, `of[]`, `recs`, `at[]`, `ord[]`, `fill[]`, `part[]` allocated per row) are **deprecated**. Use precompiled integer `TSSymbol`/`TSFieldId` jump tables and thread-local arenas.
4. **Two-Pass Stretch Reads, Lagged End-of-Source `standings_write()`, 2PC on Unlogged Tables & Client-RAM Partition Rewrites (`ingest.c`, `db.c`)**:
   - Whole-file slurping (`read_all`), 2-pass stretch reads (`pass 0` + `table_reset()` + `pass 1` in `ingest.c`), and disk re-reads (`ingest.c:503-514`) are **deprecated**.
   - Lagged end-of-source `standings_write()` (`ingest.c:557`, `db.c:195-254`) with `#pragma omp critical` + `DELETE FROM stage.consensus_%x` is **deprecated** ("consensus fold" as a lagged batch-end phase is forbidden).
   - 16-connection `PREPARE TRANSACTION` / `COMMIT PREPARED` on `UNLOGGED` `stage.*` tables, the 528-table startup loop in `stage_open()`, and client-RAM partition rewrites (`L->rewrite` pulling partitions back via `COPY TO STDOUT` in `db.c:448-484`) are **deprecated**.
5. **Arbitrary Line-Count Chunking, Stopword Lists, In-RAM BM25 Sidecars & External Vector DB Wrappers (`Laplace-MCP/native/lpm.c`, `semantic.c`)**:
   - Fixed line-count chunking (`LPM_CHUNK_MIN_LINES 3..120`, `LPM_TEXT_MIN_LINES 8..48`, `admit_text_blocks`), hardcoded English stopword lists (`is_stop_word_bytes`), ASCII-only `lower_token_ref` / `collect_subtoken_feats`, sidecar in-RAM BM25 indexes (`workspace.lpm`), and external `llama-server` / Qdrant HTTP wrappers (`semantic.c`) in `Laplace-MCP` are **deprecated** anti-patterns that destroy multi-tier DAG structure. All MCP search, embedding, and reranking must operate over the lossless multi-tier PostgreSQL substrate (`entity` + `physicality` + `consensus`) and perf-caches (`tier0.bin` + `flags.bin` + `highway.bin`).

### F. Forward Program, The Three Proofs & `laplace-blake3-v1` Mold-A-Model Export
- **Forward Program (`RESOLVE -> COUPLE -> ORIENT -> ROUTE -> SCAN -> COMPOSE -> PROPOSE -> STEER -> SELECT -> REALIZE -> WITNESS`)**:
  - Governed by three orthogonal axes: **Knowledge** (shared substrate), **Authority** (effective principal scope), and **Compute Envelope** (explicit hop/fanout/frontier/memory budget), steered by detached **Personality Firmware** (`firmware/program.firmware`, rated by observed consequences, never mixed into substrate truth).
  - Functional correspondence: $Q =$ exact admitted observation/bindings/obligations; $K =$ indexed typed responders; $QK =$ coupling across structure, relation, geometry, and consensus planes; $V =$ responding structure/evidence/calculations; $O =$ receipted state update.
- **The Three Proofs**:
  - **Proof 1 (`db-roundtrip`)**: Decompose multi-domain inputs into `entity` + `physicality` and recompose 100% byte-for-byte from PostgreSQL alone while verifying all structural invariants.
  - **Proof 2 (`Linguistic Super Highway`)**: Fixed-width bitmasks + cross-source identifier indexing, filtering, and hops in `highway.bin` without requiring raw corpora on disk.
  - **Proof 3 (`Full Corpus Ingestion`)**: Multi-corpus ingestion on top of Proofs 1 and 2 where cross-source claims collide on deterministic highway identifier nodes and update standings via `laplace_attest_series`.
- **BLAKE3-Addressed Multi-Tier Model Export (`laplace-blake3-v1`) & Royalty Attribution**:
  - Exported GGUF models (`laplace.tokenizer.scheme = "laplace-blake3-v1"`, `laplace.vocab.size`, `laplace.vocab.blake3_ids`) replace flat BPE/SentencePiece integer token IDs with **16-byte `blake3` entity IDs** across all DAG tiers, locked to Laplace's decomposer + perf-cache (commercial cartridges encrypted via `FoundryCartridgeCrypto` `LPCART01` AES-256-GCM envelope).
  - Every query receipt and **Mold-A-Model** synthesis computes exact per-witness attribution (`share_bp`, summing to `10000` basis points) from containment provenance (`laplace_attested` / `laplace_attribution` over `physicality` + `witness`) weighted by Glicko-2 Fisher information ($I_i = 1/\sigma_i^2$), persisted in `app.billing_royalties` (`GET /v1/billing/royalties`).

---

## 4. MCP Servers & Search Routing

Both deployed HTTP MCP endpoints are configured across `agy`/`gemini`, `codex`, `grok`, `copilot`, and VS Code:

| MCP Server | Endpoint | Backing Stack & DB | When to Use |
| :--- | :--- | :--- | :--- |
| **`laplace-code`** *(Primary)* | `https://dev.hartonomous.com/modular/mcp` | Modular Native C (`lpm.exe`), DB `laplace` | **Use first for all code/doc search & native ops (~150 ms)**: `code_search`, `file_outline`, `code_where`, `code_callers`, `code_callees`, `embeddings`, `rerank`, `shape`, `search`, `forward`, `hop`, `translate`, `degrees`, `pull`, `library`, `catalog`, `provenance`, `recipe_catalog`, `recipe_plan`, `ingest_status`. |
| **`laplace`** *(Monorepo)* | `https://dev.hartonomous.com/mcp` | Monorepo C# (`w3wp.exe`), DB `laplace-mono` | Use for monorepo SQL catalog discovery (`api`, `op`), `health`, `source_status`, `pipeline`, `facts`, `taxonomy`, `walk`. Do not use its slow C# `code_search`. |

- Ignore hits under `Laplace-Refactor` (frozen historical reference) and dated `2026-10-*` incident logs under `Laplace-Wiki/Repositories/` unless investigating historical state.

---

## 5. Environments, Host Topology & Execution Boundaries

| Host | Tailscale / LAN IP | Role & Config | Source / Build / Work | Databases (PostgreSQL 18.6, Port 5432) |
| :--- | :--- | :--- | :--- | :--- |
| **`HART-DESKTOP`** | `100.70.222.78`<br>`192.168.1.x` | Local Interactive Dev + RTX 4060 Ti (`C:\ProgramData\Laplace\machine.env`, `https://dev.hartonomous.com`) | Src: `E:\Repositories\Laplace`<br>Build: `D:\Libraries\build\Laplace`<br>Data: `E:\Data\Ingest`<br>Work: `E:\Data\Laplace\work` | `laplace` (modular), `laplace-mono` (mono substrate), `laplace-desktop-app` (mono app). Heap: `E:\Data\Laplace\pgdata`, WAL: `D:\Data\Laplace\pg_wal`. |
| **`hart-server`** | `100.76.181.27`<br>`192.168.1.2` | Staging (CI/CD deploy), failover DB, OpenBao, Vaultwarden (`/etc/laplace/machine.env`, `https://staging.hartonomous.com`) | Src: `/repos/src`<br>Build: `/repos/build`<br>Data: `/vault/Data`<br>Work: `/repos/work` | `laplace`, `laplace-mono`. (1080 Ti GPU disabled/offline). |
| **`hart-prod`** | `100.124.95.17` | Locked Enterprise Prod (`https://api.hartonomous.com`, `https://mcp.hartonomous.com`, CI/CD promotion only) | `/opt/laplace/app` | `laplace-app` local; proxies substrate/UCI to `hart-server` or `hart-cloud`. |
| **`hart-cloud`** | `100.103.255.122` | On-Demand AVX-512 qualification (`c3-standard-8`) | Qualification | Kept `TERMINATED` when idle; started on demand via CI lease (`qualify-cloud`). |
| **`hart-router`** | `192.168.1.1` | OpenWrt router | LAN gateway | Split-horizon DNS and LAN routing. |

- **Windows UAC / ACL Invariant (`HART-DESKTOP`)**: Every elevated script that configures ACLs on `E:\Data\Laplace`, `D:\Libraries\build\Laplace`, `D:\Data\Laplace`, or `C:\ProgramData\Laplace` must explicitly grant `$declared.windows_sid` (`HART-DESKTOP\ahart`, SID `S-1-5-21-1955383224-1799969050-1119319971-1001`) `FullControl` alongside `SYSTEM`, `BUILTIN\Administrators`, and IIS AppPool/service SIDs (`Repair-DesktopPermissions.ps1`).
- **Database Safety & Bandwidth**: Push/merge workflows never automatically drop or reseed databases. Do not reset/recreate shared databases (`laplace`, `laplace-mono`) without explicit instruction, and never download bulk external corpora or large model checkpoints over the metered 5G connection on `HART-DESKTOP`.
- **Default Platform Administrators**: Canonical migration `20260611000000_app_billing.sql` automatically seeds the 3 owner OAuth bindings (`aharttn@gmail.com` Google, `thesaltypatron@gmail.com` Google, `thesaltypatron@gmail.com` Microsoft) as audited `installation-owner` administrators.

---

## 6. Repositories (`E:\Repositories\Laplace` / GitHub `SaltyPatron`)

| Repository | Stack | Role |
| :--- | :--- | :--- |
| `Laplace-Native` | C (ICX) | Shared deterministic native library (`include/laplace/laplace.h`), golden tests, and `manifest/` (`banks.tsv`, `slots/`, `maps/`, `trust_classes.toml`). |
| `Laplace-postgres` | C / SQL | PostgreSQL 18 extension (`CREATE EXTENSION laplace`): `blake3` type, 82 C/SQL functions, partitioned schema & GiST/GIN index builders. |
| `Laplace-Engine` | C (ICX) | Unified `laplace` CLI (24 commands), `.recipe` / `.format` decomposer (`structure.c`, `say.c`), lock-free node table (`table.c`), bulk COPY loader (`db.c`), native reader (`read.c`, `pull.c`, `forward.c`, `program.c`). |
| `Laplace-MCP` | C / C# | Native `lpm` HTTP/MCP server (`native/`) + `Laplace.Modular.Web` IIS host (`web/`, `/modular/mcp`). |
| `Laplace-Operations` | PowerShell / Sh / Py | Host setup (`setup.ps1`/`setup.sh`), `laplace.env`, and lifecycle/deployment controller (`laplace.ps1` / `laplace-ops`). |
| `Laplace` | C# / C++ / SQL / TS | Monorepo (`laplace-mono` CLI, `/mcp`, OpenAI-compat API, Ingest Admin Portal & Recipe Studio, Lichess/Chess Lab, Foundry, web UI, `sql_catalog.def`). |
| `Laplace-UCI` | C# | UCI WebSocket relay and Lichess match engine connector (`mono-development`, `mono-staging`, `mono-prod`). |
| `Laplace-Wiki` | Markdown | Canonical specification (`Sequence/`, `Storage/`, `Semantics/`, `Corpora/`, `Operations/`, `Reference/`, `Research/`) + relocated repo docs (`Repositories/`). |
| `Laplace-Prototype` | C / SQL | Cleanroom test bench for the Three Proofs (`db-roundtrip`, `highway.bin`, `Full Corpus Ingestion`) and `laplace-blake3-v1` export verification. |
| `Laplace-Conventional` | Python | Conventional model evaluation and comparison benchmarks. |
| `Laplace-Refactor` | Frozen | **Frozen historical reference only** — do not build, modify, or run CI in `Laplace-Refactor`. |

---

## 7. Essential Commands & Canonical Specification Index

```powershell
# Modular CLI (E:\Data\Laplace\clients\laplace\client.json -> DB: laplace):
laplace --where                                      # Print resolved binary, client profile, and database
laplace status                                       # Verify Tier-0, flags, and highway fingerprints + table/index counts
laplace ingest --plan [SOURCE]                       # Plan recipe coverage without loading
laplace ingest --claims SOURCE                       # Dry-run decompose and print emitted claims without loading
laplace replay                                       # Verify recorded consensus standings bit-for-bit against containment

# Monorepo CLI (E:\Data\Laplace\clients\laplace-mono\client.json -> DB: laplace-mono):
laplace-mono --where                                 # Print resolved monorepo binary, profile, and database

# Operations Controller (from any directory via laplace-ops or E:\Repositories\Laplace\Laplace-Operations):
laplace-ops runs                                     # View recent CI/CD runs across all Laplace repositories
laplace-ops status -Target desktop                   # Check local development process status
laplace-ops build -Target desktop                    # Build desktop artifacts
laplace-ops verify -Target desktop                   # Run end-to-end runtime verification against desktop
laplace-ops deploy -Target desktop                   # Build, publish to dev.hartonomous.com + laplace-mono, migrate, and verify
laplace-ops deploy desktop -Components modular-web   # Deploy & verify Modular Web + MCP (/modular/mcp) in IIS
laplace-ops db -Database both -DatabaseOperation status # Inspect substrate and application databases
laplace-ops deploy -Target staging -Wait             # Build, deploy, and verify main on hart-server
laplace-ops deploy -Target production -Wait          # Promote verified staging package to hart-prod
```

- **Canonical Specifications (`E:\Repositories\Laplace\Laplace-Wiki`)**:
  - **Execution Sequence (1–30)**: `Sequence/README.md` (`Unicode.md` $\rightarrow$ `Tier-0.md` $\rightarrow$ `Registries.md` $\rightarrow$ `Recipes.md` $\rightarrow$ `Content.md` $\rightarrow$ `Attestations.md` $\rightarrow$ `Consensus.md` $\rightarrow$ `Indexes.md` $\rightarrow$ `Forward.md` $\rightarrow$ `Conflicts.md`).
  - **Contracts & Byte Layouts**: `Reference/Formats.md`, `Reference/Schema.md`, `Reference/Types.md`, `Reference/Native.md`, `Reference/SQL.md`, `Reference/CLI.md`, `Reference/Recipes.md`, `Reference/Ingest.md`, `Reference/Reads.md`, `Reference/Firmware.md`, `Reference/Laplace-MCP.md`, `Reference/Checks.md`.
