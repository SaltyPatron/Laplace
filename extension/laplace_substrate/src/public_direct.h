#ifndef LAPLACE_SUBSTRATE_PUBLIC_DIRECT_H
#define LAPLACE_SUBSTRATE_PUBLIC_DIRECT_H

#include "postgres.h"
#include "executor/tuptable.h"
#include "fmgr.h"
#include "utils/array.h"
#include "laplace/laplace.h"
#include "laplace/core/hash128.h"

/*
 * Zero-SPI direct fmgr bridge to installed public.laplace_* C primitives
 * (and C-language helpers in substrate schemas). Caches OIDs and FmgrInfo
 * per backend in TopMemoryContext with syscache invalidation on
 * PROCOID / TYPEOID / NAMESPACEOID / EXTENSIONOID.
 */

Oid laplace_pub_blake3_oid(void);
Oid laplace_pub_blake3_array_oid(void);
Oid laplace_pub_geometry_oid(void);
Oid laplace_pub_geometry_array_oid(void);
uint64 laplace_pub_generation(void);

static inline hash128_t
laplace_pub_blake3_to_hash128(Datum d)
{
    hash128_t h;
    memcpy(&h, DatumGetPointer(d), sizeof(hash128_t));
    return h;
}

static inline Datum
laplace_pub_hash128_to_blake3(const hash128_t *h)
{
    void *p = palloc(sizeof(hash128_t));
    memcpy(p, h, sizeof(hash128_t));
    return PointerGetDatum(p);
}

ArrayType *laplace_pub_blake3_array(const hash128_t *ids, int count);
int laplace_pub_deconstruct_blake3_array(ArrayType *arr, hash128_t **out_ids);
ArrayType *laplace_pub_bytea_array_to_blake3_array(ArrayType *arr);

/* Read PostGIS GSERIALIZED v2 POINT/LINESTRING ZM in place as lp_path. */
lp_path laplace_pub_geo_path(Datum geom_datum);
bool laplace_pub_geo_point4d(Datum geom_datum, double out_xyzm[4]);
Datum laplace_pub_point4d_geom(const double xyzm[4]);

/* Cached direct scalar/composite function call (honors fn_strict). */
Datum laplace_pub_call(const char *schema, const char *funcname,
                       int nargs, const Oid *argtypes,
                       const Datum *args, const bool *argnulls,
                       bool *out_isnull);

/* Callback for streaming materialized SRF rows. Return false to stop early. */
typedef bool (*LaplaceDirectSrfVisit)(TupleTableSlot *slot, void *ctx);

/* Cached direct SFRM_Materialize SRF invocation (no SPI). */
uint64 laplace_pub_call_srf(const char *schema, const char *funcname,
                            int nargs, const Oid *argtypes,
                            const Datum *args, const bool *argnulls,
                            LaplaceDirectSrfVisit visit, void *ctx);

/* Common cached scalar primitives. */
int16 laplace_pub_bank_bit(const char *bank, const char *value);
bool laplace_pub_text_id_hash(const char *str, size_t len, hash128_t *out);
int16 laplace_pub_mask_value(Datum mask_datum, const char *bank);
bool laplace_pub_mask_has_value(Datum mask_datum, const char *bank, const char *value);

#endif /* LAPLACE_SUBSTRATE_PUBLIC_DIRECT_H */
