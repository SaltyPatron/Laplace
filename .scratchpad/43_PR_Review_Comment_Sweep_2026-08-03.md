# PR review-comment sweep — last 100 merged PRs (2026-08-03)

## Method and its limits

`gh api` over the last 100 merged PRs returned **72 review comments** spanning PR #283 → #837:
70 from Copilot, 2 from SaltyPatron. Copilot comments are advisory — nothing blocks a merge on
them, so a merged PR is not evidence a comment was addressed.

**Verified against `origin/main` by reading the current code: 8.** The remaining 64 are
triaged by reading the comment text only and are marked *unverified* below. Do not treat this
document as a completed audit of all 72 — it is a completed audit of the 8 highest-severity
ones plus a triage of the rest.

Two comments are confirmed *resolved* by the reviewer themselves: #772 (SaltyPatron: "Fixed in
d87a8d9") and #762 (SaltyPatron: checked against the running system, concern does not hold).

---

## A. Confirmed still live on `main` — verified this session

### A1. Deploy ingest-guard fails open — PR #797, #778 · `scripts/wait-for-quiet-substrate.sh:32`

```
"SELECT count(*) FROM laplace.ingest_run_journal WHERE status = 'running';" 2>/dev/null || echo 0
```

stderr is discarded and **any** psql failure yields `0`, which reads as "no ingest running" and
lets the deploy proceed. DB unreachable, auth misconfigured, `laplace` schema absent, extension
not installed, connection cap reached — all report quiet.

This is the guard standing between a push to `main` and a running ingest, and `CLAUDE.md` records
the consequence: *"A push to `main` restarts `laplace-postgresql` and kills any running ingest."*
The one failure mode the guard exists to prevent is the one it cannot detect. Copilot filed it
twice, on two separate PRs, and it is unchanged.

Fix: distinguish exit status from result. Non-zero psql exit must be treated as **busy**, not
quiet — fail closed.

### A2. Worker-id telemetry is a captured loop variable — PR #783 · `IngestPipeline.cs:532-544`

```csharp
for (int w = 0; w < workers; w++)
    workerTasks[w] = Task.Run(async () =>
    {
        await foreach (var source in sources.Reader.ReadAllAsync(ct))
        {
            ...
            int workerId = w;      // <-- reads the shared loop variable at RUN time
```

`int workerId = w;` looks like a per-iteration copy but sits *inside* the lambda. C# gives
per-iteration capture semantics to `foreach`, **not** to `for` — every task closes over the one
shared `w`, reads it after the loop has advanced, and races with the loop thread mutating it.

Consequence beyond cosmetics: every `INGEST_FILE_START` / `INGEST_FILE_COMPOSED` /
`INGEST_FILE_FAILED` line carries an arbitrary worker id, usually the loop's exit value for all
workers. **This is the telemetry task #19 ("measure the parallel file-worker speedup end to end")
would be measured from.** Per-worker attribution is currently unusable.

Fix: hoist the copy above `Task.Run` — `int workerId = w; workerTasks[w] = Task.Run(...)`.

### A3. PostgreSQL array literals built without quoting — PR #827, #815 · `InstalledOpInvoker.cs:158`

```csharp
JsonArray a => "{" + string.Join(",", a.Select(e => OpValue(e) as string ?? "NULL")) + "}",
```

Elements containing a comma, brace, quote, backslash or leading/trailing whitespace mis-parse
silently, and the literal `NULL` string is indistinguishable from a SQL NULL. An element
containing `,` splits into two array members — wrong results, not an error.

Same defect was flagged independently on the MCP surface (#815, `SubstrateTools.cs`). This is the
`op` boundary, i.e. the surface whose entire premise is "no SQL across the boundary."

Fix: bind the array as a typed Npgsql parameter (`NpgsqlDbType.Array | ...`) rather than
composing a literal. That removes the quoting question rather than answering it.

### A4. `OpeningCatalogAsync` has no `ORDER BY` but the consumer depends on order — PR #797 · `NpgsqlSubstrateReads.cs:2209`

The consumer picks a stable "first wins" opening name when a position carries several. Without
`ORDER BY`, row order is whatever the plan produces, so which name a position gets can change
between runs, plans or after a vacuum. Non-deterministic naming in a content-addressed system.

Fix: order by something the fold produced — `eff_mu` descending, tie-broken on id.

### A5. STABLE function in a filter — PR #824 · `chess_missed_finish.sql.in:34`

```sql
WHERE a.type_id = chess_has_wdl_type()
```

`chess_has_wdl_type()` is `LANGUAGE sql STABLE`. `CLAUDE.md` states the rule outright: *"A
`STABLE` function in a filter runs per row."* This is that, verbatim, in the chess read path.

Fix: resolve the id once into a CTE/variable and compare against the scalar.

### A6. `geometry_audit` is declared STABLE and calls `random()` — PR #789 · `geometry_audit.sql.in:53,88`

`LANGUAGE plpgsql STABLE` with `format('WHERE random() < %s', ...)` in the sampled mode. STABLE
promises identical results within a statement; this returns a different sample each call. The
planner is entitled to act on that promise.

Fix: declare it `VOLATILE`. Sampling is volatile by definition.

### A7. Route guard matches by bare prefix — PR #830 · `TenantResolution.cs:132-133`

```csharp
var governed = path.StartsWith("/v1", ...) || path.StartsWith("/chess", ...);
```

`/v10/...` and `/chessboard/...` both match. This is a **tenant-governance** guard, so a
mismatch is an authorization-surface question, not a routing nicety. No such routes exist today,
which is exactly why it will not be noticed when one is added.

Fix: match on a segment boundary (`/v1/` or exact `/v1`).

### A8. `chess_missed_finish` scans all `HAS_WDL` attestations — PR #817 · `chess_missed_finish.sql.in`

The `wdl` CTE materializes every `HAS_WDL` attestation in the database and filters by join
afterwards. Cost scales with total Syzygy testimony, not with the query's subject. Compounds
with A5 in the same function.

---

## B. Recurring themes across the 72 — the classes worth a gate, not 72 patches

| theme | PRs | why it recurs |
|---|---|---|
| **Shell/CI errors swallowed → fail open** | #831, #797, #778, #283, #772 | `2>/dev/null \|\| echo 0`, `set +e`, ignored `dotnet build` rc. Every instance turns an infrastructure failure into a false "all clear". A1 is the costly one. |
| **SQL built by string interpolation** | #827, #815, #750 | Array literals and identifiers interpolated into SQL. |
| **STABLE/VOLATILE mislabelled or mis-sited** | #824, #789 | Already a written rule in `CLAUDE.md`; prose is not enforcing it. |
| **Full scans / N+1 in chess SQL** | #817 (×2), #786 (×3) | Correlated subqueries per row; `entity_curve()` computed for every candidate even when the anchor curve is NULL and Fréchet is skipped. |
| **Manifest ordering** | #749 (×2, install + upgrade) | Shared views/functions listed *after* the functions depending on them. |
| **Stale comments contradicting the code they head** | #750, #789, #787, #770, #796 | Matches the existing memory *"Comments are not evidence."* |
| **Unbounded/over-budget batching** | #283 (×2), #782 | A single intent larger than `BudgetBytes` is applied as one over-budget batch. |
| **Docs citing paths/anchors that do not exist** | #770 (×2), #766, #768 | |

Two of these themes are enforceable cheaply and would have caught seven of the eight A-items:

1. **A shell gate**: `shellcheck` in CI, plus a grep rejecting `|| echo 0` / `2>/dev/null` on a
   command whose result is a guard decision. Neither exists — I ran `shellcheck` this session and
   it is not installed on this box.
2. **A SQL-lint gate**: reject a `STABLE` function invoked inside `WHERE` in `.sql.in` files.
   `ReadPathArchitectureGateTests` already exists as the place for it.

---

## C. My own PR #837 — four comments, fixed in `d4443f3b`

Included for completeness because they were part of the same sweep and two were build-breaking:

1. `chess_openings` assigned only inside the cmake-configure branch, read unconditionally by the
   perfcache gate → under `set -u`, **every cached-fingerprint build aborts**. Hoisted.
2. The chess perfcache gate was a hard `exit 1` on a blob whose producing CMake target is not on
   `main` (it is on a separate branch) → any host with the openings corpus fails the build. The
   gate now self-arms off `LAPLACE_CHESS_OPENINGS:` in `CMakeCache.txt`.
3. `pg_reload_conf()` ran *before* `pg_apply_io_method` / `pg_apply_wal_compression`, which must
   probe a live connection and therefore run last — both settings sat unapplied in
   `postgresql.auto.conf`. **The emitter path had the same defect and no reload at all; that was
   not flagged in review.** Both paths now end at one `pg_tune_reload`.
4. `iow` assigned without `local` in a sourced function.

`PgTuningParityTests` 18/18.

---

## D. Unverified remainder (64)

Not individually checked against current code. Highest-value to check next, in order:

- #826 `ContentTierSpine.ResolveRoot` catches *all* `InvalidOperationException` → swallows the
  `rc=` engine failure alongside the expected miss.
- #283 (×2) `IngestRunner` over-budget single-intent batch — directly relevant to the memory
  ceiling during large ingests.
- #749 (×2) manifest install/upgrade ordering — a genuine install-order break if the dependency
  is not already present from a prior version.
- #817 `SyzygyTableUnpack.ExtractMaterialAsync` producer/consumer deadlock on early enumeration
  stop — becomes reachable the moment chess moves to multi-file parallel workers (task #22).
- #786 (×3) NULL-anchor guard around `entity_curve()` in the converse/geometry reads.
- #827 (×2) `/v1/op` error mapping: operation-level Postgres errors surface as
  `SubstrateUnavailableException` (503) instead of a 4xx, and `/v1/op` has no contract test.
- #830, #815 `ParseSignature()` treats `OUT` parameters as required inputs.
