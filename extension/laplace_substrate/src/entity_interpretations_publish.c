#include "postgres.h"
#include "fmgr.h"
#include "executor/spi.h"
#include "utils/array.h"

#include "laplace/core/sql_catalog.h"
#include "descent_probe.h"

PG_FUNCTION_INFO_V1(pg_laplace_entity_interpretations_publish);

typedef struct InterpretationRow
{
    unsigned char entity[16];
    unsigned char type[16];
    unsigned char source[16];
    int16 tier;
    bool source_null;
} InterpretationRow;

static void
read_id(Datum value, unsigned char out[16], const char *name)
{
    bytea *bytes = DatumGetByteaPP(value);
    if (VARSIZE_ANY_EXHDR(bytes) != 16)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("entity_interpretations_publish: %s must be exactly 16 bytes", name)));
    memcpy(out, VARDATA_ANY(bytes), 16);
}

static Datum
id_datum(const unsigned char value[16])
{
    bytea *bytes = palloc(VARHDRSZ + 16);
    SET_VARSIZE(bytes, VARHDRSZ + 16);
    memcpy(VARDATA(bytes), value, 16);
    return PointerGetDatum(bytes);
}

static int
compare_rows(const void *left_value, const void *right_value)
{
    const InterpretationRow *left = left_value;
    const InterpretationRow *right = right_value;
    int order = memcmp(left->entity, right->entity, 16);
    if (order != 0) return order;
    if (left->tier != right->tier) return left->tier < right->tier ? -1 : 1;
    order = memcmp(left->type, right->type, 16);
    if (order != 0) return order;
    if (left->source_null != right->source_null)
        return left->source_null ? 1 : -1;
    return left->source_null ? 0 : memcmp(left->source, right->source, 16);
}

static bool
same_key(const InterpretationRow *left, const InterpretationRow *right)
{
    return left->tier == right->tier &&
           memcmp(left->entity, right->entity, 16) == 0 &&
           memcmp(left->type, right->type, 16) == 0;
}

static ArrayType *
bytea_array(Datum *values, int count)
{
    return construct_array(values, count, BYTEAOID, -1, false, 'i');
}

static SPIPlanPtr
kept_plan(SPIPlanPtr *slot, const char *key)
{
    static const Oid types[5] =
        {BYTEAARRAYOID, INT2ARRAYOID, BYTEAARRAYOID, BYTEAARRAYOID, BOOLARRAYOID};
    if (*slot == NULL)
    {
        *slot = SPI_prepare(laplace_sql_query_text(key), 5, (Oid *) types);
        if (*slot == NULL || SPI_keepplan(*slot) != 0)
            elog(ERROR, "entity_interpretations_publish: cannot prepare %s", key);
    }
    return *slot;
}

Datum
pg_laplace_entity_interpretations_publish(PG_FUNCTION_ARGS)
{
    Datum *input[5];
    bool *nulls[5];
    int counts[5];
    const Oid element_types[5] = {BYTEAOID, INT2OID, BYTEAOID, BYTEAOID, BOOLOID};
    const int16 lengths[5] = {-1, 2, -1, -1, 1};
    const bool by_value[5] = {false, true, false, false, true};
    const char alignments[5] = {'i', 's', 'i', 'i', 'c'};
    int count;
    bool any_null = false;
    bool all_null = true;

    for (int arg = 0; arg < 5; ++arg)
    {
        any_null |= PG_ARGISNULL(arg);
        all_null &= PG_ARGISNULL(arg);
    }
    if (all_null) PG_RETURN_BOOL(false);
    if (any_null)
        ereport(ERROR,
                (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                 errmsg("entity interpretation arrays must have identical cardinality")));

    for (int arg = 0; arg < 5; ++arg)
    {
        ArrayType *array = PG_GETARG_ARRAYTYPE_P(arg);
        if (ARR_NDIM(array) > 1 || ARR_ELEMTYPE(array) != element_types[arg])
            ereport(ERROR,
                    (errcode(ERRCODE_DATATYPE_MISMATCH),
                     errmsg("entity_interpretations_publish: invalid array argument %d", arg + 1)));
        deconstruct_array(array, element_types[arg], lengths[arg], by_value[arg], alignments[arg],
                          &input[arg], &nulls[arg], &counts[arg]);
    }
    count = counts[0];
    for (int arg = 1; arg < 5; ++arg)
        if (counts[arg] != count)
            ereport(ERROR,
                    (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                     errmsg("entity interpretation arrays must have identical cardinality")));
    if (count == 0) PG_RETURN_BOOL(false);

    InterpretationRow *rows = palloc(sizeof(*rows) * count);
    bool sorted = true;
    for (int i = 0; i < count; ++i)
    {
        if (nulls[0][i] || nulls[1][i] || nulls[2][i] || nulls[4][i])
            ereport(ERROR,
                    (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                     errmsg("entity_interpretations_publish: identity, tier, type and null flag must not be NULL")));
        read_id(input[0][i], rows[i].entity, "entity id");
        rows[i].tier = DatumGetInt16(input[1][i]);
        read_id(input[2][i], rows[i].type, "type id");
        rows[i].source_null = DatumGetBool(input[4][i]);
        memset(rows[i].source, 0, 16);
        if (!rows[i].source_null)
        {
            if (nulls[3][i])
                ereport(ERROR,
                        (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
                         errmsg("entity_interpretations_publish: non-null source is absent")));
            read_id(input[3][i], rows[i].source, "source id");
        }
        if (i > 0 && compare_rows(&rows[i - 1], &rows[i]) > 0)
            sorted = false;
    }
    if (!sorted)
        qsort(rows, count, sizeof(*rows), compare_rows);

    int unique_count = 0;
    for (int i = 0; i < count; ++i)
        if (unique_count == 0 || !same_key(&rows[i], &rows[unique_count - 1]))
            rows[unique_count++] = rows[i];

    int entity_count = 0;
    for (int i = 0; i < unique_count; ++i)
        if (i == 0 || memcmp(rows[i].entity, rows[i - 1].entity, 16) != 0)
            ++entity_count;

    Datum *facet_ids = palloc(sizeof(Datum) * unique_count);
    Datum *facet_tiers = palloc(sizeof(Datum) * unique_count);
    Datum *facet_types = palloc(sizeof(Datum) * unique_count);
    Datum *facet_sources = palloc(sizeof(Datum) * unique_count);
    Datum *facet_source_nulls = palloc(sizeof(Datum) * unique_count);
    Datum *entity_ids = palloc(sizeof(Datum) * entity_count);
    Datum *entity_tiers = palloc(sizeof(Datum) * entity_count);
    Datum *entity_types = palloc(sizeof(Datum) * entity_count);
    Datum *entity_sources = palloc(sizeof(Datum) * entity_count);
    Datum *entity_source_nulls = palloc(sizeof(Datum) * entity_count);

    int entity_at = 0;
    for (int i = 0; i < unique_count; ++i)
    {
        facet_ids[i] = id_datum(rows[i].entity);
        facet_tiers[i] = Int16GetDatum(rows[i].tier);
        facet_types[i] = id_datum(rows[i].type);
        facet_sources[i] = id_datum(rows[i].source);
        facet_source_nulls[i] = BoolGetDatum(rows[i].source_null);
        if (i == 0 || memcmp(rows[i].entity, rows[i - 1].entity, 16) != 0)
        {
            int end = i + 1;
            int16 min_tier = rows[i].tier;
            unsigned char min_type[16];
            unsigned char min_source[16] = {0};
            bool source_null = true;
            memcpy(min_type, rows[i].type, 16);
            while (end < unique_count && memcmp(rows[i].entity, rows[end].entity, 16) == 0)
                ++end;
            for (int j = i; j < end; ++j)
            {
                /*
                 * Tier is the canonical floor. type_id describes that floor; it
                 * is not an independent semantic facet. Never combine the lowest
                 * tier with a type observed only at a higher tier.
                 *
                 * rows are sorted by (entity,tier,type,...), so the first tier is
                 * already the minimum. At that floor only, pick a deterministic
                 * type when duplicate structural observations disagree.
                 */
                if (rows[j].tier == min_tier &&
                    memcmp(rows[j].type, min_type, 16) < 0)
                    memcpy(min_type, rows[j].type, 16);
                if (!rows[j].source_null &&
                    (source_null || memcmp(rows[j].source, min_source, 16) < 0))
                {
                    memcpy(min_source, rows[j].source, 16);
                    source_null = false;
                }
            }
            entity_ids[entity_at] = id_datum(rows[i].entity);
            entity_tiers[entity_at] = Int16GetDatum(min_tier);
            entity_types[entity_at] = id_datum(min_type);
            entity_sources[entity_at] = id_datum(min_source);
            entity_source_nulls[entity_at] = BoolGetDatum(source_null);
            ++entity_at;
        }
    }

    ArrayType *entity_id_array = bytea_array(entity_ids, entity_count);
    uint8_t *present = palloc0((entity_count + 7) / 8);
    int presence_result = laplace_entities_stored_bitmap(entity_id_array, present, entity_count);
    if (presence_result != SPI_OK_SELECT)
        elog(ERROR, "entity_interpretations_publish: stored identity probe failed");
    for (int i = 0; i < entity_count; ++i)
        if ((present[i >> 3] & (1u << (i & 7u))) == 0)
            PG_RETURN_BOOL(true);

    Datum facet_args[5] = {
        PointerGetDatum(bytea_array(facet_ids, unique_count)),
        PointerGetDatum(construct_array(facet_tiers, unique_count, INT2OID, 2, true, 's')),
        PointerGetDatum(bytea_array(facet_types, unique_count)),
        PointerGetDatum(bytea_array(facet_sources, unique_count)),
        PointerGetDatum(construct_array(facet_source_nulls, unique_count, BOOLOID, 1, true, 'c'))};
    Datum entity_args[5] = {
        PointerGetDatum(entity_id_array),
        PointerGetDatum(construct_array(entity_tiers, entity_count, INT2OID, 2, true, 's')),
        PointerGetDatum(bytea_array(entity_types, entity_count)),
        PointerGetDatum(bytea_array(entity_sources, entity_count)),
        PointerGetDatum(construct_array(entity_source_nulls, entity_count, BOOLOID, 1, true, 'c'))};
    static SPIPlanPtr merge_plan;
    static SPIPlanPtr summary_plan;
    if (SPI_connect() != SPI_OK_CONNECT)
        elog(ERROR, "entity_interpretations_publish: SPI_connect failed");
    int rc = SPI_execute_plan(kept_plan(&merge_plan, "identity.entity_interpretations_merge"),
                              facet_args, NULL, false, 0);
    if (rc != SPI_OK_MERGE)
        elog(ERROR, "entity_interpretations_publish: facet merge failed: %s",
             SPI_result_code_string(rc));
    rc = SPI_execute_plan(kept_plan(&summary_plan, "identity.entity_summary_update"),
                          entity_args, NULL, false, 0);
    if (rc != SPI_OK_UPDATE)
        elog(ERROR, "entity_interpretations_publish: entity summary update failed: %s",
             SPI_result_code_string(rc));
    SPI_finish();
    PG_RETURN_BOOL(false);
}
