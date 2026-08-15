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

## Who calls what: the six populations

Measured 2026-08-15 by collecting every `schema.function` mention across `app/`, `scripts/`,
`web/`, the C extension sources and the extension tests, and joining that against the 381
installed functions and the SQL call graph.

| class | functions |
|---|---|
| public — called by the app / MCP | 180 |
| internal — called by another SQL function | 73 |
| internal — called from the C extension | 56 |
| **no caller found in any population** | **45** |
| ops / scripts only | 13 |
| test only | 6 |

"Unreachable from a SQL entry point" is **not** the same as unused: 88 of 373 are reachable
from the ten real entry points, and most of the remaining 285 are the entry point for something
else — the MCP runtime calls `consensus.by_ids` and `chess.moves` directly, visible in
`pg_stat_activity`. Only the 45 below have no caller in SQL, C, the app, scripts or tests.

## The 45 with no caller anywhere

These are implemented and never wired — whole families, not stragglers. Isolate and account
for them; do not delete them without knowing why each was built.

- **circuit family** — `generation.circuit_matrix`, `circuit_row`, `circuit_coupling`,
  `adjudicated_row`, `adjudicated_coupling`
- **model lane** — `generation.model_attention_row`, `model_pair_cos`, `distill`, `decay`,
  `prune`, `witness`
- **generation** — `astar_path`, `continue_text`, `variant_walk`, `respell_variant`,
  `pos_transition_plane`, `corpus_trajectory_probe`
- **structural / geometry** — `nearest_neighbors_4d`, `trajectory_prefix_distance`,
  `word_shape_distance`, `trajectory_point_count`, `geometry_predecessors`, `geometry_audit`,
  `entity_physicality_coord`, `consensus_export_relations`, `consensus_export_relations_mu`,
  `consensus_export_unary`
- **ops diagnostics** — `index_health`, `index_usage_detail`, `index_usage_report`,
  `ingest_integrity_gate`, `placement_health`, `placement_health_by_tier`,
  `orphan_physicality_count`, `ducet_ordered`, `metric_ladder_words`
- **chess** — `distance_to_syzygy`, `missed_finish`, `opening_endgames`, `opening_preference`,
  `opening_record`, `opening_shape_peers`
- **other** — `consensus.related_objects`, `converse.recall_interaction_response`,
  `lexical.word_shape_peers_fast`

`generation.pos_transition_plane` is on this list and was repaired earlier the same day — a
function can be broken, fixed, and still have no caller.

## Orchestrate versus compute

§15: native libraries hold the math, the extension is a thin versioned surface, the app
orchestrates and never inlines math a kernel owns. Where that stands:

| language | functions | recursive | window fns | generate_series | LATERAL | body > 2 KB |
|---|---|---|---|---|---|---|
| sql | 284 | 2 | 41 | 3 | 57 | 36 |
| c | 57 | — | — | — | — | — |
| plpgsql | 40 | 0 | 9 | 2 | 8 | 14 |

57 kernels against 324 orchestrators, of which 50 run window functions, 65 use LATERAL and 50
carry bodies over 2 KB. Ranked by compute sitting in the orchestration layer (recursive 3,
window 2, generate_series 2, LATERAL 1, size up to 2):

| function | body | score |
|---|---|---|
| `converse.facts` | 9,381 B | 6 |
| `generation.grapheme_order` | 2,069 B | 6 |
| `converse.chat` | **27,077 B** | 5 |
| `generation.walk_batch` | 16,028 B | 5 |
| `generation.compose_batch` | 15,098 B | 5 |
| `ops.evidence_receipt` | 8,269 B | 5 |
| `generation.foundry_vocab_crawl` | 6,184 B | 5 |
| `converse.infer` | 5,761 B | 5 |
| `consensus.salient_facts` | 4,777 B | 5 |
| `consensus.relate_path` | 4,416 B | 5 |

`converse.chat` is the tier-8 entry point and 27 KB of plpgsql — the furthest thing from a thin
versioned surface in the tree.
