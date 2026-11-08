/*
 * generate_walk.c — Beam-searched consensus branch walk (pg_laplace_walk_branches).
 */
#include "generate_walk_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_walk_branches);

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
    {
        InitMaterializedSRF(fcinfo, 0);
        return (Datum) 0;
    }
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
    ordinal_continuity_enabled = (PG_NARGS() > 6 && !PG_ARGISNULL(6)) ? PG_GETARG_BOOL(6) : false;
    use_geometry = (PG_NARGS() > 7 && !PG_ARGISNULL(7)) ? PG_GETARG_BOOL(7) : false;

    InitMaterializedSRF(fcinfo, 0);

    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "walk_branches: SPI_connect failed");
    SPIPlanPtr batch_plan = laplace_walk_get_batch_plan(use_geometry, have_intent_mask);

    cap = 1;
    nodes = (WalkNode *) palloc(sizeof(WalkNode) * cap);

    nodes[0].parent     = -1;
    nodes[0].depth      = 0;
    nodes[0].entity     = copy_bytea_datum(PointerGetDatum(prompt));
    nodes[0].rel_type   = (Datum) 0;
    nodes[0].eff_mu     = (Datum) 0;
    nodes[0].path_mu_fp = 0;
    nodes[0].witnesses  = 0;
    n_nodes = 1;

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

            rc = SPI_execute_plan(batch_plan, args, nulls, true, 0);
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

                raw[r].idx       = DatumGetInt64(SPI_getbinval(tup, td, 1, &isnull));
                raw[r].object    = copy_bytea_datum(SPI_getbinval(tup, td, 2, &isnull));
                if (raw[r].object != (Datum) 0)
                {
                    const laplace_relation_def_t *rel_def = NULL;
                    hash128_t object_id = datum_to_hash128(raw[r].object);

                    raw[r].object_is_relation_type =
                        laplace_relation_lookup(&object_id, &rel_def) == 0 && rel_def != NULL;
                }
                else
                {
                    raw[r].object_is_relation_type = true;
                }
                raw[r].rel_type  = copy_bytea_datum(SPI_getbinval(tup, td, 4, &isnull));
                raw[r].rating    = DatumGetInt64(SPI_getbinval(tup, td, 5, &isnull));
                raw[r].rd        = DatumGetInt64(SPI_getbinval(tup, td, 6, &isnull));
                raw[r].witnesses = DatumGetInt64(SPI_getbinval(tup, td, 7, &isnull));

                hm = SPI_getbinval(tup, td, 8, &isnull);
                raw[r].highway_mask = isnull ? (Datum) 0 : copy_bytea_datum(hm);

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
            qsort(raw, n_raw, sizeof(RawEdge), laplace_walk_raw_edge_cmp_idx);

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
                    hash128_t rel_type_id;
                    hash128_t obj_id;
                    bool      found2;
                    double    base;

                    if (raw[j].object == (Datum) 0 || raw[j].rel_type == (Datum) 0)
                        continue;
                    if (raw[j].object_is_relation_type)
                        continue;
                    obj_id = datum_to_hash128(raw[j].object);
                    hash_search(seen, &obj_id, HASH_FIND, &found2);
                    if (found2)
                        continue;
                    if (have_intent_mask && !laplace_walk_mask_overlaps(raw[j].highway_mask, &intent_mask))
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
                    cands[n_cands].base      = base;
                    n_cands++;
                }
                qsort(cands, n_cands, sizeof(RankedEdge), laplace_walk_ranked_edge_cmp_score_desc);

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
                        if (cands[s].base > 0.0)
                        {
                            cands[s].continuity_known = laplace_walk_ordinal_continuity_distance(
                                nodes[f].entity,
                                cands[s].object,
                                &cands[s].continuity_distance);
                        }
                    qsort(cands, n_cands, sizeof(RankedEdge), laplace_walk_ranked_edge_cmp_score_desc);
                }

                for (int k = 0; k < n_cands && k < beam; k++)
                {
                    Datum mu;
                    hash128_t obj_id = datum_to_hash128(cands[k].object);
                    bool found3;

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
            keys[i].depth      = nodes[i].depth;
            keys[i].path_mu_fp = nodes[i].path_mu_fp;
            keys[i].idx        = i;
        }
        qsort(keys, n_nodes, sizeof(WalkOrderKey), walk_order_cmp);

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
