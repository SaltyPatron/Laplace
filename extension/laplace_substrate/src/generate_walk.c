#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/numeric.h"
#include "utils/hsearch.h"
#include "lib/stringinfo.h"
#include "mb/pg_wchar.h"

#include "laplace/core/relation_law.h"
#include "laplace/core/glicko2.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_branches);
PG_FUNCTION_INFO_V1(pg_laplace_walk_strongest);

#define GENERATE_NODE_BUDGET 1000000

static const char *EDGE_QUERY =
    "SELECT object_id, type_id, rating, rd, witness_count "
    "FROM laplace.consensus_walk_edges($1, $2, $3, $4)";

static SPIPlanPtr edge_plan = NULL;

static void
ensure_edge_plan(void)
{
    if (edge_plan == NULL)
    {
        Oid argtypes[4] = { BYTEAOID, BYTEAOID, INT4OID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare(EDGE_QUERY, 4, argtypes);
        if (plan == NULL)
            elog(ERROR, "generate walk: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "generate walk: SPI_keepplan failed");
        edge_plan = plan;
    }
}

/*
 * walk_branches' batched edge fetch -- the native-C-does-the-heavy-lifting
 * replacement for calling consensus_walk_edges() once per frontier node.
 * consensus_walk_edges() did real per-call work in SQL: fetch + compute an
 * unindexed `relation_rank_resolved(type_id) * eff_mu(rating,rd)` sort key
 * per candidate row + ORDER BY + LIMIT, AND an O(path length) `= ANY(exclude)`
 * scan per row against a path array that grows every level. Multiplied by an
 * unbounded, non-deduplicated frontier (see below), a depth=6/breadth=12 walk
 * on "chess" measured 4+ minutes with no sign of finishing.
 *
 * This fetches raw, unranked, unfiltered candidate edges for the ENTIRE
 * current level's frontier in ONE query (unnest(...) WITH ORDINALITY, same
 * batching idiom as recall.c's word_shape_peers_fast), then does ranking,
 * refutation/relation-type filtering, and beam selection entirely in C via
 * qsort -- SQL never sorts or filters here, it only hands over rows.
 */
/*
 * NOTE: a per-subject CROSS JOIN LATERAL + LIMIT bound (using
 * consensus_subject_eff_mu_btree) was tried here and reverted -- EXPLAIN
 * ANALYZE confirmed it correctly used the index (0.98ms for a single-subject
 * probe), but it did not improve full-walk wall time (45.6s/172,196 nodes vs
 * a 45.8s/155,157-node baseline -- no real change) and introduced a real
 * behavior wrinkle: the LATERAL LIMIT pre-filters by raw (rating-2*rd), but
 * the final beam selection below ranks by relation_rank*eff_mu -- a
 * different key -- so for high-fan-out subjects it could silently exclude a
 * true top-beam candidate that didn't make the raw-eff_mu top-N cut. This
 * batch query only executes once per LEVEL (max_depth times total), not
 * once per subject, so per-subject row volume was never the dominant cost
 * at scale -- the real cost is the sheer number of distinct subjects in a
 * wide frontier (grows with beam^level), which a per-subject LIMIT doesn't
 * touch. Left unbounded per subject; see .scratchpad/02_Identified_Issues.txt
 * Issue 28 update for the measurement.
 */
static const char *WALK_BATCH_QUERY =
    "SELECT f.idx, c.object_id, eo.type_id, c.type_id, c.rating, c.rd, c.witness_count "
    "FROM unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "JOIN laplace.consensus c ON c.subject_id = f.subject_id "
    "JOIN laplace.entities eo ON eo.id = c.object_id "
    "WHERE c.object_id IS NOT NULL "
    "  AND ($2::bytea IS NULL OR c.type_id = $2)";

static SPIPlanPtr walk_batch_plan = NULL;

static void
ensure_walk_batch_plan(void)
{
    if (walk_batch_plan == NULL)
    {
        Oid argtypes[2] = { BYTEAARRAYOID, BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(WALK_BATCH_QUERY, 2, argtypes);
        if (plan == NULL)
            elog(ERROR, "walk_branches: SPI_prepare(batch) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_branches: SPI_keepplan(batch) failed");
        walk_batch_plan = plan;
    }
}

static hash128_t g_relationtype_type_id;
static bool      g_relationtype_type_id_ready = false;

static void
ensure_relationtype_type_id(void)
{
    int  rc;
    bool isnull;
    Datum d;

    if (g_relationtype_type_id_ready)
        return;

    rc = SPI_execute("SELECT laplace.entity_type_id('RelationType')", true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        elog(ERROR, "walk_branches: could not resolve entity_type_id('RelationType')");
    d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &isnull);
    if (isnull)
        elog(ERROR, "walk_branches: entity_type_id('RelationType') returned NULL");
    g_relationtype_type_id = datum_to_hash128(d);
    g_relationtype_type_id_ready = true;
}

/*
 * Same ranking key as consensus_walk_edges' ORDER BY (relation_rank_resolved
 * * eff_mu), computed natively instead of via a SQL function call per row.
 * eff_mu here is the SAME simple `rating - 2*rd` used by consensus_walk_edges
 * (13_mu_law.sql.in's eff_mu()) -- deliberately NOT laplace_effective_mu_fp
 * (a different, display-oriented formula used elsewhere in this file).
 * relation_rank_resolved's fast path (the static relation table lookup,
 * which covers the ~153 canonical types plus family-collapsed dynamic
 * DEP_ and FEAT_ types) is called directly, zero SQL/FunctionCall overhead.
 * Its rare SPI-fallback path (a genuinely unknown type_id, not expected in
 * practice) is deliberately not replicated here -- such a candidate scores
 * the lowest possible rank so it never wins a beam slot over a resolvable
 * candidate, rather than duplicating an 8-hop SPI parent-chain walk per row.
 */
static double
walk_relation_rank(hash128_t type_id)
{
    const laplace_relation_def_t *def = NULL;

    if (laplace_relation_lookup(&type_id, &def) == 0 && def != NULL)
        return def->rank;
    return 0.0;
}

typedef struct RawEdge
{
    int32  idx;
    Datum  object;
    Datum  object_type;
    Datum  rel_type;
    int64  rating;
    int64  rd;
    int64  witnesses;
} RawEdge;

static int
raw_edge_cmp_idx(const void *a, const void *b)
{
    int32 ia = ((const RawEdge *) a)->idx;
    int32 ib = ((const RawEdge *) b)->idx;
    return (ia > ib) - (ia < ib);
}

typedef struct RankedEdge
{
    Datum  object;
    Datum  rel_type;
    int64  rating;
    int64  rd;
    int64  witnesses;
    double score;
} RankedEdge;

static int
ranked_edge_cmp_score_desc(const void *a, const void *b)
{
    double sa = ((const RankedEdge *) a)->score;
    double sb = ((const RankedEdge *) b)->score;
    return (sa < sb) - (sa > sb);
}



typedef struct WalkNode
{
    int     parent;
    int     depth;
    Datum   entity;
    Datum   rel_type;
    Datum   eff_mu;
    int64   path_mu_fp;     /* sum of display-rounded eff_mu, int64 fp */
    int64   witnesses;
} WalkNode;

/* Final ordering key: depth ascending, path_mu descending, creation order
 * ascending — the same total order the previous per-comparison numeric_cmp
 * insertion sort produced (insertion sort was stable, so creation index is
 * the exact tie-break), at O(n log n) native compares instead of O(n²)
 * numeric function calls. */
typedef struct WalkOrderKey
{
    int     depth;
    int64   path_mu_fp;
    int     idx;
} WalkOrderKey;

static int
walk_order_cmp(const void *a, const void *b)
{
    const WalkOrderKey *x = (const WalkOrderKey *) a;
    const WalkOrderKey *y = (const WalkOrderKey *) b;

    if (x->depth != y->depth)
        return (x->depth > y->depth) - (x->depth < y->depth);
    if (x->path_mu_fp != y->path_mu_fp)
        return (x->path_mu_fp < y->path_mu_fp) - (x->path_mu_fp > y->path_mu_fp);
    return (x->idx > y->idx) - (x->idx < y->idx);
}

static ArrayType *
branch_array(WalkNode *nodes, int idx, bool types)
{
    int     depth = nodes[idx].depth;
    int     n = types ? depth : depth + 1;
    Datum  *elems = (Datum *) palloc(sizeof(Datum) * (n > 0 ? n : 1));
    int     i = idx;

    for (int slot = n - 1; slot >= 0; slot--)
    {
        elems[slot] = types ? nodes[i].rel_type : nodes[i].entity;
        i = nodes[i].parent;
    }
    return construct_array(elems, n, BYTEAOID, -1, false, TYPALIGN_INT);
}

Datum
pg_laplace_walk_branches(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea  *prompt;
    Datum   type_datum = 0;
    bool    type_null;
    int32   max_depth, beam;
    WalkNode *nodes;
    int     n_nodes = 0, cap;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("walk_branches: prompt entity must not be NULL")));
    prompt    = PG_GETARG_BYTEA_PP(0);
    type_null = PG_ARGISNULL(1);
    if (!type_null)
        type_datum = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? 4 : PG_GETARG_INT32(2);
    beam      = PG_ARGISNULL(3) ? 5 : PG_GETARG_INT32(3);
    if (max_depth < 0 || beam < 0)
        ereport(ERROR, (errmsg("walk_branches: depth and beam must be >= 0")));

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "walk_branches: SPI_connect failed");
    ensure_walk_batch_plan();
    ensure_relationtype_type_id();

    cap = 256;
    nodes = (WalkNode *) palloc(sizeof(WalkNode) * cap);

    nodes[0].parent    = -1;
    nodes[0].depth     = 0;
    nodes[0].entity    = copy_bytea_datum(PointerGetDatum(prompt));
    nodes[0].rel_type      = (Datum) 0;
    nodes[0].eff_mu    = (Datum) 0;
    nodes[0].path_mu_fp = 0;
    nodes[0].witnesses = 0;
    n_nodes = 1;

    /*
     * Global dedup across the whole walk, not just per-path -- the actual
     * fix for the exponential blowup. The original per-path exclusion array
     * only stopped a node from revisiting its OWN ancestors; it did nothing
     * to stop sibling/cousin branches from independently re-discovering and
     * re-expanding the same popular hub entity, which is exactly what turns
     * a beam search into an unbounded tree. Once an entity has been placed
     * anywhere in the walk (necessarily via the best-ranked edge that could
     * reach it, since edges are processed in score order), a worse/deeper
     * rediscovery adds no information a beam search should keep.
     */
    {
        HASHCTL ctl;
        HTAB   *seen;
        bool    found;
        hash128_t root_id = datum_to_hash128(nodes[0].entity);

        memset(&ctl, 0, sizeof(ctl));
        ctl.keysize = 16;
        ctl.entrysize = 16;
        seen = hash_create("walk_branches seen", 1024, &ctl, HASH_ELEM | HASH_BLOBS);
        hash_search(seen, &root_id, HASH_ENTER, &found);

        for (int frontier_start = 0, level = 0; level < max_depth; level++)
        {
            int frontier_end = n_nodes;
            int n_frontier;
            Datum *frontier_ids;
            ArrayType *frontier_arr;
            Datum  args[2];
            char   nulls[3] = "  ";
            int    rc;
            RawEdge *raw;
            int      n_raw;

            if (frontier_start == frontier_end)
                break;

            n_frontier = frontier_end - frontier_start;
            frontier_ids = (Datum *) palloc(sizeof(Datum) * n_frontier);
            for (int f = frontier_start; f < frontier_end; f++)
                frontier_ids[f - frontier_start] = nodes[f].entity;
            frontier_arr = construct_array(frontier_ids, n_frontier, BYTEAOID, -1, false, TYPALIGN_INT);

            args[0] = PointerGetDatum(frontier_arr);
            args[1] = type_null ? (Datum) 0 : type_datum;
            if (type_null) nulls[1] = 'n';

            rc = SPI_execute_plan(walk_batch_plan, args, nulls, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_branches: batch edge query failed: %s",
                     SPI_result_code_string(rc));

            n_raw = (int) SPI_processed;
            raw = (RawEdge *) palloc(sizeof(RawEdge) * (n_raw > 0 ? n_raw : 1));
            for (int r = 0; r < n_raw; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool isnull;

                raw[r].idx         = DatumGetInt64(SPI_getbinval(tup, td, 1, &isnull));
                raw[r].object      = copy_bytea_datum(SPI_getbinval(tup, td, 2, &isnull));
                raw[r].object_type = copy_bytea_datum(SPI_getbinval(tup, td, 3, &isnull));
                raw[r].rel_type    = copy_bytea_datum(SPI_getbinval(tup, td, 4, &isnull));
                raw[r].rating      = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
                raw[r].rd          = DatumGetInt64(SPI_getbinval(tup, td, 6, &isnull));
                raw[r].witnesses   = DatumGetInt64(SPI_getbinval(tup, td, 7, &isnull));
            }
            qsort(raw, n_raw, sizeof(RawEdge), raw_edge_cmp_idx);

            for (int r = 0; r < n_raw; )
            {
                int   run_start = r;
                int   f = frontier_start + (raw[r].idx - 1);
                RankedEdge *cands;
                int          n_cands = 0;

                while (r < n_raw && raw[r].idx == raw[run_start].idx)
                    r++;

                cands = (RankedEdge *) palloc(sizeof(RankedEdge) * (r - run_start));
                for (int j = run_start; j < r; j++)
                {
                    hash128_t obj_type_id = datum_to_hash128(raw[j].object_type);
                    hash128_t rel_type_id;
                    hash128_t obj_id;
                    bool      found2;

                    if (raw[j].rating + 2 * raw[j].rd < LAPLACE_GLICKO2_NEUTRAL_MU_FP)
                        continue; /* refuted */
                    if (hash128_eq(&obj_type_id, &g_relationtype_type_id))
                        continue; /* object is itself a RelationType meta-entity */
                    obj_id = datum_to_hash128(raw[j].object);
                    hash_search(seen, &obj_id, HASH_FIND, &found2);
                    if (found2)
                        continue; /* already placed elsewhere in this walk */

                    rel_type_id = datum_to_hash128(raw[j].rel_type);
                    cands[n_cands].object    = raw[j].object;
                    cands[n_cands].rel_type  = raw[j].rel_type;
                    cands[n_cands].rating    = raw[j].rating;
                    cands[n_cands].rd        = raw[j].rd;
                    cands[n_cands].witnesses = raw[j].witnesses;
                    cands[n_cands].score     = walk_relation_rank(rel_type_id) *
                                                (double) (raw[j].rating - 2 * raw[j].rd);
                    n_cands++;
                }
                qsort(cands, n_cands, sizeof(RankedEdge), ranked_edge_cmp_score_desc);

                for (int k = 0; k < n_cands && k < beam; k++)
                {
                    Datum mu;
                    hash128_t obj_id = datum_to_hash128(cands[k].object);
                    bool found3;

                    if (n_nodes >= GENERATE_NODE_BUDGET)
                        ereport(ERROR, (errmsg(
                            "walk_branches: node budget %d exceeded (beam %d × depth %d) — narrow the walk",
                            GENERATE_NODE_BUDGET, beam, max_depth)));
                    if (n_nodes == cap)
                    {
                        cap *= 2;
                        nodes = (WalkNode *) repalloc(nodes, sizeof(WalkNode) * cap);
                    }

                    mu = eff_mu_display_numeric(cands[k].rating, cands[k].rd);
                    nodes[n_nodes].parent    = f;
                    nodes[n_nodes].depth     = level + 1;
                    nodes[n_nodes].entity    = cands[k].object;
                    nodes[n_nodes].rel_type  = cands[k].rel_type;
                    nodes[n_nodes].eff_mu    = mu;
                    nodes[n_nodes].path_mu_fp = nodes[f].path_mu_fp +
                        eff_mu_display_fp(cands[k].rating, cands[k].rd);
                    nodes[n_nodes].witnesses = cands[k].witnesses;
                    n_nodes++;

                    hash_search(seen, &obj_id, HASH_ENTER, &found3);
                }
                pfree(cands);
            }
            pfree(raw);
            pfree(frontier_ids);

            frontier_start = frontier_end;
        }
    }

    {
        WalkOrderKey *keys = (WalkOrderKey *) palloc(sizeof(WalkOrderKey) * n_nodes);

        for (int i = 0; i < n_nodes; i++)
        {
            keys[i].depth       = nodes[i].depth;
            keys[i].path_mu_fp  = nodes[i].path_mu_fp;
            keys[i].idx         = i;
        }
        qsort(keys, n_nodes, sizeof(WalkOrderKey), walk_order_cmp);

        /* keys[0] is the root (unique depth 0) — skipped, as before. */
        for (int oi = 1; oi < n_nodes; oi++)
        {
            int    i = keys[oi].idx;
            Datum  values[7];
            bool   rnulls[7] = { false, false, false, false, false, false, false };

            values[0] = Int32GetDatum(nodes[i].depth);
            values[1] = PointerGetDatum(branch_array(nodes, i, false));
            values[2] = PointerGetDatum(branch_array(nodes, i, true));
            values[3] = nodes[i].entity;
            values[4] = nodes[i].eff_mu;
            values[5] = fp_display_numeric(nodes[i].path_mu_fp);
            values[6] = Int64GetDatum(nodes[i].witnesses);
            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);
        }
        pfree(keys);
    }

    SPI_finish();
    return (Datum) 0;
}

Datum
pg_laplace_walk_strongest(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    bytea  *prompt;
    Datum   type_datum = 0;
    bool    type_null;
    int32   max_depth;
    Datum   cur;
    Datum  *seen;
    int     n_seen, seen_cap;

    if (PG_ARGISNULL(0))
        ereport(ERROR, (errmsg("walk_strongest: prompt entity must not be NULL")));
    prompt    = PG_GETARG_BYTEA_PP(0);
    type_null = PG_ARGISNULL(1);
    if (!type_null)
        type_datum = PG_GETARG_DATUM(1);
    max_depth = PG_ARGISNULL(2) ? 8 : PG_GETARG_INT32(2);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "walk_strongest: SPI_connect failed");
    ensure_edge_plan();

    seen_cap = max_depth + 1;
    if (seen_cap < 8) seen_cap = 8;
    seen = (Datum *) palloc(sizeof(Datum) * seen_cap);
    cur = copy_bytea_datum(PointerGetDatum(prompt));
    seen[0] = cur;
    n_seen = 1;

    for (int step = 1; step <= max_depth; step++)
    {
        ArrayType *seen_arr = construct_array(seen, n_seen, BYTEAOID, -1, false, TYPALIGN_INT);
        Datum  args[4];
        char   nulls[5] = "    ";
        int    rc;
        HeapTuple tup;
        TupleDesc td;
        bool   isnull;
        Datum  obj, etype;
        int64  rating, rd;
        Datum  values[4];
        bool   rnulls[4] = { false, false, false, false };

        args[0] = cur;
        args[1] = type_null ? (Datum) 0 : type_datum;
        if (type_null) nulls[1] = 'n';
        args[2] = Int32GetDatum(1);
        args[3] = PointerGetDatum(seen_arr);

        rc = SPI_execute_plan(edge_plan, args, nulls, true, 1);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "walk_strongest: edge query failed: %s",
                 SPI_result_code_string(rc));
        if (SPI_processed == 0)
        {
            pfree(seen_arr);
            break;
        }

        tup = SPI_tuptable->vals[0];
        td  = SPI_tuptable->tupdesc;
        obj    = copy_bytea_datum(SPI_getbinval(tup, td, 1, &isnull));
        etype  = copy_bytea_datum(SPI_getbinval(tup, td, 2, &isnull));
        rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
        rd     = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));

        values[0] = Int32GetDatum(step);
        values[1] = etype;
        values[2] = obj;
        values[3] = eff_mu_display_numeric(rating, rd);
        tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, rnulls);

        if (n_seen == seen_cap)
        {
            seen_cap *= 2;
            seen = (Datum *) repalloc(seen, sizeof(Datum) * seen_cap);
        }
        seen[n_seen++] = obj;
        cur = obj;
        pfree(seen_arr);
    }

    laplace_spi_finish(spi_top);
    return (Datum) 0;
}


#define VFLAG_HAS_ATOM   ((int64) 1)
#define VFLAG_ATOM_SHIFT 31
#define VFLAG_ATOM_MASK  ((int64) 0x1FFFFF)

/* R1: one indexed bulk closure fetch; C assembles — zero per-node SPI. */
static const char *CLOSURE_QUERY =
    "SELECT parent_id, child_id, run_length, flags "
    "FROM laplace.constituents_closure($1, $2) "
    "ORDER BY parent_id, ordinal";

static SPIPlanPtr closure_plan = NULL;

typedef struct RenderMemoEntry
{
    char  key[16];
    char *text;
} RenderMemoEntry;

typedef struct ClosureChild
{
    Datum child;
    int32 run;
    int64 flags;
} ClosureChild;

typedef struct ClosureParent
{
    char          key[16];
    ClosureChild *kids;
    int           n;
    int           cap;
} ClosureParent;

static void
ensure_render_plans(void)
{
    if (closure_plan == NULL)
    {
        Oid argtypes[2] = { BYTEAARRAYOID, INT4OID };
        SPIPlanPtr plan = SPI_prepare(CLOSURE_QUERY, 2, argtypes);
        if (plan == NULL)
            elog(ERROR, "render_text: SPI_prepare(constituents_closure) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "render_text: SPI_keepplan(constituents_closure) failed");
        closure_plan = plan;
    }
}

static void
append_codepoint_utf8(StringInfo out, uint32 cp)
{
    unsigned char buf[4];
    if (cp < 0x80) { appendStringInfoChar(out, (char) cp); return; }
    if (cp < 0x800)
    {
        buf[0] = 0xC0 | (cp >> 6);
        buf[1] = 0x80 | (cp & 0x3F);
        appendBinaryStringInfo(out, (char *) buf, 2);
        return;
    }
    if (cp < 0x10000)
    {
        buf[0] = 0xE0 | (cp >> 12);
        buf[1] = 0x80 | ((cp >> 6) & 0x3F);
        buf[2] = 0x80 | (cp & 0x3F);
        appendBinaryStringInfo(out, (char *) buf, 3);
        return;
    }
    buf[0] = 0xF0 | (cp >> 18);
    buf[1] = 0x80 | ((cp >> 12) & 0x3F);
    buf[2] = 0x80 | ((cp >> 6) & 0x3F);
    buf[3] = 0x80 | (cp & 0x3F);
    appendBinaryStringInfo(out, (char *) buf, 4);
}

static bool
append_codepoint_render(StringInfo out, Datum id)
{
    bytea   *idb = DatumGetByteaPP(id);
    uint32_t cp;

    if (VARSIZE_ANY_EXHDR(idb) != 16)
        return false;
    if (!laplace_perfcache_codepoint_for_id((const uint8 *) VARDATA_ANY(idb), &cp))
        return false;
    append_codepoint_utf8(out, cp);
    return true;
}

static void
closure_parent_push(ClosureParent *p, Datum child, int32 run, int64 flags)
{
    if (p->n >= p->cap)
    {
        int ncap = (p->cap == 0) ? 4 : p->cap * 2;
        if (p->kids == NULL)
            p->kids = (ClosureChild *) palloc(sizeof(ClosureChild) * ncap);
        else
            p->kids = (ClosureChild *) repalloc(p->kids, sizeof(ClosureChild) * ncap);
        p->cap = ncap;
    }
    p->kids[p->n].child = child;
    p->kids[p->n].run = (run < 1) ? 1 : run;
    p->kids[p->n].flags = flags;
    p->n++;
}

/*
 * Bulk-fetch the constituent DAG for every root in one SPI round-trip.
 * Returns an HTAB keyed by parent entity id (16-byte blob).
 */
static HTAB *
fetch_constituents_closure(Datum *roots, int n_roots, int32 max_depth)
{
    HASHCTL ctl;
    HTAB   *map;
    Datum   args[2];
    int     rc;
    uint64  nrows;
    ArrayType *root_arr;

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(ClosureParent);
    map = hash_create("render closure", 1024, &ctl, HASH_ELEM | HASH_BLOBS);

    if (n_roots <= 0)
        return map;

    root_arr = construct_array(roots, n_roots, BYTEAOID, -1, false, TYPALIGN_INT);
    args[0] = PointerGetDatum(root_arr);
    args[1] = Int32GetDatum(max_depth);

    rc = SPI_execute_plan(closure_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "render_text: constituents_closure failed: %s",
             SPI_result_code_string(rc));

    nrows = SPI_processed;
    for (uint64 r = 0; r < nrows; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td = SPI_tuptable->tupdesc;
        bool isnull;
        Datum parent_d;
        Datum child_d;
        bytea *parent_b;
        char key[16];
        bool found;
        ClosureParent *pe;
        int32 run;
        int64 flags;

        parent_d = SPI_getbinval(tup, td, 1, &isnull);
        if (isnull)
            continue;
        parent_b = DatumGetByteaPP(parent_d);
        if (VARSIZE_ANY_EXHDR(parent_b) != 16)
            continue;
        memcpy(key, VARDATA_ANY(parent_b), 16);

        child_d = copy_bytea_datum(SPI_getbinval(tup, td, 2, &isnull));
        run = DatumGetInt32(SPI_getbinval(tup, td, 3, &isnull));
        flags = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));

        pe = (ClosureParent *) hash_search(map, key, HASH_ENTER, &found);
        if (!found)
        {
            pe->kids = NULL;
            pe->n = 0;
            pe->cap = 0;
        }
        closure_parent_push(pe, child_d, run, flags);
    }
    SPI_freetuptable(SPI_tuptable);
    return map;
}

static const char *
render_node(HTAB *closure, HTAB *memo, Datum id, int depth, int max_depth)
{
    char key[16];
    bool found;
    RenderMemoEntry *e;
    bytea *idb = DatumGetByteaPP(id);

    if (VARSIZE_ANY_EXHDR(idb) != 16)
        ereport(ERROR, (errmsg("render_text: entity id must be 16 bytes")));
    memcpy(key, VARDATA_ANY(idb), 16);

    e = (RenderMemoEntry *) hash_search(memo, key, HASH_ENTER, &found);
    if (found)
        return e->text;
    e->text = NULL;

    if (depth < max_depth)
    {
        ClosureParent *pe = (ClosureParent *) hash_search(closure, key, HASH_FIND, &found);

        if (found && pe->n > 0)
        {
            StringInfoData out;
            bool ok = true;

            initStringInfo(&out);
            for (int r = 0; r < pe->n && ok; r++)
            {
                if (pe->kids[r].flags & VFLAG_HAS_ATOM)
                {
                    uint32 cp = (uint32) ((pe->kids[r].flags >> VFLAG_ATOM_SHIFT) & VFLAG_ATOM_MASK);
                    for (int32 k = 0; k < pe->kids[r].run; k++)
                        append_codepoint_utf8(&out, cp);
                }
                else
                {
                    const char *child_text = render_node(closure, memo, pe->kids[r].child,
                                                         depth + 1, max_depth);
                    if (child_text != NULL)
                        for (int32 k = 0; k < pe->kids[r].run; k++)
                            appendStringInfoString(&out, child_text);
                    else if (!append_codepoint_render(&out, pe->kids[r].child))
                        ok = false;
                }
            }

            e = (RenderMemoEntry *) hash_search(memo, key, HASH_FIND, &found);
            Assert(found);
            e->text = ok ? out.data : NULL;
            return e->text;
        }
    }

    {
        StringInfoData out;
        initStringInfo(&out);
        if (append_codepoint_render(&out, id))
        {
            e = (RenderMemoEntry *) hash_search(memo, key, HASH_FIND, &found);
            Assert(found);
            e->text = out.data;
        }
        return e->text;
    }
}

PG_FUNCTION_INFO_V1(pg_laplace_render_text);
PG_FUNCTION_INFO_V1(pg_laplace_render_text_fast);
PG_FUNCTION_INFO_V1(pg_laplace_render_text_batch);

Datum
pg_laplace_render_text(PG_FUNCTION_ARGS)
{
    Datum   id;
    int32   max_depth;
    HASHCTL ctl;
    HTAB   *memo;
    HTAB   *closure;
    const char *rendered;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id = PG_GETARG_DATUM(0);
    max_depth = PG_ARGISNULL(1) ? 32 : PG_GETARG_INT32(1);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "render_text: SPI_connect failed");
    ensure_render_plans();

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(RenderMemoEntry);
    memo = hash_create("render_text memo", 1024, &ctl, HASH_ELEM | HASH_BLOBS);

    closure = fetch_constituents_closure(&id, 1, max_depth);
    rendered = render_node(closure, memo, id, 0, max_depth);

    if (rendered == NULL || rendered[0] == '\0')
    {
        SPI_finish();
        PG_RETURN_NULL();
    }
    else
    {
        MemoryContext spi_cxt = MemoryContextSwitchTo(caller_cxt);
        text *result = cstring_to_text(rendered);
        MemoryContextSwitchTo(spi_cxt);
        SPI_finish();
        PG_RETURN_TEXT_P(result);
    }
}

Datum
pg_laplace_render_text_fast(PG_FUNCTION_ARGS)
{
    Datum   id;
    int32   max_depth = 8;
    HASHCTL ctl;
    HTAB   *memo;
    HTAB   *closure;
    const char *rendered;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    id = PG_GETARG_DATUM(0);
    if (PG_NARGS() > 1 && !PG_ARGISNULL(1))
        max_depth = PG_GETARG_INT32(1);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "render_text_fast: SPI_connect failed");
    ensure_render_plans();

    memset(&ctl, 0, sizeof(ctl));
    ctl.keysize = 16;
    ctl.entrysize = sizeof(RenderMemoEntry);
    memo = hash_create("render_text_fast memo", 1024, &ctl, HASH_ELEM | HASH_BLOBS);

    closure = fetch_constituents_closure(&id, 1, max_depth);
    rendered = render_node(closure, memo, id, 0, max_depth);

    if (rendered == NULL || rendered[0] == '\0')
    {
        SPI_finish();
        PG_RETURN_NULL();
    }
    MemoryContext spi_cxt = MemoryContextSwitchTo(caller_cxt);
    text *result = cstring_to_text(rendered);
    MemoryContextSwitchTo(spi_cxt);
    SPI_finish();
    PG_RETURN_TEXT_P(result);
}

Datum
pg_laplace_render_text_batch(PG_FUNCTION_ARGS)
{
    ArrayType  *arr;
    int32       max_depth;
    Datum      *elems;
    bool       *nulls;
    int         n;
    Datum      *out;
    bool       *out_nulls;
    ArrayType  *result;
    Datum      *roots;
    int         n_roots = 0;
    HTAB       *closure;
    HASHCTL     mctl;
    HTAB       *memo;
    MemoryContext caller_cxt = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    arr = PG_GETARG_ARRAYTYPE_P(0);
    max_depth = PG_ARGISNULL(1) ? 8 : PG_GETARG_INT32(1);

    if (ARR_NDIM(arr) != 1)
        ereport(ERROR, (errmsg("render_text_batch: ids must be 1-dimensional")));
    if (ARR_ELEMTYPE(arr) != BYTEAOID)
        ereport(ERROR, (errmsg("render_text_batch: element type must be bytea")));

    deconstruct_array(arr, BYTEAOID, -1, false, TYPALIGN_INT,
                      &elems, &nulls, &n);
    out = (Datum *) palloc0(sizeof(Datum) * n);
    out_nulls = (bool *) palloc0(sizeof(bool) * n);
    roots = (Datum *) palloc(sizeof(Datum) * n);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "render_text_batch: SPI_connect failed");
    ensure_render_plans();

    for (int i = 0; i < n; i++)
    {
        if (!nulls[i])
            roots[n_roots++] = elems[i];
    }

    closure = fetch_constituents_closure(roots, n_roots, max_depth);

    memset(&mctl, 0, sizeof(mctl));
    mctl.keysize = 16;
    mctl.entrysize = sizeof(RenderMemoEntry);
    memo = hash_create("render_text_batch memo", 1024, &mctl, HASH_ELEM | HASH_BLOBS);

    for (int i = 0; i < n; i++)
    {
        const char *rendered;

        if (nulls[i])
        {
            out_nulls[i] = true;
            continue;
        }
        rendered = render_node(closure, memo, elems[i], 0, max_depth);
        if (rendered == NULL || rendered[0] == '\0')
            out_nulls[i] = true;
        else
        {
            MemoryContext old = MemoryContextSwitchTo(caller_cxt);
            out[i] = CStringGetTextDatum(rendered);
            MemoryContextSwitchTo(old);
        }
    }
    SPI_finish();

    {
        int dims[1] = { n };
        int lbs[1] = { 1 };
        result = construct_md_array(out, out_nulls, 1, dims, lbs,
                                  TEXTOID, -1, false, TYPALIGN_INT);
    }
    PG_RETURN_ARRAYTYPE_P(result);
}
