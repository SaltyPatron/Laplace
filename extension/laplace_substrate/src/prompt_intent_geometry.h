#ifndef LAPLACE_PROMPT_INTENT_GEOMETRY_H
#define LAPLACE_PROMPT_INTENT_GEOMETRY_H

static inline int
laplace_prompt_hilbert_compare(const void *left, const void *right)
{
    const LaplacePromptHilbertCandidate *a = left, *b = right;
    int order = memcmp(a->delta, b->delta, sizeof(a->delta));
    return order ? order : memcmp(&a->id, &b->id, sizeof(hash128_t));
}

static inline void
laplace_prompt_hilbert_delta(const uint8 left[16], const uint8 right[16],
                             uint8 out[16])
{
    const uint8 *high = left, *low = right;
    int borrow = 0;
    if (memcmp(left, right, 16) < 0)
    {
        high = right;
        low = left;
    }
    for (int i = 15; i >= 0; --i)
    {
        int value = (int) high[i] - (int) low[i] - borrow;
        if (value < 0)
        {
            value += 256;
            borrow = 1;
        }
        else
            borrow = 0;
        out[i] = (uint8) value;
    }
}

static inline void
laplace_prompt_geometry_append(LaplacePromptIntent *intent,
                               const hash128_t *source, const hash128_t *id,
                               uint32 plane, uint32 rank,
                               const uint8 *hilbert_delta, double distance)
{
    if (plane < LAPLACE_PROMPT_GEOMETRY_HILBERT ||
        plane > LAPLACE_PROMPT_GEOMETRY_FRECHET ||
        rank == 0 ||
        (plane == LAPLACE_PROMPT_GEOMETRY_HILBERT && !hilbert_delta) ||
        (plane != LAPLACE_PROMPT_GEOMETRY_HILBERT &&
         (!isfinite(distance) || distance < 0.0)))
        elog(ERROR, "prompt geometry: invalid deterministic metric response");
    if (intent->geometry_count == intent->geometry_capacity)
    {
        int64 capacity = intent->geometry_capacity ?
            (int64) intent->geometry_capacity * 2 : 32;
        if (capacity > INT_MAX ||
            (Size) capacity > MaxAllocSize / sizeof(LaplacePromptGeometryCandidate))
            elog(ERROR, "prompt geometry: response set exceeds allocation capacity");
        intent->geometry = intent->geometry ?
            repalloc(intent->geometry,
                     sizeof(*intent->geometry) * (Size) capacity) :
            palloc(sizeof(*intent->geometry) * (Size) capacity);
        intent->geometry_capacity = (int) capacity;
    }
    LaplacePromptGeometryCandidate *target =
        &intent->geometry[intent->geometry_count++];
    MemSet(target, 0, sizeof(*target));
    target->source = *source;
    target->id = *id;
    target->plane = plane;
    target->rank = rank;
    target->distance = distance;
    if (hilbert_delta)
        memcpy(target->hilbert_delta, hilbert_delta, sizeof(target->hilbert_delta));
}

/* Geometry response of one anchor: an indexed S3 angular KNN over same-tier
 * points, then at most fanout equal, predecessor and successor Hilbert keys
 * each from the btree, ranked in C by exact unsigned Native-key delta. Two SPI
 * reads per anchor, whatever the candidate count. */
static inline void
laplace_prompt_geometry_scan_anchor(
    LaplacePromptIntent *intent, const hash128_t *source, uint32 node,
    int fanout, SPIPlanPtr angular_plan, SPIPlanPtr hilbert_plan,
    hash128_t *root_angular, int *root_angular_count)
{
    const double *coords = tier_tree_coord_array(intent->input->tree);
    const uint8 *tiers = tier_tree_tier_array(intent->input->tree);
    Datum source_datum = hash128_to_datum(source);
    Datum angular_args[7] = {
        Float8GetDatum(coords[(Size) node * 4 + 0]),
        Float8GetDatum(coords[(Size) node * 4 + 1]),
        Float8GetDatum(coords[(Size) node * 4 + 2]),
        Float8GetDatum(coords[(Size) node * 4 + 3]),
        source_datum,
        Int32GetDatum(fanout),
        Int16GetDatum((int16) tiers[node])
    };
    int rc = SPI_execute_plan(angular_plan, angular_args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "prompt geometry: angular KNN failed: %s",
             SPI_result_code_string(rc));
    for (uint64 row = 0; row < SPI_processed; ++row)
    {
        bool id_null, distance_null;
        Datum id_value = SPI_getbinval(
            SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 1, &id_null);
        Datum distance_value = SPI_getbinval(
            SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 2, &distance_null);
        if (id_null || distance_null) continue;
        hash128_t id = datum_to_hash128(id_value);
        double distance = DatumGetFloat8(distance_value);
        laplace_prompt_geometry_append(
            intent, source, &id, LAPLACE_PROMPT_GEOMETRY_ANGULAR,
            (uint32) row + 1u, NULL, distance);
        if (root_angular && root_angular_count &&
            hash128_eq(source, &intent->root) && *root_angular_count < fanout)
            root_angular[(*root_angular_count)++] = id;
    }
    SPI_freetuptable(SPI_tuptable);

    /* The stored Native key has 64 bits. Project the anchor with that same
     * owner instead of comparing it to the retired 128-bit key encoding. */
    rc = SPI_execute_plan(hilbert_plan, angular_args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "prompt geometry: Hilbert locality read failed: %s",
             SPI_result_code_string(rc));
    if (SPI_processed > (uint64) INT_MAX ||
        SPI_processed > (uint64) (MaxAllocSize / sizeof(LaplacePromptHilbertCandidate)))
        elog(ERROR, "prompt geometry: Hilbert candidate set exceeds allocation capacity");
    int hilbert_count = (int) SPI_processed;
    LaplacePromptHilbertCandidate *ordered = hilbert_count > 0 ?
        palloc(sizeof(*ordered) * (Size) hilbert_count) : NULL;
    for (int row = 0; row < hilbert_count; ++row)
    {
        bool id_null, key_null;
        Datum id_value = SPI_getbinval(
            SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 1, &id_null);
        Datum key_value = SPI_getbinval(
            SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 2, &key_null);
        if (id_null || key_null)
            elog(ERROR, "prompt geometry: indexed Hilbert row contains NULL");
        bool anchor_null;
        Datum anchor_value = SPI_getbinval(SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 3, &anchor_null);
        bytea *key = DatumGetByteaPP(key_value);
        bytea *anchor = anchor_null ? NULL : DatumGetByteaPP(anchor_value);
        if (VARSIZE_ANY_EXHDR(key) != 8 || !anchor || VARSIZE_ANY_EXHDR(anchor) != 8)
            elog(ERROR, "prompt geometry: Native Hilbert key is not 64 bits");
        uint8 left[16] = {0}, right[16] = {0};
        memcpy(left + 8, VARDATA_ANY(anchor), 8);
        memcpy(right + 8, VARDATA_ANY(key), 8);
        ordered[row].id = datum_to_hash128(id_value);
        laplace_prompt_hilbert_delta(left, right, ordered[row].delta);
    }
    if (hilbert_count > 1)
        qsort(ordered, hilbert_count, sizeof(*ordered), laplace_prompt_hilbert_compare);
    for (int row = 0; row < Min(hilbert_count, fanout); ++row)
        laplace_prompt_geometry_append(
            intent, source, &ordered[row].id, LAPLACE_PROMPT_GEOMETRY_HILBERT,
            (uint32) row + 1u, ordered[row].delta, 0.0);
    SPI_freetuptable(SPI_tuptable);
    if (ordered) pfree(ordered);
    pfree(DatumGetPointer(source_datum));
}

static inline void
laplace_prompt_geometry_shape(LaplacePromptIntent *intent, uint32 root_node,
                              int fanout, SPIPlanPtr frechet_plan,
                              const hash128_t *candidates, int candidate_count)
{
    if (!frechet_plan || candidate_count <= 0 || fanout <= 0)
        return;
    size_t node_count = tier_tree_node_count(intent->input->tree);
    const uint32 *first_children =
        tier_tree_first_child_idx_array(intent->input->tree);
    const uint32 *child_counts =
        tier_tree_child_count_array(intent->input->tree);
    const double *coords = tier_tree_coord_array(intent->input->tree);
    if ((size_t) root_node >= node_count)
        elog(ERROR, "prompt geometry: root curve node is outside canonical tree");
    uint32 first_child = first_children[root_node];
    uint32 child_count = child_counts[root_node];
    if (child_count < 2)
        return;
    if (first_child == TIER_TREE_INVALID ||
        (size_t) first_child + (size_t) child_count > node_count ||
        (Size) child_count > MaxAllocSize / sizeof(Datum))
        elog(ERROR, "prompt geometry: realized root curve is invalid");

    /* One point per immediate ordered constituent of the root: the level
     * structural.entity_curve realizes for each candidate. Points from another
     * composition level would not give a Frechet distance over one physicality. */
    int point_count = (int) child_count;
    Datum *x = palloc(sizeof(Datum) * point_count);
    Datum *y = palloc(sizeof(Datum) * point_count);
    Datum *z = palloc(sizeof(Datum) * point_count);
    Datum *m = palloc(sizeof(Datum) * point_count);
    for (int i = 0; i < point_count; ++i)
    {
        Size base = ((Size) first_child + (Size) i) * 4;
        x[i] = Float8GetDatum(coords[base + 0]);
        y[i] = Float8GetDatum(coords[base + 1]);
        z[i] = Float8GetDatum(coords[base + 2]);
        m[i] = Float8GetDatum(coords[base + 3]);
    }
    Datum *ids = palloc(sizeof(Datum) * candidate_count);
    for (int i = 0; i < candidate_count; ++i)
        ids[i] = hash128_to_datum(&candidates[i]);

    ArrayType *xa = construct_array(x, point_count, FLOAT8OID,
                                    sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE);
    ArrayType *ya = construct_array(y, point_count, FLOAT8OID,
                                    sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE);
    ArrayType *za = construct_array(z, point_count, FLOAT8OID,
                                    sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE);
    ArrayType *ma = construct_array(m, point_count, FLOAT8OID,
                                    sizeof(float8), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE);
    ArrayType *candidate_array = construct_array(
        ids, candidate_count, BYTEAOID, -1, false, TYPALIGN_INT);
    Datum args[6] = {
        PointerGetDatum(xa), PointerGetDatum(ya), PointerGetDatum(za),
        PointerGetDatum(ma), PointerGetDatum(candidate_array), Int32GetDatum(fanout)
    };
    int rc = SPI_execute_plan(frechet_plan, args, NULL, true, 0);
    if (rc != SPI_OK_SELECT)
        elog(ERROR, "prompt geometry: Frechet refinement failed: %s",
             SPI_result_code_string(rc));
    for (uint64 row = 0; row < SPI_processed; ++row)
    {
        bool id_null, distance_null;
        Datum id_value = SPI_getbinval(
            SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 1, &id_null);
        Datum distance_value = SPI_getbinval(
            SPI_tuptable->vals[row], SPI_tuptable->tupdesc, 2, &distance_null);
        if (id_null || distance_null) continue;
        hash128_t id = datum_to_hash128(id_value);
        laplace_prompt_geometry_append(
            intent, &intent->root, &id, LAPLACE_PROMPT_GEOMETRY_FRECHET,
            (uint32) row + 1u, NULL, DatumGetFloat8(distance_value));
    }
    SPI_freetuptable(SPI_tuptable);

    for (int i = 0; i < candidate_count; ++i)
        pfree(DatumGetPointer(ids[i]));
    pfree(candidate_array); pfree(xa); pfree(ya); pfree(za); pfree(ma);
    pfree(ids); pfree(x); pfree(y); pfree(z); pfree(m);
}

static inline void
laplace_prompt_geometry_couple(LaplacePromptIntent *intent, int fanout)
{
    MemoryContext previous = MemoryContextSwitchTo(intent->owner);
    intent->geometry_frontier = construct_empty_array(BYTEAOID);
    if (fanout <= 0)
    {
        MemoryContextSwitchTo(previous);
        return;
    }

    const char *angular_sql = laplace_sql_query_text("geometry.prompt.angular");
    const char *hilbert_sql = laplace_sql_query_text("geometry.prompt.hilbert");
    const char *frechet_sql = laplace_sql_query_text("geometry.prompt.frechet");
    Oid angular_types[7] = {
        FLOAT8OID,FLOAT8OID,FLOAT8OID,FLOAT8OID,BYTEAOID,INT4OID,INT2OID};
    Oid frechet_types[6] = {
        FLOAT8ARRAYOID,FLOAT8ARRAYOID,FLOAT8ARRAYOID,FLOAT8ARRAYOID,
        BYTEAARRAYOID,INT4OID};
    static SPIPlanPtr angular_plan = NULL;
    static SPIPlanPtr hilbert_plan = NULL;
    static SPIPlanPtr frechet_plan = NULL;
    if (!angular_plan || !hilbert_plan || !frechet_plan)
    {
        /* Retain parameterized plans using PostgreSQL's normal plan policy.
         * Forcing custom plans here prevents automatic reuse of a generic plan
         * and replans the partitioned geometry joins on every prompt in auto mode. */
        SPIPlanPtr angular = SPI_prepare_cursor(
            angular_sql, 7, angular_types,
            CURSOR_OPT_PARALLEL_OK);
        SPIPlanPtr hilbert = SPI_prepare_cursor(
            hilbert_sql, 7, angular_types,
            CURSOR_OPT_PARALLEL_OK);
        SPIPlanPtr frechet = SPI_prepare_cursor(
            frechet_sql, 6, frechet_types,
            CURSOR_OPT_PARALLEL_OK);
        if (!angular || !hilbert || !frechet ||
            SPI_keepplan(angular) != 0 ||
            SPI_keepplan(hilbert) != 0 ||
            SPI_keepplan(frechet) != 0)
            elog(ERROR, "prompt geometry: cannot retain indexed response plans");
        angular_plan = angular;
        hilbert_plan = hilbert;
        frechet_plan = frechet;
    }

    size_t tree_nodes = tier_tree_node_count(intent->input->tree);
    const hash128_t *tree_ids = tier_tree_id_array(intent->input->tree);
    uint32 root_node = TIER_TREE_INVALID;
    for (size_t node = 0; node < tree_nodes; ++node)
        if (hash128_eq(&tree_ids[node], &intent->root))
        {
            root_node = (uint32) node;
            break;
        }
    if (root_node == TIER_TREE_INVALID)
        elog(ERROR, "prompt geometry: admitted root is absent from canonical tree");

    /* The root carries every current occurrence as provenance, so geometry
     * reads once per metric plane from the root, not once per occurrence. */
    if ((Size) fanout > MaxAllocSize / sizeof(hash128_t))
        elog(ERROR, "prompt geometry: fanout exceeds allocation capacity");
    hash128_t *root_angular = palloc(sizeof(hash128_t) * (Size) fanout);
    int root_angular_count = 0;
    laplace_prompt_geometry_scan_anchor(
        intent, &intent->root, root_node, fanout, angular_plan, hilbert_plan,
        root_angular, &root_angular_count);

    /* Frechet refinement ranks only the root's angular KNN population, over
     * each candidate's realized curve rather than its packed trajectory values. */
    laplace_prompt_geometry_shape(
        intent, root_node, fanout, frechet_plan,
        root_angular, root_angular_count);
    pfree(root_angular);

    if (intent->geometry_count > 0)
    {
        HASHCTL seen_ctl = {0};
        seen_ctl.keysize = sizeof(hash128_t);
        seen_ctl.entrysize = sizeof(hash128_t);
        seen_ctl.hcxt = intent->owner;
        HTAB *frontier_seen = hash_create(
            "prompt geometry frontier identities", Max(intent->geometry_count, 16),
            &seen_ctl, HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
        ArrayBuildState *frontier = NULL;
        for (int i = 0; i < intent->geometry_count; ++i)
        {
            const LaplacePromptGeometryCandidate *candidate = &intent->geometry[i];
            bool frontier_found;
            hash_search(frontier_seen, &candidate->id, HASH_ENTER, &frontier_found);
            if (!frontier_found)
            {
                Datum id = hash128_to_datum(&candidate->id);
                frontier = accumArrayResult(
                    frontier, id, false, BYTEAOID, intent->owner);
                pfree(DatumGetPointer(id));
            }
        }
        hash_destroy(frontier_seen);
        if (frontier)
        {
            pfree(intent->geometry_frontier);
            intent->geometry_frontier =
                DatumGetArrayTypeP(makeArrayResult(frontier, intent->owner));
        }
    }
    MemoryContextSwitchTo(previous);
}

#endif
