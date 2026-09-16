/* Executes production PG transport/provider helpers against real native stage,
 * carrier, hash and descriptor owners. PostgreSQL allocation/error/SPI services
 * below are controlled test doubles. This is NOT PostgreSQL/PostGIS execution. */
#ifndef LAPLACE_DESCRIPTOR_PG_SOURCE
#define LAPLACE_DESCRIPTOR_PG_SOURCE "../src/physicality_descriptor_admission.c"
#endif
#include LAPLACE_DESCRIPTOR_PG_SOURCE
#include "../src/conversation_session.c"
#include <setjmp.h>
#include <stdarg.h>

#undef snprintf
#undef vsnprintf
#undef printf
#undef fprintf
#undef qsort

static unsigned checks;
static jmp_buf expected_error;
static bool expecting_error;
static char error_text[512];
static char error_detail[512];
static int error_code;
static unsigned spi_calls;
static unsigned spi_prepare_calls;
static unsigned spi_prepare_cursor_calls;
static Snapshot required_snapshot;
static size_t fake_raw_size;
static Datum fake_raw_datum;

typedef struct probe_row { Datum values[10]; bool nulls[10]; } probe_row;
static probe_row row;
static size_t metadata_count = 1, payload_count = 1;
static bool corrupt_payload_owner;
static SPITupleTable tuple_table;
static HeapTuple row_pointers[3];

MemoryContext CurrentMemoryContext;
static MemoryContext allocation_context;
volatile sig_atomic_t InterruptPending;
volatile sig_atomic_t QueryCancelPending;
volatile sig_atomic_t ProcDiePending;
volatile uint32 InterruptHoldoffCount;
volatile uint32 QueryCancelHoldoffCount;
volatile uint32 CritSectionCount;
ErrorContextCallback *error_context_stack;
uint64 SPI_processed;
SPITupleTable *SPI_tuptable;

#define CHECK(expression) do { ++checks; if (!(expression)) { \
    fprintf(stderr, "check failed at %s:%d: %s\n", __FILE__, __LINE__, #expression); exit(1); } } while (0)
#define REFUSES(expression, fragment) do { \
    expecting_error = true; error_text[0] = 0; error_detail[0] = 0; \
    if (setjmp(expected_error) == 0) { expression; CHECK(false); } \
    expecting_error = false; CHECK(strstr(error_text, fragment) != NULL); \
} while (0)

bool errstart(int level, const char *domain) { (void)level; (void)domain; return true; }
bool errstart_cold(int level, const char *domain) { return errstart(level, domain); }
int errcode(int code) { error_code = code; return 0; }
int errmsg(const char *format, ...) {
    va_list args; va_start(args, format); vsnprintf(error_text, sizeof(error_text), format, args); va_end(args); return 0;
}
int errdetail(const char *format, ...) {
    va_list args; va_start(args, format); vsnprintf(error_detail, sizeof(error_detail), format, args); va_end(args); return 0;
}
int set_errcontext_domain(const char *domain) { (void)domain; return 0; }
int errcontext_msg(const char *format, ...) { (void)format; return 0; }
int errmsg_internal(const char *format, ...) {
    va_list args; va_start(args,format);vsnprintf(error_text,sizeof(error_text),format,args);va_end(args);return 0;
}
void errfinish(const char *file, int line, const char *function) {
    (void)file; (void)line; (void)function;
    if (expecting_error) longjmp(expected_error, 1);
    fprintf(stderr, "unexpected PG test error %d: %s\n", error_code, error_text); exit(1);
}
int pg_snprintf(char *buffer, size_t size, const char *format, ...) {
    int result; va_list args; va_start(args, format); result = vsnprintf(buffer, size, format, args); va_end(args); return result;
}
void pg_qsort(void *base, size_t count, size_t size, int (*compare)(const void *, const void *)) {
    qsort(base, count, size, compare);
}
void *palloc(Size bytes) { void *p = malloc(bytes ? bytes : 1); CHECK(p != NULL); return p; }
void *palloc0(Size bytes) { void *p = calloc(1, bytes ? bytes : 1); CHECK(p != NULL); return p; }
void *MemoryContextAllocZero(MemoryContext context, Size bytes) { allocation_context=context; return palloc0(bytes); }
void pfree(void *pointer) { free(pointer); }
struct varlena *pg_detoast_datum_packed(struct varlena *datum) { return datum; }
struct varlena *pg_detoast_datum(struct varlena *datum) { return datum; }
Size toast_raw_datum_size(Datum datum) {
    if (fake_raw_size && datum == fake_raw_datum) return fake_raw_size;
    const struct varlena *value = (const struct varlena *)DatumGetPointer(datum);
    return VARSIZE_ANY_EXHDR(value) + VARHDRSZ;
}
void ProcessInterrupts(void) { CHECK(false); }
Datum SPI_getbinval(HeapTuple tuple, TupleDesc desc, int column, bool *isnull) {
    probe_row *r = (probe_row *)tuple; (void)desc;
    CHECK(column >= 1 && column <= 10); *isnull = r->nulls[column - 1]; return r->values[column - 1];
}
int SPI_execute_snapshot(SPIPlanPtr plan, Datum *values, const char *nulls,
                         Snapshot snapshot, Snapshot crosscheck, bool read_only,
                         bool triggers, long count) {
    ArrayType *ids = DatumGetArrayTypeP(values[0]);
    CHECK(snapshot == required_snapshot); CHECK(crosscheck == InvalidSnapshot);
    CHECK(read_only && !triggers && count == 0 && nulls == NULL);
    CHECK(ARR_ELEMTYPE(ids) == BYTEAOID && ARR_NDIM(ids) == 1);
    ++spi_calls;
    SPI_processed = plan == (SPIPlanPtr)(uintptr_t)1 ? metadata_count : payload_count;
    CHECK(SPI_processed <= 3);
    for (size_t i = 0; i < SPI_processed; ++i) row_pointers[i] = (HeapTuple)&row;
    tuple_table.vals = row_pointers; tuple_table.numvals = SPI_processed;
    SPI_tuptable = &tuple_table;
    if (plan == (SPIPlanPtr)(uintptr_t)2 && corrupt_payload_owner)
        ((bytea *)DatumGetPointer(row.values[1]))->vl_dat[0] ^= 1;
    return SPI_OK_SELECT;
}
void SPI_freetuptable(SPITupleTable *table) { CHECK(table == &tuple_table); SPI_tuptable = NULL; }

static bytea *probe_bytes(const void *data, size_t size) {
    bytea *result = palloc0(VARHDRSZ + size); SET_VARSIZE(result, VARHDRSZ + size);
    if (size) memcpy(VARDATA(result), data, size);
    return result;
}
static bytea *probe_wkb(const double *xyzm, uint32_t count) {
    size_t offset = count == 1 ? 5 : 9;
    bytea *result = probe_bytes(NULL, 0);
    pfree(result); result = palloc0(VARHDRSZ + offset + (size_t)count * 32);
    SET_VARSIZE(result, VARHDRSZ + offset + (size_t)count * 32);
    unsigned char *data = (unsigned char *)VARDATA(result);
    uint32_t type = count == 1 ? 3001 : 3002;
    data[0] = 1;
    for (int i = 0; i < 4; ++i) { data[1 + i] = (unsigned char)(type >> (i * 8)); if (count != 1) data[5 + i] = (unsigned char)(count >> (i * 8)); }
    for (size_t i = 0; i < (size_t)count * 4; ++i) {
        uint64_t bits; memcpy(&bits, &xyzm[i], 8);
        for (int j = 0; j < 8; ++j) data[offset + i * 8 + j] = (unsigned char)(bits >> (j * 8));
    }
    return result;
}
SPIPlanPtr SPI_prepare(const char *query, int nargs, Oid *types) {
    ++spi_prepare_calls;
    CHECK(nargs == 1 && types[0] == BYTEAARRAYOID);
    CHECK(strcmp(query, "metadata") == 0 || strcmp(query, "payload") == 0);
    return (SPIPlanPtr)(uintptr_t)(strcmp(query, "metadata") == 0 ? 1 : 2);
}
SPIPlanPtr SPI_prepare_cursor(const char *query, int nargs, Oid *types, int cursor_options) {
    ++spi_prepare_cursor_calls;
    CHECK(cursor_options == CURSOR_OPT_PARALLEL_OK);
    return SPI_prepare(query, nargs, types);
}
static admission_state *probe_state(Snapshot snapshot) {
    admission_state *s = palloc0(sizeof(*s));
    s->maximum_bytes = 64 * 1024 * 1024;
    s->maximum_logical = 1000000; s->maximum_operations = 20;
    s->snapshot = snapshot; required_snapshot = snapshot;
    s->metadata_plan = (SPIPlanPtr)(uintptr_t)1; s->payload_plan = (SPIPlanPtr)(uintptr_t)2;
    return s;
}
static void probe_stages_free(admission_state *s) {
    stage_list *lists[3] = {&s->source, &s->admitted, &s->current};
    for (size_t i = 0; i < 3; ++i) {
        for (size_t j = 0; j < lists[i]->count; ++j) intent_stage_free(lists[i]->items[j]);
        pfree(lists[i]->items);
    }
    pfree(s->missing); pfree(s);
}

uint64 hex_encode(const char *src,size_t len,char *dst) {
    static const char digits[]="0123456789abcdef";
    for(size_t i=0;i<len;++i){unsigned char c=(unsigned char)src[i];dst[i*2]=digits[c>>4];dst[i*2+1]=digits[c&15];}
    return len*2;
}
static void probe_session_view_receipt(void) {
    SessionAdmission state={0};state.maximum_bytes=4096;
    state.context=(MemoryContext)(uintptr_t)1;CurrentMemoryContext=(MemoryContext)(uintptr_t)2;
    physicality_descriptor_admitted_form_t forms[2]={0};
    hash128_t missing[2]={{2,0},{3,0}};
    laplace_physicality_pg_admission_result result={0};result.forms=forms;result.form_count=2;
    size_t bytes=99;CHECK(session_view_receipt(&state,&result,&bytes)==NULL && bytes==0 && state.bytes==0);
    forms[1].descriptor_id=(hash128_t){1,0};forms[1].view_state=PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE;
    forms[1].missing_count=2;result.view_missing_ids=missing;result.view_missing_count=2;
    char *receipt=session_view_receipt(&state,&result,&bytes);
    const char *expected="{\"schema\":\"laplace.session-descriptor-views/v1\",\"transaction_pending\":true,\"forms\":[{\"descriptor_id\":\"01000000000000000000000000000000\",\"view_state\":1,\"missing_first\":0,\"missing_count\":2}],\"missing_ids\":[\"02000000000000000000000000000000\",\"03000000000000000000000000000000\"]}";
    CHECK(strcmp(receipt,expected)==0 && state.bytes==bytes && state.peak_bytes==bytes);
    CHECK(allocation_context==state.context && allocation_context!=CurrentMemoryContext);
    session_release(&state,receipt,bytes);CHECK(state.bytes==0);
    state.maximum_bytes=512;REFUSES((void)session_view_receipt(&state,&result,&bytes),"byte grant");
    CHECK(state.bytes==0);result.view_missing_count=SIZE_MAX;
    REFUSES((void)session_view_receipt(&state,&result,&bytes),"finite extent");
    CurrentMemoryContext=NULL;
}

static void probe_view_arrays(void) {
    SnapshotData snapshot={0};admission_state *s=probe_state(&snapshot);
    s->context=(MemoryContext)(uintptr_t)3;CurrentMemoryContext=(MemoryContext)(uintptr_t)4;
    physicality_descriptor_admitted_form_t forms[3]={0};
    forms[0].view_id=(hash128_t){11,12};forms[2].view_id=(hash128_t){21,22};
    forms[1].view_state=PHYSICALITY_DESCRIPTOR_VIEW_MISSING_REFERENCE;
    forms[1].missing_first=1;forms[1].missing_count=2;
    ArrayType *views=admission_views(s,forms,3,3);
    CHECK(allocation_context==s->context && allocation_context!=CurrentMemoryContext);
    CHECK(ARR_NDIM(views)==1 && ARR_DIMS(views)[0]==3 && ARR_LBOUND(views)[0]==1);
    CHECK(ARR_HASNULL(views) && ARR_NULLBITMAP(views)[0]==5);
    char *data=ARR_DATA_PTR(views);
    CHECK(VARSIZE(data)==20 && !memcmp(VARDATA(data),&forms[0].view_id,16));
    CHECK(VARSIZE(data+20)==20 && !memcmp(VARDATA(data+20),&forms[2].view_id,16));
    CHECK(VARSIZE(views)==ARR_OVERHEAD_WITHNULLS(1,3)+40);
    for(int field=0;field<3;++field) {
        ArrayType *values=admission_view_field(s,forms,3,field);
        CHECK(!ARR_HASNULL(values) && ARR_DIMS(values)[0]==3);
        if(field==0){int16 values_copy[3];memcpy(values_copy,ARR_DATA_PTR(values),sizeof(values_copy));
            CHECK(values_copy[0]==0 && values_copy[1]==1 && values_copy[2]==0);}
        else{int64 values_copy[3];memcpy(values_copy,ARR_DATA_PTR(values),sizeof(values_copy));
            CHECK(values_copy[0]==0 && values_copy[1]==(field==1?1:2) && values_copy[2]==0);}
        pfree(values);
    }
    ArrayType *empty=admission_views(s,NULL,0,0);CHECK(ARR_NDIM(empty)==0 && !ARR_HASNULL(empty));pfree(empty);
    forms[1].missing_first=2;REFUSES((void)admission_views(s,forms,3,3),"missing slice");forms[1].missing_first=1;
    forms[1].missing_count=0;REFUSES((void)admission_views(s,forms,3,3),"missing slice");forms[1].missing_count=2;
    forms[1].view_state=7;REFUSES((void)admission_views(s,forms,3,3),"view state");
    forms[1].view_state=0;REFUSES((void)admission_views(s,forms,3,3),"missing slice");
    pfree(views);probe_stages_free(s);CurrentMemoryContext=NULL;
}

static void probe_cancel_holdoffs(void)
{
    CHECK(admission_cancel_requested(NULL) == 0);
    QueryCancelPending = 1;
    CHECK(admission_cancel_requested(NULL) == 1);
    QueryCancelHoldoffCount = 1;
    CHECK(admission_cancel_requested(NULL) == 0);
    ProcDiePending = 1;
    CHECK(admission_cancel_requested(NULL) == 1);
    InterruptHoldoffCount = 1;
    CHECK(admission_cancel_requested(NULL) == 0);
    InterruptHoldoffCount = 0;
    CritSectionCount = 1;
    CHECK(admission_cancel_requested(NULL) == 0);
    CritSectionCount = 0;
    QueryCancelHoldoffCount = 0;
    QueryCancelPending = ProcDiePending = 0;
    CHECK(admission_cancel_requested(NULL) == 0);
    /* A returned cancellation cannot become partial success even when no
     * PostgreSQL interrupt is pending. This test double does not run PG. */
    REFUSES(admission_status(PHYSICALITY_DESCRIPTOR_CANCELLED, "probe"), "cancelled");
    CHECK(error_code == ERRCODE_QUERY_CANCELED);
    CHECK(error_context_stack == NULL);
}

int main(void) {
    probe_cancel_holdoffs();
    probe_view_arrays();
    probe_session_view_receipt();
    SnapshotData snapshot = {0};
    TransactionId transactions[2] = {43,51}, children[1] = {44};
    hash128_t child_ids[2] = {{10,11},{20,21}}, entity, placement, pending[3];
    double trajectory[8], coord[4] = {.1,.2,.3,.4};
    hilbert128_t hilbert;
    size_t logical;
    admission_state *s;
    bytea *encoded;
    uint32_t vertices;

    snapshot.snapshot_type = SNAPSHOT_MVCC; snapshot.xmin = 42; snapshot.xmax = 99;
    snapshot.curcid = 7; snapshot.xcnt = 2; snapshot.xip = transactions;
    snapshot.subxcnt = 1; snapshot.subxip = children;
    CHECK(trajectory_build(child_ids, 2, trajectory) == 0);
    CHECK(trajectory_content_identity(trajectory, 2, &entity, &logical) == 0 && logical == 2);
    laplace_physicality_id_compute(entity, 1, &placement); hilbert4d_encode(coord, &hilbert);
    row.values[0] = PointerGetDatum(probe_bytes(&placement, 16));
    row.values[1] = PointerGetDatum(probe_bytes(&entity, 16));
    row.values[2] = Int16GetDatum(1);
    row.values[3] = PointerGetDatum(probe_wkb(coord, 1));
    row.values[4] = PointerGetDatum(probe_bytes(&hilbert, 16));
    row.values[5] = PointerGetDatum(probe_wkb(trajectory, 2));
    row.values[6] = Int32GetDatum(2); row.nulls[7] = row.nulls[8] = true;
    row.values[9] = TimestampTzGetDatum(123456);
    pending[0] = entity; pending[1] = (hash128_t){100,101}; pending[2] = (hash128_t){200,201};

    s = probe_state(&snapshot);
    admission_prepare_provider_plans(s, "metadata", "payload");
    CHECK(s->operations == 2 && spi_prepare_calls == 2 && spi_prepare_cursor_calls == 2);
    CHECK(s->metadata_plan == (SPIPlanPtr)(uintptr_t)1 && s->payload_plan == (SPIPlanPtr)(uintptr_t)2);
    s->maximum_operations = 3;
    REFUSES(admission_prepare_provider_plans(s, "metadata", "payload"), "require two database operations");
    CHECK(s->operations == 2 && spi_prepare_calls == 2 && spi_prepare_cursor_calls == 2);
    probe_stages_free(s);
    s = probe_state(&snapshot);
    encoded = admission_snapshot_text(s);
    CHECK(VARSIZE_ANY_EXHDR(encoded) == strlen("active-mvcc-v1;xmin=42;xmax=99;cid=7;recovery=0;suboverflow=0;xip=43,51;subxip=44"));
    CHECK(memcmp(VARDATA(encoded), "active-mvcc-v1;xmin=42;xmax=99;cid=7;recovery=0;suboverflow=0;xip=43,51;subxip=44", VARSIZE_ANY_EXHDR(encoded)) == 0);
    REFUSES(admission_add(SIZE_MAX, 1), "overflow");
    REFUSES(admission_multiply(SIZE_MAX, 2), "overflow");
    REFUSES(admission_charge(s, s->maximum_bytes), "byte grant");
    s->maximum_logical = 1;
    REFUSES(admission_logical(s, 2), "logical-work");
    s->maximum_logical = 1000000;
    encoded = probe_bytes(&entity, 15);
    REFUSES((void)admission_id(PointerGetDatum(encoded)), "exactly 16");
    pfree(encoded);
    encoded = probe_wkb(trajectory, 2);
    double *decoded = admission_wkb(PointerGetDatum(encoded), &vertices);
    CHECK(vertices == 2 && memcmp(decoded, trajectory, sizeof(trajectory)) == 0); pfree(decoded);
    ((unsigned char *)VARDATA(encoded))[0] = 0;
    REFUSES((void)admission_wkb(PointerGetDatum(encoded), &vertices), "little-endian");
    ((unsigned char *)VARDATA(encoded))[0] = 1; SET_VARSIZE(encoded, VARSIZE(encoded) - 1);
    REFUSES((void)admission_wkb(PointerGetDatum(encoded), &vertices), "trailing WKB"); pfree(encoded);

    admission_hydrate(s, pending, 3);
    CHECK(spi_calls == 2 && s->operations == 2 && s->rounds == 1);
    CHECK(s->current.count == 1 && intent_stage_physicality_count(s->current.items[0]) == 1);
    CHECK(s->current_bodies == 1 && s->missing_count == 2 && s->logical_work == 2 && s->current_logical == 2);
    CHECK(s->stored_vertices == 2 && s->bytes <= s->peak_bytes && s->peak_bytes < s->maximum_bytes);
    CHECK(admission_preflight(s, &s->current) == 2);
    volatile unsigned previous_calls = spi_calls;
    REFUSES(admission_hydrate(s, &pending[1], 1), "already checked absent");
    CHECK(spi_calls == previous_calls);

    /* Raw duplicate placement addresses survive transport/capture as distinct
     * forms. Only the existing writer owns its separate placement winner. */
    intent_stage_t *original = intent_stage_new_bounded(2, 1024 * 1024);
    CHECK(original != NULL);
    CHECK(intent_stage_add_physicality(original, &placement, &entity, 1, coord, &hilbert,
        trajectory, 2, 2, 1, 0, 1, 0, INTENT_STAGE_PG_EPOCH_UNIX_US + 10) == 0);
    coord[0] = .15; hilbert4d_encode(coord, &hilbert);
    CHECK(intent_stage_add_physicality(original, &placement, &entity, 1, coord, &hilbert,
        trajectory, 2, 2, 1, 0, 1, 0, INTENT_STAGE_PG_EPOCH_UNIX_US + 20) == 0);
    const hash128_t content_type = laplace_content_tier_type_id(4);
    CHECK(intent_stage_add_entity(original, &entity, 4, &content_type, &entity) == 0);
    CHECK(intent_stage_add_attestation(original, &entity, &entity, &content_type,
        NULL, &entity, NULL, 0, INTENT_STAGE_PG_EPOCH_UNIX_US + 30, 1,
        0, 0, 0, NULL) == 0);
    CHECK(intent_stage_entity_count(original) == 1 && intent_stage_attestation_count(original) == 1);
    transport_array parts[3] = {0}; Datum tuple_values[3]; bool tuple_nulls[3] = {false};
    for (int i = 0; i < 3; ++i) {
        size_t length; const uint8_t *tuples = intent_stage_tuple_ptr(original, (intent_stage_table_t)(i + 1), &length);
        tuple_values[i] = PointerGetDatum(probe_bytes(tuples, length));
        parts[i].values = &tuple_values[i]; parts[i].nulls = &tuple_nulls[i]; parts[i].count = 1;
    }
    /* Both entry paths fully validate E/P/A framing, then retain only the
     * physicality observations needed by descriptor admission. The ordinary
     * writer still owns the untouched original E/P/A stage. */
    {
        admission_state *copy = probe_state(&snapshot);
        const intent_stage_t *inputs[1] = {original};
        const uint8_t *buffers[3];
        size_t lengths[3];
        intent_stage_t *full_import = NULL, *physical_import = NULL;
        for (int table = 0; table < 3; ++table)
            buffers[table] = intent_stage_tuple_ptr(original,
                (intent_stage_table_t)(table + 1), &lengths[table]);
        CHECK(intent_stage_from_tuple_bytes(buffers[0], lengths[0], buffers[1], lengths[1],
            buffers[2], lengths[2], copy->maximum_bytes, &full_import) == 0);
        CHECK(intent_stage_from_tuple_bytes(NULL, 0, buffers[1], lengths[1],
            NULL, 0, copy->maximum_bytes, &physical_import) == 0);
        const size_t imported_peak = intent_stage_memory_peak_bytes(full_import);
        const size_t physical_bytes = intent_stage_memory_bytes(physical_import);
        CHECK(intent_stage_memory_bytes(full_import) > physical_bytes);
        admission_clone_stages(copy, inputs, 1, &copy->source);
        CHECK(copy->source.count == 1 && copy->source.items[0] != original);
        CHECK(copy->bytes == 4 * sizeof(intent_stage_t *) + physical_bytes);
        CHECK(copy->peak_bytes == 4 * sizeof(intent_stage_t *) + imported_peak);
        CHECK(intent_stage_memory_peak_bytes(copy->source.items[0]) == imported_peak);
        for (int table = 0; table < 3; ++table) {
            size_t copied_size;
            const uint8_t *copied = intent_stage_tuple_ptr(copy->source.items[0],
                (intent_stage_table_t)(table + 1), &copied_size);
            if (table == 1) {
                CHECK(copied_size == lengths[table]);
                CHECK(memcmp(copied, buffers[table], copied_size) == 0 && copied != buffers[table]);
            } else CHECK(copied_size == 0 && copied == NULL);
        }
        CHECK(intent_stage_entity_count(copy->source.items[0]) == 0);
        CHECK(intent_stage_attestation_count(copy->source.items[0]) == 0);
        CHECK(intent_stage_entity_count(original) == 1 && intent_stage_attestation_count(original) == 1);
        intent_stage_free(full_import); intent_stage_free(physical_import);
        CHECK(admission_preflight(copy, &copy->source) == 4);
        REFUSES(admission_clone_stages(copy, NULL, 1, &copy->admitted), "array is missing");
        inputs[0] = NULL;
        REFUSES(admission_clone_stages(copy, inputs, 1, &copy->admitted), "missing stage");
        inputs[0] = original;
        copy->maximum_bytes = copy->bytes + sizeof(intent_stage_t *) * 4;
        REFUSES(admission_clone_stages(copy, inputs, 1, &copy->admitted), "caller stage import");
        probe_stages_free(copy);
    }
    /* Discarded tables must still reject malformed framing through each real
     * admission caller. These mutate actual native/SQL COPY bytes. */
    for (int table = 0; table < 3; table += 2) {
        size_t length;
        uint8_t *frame = (uint8_t *)intent_stage_tuple_ptr(original,
            (intent_stage_table_t)(table + 1), &length);
        CHECK(length > 2);
        const uint8_t columns = frame[1];
        admission_state *bad = probe_state(&snapshot);
        const intent_stage_t *inputs[1] = {original};
        frame[1] = 1;
        REFUSES(admission_clone_stages(bad, inputs, 1, &bad->source), "caller stage import");
        CHECK(bad->source.count == 1 && bad->source.items[0] == NULL);
        frame[1] = columns;
        probe_stages_free(bad);
        bytea *transported = (bytea *)DatumGetPointer(tuple_values[table]);
        const Size size = VARSIZE(transported);
        bad = probe_state(&snapshot);
        SET_VARSIZE(transported, size - 1);
        REFUSES(admission_import(bad, parts, &bad->source), "tuple import");
        CHECK(bad->source.count == 1 && bad->source.items[0] == NULL);
        SET_VARSIZE(transported, size);
        probe_stages_free(bad);
    }
    admission_import(s, parts, &s->source);
    CHECK(s->source.count == 1 && intent_stage_physicality_count(s->source.items[0]) == 2);
    CHECK(intent_stage_entity_count(s->source.items[0]) == 0);
    CHECK(intent_stage_attestation_count(s->source.items[0]) == 0);
    CHECK(admission_preflight(s, &s->source) == 4);
    {
        Datum source_values[2] = {row.values[0], row.values[1]};
        Datum unit_values[2] = {row.values[1], row.values[0]};
        Datum trusts[2] = {Float8GetDatum(.6), Float8GetDatum(.9)};
        transport_array source_arrays[3] = {{source_values,NULL,2},{unit_values,NULL,2},{trusts,NULL,2}};
        physicality_descriptor_source_observation_t *sources = admission_sources(s, source_arrays, 2);
        CHECK(memcmp(&sources[0].source_id, &placement, 16) == 0);
        CHECK(memcmp(&sources[1].source_id, &entity, 16) == 0);
        CHECK(memcmp(&sources[0].source_unit_id, &entity, 16) == 0);
        CHECK(sources[0].source_trust == .6 && sources[1].source_trust == .9);
        REFUSES((void)admission_sources(s, source_arrays, 1), "must align");
        trusts[1] = Float8GetDatum(NAN);
        REFUSES((void)admission_sources(s, source_arrays, 2), "finite registered prior");
        trusts[1] = Float8GetDatum(INFINITY);
        REFUSES((void)admission_sources(s, source_arrays, 2), "finite registered prior");
        trusts[1] = Float8GetDatum(-.1);
        REFUSES((void)admission_sources(s, source_arrays, 2), "finite registered prior");
        trusts[1] = Float8GetDatum(1.1);
        REFUSES((void)admission_sources(s, source_arrays, 2), "finite registered prior");
    }
    {
        size_t expected_bytes = 0, first_size, second_size;
        const uint8_t *first = intent_stage_tuple_ptr(original, INTENT_STAGE_TABLE_PHYSICALITIES, &first_size);
        const uint8_t *second = intent_stage_tuple_ptr(s->current.items[0], INTENT_STAGE_TABLE_PHYSICALITIES, &second_size);
        s->output[0] = original; s->output[1] = s->current.items[0]; s->output[2] = original;
        ArrayType *out = admission_output(s, INTENT_STAGE_TABLE_PHYSICALITIES, &expected_bytes);
        CHECK(expected_bytes == first_size * 2 + second_size && ARR_DIMS(out)[0] == 3 && ARR_LBOUND(out)[0] == 1);
        char *cursor = (char *)out + ARR_OVERHEAD_NONULLS(1);
        CHECK(VARSIZE_ANY_EXHDR(cursor) == first_size && memcmp(VARDATA_ANY(cursor), first, first_size) == 0);
        cursor += INTALIGN(VARHDRSZ + first_size);
        CHECK(VARSIZE_ANY_EXHDR(cursor) == second_size && memcmp(VARDATA_ANY(cursor), second, second_size) == 0);
        cursor += INTALIGN(VARHDRSZ + second_size);
        CHECK(VARSIZE_ANY_EXHDR(cursor) == first_size && memcmp(VARDATA_ANY(cursor), first, first_size) == 0);
        s->output[0] = s->output[1] = s->output[2] = NULL;
    }
    physicality_descriptor_basis_t basis;
    for (size_t i = 0; i < PHYSICALITY_DESCRIPTOR_TAG_COUNT; ++i) basis.tags[i] = (hash128_t){1000 + i,7};
    for (size_t i = 0; i < 256; ++i) basis.byte_numbers[i] = (hash128_t){2000 + i,8};
    physicality_descriptor_limits_t limits = {16 * 1024 * 1024};
    physicality_descriptor_capture_t *capture = NULL;
    CHECK(physicality_descriptor_capture_stages((const intent_stage_t *const *)s->source.items,
        s->source.count, &basis, &limits, 20 * 1024 * 1024, &capture) == PHYSICALITY_DESCRIPTOR_OK);
    size_t observed_count, root_count;
    const physicality_descriptor_observation_t *observations = physicality_descriptor_capture_observations(capture, &observed_count);
    const hash128_t *roots = physicality_descriptor_plan_roots(physicality_descriptor_capture_plan(capture), &root_count);
    CHECK(observed_count == 2 && root_count == 2 && memcmp(&roots[0], &roots[1], 16) != 0);
    CHECK(observations[0].observed_at_unix_us + 10 == observations[1].observed_at_unix_us);
    {
        const intent_stage_t *source_copy[] = {original};
        admission_state *phased = probe_state(&snapshot);
        admission_clone_stages(phased, source_copy, 1, &phased->source);
        const size_t before = phased->bytes;
        const size_t encoded = intent_stage_memory_bytes(phased->source.items[0]);
        const size_t pointers = phased->source.capacity * sizeof(*phased->source.items);
        admission_capture_source(phased, &basis, 2);
        CHECK(phased->source.count == 0 && phased->source.capacity == 0 && phased->source.items == NULL);
        CHECK(phased->source_validation == NULL);
        CHECK(physicality_descriptor_capture_plan(phased->capture) == NULL);
        const size_t decoded = physicality_descriptor_capture_bytes(phased->capture);
        CHECK(phased->bytes == before - encoded - pointers + decoded);
        CHECK(phased->peak_bytes >= before + decoded);
        CHECK(phased->peak_bytes <= phased->maximum_bytes);
        size_t phased_count = 0;
        const physicality_descriptor_observation_t *retained =
            physicality_descriptor_capture_observations(phased->capture, &phased_count);
        CHECK(phased_count == observed_count);
        for (size_t i = 0; i < phased_count; ++i) {
            CHECK(hash128_equals(&retained[i].placement_id, &observations[i].placement_id));
            CHECK(retained[i].source_stage_index == observations[i].source_stage_index);
            CHECK(retained[i].source_row_index == observations[i].source_row_index);
            CHECK(retained[i].observed_at_unix_us == observations[i].observed_at_unix_us);
        }
        CHECK(intent_stage_physicality_count(original) == 2);
        admission_cleanup(phased);
        CHECK(phased->capture == NULL && phased->source_validation == NULL);
        probe_stages_free(phased);
    }
    {
        /* A framed, identity-consistent tuple can still contain an invalid
         * descriptor scalar. Full validation must refuse it after retirement
         * of the owned encoded copy; transport-only acceptance would fail this. */
        size_t original_bytes = 0;
        uint8_t *tuples = (uint8_t *)intent_stage_tuple_ptr(original,
            INTENT_STAGE_TABLE_PHYSICALITIES, &original_bytes);
        const size_t coordinate_x = 2u + 20u + 20u + 6u + 4u + 5u;
        CHECK(original_bytes > coordinate_x + 8u);
        uint8_t saved[8]; memcpy(saved, tuples + coordinate_x, sizeof(saved));
        const uint64_t nan = UINT64_C(0x7ff8000000000001);
        for (size_t axis = 0; axis < 8u; ++axis)
            tuples[coordinate_x + axis] = (uint8_t)(nan >> (axis * 8u));
        admission_state *invalid = probe_state(&snapshot);
        const intent_stage_t *source_copy[] = {original};
        admission_clone_stages(invalid, source_copy, 1, &invalid->source);
        memcpy(tuples + coordinate_x, saved, sizeof(saved));
        REFUSES(admission_capture_source(invalid, &basis, 2), "original-form capture");
        CHECK(error_code == ERRCODE_INVALID_PARAMETER_VALUE);
        CHECK(strstr(error_detail, "phase=original-form descriptor validation ") != NULL);
        CHECK(invalid->source.count == 0 && invalid->source.items == NULL);
        CHECK(invalid->capture != NULL && invalid->source_validation == NULL);
        admission_cleanup(invalid);
        CHECK(invalid->capture == NULL && invalid->source_validation == NULL);
        probe_stages_free(invalid);
    }
    physicality_descriptor_capture_free(capture); intent_stage_free(original);
    parts[2].count = 0;
    REFUSES(admission_import(s, parts, &s->source), "must align");
    parts[2].count = 1;
    SET_VARSIZE(DatumGetPointer(tuple_values[1]), VARSIZE(DatumGetPointer(tuple_values[1])) - 1);
    REFUSES(admission_import(s, parts, &s->source), "tuple import");
    probe_stages_free(s);

    s = probe_state(&snapshot); s->maximum_operations = 1;
    previous_calls = spi_calls;
    REFUSES(admission_hydrate(s, pending, 3), "two operations"); CHECK(spi_calls == previous_calls); probe_stages_free(s);
    {
        hash128_t many_children[100], large_entity, large_placement;
        double large_trajectory[400];
        Datum previous_id = row.values[0], previous_entity = row.values[1], previous_trajectory = row.values[5];
        for (size_t i = 0; i < 100; ++i) many_children[i] = child_ids[i % 2];
        CHECK(trajectory_build(many_children, 100, large_trajectory) == 0);
        CHECK(trajectory_content_identity(large_trajectory, 100, &large_entity, &logical) == 0 && logical == 100);
        laplace_physicality_id_compute(large_entity, 1, &large_placement);
        row.values[0] = PointerGetDatum(probe_bytes(&large_placement,16));
        row.values[1] = PointerGetDatum(probe_bytes(&large_entity,16));
        row.values[5] = PointerGetDatum(probe_wkb(large_trajectory,100)); row.values[6] = Int32GetDatum(100);
        CHECK(toast_raw_datum_size(row.values[5]) > 2048);
        s = probe_state(&snapshot); s->maximum_bytes = BLCKSZ;
        previous_calls = spi_calls;
        REFUSES(admission_hydrate(s, &large_entity, 1), "byte grant");
        CHECK(spi_calls == previous_calls); probe_stages_free(s);
        pfree(DatumGetPointer(row.values[0])); pfree(DatumGetPointer(row.values[1])); pfree(DatumGetPointer(row.values[5]));
        row.values[0] = previous_id; row.values[1] = previous_entity; row.values[5] = previous_trajectory;
        row.values[6] = Int32GetDatum(2);
    }
    s = probe_state(&snapshot); s->maximum_logical = 1;
    REFUSES(admission_hydrate(s, pending, 3), "logical-work");
    CHECK(s->current.count == 1 && intent_stage_physicality_count(s->current.items[0]) == 0); probe_stages_free(s);
    s = probe_state(&snapshot); metadata_count = 2;
    REFUSES(admission_hydrate(s, pending, 3), "duplicate placement"); probe_stages_free(s); metadata_count = 1;
    s = probe_state(&snapshot); payload_count = 0;
    REFUSES(admission_hydrate(s, pending, 3), "metadata and payload disagree"); probe_stages_free(s); payload_count = 1;
    s = probe_state(&snapshot); corrupt_payload_owner = true;
    REFUSES(admission_hydrate(s, pending, 3), "duplicate placement"); probe_stages_free(s);
    ((bytea *)DatumGetPointer(row.values[1]))->vl_dat[0] ^= 1; corrupt_payload_owner = false;
    s = probe_state(&snapshot); fake_raw_datum = row.values[5]; fake_raw_size = 64 * 1024 * 1024;
    previous_calls = spi_calls;
    REFUSES(admission_hydrate(s, pending, 3), "byte grant"); CHECK(spi_calls == previous_calls + 1);
    probe_stages_free(s); fake_raw_size = 0;
    s = probe_state(&snapshot); row.values[6] = Int32GetDatum(3);
    REFUSES(admission_hydrate(s, pending, 3), "logical count disagrees");
    CHECK(intent_stage_physicality_count(s->current.items[0]) == 0); probe_stages_free(s);
    printf("physicality PG native helpers: %u checks; fake SPI calls=%u; backend/PostGIS executions=0\n", checks, spi_calls);
    return 0;
}
