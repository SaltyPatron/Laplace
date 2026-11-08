#include "postgres.h"
#include "common/int.h"

#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/fmgrprotos.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"

#include "laplace/core/math4d.h"

#include "recall_internal.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "consensus_scan.h"
#include "laplace/core/sql_catalog.h"

PG_FUNCTION_INFO_V1(pg_laplace_define_fast);
PG_FUNCTION_INFO_V1(pg_laplace_word_shape_peers_fast);

typedef struct { hash128_t id;Datum label;int64 score,witnesses; } DefineCandidate;
typedef struct { hash128_t id;int ordinal; } DefineIndex;
typedef struct { DefineCandidate *rows;HTAB *index; } DefineContext;

static void define_context_cell(const LaplaceConsensusRow *row,void *context)
{
    DefineContext *read=context;if(row->object_is_null)return;
    DefineIndex *entry=hash_search(read->index,&row->object,HASH_FIND,NULL);
    if(entry && pg_add_s64_overflow(read->rows[entry->ordinal].score,
        laplace_effective_mu_fp(row->rating,row->rd),&read->rows[entry->ordinal].score))
        ereport(ERROR,(errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),errmsg("Definition context exceeds integer capacity")));
}

static int define_order(const void *a,const void *b)
{
    const DefineCandidate *left=a,*right=b;
    if(left->score!=right->score)return left->score>right->score?-1:1;
    return memcmp(&left->id,&right->id,sizeof(left->id));
}

/* The producer-declared program is shared with the UI; no retired relation
 * vocabulary or a parallel legacy entity/consensus stack decides a gloss. */
static void define_fast_impl(Datum word,ArrayType *context,int limit,ReplyBuf *buf)
{
    if(limit<0)ereport(ERROR,(errcode(ERRCODE_INVALID_PARAMETER_VALUE),errmsg("Definition limit must not be negative")));
    if(!limit)return;
    static SPIPlanPtr plan;Oid types[]={BYTEAOID};Datum args[]={word};
    spi_common_ensure_plan(&plan,laplace_sql_query_text("recall.declared_definitions"),1,types);
    int rc=SPI_execute_plan(plan,args,NULL,true,0);
    if(rc!=SPI_OK_SELECT)elog(ERROR,"Declared definition read failed");
    uint64 n=SPI_processed;
    if(n>MaxAllocSize/sizeof(DefineCandidate))elog(ERROR,"Declared definitions exceed memory capacity");
    DefineCandidate *rows=palloc(Max(n,1)*sizeof(*rows));Datum *ids=palloc(Max(n,1)*sizeof(Datum));
    HASHCTL ctl={0};ctl.keysize=sizeof(hash128_t);ctl.entrysize=sizeof(DefineIndex);
    HTAB *index=hash_create("Declared definition context",Max(n,16),&ctl,HASH_ELEM|HASH_BLOBS);
    for(uint64 i=0;i<n;i++){
        bool missing;Datum values[4];
        for(int j=0;j<4;j++){
            values[j]=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,j+1,&missing);
            if(missing)elog(ERROR,"Declared definition has a null field");
        }
        rows[i]=(DefineCandidate){datum_to_hash128(values[0]),datumCopy(values[1],false,-1),DatumGetInt64(values[2]),DatumGetInt64(values[3])};
        ids[i]=hash128_to_datum(&rows[i].id);
        DefineIndex *entry=hash_search(index,&rows[i].id,HASH_ENTER,NULL);entry->ordinal=(int)i;
    }
    if(n && context && ArrayGetNItems(ARR_NDIM(context),ARR_DIMS(context))){
        ArrayType *candidates=construct_array(ids,(int)n,BYTEAOID,-1,false,TYPALIGN_INT);
        DefineContext read={rows,index};
        laplace_consensus_scan(context,candidates,NULL,define_context_cell,&read,NULL);
        pfree(candidates);
    }
    qsort(rows,(size_t)n,sizeof(*rows),define_order);
    for(uint64 i=0;i<n && i<(uint64)limit;i++)
        reply_buf_add(buf,rows[i].label,fp_display_numeric(rows[i].score),Int64GetDatum(rows[i].witnesses),false,false,false);
    hash_destroy(index);pfree(ids);pfree(rows);
}

Datum
pg_laplace_define_fast(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *rsinfo = (ReturnSetInfo *) fcinfo->resultinfo;
    Datum          word;
    ArrayType     *context_arr = NULL;
    int            limit;
    ReplyBuf       buf;
    bool           spi_top = false;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    word = PG_GETARG_DATUM(0);
    if (!PG_ARGISNULL(1))
        context_arr = PG_GETARG_ARRAYTYPE_P(1);
    limit = PG_ARGISNULL(2) ? 5 : PG_GETARG_INT32(2);

    reply_buf_init(&buf);
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "define_fast: SPI_connect failed");

    define_fast_impl(word, context_arr, limit, &buf);

    laplace_spi_finish(spi_top);
    InitMaterializedSRF(fcinfo, 0);
    reply_buf_emit(rsinfo, &buf);
    return (Datum) 0;
}

typedef struct ShapeCandidate
{
    Datum entity_id;
    Datum coord;
    double ang;
    double fr;
} ShapeCandidate;

static int
shape_cand_cmp(const void *a, const void *b)
{
    const ShapeCandidate *ca = (const ShapeCandidate *) a;
    const ShapeCandidate *cb = (const ShapeCandidate *) b;

    if (ca->ang != cb->ang) return (ca->ang > cb->ang) - (ca->ang < cb->ang);
    return (ca->fr > cb->fr) - (ca->fr < cb->fr);
}

/*
 * Geometric neighbors of a composition by shape: the 500 nearest coordinates,
 * kept when they share the anchor's entity type, constituent count and case
 * class, admitted by Frechet distance of their constituent curves, ordered by
 * angular distance then Frechet, capped at 48.
 *
 * The anchor coordinate is read first and bound as a parameter: a KNN
 * ORDER BY against a correlated column is not eligible for the GiST
 * index-ordered scan, a bound parameter is.
 */
static Datum
word_shape_peers_fast_impl(Datum p_word, double p_frechet_max)
{
    Oid    types1[1] = { BYTEAOID };
    Datum  args1[1] = { p_word };
    int    rc;
    Datum  me_curve, me_coord;
    int32  me_nconst;
    Oid    geom_oid;
    char  *me_case_class = NULL;
    ShapeCandidate *raw;
    int    n_raw = 0;
    ShapeCandidate *survivors;
    int    n_survivors = 0;

    static SPIPlanPtr plan_anchor = NULL;
    static SPIPlanPtr plan_case1  = NULL;
    static SPIPlanPtr plan_knn    = NULL;
    static SPIPlanPtr plan_casen  = NULL;
    static SPIPlanPtr plan_frechet = NULL;
    static SPIPlanPtr plan_xyzm1  = NULL;
    static SPIPlanPtr plan_xyzmn  = NULL;

    /* The Frechet gate compares realized curves: structural.word_curve()
     * unpacks the trajectory's child ids and reads each child's coordinate in
     * ordinal order. The packed trajectory values are identities, not
     * positions. The anchor curve is built once and bound as a parameter so
     * it is not recomputed per candidate. */
    rc = SPI_execute_plan(
        spi_common_ensure_plan(&plan_anchor,
            "SELECT structural.word_curve($1), w.coord, w.n_constituents "
            "FROM laplace.v_word_points w "
            "WHERE w.id = $1 "
            "  AND w.trajectory IS NOT NULL AND w.coord IS NOT NULL "
            "LIMIT 1",
            1, types1),
        args1, NULL, true, 1);
    if (rc != SPI_OK_SELECT || SPI_processed == 0)
        return PointerGetDatum(construct_empty_array(BYTEAOID));

    {
        HeapTuple me_tup = SPI_tuptable->vals[0];
        TupleDesc me_td  = SPI_tuptable->tupdesc;
        bool n0, n1, n2;

        me_curve   = copy_bytea_datum(SPI_getbinval(me_tup, me_td, 1, &n0));
        me_coord   = copy_bytea_datum(SPI_getbinval(me_tup, me_td, 2, &n1));
        me_nconst  = DatumGetInt32(SPI_getbinval(me_tup, me_td, 3, &n2));
        geom_oid   = SPI_gettypeid(me_td, 2);
        /* No realized curve means no shape to compare: empty result. */
        if (n0)
            return PointerGetDatum(construct_empty_array(BYTEAOID));
    }

    {
        Oid   ctypes[1] = { BYTEAOID };
        Datum cargs[1] = { p_word };
        int   crc = SPI_execute_plan(
            spi_common_ensure_plan(&plan_case1,
                "SELECT case_class FROM lexical.word_case_classes_batch(ARRAY[$1]::bytea[])",
                1, ctypes),
            cargs, NULL, true, 1);
        if (crc == SPI_OK_SELECT && SPI_processed > 0)
        {
            bool cn;
            Datum d = SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 1, &cn);
            if (!cn)
                me_case_class = text_to_cstring(DatumGetTextPP(d));
        }
    }

    {
        Oid   types2[3] = { BYTEAOID, geom_oid, INT4OID };
        Datum args2[3] = { p_word, me_coord, Int32GetDatum(500) };

        rc = SPI_execute_plan(
            spi_common_ensure_plan(&plan_knn,
                "WITH self_type AS MATERIALIZED ("
                "  SELECT self.type_id FROM laplace.entities self WHERE self.id = $1 LIMIT 1"
                ") "
                "SELECT w.id, w.coord, EXISTS ("
                "  SELECT 1 FROM self_type st WHERE st.type_id = w.type_id"
                "), w.n_constituents "
                "FROM laplace.v_word_points w "
                "WHERE w.trajectory IS NOT NULL AND w.coord IS NOT NULL "
                "  AND w.id <> $1 "
                "ORDER BY w.coord OPERATOR(public.<<->>) $2 LIMIT $3",
                3, types2),
            args2, NULL, true, 0);
        if (rc != SPI_OK_SELECT)
            elog(ERROR, "word_shape_peers_fast: KNN query failed: %s", SPI_result_code_string(rc));

        raw = (ShapeCandidate *) palloc(sizeof(ShapeCandidate) * (SPI_processed + 1));
        for (uint64 r = 0; r < SPI_processed; r++)
        {
            HeapTuple tup = SPI_tuptable->vals[r];
            TupleDesc td  = SPI_tuptable->tupdesc;
            bool cn0, cn1, cn2, cn3;
            Datum eid   = SPI_getbinval(tup, td, 1, &cn0);
            Datum coord = SPI_getbinval(tup, td, 2, &cn1);
            Datum same_type = SPI_getbinval(tup, td, 3, &cn2);
            Datum ncst  = SPI_getbinval(tup, td, 4, &cn3);

            if (cn0 || cn1 || cn2 || cn3) continue;
            if (!DatumGetBool(same_type)) continue;
            if (DatumGetInt32(ncst) != me_nconst) continue;
            raw[n_raw].entity_id  = copy_bytea_datum(eid);
            raw[n_raw].coord      = copy_bytea_datum(coord);
            n_raw++;
        }
    }

    /*
     * Each filter runs as one SPI call over the whole candidate array;
     * unnest ... WITH ORDINALITY carries the position so results scatter back
     * to their candidate in C.
     */
    survivors = (ShapeCandidate *) palloc(sizeof(ShapeCandidate) * (n_raw + 1));
    {
        bool *case_ok = (bool *) palloc0(sizeof(bool) * n_raw);

        if (me_case_class == NULL)
        {
            for (int i = 0; i < n_raw; i++) case_ok[i] = true;
        }
        else if (n_raw > 0)
        {
            Datum *idarr = (Datum *) palloc(sizeof(Datum) * n_raw);
            ArrayType *id_array;
            Oid   ctypes[1] = { BYTEAARRAYOID };
            Datum cargs[1];
            int   crc;

            for (int i = 0; i < n_raw; i++) idarr[i] = raw[i].entity_id;
            id_array = construct_array(idarr, n_raw, BYTEAOID, -1, false, 'i');
            cargs[0] = PointerGetDatum(id_array);

            crc = SPI_execute_plan(
                spi_common_ensure_plan(&plan_casen,
                    "SELECT t.idx, c.case_class "
                    "FROM unnest($1::bytea[]) WITH ORDINALITY AS t(entity_id, idx) "
                    "JOIN lexical.word_case_classes_batch($1::bytea[]) c "
                    "ON c.word_id=t.entity_id",
                    1, ctypes),
                cargs, NULL, true, 0);
            if (crc == SPI_OK_SELECT)
            {
                for (uint64 r = 0; r < SPI_processed; r++)
                {
                    HeapTuple tup = SPI_tuptable->vals[r];
                    TupleDesc td  = SPI_tuptable->tupdesc;
                    bool  in0, in1;
                    int64 idx = DatumGetInt64(SPI_getbinval(tup, td, 1, &in0));
                    Datum ccd = SPI_getbinval(tup, td, 2, &in1);

                    if (in0 || in1 || idx < 1 || idx > n_raw) continue;
                    {
                        char *cc = text_to_cstring(DatumGetTextPP(ccd));
                        case_ok[idx - 1] = (strcmp(cc, me_case_class) == 0);
                        pfree(cc);
                    }
                }
            }
            pfree(idarr);
        }

        {
            int    n_matched = 0;
            int   *matched_raw_idx = (int *) palloc(sizeof(int) * (n_raw + 1));
            Datum *eidarr = (Datum *) palloc(sizeof(Datum) * (n_raw + 1));
            Datum *coordarr = (Datum *) palloc(sizeof(Datum) * (n_raw + 1));

            for (int i = 0; i < n_raw; i++)
            {
                if (!case_ok[i]) continue;
                matched_raw_idx[n_matched] = i;
                eidarr[n_matched] = raw[i].entity_id;
                coordarr[n_matched] = raw[i].coord;
                n_matched++;
            }

            if (n_matched > 0)
            {
                ArrayType *eid_array = construct_array(eidarr, n_matched, BYTEAOID, -1, false, 'i');
                ArrayType *coord_array = construct_array(coordarr, n_matched, geom_oid, -1, false, 'i');
                Oid   geom_arr_oid = get_array_type(geom_oid);
                int   grc;
                double *fr_by_pos = (double *) palloc0(sizeof(double) * n_matched);
                double *ang_by_pos = (double *) palloc0(sizeof(double) * n_matched);
                bool  *ok_by_pos = (bool *) palloc0(sizeof(bool) * n_matched);
                bool  *coord_ok = (bool *) palloc0(sizeof(bool) * n_matched);
                double *cand_xyzm = (double *) palloc0(sizeof(double) * (size_t) n_matched * 4);
                double me_xyzm[4] = {0, 0, 0, 0};

                /* Frechet over each candidate's realized constituent curve
                 * against the anchor's, computed by laplace_frechet_4d. */
                {
                    Oid   ftypes[2] = { BYTEAARRAYOID, geom_oid };
                    Datum fargs[2] = { PointerGetDatum(eid_array), me_curve };

                    grc = SPI_execute_plan(
                        spi_common_ensure_plan(&plan_frechet,
                            "SELECT t.idx, public.laplace_frechet_4d(structural.word_curve(t.entity_id), $2) "
                            "FROM unnest($1::bytea[]) WITH ORDINALITY AS t(entity_id, idx)",
                            2, ftypes),
                        fargs, NULL, true, 0);
                    if (grc == SPI_OK_SELECT)
                    {
                        for (uint64 r = 0; r < SPI_processed; r++)
                        {
                            HeapTuple tup = SPI_tuptable->vals[r];
                            TupleDesc td  = SPI_tuptable->tupdesc;
                            bool  in0, in1;
                            int64 idx = DatumGetInt64(SPI_getbinval(tup, td, 1, &in0));
                            Datum frd = SPI_getbinval(tup, td, 2, &in1);

                            if (in0 || idx < 1 || idx > n_matched || in1) continue;
                            fr_by_pos[idx - 1] = DatumGetFloat8(frd);
                            ok_by_pos[idx - 1] = true;
                        }
                    }
                }

                /* SQL only extracts XYZM components (this module does not link
                 * liblwgeom); the angular distance itself is
                 * math4d_angular_distance_batch over the whole set. */
                {
                    Oid   mtypes[1] = { geom_oid };
                    Datum margs[1] = { me_coord };
                    int   mrc;

                    mrc = SPI_execute_plan(
                        spi_common_ensure_plan(&plan_xyzm1,
                            "SELECT ST_X($1), ST_Y($1), ST_Z($1), ST_M($1)",
                            1, mtypes),
                        margs, NULL, true, 1);
                    if (mrc == SPI_OK_SELECT && SPI_processed == 1)
                    {
                        HeapTuple tup = SPI_tuptable->vals[0];
                        TupleDesc td  = SPI_tuptable->tupdesc;
                        bool i0, i1, i2, i3;

                        me_xyzm[0] = DatumGetFloat8(SPI_getbinval(tup, td, 1, &i0));
                        me_xyzm[1] = DatumGetFloat8(SPI_getbinval(tup, td, 2, &i1));
                        me_xyzm[2] = DatumGetFloat8(SPI_getbinval(tup, td, 3, &i2));
                        me_xyzm[3] = DatumGetFloat8(SPI_getbinval(tup, td, 4, &i3));
                    }
                }
                {
                    Oid   ctypes[1] = { geom_arr_oid };
                    Datum cargs2[1] = { PointerGetDatum(coord_array) };
                    int   crc2;

                    crc2 = SPI_execute_plan(
                        spi_common_ensure_plan(&plan_xyzmn,
                            "SELECT t.idx, ST_X(t.coord), ST_Y(t.coord), ST_Z(t.coord), ST_M(t.coord) "
                            "FROM unnest($1) WITH ORDINALITY AS t(coord, idx)",
                            1, ctypes),
                        cargs2, NULL, true, 0);
                    if (crc2 == SPI_OK_SELECT)
                    {
                        for (uint64 r = 0; r < SPI_processed; r++)
                        {
                            HeapTuple tup = SPI_tuptable->vals[r];
                            TupleDesc td  = SPI_tuptable->tupdesc;
                            bool  in0, in1, in2, in3, in4;
                            int64 idx = DatumGetInt64(SPI_getbinval(tup, td, 1, &in0));
                            Datum xd = SPI_getbinval(tup, td, 2, &in1);
                            Datum yd = SPI_getbinval(tup, td, 3, &in2);
                            Datum zd = SPI_getbinval(tup, td, 4, &in3);
                            Datum wd = SPI_getbinval(tup, td, 5, &in4);

                            if (in0 || idx < 1 || idx > n_matched) continue;
                            if (in1 || in2 || in3 || in4) continue;
                            cand_xyzm[(idx - 1) * 4 + 0] = DatumGetFloat8(xd);
                            cand_xyzm[(idx - 1) * 4 + 1] = DatumGetFloat8(yd);
                            cand_xyzm[(idx - 1) * 4 + 2] = DatumGetFloat8(zd);
                            cand_xyzm[(idx - 1) * 4 + 3] = DatumGetFloat8(wd);
                            coord_ok[idx - 1] = true;
                        }
                    }
                }
                math4d_angular_distance_batch(cand_xyzm, n_matched, me_xyzm, ang_by_pos);
                for (int p = 0; p < n_matched; p++)
                    if (!coord_ok[p])
                        ok_by_pos[p] = false;

                for (int p = 0; p < n_matched; p++)
                {
                    int i = matched_raw_idx[p];

                    if (!ok_by_pos[p]) continue;
                    /* Frechet alone admits: it is order-sensitive. The coordinate
                     * is a centroid of the constituents, so angular distance cannot
                     * tell permutations of the same children apart; it only orders
                     * the admitted set. */
                    if (fr_by_pos[p] > p_frechet_max) continue;
                    survivors[n_survivors].entity_id = raw[i].entity_id;
                    survivors[n_survivors].fr = fr_by_pos[p];
                    survivors[n_survivors].ang = ang_by_pos[p];
                    n_survivors++;
                }
                pfree(fr_by_pos);
                pfree(ang_by_pos);
                pfree(ok_by_pos);
            }
            pfree(matched_raw_idx);
            pfree(eidarr);
            pfree(coordarr);
        }
        pfree(case_ok);
    }
    pfree(raw);

    qsort(survivors, n_survivors, sizeof(ShapeCandidate), shape_cand_cmp);

    {
        int n_out = n_survivors < 48 ? n_survivors : 48;
        Datum *out = (Datum *) palloc(sizeof(Datum) * (n_out > 0 ? n_out : 1));

        for (int i = 0; i < n_out; i++) out[i] = survivors[i].entity_id;
        return PointerGetDatum(construct_array(out, n_out, BYTEAOID, -1, false, 'i'));
    }
}

Datum
pg_laplace_word_shape_peers_fast(PG_FUNCTION_ARGS)
{
    Datum  word;
    double frechet_max;
    bool   spi_top = false;
    Datum  result;
    MemoryContext caller = CurrentMemoryContext;

    if (PG_ARGISNULL(0))
        PG_RETURN_NULL();
    word = PG_GETARG_DATUM(0);
    frechet_max = PG_ARGISNULL(1) ? 0.02 : PG_GETARG_FLOAT8(1);

    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT)
        elog(ERROR, "word_shape_peers_fast: SPI_connect failed");

    result = word_shape_peers_fast_impl(word, frechet_max);

    /* The result array lives in SPI's context; copy it to the caller before
     * SPI_finish releases that context. */
    MemoryContext previous = MemoryContextSwitchTo(caller);
    result = datumCopy(result, false, -1);
    MemoryContextSwitchTo(previous);
    laplace_spi_finish(spi_top);
    PG_RETURN_ARRAYTYPE_P(DatumGetArrayTypeP(result));
}
