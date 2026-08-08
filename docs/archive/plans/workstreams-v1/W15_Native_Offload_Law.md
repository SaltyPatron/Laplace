> Archived workstream analysis. Historical evidence only; generated inventory owns counts.

# W15 — Native offload: when C/SPI wins, when it loses, and the 218-function finding

**Status:** research + a measured census of this substrate's own surface.
No changes made. · **Related:** `CLAUDE.md` Reads (the offload rule),
`docs/specs/37` (ISA), W14 (machine model)

The question that started this: *why don't all these surfaces have native C
implementations with the recursion, loops, CTEs and RBAR offloaded to C/C++/SPI?*

The answer is not "write more C." It is that **60% of this schema is marked in a
way that forbids PostgreSQL from optimizing it at all**, and no amount of C
fixes that.

---

## 1. The census — measured 2026-08-02 on the live catalog

```sql
SELECT count(*) FILTER (WHERE l.lanname='sql' AND p.proparallel='u') AS sql_parallel_unsafe,
       count(*) FILTER (WHERE l.lanname='c'   AND p.proparallel='u') AS c_parallel_unsafe,
       count(*) FILTER (WHERE p.proconfig IS NOT NULL)               AS has_set_clause,
       count(*) FILTER (WHERE p.proisstrict AND l.lanname='sql')     AS sql_strict,
       count(*) FILTER (WHERE p.proretset)                           AS set_returning,
       count(*)                                                      AS total
FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang
JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='laplace';
```

| metric | count | of 363 |
|---|---|---|
| **functions carrying a `SET` clause** | **218** | **60%** |
| set-returning | 213 | 59% |
| SQL functions marked PARALLEL UNSAFE | 87 | 24% |
| C functions marked PARALLEL UNSAFE | 56 | 15% |
| SQL functions marked STRICT | 39 | 11% |
| by language | 245 sql · 79 c · 27 plpgsql · 12 internal | |

Two of those rows are the whole story.

## 2. Finding #1 — `SET search_path` disables inlining on 218 functions

The planner will inline a `LANGUAGE sql` function body into the calling query —
exposing it to predicate pushdown, index selection and constant folding — only
under a fixed list of conditions. From the PostgreSQL wiki, for **both** scalar
and set-returning forms:

> *the function has no `SET` clauses in its definition*

That is unconditional. It is not "a SET clause may cost you"; a SET clause means
the body is a black box the planner executes as an opaque call, per row, with no
visibility inside.

**Every function in this tree is generated from a template ending
`SET search_path = @extschema@, public`.** So 218 functions — including the
entire converse, lexical and consensus read path — are non-inlinable *by
construction*, for a reason nobody chose per-function.

`CLAUDE.md` already knows the rule and states it for exactly one function:

> *`eff_mu` bodies must not carry `SET` or `STRICT` — either kills SQL inlining
> and the index path with it.*

The rule is right and its scope is wrong. It was learned from one incident and
applied to one body while 218 others carry the same marking.

**There is exactly ONE distinct function-level `SET` in this schema.** Every
occurrence across `extension/laplace_substrate/sql/`:

| occurrences | usage |
|---|---|
| 190 | `SET search_path = @extschema@, public AS $$` |
| 9 | `SET search_path = @extschema@, public` |
| 4 | same string, different dollar-quote tags |
| 1 | `SET search_path = public AS $$` (no `@extschema@` — verify: deliberate or bug) |
| **204** | **total, all `search_path`** |
| 4 | genuine `UPDATE … SET` (rating, rd, highway_mask) — DML, unrelated |

No `work_mem`, no `enable_seqscan`, no `jit`, no per-function tuning of any
kind. **One string, 204 times.** That is not a considered security posture whose
cost was weighed against inlining — it is a template artifact, and its only job
is making unqualified references resolve.

**Why the `SET` is really there — measured, not assumed:**

| | count |
|---|---|
| function files | 337 |
| carry `SET search_path` | 221 |
| use `@extschema@.` qualification anywhere in the body | 68 |
| **carry the `SET` and qualify NOTHING** | **190** |

So the `SET` is **not** a deliberate security posture applied across the
surface. For 190 functions it is **load-bearing**: the bodies reference
`consensus`, `prompt_state`, `entities` bare, and without the `SET` they would
not resolve. It is a crutch for unqualified bodies, and calling it a "template
default nobody chose" — as an earlier draft of this document did — dresses up a
code-quality defect as an unfortunate inheritance.

The schema itself is not the problem and should not be blamed: an extension
installs into its own schema via `@extschema@`, and putting 363 functions in
`public` would collide with everything. **The unqualified bodies are the
defect, and they are what forced the `SET`.**

**The fix, in the honest order:** qualify the 190 unqualified bodies with
`@extschema@.`, THEN drop the `SET`. Qualification defeats the same search_path
attack the `SET` defends against, without blinding the planner. This is
mechanical — a bounded edit per body, greppable, and verifiable by regress —
but it is **190 bodies of real work**, not a one-line template change. Any plan
that claims otherwise has not looked.

**What it would buy, and the honest uncertainty:** inlining lets a predicate
reach the index inside the function body. For a read like
`eff_mu(rating, rd) >= x` over partitioned `consensus`, that is the difference
between an index scan and a full scan per call. On this substrate the win is
plausibly large and **is not yet measured** — the measurement is `EXPLAIN` on
one hot read with and without the `SET`, and it should be run before any sweep.

### The inlining conditions in full (both forms)

| condition | scalar | set-returning |
|---|---|---|
| `LANGUAGE sql` | required | required |
| not `SECURITY DEFINER` | required | required |
| **no `SET` clause** | **required** | **required** |
| not `RETURNS SETOF`/`RECORD` | required | n/a |
| declared `STABLE` or `IMMUTABLE` | volatility must comply | **required** |
| not `STRICT` | in practice fatal | **required** |
| single simple `SELECT`, one column | required | single `SELECT` |
| no aggregates/windows/subqueries/CTE/`FROM`/`GROUP BY`/`ORDER BY`/`LIMIT` | required | **CTEs and complex bodies ARE allowed** |
| return type matches declaration | required | required |
| volatile args not referenced twice | required | no volatile args |

**The asymmetry is the useful part:** a `RETURNS TABLE` function may contain
CTEs and still inline, where a scalar may not. So an inlinable *parameterized
table function* is achievable — which is exactly the shape most of this
substrate's reads have. `STRICT` and `SET` are what forbid it today.

## 3. Finding #2 — 143 functions cannot run in a parallel worker

87 SQL + 56 C functions are `PARALLEL UNSAFE`. Per the docs:

> *Parallel unsafe operations cannot be performed while parallel query is in use
> at all.*

Not "run serially" — **the whole plan loses parallelism.** One unsafe function
in a predicate poisons the query. For a 92M-attestation substrate on a 12-core
box, that is the difference between 1 core and 12.

`PARALLEL RESTRICTED` is the correct marking for functions touching temp tables,
cursors, prepared statements, or backend-local state. `resolve_topic` already
does this deliberately and documents why (`prompt_coherence` is C without a
parallel marking, so its caller cannot claim more than its callee).

**The likely truth for most of the 143: nobody chose.** The default for
unmarked functions is UNSAFE, and the template does not set it. So a read that
could parallelize is serialized by omission, not by analysis.

**Caveat that must be respected:** the docs warn that a mislabeled C function
*"could in theory exhibit totally undefined behavior."* This is not a marking to
sweep blindly. Every C function that calls SPI must be audited before being
marked safe — SPI in a worker is the classic hazard.

## 4. When C actually wins — and when it does not

The offload rule in `CLAUDE.md` is right about *shape*, not language:

> *Per-row set-returning functions, string operations, and both-directions `OR`
> joins belong in C, not in a rewritten CTE.*

### C wins decisively

- **Per-row SRF as a table source.** `prompt_coherence`'s own header records the
  measurement: the SQL form ran **>280s**; rewritten as two indexed joins behind
  a `MATERIALIZED` fence it still measured **82s**; native it is **1.4–3.9s**.
  That is 70–200×, and it came from replacing an O(n²) join with an indexed scan
  plus an O(1) hash probe per edge — an **algorithmic** change C made expressible.
- **Both-direction reads.** An `OR` over `subject_id`/`object_id` is unservable
  by any consensus index. Two indexed range reads plus a hash union is the fix,
  and it is natural in C and awkward in SQL.
- **Tight numeric loops.** PL/pgSQL interprets; C compiles. Historical benchmarks
  put the gap at an order of magnitude for arithmetic-heavy work, and PL/pgSQL is
  best understood as *glue between SQL statements*, not a computation language.
- **Aggregation over a scan.** Hash-aggregating in C while streaming a cursor
  avoids materializing intermediate sets — `prompt_language.c` is this shape.

### C loses, or is neutral

- **When the work is a single set operation.** SQL's planner beats hand-rolled C
  at joins and sorts. A C function that opens a cursor and re-implements a join
  is slower *and* unmaintainable.
- **When the SQL was only slow because it could not inline.** This is the trap
  the census exposes: port a `SET`-marked SQL function to C, measure a win, and
  attribute it to C — when removing the `SET` would have delivered much of it
  for one line.
- **Scalar helpers.** `eff_mu` as `rating - 2*rd` inlines to nothing. In C it
  becomes an opaque function call the planner cannot fold into an index
  expression. **`eff_mu` in C would be a regression.**
- **When SPI is the bottleneck.** A C function that issues one SPI call per row
  has all of PL/pgSQL's cost plus a build dependency.

## 5. SPI: the patterns that matter

### 5.1 Prepare once, keep across calls

`SPI_prepare` performs parse analysis once and returns a reusable statement;
`SPI_execute_plan` runs it. Critically, **`SPI_keepplan` saves the statement so
it survives `SPI_finish` and the transaction**, making it reusable across
invocations *in the same session*.

Plan-type behavior is automatic and worth knowing: the first few executions
build **custom** plans specific to the parameter values; after enough uses it
builds a **generic** plan and adopts it if not much more expensive.
`SPI_prepare_cursor` with `CURSOR_OPT_GENERIC_PLAN` / `CURSOR_OPT_CUSTOM_PLAN`
forces the choice.

**Applies here:** `realize_batch.c` already uses `static SPIPlanPtr` +
`ensure_plan`. `prompt_coherence.c`, `prompt_language.c` and
`trajectory_generate.c` open cursors with `SPI_cursor_open_with_args`, which
re-parses every call. For functions invoked per turn, that is repeated parse
analysis nobody is paying attention to.

### 5.2 `read_only = true` is free performance

> *If `read_only` is true, execution overhead is somewhat reduced… somewhat
> faster than read/write mode due to eliminating per-command overhead.*

Every read-path SPI call in this tree should pass `true`, and most already do.
Worth a gate rather than a habit.

### 5.3 Cursor batching, not row-at-a-time

`SPI_cursor_fetch(portal, true, N)` with a large N amortizes per-call overhead.
The tree uses 50,000 for edge scans and 4,096 for candidates — sound. The
anti-pattern is `SPI_execute` per row, which is RBAR with extra steps.

### 5.4 The deeper option — skip SPI entirely

For pure scans, the table and index access methods (`table_beginscan`,
`index_beginscan`, `TableAmRoutine`) let a C extension read heap and index
directly, below the executor. That removes parse, plan, and executor overhead
completely.

**Recommendation: do not go there yet.** It forfeits the planner, partition
pruning and parallelism; it is version-fragile across major releases; and it is
a large correctness surface. It is the right tool for a custom access method,
not for a read that a well-marked SQL function could serve. Note it as available
and treat SPI-with-kept-plans as the working ceiling.

## 6. Set-returning functions: which mode

- **ValuePerCall** (`SRF_RETURN_NEXT`): one row per call, state between calls.
  Streams — the consumer can stop early and nothing is materialized. Better for
  large or unbounded results, and for `LIMIT`-terminated reads.
- **Materialize** (`InitMaterializedSRF` + `tuplestore_putvalues`): build the
  whole result, one call. Simpler, no inter-call state, and better when the
  result is re-read or the source would otherwise be rescanned.

Every native SRF in this tree is Materialize. That is **correct for
`prompt_coherence`** (small bounded result, consumed whole) and **worth
questioning for any read whose caller applies a `LIMIT`** — Materialize computes
every row before the limit discards them.

## 7. Memory contexts — the rules that prevent leaks

- `palloc`/`pfree`, never `malloc`/`free`. Context teardown frees everything;
  destroying a context releases all of it without tracking individual objects.
- **The per-call context of an SRF is cleared between calls.** Anything that must
  survive belongs in `multi_call_memory_ctx`, allocated during first-call setup,
  and is freed automatically at query end.
- The tree's pattern — one `AllocSetContextCreate` work context, switch into it
  for anything long-lived, single `MemoryContextDelete` at exit — is the correct
  shape and should be the template for every new native surface.

## 8. A decision procedure

Before porting anything to C, in order:

1. **Does it carry a `SET` clause?** Remove it (schema-qualify instead) and
   re-`EXPLAIN`. If that fixes it, stop — you were fighting the marking.
2. **Is it `STRICT` without needing to be?** Same test.
3. **Is it PARALLEL UNSAFE by omission?** Audit and mark. 12 cores versus 1.
4. **Is the shape wrong?** Per-row SRF as a table source, both-direction `OR`,
   `realize()` inside a row-producing `SELECT`, correlated per-candidate
   aggregate — these are algorithmic defects. Fix the shape; C may or may not be
   needed once the shape is right.
5. **Only then C**, and only for: hash aggregation over a scan, both-direction
   indexed reads with an O(1) probe, tight numeric loops, or native math already
   living in `engine/core`.
6. **Never C for a scalar that would otherwise inline.**

**And measure the port against what it replaced.** `prompt_language.c` disagreed
with its SQL predecessor — English 12,260 vs 8,553 — and the difference was a
real double-count the SQL form inherited from the tier-collision fan-out. That
disagreement is only visible if parity is checked, which is what spec 37's G6
gate exists to do.

## 9. Ordered work this implies

1. **Measure the `SET`-clause cost.** One hot read, `EXPLAIN` with and without.
   Nothing else here should be scheduled until this number exists.
2. **If it is large: qualify the bodies, then drop the `SET`.** 190 bodies
   reference substrate objects bare and depend on the `SET` to resolve; they
   must be qualified FIRST or they break. Mechanical and greppable, but real
   work — not a template flip. Gate with regress, and add a policy check that
   fails any new body containing an unqualified substrate reference, or the
   next 190 arrive the same way.
3. **Audit parallel markings.** Mechanical for SQL; case-by-case for the 56 C
   functions, none of which may be marked safe while calling SPI unaudited.
4. **`SPI_keepplan` on repeat-invoked native functions** — `prompt_coherence`,
   `prompt_language`, `trajectory_generate`.
5. **Revisit Materialize vs ValuePerCall** for SRFs whose callers apply a LIMIT.
6. **Then** consider further C ports, by §8's procedure rather than by instinct.

## 10. The honest summary

The offload rule was right and incomplete. Shape matters — a per-row SRF as a
table source is a defect no language fixes. But **this substrate's SQL is not
slow because it is SQL. It is slow because 60% of it is marked
non-inlinable and 24% is marked non-parallelizable, both by template default
rather than by decision.** Porting those to C would deliver a real win and
attribute it to the wrong cause, leaving the template to keep producing the same
defect for every function written after.

Fix the markings first. Then port what still deserves it.

---

## Sources

- [SPI — Server Programming Interface](https://www.postgresql.org/docs/current/spi.html)
- [SPI_prepare](https://www.postgresql.org/docs/current/spi-spi-prepare.html)
- [SPI_keepplan](https://www.postgresql.org/docs/current/spi-spi-keepplan.html)
- [SPI_execute (read_only overhead)](https://www.postgresql.org/docs/current/spi-spi-execute.html)
- [SPI Memory Management](https://www.postgresql.org/docs/current/spi-memory.html)
- [Inlining of SQL functions (PostgreSQL wiki)](https://wiki.postgresql.org/wiki/Inlining_of_SQL_functions)
- [Query Language (SQL) Functions](https://www.postgresql.org/docs/current/xfunc-sql.html)
- [STRICT on SQL Function Breaks In-lining Gotcha](https://www.postgresonline.com/journal/archives/163-STRICT-on-SQL-Function-Breaks-In-lining-Gotcha.html)
- [Parallel Safety](https://www.postgresql.org/docs/current/parallel-safety.html)
- [C-Language Functions](https://www.postgresql.org/docs/current/xfunc-c.html)
- [Memory Contexts in PostgreSQL — Nidzwetzki](https://jnidzwetzki.github.io/2022/05/28/postgres-memory-context.html)
- [Memory context: how PostgreSQL allocates memory — CYBERTEC](https://www.cybertec-postgresql.com/en/memory-context-for-postgresql-memory-management/)
- [Index Access Method Functions](https://www.postgresql.org/docs/devel/index-functions.html)
- [Writing a Table Access Method — Eaton Phil](https://notes.eatonphil.com/2023-11-01-postgres-table-access-methods.html)
- [SRF ValuePerCall vs Materialize benchmark — EvanCarroll](https://github.com/EvanCarroll/pg-srf-repeat-benchmark)
- [Materialization in PostgreSQL 9.0 — Robert Haas](http://rhaas.blogspot.com/2010/04/materialization-in-postgresql-90.html)
