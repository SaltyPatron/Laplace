#include "forward_respond_internal.h"

PG_FUNCTION_INFO_V1(pg_laplace_content_terms);
PG_FUNCTION_INFO_V1(pg_laplace_firmware);

/*
 * converse.content_terms(ids): the ids, deduplicated in order, that have a
 * positively standing producer-declared binding and whose highest-standing
 * canonical UPOS value, if any, is a content tag. Both reads acquire sets.
 */
typedef struct
{
    hash128_t id;
    bool has_meaning;
    bool has_upos;
    hash128_t upos;
    __int128 upos_standing;
} TermEvidence;

typedef struct
{
    HTAB *evidence;
} TermRead;

static void
receive_term_evidence(const LaplaceConsensusRow *row, void *context)
{
    TermRead *read = context;
    TermEvidence *entry = hash_search(read->evidence, &row->subject, HASH_FIND, NULL);
    if (!entry || row->object_is_null || !(laplace_walk_edge_weight(row->rating, row->rd) > 0.0))
        return;
    entry->has_meaning = true;
}

static void
receive_term_upos(const LaplaceClassificationRow *row, void *context)
{
    TermRead *read = context;
    TermEvidence *entry = hash_search(read->evidence, &row->subject, HASH_FIND, NULL);
    if (!entry || !(laplace_walk_edge_weight(row->rating, row->rd) > 0.0))
        return;
    {
        __int128 standing = (__int128) row->rating - 2 * (__int128) row->rd;
        if (!entry->has_upos || standing > entry->upos_standing ||
            (standing == entry->upos_standing && memcmp(&row->value, &entry->upos, sizeof(hash128_t)) < 0))
        {
            entry->has_upos = true;
            entry->upos = row->value;
            entry->upos_standing = standing;
        }
    }
}

Datum
pg_laplace_content_terms(PG_FUNCTION_ARGS)
{
    Datum *values;
    bool *nulls;
    int n;
    HASHCTL ctl = {0};
    TermRead read;
    hash128_t *ids;
    int n_ids = 0;
    ArrayBuildState *out = NULL;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    deconstruct_array(PG_GETARG_ARRAYTYPE_P(0), BYTEAOID, -1, false, TYPALIGN_INT,
                      &values, &nulls, &n);
    ctl.keysize = sizeof(hash128_t);
    ctl.entrysize = sizeof(TermEvidence);
    ctl.hcxt = CurrentMemoryContext;
    read.evidence = hash_create("content term evidence", Max(n, 8), &ctl,
                                HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);
    ids = palloc(sizeof(hash128_t) * Max(n, 1));
    for (int i = 0; i < n; ++i)
    {
        bool found;
        hash128_t id;
        if (nulls[i]) continue;
        id = datum_to_hash128(values[i]);
        TermEvidence *entry = hash_search(read.evidence, &id, HASH_ENTER, &found);
        if (found) continue;
        entry->has_meaning = entry->has_upos = false;
        ids[n_ids++] = id;
    }
    if (n_ids > 0)
    {
        bool spi_top = false;
        ArrayType *subjects = hash128_array_from_ids(ids, n_ids);
        int n_bound = 0;
        LaplaceNeighbor *bound;
        if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
            elog(ERROR, "content_terms: SPI connect failed");
        laplace_consensus_candidates(subjects, NULL, NULL, receive_term_evidence, &read, NULL);
        bound = laplace_consensus_neighbors(subjects, respond_relation_set("KEY_BINDING"), 4,
                                            false, true, true, &n_bound, NULL);
        for (int r = 0; r < n_bound; ++r)
        {
            TermEvidence *entry;
            if (!bound[r].outbound) continue;
            if (!(laplace_walk_edge_weight(bound[r].rating, bound[r].rd) > 0.0)) continue;
            entry = hash_search(read.evidence, &bound[r].frontier, HASH_FIND, NULL);
            if (entry) entry->has_meaning = true;
        }
        laplace_classification_scan(subjects, "upos", receive_term_upos, &read);
        laplace_spi_finish(spi_top);
    }
    for (int i = 0; i < n_ids; ++i)
    {
        TermEvidence *entry = hash_search(read.evidence, &ids[i], HASH_FIND, NULL);
        if (!entry->has_meaning) continue;
        if (entry->has_upos && !laplace_upos_is_content(&entry->upos)) continue;
        Datum id = hash128_to_datum(&ids[i]);
        out = accumArrayResult(out, id, false, BYTEAOID, CurrentMemoryContext);
    }
    if (!out)
        PG_RETURN_ARRAYTYPE_P(construct_empty_array(BYTEAOID));
    PG_RETURN_DATUM(makeArrayResult(out, CurrentMemoryContext));
}

/* converse.firmware(name): a governed firmware image's compute coordinates and
 * content id, so orchestration passes the image's values instead of literals.
 * NULL name = default image. */
Datum
pg_laplace_firmware(PG_FUNCTION_ARGS)
{
    const laplace_firmware_image_t *image = PG_ARGISNULL(0)
        ? laplace_firmware_default()
        : laplace_firmware_lookup(text_to_cstring(PG_GETARG_TEXT_PP(0)));
    TupleDesc desc;
    Datum values[15];
    bool nulls[15] = {false};
    hash128_t id;
    if (!image)
        ereport(ERROR, (errmsg("firmware: no governed image of that name")));
    if (get_call_result_type(fcinfo, NULL, &desc) != TYPEFUNC_COMPOSITE)
        elog(ERROR, "firmware: composite result required");
    laplace_firmware_id(image, &id);
    values[0] = hash128_to_datum(&id);
    values[1] = CStringGetTextDatum(image->name);
    values[2] = Float8GetDatum(image->salience_floor);
    values[3] = Int32GetDatum(image->semantic_hops);
    values[4] = Int32GetDatum(image->fanout);
    values[5] = Int32GetDatum(image->meeting_hops);
    values[6] = Int32GetDatum(image->meeting_fanout);
    values[7] = Int32GetDatum(image->meeting_frontier);
    values[8] = Int32GetDatum(image->sharing_window);
    values[9] = Int32GetDatum(image->sharing_cap);
    values[10] = Float8GetDatum(image->spread);
    values[11] = Int32GetDatum(image->top_k);
    values[12] = Int32GetDatum(image->steps);
    values[13] = Int32GetDatum(image->max_stride);
    values[14] = Int32GetDatum((int32) image->version);
    PG_RETURN_DATUM(HeapTupleGetDatum(heap_form_tuple(BlessTupleDesc(desc), values, nulls)));
}
