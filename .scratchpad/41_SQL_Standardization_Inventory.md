# 41 — SQL Standardization Inventory (2026-08-01)

READ-ONLY research pass. Inventory only — nothing implemented here. Scope requested:
(1) dynamic-SQL construction sites in C#/C-extension/`.sql.in`, (2) the two
`ReadPathArchitectureGateTests` allowlists, (3) reimplemented consensus scans /
entity+physicality joins / realize-label / "not refuted" filters instead of shared
helpers. Every claim below was read from the current tree today, not carried over from
prior scratchpad files — where it corroborates or updates `07_SQL_Surface_Audit.txt`
(2026-07-02, itself marked partially DRAINED) or `10_SQL_Consolidation_Reconciliation.txt`
that is called out explicitly, because several of those findings have since been fixed
and citing them as open would be wrong.

Verified against source: `app/Laplace.Substrate.Tests/Abstractions/ReadPathArchitectureGateTests.cs`,
`app/Laplace.Substrate/Crud/Npgsql/{NpgsqlRead,NpgsqlSubstrateReader}.cs`,
`extension/laplace_substrate/src/{spi_common.h,fold_route.c,graph_contrast.c,graph_taxonomy.c,variant_synth.c,generate_walk.c,recall.c}`,
`extension/laplace_substrate/sql/**/*.sql.in`, and every C# file named in the two gate
allowlists.

---

## 0. Headline finding

The single highest-value, lowest-risk consolidation is **not** a new abstraction — it's
finishing the migration onto one that already exists and is already proven:
`NpgsqlRead` (`app/Laplace.Substrate/Crud/Npgsql/NpgsqlRead.cs`). Its own doc-comment says
it replaces a pattern "hand-copied roughly a hundred times." `Laplace.Endpoints.OpenAICompat`
— the biggest single file group on `HandWrittenSqlAllowlist` — never adopted it: it built
its own private, near-byte-identical reimplementation instead (Cluster 1 below). Fixing
that one file family is mechanical, has no SQL-text changes, and retires ~9 of the 33
`HandWrittenSqlAllowlist` entries in one motion.

The second headline finding is a **live, currently-swallowed bug** caused by exactly this
kind of duplication: `Laplace.Cli/Provenance/ProvenanceExtractor.cs:263-287` selects
`c.witnesses` — a column that does not exist on `laplace.consensus` (the real column is
`witness_count`, confirmed against `extension/laplace_substrate/sql/schema/tables/consensus.sql.in:31`)
— inside a `try { } catch { }` that swallows the resulting `PostgresException` silently.
The circuit-provenance feature has been failing (and returning empty results) instead of
throwing, since whenever this code path was last touched. This is the kind of defect a
shared read helper with typed row-mapping would have caught at compile/first-run time
rather than never.

---

## 1. Cluster inventory

### Cluster 1 — Private reimplementation of `NpgsqlRead` inside `SubstrateClient.*`
**Category:** (1) dynamic SQL / connection boilerplate, overlapping (2) allowlist membership.

`Laplace.Endpoints.OpenAICompat.SubstrateClient` is a `partial class` split across 7 files.
It does **not** use `NpgsqlRead` (the sanctioned shared surface per the gate's own doc
comment, line 30 of the gate file). Instead:

- `SubstrateClient.Query.cs:274-301` defines a **private** `ReadRowsAsync<T>` that is a
  line-for-line reimplementation of `NpgsqlRead.ReadRowsAsync`: open connection → new
  `NpgsqlCommand` → optional timeout → bind → read loop → `catch (PostgresException)` /
  `catch (NpgsqlException or TimeoutException)`, translating to
  `SubstrateQueryException` / `SubstrateUnavailableException`.
- That private method is then called ~28 times across the partial class:
  `SubstrateClient.Chess.cs` (9 sites), `SubstrateClient.Matchup.cs` (6),
  `SubstrateClient.Mesh.cs` (2), `SubstrateClient.Query.cs` (11 more, self-referential).
- `SubstrateClient.cs` (the main partial, 814 lines) does **not even use its own sibling
  helper** — it hand-rolls the identical open/create/bind/execute/catch block manually at
  least 7 times (e.g. lines 29-44, 56-77, 94-135, 190-210, 636-680), each with its own
  copy-pasted `catch (PostgresException pg) { throw new SubstrateQueryException($"...
  query failed [{pg.SqlState}] {pg.MessageText}" + ...) }`.
- `SubstrateClient.Explore.cs` (1055 lines) is the worst offender: **14** raw
  `new NpgsqlCommand(sql, conn)` sites (lines 205, 286, 342, 534, 573, 616, 781, 867, 892,
  921, 948, 980, 1008, 1034), only 2 of which route through the shared private helper.

So within one C# assembly there are **three parallel implementations** of the same
transport mechanics: `NpgsqlRead` (unused here), the partial class's private
`ReadRowsAsync`, and ad-hoc inline blocks that don't even use that private helper.

**Target shared surface:** `NpgsqlRead.ReadRowsAsync` / `ReadFirstOrDefaultAsync` /
`ExecuteScalarAsync`, with `onError` bound once to a lambda that reproduces the exact
`SubstrateQueryException`/`SubstrateUnavailableException` split (this is a 1:1 behavioral
port — `NpgsqlRead.Translatable` already special-cases `NpgsqlException`/`TimeoutException`
the same way). Delete the private `ReadRowsAsync` in `SubstrateClient.Query.cs` once every
call site is ported.

**Difficulty:** Low-medium. Mechanical, file-by-file, no SQL text changes, ~50 call sites
across 7 files, each individually testable. The main risk is `SubstrateClient.cs`'s two
call sites that layer extra logic around the connection (`ConverseTenantScopedAsync`'s
`CREATE TEMP TABLE consensus AS SELECT * FROM laplace.scoped_consensus(...)` before the
read) — those need the connection to stay open across two commands, so they should use
`NpgsqlRead` variants that accept an already-open `NpgsqlConnection`, or gain one (today
`NpgsqlRead` only takes `NpgsqlDataSource`; this is the one real gap to close first).

---

### Cluster 2 — Catalog-function calls reimplemented 2-3x with separate binding/mapping
**Category:** (2) allowlist + (3) shared-helper bypass. This is exactly what the gate's
own doc comment (lines 20-24) predicts and names: "18 functions called from two or three
consumers with separately written binding and result mapping."

Confirmed independent implementations, by function, with file:line evidence:

| Function | Call sites (own SQL text + own binding + own row map each) |
|---|---|
| `recall_session` | `Laplace.Cli/QueryCommands.cs:68` · `Laplace.Endpoints.Mcp/SubstrateTools.cs:147` · `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:96` |
| `walk_text` | `Laplace.Cli/QueryCommands.cs:246` · `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:131` |
| `walk_branches` | `Laplace.Endpoints.Mcp/SubstrateTools.cs:196` · `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:325` (+ `SubstrateClient.Query.cs` beam/greedy variants) |
| `resolve_ref` | `Laplace.Endpoints.Mcp/SubstrateTools.cs:156,166,208` · `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:555,709` |
| `salient_facts` | `Laplace.Cli/QueryCommands.cs:192` · `Laplace.Endpoints.Mcp/SubstrateTools.cs:212` · `Laplace.Chess/Service/ChessMoveCommentary.cs:146` |
| `substrate_counts` | `Laplace.Endpoints.Mcp/SubstrateTools.cs:225` · `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:199,639` (2 call sites in the same file) |
| `entity_physicalities` | see Cluster 5 (own table below — 5 sites) |
| `consensus_out_readable` | `Laplace.Cli/QueryCommands.cs:482` · `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:726` |
| `word_id` | `Laplace.Endpoints.Mcp/SubstrateTools.cs:208` · `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:675` · `SubstrateClient.Explore.cs:187,192,330` · `Laplace.Chess/Service/ChessMoveCommentary.cs:135,146` |

Three consumer families (`Laplace.Cli` / CLI, `Laplace.Endpoints.Mcp` / MCP tool surface,
`Laplace.Endpoints.OpenAICompat` / HTTP) independently decide the SQL text, the parameter
binding style (`AddWithValue` vs `NpgsqlParameter{NpgsqlDbType=...}` vs positional `$1`),
and the row-to-DTO mapping for the *same* 9 functions. None of the three share a type for
"a recall_session row" or "a walk_branches row" — `QueryRow` (OpenAICompat), the MCP
tool's ad-hoc `Rows(...)` helper, and QueryCommands' direct `Console.WriteLine` are three
different shapes for the same three columns (`reply/eff_mu/witnesses` or similar).

**Target shared surface:** Extend `Laplace.Substrate/Crud/Npgsql` with one typed reader
per catalog function (`NpgsqlRead`-style static methods, e.g.
`SubstrateCatalogReads.RecallSessionAsync(dataSource, prompt, session, ct)` returning a
shared `RecallRow` record), so CLI/MCP/HTTP each call the same method and differ only in
how they render the result (console table vs JSON tool response vs HTTP DTO). This is a
bigger lift than Cluster 1 because it requires picking ONE canonical row shape per
function and updating 3 renderers, not just swapping the transport call.

**Difficulty:** Medium, per-function. Start with the ones that are already
byte-identical in SQL text (`substrate_counts`, `word_id`) before the ones with per-caller
extra clauses (`walk_branches`'s highway-band gating differs between MCP and OpenAICompat).

---

### Cluster 3 — `laplace.consensus_by_ids($1, $2)` duplicated verbatim across 4 Chess files
**Category:** (2) allowlist + (3) shared-helper bypass. Cleanest, smallest, highest
confidence cluster in this inventory — near-zero judgment calls.

Four files on `HandWrittenSqlAllowlist`, each with its own copy of the identical query
text, identical two-parameter binding (`bytea[]` ids + single `bytea` type id), and
near-identical `Dictionary<Hash128, (double, double[, double])>` row mapping:

- `Laplace.Chess/Service/SubstrateTurnHost.cs:52-66` — `SELECT id, eff_mu, witness_count FROM laplace.consensus_by_ids($1, $2)`, binds `ChessVocabulary.MoveType`.
- `Laplace.Chess/Service/LearnedPst.cs:91-105` — identical SQL, binds `ChessVocabulary.OutcomeType`.
- `Laplace.Chess/Service/SubstrateRootBias.cs:62-79` — identical SQL, binds `ChessVocabulary.MoveType`.
- `Laplace.Chess/Service/SubstrateStateValuer.cs:73-88` — same shape plus `rd` column (`SELECT id, eff_mu, rd, witness_count FROM laplace.consensus_by_ids($1, $2)`), binds `ChessVocabulary.OutcomeType`.

Historical note: an earlier audit (`07_SQL_Surface_Audit.txt` §1.1, 2026-07-02) found
these four files inlining the raw formula `(rating - 2*rd)` instead of calling `eff_mu()`.
**That has since been fixed** — all four now call `consensus_by_ids()`, which returns a
precomputed `eff_mu` column. What's left is a narrower, purely mechanical residual: the
same ADO.NET call-and-map boilerplate copy-pasted 4 times around that one query.

**Target shared surface:** One method,
`NpgsqlRead`-family `ConsensusByIdsAsync(NpgsqlDataSource, IReadOnlyList<Hash128> ids, Hash128 typeId, CancellationToken)`
returning `IReadOnlyDictionary<Hash128, ConsensusStat>` (`ConsensusStat` = `EffMu`, `Rd`
(nullable, since 3 of 4 sites don't select it), `Witnesses`). Each of the 4 call sites
becomes a 1-line call plus its own post-processing (`ChessShrink.Apply`, etc. — those
stay, they're genuinely different per caller).

**Difficulty:** Low. Smallest, most mechanical cluster here — good first PR to prove the
pattern before tackling Cluster 1/2.

---

### Cluster 4 — Direct `FROM laplace.consensus` / `JOIN laplace.consensus` bypassing `edges()`
**Category:** (3) reimplements consensus scans instead of the shared catalog surface.

`edges()` / `edges_raw()` (`extension/laplace_substrate/sql/functions/consensus/edges.sql.in`)
is the extension's own answer to this exact problem — its header comment documents that it
replaces **nine** SQL-side ways to scan consensus that "disagreed on four independent
axes" (table, direction, ranking, refuted-inclusion). That consolidation happened inside
the extension. It was never extended to the C# call sites that hand-roll the same axes
directly against the table:

- `Laplace.Cli/EvalCommands.cs:70,74,86-87,92,105-106` — six separate inline joins to
  `laplace.consensus` (vocab CTE, synonym-pair CTE, and two `LEFT JOIN laplace.consensus
  c1/c2 ON c1.id = laplace.consensus_id(...)` triple-lookups) building a full eval kernel
  in C#. **No refuted filter at all** — reads raw `consensus`, not `v_consensus_unrefuted`
  or `edges()` with its `p_refuted` default of `false`.
- `Laplace.Cli/FoundryExport.cs:230` — `FROM laplace.consensus` for a plane export; same
  file already calls `relation_plane`/`consensus_adjacency` elsewhere for the equivalent
  shape (per `07_SQL_Surface_Audit.txt` §1.5, still true today).
- `Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateReader.cs:286-291`
  (`GetEdgeStrengthsAsync`) — `LEFT JOIN laplace.consensus c ON c.id = laplace.consensus_id(s.sid, $3, o.oid)`, no refuted filter, computing `eff_mu_display` per pair. This is in
  the *sanctioned* `Crud/Npgsql` folder, so it's exempt from the gate, but it's still a
  hand-rolled consensus join that duplicates the by-triple lookup pattern below.
- `Laplace.Endpoints.OpenAICompat/SubstrateClient.Chess.cs:130-133` — `JOIN laplace.consensus c ON c.subject_id = p.id AND c.type_id = ...` for chess player Elo, no refuted filter (chess OUTCOME rows are presumably never refuted in practice, but nothing enforces that here).
- `Laplace.Endpoints.OpenAICompat/SubstrateClient.Query.cs:115-127` (`BandFactsAsync`) —
  `UNION ALL` of two direct `FROM laplace.consensus c WHERE c.subject_id = @topic` /
  `WHERE c.object_id = @topic` scans, replicating exactly the "both direction" axis
  `edges()` already parametrizes as `p_direction = 'both'`, minus refuted-exclusion.
- `Laplace.Substrate/Abstractions/FeedbackContent.cs:129-131` (`ConsensusStateAsync`) —
  `SELECT rating, rd, witness_count FROM laplace.consensus WHERE subject_id=@s AND
  type_id=@t AND object_id=@o`, the third independent "look up one cell by (s,t,o)"
  implementation (alongside `NpgsqlSubstrateReader`'s and `EvalCommands`' `consensus_id`-
  join forms). This one is arguably correct to stay raw — it explicitly wants the prior
  state *including refuted* cells for feedback bookkeeping — but it should still share the
  by-triple lookup, not reinvent it a third way.
- `Laplace.Cli/Provenance/ProvenanceExtractor.cs:262-268` — `FROM laplace.consensus c
  WHERE c.subject_id = ANY($1::bytea[]) AND c.type_id = $2`, **the live bug**: selects
  `c.witnesses` (does not exist; real column `witness_count`), wrapped in `catch { }` that
  swallows the resulting `PostgresException` silently (lines 259-287). This is functionally
  identical to `consensus_by_ids()` (Cluster 3) — a batch-by-type consensus lookup — except
  hand-rolled here a fifth way, and broken.

**Target shared surface:** `edges()`/`edges_raw()` where a general edge scan is wanted
(EvalCommands' vocab/pair discovery, FoundryExport's plane export); `consensus_by_ids()`
(the one Cluster 3 already exists for) for the batch-by-type-and-ids shape
(ProvenanceExtractor should call this instead of hand-rolling — it would have caught the
`witnesses`/`witness_count` bug immediately). For the single-triple lookup
(`FeedbackContent`, `NpgsqlSubstrateReader.GetEdgeStrengthsAsync`, `EvalCommands`'
pairwise joins), the extension has no single-triple or small-batch-of-triples function
today — `consensus_by_ids` takes an id array, not (subject,type,object) triples. This is
the one place a genuinely new shared surface, not just an adoption of an existing one, may
be warranted: `edge_strength(subject, type, object)` was already proposed in
`07_SQL_Surface_Audit.txt` §1.2/§4.2 and is still missing.

**Difficulty:** Medium-high. Mixed: some sites (ProvenanceExtractor) are a drop-in swap
to `consensus_by_ids` (low difficulty, fixes a live bug); EvalCommands is a deliberate
"eval kernel in C#" that would need a new SQL-side function
(`laplace.eval_relation_pairs(...)`, as `07_SQL_Surface_Audit.txt` §1.5 already proposed)
to fully retire, which is a larger, riskier change; the single-triple lookup needs a new
shared primitive before three call sites can converge on it.

---

### Cluster 5 — `entity_physicalities()` LATERAL-join boilerplate copy-pasted 5x
**Category:** (3) reimplements entity+physicality join instead of a shared C# reader.

The identical shape — `LEFT/CROSS JOIN LATERAL (SELECT type, x, y, z, m, radius,
n_constituents FROM laplace.entity_physicalities(<id>) ORDER BY type LIMIT 1)` or its
bare `FROM laplace.entity_physicalities(@id) p` form — is hand-written at:

- `Laplace.Cli/QueryCommands.cs:527-530`
- `Laplace.Cli/IngestCommands.cs:941` (`CROSS JOIN laplace.entity_physicalities(laplace.canonical_id('A')) p`)
- `Laplace.Endpoints.OpenAICompat/SubstrateClient.cs:250-254` **and again** at
  `:715-719` — the *same file* has this LATERAL block copy-pasted twice, ~460 lines apart
  (already flagged as a same-file copy-paste in `07_SQL_Surface_Audit.txt` §1.4, still true
  today, unresolved).
- `Laplace.Endpoints.OpenAICompat/SubstrateClient.Explore.cs:888`

Each site does its own `r.GetDouble(n)`/`r.IsDBNull(n)` unpacking into a different local
tuple/record shape.

**Target shared surface:** A new typed reader,
`NpgsqlRead`-family `EntityPhysicalityAsync(NpgsqlDataSource, Hash128 id, CancellationToken)`
returning a shared `EntityPlacement` record (`Type, X, Y, Z, M, Radius, NConstituents`).
This is the `entity_form(id)` primitive `07_SQL_Surface_Audit.txt` §4.2 item 5 already
named as missing — still missing today, one month later.

**Difficulty:** Low. 5 call sites, one obvious shared shape, no SQL semantics to
reconcile (all 5 already query the identical function with the identical column list).

---

### Cluster 6 — `NOT refuted(...)` reimplementation — RESOLVED, verify-only
**Category:** (3), but this is the one requested cluster that is **already fixed** at the
SQL layer and should not be re-flagged as open work.

`v_consensus_unrefuted` (`extension/laplace_substrate/sql/views/v_consensus_unrefuted.sql.in`)
exists, its own header comment states it replaced "~21 functions that each reimplemented
`NOT refuted(c.rating, c.rd)` inline," and a live grep today finds **zero** files under
`extension/laplace_substrate/sql/**/*.sql.in` still inlining `NOT refuted(...)` — every
result for `refuted` in the tree is either the view definition itself or a legitimate
consumer of it (`consensus_peer`, `related`, `related_in`, `salient_facts`,
`realize_defines`, `realize_has_name`, `realize_translation`, `realize_synset_lemma`,
`resolve_name`, `concept_members`, `concept_peers`, `taxonomy/*`, etc.). A dedicated test,
`app/Laplace.Substrate.Tests/Crud/SqlConsolidationTests.cs:96-101,116-119`, pins that the
view exists post-install and that `collocates()` reads through it rather than an inline
copy. **No action needed here** — listed only because the prompt asked explicitly for
this pattern and a thorough inventory should say "checked, clean" rather than silently
omit it.

The one open item in the same family: `edges.sql.in`'s own header (lines 6-15) documents
that `explore_web_neighbors` and `foundry_crawl_neighbors` are **intentionally** raw-table
readers (refuted rows included, by design, for crawl/exploration purposes) — that's a
correct, documented exception, not a violation.

---

## 2. C extension: `EXECUTE format` / dynamic SQL inventory

### 6a. Already fixed — kept as a template for 6b
`extension/laplace_substrate/src/fold_route.c` is the extension's own prior fix for
*exactly* this class of problem. Its header comment (lines 1-37) documents that the
plpgsql bodies it replaced used `EXECUTE format(%L)` per relation-type to get LIST-
partition pruning on `attestations`/`consensus` UPDATEs, and that this caused a full
re-plan (and, for a bug window, a full 1,300-leaf Append scan) per call. The fix: group
rows by type in C, then execute a **session-cached prepared plan per type** via an
`HTAB` of `type_id -> SPI_keepplan'd SPIPlanPtr` (`fold_route.c:56-133`), so the
hex-literal substitution into SQL text (`typed_plan()`, lines 86-133) happens once per
(backend, type) ever, not once per call. `attestation_merge.sql.in` and
`consensus_fold_result.sql.in` are now 1-line `LANGUAGE C` shims onto this
(`pg_laplace_attestation_merge`, `pg_laplace_consensus_upsert`); their `.sql.in` header
comments keep the old plpgsql `EXECUTE format(%L)` forms only as commented-out history.

### 6b. The exact same anti-pattern, still live, one file over
`extension/laplace_substrate/sql/functions/ops/evict_source.sql.in` (a `plpgsql`
`PROCEDURE`, ~260 lines) contains **7** `EXECUTE format($q$ ... %L ... %s ... $q$, rel, ...)`
blocks (lines 102-111, 130-155, 166-192, 197-208) that substitute the relation-type
`bytea` as a hex literal into DELETE/UPDATE/INSERT text against `attestations` and
`consensus` for the identical reason `fold_route.c`'s comment describes: "a variable type
plans generically and opens every leaf" (the procedure's own comment at lines 12-14
literally names "the attestation_merge lesson"). This is **not** a bug — it is correctly
using the documented workaround — but it is the one place in the tree where the
workaround that `fold_route.c` proved to have a better (session-cached-plan) answer has
not been ported. `evict_source` runs on eviction (source retraction), an infrequent
administrative path rather than the ingest hot path `fold_route.c` targets, which is
probably why it hasn't been prioritized — but it is a real, reproducible instance of the
requested `EXECUTE format` / dynamic-SQL pattern in `laplace_substrate`.

**Target shared surface:** Either (a) leave as-is (infrequent path, correctness is fine,
just not maximally fast), or (b) port to a `fold_route.c`-style native routine with a
per-type cached plan HTAB, mirroring `typed_plan()`. Given eviction is rare and batched
with `COMMIT` between batches already, (a) is likely the right call — flagged here for
completeness per the explicit ask, not as a recommended priority.

**Difficulty if pursued:** High (native C rewrite of a 260-line plpgsql procedure with
three DML shapes instead of `fold_route.c`'s two) for a cold path — low expected ROI.

### 6c. Legitimate, lower-priority `EXECUTE format` idioms (DDL bootstrap/upgrade, not query duplication)
These are real `EXECUTE format`/`EXECUTE '...'` sites but are structurally different from
6a/6b — they are one-time schema bootstrap or upgrade-time object retirement, not a
reimplemented *read*:

- **Partition fan-out at CREATE TIME** (schema definition, not a query):
  `sql/schema/tables/entities.sql.in:35-49` (`CREATE TABLE entities_t2_h%s PARTITION OF
  entities_t2`, looped per hash bucket), `sql/schema/tables/physicalities.sql.in:46-56`
  (same shape). Necessary because PostgreSQL has no declarative "N hash partitions"
  syntax pre-parameterization; this is the standard idiom, not duplication to fix.
- **"Drop retired object" idempotent upgrade blocks** — the same
  `FOREACH obj IN ARRAY [...] LOOP BEGIN EXECUTE format('ALTER EXTENSION %I DROP %s', ...)
  EXCEPTION WHEN OTHERS THEN NULL; END; END LOOP;` skeleton is copy-pasted across at least
  4 files: `sql/functions/generation/drop_retired_content_lane.sql.in` (2 separate `DO $do$`
  blocks in one file), `sql/indexes/drop_entities_tier_btree.sql.in`, plus others matched by
  grep (`drop_apply_batch_merge_*.sql.in`, `drop_retired_case_surface.sql.in`,
  `drop_retired_english_router.sql.in`, `drop_retired_ingest_lanes.sql.in`,
  `drop_content_descent_novel_ordinals.sql.in`, `drop_retired_constituent_edges.sql.in`,
  `drop_retired_eff_mu.sql.in`, `drop_retired_chess_aggregates.sql.in` — 10+ files share
  this exact FOREACH/EXCEPTION skeleton verbatim, only the object-name array and the
  trailing unconditional `DROP` statements differ).
  **Target shared surface (low priority, real but cosmetic):** a small plpgsql helper,
  e.g. `_drop_retired_objects(text[])`, taking the `'TABLE @extschema@.x'`-style spec array
  and doing the release-then-drop loop once. Would collapse ~15-20 lines of duplicated
  control flow per file down to a 1-line call, across ~10 files.
  **Difficulty:** Low, but genuinely cosmetic — these files run once per upgrade and are
  already individually correct; the value is readability/maintenance, not correctness or
  performance.
- **DB bootstrap GRANT idioms**: `db/migrations/20260606000000_layer1_database.sql:10-34`
  — conditional `EXECUTE 'GRANT ...'` gated on `pg_roles` existence (roles may not exist in
  every environment). Standard DbUp/bootstrap idiom, single file, not duplicated elsewhere.
  No action warranted.
- `db/migrations/20260724000000_ops_logs.sql:67` — `EXECUTE format('ALTER FOREIGN TABLE
  ops.pg_log OPTIONS (SET filename %L)', f)`, a one-off FDW option rewrite. Not duplicated.

### 6d. SPI-level duplication inside the extension's C sources
The extension already has its own shared-helper file for this,
`extension/laplace_substrate/src/spi_common.h` (`spi_realize`, `spi_label`,
`spi_type_label`, `spi_word_language`, `spi_render_text`, `spi_fetch_rd_kappa`,
`spi_emit_all_rows`, `spi_fetch_synset_ids`) — this is the C-extension-side analogue of
`NpgsqlRead.cs`, included by 14 of the ~20 `.c` files that call SPI. Two duplications slip
past it:

- **`realize_batch` inline SPI call, duplicated verbatim in two files, not lifted into
  `spi_common.h`:**
  `graph_contrast.c:328-330` and `graph_taxonomy.c:402-404` both contain the byte-identical
  `SPI_execute_with_args("SELECT laplace.realize_batch($1, $2)", 2, rtypes, rargs, rnulls,
  true, 1)` call, each wrapped in ~15 lines of duplicated array-construct /
  `deconstruct_array` boilerplate around it (`graph_contrast.c:305-339`,
  `graph_taxonomy.c:390-428`). `spi_common.h` has `spi_realize` (singular) and
  `spi_fetch_synset_ids` but no batch-realize helper, so both call sites had to write their
  own.
  **Target:** add `spi_realize_batch(Datum ids_array, Datum lang, bool **nulls_out, char ***out, int *n_out)`
  (or a `Datum`-returning form matching the existing `spi_*` helpers' style) to
  `spi_common.h`; both call sites shrink to a 2-3 line call.
  **Difficulty:** Low — the two call sites are already provably identical in the part that
  would move (verified above); only the surrounding array-construction differs slightly
  (object ids + type ids in `graph_contrast.c` vs just ids in `graph_taxonomy.c`), which
  stays local to each caller.
- **`render_text` arity split, not a true duplicate but worth flagging:**
  `spi_common.h:245-258` (`spi_render_text`) calls the 1-argument
  `SELECT laplace.render_text($1)`; `variant_synth.c:77` independently calls the 2-argument
  `SELECT laplace.render_text($1, $2)` inline rather than extending `spi_common.h` with an
  overload. Not wrong (different arity, different need), but it means a future caller
  needing the 2-arg form has no shared helper to reach for and will likely re-inline it a
  third time.
  **Difficulty:** Low, optional — add `spi_render_text_lang` alongside `spi_render_text`.

---

## 3. Gate allowlist → cluster mapping

`OwnDataSourceAllowlist` (7/7 ceiling — already at ceiling, cannot grow):
all 7 entries (`ChessEngineService.cs`, `ChessLabRunners.cs`, `ChessLiveGameHost.cs`,
`ChessPgnIngestor.cs`, `UciEngine.cs`, `SubstrateTools.cs`, `Laplace.Migrations/Program.cs`)
are about *datasource construction*, a narrower and already-documented concern (the gate's
own doc comment explains each). Not re-audited in depth here since the prompt's 3 clusters
are about SQL text, not datasource wiring, but note that fixing Cluster 1
(`SubstrateClient.*` onto `NpgsqlRead`) does not touch this list — `SubstrateClient`
already isn't on `OwnDataSourceAllowlist` (it correctly uses `LaplaceDataSource.Create`).

`HandWrittenSqlAllowlist` (24/24 ceiling — was 33; Mesh/Matchup/Pulse + Chess
`consensus_by_ids` drained 2026-08-01), mapped to clusters above:

| File | Cluster(s) |
|---|---|
| `SubstrateClient.Chess.cs`, `SubstrateClient.cs`, `SubstrateClient.Explore.cs`, `SubstrateClient.Matchup.cs`, `SubstrateClient.Mesh.cs`, `SubstrateClient.Pulse.cs`, `SubstrateClient.Query.cs` (7 files) | Cluster 1 (primary), Cluster 2, Cluster 4, Cluster 5 |
| `QueryCommands.cs` | Cluster 2, Cluster 5 |
| `SubstrateTools.cs` | Cluster 2 |
| `Laplace.Chess/Service/LearnedPst.cs`, `SubstrateRootBias.cs`, `SubstrateStateValuer.cs`, `SubstrateTurnHost.cs` (4 files) | Cluster 3 |
| `Laplace.Cli/Provenance/ProvenanceExtractor.cs` | Cluster 4 (live bug) |
| `Laplace.Cli/EvalCommands.cs`* | Cluster 4 |
| `Laplace.Cli/FoundryExport.cs` | Cluster 4 |
| `Laplace.Cli/IngestCommands.cs` | Cluster 5 |
| `Laplace.Substrate/Abstractions/FeedbackContent.cs` | Cluster 4 |
| `Laplace.Chess/Service/ChessMoveCommentary.cs` | Cluster 2 |
| `Laplace.Chess/Service/ChessWitnessHydrator.cs` | Chess-specific analytics joins (entities+attestations counts) — not a strong match for any existing catalog function; lower priority, likely a legitimate long-term resident of the allowlist. |
| `Laplace.Cli/ContentRoundtrip.cs` | Not covered by clusters above — hand-rolled `WITH RECURSIVE` trajectory descent + PostGIS unpack (`07_SQL_Surface_Audit.txt` §1.5, still open, still unique to this file). Would need a new `reconstruct_content(id) -> bytea` primitive to retire, out of scope for this inventory's 3 requested clusters but worth a follow-up. |
| `Laplace.Cli/CpuTopologyCommands.cs` | Not real query duplication — grep match is a `.conf`-file text generator that happens to contain the string `SELECT`, not a live query. Low value to chase. |
| `Laplace.Cli/FoundryCommands.cs` | One `render_text(word_id(s), 80)` batch call — no duplicate found elsewhere with this exact shape; low priority. |
| `AppComposition.cs`, `BillingBootstrap.cs`, `BillingPostgres/*` (5 files), `ApiKeys.cs` | Genuinely separate concern per the gate's own comment ("BillingPostgres/* ... may legitimately stay hand-rolled"); `ApiKeys.cs`/billing files already route through `NpgsqlRead` in several places (confirmed: `PostgresBillingEntitlementStore.cs:117`, `PostgresBillingLedger.cs:37`, `ApiKeys.cs:106,111` all call `NpgsqlRead.ReadRowsAsync` already) — these are the allowlist's best-behaved members and mostly stay on it only because of one or two remaining raw `CommandText` bootstrap probes. Not part of the 3 requested clusters. |
| `Laplace.Migrations/Program.cs` | Documented permanent exception (pre-extension bootstrap). No action. |

\* `EvalCommands.cs` is **not currently on `HandWrittenSqlAllowlist`** despite containing
6+ raw `laplace.consensus` joins (Cluster 4) — worth double-checking whether the gate's
regex (`\bCommandText\b|\bCreateCommand\s*\(\s*"|""\s*SELECT\s`) actually catches its
`@"..."` verbatim-string SQL (it uses `ds.CreateCommand(sql)` with `sql` as a `const
string` field, which may not match `CreateCommand\s*\(\s*"` literally since the argument
is an identifier, not an inline string literal). If the gate is supposed to catch this
file and doesn't, that's a gate coverage gap, not a clean bill of health — flagged for
verification, not asserted as certain, since re-running the actual test was out of scope
for a read-only pass.

---

## 4. Prioritized consolidation map

| # | Cluster | Files | Current pattern | Target shared surface | Difficulty | Why this order |
|---|---|---|---|---|---|---|
| 1 | Cluster 3 — `consensus_by_ids` dup | 4 (`LearnedPst`, `SubstrateRootBias`, `SubstrateStateValuer`, `SubstrateTurnHost`) | Identical SQL text + binding + dict-mapping, copy-pasted 4x | New `NpgsqlRead`-family `ConsensusByIdsAsync` | **Low** | Smallest, cleanest, zero semantic judgment calls — proves the pattern for reviewers before bigger PRs land. |
| 2 | Cluster 4 (ProvenanceExtractor only) — fix the live bug | 1 (`ProvenanceExtractor.cs`) | `c.witnesses` (wrong column) inside swallowed `catch{}` | Swap to `consensus_by_ids()` / the Cluster-3 helper from #1 | **Low** | Live correctness bug, not just style; also deletes hand-rolled SQL, so it's a 2-for-1 with #1's new helper. |
| 3 | Cluster 5 — `entity_physicalities()` LATERAL dup | 5 sites, 4 files (incl. 2x in one file) | Copy-pasted LATERAL + per-site row unpack | New `EntityPlacement`/`entity_form(id)` reader in `NpgsqlRead` family | **Low** | All 5 sites already call the identical function with the identical column list — no reconciliation needed, just extraction. |
| 4 | Cluster 1 — `SubstrateClient.*` onto `NpgsqlRead` | 7 files, ~50 call sites | Private reimplementation of `NpgsqlRead` (+ ad-hoc inline duplicates of that reimplementation) | `NpgsqlRead.ReadRowsAsync`/`ReadFirstOrDefaultAsync`/`ExecuteScalarAsync` with a bound `onError` translator | **Low-Medium** | Highest line-count impact (single biggest file family on the allowlist) but mechanical once the `NpgsqlRead`-needs-an-open-`NpgsqlConnection`-overload gap (for `ConverseTenantScopedAsync`'s temp-table-then-read) is closed. |
| 5 | C extension `realize_batch` SPI dup | 2 files (`graph_contrast.c`, `graph_taxonomy.c`) | Byte-identical `SPI_execute_with_args("SELECT laplace.realize_batch($1,$2)", ...)` inlined twice | New `spi_realize_batch(...)` in `spi_common.h` | **Low** | Small, contained, C-side mirror of #3 — same shape of win, smaller blast radius (2 files). |
| 6 | Cluster 2 — catalog-function triplication (recall_session, walk_text, walk_branches, resolve_ref, salient_facts, substrate_counts, consensus_out_readable, word_id) | CLI + MCP + OpenAICompat, ~9 functions x 2-5 sites each | 3 independently-authored read surfaces, no shared row type, no shared call | Typed readers per function in `Laplace.Substrate/Crud/Npgsql`, one shared row record per function | **Medium** | Real payoff but requires picking one canonical row shape per function and touching 3 renderers (console/JSON-tool/HTTP-DTO) each time; do incrementally, one function at a time, cheapest-first (`substrate_counts`, `word_id` have no per-caller SQL variation; `walk_branches` does and should go last). |
| 7 | Cluster 4 (remainder) — `EvalCommands`, `FoundryExport`, `NpgsqlSubstrateReader.GetEdgeStrengthsAsync`, `FeedbackContent` | 4 files | Hand-rolled consensus joins/lookups, 3 different "look up by triple" implementations | `edges()`/`edges_raw()` for scans; a new `edge_strength(subject,type,object)` (+batch) primitive for the triple-lookup shape — proposed but not built | **Medium-High** | `EvalCommands` is a genuine "eval kernel embedded in C#" (per `07_SQL_Surface_Audit.txt` §1.5) — retiring it fully means writing new SQL (`laplace.eval_relation_pairs`), not just swapping a call. Do the low-risk swaps (FoundryExport → existing `relation_plane`) before the new-primitive work. |
| 8 | `evict_source.sql.in` `EXECUTE format` (6b) | 1 file | plpgsql per-type literal substitution, same disease `fold_route.c` already cured | Native C routine mirroring `fold_route.c`'s `typed_plan()` HTAB | **High**, low ROI | Cold/administrative path; only worth doing if eviction throughput becomes a real bottleneck. Listed for completeness per the explicit ask, not recommended as near-term work. |
| 9 | `drop_retired_*` EXECUTE-format skeleton dup (6c) | 10+ `.sql.in` files | Identical `FOREACH/BEGIN EXCEPTION/END LOOP` idiom around `ALTER EXTENSION ... DROP` | New `_drop_retired_objects(text[])` plpgsql helper | **Low**, cosmetic only | Real duplication, zero behavior risk, but purely a readability win on one-shot upgrade files — do only if touching these files anyway. |

---

## 5. What was checked and found clean (worth knowing, not re-chasing)

- `NOT refuted(...)` inline reimplementation — fully consolidated onto
  `v_consensus_unrefuted`, test-pinned (Cluster 6 above).
- The extension's own consensus-edge-read family (`consensus_subject_edges`,
  `consensus_neighbors_directed/undirected`, `consensus_walk_edges`, `consensus_step_edge`,
  `explore_web_neighbors`, `foundry_crawl_neighbors`, `related`/`related_in`,
  `related_objects`) — this was the exact duplication `07_SQL_Surface_Audit.txt` §2.3
  flagged a month ago ("Overlapping consensus-edge reader family... all re-expressing
  'consensus JOIN entities, unrefuted, ORDER BY eff_mu'"). It has since been resolved by
  `edges()`/`edges_raw()`, per that function's own header comment, which explicitly
  enumerates and retires all nine. The remaining gap is that C# consumers don't call
  `edges()` yet (Cluster 4) — the SQL-layer half of this problem is done.
- The two `Laplace.Migrations`/`db/migrations` `EXECUTE` sites — legitimate bootstrap
  idioms, single-use, not duplicated.
