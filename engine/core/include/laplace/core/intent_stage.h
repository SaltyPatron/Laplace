#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

/* intent_stage — in-engine materialization of PostgreSQL COPY BINARY byte
 * streams for the three substrate tables (entities, physicalities,
 * attestations).
 *
 * SubstrateCRUD + the locked invariant that the C# layer is an
 * I/O transport only: the C# writer constructs an intent_stage, adds rows
 * via the per-table add_* functions (each row is one tight C path that
 * appends pre-byteswapped field bytes to an internal arena), then calls
 * intent_stage_emit_copy_binary to obtain a ready-to-send byte buffer that
 * Npgsql streams directly into the PG COPY protocol via the low-level
 * binary import API.
 *
 * Zero per-row managed allocations on the C# side. Zero parameter binding.
 * One COPY round-trip per table. Endianness, EWKB, microsecond-epoch
 * conversion, NULL framing — all handled here.
 *
 * Wire format: per PG COPY BINARY spec
 *   header = "PGCOPY\n\xff\r\n\0" (11 bytes)
 *          | flags          (uint32 BE)  -- 0; we never include OIDs
 *          | hdr_ext_length (uint32 BE)  -- 0; no header extension area
 *   tuples = field_count    (int16 BE)
 *          | for each field: length (int32 BE, -1=NULL) | data bytes
 *   trailer = int16 BE -1 (0xFFFF)
 *
 * Geometry columns (coord POINTZM, trajectory LINESTRINGZM) are emitted as
 * PostGIS EWKB with SRID_UNKNOWN (no SRID flag), matching the convention
 * used by liblaplace_geom (lwpoint_make4d(SRID_UNKNOWN, ...) at
 * extension/laplace_geom/src/laplace_geom.c:165).
 *
 * Timestamp columns are emitted as int64 microseconds since 2000-01-01
 * UTC — PG's internal timestamptz binary format.
 *
 * POD args only at the C ABI surface; no C++ types.. */

typedef struct intent_stage intent_stage_t;

typedef enum {
    INTENT_STAGE_TABLE_ENTITIES      = 1,
    INTENT_STAGE_TABLE_PHYSICALITIES = 2,
    INTENT_STAGE_TABLE_ATTESTATIONS  = 3,
} intent_stage_table_t;

/* PG epoch (2000-01-01 00:00:00 UTC) expressed as microseconds since the
 * Unix epoch. Subtract from unix-µs to get PG-µs. */
#define INTENT_STAGE_PG_EPOCH_UNIX_US INT64_C(946684800000000)

/* Allocator + lifecycle. row_capacity_hint applies to each per-table buffer.
 * Returns NULL on out-of-memory. */
intent_stage_t* intent_stage_new(size_t row_capacity_hint);
void            intent_stage_free(intent_stage_t* stage);

/* Counts. */
size_t intent_stage_entity_count(const intent_stage_t* stage);
size_t intent_stage_physicality_count(const intent_stage_t* stage);
size_t intent_stage_attestation_count(const intent_stage_t* stage);

/* Returns the comma-separated column list for the COPY ... FROM STDIN BINARY
 * statement matching what the emitted byte stream contains. Pointer is
 * static; never NULL for valid table_kind. */
const char* intent_stage_copy_column_list(intent_stage_table_t table);

/* Add one entities row.
 *   id           — 16-byte hash128
 *   tier         — 0..255
 *   type_id      — 16-byte hash128
 *   first_observed_by — nullable; pass NULL to emit SQL NULL
 * Returns 0 on success, non-zero on OOM or invalid args. */
int intent_stage_add_entity(
    intent_stage_t*  stage,
    const hash128_t* id,
    int16_t          tier,
    const hash128_t* type_id,
    const hash128_t* first_observed_by);

/* Add one physicalities row.
 *   id, entity_id, source_id     — 16-byte hash128
 *   kind                         — 1=CONTENT, 2=BUILDING_BLOCK, 3=PROJECTION
 *   coord                        — x,y,z,m doubles for POINTZM
 *   hilbert_index                — 16 raw bytes
 *   trajectory_xyzm              — nullable; trajectory_n_vertices*4 doubles
 *                                  for LINESTRINGZM (pass NULL + 0 for SQL NULL)
 *   n_constituents               — non-negative
 *   alignment_residual           — pass alignment_residual_is_null=1 for SQL NULL
 *   source_dim                   — pass source_dim_is_null=1 for SQL NULL
 *   observed_at_unix_us          — microseconds since Unix epoch
 * Returns 0 on success, non-zero on OOM or invalid args. */
int intent_stage_add_physicality(
    intent_stage_t*     stage,
    const hash128_t*    id,
    const hash128_t*    entity_id,
    const hash128_t*    source_id,
    int16_t             type,
    const double        coord[4],
    const hilbert128_t* hilbert_index,
    const double*       trajectory_xyzm,
    uint32_t            trajectory_n_vertices,
    int32_t             n_constituents,
    int                 alignment_residual_is_null,
    double              alignment_residual,
    int                 source_dim_is_null,
    int32_t             source_dim,
    int64_t             observed_at_unix_us);

/* Add one attestations (EVIDENCE) row — PROVENANCE, never values.
 *   id, subject_id, type_id, source_id — 16-byte hash128 (NOT NULL)
 *   object_id, context_id              — nullable; pass NULL to emit SQL NULL
 *   outcome      — the dissent record as a CLASS, never a magnitude:
 *                  0 = refute, 1 = draw, 2 = confirm
 *   last_observed_at_unix_us           — microseconds since Unix epoch
 *   observation_count                  — non-negative (occurrences = games)
 * The witness's magnitude is testimony, CONSUMED into the consensus match at
 * ingest and never persisted (a stored per-witness score is invertible to the
 * weight — recording raw weights). The accumulated rating/rd/volatility live
 * on consensus, not here.
 * Returns 0 on success, non-zero on OOM or invalid args. */
int intent_stage_add_attestation(
    intent_stage_t*  stage,
    const hash128_t* id,
    const hash128_t* subject_id,
    const hash128_t* type_id,
    const hash128_t* object_id,
    const hash128_t* source_id,
    const hash128_t* context_id,
    int16_t          outcome,
    int64_t          last_observed_at_unix_us,
    int64_t          observation_count);

/* Emit the full COPY BINARY byte stream (header + accumulated tuples +
 * trailer) for one table into `buf`.
 *
 * If buf == NULL: returns the required byte count without writing.
 * If buf_capacity < required: writes nothing, returns required count.
 * On successful write: returns bytes written, which equals required count.
 * If the stage has zero rows for this table: returns the size of the
 *   header + trailer (still a valid, empty COPY stream). */
size_t intent_stage_emit_copy_binary(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    uint8_t*              buf,
    size_t                buf_capacity);

/* Direct pointer to a table's accumulated COPY-binary TUPLE bytes (the rows as
 * serialized by the engine, WITHOUT the 19-byte header or 2-byte trailer — those
 * are constant and framed by the caller). Lets the caller STREAM the engine's
 * native serialization straight into a COPY socket instead of materializing the
 * whole stage into one managed array. Sets *out_len to the tuple byte count;
 * returns NULL (and *out_len = 0) for an empty/invalid table. The pointer is
 * owned by the stage and stays valid until the next add / clear / free. */
const uint8_t* intent_stage_tuple_ptr(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    size_t*               out_len);

#ifdef __cplusplus
}
#endif
