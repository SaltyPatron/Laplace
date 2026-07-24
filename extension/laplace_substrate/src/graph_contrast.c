




#include "postgres.h"

#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/numeric.h"

#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "graph_taxonomy.h"

/* Initial sizing only — the fact table grows as needed. The old fixed cap
 * hard-errored ("contrast row cap exceeded") once hub words' ancestor sets
 * outgrew it at live scale. */
#define CONTRAST_FEAT_INITIAL 512

PG_FUNCTION_INFO_V1(pg_laplace_contrast);

/*
 * consensus_subject_edges is prepared once (static) and re-executed per
 * synset/anchor subject instead of re-planning on every call. Same query,
 * same single bytea argument, same rows read back -- output is unchanged.
 */
static SPIPlanPtr subject_edges_plan = NULL;

static void
ensure_subject_edges_plan(void)
{
    if (subject_edges_plan == NULL)
    {
        Oid        argtypes[1] = { BYTEAOID };
        SPIPlanPtr plan = SPI_prepare(
            "SELECT type_id, object_id, rating, rd "
            "FROM laplace.consensus_subject_edges($1)",
            1, argtypes);
        if (plan == NULL)
            elog(ERROR, "contrast: SPI_prepare failed: %s",
                 SPI_result_code_string(SPI_result));
        if (SPI_keepplan(plan) != 0)
            elog(ERROR, "contrast: SPI_keepplan failed");
        subject_edges_plan = plan;
    }
}

typedef struct {
    hash128_t type_id;
    hash128_t object_id;
    Datum     mu;
    bool      from_x;
    bool      from_y;
} ContrastRow;

/* (type_id, object_id) -> row-index map, replacing the O(n) linear scan that
 * turned hub-word fact accumulation (10^4 rows post-cap-removal) into an
 * O(n^2) pass. Same first-wins index the scan returned; output unchanged. */
typedef struct ContrastIdxEntry
{
    char key[32];
    int  idx;
} ContrastIdxEntry;

static int
contrast_idx_find(HTAB *map, const hash128_t *tid, const hash128_t *oid)
{
    char key[32];
    bool found;
    ContrastIdxEntry *e;

    memcpy(key, tid, 16);
    memcpy(key + 16, oid, 16);
    e = (ContrastIdxEntry *) hash_search(map, key, HASH_FIND, &found);
    return found ? e->idx : -1;
}

static void
contrast_idx_add(HTAB *map, const hash128_t *tid, const hash128_t *oid, int idx)
{
    char key[32];
    bool found;
    ContrastIdxEntry *e;

    memcpy(key, tid, 16);
    memcpy(key + 16, oid, 16);
    e = (ContrastIdxEntry *) hash_search(map, key, HASH_ENTER, &found);
    e->idx = idx;
}

static void
contrast_add_fact(HTAB *rowmap, ContrastRow **rows_io, int *n, int *cap,
                  const hash128_t *tid, const hash128_t *oid, Datum mu, bool from_x)
{
    ContrastRow *rows = *rows_io;
    int idx = contrast_idx_find(rowmap, tid, oid);
    if (idx < 0)
    {
        if (*n >= *cap)
        {
            *cap *= 2;
            rows = (ContrastRow *) repalloc(rows, sizeof(ContrastRow) * *cap);
            *rows_io = rows;
        }
        rows[*n].type_id = *tid;
        rows[*n].object_id = *oid;
        rows[*n].mu = mu;
        rows[*n].from_x = from_x;
        rows[*n].from_y = !from_x;
        contrast_idx_add(rowmap, tid, oid, *n);
        (*n)++;
        return;
    }
    if (from_x)
        rows[idx].from_x = true;
    else
        rows[idx].from_y = true;
    if (mu != (Datum) 0 && (rows[idx].mu == (Datum) 0 ||
        DatumGetInt32(DirectFunctionCall2(numeric_cmp, mu, rows[idx].mu)) > 0))
        rows[idx].mu = mu;
}

/* left(encode(id,'hex'),16) — the display convention every other read surface
 * uses as the genuine last resort, so every claim still shows even when
 * realize_batch/type_label cannot produce a label. The serving contract is
 * non-null type/fact (SubstrateClient.TapeAsync reads them unguarded). */
static Datum
hash128_hex16_text(const hash128_t *h)
{
    bytea *b = DatumGetByteaP(hash128_to_datum(h));
    const unsigned char *d = (const unsigned char *) VARDATA(b);
    static const char digits[] = "0123456789abcdef";
    char hex[17];

    for (int i = 0; i < 8; i++)
    {
        hex[i * 2]     = digits[d[i] >> 4];
        hex[i * 2 + 1] = digits[d[i] & 0xf];
    }
    hex[16] = '\0';
    return CStringGetTextDatum(hex);
}

static bool
contrast_type_allowed(const hash128_t *type_id, const hash128_t *feat_types, int feat_n)
{
    int in_family = 0;
    for (int i = 0; i < feat_n; i++)
        if (hash128_eq(type_id, &feat_types[i]))
            return true;
    if (laplace_relation_in_family(type_id, "HAS_POS", &in_family) == 0 && in_family)
        return true;
    return false;
}

Datum
pg_laplace_contrast(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          x, y, lang;
    int32          lim;
    hash128_t      up_types[2];
    hash128_t      feat_types[9];
    int            feat_n = 9;
    TaxNode       *ax, *ay;
    int            n_ax, n_ay;
    ContrastRow   *rows;
    int            n_rows = 0;
    int            emitted = 0;

    if (PG_ARGISNULL(0) || PG_ARGISNULL(1))
        ereport(ERROR, (errmsg("contrast: endpoints must not be NULL")));
    x = PG_GETARG_DATUM(0);
    y = PG_GETARG_DATUM(1);
    lang = PG_ARGISNULL(2) ? (Datum) 0 : PG_GETARG_DATUM(2);
    lim = PG_ARGISNULL(3) ? 80 : PG_GETARG_INT32(3);

    InitMaterializedSRF(fcinfo, 0);

    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "contrast: SPI_connect failed");
    ensure_subject_edges_plan();

    up_types[0] = rel_type_id("IS_A");
    up_types[1] = rel_type_id("IS_INSTANCE_OF");
    feat_types[0] = rel_type_id("HAS_PART");
    feat_types[1] = rel_type_id("HAS_MEMBER");
    feat_types[2] = rel_type_id("HAS_SUBSTANCE");
    feat_types[3] = rel_type_id("HAS_ATTRIBUTE");
    feat_types[4] = rel_type_id("CAUSES");
    feat_types[5] = rel_type_id("USED_FOR");
    feat_types[6] = rel_type_id("IS_ANTONYM_OF");
    feat_types[7] = rel_type_id("IS_SIMILAR_TO");
    feat_types[8] = rel_type_id("PERTAINS_TO");

    {
        hash128_t seeds_x[32], seeds_y[32];
        int nx = 0, ny = 0;
        Datum *synsets_x = NULL;
        Datum *synsets_y = NULL;
        int    ns_x = 0, ns_y = 0;

        seeds_x[nx++] = datum_to_hash128(x);
        spi_fetch_synset_ids(x, &synsets_x, &ns_x);
        for (int i = 0; i < ns_x && nx < 32; i++)
            seeds_x[nx++] = datum_to_hash128(synsets_x[i]);

        seeds_y[ny++] = datum_to_hash128(y);
        spi_fetch_synset_ids(y, &synsets_y, &ns_y);
        for (int i = 0; i < ns_y && ny < 32; i++)
            seeds_y[ny++] = datum_to_hash128(synsets_y[i]);

        {
            Datum up_d[2] = { hash128_to_datum(&up_types[0]), hash128_to_datum(&up_types[1]) };
            n_ax = tax_bfs_up(seeds_x, nx, 7, up_types, 2, &ax);
            n_ay = tax_bfs_up(seeds_y, ny, 7, up_types, 2, &ay);
            pfree(DatumGetPointer(up_d[0]));
            pfree(DatumGetPointer(up_d[1]));
        }
    }

    int rows_cap = CONTRAST_FEAT_INITIAL;
    rows = (ContrastRow *) palloc0(sizeof(ContrastRow) * rows_cap);
    HTAB *rowmap;
    {
        HASHCTL rctl;
        memset(&rctl, 0, sizeof(rctl));
        rctl.keysize = 32;
        rctl.entrysize = sizeof(ContrastIdxEntry);
        rowmap = hash_create("contrast rowmap", CONTRAST_FEAT_INITIAL, &rctl,
                             HASH_ELEM | HASH_BLOBS);
    }
  {
    hash128_t isa_tid = up_types[0];
    for (int i = 0; i < n_ax; i++)
        if (ax[i].depth > 0)
            contrast_add_fact(rowmap, &rows, &n_rows, &rows_cap,
                              &isa_tid, &ax[i].id, (Datum) 0, true);
    for (int i = 0; i < n_ay; i++)
        if (ay[i].depth > 0)
            contrast_add_fact(rowmap, &rows, &n_rows, &rows_cap,
                              &isa_tid, &ay[i].id, (Datum) 0, false);
  }

    {
        bytea *subjbuf = (bytea *) palloc(VARHDRSZ + sizeof(hash128_t));
        Datum  args[1];
        SET_VARSIZE(subjbuf, VARHDRSZ + sizeof(hash128_t));

        for (int side = 0; side < 2; side++)
        {
            Datum  anchor = side == 0 ? x : y;
            Datum *synsets;
            int    ns;
            bool   from_x = side == 0;

            spi_fetch_synset_ids(anchor, &synsets, &ns);
            for (int si = -1; si < ns; si++)
            {
                Datum subj = si < 0 ? anchor : synsets[si];
                int   rc;

                args[0] = subj;
                rc = SPI_execute_plan(subject_edges_plan, args, NULL, true, 0);
                if (rc != SPI_OK_SELECT)
                    elog(ERROR, "contrast: consensus query failed");

                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td  = SPI_tuptable->tupdesc;
                    bool      isnull;
                    hash128_t tid, oid;
                    int64     rating, rd;
                    Datum     mu;

                    tid = datum_to_hash128(SPI_getbinval(tup, td, 1, &isnull));
                    oid = datum_to_hash128(SPI_getbinval(tup, td, 2, &isnull));
                    rating = DatumGetInt64(SPI_getbinval(tup, td, 3, &isnull));
                    rd = DatumGetInt64(SPI_getbinval(tup, td, 4, &isnull));
                    if (!contrast_type_allowed(&tid, feat_types, feat_n))
                        continue;
                    mu = eff_mu_display_numeric(rating, rd);
                    contrast_add_fact(rowmap, &rows, &n_rows, &rows_cap, &tid, &oid, mu, from_x);
                }
            }
        }
        pfree(subjbuf);
    }

    /* Batch label resolution for the emitted window: the per-row
     * spi_realize + spi_type_label pair was 2 unprepared round trips per
     * emitted row (up to lim=80). realize_batch resolves the facts in 6
     * fixed round trips; type labels come back in one set query. */
    {
        int    n_emit = n_rows < lim ? n_rows : lim;
        Datum *facts = NULL, *type_lbls = NULL;
        bool  *fact_nulls = NULL, *type_lbl_nulls = NULL;
        int    n_facts = 0, n_type_lbls = 0;

        if (n_emit > 0)
        {
            Datum *obj_ids  = (Datum *) palloc(sizeof(Datum) * n_emit);
            Datum *type_ids = (Datum *) palloc(sizeof(Datum) * n_emit);
            ArrayType *obj_arr, *type_arr;
            Oid   rtypes[2] = { BYTEAARRAYOID, BYTEAOID };
            Datum rargs[2];
            char  rnulls[3] = "  ";
            bool  isnull;
            int   rc2;

            for (int i = 0; i < n_emit; i++)
            {
                obj_ids[i]  = hash128_to_datum(&rows[i].object_id);
                type_ids[i] = hash128_to_datum(&rows[i].type_id);
            }
            obj_arr  = construct_array(obj_ids, n_emit, BYTEAOID, -1, false, TYPALIGN_INT);
            type_arr = construct_array(type_ids, n_emit, BYTEAOID, -1, false, TYPALIGN_INT);

            rargs[0] = PointerGetDatum(obj_arr);
            rargs[1] = lang;
            if (lang == (Datum) 0)
                rnulls[1] = 'n';
            rc2 = SPI_execute_with_args(
                "SELECT laplace.realize_batch($1, $2)",
                2, rtypes, rargs, rnulls, true, 1);
            if (rc2 == SPI_OK_SELECT && SPI_processed > 0)
            {
                Datum arr = SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull);
                if (!isnull)
                    deconstruct_array(DatumGetArrayTypePCopy(arr), TEXTOID,
                                      -1, false, TYPALIGN_INT,
                                      &facts, &fact_nulls, &n_facts);
            }

            rargs[0] = PointerGetDatum(type_arr);
            rc2 = SPI_execute_with_args(
                "SELECT array_agg(laplace.type_label(u.id) ORDER BY u.ord) "
                "FROM unnest($1) WITH ORDINALITY AS u(id, ord)",
                1, rtypes, rargs, NULL, true, 1);
            if (rc2 == SPI_OK_SELECT && SPI_processed > 0)
            {
                Datum arr = SPI_getbinval(SPI_tuptable->vals[0],
                                          SPI_tuptable->tupdesc, 1, &isnull);
                if (!isnull)
                    deconstruct_array(DatumGetArrayTypePCopy(arr), TEXTOID,
                                      -1, false, TYPALIGN_INT,
                                      &type_lbls, &type_lbl_nulls, &n_type_lbls);
            }
        }

        for (int i = 0; i < n_emit; i++)
        {
            Datum values[4];
            bool  nulls[4] = { false, false, false, false };
            const char *holder;

            if (rows[i].from_x && rows[i].from_y)
                holder = "both";
            else if (rows[i].from_x)
                holder = "x-only";
            else
                holder = "y-only";

            values[0] = CStringGetTextDatum(holder);
            if (type_lbls != NULL && i < n_type_lbls && !type_lbl_nulls[i])
                values[1] = type_lbls[i];
            else
                values[1] = hash128_hex16_text(&rows[i].type_id);
            if (facts != NULL && i < n_facts && !fact_nulls[i])
                values[2] = facts[i];
            else
                values[2] = hash128_hex16_text(&rows[i].object_id);
            if (rows[i].mu == (Datum) 0)
                nulls[3] = true;
            else
                values[3] = rows[i].mu;

            tuplestore_putvalues(rsinfo->setResult, rsinfo->setDesc, values, nulls);
            emitted++;
        }
    }

    hash_destroy(rowmap);
    laplace_spi_finish(spi_top);
    return (Datum) 0;
}
