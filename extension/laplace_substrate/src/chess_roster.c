#include "postgres.h"
#include "miscadmin.h"
#include "funcapi.h"
#include "utils/array.h"
#include "spi_common.h"
#include "spi_nested.h"
#include "laplace/core/sql_catalog.h"

/* Two disjoint ordered sets: canonical OUTCOME cells and typed players without
 * that cell. Taking K from each is sufficient for the first K of their union.
 * SQL retains its index order; C merges exact fixed-point keys and labels only
 * the requested page. Profile-only players remain navigable at neutral standing. */
typedef struct {
    hash128_t id;
    int64 games, rating, rd, key;
} RosterRow;

static const char *keys[] = {
    "chess.roster_strength_desc", "chess.roster_strength_asc",
    "chess.roster_games_desc", "chess.roster_games_asc",
    "chess.roster_rating_desc", "chess.roster_rating_asc",
    "chess.roster_rd_desc", "chess.roster_rd_asc",
    "chess.roster_unrated", "chess.roster_types", "display.labels",
    "chess.search_exact", "chess.search_named_players", "chess.search_standings"
};
static SPIPlanPtr plans[lengthof(keys)];

static void
read_set(int which, int n, Oid *types, Datum *args)
{
    if (plans[which] == NULL) {
        plans[which] = SPI_prepare_cursor(laplace_sql_query_text(keys[which]), n, types, CURSOR_OPT_PARALLEL_OK);
        if (!plans[which] || SPI_keepplan(plans[which]) != 0)
            elog(ERROR, "chess roster: cannot retain %s", keys[which]);
    }
    if (SPI_execute_plan(plans[which], args, NULL, true, 0) != SPI_OK_SELECT)
        elog(ERROR, "chess roster: read failed for %s", keys[which]);
}

static int
compare_rows(const void *a, const void *b, void *arg)
{
    const RosterRow *x = a, *y = b;
    bool ascending = *(bool *)arg;
    if (x->key != y->key) return (x->key < y->key) == ascending ? -1 : 1;
    return memcmp(&x->id, &y->id, sizeof(hash128_t));
}

PG_FUNCTION_INFO_V1(pg_laplace_chess_ranked);
Datum
pg_laplace_chess_ranked(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *r = (ReturnSetInfo *)fcinfo->resultinfo;
    int64 limit = PG_ARGISNULL(0) ? PG_INT32_MAX : PG_GETARG_INT32(0);
    int64 offset = PG_ARGISNULL(1) ? 0 : PG_GETARG_INT32(1);
    if (limit < 0 || offset < 0) ereport(ERROR, (errmsg("chess roster: negative page bound")));
    if (limit == 0) return (Datum)0;
    char *sort = PG_ARGISNULL(2) ? "strength" : text_to_cstring(PG_GETARG_TEXT_PP(2));
    char *direction = PG_ARGISNULL(3) ? "desc" : text_to_cstring(PG_GETARG_TEXT_PP(3));
    int field = strcmp(sort, "games") == 0 ? 1 : strcmp(sort, "rating") == 0 ? 2 : strcmp(sort, "rd") == 0 ? 3 : 0;
    bool ascending = strcmp(direction, "asc") == 0;
    bool spi_top = false;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT) elog(ERROR, "chess roster: SPI connect failed");
    read_set(9, 0, NULL, NULL);
    if (SPI_processed != 1) elog(ERROR, "chess roster: missing type identities");
    Datum args[4]; bool isnull;
    for (int i = 0; i < 3; ++i)
        args[i] = copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, i + 1, &isnull));
    int64 neutral = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 4, &isnull));
    int64 initial_rd = DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0], SPI_tuptable->tupdesc, 5, &isnull));
    SPI_freetuptable(SPI_tuptable);
    args[3] = Int64GetDatum(offset + limit);
    Oid types[] = {BYTEAOID, BYTEAOID, BYTEAOID, INT8OID};
    RosterRow *rows = NULL; Size count = 0;
    for (int arm = 0; arm < 2; ++arm) {
        CHECK_FOR_INTERRUPTS();
        read_set(arm == 0 ? field * 2 + ascending : 8, 4, types, args);
        Size total = count + SPI_processed;
        if (total > MaxAllocSize / sizeof(RosterRow)) elog(ERROR, "chess roster: result exceeds allocation capacity");
        rows = rows ? repalloc(rows, Max(total, 1) * sizeof(RosterRow)) : palloc(Max(total, 1) * sizeof(RosterRow));
        for (uint64 i = 0; i < SPI_processed; ++i) {
            HeapTuple tuple = SPI_tuptable->vals[i]; TupleDesc desc = SPI_tuptable->tupdesc;
            RosterRow *row = &rows[count++];
            row->id = datum_to_hash128(SPI_getbinval(tuple, desc, 1, &isnull));
            row->games = arm ? 0 : DatumGetInt64(SPI_getbinval(tuple, desc, 2, &isnull));
            row->rating = arm ? neutral : DatumGetInt64(SPI_getbinval(tuple, desc, 3, &isnull));
            row->rd = arm ? initial_rd : DatumGetInt64(SPI_getbinval(tuple, desc, 4, &isnull));
            row->key = field == 1 ? row->games : field == 2 ? row->rating : field == 3 ? row->rd : laplace_effective_mu_fp(row->rating, row->rd);
        }
        SPI_freetuptable(SPI_tuptable);
    }
    qsort_arg(rows, count, sizeof(RosterRow), compare_rows, &ascending);
    Size end = Min(count, (Size)(offset + limit));
    if ((Size)offset < end) {
        int n = end - offset;
        Datum *ids = palloc(n * sizeof(Datum));
        for (int i = 0; i < n; ++i) ids[i] = hash128_to_datum(&rows[offset + i].id);
        Oid label_types[] = {BYTEAARRAYOID};
        Datum label_args[] = {PointerGetDatum(construct_array(ids, n, BYTEAOID, -1, false, TYPALIGN_INT))};
        read_set(10, 1, label_types, label_args);
        if (SPI_processed != n) elog(ERROR, "chess roster: display lost page positions");
        for (int i = 0; i < n; ++i) {
            RosterRow *row = &rows[offset + i];
            Datum label = SPI_getbinval(SPI_tuptable->vals[i], SPI_tuptable->tupdesc, 2, &isnull);
            if (isnull) label = CStringGetTextDatum("Unrealized player");
            Datum v[] = {Int64GetDatum(offset + i + 1), ids[i], label, Int64GetDatum(row->games),
                DirectFunctionCall1(numeric_float8, fp_display_numeric(row->rating)),
                DirectFunctionCall1(numeric_float8, fp_display_numeric(row->rd)),
                DirectFunctionCall1(numeric_float8, eff_mu_display_numeric(row->rating, row->rd))};
            bool nulls[7] = {false};
            tuplestore_putvalues(r->setResult, r->setDesc, v, nulls);
        }
        SPI_freetuptable(SPI_tuptable);
    }
    laplace_spi_finish(spi_top);
    return (Datum)0;
}

/* Resolve the exact identity once, then ascend witnessed ordered Content
 * trajectories on a miss. Standings use the roster's canonical arena. Native
 * sorting precedes pagination and the only label read covers that final page. */
PG_FUNCTION_INFO_V1(pg_laplace_chess_search_candidates);
Datum
pg_laplace_chess_search_candidates(PG_FUNCTION_ARGS)
{
    InitMaterializedSRF(fcinfo, 0);
    ReturnSetInfo *r = (ReturnSetInfo *)fcinfo->resultinfo;
    if (PG_ARGISNULL(0)) return (Datum)0;
    int64 limit = PG_ARGISNULL(1) ? PG_INT32_MAX : PG_GETARG_INT32(1);
    int64 offset = PG_ARGISNULL(2) ? 0 : PG_GETARG_INT32(2);
    if (limit < 0 || offset < 0) ereport(ERROR, (errmsg("chess search: negative page bound")));
    if (!limit) return (Datum)0;
    char *sort = PG_ARGISNULL(3) ? "strength" : text_to_cstring(PG_GETARG_TEXT_PP(3));
    char *direction = PG_ARGISNULL(4) ? "desc" : text_to_cstring(PG_GETARG_TEXT_PP(4));
    bool exact_only = !PG_ARGISNULL(5) && PG_GETARG_BOOL(5);
    int field = strcmp(sort,"games") == 0 ? 1 : strcmp(sort,"rating") == 0 ? 2 : strcmp(sort,"rd") == 0 ? 3 : 0;
    bool ascending = strcmp(direction,"asc") == 0;
    bool spi_top = false, isnull;
    if (laplace_spi_connect(&spi_top) != SPI_OK_CONNECT) elog(ERROR,"chess search: SPI connect failed");
    read_set(9, 0, NULL, NULL);
    if (SPI_processed != 1) elog(ERROR,"chess search: missing type identities");
    Datum args[4] = {PointerGetDatum(PG_GETARG_ARRAYTYPE_P(0))};
    for (int i=0; i<3; ++i)
        args[i+1]=copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[0],SPI_tuptable->tupdesc,i+1,&isnull));
    int64 neutral=DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],SPI_tuptable->tupdesc,4,&isnull));
    int64 initial_rd=DatumGetInt64(SPI_getbinval(SPI_tuptable->vals[0],SPI_tuptable->tupdesc,5,&isnull));
    SPI_freetuptable(SPI_tuptable);
    Oid types[]={TEXTARRAYOID,BYTEAOID,BYTEAOID,BYTEAOID};
    read_set(11,4,types,args);
    if (!SPI_processed && !exact_only) {
        SPI_freetuptable(SPI_tuptable);
        read_set(12,1,types,args);
    }
    if (SPI_processed > MaxAllocSize/sizeof(Datum)) elog(ERROR,"chess search: candidate set exceeds allocation capacity");
    int candidates=(int)SPI_processed;
    Datum *ids=palloc(Max(candidates,1)*sizeof(Datum));
    for(int i=0;i<candidates;++i)
        ids[i]=copy_bytea_datum(SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,1,&isnull));
    SPI_freetuptable(SPI_tuptable);
    args[0]=PointerGetDatum(construct_array(ids,candidates,BYTEAOID,-1,false,TYPALIGN_INT));
    types[0]=BYTEAARRAYOID;
    read_set(13,4,types,args);
    if (SPI_processed > MaxAllocSize/sizeof(RosterRow)) elog(ERROR,"chess search: standing set exceeds allocation capacity");
    Size count=SPI_processed;
    RosterRow *rows=palloc(Max(count,1)*sizeof(RosterRow));
    for(uint64 i=0;i<SPI_processed;++i) {
        HeapTuple tuple=SPI_tuptable->vals[i];TupleDesc desc=SPI_tuptable->tupdesc;
        RosterRow *row=&rows[i];
        row->id=datum_to_hash128(SPI_getbinval(tuple,desc,1,&isnull));
        row->games=DatumGetInt64(SPI_getbinval(tuple,desc,2,&isnull));if(isnull)row->games=0;
        row->rating=DatumGetInt64(SPI_getbinval(tuple,desc,3,&isnull));if(isnull)row->rating=neutral;
        row->rd=DatumGetInt64(SPI_getbinval(tuple,desc,4,&isnull));if(isnull)row->rd=initial_rd;
        row->key=field==1?row->games:field==2?row->rating:field==3?row->rd:laplace_effective_mu_fp(row->rating,row->rd);
    }
    SPI_freetuptable(SPI_tuptable);
    qsort_arg(rows,count,sizeof(RosterRow),compare_rows,&ascending);
    Size end=Min(count,(Size)(offset+limit));
    if((Size)offset<end) {
        int n=end-offset;
        Datum *page=palloc(n*sizeof(Datum));
        for(int i=0;i<n;++i)page[i]=hash128_to_datum(&rows[offset+i].id);
        Datum label_args[]={PointerGetDatum(construct_array(page,n,BYTEAOID,-1,false,TYPALIGN_INT))};
        Oid label_types[]={BYTEAARRAYOID};
        read_set(10,1,label_types,label_args);
        if(SPI_processed!=n)elog(ERROR,"chess search: display lost page positions");
        for(int i=0;i<n;++i) {
            RosterRow *row=&rows[offset+i];
            Datum label=SPI_getbinval(SPI_tuptable->vals[i],SPI_tuptable->tupdesc,2,&isnull);
            if(isnull)label=CStringGetTextDatum("Unrealized player");
            Datum v[]={page[i],label,Int64GetDatum(row->games),
                DirectFunctionCall1(numeric_float8,fp_display_numeric(row->rating)),
                DirectFunctionCall1(numeric_float8,fp_display_numeric(row->rd)),
                DirectFunctionCall1(numeric_float8,eff_mu_display_numeric(row->rating,row->rd))};
            bool nulls[6]={false};
            tuplestore_putvalues(r->setResult,r->setDesc,v,nulls);
        }
        SPI_freetuptable(SPI_tuptable);
    }
    laplace_spi_finish(spi_top);
    return (Datum)0;
}
