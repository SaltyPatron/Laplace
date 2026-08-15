# The SQL surface as a cascade

Order of operations for verifying the installed SQL surface: a function may only be
trusted once everything it calls is trusted. Tier 0 first, then 1, then 2. Measured
2026-08-15 against the live substrate.

## The cascade

381 functions across 9 schemas (`consensus` 73, `converse` 72, `generation` 68, `ops` 62,
`realize` 28, `structural` 28, `chess` 25, `lexical` 17, `taxonomy` 8). 573 call edges.
**No cycles** — every function has a finite depth, so bottom-up verification terminates.

| tier | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|---|---|---|---|---|---|---|
| functions | 130 | 82 | 51 | 50 | 15 | 25 | 14 | 5 | 1 |

Tier 8 is a single function: **`converse.chat`**. Tier 7 is `converse.converse` and the
four `converse.recall_*_response` surfaces. Tier 6 includes `converse.attention`,
`converse.compose`, `generation.probe`, `consensus.relation_summary`, `ops.entity_record`.

## Regenerating it

**Strip comments first.** Matching function names against raw `pg_get_functiondef` counts
prose mentions as calls: it reports 624 edges and six mutually-recursive pairs
(`converse.chat` ↔ `converse.converse`, `lexical.senses` ↔ `taxonomy.bubble_up`, …). All
six are comment artifacts. Stripped, there are 573 edges and zero cycles.

```sql
CREATE TEMP TABLE fns AS
SELECT n.nspname||'.'||p.proname AS fq,
       regexp_replace(regexp_replace(pg_get_functiondef(p.oid),'--[^\n]*',' ','g'),
                      '/\*.*?\*/',' ','gs') AS def
FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname IN ('consensus','converse','generation','structural','taxonomy',
                    'lexical','realize','ops','chess') AND p.prokind='f';

CREATE TEMP TABLE edges AS
SELECT DISTINCT f.fq AS caller, g.fq AS callee
FROM fns f JOIN fns g ON g.fq <> f.fq AND position(g.fq||'(' in f.def) > 0;

WITH RECURSIVE walk(root,node,depth) AS (
  SELECT f.fq,f.fq,0 FROM (SELECT DISTINCT fq FROM fns) f
  UNION ALL SELECT w.root,e.callee,w.depth+1 FROM walk w JOIN edges e ON e.caller=w.node
  WHERE w.depth<20)
SELECT max(depth) AS tier, root FROM walk GROUP BY root ORDER BY 1,2;
```

Note `{0,400}` and larger repetition counts are rejected by the Postgres regex engine
(limit 255).

## Analyse before running

`EXPLAIN` without `ANALYZE` plans without executing. Bodies can also be scanned statically
for signatures whose cost is already measured, which ranks the surface without touching it.

Measured costs of the shared primitives, live:

| primitive | cost | note |
|---|---|---|
| `v_word_points` ⋈ `laplace_trajectory_constituents` | 57.3 s mean over 33 calls | highest total cost on the box (1,890 s) |
| `generation.separator_ids()` | 9.5 s, twice, 85 ids | its own comment claims 1.2 s — measured on the foundation substrate, stale here |
| `structural.geometry_neighbours(id,200,8)` | 43.5 s | |
| `structural.entity_container_degree` | 171 s at `p_cap` 65536 | sub-second at 8192, where it stops discriminating |
| `converse.infer` | 3.1 s | was 54.0 s before `cd11acdf` |

Functions carrying two or more of those signatures, highest first:
`generation.relation_plane`, `generation.separator_ids`, `generation.trajectory_continuations`,
`generation.word_adjacency` (all four: trajectory unpack **and** separator_ids), then
`structural.entity_container_degree`, `converse.election_token_profile`,
`generation.foundry_vocab`, `generation.grapheme_order`, `generation.sentence_order`,
`realize.constituents`, `taxonomy.bubble_up`.

`separator_ids` is the shared tax: a tier-0 leaf paid by the export plane, the forward-pass
proposer and the adjacency lane alike.

## Case study: separator_ids, three rewrites measured and rejected

The function returns 85 ids: 84 separator atoms plus grapheme clusters composed entirely of
them. Arms measured separately — atoms **28 ms**, clusters **4,372 ms** scanning all 57,736
tier-1 clusters to find 2.

| rewrite | result | verdict |
|---|---|---|
| GIN `<@` containment on `laplace_trajectory_constituent_ids` | 107.8 s | rejected — GIN serves `@>`/`&&`, not contained-by |
| GIN `&&` overlap then verify | 103.0 s, shortlists 32,410,282 | rejected |
| `physicalities_traj_first_id_btree` on the first constituent | 4.04 s, shortlists 599,753 | rejected — leading separators are ubiquitous, no gain over 4.37 s |

The set is alphabet-bounded and immutable in practice, and the function's own comment calls
it "the same class of lookup as `laplace.relation_type_id()`" — which is served from a
compiled perfcache. Compiling it is the remaining option; scanning for it is not.

## Tier-0 sweep harness

Zero-argument leaves can be exercised directly. Two things are mandatory.

**Force the value.** `SELECT count(*) FROM (SELECT fn()) q` does **not** evaluate `fn()` — the
count needs no column, so the planner elides the call. A first sweep using that form reported
`generation.separator_ids` at **0.2 ms** when it actually takes **10–26 s**. Scalar returns
must be consumed (cast to text and tested `IS NOT NULL`); set returns must be counted from the
FROM clause.

**Cap each call.** Without `SET LOCAL statement_timeout` inside the loop the sweep dies on the
first slow leaf.

```sql
DO $$ DECLARE r record; t0 timestamptz; n bigint; BEGIN
  FOR r IN SELECT fq, proretset FROM leaf0 ORDER BY fq LOOP
    BEGIN SET LOCAL statement_timeout='30s'; t0:=clock_timestamp();
      IF r.proretset THEN
        EXECUTE format('SELECT count(*) FROM %s() q', r.fq) INTO n;
      ELSE
        EXECUTE format('SELECT count(*) FROM (SELECT (%s())::text AS v) q WHERE q.v IS NOT NULL',
                       r.fq) INTO n;
      END IF;
      INSERT INTO tm VALUES (r.fq, EXTRACT(epoch FROM clock_timestamp()-t0)*1000, NULL);
    EXCEPTION WHEN OTHERS THEN
      INSERT INTO tm VALUES (r.fq, EXTRACT(epoch FROM clock_timestamp()-t0)*1000, SQLERRM);
    END;
  END LOOP; END $$;
```

A killed `psql` does not kill its backend. An orphaned harness holding catalog locks blocks
`ALTER EXTENSION` with `canceling statement due to lock timeout`; find it in
`pg_stat_activity` and `pg_cancel_backend` it before diagnosing anything else.

22 of the 130 tier-0 leaves take no arguments. Measured, with the forcing form:
`ops.consensus_tier_distribution` **549,925 ms**, `consensus.stats` **134,740 ms**,
`generation.separator_ids` 10–26 s, `ops.app_log` 201 ms, `generation.corpus_trajectory_probe`
41 ms, the rest ≤ 3 ms. The remaining 108 leaves need a fixture argument set per signature,
which does not exist yet.
