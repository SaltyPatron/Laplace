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
#include "laplace/core/highway_table.h"
#include "laplace/core/math4d.h"
#include "laplace/core/mantissa.h"

#include "spi_common.h"
#include "spi_nested.h"
#include "perfcache_native.h"
#include "walk_score.h"
#include "relation_symmetry.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_branches);
PG_FUNCTION_INFO_V1(pg_laplace_walk_strongest);

static const char *EDGE_QUERY =
    "SELECT object_id, type_id, rating, rd, witness_count "
    "FROM consensus.walk_edges($1, $2, $3, $4)";

static SPIPlanPtr edge_plan = NULL;

static void
ensure_edge_plan(void)
{
    if (edge_plan == NULL)
    {
        Oid argtypes[4] = { BYTEAOID, BYTEAOID, INT4OID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(EDGE_QUERY, 4, argtypes, CURSOR_OPT_PARALLEL_OK);
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
 * replacement for calling consensus.walk_edges() once per frontier node.
 * consensus.walk_edges() did real per-call work in SQL: fetch + compute an
 * unindexed `consensus.relation_rank_resolved(type_id) * consensus.eff_mu(rating,rd)` sort key
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
/*
 * 3Cb/3Cc: highway_mask (native bit-gate, gated in C against p_intent_mask)
 * and subject/object point coordinates (S3 angular beam term) ride the same
 * per-level batch query -- no extra round trip. Coordinates are fetched via
 * ST_X/Y/Z/M (liblwgeom isn't linked, same constraint as recall.c's
 * word_shape_peers_fast_impl) with LEFT JOINs so a coord-less entity (no
 * point physicality yet) degrades to "unknown geometry", never an error.
 * tableoid on each side gives the physicalities partition. That partition is
 * HASH(id) over 64 partitions -- NOT a hilbert band -- so tableoid equality
 * certifies only "same hash bucket", which carries no locality. The +1.0 it
 * used to add scored a hash collision; the bonus is gone, not merely gated.
 *
 * GEOMETRY IS OPT-IN because physicalities is HASH(id)-partitioned while this
 * joins on entity_id: the key cannot prune, so each side fans out across all
 * 64 partitions, twice per candidate edge. MEASURED live on a 200-node
 * frontier (21,413 candidate edges): 30,305ms with the two LEFT JOINs vs
 * 408ms without -- 74x, to fund an ordering tie-break against folded belief.
 * Identical pathology to ordinal_continuity_distance below,
 * which was already made opt-in for it; this sibling was never rewired.
 */
/*
 * A SYMMETRIC RELATION IS ONE CELL, SO THE READ MUST PROBE BOTH ENDS.
 *
 * laplace_attestation_orient() canonicalises a symmetric assertion to
 * subject = min(subject, object) by hash bytes, so the unordered pair {a,b}
 * folds into exactly one consensus cell instead of two half-rated ones. That
 * is correct and load-bearing on the WRITE side -- all evidence for the pair
 * accumulates in one place.
 *
 * It also means `c.subject_id = frontier` alone can only ever traverse such a
 * pair from its lesser-hashed end. Measured on the live substrate 2026-08-24:
 * of 133,019 symmetric cells (DERIVATIONALLY_RELATED, IS_ANTONYM_OF,
 * IS_SIMILAR_TO, CONFUSABLE_WITH, IN_VERB_GROUP_WITH) 133,019 are stored
 * subject < object and 0 the other way -- so the reverse direction of every
 * symmetric edge in the substrate was unreachable. Concretely: the antonym
 * cell ('avant jesus christ', IS_ANTONYM_OF, 'apres jesus christ') answered
 * the question asked from `avant` and returned NOTHING asked from `apres`.
 * Same cell, same rating; the hash order of the id decided whether the
 * substrate appeared to know the fact.
 *
 * The reverse arm is restricted to symmetric type ids ($3): traversing an
 * ASYMMETRIC edge backwards would be a different claim (IS_A up is not IS_A
 * down), so it stays forbidden. steer_candidates.c already unions both
 * directions; this is retrieval agreeing with steering about which edges
 * exist, the same way walk_score.h makes them agree about what one is worth.
 *
 * Indexed, not a scan: consensus_object_type_btree (object_id, type_id)
 * WHERE object_id IS NOT NULL exists on every partition.
 */
static const char *WALK_BATCH_QUERY =
    "SELECT e.idx, e.neighbor, eo.type_id, e.type_id, e.rating, e.rd, e.witness_count, "
    "       eo.highway_mask, "
    "       ST_X(ps.coord), ST_Y(ps.coord), ST_Z(ps.coord), ST_M(ps.coord), ps.physicality_tableoid, "
    "       ST_X(po.coord), ST_Y(po.coord), ST_Z(po.coord), ST_M(po.coord), po.physicality_tableoid "
    "FROM ( "
    "  SELECT f.idx, f.subject_id AS anchor, c.object_id AS neighbor, "
    "         c.type_id, c.rating, c.rd, c.witness_count "
    "    FROM unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "    JOIN laplace.consensus c ON c.subject_id = f.subject_id "
    "   WHERE c.object_id IS NOT NULL "
    "     AND ($2::bytea IS NULL OR c.type_id = $2) "
    "  UNION ALL "
    "  SELECT f.idx, f.subject_id AS anchor, c.subject_id AS neighbor, "
    "         c.type_id, c.rating, c.rd, c.witness_count "
    "    FROM unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "    JOIN laplace.consensus c ON c.object_id = f.subject_id "
    "   WHERE c.object_id IS NOT NULL "
    "     AND c.subject_id <> c.object_id "
    "     AND c.type_id = ANY($3::bytea[]) "
    "     AND ($2::bytea IS NULL OR c.type_id = $2) "
    ") e "
    "JOIN laplace.entities eo ON eo.id = e.neighbor "
    "LEFT JOIN laplace.v_word_points ps ON ps.id = e.anchor "
    "LEFT JOIN laplace.v_word_points po ON po.id = e.neighbor";

/* Same edge set, no coordinate fetch. The geometry columns are read
 * conditionally in the fetch loop, so this plan's narrower tuple descriptor is
 * the only difference the caller sees. */
static const char *WALK_BATCH_QUERY_NOGEO =
    "SELECT e.idx, e.neighbor, eo.type_id, e.type_id, e.rating, e.rd, e.witness_count, "
    "       eo.highway_mask "
    "FROM ( "
    "  SELECT f.idx, f.subject_id AS anchor, c.object_id AS neighbor, "
    "         c.type_id, c.rating, c.rd, c.witness_count "
    "    FROM unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "    JOIN laplace.consensus c ON c.subject_id = f.subject_id "
    "   WHERE c.object_id IS NOT NULL "
    "     AND ($2::bytea IS NULL OR c.type_id = $2) "
    "  UNION ALL "
    "  SELECT f.idx, f.subject_id AS anchor, c.subject_id AS neighbor, "
    "         c.type_id, c.rating, c.rd, c.witness_count "
    "    FROM unnest($1::bytea[]) WITH ORDINALITY AS f(subject_id, idx) "
    "    JOIN laplace.consensus c ON c.object_id = f.subject_id "
    "   WHERE c.object_id IS NOT NULL "
    "     AND c.subject_id <> c.object_id "
    "     AND c.type_id = ANY($3::bytea[]) "
    "     AND ($2::bytea IS NULL OR c.type_id = $2) "
    ") e "
    "JOIN laplace.entities eo ON eo.id = e.neighbor";

static SPIPlanPtr walk_batch_plan = NULL;
static SPIPlanPtr walk_batch_nogeo_plan = NULL;

static void
ensure_walk_batch_nogeo_plan(void)
{
    if (walk_batch_nogeo_plan == NULL)
    {
        Oid argtypes[3] = { BYTEAARRAYOID, BYTEAOID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(WALK_BATCH_QUERY_NOGEO, 3, argtypes, CURSOR_OPT_PARALLEL_OK);
        if (plan == NULL)
            elog(ERROR, "generate walk: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "generate walk: SPI_keepplan failed");
        walk_batch_nogeo_plan = plan;
    }
}

static void
ensure_walk_batch_plan(void)
{
    if (walk_batch_plan == NULL)
    {
        Oid argtypes[3] = { BYTEAARRAYOID, BYTEAOID, BYTEAARRAYOID };
        SPIPlanPtr plan = SPI_prepare_cursor(WALK_BATCH_QUERY, 3, argtypes, CURSOR_OPT_PARALLEL_OK);
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
 * The per-edge score (relation_rank x Glicko-complete signed weight, doc 15
 * Phase 3Ca / doc 14 P5) now lives in walk_score.h, shared with S7
 * (steer_candidates.c) so retrieval and steering cannot disagree about what an
 * edge is worth. The full rationale, including why the SPI-fallback rank path
 * is deliberately not replicated, moved with it.
 */

typedef struct RawEdge
{
    int32  idx;
    Datum  object;
    Datum  object_type;
    Datum  rel_type;
    int64  rating;
    int64  rd;
    int64  witnesses;
    Datum  highway_mask;   /* bytea(32), may be (Datum) 0 if NULL (not yet backfilled) */
    bool   subj_coord_ok;
    double subj_xyzm[4];
    Oid    subj_partoid;   /* InvalidOid if no point physicality */
    bool   obj_coord_ok;
    double obj_xyzm[4];
    Oid    obj_partoid;
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
    double base;                  /* adjudicated relation belief */
    bool   topic_match;           /* exact membership in caller topic set */
    bool   continuity_known;      /* both endpoints found in one trajectory */
    uint64 continuity_distance;   /* exact ordinal distance; smaller is nearer */
    bool   geometry_known;        /* both endpoints have point physicalities */
    double geometry_distance;     /* exact S3 angle; smaller is nearer */
} RankedEdge;

/*
 * Belief first, always. Base is the relation rank times the signed Glicko
 * expectation of the folded state. The remaining dimensions are ordered facts,
 * not a weighted score: caller topic membership, exact trajectory distance,
 * then exact S3 distance. They break belief ties only and therefore cannot
 * replace or reverse the adjudicated verdict.
 *
 * Final key is the object id, so otherwise-equal candidates emit in
 * one fixed order. qsort is unstable and SS15 requires byte-identical output.
 */
static int
ranked_edge_cmp_score_desc(const void *a, const void *b)
{
    const RankedEdge *ea = (const RankedEdge *) a;
    const RankedEdge *eb = (const RankedEdge *) b;
    bytea *oa, *ob;
    int    la, lb, c;

    if (ea->base != eb->base)
        return (ea->base < eb->base) - (ea->base > eb->base);
    if (ea->topic_match != eb->topic_match)
        return ea->topic_match ? -1 : 1;
    if (ea->continuity_known != eb->continuity_known)
        return ea->continuity_known ? -1 : 1;
    if (ea->continuity_known && ea->continuity_distance != eb->continuity_distance)
        return (ea->continuity_distance > eb->continuity_distance) -
               (ea->continuity_distance < eb->continuity_distance);
    if (ea->geometry_known != eb->geometry_known)
        return ea->geometry_known ? -1 : 1;
    if (ea->geometry_known && ea->geometry_distance != eb->geometry_distance)
        return (ea->geometry_distance > eb->geometry_distance) -
               (ea->geometry_distance < eb->geometry_distance);

    oa = DatumGetByteaPP(ea->object);
    ob = DatumGetByteaPP(eb->object);
    la = VARSIZE_ANY_EXHDR(oa);
    lb = VARSIZE_ANY_EXHDR(ob);
    c  = memcmp(VARDATA_ANY(oa), VARDATA_ANY(ob), la < lb ? la : lb);
    if (c != 0)
        return c;
    return (la > lb) - (la < lb);
}

/*
 * 3Cb gating: p_intent_mask is an opaque caller-supplied bytea(32) -- the
 * intent->band decision itself lives at the SQL call site (doc 22 Phase B's
 * frame-evocation intent_band(prompt) isn't built yet, so
 * recall_walk_response.sql.in resolves a band from route.intent via
 * highway_band_mask(band) as a stopgap and passes the resulting mask in
 * here). Swapping in real intent_band(prompt) later is a SQL-only change --
 * this native gate has no intent vocabulary baked into it.
 *
 * laplace_mask256_t <-> bytea(32), matching pg_laplace_highway_mask_from_bits'
 * own layout assumption (highway_mask.c) -- direct memcpy, no byte-swap. */
static bool
mask_overlaps(Datum highway_mask_bytea, const laplace_mask256_t *intent_mask)
{
    bytea *b;
    laplace_mask256_t cand, overlap;

    if (highway_mask_bytea == (Datum) 0)
        return true; /* unknown mask: never gate on absence of information */
    b = DatumGetByteaPP(highway_mask_bytea);
    if (VARSIZE_ANY_EXHDR(b) != (int) sizeof(laplace_mask256_t))
        return true; /* malformed/legacy row: fail open, don't silently drop candidates */
    memcpy(&cand, VARDATA_ANY(b), sizeof(laplace_mask256_t));
    overlap = highway_table_mask_and(cand, *intent_mask);
    return highway_table_mask_any(&overlap) != 0;
}

/*
 * 3Cc trajectory-ordinal continuity: mirrors containers_of.c's exact idiom
 * (SPI_prepare/SPI_keepplan once, ensure_*_plan() pattern) rather than doing
 * raw float math on physicalities.trajectory -- those vertices are
 * intentionally mantissa-packed hash/ordinal/run_length payloads (see
 * mantissa.h), packed specifically so structural lookups like this stay
 * GIN-index-backed. Finds ONE trajectory containing both the walk's current
 * subject and a candidate object (single LIMIT 1, not the unbounded
 * ST_DumpPoints-over-every-match scan containers_of.c's own comment warns
 * against for large arrays), dumps its points, and mantissa_unpacks each
 * vertex looking for the two target ordinals. Applied only to the
 * exact post-belief-sort admission set. Belief is the primary ordering key and
 * continuity is only a tie-break, so only candidates through the full belief
 * tie at the requested beam boundary can affect the result.
 */
/*
 * Single-key containment ONLY (proven ~2ms/GIN-index-backed regardless of
 * table size, per containers_of.c's own measurement) -- the object entity
 * alone selects the candidate trajectory. Whether that trajectory also
 * contains the subject is checked in C during the vertex-decode loop below,
 * NOT pushed into a second SQL key. A two-key `@> ARRAY[$1,$2]` probe was
 * tried here first and hit exactly the anti-pattern containers_of.c's header
 * comment warns about (planner abandons the GIN index for a full/bitmap
 * scan on a multi-key bound array) -- caught live via a 24s regress test
 * that should have run in milliseconds.
 */
static const char *ORDINAL_CONTINUITY_QUERY =
    "WITH t AS ( "
    "  SELECT w.trajectory FROM laplace.v_word_points w "
    "  WHERE public.laplace_trajectory_constituent_ids(w.trajectory) @> ARRAY[$1]::bytea[] "
    "  LIMIT 1 "
    ") "
    "SELECT ST_X(dp.geom), ST_Y(dp.geom), ST_Z(dp.geom), ST_M(dp.geom) "
    "FROM t, ST_DumpPoints(t.trajectory) dp "
    "ORDER BY (dp.path)[1]";

static SPIPlanPtr ordinal_continuity_plan = NULL;

static void
ensure_ordinal_continuity_plan(void)
{
    if (ordinal_continuity_plan == NULL)
    {
        Oid argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare_cursor(ORDINAL_CONTINUITY_QUERY, 1, argtypes, CURSOR_OPT_PARALLEL_OK);

        if (plan == NULL)
            elog(ERROR, "walk_branches: SPI_prepare_cursor(ordinal continuity) failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "walk_branches: SPI_keepplan(ordinal continuity) failed");
        ordinal_continuity_plan = plan;
    }
}

/*
 * Returns true and the exact |ordinal_delta| when both subject_entity and
 * object_entity are found as vertices of the SAME
 * trajectory selected by the object's own (fast, single-key) containment
 * probe; false otherwise (no such trajectory, that trajectory doesn't also
 * carry the subject, or coordinate/mantissa data didn't decode -- never an
 * error). Trades a little converse.recall(a
 * DIFFERENT trajectory might contain both when the object's first match
 * doesn't) for guaranteed single-key-probe performance.
 */
static bool
ordinal_continuity_distance(Datum subject_entity, Datum object_entity,
                            uint64 *distance)
{
    Datum  args[1];
    int    rc;
    bool   have_subj = false, have_obj = false;
    /* int64, not uint16: the ordinal is DERIVED from vertex position here (see
     * the loop), so it is no longer bounded by the packed field's width. */
    int64  subj_ord = 0, obj_ord = 0;
    hash128_t subj_h, obj_h;

    ensure_ordinal_continuity_plan();
    subj_h = datum_to_hash128(subject_entity);
    obj_h  = datum_to_hash128(object_entity);

    args[0] = object_entity;
    rc = SPI_execute_plan(ordinal_continuity_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return false;

    /* ORDINAL IS DERIVED FROM VERTEX POSITION, NOT READ OUT OF THE PACKED FIELD.
     * The query above is ORDER BY (dp.path)[1], so vertices arrive in sequence
     * and the running sum of run_length IS the ordinal -- exact for both vertex
     * shapes (run_length is 1 on every plain trajectory, making the sum the
     * position; it is the true source ordinal on a run-length vertex, where
     * position deliberately does not track it).
     *
     * The packed copy is 16 bits and stops being able to state the position past
     * 65,535 constituents, where the writer now stores 0 rather than a wrapped
     * value. Reading it directly there would give both endpoints ordinal 0, hence
     * delta 0, hence the minimum distance for any pair in a wide
     * trajectory -- silently, since this path never errors by contract. Deriving
     * costs nothing and removes the width from the equation entirely. */
    int64 ordinal = 1;

    for (uint64 r = 0; r < SPI_processed; r++)
    {
        HeapTuple tup = SPI_tuptable->vals[r];
        TupleDesc td  = SPI_tuptable->tupdesc;
        bool isnull[4];
        double vertex[4];
        mantissa_payload_t payload;
        int64 run;

        vertex[0] = DatumGetFloat8(SPI_getbinval(tup, td, 1, &isnull[0]));
        vertex[1] = DatumGetFloat8(SPI_getbinval(tup, td, 2, &isnull[1]));
        vertex[2] = DatumGetFloat8(SPI_getbinval(tup, td, 3, &isnull[2]));
        vertex[3] = DatumGetFloat8(SPI_getbinval(tup, td, 4, &isnull[3]));
        if (isnull[0] || isnull[1] || isnull[2] || isnull[3])
        {
            /* Undecodable vertex: its run_length is unknown, so advance by the
             * minimum a vertex can cover rather than desynchronising the count. */
            ordinal += 1;
            continue;
        }

        mantissa_unpack(vertex, &payload);
        run = (payload.run_length < 1) ? 1 : (int64) payload.run_length;

        if (!have_obj && hash128_eq(&payload.entity_id, &obj_h))
        {
            obj_ord = ordinal;
            have_obj = true;
        }
        if (!have_subj && hash128_eq(&payload.entity_id, &subj_h))
        {
            subj_ord = ordinal;
            have_subj = true;
        }
        if (have_obj && have_subj)
            break;

        ordinal += run;
    }
    /* SPI_tuptable freed automatically at the enclosing SPI_finish/next
     * SPI_execute_plan call -- this function borrows no pointers past return. */
    if (!have_obj || !have_subj)
        return false;
    {
        int64 delta = obj_ord - subj_ord;   /* int64: ordinals are no longer 16-bit */
        if (delta < 0) delta = -delta;
        *distance = (uint64) delta;
        return true;
    }
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
    bool    have_intent_mask = false;
    laplace_mask256_t intent_mask;
    Datum  *topic_bias = NULL;
    int     n_topic_bias = 0;
    bool    ordinal_continuity_enabled;
    bool    use_geometry;

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
    if (PG_NARGS() > 4 && !PG_ARGISNULL(4))
    {
        bytea *mb = PG_GETARG_BYTEA_PP(4);
        if (VARSIZE_ANY_EXHDR(mb) != (int) sizeof(laplace_mask256_t))
            ereport(ERROR, (errmsg("walk_branches: p_intent_mask must be exactly 32 bytes")));
        memcpy(&intent_mask, VARDATA_ANY(mb), sizeof(laplace_mask256_t));
        have_intent_mask = true;
    }
    if (PG_NARGS() > 5 && !PG_ARGISNULL(5))
    {
        ArrayType *bias_arr = PG_GETARG_ARRAYTYPE_P(5);
        bool      *bias_nulls;
        deconstruct_array(bias_arr, BYTEAOID, -1, false, TYPALIGN_INT,
                          &topic_bias, &bias_nulls, &n_topic_bias);
    }
    /*
     * Opt-in, default false: ordinal_continuity_distance's containment probe
     * has no locality restriction, so physicalities (HASH-partitioned
     * 64 ways) pays a 64-partition Append scan on every miss -- measured
     * 42ms/call live via EXPLAIN ANALYZE, and misses are the COMMON case
     * (most walked entities aren't a trajectory constituent of anything).
     * Per-candidate calls across an unfiltered deep walk were
     * caught turning a sub-second regress test into 24s. Needs a
     * hilbert-band-scoped search before it's cheap enough to default on --
     * tracked as follow-up, not shipped silently slow. recall_walk_response
     * (the live-served path) does not opt in.
     */
    ordinal_continuity_enabled = (PG_NARGS() > 6 && !PG_ARGISNULL(6)) ? PG_GETARG_BOOL(6) : false;
    /*
     * Opt-in, default false, for the reason given above WALK_BATCH_QUERY: the
     * coordinate fetch costs 74x the whole edge scan and buys an ordering
     * nudge bounded at +2.0. Off, the walk ranks on adjudicated belief alone,
     * which is what INVENTION SS9 rules anyway -- geometry is instrument-tier
     * and point proximity is not the relatedness signal.
     */
    use_geometry = (PG_NARGS() > 7 && !PG_ARGISNULL(7)) ? PG_GETARG_BOOL(7) : false;

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "walk_branches: SPI_connect failed");
    if (use_geometry)
        ensure_walk_batch_plan();
    else
        ensure_walk_batch_nogeo_plan();
    ensure_relationtype_type_id();

    cap = 1;
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
     * reach it, since edges are processed in rank order), a worse/deeper
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

        for (int frontier_start = 0, level = 0;
             level < max_depth && beam > 0;
             level++)
        {
            int frontier_end = n_nodes;
            int n_frontier;
            Datum *frontier_ids;
            ArrayType *frontier_arr;
            Datum  args[3];
            char   nulls[4] = "   ";
            int    rc;
            RawEdge *raw;
            int      n_raw;

            if (frontier_start == frontier_end)
                break;

            n_frontier = frontier_end - frontier_start;

            /* This level can add at most p_breadth children per frontier
             * member. Reserve that exact requested work once, rather than
             * repeatedly doubling a guessed capacity and then failing at an
             * unrelated fixed global node count. */
            {
                int64 level_capacity = (int64) n_nodes
                                     + (int64) n_frontier * (int64) beam;

                if (level_capacity > PG_INT32_MAX
                    || (uint64) level_capacity >
                       (uint64) (MaxAllocSize / sizeof(WalkNode)))
                    ereport(ERROR, (errmsg(
                        "walk_branches: requested depth/breadth exceeds PostgreSQL allocation capacity at level %d",
                        level + 1)));
                if ((int) level_capacity > cap)
                {
                    cap = (int) level_capacity;
                    nodes = (WalkNode *) repalloc(nodes, sizeof(WalkNode) * cap);
                }
            }

            frontier_ids = (Datum *) palloc(sizeof(Datum) * n_frontier);
            for (int f = frontier_start; f < frontier_end; f++)
                frontier_ids[f - frontier_start] = nodes[f].entity;
            frontier_arr = construct_array(frontier_ids, n_frontier, BYTEAOID, -1, false, TYPALIGN_INT);

            args[0] = PointerGetDatum(frontier_arr);
            args[1] = type_null ? (Datum) 0 : type_datum;
            if (type_null) nulls[1] = 'n';
            args[2] = PointerGetDatum(laplace_symmetric_relation_types());

            rc = SPI_execute_plan(use_geometry ? walk_batch_plan : walk_batch_nogeo_plan,
                                  args, nulls, true, 0);
            if (rc != SPI_OK_SELECT)
                elog(ERROR, "walk_branches: batch edge query failed: %s",
                     SPI_result_code_string(rc));

            if (SPI_processed > (uint64) PG_INT32_MAX
                || SPI_processed > (uint64) (MaxAllocSize / sizeof(RawEdge)))
                ereport(ERROR, (errmsg(
                    "walk_branches: edge frontier exceeds PostgreSQL allocation capacity")));
            n_raw = (int) SPI_processed;
            raw = (RawEdge *) palloc(sizeof(RawEdge) * (n_raw > 0 ? n_raw : 1));
            for (int r = 0; r < n_raw; r++)
            {
                HeapTuple tup = SPI_tuptable->vals[r];
                TupleDesc td  = SPI_tuptable->tupdesc;
                bool isnull, cnull;
                Datum hm;

                raw[r].idx         = DatumGetInt64(SPI_getbinval(tup, td, 1, &isnull));
                raw[r].object      = copy_bytea_datum(SPI_getbinval(tup, td, 2, &isnull));
                raw[r].object_type = copy_bytea_datum(SPI_getbinval(tup, td, 3, &isnull));
                raw[r].rel_type    = copy_bytea_datum(SPI_getbinval(tup, td, 4, &isnull));
                raw[r].rating      = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
                raw[r].rd          = DatumGetInt64(SPI_getbinval(tup, td, 6, &isnull));
                raw[r].witnesses   = DatumGetInt64(SPI_getbinval(tup, td, 7, &isnull));

                hm = SPI_getbinval(tup, td, 8, &isnull);
                raw[r].highway_mask = isnull ? (Datum) 0 : copy_bytea_datum(hm);

                /* Columns 9..18 exist only in the geometry plan. */
                if (!use_geometry)
                {
                    raw[r].subj_coord_ok = false;
                    raw[r].obj_coord_ok  = false;
                    raw[r].subj_partoid  = InvalidOid;
                    raw[r].obj_partoid   = InvalidOid;
                    continue;
                }

                raw[r].subj_xyzm[0] = DatumGetFloat8(SPI_getbinval(tup, td, 9,  &isnull));
                raw[r].subj_xyzm[1] = DatumGetFloat8(SPI_getbinval(tup, td, 10, &cnull)); isnull |= cnull;
                raw[r].subj_xyzm[2] = DatumGetFloat8(SPI_getbinval(tup, td, 11, &cnull)); isnull |= cnull;
                raw[r].subj_xyzm[3] = DatumGetFloat8(SPI_getbinval(tup, td, 12, &cnull)); isnull |= cnull;
                raw[r].subj_coord_ok = !isnull;
                raw[r].subj_partoid = DatumGetObjectId(SPI_getbinval(tup, td, 13, &isnull));
                if (isnull) raw[r].subj_partoid = InvalidOid;

                raw[r].obj_xyzm[0] = DatumGetFloat8(SPI_getbinval(tup, td, 14, &isnull));
                raw[r].obj_xyzm[1] = DatumGetFloat8(SPI_getbinval(tup, td, 15, &cnull)); isnull |= cnull;
                raw[r].obj_xyzm[2] = DatumGetFloat8(SPI_getbinval(tup, td, 16, &cnull)); isnull |= cnull;
                raw[r].obj_xyzm[3] = DatumGetFloat8(SPI_getbinval(tup, td, 17, &cnull)); isnull |= cnull;
                raw[r].obj_coord_ok = !isnull;
                raw[r].obj_partoid = DatumGetObjectId(SPI_getbinval(tup, td, 18, &isnull));
                if (isnull) raw[r].obj_partoid = InvalidOid;
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
                    double    base;

                    /*
                     * 3Ca: refutation is no longer a hard drop -- signed
                     * folded belief (below) naturally pushes refuted consensus.edges(eff_mu
                     * below neutral) to a negative base, so they qsort last
                     * but still appear in output (doc 15 I2: confirming a
                     * response = confirming the edges it walked; refuting
                     * must be equally visible, not silently absent).
                     */
                    if (hash128_eq(&obj_type_id, &g_relationtype_type_id))
                        continue; /* object is itself a RelationType meta-entity */
                    obj_id = datum_to_hash128(raw[j].object);
                    hash_search(seen, &obj_id, HASH_FIND, &found2);
                    if (found2)
                        continue; /* already placed elsewhere in this walk */
                    /* 3Cb: hard-gate on the caller's intent mask, if given. */
                    if (have_intent_mask && !mask_overlaps(raw[j].highway_mask, &intent_mask))
                        continue;

                    rel_type_id = datum_to_hash128(raw[j].rel_type);
                    base = walk_relation_rank(rel_type_id) *
                           laplace_walk_edge_weight(raw[j].rating, raw[j].rd);

                    cands[n_cands].topic_match = false;
                    cands[n_cands].continuity_known = false;
                    cands[n_cands].continuity_distance = 0;
                    cands[n_cands].geometry_known =
                        raw[j].subj_coord_ok && raw[j].obj_coord_ok;
                    cands[n_cands].geometry_distance =
                        cands[n_cands].geometry_known
                            ? math4d_angular_distance(raw[j].subj_xyzm, raw[j].obj_xyzm)
                            : 0.0;
                    /*
                     * No partition-band bonus. laplace.physicalities is
                     * HASH(id) over 64 partitions, not RANGE over hilbert
                     * bands, so equal tableoids mean "same hash bucket" and
                     * nothing else -- the bonus was paid on collision.
                     */

                    /* 3Cd: session/topic frontier bias. */
                    if (topic_bias != NULL)
                    {
                        for (int t = 0; t < n_topic_bias; t++)
                        {
                            if (bytea_eq(raw[j].object, topic_bias[t]))
                            {
                                cands[n_cands].topic_match = true;
                                break;
                            }
                        }
                    }

                    cands[n_cands].object    = raw[j].object;
                    cands[n_cands].rel_type  = raw[j].rel_type;
                    cands[n_cands].rating    = raw[j].rating;
                    cands[n_cands].rd        = raw[j].rd;
                    cands[n_cands].witnesses = raw[j].witnesses;

                    /*
                     * The consensus verdict alone gates placement. Every other
                     * field is a lexicographic tie-break and cannot turn a
                     * refuted edge into a walked edge.
                     */
                    cands[n_cands].base = base;
                    n_cands++;
                }
                qsort(cands, n_cands, sizeof(RankedEdge), ranked_edge_cmp_score_desc);

                /*
                 * 3Cc trajectory-ordinal continuity: belief remains the
                 * primary key and continuity only breaks equal-belief ties.
                 * Probe the exact admission prefix through the complete tie
                 * at the beam boundary; a fixed beam multiplier can truncate
                 * a larger tie and silently choose the wrong members.
                 */
                if (ordinal_continuity_enabled)
                {
                    int shortlist_n = n_cands < beam ? n_cands : beam;

                    if (shortlist_n > 0)
                    {
                        double cutoff_base = cands[shortlist_n - 1].base;
                        while (shortlist_n < n_cands
                               && cands[shortlist_n].base == cutoff_base)
                            shortlist_n++;
                    }
                    for (int s = 0; s < shortlist_n; s++)
                        if (cands[s].base > 0.0) /* confirmed-only, same gate as 3Cc */
                        {
                            cands[s].continuity_known = ordinal_continuity_distance(
                                nodes[f].entity,
                                cands[s].object,
                                &cands[s].continuity_distance);
                        }
                    qsort(cands, n_cands, sizeof(RankedEdge), ranked_edge_cmp_score_desc);
                }

                for (int k = 0; k < n_cands && k < beam; k++)
                {
                    Datum mu;
                    hash128_t obj_id = datum_to_hash128(cands[k].object);
                    bool found3;

                    /*
                     * Scoring is signed (3Ca) so refutation is visible in
                     * ranking rather than silently dropped from
                     * consideration -- but a net-negative candidate must
                     * still never become a WALKED node, even when it's the
                     * only option at this step (caught live in regress,
                     * converse.sql's deliberate "syn_bad" refuted-edge
                     * fixture: a beam with nothing else available must dead-
                     * end, not walk into the refuted claim by default).
                     * cands[] is sorted belief-descending, so the first
                     * non-positive belief means every remaining candidate is
                     * also non-positive -- stop placing, don't just skip.
                     */
                    if (cands[k].base <= 0.0)
                        break;

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
    /* NULL means walk until the strongest chain ends or reaches an entity it
     * has already seen. The old implicit depth 8 truncated a cycle-safe greedy
     * chain for reasons unrelated to either the data or the caller. */
    max_depth = PG_ARGISNULL(2) ? PG_INT32_MAX : PG_GETARG_INT32(2);
    if (max_depth < 0)
        ereport(ERROR, (errmsg("walk_strongest: depth must be >= 0")));

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "walk_strongest: SPI_connect failed");
    ensure_edge_plan();

    /* Grow with the observed chain; do not reserve p_depth entities up front. */
    seen_cap = 8;
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

/*
 * ONE PREPARED LEVEL QUERY, WALKED IN C (was: realize.constituents_closure).
 *
 * The closure used to be a SQL function wrapping a WITH RECURSIVE, called once per
 * render. MEASURED 2026-08-07 on the foundation seed: 59 ms to return THREE rows,
 * and this is the most-called query in the system (1,496,183 calls / 19.6 h
 * cumulative). The breakdown says the recursion was never the problem:
 *
 *   planning                    ~30 ms   v_word_points is a view over partitioned
 *                                        physicalities, re-planned on EVERY call
 *   ST_NPoints in the sort key  ~11 ms   PostGIS per row, for a tiebreak that only
 *                                        5,063 entities in the substrate need
 *   the recursive term          ~5.4 ms  the part that looks like the cost
 *
 * A SQL function cannot fix the first line: an invoked SQL body is planned per
 * invocation. A prepared plan is planned ONCE per backend and executed with bound
 * parameters thereafter, which is what SPI_keepplan buys — so the walk moves here
 * and SQL is left holding one indexed level query.
 *
 * LEVEL-SYNCHRONOUS, NOT PER-NODE. Each iteration executes this once with the whole
 * frontier as a bound bytea[]; `= ANY($1)` is the index condition and prunes the
 * HASH sublevel per element. Depth for words is 2-3 levels, so a render costs a
 * handful of executions rather than one re-planned recursion — and never one query
 * per node, which is the RBAR shape the read law bans.
 *
 * The DISTINCT ON tiebreak is preserved exactly (physicality_id, then ST_NPoints
 * DESC): 5,063 entities carry two type-1 rows with the same (entity_id, type, id)
 * and different trajectories, and without the second key the PLAN decided which
 * survived — so batch size changed the answer. ST_NPoints prefers the finer
 * decomposition, the one that reaches the tier-0 floor and reconstructs the text.
 */
static const char *CLOSURE_QUERY =
    "SELECT parent_id, child_id, run_length, flags "
    "FROM realize.constituents_closure($1, $2) "
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
        SPIPlanPtr plan = SPI_prepare_cursor(CLOSURE_QUERY, 2, argtypes, CURSOR_OPT_PARALLEL_OK);
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

    if (max_depth == 0 || depth < max_depth)
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
    max_depth = PG_ARGISNULL(1) ? 0 : PG_GETARG_INT32(1);
    if (max_depth < 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("render_text: max_depth must be zero or positive")));

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
    max_depth = PG_ARGISNULL(1) ? 0 : PG_GETARG_INT32(1);
    if (max_depth < 0)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("render_text_batch: max_depth must be zero or positive")));

    if (ARR_NDIM(arr) == 0)
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(TEXTOID));
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
