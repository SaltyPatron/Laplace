# W8 — Port `infer()` to C: both directions, n-hop bias, multi-step

**Issue:** #757 · **Plan:** `COMPLETION_PLAN.md` R5 / Phases 5–6 ·
**Related:** W7 (rel_type wiring), W9 (prior turns as a bias head)

---

## 1. Why this exists

`infer()` shipped as SQL on 2026-08-02 and its own header states the limits:
forward direction only, one-hop bias family, single-step emission, and a per-row
set-returning call over bias tokens. It works — `the capital of California is` →
`sacramento`, rank 1 — and it is a prototype of itself.

The performance argument is the one `prompt_coherence.c:5-16` already made and
won: the `scored` CTE (`infer.sql.in:64-72`) is a `LEFT JOIN … LEFT JOIN …
GROUP BY` costing O(candidates × edges × biasfam). In C that is one indexed
range read per direction plus an **O(1) hash probe per edge**.

## 2. What exists today

`sql/functions/converse/infer.sql.in`, `LANGUAGE sql STABLE`, registered at
`manifest.install:399` / `manifest.upgrade:400`.

| CTE | lines | What |
|---|---|---|
| `ranked` | 29-38 | `prompt_coherence` under chat's verbatim key order |
| `topic` | 39 | `rk = 1` |
| `bias` | 48-53 | every sense of every non-topic token, deliberately uncollapsed |
| `biasfam` | 54-58 | bias ∪ one-hop **in**-edges |
| `cand` | 59-63 | `consensus WHERE subject_id = topic` — **forward only** |
| `scored` | 64-72 | per candidate, count of edges into `biasfam`; `ORDER BY hits DESC, m DESC, id` |
| `ord`/`lbl` | 73-80 | one `realize_batch` at the end |

`rel_type_id` is returned by the election and **never selected** (`:30`).

## 3. The port

**New:** `extension/laplace_substrate/src/infer.c`, entry
`pg_laplace_infer`. Target signature:
`infer(p_prompt text, p_limit int DEFAULT 8, p_hops int DEFAULT 1, p_steps int DEFAULT 1)`.

### Stage 1 — election stays SQL

Call `prompt_coherence` via cursor with the exact key order. **Do not
reimplement it in C.** The election is `prompt_coherence`'s contract, and
duplicating its ordering is precisely the drift R8 describes. The
`ORDER BY y.synset_id` bytea tiebreak must also stay in SQL — reproducing
PostgreSQL's `byteacmp` in C is a silent-divergence trap.

### Stage 2 — n-hop bias family

`HTAB *fam_h { uint8 key[16]; int16 hop; }`. Seed from the bias tokens' senses;
expand `hop = 1..p_hops`, one indexed read per direction over the **previous
hop's frontier only**, inserting with `HASH_ENTER` and skipping present keys —
that is what makes n-hop terminate.

**The trap that would make this port worse than the SQL:** `hits` is a raw
`count(*)` today. At n hops, an unweighted count lets a 3-hop path outvote a
1-hop path. Store `hop` and credit `1/hop` (or `rank × eff / hop`). *This is the
single most likely way the port regresses.*

### Stage 3 — both-direction candidate scan

Model on `pc_scan_edges` (`prompt_coherence.c:146-242`) exactly: two cursors,
never an `OR` predicate — the OR-split rationale is at `:142-145` and is the
difference between an index range read and the 280 s hang.

**Directionality is a re-baselining event.** Today's measured result rests on a
forward edge. Adding the reverse doubles the candidate set and changes every
answer. Gate it behind a parameter, defaulted off, so one bad axis is bisectable.
Apply `prompt_coherence`'s rule for double-counting: credit `total_mass`-style
aggregates on the **forward pass only** (`:217-221`).

### Stage 4 — `rel_type_id` typed scan (the W7 wiring)

If the election returned a non-NULL `rel_type_id`, replace Stage 3's cursors
with a typed pair (`subject_id = ANY($1) AND type_id = ANY($2)`), where `$2` is
the elected type **plus its family members** — expanded in C for free via
`laplace_relation_in_family` (`relation_law.h:44`), no SQL.

**Fallback rule:** an elected relation with zero edges must fall through to the
untyped distribution, **never** return empty. A typed read that abstains looks
identical to "the substrate knows nothing," which is the exact conflation the
read laws forbid.

### Stage 5 — multi-step with WITNESS between steps

Loop: rank the candidate hash, emit top-1, deposit, advance the frontier, clear,
repeat. Accumulate deposits and flush **once** through `consensus_upsert`
(the batched spine, as `witness_precedes_chain.sql.in:47-55` does) rather than
per-step `laplace_witness` calls.

**`STABLE` + write is illegal.** Keep `infer()` `STABLE` and read-only, and add
a separate `infer_witnessed()` VOLATILE wrapper. Making `infer()` VOLATILE costs
every existing caller the planner optimization and causes re-execution per row
inside a `SELECT` list.

### Stage 6 — emission

`InitMaterializedSRF`, one `spi_realize_batch` over the ranked survivors,
position-aligned, `tuplestore_putvalues` per row with `step` added.

### What stays SQL vs moves to C

| Stays SQL (do not port) | Why |
|---|---|
| `prompt_coherence(prompt)` + ORDER BY | the election contract |
| `senses()` / `bubble_up` | a ~150-line ranked CTE with family members and geometry neighbours — its own workstream |
| `realize_batch` | already C behind a binding; call it |
| `consensus_upsert` | the fold spine — never reimplement a fold |

| Moves to C | Replaces |
|---|---|
| `eff_mu` → `rating - 2*rd` inline | a SQL call per row |
| `biasfam` UNION → `fam_h` hash, n-hop | a CTE that cannot express hops |
| `scored`'s double LEFT JOIN + GROUP BY | **the whole performance argument** — O(1) probe per edge |
| `row_number() OVER` | `qsort` with an explicit comparator |
| relation family membership | `laplace_relation_in_family` |

## 4. Registration

- **CMake:** add `src/infer.c` to the single `EXT_C_SOURCES` list
  (`CMakeLists.txt:49-76`). The comment at `:46-48` explains why the list is not
  per-platform: a missing file becomes a runtime `undefined symbol`.
- **Version hash:** `CMakeLists.txt:30-43` SHA256s the manifests and `.sql.in`
  inputs to derive `EXT_VERSION` — so the SQL binding change and the C change
  **must ship together**, or the installed version disagrees with the loaded
  `.so`.
- **SQL binding:** rewrite `infer.sql.in` in the `prompt_coherence.sql.in`
  shape. The `DROP FUNCTION IF EXISTS infer(text, int);` is **mandatory** —
  adding defaulted parameters to an existing signature otherwise creates an
  ambiguous overload.
- **Manifests:** already present; no edit needed unless the VOLATILE wrapper
  gets its own file, in which case add it immediately after (order matters).

## 5. Where to look

| Concern | Citation |
|---|---|
| The SQL being ported | `sql/functions/converse/infer.sql.in` |
| Structural template (all patterns) | `src/prompt_coherence.c` — SRF init `:263`; nested SPI `:265-266,753`; work context `:268-269`; hash tables `:271-287`; cursor + chunked fetch `:160-241`; `bytea[]` params `:385,468`; 16-byte guards `:182-189`; both-direction scan `:146-242`; native math `:211-212`; emission `:747` |
| Prepared plans across calls | `src/realize_batch.c:52-58,118+` |
| Batched render from C | `src/spi_common.h` (`spi_realize_batch`) |
| Nested-SPI helper | `src/spi_nested.h:7-30` |
| Batched fold entry | `sql/functions/.../witness_precedes_chain.sql.in:47-55` |
| Family expansion | `engine/core/include/laplace/core/relation_law.h:33,44` |
| Closest test shape | `extension/laplace_substrate/tests/sql/walk_richer_forward_pass.sql` |

## 6. Acceptance

1. **Row-set parity** at `p_hops=1, p_steps=1`, forward-only: identical ordered
   `(prediction, hits)` to the SQL version on the same snapshot, including the
   header's measured case.
2. **Measured latency win** on a high-degree topic — the densest subject in the
   substrate carries 6,687 edges. Without a measurement this port has no
   justification.
3. `rel_type_id` non-NULL runs the typed scan (verifiable by a strictly smaller
   candidate count than the untyped scan for the same topic).
4. `p_steps > 1` yields `step` values 1..n and exactly n new/updated consensus
   cells attributable to the inference source.
5. No `SPI_processed` handled without `SPI_freetuptable`; no `palloc` outside the
   work context that must survive `MemoryContextDelete`.

## 7. Risks

- **`senses()` per bias token in C is still `senses()` per bias token.** The
  port does not fix that (`infer.sql.in:46-47` says so). If bias tokens are
  many, `bubble_up` dominates and the C port shows **no win**. Measure before
  promising one.
- **Nested SPI inside a cursor loop.** `prompt_coherence` never opens a cursor
  while another is fetching. The multi-step loop must fully drain and close each
  cursor before depositing.
- **Two axes that each change every answer** (both-direction, n-hop). Ship them
  as separately-defaulted-off parameters, or a regression becomes unbisectable.
- **`STABLE` + write**, covered above — the failure mode appears only under
  certain plans, which makes it a late and confusing discovery.
